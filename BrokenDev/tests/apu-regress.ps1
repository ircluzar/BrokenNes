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
  (4, 5: envelope restart, linear-counter reload - see the programs.)
  6. triangle-phase (2026-10-29): a $400B write must not reset the triangle's step or timer: a twin rewriting $400B every frame plays the same steps as
     a twin rewriting $400A (read from 'Workshop --apu-audio --phase'). Mesen 2.1.1: both twins 6 1 29 24 19 14 10 5 0 28 ... at frames 5..; the old core
     held the $400B twin at step 9 every frame.
  7. sweep-mute (2026-10-29): a target period above $7FF mutes a pulse at shift 0 too ($4001 = $00, period $47C: 0 of 64 samples heard on Mesen), the
     boundary $3FF plays (36), and a target below 8 does not mute ($08 / $47C: 30; $09 / $00A: 32). The old core played 7a and silenced 7d.

  The programs run through 'BrokenNes.Workshop --trace' (CPU/PPU/APU FIX); the checks read the trace's x, p1len and p1per columns at fixed frames.
  NEGATIVE CONTROLS (shown able to fail): every program is also run on a different, older APU core (FMC), which has the behaviour being guarded
  against only if it does not keep the fixed behaviour; the test prints what that core did and a check that PASSES on it would be reported as "not discriminating".
  The oracle values can be re-measured with Mesen: -MesenDir (a Mesen 2 folder) and -Measure; the tracer is the user's state_trace.lua
  (BrokenDev's copy in tools\vrun\mesen\, or BROKENDEV_TRACE_LUA).

  Exit code: 0 all guards hold, 1 a guard failed, 2 setup problem.
#>
param(
    [Parameter(Mandatory = $true)][string]$Workshop,
    [string]$MesenDir = '',
    [string]$TraceLua = $(if ($env:BROKENDEV_TRACE_LUA) { $env:BROKENDEV_TRACE_LUA } else { Join-Path (Split-Path -Parent $PSScriptRoot) 'tools\vrun\mesen\state_trace.lua' }),
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
# the waveform-phase trace (ApuAudioCli --phase): tseq, ttimer, p1out, p2out ... in decimal, one row per frame
function Invoke-Phase([string]$Rom, [int]$Frames) {
    $out = Join-Path $tmp ([System.IO.Path]::GetFileNameWithoutExtension($Rom) + '.phase.txt')
    $r = & $Workshop --apu-audio --rom $Rom --phase $out --frames $Frames --cpu FIX --ppu FIX --apu FIX 2>&1 | Out-String
    if (-not (Test-Path -LiteralPath $out)) { throw "no phase trace written:`n$r" }
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

# ---- program 4: a write to the envelope register ($4000 / $4004 / $400C) must NOT restart the envelope (only $4003 / $4007 / $400F does) ----------
$p4 = [byte[]](
    0x78, 0xD8, 0xA9, 0x0F, 0x8D, 0x15, 0x40,                                   # SEI CLD LDA #$0F STA $4015 (pulse 1, pulse 2, triangle, noise on)
    0xA9, 0x00, 0x8D, 0x00, 0x40, 0x8D, 0x04, 0x40, 0x8D, 0x0C, 0x40,          # $4000 $4004 $400C = 0: envelope mode, no loop, divider period 1
    0xA9, 0x08, 0x8D, 0x01, 0x40, 0x8D, 0x05, 0x40,                            # sweeps off
    0xA9, 0x7C, 0x8D, 0x02, 0x40, 0x8D, 0x06, 0x40,                            # pulse periods $17C
    0xA9, 0x08, 0x8D, 0x03, 0x40, 0x8D, 0x07, 0x40, 0x8D, 0x0F, 0x40,          # length index 1 (254): this write starts each envelope
    0xA2, 0x08,                                                                 # LDX #8
    0x2C, 0x02, 0x20, 0x10, 0xFB, 0xCA, 0xD0, 0xF8,                            # eight vblanks: every envelope has decayed to 0
    0xA9, 0x00, 0x8D, 0x00, 0x40, 0x8D, 0x04, 0x40, 0x8D, 0x0C, 0x40,          # write the SAME envelope registers again: hardware leaves the decayed level alone
    0x4C, 0x42, 0xC0)                                                           # JMP *
Check 'envelope-restart program layout' (($p4.Length -eq 69) -and ($p4[47] -eq 0x2C) -and ($p4[66] -eq 0x4C)) "length $($p4.Length)"
$r4 = Join-Path $tmp 'envelope-restart.nes'; [System.IO.File]::WriteAllBytes($r4, (New-NesRom $p4))
# ---- program 5: a write to $4008 must NOT reload the triangle's linear counter (only $400B sets the reload flag) ----------------------------------
$p5 = [byte[]](
    0x78, 0xD8, 0xA9, 0x04, 0x8D, 0x15, 0x40,                                   # SEI CLD, triangle on
    0xA9, 0x7F, 0x8D, 0x08, 0x40,                                               # $4008 = $7F: control clear, reload value 127
    0xA9, 0x40, 0x8D, 0x0A, 0x40,                                               # period low
    0xA9, 0x08, 0x8D, 0x0B, 0x40,                                               # length index 1, period high 0: this write sets the reload flag
    0xA2, 0x09,                                                                 # LDX #9
    0x2C, 0x02, 0x20, 0x10, 0xFB, 0xCA, 0xD0, 0xF8,                            # nine vblanks: the counter has counted down from 127
    0xA9, 0x7F, 0x8D, 0x08, 0x40,                                               # $4008 = $7F again: hardware does not reload the counter
    0x4C, 0x25, 0xC0)                                                           # JMP *
Check 'linear-counter program layout' (($p5.Length -eq 40) -and ($p5[24] -eq 0x2C) -and ($p5[37] -eq 0x4C)) "length $($p5.Length)"
$r5 = Join-Path $tmp 'linear-reload.nes'; [System.IO.File]::WriteAllBytes($r5, (New-NesRom $p5))

# ---- program 6: a write to $400B must NOT reset the triangle's phase (nesdev "APU": "There is no way to reset the triangle channel's phase") ---------
# Twin programs, identical except for the register rewritten once per frame: 6a rewrites $400B (length, period high), 6b rewrites $400A (period low)
# with the value already there. $4008 = $FF keeps the linear counter reloading and the length counter halted, so neither write changes WHEN the
# triangle runs; on hardware the two must therefore play the identical waveform, step for step. The old core zeroed the sequence step (and the
# timer divider) on every $400B write, so 6a sat on the same few steps every frame (the VRUN NES songs satnkrt / vectorplane, which rewrite the
# triangle every two frames, came out at the rewrite rate).
function New-TriangleProgram([byte]$RewriteReg, [byte]$RewriteVal) {
    [byte[]](
        0x78, 0xD8, 0xA9, 0x04, 0x8D, 0x15, 0x40,                               # SEI CLD, triangle on
        0xA9, 0xFF, 0x8D, 0x08, 0x40,                                           # $4008 = $FF: control set (linear counter keeps reloading, length halted)
        0xA9, 0xF1, 0x8D, 0x0A, 0x40,                                           # period low $F1 (242 cycles a step: ~27 steps of drift per frame)
        0xA9, 0xF8, 0x8D, 0x0B, 0x40,                                           # $400B = $F8: length index 31, period high 0
        0x2C, 0x02, 0x20, 0x10, 0xFB,                                           # loop ($C016): BIT $2002 / BPL loop   (wait for vblank)
        0xA9, $RewriteVal, 0x8D, $RewriteReg, 0x40,                             # rewrite one register with the value it already holds
        0x4C, 0x16, 0xC0)                                                       # JMP loop
}
$p6a = New-TriangleProgram 0x0B 0xF8; $p6b = New-TriangleProgram 0x0A 0xF1
Check 'triangle-phase program layout' (($p6a.Length -eq 35) -and ($p6a[22] -eq 0x2C) -and ($p6a[32] -eq 0x4C)) "length $($p6a.Length)"
$r6a = Join-Path $tmp 'tri-rewrite-400b.nes'; [System.IO.File]::WriteAllBytes($r6a, (New-NesRom $p6a))
$r6b = Join-Path $tmp 'tri-rewrite-400a.nes'; [System.IO.File]::WriteAllBytes($r6b, (New-NesRom $p6b))

# ---- program 7: the sweep unit mutes on target period > $7FF whatever the shift count (nesdev "APU Sweep": "This muting happens regardless of the
# state of the sweep unit"; the shift only decides whether the period is updated). Both pulses at constant volume 15, duty 50%, sweep register as given:
#   7a  $4001/$4005 = $00 (the power-on state: a game that never writes them), period $47C: target $8F8 > $7FF -> MUTED on hardware
#   7b  $00, period $3FF: target $7FE -> plays (the boundary)
#   7c  $08 (negate, shift 0: the documented way to switch a sweep off), period $47C: target <= period -> plays
#   7d  $09 (negate, shift 1), period $00A: target 4 (pulse 1) / 5 (pulse 2) is below 8 but only the CURRENT period < 8 mutes -> plays
# The old core returned "not muted" for every shift-0 case (7a was heard) and muted on a target below 8 (7d was silent).
function New-SweepMuteProgram([byte]$Sweep, [byte]$Lo, [byte]$Hi) {
    [byte[]](
        0x78, 0xD8, 0xA9, 0x03, 0x8D, 0x15, 0x40,                               # SEI CLD, pulses 1 and 2 on
        0xA9, 0xBF, 0x8D, 0x00, 0x40, 0x8D, 0x04, 0x40,                        # $4000 / $4004 = $BF: duty 50%, halt, constant volume 15
        0xA9, $Sweep, 0x8D, 0x01, 0x40, 0x8D, 0x05, 0x40,                      # $4001 / $4005
        0xA9, $Lo, 0x8D, 0x02, 0x40, 0x8D, 0x06, 0x40,                         # period low
        0xA9, $Hi, 0x8D, 0x03, 0x40, 0x8D, 0x07, 0x40,                         # length index 1, period high
        0x4C, 0x27, 0xC0)                                                       # JMP *
}
$sweepCases = [ordered]@{
    '7a' = @{ Sweep = 0x00; Lo = 0x7C; Hi = 0x0C; Muted = $true;  What = 'shift 0, period $47C (target $8F8)' }
    '7b' = @{ Sweep = 0x00; Lo = 0xFF; Hi = 0x0B; Muted = $false; What = 'shift 0, period $3FF (target $7FE)' }
    '7c' = @{ Sweep = 0x08; Lo = 0x7C; Hi = 0x0C; Muted = $false; What = 'negate shift 0, period $47C' }
    '7d' = @{ Sweep = 0x09; Lo = 0x0A; Hi = 0x08; Muted = $false; What = 'negate shift 1, period $00A (target < 8)' }
}
$p7 = New-SweepMuteProgram 0 0x7C 0x0C
Check 'sweep-mute program layout' (($p7.Length -eq 42) -and ($p7[39] -eq 0x4C)) "length $($p7.Length)"
foreach ($k in $sweepCases.Keys) { $c = $sweepCases[$k]; $c.Rom = Join-Path $tmp "sweep-mute-$k.nes"; [System.IO.File]::WriteAllBytes($c.Rom, (New-NesRom (New-SweepMuteProgram $c.Sweep $c.Lo $c.Hi))) }

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
    $m4 = Invoke-MesenTrace $r4 24
    Write-Host ("Mesen envelope-restart: p1vol/p2vol/nvol at trace frames 5..16 = " + ((5..16 | ForEach-Object { 'f' + $_ + '=' + (Get-TraceColumn $m4 'p1vol' $_) + '/' + (Get-TraceColumn $m4 'p2vol' $_) + '/' + (Get-TraceColumn $m4 'nvol' $_) }) -join ' '))
    $m5 = Invoke-MesenTrace $r5 24
    Write-Host ("Mesen linear-reload: tlin at trace frames 5..16 = " + ((5..16 | ForEach-Object { 'f' + $_ + '=' + (Get-TraceColumn $m5 'tlin' $_) }) -join ' '))
    # programs 6 and 7 through the phase tracer (apu_phase_trace.lua next to state_trace.lua). Its fields lag Mesen's CPU, but both twins of
    # program 6 stop at the same instruction, so their lag is the same; and a muted pulse's output is 0 whatever the lag.
    $phaseLua = Join-Path (Split-Path -Parent $TraceLua) 'apu_phase_trace.lua'
    function Invoke-MesenPhase([string]$Rom, [int]$Frames) {
        $out = Join-Path $tmp ([System.IO.Path]::GetFileNameWithoutExtension($Rom) + '.mesen.phase.txt')
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = Join-Path $MesenDir 'Mesen.exe'; $psi.Arguments = '"' + $Rom + '" --luaScriptsToLoad "' + $phaseLua + '" --testRunner'
        $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; $psi.WorkingDirectory = $MesenDir
        $psi.EnvironmentVariables['VRUN_PHASE_OUT'] = $out; $psi.EnvironmentVariables['VRUN_PHASE_FRAMES'] = [string]$Frames
        $psi.EnvironmentVariables['VRUN_PHASE_RAMFILL'] = 'none'; $psi.EnvironmentVariables['VRUN_PHASE_ROM'] = $Rom
        $p = [System.Diagnostics.Process]::Start($psi); [void]$p.WaitForExit(120000)
        if (-not (Test-Path -LiteralPath $out)) { throw "Mesen wrote no phase trace for $Rom" }
        $out
    }
    $m6a = Invoke-MesenPhase $r6a 40; $m6b = Invoke-MesenPhase $r6b 40
    Write-Host ("Mesen triangle-phase: tseq frames 5..20 `$400B twin = " + ((5..20 | ForEach-Object { Get-TraceColumn $m6a 'tseq' $_ }) -join ' '))
    Write-Host ("Mesen triangle-phase: tseq frames 5..20 `$400A twin = " + ((5..20 | ForEach-Object { Get-TraceColumn $m6b 'tseq' $_ }) -join ' '))
    foreach ($k in $sweepCases.Keys) {
        $m = Invoke-MesenPhase $sweepCases[$k].Rom 40
        $heard = 0; foreach ($f in 5..36) { foreach ($col in 'p1out', 'p2out') { if ((Get-TraceColumn $m $col $f) -ne '0') { $heard++ } } }
        Write-Host ("Mesen sweep mute {0} ({1}): p1out/p2out non-zero on {2} of 64 frame samples" -f $k, $sweepCases[$k].What, $heard)
    }
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
    # a write to $4000 / $4004 / $400C must not restart a decayed envelope: Mesen keeps all three volumes at 0 through frame 16
    $t = Invoke-Trace $r4 24 'FIX'
    $bad = @(); foreach ($f in 9..16) { foreach ($c in 'p1vol', 'p2vol', 'nvol') { $v = Get-TraceColumn $t $c $f; if ($v -ne '0') { $bad += "f$f $c=$v" } } }
    Check 'envelope: rewriting the envelope register does not restart a decayed envelope' ($bad.Count -eq 0) $(if ($bad.Count -eq 0) { 'p1vol, p2vol and nvol stay 0 at frames 9..16 (Mesen: 0)' } else { 'volumes came back: ' + ($bad[0..([Math]::Min(5, $bad.Count - 1))] -join ', ') + ' (Mesen: 0)' })
    $tf = Invoke-Trace $r4 24 'FMC'
    $badf = @(); foreach ($f in 9..16) { foreach ($c in 'p1vol', 'p2vol', 'nvol') { $v = Get-TraceColumn $tf $c $f; if ($v -ne '0') { $badf += "f$f $c=$v" } } }
    Write-Host "         negative control: the older FMC core $(if ($badf.Count -eq 0) { 'keeps the volumes at 0 (agrees with Mesen)' } else { 'brings volumes back (' + $badf[0] + '): DISAGREES, so the guard can fail' })"
    # a write to $4008 must not reload the linear counter: Mesen counts down 4 per frame (0x69 at frame 5 ... 0x3d at frame 16) with no jump back to 0x7f
    $t = Invoke-Trace $r5 24 'FIX'
    $tl = @(); foreach ($f in 5..16) { $tl += [Convert]::ToInt32((Get-TraceColumn $t 'tlin' $f), 16) }
    $mono = $true; for ($i = 1; $i -lt $tl.Count; $i++) { if ($tl[$i] -gt $tl[$i - 1]) { $mono = $false } }
    $endOk = ([Math]::Abs($tl[-1] - 0x3d) -le 4)
    Check 'linear counter: rewriting $4008 does not reload the counter' ($mono -and $endOk) ("tlin frames 5..16 = " + (($tl | ForEach-Object { '{0:x}' -f $_ }) -join ' ') + "  (Mesen: 69 65 61 5d 59 55 51 4d 49 45 41 3d)")
    $tf = Invoke-Trace $r5 24 'FMC'
    $tlf = @(); foreach ($f in 5..16) { $tlf += [Convert]::ToInt32((Get-TraceColumn $tf 'tlin' $f), 16) }
    $monof = $true; for ($i = 1; $i -lt $tlf.Count; $i++) { if ($tlf[$i] -gt $tlf[$i - 1]) { $monof = $false } }
    Write-Host "         negative control: the older FMC core shows tlin $(($tlf | ForEach-Object { '{0:x}' -f $_ }) -join ' ') ($(if ($monof) { 'it also counts down, agrees with Mesen' } else { 'it jumps back up: DISAGREES, so the guard can fail' }))"
    # a write to $400B must not reset the triangle's phase: the $400B twin and the $400A twin play the identical step sequence and divider
    $ta = Invoke-Phase $r6a 40; $tb = Invoke-Phase $r6b 40
    $seqA = @(); $seqB = @(); $same = $true
    foreach ($f in 5..36) {
        $a = (Get-TraceColumn $ta 'tseq' $f) + '/' + (Get-TraceColumn $ta 'ttimer' $f); $b = (Get-TraceColumn $tb 'tseq' $f) + '/' + (Get-TraceColumn $tb 'ttimer' $f)
        $seqA += (Get-TraceColumn $ta 'tseq' $f); $seqB += (Get-TraceColumn $tb 'tseq' $f); if ($a -ne $b) { $same = $false }
    }
    $moving = (@($seqB | Sort-Object -Unique).Count -ge 8)
    Check 'triangle: a $400B write does not reset the phase (the $400B twin plays the $400A twin''s steps)' ($same -and $moving) ("tseq frames 5..20: `$400B " + ($seqA[0..15] -join ' ') + "  /  `$400A " + ($seqB[0..15] -join ' ') + $(if (-not $moving) { '  (the $400A twin does not move: the program is wrong)' } else { '' }))
    # the sweep unit mutes on target > $7FF at shift 0, and never on a target below 8
    foreach ($k in $sweepCases.Keys) {
        $c = $sweepCases[$k]; $t = Invoke-Phase $c.Rom 40
        $heard = 0; foreach ($f in 5..36) { foreach ($col in 'p1out', 'p2out') { if ((Get-TraceColumn $t $col $f) -ne '0') { $heard++ } } }
        $ok = if ($c.Muted) { $heard -eq 0 } else { $heard -ge 8 }
        Check "sweep mute ${k}: $($c.What) is $(if ($c.Muted) { 'MUTED' } else { 'heard' })" $ok "p1out/p2out non-zero on $heard of 64 frame samples ($(if ($c.Muted) { 'hardware: 0' } else { 'hardware: about half' }))"
    }
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
