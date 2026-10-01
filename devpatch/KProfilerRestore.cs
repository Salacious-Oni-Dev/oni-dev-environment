using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Klei;
using UnityEngine;

namespace Doorstop
{
	/// <summary>
	/// The managed half of Klei's KProfiler, put back on the development install. The native half
	/// is the SimDLL's kprofiler exports; oni-sim-replacement's docs/KPROFILER.md has both, the
	/// stream format, and how to read a capture.
	///
	/// WHAT THE SHIPPED GAME LOST. Every KProfiler call site is [Conditional("ENABLE_KPROFILER")],
	/// so the compiler removed them; KProfilerPlugin.Initialized is never set; KProfilerBegin and
	/// KProfilerEnd have empty Update/LateUpdate bodies; the settings.yml scripted profile fires
	/// OnStartCapture/OnStopCapture with nothing subscribed that records. What survived: the
	/// P/Invoke declarations, the scene's KProfilerBegin end-of-frame coroutine, and the
	/// SuperluminalPerf markers Klei left in Game.Update, Game.LateUpdate and Brain.UpdateBrain.
	///
	/// WHAT THIS PUTS BACK, and where each section comes from rather than being guessed:
	///
	///  - Frame: rolled over at KProfilerBegin's end-of-frame tick, the component Klei built for
	///    exactly that, so every other section on the main thread nests inside a frame.
	///  - Klei's own surviving markers, as sections on the methods that carry them: Game.Update,
	///    Game.LateUpdate, and UpdateBrain with the brain's name as its category. The markers
	///    themselves cannot be patched: SuperluminalPerf.BeginEvent pins its strings and calls
	///    through an unmanaged function pointer, and Harmony's method copy cannot declare those
	///    locals (patching them throws ArgumentNullException "localType").
	///    Each section spans its whole method, so Game.Update's also covers the loading check and
	///    AsyncPathProber.TickFrame after Klei's EndEvent, and Game.LateUpdate's the scripted
	///    profile's unpause after its own.
	///  - The fixed parts of a frame: Game.SimEveryTick, Game.UnsafeSim200ms,
	///    StateMachineUpdater.AdvanceOneSimSubTick, Pathfinding.RenderEveryTick,
	///    KAnimBatchManager.Render and UpdateActiveArea.
	///  - The sim worker's kernels, recorded by the SimDLL itself.
	///
	/// PATCH SHAPE AND TIMING. One prefix per section carries that section's name, and one shared
	/// finalizer closes it, exception or not. The sections are patched on the MAIN THREAD, at the
	/// first end-of-frame tick, never from Install. Install runs on Doorstop's background thread,
	/// and patching Game from there runs Game's static constructor on that thread, which sets
	/// `Game.MainThread = Thread.CurrentThread` to the wrong thread for the rest of the session.
	/// Game.IsOnMainThread() is then false on the real main thread, Chore.Precondition.Context
	/// skips every main-thread precondition, and StandardChoreBase.CollectChores adds the
	/// incomplete context to the null list FindNextChore passes. The result is a
	/// NullReferenceException in every brain update, every frame, and duplicants that never pick
	/// a chore. The exceptions' own stacks, logged by the finalizer, are how this was found.
	/// StateMachineUpdater.BucketGroup.AdvanceOneSubTick is not patched: its time is inside
	/// AdvanceOneSimSubTick.
	///
	/// CONTROLS. Alt+` (ToggleChromeProfiler, which Klei bound and never handled) starts and
	/// stops a capture to DevData/kprofile/&lt;time&gt;.kprof. The settings.yml scripted profile
	/// captures between its start and stop. `devpatch_kprofiler.off` turns all of it off.
	///
	/// LISTENER MODE. A file `devpatch_kprofiler.port` holding a port number starts Klei's HTTP
	/// control listener on 127.0.0.1 (/start /stop /ping /syncstrings). /start and /stop change
	/// the SimDLL's state from the listener's thread, which this side cannot see, so in this mode
	/// the hooks call the SimDLL on every pass, as Klei's KProfiler did behind
	/// KProfilerPlugin.Initialized, and the SimDLL drops what arrives while it is not running.
	/// Recorded data needs somewhere to go, and nothing outside the process can attach a sender,
	/// so one file, DevData/kprofile/&lt;time&gt;-session.kprof, is opened at boot and receives every
	/// capture of the session, however it was started, one after another; it is closed when the
	/// game closes. Alt+` in this mode starts and stops profiling into that file.
	///
	/// Idle cost is one bool test per hooked call, and nothing is recorded outside a capture.
	/// Listener mode costs a native call per hooked call, and a name lookup per brain update.
	/// </summary>
	internal static class KProfilerRestore
	{
		private static bool available;
		private static Harmony sectionHarmony;
		private static bool sectionsInstalled;
		private static volatile bool capturing;
		/// <summary>devpatch_kprofiler.port: the hooks always call the SimDLL (see LISTENER MODE).</summary>
		private static bool listening;
		private static string sessionPath;

		private static bool Recording => capturing || listening;
		private static string capturePath;
		private static int frameOpenFrame = -1;
		private static bool frameOpen;

		private static ulong frameName;
		private static ulong frameCategory;
		private static ulong updateBrainName;
		private static readonly Dictionary<string, ulong> strings = new Dictionary<string, ulong>();

		/// <summary>Type, method, and the prefix that opens its section (index into sectionIds).</summary>
		private static readonly string[] Sections = new string[]
		{
			"Game", "Update", nameof(Prefix0),
			"Game", "LateUpdate", nameof(Prefix1),
			"Game", "SimEveryTick", nameof(Prefix2),
			"Game", "UnsafeSim200ms", nameof(Prefix3),
			"StateMachineUpdater", "AdvanceOneSimSubTick", nameof(Prefix4),
			"Pathfinding", "RenderEveryTick", nameof(Prefix5),
			"KAnimBatchManager", "Render", nameof(Prefix6),
			"KAnimBatchManager", "UpdateActiveArea", nameof(Prefix7),
		};

		private static readonly ulong[] sectionIds = new ulong[Sections.Length / 3];

		internal static void Install(Harmony harmony)
		{
			if (File.Exists("devpatch_kprofiler.off"))
			{
				Entrypoint.Log("kprofiler: disabled by devpatch_kprofiler.off");
				return;
			}
			try
			{
				// A SimDLL without the exports (Klei's is fine; an older replacement is not)
				// throws here, once, instead of from inside a frame later.
				KProfilerPlugin.kprofiler_load_plugin();
				frameName = Id("Frame");
				frameCategory = Id("Frame");
				updateBrainName = Id("UpdateBrain");
			}
			catch (Exception ex)
			{
				Entrypoint.Log("kprofiler: the installed SimDLL has no working kprofiler exports, restore off: " + ex.GetType().Name);
				return;
			}

			for (int i = 0; i < sectionIds.Length; i++)
			{
				sectionIds[i] = Id(Sections[i * 3] + "." + Sections[i * 3 + 1]);
			}

			// The frame tick is the one patch made here: it is not on Game, and it is the main-thread
			// place the sections get patched from (see PATCH SHAPE AND TIMING).
			sectionHarmony = harmony;
			if (!TryPatch(harmony, typeof(KProfilerBegin), "TickScriptedProfile", Hook(nameof(FrameTickPrefix)), null))
			{
				Entrypoint.Log("kprofiler: no frame tick, restore off");
				return;
			}
			KProfilerBegin.OnStartCapture = (System.Action)Delegate.Combine(KProfilerBegin.OnStartCapture, new System.Action(() => Start("scripted")));
			KProfilerBegin.OnStopCapture = (System.Action)Delegate.Combine(KProfilerBegin.OnStopCapture, new System.Action(Stop));
			// Closing the game with a capture running would leave the file cut off mid-record and
			// without its string table, so every name would read as a hex id.
			Application.quitting += Quitting;

			string portFile = "devpatch_kprofiler.port";
			if (File.Exists(portFile))
			{
				if (int.TryParse(File.ReadAllText(portFile).Trim(), out int port) && port > 0 && port < 65536)
				{
					sessionPath = NewCapturePath("-session");
					KProfilerPlugin.kprofiler_start_file_data_sender(sessionPath);
					KProfilerPlugin.kprofiler_start_http_control_listener(port);
					listening = true;
					Entrypoint.Log("kprofiler: HTTP control listener on 127.0.0.1:" + port + ", every capture this session goes to " + sessionPath);
				}
				else
				{
					Entrypoint.Log("kprofiler: " + portFile + " does not hold a port number, no listener");
				}
			}
			available = true;
			KProfilerPlugin.Initialized = true;
			Entrypoint.Log("kprofiler: installed frame tick; sections are patched on the main thread at the first tick");
		}

		/// <summary>Main thread only: called from the first end-of-frame tick.</summary>
		private static void InstallSections()
		{
			sectionsInstalled = true;
			if (listening)
			{
				// Start names the main thread for its own captures; a /start never passes through it.
				KProfilerPlugin.kprofiler_set_thread_info(KProfilerPlugin.kprofiler_get_thread_uid(), Id("Main thread"), Id("Unity"));
			}
			// Each patch on its own: one target Harmony cannot wrap costs that section, not the rest.
			HarmonyMethod finalizer = Hook(nameof(SectionFinalizer));
			int patched = 0;
			int failed = 0;
			for (int i = 0; i < sectionIds.Length; i++)
			{
				if (TryPatch(sectionHarmony, AccessTools.TypeByName(Sections[i * 3]), Sections[i * 3 + 1], Hook(Sections[i * 3 + 2]), finalizer))
				{
					patched++;
				}
				else
				{
					failed++;
				}
			}
			if (TryPatch(sectionHarmony, typeof(Brain), nameof(Brain.UpdateBrain), Hook(nameof(BrainPrefix)), finalizer))
			{
				patched++;
			}
			else
			{
				failed++;
			}
			// The positive control for the failure this timing exists to avoid.
			Entrypoint.Log("kprofiler: " + patched + " sections patched" + (failed > 0 ? ", " + failed + " could not be" : "")
				+ " at frame " + Time.frameCount + "; Game.IsOnMainThread()=" + Game.IsOnMainThread() + " (must be True)");
		}

		private static HarmonyMethod Hook(string name)
		{
			return new HarmonyMethod(AccessTools.Method(typeof(KProfilerRestore), name));
		}

		private static bool TryPatch(Harmony harmony, Type type, string methodName, HarmonyMethod prefix, HarmonyMethod finalizer)
		{
			MethodInfo method = type == null ? null : AccessTools.Method(type, methodName);
			if (method == null)
			{
				Entrypoint.Log("kprofiler: no " + (type != null ? type.FullName : "?") + "." + methodName);
				return false;
			}
			try
			{
				harmony.Patch(method, prefix: prefix, finalizer: finalizer);
				return true;
			}
			catch (Exception ex)
			{
				Entrypoint.Log("kprofiler: could not patch " + type.FullName + "." + methodName + ": " + ex.GetType().Name + ": " + ex.Message);
				return false;
			}
		}

		private static ulong Id(string name)
		{
			lock (strings)
			{
				if (!strings.TryGetValue(name, out ulong id))
				{
					id = KProfilerPlugin.kprofile_record_string(name);
					strings[name] = id;
				}
				return id;
			}
		}

		/// <summary>Alt+`: start a capture, or stop the one running.</summary>
		internal static void Toggle()
		{
			if (!available)
			{
				Say("kprofiler: not available (see devpatch.log)");
				return;
			}
			if (capturing)
			{
				Stop();
			}
			else
			{
				Start("key");
			}
		}

		private static string NewCapturePath(string suffix)
		{
			string dir = Path.GetFullPath(Path.Combine("DevData", "kprofile"));
			Directory.CreateDirectory(dir);
			return Path.Combine(dir, System.DateTime.Now.ToString("yyyyMMdd-HHmmss") + suffix + ".kprof");
		}

		private static void Start(string reason)
		{
			if (!available || capturing)
			{
				return;
			}
			if (listening)
			{
				capturePath = sessionPath;
			}
			else
			{
				capturePath = NewCapturePath("");
				KProfilerPlugin.kprofiler_start_file_data_sender(capturePath);
			}
			KProfilerPlugin.kprofiler_set_thread_info(KProfilerPlugin.kprofiler_get_thread_uid(), Id("Main thread"), Id("Unity"));
			frameOpen = false;
			KProfilerPlugin.kprofiler_start_profiling();
			capturing = true;
			Say("kprofiler: capturing (" + reason + ") to " + capturePath);
		}

		private static void Stop()
		{
			if (!available || !capturing)
			{
				return;
			}
			capturing = false;
			if (frameOpen)
			{
				KProfilerPlugin.kprofiler_end_section(-1);
				frameOpen = false;
			}
			KProfilerPlugin.kprofiler_stop_profiling(1);
			if (!listening)
			{
				KProfilerPlugin.kprofiler_stop_data_sender();
			}
			long bytes = File.Exists(capturePath) ? new FileInfo(capturePath).Length : -1;
			Say("kprofiler: capture written, " + bytes + " bytes: " + capturePath
				+ " (python3 oni-sim-replacement/tools/kprofile2chrome.py <file> for chrome://tracing)");
		}

		private static void Quitting()
		{
			Stop();
			if (listening)
			{
				// A capture a /start began is invisible to Stop. Stopping when none is running only
				// appends one more thread and string table, which a reader merges.
				KProfilerPlugin.kprofiler_stop_profiling(1);
				KProfilerPlugin.kprofiler_stop_data_sender();
			}
		}

		private static void Say(string message)
		{
			Debug.Log("[kprofiler] " + message);
			Entrypoint.Log("[" + System.DateTime.Now.ToString("HH:mm:ss.fff") + "] " + message);
		}

		/// <summary>End of frame: close the frame just rendered and open the next. Once per frame
		/// however many KProfilerBegin components tick.</summary>
		private static void FrameTickPrefix()
		{
			if (!sectionsInstalled)
			{
				InstallSections();
			}
			if (!Recording || frameOpenFrame == Time.frameCount)
			{
				return;
			}
			frameOpenFrame = Time.frameCount;
			if (frameOpen)
			{
				KProfilerPlugin.kprofiler_end_section(-1);
			}
			KProfilerPlugin.kprofiler_begin_section(frameName, frameCategory, -1);
			frameOpen = true;
		}

		private static bool Begin(int section)
		{
			if (!Recording)
			{
				return false;
			}
			KProfilerPlugin.kprofiler_begin_section(sectionIds[section], frameCategory, -1);
			return true;
		}

		private static void Prefix0(out bool __state) { __state = Begin(0); }
		private static void Prefix1(out bool __state) { __state = Begin(1); }
		private static void Prefix2(out bool __state) { __state = Begin(2); }
		private static void Prefix3(out bool __state) { __state = Begin(3); }
		private static void Prefix4(out bool __state) { __state = Begin(4); }
		private static void Prefix5(out bool __state) { __state = Begin(5); }
		private static void Prefix6(out bool __state) { __state = Begin(6); }
		private static void Prefix7(out bool __state) { __state = Begin(7); }

		/// <summary>Klei's marker: BeginEvent("UpdateBrain", base.name).</summary>
		private static void BrainPrefix(Brain __instance, out bool __state)
		{
			__state = Recording;
			if (__state)
			{
				KProfilerPlugin.kprofiler_begin_section(updateBrainName, Id(__instance.name), -1);
			}
		}

		private static int exceptionsLogged;

		private static Exception SectionFinalizer(Exception __exception, bool __state)
		{
			if (__state)
			{
				KProfilerPlugin.kprofiler_end_section(-1);
			}
			// Harmony rethrows what a finalizer returns, and the rethrow is where Unity's log says it
			// came from. The first few keep their real stack here.
			if (__exception != null && exceptionsLogged < 5)
			{
				exceptionsLogged++;
				Entrypoint.Log("kprofiler: exception passing a section (original stack): " + __exception);
			}
			return __exception;
		}
	}
}
