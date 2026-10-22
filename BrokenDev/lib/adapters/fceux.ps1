# BrokenDev compat adapter: FCEUX 2.7.0 (Windows, fceux64.exe; NES only), Lua through -lua. Dot-sourced by lib\Compat.ps1.
# Also Start-BdPlayFceux, the same private FCEUX for the 'play' verb (hand testing: sound on, normal speed, no script).
#
# FCEUX keeps fceux.cfg beside its exe and makes its folders (sav, fcs, snaps, cheats, movies, luascripts, tools) there too. Its -cfg switch
# does not help: it takes a file NAME that FCEUX joins to the exe's own folder (the 2.7.0 help, "Command Line Options": "A pathname can NOT
# be specified"; src\drivers\win\main.cpp: sprintf("%s\\%s", BaseDirectory, cfgFile), BaseDirectory = the exe's folder), and FCEUX writes
# the whole config back there when it exits. So BrokenDev never runs the installed copy: every run gets its own copy of fceux64.exe and its
# DLLs in <scratch>\fceux (7.6 MB), with a fresh fceux.cfg beside it. The installed folder is never written (the compat adapter checks: it
# compares the installed folder before and after the run and reports any change in Settings.installFolderChanged). Nothing is written
# beside the ROM or the Lua script either (checked).
#
# What the config pins (everything else is FCEUX's compiled-in default, so the run is what a player's stock FCEUX does):
#   "sound" 0          compat runs: no sound output, so no audio device is needed. The APU is emulated all the same: sound.get() reports
#                      the same channel state as with sound on (measured), which is what the result's audio lines use. Play: "sound" 1.
#   folders            "odbase" <scratch>\fceux-files (every default folder goes there), "odnonvol" = the save folder (battery saves,
#                      <rom name>.sav), "odstates" / "odsnaps" / "odmovies" / "odcheats" / "odmemwatch" / "odinput" / "odlua" / "odavi" /
#                      "odmacro" under <scratch>\fceux-files.
#   SingleInstanceOnly 0   (the default; pinned so a second FCEUX never hands its ROM to another agent's window)
# Speed (compat): the script calls emu.speedmode("nothrottle"): 1800 frames in about 3 s here. Measured: the pictures of every frame are
# identical to a normal-speed run (no frame skipping reaches the back buffer the script reads), the same in "turbo" and "maximum".
# Defaults measured on 2026-10-22 (and recorded per run from the config FCEUX writes back on exit): power-on RAM 00 00 00 00 FF FF FF FF
# repeated (RAMInitOption 0), NTSC (palyo 0, dendy 0; 29780.5 CPU cycles a frame, also for a NES 2.0 'multiple region' header), old PPU
# (newppu 0), 8 sprites a line (EO_NOSPRLIM off), the window shows lines 8-231 (srendline 8, erendline 231), FCEUX's built-in palette.
#
# Battery saves: FCEUX writes <rom name>.sav into odnonvol when the game closes (emu.exit, or the window closed) and reads it at power-on.
# For UNROM-512 (mapper 30) with the battery bit, the .sav is the WHOLE 512 KB PRG flash (src\boards\unrom512.cpp: addSaveGameBuf(flash_data,
# PRGsize)), written on every close even when the game never flashed anything, and at the next power-on the CPU runs from it. So a .sav
# left in the save folder by an older build REPLACES the program of a newer build with the same file name (measured: a different ROM named
# vrun_game.nes booted VRUN's title from vrun_game.sav). A save folder must be emptied when the ROM changes.

Set-StrictMode -Version Latest

function Get-BdFceuxFolderState {
    <# path -> "length|lastwrite" of every file under a folder (the installed-folder guard). #>
    param([Parameter(Mandatory)][string]$Dir)
    $m = @{}
    if (Test-Path -LiteralPath $Dir) {
        foreach ($f in @(Get-ChildItem -LiteralPath $Dir -Recurse -Force -File -ErrorAction SilentlyContinue)) {
            $m[$f.FullName.Substring($Dir.Length).TrimStart('\')] = '{0}|{1}' -f $f.Length, $f.LastWriteTimeUtc.Ticks
        }
    }
    $m
}

function Read-BdFceuxConfig {
    <# fceux.cfg -> hashtable key -> value text. Lines are '"key" value' or 'key value'; '!version 1' is skipped. #>
    param([Parameter(Mandatory)][string]$Path)
    $h = @{}
    foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
        if ($line -match '^"([^"]+)"\s?(.*)$' -or $line -match '^([^\s"!][^\s]*)\s?(.*)$') { $h[$Matches[1]] = $Matches[2] }
    }
    $h
}

function New-BdFceuxRuntime {
    <#
      A private FCEUX in <Scratch>\fceux: a copy of the installed exe and its DLLs (lua5.1.dll and lua51.dll for the Lua engine, 7z_64.dll
      for zipped ROMs) and a fresh fceux.cfg beside it (the pinned keys above; FCEUX fills every other key with its default and writes the
      whole file back on exit). Returns { Exe; Config; Files; Dirs }.
    #>
    param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$Scratch, [Parameter(Mandatory)][string]$SaveDir, [bool]$Sound = $false)
    if (-not (Test-Path -LiteralPath $Exe)) { throw "fceux: $Exe is not there (install it: brokendev.ps1 emulators install fceux)" }
    $installDir = Split-Path -Parent $Exe
    $rt = Join-Path $Scratch 'fceux'
    if ([System.IO.Path]::GetFullPath($rt).TrimEnd('\') -ieq [System.IO.Path]::GetFullPath($installDir).TrimEnd('\')) { throw "fceux: the scratch folder must not be the install folder ($installDir)" }
    New-Item -ItemType Directory -Force -Path $rt, $SaveDir | Out-Null
    $exeName = Split-Path -Leaf $Exe
    Copy-Item -LiteralPath $Exe -Destination (Join-Path $rt $exeName) -Force
    foreach ($dll in @(Get-ChildItem -LiteralPath $installDir -Filter '*.dll' -File)) { Copy-Item -LiteralPath $dll.FullName -Destination (Join-Path $rt $dll.Name) -Force }
    $files = Join-Path $Scratch 'fceux-files'
    $dirs = [ordered]@{ odbase = $files; odnonvol = $SaveDir; odstates = "$files\fcs"; odsnaps = "$files\snaps"; odmovies = "$files\movies"
                        odcheats = "$files\cheats"; odmemwatch = "$files\memw"; odinput = "$files\input"; odlua = "$files\luascripts"
                        odavi = "$files\avi"; odmacro = "$files\macro" }
    foreach ($d in $dirs.Values) { New-Item -ItemType Directory -Force -Path $d | Out-Null }
    $cfg = Join-Path $rt 'fceux.cfg'
    $lines = @('!version 1', ('"sound" {0}' -f $(if ($Sound) { 1 } else { 0 })))
    foreach ($k in $dirs.Keys) { $lines += ('"{0}" {1}' -f $k, $dirs[$k]) }
    $lines += 'SingleInstanceOnly 0'
    [System.IO.File]::WriteAllText($cfg, (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
    [pscustomobject]@{ Exe = (Join-Path $rt $exeName); Config = $cfg; Files = $files; Dirs = $dirs }
}

function Invoke-BdAdapterFceux {
    param([Parameter(Mandatory)]$Emu, $Package, [Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$SpecPath, [Parameter(Mandatory)]$Spec,
          [Parameter(Mandatory)][string]$Scratch, [int]$TimeoutSec = 600, [int]$HangSec = 60)
    if ([string]$Spec.Console -ne 'nes') { throw "fceux: FCEUX runs the NES only; the spec is for '$($Spec.Console)'" }
    if (-not (Test-Path -LiteralPath ([string]$Spec.Rom))) { throw "fceux: the ROM $($Spec.Rom) is not there" }
    $version = if ($Package) { [string]$Package.version } else { '2.7.0' }
    $installDir = Split-Path -Parent $Exe
    $Scratch = [System.IO.Path]::GetFullPath($Scratch)
    $saveDir = [System.IO.Path]::GetFullPath([string]$Spec.SaveDir)
    $outDir = [System.IO.Path]::GetFullPath([string]$Spec.Out)
    New-Item -ItemType Directory -Force -Path $Scratch, $outDir | Out-Null
    $rt = New-BdFceuxRuntime -Exe $Exe -Scratch $Scratch -SaveDir $saveDir -Sound $false

    $lua = Join-Path (Get-BdRoot) 'tools\compat\fceux.lua'
    if (-not (Test-Path -LiteralPath $lua)) { throw "fceux: the adapter script $lua is missing" }
    $resultFile = Join-Path $outDir 'result.txt'
    if (Test-Path -LiteralPath $resultFile) { Remove-Item -LiteralPath $resultFile -Force }
    $exeName = Split-Path -Leaf $Exe
    $emuText = "FCEUX $version ($exeName, Windows)"
    $notes = @("config: a fresh fceux.cfg beside a copy of $exeName in the scratch folder; sound output off, folders in the scratch folder, every emulation setting at FCEUX's default",
               "battery saves: $saveDir (FCEUX writes <rom name>.sav when the game closes and reads it at power-on; for mapper 30 it is the whole PRG flash)")
    $env2 = @{ BDC_SPEC = $SpecPath; BDC_EMU = $emuText; BDC_NOTES = ($notes -join '|') }

    $before = Get-BdFceuxFolderState $installDir
    # The ROM goes last (FCEUX takes the first argument after its options as the ROM). FCEUX prints nothing on stdout: the log stays empty.
    $argv = @('-lua', $lua, [string]$Spec.Rom)
    $r = Invoke-BdWatchedProcess -FilePath $rt.Exe -Arguments $argv -WorkingDirectory (Split-Path -Parent $rt.Exe) -Env $env2 -ResultFile $resultFile `
        -TimeoutSec $TimeoutSec -HangSec $HangSec -LogFile (Join-Path $Scratch 'fceux.log')
    $after = Get-BdFceuxFolderState $installDir

    # What it ran with: FCEUX writes its whole configuration back to fceux.cfg when it exits normally.
    $settings = [ordered]@{
        emulator = "FCEUX $version"; exe = $rt.Exe; installDir = $installDir; config = $rt.Config; lua = $lua
        sound = 'output off ("sound" 0, no audio device opened); the APU is still emulated and sound.get() feeds the audio lines'
        speed = 'emu.speedmode("nothrottle"): unthrottled, every frame rendered (measured)'
        picture = '256x240, all 240 lines (gui.gdscreenshot(true), the frame just completed), 24-bit BMP; the FCEUX window shows lines 8-231'
        saveDir = $saveDir
    }
    try {
        $c = Read-BdFceuxConfig $rt.Config
        $wroteBack = $c.ContainsKey('RAMInitOption')
        $settings['configWrittenBack'] = $wroteBack
        if ($wroteBack) {
            $opt = [int]$c['RAMInitOption']
            $settings['ramInit'] = switch ($opt) {
                0 { 'RAMInitOption 0 (FCEUX default): 00 00 00 00 FF FF FF FF repeated over the 2 KB RAM' }
                1 { 'RAMInitOption 1: all $FF' }
                2 { 'RAMInitOption 2: all $00' }
                3 { "RAMInitOption 3: random (seed $($c['RAMInitSeed']))" }
                default { "RAMInitOption $opt (FCEUX treats unknown values as 0: 00 00 00 00 FF FF FF FF)" }
            }
            $pal = [int]$c['palyo']; $dendy = [int]$c['dendy']
            $settings['region'] = if ($dendy) { 'Dendy (dendy 1)' } elseif ($pal) { 'PAL (palyo 1)' } else { 'NTSC (palyo 0, dendy 0)' }
            $settings['ppu'] = if ([int]$c['newppu']) { 'new PPU (newppu 1)' } else { 'old PPU (newppu 0, the default)' }
            $eo = [int64]$c['eoptions']
            $settings['spriteLimit'] = if ($eo -band 8) { 'off: more than 8 sprites a line (EO_NOSPRLIM)' } else { '8 sprites a line (the default)' }
            $settings['shownLines'] = "NTSC $($c['srendline'])-$($c['erendline']), PAL $($c['srendlinep'])-$($c['erendlinep']) (the window only; pictures have all 240)"
            $settings['palette'] = if ($eo -band 4) { 'a custom palette (EO_CPALETTE)' } elseif ([int]$c['ntsccol_enable']) { 'NTSC colour emulation' } else { "FCEUX's built-in default palette" }
            $settings['eoptions'] = $eo
            $settings['soundConfig'] = [int]$c['sound']
            $settings['batteryDirInConfig'] = [string]$c['odnonvol']
        } else {
            $settings['ramInit'] = 'RAMInitOption 0 (FCEUX default, not written back: the run did not end normally): 00 00 00 00 FF FF FF FF repeated'
            $settings['region'] = 'NTSC (FCEUX default; not written back)'
        }
    } catch { $settings['configReadBack'] = $_.Exception.Message }
    if (Test-Path -LiteralPath $resultFile) {
        foreach ($line in [System.IO.File]::ReadAllLines($resultFile)) {
            if ($line -match '^note power-on RAM \(read before the first frame\): (.*)$') { $settings['ramMeasured'] = $Matches[1] }
            elseif ($line -match '^note timing: (.*)$') { $settings['timing'] = $Matches[1] }
        }
    }
    $settings['saveFiles'] = @(Get-ChildItem -LiteralPath $saveDir -File -ErrorAction SilentlyContinue | ForEach-Object { '{0} ({1} bytes)' -f $_.Name, $_.Length })
    $changed = @()
    foreach ($k in $after.Keys) { if (-not $before.ContainsKey($k) -or $before[$k] -ne $after[$k]) { $changed += $k } }
    foreach ($k in $before.Keys) { if (-not $after.ContainsKey($k)) { $changed += "$k (removed)" } }
    $settings['installFolderChanged'] = $changed
    if ($changed.Count -gt 0) { Write-BdWarn "fceux: the installed folder $installDir changed during the run: $($changed -join ', ')" }
    $r | Add-Member -NotePropertyName Settings -NotePropertyValue $settings -Force
    $r
}

function Start-BdPlayFceux {
    <#
      'play': the ROM in a private FCEUX for hand testing. Sound on, normal speed, no script, every emulation setting at FCEUX's default;
      battery saves in $SaveDir, everything else under $Scratch (a fresh fceux.cfg each time: settings changed in FCEUX's menus last for
      that session only). Does not wait: returns the started System.Diagnostics.Process (closing the window writes the battery save).
    #>
    param([Parameter(Mandatory)]$Emu, $Package, [Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$Rom, [Parameter(Mandatory)][string]$SaveDir,
          [Parameter(Mandatory)][string]$Scratch)
    if (-not (Test-Path -LiteralPath $Rom)) { throw "fceux: the ROM $Rom is not there" }
    $Scratch = [System.IO.Path]::GetFullPath($Scratch)
    $SaveDir = [System.IO.Path]::GetFullPath($SaveDir)
    New-Item -ItemType Directory -Force -Path $Scratch | Out-Null
    $rt = New-BdFceuxRuntime -Exe $Exe -Scratch $Scratch -SaveDir $SaveDir -Sound $true
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $rt.Exe
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = Split-Path -Parent $rt.Exe
    $argv = @([System.IO.Path]::GetFullPath($Rom))
    if ($psi.PSObject.Properties['ArgumentList']) { foreach ($a in $argv) { [void]$psi.ArgumentList.Add([string]$a) } }
    else { $psi.Arguments = ConvertTo-BdArgString $argv }
    [System.Diagnostics.Process]::Start($psi)
}
