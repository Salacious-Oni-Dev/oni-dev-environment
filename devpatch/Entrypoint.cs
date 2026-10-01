using System;
using System.IO;
using System.Reflection;
using System.Threading;

namespace Doorstop
{
	/// <summary>
	/// Doorstop's entry point on the development install. Doorstop hooks the Mono runtime
	/// before Unity starts it, calls Start() on the main thread, and then lets the game boot
	/// normally. Nothing here is loaded by the retail install.
	///
	/// Start() must stay free of any type outside mscorlib: it runs before Unity has finished
	/// setting up assembly resolution, so touching HarmonyLib here would try to load 0Harmony
	/// too early. The patches live in Installer, which is only reached once Assembly-CSharp
	/// has loaded and the search path is definitely good.
	/// </summary>
	public static class Entrypoint
	{
		public const string LogFile = "DevData/devpatch.log";

		private static bool installed;

		public static void Start()
		{
			// Kept from the original no-op assembly: a file next to the exe that says the hook fired.
			try
			{
				File.WriteAllText("doorstop_loaded.txt", "Doorstop ran; mono debugger server should be listening.");
			}
			catch (Exception)
			{
			}

			try
			{
				Directory.CreateDirectory("DevData");
				File.WriteAllText(LogFile, "devpatch: doorstop entry\n");
			}
			catch (Exception)
			{
			}

			try
			{
				Thread thread = new Thread(WaitThenInstall);
				thread.IsBackground = true;
				thread.Start();
			}
			catch (Exception ex)
			{
				Log("entry hook failed: " + ex);
			}
		}

		public static void Log(string message)
		{
			try
			{
				File.AppendAllText(LogFile, message + "\n");
			}
			catch (Exception)
			{
			}
		}

		/// <summary>
		/// Waits for Assembly-CSharp on a background thread instead of subscribing to
		/// AppDomain.AssemblyLoad. Installing from inside that callback fails: the runtime is
		/// mid-load, and pulling 0Harmony in from there comes back as
		/// "Recursive type definition detected". Polling costs nothing and runs a long way
		/// ahead of the methods being patched -- Assembly-CSharp loads during Global.Awake,
		/// and MainMenu.OnSpawn is tens of seconds later.
		/// </summary>
		private static void WaitThenInstall()
		{
			for (int i = 0; i < 600 && !installed; i++)
			{
				try
				{
					if (Find("Assembly-CSharp") != null)
					{
						// Let the load that brought it in finish before touching Harmony.
						Thread.Sleep(200);
						installed = true;
						Installer.Install();
						return;
					}
				}
				catch (Exception ex)
				{
					Log("install failed: " + ex);
					return;
				}
				Thread.Sleep(100);
			}
			if (!installed)
			{
				Log("install failed: Assembly-CSharp never appeared");
			}
		}

		private static Assembly Find(string name)
		{
			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				if (assembly != null && assembly.GetName().Name == name)
				{
					return assembly;
				}
			}
			return null;
		}
	}
}
