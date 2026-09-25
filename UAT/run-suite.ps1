<#
.SYNOPSIS
    BrokenNes regression suite - the verdict layer over the Workshop headless CLIs.

.DESCRIPTION
    Every other tool in this repo prints a report and leaves a human to decide whether the numbers
    are right. This script is the missing half: UAT/cases/*.json stores the EXPECTED value for each
    run, this runner produces the ACTUAL value, diffs them, and exits non-zero when they disagree.

    Three case kinds, all driven through Workshop\bin\Release\net10.0-windows\BrokenNes.Workshop.exe:

      romtest    -> --romtest. Gates on exit code + FNV-1a64 framebuffer hash after N frames.
      savestate  -> --diag-savestate-roundtrip. Gates on exit code + verdict.
      benchmark  -> --benchmark. INFORMATIONAL ONLY. Timing is machine-dependent; it is printed
                    but can never fail the suite.

    WHAT A GREEN RUN PROVES, AND WHAT IT DOES NOT:
      It proves the emulator is deterministic and that nothing changed unintentionally.
      It does NOT prove the emulator is correct. A consistently wrong renderer produces a perfectly
      stable hash. Goldens here are "what this build did on the day it was recorded", not "what a
      real NES does". See UAT/README.md.

.PARAMETER Filter
    Wildcard matched against case id, suite name and tags. e.g. -Filter 'mapper30*'

.PARAMETER UpdateGolden
    THE "ACCEPT THE NEW REALITY" BUTTON. Re-records every selected case's expected values and
    rewrites the manifest on disk. Runs each case TWICE and refuses to write a golden whose two
    runs disagree. Prompts for confirmation unless -Yes is also passed. Never the default.

.PARAMETER Json
    Write a machine-readable result document to this path.

.PARAMETER Repeat
    Run each case N times and require all N to agree (determinism audit). Default 1.

.EXAMPLE
    .\UAT\run-suite.ps1
.EXAMPLE
    .\UAT\run-suite.ps1 -Filter 'corpus-*' -Json .\suite.json
.EXAMPLE
    .\UAT\run-suite.ps1 -Filter 'mapper30-start-fix' -UpdateGolden
#>
[CmdletBinding()]
param(
    [string]   $CasesDir,
    [string]   $Filter,
    [switch]   $UpdateGolden,
    [switch]   $Yes,
    [string]   $Json,
    [switch]   $StopOnFirstFailure,
    [int]      $Repeat = 1,
    [switch]   $SkipInformational,
    [switch]   $StrictMissing,
    [string]   $Exe,
    [int]      $TimeoutSeconds = 300
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------- paths

$UatRoot  = $PSScriptRoot
$RepoRoot = Split-Path -Parent $UatRoot
if (-not $CasesDir) { $CasesDir = Join-Path $UatRoot 'cases' }
if (-not $Exe) { $Exe = Join-Path $RepoRoot 'Workshop\bin\Release\net10.0-windows\BrokenNes.Workshop.exe' }

if (-not (Test-Path -LiteralPath $Exe)) {
    Write-Host "Workshop executable not found: $Exe" -ForegroundColor Red
    Write-Host "Build it first:  dotnet build Workshop/BrokenNes.Workshop.csproj -c Release" -ForegroundColor Red
    exit 2
}
if (-not (Test-Path -LiteralPath $CasesDir)) {
    Write-Host "Cases directory not found: $CasesDir" -ForegroundColor Red
    exit 2
}

$exeInfo  = Get-Item -LiteralPath $Exe
$exeStamp = (Get-Item -LiteralPath (Join-Path $exeInfo.DirectoryName 'BrokenNes.Workshop.dll') -ErrorAction SilentlyContinue)
if (-not $exeStamp) { $exeStamp = $exeInfo }

# ---------------------------------------------------------------------------- process helper

function Invoke-Workshop {
    <#
      Runs the Workshop CLI with stdout/stderr redirected to real pipes.
      Redirection matters twice over: Workshop is a WinExe, so an un-redirected run has no stdout
      handle at all (RomTestCli.EnsureConsole compensates by attaching to the parent console, which
      we do NOT want to depend on), and ArgumentList sidesteps quoting for ROM paths that contain
      spaces, brackets and apostrophes - the 65-o-borked corpus is full of all three.
    #>
    param([string[]]$Arguments, [int]$TimeoutMs)

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName               = $Exe
    $psi.UseShellExecute        = $false
    $psi.CreateNoWindow         = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    if ($psi.PSObject.Properties['ArgumentList']) {
        foreach ($a in $Arguments) { [void]$psi.ArgumentList.Add([string]$a) }
    } else {
        # Windows PowerShell 5.1 fallback - no ArgumentList, so quote by hand.
        $psi.Arguments = ($Arguments | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -join ' '
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p  = [System.Diagnostics.Process]::Start($psi)
    # Read both streams asynchronously BEFORE waiting: a child that fills one pipe's buffer while
    # we block on the other deadlocks forever.
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()
    $timedOut = $false
    if (-not $p.WaitForExit($TimeoutMs)) {
        $timedOut = $true
        try { $p.Kill($true) } catch { try { $p.Kill() } catch { } }
        try { $p.WaitForExit(5000) | Out-Null } catch { }
    }
    $sw.Stop()

    $stdout = ''; $stderr = ''
    try { $stdout = $outTask.GetAwaiter().GetResult() } catch { }
    try { $stderr = $errTask.GetAwaiter().GetResult() } catch { }
    $code = -1
    try { $code = $p.ExitCode } catch { }
    $p.Dispose()

    [pscustomobject]@{
        ExitCode = $code
        StdOut   = $stdout
        StdErr   = $stderr
        Ms       = [int]$sw.ElapsedMilliseconds
        TimedOut = $timedOut
    }
}

function ConvertFrom-CliJson {
    # The CLIs print one indented JSON document. Be forgiving about a stray leading line.
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    $start = $Text.IndexOf('{')
    $end   = $Text.LastIndexOf('}')
    if ($start -lt 0 -or $end -le $start) { return $null }
    try { return $Text.Substring($start, $end - $start + 1) | ConvertFrom-Json } catch { return $null }
}

function Get-Prop {
    param($Object, [string]$Name, $Default = $null)
    if ($null -eq $Object) { return $Default }
    $p = $Object.PSObject.Properties[$Name]
    if ($null -eq $p) { return $Default }
    if ($null -eq $p.Value) { return $Default }
    return $p.Value
}

# ---------------------------------------------------------------------------- manifest loading

$manifestFiles = Get-ChildItem -LiteralPath $CasesDir -Filter '*.json' -File | Sort-Object Name
if (-not $manifestFiles) {
    Write-Host "No *.json case manifests in $CasesDir" -ForegroundColor Red
    exit 2
}

$manifests = @()
foreach ($mf in $manifestFiles) {
    $doc = $null
    try { $doc = Get-Content -LiteralPath $mf.FullName -Raw | ConvertFrom-Json } catch {
        Write-Host "Manifest $($mf.Name) is not valid JSON: $($_.Exception.Message)" -ForegroundColor Red
        exit 2
    }
    $manifests += [pscustomobject]@{ File = $mf.FullName; Name = $mf.BaseName; Doc = $doc; Dirty = $false }
}

function Resolve-RomPath {
    param([string]$Raw, $Roots)
    $p = $Raw
    if ($Roots) {
        foreach ($r in $Roots.PSObject.Properties) {
            $p = $p.Replace('{' + $r.Name + '}', [string]$r.Value)
        }
    }
    $p = $p -replace '/', '\'
    if ([System.IO.Path]::IsPathRooted($p)) { return $p }
    return (Join-Path $RepoRoot $p)
}

# Flatten every manifest into a work list, applying -Filter.
$work = @()
foreach ($m in $manifests) {
    $suite = [string](Get-Prop $m.Doc 'suite' $m.Name)
    $roots = Get-Prop $m.Doc 'roots' $null
    $cases = Get-Prop $m.Doc 'cases' @()
    foreach ($c in $cases) {
        $id   = [string](Get-Prop $c 'id' '(unnamed)')
        $tags = @(Get-Prop $c 'tags' @())
        if ($Filter) {
            $hit = ($id -like $Filter) -or ($suite -like $Filter) -or ($m.Name -like $Filter)
            foreach ($t in $tags) { if ([string]$t -like $Filter) { $hit = $true } }
            if (-not $hit) { continue }
        }
        $work += [pscustomobject]@{
            Manifest = $m
            Suite    = $suite
            Case     = $c
            Id       = $id
            Kind     = [string](Get-Prop $c 'kind' 'romtest')
            RomPath  = Resolve-RomPath -Raw ([string](Get-Prop $c 'rom' '')) -Roots $roots
        }
    }
}

if ($work.Count -eq 0) {
    Write-Host "No cases matched$(if ($Filter) { " filter '$Filter'" })." -ForegroundColor Yellow
    exit 2
}

# ---------------------------------------------------------------------------- -UpdateGolden gate

if ($UpdateGolden) {
    Write-Host ''
    Write-Host '  ###############################################################################' -ForegroundColor Yellow
    Write-Host '  #                                                                             #' -ForegroundColor Yellow
    Write-Host '  #   -UpdateGolden : REWRITING EXPECTED VALUES ON DISK                         #' -ForegroundColor Yellow
    Write-Host '  #                                                                             #' -ForegroundColor Yellow
    Write-Host '  #   This does not test anything. It declares whatever the emulator does        #' -ForegroundColor Yellow
    Write-Host '  #   RIGHT NOW to be correct, and destroys the evidence of any regression       #' -ForegroundColor Yellow
    Write-Host '  #   currently present in the working tree.                                     #' -ForegroundColor Yellow
    Write-Host '  #                                                                             #' -ForegroundColor Yellow
    Write-Host '  #   Only legitimate when you have already read the failing diff and decided    #' -ForegroundColor Yellow
    Write-Host '  #   the NEW behaviour is the intended one. Commit the manifest change on its   #' -ForegroundColor Yellow
    Write-Host '  #   own, with a message saying WHY the goldens moved.                          #' -ForegroundColor Yellow
    Write-Host '  #                                                                             #' -ForegroundColor Yellow
    Write-Host '  ###############################################################################' -ForegroundColor Yellow
    Write-Host ''
    Write-Host ("  Cases in scope : {0}" -f $work.Count) -ForegroundColor Yellow
    Write-Host ("  Filter         : {0}" -f $(if ($Filter) { $Filter } else { '(none - ALL CASES)' })) -ForegroundColor Yellow
    Write-Host ("  Manifests      : {0}" -f (($manifests | ForEach-Object { $_.Name }) -join ', ')) -ForegroundColor Yellow
    Write-Host ''
    if (-not $Yes) {
        $answer = Read-Host '  Type UPDATE (all caps) to rewrite these goldens, anything else to abort'
        if ($answer -cne 'UPDATE') {
            Write-Host '  Aborted. Nothing was written.' -ForegroundColor Green
            exit 2
        }
    } else {
        Write-Host '  -Yes supplied: confirmation prompt skipped.' -ForegroundColor Yellow
    }
    Write-Host '  Each case runs TWICE; a case whose two runs disagree is NOT written as golden.' -ForegroundColor Yellow
    Write-Host ''
}

$runsPerCase = [Math]::Max(1, $Repeat)
if ($UpdateGolden) { $runsPerCase = [Math]::Max(2, $runsPerCase) }

# ---------------------------------------------------------------------------- per-kind execution

function Invoke-RomTestCase {
    param($Case, [string]$RomPath)
    $a = @('--romtest', '--rom', $RomPath)
    foreach ($k in @('cpu', 'ppu', 'apu')) {
        $v = Get-Prop $Case $k $null
        if ($v) { $a += @("--$k", [string]$v) }
    }
    $frames = [int](Get-Prop $Case 'frames' 600)
    $a += @('--frames', "$frames")
    $inp = Get-Prop $Case 'input' $null
    if ($inp) { $a += @('--input', [string]$inp) }
    $a += '--json'

    $r = Invoke-Workshop -Arguments $a -TimeoutMs ($TimeoutSeconds * 1000)
    $j = ConvertFrom-CliJson $r.StdOut
    [pscustomobject]@{
        Raw      = $r
        Observed = [pscustomobject]@{
            exitCode        = $r.ExitCode
            frameBufferHash = [string](Get-Prop $j 'FrameBufferHash' '')
            framesRun       = [int](Get-Prop $j 'FramesRun' (-1))
            distinctColors  = [int](Get-Prop $j 'DistinctColors' (-1))
            result          = [string](Get-Prop $j 'Result' '')
            crashInfo       = [string](Get-Prop $j 'CrashInfo' '')
        }
        Display  = "exit={0} {1}" -f $r.ExitCode, $(if ($j) { [string](Get-Prop $j 'FrameBufferHash' '(no hash)') } else { '(no json)' })
    }
}

function Invoke-SaveStateCase {
    param($Case, [string]$RomPath)
    $a = @('--diag-savestate-roundtrip', '--rom', $RomPath)
    foreach ($k in @('cpu', 'ppu', 'apu')) {
        $v = Get-Prop $Case $k $null
        if ($v) { $a += @("--$k", [string]$v) }
    }
    $a += @('--warmup-frames', "$([int](Get-Prop $Case 'warmupFrames' 180))")
    $a += @('--continue-frames', "$([int](Get-Prop $Case 'continueFrames' 120))")
    if (Get-Prop $Case 'strict' $false) { $a += '--strict' }
    # freshInstance: load into a new machine, as a player loading a slot after a restart does.
    # timing: 'ntsc' or 'precise' - each timing mode keeps its own frame-clock state to round-trip.
    if (Get-Prop $Case 'freshInstance' $false) { $a += '--fresh-instance' }
    $timing = [string](Get-Prop $Case 'timing' '')
    if ($timing -eq 'ntsc') { $a += '--ntsc' } elseif ($timing -eq 'precise') { $a += '--precise' }

    $r = Invoke-Workshop -Arguments $a -TimeoutMs ($TimeoutSeconds * 1000)
    $j = ConvertFrom-CliJson $r.StdOut
    $verdict = [string](Get-Prop $j 'Verdict' '')
    [pscustomobject]@{
        Raw      = $r
        Observed = [pscustomobject]@{
            exitCode             = $r.ExitCode
            verdict              = $verdict
            mismatchedFrameCount = [int](Get-Prop $j 'MismatchedFrameCount' (-1))
            immediateLossy       = [bool](Get-Prop $j 'ImmediateRoundtripLossy' $false)
        }
        Display  = "exit={0} {1}" -f $r.ExitCode, $(if ($verdict) { ($verdict -split ' \(')[0] } else { '(no json)' })
    }
}

function Invoke-BenchmarkCase {
    param($Case, [string]$RomPath)
    $a = @('--benchmark', '--rom', $RomPath)
    foreach ($k in @('cpu', 'ppu', 'apu')) {
        $v = Get-Prop $Case $k $null
        if ($v) { $a += @("--$k", [string]$v) }
    }
    $a += @('--weight', "$([int](Get-Prop $Case 'weight' 1))")

    $r = Invoke-Workshop -Arguments $a -TimeoutMs ($TimeoutSeconds * 1000)
    $j = ConvertFrom-CliJson $r.StdOut
    $msPerFrame = $null
    $results = Get-Prop $j 'Results' @()
    foreach ($res in $results) {
        if ([string](Get-Prop $res 'Name' '') -like 'Frame(*') { $msPerFrame = [double](Get-Prop $res 'MsPerIter' 0) }
    }
    $display = '(no data)'
    if ($msPerFrame -and $msPerFrame -gt 0) {
        # 60fps NTSC frame budget is 16.639 ms; x-realtime is the number people actually care about.
        $display = '{0:0.000} ms/frame ({1:0.0}x realtime)' -f $msPerFrame, (16.639 / $msPerFrame)
    }
    [pscustomobject]@{
        Raw      = $r
        Observed = [pscustomobject]@{ exitCode = $r.ExitCode; msPerFrame = $msPerFrame }
        Display  = $display
    }
}

# ---------------------------------------------------------------------------- main loop

$rows        = @()
$failCount   = 0
$passCount   = 0
$skipCount   = 0
$infoCount   = 0
$aborted     = $false
$startedUtc  = (Get-Date).ToUniversalTime()

Write-Host ''
Write-Host ('BrokenNes suite  |  {0} case(s)  |  exe {1:yyyy-MM-dd HH:mm}  |  {2}' -f `
    $work.Count, $exeStamp.LastWriteTime, $(if ($UpdateGolden) { 'MODE: UPDATE GOLDEN' } else { 'MODE: verify' })) -ForegroundColor Cyan
Write-Host ''

foreach ($w in $work) {
    $c    = $w.Case
    $id   = $w.Id
    $kind = $w.Kind

    $cores = '{0}/{1}/{2}' -f `
        ([string](Get-Prop $c 'cpu' '-')), ([string](Get-Prop $c 'ppu' '-')), ([string](Get-Prop $c 'apu' '-'))
    $framesLabel = switch ($kind) {
        'savestate' { '{0}+{1}' -f [int](Get-Prop $c 'warmupFrames' 180), [int](Get-Prop $c 'continueFrames' 120) }
        'benchmark' { 'w{0}'   -f [int](Get-Prop $c 'weight' 1) }
        default     { '{0}'    -f [int](Get-Prop $c 'frames' 600) }
    }

    $expect  = Get-Prop $c 'expect' $null
    $row = [ordered]@{
        id = $id; suite = $w.Suite; kind = $kind; cores = $cores; frames = $framesLabel
        rom = $w.RomPath; status = ''; expected = ''; actual = ''; ms = 0; note = ''
        description = [string](Get-Prop $c 'description' '')
    }

    # --- explicit skip recorded in the manifest (e.g. unsupported mapper) -----
    $skipReason = [string](Get-Prop $c 'skip' '')
    if ($skipReason) {
        $row.status = 'SKIP'; $row.expected = '(not run)'; $row.actual = '-'; $row.note = $skipReason
        $rows += [pscustomobject]$row; $skipCount++
        continue
    }

    # --- ROM missing on this machine -----------------------------------------
    if ($kind -ne 'none' -and -not (Test-Path -LiteralPath $w.RomPath)) {
        if ($StrictMissing) {
            $row.status = 'FAIL'; $row.note = "ROM not found: $($w.RomPath)"; $failCount++
        } else {
            $row.status = 'SKIP'; $row.note = "ROM not found on this machine: $($w.RomPath)"; $skipCount++
        }
        $row.expected = '(not run)'; $row.actual = '-'
        $rows += [pscustomobject]$row
        if ($StrictMissing -and $StopOnFirstFailure) { $aborted = $true; break }
        continue
    }

    if ($SkipInformational -and $kind -eq 'benchmark') {
        $row.status = 'SKIP'; $row.expected = '(info)'; $row.actual = '-'; $row.note = '-SkipInformational'
        $rows += [pscustomobject]$row; $skipCount++
        continue
    }

    # --- run (possibly several times, for determinism) ------------------------
    $runs = @()
    for ($i = 0; $i -lt $runsPerCase; $i++) {
        $runs += switch ($kind) {
            'romtest'   { Invoke-RomTestCase   -Case $c -RomPath $w.RomPath }
            'savestate' { Invoke-SaveStateCase -Case $c -RomPath $w.RomPath }
            'benchmark' { Invoke-BenchmarkCase -Case $c -RomPath $w.RomPath }
            default     { throw "Case '$id' has unknown kind '$kind' (expected romtest|savestate|benchmark)" }
        }
    }
    $first      = $runs[0]
    $totalMs    = 0
    foreach ($r in $runs) { $totalMs += $r.Raw.Ms }
    $row.ms     = $totalMs
    $row.actual = $first.Display

    if ($first.Raw.TimedOut) {
        $row.status = 'FAIL'; $row.expected = '(any)'; $row.actual = 'TIMEOUT'
        $row.note = "no exit within ${TimeoutSeconds}s - killed"
        $rows += [pscustomobject]$row; $failCount++
        if ($StopOnFirstFailure) { $aborted = $true; break }
        continue
    }

    # Determinism check across repeats. Benchmarks are timing-based and are exempt.
    $unstable = $null
    if ($kind -ne 'benchmark' -and $runs.Count -gt 1) {
        $sig0 = ($first.Observed | ConvertTo-Json -Compress -Depth 5)
        for ($i = 1; $i -lt $runs.Count; $i++) {
            $sigN = ($runs[$i].Observed | ConvertTo-Json -Compress -Depth 5)
            if ($sigN -ne $sig0) { $unstable = "run 1 = $($first.Display); run $($i+1) = $($runs[$i].Display)"; break }
        }
    }
    if ($unstable) {
        $row.status = 'FAIL'; $row.expected = '(reproducible)'; $row.actual = 'NON-DETERMINISTIC'
        $row.note = $unstable
        $rows += [pscustomobject]$row; $failCount++
        if ($StopOnFirstFailure) { $aborted = $true; break }
        continue
    }

    # --- benchmark: informational, never gates -------------------------------
    if ($kind -eq 'benchmark') {
        $row.status = 'INFO'; $row.expected = '(not gated)'
        if ($first.Raw.ExitCode -ne 0) {
            $row.note = "benchmark exited $($first.Raw.ExitCode) - reported, not gated"
        }
        $rows += [pscustomobject]$row; $infoCount++
        continue
    }

    # --- golden update -------------------------------------------------------
    if ($UpdateGolden) {
        $before = if ($expect) { ($expect | ConvertTo-Json -Compress -Depth 5) } else { '(none)' }
        $after  = ($first.Observed | ConvertTo-Json -Compress -Depth 5)
        if ($c.PSObject.Properties['expect']) { $c.expect = $first.Observed }
        else { $c | Add-Member -NotePropertyName expect -NotePropertyValue $first.Observed }
        $w.Manifest.Dirty = $true
        $row.status   = if ($before -eq $after) { 'SAME' } else { 'WROTE' }
        $row.expected = $before
        $row.actual   = $after
        $row.note     = 'golden rewritten (verified reproducible over 2 runs)'
        $rows += [pscustomobject]$row
        $passCount++
        continue
    }

    # --- verify against golden -----------------------------------------------
    if (-not $expect) {
        $row.status = 'FAIL'; $row.expected = '(no golden recorded)'
        $row.note = "case has no 'expect' block - run with -UpdateGolden to record one"
        $rows += [pscustomobject]$row; $failCount++
        if ($StopOnFirstFailure) { $aborted = $true; break }
        continue
    }

    $diffs = @()
    foreach ($p in $expect.PSObject.Properties) {
        $want = $p.Value
        $got  = Get-Prop $first.Observed $p.Name '(absent)'
        # Normalise: JSON numbers vs PS ints, and null vs empty string.
        $wantS = if ($null -eq $want) { '' } else { [string]$want }
        $gotS  = if ($null -eq $got)  { '' } else { [string]$got }
        if ($wantS -cne $gotS) { $diffs += "$($p.Name): expected '$wantS', got '$gotS'" }
    }

    $expectExit = [string](Get-Prop $expect 'exitCode' '?')
    $expectWhat = [string](Get-Prop $expect 'frameBufferHash' '')
    if (-not $expectWhat) {
        $v = [string](Get-Prop $expect 'verdict' '')
        if ($v) { $expectWhat = ($v -split ' \(')[0] }
    }
    $row.expected = "exit=$expectExit $expectWhat".TrimEnd()

    if ($diffs.Count -eq 0) {
        $row.status = 'PASS'
        $rows += [pscustomobject]$row; $passCount++
    } else {
        $row.status = 'FAIL'
        $row.note = $diffs -join '; '
        if ($first.Raw.StdErr) { $row.note += ' | stderr: ' + (($first.Raw.StdErr -split "`r?`n")[0]) }
        $rows += [pscustomobject]$row; $failCount++
        if ($StopOnFirstFailure) { $aborted = $true; break }
    }
}

# ---------------------------------------------------------------------------- manifest writeback

$written = @()
if ($UpdateGolden) {
    foreach ($m in $manifests) {
        if (-not $m.Dirty) { continue }
        if ($m.Doc.PSObject.Properties['goldenGeneratedUtc']) {
            $m.Doc.goldenGeneratedUtc = $startedUtc.ToString('yyyy-MM-ddTHH:mm:ssZ')
        } else {
            $m.Doc | Add-Member -NotePropertyName goldenGeneratedUtc -NotePropertyValue $startedUtc.ToString('yyyy-MM-ddTHH:mm:ssZ')
        }
        ($m.Doc | ConvertTo-Json -Depth 12) | Set-Content -LiteralPath $m.File -Encoding UTF8
        $written += $m.File
    }
}

# ---------------------------------------------------------------------------- table

function Write-SuiteTable {
    param($Rows)
    $cols = @(
        @{ H = 'STATUS'; K = 'status'   },
        @{ H = 'CASE';   K = 'id'       },
        @{ H = 'KIND';   K = 'kind'     },
        @{ H = 'CORES';  K = 'cores'    },
        @{ H = 'FRAMES'; K = 'frames'   },
        @{ H = 'EXPECTED'; K = 'expected' },
        @{ H = 'ACTUAL';   K = 'actual'   },
        @{ H = 'MS';       K = 'ms'       }
    )
    foreach ($col in $cols) {
        $w = $col.H.Length
        foreach ($r in $Rows) { $l = ([string]$r.($col.K)).Length; if ($l -gt $w) { $w = $l } }
        $col.W = [Math]::Min($w, 40)
    }
    $header = ($cols | ForEach-Object { $_.H.PadRight($_.W) }) -join '  '
    Write-Host $header -ForegroundColor White
    Write-Host ('-' * $header.Length) -ForegroundColor DarkGray
    foreach ($r in $Rows) {
        $line = ($cols | ForEach-Object {
            $v = [string]$r.($_.K)
            if ($v.Length -gt $_.W) { $v = $v.Substring(0, $_.W - 1) + '~' }
            $v.PadRight($_.W)
        }) -join '  '
        $color = switch ($r.status) {
            'PASS'  { 'Green' }  'FAIL' { 'Red' }   'SKIP' { 'DarkYellow' }
            'INFO'  { 'Cyan' }   'WROTE'{ 'Yellow' } 'SAME' { 'DarkGreen' }
            default { 'Gray' }
        }
        Write-Host $line -ForegroundColor $color
        if ($r.note) {
            $prefix = ' ' * ($cols[0].W + 2)
            Write-Host ("{0}> {1}" -f $prefix, $r.note) -ForegroundColor $(if ($r.status -eq 'FAIL') { 'Red' } else { 'DarkGray' })
        }
    }
}

Write-SuiteTable -Rows $rows

$elapsed = ((Get-Date).ToUniversalTime() - $startedUtc).TotalSeconds
Write-Host ''
Write-Host ('{0} pass  {1} fail  {2} skip  {3} info   ({4:0.0}s)' -f $passCount, $failCount, $skipCount, $infoCount, $elapsed) `
    -ForegroundColor $(if ($failCount -gt 0) { 'Red' } else { 'Green' })

if ($UpdateGolden) {
    Write-Host ''
    if ($written.Count -gt 0) {
        Write-Host 'GOLDENS REWRITTEN in:' -ForegroundColor Yellow
        foreach ($f in $written) { Write-Host "  $f" -ForegroundColor Yellow }
        Write-Host 'Review the diff (git diff UAT/cases) before committing.' -ForegroundColor Yellow
    } else {
        Write-Host 'No manifest needed rewriting.' -ForegroundColor Yellow
    }
}
if ($aborted) {
    Write-Host '-StopOnFirstFailure: remaining cases were not run.' -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------- machine-readable

if ($Json) {
    $doc = [ordered]@{
        startedUtc      = $startedUtc.ToString('yyyy-MM-ddTHH:mm:ssZ')
        durationSeconds = [Math]::Round($elapsed, 2)
        machine         = $env:COMPUTERNAME
        exe             = $Exe
        exeBuiltUtc     = $exeStamp.LastWriteTimeUtc.ToString('yyyy-MM-ddTHH:mm:ssZ')
        mode            = $(if ($UpdateGolden) { 'update-golden' } else { 'verify' })
        filter          = $Filter
        totals          = [ordered]@{ pass = $passCount; fail = $failCount; skip = $skipCount; info = $infoCount }
        exitCode        = $(if ($failCount -gt 0) { 1 } else { 0 })
        cases           = $rows
    }
    $dir = Split-Path -Parent $Json
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    ($doc | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $Json -Encoding UTF8
    Write-Host "JSON written to $Json" -ForegroundColor DarkGray
}

Write-Host ''
if ($failCount -gt 0) { exit 1 }
exit 0
