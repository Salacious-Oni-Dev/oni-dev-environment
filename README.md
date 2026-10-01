# oni-dev-environment

A development install of Oxygen Not Included, made from your own Steam copy of the game by one
script. It gives you:

- **A managed debugger.** Breakpoints and locals in the game's C# code and in your mods, from
  dnSpy, Rider or Visual Studio.
- **Its own data.** Saves, mods, settings, logs and Workshop mod files all stay inside the
  development copy, so nothing you do there touches the game you play.
- **Development patches.** Klei's own debug keys, profiler, cell event log and debug overlays
  made to work again, a load-progress panel for long loads, and a faster boot.
- **Optionally, a Unity Development Build**, with the Unity Profiler connection.

The script copies the game to a new folder and changes only that copy. Your Steam install is
only read. It is part of the Oxygen Not Included simulation SDK; see
[oni-sdk-docs](https://github.com/Salacious-Oni-Dev/oni-sdk-docs) for the rest of it.

**Status: alpha.**

## Why use it

Modding against the normal game means working mostly blind: you add log lines, rebuild,
restart, and read `Player.log`. The development copy removes the main reasons for that.

| in the normal game | in the development copy |
|---|---|
| No debugger can attach: the game's runtime contains a debugger, but the game never starts it. | Breakpoints, stepping and locals in the game's code and in your mods, from dnSpy, Rider or Visual Studio, with no game binary replaced. A launcher can hold the game at startup so you can debug mod loading and the main menu. |
| The game's code is compiled with optimizations, so even a debugger shows inlined methods and missing locals. | Both game assemblies are marked for debugging, so breakpoints land where you put them and variables can be read. |
| A test colony, a crashed save or a broken mod lands in the same `Documents\Klei` as the game you play, and Workshop mods are shared with it. | Saves, mods, settings, logs and Workshop mod files all live inside the copy's own `DevData` folder. Deleting the folder removes everything. |
| Many of Klei's debug keys do nothing in the shipped game, and three debug overlays draw nothing. | Those keys work: single simulation steps, placing copies of objects with their element, mass and temperature, an element eyedropper, a 15x time scale, Klei's stress test, and a local bug bundle with a save, logs and a screenshot that is never uploaded. The overlays show the liquid flow field, what each pipe carries, and where duplicants' navigation and drawing disagree. |
| Klei's KProfiler is in the game but never switched on. | Per-thread captures of each frame, Klei's own markers and the simulation's sections, with a converter to Chrome's trace viewer. |
| When mass or heat appears in the wrong place, the record of what changed a cell was compiled out of the game. | The cell event log records which system changed each cell, with which element and by how much, for the whole map or one cell. |
| A large colony loads in one frozen frame, and Windows marks the game "not responding". | The window stays responsive, and a panel shows the load phase, progress, rate, time left, memory and the slowest object groups. |
| Every start preloads all world generation templates, which loading a save never uses. | The preload is skipped, so starting the game to test a change is faster. |
| The game is a Unity release build, with no Unity Profiler connection. | Optionally, Unity's development player for the game's exact Unity version, checked against the game's own, with the profiler connection and `Debug.isDebugBuild`. |

None of this changes your Steam install, and your Workshop mods run in the copy exactly as they
do in the normal game: no mod loader and no second copy of Harmony are added.

## Requirements

- Windows, with Windows PowerShell 5.1 or later (built into Windows 10 and 11).
- Oxygen Not Included installed through Steam.
- The .NET SDK, version 8 or later: <https://dotnet.microsoft.com/download>. The script builds
  both tools in this repository from source on your machine.
- About 4 GB of free space for the copy.
- Steam running whenever you use the development copy. It runs under your own Steam login.

## Installing

Clone or download this repository, then from its folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Destination D:\OniDev
```

`-ExecutionPolicy Bypass` applies to this one run only; Windows does not run downloaded scripts
by default.

The destination must be a new or empty folder. The script:

1. finds the game through Steam (or takes `-Game <folder>`), and stops if the game is running;
2. copies the game into the destination;
3. writes `steam_appid.txt` beside the exe, so the copy can be started directly and still use
   Steam;
4. builds the patcher in `tool/` and patches the copy's two game assemblies (below);
5. downloads [Unity Doorstop](https://github.com/NeighTools/UnityDoorstop) 4.5.0 from its
   GitHub release, checks the download's SHA-256, and installs its `winhttp.dll` with
   `doorstop_config.ini`;
6. builds `DevDoorstop.dll` from `devpatch/` against the copy's assemblies and installs it;
7. writes the two launchers and an empty `DevData` folder;
8. with `-DevelopmentPlayer`, installs Unity's development player (below);
9. prints the state of every patch.

| parameter | |
|---|---|
| `-Destination <folder>` | where the development copy goes. Required |
| `-Game <folder>` | the game's install folder, if Steam cannot be asked |
| `-DevelopmentPlayer` | also install Unity's development player (downloads about 350 MB from Unity) |
| `-NoDevelopmentPlayer` | put the game's own player back in a copy that has the development one |

## Using it

Start Steam, then run one of the launchers in the development copy:

| launcher | |
|---|---|
| `Dev-Launch.bat` | starts the game with the debugger listening; the game does not wait for it |
| `Dev-Launch-WaitForDebugger.bat` | the runtime waits at startup until a debugger attaches, for breakpoints before the main menu (game startup, mod loading) |

Both write `Player.log` to `DevData\Player.log`. Starting `OxygenNotIncluded.exe` directly
works too, but the log then goes to Unity's usual location.

The game runs one copy at a time. If the Steam copy is already running, a development launch
looks as if it did nothing.

`doorstop_loaded.txt` appears beside the exe on every start where Doorstop ran. If it is
missing, none of the development features are active.

### Updating after a game update

Run the script again with the same destination. It copies the updated game files over the old
ones and patches them again. `DevData` and your choice of player are kept.

### Removing

Delete the development copy's folder. Nothing is written anywhere else: not to your Steam
install, not to `Documents\Klei`, not to `AppData`.

## Attaching a debugger

The Mono soft debugger listens on **`127.0.0.1:56000`** (`debug_address` in
`doorstop_config.ini`).

- **dnSpy**: open `OxygenNotIncluded_Data\Managed\Assembly-CSharp.dll`, then
  *Debug > Start Debugging > Unity (Connect)*, address `127.0.0.1`, port `56000`. dnSpy makes
  its own sequence points, so breakpoints work without symbol files.
- **Rider or Visual Studio**: *Attach to Unity Process*, or a remote Mono debug configuration
  pointed at the same address.

To make the game wait for you, use `Dev-Launch-WaitForDebugger.bat`, or set
`debug_suspend = true` in `doorstop_config.ini`. To turn Doorstop off for one run, set the
environment variable `DOORSTOP_DISABLE=1`.

## How it works

### The debugger

The game's Mono runtime (`mono-2.0-bdwgc.dll`) already contains the debugger agent. The release
player never passes it `--debugger-agent`, so it never starts. Doorstop is a `winhttp.dll` proxy
that Windows loads from the game folder. It hooks the runtime's startup before Unity reaches it,
and passes the debugger options to whatever Mono version the game ships. No game binary is
replaced for this, and it keeps working across Unity versions.

Doorstop loads one assembly of its own, `DevDoorstop.dll`. It loads no mod loader and no second
copy of Harmony, so the game's own Harmony and your Steam Workshop mods behave exactly as they
do in the normal game.

Both game assemblies, `Assembly-CSharp.dll` and `Assembly-CSharp-firstpass.dll`, are also marked
`[assembly: Debuggable(... | DisableOptimizations)]`. Without that, the JIT inlines methods and
drops locals, so breakpoints drift and most variables read as optimized away.

### Keeping the data inside the copy

Almost every file the game writes gets its folder from one method, `Util.GetKleiRootPath()` in
`Assembly-CSharp-firstpass.dll`, which normally returns `Documents\Klei`. The patcher rewrites it
to return `<copy>\DevData`:

```csharp
public static string GetKleiRootPath()
{
    return Path.Combine(Path.GetDirectoryName(Application.dataPath), "DevData");
}
```

That one change moves saves and cloud saves, mods, preferences, key bindings, unlocks, retired
colonies, timelapses, world generation settings and caches. `Util.defaultRootFolder`, which
feeds the cache folder, is redirected the same way.

Two places do not go through `Util`, and both are patched in `Assembly-CSharp.dll`:

- **The startup data-location check.** `Global.TestDataLocations()` writes and deletes a test
  file under `%LOCALAPPDATA%\Klei`. It now does that under `DevData\Klei`. It still runs, so a
  failing check still shows in the log.
- **Steam Workshop mods.** Steam keeps one copy of each Workshop item for every install of the
  game. Each mod is a single zip, opened in two places: `KMod.Steam.MakeMod` (the mod itself)
  and `SteamUGCService.GetBytesFromZip` (preview and metadata). Before each open, the patch
  copies the zip to `DevData\workshop_mods\<item id>_<name>.zip` and opens the copy. If the copy
  fails, it opens the original, exactly as the game does. Steam still decides when to download
  an item; nothing is written back to Steam's folder.

The patcher checks what it did: the `verify` step at the end of every install prints each patch
and its state.

### Updates and originals

The patcher keeps each original assembly beside it as `*.dll.orig`, and always patches a clean
copy: it restores from `.orig` first, so patches never stack. Whether a file is already patched
is read from markers the patcher itself injects, never from `.orig` existing. After a game
update, the fresh assembly is unpatched, and restoring an old `.orig` over it would quietly put
the previous game version back.

The patcher can also be run on its own:

```powershell
dotnet run --project tool -c Release -- scan   <copy>\OxygenNotIncluded_Data\Managed
dotnet run --project tool -c Release -- verify <copy>\OxygenNotIncluded_Data\Managed
```

`scan` lists every call site that picks a folder to write to, and every zip the game opens.
Run it after a game update to check that nothing new bypasses the redirect.

### The development player (optional)

The debugger above does not make the game a Unity *Development Build*. That is a different
player binary, not a setting. The `UnityPlayer.dll` the game ships is byte-for-byte Unity's
standard release player for its Unity version. Unity's development player has the profiler
connection, the "Development Build" watermark and `Debug.isDebugBuild` built in. Editing the
build settings in `globalgamemanagers` cannot turn them on, because the release player does not
contain that code.

With `-DevelopmentPlayer`, the script:

1. reads the game's Unity version and changeset from `UnityPlayer.dll`'s version information;
2. downloads Unity's Windows player support package for exactly that version from Unity's
   download server;
3. unpacks it with Windows' own `tar`;
4. checks that the package's *release* player is byte-for-byte the game's `UnityPlayer.dll`, and
   stops if it is not;
5. installs the development `UnityPlayer.dll`, its crash handler, `WinPixEventRuntime.dll` and
   its symbol file.

The game's `OxygenNotIncluded.exe` is not changed; it starts whichever `UnityPlayer.dll` sits
beside it. Nothing from Unity is included in this repository.

With it you get the watermark, `Debug.isDebugBuild == true`, and Unity's player connection on
port 55000, next to the debugger on 56000. The Unity Profiler can attach there from a Unity
editor of the same version. Without an editor, the development player can still record a
profile: add `-profiler-enable -profiler-log-file <path>` to the launch.

## DevDoorstop: the development patches

`devpatch/` builds `DevDoorstop.dll`, a small Harmony assembly that Doorstop starts before the
game. It uses the game's own Harmony. Each feature is on or off by an empty file beside the exe:

| feature | default | switch |
|---|---|---|
| debug keys and debug overlays | on | `devpatch_debugkeys.off` |
| KProfiler | on | `devpatch_kprofiler.off` |
| load progress panel | on | `devpatch_loadprogress.off` |
| fast boot and no boot logo | on | `devpatch_fastboot.off` |
| cell event log | off | `devpatch_celllog.on` |
| boot timings | off | `devpatch_trace.on` |

`DevData\devpatch.log` lists what installed at each start, and why anything failed to.

The switches are files, so they stay put: one left behind applies to every later run.

### Debug keys

Klei's debug keys need debug mode. Create an empty file `debug_enable.txt` in
`OxygenNotIncluded_Data` (or beside the exe). A few tools also need `developerDebugEnable: true`
in a `settings.yml` beside the exe. Any debug key you use marks the save as having used debug
mode, exactly as in the normal game.

Many of Klei's keys work as shipped. The most useful for simulation work:

| key | does |
|---|---|
| `Alt` + `-` | exactly one simulation step |
| `Alt` + `=` | one game frame |
| `Alt` + `Z` | very fast game speed |
| `Ctrl` + `F4` | instant build |
| `Ctrl` + `F6` | dig the cell under the mouse |
| `Alt` + `F1` | hide the UI |
| `Backspace` | debug paint and the free camera. **It also reveals the whole map and spawns everything still hidden in it, which cannot be undone.** |

Others are bound but do nothing in the shipped game: their binding cannot fire, no key is
bound, or the method behind them is empty. DevDoorstop fixes these:

| key | does |
|---|---|
| `Shift` + `` ` `` | sends the game's profiler toggle to the simulation library. The plain `` ` `` binding can never fire, because `` ` `` is also a modifier key. The SDK's library answers with per-kernel timings in `sim_profile.log` in the game folder |
| `Alt` + `` ` `` | starts and stops a KProfiler capture (below) |
| `Alt` + `I` | cell info for the cell under the mouse |
| `Ctrl` + `T` | moves the camera to the selected object |
| `Ctrl` + `F3` | places a copy of the selection at the mouse: a building, a duplicant, or any other object with its element, mass and temperature |
| `Ctrl` + `S` | eyedropper: the element under the mouse becomes the debug paint element |
| `Alt` + `F7` | invincible duplicants |
| `Ctrl` + `F1` | counts the scene partitioner's entries per layer, and those left on destroyed objects, into `devpatch.log` |
| `Alt` + `T` | opens the DevTools entity debugger on the selection |
| `Alt` + `D` | opens the DevTools command palette |
| `Ctrl` + `Shift` + `U` | 15x time scale; again for 1x |
| `Alt` + `B` | a local bug bundle in `DevData\bug-reports\<time>\`: a save, `Player.log`, `devpatch.log`, a screenshot and a note. **Nothing is uploaded**, unlike Klei's own bug report |
| `Ctrl` + `Shift` + `F2` | 60 duplicants at the mouse (Klei's stress test) |
| `Alt` + `L` | writes the cell event log (below) |
| `Shift` + `F9` / `Shift` + `F11` | Klei's gameplay and visual test scenes. **Use a throwaway save**: they build over the world around the camera |

Klei's debug overlays list three modes that draw nothing in the shipped game. DevDoorstop
gives them colours:

- **Flow**: the simulation's flow texture, the vector the liquid shader distorts by. Hue is the
  direction; brightness is the magnitude on a log scale.
- **ConduitUpdates**: what pipe networks carry. Blue is liquid, green gas and yellow conveyor
  items, brighter when fuller; an empty pipe is dim grey.
- **MinionAsyncRenderDelta**: white on the cell a duplicant's navigation last settled on, red on
  the cell it is drawn in when the two differ.

### KProfiler

The game's simulation library contains Klei's KProfiler, a per-thread event profiler, but the
game never switches it on. DevDoorstop does. It records the frame, Klei's own markers
(`Game.Update`, `Game.LateUpdate`, each brain update), the fixed parts of a frame, and the
simulation's own sections, all nested by thread.

`Alt` + `` ` `` starts a capture and stops it into `DevData\kprofile\<time>.kprof`. A file
`devpatch_kprofiler.port` holding a port number also starts KProfiler's HTTP control listener
on `127.0.0.1` at that port; every capture of the session then goes to one
`DevData\kprofile\<time>-session.kprof`.

The capture format and a converter to Chrome's trace viewer are documented in
[oni-sim-replacement's KPROFILER.md](https://github.com/Salacious-Oni-Dev/oni-sim-replacement/blob/main/docs/KPROFILER.md).

### Cell event log

Klei's `CellEventLogger` records which system changed a cell, with which element, and by how
much. That is the question to ask when mass or heat appears where it should not. The game still
passes the reason ("Mop", "Meteor", "Element Consumer SimUpdate" and about 80 more) to every
method that changes a cell, but the recording itself was compiled out. With
`devpatch_celllog.on`, DevDoorstop records at those methods:

- `SimMessages.AddRemoveSubstance`, `ReplaceElement`, `ReplaceAndDisplaceElement` and
  `ModifyMass`;
- `Grid.SetSolid`.

It keeps the last 10,000 events in memory. `Alt` + `L` writes them to
`DevData\cell-events\<time>.log`, and so does closing the game. A file `devpatch_celllog.cell`
holding `x,y` or a cell index records that one cell only. The events are never written into
the save.

### Load progress

A large colony can take minutes to load. The load runs in one call on the main thread, so
Windows marks the window "not responding" and the screen shows a frozen LOADING... frame.
DevDoorstop keeps the window responsive without removing any of your input. It also shows a
small panel under LOADING...: the phase, progress through the save's objects, rate, time left,
memory, and the slowest object groups so far. What is loaded, and in what order, does not
change. Each finished phase is also written to `devpatch.log`.

### Fast boot

At startup the game preloads every world generation template, which takes several seconds and
is only needed to generate a new world. Loading a save never uses them. DevDoorstop skips the
preload; a template is still loaded the first time something asks for it.

It also hides the Klei logo at boot. That saves no time: the logo only covers loading work that
runs anyway.

`devpatch_trace.on` writes the time spent in each step of the boot to `devpatch.log`.

## Security

- The managed debugger listens on `127.0.0.1` only. Anything on your machine that can reach that
  port can run code in the game.
- **With the development player, Unity's player connection listens on every network interface
  (port 55000) and announces itself on the local network.** Install the development player only
  on a machine and network you trust.
- Doorstop is downloaded from its own GitHub release and checked against a fixed SHA-256 before
  it is installed. The development player is taken from Unity's own download server, and is
  installed only after its matching release player has been checked byte-for-byte against the
  game's.
- This repository contains no game files and no Unity or Doorstop binaries. Your game's
  assemblies are patched on your machine, in the copy only.

## Third-party components

| component | license | how it is used |
|---|---|---|
| [Unity Doorstop](https://github.com/NeighTools/UnityDoorstop) | LGPL-2.1 | downloaded unmodified from its release page at install time |
| [Mono.Cecil](https://github.com/jbevain/cecil) | MIT | fetched from NuGet when the patcher is built |
| Harmony | MIT | the copy the game ships; DevDoorstop builds against it and uses it at runtime |

## License

MIT, see `LICENSE`.

## Credits

Oxygen Not Included is developed and published by Klei Entertainment. This project is not
affiliated with or endorsed by Klei.

Development of this project uses AI coding assistants. All changes are reviewed and released
by the maintainer.
