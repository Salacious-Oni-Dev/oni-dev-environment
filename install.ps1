<#
.SYNOPSIS
    Makes a debuggable development copy of Oxygen Not Included from your Steam install.

.DESCRIPTION
    Copies the game to a folder of your choice and turns the copy into a development install:
    the Mono debugger listens on 127.0.0.1:56000, every file the game writes stays in
    <copy>\DevData, and DevDoorstop.dll adds the development patches. The Steam install is only
    read, never changed.

    Run it again on the same folder after a game update: it copies the new game files over the
    old ones and patches them again. DevData and your choices are kept.

    To remove the development copy, delete its folder. Nothing is written anywhere else.

    Needs: Windows PowerShell 5.1 or later, the .NET SDK 8 or later (`dotnet` on PATH), about
    4 GB free where the copy goes, and Steam running when you launch the copy.

.PARAMETER Destination
    Folder for the development copy. It must not exist yet, be empty, or be a copy this script
    made earlier (which it then updates).

.PARAMETER Game
    The game's install folder. Found through Steam when left out.

.PARAMETER DevelopmentPlayer
    Also swap in Unity's development player (Development Build watermark, Debug.isDebugBuild,
    Unity Profiler connection on port 55000). Downloads about 350 MB from Unity. Once chosen,
    later updates keep it.

.PARAMETER NoDevelopmentPlayer
    Put the game's own release player back in a copy that has the development player.

.EXAMPLE
    .\install.ps1 -Destination D:\OniDev

.EXAMPLE
    .\install.ps1 -Destination D:\OniDev -DevelopmentPlayer
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Destination,
    [string] $Game,
    [switch] $DevelopmentPlayer,
    [switch] $NoDevelopmentPlayer
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$AppId = '457140'
$StateFile = 'oni-dev-environment.json'

# Unity Doorstop, fetched from its own release page and checked before use. Bump both together.
$DoorstopVersion = '4.5.0'
$DoorstopUrl = "https://github.com/NeighTools/UnityDoorstop/releases/download/v$DoorstopVersion/doorstop_win_release_$DoorstopVersion.zip"
$DoorstopSha256 = '7bb953e8d883c8bde76ced96f6d0e45660ad6e0151880d8ab5856bf4f532b147'

# Files the development player brings with it, beside UnityPlayer.dll.
$DevPlayerFiles = @('UnityPlayer.dll', 'UnityCrashHandler64.exe', 'WinPixEventRuntime.dll',
                    'UnityPlayer_Win64_player_development_mono_x64.pdb')

$Here = $PSScriptRoot
$Patcher = Join-Path $Here 'tool\OniDevEnv.csproj'
$DevPatch = Join-Path $Here 'devpatch\DevDoorstop.csproj'
$FilesDir = Join-Path $Here 'files'

function Step($text) { Write-Host "== $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host "install: $text" -ForegroundColor Red; exit 1 }

function Invoke-Checked {
    param([string] $Exe, [string[]] $Arguments, [string] $What)
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { Fail "$What failed (exit code $LASTEXITCODE)" }
}

function Test-GameFolder($path) {
    return $path -and (Test-Path (Join-Path $path 'OxygenNotIncluded_Data\Managed\Assembly-CSharp.dll'))
}

# The game's install folder from Steam's own records: every library folder in
# libraryfolders.vdf, and the one whose appmanifest names the game.
function Find-SteamGame {
    $steam = $null
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam') {
        $props = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
        if ($props) {
            if ($props.SteamPath) { $steam = $props.SteamPath; break }
            if ($props.InstallPath) { $steam = $props.InstallPath; break }
        }
    }
    if (-not $steam) { return $null }
    $libraries = @($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
            $libraries += $m.Groups[1].Value -replace '\\\\', '\'
        }
    }
    foreach ($lib in $libraries) {
        $manifest = Join-Path $lib "steamapps\appmanifest_$AppId.acf"
        if (Test-Path $manifest) {
            $m = [regex]::Match((Get-Content $manifest -Raw), '"installdir"\s+"([^"]+)"')
            if ($m.Success) {
                $dir = Join-Path $lib ('steamapps\common\' + $m.Groups[1].Value)
                if (Test-GameFolder $dir) { return (Resolve-Path $dir).Path }
            }
        }
    }
    return $null
}

function Test-Inside($inner, $outer) {
    $i = $inner.TrimEnd('\') + '\'
    $o = $outer.TrimEnd('\') + '\'
    return $i.StartsWith($o, [StringComparison]::OrdinalIgnoreCase)
}

function Write-TextCrLf($source, $target) {
    $text = [IO.File]::ReadAllText($source) -replace "`r?`n", "`r`n"
    [IO.File]::WriteAllText($target, $text)
}

# ---------------------------------------------------------------------------------------------
Step 'checking'

foreach ($f in $Patcher, $DevPatch) {
    if (-not (Test-Path $f)) { Fail "missing $f; run this script from a full clone of the repository" }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail 'the .NET SDK is not installed (no `dotnet` on PATH). Install the .NET SDK 8 or later from https://dotnet.microsoft.com/download'
}
$sdkMajors = @(& dotnet --list-sdks 2>$null | ForEach-Object { if ($_ -match '^(\d+)\.') { [int]$Matches[1] } })
if (-not ($sdkMajors | Where-Object { $_ -ge 8 })) {
    Fail 'no .NET SDK 8 or later found (`dotnet --list-sdks`). Install one from https://dotnet.microsoft.com/download'
}

if (Get-Process -Name 'OxygenNotIncluded' -ErrorAction SilentlyContinue) {
    Fail 'Oxygen Not Included is running. Close it first: the game runs one copy at a time, and its files are in use.'
}

$Destination = [IO.Path]::GetFullPath($Destination)
$state = $null
$statePath = Join-Path $Destination $StateFile
if (Test-Path $statePath) {
    $state = Get-Content $statePath -Raw | ConvertFrom-Json
    Write-Host "updating the development copy in $Destination"
} elseif ((Test-Path $Destination) -and (Get-ChildItem -Force $Destination | Select-Object -First 1)) {
    Fail "$Destination exists and is not empty. Choose a new folder."
}

if (-not $Game -and $state) { $Game = $state.game }
if (-not $Game) { $Game = Find-SteamGame }
if (-not (Test-GameFolder $Game)) {
    Fail 'cannot find the game. Pass its install folder with -Game "<path>".'
}
$Game = (Resolve-Path $Game).Path
Write-Host "game:        $Game"
Write-Host "destination: $Destination"

if ((Test-Inside $Destination $Game) -or (Test-Inside $Game $Destination)) {
    Fail 'the destination must be outside the game folder, and the game folder outside the destination.'
}

$wantPlayer = $false
if ($state -and $state.developmentPlayer) { $wantPlayer = $true }
if ($DevelopmentPlayer) { $wantPlayer = $true }
if ($NoDevelopmentPlayer) { $wantPlayer = $false }

if (-not $state) {
    $need = (Get-ChildItem $Game -Recurse -File | Measure-Object Length -Sum).Sum + 512MB
    $root = [IO.Path]::GetPathRoot($Destination)
    $drive = Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Root -eq $root }
    if ($drive -and $drive.Free -lt $need) {
        Fail ("not enough space on {0}: {1:N1} GB needed, {2:N1} GB free" -f $root, ($need / 1GB), ($drive.Free / 1GB))
    }
}

# ---------------------------------------------------------------------------------------------
Step 'copying the game (the Steam install is only read)'
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
& robocopy $Game $Destination /E /MT:16 /R:2 /W:2 /NFL /NDL /NP /NJH /NJS
# robocopy: 0-7 are success (bit flags for copied / extra / mismatched); 8 and up are failures.
if ($LASTEXITCODE -ge 8) { Fail "copying the game failed (robocopy exit code $LASTEXITCODE)" }
$global:LASTEXITCODE = 0

# Written now as well as at the end, so a run that fails after this point can simply be run again:
# the folder is then recognised as this script's and updated.
function Save-State {
    @{ game = $Game; developmentPlayer = $wantPlayer; doorstop = $DoorstopVersion } |
        ConvertTo-Json | Set-Content -Path $statePath -Encoding UTF8
}
Save-State

$managed = Join-Path $Destination 'OxygenNotIncluded_Data\Managed'

# Launching the copy directly (not through Steam) needs the app id beside the exe; Steam must
# still be running, and the copy uses your own Steam login.
Set-Content -Path (Join-Path $Destination 'steam_appid.txt') -Value $AppId -NoNewline -Encoding ASCII

# ---------------------------------------------------------------------------------------------
Step 'patching the copy''s game assemblies'
Invoke-Checked dotnet @('build', $Patcher, '-c', 'Release', '-v', 'q', '--nologo') 'building the patcher'
Invoke-Checked dotnet @('run', '--project', $Patcher, '-c', 'Release', '--no-build', '--', 'patch', $managed) 'patching'

# ---------------------------------------------------------------------------------------------
Step "Unity Doorstop $DoorstopVersion"
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('oni-dev-environment-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $zip = Join-Path $tmp 'doorstop.zip'
    Invoke-WebRequest -Uri $DoorstopUrl -OutFile $zip -UseBasicParsing
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    if ($hash -ne $DoorstopSha256.ToUpperInvariant()) {
        Fail "the Doorstop download does not match its expected SHA-256 (got $hash); not installing it"
    }
    Expand-Archive -Path $zip -DestinationPath (Join-Path $tmp 'doorstop')
    Copy-Item (Join-Path $tmp 'doorstop\x64\winhttp.dll') (Join-Path $Destination 'winhttp.dll') -Force
} finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
$config = Join-Path $Destination 'doorstop_config.ini'
if (-not (Test-Path $config)) { Write-TextCrLf (Join-Path $FilesDir 'doorstop_config.ini') $config }

# ---------------------------------------------------------------------------------------------
Step 'building DevDoorstop.dll'
# MSB3277 (conflicting framework facade versions) is expected here: the game's assemblies were
# built against newer facades than netstandard2.1's. Nothing is copied, so it changes nothing.
Invoke-Checked dotnet @('build', $DevPatch, '-c', 'Release', '-v', 'q', '--nologo', "-p:OniManaged=$managed",
                        '-p:MSBuildWarningsAsMessages=MSB3277') 'building DevDoorstop'
Copy-Item (Join-Path $Here 'devpatch\bin\Release\DevDoorstop.dll') (Join-Path $Destination 'DevDoorstop.dll') -Force

foreach ($b in 'Dev-Launch.bat', 'Dev-Launch-WaitForDebugger.bat') {
    Write-TextCrLf (Join-Path $FilesDir $b) (Join-Path $Destination $b)
}
New-Item -ItemType Directory -Force -Path (Join-Path $Destination 'DevData') | Out-Null

# ---------------------------------------------------------------------------------------------
# The copy above always leaves the game's own release player in place: robocopy replaces a
# development UnityPlayer.dll because it differs from the game's. So a run without the
# development player only has to remove the files the game does not have.
if ($wantPlayer) {
    Step 'Unity development player'
    $pv = (Get-Item (Join-Path $Game 'UnityPlayer.dll')).VersionInfo.ProductVersion
    $m = [regex]::Match("$pv", '^(\d+\.\d+\.\d+[abfp]\d+) \(([0-9a-f]{12})\)')
    if (-not $m.Success) { Fail "cannot read the game's Unity version from UnityPlayer.dll (ProductVersion '$pv')" }
    $unity = $m.Groups[1].Value
    $changeset = $m.Groups[2].Value
    Write-Host "the game is built with Unity $unity ($changeset)"
    $url = "https://download.unity3d.com/download_unity/$changeset/MacEditorTargetInstaller/UnitySetup-Windows-Mono-Support-for-Editor-$unity.pkg"
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('oni-dev-environment-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        Write-Host "downloading Unity's Windows player support package ($url)"
        Invoke-WebRequest -Uri $url -OutFile (Join-Path $tmp 'support.pkg') -UseBasicParsing
        Push-Location $tmp
        try {
            # The .pkg is a xar archive holding a gzip'd cpio payload; Windows' own tar reads both.
            Invoke-Checked tar @('-xf', 'support.pkg', 'TargetSupport.pkg.tmp/Payload') 'unpacking the package'
            Invoke-Checked tar @('-xf', 'TargetSupport.pkg.tmp/Payload',
                                 './Variations/win64_player_development_mono/*',
                                 './Variations/win64_player_nondevelopment_mono/UnityPlayer.dll') 'unpacking the players'
        } finally { Pop-Location }
        $rel = Join-Path $tmp 'Variations\win64_player_nondevelopment_mono\UnityPlayer.dll'
        $dev = Join-Path $tmp 'Variations\win64_player_development_mono'
        # Swap only if the game's player is exactly Unity's stock release player for this version.
        # Anything else means the package is not the one the game was built with.
        if ((Get-FileHash $rel).Hash -ne (Get-FileHash (Join-Path $Game 'UnityPlayer.dll')).Hash) {
            Fail "the game's UnityPlayer.dll is not Unity's stock $unity release player; not swapping it"
        }
        foreach ($f in $DevPlayerFiles) {
            $src = Join-Path $dev $f
            if (-not (Test-Path $src)) { Fail "the package has no $f" }
            Copy-Item $src (Join-Path $Destination $f) -Force
        }
        Write-Host "installed Unity's $unity development player"
    } finally {
        Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    }
} else {
    foreach ($f in $DevPlayerFiles) {
        if (-not (Test-Path (Join-Path $Game $f))) { Remove-Item (Join-Path $Destination $f) -ErrorAction SilentlyContinue }
    }
}

# ---------------------------------------------------------------------------------------------
Save-State

Step 'state'
Invoke-Checked dotnet @('run', '--project', $Patcher, '-c', 'Release', '--no-build', '--', 'verify', $managed) 'verifying'

Write-Host ''
Write-Host "Done. Start Steam, then launch $Destination\Dev-Launch.bat" -ForegroundColor Green
Write-Host 'Debugger: 127.0.0.1:56000. Saves, mods, settings and Player.log: DevData\'
Write-Host 'To remove the development copy, delete its folder.'
