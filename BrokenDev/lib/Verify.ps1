# BrokenDev <-> BrokenNes: building the runner, verifying a built ROM, launching it. Requires Common, Tools, Archive, Manifest, Build.

Set-StrictMode -Version Latest

function Get-BdWorkshopLocationFile { Join-Path (Get-BdHome) 'workshop-location.txt' }

function Invoke-BdBuildBrokenNes {
    <#
      Builds a BrokenNes executable with 'dotnet build -c Release' into an artifacts folder OUTSIDE the repository
      (default BROKENDEV_HOME\bnbuild\<which>), checks the exit code, and returns the exe path. -Project is workshop or app.
      A stale exe is never returned: the build must exit 0 and the exe must be newer than the build start.
    #>
    param([ValidateSet('workshop', 'app')][string]$Which = 'workshop', [string]$ArtifactsPath = '')
    $dotnet = Find-BdExecutable -Name 'dotnet.exe' -Candidates @("$env:ProgramFiles\dotnet\dotnet.exe")
    if (-not $dotnet) { throw 'dotnet.exe not found (.NET 10 SDK needed)' }
    $repo = Get-BdRepoRoot
    $proj = if ($Which -eq 'workshop') { Join-Path $repo 'Workshop\BrokenNes.Workshop.csproj' } else { Join-Path $repo 'Windows\BrokenNes.Windows.csproj' }
    $asm = if ($Which -eq 'workshop') { 'BrokenNes.Workshop' } else { 'BrokenNes.Windows' }
    if (-not $ArtifactsPath) { $ArtifactsPath = Join-Path (Get-BdHome) "bnbuild\$Which" }
    New-Item -ItemType Directory -Force -Path $ArtifactsPath | Out-Null
    $log = Join-Path (Get-BdHome) "logs\build-$Which.log"
    $started = Get-Date
    Write-BdInfo "building $asm into $ArtifactsPath ..."
    $r = Invoke-BdProcess -FilePath $dotnet -Arguments @('build', $proj, '-c', 'Release', '--artifacts-path', $ArtifactsPath, '-v', 'q', '-nologo') -WorkingDirectory $repo -TimeoutSec 1800 -LogFile $log
    if ($r.TimedOut -or $r.ExitCode -ne 0) { throw "dotnet build of $asm failed (exit $($r.ExitCode)). Log: $log`n$(Select-BdTail $r.Output 25)" }
    # The desktop app builds to release_win-x64, the Workshop to release: take the newest exe under bin\<asm>\release*.
    $found = @(Get-ChildItem -Path (Join-Path $ArtifactsPath "bin\$asm") -Directory -Filter 'release*' -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName "$asm.exe" } | Where-Object { Test-Path -LiteralPath $_ } | Sort-Object { (Get-Item -LiteralPath $_).LastWriteTime } -Descending)
    if ($found.Count -eq 0) { throw "build reported success but no $asm.exe exists under $ArtifactsPath\bin\$asm" }
    $exe = $found[0]
    if ((Get-Item -LiteralPath $exe).LastWriteTime -lt $started.AddSeconds(-5)) {
        # An incremental build with no source change legitimately leaves the exe alone: accept it only if the compiler said it was up to date.
        Write-BdWarn "$asm.exe was not rewritten (an incremental build found nothing to compile)"
    }
    if ($Which -eq 'workshop') { [System.IO.File]::WriteAllText((Get-BdWorkshopLocationFile), $exe) }
    $exe
}

function Invoke-BdBuildWorkshop { param([string]$ArtifactsPath = '') Invoke-BdBuildBrokenNes -Which workshop -ArtifactsPath $ArtifactsPath }

function Get-BdChildEnv {
    @{ BROKENNES_DEV = '1'; BROKENDEV_HOME = (Get-BdHome) }
}

function Invoke-BdProjectVerify {
    <#
      Runs a built project's verify recipe through 'BrokenNes.Workshop.exe --brokendev verify' and records out\<id>\verify.json.
      Returns the parsed record (its .ok is the verdict). Throws when the prerequisites are missing (no Workshop, not built).
    #>
    param([Parameter(Mandatory)][string]$Id, [string]$Exe = '', [string]$Only = '', [switch]$Coverage)
    $m = Read-BdManifest -Id $Id
    $out = Get-BdOutDir $Id
    $bp = Join-Path $out 'build.json'
    if (-not (Test-Path -LiteralPath $bp)) { throw "project $Id has not been built (no $bp). Run: brokendev.ps1 build $Id" }
    $build = Read-BdJson $bp
    if (-not $build.ok) { throw "the last build of $Id failed; fix it before verifying" }

    if (-not $Exe) { $Exe = Get-BdWorkshopExe }
    if (-not $Exe -or -not (Test-Path -LiteralPath $Exe)) { throw 'BrokenNes Workshop is not built. Run: brokendev.ps1 build-workshop' }

    $rom = $build.outputs | Where-Object { $_.id -eq 'rom' } | Select-Object -First 1
    if (-not (Test-Path -LiteralPath $rom.published)) { throw "the built ROM is missing: $($rom.published)" }
    $vjson = Join-Path $out 'verify.json'
    if (Test-Path -LiteralPath $vjson) { Remove-Item -LiteralPath $vjson -Force }
    $timeout = [int](Get-BdProp $m.verify 'timeoutSec' 1800)
    # the budgets' trend: one entry per build (by the ROM's sha256) in out\<id>\budget-history.json; the report compares with the previous build
    $args = @('--brokendev', 'verify', $Id, '--manifests', (Get-BdProjectsDir), '--home', (Get-BdHome), '--rom', $rom.published, '--out', $vjson, '--png-dir', (Join-Path $out 'shots'), '--history', (Join-Path $out 'budget-history.json'))
    if ($Only) { $args += @('--only', $Only) }
    # the coverage map (D9): the code run in the checked scenarios, per bank (a manifest's coverage section turns it on by itself)
    if ($Coverage) { $args += @('--coverage') }
    $r = Invoke-BdProcess -FilePath $Exe -Arguments $args -TimeoutSec $timeout -Env (Get-BdChildEnv) -LogFile (Join-Path $out 'logs\verify.log')
    if ($r.TimedOut) { throw "verify of $Id timed out after $timeout s" }
    if (-not (Test-Path -LiteralPath $vjson)) { throw "verify of $Id produced no result (exit $($r.ExitCode)):`n$(Select-BdTail $r.Output 15)" }
    $rec = Read-BdJson $vjson
    # The runner's verdict is final; this wrapper only adds provenance (which Workshop produced it).
    $extra = [ordered]@{ workshop = $Exe; workshopWritten = (Get-Item -LiteralPath $Exe).LastWriteTime.ToString('o'); exitCode = $r.ExitCode; seconds = $r.Seconds }
    $obj = [ordered]@{}
    foreach ($p in $rec.PSObject.Properties) { $obj[$p.Name] = $p.Value }
    $obj['runner'] = $extra
    Save-BdJson -Object $obj -Path $vjson -Depth 20
    foreach ($sc in $rec.scenarios) {
        $chk = @($sc.checks | Where-Object { -not $_.ok })
        $isInfo = ($sc.PSObject.Properties['info'] -and $sc.info)
        $state = if ($sc.ok) { 'ok' } elseif ($isInfo) { 'INFO' } else { 'FAIL' }
        $line = '  {0,-14} {1,-6} frames={2,-6} colors={3,-3} {4,6} fps  audio-peak={5}' -f $sc.id, $state, $sc.framesRun, $sc.distinctColors, $sc.fps, $sc.audio.peak
        if ($sc.ok) { Write-BdOk $line }
        elseif ($isInfo) { Write-BdWarn ($line + '  (information scenario: recorded, does not decide the verdict)'); foreach ($c in $chk) { Write-BdWarn ("      {0}: {1}" -f $c.id, $c.detail) } }
        else { Write-BdFail $line; foreach ($c in $chk) { Write-BdFail ("      {0}: {1}" -f $c.id, $c.detail) }; if ($sc.result -ne 'ok') { Write-BdFail "      $($sc.result): $($sc.crashInfo)" } }
        # the debug port (D7): what the game said, when it said anything
        $dp = if ($sc.PSObject.Properties['debugPort']) { $sc.debugPort } else { $null }
        if ($dp -and $dp.PSObject.Properties['counts'] -and ($dp.counts.print + $dp.counts.marker + $dp.counts.assert + $dp.counts.error + @($dp.timers).Count) -gt 0) {
            $tm = (@($dp.timers | Select-Object -First 4 | ForEach-Object { '{0} {1}x avg {2} worst {3}' -f $_.name, $_.calls, $_.average, $_.worst })) -join '; '
            Write-Host ('      port: {0} print(s), {1} marker(s), {2} assert(s), {3} error(s){4}' -f $dp.counts.print, $dp.counts.marker, $dp.counts.assert, $dp.counts.error, $(if ($tm) { "; timers ($($dp.cycleUnit)): $tm" } else { '' }))
            foreach ($e in @($dp.events | Where-Object { $_.kind -in 'print', 'marker', 'assert', 'error' } | Select-Object -First 6)) { Write-Host ('        frame {0} {1}: {2}' -f $e.frame, $e.kind, $e.text) }
        }
    }
    foreach ($c in $rec.compare) { $l = "  compare {0}: {1}" -f $c.id, $(if ($c.ok) { 'ok' } else { 'FAIL ' + $c.detail }); if ($c.ok) { Write-BdOk $l } else { Write-BdFail $l } }
    if ($rec.PSObject.Properties['checks'] -and $rec.checks) { Write-BdChecks $rec.checks }
    if ($rec.PSObject.Properties['coverage'] -and $rec.coverage) {
        $cv = $rec.coverage
        $l = '  coverage: {0}% of the code bytes ran ({1} of {2}), {3} of {4} ROM symbols never ran; page: {5}' -f $cv.percent, $cv.ranBytes, $cv.codeBytes, $cv.symbols.neverRun, $cv.symbols.inRom, (Join-Path $out 'coverage.html')
        if ($cv.ok) { Write-BdOk $l } else { Write-BdFail $l }
        foreach ($p in @($cv.problems)) { Write-BdFail "      $p" }
        Write-BdCoveragePage -Id $Id -Coverage $cv -Path (Join-Path $out 'coverage.html')
    }
    $rec
}

function Write-BdCoveragePage {
    <# The coverage map's page (D9): one bar per ROM bank (run / not run / padding), the share, the check, the ROM symbols never run. #>
    param([Parameter(Mandatory)][string]$Id, [Parameter(Mandatory)]$Coverage, [Parameter(Mandatory)][string]$Path)
    $H = { param($s) [System.Net.WebUtility]::HtmlEncode([string]$s) }
    $c = $Coverage
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append("<!doctype html><html><head><meta charset=`"utf-8`"><meta name=`"viewport`" content=`"width=device-width, initial-scale=1`"><title>Coverage: $(& $H $Id)</title><style>")
    [void]$sb.Append(':root{--bg:#fff;--fg:#1d1d1f;--muted:#6b6b70;--ran:#2f7d4f;--not:#c9a227;--free:#d9d9de;--bad:#b3261e}@media (prefers-color-scheme:dark){:root{--bg:#161618;--fg:#ececef;--muted:#9a9aa2;--ran:#4fb27a;--not:#d8b545;--free:#3a3a40;--bad:#ff6b5e}}')
    [void]$sb.Append('body{background:var(--bg);color:var(--fg);font:14px/1.45 system-ui,sans-serif;margin:16px;max-width:980px}h1{font-size:20px}.muted{color:var(--muted)}.bar{display:flex;height:14px;border-radius:3px;overflow:hidden;background:var(--free);min-width:120px}.bar span{display:block}.r{background:var(--ran)}.n{background:var(--not)}table{border-collapse:collapse;width:100%}td,th{padding:3px 8px;text-align:left;border-bottom:1px solid var(--free)}.bad{color:var(--bad)}.key span{display:inline-block;width:10px;height:10px;margin:0 4px 0 12px}</style></head><body>')
    [void]$sb.Append("<h1>Coverage map: $(& $H $Id)</h1><p><b>$($c.percent)%</b> of the code bytes ran ($($c.ranBytes) of $($c.codeBytes); $($c.romBytes) ROM bytes, banks of $($c.bankSize)), over the checked scenarios: $(& $H ((@($c.scenarios)) -join ', ')).</p>")
    [void]$sb.Append('<p class="key"><span class="r"></span>ran<span class="n"></span>not run<span style="background:var(--free)"></span>padding (free)</p>')
    foreach ($p in @($c.problems)) { [void]$sb.Append("<p class=`"bad`">FAIL $(& $H $p)</p>") }
    [void]$sb.Append('<table><tr><th>Bank</th><th>Share run</th><th>Bytes run / not run / free</th><th style="width:50%"></th></tr>')
    $empty = 0
    foreach ($b in @($c.banks)) {
        if ([int]$b.ran -eq 0 -and [int]$b.free -eq [int]$b.size) { $empty++; continue }
        $size = [math]::Max(1, [int]$b.size); $not = [math]::Max(0, [int]$b.size - [int]$b.free - [int]$b.ran)
        $wr = [math]::Round(100 * [int]$b.ran / $size, 2); $wn = [math]::Round(100 * $not / $size, 2)
        [void]$sb.Append("<tr><td>$($b.bank)</td><td>$($b.percent)%</td><td>$($b.ran) / $not / $($b.free)</td><td><div class=`"bar`"><span class=`"r`" style=`"width:$wr%`"></span><span class=`"n`" style=`"width:$wn%`"></span></div></td></tr>")
    }
    [void]$sb.Append('</table>')
    if ($empty) { [void]$sb.Append("<p class=`"muted`">$empty bank(s) all padding, not listed.</p>") }
    if (@($c.mustRun).Count) {
        [void]$sb.Append('<h2>The check: must run</h2><ul>')
        foreach ($m in @($c.mustRun)) { [void]$sb.Append("<li class=`"$(if ($m.ran) { '' } else { 'bad' })`">$(& $H $m.spec) $(& $H (Get-BdProp $m 'at' '')): $(if ($m.ran) { 'ran' } else { 'NEVER RAN' })</li>") }
        [void]$sb.Append('</ul>')
    }
    [void]$sb.Append("<h2>ROM symbols never run ($($c.symbols.neverRun) of $($c.symbols.inRom))</h2><p class=`"muted`">Data labels kept in ROM and constants the symbol file places there show here too: they are never run.</p><table>")
    foreach ($s in @($c.symbols.neverRunList)) { [void]$sb.Append("<tr><td>$(& $H $s.name)</td><td>$(& $H $s.at)</td></tr>") }
    [void]$sb.Append('</table></body></html>')
    [System.IO.File]::WriteAllText($Path, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
}

function Write-BdChecks {
    <# The hardware checks, budgets and profile of a verify record (the manifest's checks / budgets sections; milestone D4). #>
    param($Checks)
    $c = $Checks
    $line = '  checks: {0} new finding(s), {1} accepted' -f $c.newFindings, $c.acceptedFindings
    if ($c.ok) { Write-BdOk $line } else { Write-BdFail $line }
    foreach ($p in @($c.problems)) { Write-BdFail "      manifest: $p" }
    foreach ($s in @($c.scenarios)) {
        $pr = $s.profile
        $l = '    {0,-14} busy {1,5:F1}% (worst {2,5:F1}% at frame {3}), {4} lag frame(s), vblank uploads worst {5:F1}%, {6:F0} fps checked' -f $s.id, $pr.busyPercentAverage, $pr.busyPercentWorst, $pr.busyWorstFrame, $pr.lagFrames, $pr.vblankUploadPercentWorst, $s.fpsChecked
        if ($s.result -ne 'ok') { Write-BdFail "$l  ($($s.result): $($s.error))" }
        elseif (-not $s.frameIdentical) { Write-BdFail "$l  ($($s.identity))" }
        else { Write-BdInfo $l }
    }
    foreach ($f in @($c.findings | Where-Object { -not $_.accepted })) {
        Write-BdFail ('      NEW {0} at {1} ({2} frame {3}, pc {4} {5}, {6}x): {7}' -f $f.rule, $f.place, $f.scenario, $f.firstFrame, $f.pc, $f.pcSymbol, $f.count, $f.detail)
    }
    foreach ($b in @($c.budgets)) {
        $trend = if ($null -ne $b.previous) { ' (previous build {0}, {1}{2})' -f $b.previous, $(if ($b.delta -ge 0) { '+' } else { '' }), $b.delta } else { '' }
        $l = '    budget {0}: {1} ({2} {3}){4} - {5}' -f $b.id, $b.value, $(if ($b.kind -eq 'max') { 'at most' } else { 'at least' }), $b.limit, $trend, $b.detail
        switch ($b.status) { 'fail' { Write-BdFail $l } 'warn' { Write-BdWarn $l } default { Write-BdOk $l } }
    }
    foreach ($a in @($c.staleAccepted)) { Write-BdWarn ("      the accepted {0} at '{1}' matched nothing in this run (still needed?)" -f $a.rule, $a.at) }
}

function Invoke-BdProjectRun {
    <#
      Launches the built ROM in BrokenNes. The desktop app has no ROM command-line argument, so this drives its own HTTP API
      (the same one the UAT harness uses): start the app with BROKENNES_DEV=1, wait for /api/health, then POST /api/emulator/load-rom.
      If the desktop app is not built (BROKENDEV_APP, or 'brokendev.ps1 build-app'), SNES ROMs open in the Workshop's own SNES player
      and the script says so; a NES ROM needs the desktop app.
    #>
    param([Parameter(Mandatory)][string]$Id)
    $m = Read-BdManifest -Id $Id
    $out = Get-BdOutDir $Id
    $bp = Join-Path $out 'build.json'
    if (-not (Test-Path -LiteralPath $bp)) { throw "project $Id has not been built. Run: brokendev.ps1 build $Id" }
    $build = Read-BdJson $bp
    $rom = $build.outputs | Where-Object { $_.id -eq 'rom' } | Select-Object -First 1
    if (-not $rom -or -not (Test-Path -LiteralPath $rom.published)) { throw "the built ROM is missing for $Id" }

    $app = $env:BROKENDEV_APP
    if (-not $app) {
        $cands = @(Get-ChildItem -Path (Join-Path (Get-BdHome) 'bnbuild\app\bin\BrokenNes.Windows') -Directory -Filter 'release*' -ErrorAction SilentlyContinue |
            ForEach-Object { Join-Path $_.FullName 'BrokenNes.Windows.exe' } | Where-Object { Test-Path -LiteralPath $_ })
        if ($cands.Count -gt 0) { $app = $cands[0] }
    }
    $env:BROKENNES_DEV = '1'
    if ($app) {
        $api = Join-Path (Get-BdRepoRoot) 'UAT\lib\ApiClient.ps1'
        . $api
        $inst = Start-BrokenNesInstance -ExePath $app
        Start-Sleep -Seconds 5   # the app finishes loading its embedded test ROM a few seconds after the API answers; a ROM loaded earlier is replaced
        [void](Show-BrokenNesEmulator -ProcessId $inst.ProcessId)
        $res = Load-BrokenNesRom -ProcessId $inst.ProcessId -Path $rom.published
        Write-BdOk "launched $Id in BrokenNes (PID $($inst.ProcessId)): $($rom.published)"
        $res | ConvertTo-Json -Depth 3 | Write-Host
        return
    }
    if ([string]$m.console -eq 'wms') {
        throw "the BrokenNes desktop app is not built (set BROKENDEV_APP, or run: brokendev.ps1 build-app). A WideMess game opens in the desktop app; without it, Workshop --wms-run / --wms-shot run it headless."
    }
    if ([string]$m.console -in 'gb', 'gbc') {
        # (the Workshop's own Game Boy player keeps its saves in the player's %APPDATA%\BrokenNes\GbSaves: not a fallback BrokenDev takes)
        throw "the BrokenNes desktop app is not built (set BROKENDEV_APP, or run: brokendev.ps1 build-app). A Game Boy game opens in the desktop app."
    }
    if ([string]$m.console -ne 'snes') {
        throw "the BrokenNes desktop app is not built (set BROKENDEV_APP, or run: brokendev.ps1 build-app). A NES ROM cannot be opened without it."
    }
    $ws = Get-BdWorkshopExe
    if (-not $ws) { throw 'neither the desktop app nor the Workshop is built. Run: brokendev.ps1 build-workshop' }
    Write-BdWarn 'desktop app not built: opening the ROM in the Workshop SNES player instead (same SNES cores)'
    Start-Process -FilePath $ws -ArgumentList @('--snes', $rom.published) | Out-Null
    Write-BdOk "launched $Id in the Workshop SNES player: $($rom.published)"
}
