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
    param([Parameter(Mandatory)][string]$Id, [string]$Exe = '', [string]$Only = '')
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
    }
    foreach ($c in $rec.compare) { $l = "  compare {0}: {1}" -f $c.id, $(if ($c.ok) { 'ok' } else { 'FAIL ' + $c.detail }); if ($c.ok) { Write-BdOk $l } else { Write-BdFail $l } }
    if ($rec.PSObject.Properties['checks'] -and $rec.checks) { Write-BdChecks $rec.checks }
    $rec
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
