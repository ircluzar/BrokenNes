<#
.SYNOPSIS
  Differential replay of one NES ROM, BrokenNes against Mesen 2 (used as a black-box oracle): the same ROM and the same
  input script, one trace record per frame from each emulator, then the cross-emulator differ.

.DESCRIPTION
  This is the method of the VRUN accuracy campaign (see the project memory "VRUN accuracy campaign"), packaged as one command:

    Mesen    : Mesen.exe <rom> --luaScriptsToLoad state_trace.lua --testRunner      (env VRUN_TRACE_*)
    BrokenNes: BrokenNes.Workshop.exe --trace --rom <rom> --input <script> --frames N  (CPU/PPU/APU FIX)
    differ   : UAT\diff-trace.ps1 -A <mesen trace> -B <brokennes trace>  (its full verdict is kept; the gate below reads the traces itself)

  The record per frame is CPU registers, a hash of work RAM, the PPU state (nametable, OAM, palette, scroll registers, CHR) and
  the rendered frame as NES palette INDICES (not RGB: palettes are not standardised), identically on both sides. A column that one
  side cannot measure is not compared. Audio samples cannot be compared (Mesen's headless Lua API has none): only the APU registers
  are. Read UAT\diff-trace.ps1 and the header of state_trace.lua before trusting a verdict.

  The Mesen tracer (state_trace.lua) belongs to the user's VRUN repository and is NOT part of BrokenNes: pass its path with
  -TraceLua or set BROKENDEV_TRACE_LUA. Mesen runs from a private copy with flash saves disabled (mapper 30 would otherwise
  persist the previous run's flash into the next run) and the user's own Mesen install is never modified.

  Mesen's test runner stops a script after about 100 seconds of wall clock; a long run needs the segmented runner of the
  campaign. This script runs one segment and says so when Mesen stopped early.

  The gate (rules and reasons in lib\TraceGate.ps1): the picture columns (fbhash, nametables, OAM, palette, CHR) and the PPU control
  columns must agree exactly after the first -BootFrames frames; so must every APU register, except ISOLATED single-frame blips (a
  frame where the column differs but agrees on the frame before and the frame after), which are listed in the summary and tolerated up
  to a budget (the two emulators read the APU at slightly different instants of a frame). CPU registers, the work-RAM hash and the PPU
  address v and its write toggle w are sampled at an instruction boundary / mid-write-sequence the two emulators cannot align, so they
  are information only. Divergence inside the boot window (Mesen does not draw frame 0; a multi-frame CHR upload at boot lands a frame
  apart) is listed, not failed.

  Exit code: 0 gate passed, 1 divergence, 2 usage or a tool failed.
#>
param(
    [Parameter(Mandatory = $true)][string]$Rom,
    [Parameter(Mandatory = $true)][string]$Work,
    [Parameter(Mandatory = $true)][string]$MesenDir,
    [Parameter(Mandatory = $true)][string]$Workshop,
    [string]$TraceLua = $env:BROKENDEV_TRACE_LUA,
    [string]$InputScript = '',
    [string]$BrokenNesInput = '',
    [int]$Frames = 1200,
    [string]$Cores = 'FIX',
    [ValidateSet('fceux', 'zeros', 'ones')][string]$RamFill = 'fceux',
    [ValidateSet('on', 'off')][string]$Ntsc = 'on',
    [string]$Tag = 'diff',
    [int]$BootFrames = 10,
    [string]$DiffScript = ''
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent (Split-Path -Parent $here)
. (Join-Path (Split-Path -Parent $here) 'lib\TraceGate.ps1')
if (-not $DiffScript) { $DiffScript = Join-Path $repo 'UAT\diff-trace.ps1' }
foreach ($p in @($Rom, $MesenDir, $Workshop, $DiffScript)) { if (-not (Test-Path -LiteralPath $p)) { Write-Host "missing: $p"; exit 2 } }
if (-not $TraceLua -or -not (Test-Path -LiteralPath $TraceLua)) { Write-Host 'the Mesen tracer state_trace.lua was not found: pass -TraceLua or set BROKENDEV_TRACE_LUA (it lives in the user''s VRUN repository, tools\state_trace.lua)'; exit 2 }
New-Item -ItemType Directory -Force -Path $Work | Out-Null

# ---- a private Mesen copy: flash saves off, no saves, the user's install untouched ---------------------------------
$priv = Join-Path $Work 'mesen'
if (-not (Test-Path -LiteralPath (Join-Path $priv 'Mesen.exe'))) {
    New-Item -ItemType Directory -Force -Path $priv | Out-Null
    foreach ($f in (Get-ChildItem -LiteralPath $MesenDir -File | Where-Object { $_.Extension -in '.exe', '.dll' -or $_.Name -in 'settings.json', 'MesenNesDB.txt' })) { Copy-Item -LiteralPath $f.FullName -Destination $priv -Force }
    if (Test-Path -LiteralPath (Join-Path $MesenDir 'GameConfig')) { Copy-Item -LiteralPath (Join-Path $MesenDir 'GameConfig') -Destination $priv -Recurse -Force }
    $sp = Join-Path $priv 'settings.json'
    $txt = [System.IO.File]::ReadAllText($sp)
    $new = $txt -replace '"DisableFlashSaves"\s*:\s*false', '"DisableFlashSaves": true'
    if ($new -eq $txt -and $txt -notmatch '"DisableFlashSaves"\s*:\s*true') { Write-Host 'could not set DisableFlashSaves in the private Mesen settings'; exit 2 }
    [System.IO.File]::WriteAllText($sp, $new, (New-Object System.Text.UTF8Encoding($false)))
}
foreach ($d in 'Saves', 'SaveStates') { $p = Join-Path $priv $d; if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force } }

$mesenTrace = Join-Path $Work "$Tag.mesen.trace.txt"
$bnTrace = Join-Path $Work "$Tag.brokennes.trace.txt"
Remove-Item -LiteralPath $mesenTrace, $bnTrace -ErrorAction SilentlyContinue

# ---- Mesen side ------------------------------------------------------------------------------------------------
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = Join-Path $priv 'Mesen.exe'
$psi.Arguments = '"' + $Rom + '" --luaScriptsToLoad "' + $TraceLua + '" --testRunner'
$psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; $psi.WorkingDirectory = $priv
$psi.EnvironmentVariables['VRUN_TRACE_OUT'] = $mesenTrace
$psi.EnvironmentVariables['VRUN_TRACE_INPUT'] = $InputScript
$psi.EnvironmentVariables['VRUN_TRACE_FRAMES'] = [string]$Frames
$psi.EnvironmentVariables['VRUN_TRACE_RAMFILL'] = $RamFill
$psi.EnvironmentVariables['VRUN_TRACE_ROM'] = $Rom
Write-Host "diff-nes: Mesen ($Frames frames) ..."
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = [System.Diagnostics.Process]::Start($psi)
if (-not $p.WaitForExit(600000)) { try { & taskkill.exe /PID $p.Id /T /F | Out-Null } catch { }; Write-Host 'Mesen did not finish in 600 s'; exit 2 }
$mesenSec = [math]::Round($sw.Elapsed.TotalSeconds, 1)
Write-Host "diff-nes: Mesen exit $($p.ExitCode) in $mesenSec s"
if (-not (Test-Path -LiteralPath $mesenTrace)) { Write-Host 'Mesen wrote no trace (check the script path and the ROM)'; exit 2 }
$mesenRows = @(Get-Content -LiteralPath $mesenTrace | Where-Object { $_ -notmatch '^#' }).Count
if ($mesenRows -lt $Frames) { Write-Host "diff-nes: NOTE Mesen emitted only $mesenRows of $Frames frames (its test runner stops a script after about 100 s); the comparison covers those $mesenRows frames" }

# ---- BrokenNes side ----------------------------------------------------------------------------------------------
$a = @('--trace', '--rom', $Rom, '--out', $bnTrace, '--frames', [string]$mesenRows, '--cpu', $Cores, '--ppu', $Cores, '--apu', $Cores, '--ntsc-frame-timing', $Ntsc, '--power-on-ram', $RamFill)
# -BrokenNesInput is the negative control: feeding BrokenNes a different pad script than Mesen got must make the gate FAIL.
$bnInput = if ($PSBoundParameters.ContainsKey('BrokenNesInput')) { $BrokenNesInput } else { $InputScript }
if ($bnInput) { $a += @('--input', $bnInput) }
Write-Host "diff-nes: BrokenNes --trace ($mesenRows frames, cores $Cores, ntsc $Ntsc, power-on RAM $RamFill) ..."
$r = & $Workshop @a 2>&1 | Out-String
if (-not (Test-Path -LiteralPath $bnTrace)) { Write-Host ("BrokenNes wrote no trace:`n" + $r); exit 2 }

# ---- the differ (its verdict is kept for reading) -------------------------------------------------------------------
Write-Host 'diff-nes: diff-trace ...'
# UAT\diff-trace.ps1 is written for PowerShell 7 (it does not parse in Windows PowerShell 5.1): use pwsh, and say so when there is none.
$pwsh = (Get-Command pwsh.exe -ErrorAction SilentlyContinue | Select-Object -First 1)
if (-not $pwsh) { Write-Host 'PowerShell 7 (pwsh.exe) is needed to run UAT\diff-trace.ps1'; exit 2 }
$out = & $pwsh.Source -NoProfile -File $DiffScript -A $mesenTrace -B $bnTrace -LabelA 'Mesen' -LabelB 'BrokenNes' -Quiet 2>&1 | Out-String
$code = $LASTEXITCODE
$verdictFile = Join-Path $Work "$Tag.verdict.txt"
[System.IO.File]::WriteAllText($verdictFile, $out)

# ---- the gate (BrokenDev\lib\TraceGate.ps1 holds the rules and their explanation) ------------------------------------------
$g = Get-BdTraceGate -MesenTrace $mesenTrace -BrokenNesTrace $bnTrace -BootFrames $BootFrames
$n = $g.Frames; $census = $g.Census; $boot = $g.BootSkew; $gate = $g.GateFailures
$blipMap = [ordered]@{}; foreach ($k in $g.Blips.Keys) { $blipMap[$k] = @($g.Blips[$k]) }
$summary = [ordered]@{
    rom = $Rom; frames = $n; input = $InputScript; cores = $Cores; ntsc = $Ntsc; ramFill = $RamFill; mesenSeconds = $mesenSec; bootFrames = $BootFrames
    differExitCode = $code; divergentFrameCounts = $census; bootSkew = @($boot); gateFailures = @($gate); comparedColumns = $g.ComparedColumns; pass = $g.Pass
    isolatedApuBlips = $blipMap; isolatedBlipFrames = $g.BlipFrames; blipBudget = $g.BlipLimit
    note = 'picture and PPU control columns must agree after the boot window; APU registers too, except isolated single-frame blips (listed, budgeted); pc/a/x/y/sp/p/ramhash/v/w are sample-skew columns and are information only'
}
[System.IO.File]::WriteAllText((Join-Path $Work "$Tag.summary.json"), ($summary | ConvertTo-Json -Depth 5))
$info = $script:BdSkewColumns | ForEach-Object { if ($census.Contains($_)) { "$_=$($census[$_])" } }
Write-Host ("diff-nes: {0} frames, {1} columns compared (picture and PPU control exactly; APU registers exactly apart from isolated single-frame blips); the sample-skew columns are information only" -f $n, $g.ComparedColumns)
Write-Host ("diff-nes: sample-skew columns (frames that differ): " + ($info -join ' '))
if ($g.BlipFrames -gt 0) { Write-Host ("diff-nes: isolated APU blips on {0} frame(s) (budget {1}), listed in the summary: {2}" -f $g.BlipFrames, $g.BlipLimit, (($blipMap.Keys | ForEach-Object { "$_@" + ($blipMap[$_] -join ',') }) -join '  ')) }
if ($boot.Count -gt 0) { Write-Host ("diff-nes: boot-window skew (listed, not failed): " + ($boot -join '; ')) }
if ($g.Pass) { Write-Host 'diff-nes: PASS (picture, PPU state and APU registers agree with Mesen after the boot window)' } else { Write-Host ('diff-nes: FAIL: ' + ($gate -join '; ')) }
Write-Host "diff-nes: full verdict $verdictFile ; traces $mesenTrace , $bnTrace ; summary $(Join-Path $Work "$Tag.summary.json")"
if ($g.Pass) { exit 0 } else { exit 1 }
