using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace Doorstop
{
	/// <summary>
	/// Makes the debug keys the shipped game binds but cannot use do something, on the development
	/// install only. On by default like the boot skips; `devpatch_debugkeys.off` next to the exe
	/// puts the stock handler back. Every key here still needs debug mode (DebugHandler.enabled),
	/// exactly like Klei's own, and a consumed key still marks the save debugWasUsed.
	///
	/// Three kinds of repair, and the repository README lists every key:
	///
	///  - BINDINGS THAT CAN NEVER MATCH. A plain ` is itself the Backtick modifier, and
	///    KInputController matches modifiers exactly, so `BackQuote + None` (ToggleProfiler) and
	///    `BackQuote + Alt` (ToggleChromeProfiler) never fire. They move to Shift+` and Alt+`,
	///    which the input system can produce. A bare ` stays free because ` + digit switches worlds.
	///    Klei never handled ToggleChromeProfiler at all; Alt+` now starts and stops a KProfiler
	///    capture (KProfilerRestore.cs). Shift+` is still the SimDLL's own kernel profiler.
	///
	///  - ACTIONS WITH NO KEY. Klei's handlers for cell info, 15x speed, the visual and gameplay
	///    scenarios, the 60-minion stress test, bug report and quick dev actions exist but have no
	///    default binding; they get unused combinations.
	///
	///  - ACTIONS WHOSE BODY WAS REMOVED. DebugFocus, DebugPlace, DebugSelectMaterial,
	///    DebugToggleSelectInEditor, the partitioner leak dump and QuickDevActions are consumed and
	///    do nothing, and InvincibleMode is a flag nothing reads. Each gets the behaviour its name
	///    describes. DebugReportBug is replaced outright: Klei's uploads
	///    to Klei, and a report from a replaced SimDLL is not theirs to receive, so this one writes a
	///    local bundle instead.
	///
	/// One key is not Klei's at all: Alt+L, on `Action.DebugRiverTest`, which the shipped game
	/// neither binds nor handles. It writes the cell-event log (CellEventLog.cs).
	/// </summary>
	internal static class DebugKeys
	{
		private static readonly Modifier ShiftBacktick = Modifier.Shift | Modifier.Backtick;
		private static readonly Modifier AltBacktick = Modifier.Alt | Modifier.Backtick;
		private static readonly Modifier CtrlShift = Modifier.Ctrl | Modifier.Shift;

		internal static void Install(Harmony harmony)
		{
			if (File.Exists("devpatch_debugkeys.off"))
			{
				Entrypoint.Log("debugkeys: disabled by devpatch_debugkeys.off");
				return;
			}
			harmony.Patch(AccessTools.Method(typeof(Global), nameof(Global.GenerateDefaultBindings)),
				postfix: new HarmonyMethod(AccessTools.Method(typeof(DebugKeys), nameof(BindingsPostfix))));
			harmony.Patch(AccessTools.Method(typeof(DebugHandler), nameof(DebugHandler.OnKeyDown)),
				prefix: new HarmonyMethod(AccessTools.Method(typeof(DebugKeys), nameof(OnKeyDownPrefix))));
			harmony.Patch(AccessTools.Method(typeof(Health), nameof(Health.Damage)),
				prefix: new HarmonyMethod(AccessTools.Method(typeof(DebugKeys), nameof(HealthPrefix))));
			harmony.Patch(AccessTools.Method(typeof(Health), nameof(Health.Incapacitate)),
				prefix: new HarmonyMethod(AccessTools.Method(typeof(DebugKeys), nameof(HealthPrefix))));
			harmony.Patch(AccessTools.Method(typeof(DeathMonitor.Instance), nameof(DeathMonitor.Instance.Kill)),
				prefix: new HarmonyMethod(AccessTools.Method(typeof(DebugKeys), nameof(KillPrefix))));
			Entrypoint.Log("debugkeys: installed");
		}

		// ------------------------------------------------------------------ bindings

		private static void BindingsPostfix(ref BindingEntry[] __result)
		{
			List<BindingEntry> list = new List<BindingEntry>(__result);
			for (int i = 0; i < list.Count; i++)
			{
				BindingEntry entry = list[i];
				if (entry.mKeyCode != KKeyCode.BackQuote)
				{
					continue;
				}
				if (entry.mAction == Action.ToggleProfiler)
				{
					entry.mModifier = ShiftBacktick;
				}
				else if (entry.mAction == Action.ToggleChromeProfiler)
				{
					entry.mModifier = AltBacktick;
				}
				list[i] = entry;
			}
			Add(list, KKeyCode.I, Modifier.Alt, Action.DebugCellInfo);
			Add(list, KKeyCode.U, CtrlShift, Action.DebugSuperTestMode);
			Add(list, KKeyCode.F11, Modifier.Shift, Action.DebugVisualTest);
			Add(list, KKeyCode.F9, Modifier.Shift, Action.DebugGameplayTest);
			Add(list, KKeyCode.F2, CtrlShift, Action.DebugSpawnStressTest);
			Add(list, KKeyCode.B, Modifier.Alt, Action.DebugReportBug);
			Add(list, KKeyCode.D, Modifier.Alt, Action.DebugQuickDevActions);
			// DebugRiverTest is the one Debug action with neither a handler nor a binding left in
			// the shipped game, so it carries the cell-event dump (CellEventLog.cs).
			Add(list, KKeyCode.L, Modifier.Alt, Action.DebugRiverTest);
			__result = list.ToArray();
		}

		/// <summary>Adds a Debug-group binding unless the action already has one -- a later Klei
		/// build that binds it itself wins.</summary>
		private static void Add(List<BindingEntry> list, KKeyCode key, Modifier modifier, Action action)
		{
			foreach (BindingEntry entry in list)
			{
				if (entry.mAction == action)
				{
					return;
				}
			}
			list.Add(new BindingEntry("Debug", GamepadButton.NumButtons, key, modifier, action));
		}

		// ------------------------------------------------------------------ key handler

		private static bool OnKeyDownPrefix(DebugHandler __instance, KButtonEvent e)
		{
			if (!DebugHandler.enabled)
			{
				return true;
			}
			try
			{
				if (e.TryConsume(Action.DebugFocus))
				{
					Focus();
				}
				else if (e.TryConsume(Action.DebugPlace))
				{
					Place(__instance);
				}
				else if (e.TryConsume(Action.DebugSelectMaterial))
				{
					EyeDrop();
				}
				else if (e.TryConsume(Action.DebugToggleSelectInEditor))
				{
					OpenDevTool("Entity Debug", () => DevToolManager.Instance.panels.AddOrGetDevTool<DevToolEntityDebug>());
				}
				else if (e.TryConsume(Action.DebugQuickDevActions))
				{
					OpenDevTool("Command Palette", DevToolCommandPalette.Init);
				}
				else if (e.TryConsume(Action.DebugDumpSceneParitionerLeakData))
				{
					DumpPartitioner();
				}
				else if (e.TryConsume(Action.DebugInvincible))
				{
					DebugHandler.InvincibleMode = !DebugHandler.InvincibleMode;
					Say("InvincibleMode=" + DebugHandler.InvincibleMode + " (duplicants take no damage and cannot die)");
				}
				else if (e.TryConsume(Action.DebugReportBug))
				{
					ReportBug();
				}
				else if (e.TryConsume(Action.ToggleChromeProfiler))
				{
					KProfilerRestore.Toggle();
				}
				else if (e.TryConsume(Action.DebugRiverTest))
				{
					CellEventLog.Dump("key");
				}
				else
				{
					return true;
				}
			}
			catch (Exception ex)
			{
				Say("debugkeys: " + ex);
			}
			if (Game.Instance != null)
			{
				Game.Instance.debugWasUsed = true;
				KCrashReporter.debugWasUsed = true;
			}
			return false;
		}

		/// <summary>Logs to both Player.log and devpatch.log, so a key press is visible wherever the
		/// run is being read.</summary>
		private static void Say(string message)
		{
			Debug.Log("[debugkeys] " + message);
			Entrypoint.Log("[" + System.DateTime.Now.ToString("HH:mm:ss.fff") + "] debugkeys: " + message);
		}

		private static KSelectable Selected()
		{
			return SelectTool.Instance == null ? null : SelectTool.Instance.selected;
		}

		/// <summary>Ctrl+T: centre the camera on the selected object.</summary>
		private static void Focus()
		{
			KSelectable selected = Selected();
			if (selected == null || CameraController.Instance == null)
			{
				Say("DebugFocus: nothing selected");
				return;
			}
			Vector3 pos = selected.transform.GetPosition();
			CameraController.Instance.CameraGoTo(pos, 2f, playSound: false);
			Say("DebugFocus: camera to " + selected.name + " at cell " + Grid.PosToCell(pos));
		}

		/// <summary>
		/// Ctrl+F3: put a copy of the selected object at the mouse cell. A building is built from
		/// its def in the same element and temperature; a duplicant goes through Klei's own
		/// SpawnMinion, which sets up an identity; anything else is instantiated from its prefab
		/// and given the source's element, mass and temperature.
		/// </summary>
		private static void Place(DebugHandler handler)
		{
			KSelectable selected = Selected();
			int cell = DebugHandler.GetMouseCell();
			if (selected == null || !Grid.IsValidCell(cell))
			{
				Say("DebugPlace: select something and point at a cell");
				return;
			}
			PrimaryElement source = selected.GetComponent<PrimaryElement>();
			BuildingComplete building = selected.GetComponent<BuildingComplete>();
			if (building != null)
			{
				Rotatable rotatable = selected.GetComponent<Rotatable>();
				Orientation orientation = rotatable != null ? rotatable.GetOrientation() : Orientation.Neutral;
				Tag[] elements = { source != null ? source.Element.tag : SimHashes.Steel.CreateTag() };
				float temperature = source != null ? source.Temperature : 293.15f;
				GameObject built = building.Def.Build(cell, orientation, null, elements, temperature, playsound: false, GameClock.Instance.GetTime());
				Say("DebugPlace: built " + building.Def.PrefabID + " at cell " + cell + (built == null ? " (failed)" : ""));
				return;
			}
			if (selected.GetComponent<MinionIdentity>() != null)
			{
				AccessTools.Method(typeof(DebugHandler), "SpawnMinion").Invoke(handler, new object[] { false });
				Say("DebugPlace: spawned a duplicant at cell " + cell);
				return;
			}
			GameObject prefab = Assets.GetPrefab(selected.PrefabID());
			if (prefab == null)
			{
				Say("DebugPlace: no prefab for " + selected.PrefabID());
				return;
			}
			GameObject go = GameUtil.KInstantiate(prefab, Grid.CellToPosCBC(cell, Grid.SceneLayer.Creatures), Grid.SceneLayer.Creatures);
			go.SetActive(true);
			PrimaryElement copy = go.GetComponent<PrimaryElement>();
			if (copy != null && source != null)
			{
				copy.ElementID = source.ElementID;
				copy.Mass = source.Mass;
				copy.Temperature = source.Temperature;
			}
			Say("DebugPlace: placed " + selected.PrefabID() + " at cell " + cell);
		}

		/// <summary>Ctrl+S: the element under the mouse becomes the paint screen's element.</summary>
		private static void EyeDrop()
		{
			int cell = DebugHandler.GetMouseCell();
			if (!Grid.IsValidCell(cell))
			{
				return;
			}
			SimHashes element = Grid.Element[cell].id;
			DebugPaintElementScreen screen = DebugPaintElementScreen.Instance;
			if (screen == null)
			{
				Say("DebugSelectMaterial: " + element + " under the mouse, but the paint screen does not exist (Backspace opens it)");
				return;
			}
			AccessTools.Method(typeof(DebugPaintElementScreen), "OnSelectElement", new[] { typeof(SimHashes) }).Invoke(screen, new object[] { element });
			Say("DebugSelectMaterial: paint element = " + element + " from cell " + cell);
		}

		/// <summary>
		/// Alt+T and Alt+D: open a DevTools panel. The DevTools warning is the player's to accept,
		/// so it is not accepted here -- until it is, the menu opens on the warning.
		/// </summary>
		private static void OpenDevTool(string name, System.Action open)
		{
			DevToolManager manager = DevToolManager.Instance;
			if (manager == null)
			{
				Say(name + ": no DevToolManager");
				return;
			}
			AccessTools.Field(typeof(DevToolManager), "showImGui").SetValue(manager, true);
			if (!manager.UserAcceptedWarning)
			{
				Say(name + ": DevTools warning not accepted yet; accept it, then press again");
				return;
			}
			open();
			Say(name + ": opened");
		}

		/// <summary>
		/// Ctrl+F1: every live scene-partitioner entry, per layer, and how many belong to an object
		/// that has been destroyed -- the leak the action is named for. An entry whose object or
		/// callback target is a destroyed UnityEngine.Object was never freed by its owner.
		/// </summary>
		private static void DumpPartitioner()
		{
			GameScenePartitioner gsp = GameScenePartitioner.Instance;
			if (gsp == null)
			{
				Say("partitioner: no GameScenePartitioner");
				return;
			}
			ScenePartitioner partitioner = (ScenePartitioner)AccessTools.Field(typeof(GameScenePartitioner), "partitioner").GetValue(gsp);
			KCompactedVector<ScenePartitionerEntry> entries = (KCompactedVector<ScenePartitionerEntry>)AccessTools.Field(typeof(GameScenePartitioner), "scenePartitionerEntries").GetValue(gsp);
			int layerCount = partitioner.layers.Count;
			int[] total = new int[layerCount];
			int[] dead = new int[layerCount];
			int all = 0;
			int allDead = 0;
			foreach (ScenePartitionerEntry entry in entries.GetDataList())
			{
				if (entry == null || entry.layer < 0 || entry.layer >= layerCount)
				{
					continue;
				}
				all++;
				total[entry.layer]++;
				if (IsDestroyed(entry.obj) || (entry.eventCallback != null && IsDestroyed(entry.eventCallback.Target)))
				{
					dead[entry.layer]++;
					allDead++;
				}
			}
			StringBuilder sb = new StringBuilder();
			sb.Append("partitioner: ").Append(all).Append(" entries, ").Append(allDead).Append(" on destroyed objects");
			for (int i = 0; i < layerCount; i++)
			{
				if (total[i] == 0)
				{
					continue;
				}
				sb.Append("\n  ").Append(partitioner.layers[i].name).Append(": ").Append(total[i]);
				if (dead[i] > 0)
				{
					sb.Append(" (").Append(dead[i]).Append(" dead)");
				}
			}
			Say(sb.ToString());
		}

		private static bool IsDestroyed(object obj)
		{
			UnityEngine.Object unity = obj as UnityEngine.Object;
			return !ReferenceEquals(unity, null) && unity == null;
		}

		/// <summary>
		/// Alt+B: a local bug bundle in DevData/bug-reports/&lt;time&gt;/ -- a save, Player.log,
		/// devpatch.log, a screenshot and a note of what was selected and where the mouse was.
		/// Nothing is sent anywhere.
		/// </summary>
		private static void ReportBug()
		{
			string dir = Path.GetFullPath(Path.Combine("DevData", "bug-reports", System.DateTime.Now.ToString("yyyyMMdd-HHmmss")));
			Directory.CreateDirectory(dir);
			if (SaveLoader.Instance != null)
			{
				SaveLoader.Instance.Save(Path.Combine(dir, "bug.sav"), isAutoSave: false, updateSavePointer: false);
			}
			CopyShared(Application.consoleLogPath, Path.Combine(dir, "Player.log"));
			CopyShared(Path.GetFullPath(Entrypoint.LogFile), Path.Combine(dir, "devpatch.log"));
			ScreenCapture.CaptureScreenshot(Path.Combine(dir, "screen.png"));
			KSelectable selected = Selected();
			int cell = DebugHandler.GetMouseCell();
			File.WriteAllText(Path.Combine(dir, "note.txt"),
				"time " + System.DateTime.Now.ToString("o") + "\n"
				+ "cycle " + (GameClock.Instance != null ? GameClock.Instance.GetCycle().ToString() : "-") + "\n"
				+ "selected " + (selected != null ? selected.name + " at cell " + Grid.PosToCell(selected.transform.GetPosition()) : "none") + "\n"
				+ "mouse cell " + cell + (Grid.IsValidCell(cell) ? " " + Grid.Element[cell].id + " " + Grid.Mass[cell] + " kg " + Grid.Temperature[cell] + " K" : "") + "\n");
			Say("ReportBug: bundle written to " + dir);
		}

		/// <summary>Copies a file another process is still writing (Unity holds Player.log open).</summary>
		private static void CopyShared(string from, string to)
		{
			if (string.IsNullOrEmpty(from) || !File.Exists(from))
			{
				return;
			}
			using (FileStream input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
			using (FileStream output = File.Create(to))
			{
				input.CopyTo(output);
			}
		}

		// ------------------------------------------------------------------ invincible

		private static bool IsProtected(Component component)
		{
			return DebugHandler.InvincibleMode && component != null && component.GetComponent<MinionIdentity>() != null;
		}

		private static bool HealthPrefix(Health __instance)
		{
			return !IsProtected(__instance);
		}

		private static bool KillPrefix(DeathMonitor.Instance __instance)
		{
			return !IsProtected(__instance.gameObject.transform);
		}
	}
}
