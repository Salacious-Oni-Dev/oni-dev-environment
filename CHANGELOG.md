# Changelog

Notable changes to this repository, newest first. All SDK repositories share one version
number per release; see [Compatibility](https://github.com/Salacious-Oni-Dev/oni-sdk-docs/blob/main/guides/compatibility.md).

## 0.1.0-alpha.1 (2026-10-01)

First public release.

- `install.ps1` makes a development copy of the game from your Steam install in a folder you
  choose. The Steam install is only read. Run it again after a game update to update the copy.
- A managed debugger: Unity Doorstop starts the game's Mono soft debugger on
  `127.0.0.1:56000`, and both game assemblies are marked debuggable so breakpoints and locals
  work from dnSpy, Rider or Visual Studio.
- The copy keeps its own data: saves, mods, settings, logs and Workshop mod files stay inside
  its `DevData` folder.
- Development patches: Klei's debug keys, profiler, cell event log and debug overlays made to
  work again, a load-progress panel, and a faster boot. A profiler capture can also be
  controlled over HTTP on `127.0.0.1`, on a port you choose.
- `-DevelopmentPlayer` optionally installs Unity's development player, with the Unity Profiler
  connection. That connection listens on every network interface; the README's Security
  section says what that means.
