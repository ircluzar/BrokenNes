<#
.SYNOPSIS
  Differential replay of one NES ROM, BrokenNes against Mesen 2 (used as a black-box oracle): the same ROM and the same
  input script, one trace record per frame from each emulator, then the cross-emulator differ.

.DESCRIPTION
  This is the method of the VRUN accuracy campaign (see the project memory "VRUN accuracy campaign"), packaged as one command:

    Mesen    : Mesen.exe <rom> --luaScriptsToLoad state_trace.lua --testRunner      (env VRUN_TRACE_*)
    BrokenNes: BrokenNes.Workshop.exe --trace --rom <rom> --input <script> --frames N  (CPU/PPU/APU FIX)
    differ   : UAT\diff-trace.ps1 -A <mesen trace> -B <brokennes trace>

  The record per frame is CPU registers, a hash of work RAM, the PPU state (nametable, OAM, palette, scroll registers, CHR) and
  the rendered frame as NES palette INDICES (not RGB: palettes are not standardised), identically on both sides. A column that one
  side cannot measure is not compared and is named in the verdict. Audio samples cannot be compared (Mesen's headless Lua API has
  none): only the APU registers are. Read UAT\diff-trace.ps1 and the header of state_trace.lua before trusting a verdict.

  The Mesen tracer (state_trace.lua) belongs to the user's VRUN repository and is NOT part of BrokenNes: pass its path with
  -TraceLua or set BROKENDEV_TRACE_LUA. Mesen runs from a private copy with flash saves disabled (mapper 30 would otherwise
  persist the previous run's flash into the next run) and the user's own Mesen install is never modified.

  Mesen's test runner stops a script after about 100 seconds of wall clock; a long run needs the segmented runner of the
  campaign. This script runs one segment and says so when Mesen stopped early.

  Exit code: 0 identical over the compared range, 1 divergence, 2 usage or a tool failed.
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
    [ValidateSet('on', 'off')][string]$Ntsc = 'on',
    [string]$Tag = 'diff',
    [string]$DiffScript = ''
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent (Split-Path -Parent $here)
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
$psi.EnvironmentVariables['VRUN_TRACE_RAMFILL'] = 'fceux'
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
$a = @('--trace', '--rom', $Rom, '--out', $bnTrace, '--frames', [string]$mesenRows, '--cpu', $Cores, '--ppu', $Cores, '--apu', $Cores, '--ntsc-frame-timing', $Ntsc)
# -BrokenNesInput is the negative control: feeding BrokenNes a different pad script than Mesen got must make the gate FAIL.
$bnInput = if ($PSBoundParameters.ContainsKey('BrokenNesInput')) { $BrokenNesInput } else { $InputScript }
if ($bnInput) { $a += @('--input', $bnInput) }
Write-Host "diff-nes: BrokenNes --trace ($mesenRows frames, cores $Cores, ntsc $Ntsc) ..."
$r = & $Workshop @a 2>&1 | Out-String
if (-not (Test-Path -LiteralPath $bnTrace)) { Write-Host ("BrokenNes wrote no trace:`n" + $r); exit 2 }

# ---- the differ --------------------------------------------------------------------------------------------------
Write-Host 'diff-nes: diff-trace ...'
# UAT\diff-trace.ps1 is written for PowerShell 7 (it does not parse in Windows PowerShell 5.1): use pwsh, and say so when there is none.
$pwsh = (Get-Command pwsh.exe -ErrorAction SilentlyContinue | Select-Object -First 1)
if (-not $pwsh) { Write-Host 'PowerShell 7 (pwsh.exe) is needed to run UAT\diff-trace.ps1'; exit 2 }
$out = & $pwsh.Source -NoProfile -File $DiffScript -A $mesenTrace -B $bnTrace -LabelA 'Mesen' -LabelB 'BrokenNes' -Quiet 2>&1 | Out-String
$code = $LASTEXITCODE
$verdictFile = Join-Path $Work "$Tag.verdict.txt"
[System.IO.File]::WriteAllText($verdictFile, $out)

# ---- the gate -----------------------------------------------------------------------------------------------------
# The census lines of the verdict ("    * fbhash        1 frame(s)") give the number of frames each column differed. The columns split by what
# a difference MEANS (the campaign's own lesson): the picture and sound-register columns agree exactly when the emulators agree, while
# pc/a/x/y/sp/p/ramhash/v are sampled at an instruction boundary the two emulators cannot align below one instruction, so they differ
# on a share of frames for no emulation reason: information here, never a failure.
$census = [ordered]@{}
foreach ($line in ($out -split "`r?`n")) { if ($line -match '^\s+\*?\s*([a-z0-9]+)\s+(\d+) frame\(s\)\s*$') { $census[$Matches[1]] = [int]$Matches[2] } }
$exact = @('ntbhash', 'oamhash', 'palhash', 'ctrl', 'mask', 'stat', 'chrhash', 't', 'fx', 'w')
$apuCols = @($census.Keys | Where-Object { $_ -match '^(p1|p2|t|n|d)(per|vol|len|dut|swp|lin|mod|out|cur|rem)$' -or $_ -in 'fcs', 'st', 'en', 'flg' })
$gate = New-Object System.Collections.Generic.List[string]
foreach ($c in $exact) { if ($census.Contains($c) -and $census[$c] -gt 0) { $gate.Add("$c differs on $($census[$c]) frame(s)") } }
foreach ($c in $apuCols) { if ($census[$c] -gt 0) { $gate.Add("$c differs on $($census[$c]) frame(s)") } }
# fbhash: frame 0 differs on every Mesen trace (its power-on frame is not drawn), so one frame of tolerance, named.
if ($census.Contains('fbhash') -and $census['fbhash'] -gt 1) { $gate.Add("fbhash (the picture) differs on $($census['fbhash']) frames; only frame 0 may") }
if ($census.Count -eq 0) { $gate.Add('the verdict could not be parsed (no census lines): the differ output changed or failed') }
$summary = [ordered]@{
    rom = $Rom; frames = $mesenRows; input = $InputScript; cores = $Cores; ntsc = $Ntsc; mesenSeconds = $mesenSec
    differExitCode = $code; census = $census; gateFailures = @($gate); pass = ($gate.Count -eq 0)
    note = 'picture (fbhash, nametables, OAM, palette, CHR), PPU control and every APU register must agree; pc/a/x/y/sp/p/ramhash/v are sample-skew columns and are information only'
}
[System.IO.File]::WriteAllText((Join-Path $Work "$Tag.summary.json"), ($summary | ConvertTo-Json -Depth 5))
$info = @('pc', 'a', 'x', 'y', 'sp', 'p', 'ramhash', 'v') | ForEach-Object { if ($census.Contains($_)) { "$_=$($census[$_])" } }
$apuBad = @($apuCols | Where-Object { $census[$_] -gt 0 }).Count
$cf = @{}; foreach ($k in 'fbhash', 'ntbhash', 'oamhash', 'palhash', 'chrhash') { $cf[$k] = if ($census.Contains($k)) { $census[$k] } else { 'n/a' } }
Write-Host ("diff-nes: {0} frames compared. picture: fbhash differs on {1}, nametables {2}, OAM {3}, palette {4}, CHR {5}; APU columns differing: {6}" -f $mesenRows, $cf['fbhash'], $cf['ntbhash'], $cf['oamhash'], $cf['palhash'], $cf['chrhash'], $apuBad)
Write-Host ("diff-nes: sample-skew columns (information): " + ($info -join ' '))
if ($gate.Count -eq 0) { Write-Host 'diff-nes: PASS (picture, PPU state and APU registers agree with Mesen)' } else { Write-Host ('diff-nes: FAIL: ' + ($gate -join '; ')) }
Write-Host "diff-nes: full verdict $verdictFile ; traces $mesenTrace , $bnTrace ; summary $(Join-Path $Work "$Tag.summary.json")"
if ($gate.Count -eq 0) { exit 0 } else { exit 1 }
