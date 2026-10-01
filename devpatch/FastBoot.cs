using System;
using System.Reflection;
using HarmonyLib;

namespace Doorstop
{
	/// <summary>
	/// Boot-time skips for the development install. Retail never loads this assembly; here they
	/// are on unless `devpatch_fastboot.off` sits next to the exe.
	///
	/// ## Where the boot actually goes
	///
	/// Global.Awake starts ProcGenGame.WorldGen.LoadSettings on a worker thread, and the main
	/// thread later blocks on that task inside SaveLoader.Load. Measured on one machine, with the
	/// stopwatch patches in Installer:
	///
	///     LoadSettings_Internal (worker thread)   18.5 s
	///       SettingsCache.LoadFiles               ~3.8 s
	///       TemplateCache.Init                     0.1 ms
	///       the preloadTemplates loop            ~14.7 s
	///     main thread blocked in SaveLoader.Load  ~11 s of that
	///
	/// The preload loop only runs on the async path. It walks every world's and every
	/// subworld's template spawn rules plus each subscribed DLC's asteroid_impacts directory
	/// and calls TemplateCache.GetTemplate on all of them, which parses a YAML file each. It
	/// is a warm-up for world generation and nothing else: TemplateCache.GetTemplate loads and
	/// caches on demand, so skipping the preload defers that work rather than removing it, and
	/// a run that loads an existing save never asks for a template at all.
	///
	/// So the skip is: force preloadTemplates to false. The settings themselves still load.
	/// Turn it off for a run that generates a world, where the preload is real work being done
	/// early rather than work being done twice.
	/// </summary>
	internal static class FastBoot
	{
		private static MethodInfo loadSettingsInternal;

		/// <summary>Traces every LoadSettings caller. Separate from the skip, and only worth
		/// turning on when the boot cost moves and the callers need re-checking.</summary>
		internal static void InstallTrace(Harmony harmony)
		{
			MethodInfo loadSettings = AccessTools.Method(AccessTools.TypeByName("ProcGenGame.WorldGen"), "LoadSettings");
			if (loadSettings == null)
			{
				Entrypoint.Log("worldgen: LoadSettings not found");
				return;
			}
			harmony.Patch(loadSettings, new HarmonyMethod(AccessTools.Method(typeof(FastBoot), nameof(LoadSettingsPrefix))));
			Entrypoint.Log("worldgen: LoadSettings callers traced");
		}

		internal static void Install(Harmony harmony)
		{
			Type worldGen = AccessTools.TypeByName("ProcGenGame.WorldGen");
			loadSettingsInternal = worldGen == null ? null : AccessTools.Method(worldGen, "LoadSettings_Internal");
			if (loadSettingsInternal == null)
			{
				Entrypoint.Log("fastboot: WorldGen.LoadSettings_Internal not found; no skip installed");
				return;
			}
			if (loadSettingsInternal.GetParameters().Length != 2)
			{
				Entrypoint.Log("fastboot: LoadSettings_Internal has an unexpected signature; no skip installed");
				return;
			}
			harmony.Patch(loadSettingsInternal, new HarmonyMethod(AccessTools.Method(typeof(FastBoot), nameof(NoPreloadPrefix))));
			Entrypoint.Log("fastboot: worldgen template preload disabled");
		}

		private static void NoPreloadPrefix(object[] __args)
		{
			if (__args != null && __args.Length == 2 && __args[1] is bool && (bool)__args[1])
			{
				__args[1] = false;
			}
		}

		private static void LoadSettingsPrefix(object[] __args)
		{
			bool inAsyncThread = __args != null && __args.Length > 0 && __args[0] is bool && (bool)__args[0];
			Entrypoint.Log("worldgen: LoadSettings(in_async_thread: " + inAsyncThread + ") from\n" + Environment.StackTrace);
		}
	}
}
