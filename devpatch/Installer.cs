using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace Doorstop
{
	/// <summary>
	/// Wraps a short list of boot-path methods in a stopwatch and writes the timings to
	/// DevData/devpatch.log. This exists to find where the ~12 s of silence between
	/// "-- MAIN MENU --" and the save load actually goes; the game logs nothing in that window,
	/// and every test launch pays for it.
	///
	/// Timings nest: OnSpawn's number includes the children listed under it, so the cost that
	/// belongs to no child is the parent minus the sum of its children.
	/// </summary>
	internal static class Installer
	{
		private static readonly Stopwatch Uptime = Stopwatch.StartNew();

		private static int depth;

		/// <summary>Methods to time, as (type name, method name). A miss is logged, not fatal:
		/// the development install's Assembly-CSharp is rewritten, so a name can move.</summary>
		private static readonly string[,] Targets = new string[,]
		{
			{ "KFMOD", "Initialize" },
			{ "BundledAssetsLoader", "OnPrefabInit" },
			{ "Global", "Awake" },
			{ "Global", "Start" },
			{ "Localization", "Initialize" },
			{ "Db", "Initialize" },
			{ "Assets", "OnPrefabInit" },
			{ "GameAudioSheets", "Initialize" },
			{ "AudioSheets", "Initialize" },
			{ "AudioMixer", "Create" },
			{ "GlobalAssets", "OnPrefabInit" },
			{ "KMod.Manager", "Load" },
			{ "LaunchInitializer", "Update" },
			{ "MainMenu", "OnPrefabInit" },
			{ "MainMenu", "SpawnVideoScreen" },
			{ "MainMenu", "OnSpawn" },
			{ "KScreen", "OnSpawn" },
			{ "MainMenu", "ShowLanguageConfirmation" },
			{ "MainMenu", "InitLoadScreen" },
			{ "LoadScreen", "ShowMigrationIfNecessary" },
			{ "LoadScreen", "GetMigrationSaveCounts" },
			{ "LoadScreen", "CountValidSaves" },
			{ "LoadScreen", "Activate" },
			{ "LoadScreen", "RefreshColonyList" },
			{ "LoadScreen", "GetColonies" },
			{ "LoadScreen", "CheckCloudLocalOverlap" },
			{ "LoadScreen", "OnActivate" },
			{ "LoadScreen", "CloudSavesVisible" },
			{ "KScreen", "Activate" },
			{ "WorldGen", "LoadSettings" },
			{ "ProcGenGame.WorldGen", "LoadSettings_Internal" },
			{ "SettingsCache", "LoadFiles" },
			{ "TemplateCache", "Init" },
			{ "WorldGen", "WaitForPendingLoadSettings" },
			{ "SaveLoader", "GetCloudSavesAvailable" },
			{ "SaveLoader", "LoadHeader" },
			{ "KMod.Manager", "Report" },
			{ "KMod.Manager", "SetModLoadingInProgress" },
			{ "MainMenu", "ResumeGame" }
		};

		internal static void Install()
		{
			Harmony harmony = new Harmony("oni-sim.devpatch");

			// Debug keys (DebugKeys.cs). On by default; devpatch_debugkeys.off turns them off.
			try
			{
				DebugKeys.Install(harmony);
			}
			catch (Exception ex)
			{
				Entrypoint.Log("debugkeys: install failed: " + ex);
			}
			try
			{
				DebugOverlays.Install(harmony);
			}
			catch (Exception ex)
			{
				Entrypoint.Log("debugoverlays: install failed: " + ex);
			}

			// Klei's CellEventLogger, made to record again (CellEventLog.cs). Off unless
			// devpatch_celllog.on sits next to the exe.
			try
			{
				CellEventLog.Install(harmony);
			}
			catch (Exception ex)
			{
				Entrypoint.Log("celllog: install failed: " + ex);
			}

			// Load progress panel + no "not responding" during long save loads (LoadProgress.cs).
			// On by default; devpatch_loadprogress.off turns it off.
			try
			{
				LoadProgress.Install(harmony);
			}
			catch (Exception ex)
			{
				Entrypoint.Log("loadprogress: install failed: " + ex);
			}

			// Klei's KProfiler, restored (KProfilerRestore.cs). devpatch_kprofiler.off turns it off.
			//
			// Everything here runs on Doorstop's background thread. Never patch a method on Game
			// from here: patching runs the type's static constructor on this thread, and Game's
			// sets Game.MainThread = Thread.CurrentThread, which breaks every chore precondition
			// for the whole session. Patch Game from the main thread (KProfilerRestore shows how).
			try
			{
				KProfilerRestore.Install(harmony);
			}
			catch (Exception ex)
			{
				Entrypoint.Log("kprofiler: install failed: " + ex);
			}

			// The boot skips are on by default -- making a test spin-up cheap is what this
			// assembly is for. `devpatch_fastboot.off` next to the exe puts the stock boot back.
			if (!System.IO.File.Exists("devpatch_fastboot.off"))
			{
				try
				{
					FastBoot.Install(harmony);
				}
				catch (Exception ex)
				{
					Entrypoint.Log("fastboot: install failed: " + ex);
				}
				try
				{
					Splash.Install(harmony);
				}
				catch (Exception ex)
				{
					Entrypoint.Log("splash: install failed: " + ex);
				}
				try
				{
					SplashMessageScreenPatch.Install(harmony);
				}
				catch (Exception ex)
				{
					Entrypoint.Log("splash: SplashMessageScreen install failed: " + ex);
				}
			}
			else
			{
				Entrypoint.Log("fastboot: disabled by devpatch_fastboot.off");
			}

			// The stopwatch and stack-trace patches are opt-in: they are how the boot cost was
			// found, not something every run should pay for or log.
			if (!System.IO.File.Exists("devpatch_trace.on"))
			{
				return;
			}

			try
			{
				FastBoot.InstallTrace(harmony);
			}
			catch (Exception ex)
			{
				Entrypoint.Log("worldgen: trace install failed: " + ex);
			}
			MethodInfo prefix = AccessTools.Method(typeof(Installer), nameof(TimerPrefix));
			MethodInfo postfix = AccessTools.Method(typeof(Installer), nameof(TimerPostfix));

			int patched = 0;
			for (int i = 0; i < Targets.GetLength(0); i++)
			{
				string typeName = Targets[i, 0];
				string methodName = Targets[i, 1];
				try
				{
					Type type = AccessTools.TypeByName(typeName);
					if (type == null)
					{
						Entrypoint.Log("miss: no type " + typeName);
						continue;
					}
					MethodInfo method = AccessTools.Method(type, methodName);
					if (method == null || method.IsAbstract || method.GetMethodBody() == null)
					{
						Entrypoint.Log("miss: no body for " + typeName + "." + methodName);
						continue;
					}
					harmony.Patch(method, new HarmonyMethod(prefix), new HarmonyMethod(postfix));
					patched++;
				}
				catch (Exception ex)
				{
					Entrypoint.Log("miss: " + typeName + "." + methodName + ": " + ex.Message);
				}
			}
			Entrypoint.Log("devpatch: patched " + patched + " of " + Targets.GetLength(0) + " targets");
		}

		private static void TimerPrefix(out long __state)
		{
			__state = Stopwatch.GetTimestamp();
			depth++;
		}

		private static void TimerPostfix(long __state, MethodBase __originalMethod)
		{
			depth--;
			double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
			string indent = depth > 0 ? new string(' ', depth * 2) : string.Empty;
			Entrypoint.Log(string.Format(
				"[{0:hh\\:mm\\:ss\\.fff}] {1}{2}.{3} {4:F1} ms",
				Uptime.Elapsed,
				indent,
				__originalMethod.DeclaringType != null ? __originalMethod.DeclaringType.Name : "?",
				__originalMethod.Name,
				ms));
		}
	}
}
