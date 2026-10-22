# BrokenDev compat adapter: Mesen 2 (NES and SNES), Lua through --testRunner. Dot-sourced by lib\Compat.ps1.
#
# Mesen is portable when a settings.json sits beside Mesen.exe, and it then writes its own folders there (Saves, Debugger workspaces, recent
# games, Lua script data). So BrokenDev never runs the installed copy: it keeps a private home, BROKENDEV_HOME\work\compat\mesen-<version>,
# with a copy of the installed Mesen.exe, and rewrites that home's settings.json before every run. The installed folder and the user's
# Documents\Mesen2 (which Super VRUN's tests use) are never read or written.
#
# Settings BrokenDev pins (everything else is Mesen's own default, so the run is what a player's stock Mesen does):
#   Preferences: SingleInstance off (a second Mesen must not hand its ROM to the first), no update check, no auto save-state, never pause
#                in the background; save data -> the run's savedir, save states / screenshots / movies / AVI / WAV -> the scratch folder.
#   Debug.ScriptWindow.AllowIoOsAccess on (the script writes its result file); Audio off (no sound device needed).
#   Snes.DisableFrameSkipping on: an unthrottled test runner otherwise skips rendering frames and the pictures depend on host load (found by
#   BrokenDev's SNES diff); it does not change emulation.

Set-StrictMode -Version Latest

function Get-BdMesenHome {
    <# The private Mesen home (a copy of the installed exe): refreshed when the installed exe changed. Returns the exe path in it. #>
    param([Parameter(Mandatory)][string]$InstalledExe, [Parameter(Mandatory)][string]$Version)
    $homeDir = Join-Path (Join-Path (Get-BdHome) 'work\compat') "mesen-$Version"
    $exe = Join-Path $homeDir 'Mesen.exe'
    $src = Get-Item -LiteralPath $InstalledExe
    $same = (Test-Path -LiteralPath $exe) -and ((Get-Item -LiteralPath $exe).Length -eq $src.Length) -and ((Get-Item -LiteralPath $exe).LastWriteTimeUtc -eq $src.LastWriteTimeUtc)
    if (-not $same) {
        New-Item -ItemType Directory -Force -Path $homeDir | Out-Null
        Copy-Item -LiteralPath $InstalledExe -Destination $exe -Force
        (Get-Item -LiteralPath $exe).LastWriteTimeUtc = $src.LastWriteTimeUtc
    }
    $exe
}

function Set-BdMesenSettings {
    <# Writes the private home's settings.json for one run (the pinned keys above; Mesen fills every other key with its default). #>
    param([Parameter(Mandatory)][string]$HomeDir, [Parameter(Mandatory)][string]$SaveDir, [Parameter(Mandatory)][string]$Scratch, [string]$Version = '2.1.1', [switch]$Play)
    foreach ($d in $SaveDir, (Join-Path $Scratch 'states'), (Join-Path $Scratch 'pictures')) { New-Item -ItemType Directory -Force -Path $d | Out-Null }
    $s = [ordered]@{
        Version = $Version; ConfigUpgrade = 5
        # hand testing (-Play) wants sound; a compat run needs no sound device
        Audio = [ordered]@{ EnableAudio = [bool]$Play }
        # Port 1 holds a standard pad (what Mesen's first-run setup gives a player; without it a settings file has no controller at all and
        # emu.setInput does nothing: measured, getInput(0) came back empty)
        Nes = [ordered]@{ Port1 = [ordered]@{ Type = 'NesController' } }
        Snes = [ordered]@{ DisableFrameSkipping = $true; Port1 = [ordered]@{ Type = 'SnesController' }; Port2 = [ordered]@{ Type = 'SnesController' } }
        Preferences = [ordered]@{
            SingleInstance = $false; AutomaticallyCheckForUpdates = $false; PauseWhenInBackground = $false; PauseWhenInMenusAndConfig = $false
            EnableAutoSaveState = $false
            OverrideSaveDataFolder = $true; SaveDataFolder = $SaveDir
            OverrideSaveStateFolder = $true; SaveStateFolder = (Join-Path $Scratch 'states')
            OverrideScreenshotFolder = $true; ScreenshotFolder = (Join-Path $Scratch 'pictures')
            OverrideMovieFolder = $true; MovieFolder = $Scratch
            OverrideAviFolder = $true; AviFolder = $Scratch
            OverrideWaveFolder = $true; WaveFolder = $Scratch
        }
        Debug = [ordered]@{ ScriptWindow = [ordered]@{ AllowIoOsAccess = $true } }
    }
    Save-BdJson -Object $s -Path (Join-Path $HomeDir 'settings.json') -Depth 6
}

function Start-BdPlayMesen {
    <# 'play': the ROM in the private Mesen home for hand testing (sound on, normal speed, saves in $SaveDir). Does not wait. #>
    param([Parameter(Mandatory)]$Emu, $Package, [Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$Rom, [Parameter(Mandatory)][string]$SaveDir, [Parameter(Mandatory)][string]$Scratch)
    $version = if ($Package) { [string]$Package.version } else { '2.1.1' }
    $mesen = Get-BdMesenHome -InstalledExe $Exe -Version $version
    $homeDir = Split-Path -Parent $mesen
    Set-BdMesenSettings -HomeDir $homeDir -SaveDir $SaveDir -Scratch $Scratch -Version $version -Play
    Start-Process -FilePath $mesen -ArgumentList @('"' + $Rom + '"') -WorkingDirectory $homeDir -PassThru
}

function Invoke-BdAdapterMesen {
    param([Parameter(Mandatory)]$Emu, $Package, [Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$SpecPath, [Parameter(Mandatory)]$Spec,
          [Parameter(Mandatory)][string]$Scratch, [int]$TimeoutSec = 600, [int]$HangSec = 60)
    $version = if ($Package) { [string]$Package.version } else { '2.1.1' }
    $mesen = Get-BdMesenHome -InstalledExe $Exe -Version $version
    $homeDir = Split-Path -Parent $mesen
    Set-BdMesenSettings -HomeDir $homeDir -SaveDir ([string]$Spec.SaveDir) -Scratch $Scratch -Version $version
    $lua = Join-Path (Get-BdRoot) 'tools\compat\mesen.lua'
    $resultFile = Join-Path ([string]$Spec.Out) 'result.txt'
    # Mesen's test runner ends a script after its --timeout (about 100 s by default): give it the run's own limit.
    $argv = @([string]$Spec.Rom, '--luaScriptsToLoad', $lua, '--testRunner', "--timeout=$TimeoutSec")
    # -OutputFile (a plain file, not a pipe): with its standard output on a pipe, Mesen 2.1.1's test runner never starts the script
    # (measured: hung with no output; the same command writing to a file finishes 600 frames in 2 s). Its output is worth keeping: the test
    # runner prints "[CPU] Uninitialized memory read: $XXXX" for every RAM byte a game reads before writing it (lib\Compat.ps1 lists them).
    $r = Invoke-BdWatchedProcess -FilePath $mesen -Arguments $argv -WorkingDirectory $homeDir -Env @{ BDC_SPEC = $SpecPath } -ResultFile $resultFile -TimeoutSec ($TimeoutSec + 30) -HangSec $HangSec -OutputFile (Join-Path $Scratch 'mesen.log')
    # What it ran with: Mesen writes its whole configuration back on exit when it can; read the values that matter from it.
    $settings = [ordered]@{ home = $homeDir; settings = (Join-Path $homeDir 'settings.json'); audio = 'off'; saveData = [string]$Spec.SaveDir }
    try {
        $j = Read-BdJson (Join-Path $homeDir 'settings.json')
        $sec = if ([string]$Spec.Console -eq 'nes') { Get-BdProp $j 'Nes' $null } else { Get-BdProp $j 'Snes' $null }
        if ($sec) {
            foreach ($k in 'Region', 'RamPowerOnState', 'EnableRandomPowerOnState', 'RandomizeMapperPowerOnState', 'RandomizeCpuPpuAlignment', 'ConsoleType', 'DisableFrameSkipping') {
                $v = Get-BdProp $sec $k $null; if ($null -ne $v) { $settings[$k] = $v }
            }
            $ov = Get-BdProp $sec $(if ([string]$Spec.Console -eq 'nes') { 'NtscOverscan' } else { 'Overscan' }) $null
            if ($ov) { $settings['overscan'] = "L$($ov.Left) R$($ov.Right) T$($ov.Top) B$($ov.Bottom)" }
        }
        if (-not $settings.Contains('RamPowerOnState')) { $settings['RamPowerOnState'] = "Mesen's default (the test runner does not write its settings back; see ramMeasured)" }
    } catch { $settings['settingsReadBack'] = $_.Exception.Message }
    if (Test-Path -LiteralPath $resultFile) {
        foreach ($line in [System.IO.File]::ReadAllLines($resultFile)) { if ($line -match '^note power-on RAM [^:]*: (.*)$') { $settings['ramMeasured'] = $Matches[1] } }
    }
    $r | Add-Member -NotePropertyName Settings -NotePropertyValue $settings -Force
    $r
}
