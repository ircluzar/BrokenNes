<#
.SYNOPSIS
  The Sega gate: every Workshop verifier of the Master System, Game Gear and Genesis in one command, one table, one exit code.
.DESCRIPTION
  Runs, one after the other (the machine is shared; nothing here runs in parallel except inside a verifier that says so):

    --sega-test --sega-chips-test --sega-contracts-test --sega-bridge-test --sega-stems-test --picture-bridge-test --cpu-wall-test --swap-matrix-test
    --md-test --md-cart-test --md-pic-test --md-golden --m68ktest --z80test (self-test and the SingleStepTests vectors)
    --sega-oracle-test --ym-oracle-test --fm-oracle-test --smstest

  and prints a table (step, result, passed/failed, seconds) and writes it to the report folder. Exit 0 only when every step that ran passed.
  A step whose inputs are not on this machine (a vector set, an oracle program, the game library) is SKIPPED and says what is missing: a skip is not a pass, and
  the summary counts them. -Strict turns every skip into a failure.

  It refuses to run a stale exe: the Workshop exe must be newer than every source file under Windows\NesEmulator, Core and Workshop (a stale exe falls through to
  the GUI and tests nothing). Build it first, or pass -Build to build it under %LOCALAPPDATA%\VRUN_Nes_Dev_work\bn_sega_gate (never into the repo).

  Time: the whole gate takes about 10 to 15 minutes on a quiet machine (the bridge test and the vector sets are most of it). -Quick leaves out the long vector
  sets and the bridge test's real-game groups and finishes in about 2 minutes (it is a smoke check, not the gate; the report says QUICK).

  Every verifier is shown able to fail in the track records (docs\projects\sega-track-*.md); -Prove runs the ones that have a built-in proof of failure (--smstest --prove).
.EXAMPLE
  pwsh -File UAT\sega-gate.ps1                 # the gate, against the Workshop exe built by `dotnet build Workshop -c Release`
  pwsh -File UAT\sega-gate.ps1 -Build          # build first (own artifacts folder)
  pwsh -File UAT\sega-gate.ps1 -Only md-test,sega-bridge-test
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [switch]$Build,
    [switch]$Quick,
    [switch]$Strict,
    [switch]$Prove,
    [string[]]$Only,
    [string]$ReportDir
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$work = Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work'
$tools = 'C:\BrokenNes-tools\sega'
if (-not $ReportDir) { $ReportDir = Join-Path $work ('sega_gate\' + (Get-Date -Format 'yyyyMMdd_HHmmss')) }
New-Item -ItemType Directory -Force $ReportDir | Out-Null

# ---------------------------------------------------------------------------------------------------- the exe, and whether it is current
if ($Build) {
    $art = Join-Path $work 'bn_sega_gate'
    Write-Host 'building the Workshop ...'
    $o = & dotnet.exe build (Join-Path $repo 'Workshop\BrokenNes.Workshop.csproj') -c Release --artifacts-path $art --disable-build-servers -nodeReuse:false -nologo -v q 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { Write-Host $o.Substring([Math]::Max(0, $o.Length - 1500)); Write-Host 'BUILD FAILED: the gate did not run' -ForegroundColor Red; exit 2 }
    $Exe = Join-Path $art 'bin\BrokenNes.Workshop\release\BrokenNes.Workshop.exe'
}
if (-not $Exe) { $Exe = Join-Path $repo 'Workshop\bin\Release\net10.0-windows\BrokenNes.Workshop.exe' }
if (-not (Test-Path -LiteralPath $Exe)) { Write-Host "Workshop exe not found: $Exe  (build it, or pass -Build)" -ForegroundColor Red; exit 2 }
$dll = Join-Path (Split-Path $Exe) 'BrokenNes.Workshop.dll'
$built = (Get-Item -LiteralPath $(if (Test-Path $dll) { $dll } else { $Exe })).LastWriteTimeUtc
$newest = Get-ChildItem (Join-Path $repo 'Windows\NesEmulator'), (Join-Path $repo 'Core'), (Join-Path $repo 'Workshop') -Recurse -Include *.cs, *.csproj -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if ($newest -and $newest.LastWriteTimeUtc -gt $built.AddSeconds(2)) {
    Write-Host ("STALE EXE: {0} was built {1:u}, but {2} changed {3:u}. A stale Workshop exe tests the old code (or falls through to the GUI). Build it: dotnet build Workshop\BrokenNes.Workshop.csproj -c Release, or pass -Build." -f $Exe, $built, $newest.FullName.Substring($repo.Length + 1), $newest.LastWriteTimeUtc) -ForegroundColor Red
    exit 2
}

# ---------------------------------------------------------------------------------------------------- the steps
$z80Vectors = Join-Path $tools 'test-sets\singlesteptests-z80\v1'
$libMd = 'X:\EMULATION\Sega Genesis'
$steps = @(
    @{ n = 'sega-test';            a = @('--sega-test') },
    @{ n = 'sega-chips-test';      a = @('--sega-chips-test') },
    @{ n = 'sega-contracts-test';  a = @('--sega-contracts-test') },
    @{ n = 'cpu-wall-test';        a = @('--cpu-wall-test') },
    @{ n = 'swap-matrix-test';     a = @('--swap-matrix-test') },
    @{ n = 'sega-bridge-test';     a = @('--sega-bridge-test'); heavy = $true },
    @{ n = 'sega-stems-test';      a = @('--sega-stems-test'); heavy = $true },
    @{ n = 'picture-bridge-test';  a = @('--picture-bridge-test'); heavy = $true },
    @{ n = 'md-test';              a = @('--md-test') },
    @{ n = 'md-cart-test';         a = @('--md-cart-test') },
    @{ n = 'md-pic-test';          a = @('--md-pic-test') },
    @{ n = 'md-golden';            a = @('--md-golden'); needs = @($libMd) },
    @{ n = 'z80test-selftest';     a = @('--z80test', '--selftest') },
    @{ n = 'z80test-vectors';      a = @('--z80test', '--vectors', $z80Vectors, '--jobs', '2'); needs = @($z80Vectors); heavy = $true },
    @{ n = 'm68ktest';             a = @('--m68ktest', '--jobs', '2'); needs = @((Join-Path $tools 'test-sets\singlesteptests-680x0\68000\v1')); heavy = $true },
    @{ n = 'sega-oracle-test';     a = @('--sega-oracle-test'); needs = @((Join-Path $tools 'oracles\harness\psg_oracle.exe')) },
    @{ n = 'ym-oracle-test';       a = @('--ym-oracle-test'); needs = @((Join-Path $tools 'oracles\harness\ymfm_ref.exe'), (Join-Path $tools 'oracles\harness\opn2_oracle.exe')) },
    @{ n = 'fm-oracle-test';       a = @('--fm-oracle-test'); needs = @((Join-Path $tools 'oracles\harness\opll_oracle.exe')) },
    @{ n = 'smstest';              a = @('--smstest'); needs = @((Join-Path $tools 'test-sets\sms-roms')) }
)
if ($Prove) { $steps += @{ n = 'smstest-prove'; a = @('--smstest', '--prove'); needs = @((Join-Path $tools 'test-sets\sms-roms')); heavy = $true } }
if ($Only) { $steps = @($steps | Where-Object { $Only -contains $_.n }) }
if ($Quick) { $steps = @($steps | Where-Object { -not $_.heavy }) }
if (-not $steps) { Write-Host 'no step selected' -ForegroundColor Yellow; exit 2 }

function Invoke-Step($step) {
    $psi = [Diagnostics.ProcessStartInfo]::new($Exe)
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    foreach ($x in $step.a) { [void]$psi.ArgumentList.Add($x) }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = [Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEndAsync(); $err = $p.StandardError.ReadToEndAsync()
    $limit = if ($step.timeoutMin) { $step.timeoutMin } else { 40 }
    $timedOut = -not $p.WaitForExit($limit * 60000)
    if ($timedOut) { try { $p.Kill($true) } catch { } }
    $text = $out.GetAwaiter().GetResult() + $err.GetAwaiter().GetResult()
    [pscustomobject]@{ Code = $(if ($timedOut) { -1 } else { $p.ExitCode }); Text = $text; Seconds = $sw.Elapsed.TotalSeconds; TimedOut = $timedOut }
}

# the counts a verifier prints: "N passed, M failed" (the last such line), or "N/M"
function Get-Counts([string]$text) {
    # "N passed, M failed" (most), "smstest: N pass, M fail, K skip", "== N frames match, M changed, K entries skipped" (md-golden)
    # z80test --vectors: "1604000/1604000 tests passed; 0 failed"; m68ktest: "TOTAL: N vectors: passed P, excluded E, failed F"
    foreach ($pat in @('(\d[\d,]*)\s+passed,\s+(\d[\d,]*)\s+failed', '(\d[\d,]*)\s+pass,\s+(\d[\d,]*)\s+fail', '(\d[\d,]*)\s+frames match,\s+(\d[\d,]*)\s+changed', '(\d[\d,]*)/\d[\d,]*\s+tests passed;\s+(\d[\d,]*)\s+failed', 'vectors:\s+passed\s+(\d[\d,]*),\s+excluded\s+\d[\d,]*,\s+failed\s+(\d[\d,]*)')) {
        $m = [regex]::Matches($text, $pat)
        if ($m.Count) {
            $l = $m[$m.Count - 1]
            $skip = [regex]::Match($l.Value + $text.Substring($l.Index + $l.Length, [Math]::Min(60, $text.Length - $l.Index - $l.Length)), '(\d+)\s+(?:skip|entries skipped)')
            return @{ Passed = [int64]($l.Groups[1].Value -replace ',', ''); Failed = [int64]($l.Groups[2].Value -replace ',', ''); Skipped = $(if ($skip.Success) { [int]$skip.Groups[1].Value } else { 0 }) }
        }
    }
    return $null
}

$rows = New-Object System.Collections.ArrayList
$total = [Diagnostics.Stopwatch]::StartNew()
Write-Host ("Sega gate | exe {0} (built {1:u}) | {2}{3}" -f $Exe, $built, $(if ($Quick) { 'QUICK' } else { 'full' }), $(if ($Strict) { ' | strict' } else { '' })) -ForegroundColor Cyan
foreach ($s in $steps) {
    $missing = @(); if ($s.needs) { $missing = @($s.needs | Where-Object { -not (Test-Path -LiteralPath $_) }) }
    if ($missing.Count) {
        $status = if ($Strict) { 'FAIL' } else { 'SKIP' }
        [void]$rows.Add([pscustomobject]@{ Step = $s.n; Result = $status; Passed = ''; Failed = ''; Seconds = 0; Note = 'missing: ' + ($missing -join ', ') })
        Write-Host ("{0,-22} {1,-5} missing: {2}" -f $s.n, $status, ($missing -join ', ')) -ForegroundColor Yellow
        continue
    }
    Write-Host ("{0,-22} running ..." -f $s.n) -NoNewline
    $r = Invoke-Step $s
    $r.Text | Set-Content (Join-Path $ReportDir ($s.n + '.txt'))
    $c = Get-Counts $r.Text
    $ok = $r.Code -eq 0 -and -not $r.TimedOut -and ($null -eq $c -or $c.Failed -eq 0)
    $note = if ($r.TimedOut) { 'TIMED OUT' } elseif ($r.Code -ne 0 -and $null -eq $c) { "exit $($r.Code), no result line (a stale exe falls through to the GUI)" } elseif ($r.Code -ne 0) { "exit $($r.Code)" } else { '' }
    if (-not $ok) {
        # say which checks failed, and when every one of them is a speed check, that a loaded machine can cause that (rerun on a quiet one before believing it)
        $failing = @($r.Text -split "`n" | Where-Object { $_ -match '^\s*FAIL\s' } | ForEach-Object { ($_ -replace '^\s*FAIL\s+', '').Trim() })
        if ($failing.Count) {
            $short = ($failing | Select-Object -First 3 | ForEach-Object { $_.Substring(0, [Math]::Min(90, $_.Length)) }) -join ' ; '
            $speedOnly = -not ($failing | Where-Object { $_ -notmatch 'real time|throughput|times faster|[0-9]x\b' })
            $note = ($note + " failing: $short" + $(if ($failing.Count -gt 3) { " (+$($failing.Count - 3) more)" } else { '' }) + $(if ($speedOnly) { ' [every failing check is a speed check: a busy machine causes this; rerun on a quiet one]' } else { '' })).Trim()
        }
    }
    if ($c -and $c.Skipped -gt 0) { $note = ($note + " $($c.Skipped) check(s) skipped inside (missing input: see the step's report)").Trim() }
    [void]$rows.Add([pscustomobject]@{ Step = $s.n; Result = $(if ($ok) { 'PASS' } else { 'FAIL' }); Passed = $(if ($c) { $c.Passed } else { '' }); Failed = $(if ($c) { $c.Failed } else { '' }); Seconds = [math]::Round($r.Seconds); Note = $note })
    Write-Host ("`r{0,-22} {1,-5} {2,9} passed {3,3} failed {4,5:F0}s {5}" -f $s.n, $(if ($ok) { 'PASS' } else { 'FAIL' }), $(if ($c) { $c.Passed } else { '-' }), $(if ($c) { $c.Failed } else { '-' }), $r.Seconds, $note) -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
}

$fail = @($rows | Where-Object { $_.Result -eq 'FAIL' }).Count
$skip = @($rows | Where-Object { $_.Result -eq 'SKIP' }).Count
$pass = @($rows | Where-Object { $_.Result -eq 'PASS' }).Count
$summary = "# Sega gate $(Get-Date -Format 'yyyy-MM-dd HH:mm') ($(if ($Quick) { 'QUICK' } else { 'full' }))`n`n| Step | Result | Passed | Failed | Seconds | Note |`n|---|---|---|---|---|---|`n"
foreach ($r in $rows) { $summary += "| $($r.Step) | $($r.Result) | $($r.Passed) | $($r.Failed) | $($r.Seconds) | $($r.Note -replace '\|', '/') |`n" }
$summary += "`n$pass passed, $fail failed, $skip skipped in $([math]::Round($total.Elapsed.TotalSeconds)) s. Exe: $Exe`n"
Set-Content (Join-Path $ReportDir 'summary.md') $summary
$rows | ConvertTo-Json | Set-Content (Join-Path $ReportDir 'summary.json')
Write-Host ""
Write-Host ("{0} step(s) passed, {1} failed, {2} skipped in {3:F0} s. Report: {4}" -f $pass, $fail, $skip, $total.Elapsed.TotalSeconds, $ReportDir) -ForegroundColor $(if ($fail) { 'Red' } elseif ($skip) { 'Yellow' } else { 'Green' })
if ($Quick) { Write-Host 'QUICK run: the long steps were left out; this is a smoke check, not the gate.' -ForegroundColor Yellow }
exit $(if ($fail) { 1 } else { 0 })
