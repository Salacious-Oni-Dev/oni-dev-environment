using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Doorstop
{
	// Reference: PeterHan's NoSplashScreen mod (github.com/peterhaneve/ONIMods,
	// NoSplashScreen/NoSplashScreenPatches.cs) targets a *different* splash than the one below --
	// SplashMessageScreen, the "click to continue" ALPHA.LOADING message screen shown at the main
	// menu once Expansion1 is active. It is a public KMonoBehaviour in Assembly-CSharp.dll, not a
	// renamed or internal type, so a direct compile-time reference is safe. Ported below as its own Install step alongside the
	// boot-logo one, same file since both are "hide a splash" and share the class doc comment.
	internal static class SplashMessageScreenPatch
	{
		internal static void Install(Harmony harmony)
		{
			MethodInfo onPrefabInit = AccessTools.Method(typeof(SplashMessageScreen), "OnPrefabInit");
			if (onPrefabInit == null)
			{
				Entrypoint.Log("splash: SplashMessageScreen.OnPrefabInit not found; click-through screen left alone");
				return;
			}
			harmony.Patch(onPrefabInit, postfix: new HarmonyMethod(AccessTools.Method(typeof(SplashMessageScreenPatch), nameof(Postfix))));
			Entrypoint.Log("splash: main-menu click-through screen will be hidden");
		}

		/// <summary>Same fix as NoSplashScreenPatches.cs: deactivate then destroy, so it never
		/// gets a frame to render even if something else re-enables the GameObject later.</summary>
		private static void Postfix(SplashMessageScreen __instance)
		{
			try
			{
				GameObject obj = __instance.gameObject;
				if (obj != null)
				{
					obj.SetActive(false);
					UnityEngine.Object.Destroy(obj);
				}
			}
			catch (Exception ex)
			{
				Entrypoint.Log("splash: SplashMessageScreen hide failed: " + ex);
			}
		}
	}


	/// <summary>
	/// Removes the Klei logo that sits on a black screen for the first several seconds of a boot.
	///
	/// It is worth saying what it is NOT, because every published recipe for this assumes the
	/// wrong thing. It is not Unity's engine splash: `UnityEngine.Rendering.SplashScreen.isFinished`
	/// is already true by the first frame of game code, so the USSR-style globalgamemanagers patches
	/// (Unity 5/2018/2020, and dead on Unity 6 anyway) have nothing to switch off here. It is the
	/// game's own boot scene -- `level0` holds `BootLogo`, `BootBG` and `BootCanvas` -- and the logo
	/// is simply what renders while the boot work runs behind it.
	///
	/// That last point is the honest caveat: hiding the logo buys **no time at all**. The seconds
	/// belong to FMOD's banks, six DLC asset bundles and the worldgen settings task, and they are
	/// spent whether or not anything is drawn over them. This turns the logo off; it does not
	/// make the boot shorter.
	/// </summary>
	internal static class Splash
	{
		private static readonly Stopwatch Uptime = Stopwatch.StartNew();

		/// <summary>Boot-scene objects to switch off. `BootBG` is the black backdrop and is left
		/// alone deliberately: without it the boot renders whatever was last in the framebuffer.</summary>
		private static readonly string[] LogoObjects = { "BootLogo" };

		private static bool done;

		internal static void Install(Harmony harmony)
		{
			Type launchInitializer = AccessTools.TypeByName("LaunchInitializer");
			MethodInfo update = launchInitializer == null ? null : AccessTools.Method(launchInitializer, "Update");
			if (update == null)
			{
				Entrypoint.Log("splash: LaunchInitializer.Update not found; logo left alone");
				return;
			}

			harmony.Patch(update, new HarmonyMethod(AccessTools.Method(typeof(Splash), nameof(HideLogoPrefix))));
			Entrypoint.Log("splash: boot logo will be hidden at the first frame");
		}

		/// <summary>Runs before the boot scene's first Update, which is the earliest main-thread
		/// code in the game and already has the boot scene loaded around it.</summary>
		private static void HideLogoPrefix()
		{
			if (done)
			{
				return;
			}
			done = true;
			try
			{
				CancelUnitySplash();

				int hidden = 0;
				GameObject[] roots = SceneManager.GetActiveScene().GetRootGameObjects();
				foreach (GameObject root in roots)
				{
					foreach (string name in LogoObjects)
					{
						Transform found = root.name == name ? root.transform : root.transform.Find(name);
						if (found != null)
						{
							found.gameObject.SetActive(false);
							hidden++;
						}
					}
				}

				if (hidden == 0)
				{
					// Worth a list rather than a bare miss: the boot scene is the one place where
					// a renamed object cannot be found by grepping the assemblies, because these
					// are scene objects and the names only exist inside level0.
					string[] names = new string[roots.Length];
					for (int i = 0; i < roots.Length; i++)
					{
						names[i] = roots[i].name;
					}
					Entrypoint.Log("splash: no boot logo found; boot scene roots are: " + string.Join(", ", names));
					return;
				}

				Entrypoint.Log(string.Format("[{0:hh\\:mm\\:ss\\.fff}] splash: hid {1} boot logo object(s)",
					Uptime.Elapsed, hidden));
			}
			catch (Exception ex)
			{
				Entrypoint.Log("splash: hide failed: " + ex);
			}
		}

		/// <summary>Cancels Unity's own splash as well, by reflection so a Unity upgrade that
		/// renames it costs a log line rather than a crash. It has always reported itself already
		/// finished by this point, so this is insurance, not the fix.</summary>
		private static void CancelUnitySplash()
		{
			Type splash = AccessTools.TypeByName("UnityEngine.Rendering.SplashScreen");
			MethodInfo cancel = splash == null ? null : AccessTools.Method(splash, "CancelSplashScreen");
			if (cancel == null)
			{
				return;
			}
			PropertyInfo isFinished = AccessTools.Property(splash, "isFinished");
			bool finished = isFinished != null && (bool)isFinished.GetValue(null, null);
			cancel.Invoke(null, null);
			Entrypoint.Log("splash: unity engine splash cancelled (was already finished: " + finished + ")");
		}
	}
}
