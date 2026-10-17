<#
.SYNOPSIS
  Regression guards for three APU_FIX inaccuracies the BrokenDev differential replay found on the SMB1 remake, each with a hand-assembled
  16 KB program the test writes itself (no ROM is stored anywhere) and expected values measured on Mesen 2.1.1 (the oracle) on 2026-10-16.

.DESCRIPTION
  1. five-step: a game that writes $4017 = $FF (5-step, IRQ inhibit) once per frame must lose a pulse length counter two units per frame (the write clocks
     a half frame at once, the sequence adds one at 14913 cycles). The old table put the half-frame clocks one step early: three units per frame.
     The program counts the frames until pulse 1 falls silent (length index 0 = 10) and shows that count as its backdrop colour; the CPU's X register holds it.
  2. sweep-disabled: a sweep with its ENABLE flag clear ($4001 = $0F: negate, shift 7) must leave the period alone. The old code ground it down.
  3. sweep-reload: an upward sweep with P = 0 ($4001 = $84) must update on the first half-frame clock after the write, and every one after it.

  The programs run through 'BrokenNes.Workshop --trace' (CPU/PPU/APU FIX); the checks read the trace's x, p1len and p1per columns at fixed frames.
  NEGATIVE CONTROLS (shown able to fail): every program is also run on a different, older APU core (FMC), which has the behaviour being guarded
  against only if it does not keep the fixed behaviour; the test prints what that core did and a check that PASSES on it would be reported as "not discriminating".
  The oracle values can be re-measured with Mesen: -MesenDir (a Mesen 2 folder) and BROKENDEV_TRACE_LUA (the user's state_trace.lua).

  Exit code: 0 all guards hold, 1 a guard failed, 2 setup problem.
#>
param(
    [Parameter(Mandatory = $true)][string]$Workshop,
    [string]$MesenDir = '',
    [string]$TraceLua = $env:BROKENDEV_TRACE_LUA,
    [switch]$Measure
)
$ErrorActionPreference = 'Stop'
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('bd-apu-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$fails = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail) { Write-Host ("[{0}] {1} - {2}" -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Name, $Detail); if (-not $Ok) { $script:fails++ } }

function New-NesRom([byte[]]$Code) {
    $b = New-Object byte[] (16 + 16384 + 8192)
    $b[0] = 0x4E; $b[1] = 0x45; $b[2] = 0x53; $b[3] = 0x1A; $b[4] = 1; $b[5] = 1
    [Array]::Copy($Code, 0, $b, 16, $Code.Length)
    $b[16 + 0x3FFC] = 0x00; $b[16 + 0x3FFD] = 0xC0     # reset vector -> $C000
    $b
}
function Get-TraceColumn([string]$Path, [string]$Column, [int]$Frame) {
    $names = $null; $row = $null; $i = 0
    foreach ($line in [System.IO.File]::ReadLines($Path)) {
        if ($line.StartsWith('# columns:')) { $names = ($line.Substring(10).Trim()) -split '\|'; continue }
        if ($line.StartsWith('#') -or $line.Length -eq 0) { continue }
        if ($i -eq $Frame) { $row = $line -split '\|'; break }
        $i++
    }
    if (-not $names -or -not $row) { throw "trace $Path has no frame $Frame" }
    $row[[array]::IndexOf($names, $Column)]
}
function Invoke-Trace([string]$Rom, [int]$Frames, [string]$Apu) {
    $out = Join-Path $tmp ([System.IO.Path]::GetFileNameWithoutExtension($Rom) + ".$Apu.trace.txt")
    $r = & $Workshop --trace --rom $Rom --out $out --frames $Frames --cpu FIX --ppu FIX --apu $Apu --power-on-ram zeros 2>&1 | Out-String
    if (-not (Test-Path -LiteralPath $out)) { throw "no trace written:`n$r" }
    $out
}

# ---- program 1: frames until pulse 1's length counter reaches zero, with $4017 = $FF written every frame -----------------------
$p1 = [byte[]](
    0x78, 0xD8, 0xA2, 0x00, 0x8E, 0x00, 0x20, 0x8E, 0x01, 0x20,               # SEI CLD LDX #0 STX $2000 STX $2001
    0xA9, 0x01, 0x8D, 0x15, 0x40,                                               # LDA #1 STA $4015
    0xA9, 0x10, 0x8D, 0x00, 0x40,                                               # LDA #$10 STA $4000 (halt clear, constant volume)
    0xA9, 0x00, 0x8D, 0x01, 0x40, 0x8D, 0x02, 0x40, 0x8D, 0x03, 0x40,          # sweep off, period 0, length index 0 (= 10)
    0xA2, 0x00,                                                                 # LDX #0
    0x2C, 0x02, 0x20, 0x10, 0xFB,                                               # loop: BIT $2002 / BPL loop   (wait for vblank)
    0xA9, 0xFF, 0x8D, 0x17, 0x40,                                               # LDA #$FF STA $4017
    0xAD, 0x15, 0x40, 0x29, 0x01, 0xF0, 0x04,                                   # LDA $4015 AND #1 BEQ done
    0xE8, 0x4C, 0x21, 0xC0,                                                     # INX / JMP loop
    0xA9, 0x3F, 0x8D, 0x06, 0x20, 0xA9, 0x00, 0x8D, 0x06, 0x20,               # done: point the PPU at $3F00
    0x8E, 0x07, 0x20,                                                           # STX $2007 (backdrop = frames counted)
    0xA9, 0x3F, 0x8D, 0x06, 0x20, 0xA9, 0x00, 0x8D, 0x06, 0x20,
    0xA9, 0x08, 0x8D, 0x01, 0x20,                                               # show background
    0x4C, 0x52, 0xC0)                                                           # JMP *
# the loop starts at $C021 and 'done' at $C036 (checked below)
Check 'five-step program layout' (($p1.Length -eq 0x55) -and ($p1[0x21] -eq 0x2C) -and ($p1[0x36] -eq 0xA9)) "length $($p1.Length), loop opcode $('{0:X2}' -f $p1[0x21]), done opcode $('{0:X2}' -f $p1[0x36])"
$r1 = Join-Path $tmp 'five-step.nes'; [System.IO.File]::WriteAllBytes($r1, (New-NesRom $p1))

# ---- program 2: sweep with the enable flag clear (period must stay) ------------------------------------------------------------
$p2 = [byte[]](
    0x78, 0xD8, 0xA9, 0x01, 0x8D, 0x15, 0x40,                                   # SEI CLD LDA #1 STA $4015
    0xA9, 0x3F, 0x8D, 0x00, 0x40,                                               # $4000: halt, constant volume $F
    0xA9, 0x0F, 0x8D, 0x01, 0x40,                                               # $4001 = $0F: E=0 P=0 negate shift 7
    0xA9, 0x7C, 0x8D, 0x02, 0x40, 0xA9, 0x41, 0x8D, 0x03, 0x40,               # period $17C, length index 8
    0x4C, 0x1B, 0xC0)                                                           # JMP *
$r2 = Join-Path $tmp 'sweep-disabled.nes'; [System.IO.File]::WriteAllBytes($r2, (New-NesRom $p2))
# ---- program 3: an upward sweep (E=1 P=0 add shift 4, $4001 = $84) started after three idle frames, so the divider is in a defined state ----------------
$p3 = [byte[]](
    0x78, 0xD8, 0xA9, 0x01, 0x8D, 0x15, 0x40,                                   # SEI CLD, pulse 1 on
    0xA9, 0x3F, 0x8D, 0x00, 0x40,                                               # $4000: halt, constant volume $F
    0xA9, 0x00, 0x8D, 0x01, 0x40,                                               # $4001 = 0: sweep off (the divider settles)
    0xA9, 0x7C, 0x8D, 0x02, 0x40, 0xA9, 0x41, 0x8D, 0x03, 0x40,               # period $17C, length index 8
    0xA2, 0x03,                                                                 # LDX #3
    0x2C, 0x02, 0x20, 0x10, 0xFB, 0xCA, 0xD0, 0xF8,                            # w: BIT $2002 / BPL w / DEX / BNE w   (three vblanks)
    0xA9, 0x84, 0x8D, 0x01, 0x40,                                               # $4001 = $84: enabled, P = 0, add, shift 4
    0x4C, 0x2A, 0xC0)                                                           # JMP *
Check 'sweep-reload program layout' (($p3.Length -eq 45) -and ($p3[29] -eq 0x2C) -and ($p3[42] -eq 0x4C)) "length $($p3.Length)"
$r3 = Join-Path $tmp 'sweep-reload.nes'; [System.IO.File]::WriteAllBytes($r3, (New-NesRom $p3))

# expected values, MEASURED on Mesen 2.1.1 (the oracle) on 2026-10-16 with the same programs (power-on RAM zeros); re-measure with -Measure
$expectX = 4          # frames until the length counter hits zero (two units per frame)
$expectPer2 = '17c'   # sweep disabled: the period stays 0x17C
$expectPer3 = @{ 3 = '1ac'; 4 = '1e2'; 5 = '220'; 6 = '266'; 7 = '2b4'; 8 = '30c' }   # p1per at these trace frames of the upward sweep, measured on Mesen

function Invoke-MesenTrace([string]$Rom, [int]$Frames) {
    $out = Join-Path $tmp ([System.IO.Path]::GetFileNameWithoutExtension($Rom) + '.mesen.trace.txt')
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = Join-Path $MesenDir 'Mesen.exe'; $psi.Arguments = '"' + $Rom + '" --luaScriptsToLoad "' + $TraceLua + '" --testRunner'
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; $psi.WorkingDirectory = $MesenDir
    $psi.EnvironmentVariables['VRUN_TRACE_OUT'] = $out; $psi.EnvironmentVariables['VRUN_TRACE_FRAMES'] = [string]$Frames
    $psi.EnvironmentVariables['VRUN_TRACE_RAMFILL'] = 'zeros'; $psi.EnvironmentVariables['VRUN_TRACE_ROM'] = $Rom
    $p = [System.Diagnostics.Process]::Start($psi); [void]$p.WaitForExit(120000)
    if (-not (Test-Path -LiteralPath $out)) { throw "Mesen wrote no trace for $Rom" }
    $out
}
if ($Measure) {
    if (-not $MesenDir -or -not $TraceLua) { Write-Host '-Measure needs -MesenDir and BROKENDEV_TRACE_LUA'; exit 2 }
    $m1 = Invoke-MesenTrace $r1 40; Write-Host ("Mesen five-step: X = {0}" -f [Convert]::ToInt32((Get-TraceColumn $m1 'x' 38), 16))
    $m2 = Invoke-MesenTrace $r2 30; Write-Host ("Mesen sweep-disabled: p1per after 25 frames = {0}" -f (Get-TraceColumn $m2 'p1per' 25))
    $m3 = Invoke-MesenTrace $r3 30; Write-Host ("Mesen sweep-reload: p1per at trace frames 3..10 = " + ((3..10 | ForEach-Object { 'f' + $_ + '=' + (Get-TraceColumn $m3 'p1per' $_) }) -join ' '))
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue; exit 0
}
try {
    # five-step
    $t = Invoke-Trace $r1 40 'FIX'
    $x = [Convert]::ToInt32((Get-TraceColumn $t 'x' 38), 16)
    Check 'five-step: pulse length lasts the frames Mesen measured' ($x -eq $expectX) "X = $x frames (Mesen: $expectX)"
    $tf = Invoke-Trace $r1 40 'FMC'
    $xf = [Convert]::ToInt32((Get-TraceColumn $tf 'x' 38), 16)
    Write-Host "         negative control: the older FMC core measures X = $xf for the same program ($(if ($xf -eq $expectX) { 'it AGREES, so this guard does not discriminate against FMC' } else { 'it DISAGREES, so the guard can fail' }))"
    # sweep disabled
    $t = Invoke-Trace $r2 30 'FIX'
    $per = Get-TraceColumn $t 'p1per' 25
    Check 'sweep with the enable flag clear leaves the period alone' ($per -eq $expectPer2) "p1per = $per after 25 frames (Mesen: $expectPer2)"
    $tf = Invoke-Trace $r2 30 'FMC'
    $perf = Get-TraceColumn $tf 'p1per' 25
    Write-Host "         negative control: the older FMC core shows p1per = $perf ($(if ($perf -eq $expectPer2) { 'agrees with Mesen' } else { 'DISAGREES, so the guard can fail' }))"
    # sweep reload order
    # the period must follow Mesen half-frame by half-frame after the sweep starts (the old code was one half-frame late)
    $t = Invoke-Trace $r3 30 'FIX'
    $got = @(); $okAll = $true
    foreach ($f in ($expectPer3.Keys | Sort-Object)) { $v = Get-TraceColumn $t 'p1per' $f; $got += "f$f=$v"; if ($v -ne $expectPer3[$f]) { $okAll = $false } }
    Check 'sweep reload: the period follows Mesen half-frame by half-frame' $okAll ("p1per " + ($got -join ' ') + "  (Mesen: " + (($expectPer3.Keys | Sort-Object | ForEach-Object { 'f' + $_ + '=' + $expectPer3[$_] }) -join ' ') + ")")
    $tf = Invoke-Trace $r3 30 'FMC'
    $gotf = @(); $agree = $true
    foreach ($f in ($expectPer3.Keys | Sort-Object)) { $v = Get-TraceColumn $tf 'p1per' $f; $gotf += "f$f=$v"; if ($v -ne $expectPer3[$f]) { $agree = $false } }
    Write-Host "         negative control: the older FMC core shows p1per $($gotf -join ' ') ($(if ($agree) { 'agrees with Mesen' } else { 'DISAGREES, so the guard can fail' }))"
} catch {
    Write-Host "setup problem: $($_.Exception.Message)"; Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue; exit 2
}

# optional: re-measure the expected values with Mesen itself
if ($MesenDir -and $TraceLua -and (Test-Path -LiteralPath (Join-Path $MesenDir 'Mesen.exe')) -and (Test-Path -LiteralPath $TraceLua)) {
    $env:VRUN_TRACE_OUT = Join-Path $tmp 'mesen1.txt'; $env:VRUN_TRACE_FRAMES = '40'; $env:VRUN_TRACE_RAMFILL = 'zeros'; $env:VRUN_TRACE_ROM = $r1
    Push-Location $MesenDir
    & (Join-Path $MesenDir 'Mesen.exe') $r1 --luaScriptsToLoad $TraceLua --testRunner | Out-Null
    Pop-Location
    if (Test-Path $env:VRUN_TRACE_OUT) { $mx = [Convert]::ToInt32((Get-TraceColumn $env:VRUN_TRACE_OUT 'x' 38), 16); Check 'oracle re-measured: Mesen still says X = 4' ($mx -eq $expectX) "Mesen X = $mx" }
    else { Write-Host 'oracle re-measure skipped: Mesen wrote no trace' }
} else { Write-Host 'oracle re-measure not run (pass -MesenDir and set BROKENDEV_TRACE_LUA); the constants above were measured on 2026-10-16' }

Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
if ($fails -eq 0) { Write-Host 'apu-regress: all guards hold'; exit 0 } else { Write-Host "apu-regress: $fails guard(s) FAILED"; exit 1 }
