<#
.SYNOPSIS
  Drives Mesen 2.1.1 (the Master System / Game Gear oracle, GPL: run it, never read it) headless and takes screenshots / state dumps at fixed frame counts.

.DESCRIPTION
  Makes a private configured copy of the oracle under %LOCALAPPDATA%\BrokenNes\sega-mesen (so the pinned copy in C:\BrokenNes-tools\sega\oracles stays untouched):
  RAM powers on as zeros (like this emulator), the YM2413 is off unless -Fm (the board has none until milestone A2; Mesen otherwise reports one to the game), the picture is the 256x192 / 160x144 window, scripts may use io.open. Mesen's Saves folder is emptied before every run, because a
  battery file left by an earlier run makes a game skip its initialisation. The run is killed after -TimeoutSec.

  Usage:
    mesen-capture.ps1 -Rom game.sms -Frames 60,120,300 -OutDir out -Tag game [-Pad "60:Start,70:"] [-Region Ntsc|Pal|Auto] [-Dump] [-State]
  then compare with the Workshop:   BrokenNes.Workshop.exe --sms-run game.sms --frames 300 --png-at 60,120,300 --out mine --tag game --mesen out
#>
param(
    [Parameter(Mandatory)] [string]$Rom,
    [Parameter(Mandatory)] [string[]]$Frames,
    [Parameter(Mandatory)] [string]$OutDir,
    [string]$Tag,
    [string]$Pad = "",
    [ValidateSet("Auto", "Ntsc", "Pal")] [string]$Region = "Auto",
    [switch]$Dump,
    [switch]$State,
    [switch]$Fm,
    [switch]$Ports,
    [string]$Exec = "",
    [string]$Watch = "",
    [switch]$ExecRegs,
    [switch]$ExecCyc,
    [string]$VWatch = "",
    [string]$MWatch = "",
    [string]$VFrames = "",
    [int]$TimeoutSec = 240,
    [int]$Overscan = 24,        # rows cut top and bottom of the 240-line frame: 24 leaves 192 lines; 0 writes the whole frame and --sms-run cuts it to the picture height of each frame (a 224-line game)
    [string]$OracleDir = "C:\BrokenNes-tools\sega\oracles\mesen-2.1.1",
    [string]$RamPowerOn = "AllZeros",   # the oracle's work RAM at power-on: AllZeros (like this emulator), AllOnes or Random
    [string]$SmsRevision = "",   # the oracle's VDP revision setting (Sms.Revision: Compatibility, Sms1, Sms2); empty leaves its default
    [string]$Work = ""           # a private copy of the oracle to run from; default %LOCALAPPDATA%\BrokenNes\sega-mesen. Parallel runs each need their own (mesen-compare.ps1 gives every job slot one).
)
$ErrorActionPreference = "Stop"
if (-not $Tag) { $Tag = [IO.Path]::GetFileNameWithoutExtension($Rom) }
$work = if ($Work) { $Work } else { Join-Path $env:LOCALAPPDATA "BrokenNes\sega-mesen" }
if (-not (Test-Path (Join-Path $work "Mesen.exe"))) {
    New-Item -ItemType Directory -Force $work | Out-Null
    Copy-Item (Join-Path $OracleDir "*.exe") $work
    Copy-Item (Join-Path $OracleDir "*.dll") $work
}
$settings = @{
    Version = "2.1.1"; ConfigUpgrade = 5
    Sms = @{
        Region = $Region; GameGearRegion = $Region; RamPowerOnState = $RamPowerOn; EnableFmAudio = $Fm.IsPresent; GgBlendFrames = $false
        Port1 = @{ Type = "SmsController" }; Port2 = @{ Type = "SmsController" }
        NtscOverscan = @{ Left = 0; Right = 0; Top = $Overscan; Bottom = $Overscan }
        PalOverscan = @{ Left = 0; Right = 0; Top = $Overscan; Bottom = $Overscan }
        GameGearOverscan = @{ Left = 48; Right = 48; Top = 48; Bottom = 48 }
    }
    Debug = @{ ScriptWindow = @{ AllowIoOsAccess = $true; ScriptTimeout = 600 } }
}
if ($SmsRevision) { $settings.Sms.Revision = $SmsRevision }
$settings | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $work "settings.json") -Encoding utf8
$saves = Join-Path $work "Saves"
if (Test-Path $saves) { Get-ChildItem $saves -Recurse -File | Remove-Item -Force }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$lua = Join-Path $PSScriptRoot "capture.lua"
$env:MESEN_OUT = (Resolve-Path $OutDir).Path; $env:MESEN_TAG = $Tag; $env:MESEN_FRAMES = ($Frames -join ","); $env:MESEN_INPUT = $Pad
$env:MESEN_PORTS = if ($Ports) { "1" } else { "" }
$env:MESEN_EXEC = $Exec
$env:MESEN_WATCH = $Watch
$env:MESEN_EXECREGS = if ($ExecRegs) { "1" } else { "" }
$env:MESEN_EXECCYC = if ($ExecCyc) { "1" } else { "" }
$env:MESEN_VWATCH = $VWatch; $env:MESEN_VFRAMES = $VFrames; $env:MESEN_MWATCH = $MWatch
$env:MESEN_GG = if ([IO.Path]::GetExtension($Rom).ToLowerInvariant() -eq ".gg") { "1" } else { "" }
$env:MESEN_DUMP = if ($Dump) { "1" } else { "" }; $env:MESEN_STATE = if ($State) { "1" } else { "" }
$log = Join-Path $env:MESEN_OUT "$Tag.mesen.log"
$p = Start-Process -FilePath (Join-Path $work "Mesen.exe") -ArgumentList @("--testRunner", "`"$((Resolve-Path -LiteralPath $Rom).Path)`"", "`"$lua`"") -PassThru -NoNewWindow `
    -RedirectStandardOutput $log -RedirectStandardError "$log.err"
if (-not $p.WaitForExit($TimeoutSec * 1000)) { $p.Kill(); Write-Error "Mesen timed out after $TimeoutSec s ($Rom)"; exit 3 }
$expected = foreach ($tok in ($Frames -join "," -split ",")) { if ($tok -match '^(\d+)-(\d+)$') { [int]$Matches[1]..[int]$Matches[2] } elseif ($tok) { [int]$tok } }
$missing = $expected | Where-Object { -not (Test-Path (Join-Path $env:MESEN_OUT "${Tag}_f$_.png")) } | Select-Object -First 5
if ($missing) { Write-Error "Mesen did not write frames: $($missing -join ',') (see $log)"; exit 4 }
"mesen: $(@($expected).Count) screenshots in $env:MESEN_OUT"
