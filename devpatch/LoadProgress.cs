using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace Doorstop
{
	/// <summary>
	/// Load progress for long save loads: a 239,370-object colony takes ~7 minutes to load, the window turns "not responding", and the only thing on screen
	/// is the frozen LOADING... frame.
	///
	/// Klei's load is one synchronous main-thread call (SaveLoader.OnSpawn -> Load(string) ->
	/// SaveManager.Load's object loop), so Unity cannot draw anything until it returns. Two
	/// things here work around that without touching the load itself:
	///
	///   * the hang flag. Windows calls a window hung when its thread has not called PeekMessage
	///     for 5 s (IsHungAppWindow). The load hooks below call PeekMessage(PM_NOREMOVE) at most
	///     every 250 ms, which clears it without removing or dispatching any posted input: the
	///     clicks and keys stay queued for Unity. DisableProcessWindowsGhosting covers the
	///     stretches no hook reaches (the native sim load, one long first frame).
	///   * the detail. A small click-through panel, owned by its own thread with its own message
	///     loop, sits under the LOADING... text and shows the phase, the object loop's position
	///     in the save's prefab table (read ahead from the decompressed bytes, so the total is
	///     known before the first object loads), rate, ETA, memory and the slowest groups so far.
	///     It is a separate top-level window so nothing in it can block on the frozen main
	///     thread; it hides whenever the game is not the foreground window.
	///
	/// Nothing here changes what is loaded or in what order. Each finished phase is also written
	/// to DevData/devpatch.log. `devpatch_loadprogress.off` next to the exe turns it all off.
	///
	/// Installed in two steps because of the Game static-constructor hazard (Installer.cs): the
	/// Doorstop thread patches only LaunchInitializer.Update and LoadingOverlay.Load, and the
	/// first of those to run patches the load path from the main thread.
	/// </summary>
	internal static class LoadProgress
	{
		private static readonly Stopwatch Clock = Stopwatch.StartNew();
		private static readonly long PumpTicks = Stopwatch.Frequency / 4;

		private static Harmony harmony;
		private static bool mainInstalled;

		// --- state: written on the main thread, read by the overlay thread ---------------------
		// Plain fields; the overlay only ever shows a slightly stale value, never a torn one that
		// matters (each is a single int/long/reference write).
		private static volatile bool active;
		private static long startTicks;
		private static string saveName = "";
		private static long saveBytes;
		private static volatile string phasePath = "";
		private static readonly List<string> Finished = new List<string>();
		private static readonly object FinishedLock = new object();

		private sealed class Group
		{
			public string Name;
			public int Count;
			public int Done;
			public long Ticks;
		}

		private static Group[] groups = new Group[0];
		private static int objectsTotal;
		private static int objectsDone;
		private static volatile int groupIndex = -1;
		private static long objectsStartTicks = -1;
		private static int rootDepth;
		private static long rootStart;
		private static long spawns;
		private static long lastPump;

		// After Game.OnSpawn: the first frames are still slow; one large colony sat ~90 s in
		// them. The panel stays up, showing frame times, until three frames in a row come in
		// under 250 ms or a minute has gone by.
		private static volatile bool spawned;
		private static long spawnTicks;
		private static int frames;
		private static int fastFrames;
		private static double lastFrameSeconds;
		private static long lastFrameTicks;

		private static Overlay overlay;

		private sealed class Phase
		{
			public string Name;
			public long Start;
			public string OuterPath;
		}

		private static readonly (Type type, string method, Type[] args, string label)[] Targets =
		{
			(typeof(SaveLoader), "Load", new[] { typeof(string) }, "read save"),
			(typeof(SaveLoader), "DecompressContents", null, "decompress"),
			(typeof(KSerialization.Manager), "DeserializeDirectory", null, "type directory"),
			(typeof(SaveLoader), "Load", new[] { typeof(IReader) }, "load world"),
			(typeof(ProcGenGame.WorldGen), "LoadSettings", null, "worldgen settings"),
			(typeof(CustomGameSettings), "LoadClusters", null, "cluster settings"),
			(typeof(Game), "LoadSettings", null, "game settings"),
			(typeof(Sim), "LoadWorld", null, "sim world (native)"),
			(typeof(SceneInitializer), "PostLoadPrefabs", null, "prefabs"),
			(typeof(SaveManager), "Load", new[] { typeof(IReader) }, "objects"),
			(typeof(Game), "Load", new[] { typeof(KSerialization.Deserializer) }, "game state"),
			(typeof(Game), "OnSpawn", null, "Game.OnSpawn"),
		};

		private static readonly Dictionary<MethodBase, string> Labels = new Dictionary<MethodBase, string>();

		internal static void Install(Harmony h)
		{
			if (File.Exists("devpatch_loadprogress.off"))
			{
				Entrypoint.Log("loadprogress: disabled by devpatch_loadprogress.off");
				return;
			}
			harmony = h;
			var ensure = new HarmonyMethod(AccessTools.Method(typeof(LoadProgress), nameof(EnsureMainInstalled)));
			Type launch = AccessTools.TypeByName("LaunchInitializer");
			MethodInfo launchUpdate = launch == null ? null : AccessTools.Method(launch, "Update");
			if (launchUpdate != null)
			{
				h.Patch(launchUpdate, new HarmonyMethod(AccessTools.Method(typeof(LoadProgress), nameof(EnsureMainInstalledWhenPlatformReady))));
			}
			h.Patch(AccessTools.Method(typeof(LoadingOverlay), nameof(LoadingOverlay.Load)), ensure);
			Entrypoint.Log("loadprogress: armed; load hooks go in on the main thread");
		}

		// LaunchInitializer.Update runs from the first frame, before DistributionPlatform.Initialize.
		// Patching Game.OnSpawn or the settings loaders then JIT-compiles code that touches
		// CustomGameSettingConfigs, whose static constructor asks DlcManager, which dereferences a
		// null DistributionPlatform.Inst. A failed static constructor poisons the type for the whole
		// session, and every mod that patches Game.OnSpawn after that fails to load. So wait for the gate LaunchInitializer itself waits on.
		private static void EnsureMainInstalledWhenPlatformReady()
		{
			if (mainInstalled || !DistributionPlatform.Initialized || !DistributionPlatform.Inst.IsDLCStatusReady())
			{
				return;
			}
			EnsureMainInstalled();
		}

		private static void EnsureMainInstalled()
		{
			if (mainInstalled)
			{
				return;
			}
			mainInstalled = true;
			try
			{
				DisableProcessWindowsGhosting();
				var prefix = new HarmonyMethod(AccessTools.Method(typeof(LoadProgress), nameof(PhasePrefix)));
				var postfix = new HarmonyMethod(AccessTools.Method(typeof(LoadProgress), nameof(PhasePostfix)));
				int n = 0;
				foreach (var t in Targets)
				{
					MethodInfo m = t.args == null ? AccessTools.Method(t.type, t.method) : AccessTools.Method(t.type, t.method, t.args);
					if (m == null)
					{
						Entrypoint.Log("loadprogress: no " + t.type.Name + "." + t.method + "; phase not shown");
						continue;
					}
					Labels[m] = t.label;
					harmony.Patch(m, prefix, postfix);
					n++;
				}
				harmony.Patch(AccessTools.Method(typeof(SaveLoader), "Load", new[] { typeof(string) }),
					postfix: new HarmonyMethod(AccessTools.Method(typeof(LoadProgress), nameof(LoadResult))));
				harmony.Patch(AccessTools.Method(typeof(SaveLoader), nameof(SaveLoader.LoadScene)),
					new HarmonyMethod(AccessTools.Method(typeof(LoadProgress), nameof(LoadScenePrefix))));
				harmony.Patch(AccessTools.Method(typeof(SaveManager), "Load", new[] { typeof(IReader) }),
					new HarmonyMethod(AccessTools.Method(typeof(LoadProgress), nameof(ObjectsPrefix))));
				harmony.Patch(AccessTools.Method(typeof(SaveLoadRoot), "Load", new[] { typeof(GameObject), typeof(IReader) }),
					new HarmonyMethod(AccessTools.Method(typeof(LoadProgress), nameof(RootPrefix))),
					new HarmonyMethod(AccessTools.Method(typeof(LoadProgress), nameof(RootPostfix))));
				harmony.Patch(AccessTools.Method(typeof(KMonoBehaviour), nameof(KMonoBehaviour.Spawn)),
					new HarmonyMethod(AccessTools.Method(typeof(LoadProgress), nameof(SpawnPrefix))));
				harmony.Patch(AccessTools.Method(typeof(Game), "OnSpawn"),
					postfix: new HarmonyMethod(AccessTools.Method(typeof(LoadProgress), nameof(GameSpawned))));
				Entrypoint.Log("loadprogress: " + n + " load phases hooked on the main thread; window ghosting off");
			}
			catch (Exception ex)
			{
				Entrypoint.Log("loadprogress: main-thread install failed: " + ex);
			}
		}

		// --- session ---------------------------------------------------------------------------

		private static void Begin(string path)
		{
			if (active)
			{
				return;
			}
			startTicks = Clock.ElapsedTicks;
			saveName = string.IsNullOrEmpty(path) ? "(unknown save)" : Path.GetFileName(path);
			try
			{
				saveBytes = string.IsNullOrEmpty(path) ? 0 : new FileInfo(path).Length;
			}
			catch (Exception)
			{
				saveBytes = 0;
			}
			lock (FinishedLock)
			{
				Finished.Clear();
			}
			groups = new Group[0];
			objectsTotal = objectsDone = 0;
			groupIndex = -1;
			objectsStartTicks = -1;
			rootDepth = 0;
			spawns = 0;
			spawned = false;
			frames = fastFrames = 0;
			phasePath = "loading scene";
			active = true;
			Entrypoint.Log("loadprogress: loading " + saveName + " (" + (saveBytes / 1e6).ToString("0.0") + " MB)");
			try
			{
				overlay = new Overlay();
				overlay.Start();
			}
			catch (Exception ex)
			{
				Entrypoint.Log("loadprogress: overlay failed to start: " + ex);
			}
		}

		private static void End(string why)
		{
			if (!active)
			{
				return;
			}
			active = false;
			Application.onBeforeRender -= OnFrame;
			Entrypoint.Log(string.Format("loadprogress: done ({0}) {1:0.0} s after the load began; {2} objects, {3} spawns, {4} frames after Game.OnSpawn",
				why, Seconds(Clock.ElapsedTicks - startTicks), objectsDone, spawns, frames));
			foreach (Group g in SlowestGroups(10))
			{
				Entrypoint.Log(string.Format("loadprogress:   group {0,-32} n {1,6}  {2,7:0.0} s", g.Name, g.Count, Seconds(g.Ticks)));
			}
			overlay?.Stop();
			overlay = null;
		}

		// Klei catches its own load exceptions and returns false, then falls back to worldgen or
		// the front end; the panel must not outlive that.
		private static void LoadResult(bool __result)
		{
			if (!__result)
			{
				End("SaveLoader.Load returned false");
			}
		}

		private static void LoadScenePrefix()
		{
			Begin(SaveLoader.GetActiveSaveFilePath());
		}

		// --- phases ----------------------------------------------------------------------------

		private static void PhasePrefix(MethodBase __originalMethod, object[] __args, out Phase __state)
		{
			__state = null;
			if (__originalMethod.DeclaringType == typeof(SaveLoader) && __originalMethod.Name == "Load" && __args.Length == 1 && __args[0] is string file)
			{
				Begin(file);
			}
			if (!active || rootDepth > 0)
			{
				return;
			}
			string label = Labels.TryGetValue(__originalMethod, out string l) ? l : __originalMethod.Name;
			__state = new Phase { Name = label, Start = Clock.ElapsedTicks, OuterPath = phasePath };
			phasePath = phasePath.Length == 0 || phasePath == "loading scene" ? label : phasePath + "  >  " + label;
			Pump();
		}

		private static void PhasePostfix(Phase __state)
		{
			if (__state == null || !active)
			{
				return;
			}
			double s = Seconds(Clock.ElapsedTicks - __state.Start);
			phasePath = __state.OuterPath;
			string line = string.Format("{0} {1:0.0} s", __state.Name, s);
			lock (FinishedLock)
			{
				Finished.Add(line);
			}
			Entrypoint.Log(string.Format("loadprogress: phase {0,-20} {1,8:0.00} s  (at {2:0.0} s)", __state.Name, s, Seconds(Clock.ElapsedTicks - startTicks)));
			Pump();
		}

		/// <summary>SaveManager.Load reads "KSAV", the version pair, a group count, then per
		/// group a prefab name, an object count and the group's byte length. Walking those
		/// lengths over the same bytes gives the whole table up front. Read from a copy of the
		/// position, never the game's reader; on anything unexpected the table is just left out.</summary>
		private static void ObjectsPrefix(IReader reader)
		{
			if (!active)
			{
				return;
			}
			try
			{
				byte[] b = reader.RawBytes();
				int p = reader.Position;
				if (b == null || p + 16 > b.Length || b[p] != 'K' || b[p + 1] != 'S' || b[p + 2] != 'A' || b[p + 3] != 'V')
				{
					return;
				}
				p += 12;
				int count = BitConverter.ToInt32(b, p);
				p += 4;
				var table = new List<Group>(Math.Max(0, Math.Min(count, 100000)));
				int total = 0;
				for (int i = 0; i < count; i++)
				{
					int len = BitConverter.ToInt32(b, p);
					p += 4;
					string name = len >= 0 ? Encoding.UTF8.GetString(b, p, len) : "(null)";
					p += Math.Max(len, 0);
					int n = BitConverter.ToInt32(b, p);
					int bytes = BitConverter.ToInt32(b, p + 4);
					p += 8 + bytes;
					if (n < 0 || bytes < 0 || p > b.Length)
					{
						return;
					}
					table.Add(new Group { Name = name, Count = n });
					total += n;
				}
				groups = table.ToArray();
				objectsTotal = total;
				objectsStartTicks = Clock.ElapsedTicks;
				Entrypoint.Log("loadprogress: save holds " + total + " objects in " + groups.Length + " prefab groups");
			}
			catch (Exception ex)
			{
				Entrypoint.Log("loadprogress: prefab table read failed: " + ex.Message);
			}
		}

		// SaveLoadRoot.Load nests (a Storage loads what it holds through it); only the outermost
		// call is one saved object.
		private static void RootPrefix(GameObject prefab)
		{
			if (!active || rootDepth++ != 0)
			{
				return;
			}
			rootStart = Clock.ElapsedTicks;
			Group[] g = groups;
			int gi = groupIndex;
			if (gi >= 0 && gi < g.Length && g[gi].Done < g[gi].Count)
			{
				return;
			}
			string name = prefab != null ? prefab.name : null;
			for (int i = Math.Max(gi + 1, 0); i < g.Length; i++)
			{
				if (g[i].Count > 0 && (name == null || g[i].Name == name))
				{
					groupIndex = i;
					return;
				}
			}
		}

		private static void RootPostfix()
		{
			if (!active || --rootDepth != 0)
			{
				return;
			}
			objectsDone++;
			Group[] g = groups;
			int gi = groupIndex;
			if (gi >= 0 && gi < g.Length)
			{
				g[gi].Done++;
				g[gi].Ticks += Clock.ElapsedTicks - rootStart;
			}
			Pump();
		}

		private static void SpawnPrefix()
		{
			if (!active)
			{
				return;
			}
			spawns++;
			Pump();
		}

		private static void GameSpawned()
		{
			if (!active)
			{
				return;
			}
			spawnTicks = lastFrameTicks = Clock.ElapsedTicks;
			spawned = true;
			phasePath = "first frames";
			Application.onBeforeRender += OnFrame;
		}

		private static void OnFrame()
		{
			long now = Clock.ElapsedTicks;
			lastFrameSeconds = Seconds(now - lastFrameTicks);
			lastFrameTicks = now;
			frames++;
			fastFrames = lastFrameSeconds < 0.25 ? fastFrames + 1 : 0;
			if (fastFrames >= 3)
			{
				End("frames under 250 ms");
			}
			else if (Seconds(now - spawnTicks) > 60)
			{
				End("one minute after Game.OnSpawn");
			}
		}

		/// <summary>Clears the hung-window flag. PM_NOREMOVE leaves every posted message queued;
		/// sent messages (cross-thread SendMessage) are delivered, as any PeekMessage does.</summary>
		private static void Pump()
		{
			long now = Clock.ElapsedTicks;
			if (now - lastPump < PumpTicks)
			{
				return;
			}
			lastPump = now;
			PeekMessage(out MSG _, IntPtr.Zero, 0, 0, PM_NOREMOVE);
		}

		private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;

		private static List<Group> SlowestGroups(int n)
		{
			var list = new List<Group>(groups);
			list.RemoveAll(g => g.Ticks == 0);
			list.Sort((a, b) => b.Ticks.CompareTo(a.Ticks));
			if (list.Count > n)
			{
				list.RemoveRange(n, list.Count - n);
			}
			return list;
		}

		private static string Clock2(double s)
		{
			if (double.IsNaN(s) || double.IsInfinity(s) || s < 0)
			{
				return "--:--";
			}
			int t = (int)s;
			return t >= 3600 ? string.Format("{0}:{1:00}:{2:00}", t / 3600, t / 60 % 60, t % 60) : string.Format("{0}:{1:00}", t / 60, t % 60);
		}

		/// <summary>The panel text. Built on the overlay thread from the fields above.</summary>
		private static List<string> Compose(out double fraction)
		{
			var lines = new List<string>();
			long now = Clock.ElapsedTicks;
			double elapsed = Seconds(now - startTicks);
			lines.Add(string.Format("{0}   {1:0.0} MB   elapsed {2}", saveName, saveBytes / 1e6, Clock2(elapsed)));
			lines.Add("Phase: " + phasePath);

			fraction = -1;
			Group[] g = groups;
			int done = objectsDone;
			if (objectsTotal > 0)
			{
				fraction = done / (double)objectsTotal;
				double objSeconds = objectsStartTicks < 0 ? 0 : Seconds(now - objectsStartTicks);
				double rate = objSeconds > 0 ? done / objSeconds : 0;
				string eta = done >= objectsTotal ? "done" : rate > 0 ? "ETA " + Clock2((objectsTotal - done) / rate) : "ETA --:--";
				lines.Add(string.Format("Objects: {0:N0} / {1:N0}  ({2:0.0}%)   {3:N0} obj/s   {4}", done, objectsTotal, 100 * fraction, rate, eta));
				int gi = groupIndex;
				if (gi >= 0 && gi < g.Length)
				{
					Group c = g[gi];
					lines.Add(string.Format("Group {0} of {1}: {2}   {3:N0} / {4:N0}   {5:0.0} s", gi + 1, g.Length, c.Name, c.Done, c.Count, Seconds(c.Ticks)));
				}
			}
			else
			{
				lines.Add("Objects: waiting for the object table");
			}
			if (spawns > 0)
			{
				lines.Add(string.Format("Components spawned: {0:N0}", spawns));
			}
			if (spawned)
			{
				lines.Add(string.Format("Frames since Game.OnSpawn: {0}   last {1:0.00} s   {2:0.0} s since spawn",
					frames, frames == 0 ? Seconds(now - lastFrameTicks) : lastFrameSeconds, Seconds(now - spawnTicks)));
			}

			long privateBytes = 0;
			try
			{
				using (Process p = Process.GetCurrentProcess())
				{
					privateBytes = p.PrivateMemorySize64;
				}
			}
			catch (Exception)
			{
			}
			lines.Add(string.Format("Memory: private {0:0.00} GB   managed heap {1:0.00} GB   GC {2}/{3}/{4}",
				privateBytes / 1e9, GC.GetTotalMemory(false) / 1e9, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)));

			string finished;
			lock (FinishedLock)
			{
				int from = Math.Max(0, Finished.Count - 6);
				finished = string.Join(",  ", Finished.GetRange(from, Finished.Count - from));
			}
			if (finished.Length > 0)
			{
				lines.Add("Done: " + finished);
			}
			var slow = new List<string>();
			try
			{
				foreach (Group s in SlowestGroups(4))
				{
					slow.Add(string.Format("{0} {1:0.0} s", s.Name, Seconds(s.Ticks)));
				}
			}
			catch (Exception)
			{
				// the main thread swapped the table mid-read; next repaint gets it
			}
			if (slow.Count > 0)
			{
				lines.Add("Slowest groups: " + string.Join(",  ", slow));
			}
			return lines;
		}

		// --- the panel ---------------------------------------------------------------------------

		/// <summary>A top-level, click-through, no-activate popup on its own thread. It never sends
		/// to or owns anything on the game's thread: it finds the game window by process id and
		/// class name, reads its rectangle, and positions itself. GetWindowRect/GetClientRect/
		/// ClientToScreen do not send messages, so a frozen game thread cannot block it.</summary>
		private sealed class Overlay
		{
			private Thread thread;
			private volatile bool stop;
			private IntPtr hwnd;
			private IntPtr game;
			private IntPtr font;
			private int fontHeight;
			private List<string> lines = new List<string>();
			private double fraction = -1;
			private WndProc proc;

			public void Start()
			{
				thread = new Thread(Run) { IsBackground = true, Name = "devpatch load progress" };
				thread.Start();
			}

			public void Stop()
			{
				stop = true;
			}

			private void Run()
			{
				try
				{
					Create();
					while (!stop)
					{
						while (PeekMessage(out MSG msg, IntPtr.Zero, 0, 0, PM_REMOVE))
						{
							TranslateMessage(ref msg);
							DispatchMessage(ref msg);
						}
						Tick();
						Thread.Sleep(100);
					}
				}
				catch (Exception ex)
				{
					Entrypoint.Log("loadprogress: overlay thread: " + ex);
				}
				finally
				{
					if (hwnd != IntPtr.Zero)
					{
						DestroyWindow(hwnd);
					}
					if (font != IntPtr.Zero)
					{
						DeleteObject(font);
					}
				}
			}

			private void Create()
			{
				proc = WindowProc;
				var wc = new WNDCLASSEX
				{
					cbSize = Marshal.SizeOf(typeof(WNDCLASSEX)),
					lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
					hInstance = GetModuleHandle(null),
					lpszClassName = "DevpatchLoadProgress" + Environment.TickCount,
				};
				if (RegisterClassEx(ref wc) == 0)
				{
					throw new InvalidOperationException("RegisterClassEx " + Marshal.GetLastWin32Error());
				}
				hwnd = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TRANSPARENT,
					wc.lpszClassName, "ONI load progress", WS_POPUP, 0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
				if (hwnd == IntPtr.Zero)
				{
					throw new InvalidOperationException("CreateWindowEx " + Marshal.GetLastWin32Error());
				}
				SetLayeredWindowAttributes(hwnd, 0, 235, LWA_ALPHA);
			}

			private void Tick()
			{
				if (game == IntPtr.Zero || !IsWindow(game))
				{
					game = FindGameWindow();
				}
				IntPtr fg = GetForegroundWindow();
				if (game == IntPtr.Zero || IsIconic(game) || (fg != game && fg != hwnd))
				{
					ShowWindow(hwnd, SW_HIDE);
					return;
				}
				lines = Compose(out fraction);

				GetClientRect(game, out RECT client);
				var origin = new POINT();
				ClientToScreen(game, ref origin);
				int cw = client.right - client.left, ch = client.bottom - client.top;
				if (cw <= 0 || ch <= 0)
				{
					return;
				}
				int fh = Math.Max(14, Math.Min(30, ch / 58));
				if (fh != fontHeight)
				{
					if (font != IntPtr.Zero)
					{
						DeleteObject(font);
					}
					font = CreateFont(-fh, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Consolas");
					fontHeight = fh;
				}
				int lineH = fh * 13 / 10, pad = fh;
				int width = Math.Min(cw - 2 * pad, fh * 56);
				int height = 2 * pad + lines.Count * lineH + (fraction >= 0 ? lineH : 0);
				int x = origin.x + (cw - width) / 2;
				int y = origin.y + ch * 62 / 100;
				SetWindowPos(hwnd, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
				InvalidateRect(hwnd, IntPtr.Zero, false);
				UpdateWindow(hwnd);
			}

			private static IntPtr FindGameWindow()
			{
				uint pid = (uint)Process.GetCurrentProcess().Id;
				IntPtr found = IntPtr.Zero;
				var sb = new StringBuilder(64);
				EnumWindows((h, _) =>
				{
					GetWindowThreadProcessId(h, out uint wpid);
					if (wpid != pid || !IsWindowVisible(h))
					{
						return true;
					}
					sb.Length = 0;
					GetClassName(h, sb, sb.Capacity);
					if (sb.ToString() == "UnityWndClass")
					{
						found = h;
						return false;
					}
					return true;
				}, IntPtr.Zero);
				return found;
			}

			private IntPtr WindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
			{
				if (msg == WM_PAINT)
				{
					Paint(h);
					return IntPtr.Zero;
				}
				if (msg == WM_ERASEBKGND)
				{
					return (IntPtr)1;
				}
				return DefWindowProc(h, msg, w, l);
			}

			private void Paint(IntPtr h)
			{
				IntPtr dc = BeginPaint(h, out PAINTSTRUCT ps);
				try
				{
					GetClientRect(h, out RECT r);
					int w = r.right, ht = r.bottom;
					IntPtr mem = CreateCompatibleDC(dc);
					IntPtr bmp = CreateCompatibleBitmap(dc, w, ht);
					IntPtr oldBmp = SelectObject(mem, bmp);
					Fill(mem, 0, 0, w, ht, Rgb(28, 30, 36));
					Fill(mem, 0, 0, w, 2, Rgb(98, 160, 234));
					IntPtr oldFont = SelectObject(mem, font);
					SetBkMode(mem, TRANSPARENT);
					int lineH = fontHeight * 13 / 10, pad = fontHeight, y = pad;
					List<string> text = lines;
					for (int i = 0; i < text.Count; i++)
					{
						SetTextColor(mem, i == 0 ? Rgb(236, 238, 242) : Rgb(178, 184, 196));
						var rc = new RECT { left = pad, top = y, right = w - pad, bottom = y + lineH };
						DrawText(mem, text[i], -1, ref rc, DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX | DT_VCENTER);
						y += lineH;
						if (i == 2 && fraction >= 0)
						{
							int barH = Math.Max(4, lineH / 3), barY = y + (lineH - barH) / 2;
							Fill(mem, pad, barY, w - 2 * pad, barH, Rgb(56, 60, 70));
							Fill(mem, pad, barY, (int)((w - 2 * pad) * Math.Min(1.0, fraction)), barH, Rgb(98, 160, 234));
							y += lineH;
						}
					}
					SelectObject(mem, oldFont);
					BitBlt(dc, 0, 0, w, ht, mem, 0, 0, SRCCOPY);
					SelectObject(mem, oldBmp);
					DeleteObject(bmp);
					DeleteDC(mem);
				}
				finally
				{
					EndPaint(h, ref ps);
				}
			}

			private static void Fill(IntPtr dc, int x, int y, int w, int h, uint color)
			{
				IntPtr brush = CreateSolidBrush(color);
				var rc = new RECT { left = x, top = y, right = x + w, bottom = y + h };
				FillRect(dc, ref rc, brush);
				DeleteObject(brush);
			}

			private static uint Rgb(int r, int g, int b) => (uint)(r | (g << 8) | (b << 16));
		}

		// --- Win32 ---------------------------------------------------------------------------------

		private const uint PM_NOREMOVE = 0, PM_REMOVE = 1;
		private const uint WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014;
		private const uint WS_POPUP = 0x80000000;
		private const uint WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
		private const uint LWA_ALPHA = 0x2;
		private const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
		private const int SW_HIDE = 0, TRANSPARENT = 1;
		private const uint DT_VCENTER = 0x4, DT_SINGLELINE = 0x20, DT_NOPREFIX = 0x800, DT_END_ELLIPSIS = 0x8000;
		private const uint SRCCOPY = 0x00CC0020;
		private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

		private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
		private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

		[StructLayout(LayoutKind.Sequential)]
		private struct MSG
		{
			public IntPtr hwnd;
			public uint message;
			public IntPtr wParam, lParam;
			public uint time;
			public int ptX, ptY;
			public uint lPrivate;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct RECT
		{
			public int left, top, right, bottom;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct POINT
		{
			public int x, y;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct PAINTSTRUCT
		{
			public IntPtr hdc;
			public int fErase;
			public RECT rcPaint;
			public int fRestore, fIncUpdate;
			[MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
			public byte[] rgbReserved;
		}

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		private struct WNDCLASSEX
		{
			public int cbSize;
			public uint style;
			public IntPtr lpfnWndProc;
			public int cbClsExtra, cbWndExtra;
			public IntPtr hInstance, hIcon, hCursor, hbrBackground;
			public string lpszMenuName, lpszClassName;
			public IntPtr hIconSm;
		}

		[DllImport("user32.dll")] private static extern void DisableProcessWindowsGhosting();
		[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
		[DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
		[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessage(ref MSG msg);
		[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
		[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
		[DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
		[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
		[DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
		[DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
		[DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
		[DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
		[DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr hwnd);
		[DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
		[DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
		[DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
		[DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
		[DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
		[DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
		[DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
		[DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
		[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
		[DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);
		[DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
		[DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT rc, IntPtr brush);
		[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawText(IntPtr dc, string text, int len, ref RECT rc, uint format);
		[DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
		[DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
		[DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
		[DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
		[DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
		[DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
		[DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
		[DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
		[DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
		[DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint color);
		[DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
		private static extern IntPtr CreateFont(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike,
			uint charset, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
	}
}
