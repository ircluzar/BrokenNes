<#
.SYNOPSIS
    Differential replay differ: aligns two frame-state traces and reports the first divergence.

.DESCRIPTION
    Consumes the shared cross-emulator trace format produced by
      - Mesen side     : sys0/simulations/VRUN/tools/state_trace.lua
      - BrokenNes side : Workshop --trace  (Workshop/TraceCli.cs)

    Format (UTF-8, LF; '#' lines are header/comment and ignored as data):

        <frame>|<pc>|<a>|<x>|<y>|<sp>|<p>|<ramhash>

    The framebuffer is deliberately not part of the record: NES palettes are not
    standardized, so two correct emulators legitimately produce different RGB.
    RAM + CPU state is the emulator-independent oracle.

    Designed to be usable as a CI gate: exit code 0 = identical over the compared
    range, 1 = divergence found, 2 = usage/parse error.

    Because the overwhelmingly most common cause of a "divergence" is a HARNESS
    artifact rather than an emulation defect, this differ also:
      * compares the two headers' rom-sha256 and power-on RAM hash and shouts if
        they disagree (a different power-on fill makes every frame differ for a
        reason that has nothing to do with emulation);
      * runs a frame-shift probe (-ProbeShift) that tests whether side B's trace
        matches side A shifted by +/-N frames, which is what an off-by-one frame
        boundary or an input applied a frame early looks like.

.PARAMETER A
    First trace file (conventionally the reference / Mesen side).

.PARAMETER B
    Second trace file (conventionally BrokenNes).

.PARAMETER Context
    How many frames of context to print before the first divergence. Default 5.

.PARAMETER Follow
    How many frames after the first divergence to print, used to classify the
    divergence as persistent or a one-frame blip. Default 12.

.PARAMETER ProbeShift
    Max +/- frame shift to test when looking for a constant frame-alignment
    offset between the two traces. 0 disables. Default 3.

.PARAMETER LabelA / .PARAMETER LabelB
    Display names for the two sides. Default: the file base names.

.PARAMETER Quiet
    Suppress the header/determinism section; print only the verdict and the
    divergence report.

.PARAMETER RamDumpA / .PARAMETER RamDumpB
    Directories holding raw 2048-byte work-RAM dumps named ram_f<N>.bin, as
    produced by `--trace --ram-dump-at N,...` (BrokenNes) and
    VRUN_TRACE_RAMDUMP=N,... (Mesen). When both are given, the differ stops
    guessing from a hash and reports the actual differing ADDRESSES around the
    first divergence. This matters more than it sounds: the ramhash column is
    an all-or-nothing comparison of 2048 bytes taken at an instant two
    different emulators cannot align to better than a few CPU cycles, so on a
    ROM that touches RAM every frame it goes to 0% agreement while the states
    are in fact identical bar two or three in-flight scratch bytes. Differing
    BYTE COUNT, not hash equality, is the oracle that survives that.

.PARAMETER RamMap
    A markdown RAM map (VRUN's docs/23-ram-map.md) used to put names on those
    addresses. Rows look like `| $0344 | 2 | mus_sq1_wait | ... |`. Names are
    printed with a health warning: a map measured against one build does not
    necessarily apply to the ROM under test.

.EXAMPLE
    ./diff-trace.ps1 mesen.txt brokennes.txt
    ./diff-trace.ps1 -A mesen.txt -B bn.txt -Context 10 -Follow 30
    ./diff-trace.ps1 -A ms.txt -B bn.txt -RamDumpA ramA -RamDumpB ramB `
                     -RamMap ../../sys0/simulations/VRUN/docs/23-ram-map.md
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$A,
    [Parameter(Mandatory = $true, Position = 1)][string]$B,
    [int]$Context = 5,
    [int]$Follow = 12,
    [int]$ProbeShift = 3,
    [string]$LabelA,
    [string]$LabelB,
    [switch]$Quiet,
    [string]$RamDumpA,
    [string]$RamDumpB,
    [string]$RamMap
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Field names in record order, after the leading frame number.
$FIELDS = @('pc', 'a', 'x', 'y', 'sp', 'p', 'ramhash')

function Fail([string]$msg) {
    Write-Host "ERROR: $msg" -ForegroundColor Red
    exit 2
}

function Read-Trace([string]$path, [string]$label) {
    if (-not (Test-Path -LiteralPath $path)) { Fail "trace file not found: $path" }
    $lines = [System.IO.File]::ReadAllLines((Resolve-Path -LiteralPath $path))

    $header = New-Object System.Collections.Generic.List[string]
    $records = @{}                       # frame -> record object
    $order = New-Object System.Collections.Generic.List[int]
    $bad = New-Object System.Collections.Generic.List[string]
    $fatal = $null

    $lineNo = 0
    foreach ($raw in $lines) {
        $lineNo++
        $line = $raw.TrimEnd("`r")       # tolerate a CRLF trace rather than silently mis-diffing it
        if ($line.Length -eq 0) { continue }
        if ($line[0] -eq '#') {
            $header.Add($line.Substring(1).Trim())
            if ($line -match 'FATAL|ABORTED|CRASHED') { $fatal = $line }
            continue
        }
        $parts = $line.Split('|')
        if ($parts.Count -ne 8) {
            $bad.Add("${label}:${lineNo}: expected 8 pipe-separated fields, got $($parts.Count): '$line'")
            continue
        }
        [int]$fr = 0
        if (-not [int]::TryParse($parts[0], [ref]$fr)) {
            $bad.Add("${label}:${lineNo}: frame '$($parts[0])' is not an integer")
            continue
        }
        if ($records.ContainsKey($fr)) {
            $bad.Add("${label}:${lineNo}: duplicate record for frame $fr")
            continue
        }
        $records[$fr] = [pscustomobject]@{
            frame   = $fr
            pc      = $parts[1].ToLowerInvariant()
            a       = $parts[2].ToLowerInvariant()
            x       = $parts[3].ToLowerInvariant()
            y       = $parts[4].ToLowerInvariant()
            sp      = $parts[5].ToLowerInvariant()
            p       = $parts[6].ToLowerInvariant()
            ramhash = $parts[7].ToLowerInvariant()
            raw     = $line
        }
        $order.Add($fr)
    }

    [pscustomobject]@{
        Path    = (Resolve-Path -LiteralPath $path).Path
        Label   = $label
        Header  = $header
        Records = $records
        Frames  = $order
        Bad     = $bad
        Fatal   = $fatal
    }
}

# Pull "key: value" out of either tracer's header. The two tracers word their
# headers differently, so match on a regex over the whole header block.
function Get-HeaderValue($trace, [string]$pattern) {
    foreach ($h in $trace.Header) {
        $m = [regex]::Match($h, $pattern)
        if ($m.Success) { return $m.Groups[1].Value.Trim() }
    }
    return $null
}

function Format-Record($rec) {
    if ($null -eq $rec) { return '(absent)' }
    return $rec.raw
}

# Load a markdown RAM map into address -> name. Rows: | `$0344` | 2 | `mus_sq1_wait` | ... |
function Read-RamMap([string]$path) {
    $map = @{}
    if (-not $path) { return $map }
    if (-not (Test-Path -LiteralPath $path)) { Fail "RAM map not found: $path" }
    foreach ($line in [System.IO.File]::ReadAllLines((Resolve-Path -LiteralPath $path))) {
        $m = [regex]::Match($line, '^\s*\|\s*`?\$([0-9A-Fa-f]{4})`?\s*\|\s*(\d+)\s*\|\s*`?([^`|]+?)`?\s*\|')
        if (-not $m.Success) { continue }
        $addr = [Convert]::ToInt32($m.Groups[1].Value, 16)
        $size = [int]$m.Groups[2].Value
        $name = $m.Groups[3].Value.Trim()
        for ($k = 0; $k -lt [Math]::Max(1, $size); $k++) {
            if (-not $map.ContainsKey($addr + $k)) {
                $map[$addr + $k] = if ($size -gt 1) { "$name+$k" } else { $name }
            }
        }
    }
    return $map
}

function Get-RamDump([string]$dir, [int]$frame) {
    if (-not $dir) { return $null }
    $p = Join-Path $dir "ram_f$frame.bin"
    if (-not (Test-Path -LiteralPath $p)) { return $null }
    return [System.IO.File]::ReadAllBytes($p)
}

# Standing NES address-space landmarks, used when no symbol covers an address.
function Get-RamRegion([int]$addr) {
    if ($addr -lt 0x100) { return 'zero page' }
    if ($addr -lt 0x200) { return 'HARDWARE STACK - bytes below SP are dead, expect noise here' }
    return 'work RAM'
}

function Compare-Record($ra, $rb) {
    # Returns the list of field names that differ, in record order.
    $d = @()
    foreach ($f in $FIELDS) { if ($ra.$f -ne $rb.$f) { $d += $f } }
    return , $d
}

# ---------------------------------------------------------------- load

if (-not $LabelA) { $LabelA = [System.IO.Path]::GetFileNameWithoutExtension($A) }
if (-not $LabelB) { $LabelB = [System.IO.Path]::GetFileNameWithoutExtension($B) }
$w = [Math]::Max($LabelA.Length, $LabelB.Length)
$padA = $LabelA.PadRight($w)
$padB = $LabelB.PadRight($w)

$ta = Read-Trace $A $LabelA
$tb = Read-Trace $B $LabelB

if ($ta.Bad.Count -gt 0 -or $tb.Bad.Count -gt 0) {
    Write-Host "Malformed trace lines:" -ForegroundColor Red
    foreach ($m in $ta.Bad) { Write-Host "  $m" }
    foreach ($m in $tb.Bad) { Write-Host "  $m" }
    exit 2
}
if ($ta.Records.Count -eq 0) { Fail "$LabelA has no records ($($ta.Path))$(if($ta.Fatal){" - $($ta.Fatal)"})" }
if ($tb.Records.Count -eq 0) { Fail "$LabelB has no records ($($tb.Path))$(if($tb.Fatal){" - $($tb.Fatal)"})" }

# Contract check: frames must start at 0 and step by exactly 1.
foreach ($t in @($ta, $tb)) {
    for ($i = 0; $i -lt $t.Frames.Count; $i++) {
        if ($t.Frames[$i] -ne $i) {
            Fail "$($t.Label): frame numbering broken - record #$i carries frame $($t.Frames[$i]); the contract is 0-based and +1 per record"
        }
    }
}

$nA = $ta.Frames.Count
$nB = $tb.Frames.Count
$common = [Math]::Min($nA, $nB)

# ---------------------------------------------------------------- header / determinism

if (-not $Quiet) {
    Write-Host "=== traces ===" -ForegroundColor Cyan
    Write-Host ("  {0} : {1}" -f $padA, $ta.Path)
    Write-Host ("  {0} : {1}" -f $padB, $tb.Path)
    Write-Host ("  {0} : {1} record(s), frames 0..{2}" -f $padA, $nA, ($nA - 1))
    Write-Host ("  {0} : {1} record(s), frames 0..{2}" -f $padB, $nB, ($nB - 1))
    if ($nA -ne $nB) {
        Write-Host ("  NOTE: differing lengths; comparing the common range 0..{0}" -f ($common - 1)) -ForegroundColor Yellow
    }

    Write-Host ""
    Write-Host "=== determinism cross-check (header, not data) ===" -ForegroundColor Cyan

    $checks = @(
        @{ name = 'rom-sha256';        pat = '^rom-sha256:\s*(.+)$' },
        @{ name = 'power-on ram hash'; pat = '(?:power-on-ramhash:|post-fill pre-boot RAM sha256\s*=)\s*([0-9a-fA-F]+)' },
        @{ name = 'input-script';      pat = '^input-script:\s*(.+)$' }
    )
    foreach ($c in $checks) {
        $va = Get-HeaderValue $ta $c.pat
        $vb = Get-HeaderValue $tb $c.pat
        if ($null -eq $va -and $null -eq $vb) {
            Write-Host ("  {0,-18} : (absent from both headers)" -f $c.name) -ForegroundColor DarkGray
            continue
        }
        # Power-on hashes are emitted at different truncations by the two tracers
        # (16 hex chars vs the full 64), so compare on the shorter prefix.
        $ca = $va; $cb = $vb
        if ($ca -and $cb -and $ca.Length -ne $cb.Length) {
            $len = [Math]::Min($ca.Length, $cb.Length)
            $ca = $ca.Substring(0, $len); $cb = $cb.Substring(0, $len)
        }
        if ($ca -eq $cb) {
            Write-Host ("  {0,-18} : MATCH  {1}" -f $c.name, $va) -ForegroundColor Green
        }
        else {
            Write-Host ("  {0,-18} : ***MISMATCH***" -f $c.name) -ForegroundColor Red
            Write-Host ("      {0} : {1}" -f $padA, $va)
            Write-Host ("      {0} : {1}" -f $padB, $vb)
            if ($c.name -eq 'power-on ram hash') {
                Write-Host "      A different power-on RAM fill makes every downstream frame differ for a" -ForegroundColor Red
                Write-Host "      reason that is NOT an emulation defect. Align the fills before reading" -ForegroundColor Red
                Write-Host "      anything into the divergence below." -ForegroundColor Red
            }
            if ($c.name -eq 'rom-sha256') {
                Write-Host "      The two sides ran DIFFERENT ROMs. Nothing below is meaningful." -ForegroundColor Red
            }
        }
    }
    if ($ta.Fatal) { Write-Host ("  {0}: {1}" -f $padA, $ta.Fatal) -ForegroundColor Red }
    if ($tb.Fatal) { Write-Host ("  {0}: {1}" -f $padB, $tb.Fatal) -ForegroundColor Red }
    Write-Host ""
}

# ---------------------------------------------------------------- compare

$firstDiv = -1
$firstFields = @()
for ($f = 0; $f -lt $common; $f++) {
    $d = Compare-Record $ta.Records[$f] $tb.Records[$f]
    if ($d.Count -gt 0) { $firstDiv = $f; $firstFields = $d; break }
}

# Per-field divergence census over the whole common range - tells you whether
# only the RAM hash drifts (state difference) or the CPU registers go too
# (control-flow difference).
$fieldCounts = @{}
foreach ($fn in $FIELDS) { $fieldCounts[$fn] = 0 }
$divFrames = 0
for ($f = 0; $f -lt $common; $f++) {
    $d = Compare-Record $ta.Records[$f] $tb.Records[$f]
    if ($d.Count -gt 0) {
        $divFrames++
        foreach ($fn in $d) { $fieldCounts[$fn]++ }
    }
}

# ---------------------------------------------------------------- frame-shift probe

$shiftReport = @()
if ($ProbeShift -gt 0) {
    foreach ($s in (-$ProbeShift..$ProbeShift)) {
        # side B frame (f) compared against side A frame (f + s)
        $lo = [Math]::Max(0, -$s)
        $hi = [Math]::Min($nB - 1, $nA - 1 - $s)
        if ($hi -lt $lo) { continue }
        $match = 0
        $firstBad = -1
        for ($f = $lo; $f -le $hi; $f++) {
            $d = Compare-Record $ta.Records[$f + $s] $tb.Records[$f]
            if ($d.Count -eq 0) { $match++ }
            elseif ($firstBad -lt 0) { $firstBad = $f }
        }
        $total = $hi - $lo + 1
        $shiftReport += [pscustomobject]@{
            Shift    = $s
            Matched  = $match
            Total    = $total
            Pct      = [Math]::Round(100.0 * $match / $total, 2)
            FirstBad = $firstBad
        }
    }
}

# ---------------------------------------------------------------- report

if ($firstDiv -lt 0) {
    Write-Host "=== VERDICT: IDENTICAL ===" -ForegroundColor Green
    Write-Host ("  {0} frame(s) compared (0..{1}); every field matched on every frame." -f $common, ($common - 1))
    Write-Host ("  Fields compared: {0}" -f ($FIELDS -join ', '))
    if ($nA -ne $nB) {
        Write-Host ("  (lengths differed: {0}={1}, {2}={3}; the tail beyond frame {4} was not compared)" -f $padA, $nA, $padB, $nB, ($common - 1)) -ForegroundColor Yellow
    }
    exit 0
}

Write-Host "=== VERDICT: DIVERGENCE ===" -ForegroundColor Red
Write-Host ("  first divergent frame : {0}" -f $firstDiv)
Write-Host ("  field(s) that broke   : {0}" -f ($firstFields -join ', '))
Write-Host ("  frames matched before : {0} (frames 0..{1})" -f $firstDiv, ($firstDiv - 1))
Write-Host ("  frames compared       : {0}" -f $common)
Write-Host ("  divergent frames      : {0} of {1} ({2}%)" -f $divFrames, $common, ([Math]::Round(100.0 * $divFrames / $common, 2)))
Write-Host ""

# Persistent vs blip: look at the run immediately after the first divergence.
$after = [Math]::Min($common - 1, $firstDiv + $Follow)
$runLen = 0
for ($f = $firstDiv; $f -le $after; $f++) {
    if ((Compare-Record $ta.Records[$f] $tb.Records[$f]).Count -gt 0) { $runLen++ } else { break }
}
$reconverged = ($firstDiv + $runLen) -le $after
if ($reconverged) {
    Write-Host ("  classification        : BLIP - diverges for {0} frame(s) then re-converges at frame {1}" -f $runLen, ($firstDiv + $runLen)) -ForegroundColor Yellow
}
else {
    Write-Host ("  classification        : PERSISTENT - still divergent {0} frame(s) later (through frame {1})" -f $runLen, $after) -ForegroundColor Red
}

Write-Host ""
Write-Host "  per-field divergence census over the full compared range:"
foreach ($fn in $FIELDS) {
    $c = $fieldCounts[$fn]
    $bar = if ($c -gt 0) { '*' } else { ' ' }
    Write-Host ("    {0} {1,-8} {2,6} frame(s)" -f $bar, $fn, $c)
}

# ---------------------------------------------------------------- constant-offset probe
#
# A register column that differs on EVERY frame by the SAME xor/delta is a representation
# difference between the two tracers, not an emulation difference. The one that actually
# bites here: Mesen's emu.getState() 'cpu.ps' reports the 6502's unused bit 5 clear, while
# BrokenNes keeps it set as hardware does - so p differs on 100% of frames, forever, for no
# emulation reason at all. Left undiagnosed that alone makes every frame look divergent.
$constant = @()
foreach ($fn in @('pc', 'a', 'x', 'y', 'sp', 'p')) {
    if ($fieldCounts[$fn] -ne $common) { continue }   # must differ on every single frame
    $xors = @{}; $deltas = @{}
    for ($f = 0; $f -lt $common; $f++) {
        $va = [Convert]::ToInt32($ta.Records[$f].$fn, 16)
        $vb = [Convert]::ToInt32($tb.Records[$f].$fn, 16)
        $xors[($va -bxor $vb)] = $true
        $deltas[($vb - $va)] = $true
        if ($xors.Count -gt 1 -and $deltas.Count -gt 1) { break }
    }
    if ($xors.Count -eq 1) {
        $constant += [pscustomobject]@{ Field = $fn; Kind = 'xor'; Value = ($xors.Keys | Select-Object -First 1) }
    }
    elseif ($deltas.Count -eq 1) {
        $constant += [pscustomobject]@{ Field = $fn; Kind = 'delta'; Value = ($deltas.Keys | Select-Object -First 1) }
    }
}
if ($constant.Count -gt 0) {
    Write-Host ""
    Write-Host "  *** CONSTANT-OFFSET FIELDS - almost certainly a tracer representation bug ***" -ForegroundColor Yellow
    foreach ($c in $constant) {
        if ($c.Kind -eq 'xor') {
            Write-Host ("    {0}: differs on all {1} frames, always by xor 0x{2:x2}" -f $c.Field, $common, $c.Value) -ForegroundColor Yellow
            if ($c.Field -eq 'p' -and $c.Value -eq 0x20) {
                Write-Host "      That is bit 5, the 6502's unused/always-set flag. One side is masking it off." -ForegroundColor Yellow
                Write-Host "      Fix the tracer (force it set on both sides); do not read this as an emulation bug." -ForegroundColor Yellow
            }
        }
        else {
            Write-Host ("    {0}: differs on all {1} frames, always by {2:+#;-#;0}" -f $c.Field, $common, $c.Value) -ForegroundColor Yellow
        }
    }
}

Write-Host ""
Write-Host "=== records around the first divergence ===" -ForegroundColor Cyan
$from = [Math]::Max(0, $firstDiv - $Context)
$to = [Math]::Min($common - 1, $firstDiv + $Follow)
for ($f = $from; $f -le $to; $f++) {
    $ra = $ta.Records[$f]
    $rb = $tb.Records[$f]
    $d = Compare-Record $ra $rb
    $mark = if ($f -eq $firstDiv) { '>>' } elseif ($d.Count -gt 0) { ' !' } else { '  ' }
    $color = if ($d.Count -gt 0) { 'Red' } else { 'DarkGray' }
    Write-Host ("{0} {1} {2}" -f $mark, $padA, (Format-Record $ra)) -ForegroundColor $color
    Write-Host ("{0} {1} {2}" -f $mark, $padB, (Format-Record $rb)) -ForegroundColor $color
    if ($d.Count -gt 0) {
        Write-Host ("   {0} differs: {1}" -f (' ' * $w), ($d -join ', ')) -ForegroundColor Yellow
    }
    Write-Host ""
}

# ---------------------------------------------------------------- harness-artifact probe

if ($shiftReport.Count -gt 0) {
    Write-Host "=== frame-alignment probe (harness-artifact check) ===" -ForegroundColor Cyan
    Write-Host ("  Tests whether {0} frame f equals {1} frame f+shift. A non-zero shift scoring far" -f $padB, $padA)
    Write-Host  "  better than shift 0 means the two harnesses disagree about where a frame ends or"
    Write-Host  "  when input is applied - a harness bug, not an emulation bug."
    foreach ($r in ($shiftReport | Sort-Object -Property Shift)) {
        $tag = if ($r.Shift -eq 0) { ' (aligned)' } else { '' }
        $hl = if ($r.Shift -ne 0 -and $r.Pct -gt 50) { 'Yellow' } else { 'Gray' }
        $sgn = if ($r.Shift -gt 0) { "+$($r.Shift)" } else { "$($r.Shift)" }
        Write-Host ("    shift {0,3} : {1,7} / {2,-7} matched ({3,6}%) first mismatch at frame {4}{5}" -f `
                $sgn, $r.Matched, $r.Total, $r.Pct, $r.FirstBad, $tag) -ForegroundColor $hl
    }
    $best = $shiftReport | Sort-Object -Property @{Expression = 'Matched'; Descending = $true } | Select-Object -First 1
    if ($best.Shift -ne 0 -and $best.Matched -gt 0) {
        Write-Host ("  >> best shift is {0}, not 0. Suspect a frame-boundary off-by-one in one tracer." -f $best.Shift) -ForegroundColor Yellow
    }
    else {
        Write-Host "  >> shift 0 is the best alignment; the divergence is not a whole-frame offset." -ForegroundColor Gray
    }
    Write-Host ""
}

# ---------------------------------------------------------------- byte-level narrowing

if ($RamDumpA -and $RamDumpB) {
    Write-Host "=== byte-level RAM narrowing ===" -ForegroundColor Cyan
    $names = Read-RamMap $RamMap
    if ($RamMap) {
        Write-Host ("  symbol names from {0} ({1} addresses covered)" -f $RamMap, $names.Count) -ForegroundColor DarkGray
        Write-Host "  CAUTION: a RAM map is measured against one specific build. Verify any symbol you" -ForegroundColor DarkGray
        Write-Host "  are about to act on against the ROM actually under test before believing it." -ForegroundColor DarkGray
    }
    $any = $false
    for ($f = [Math]::Max(0, $firstDiv - 1); $f -le $to; $f++) {
        $da = Get-RamDump $RamDumpA $f
        $db = Get-RamDump $RamDumpB $f
        if ($null -eq $da -or $null -eq $db) { continue }
        $any = $true
        $diffs = @()
        for ($i = 0; $i -lt [Math]::Min($da.Length, $db.Length); $i++) {
            if ($da[$i] -ne $db[$i]) { $diffs += $i }
        }
        $color = if ($diffs.Count -eq 0) { 'Green' } elseif ($diffs.Count -le 16) { 'Yellow' } else { 'Red' }
        Write-Host ("  frame {0}: {1} of {2} byte(s) differ" -f $f, $diffs.Count, $da.Length) -ForegroundColor $color
        foreach ($i in ($diffs | Select-Object -First 40)) {
            $nm = if ($names.ContainsKey($i)) { $names[$i] } else { "(unnamed - " + (Get-RamRegion $i) + ")" }
            Write-Host ("      `${0:x4}  {1} = {2:x2}   {3} = {4:x2}   {5}" -f `
                    $i, $padA, $da[$i], $padB, $db[$i], $nm)
        }
        if ($diffs.Count -gt 40) { Write-Host ("      ... and {0} more" -f ($diffs.Count - 40)) }
    }
    if (-not $any) {
        Write-Host "  no ram_f<N>.bin dumps found for any frame in the reported window." -ForegroundColor Yellow
        Write-Host "  Re-run both tracers with dumps at the frames named above:" -ForegroundColor Yellow
        Write-Host ("    BrokenNes: --trace ... --ram-dump-at {0},{1} --ram-dump-dir <dir>" -f ($firstDiv - 1), $firstDiv)
        Write-Host ("    Mesen:     VRUN_TRACE_RAMDUMP={0},{1} VRUN_TRACE_RAMDUMP_DIR=<dir>" -f ($firstDiv - 1), $firstDiv)
    }
    else {
        Write-Host "  A handful of differing bytes confined to the stack page, unallocated zero page, or" -ForegroundColor DarkGray
        Write-Host "  a counter that is off by one is the signature of the two emulators being SAMPLED a" -ForegroundColor DarkGray
        Write-Host "  few CPU cycles apart, not of the game state having forked. A fork shows tens to" -ForegroundColor DarkGray
        Write-Host "  hundreds of differing bytes and grows frame over frame." -ForegroundColor DarkGray
    }
    Write-Host ""
}
else {
    Write-Host "Next steps: dump full RAM from both sides at frames $($firstDiv - 1) and $firstDiv and diff byte-by-byte," -ForegroundColor Cyan
    Write-Host "then re-run this with -RamDumpA/-RamDumpB/-RamMap to name the differing addresses." -ForegroundColor Cyan
    Write-Host ("  BrokenNes: --trace ... --ram-dump-at {0},{1} --ram-dump-dir <dir>" -f ($firstDiv - 1), $firstDiv) -ForegroundColor Cyan
    Write-Host ("  Mesen:     VRUN_TRACE_RAMDUMP={0},{1} VRUN_TRACE_RAMDUMP_DIR=<dir>" -f ($firstDiv - 1), $firstDiv) -ForegroundColor Cyan
}

exit 1
