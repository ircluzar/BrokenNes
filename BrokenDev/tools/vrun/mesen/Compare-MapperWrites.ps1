# Compare a BrokenNes --mapper-write-log against a Mesen mapper_write_probe.lua log,
# WRITE FOR WRITE.
#
# WHY THIS AND NOT THE TRACE'S chrbank COLUMN. The per-frame trace samples the mapper's CHR bank
# once, at the frame boundary. That sample answers "which bank was selected when the frame ended",
# which is not the question - the PPU fetches pattern data all the way down the screen, so a bank
# that is selected at scanline 19 and replaced at scanline 200 is load-bearing for 180 scanlines of
# picture and completely invisible at the boundary. This compares the WRITES: address, byte, the
# bank each one produced, the mirroring it left behind, and the scanline/dot it landed on.
#
# WHAT COUNTS AS A DIVERGENCE, AND WHAT DOES NOT.
#   * sequence (addr, value, in order)  - a real divergence. The two CPUs disagreed about what to
#     write or when to write it.
#   * prgbank / chrbank / mir after the write - a real divergence. The two MAPPERS disagreed about
#     what the same byte means.
#   * frame - real, but read it together with the sequence: one shifted write shifts every frame
#     number after it, so only the FIRST frame mismatch is informative.
#   * scanline/dot - NOT a divergence on its own. BrokenNes' IPpuProbe reports the PPU's catch-up
#     position, which is exact only to an instruction boundary, while Mesen's ppu.cycle is
#     cycle-exact. A small bounded offset is a harness artefact. A GROWING one is not, which is why
#     the delta is reported as a distribution rather than a pass/fail.
#
# HOW TO ACTUALLY EXERCISE CHR BANKING ON game.nes. Left to itself the game barely banks CHR: it
# selects bank 1 once during boot (frames 8-11) and runs on bank 0 for the whole session, and banks
# 2 and 3 are never used at all. That is not an accident of the input script - main.fab only ever
# emits state(M30_CHR(1)) while `bank_flip_timer` is non-zero, and that timer is set only on a REAL
# boss phase transition (12 frames per transition, the deliberate "CHR bank flip" corruption
# effect). So a comparison run that just walks around measures nothing about CHR banking.
#
# bank_flip_timer lives at work RAM $0382 (found by poking $0380-$0392 one address per 100 frames
# and watching this log for the first chr=1 write). Poking it drives the effect on demand, and both
# tracers can poke identically:
#
#   BrokenNes : --poke-at "600:382=06,612:382=06,624:382=06,..."
#   Mesen     : VRUN_MW_POKE="600:382=06,612:382=06,..."
#
# Poking 6 every 12 frames gives ~6 frames on / ~6 frames off, which over 10,000 frames produced
# 1,569 mid-frame bank changes - 1,546 of them on visible scanlines 30-59, i.e. a quarter of the way
# down the picture, with ~200 scanlines still to draw. THAT is the case a frame-boundary chrbank
# sample cannot see, and the case worth comparing.
#
# Usage:
#   .\Compare-MapperWrites.ps1 -Bn bn_mw.txt -Mesen ms_mw.txt [-Examples 12]
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Bn,
    [Parameter(Mandatory)][string]$Mesen,
    [int]$Examples = 12
)

$ErrorActionPreference = 'Stop'
foreach ($p in @($Bn, $Mesen)) { if (-not (Test-Path $p)) { throw "not found: $p" } }

# BrokenNes: frame|scanline|dot|addr|value|eff|latched|prgbank|chrbank|mir|status
# Mesen:     frame|scanline|dot|addr|value|prgbank|chrbank|mir|flash
function Read-Log {
    param([string]$Path, [int[]]$Idx)   # Idx = frame,scanline,dot,addr,value,prg,chr,mir
    $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($line in [System.IO.File]::ReadLines($Path)) {
        if ($line.Length -eq 0 -or $line[0] -eq '#') { continue }
        $f = $line.Split('|')
        $sl = [int]$f[$Idx[1]]
        # Mesen numbers the pre-render line -1; BrokenNes numbers it 261. Normalise to BrokenNes so
        # a write on that line is not reported as a 262-scanline disagreement.
        if ($sl -lt 0) { $sl = 261 }
        $rows.Add([pscustomobject]@{
            Frame = [int]$f[$Idx[0]]
            Scan  = $sl
            Dot   = [int]$f[$Idx[2]]
            Addr  = $f[$Idx[3]]
            Value = $f[$Idx[4]]
            Prg   = $f[$Idx[5]]
            Chr   = $f[$Idx[6]]
            Mir   = $f[$Idx[7]]
        })
    }
    return $rows
}

Write-Host "reading..."
$a = Read-Log -Path $Bn    -Idx @(0,1,2,3,4,7,8,9)
$b = Read-Log -Path $Mesen -Idx @(0,1,2,3,4,5,6,7)
Write-Host ("brokennes writes : {0}" -f $a.Count)
Write-Host ("mesen writes     : {0}" -f $b.Count)

$n = [Math]::Min($a.Count, $b.Count)
if ($a.Count -ne $b.Count) {
    Write-Host ("COUNT MISMATCH   : {0} (comparing the first {1})" -f ($b.Count - $a.Count), $n) -ForegroundColor Yellow
}

# --- 1. the write sequence itself -----------------------------------------------------------
$seqBad = [System.Collections.Generic.List[int]]::new()
for ($i = 0; $i -lt $n; $i++) {
    if ($a[$i].Addr -ne $b[$i].Addr -or $a[$i].Value -ne $b[$i].Value) { $seqBad.Add($i) }
}
Write-Host ""
Write-Host "--- write sequence (addr, value) ---"
if ($seqBad.Count -eq 0) {
    Write-Host ("IDENTICAL over all {0} writes" -f $n) -ForegroundColor Green
} else {
    Write-Host ("{0} of {1} writes differ; first at index {2}" -f $seqBad.Count, $n, $seqBad[0]) -ForegroundColor Red
    foreach ($i in ($seqBad | Select-Object -First $Examples)) {
        Write-Host ("  #{0}  bn f{1} sl{2} d{3} {4}={5}   mesen f{6} sl{7} d{8} {9}={10}" -f `
            $i, $a[$i].Frame, $a[$i].Scan, $a[$i].Dot, $a[$i].Addr, $a[$i].Value,
            $b[$i].Frame, $b[$i].Scan, $b[$i].Dot, $b[$i].Addr, $b[$i].Value)
    }
}

# --- 2. the mapper's interpretation of each write --------------------------------------------
$bankBad = [System.Collections.Generic.List[int]]::new()
$mirBad  = [System.Collections.Generic.List[int]]::new()
$frameBad = [System.Collections.Generic.List[int]]::new()
for ($i = 0; $i -lt $n; $i++) {
    if ($a[$i].Prg -ne $b[$i].Prg -or $a[$i].Chr -ne $b[$i].Chr) { $bankBad.Add($i) }
    if ($a[$i].Mir -ne $b[$i].Mir) { $mirBad.Add($i) }
    if ($a[$i].Frame -ne $b[$i].Frame) { $frameBad.Add($i) }
}
Write-Host ""
Write-Host "--- resulting banks (prg, chr) ---"
if ($bankBad.Count -eq 0) { Write-Host ("IDENTICAL over all {0} writes" -f $n) -ForegroundColor Green }
else {
    Write-Host ("{0} differ; first at index {1}" -f $bankBad.Count, $bankBad[0]) -ForegroundColor Red
    foreach ($i in ($bankBad | Select-Object -First $Examples)) {
        Write-Host ("  #{0}  f{1} sl{2} {3}={4}  bn prg={5} chr={6}   mesen prg={7} chr={8}" -f `
            $i, $a[$i].Frame, $a[$i].Scan, $a[$i].Addr, $a[$i].Value,
            $a[$i].Prg, $a[$i].Chr, $b[$i].Prg, $b[$i].Chr)
    }
}
Write-Host "--- mirroring ---"
if ($mirBad.Count -eq 0) { Write-Host ("IDENTICAL over all {0} writes" -f $n) -ForegroundColor Green }
else {
    Write-Host ("{0} differ; first at index {1}" -f $mirBad.Count, $mirBad[0]) -ForegroundColor Red
    foreach ($i in ($mirBad | Select-Object -First $Examples)) {
        Write-Host ("  #{0}  f{1} sl{2} {3}={4}  bn={5} mesen={6}" -f `
            $i, $a[$i].Frame, $a[$i].Scan, $a[$i].Addr, $a[$i].Value, $a[$i].Mir, $b[$i].Mir)
    }
}
# --- 3. where in the frame the write landed ---------------------------------------------------
# One number for both the dot difference and the frame-stamp difference, because they are the same
# phenomenon. BrokenNes' RunFrame() returns at the first INSTRUCTION boundary at or after the
# frame's dot budget, and its IPpuProbe dot is likewise only exact to an instruction boundary,
# so a write landing within one instruction of the boundary can be stamped with either frame.
# Position is therefore taken globally (frame * 89342 + scanline * 341 + dot) and the difference
# folded modulo a frame into (-44671, +44671]: a write the two sides put on opposite sides of the
# boundary then reads as the handful of dots it really is, instead of a spurious 89,340.
$FRAME_DOTS = 89342
function Fold([long]$d) {
    $r = $d % $FRAME_DOTS
    if ($r -gt ($FRAME_DOTS / 2)) { $r -= $FRAME_DOTS }
    elseif ($r -lt -($FRAME_DOTS / 2)) { $r += $FRAME_DOTS }
    return [int]$r
}
Write-Host ""
Write-Host "--- write position (mesen - brokennes, dots, folded across the frame boundary) ---"
$hist = @{}
$maxAbs = 0; $maxAt = -1
$deltas = New-Object 'int[]' $n
for ($i = 0; $i -lt $n; $i++) {
    $pa = [long]$a[$i].Frame * $FRAME_DOTS + $a[$i].Scan * 341 + $a[$i].Dot
    $pb = [long]$b[$i].Frame * $FRAME_DOTS + $b[$i].Scan * 341 + $b[$i].Dot
    $d = Fold ($pb - $pa)
    $deltas[$i] = $d
    if ($hist.ContainsKey($d)) { $hist[$d]++ } else { $hist[$d] = 1 }
    $abs = [Math]::Abs($d)
    if ($abs -gt $maxAbs) { $maxAbs = $abs; $maxAt = $i }
}
foreach ($k in ($hist.Keys | Sort-Object)) {
    Write-Host ("  delta {0,5} dots : {1,8}  ({2:P2})" -f $k, $hist[$k], ($hist[$k] / [double]$n))
}
if ($maxAt -ge 0) {
    Write-Host ("worst: #{0} f{1}  bn sl{2} d{3}  mesen f{4} sl{5} d{6}  ({7} dots)" -f `
        $maxAt, $a[$maxAt].Frame, $a[$maxAt].Scan, $a[$maxAt].Dot, $b[$maxAt].Frame, $b[$maxAt].Scan, $b[$maxAt].Dot, $maxAbs)
}
# Drift is the thing that would be a real defect: a bounded offset is the instruction-boundary
# artefact, a growing one is a frame-length disagreement. Reported per decile of the run.
Write-Host "drift check (mean delta per tenth of the run):"
$bucket = [Math]::Max(1, [int][Math]::Ceiling($n / 10.0))
for ($s = 0; $s -lt $n; $s += $bucket) {
    $e = [Math]::Min($s + $bucket, $n)
    $sum = 0.0
    for ($i = $s; $i -lt $e; $i++) { $sum += $deltas[$i] }
    Write-Host ("  writes {0,8}-{1,-8} mean {2,7:N2} dots" -f $s, ($e - 1), ($sum / ($e - $s)))
}

Write-Host ""
Write-Host "--- frame stamp ---"
$offBoundary = 0
if ($frameBad.Count -eq 0) { Write-Host ("IDENTICAL over all {0} writes" -f $n) -ForegroundColor Green }
else {
    # A frame-stamp difference is only meaningful when the two sides put the write at genuinely
    # different POSITIONS. The folded delta computed above already accounts for the frame number,
    # so a write both sides agree on to within a fraction of a scanline is the instruction-boundary
    # artefact wearing a different hat - BrokenNes' RunFrame() returned one instruction early or
    # late relative to Mesen's scanline-0 crossing, and the write fell on the other side. A genuine
    # frame disagreement cannot fold to a small number.
    $firstOff = -1
    foreach ($i in $frameBad) {
        if ([Math]::Abs($deltas[$i]) -gt 64) { $offBoundary++; if ($firstOff -lt 0) { $firstOff = $i } }
    }
    Write-Host ("{0} differ, of which {1} are more than 64 dots apart in absolute position" -f $frameBad.Count, $offBoundary)
    if ($offBoundary -eq 0) {
        Write-Host "  all of them land on the frame boundary: the instruction-boundary artefact, not a divergence" -ForegroundColor Green
    } else {
        Write-Host ("  first off-boundary mismatch at index {0} (bn f{1} sl{2} vs mesen f{3} sl{4})" -f `
            $firstOff, $a[$firstOff].Frame, $a[$firstOff].Scan, $b[$firstOff].Frame, $b[$firstOff].Scan) -ForegroundColor Red
    }
}

# --- 4. what the frame-boundary sample would have missed --------------------------------------
# The claim this whole tool exists to test: how much CHR banking happens INSIDE a frame and is
# undone before the frame ends, and is therefore invisible to the trace's chrbank column.
function Chr-Census {
    param($rows, [string]$label)
    $perBank = @{}
    $switches = 0
    $switchScan = @{}
    $framesWithSwitch = @{}
    $framesHidden = @{}          # frames whose LAST bank differs from some bank used earlier in it
    $curFrame = -1
    $seenThisFrame = $null
    $prevChr = $null
    foreach ($r in $rows) {
        if ($perBank.ContainsKey($r.Chr)) { $perBank[$r.Chr]++ } else { $perBank[$r.Chr] = 1 }
        if ($r.Frame -ne $curFrame) {
            if ($seenThisFrame -ne $null -and $prevChr -ne $null) {
                foreach ($c in $seenThisFrame.Keys) { if ($c -ne $prevChr) { $framesHidden[$curFrame] = $true } }
            }
            $curFrame = $r.Frame
            $seenThisFrame = @{}
        }
        $seenThisFrame[$r.Chr] = $true
        if ($prevChr -ne $null -and $r.Chr -ne $prevChr) {
            $switches++
            $framesWithSwitch[$r.Frame] = $true
            # Bucketed by where on the SCREEN the change lands, because that is what decides how
            # much of the picture is drawn from the new bank. A change at scanline 20 repaints 220
            # scanlines' worth of pattern fetches; one in vblank repaints nothing in this frame.
            $key = if ($r.Scan -le 239) { "visible sl {0,3}-{1,-3}" -f ([Math]::Floor($r.Scan / 30) * 30), ([Math]::Floor($r.Scan / 30) * 30 + 29) }
                   elseif ($r.Scan -le 260) { "vblank" } else { "pre-render" }
            if ($switchScan.ContainsKey($key)) { $switchScan[$key]++ } else { $switchScan[$key] = 1 }
        }
        $prevChr = $r.Chr
    }
    if ($seenThisFrame -ne $null -and $prevChr -ne $null) {
        foreach ($c in $seenThisFrame.Keys) { if ($c -ne $prevChr) { $framesHidden[$curFrame] = $true } }
    }
    Write-Host ""
    Write-Host ("--- CHR bank census ({0}) ---" -f $label)
    foreach ($k in ($perBank.Keys | Sort-Object)) {
        Write-Host ("  bank {0}: {1,8} writes resolved to it ({2:P3})" -f $k, $perBank[$k], ($perBank[$k] / [double]$rows.Count))
    }
    Write-Host ("  bank CHANGES: {0}" -f $switches)
    foreach ($k in ($switchScan.Keys | Sort-Object)) { Write-Host ("    on {0}: {1}" -f $k, $switchScan[$k]) }
    Write-Host ("  frames containing a CHR bank change      : {0}" -f $framesWithSwitch.Count)
    Write-Host ("  frames a FRAME-BOUNDARY sample would MISS: {0}" -f $framesHidden.Count)
    Write-Host ("     (a bank was live during the frame but not the one left selected at its end)")
}
Chr-Census -rows $a -label "brokennes"
Chr-Census -rows $b -label "mesen"

Write-Host ""
# The verdict deliberately EXCLUDES the two known harness artefacts, both of which are properties
# of how the two tracers stop and sample rather than of the emulation:
#   * a tail overshoot of a few writes - the Mesen probe runs one endFrame past the last requested
#     frame so that frame's vblank is seen, and BrokenNes' RunFrame() returns mid-vblank on its very
#     last frame, so the last frame's trailing writes exist on one side only;
#   * boundary-adjacent frame stamps and a bounded dot delta - the instruction-boundary artefact.
# Anything else is a real disagreement between the two mappers or the two CPUs.
$tailOnly = ($seqBad.Count -eq 0) -and ([Math]::Abs($a.Count - $b.Count) -le 8)
$verdict = ($seqBad.Count -eq 0 -and $bankBad.Count -eq 0 -and $mirBad.Count -eq 0 -and $offBoundary -eq 0 -and $tailOnly)
if ($verdict) {
    Write-Host ("VERDICT: mapper write streams AGREE over {0} writes - address, byte, resulting PRG" -f $n) -ForegroundColor Green
    Write-Host "         bank, resulting CHR bank and mirroring all identical, write for write." -ForegroundColor Green
    if ($a.Count -ne $b.Count) {
        Write-Host ("         (differs only by a {0}-write tail on the last frame; see above)" -f [Math]::Abs($a.Count - $b.Count)) -ForegroundColor Green
    }
} else {
    Write-Host "VERDICT: streams DIFFER - see above." -ForegroundColor Red
}
