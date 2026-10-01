using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace Doorstop
{
	/// <summary>
	/// Three SimDebugView modes are listed in the shipped game and draw nothing: Flow and
	/// ConduitUpdates have no entry in SimDebugView.getColourFuncs, so every cell falls back to
	/// GetBlack, and GetMinionAsyncRenderDataColour's body is `return Color.black`. Flow is even
	/// offered by the DebugOverlays screen. This gives each a colour function, on the development
	/// install only (it rides DebugKeys' switch: `devpatch_debugkeys.off` turns both off).
	///
	/// What each draws is chosen from its name, as the body-removed debug keys were:
	///
	///  - Flow: the sim's own flow texture (PropertyTextures.externalFlowTex, two floats per cell,
	///    the vector the liquid shader distorts by). Hue is the direction, brightness the
	///    magnitude on a log scale; still cells are black.
	///  - ConduitUpdates: what the pipe networks carry. Blue for liquid, green for gas, yellow
	///    for solid conveyor items, brightness by how full the segment is; an empty pipe is dim
	///    grey, so a stalled network reads as grey against a live one.
	///  - MinionAsyncRenderDelta: where each duplicant is drawn against the cell its navigator
	///    last settled on. White on the nav cell; red on the drawn cell when the two differ, so
	///    a lagging or teleported render shows as a red cell beside a white one.
	///
	/// Colour functions run on SimDebugView's worker threads. The first two only read arrays;
	/// the duplicant map is built on the main thread in SimDebugView.Update and swapped in whole.
	/// </summary>
	internal static class DebugOverlays
	{
		private static Dictionary<int, Color> minionCells = new Dictionary<int, Color>();

		internal static void Install(Harmony harmony)
		{
			if (File.Exists("devpatch_debugkeys.off"))
			{
				return;
			}
			harmony.Patch(AccessTools.Method(typeof(SimDebugView), "OnPrefabInit"),
				postfix: new HarmonyMethod(AccessTools.Method(typeof(DebugOverlays), nameof(OnPrefabInitPostfix))));
			harmony.Patch(AccessTools.Method(typeof(SimDebugView), "Update"),
				prefix: new HarmonyMethod(AccessTools.Method(typeof(DebugOverlays), nameof(UpdatePrefix))));
			Entrypoint.Log("debugoverlays: installed");
		}

		private static void OnPrefabInitPostfix(SimDebugView __instance)
		{
			var funcs = (Dictionary<HashedString, Func<SimDebugView, int, Color>>)AccessTools.Field(typeof(SimDebugView), "getColourFuncs").GetValue(__instance);
			funcs[SimDebugView.OverlayModes.Flow] = FlowColour;
			funcs[SimDebugView.OverlayModes.ConduitUpdates] = ConduitColour;
			funcs[SimDebugView.OverlayModes.MinionAsyncRenderDelta] = MinionRenderDeltaColour;
		}

		private static void UpdatePrefix(SimDebugView __instance)
		{
			if (__instance.GetMode() != SimDebugView.OverlayModes.MinionAsyncRenderDelta)
			{
				return;
			}
			Dictionary<int, Color> map = new Dictionary<int, Color>();
			foreach (MinionIdentity identity in Components.LiveMinionIdentities.Items)
			{
				if (identity == null)
				{
					continue;
				}
				Navigator navigator = identity.GetComponent<Navigator>();
				KBatchedAnimController anim = identity.GetComponent<KBatchedAnimController>();
				int navCell = navigator != null ? navigator.cachedCell : Grid.PosToCell(identity);
				int drawnCell = anim != null ? Grid.PosToCell(anim.PositionIncludingOffset) : navCell;
				if (Grid.IsValidCell(drawnCell) && drawnCell != navCell)
				{
					map[drawnCell] = Color.red;
				}
				if (Grid.IsValidCell(navCell))
				{
					map[navCell] = Color.white;
				}
			}
			minionCells = map;
		}

		private static unsafe Color FlowColour(SimDebugView instance, int cell)
		{
			IntPtr tex = PropertyTextures.externalFlowTex;
			if (tex == IntPtr.Zero)
			{
				return Color.black;
			}
			float* flow = (float*)tex.ToPointer() + cell * 2;
			float x = flow[0];
			float y = flow[1];
			float magnitude = Mathf.Sqrt(x * x + y * y);
			if (!(magnitude > 0f))
			{
				return Color.black;
			}
			float hue = (Mathf.Atan2(y, x) / (2f * Mathf.PI) + 1f) % 1f;
			float value = Mathf.Clamp01(0.25f + Mathf.Log10(1f + magnitude * 1000f) / 4f);
			return Color.HSVToRGB(hue, 1f, value);
		}

		private static Color ConduitColour(SimDebugView instance, int cell)
		{
			Color colour = Color.black;
			bool pipe = false;
			if (Grid.Objects[cell, (int)ObjectLayer.LiquidConduit] != null)
			{
				pipe = true;
				colour = Brighter(colour, Fill(Conduit.GetFlowManager(ConduitType.Liquid).GetContents(cell).mass, 10f), new Color(0.2f, 0.4f, 1f));
			}
			if (Grid.Objects[cell, (int)ObjectLayer.GasConduit] != null)
			{
				pipe = true;
				colour = Brighter(colour, Fill(Conduit.GetFlowManager(ConduitType.Gas).GetContents(cell).mass, 1f), new Color(0.3f, 1f, 0.3f));
			}
			if (Grid.Objects[cell, (int)ObjectLayer.SolidConduit] != null)
			{
				pipe = true;
				bool carrying = SolidConduit.GetFlowManager().GetContents(cell).pickupableHandle.IsValid();
				colour = Brighter(colour, carrying ? 1f : 0f, new Color(1f, 0.9f, 0.2f));
			}
			if (pipe && colour == Color.black)
			{
				return new Color(0.2f, 0.2f, 0.2f);
			}
			return colour;
		}

		private static float Fill(float mass, float capacity)
		{
			return mass > 0f ? Mathf.Clamp01(mass / capacity) : 0f;
		}

		private static Color Brighter(Color current, float fill, Color hue)
		{
			if (fill <= 0f)
			{
				return current;
			}
			Color candidate = hue * (0.35f + 0.65f * fill);
			candidate.a = 1f;
			return candidate.maxColorComponent > current.maxColorComponent ? candidate : current;
		}

		private static Color MinionRenderDeltaColour(SimDebugView instance, int cell)
		{
			return minionCells.TryGetValue(cell, out Color colour) ? colour : Color.black;
		}
	}
}
