using System;
using System.IO;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace Doorstop
{
	/// <summary>
	/// Klei's CellEventLogger, made to record again on the development install. Opt-in: nothing
	/// here is patched unless `devpatch_celllog.on` sits next to the exe.
	///
	/// WHAT THE SHIPPED GAME LOST, and what it kept. `CellEventLogger` is a live KMonoBehaviour
	/// in every game: its OnPrefabInit is not conditional, so all ~80 event objects exist, each
	/// with the reason string Klei gave it ("Mop", "Meteor", "Element Consumer SimUpdate", ...).
	/// What vanished is the recording: every `Log` method on the CellEvent classes is
	/// [Conditional("ENABLE_CELL_EVENT_LOGGER")], so the compiler removed the calls to them. The
	/// 101 call sites still pass their event object as an ARGUMENT, and arguments are not
	/// conditional, so the reason still arrives at the method that changes the cell. Nothing in
	/// the shipped game reads the log either: outside its own file, the only reference left is
	/// `Game.OnCleanUp` calling `CellEventLogger.DestroyInstance()`.
	///
	/// So this does not chase the call sites. It patches the five methods the reasons arrive at:
	///
	///  - SimMessages.AddRemoveSubstance(int, ushort, ...)  every mass added to or taken from a
	///    cell. The SimHashes overload forwards to this one, so patching this one alone counts
	///    each call once.
	///  - SimMessages.ReplaceElement and ReplaceAndDisplaceElement  a cell's element replaced.
	///  - SimMessages.ModifyMass  mass added to a cell, keeping its element.
	///  - Grid.SetSolid  a cell becoming solid or not.
	///
	/// That is the question the tool exists to answer: WHICH system changed this cell, with what,
	/// and how much. Klei's own dig and callback events are not covered; SimMessages.Dig takes no
	/// event, and the callback map only pairs a callback id with a cell.
	///
	/// NOT KLEI'S BUFFER. `EventLogger.Add` puts instances in a list marked [Serialize], so
	/// feeding it would write every recorded event into the save file. This keeps its own ring
	/// of the last 10,000 events (Klei's own cap) in memory, and writes them out on request.
	///
	/// PATCH TIMING. The sinks are patched on the main thread at the first end-of-frame tick, not
	/// from Install: patching runs the target type's static constructor on the calling thread, and
	/// devpatch installs from Doorstop's background thread. KProfilerRestore.cs has what that cost
	/// the first live runs.
	///
	/// CONTROLS. `devpatch_celllog.on` turns it on. `devpatch_celllog.cell`, holding `x,y` or a
	/// cell index, records only that cell. Alt+L writes the ring to DevData/cell-events/, and so
	/// does closing the game.
	/// </summary>
	internal static class CellEventLog
	{
		private const int Capacity = 10000;

		private static readonly object Lock = new object();
		private static readonly string[] reasons = new string[Capacity];
		private static readonly int[] cells = new int[Capacity];
		private static readonly int[] elements = new int[Capacity];
		private static readonly float[] amounts = new float[Capacity];
		private static readonly int[] frames = new int[Capacity];
		private static int next;
		private static int recorded;

		private static bool armed;
		private static bool sinksInstalled;
		private static Harmony sinkHarmony;
		/// <summary>Grid.InvalidCell is -1; this stays -1 unless devpatch_celllog.cell names one.</summary>
		private static int watchedCell = -1;
		private static int watchedX;
		private static int watchedY;

		internal static void Install(Harmony harmony)
		{
			if (!File.Exists("devpatch_celllog.on"))
			{
				return;
			}
			string cellFile = "devpatch_celllog.cell";
			if (File.Exists(cellFile))
			{
				string text = File.ReadAllText(cellFile).Trim();
				string[] parts = text.Split(',');
				if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out int x) && int.TryParse(parts[1].Trim(), out int y))
				{
					watchedCell = -2; // resolved on the main thread, where Grid.WidthInCells is set
					watchedX = x;
					watchedY = y;
				}
				else if (int.TryParse(text, out int cell))
				{
					watchedCell = cell;
				}
				else
				{
					Entrypoint.Log("celllog: " + cellFile + " holds neither `x,y` nor a cell index, recording every cell");
				}
			}
			sinkHarmony = harmony;
			// The same main-thread tick KProfilerRestore uses, and for the same reason.
			System.Reflection.MethodInfo tick = AccessTools.Method(typeof(KProfilerBegin), "TickScriptedProfile");
			if (tick == null)
			{
				Entrypoint.Log("celllog: no KProfilerBegin.TickScriptedProfile to patch the sinks from, off");
				return;
			}
			harmony.Patch(tick, prefix: new HarmonyMethod(AccessTools.Method(typeof(CellEventLog), nameof(TickPrefix))));
			Application.quitting += Quitting;
			armed = true;
			Entrypoint.Log("celllog: armed" + (watchedCell == -1 ? ", every cell" : ", one cell only")
				+ "; sinks are patched on the main thread at the first tick, Alt+L dumps");
		}

		private static void TickPrefix()
		{
			if (sinksInstalled)
			{
				return;
			}
			sinksInstalled = true;
			if (watchedCell == -2)
			{
				watchedCell = Grid.XYToCell(watchedX, watchedY);
				Entrypoint.Log("celllog: watching cell " + watchedCell + " (" + watchedX + "," + watchedY + ")");
			}
			int patched = 0;
			patched += Patch(typeof(SimMessages), "AddRemoveSubstance", nameof(AddRemoveSubstancePrefix),
				new Type[] { typeof(int), typeof(ushort), typeof(CellAddRemoveSubstanceEvent), typeof(float), typeof(float), typeof(byte), typeof(int), typeof(bool), typeof(int) });
			patched += Patch(typeof(SimMessages), "ReplaceElement", nameof(ReplaceElementPrefix), null);
			patched += Patch(typeof(SimMessages), "ReplaceAndDisplaceElement", nameof(ReplaceElementPrefix), null);
			patched += Patch(typeof(SimMessages), "ModifyMass", nameof(ModifyMassPrefix), null);
			patched += Patch(typeof(Grid), "SetSolid", nameof(SetSolidPrefix), null);
			Entrypoint.Log("celllog: " + patched + " of 5 sinks patched at frame " + Time.frameCount
				+ "; Game.IsOnMainThread()=" + Game.IsOnMainThread() + " (must be True)");
		}

		private static int Patch(Type type, string methodName, string prefix, Type[] parameters)
		{
			try
			{
				System.Reflection.MethodInfo method = parameters == null
					? AccessTools.Method(type, methodName)
					: AccessTools.Method(type, methodName, parameters);
				if (method == null)
				{
					Entrypoint.Log("celllog: no " + type.Name + "." + methodName);
					return 0;
				}
				sinkHarmony.Patch(method, prefix: new HarmonyMethod(AccessTools.Method(typeof(CellEventLog), prefix)));
				return 1;
			}
			catch (Exception ex)
			{
				Entrypoint.Log("celllog: could not patch " + type.Name + "." + methodName + ": " + ex.GetType().Name + ": " + ex.Message);
				return 0;
			}
		}

		/// <summary>One ring slot. `element` is a SimHashes value, or 0 where the event carries no
		/// element (Grid.SetSolid, where `amount` is 1 for solid and 0 for not).</summary>
		private static void Record(string reason, int cell, int element, float amount)
		{
			if (watchedCell >= 0 && cell != watchedCell)
			{
				return;
			}
			lock (Lock)
			{
				reasons[next] = reason;
				cells[next] = cell;
				elements[next] = element;
				amounts[next] = amount;
				frames[next] = Time.frameCount;
				next = (next + 1) % Capacity;
				if (recorded < Capacity)
				{
					recorded++;
				}
			}
		}

		private static string Reason(CellEvent ev)
		{
			return ev != null ? ev.reason : "?";
		}

		// ------------------------------------------------------------------ sinks

		private static void AddRemoveSubstancePrefix(int gameCell, ushort elementIdx, CellAddRemoveSubstanceEvent ev, float mass)
		{
			int element = 0;
			if (elementIdx != ushort.MaxValue && ElementLoader.elements != null && elementIdx < ElementLoader.elements.Count)
			{
				element = (int)ElementLoader.elements[elementIdx].id;
			}
			Record(Reason(ev), gameCell, element, mass);
		}

		private static void ReplaceElementPrefix(int gameCell, SimHashes new_element, CellElementEvent ev, float mass)
		{
			Record(Reason(ev), gameCell, (int)new_element, mass);
		}

		private static void ModifyMassPrefix(int gameCell, float mass, CellModifyMassEvent ev, SimHashes element)
		{
			Record(Reason(ev), gameCell, (int)element, mass);
		}

		private static void SetSolidPrefix(int cell, bool solid, CellSolidEvent ev)
		{
			Record(Reason(ev), cell, 0, solid ? 1f : 0f);
		}

		// ------------------------------------------------------------------ dump

		private static void Quitting()
		{
			Dump("quit");
		}

		/// <summary>Alt+L, and the game closing. Writes oldest first.</summary>
		internal static void Dump(string why)
		{
			if (!armed)
			{
				Say("celllog: not on (devpatch_celllog.on next to the exe turns it on)");
				return;
			}
			string[] outReasons;
			int[] outCells;
			int[] outElements;
			float[] outAmounts;
			int[] outFrames;
			int count;
			int start;
			lock (Lock)
			{
				count = recorded;
				start = (next - recorded + Capacity) % Capacity;
				outReasons = reasons;
				outCells = cells;
				outElements = elements;
				outAmounts = amounts;
				outFrames = frames;
			}
			if (count == 0)
			{
				Say("celllog: nothing recorded yet");
				return;
			}
			string dir = Path.GetFullPath(Path.Combine("DevData", "cell-events"));
			Directory.CreateDirectory(dir);
			string path = Path.Combine(dir, System.DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
			StringBuilder text = new StringBuilder();
			text.Append("cell events, ").Append(count).Append(count == Capacity ? " (the ring was full, older events were dropped)" : "")
				.Append(", dumped on ").Append(why).Append(" at frame ").Append(Time.frameCount).Append('\n');
			text.Append(watchedCell >= 0 ? "one cell only: " + watchedCell + "\n" : "every cell\n");
			text.Append("frame\tcell\tx,y\treason\telement\tamount\n");
			for (int i = 0; i < count; i++)
			{
				int at = (start + i) % Capacity;
				int cell = outCells[at];
				string where = Grid.IsValidCell(cell) ? Grid.CellToXY(cell).x + "," + Grid.CellToXY(cell).y : "-";
				string element = outElements[at] == 0 ? "-" : ((SimHashes)outElements[at]).ToString();
				text.Append(outFrames[at]).Append('\t').Append(cell).Append('\t').Append(where).Append('\t')
					.Append(outReasons[at] ?? "?").Append('\t').Append(element).Append('\t')
					.Append(outAmounts[at].ToString("0.###")).Append('\n');
			}
			File.WriteAllText(path, text.ToString());
			Say("celllog: " + count + " events written to " + path);
		}

		private static void Say(string message)
		{
			Debug.Log("[celllog] " + message);
			Entrypoint.Log("[" + System.DateTime.Now.ToString("HH:mm:ss.fff") + "] " + message);
		}
	}
}
