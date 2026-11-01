<#
.SYNOPSIS
  Compare two APU waveform-phase traces (Mesen vs BrokenNes) and classify each field.

.DESCRIPTION
  Consumes the paired traces produced by
      VRUN tools/apu_phase_trace.lua                 (Mesen side)
      Workshop --apu-audio --phase <file>            (BrokenNes side)

  WHY THIS IS NOT A PLAIN EQUALITY DIFF - read this before quoting a number from it.

  Mesen runs its APU lazily: emu.getState() reports the channels from wherever NesApu::Run()
  last caught them up, which at a frame boundary is a bounded but variable distance behind
  the CPU (measured on this project: mean ~1582, max ~2505 CPU cycles). Every field in these
  traces is therefore sampled at a slightly different instant on the two sides, and testing
  them for equality would report a divergence on nearly every frame for a HARNESS reason.

  That argument bounds how far apart two CORRECT emulators can look. It says nothing about a
  defect whose signature is unbounded. A channel sequencer or an LFSR that one emulator
  stops clocking while the channel is muted does not drift by a lag-sized amount - it
  desynchronises permanently. So this script does not ask "are these equal"; it asks three
  questions whose answers a lag cannot fake:

    FROZEN-SIDE   How often does a field hold EXACTLY the same value across a whole frame?
                  A running counter cannot do that: the slowest NES channel divider
                  (noise period 4068 CPU cycles) still ticks 7+ times in a 29780-cycle
                  frame, and a 15-bit LFSR needs 32767 shifts to return to a value. So a
                  frame-to-frame repeat means the field STOPPED. Counting repeats on each
                  side and comparing the counts is immune to any sampling lag.

    RANGE         Does each side visit the same set of values at all? A field pinned to its
                  power-on value on one side and churning on the other is a defect however
                  the two runs are aligned.

    SKEW-ADJUSTED For the lag-immune columns (the period registers, which only change on a
                  register write), how many frames differ, and how many of those are
                  explained by a +/-1 frame sampling offset.

  Only a disagreement that survives all three is reported as a finding.

.PARAMETER Mesen      Path to the Mesen-side phase trace.
.PARAMETER BrokenNes  Path to the BrokenNes-side phase trace.
.PARAMETER Detail     Also print the first few offending frames per field.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Mesen,
    [Parameter(Mandatory)] [string] $BrokenNes,
    [switch] $Detail
)

function Read-PhaseTrace {
    param([string] $Path)
    if (-not (Test-Path $Path)) { throw "phase trace not found: $Path" }
    $cols = $null
    $rows = New-Object System.Collections.Generic.List[string[]]
    foreach ($line in [System.IO.File]::ReadLines($Path)) {
        if ($line.StartsWith('#')) {
            # The column list is read from the file, never assumed, so the two sides can
            # gain a column independently without this script silently mis-aligning them.
            if ($line -match '^#\s*columns:\s*(.+)$') { $cols = $Matches[1].Trim() -split '\|' }
            continue
        }
        if ($line.Length -eq 0) { continue }
        $rows.Add(($line -split '\|'))
    }
    if (-not $cols) { throw "no '# columns:' header in $Path" }
    [pscustomobject]@{ Columns = $cols; Rows = $rows }
}

$m = Read-PhaseTrace $Mesen
$b = Read-PhaseTrace $BrokenNes

if (($m.Columns -join '|') -ne ($b.Columns -join '|')) {
    Write-Error "column lists differ - the two traces are not comparable.`n  mesen: $($m.Columns -join '|')`n  bn   : $($b.Columns -join '|')"
    exit 2
}
$n = [Math]::Min($m.Rows.Count, $b.Rows.Count)
if ($n -eq 0) { Write-Error 'no records'; exit 2 }

Write-Host ''
Write-Host "APU phase comparison over $n frames" -ForegroundColor Cyan
Write-Host "  mesen     : $Mesen"
Write-Host "  brokennes : $BrokenNes"
Write-Host ''

# Columns that genuinely cannot be compared for equality, and are only checked for
# freeze/range behaviour. cpuCycle/apuPrevCycle are emitted as '-' by the BrokenNes side.
# fcprev is the Mesen-only APU catch-up cycle; the BrokenNes side emits '-' because its APU
# is never behind its CPU, so there is no lag to report. Comparing it is comparing a number
# against a dash.
$skip = @('frame', 'cpuCycle', 'apuPrevCycle', 'fcprev')

# Columns that only change on a register write or a frame-sequencer tick, so a +/-1 frame
# sampling offset explains any disagreement and nothing else should.
$lagImmune = @('p1duty', 'p1period', 'p2duty', 'p2period', 'tperiod', 'nperiod', 'nmode')

$findings = New-Object System.Collections.Generic.List[string]

Write-Host ('{0,-12} {1,12} {2,12}   {3}' -f 'field', 'mesenMaxRun', 'bnMaxRun', 'verdict')
Write-Host ('-' * 74)

for ($c = 0; $c -lt $m.Columns.Count; $c++) {
    $name = $m.Columns[$c]
    if ($skip -contains $name) { continue }

    # THE DISCRIMINATOR, and the reason this script is worth more than a frozen-frame count.
    #
    # Counting how often a field equals its previous frame does NOT separate a stalled
    # counter from a live one, because a live divider can land on the same value at two
    # consecutive frame boundaries by arithmetic coincidence - e.g. a noise period of 4
    # divides 29780 exactly, so its counter is at the same phase every frame. Measured here:
    # after the divider fix, BrokenNes' noise timer still "froze" on 402 frames against
    # Mesen's 9, purely because BrokenNes samples at a deterministic instant and Mesen's
    # lazily-updated copy jitters.
    #
    # Two statistics tell the two apart, and neither can be faked by a sampling lag:
    #   longest RUN of consecutive equal frames - coincidence gives runs of 1-2, a genuinely
    #     unclocked field gives one enormous run;
    #   number of DISTINCT values - a field that is never clocked has exactly one.
    # A finding needs a long run AND a collapsed value set.
    $mVals = @{}; $bVals = @{}
    $mFrozen = 0; $bFrozen = 0
    $mRun = 0; $bRun = 0; $mMaxRun = 0; $bMaxRun = 0
    for ($i = 0; $i -lt $n; $i++) {
        $mv = $m.Rows[$i][$c]; $bv = $b.Rows[$i][$c]
        $mVals[$mv] = $true; $bVals[$bv] = $true
        if ($i -gt 0) {
            if ($mv -eq $m.Rows[$i - 1][$c]) { $mFrozen++; $mRun++; if ($mRun -gt $mMaxRun) { $mMaxRun = $mRun } } else { $mRun = 0 }
            if ($bv -eq $b.Rows[$i - 1][$c]) { $bFrozen++; $bRun++; if ($bRun -gt $bMaxRun) { $bMaxRun = $bRun } } else { $bRun = 0 }
        }
    }

    $verdict = 'ok'
    # "Stalled" = held for a long unbroken stretch while the other side kept moving, and
    # collapsed to a handful of values. 5% of the run is far longer than any coincidence.
    $runFloor = [Math]::Max(20, 0.05 * $n)
    if ($bMaxRun -gt $runFloor -and $mMaxRun -lt $runFloor -and $bVals.Count -le 2 -and $mVals.Count -gt 2) {
        $verdict = "STALLED on BrokenNes (run $bMaxRun, $($bVals.Count) value(s) vs Mesen's $($mVals.Count))"
        $findings.Add("$name : BrokenNes holds this field for $bMaxRun consecutive frames and only ever takes $($bVals.Count) value(s), while Mesen keeps it moving over $($mVals.Count) - it is not being clocked.")
    }
    elseif ($mMaxRun -gt $runFloor -and $bMaxRun -lt $runFloor -and $mVals.Count -le 2 -and $bVals.Count -gt 2) {
        $verdict = "STALLED on Mesen (run $mMaxRun, $($mVals.Count) value(s) vs BrokenNes' $($bVals.Count))"
        $findings.Add("$name : Mesen holds this field for $mMaxRun consecutive frames while BrokenNes keeps it moving.")
    }
    else {
        $verdict = "ok (runs m=$mMaxRun b=$bMaxRun, distinct m=$($mVals.Count) b=$($bVals.Count))"
    }

    if ($lagImmune -contains $name) {
        $diff = 0; $skewed = 0
        for ($i = 1; $i -lt $n - 1; $i++) {
            if ($m.Rows[$i][$c] -ne $b.Rows[$i][$c]) {
                $diff++
                if ($m.Rows[$i][$c] -eq $b.Rows[$i - 1][$c] -or $m.Rows[$i][$c] -eq $b.Rows[$i + 1][$c]) { $skewed++ }
            }
        }
        $unexplained = $diff - $skewed
        $verdict += "  |  lag-immune: $diff differ, $skewed are +/-1 frame skew, $unexplained unexplained"
        if ($unexplained -gt 0) {
            $findings.Add("$name : $unexplained frames differ by more than a one-frame sampling offset, on a field that only changes when the ROM writes it.")
        }
    }

    $colour = if ($verdict -like '*STALLED*' -or $verdict -match 'unexplained ([1-9])') { 'Yellow' } else { 'DarkGray' }
    Write-Host ('{0,-12} {1,12} {2,12}   {3}' -f $name, $mMaxRun, $bMaxRun, $verdict) -ForegroundColor $colour

    if ($Detail -and $verdict -notlike 'ok*') {
        $shown = 0
        for ($i = 1; $i -lt $n -and $shown -lt 6; $i++) {
            if ($b.Rows[$i][$c] -eq $b.Rows[$i - 1][$c] -and $m.Rows[$i][$c] -ne $m.Rows[$i - 1][$c]) {
                Write-Host ("      f{0}: mesen {1} -> {2}   brokennes {3} (held)" -f `
                    $m.Rows[$i][0], $m.Rows[$i - 1][$c], $m.Rows[$i][$c], $b.Rows[$i][$c]) -ForegroundColor DarkYellow
                $shown++
            }
        }
    }
}

Write-Host ''
if ($findings.Count -eq 0) {
    Write-Host 'No divergence found that a sampling lag cannot explain.' -ForegroundColor Green
} else {
    Write-Host "$($findings.Count) finding(s):" -ForegroundColor Yellow
    foreach ($f in $findings) { Write-Host "  - $f" -ForegroundColor Yellow }
}
Write-Host ''
