<#
.SYNOPSIS
  Frame-by-frame comparison of this emulator with Mesen 2.1.1 (the Master System / Game Gear oracle, GPL: run it, never read it) over a LIST of games, with a per-game count of identical frames.

.DESCRIPTION
  For every game of the list: the oracle is run twice headless (its power-on line is random, so a frame where its two runs differ from each other is "the oracle disagrees with itself" and is counted apart),
  its screenshots of frames 2..N are written, and the Workshop's `--sms-run --mesen` compares them with this emulator's frames (the same pad script, the oracle's power-on line replayed). Each game is
  compared twice: with the Zilog Z80 timing the board uses, and with `OracleZ80Timing=1` (the oracle's instruction lengths, a tool-only switch). The result is one TSV row per game.
  Mesen's pictures are the only large thing written; they are deleted after each game unless -Keep.

  The input list is a text file, one game per line:  <rom path or file name> [TAB <pad script>] [TAB <region Auto|Ntsc|Pal>] [TAB <frames>]
  Without a pad script the Workshop's golden scripts are used (tap button 1 / Start, then walk); without a region the file name decides (a "(Europe)" tag is PAL) like the board does.
  Each concurrent job needs its own copy of the oracle (about 85 MB): they live in <Scratch>\mesen-slot<n>.

  Oracle windows: the test runner opens none, so this needs no `oracle-lock` (a run that DOES open Mesen's window must take the `mesen` lock first).

.EXAMPLE
  pwsh -File mesen-compare.ps1 -List games.txt -OutTsv compare.tsv -Scratch D:\scratch\cmp -Jobs 3
#>
param(
    [Parameter(Mandatory)] [string]$List,
    [Parameter(Mandatory)] [string]$OutTsv,
    [string]$Scratch = (Join-Path $env:TEMP "bn-mesen-compare"),
    [string]$Workshop = (Join-Path $env:LOCALAPPDATA "VRUN_Nes_Dev_work\bn_sega_a3\bin\BrokenNes.Workshop\release\BrokenNes.Workshop.exe"),
    [int]$Frames = 899,
    [string]$Spec = "",             # the oracle's screenshots to compare: "2-899" (default: every frame from 2 to -Frames) or a list such as "300,600,900,1800" for a quick screen of many games
    [int]$Runs = 2,                 # oracle runs per game: 2 gives the "oracle disagrees with itself" frames; 1 is enough for a screen
    [switch]$SweepPad,              # use the sweep tool's longer pad pattern (Workshop --sms-sweep --print-pad) instead of the 900-frame golden script
    [int]$Jobs = 3,
    [int]$TimeoutSec = 400,
    [switch]$Keep,
    [string]$Modes = "zilog=;oracle=--tune OracleZ80Timing=1",   # the comparisons made against each game's oracle pictures: "name=extra --sms-run arguments;name=..." (the app's own configuration is "zilog"; "oracle" runs the Z80 with the oracle's instruction lengths; add for example "rhalt=--tune OracleHaltNoRefresh=1" for R not counting while halted, as the oracle's does not; tool settings combine in one --tune: "both=--tune OracleZ80Timing=1,OracleHaltNoRefresh=1")
    [string]$ExtraArgs = "",        # added to every `--sms-run`
    [string]$RomDir = ""            # a list row that is a bare file name is looked up under this folder (the committed menu-scripts.tsv has names, not paths)
)
$ErrorActionPreference = "Stop"
if (-not (Test-Path $Workshop)) { throw "Workshop exe not found: $Workshop" }
New-Item -ItemType Directory -Force $Scratch | Out-Null
$capture = Join-Path $PSScriptRoot "mesen-capture.ps1"
$padSms = "150:A,156:,210:A,216:,270:A,276:,330:A,336:,400:Right,460:Right+A,470:Right,520:Right+B,530:Right,580:Left+A,590:Left,640:Down,700:Up+A,710:Right,760:Right+A,770:Right"
$padGg  = "150:Start,156:,210:A,216:,270:Start,276:,330:A,336:,400:Right,460:Right+A,470:Right,520:Right+B,530:Right,580:Left+A,590:Left,640:Down,700:Up+A,710:Right,760:Right+A,770:Right"

$games = foreach ($line in Get-Content $List) {
    if (-not $line.Trim() -or $line.TrimStart().StartsWith("#")) { continue }
    $f = $line -split "`t"
    $romPath = $f[0].Trim()
    if (-not (Test-Path -LiteralPath $romPath) -and $RomDir) { $hit = Get-ChildItem -LiteralPath $RomDir -Recurse -File -Filter $romPath | Select-Object -First 1; if ($hit) { $romPath = $hit.FullName } else { throw "ROM not found under ${RomDir}: $romPath" } }
    [pscustomobject]@{ Rom = $romPath; Pad = if ($f.Count -gt 1 -and $f[1].Trim()) { $f[1].Trim() } else { "" }; Region = if ($f.Count -gt 2 -and $f[2].Trim()) { $f[2].Trim() } else { "" }; Frames = if ($f.Count -gt 3 -and $f[3].Trim()) { [int]$f[3].Trim() } else { 0 } }
}
"compare: $(@($games).Count) games, $Frames frames, $Jobs jobs"
$slots = [System.Collections.Concurrent.ConcurrentQueue[int]]::new()
1..$Jobs | ForEach-Object { $slots.Enqueue($_) }
$results = [System.Collections.Concurrent.ConcurrentBag[object]]::new()

$games | ForEach-Object -ThrottleLimit $Jobs -Parallel {
    $g = $_
    $slot = 0
    while (-not ($using:slots).TryDequeue([ref]$slot)) { Start-Sleep -Milliseconds 300 }
    try {
        $rom = $g.Rom
        $name = [IO.Path]::GetFileNameWithoutExtension($rom)
        $ext = [IO.Path]::GetExtension($rom).ToLowerInvariant()
        $gg = $ext -eq ".gg"
        $tag = ($name -replace '[^A-Za-z0-9]+', '_')
        if ($tag.Length -gt 60) { $tag = $tag.Substring(0, 60) }
        $gFrames = if ($g.Frames -gt 0) { $g.Frames } else { $using:Frames }      # a list row may give its own length (a menu script that needs more than the default 899 frames)
        $pad = if ($g.Pad) { $g.Pad } elseif ($using:SweepPad) { (& $using:Workshop --sms-sweep --print-pad $(if ($gg) { "gg" } else { "sms" }) --frames $gFrames | Out-String).Trim() } elseif ($gg) { $using:padGg } else { $using:padSms }
        $region = $g.Region
        if (-not $region) {
            $pal = $name -match '[\(\[](?:[^\)\]]*,\s*)?(Europe|Australia|PAL|E)(?:\s*,[^\)\]]*)?[\)\]]'
            $ntsc = $name -match '[\(\[](?:[^\)\]]*,\s*)?(USA|Japan|World|Korea|Brazil|NTSC|U|J|W|JU|UE|JE)(?:\s*,[^\)\]]*)?[\)\]]'
            $region = if ($gg) { "Ntsc" } elseif ($pal -and -not $ntsc) { "Pal" } else { "Ntsc" }
        }
        $dir = Join-Path $using:Scratch "g$($slot)_$tag"
        Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
        $a = Join-Path $dir "a"; $b = Join-Path $dir "b"; $mine = Join-Path $dir "mine"
        $work = Join-Path $using:Scratch "mesen-slot$slot"
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $row = [ordered]@{ game = $name; console = $(if ($gg) { "GG" } else { "SMS" }); region = $region; error = "" }
        $frameList = if ($using:Spec) { $using:Spec } else { "2-$gFrames" }
        $runList = @(@{ Dir = $a; Tag = $tag }, @{ Dir = $b; Tag = $tag })[0..($using:Runs - 1)]
        foreach ($run in $runList) {
            # a child process each: the capture script passes its settings to the oracle through environment variables, which concurrent runs of one process would overwrite in each other
            # the oracle sometimes ends without writing its pictures when several run at once: each run is tried up to three times before it counts as an error
            $cap = $null
            for ($try = 1; $try -le 3; $try++) {
                Remove-Item -Recurse -Force $run.Dir -ErrorAction SilentlyContinue
                $cap = Start-Process -FilePath pwsh -ArgumentList @("-NoProfile", "-File", $using:capture, "-Rom", "`"$rom`"", "-Frames", "`"$frameList`"", "-OutDir", "`"$($run.Dir)`"", "-Tag", $run.Tag, "-Pad", "`"$pad`"", "-Region", $region, "-Overscan", "0", "-Work", "`"$work`"", "-TimeoutSec", $using:TimeoutSec) -PassThru -Wait -WindowStyle Hidden -RedirectStandardOutput "$dir.cap.out" -RedirectStandardError "$dir.cap.err"
                if ($cap.ExitCode -eq 0) { break }
            }
            if ($cap.ExitCode -ne 0) { $row.error = "mesen capture exit $($cap.ExitCode) after 3 tries: " + ((Get-Content "$dir.cap.err" -ErrorAction SilentlyContinue | Select-Object -First 2) -join " "); break }
        }
        $row.mesen_s = [math]::Round($sw.Elapsed.TotalSeconds)
        if (-not $row.error) {
            foreach ($def in ($using:Modes -split ";")) {
                $nm, $ar = $def -split "=", 2
                $mode = @{ Name = $nm.Trim(); Tune = @(($ar -split " ") | Where-Object { $_ }) }
                $args2 = @("--sms-run", $rom, "--frames", $gFrames, "--input", $pad, "--png-at", $frameList, "--out", $mine, "--tag", $tag, "--mesen", $a, "--region", $region, "--diff-detail", $(if ($using:Spec) { "40" } else { "0" })) + $mode.Tune
                if ($using:Runs -gt 1) { $args2 += @("--mesen-b", $b) }
                if ($using:ExtraArgs) { $args2 += ($using:ExtraArgs -split " ") }
                $o = & $using:Workshop @args2 2>&1 | Out-String
                if ($using:Spec) {
                    # a short list of checkpoints: which were identical, and by how many pixels the others differ
                    $per = @()
                    foreach ($m in [regex]::Matches($o, 'frame (\d+): (IDENTICAL to Mesen|(\d+) pixels differ|SIZE MISMATCH[^\r\n]*)')) { $per += "$($m.Groups[1].Value):$(if ($m.Groups[3].Success) { $m.Groups[3].Value } elseif ($m.Groups[2].Value -like 'IDENT*') { 0 } else { 'size' })" }
                    $row["$($mode.Name)_checkpoints"] = $per -join ","
                }
                if ($o -match 'mesen compare: stable frames (\d+): (\d+) identical, (\d+) differ(?: \(first at frame (\d+)\))?(?:; unstable frames \(Mesen''s two runs differ\) (\d+): this emulator equals one of them in (\d+))?') {
                    $row["$($mode.Name)_stable"] = [int]$Matches[1]; $row["$($mode.Name)_identical"] = [int]$Matches[2]; $row["$($mode.Name)_differ"] = [int]$Matches[3]
                    $row["$($mode.Name)_first_diff"] = if ($Matches[4]) { [int]$Matches[4] } else { "" }
                    $row["$($mode.Name)_oo"] = if ($Matches[5]) { [int]$Matches[5] } else { 0 }
                    $row["$($mode.Name)_oo_matched"] = if ($Matches[6]) { [int]$Matches[6] } else { 0 }
                } else { $row.error = "no compare line ($($mode.Name)): " + ($o.Trim() -split "`n" | Select-Object -Last 1) }
            }
        }
        $row.total_s = [math]::Round($sw.Elapsed.TotalSeconds)
        ($using:results).Add([pscustomobject]$row)
        if (-not $using:Keep) { Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue; Remove-Item -Force "$dir.cap.out", "$dir.cap.err" -ErrorAction SilentlyContinue }
        "{0,-60} {1}" -f $name, $(if ($row.error) { "ERROR " + $row.error } else { (($using:Modes -split ";") | ForEach-Object { $n = ($_ -split "=", 2)[0].Trim(); "$n $($row["$($n)_identical"])/$($row["$($n)_stable"])+oo$($row["$($n)_oo_matched"])/$($row["$($n)_oo"])" }) -join "  " })
    }
    catch { ($using:results).Add([pscustomobject]@{ game = $g.Rom; error = $_.Exception.Message }); "ERROR $($g.Rom): $($_.Exception.Message)" }
    finally { ($using:slots).Enqueue($slot) }
}
$modeNames = ($Modes -split ";") | ForEach-Object { ($_ -split "=", 2)[0].Trim() }
$cols = @("game", "console", "region") + ($modeNames | ForEach-Object { "$($_)_stable", "$($_)_identical", "$($_)_differ", "$($_)_first_diff", "$($_)_oo", "$($_)_oo_matched", "$($_)_checkpoints" }) + @("mesen_s", "total_s", "error")
$results | Sort-Object game | Select-Object $cols | Export-Csv -Path $OutTsv -Delimiter "`t" -NoTypeInformation
"wrote $OutTsv"
