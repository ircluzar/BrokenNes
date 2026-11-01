# Run state_trace.lua for arbitrarily many frames by cutting the run into segments.
#
# WHY THIS EXISTS. Mesen 2.1.1's --testRunner kills the process at 100 SECONDS of wall
# clock, unconditionally. Measured five ways on game.nes, every one dead at exactly 100s:
#   frame counter only, nothing else          -> reached frame 35750
#   the same, 20000 frames requested          -> COMPLETED in 68s
#   getScreenBuffer + palette-index hash      -> died at 19250, and 19000 on a rerun
#   the above + CIRAM/OAM/palette/CHR hashes  -> died at 15750, and 16000 on a rerun
#   state_trace.lua with its full column set  -> died at ~8320
# The death FRAME moves run to run and falls as per-frame work rises; the death TIME never
# moves. It is a wall clock, not a frame cap, and not the whole-range exec memory callback
# an older comment in state_trace.lua blamed - a callback-only control completed 20000
# frames clean. Debug.ScriptWindow.ScriptTimeout is not the knob either: raised from 600 to
# 100000, the wall stayed at exactly 100s.
#
# So each segment runs well inside 100s, hands the next one a savestate, and this script
# concatenates the pieces. Segments overlap by ONE frame, which the stitcher drops from the
# resumed side: Mesen's savestate does not carry the output screen buffer, so the first
# getScreenBuffer() after a resume returns a frame the restored PPU never fully drew, and
# fbhash - only fbhash, only on that one frame - would otherwise be wrong. With the overlap,
# the stitched trace is byte-identical to an unsegmented one; verified on a 150+150 split
# against a straight 300, all 51 columns, zero differing lines.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Rom,
    [Parameter(Mandatory)][string]$Out,           # final stitched trace
    [int]$Frames = 20000,
    [int]$SegmentFrames = 6000,                   # keep each segment comfortably under 100s
    # NOT named -Input: $Input is a PowerShell automatic variable (the pipeline enumerator),
    # and binding a parameter over it makes the call silently misbehave rather than error.
    [string]$InputScript = '',                    # VRUN_TRACE_INPUT, absolute frame numbers
    [string]$Poke = '',                           # VRUN_TRACE_POKE, absolute frame numbers
    [string]$Prg = 'full',
    [string]$RamFill = 'fceux',
    [string]$Mesen  = "$PSScriptRoot\mesen\Mesen.exe",
    [string]$Script = "$PSScriptRoot\state_trace.lua",
    [string]$WorkDir = ''
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Mesen))  { throw "Mesen not found: $Mesen" }
if (-not (Test-Path $Script)) { throw "state_trace.lua not found: $Script" }
if (-not (Test-Path $Rom))    { throw "ROM not found: $Rom" }

if ([string]::IsNullOrWhiteSpace($WorkDir)) {
    $WorkDir = Join-Path ([IO.Path]::GetTempPath()) ("vrunseg_" + [Guid]::NewGuid().ToString('N').Substring(0,8))
}
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
Write-Host "work dir: $WorkDir"

# Plan the OUTPUT frame ranges first, then derive each segment's run parameters from them.
# Doing it the other way round is what makes the fencepost easy to get wrong: segment i's
# savestate is taken AT its last output frame E_i, so segment i+1 resumes ON E_i and must be
# told FRAME_BASE = E_i - one lower than the first frame it actually contributes - emitting
# E_i again as the warm-up the stitcher then drops.
$ranges = @()
for ($s = 0; $s -lt $Frames; $s += $SegmentFrames) {
    $e = [Math]::Min($s + $SegmentFrames, $Frames) - 1
    $ranges += ,@($s, $e)
}
Write-Host ("segments : {0}, output {1} frames, <= {2} per segment" -f $ranges.Count, $Frames, $SegmentFrames)

$partFiles = @()
$prevState = $null
$swAll = [Diagnostics.Stopwatch]::StartNew()

for ($i = 0; $i -lt $ranges.Count; $i++) {
    $s = $ranges[$i][0]; $e = $ranges[$i][1]
    $isLast = ($i -eq $ranges.Count - 1)

    # Segment 0 starts cold at frame 0. Every later segment resumes ON the previous
    # segment's last output frame and re-emits it as a warm-up, so its base is one lower
    # than the first frame it contributes and it runs one frame longer.
    $base  = if ($i -eq 0) { $s } else { $s - 1 }
    $count = $e - $base + 1

    # Snapshot at this segment's LAST output frame, which is where the next one resumes.
    $outState = if ($isLast) { $null } else { Join-Path $WorkDir ("seg{0}.mss" -f $i) }
    $outFrame = if ($isLast) { '' } else { [string]$e }

    $part = Join-Path $WorkDir ("part{0}.txt" -f $i)
    $partFiles += $part

    $env:VRUN_TRACE_ROM             = $Rom
    $env:VRUN_TRACE_OUT             = $part
    $env:VRUN_TRACE_FRAMES          = [string]$count
    $env:VRUN_TRACE_FRAME_BASE      = [string]$base
    $env:VRUN_TRACE_INPUT           = $InputScript
    $env:VRUN_TRACE_POKE            = $Poke
    $env:VRUN_TRACE_RAMFILL         = $RamFill
    $env:VRUN_TRACE_PPU             = 'on'
    $env:VRUN_TRACE_PPU_FB          = 'on'
    $env:VRUN_TRACE_PRG             = $Prg
    $env:VRUN_TRACE_SEG_IN          = if ($prevState) { $prevState } else { '' }
    $env:VRUN_TRACE_SEG_OUT         = if ($outState)  { $outState }  else { '' }
    $env:VRUN_TRACE_SEG_OUT_FRAME   = $outFrame

    $sw = [Diagnostics.Stopwatch]::StartNew()
    # Start-Process rather than the call operator: Mesen is a GUI-subsystem binary, so
    # $LASTEXITCODE is not reliably populated by `&`, and this run's whole error handling
    # depends on seeing a non-zero exit (that is how the 100s wall announces itself).
    $log = Join-Path $WorkDir ("seg{0}.log" -f $i)
    $proc = Start-Process -FilePath $Mesen -ArgumentList @('--testRunner', $Rom, $Script) `
                          -NoNewWindow -Wait -PassThru `
                          -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    $rc = $proc.ExitCode
    $sw.Stop()

    $recs = 0
    if (Test-Path $part) { $recs = @(Select-String -Path $part -Pattern '^\d' -Raw).Count }
    Write-Host ("  seg {0,2}: emits {1,7}..{2,-7} (base {3}) exit={4} records={5} {6:n1}s" -f `
        $i, $s, $e, $base, $rc, $recs, $sw.Elapsed.TotalSeconds)

    # 100s is the wall. A segment that took ~100s was almost certainly truncated, so say so
    # rather than letting a short part sail into the stitched output as if it were complete.
    if ($sw.Elapsed.TotalSeconds -ge 95) {
        throw ("segment $i ran $([int]$sw.Elapsed.TotalSeconds)s - at or near Mesen's 100s testRunner wall. " +
               "Lower -SegmentFrames (currently $SegmentFrames).")
    }
    if ($rc -ne 0) { throw "segment $i exited $rc; see $part" }
    if ($recs -ne $count) { throw "segment $i emitted $recs records, expected $count; see $part" }
    if ($outState -and -not (Test-Path $outState)) { throw "segment $i wrote no savestate" }

    $prevState = $outState
}

# Stitch: the first part whole (header included), then each later part's records with its
# first record dropped - that frame is the warm-up duplicate of the previous part's last.
$sw = [Diagnostics.Stopwatch]::StartNew()
$writer = [IO.StreamWriter]::new($Out, $false, [Text.UTF8Encoding]::new($false))
$writer.NewLine = "`n"
try {
    $total = 0
    for ($i = 0; $i -lt $partFiles.Count; $i++) {
        $first = $true
        foreach ($line in [IO.File]::ReadLines($partFiles[$i])) {
            if ($line.StartsWith('#')) {
                # Keep segment 0's header verbatim (it carries the determinism block the
                # differ cross-checks); reduce later segments to a one-line marker so the
                # provenance of every piece is still on the record.
                if ($i -eq 0) { $writer.WriteLine($line) }
                continue
            }
            if ($line.Length -eq 0) { continue }
            if ($i -gt 0 -and $first) { $first = $false; continue }   # drop warm-up frame
            $first = $false
            $writer.WriteLine($line)
            $total++
        }
        if ($i -gt 0) { $writer.WriteLine("# stitched-segment: $i from $($partFiles[$i]) (warm-up frame dropped)") }
    }
    $writer.WriteLine("# stitched: $total records from $($partFiles.Count) segment(s)")
} finally { $writer.Dispose() }
$sw.Stop()

$final = @(Select-String -Path $Out -Pattern '^\d' -Raw).Count
Write-Host ("stitched : {0} records -> {1} ({2:n1}s total)" -f $final, $Out, $swAll.Elapsed.TotalSeconds)
if ($final -ne $Frames) { throw "stitched $final records, expected $Frames" }
