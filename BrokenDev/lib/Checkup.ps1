# BrokenDev 'checkup' (milestone D9 of docs/projects/brokendev-debugging-plan.md, "D9 settled"): before every hand-off and on request (never
# scheduled), every project's build, verify (checks, budgets, promoted movies, debug-port asserts, coverage), compat and open bug captures,
# and the BrokenNes repo's own suites; a one-screen summary, out\checkup\checkup.json, and what changed since the last checkup.
# Requires Common, Manifest, Build, Verify, Compat, Bugs.

Set-StrictMode -Version Latest

function Get-BdCheckupDir { Join-Path (Join-Path (Get-BdHome) 'out') 'checkup' }

function New-BdCheckupStep {
    param([string]$Step, [string]$State, [string]$Detail = '', [double]$Seconds = 0, [string]$Record = '')
    [pscustomobject]([ordered]@{ step = $Step; state = $State; detail = $Detail; seconds = [math]::Round($Seconds, 1); record = $Record })
}

function Get-BdVerifySteps {
    <#
      The checkup's columns read from one verify record: verify (the scenarios and compares, promoted movies aside), findings (the hardware
      checks), budgets, movies (the promoted movies), port (debug-port asserts and errors), coverage. Each a step object; a FAIL names why.
    #>
    param([Parameter(Mandatory)]$Rec, $Manifest, [string]$RecordPath = '', [double]$Seconds = 0)
    $movieIds = @(@(Get-BdProp $Manifest 'movies' @()) | ForEach-Object { [string]$_.id })
    $steps = New-Object System.Collections.Generic.List[object]
    # verify: the plain scenarios and compares
    $bad = @()
    foreach ($sc in @($Rec.scenarios)) {
        if ($movieIds -contains [string]$sc.id) { continue }
        if ($sc.ok -or ((Get-BdProp $sc 'info' $false) -eq $true)) { continue }
        $why = @(@($sc.checks) | Where-Object { -not $_.ok } | ForEach-Object { "$($_.id): $($_.detail)" })
        if ([string]$sc.result -ne 'ok') { $why = @("$($sc.result): $($sc.crashInfo)") + $why }
        $bad += "scenario $($sc.id): $(($why | Select-Object -First 2) -join '; ')"
    }
    foreach ($c in @(Get-BdProp $Rec 'compare' @())) { if (-not $c.ok) { $bad += "compare $($c.id): $($c.detail)" } }
    $nsc = @($Rec.scenarios | Where-Object { $movieIds -notcontains [string]$_.id }).Count
    $steps.Add((New-BdCheckupStep 'verify' $(if ($bad.Count) { 'FAIL' } else { 'ok' }) $(if ($bad.Count) { $bad -join ' | ' } else { "$nsc scenario(s) pass" }) $Seconds $RecordPath))

    $chk = Get-BdProp $Rec 'checks' $null
    if (-not $chk) {
        $steps.Add((New-BdCheckupStep 'findings' 'skip' 'the manifest has no checks section'))
        $steps.Add((New-BdCheckupStep 'budgets' 'skip' 'the manifest has no budgets'))
    } else {
        $new = @(@($chk.findings) | Where-Object { -not $_.accepted })
        $why = @($new | ForEach-Object { 'NEW {0} at {1} ({2} frame {3}, pc {4} {5})' -f $_.rule, $_.place, $_.scenario, $_.firstFrame, $_.pc, $_.pcSymbol })
        $why += @(@($chk.problems) | ForEach-Object { "manifest: $_" })
        $why += @(@($chk.scenarios) | Where-Object { [string]$_.result -ne 'ok' -or -not $_.frameIdentical } | ForEach-Object { "checked run $($_.id): $(if ([string]$_.result -ne 'ok') { $_.error } else { $_.identity })" })
        $st = if ($why.Count) { 'FAIL' } else { 'ok' }
        $steps.Add((New-BdCheckupStep 'findings' $st $(if ($why.Count) { $why -join ' | ' } else { "0 new, $($chk.acceptedFindings) accepted" })))
        $bud = @($chk.budgets)
        if ($bud.Count -eq 0) { $steps.Add((New-BdCheckupStep 'budgets' 'skip' 'no budgets in the manifest')) }
        else {
            $f = @($bud | Where-Object { $_.status -eq 'fail' }); $w = @($bud | Where-Object { $_.status -eq 'warn' })
            $st = if ($f.Count) { 'FAIL' } elseif ($w.Count) { 'warn' } else { 'ok' }
            $d = @(@($f) + @($w) | ForEach-Object { '{0} {1} {2} ({3} {4})' -f $_.status.ToUpperInvariant(), $_.id, $_.value, $(if ($_.kind -eq 'max') { 'at most' } else { 'at least' }), $_.limit })
            $steps.Add((New-BdCheckupStep 'budgets' $st $(if ($d.Count) { $d -join ' | ' } else { "$($bud.Count) within their limits" })))
        }
    }

    # the promoted movies: their scenarios and the movies section's problems
    $mv = Get-BdProp $Rec 'movies' $null
    if ($movieIds.Count -eq 0 -and -not $mv) { $steps.Add((New-BdCheckupStep 'movies' 'skip' 'no promoted movies')) }
    else {
        $why = @(@(if ($mv) { @($mv.problems) } else { @() }) | ForEach-Object { [string]$_ })
        foreach ($sc in @($Rec.scenarios | Where-Object { $movieIds -contains [string]$_.id -and -not $_.ok })) {
            $why += "movie $($sc.id): $((@(@($sc.checks) | Where-Object { -not $_.ok } | ForEach-Object { "$($_.id): $($_.detail)" }) | Select-Object -First 2) -join '; ')"
        }
        $steps.Add((New-BdCheckupStep 'movies' $(if ($why.Count) { 'FAIL' } else { 'ok' }) $(if ($why.Count) { $why -join ' | ' } else { "$($movieIds.Count) in sync" })))
    }

    # the debug port: asserts and errors anywhere (they fail verify already; the column names them)
    $asserts = @(); $said = 0
    foreach ($sc in @($Rec.scenarios)) {
        $dp = Get-BdProp $sc 'debugPort' $null
        if (-not $dp -or -not (Get-BdProp $dp 'counts' $null)) { continue }
        $said += [int]$dp.counts.print + [int]$dp.counts.marker + [int]$dp.counts.assert + [int]$dp.counts.error
        foreach ($e in @(@(Get-BdProp $dp 'events' @()) | Where-Object { $_.kind -in 'assert', 'error' } | Select-Object -First 3)) { $asserts += "$($sc.id) frame $($e.frame) $($e.kind): $($e.text)" }
        if (([int]$dp.counts.assert + [int]$dp.counts.error) -gt 0 -and $asserts.Count -eq 0) { $asserts += "$($sc.id): $($dp.counts.assert) assert(s), $($dp.counts.error) error(s)" }
    }
    if ($asserts.Count) { $steps.Add((New-BdCheckupStep 'port' 'FAIL' ($asserts -join ' | '))) }
    elseif ($said -gt 0) { $steps.Add((New-BdCheckupStep 'port' 'ok' "$said message(s), no assert")) }
    else { $steps.Add((New-BdCheckupStep 'port' 'skip' 'the game said nothing on the debug port')) }

    # coverage (D9): verify's coverage section, when the Workshop made one
    $cov = Get-BdProp $Rec 'coverage' $null
    if (-not $cov) { $steps.Add((New-BdCheckupStep 'coverage' 'skip' 'verify made no coverage map (a Workshop from before D9?)')) }
    else {
        $why = @(@(Get-BdProp $cov 'problems' @()) | ForEach-Object { [string]$_ })
        $steps.Add((New-BdCheckupStep 'coverage' $(if ($why.Count) { 'FAIL' } else { 'ok' }) $(if ($why.Count) { $why -join ' | ' } else { '{0}% of the code bytes ran' -f $cov.percent })))
    }
    $steps
}

function Invoke-BdCheckupProject {
    <# One project's checkup: build, verify (and its columns), compat, bug captures. Returns { id; ok; steps; coverage; seconds }. #>
    param([Parameter(Mandatory)][string]$Id, [string]$Exe = '', [switch]$NoCompat)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $m = Read-BdManifest -Id $Id
    $out = Get-BdOutDir $Id
    $steps = New-Object System.Collections.Generic.List[object]
    $cols = 'verify', 'findings', 'budgets', 'movies', 'port', 'coverage'

    # 1. build (dependencies first)
    $t = [System.Diagnostics.Stopwatch]::StartNew(); $built = $false
    try {
        foreach ($d in (Resolve-BdProjectDeps -Id $Id)) { Write-BdInfo "  build $d"; [void](Invoke-BdProjectBuild -Id $d) }
        $built = $true
        $steps.Add((New-BdCheckupStep 'build' 'ok' 'built' $t.Elapsed.TotalSeconds (Join-Path $out 'build.json')))
    } catch { $steps.Add((New-BdCheckupStep 'build' 'FAIL' ($_.Exception.Message -split "`n")[0] $t.Elapsed.TotalSeconds (Join-Path $out 'build.json'))) }

    # 2. verify, coverage on
    if (-not $built) { foreach ($c in $cols) { $steps.Add((New-BdCheckupStep $c 'skip' 'not built')) } }
    else {
        $t = [System.Diagnostics.Stopwatch]::StartNew()
        try {
            $rec = Invoke-BdProjectVerify -Id $Id -Exe $Exe -Coverage
            foreach ($s in (Get-BdVerifySteps -Rec $rec -Manifest $m -RecordPath (Join-Path $out 'verify.json') -Seconds $t.Elapsed.TotalSeconds)) { $steps.Add($s) }
        } catch {
            $steps.Add((New-BdCheckupStep 'verify' 'FAIL' ($_.Exception.Message -split "`n")[0] $t.Elapsed.TotalSeconds))
            foreach ($c in ($cols | Select-Object -Skip 1)) { $steps.Add((New-BdCheckupStep $c 'skip' 'verify did not finish')) }
        }
    }

    # 3. compat on the installed emulators of its console
    if (-not (Get-BdProp $m 'compat' $null)) { $steps.Add((New-BdCheckupStep 'compat' 'skip' 'no compat section')) }
    elseif ($NoCompat) { $steps.Add((New-BdCheckupStep 'compat' 'skip' '-NoCompat')) }
    elseif (-not $built) { $steps.Add((New-BdCheckupStep 'compat' 'skip' 'not built')) }
    else {
        $t = [System.Diagnostics.Stopwatch]::StartNew()
        try {
            $c = Invoke-BdCompat -Project $Id
            $v = @($c.emulators | ForEach-Object { "$($_.id) $($_.verdict)" })
            $st = if (-not $c.ok) { 'FAIL' } elseif (@($c.emulators | Where-Object { $_.verdict -eq 'differs' }).Count) { 'warn' } else { 'ok' }
            $steps.Add((New-BdCheckupStep 'compat' $st ($v -join ', ') $t.Elapsed.TotalSeconds (Join-Path $out 'compat\compat.json')))
        } catch { $steps.Add((New-BdCheckupStep 'compat' 'FAIL' ($_.Exception.Message -split "`n")[0] $t.Elapsed.TotalSeconds)) }
    }

    # 4. the open bug captures: listed, never a failure (a workspace that would put them in git is one)
    try {
        $b = Get-BdBugs -Id $Id -Exe $Exe
        # (copied one by one: Windows PowerShell's @() on a generic List can throw "Argument types do not match")
        $rows = @(); foreach ($x in $b.Rows) { $rows += $x }
        $notIgnored = @(); foreach ($x in $b.Folders) { if ($x.Git -and $x.Git.State -eq 'NOT IGNORED') { $notIgnored += $x } }
        $list = @($rows | ForEach-Object { $x = $_.Bug; '{0} ({1}{2})' -f $x.id, (Get-BdProp $x 'captured' '?'), $(if (Get-BdProp $x 'note' '') { ': ' + $x.note } else { '' }) })
        $st = if ($notIgnored.Count) { 'FAIL' } elseif ($rows.Count) { 'warn' } else { 'ok' }
        $d = if ($notIgnored.Count) { $notIgnored[0].Git.Detail } elseif ($rows.Count) { "$($rows.Count) open: $(($list | Select-Object -First 3) -join '; ')" } else { 'none open' }
        $s = New-BdCheckupStep 'bugs' $st $d
        $s | Add-Member -NotePropertyName open -NotePropertyValue $list
        $steps.Add($s)
    } catch { $steps.Add((New-BdCheckupStep 'bugs' 'skip' ($_.Exception.Message -split "`n")[0])) }

    $cov = $null
    if ($built) { $vp = Join-Path $out 'verify.json'; if (Test-Path -LiteralPath $vp) { $cov = Get-BdProp (Read-BdJson $vp) 'coverage' $null } }
    [object[]]$all = $steps.ToArray()
    $nFail = @($all | Where-Object { $_.state -eq 'FAIL' }).Count
    [pscustomobject]([ordered]@{
        id = $Id; title = [string]$m.title
        ok = ($nFail -eq 0)
        steps = $all
        coveragePercent = $(if ($cov) { [double]$cov.percent } else { $null })
        seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    })
}

function Get-BdSuiteCount {
    <# "N passed, M failed" or "N of M" from a suite's output (the last one), as "passed/total"; '' when it prints neither. #>
    param([string]$Text)
    $m = [regex]::Matches($Text, '(\d+) passed, (\d+) failed')
    if ($m.Count) { $l = $m[$m.Count - 1]; $p = [int]$l.Groups[1].Value; return "$p/$($p + [int]$l.Groups[2].Value)" }
    $m = [regex]::Matches($Text, '(\d+) (?:of|/) (\d+)')
    if ($m.Count) { $l = $m[$m.Count - 1]; return "$($l.Groups[1].Value)/$($l.Groups[2].Value)" }
    ''
}

function Invoke-BdCheckupRepo {
    <# The BrokenNes repo's own suites on the Workshop checkup uses: --debug-test, --check-test, --movie-test, BrokenDev run-tests. #>
    param([Parameter(Mandatory)][string]$Exe, [string]$McpExe = '')
    $repo = Get-BdRepoRoot
    $logs = Join-Path (Get-BdCheckupDir) 'logs'
    New-Item -ItemType Directory -Force -Path $logs | Out-Null
    $suites = @(
        @{ id = 'debug-test'; file = $Exe; args = @('--debug-test') },
        @{ id = 'check-test'; file = $Exe; args = @('--check-test', '--wmsroms', (Join-Path $repo 'WideMess\testroms\bin')) },
        @{ id = 'movie-test'; file = $Exe; args = @('--movie-test') }
    )
    $ps = Find-BdExecutable -Name 'powershell.exe' -Candidates @("$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe")
    $rt = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $repo 'BrokenDev\tests\run-tests.ps1'), '-WorkshopExe', $Exe)
    if ($McpExe) { $rt += @('-McpExe', $McpExe) }
    $suites += @{ id = 'brokendev-tests'; file = $ps; args = $rt }
    foreach ($s in $suites) {
        Write-BdInfo "  suite $($s.id)"
        $r = Invoke-BdProcess -FilePath $s.file -Arguments $s.args -WorkingDirectory $repo -TimeoutSec 3600 -Env (Get-BdChildEnv) -LogFile (Join-Path $logs "$($s.id).log")
        $count = Get-BdSuiteCount $r.Output
        $st = if ($r.TimedOut) { 'FAIL' } elseif ($r.ExitCode -eq 0) { 'ok' } else { 'FAIL' }
        $d = if ($r.TimedOut) { 'timed out' } elseif ($r.ExitCode -eq 0) { "$count pass" } else { "exit $($r.ExitCode) ${count}: $((@(($r.Output -split "`r?`n") | Where-Object { $_ -match 'FAIL' }) | Select-Object -First 2) -join ' | ')" }
        $x = New-BdCheckupStep $s.id $st $d $r.Seconds (Join-Path $logs "$($s.id).log")
        $x | Add-Member -NotePropertyName count -NotePropertyValue $count
        $x
    }
}

function Compare-BdCheckup {
    <# What changed since the previous checkup: a step whose state changed, a coverage share that moved by a point or more. #>
    param($Now, $Before)
    $changes = New-Object System.Collections.Generic.List[string]
    if (-not $Before) { return @() }
    foreach ($p in @($Now.projects)) {
        $old = @($Before.projects | Where-Object { $_.id -eq $p.id }) | Select-Object -First 1
        if (-not $old) { $changes.Add("$($p.id): first checkup"); continue }
        foreach ($s in @($p.steps)) {
            $o = @($old.steps | Where-Object { $_.step -eq $s.step }) | Select-Object -First 1
            if ($o -and $o.state -ne $s.state) { $changes.Add("$($p.id) $($s.step): $($o.state) -> $($s.state)") }
        }
        if ($null -ne $p.coveragePercent -and $null -ne (Get-BdProp $old 'coveragePercent' $null) -and [math]::Abs([double]$p.coveragePercent - [double]$old.coveragePercent) -ge 1) {
            $changes.Add(('{0} coverage: {1}% -> {2}%' -f $p.id, $old.coveragePercent, $p.coveragePercent))
        }
    }
    foreach ($s in @($Now.suites)) {
        $o = @(@(Get-BdProp $Before 'suites' @()) | Where-Object { $_.step -eq $s.step }) | Select-Object -First 1
        if ($o -and ($o.state -ne $s.state -or (Get-BdProp $o 'count' '') -ne (Get-BdProp $s 'count' ''))) { $changes.Add("suite $($s.step): $($o.state) $(Get-BdProp $o 'count' '') -> $($s.state) $(Get-BdProp $s 'count' '')") }
    }
    $changes.ToArray()
}

function Write-BdCheckupSummary {
    <# The one-screen summary: a line per project and per suite, every failure named, what changed, the time. #>
    param([Parameter(Mandatory)]$Report)
    $cols = 'build', 'verify', 'findings', 'budgets', 'movies', 'port', 'compat', 'bugs', 'coverage'
    Write-Host ''
    Write-Host ('{0,-22} ' -f 'PROJECT') -NoNewline; foreach ($c in $cols) { Write-Host ('{0,-9}' -f $c.ToUpperInvariant()) -NoNewline }; Write-Host ' SECONDS'
    foreach ($p in @($Report.projects)) {
        Write-Host ('{0,-22} ' -f $p.id) -NoNewline
        foreach ($c in $cols) {
            $s = @($p.steps | Where-Object { $_.step -eq $c }) | Select-Object -First 1
            $txt = if (-not $s) { '-' } elseif ($c -eq 'coverage' -and $s.state -eq 'ok' -and $null -ne $p.coveragePercent) { '{0}%' -f $p.coveragePercent } else { $s.state }
            $col = switch ($(if ($s) { $s.state } else { 'skip' })) { 'FAIL' { 'Red' } 'warn' { 'Yellow' } 'ok' { 'Green' } default { 'DarkGray' } }
            Write-Host ('{0,-9}' -f $txt) -NoNewline -ForegroundColor $col
        }
        Write-Host (' {0}' -f $p.seconds)
    }
    foreach ($s in @($Report.suites)) {
        $col = if ($s.state -eq 'ok') { 'Green' } else { 'Red' }
        Write-Host ('{0,-22} {1,-9}{2}  ({3} s)' -f "suite $($s.step)", $s.state, $s.detail, $s.seconds) -ForegroundColor $col
    }
    if (@($Report.failures).Count) { Write-Host ''; Write-Host 'FAILURES' -ForegroundColor Red; foreach ($f in $Report.failures) { Write-BdFail $f } }
    if (@($Report.warnings).Count) { Write-Host ''; foreach ($w in $Report.warnings) { Write-BdWarn $w } }
    Write-Host ''
    if ($null -eq $Report.previousAtUtc) { Write-BdInfo 'changed since the last checkup: (no earlier checkup)' }
    elseif (@($Report.changed).Count -eq 0) { Write-BdInfo "changed since the last checkup ($($Report.previousAtUtc)): nothing" }
    else { Write-BdInfo "changed since the last checkup ($($Report.previousAtUtc)):"; foreach ($c in $Report.changed) { Write-BdInfo "  $c" } }
    $v = if ($Report.ok) { 'PASS' } else { 'FAIL' }
    $line = "checkup $v in $($Report.seconds) s; report: $(Join-Path (Get-BdCheckupDir) 'checkup.json')"
    if ($Report.ok) { Write-BdOk $line } else { Write-BdFail $line }
}

function Invoke-BdCheckup {
    <#
      'checkup [project|all|brokennes]': the projects' checkups and (all, brokennes) the repo's suites; the report in out\checkup\checkup.json
      (the previous one kept as checkup-previous.json). Returns the report (its .ok: nothing failed).
    #>
    param([string]$Target = 'all', [string]$Exe = '', [string]$McpExe = '', [switch]$NoCompat, [switch]$NoSuites, [switch]$Quiet)
    if (-not $Target) { $Target = 'all' }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    if (-not $Exe) { $Exe = Get-BdWorkshopExe }
    if (-not $Exe -or -not (Test-Path -LiteralPath $Exe)) { $e = New-Object System.Exception 'BrokenNes Workshop is not built. Run: brokendev.ps1 build-workshop (or pass -Exe)'; $e.Data['exit'] = 3; throw $e }
    $ids = if ($Target -eq 'brokennes') { @() } elseif ($Target -eq 'all') { @(Get-BdProjectIds) } else {
        if ((Get-BdProjectIds) -notcontains $Target) { throw "unknown project '$Target' (known: $((Get-BdProjectIds) -join ', '), all, brokennes)" }
        @($Target)
    }
    $projects = foreach ($id in $ids) { Write-BdInfo "== checkup $id"; Invoke-BdCheckupProject -Id $id -Exe $Exe -NoCompat:$NoCompat }
    $suites = @()
    if ($Target -in 'all', 'brokennes' -and -not $NoSuites) { Write-BdInfo '== checkup brokennes (the repo suites)'; $suites = @(Invoke-BdCheckupRepo -Exe $Exe -McpExe $McpExe) }

    $failures = New-Object System.Collections.Generic.List[string]; $warnings = New-Object System.Collections.Generic.List[string]
    foreach ($p in @($projects)) { foreach ($s in @($p.steps)) {
        if ($s.state -eq 'FAIL') { $failures.Add("$($p.id) $($s.step): $($s.detail)") } elseif ($s.state -eq 'warn') { $warnings.Add("$($p.id) $($s.step): $($s.detail)") } } }
    foreach ($s in $suites) { if ($s.state -eq 'FAIL') { $failures.Add("suite $($s.step): $($s.detail)") } }

    $dir = Get-BdCheckupDir
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $path = Join-Path $dir 'checkup.json'; $prevPath = Join-Path $dir 'checkup-previous.json'
    $before = $null
    if (Test-Path -LiteralPath $path) { try { $before = Read-BdJson $path } catch { $before = $null }; Copy-Item -LiteralPath $path -Destination $prevPath -Force }
    $report = [ordered]@{
        schema = 1; target = $Target; ranAtUtc = (Get-Date).ToUniversalTime().ToString('o'); workshop = $Exe
        ok = ($failures.Count -eq 0); noCompat = [bool]$NoCompat
        projects = @($projects); suites = @($suites)
        failures = $failures.ToArray(); warnings = $warnings.ToArray()
        previousAtUtc = $(if ($before) { [string]$before.ranAtUtc } else { $null }); changed = @()
        seconds = 0
    }
    $report.changed = @(Compare-BdCheckup -Now ([pscustomobject]$report) -Before $before)
    $report.seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    Save-BdJson -Object $report -Path $path -Depth 12
    $obj = [pscustomobject]$report
    if (-not $Quiet) { Write-BdCheckupSummary -Report $obj }
    $obj
}
