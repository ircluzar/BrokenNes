# BrokenDev adapter: BizHawk 2.11.1 (EmuHawk) with one pinned core per emulator id (bizhawk-neshawk: NES -> NesHawk; bizhawk-bsnes: SNES ->
# the registry's core). Compat runs are driven by tools\compat\bizhawk.lua through --lua; 'play' starts EmuHawk for hand testing.
# Dot-sourced by lib\Compat.ps1.
#
# EmuHawk reads and writes ONE config file, the one --config names, and every folder it uses comes from that file's PathEntries (absolute
# paths are accepted). So BrokenDev writes a fresh config.ini into the run's scratch folder before every start and passes --config=<it>:
# nothing is written beside EmuHawk.exe or beside the ROM (checked on a copy of the install: no file added or changed after NES and SNES
# runs). EmuHawk writes the whole config back on exit (with every core setting it used); the adapter reads it back to report the run.
#
# Settings BrokenDev pins for a compat run (everything else is EmuHawk's own default, so the core runs as a player's stock BizHawk runs it):
#   PreferredCores[<system>] = the core (2.11.1 ships quickerNES for the NES and Snes9x for the SNES); DontTryOtherCores (no silent fallback).
#   Sound off (SoundEnabled false; EmuHawk resets a Dummy SoundOutputMethod to XAudio2 at start, so that key is left alone).
#   No throttle and no frame skipping: ClockThrottle, Unthrottled, VSyncThrottle, SoundThrottle off, FrameSkip 0, AutoMinimizeSkipping off.
#   (Unthrottled would force a skip rate of 3 = 2 frames in 3 not rendered, and a skipped frame's screenshot is an older picture.)
#   Rewind off (a savestate every few frames: speed only). No update check, no first-boot message, no OS / admin warnings.
#   Every hotkey that 2.11.1 binds by default is unbound: EmuHawk takes the keyboard focus when it starts, and a stray key (F = frame
#   advance, Pause, Tab = fast forward, F1-F10 = load state, Ctrl+W = close ROM...) would change or stall the run. The pad is safe anyway:
#   the script sets every button of pad 1 every frame, which overrides the host's keyboard and gamepads.
#   Windows: the main window and the Lua console are placed off screen (they cannot be hidden: --chromeless only drops the menu, the
#   status bar and the border).
#   StartPaused: a --lua script otherwise starts after EmuHawk ran the first frame; paused, it starts at power-on (frame 0) and unpauses.
#   Paths: the system's Save RAM -> the spec's savedir; savestates, screenshots, movies, cheats, temp, lua, tools -> the scratch folder.
#   SaveRAM: EmuHawk writes <game>.SaveRAM when it closes the game (on exit), for every battery cart whether the game saved or not; a file
#   it replaces becomes <game>.SaveRAM.bak. <game> is the ROM file's name without extension, '_' shown as ' ' (vrun_game.nes ->
#   'vrun game.SaveRAM'): runs that share a savedir must use the same ROM file name. AutosaveSaveRAM stays off.

Set-StrictMode -Version Latest

$script:BdBizhawkCores = @{
    # system -> the core names EmuHawk 2.11.1 accepts for it (Config.CorePickerUIData; Emulation.Cores\CoreNames.cs). Note: 'BSNES' is
    # BizHawk's legacy bsnes v0.87 (LibsnesCore); 'BSNESv115+' is bsnes v115 (BsnesCore).
    'NES'  = @('quickerNES', 'NesHawk', 'SubNESHawk')
    'SNES' = @('Snes9x', 'BSNESv115+', 'SubBSNESv115+', 'Faust', 'BSNES')
}

# The hotkeys 2.11.1 binds by default (read from the config it writes; the others are unbound already).
$script:BdBizhawkHotkeys = @(
    'Frame Advance', 'Rewind', 'Pause', 'Fast Forward', 'Turbo', 'Screenshot', 'Full Screen', 'Open ROM', 'Close ROM', 'Flush SaveRAM',
    'Larger Window', 'Smaller Window', 'Increase Speed', 'Decrease Speed', 'Reset Speed', 'Reboot Core', 'Screen Raw to Clipboard',
    'Screen Client to Clipboard', 'Capture Mouse', 'Save State 1', 'Save State 2', 'Save State 3', 'Save State 4', 'Save State 5',
    'Save State 6', 'Save State 7', 'Save State 8', 'Save State 9', 'Save State 10', 'Load State 1', 'Load State 2', 'Load State 3',
    'Load State 4', 'Load State 5', 'Load State 6', 'Load State 7', 'Load State 8', 'Load State 9', 'Load State 10', 'Select State 1',
    'Select State 2', 'Select State 3', 'Select State 4', 'Select State 5', 'Select State 6', 'Select State 7', 'Select State 8',
    'Select State 9', 'Select State 10', 'Quick Load', 'Quick Save', 'Toggle read-only', 'ToolBox', 'Add Branch', 'Delete Branch',
    'Toggle Follow Cursor', 'Toggle Auto-Restore', 'Seek To Green Arrow', 'Toggle Turbo Seek', 'Undo', 'Redo', 'Seek To Prev Marker',
    'Seek To Next Marker', 'Set Marker', 'Delete Marker', 'Sel. bet. Markers', 'Select All', 'Reselect Clip.', 'Clear Frames',
    'Delete Frames', 'Insert Frame', 'Insert # Frames', 'Clone Frames', 'Clone # Times', 'Analog Increment', 'Analog Decrement',
    'Analog Incr. by 10', 'Analog Decr. by 10', 'Analog Maximum', 'Analog Minimum', 'Open RA Overlay', 'RA Up', 'RA Down', 'RA Left',
    'RA Right', 'RA Confirm', 'RA Cancel', 'RA Quit'
)

function Test-BdBizhawkEmu {
    <# The emulator's system and core, checked against what EmuHawk 2.11.1 knows. Returns @{ System; Core }. #>
    param([Parameter(Mandatory)]$Emu)
    $system = [string](Get-BdProp $Emu 'system' '')
    $core = [string](Get-BdProp $Emu 'core' '')
    if (-not $script:BdBizhawkCores.ContainsKey($system)) { throw "bizhawk: system '$system' of emulator $(Get-BdProp $Emu 'id' '?') is not one BrokenDev runs (NES, SNES)" }
    if ($script:BdBizhawkCores[$system] -cnotcontains $core) { throw "bizhawk: core '$core' is not a $system core of EmuHawk 2.11.1 (known: $($script:BdBizhawkCores[$system] -join ', '))" }
    @{ System = $system; Core = $core }
}

function New-BdBizhawkConfig {
    <#
      The config.ini (JSON) for one start. -Mode compat: the pinned settings above. -Mode play: only the core pin, the paths and the
      no-dialog keys; sound, throttle, frame skipping, rewind, hotkeys and windows stay EmuHawk's defaults (a normal session at normal speed).
      Creates the folders the paths name. Returns the ordered object; the caller saves it.
    #>
    param([Parameter(Mandatory)][string]$System, [Parameter(Mandatory)][string]$Core, [Parameter(Mandatory)][string]$SaveDir,
          [Parameter(Mandatory)][string]$Scratch, [Parameter(Mandatory)][string]$InstallDir, [string]$Version = '2.11.1',
          [ValidateSet('compat', 'play')][string]$Mode = 'compat')
    $base = Join-Path $Scratch 'bizhawk'
    $p = @{}
    foreach ($k in 'states', 'screenshots', 'movies', 'cheats', 'temp', 'lua', 'tools', 'exttools', 'roms', 'logs', 'avdumps', 'macros', 'bundles', 'watch', 'palettes') {
        $p[$k] = Join-Path $base $k
    }
    $paths = New-Object System.Collections.Generic.List[object]
    $add = { param($Sys, $Type, $Path) $paths.Add([ordered]@{ Type = $Type; Path = $Path; System = $Sys }) }
    $g = 'Global_NULL'
    & $add $g 'Base' $base
    & $add $g 'ROM' $p.roms
    & $add $g 'Firmware' (Join-Path $InstallDir 'Firmware')     # read only (NES and SNES need none)
    & $add $g 'Movies' $p.movies
    & $add $g 'Movie backups' $p.movies
    & $add $g 'A/V Dumps' $p.avdumps
    & $add $g 'Tools' $p.tools
    & $add $g 'Lua' $p.lua
    & $add $g 'Watch (.wch)' $p.watch
    & $add $g 'Debug Logs' $p.logs
    & $add $g 'Macros' $p.macros
    & $add $g 'Multi-Disk Bundles' $p.bundles
    & $add $g 'External Tools' $p.exttools                     # an empty folder: no external tool is loaded
    & $add $g 'Temp Files' $p.temp
    foreach ($sys in 'NES', 'SNES') {
        & $add $sys 'Base' (Join-Path $base $sys)
        & $add $sys 'ROM' $p.roms
        & $add $sys 'Savestates' $p.states
        & $add $sys 'Save RAM' $(if ($sys -eq $System) { $SaveDir } else { Join-Path $base "$sys\SaveRAM" })
        & $add $sys 'Screenshots' $p.screenshots
        & $add $sys 'Cheats' $p.cheats
    }
    & $add 'NES' 'Palettes' $p.palettes
    foreach ($d in $p.Values) { New-Item -ItemType Directory -Force -Path $d | Out-Null }
    New-Item -ItemType Directory -Force -Path $SaveDir | Out-Null
    $c = [ordered]@{
        LastWrittenFrom = $Version
        FirstBoot = $false
        UpdateAutoCheckEnabled = $false
        SkipOutdatedOsCheck = $true
        SkipSuperuserPrivsCheck = $true
        RAAutostart = $false
        PreferredCores = [ordered]@{ $System = $Core }
        DontTryOtherCores = $true
        SingleInstanceMode = $false
        AutoLoadLastSaveSlot = $false; AutoSaveLastSaveSlot = $false
        AutosaveSaveRAM = $false
        PathEntries = [ordered]@{ Paths = $paths.ToArray(); UseRecentForRoms = $false; LastRomPath = $p.roms }
    }
    if ($Mode -eq 'compat') {
        $off = -32000
        $hot = [ordered]@{}
        foreach ($h in $script:BdBizhawkHotkeys) { $hot[$h] = '' }
        $c['SoundEnabled'] = $false; $c['SoundEnabledNormal'] = $false; $c['SoundEnabledRWFF'] = $false
        $c['Unthrottled'] = $false; $c['ClockThrottle'] = $false; $c['VSyncThrottle'] = $false; $c['SoundThrottle'] = $false; $c['VSync'] = $false
        $c['FrameSkip'] = 0; $c['AutoMinimizeSkipping'] = $false
        $c['Rewind'] = [ordered]@{ Enabled = $false }
        $c['RunInBackground'] = $true; $c['AcceptBackgroundInput'] = $false; $c['AcceptBackgroundInputControllerOnly'] = $false
        # StartPaused: the Lua script then starts at frame 0 (power-on) and unpauses; started running, EmuHawk runs one frame before it.
        $c['StartPaused'] = $true; $c['PauseWhenMenuActivated'] = $false; $c['StartFullscreen'] = $false
        $c['ScreenshotCaptureOsd'] = $false
        $c['RunLuaDuringTurbo'] = $true
        $c['HotkeyBindings'] = $hot
        $c['SaveWindowPosition'] = $true; $c['MainWindowPosition'] = "$off, $off"
        $c['CommonToolSettings'] = [ordered]@{
            'BizHawk.Client.EmuHawk.LuaConsole' = [ordered]@{ _wndx = $off; _wndy = $off; Width = $null; Height = $null; SaveWindowPosition = $true; TopMost = $false; FloatingWindow = $true; AutoLoad = $false }
        }
    }
    $c
}

function Get-BdBizhawkRunSettings {
    <# What a run used, from the config EmuHawk wrote back on exit and the result's notes. #>
    param([Parameter(Mandatory)][string]$ConfigPath, [string]$ResultFile = '', [Parameter(Mandatory)][string]$System, [Parameter(Mandatory)][string]$Core,
          [Parameter(Mandatory)][string]$SaveDir, [string]$Version = '2.11.1')
    $s = [ordered]@{ emulator = "BizHawk $version"; core = $Core; system = $System; config = $ConfigPath
                     sound = 'off (SoundEnabled false)'
                     throttle = 'none: ClockThrottle, Unthrottled, VSyncThrottle, SoundThrottle off; FrameSkip 0 and AutoMinimizeSkipping off (every frame rendered); rewind off'
                     saveRam = "$SaveDir\<game>.SaveRAM, written by EmuHawk when it closes the game (the previous file becomes .SaveRAM.bak)" }
    try {
        $back = Read-BdJson $ConfigPath
        $rc = @(Get-BdProp $back 'RecentCores' @())
        if ($rc.Count -gt 0) { $s.core = [string]$rc[$rc.Count - 1] }
        foreach ($kind in 'CoreSyncSettings', 'CoreSettings') {
            $all = Get-BdProp $back $kind $null
            if (-not $all) { continue }
            foreach ($prop in @($all.PSObject.Properties)) {
                $kv = @()
                foreach ($q in @($prop.Value.PSObject.Properties)) {
                    if ($q.Name -eq '$type' -or $q.Name -match '^Show(BG|OBJ)') { continue }
                    $v = $q.Value
                    if ($null -eq $v) { $kv += "$($q.Name)=null" }
                    elseif ($v -is [datetime]) { $kv += "$($q.Name)=$($v.ToString('s'))" }
                    elseif ($v -is [string] -or $v -is [bool] -or $v -is [int] -or $v -is [long] -or $v -is [double] -or $v -is [decimal]) { $kv += "$($q.Name)=$v" }
                    elseif ($v -is [System.Array]) { if ($q.Name -ne 'Palette') { $kv += "$($q.Name)=[$(@($v).Count)]" } }
                    else { $kv += "$($q.Name)={" + ((@($v.PSObject.Properties) | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ' ') + '}' }
                }
                $short = ($prop.Name -split '\.')[-1]
                $s["$short.$kind"] = $kv -join ', '
            }
        }
        $hk = Get-BdProp $back 'HotkeyBindings' $null
        if ($hk) { $bound = @($hk.PSObject.Properties | Where-Object { $_.Value }); $s['hotkeys'] = if ($bound.Count -eq 0) { 'all unbound' } else { "$($bound.Count) bound: " + (($bound | ForEach-Object { $_.Name }) -join ', ') } }
    } catch { $s['configReadBack'] = $_.Exception.Message }
    if ($ResultFile -and (Test-Path -LiteralPath $ResultFile)) {
        foreach ($line in [System.IO.File]::ReadAllLines($ResultFile)) {
            if ($line -match '^note core: .*display type (\S+)$') { $s['region'] = $Matches[1] }
            elseif ($line -match '^note power-on \S+ \(frame \d+\): (.*)$') { $s['ramInit'] = $Matches[1] }
            elseif ($line -match '^note picture: (\d+x\d+) from frame (\d+)$') { if ($s.Contains('picture')) { $s['picture'] += ", $($Matches[1]) from frame $($Matches[2])" } else { $s['picture'] = "$($Matches[1]) from frame $($Matches[2])" } }
            elseif ($line -match '^note nes: (.*)$') { $s['clipping'] = $Matches[1] }
        }
    }
    $s
}

function Invoke-BdAdapterBizhawk {
    param([Parameter(Mandatory)]$Emu, $Package, [Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$SpecPath, [Parameter(Mandatory)]$Spec,
          [Parameter(Mandatory)][string]$Scratch, [int]$TimeoutSec = 600, [int]$HangSec = 90)
    $ec = Test-BdBizhawkEmu -Emu $Emu
    $want = if ([string]$Spec.Console -eq 'nes') { 'NES' } elseif ([string]$Spec.Console -eq 'snes') { 'SNES' } else { '?' }
    if ($want -ne $ec.System) { throw "bizhawk: emulator $(Get-BdProp $Emu 'id' '?') runs the $($ec.System), the spec is for the '$($Spec.Console)'" }
    $version = if ($Package) { [string]$Package.version } else { '2.11.1' }
    $installDir = Split-Path -Parent $Exe
    New-Item -ItemType Directory -Force -Path $Scratch | Out-Null
    $saveDir = [System.IO.Path]::GetFullPath([string]$Spec.SaveDir)
    $cfgPath = Join-Path $Scratch 'config.ini'
    $cfg = New-BdBizhawkConfig -System $ec.System -Core $ec.Core -SaveDir $saveDir -Scratch $Scratch -InstallDir $installDir -Version $version -Mode compat
    Save-BdJson -Object $cfg -Path $cfgPath -Depth 8
    $lua = Join-Path (Get-BdRoot) 'tools\compat\bizhawk.lua'
    $resultFile = Join-Path ([string]$Spec.Out) 'result.txt'
    # The ROM goes last (EmuHawk's --help says so). --lua opens the Lua console too.
    $argv = @("--config=$cfgPath", '--chromeless', "--lua=$lua", [string]$Spec.Rom)
    $r = Invoke-BdWatchedProcess -FilePath $Exe -Arguments $argv -WorkingDirectory $Scratch -Env @{ BDC_SPEC = $SpecPath } -ResultFile $resultFile `
        -TimeoutSec $TimeoutSec -HangSec $HangSec -LogFile (Join-Path $Scratch 'bizhawk.log')
    $settings = Get-BdBizhawkRunSettings -ConfigPath $cfgPath -ResultFile $resultFile -System $ec.System -Core $ec.Core -SaveDir $saveDir -Version $version
    $r | Add-Member -NotePropertyName Settings -NotePropertyValue $settings -Force
    $r
}

function Start-BdPlayBizhawk {
    <#
      'play': EmuHawk with the ROM for hand testing, the same core pin as the compat runs but a normal session (sound on, normal speed,
      EmuHawk's own hotkeys and windows), the battery save in $SaveDir and every other path in $Scratch. Does not wait: returns the started
      System.Diagnostics.Process (started through the shell, so no pipe is left for EmuHawk to fill).
    #>
    param([Parameter(Mandatory)]$Emu, $Package, [Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$Rom, [Parameter(Mandatory)][string]$SaveDir,
          [Parameter(Mandatory)][string]$Scratch)
    $ec = Test-BdBizhawkEmu -Emu $Emu
    if (-not (Test-Path -LiteralPath $Exe)) { throw "bizhawk: $Exe is not there (install the package first)" }
    if (-not (Test-Path -LiteralPath $Rom)) { throw "bizhawk: the ROM $Rom is not there" }
    $version = if ($Package) { [string]$Package.version } else { '2.11.1' }
    New-Item -ItemType Directory -Force -Path $Scratch | Out-Null
    $saveDir = [System.IO.Path]::GetFullPath($SaveDir)
    $cfgPath = Join-Path $Scratch 'config.ini'
    $cfg = New-BdBizhawkConfig -System $ec.System -Core $ec.Core -SaveDir $saveDir -Scratch $Scratch -InstallDir (Split-Path -Parent $Exe) -Version $version -Mode play
    Save-BdJson -Object $cfg -Path $cfgPath -Depth 8
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = ConvertTo-BdArgString @("--config=$cfgPath", [string](Resolve-Path -LiteralPath $Rom).ProviderPath)
    $psi.WorkingDirectory = $Scratch
    $psi.UseShellExecute = $true
    [System.Diagnostics.Process]::Start($psi)
}
