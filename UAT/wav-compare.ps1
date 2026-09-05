<#
.SYNOPSIS
  Compare two 32-bit-float mono WAVs sample by sample.

.DESCRIPTION
  Built for the pair of captures Workshop --apu-audio --wav produces before and after an APU
  change, so "did this change the sound, and by how much" stops being a matter of opinion.

  WHAT THE NUMBERS MEAN
    identical samples   how many line up exactly. Two runs of the SAME build must be 100%;
                        anything less means the capture is not deterministic and every other
                        number here is worthless.
    max |difference|    the largest single-sample excursion, in the mixer's own units (the
                        NES mixer's full scale is about 1.0).
    RMS of difference   overall energy of the change.
    correlation         Pearson r between the two signals. Near 1.0 means the same music
                        with a small perturbation; a drop means a structurally different
                        waveform.
    divergent windows   contiguous 1024-sample blocks (about 23ms at 44.1kHz) whose RMS
                        difference exceeds the threshold, with their times - so a change can
                        be located in the run rather than just totalled.

  This compares two BrokenNes captures with each other. It is NOT a cross-emulator test:
  Mesen 2.1.1 exposes no audio to its headless scripting API (verified by enumerating the
  whole emu table - there is no sample getter, no audio event and no WAV binding), so no
  reference WAV exists to diff against. See tools/api_dump.lua for that enumeration.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $A,
    [Parameter(Mandatory)] [string] $B,
    [double] $WindowRmsThreshold = 0.002,
    [int] $WindowSize = 1024,
    [int] $ShowWindows = 12
)

function Read-FloatWav {
    param([string] $Path)
    if (-not (Test-Path $Path)) { throw "wav not found: $Path" }
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 44) { throw "too short to be a WAV: $Path" }
    if ([System.Text.Encoding]::ASCII.GetString($bytes, 0, 4) -ne 'RIFF' -or
        [System.Text.Encoding]::ASCII.GetString($bytes, 8, 4) -ne 'WAVE') { throw "not a RIFF/WAVE file: $Path" }

    # Walk the chunk list rather than assuming the canonical 44-byte layout - a writer that
    # emits a LIST or fact chunk would otherwise shift every sample and silently produce
    # garbage comparisons.
    $pos = 12; $fmtTag = 0; $rate = 0; $channels = 0; $bits = 0; $dataOff = -1; $dataLen = 0
    while ($pos + 8 -le $bytes.Length) {
        $id = [System.Text.Encoding]::ASCII.GetString($bytes, $pos, 4)
        $sz = [BitConverter]::ToInt32($bytes, $pos + 4)
        $body = $pos + 8
        if ($id -eq 'fmt ') {
            $fmtTag   = [BitConverter]::ToUInt16($bytes, $body)
            $channels = [BitConverter]::ToUInt16($bytes, $body + 2)
            $rate     = [BitConverter]::ToInt32($bytes, $body + 4)
            $bits     = [BitConverter]::ToUInt16($bytes, $body + 14)
        } elseif ($id -eq 'data') { $dataOff = $body; $dataLen = $sz }
        $pos = $body + $sz + ($sz -band 1)   # chunks are word-aligned
    }
    if ($dataOff -lt 0) { throw "no data chunk in $Path" }
    if ($fmtTag -ne 3 -or $bits -ne 32) { throw "expected 32-bit IEEE float (tag 3), got tag $fmtTag / $bits-bit in $Path" }
    if ($channels -ne 1) { throw "expected mono, got $channels channels in $Path" }

    $n = [int]($dataLen / 4)
    $s = New-Object 'single[]' $n
    [System.Buffer]::BlockCopy($bytes, $dataOff, $s, 0, $n * 4)
    [pscustomobject]@{ Samples = $s; Rate = $rate; Count = $n }
}

$wa = Read-FloatWav $A
$wb = Read-FloatWav $B
if ($wa.Rate -ne $wb.Rate) { Write-Warning "sample rates differ: $($wa.Rate) vs $($wb.Rate)" }

$n = [Math]::Min($wa.Count, $wb.Count)
if ($n -eq 0) { Write-Error 'no samples'; exit 2 }

$sa = $wa.Samples; $sb = $wb.Samples
$same = 0; $maxDiff = 0.0; $sumSqDiff = 0.0
$sumA = 0.0; $sumB = 0.0; $sumAA = 0.0; $sumBB = 0.0; $sumAB = 0.0
for ($i = 0; $i -lt $n; $i++) {
    $x = [double]$sa[$i]; $y = [double]$sb[$i]
    if ($sa[$i] -eq $sb[$i]) { $same++ }
    $d = [Math]::Abs($x - $y)
    if ($d -gt $maxDiff) { $maxDiff = $d }
    $sumSqDiff += $d * $d
    $sumA += $x; $sumB += $y; $sumAA += $x * $x; $sumBB += $y * $y; $sumAB += $x * $y
}
$rmsDiff = [Math]::Sqrt($sumSqDiff / $n)
$rmsA = [Math]::Sqrt($sumAA / $n)
$rmsB = [Math]::Sqrt($sumBB / $n)
# The 0.0 literals are load-bearing: [Math]::Max(0, <double>) binds the INT overload in
# PowerShell and truncates the variance to zero, which silently reported the correlation as
# NaN for every comparison until it was traced back to here.
$meanA = $sumA / $n
$meanB = $sumB / $n
$covar = ($sumAB / $n) - $meanA * $meanB
$varA = [Math]::Max(0.0, ($sumAA / $n) - $meanA * $meanA)
$varB = [Math]::Max(0.0, ($sumBB / $n) - $meanB * $meanB)
$sdA = [Math]::Sqrt($varA)
$sdB = [Math]::Sqrt($varB)
$corr = if ($sdA -gt 0.0 -and $sdB -gt 0.0) { $covar / ($sdA * $sdB) } else { [double]::NaN }

Write-Host ''
Write-Host 'WAV comparison' -ForegroundColor Cyan
Write-Host "  A: $A"
Write-Host "  B: $B"
Write-Host ('  {0} samples ({1:F2}s @ {2}Hz)' -f $n, ($n / $wa.Rate), $wa.Rate)
Write-Host ''
Write-Host ('  identical samples : {0} / {1}  ({2:P4})' -f $same, $n, ($same / $n))
Write-Host ('  max |difference|  : {0:F6}' -f $maxDiff)
Write-Host ('  RMS of difference : {0:F6}' -f $rmsDiff)
Write-Host ('  RMS of A / of B   : {0:F6} / {1:F6}' -f $rmsA, $rmsB)
Write-Host ('  correlation       : {0:F8}' -f $corr)

$windows = New-Object System.Collections.Generic.List[object]
for ($w = 0; $w + $WindowSize -le $n; $w += $WindowSize) {
    $acc = 0.0
    for ($i = $w; $i -lt $w + $WindowSize; $i++) { $d = [double]$sa[$i] - [double]$sb[$i]; $acc += $d * $d }
    $r = [Math]::Sqrt($acc / $WindowSize)
    if ($r -gt $WindowRmsThreshold) {
        $windows.Add([pscustomobject]@{ Start = $w; Seconds = $w / $wa.Rate; Rms = $r })
    }
}
$totalWindows = [int]($n / $WindowSize)
Write-Host ''
Write-Host ('  divergent windows : {0} / {1} blocks of {2} samples over RMS {3}' -f `
    $windows.Count, $totalWindows, $WindowSize, $WindowRmsThreshold)
if ($windows.Count -gt 0) {
    foreach ($x in ($windows | Sort-Object -Property Rms -Descending | Select-Object -First $ShowWindows)) {
        Write-Host ('      t={0,8:F3}s  frame~{1,-6} rmsDiff={2:F6}' -f `
            $x.Seconds, [int]($x.Seconds * 60.0988), $x.Rms) -ForegroundColor DarkYellow
    }
}
Write-Host ''
