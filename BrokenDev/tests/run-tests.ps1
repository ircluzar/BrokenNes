<#
.SYNOPSIS
  Tests for the BrokenDev scripts themselves. Every check is paired with a negative control: the same code is shown to FAIL
  on input that must fail (a broken manifest, a missing sentinel, a build that does not rewrite its output, a ROM that
  draws nothing), so a green run cannot be a verifier that never says no.

.DESCRIPTION
  Pure-script tests need only PowerShell, 7-Zip and Git Bash. Tests that drive BrokenNes need a built Workshop
  (BROKENDEV_WORKSHOP or 'brokendev.ps1 build-workshop'); without one they are SKIPPED, with the reason printed.
  Nothing here touches BROKENDEV_HOME: every test uses its own temporary home.

  Exit code: 0 all run tests passed (skips are listed), 1 a test failed.
#>
[CmdletBinding()]
param([string]$WorkshopExe = '', [switch]$NoWorkshop, [string]$McpExe = '')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$bd = Split-Path -Parent $here
. (Join-Path $bd 'lib\Common.ps1')
. (Join-Path $bd 'lib\Tools.ps1')
. (Join-Path $bd 'lib\Archive.ps1')
. (Join-Path $bd 'lib\Toolchains.ps1')
. (Join-Path $bd 'lib\Manifest.ps1')
. (Join-Path $bd 'lib\Build.ps1')
. (Join-Path $bd 'lib\Verify.ps1')
. (Join-Path $bd 'lib\Diff.ps1')
. (Join-Path $bd 'lib\DevTools.ps1')
. (Join-Path $bd 'lib\Emulators.ps1')
. (Join-Path $bd 'lib\Compat.ps1')
. (Join-Path $bd 'lib\WideMess.ps1')
. (Join-Path $bd 'lib\Mcp.ps1')
. (Join-Path $bd 'lib\Movies.ps1')
. (Join-Path $bd 'lib\Bugs.ps1')
. (Join-Path $bd 'lib\Checkup.ps1')

$script:pass = 0; $script:fail = 0; $script:skip = 0
$tmpRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('bdtests-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $tmpRoot | Out-Null

function Test-Case {
    param([string]$Name, [scriptblock]$Body)
    try { & $Body; $script:pass++; Write-Host "  PASS  $Name" -ForegroundColor Green }
    catch {
        if ($_.Exception.Message -like 'SKIP:*') { $script:skip++; Write-Host "  SKIP  $Name  ($($_.Exception.Message.Substring(5).Trim()))" -ForegroundColor Yellow }
        else { $script:fail++; Write-Host "  FAIL  $Name" -ForegroundColor Red; Write-Host "        $($_.Exception.Message)" -ForegroundColor Red }
    }
}
function Skip-Test { param([string]$Why) throw "SKIP: $Why" }
function Assert-True  { param($Cond, [string]$Msg = 'condition is false') if (-not $Cond) { throw $Msg } }
function Assert-Equal { param($Actual, $Expected, [string]$Msg = '') if ("$Actual" -ne "$Expected") { throw "expected '$Expected' but got '$Actual' $Msg" } }
function Assert-Throws {
    # The negative control: the call MUST throw, and the message must contain $Like.
    param([scriptblock]$Body, [string]$Like = '*')
    $threw = $false; $msg = ''
    try { & $Body } catch { $threw = $true; $msg = $_.Exception.Message }
    if (-not $threw) { throw "expected an error ($Like) but none was raised" }
    if ($msg -notlike "*$Like*") { throw "error raised, but '$msg' does not contain '$Like'" }
}

function Save-Env { param([string[]]$Names) $h = @{}; foreach ($n in $Names) { $h[$n] = [Environment]::GetEnvironmentVariable($n) }; $h }
function Restore-Env { param($Saved) foreach ($k in $Saved.Keys) { [Environment]::SetEnvironmentVariable($k, $Saved[$k]) } }
$envNames = @('BROKENDEV_HOME', 'BROKENDEV_ARCHIVES', 'BROKENDEV_PROJECTS', 'BROKENDEV_ARCHIVES_JSON', 'BROKENDEV_PYTHON', 'BROKENDEV_WORKSHOP', 'BROKENNES_DEV', 'BROKENNES_CONFIG_DIR',
              'BROKENDEV_DEVTOOLS', 'BROKENDEV_TRACE_LUA', 'BROKENDEV_LIVE_SYNTH', 'BROKENDEV_LIVE_MISSING', 'BD_TEST_TOOL_VAR', 'BROKENDEV_EMULATORS',
              'BROKENDEV_TOOLCHAINS', 'BROKENDEV_LIVE_BDTEST_GB_WS', 'BROKENDEV_LIVE_BDTEST_NO_SUCH_WS', 'BROKENNES_BUGS_DIR')
$savedEnv = Save-Env $envNames

try {
    Write-Host 'BrokenDev script tests' -ForegroundColor Cyan

    # ------------------------------------------------------------------------------------------ manifests
    Write-Host 'manifests'
    $ids = Get-BdProjectIds
    Test-Case 'the six shipped projects are present' {
        $want = 'smb1-nes', 'smb1-nested', 'vrun-nes', 'vrun-nested', 'vrun-nested-enhanced', 'supervrun'
        foreach ($w in $want) { Assert-True ($ids -contains $w) "missing project $w (have: $($ids -join ', '))" }
    }
    Test-Case 'every shipped manifest validates' {
        $aid = @{}; foreach ($k in (Get-BdArchiveTable).Keys) { $aid[$k] = $true }
        foreach ($id in $ids) {
            $m = Read-BdJson (Get-BdManifestPath $id)
            $e = @(Test-BdManifest -M $m -ExpectedId $id -ArchiveIds $aid)
            Assert-True ($e.Count -eq 0) "$id : $($e -join '; ')"
        }
    }
    $good = Read-BdJson (Get-BdManifestPath ($ids | Select-Object -First 1))
    $mut = {
        param([scriptblock]$Edit)
        $c = ($good | ConvertTo-Json -Depth 30) | ConvertFrom-Json
        & $Edit $c
        @(Test-BdManifest -M $c)
    }
    Test-Case 'NEGATIVE: a manifest missing a field is rejected' {
        $e = & $mut { param($m) $m.PSObject.Properties.Remove('verify') }
        Assert-True ($e -match "missing field 'verify'") "errors were: $($e -join '; ')"
    }
    Test-Case 'NEGATIVE: an unknown kind is rejected' {
        $e = & $mut { param($m) $m.kind = 'dreamcast' }
        Assert-True (($e -join ';') -match "kind 'dreamcast'") "errors were: $($e -join '; ')"
    }
    Test-Case 'NEGATIVE: a build step without a timeout is rejected (no unattended run without one)' {
        $e = & $mut { param($m) $m.build.steps[0].timeoutSec = 0 }
        Assert-True (($e -join ';') -match 'timeoutSec') "errors were: $($e -join '; ')"
    }
    Test-Case 'NEGATIVE: kind nes on console snes is rejected' {
        $e = & $mut { param($m) $m.kind = 'nes'; $m.console = 'snes' }
        Assert-True (($e -join ';') -match 'must run on console nes') "errors were: $($e -join '; ')"
    }
    Test-Case 'NEGATIVE: an oracle with an unknown kind is rejected' {
        $e = & $mut { param($m) $m | Add-Member -NotePropertyName oracle -NotePropertyValue ([pscustomobject]@{ kind = 'bsnes'; mesenDir = 'x'; frames = 10 }) -Force }
        Assert-True (($e -join ';') -match "oracle.kind 'bsnes'") "errors were: $($e -join '; ')"
    }
    Test-Case 'NEGATIVE: an oracle without frames is rejected' {
        $e = & $mut { param($m) $m | Add-Member -NotePropertyName oracle -NotePropertyValue ([pscustomobject]@{ kind = 'mesen-screens'; mesenDir = 'x'; mesenSettings = 'y' }) -Force }
        Assert-True (($e -join ';') -match 'oracle.frames') "errors were: $($e -join '; ')"
    }
    Test-Case 'every oracle section in a shipped manifest names a Mesen folder and a frame list' {
        foreach ($id in $ids) { $m = Read-BdJson (Get-BdManifestPath $id); $o = Get-BdProp $m 'oracle' $null; if ($o) { Assert-True ([bool](Get-BdProp $o 'mesenDir' '')) "$id has no oracle.mesenDir"; Assert-True ($null -ne (Get-BdProp $o 'frames' $null)) "$id has no oracle.frames" } }
    }
    Test-Case 'every shipped game manifest has a checks section (verify runs the hardware checks on it) and budgets' {
        foreach ($id in 'smb1-nes', 'smb1-nested', 'vrun-nes', 'vrun-nested', 'vrun-nested-enhanced', 'supervrun', 'widemess-sample') {
            $m = Read-BdJson (Get-BdManifestPath $id)
            Assert-True ($null -ne (Get-BdProp $m 'checks' $null)) "$id has no checks section"
            Assert-True ($null -ne (Get-BdProp $m 'budgets' $null)) "$id has no budgets"
            foreach ($a in @($m.checks.accepted)) { Assert-True ([string]$a.reason -match '^(known|unverified|by design|test)') "$id accepts $($a.rule) at $($a.at) without an honest reason: '$($a.reason)'" }
        }
    }
    Test-Case 'NEGATIVE: an accepted finding without a reason is rejected' {
        $e = & $mut { param($m) $m | Add-Member -NotePropertyName checks -NotePropertyValue ([pscustomobject]@{ accepted = @([pscustomobject]@{ rule = 'nes.lag-frame'; at = '*' }) }) -Force }
        Assert-True (($e -join ';') -match 'has no reason') "errors were: $($e -join '; ')"
    }
    Test-Case 'NEGATIVE: an opt-out without a reason, a checks scenario that does not exist and an unknown budget are rejected' {
        $e = & $mut { param($m) $m | Add-Member -NotePropertyName checks -NotePropertyValue ([pscustomobject]@{ off = @([pscustomobject]@{ rule = 'nes.open-bus' }); scenarios = @('nope') }) -Force
                                    $m | Add-Member -NotePropertyName budgets -NotePropertyValue ([pscustomobject]@{ speed = [pscustomobject]@{ max = 1 }; lagFrames = [pscustomobject]@{ fromFrame = 3 } }) -Force }
        $j = $e -join ';'
        Assert-True ($j -match "checks.off: 'nes.open-bus' has no reason" -and $j -match "no verify scenario 'nope'" -and $j -match "unknown budget 'speed'" -and $j -match "lagFrames needs 'max'") "errors were: $j"
    }
    Test-Case 'NEGATIVE: outputs without a rom are rejected' {
        $e = & $mut { param($m) $m.build.outputs[0].id = 'notrom' }
        Assert-True (($e -join ';') -match "id 'rom'") "errors were: $($e -join '; ')"
    }

    # ------------------------------------------------------------------------------------------ placeholders and paths
    Write-Host 'paths'
    Test-Case 'placeholders expand' {
        $ctx = @{ Roots = @{ proj = 'X:\proj' }; Out = 'X:\out\p'; python = 'X:\py.exe' }
        Assert-Equal (Expand-BdPath -Text '{root:proj}\a' -Context $ctx) 'X:\proj\a'
        Assert-Equal (Expand-BdPath -Text '{out}\b' -Context $ctx) 'X:\out\p\b'
        Assert-Equal (Expand-BdPath -Text '{python}' -Context $ctx) 'X:\py.exe'
    }
    Test-Case 'NEGATIVE: an unknown placeholder is an error, not an empty string' {
        Assert-Throws { Expand-BdPath -Text '{nope}\x' -Context @{} } 'unknown placeholder'
        Assert-Throws { Expand-BdPath -Text '{root:nope}' -Context @{ Roots = @{} } } 'unknown root'
    }
    Test-Case 'NEGATIVE: an unset {env:NAME} is an error' {
        [Environment]::SetEnvironmentVariable('BD_TEST_UNSET_VAR', $null)
        Assert-Throws { Expand-BdPath -Text '{env:BD_TEST_UNSET_VAR}' -Context @{} } 'is not set'
    }
    Test-Case 'BROKENDEV_HOME is honoured and NEGATIVE: a home inside the repository is refused' {
        $env:BROKENDEV_HOME = Join-Path $tmpRoot 'h1'
        Assert-Equal (Get-BdHome) (Join-Path $tmpRoot 'h1')
        $env:BROKENDEV_HOME = Join-Path (Get-BdRepoRoot) 'BrokenDev\scratch-home'
        Assert-Throws { Get-BdHome } 'inside the repository'
    }
    Test-Case 'ConvertTo-BdArgString quotes spaces and quotes' {
        Assert-Equal (ConvertTo-BdArgString @('a b', 'c', '')) '"a b" c ""'
    }

    # ------------------------------------------------------------------------------------------ restore
    Write-Host 'restore'
    $sevenZip = Find-Bd7Zip
    $archDir = Join-Path $tmpRoot 'archives'; $stage = Join-Path $tmpRoot 'stage'
    New-Item -ItemType Directory -Force -Path "$stage\Top\sub", "$stage\Top\skip", $archDir | Out-Null
    Set-Content "$stage\Top\keep.txt" 'keep'; Set-Content "$stage\Top\sub\sentinel.txt" 'sentinel'; Set-Content "$stage\Top\skip\big.txt" 'excluded'
    if ($sevenZip) { Push-Location $stage; & $sevenZip a -tzip (Join-Path $archDir 'synth.zip') 'Top' -bso0 -bsp0 | Out-Null; Pop-Location }
    $table = @{ schema = 1; archives = @{ synth = @{ file = 'synth.zip'; extractTo = 'synth'; stripTop = 'Top'; include = @(); exclude = @('skip\*'); roots = @{ synthroot = 'sub' }; sentinels = @('keep.txt', 'sub/sentinel.txt') };
                                             missing = @{ file = 'synth.zip'; extractTo = 'missing'; stripTop = 'Top'; include = @(); exclude = @('sub\*'); roots = @{ r = '.' }; sentinels = @('sub/sentinel.txt') } } }
    Save-BdJson -Object $table -Path (Join-Path $tmpRoot 'archives.json')
    $env:BROKENDEV_HOME = Join-Path $tmpRoot 'home'; $env:BROKENDEV_ARCHIVES = $archDir; $env:BROKENDEV_ARCHIVES_JSON = Join-Path $tmpRoot 'archives.json'
    Test-Case 'restore extracts, strips the top folder, honours exclude, writes a marker' {
        if (-not $sevenZip) { Skip-Test '7-Zip not installed' }
        $st = Restore-BdArchive -Id synth
        Assert-Equal $st.State 'restored'
        $t = Join-Path (Get-BdHome) 'synth'
        Assert-True (Test-Path "$t\keep.txt") 'keep.txt not restored'
        Assert-True (Test-Path "$t\sub\sentinel.txt") 'sentinel not restored'
        Assert-True (-not (Test-Path "$t\skip\big.txt")) 'an excluded file was extracted'
        Assert-True (-not (Test-Path "$t\Top")) 'the top folder was not stripped'
        Assert-True (Test-Path "$t\.brokendev-restore.json") 'no marker'
    }
    Test-Case 'a second restore is a no-op (the marker matches)' {
        if (-not $sevenZip) { Skip-Test '7-Zip not installed' }
        $marker = Join-Path (Get-BdHome) 'synth\.brokendev-restore.json'
        $before = (Get-Item $marker).LastWriteTimeUtc
        Start-Sleep -Milliseconds 1100
        [void](Restore-BdArchive -Id synth)
        Assert-Equal (Get-Item $marker).LastWriteTimeUtc $before
    }
    Test-Case '-Full ignores the exclude list' {
        if (-not $sevenZip) { Skip-Test '7-Zip not installed' }
        [void](Restore-BdArchive -Id synth -Full)
        Assert-True (Test-Path (Join-Path (Get-BdHome) 'synth\skip\big.txt')) '-Full did not restore the excluded file'
    }
    Test-Case 'NEGATIVE: a restore that lacks a sentinel file is reported as failed' {
        if (-not $sevenZip) { Skip-Test '7-Zip not installed' }
        Assert-Throws { Restore-BdArchive -Id missing } 'sentinel'
    }
    Test-Case 'NEGATIVE: a missing archive file is an error that names the folder to set' {
        if (-not $sevenZip) { Skip-Test '7-Zip not installed' }
        $env:BROKENDEV_ARCHIVES = Join-Path $tmpRoot 'nowhere'
        Assert-Throws { Restore-BdArchive -Id synth -Force } 'BROKENDEV_ARCHIVES'
        $env:BROKENDEV_ARCHIVES = $archDir
    }
    Test-Case 'NEGATIVE: restore state is partial without a marker' {
        if (-not $sevenZip) { Skip-Test '7-Zip not installed' }
        $spec = Get-BdArchiveSpec -Id synth
        Remove-Item (Join-Path (Get-BdHome) 'synth\.brokendev-restore.json') -Force
        Assert-Equal (Get-BdRestoreState $spec).State 'partial'
    }

    # ------------------------------------------------------------------------------------------ build runner
    Write-Host 'build runner'
    $projDir = Join-Path $tmpRoot 'projects'
    function New-SynthProject {
        param([string]$Id, [string]$Run, [int]$Timeout = 60, [string]$Kind = 'nes')
        $m = [ordered]@{
            schema = 1; id = $Id; title = "synthetic $Id"; family = 'synth'; kind = $Kind; console = 'nes'
            source = @{ archive = 'synth'; path = 'x' }; requires = @{ tools = @('gitbash'); projects = @(); inputs = @() }
            build = @{ steps = @(@{ name = 'make'; shell = 'bash'; cwd = '{root:synthroot}'; run = $Run; timeoutSec = $Timeout }); outputs = @(@{ id = 'rom'; path = '{root:synthroot}/rom.bin'; publishAs = 'rom.bin' }) }
            verify = @{ scenarios = @(@{ id = 'boot'; frames = 10 }) }; knownIssues = @()
        }
        New-Item -ItemType Directory -Force -Path (Join-Path $projDir $Id) | Out-Null
        Save-BdJson -Object $m -Path (Join-Path (Join-Path $projDir $Id) 'project.json')
    }
    $env:BROKENDEV_PROJECTS = $projDir
    $hasBash = [bool](Find-BdGitBash)
    if ($sevenZip -and $hasBash) { [void](Restore-BdArchive -Id synth -Force) }
    Test-Case 'a build runs its steps and publishes the output with hashes into out\<id>' {
        if (-not ($sevenZip -and $hasBash)) { Skip-Test '7-Zip or Git Bash missing' }
        New-SynthProject -Id 'synth-ok' -Run 'printf "ROM%s" $RANDOM > rom.bin'
        $rec = Invoke-BdProjectBuild -Id 'synth-ok'
        Assert-True $rec.ok 'record not ok'
        $pub = Join-Path (Get-BdOutDir 'synth-ok') 'rom.bin'
        Assert-True (Test-Path $pub) 'output not published'
        Assert-Equal (Get-BdSha256 $pub) $rec.outputs[0].sha256
        Assert-True (Test-Path (Join-Path (Get-BdOutDir 'synth-ok') 'build.json')) 'no build.json'
    }
    Test-Case 'NEGATIVE: a failing step fails the build and records the failure' {
        if (-not ($sevenZip -and $hasBash)) { Skip-Test '7-Zip or Git Bash missing' }
        New-SynthProject -Id 'synth-fail' -Run 'echo about to fail; exit 7'
        Assert-Throws { Invoke-BdProjectBuild -Id 'synth-fail' } "exit 7"
        $r = Read-BdJson (Join-Path (Get-BdOutDir 'synth-fail') 'build.json')
        Assert-True (-not $r.ok) 'the failed build was recorded as ok'
    }
    Test-Case 'NEGATIVE: a build that leaves the old output in place is a failure, not a pass' {
        if (-not ($sevenZip -and $hasBash)) { Skip-Test '7-Zip or Git Bash missing' }
        New-SynthProject -Id 'synth-stale' -Run 'true'   # succeeds, never touches rom.bin (which an earlier project left behind)
        Assert-Throws { Invoke-BdProjectBuild -Id 'synth-stale' } 'was not rewritten'
    }
    Test-Case 'NEGATIVE: a step that outlives its timeout is killed and fails' {
        if (-not ($sevenZip -and $hasBash)) { Skip-Test '7-Zip or Git Bash missing' }
        New-SynthProject -Id 'synth-slow' -Run 'sleep 120' -Timeout 3
        $sw = [Diagnostics.Stopwatch]::StartNew()
        Assert-Throws { Invoke-BdProjectBuild -Id 'synth-slow' } 'timed out'
        Assert-True ($sw.Elapsed.TotalSeconds -lt 60) "the timeout took $($sw.Elapsed.TotalSeconds) s"
    }
    Test-Case 'NEGATIVE: building a project whose archive is not restored is refused' {
        if (-not $hasBash) { Skip-Test 'Git Bash missing' }
        New-SynthProject -Id 'synth-unrestored' -Run 'true'
        $mp = Join-Path $projDir 'synth-unrestored\project.json'
        $j = Read-BdJson $mp; $j.source.archive = 'missing'; Save-BdJson -Object $j -Path $mp
        # 'missing' was restored in the previous section only partially (sentinel absent): the build must refuse it
        Remove-Item -LiteralPath (Join-Path (Get-BdHome) 'missing') -Recurse -Force -ErrorAction SilentlyContinue
        Assert-Throws { Invoke-BdProjectBuild -Id 'synth-unrestored' } 'restore'
    }
    Test-Case 'dry run resolves every step without executing' {
        if (-not ($sevenZip -and $hasBash)) { Skip-Test '7-Zip or Git Bash missing' }
        New-SynthProject -Id 'synth-dry' -Run 'echo should-not-run > dry.flag'
        $rec = Invoke-BdProjectBuild -Id 'synth-dry' -DryRun
        Assert-Equal @($rec.steps).Count 1
        Assert-True (-not (Test-Path (Join-Path (Get-BdHome) 'synth\sub\dry.flag'))) 'the dry run executed the step'
    }

    # ------------------------------------------------------------------------------------------ live workspaces
    Write-Host 'live workspaces'
    $liveRoot = Join-Path $tmpRoot 'liveproj'
    $liveName = 'bdlive-' + [guid]::NewGuid().ToString('N').Substring(0, 6)
    $liveWs = Join-Path $liveRoot $liveName
    $wt = Join-Path $liveRoot 'Repo\.claude\worktrees\wt'     # a git worktree's place inside a checkout
    New-Item -ItemType Directory -Force -Path (Join-Path $liveWs 'sub'), $wt | Out-Null
    Set-Content (Join-Path $liveWs 'sub\live.flag') 'live'
    $liveTable = @{ schema = 1; archives = @{ synth = @{ file = 'synth.zip'; extractTo = 'synth'; stripTop = 'Top'; include = @(); exclude = @('skip\*'); roots = @{ synthroot = 'sub'; other = '.' }; sentinels = @('keep.txt', 'sub/sentinel.txt')
                                                       live = @{ folder = $liveName; roots = @{ synthroot = 'sub' }; sentinels = @('sub/live.flag') } } } }
    Save-BdJson -Object $liveTable -Path (Join-Path $tmpRoot 'archives-live.json') -Depth 10
    $tableBefore = $env:BROKENDEV_ARCHIVES_JSON
    $env:BROKENDEV_ARCHIVES_JSON = Join-Path $tmpRoot 'archives-live.json'
    Test-Case 'a live workspace beside a parent of the repository is found (a git worktree finds the main checkout''s neighbour)' {
        $spec = Get-BdArchiveSpec -Id synth
        Assert-Equal (Find-BdLiveFolder -Spec $spec -SearchFrom $wt) $liveWs
        $w = Get-BdWorkspace -Id synth -SearchFrom $wt
        Assert-Equal $w.Kind 'live'
        Assert-Equal $w.Path $liveWs
    }
    Test-Case 'NEGATIVE: a folder with the right name but without the live sentinels is not taken for the workspace' {
        Remove-Item (Join-Path $liveWs 'sub\live.flag')
        try {
            $spec = Get-BdArchiveSpec -Id synth
            Assert-True ($null -eq (Find-BdLiveFolder -Spec $spec -SearchFrom $wt)) 'a folder without the sentinels was accepted'
            Assert-True ((Get-BdWorkspace -Id synth -SearchFrom $wt).Kind -ne 'live') 'the workspace is live without its sentinels'
        } finally { Set-Content (Join-Path $liveWs 'sub\live.flag') 'live' }
    }
    Test-Case 'BROKENDEV_LIVE_<ARCHIVE> names the live folder; the roots it maps come from it, the others from the restored copy' {
        $env:BROKENDEV_LIVE_SYNTH = $liveWs
        try {
            $w = Get-BdWorkspace -Id synth
            Assert-Equal $w.Kind 'live'
            Assert-Equal $w.Source 'BROKENDEV_LIVE_SYNTH'
            $r = Get-BdArchiveRoots -Detailed
            Assert-Equal $r['synthroot'].Path (Join-Path $liveWs 'sub')
            Assert-Equal $r['synthroot'].From 'live'
            Assert-Equal $r['other'].From 'restored'
            Assert-Equal $r['other'].Path (Join-Path (Get-BdHome) 'synth')
        } finally { $env:BROKENDEV_LIVE_SYNTH = $null }
    }
    Test-Case 'NEGATIVE: BROKENDEV_LIVE_<ARCHIVE> pointing at a folder that is not the workspace is an error, not a silent fallback' {
        $env:BROKENDEV_LIVE_SYNTH = Join-Path $tmpRoot 'archives'
        try { Assert-Throws { Get-BdWorkspace -Id synth } 'missing' } finally { $env:BROKENDEV_LIVE_SYNTH = $null }
    }
    Test-Case 'NEGATIVE: BROKENDEV_LIVE_<ARCHIVE>=off uses the restored copy even when a live workspace exists' {
        $env:BROKENDEV_LIVE_SYNTH = 'off'
        try {
            $w = Get-BdWorkspace -Id synth -SearchFrom $wt
            Assert-True ($w.Kind -ne 'live') "off still gave $($w.Kind)"
            Assert-Equal (Get-BdArchiveRoots)['synthroot'] (Join-Path (Get-BdHome) 'synth\sub')
        } finally { $env:BROKENDEV_LIVE_SYNTH = $null }
    }
    Test-Case 'a project builds in its live workspace with nothing restored; NEGATIVE: with the live workspace off and nothing restored, the build is refused' {
        if (-not $hasBash) { Skip-Test 'Git Bash missing' }
        $env:BROKENDEV_HOME = Join-Path $tmpRoot 'home-live'    # nothing is restored in this home
        $env:BROKENDEV_LIVE_SYNTH = $liveWs
        try {
            New-SynthProject -Id 'synth-live' -Run 'printf "LIVE%s" $RANDOM > rom.bin'
            $rec = Invoke-BdProjectBuild -Id 'synth-live'
            Assert-True $rec.ok 'record not ok'
            Assert-Equal $rec.workspace.kind 'live'
            Assert-True (Test-Path (Join-Path $liveWs 'sub\rom.bin')) 'the build did not run in the live workspace'
            $env:BROKENDEV_LIVE_SYNTH = 'off'
            Assert-Throws { Invoke-BdProjectBuild -Id 'synth-live' } 'restore'
        } finally { $env:BROKENDEV_LIVE_SYNTH = $null; $env:BROKENDEV_HOME = Join-Path $tmpRoot 'home' }
    }
    Test-Case 'the shipped archives.json live sections validate; NEGATIVE: a live root that is not an archive root is rejected' {
        $env:BROKENDEV_ARCHIVES_JSON = $null
        $e = @(Test-BdArchiveLive -Table (Get-BdArchiveTable))
        Assert-True ($e.Count -eq 0) ($e -join '; ')
        $env:BROKENDEV_ARCHIVES_JSON = Join-Path $tmpRoot 'archives-live.json'
        $t = Get-BdArchiveTable
        $t['synth'].live.roots | Add-Member -NotePropertyName nosuchroot -NotePropertyValue 'x'
        Assert-True ((@(Test-BdArchiveLive -Table $t) -join ';') -match 'nosuchroot') 'an unknown live root was accepted'
    }
    $env:BROKENDEV_ARCHIVES_JSON = $tableBefore

    # ------------------------------------------------------------------------------------------ developer tools (devtools.json)
    Write-Host 'developer tools'
    Test-Case 'the shipped devtools.json validates against the shipped projects' {
        $saveP = $env:BROKENDEV_PROJECTS; $env:BROKENDEV_PROJECTS = $null; $env:BROKENDEV_DEVTOOLS = $null
        try { $e = @(Test-BdDevtools -J (Read-BdJson (Get-BdDevtoolsPath)) -ProjectIds (Get-BdProjectIds)); Assert-True ($e.Count -eq 0) ($e -join '; ') }
        finally { $env:BROKENDEV_PROJECTS = $saveP }
    }
    $dtGood = Read-BdJson (Join-Path $bd 'devtools.json')
    $dtMut = { param([scriptblock]$Edit) $c = ($dtGood | ConvertTo-Json -Depth 30) | ConvertFrom-Json; & $Edit $c; @(Test-BdDevtools -J $c -ProjectIds @('vrun-nes', 'vrun-nested', 'supervrun')) }
    Test-Case 'NEGATIVE: a server without a port, two servers on one port, an unknown project, a script without a timeout are rejected' {
        $e = & $dtMut { param($j) ($j.tools | Where-Object { $_.kind -eq 'server' } | Select-Object -First 1).PSObject.Properties.Remove('port') }
        Assert-True (($e -join ';') -match "needs its 'port'") ($e -join '; ')
        $e = & $dtMut { param($j) $s = @($j.tools | Where-Object { $_.kind -eq 'server' }); $s[1].port = $s[0].port }
        Assert-True (($e -join ';') -match 'also the port') ($e -join '; ')
        $e = & $dtMut { param($j) $j.tools[0].project = 'nes-zelda' }
        Assert-True (($e -join ';') -match 'not a BrokenDev project') ($e -join '; ')
        $e = & $dtMut { param($j) ($j.tools | Where-Object { $_.kind -eq 'script' } | Select-Object -First 1).timeoutSec = 0 }
        Assert-True (($e -join ';') -match 'timeoutSec') ($e -join '; ')
    }
    Test-Case 'every shipped tool finds its folder and scripts in the live workspace; NEGATIVE: a tool naming a missing script is reported' {
        $saveP = $env:BROKENDEV_PROJECTS; $saveA = $env:BROKENDEV_ARCHIVES_JSON
        $env:BROKENDEV_PROJECTS = $null; $env:BROKENDEV_ARCHIVES_JSON = $null
        try {
            $live = @((Get-BdArchiveTable).Keys | Where-Object { (Get-BdWorkspace -Id $_).Kind -eq 'live' })
            if ($live.Count -eq 0) { Skip-Test 'no live workspace beside the repository on this machine' }
            $bad = @(); $checked = 0
            foreach ($t in (Read-BdDevtools)) {
                $arch = [string](Read-BdManifest -Id ([string]$t.project)).source.archive
                if ((Get-BdWorkspace -Id $arch).Kind -ne 'live') { continue }
                $checked++
                $m = @(Test-BdToolFiles -Tool $t -Ctx (New-BdToolContext $t)); if ($m.Count) { $bad += "$($t.id): $($m -join ', ')" }
            }
            Assert-True ($bad.Count -eq 0) ($bad -join ' | ')
            Assert-True ($checked -ge 10) "only $checked tools were checked"
            $t0 = (Read-BdDevtools)[0] | ConvertTo-Json -Depth 10 | ConvertFrom-Json
            $t0.run = @('{python}', 'tools/no_such_tool.py')
            Assert-True ((@(Test-BdToolFiles -Tool $t0 -Ctx (New-BdToolContext $t0)) -join ';') -match 'no_such_tool') 'a missing script was not reported'
        } finally { $env:BROKENDEV_PROJECTS = $saveP; $env:BROKENDEV_ARCHIVES_JSON = $saveA }
    }
    $toolDir = Join-Path $tmpRoot 'toolsrv'
    New-Item -ItemType Directory -Force -Path $toolDir | Out-Null
    Set-Content (Join-Path $toolDir 'index.html') '<html><body>bd-synthetic-tool</body></html>'
    function Get-FreePort { $l = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0); $l.Start(); $p = $l.LocalEndpoint.Port; $l.Stop(); $p }
    $srvPort = Get-FreePort; $busyPort = Get-FreePort
    while ($busyPort -eq $srvPort) { $busyPort = Get-FreePort }
    $synthTools = @{ schema = 1; tools = @(
        @{ id = 'synth-server'; title = 'synthetic server'; project = 'synth-ok'; edits = 'nothing'; kind = 'server'; cwd = $toolDir; run = @('{python}', '-m', 'http.server', [string]$srvPort, '--bind', '127.0.0.1'); port = $srvPort; probe = '/index.html'; probeText = 'bd-synthetic-tool'; from = 'test' },
        @{ id = 'synth-busy'; title = 'synthetic server on a taken port'; project = 'synth-ok'; edits = 'nothing'; kind = 'server'; cwd = $toolDir; run = @('{python}', '-m', 'http.server', [string]$busyPort, '--bind', '127.0.0.1'); port = $busyPort; probe = '/index.html'; probeText = 'bd-synthetic-tool'; from = 'test' },
        @{ id = 'synth-script'; title = 'synthetic script'; project = 'synth-ok'; edits = 'nothing'; kind = 'script'; cwd = $toolDir; run = @('{bash}', '-c', 'echo "var=$BD_TEST_TOOL_VAR args=$*"; exit "$1"', 'x'); env = @{ BD_TEST_TOOL_VAR = 'from-entry' }; timeoutSec = 60; from = 'test' },
        @{ id = 'synth-page'; title = 'synthetic page'; project = 'synth-ok'; edits = 'nothing'; kind = 'page'; cwd = $toolDir; run = @('{bash}', '-c', 'mkdir -p "$1" && echo "<html>page</html>" > "$1/index.html"', 'x', '{work}'); open = '{work}/index.html'; timeoutSec = 60; from = 'test' },
        @{ id = 'synth-nopage'; title = 'synthetic page that writes nothing'; project = 'synth-ok'; edits = 'nothing'; kind = 'page'; cwd = $toolDir; run = @('{bash}', '-c', 'true'); open = '{work}/never.html'; timeoutSec = 60; from = 'test' },
        @{ id = 'synth-okexit'; title = 'synthetic script whose exit 1 means done'; project = 'synth-ok'; edits = 'nothing'; kind = 'script'; cwd = $toolDir; run = @('{bash}', '-c', 'exit "$1"', 'x'); okExitCodes = @(1); timeoutSec = 60; from = 'test' },
        @{ id = 'synth-pymod'; title = 'synthetic tool that needs numpy'; project = 'synth-ok'; edits = 'nothing'; kind = 'script'; cwd = $toolDir; run = @('{python}', '-c', 'import numpy'); pythonModules = @('numpy'); timeoutSec = 60; from = 'test' },
        @{ id = 'synth-nomod'; title = 'synthetic tool that needs a module no Python has'; project = 'synth-ok'; edits = 'nothing'; kind = 'script'; cwd = $toolDir; run = @('{python}', '-c', 'pass'); pythonModules = @('bd_no_such_module'); timeoutSec = 60; from = 'test' }
    ) }
    Save-BdJson -Object $synthTools -Path (Join-Path $tmpRoot 'devtools.json') -Depth 10
    $env:BROKENDEV_DEVTOOLS = Join-Path $tmpRoot 'devtools.json'
    $toolsReady = ($sevenZip -and $hasBash -and (Test-Path (Join-Path $projDir 'synth-ok\project.json')))
    function Invoke-ExpectedToolFailure {
        # A negative control's call: its own 'FAIL  tool ...: step N exited X' line is the expected outcome, not a failed test, so it is
        # captured (not printed, where it reads like one) and returned with the exit code for the test to assert
        param([scriptblock]$Call)
        $all = @(& $Call 6>&1)
        [pscustomobject]@{ Code = @($all | Where-Object { $_ -is [int] })[-1]; Said = (@($all | Where-Object { $_ -isnot [int] } | ForEach-Object { [string]$_ }) -join "`n") }
    }
    Test-Case 'a server tool starts hidden, answers its probe, is reused when started again, and stops by the PID BrokenDev recorded' {
        if (-not ($toolsReady -and (Find-BdPython))) { Skip-Test 'Python, 7-Zip or Git Bash missing' }
        $t = Get-BdTool -Id 'synth-server'; $ctx = New-BdToolContext $t
        $r = Start-BdServerTool -Tool $t -Ctx $ctx
        try {
            Assert-True (-not $r.Reused) 'the first start was reported as a reuse'
            Assert-True (Test-BdToolProbe $t) 'the server does not answer its probe'
            $r2 = Start-BdServerTool -Tool $t -Ctx $ctx
            Assert-True $r2.Reused 'a second start did not reuse the running server'
        } finally { [void](Stop-BdServerTool $t) }
        Start-Sleep -Milliseconds 800
        Assert-True (-not (Test-BdPortOpen -Port $srvPort)) 'the server still listens after Stop'
    }
    Test-Case 'NEGATIVE: a tool whose port is taken by another program is refused, and that program is left running' {
        if (-not ($toolsReady -and (Find-BdPython))) { Skip-Test 'Python, 7-Zip or Git Bash missing' }
        $l = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $busyPort); $l.Start()
        try {
            $t = Get-BdTool -Id 'synth-busy'
            Assert-Throws { Start-BdServerTool -Tool $t -Ctx (New-BdToolContext $t) } 'taken by'
            Assert-True (Test-BdPortOpen -Port $busyPort) 'the program on the port was stopped'
        } finally { $l.Stop() }
    }
    Test-Case 'a script tool runs in its folder with its env defaults and extra arguments; NEGATIVE: a failing script returns its failure' {
        if (-not $toolsReady) { Skip-Test '7-Zip or Git Bash missing' }
        $t = Get-BdTool -Id 'synth-script'; $ctx = New-BdToolContext $t
        Assert-Equal (Invoke-BdScriptTool -Tool $t -Ctx $ctx -Extra @('0')) 0
        $log = [System.IO.File]::ReadAllText((Join-Path $ctx['work'] 'run.log'))
        Assert-True ($log -match 'var=from-entry args=0') "the entry's env default or the argument did not reach the script: $log"
        $env:BD_TEST_TOOL_VAR = 'from-caller'
        try {
            [void](Invoke-BdScriptTool -Tool $t -Ctx $ctx -Extra @('0'))
            Assert-True ([System.IO.File]::ReadAllText((Join-Path $ctx['work'] 'run.log')) -match 'var=from-caller') 'the caller''s own value did not win'
        } finally { $env:BD_TEST_TOOL_VAR = $null }
        $x = Invoke-ExpectedToolFailure { Invoke-BdScriptTool -Tool $t -Ctx $ctx -Extra @('3') }
        Assert-Equal $x.Code 3; Assert-True ($x.Said -match 'synth-script: step 1 exited 3') "the failure was not reported: $($x.Said)"
    }
    Test-Case 'a page tool writes and offers its page; NEGATIVE: a page tool whose steps write nothing fails' {
        if (-not $toolsReady) { Skip-Test '7-Zip or Git Bash missing' }
        Assert-Equal (Invoke-BdTool -Id 'synth-page' -NoBrowser) 0
        Assert-True (Test-Path (Join-Path (Get-BdHome) 'work\tools\synth-page\index.html')) 'no page was written'
        Assert-Throws { Invoke-BdTool -Id 'synth-nopage' -NoBrowser } 'did not write'
    }
    Test-Case 'an exit code a tool declares as done counts as success; NEGATIVE: any other non-zero exit still fails' {
        if (-not $toolsReady) { Skip-Test '7-Zip or Git Bash missing' }
        $t = Get-BdTool -Id 'synth-okexit'; $ctx = New-BdToolContext $t
        Assert-Equal (Invoke-BdScriptTool -Tool $t -Ctx $ctx -Extra @('1')) 0
        $x = Invoke-ExpectedToolFailure { Invoke-BdScriptTool -Tool $t -Ctx $ctx -Extra @('2') }
        Assert-Equal $x.Code 2; Assert-True ($x.Said -match 'synth-okexit: step 1 exited 2') "the failure was not reported: $($x.Said)"
    }
    Test-Case 'a tool that needs Python modules gets a Python that has them; NEGATIVE: a module no Python has is refused with its name' {
        if (-not ($toolsReady -and (Find-BdPython))) { Skip-Test 'Python, 7-Zip or Git Bash missing' }
        Assert-Equal (Invoke-BdTool -Id 'synth-pymod') 0
        Assert-Throws { Invoke-BdTool -Id 'synth-nomod' } 'bd_no_such_module'
    }
    $env:BROKENDEV_DEVTOOLS = $null
    Test-Case 'diff uses BrokenDev''s copy of the user''s Mesen tracer, NEGATIVE: unless BROKENDEV_TRACE_LUA names another; the 20 sys0 scripts are all there' {
        $env:BROKENDEV_TRACE_LUA = $null
        Assert-Equal (Get-BdTraceLua) (Join-Path $bd 'tools\vrun\mesen\state_trace.lua')
        Assert-True (Test-Path (Get-BdTraceLua)) 'the copy is missing'
        $env:BROKENDEV_TRACE_LUA = 'X:\elsewhere\state_trace.lua'
        try { Assert-Equal (Get-BdTraceLua) 'X:\elsewhere\state_trace.lua' } finally { $env:BROKENDEV_TRACE_LUA = $null }
        $names = @(Get-ChildItem (Join-Path $bd 'tools\vrun\mesen') -File | ForEach-Object { $_.Name })
        Assert-Equal $names.Count 20 ("files: " + ($names -join ', '))
    }
    Test-Case 'the Nested Builder state of the old machine is repaired (Mesen, destinations, recent ROMs); NEGATIVE: a Mesen that exists is kept' {
        $nb = Join-Path $tmpRoot 'nb'; $pipe = Join-Path $nb 'pipe'; $vr = Join-Path $nb 'vrun'
        New-Item -ItemType Directory -Force -Path (Join-Path $pipe 'out\ui'), (Join-Path $vr 'tools\mesen'), (Join-Path $vr 'build') | Out-Null
        Set-Content (Join-Path $vr 'tools\mesen\Mesen.exe') 'x'; Set-Content (Join-Path $vr 'build\vrun_game.nes') 'x'
        $old = @{ recent = @(@{ path = 'C:\PROJECTS\sys0\simulations\VRUN\build\vrun_game.nes'; name = 'vrun_game.nes'; ts = 1 }); mesen_exe = 'C:\PROJECTS\sys0\simulations\VRUN\tools\mesen\Mesen.exe'
                  dest_by_rom = @{ 'c:\users\user\desktop\vrun_game.nes' = 'C:\Users\user\Desktop\Nested_VRUN\x.smc' }
                  dest_by_name = @{ 'vrun_game.nes' = 'C:\Users\user\Desktop\Nested_VRUN\x.smc'; 'keep.nes' = (Join-Path $nb 'keep.smc') } }
        $sp = Join-Path $pipe 'out\ui\state.json'
        Save-BdJson -Object $old -Path $sp
        $ctx = @{ Roots = @{ m30pipe = $pipe; vrun = $vr } }
        Update-BdNestedBuilderState -Ctx $ctx
        $st = Read-BdJson $sp
        Assert-Equal $st.mesen_exe (Join-Path $vr 'tools\mesen\Mesen.exe')
        Assert-True ($null -eq $st.dest_by_name.PSObject.Properties['vrun_game.nes']) 'a destination in a folder that does not exist was kept'
        Assert-True ($null -ne $st.dest_by_name.PSObject.Properties['keep.nes']) 'a valid destination was dropped'
        Assert-Equal @($st.recent)[0].path (Join-Path $vr 'build\vrun_game.nes')
        Assert-True (Test-Path "$sp.oldmachine") 'the original state was not kept'
        $mine = Join-Path $nb 'MyMesen.exe'; Set-Content $mine 'x'
        $st.mesen_exe = $mine; Save-BdJson -Object $st -Path $sp
        Update-BdNestedBuilderState -Ctx $ctx
        Assert-Equal (Read-BdJson $sp).mesen_exe $mine
    }
    Test-Case 'the SFX configurator lists every trigger call site with its clip; NEGATIVE: a renderer copy that differs from the game is flagged on the page' {
        $spy = Find-BdPython; if (-not $spy) { Skip-Test 'no Python with pillow and numpy' }
        $sv = Join-Path $tmpRoot 'sfxvrun'
        New-Item -ItemType Directory -Force -Path "$sv\src\game", "$sv\engine\audio", "$sv\tools" | Out-Null
        [System.IO.File]::WriteAllText("$sv\src\game\main.fab", "fn foo()`n    trigger_sfx_sq2_bleep()`nfn damage_player()`n    trigger_hit_sfx()`nmode main()`n    if paused`n        trigger_fire_sfx()`n    // trigger_sfx_noise_boom() in a comment is not a call`n")
        [System.IO.File]::WriteAllText("$sv\engine\audio\audio_engine.fab", "data /music`n    [] sfx_fire`n        U[3](1, 2, 255)`n    [] sfx_hit`n        U[2](3, 255)`n    [] sfx_bleep`n        U[2](4, 255)`n")
        $zeros = ((1..128 | ForEach-Object { '0' }) -join ', ')
        [System.IO.File]::WriteAllText("$sv\src\game\music_data.fab", "ct U[128] PERIOD_LO = U[128]($zeros)`nct U[128] PERIOD_HI = U[128]($zeros)`n")
        $stub = { param($bleep) "import argparse, wave`nSQ2_CLIPS = {'fire': [1, 2, 255], 'bleep': $bleep}`nNOISE_CLIPS = {'hit': [3, 255]}`nPERIOD_LO = [0] * 128`nPERIOD_HI = [0] * 128`ndef main():`n    ap = argparse.ArgumentParser(); ap.add_argument('--out-dir', required=True); a = ap.parse_args()`n    for fam, t in (('sq2', SQ2_CLIPS), ('noise', NOISE_CLIPS)):`n        for n in t:`n            w = wave.open(a.out_dir + '/' + fam + '_' + n + '.wav', 'wb'); w.setnchannels(1); w.setsampwidth(2); w.setframerate(44100); w.writeframes(b'\x00\x10' * 441); w.close()`nif __name__ == '__main__':`n    main()`n" }
        [System.IO.File]::WriteAllText("$sv\tools\render_sfx_wav.py", (& $stub '[4, 255]'))
        $gen = Join-Path $bd 'tools\vrun\sfx-configurator\generate.py'
        $out = Join-Path $tmpRoot 'sfxout'
        $r = Invoke-BdProcess -FilePath $spy.Path -Arguments @($gen, '--vrun', $sv, '--out', $out) -TimeoutSec 120
        Assert-Equal $r.ExitCode 0 $r.Output
        $j = Read-BdJson (Join-Path $out 'events.json')
        Assert-Equal @($j.sites).Count 3 ('sites: ' + (@($j.sites | ForEach-Object { "$($_.fn):$($_.line)" }) -join ', '))
        $hurt = @($j.events | Where-Object { $_.id -eq 'player_hurt' })[0]
        Assert-True ($hurt -and $hurt.current -eq 'noise_hit' -and $hurt.loc -eq 'main.fab:4 in damage_player()') "player_hurt row: $($hurt | ConvertTo-Json -Compress)"
        Assert-Equal @($j.warnings).Count 0 ($j.warnings -join '; ')
        $page = [System.IO.File]::ReadAllText((Join-Path $out 'index.html'))
        Assert-True ($page -notmatch '/\*__|__N_CLIPS__|__WARNINGS__|__FOOTER__') 'a placeholder was left in the page'
        Assert-True ($page -match 'function buildConfigJson') 'the export code of the August page is gone'
        [System.IO.File]::WriteAllText("$sv\tools\render_sfx_wav.py", (& $stub '[5, 255]'))
        $r = Invoke-BdProcess -FilePath $spy.Path -Arguments @($gen, '--vrun', $sv, '--out', $out) -TimeoutSec 120
        $j = Read-BdJson (Join-Path $out 'events.json')
        Assert-True ((@($j.warnings) -join ' ') -match 'sq2_bleep') "no warning for the stale clip: $($j.warnings -join '; ')"
        Assert-True ([System.IO.File]::ReadAllText((Join-Path $out 'index.html')) -match 'Check before trusting the sounds') 'the page does not show the warning'
    }

    # ------------------------------------------------------------------------------------------ tools and doctor
    Write-Host 'tools and doctor'
    Test-Case 'Python discovery requires pillow AND numpy; NEGATIVE: an interpreter without them is refused' {
        $bare = Get-BdPythonCandidates | Where-Object { $i = Get-BdPythonInfo -Path $_; $i -and ($i.Modules['PIL'] -eq 'MISSING' -or $i.Modules['numpy'] -eq 'MISSING') } | Select-Object -First 1
        if (-not $bare) { Skip-Test 'every Python on this machine has pillow and numpy, so there is no negative control to point at' }
        $env:BROKENDEV_PYTHON = $bare
        Assert-True ($null -eq (Find-BdPython)) 'Find-BdPython accepted an interpreter lacking pillow/numpy'
        $c = Test-BdPython
        Assert-True (-not $c.Ok) 'Test-BdPython passed for an interpreter lacking pillow/numpy'
        $env:BROKENDEV_PYTHON = $null
    }
    Test-Case 'doctor -Json reports every required check and exits 0 on a healthy machine' {
        $env:BROKENDEV_HOME = Join-Path $tmpRoot 'doctorhome'
        $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $bd 'doctor.ps1') -Json -SkipProjects | Out-String
        $j = $out | ConvertFrom-Json
        $names = @($j.checks | ForEach-Object { $_.Name })
        foreach ($need in 'Git Bash', 'Python 3.10+ with pillow and numpy', 'Node.js 20+', '.NET 10 SDK', 'Windows PowerShell 5.1', '7-Zip', 'Disk space for BROKENDEV_HOME') {
            Assert-True ($names -contains $need) "doctor does not check '$need'"
        }
        Assert-True $j.ok ('doctor failed on this machine: ' + (($j.checks | Where-Object { $_.Required -and -not $_.Ok } | ForEach-Object { $_.Name }) -join ', '))
    }
    Test-Case 'NEGATIVE: doctor exits 1 when a required tool is missing (Python forced to a nonexistent path)' {
        $env:BROKENDEV_HOME = Join-Path $tmpRoot 'doctorhome'; $env:BROKENDEV_PYTHON = (Join-Path $tmpRoot 'no-such-python.exe')
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $bd 'doctor.ps1') -Json -SkipProjects | Out-Null
        Assert-Equal $LASTEXITCODE 1
        $env:BROKENDEV_PYTHON = $null
    }
    Test-Case 'NEGATIVE: doctor exits 1 when BROKENDEV_HOME is inside the repository' {
        $env:BROKENDEV_HOME = Join-Path (Get-BdRepoRoot) 'BrokenDev\inside'
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $bd 'doctor.ps1') -Json -SkipProjects | Out-Null
        Assert-Equal $LASTEXITCODE 1
    }
    Test-Case 'the repository stays light: no ROM, archive or binary is tracked under BrokenDev' {
        $files = Get-ChildItem -LiteralPath $bd -Recurse -File | Where-Object { $_.FullName -notmatch '\\\.git\\' }
        $bad = @($files | Where-Object { $_.Extension -in '.nes', '.smc', '.sfc', '.zip', '.7z', '.exe', '.dll', '.bin' -or $_.Length -gt 1MB })
        Assert-True ($bad.Count -eq 0) ("heavy or forbidden files under BrokenDev: " + (($bad | ForEach-Object { $_.Name }) -join ', '))
    }

    # ------------------------------------------------------------------------------------------ the NES differential gate
    Write-Host 'differential gate'
    . (Join-Path $bd 'lib\TraceGate.ps1')
    $gateDir = Join-Path $tmpRoot 'gate'
    New-Item -ItemType Directory -Force -Path $gateDir | Out-Null
    function New-GateTrace {
        # 40 frames of: frame|fbhash|p1per|w|pc ; $Mutate maps frame -> hashtable of column overrides
        param([string]$Name, [hashtable]$Mutate = @{})
        $lines = @('# columns: frame|fbhash|p1per|w|pc')
        for ($f = 0; $f -lt 40; $f++) {
            $row = @{ fbhash = ('{0:x4}' -f (100 + $f)); p1per = '07e'; w = '0'; pc = '8000' }
            if ($Mutate.ContainsKey($f)) { foreach ($k in $Mutate[$f].Keys) { $row[$k] = $Mutate[$f][$k] } }
            $lines += ('{0}|{1}|{2}|{3}|{4}' -f $f, $row.fbhash, $row.p1per, $row.w, $row.pc)
        }
        $p = Join-Path $gateDir $Name
        [System.IO.File]::WriteAllText($p, ($lines -join "`n") + "`n")
        $p
    }
    $gA = New-GateTrace 'a.trace'
    Test-Case 'gate: identical traces pass' {
        $g = Get-BdTraceGate -MesenTrace $gA -BrokenNesTrace (New-GateTrace 'same.trace')
        Assert-True $g.Pass ($g.GateFailures -join '; ')
        Assert-Equal $g.BlipFrames 0
    }
    Test-Case 'gate: one isolated APU blip passes, and is LISTED with its column and frame' {
        $g = Get-BdTraceGate -MesenTrace $gA -BrokenNesTrace (New-GateTrace 'blip.trace' @{ 20 = @{ p1per = '12e' } })
        Assert-True $g.Pass ($g.GateFailures -join '; ')
        Assert-Equal $g.BlipFrames 1
        Assert-Equal (@($g.Blips['p1per'])[0]) 20
    }
    Test-Case 'NEGATIVE: an APU difference that lasts two frames is a failure, not a blip' {
        $g = Get-BdTraceGate -MesenTrace $gA -BrokenNesTrace (New-GateTrace 'two.trace' @{ 20 = @{ p1per = '12e' }; 21 = @{ p1per = '12e' } })
        Assert-True (-not $g.Pass) 'a two-frame APU divergence passed'
    }
    Test-Case 'NEGATIVE: a single-frame PICTURE difference after the boot window fails (no blip allowance for pictures)' {
        $g = Get-BdTraceGate -MesenTrace $gA -BrokenNesTrace (New-GateTrace 'pic.trace' @{ 20 = @{ fbhash = 'dead' } })
        Assert-True (-not $g.Pass) 'a one-frame picture difference passed'
        Assert-True ((($g.GateFailures -join ' ') -match 'fbhash')) ($g.GateFailures -join '; ')
    }
    Test-Case 'NEGATIVE: more isolated blips than the budget fails' {
        $mut = @{}; foreach ($f in 12, 15, 18, 21, 24, 27, 30) { $mut[$f] = @{ p1per = '12e' } }
        $g = Get-BdTraceGate -MesenTrace $gA -BrokenNesTrace (New-GateTrace 'many.trace' $mut) -BlipLimit 5
        Assert-True (-not $g.Pass) 'seven blips passed with a budget of five'
        Assert-True (($g.GateFailures -join ' ') -match 'budget') ($g.GateFailures -join '; ')
    }
    Test-Case 'sample-skew columns (pc, w) never fail the gate; the boot window is listed, not failed' {
        $g = Get-BdTraceGate -MesenTrace $gA -BrokenNesTrace (New-GateTrace 'skew.trace' @{ 20 = @{ pc = '8123'; w = '1' }; 21 = @{ pc = '8124' }; 3 = @{ fbhash = 'beef' } })
        Assert-True $g.Pass ($g.GateFailures -join '; ')
        Assert-True (($g.BootSkew -join ' ') -match 'fbhash@3') ($g.BootSkew -join '; ')
    }
    Test-Case 'NEGATIVE: a trace file without a columns header is refused' {
        $p = Join-Path $gateDir 'nohdr.trace'; [System.IO.File]::WriteAllText($p, "0|1|2`n")
        Assert-Throws { Get-BdTraceGate -MesenTrace $p -BrokenNesTrace $gA } 'columns'
    }

    # ------------------------------------------------------------------------------------------ music tools (decoder model)
    Write-Host 'music tools'
    $musicDir = Join-Path $tmpRoot 'music'
    New-Item -ItemType Directory -Force -Path $musicDir | Out-Null
    $modelPy = Join-Path $bd 'tools\music\decoder_model.py'
    $fabText = "ct U[128] PERIOD_LO = U[128](" + ((1..128 | ForEach-Object { 100 }) -join ', ') + ")`n" +
               "ct U[128] PERIOD_HI = U[128](" + ((1..128 | ForEach-Object { 1 }) -join ', ') + ")`n" +
               "data /music_t`n    [] music_t_square1`n        U[5](60, 204, 130, 128, 255)`n" +
               "    [] music_t_square2`n        U[2](128, 255)`n    [] music_t_triangle`n        U[2](128, 255)`n    [] music_t_noise`n        U[2](128, 255)`n"
    [System.IO.File]::WriteAllText((Join-Path $musicDir 't.fab'), $fabText)
    # Hand-worked expectation for square1 (stream: note 60, volume 12, wait 2, note off, loop; the vol variable starts at 15):
    #   row0 = state before the decode; then per frame: note (vol 15, period 0x164), volume 12, wait, wait, note off (vol 0), loop, note (vol 12), volume 12
    $sq1 = @(@('000', '0'), @('164', 'f'), @('164', 'c'), @('164', 'c'), @('164', 'c'), @('164', '0'), @('164', '0'), @('164', 'c'), @('164', 'c'))
    function New-ModelTrace {
        param([string]$Path, [int]$BadRow = -1)
        $lines = @('# columns: frame|p1per|p1vol|p2per|p2vol|tper|tlin|nper|nvol')
        for ($i = 0; $i -lt $sq1.Count; $i++) {
            $per = $sq1[$i][0]; if ($i -eq $BadRow) { $per = '165' }
            $lines += ('{0}|{1}|{2}|0|0|0|0|0|0' -f $i, $per, $sq1[$i][1])
        }
        [System.IO.File]::WriteAllText($Path, ($lines -join "`n") + "`n")
    }
    $mpy = Find-BdPython
    $mskip = if ($mpy) { '' } else { 'no Python with pillow and numpy' }
    New-ModelTrace -Path (Join-Path $musicDir 'good.trace')
    New-ModelTrace -Path (Join-Path $musicDir 'bad.trace') -BadRow 5
    function Invoke-ModelOn { param([string]$Trace, [string[]]$More) Invoke-BdProcess -FilePath $mpy.Path -Arguments (@('-I', $modelPy, (Join-Path $musicDir 't.fab'), (Join-Path $musicDir $Trace), '--song', 't', '--frames', '8') + $More) -TimeoutSec 60 }
    Test-Case 'decoder model: a trace made by hand from the opcode rules is matched (exit 0)' {
        if (-not $mpy) { Skip-Test $mskip }
        $r = Invoke-ModelOn -Trace 'good.trace' -More @('--start', '1')
        Assert-Equal $r.ExitCode 0 $r.Output
        Assert-True ($r.Output -match 'MODEL MATCHES') $r.Output
    }
    Test-Case 'NEGATIVE: one wrong period in the trace makes the model fail (exit 1)' {
        if (-not $mpy) { Skip-Test $mskip }
        $r = Invoke-ModelOn -Trace 'bad.trace' -More @('--start', '1')
        Assert-Equal $r.ExitCode 1 $r.Output
    }
    Test-Case 'NEGATIVE: the model one frame out of step with the trace fails (exit 1)' {
        if (-not $mpy) { Skip-Test $mskip }
        $r = Invoke-ModelOn -Trace 'good.trace' -More @('--start', '2')
        Assert-Equal $r.ExitCode 1 $r.Output
    }
    Test-Case 'NEGATIVE: a trace file without a columns header is refused, not scored' {
        if (-not $mpy) { Skip-Test $mskip }
        [System.IO.File]::WriteAllText((Join-Path $musicDir 'nohdr.trace'), "0|164|f`n")
        $r = Invoke-ModelOn -Trace 'nohdr.trace' -More @('--start', '1')
        Assert-True ($r.ExitCode -ne 0) 'a file with no # columns: header was accepted'
    }

    # ------------------------------------------------------------------------------------------ emulators (registry, install)
    Write-Host 'emulators'
    $env:BROKENDEV_HOME = Join-Path $tmpRoot 'emuhome'
    $env:BROKENDEV_EMULATORS = $null
    $emuGood = Read-BdJson (Join-Path $bd 'emulators.json')
    $emuMut = { param([scriptblock]$Edit) $c = ($emuGood | ConvertTo-Json -Depth 30) | ConvertFrom-Json; & $Edit $c; @(Test-BdEmulators -J $c) }
    Test-Case 'the shipped emulators.json validates: six emulators, BrokenNes the one reference, four pinned packages' {
        $e = @(Test-BdEmulators -J $emuGood); Assert-True ($e.Count -eq 0) ($e -join '; ')
        $ids = @($emuGood.emulators | ForEach-Object { $_.id })
        foreach ($w in 'brokennes', 'mesen', 'bizhawk-neshawk', 'fceux', 'snes9x', 'bizhawk-bsnes') { Assert-True ($ids -contains $w) "missing emulator $w" }
        Assert-Equal @($emuGood.packages).Count 4
        Assert-Equal (@($emuGood.emulators | Where-Object { Get-BdProp $_ 'reference' $false }) | ForEach-Object { $_.id }) 'brokennes'
    }
    Test-Case 'NEGATIVE: a download URL outside the pinned release locations, a bad sha256, a missing package, two references, a bizhawk id without its core are rejected' {
        $e = & $emuMut { param($j) $j.packages[0].url = 'https://example.com/releases/Mesen_2.1.1_Windows.zip' }
        Assert-True (($e -join ';') -match 'not one of the pinned release locations') ($e -join '; ')
        $e = & $emuMut { param($j) $j.packages[1].url = 'https://github.com/someone/BizHawk/releases/download/2.11.1/BizHawk-2.11.1-win-x64.zip' }
        Assert-True (($e -join ';') -match 'not one of the pinned release locations') 'a fork on github.com passed for the official repository'
        $e = & $emuMut { param($j) $j.packages[0].sha256 = 'abc' }
        Assert-True (($e -join ';') -match 'sha256') ($e -join '; ')
        $e = & $emuMut { param($j) ($j.emulators | Where-Object { $_.id -eq 'fceux' }).package = 'nestopia' }
        Assert-True (($e -join ';') -match "package 'nestopia' is not in packages") ($e -join '; ')
        $e = & $emuMut { param($j) ($j.emulators | Where-Object { $_.id -eq 'brokennes' }) | Add-Member -NotePropertyName x -NotePropertyValue 1; ($j.emulators | Where-Object { $_.id -eq 'mesen' }) | Add-Member -NotePropertyName reference -NotePropertyValue $true -Force }
        Assert-True (($e -join ';') -match 'reference') ($e -join '; ')
        $e = & $emuMut { param($j) ($j.emulators | Where-Object { $_.id -eq 'bizhawk-bsnes' }).PSObject.Properties.Remove('core') }
        Assert-True (($e -join ';') -match "needs 'core'") ($e -join '; ')
    }
    # a synthetic package: a tiny zip with an exe-named file, at a URL of the allowed form (never downloaded: -NoDownload)
    $zstage = Join-Path $tmpRoot 'zstage'; New-Item -ItemType Directory -Force -Path (Join-Path $zstage 'Top') | Out-Null
    Set-Content (Join-Path $zstage 'Top\fakeemu.exe') 'not a real program'
    $dlDir = Join-Path $env:BROKENDEV_HOME 'emulators\_downloads'; New-Item -ItemType Directory -Force -Path $dlDir | Out-Null
    $zipPath = Join-Path $dlDir 'fakeemu-1.0.zip'
    if ($sevenZip) { Push-Location $zstage; & $sevenZip a -tzip $zipPath 'Top' -bso0 -bsp0 | Out-Null; Pop-Location }
    $fakePkg = [pscustomobject]@{ id = 'fakeemu'; version = '1.0'; url = 'https://github.com/TASEmulators/fceux/releases/download/x/fakeemu-1.0.zip'; file = 'fakeemu-1.0.zip'; exe = 'fakeemu.exe'
                                  bytes = $(if (Test-Path $zipPath) { (Get-Item $zipPath).Length } else { 1 }); sha256 = $(if (Test-Path $zipPath) { Get-BdSha256 $zipPath } else { '0' * 64 }) }
    Test-Case 'install reuses a zip already in _downloads (size and sha256 pinned), unpacks it portable into <id>-<version> and marks it installed' {
        if (-not $sevenZip) { Skip-Test '7-Zip not installed' }
        $st = Install-BdEmuPackage -Package $fakePkg -NoDownload
        Assert-Equal $st.State 'installed'
        Assert-True (Test-Path (Join-Path $env:BROKENDEV_HOME 'emulators\fakeemu-1.0\fakeemu.exe')) 'the exe is not where the registry says'
        Assert-True (Test-Path (Join-Path $env:BROKENDEV_HOME 'emulators\fakeemu-1.0\.brokendev-install.json')) 'no install marker'
    }
    Test-Case 'NEGATIVE: a zip in _downloads with the wrong sha256 is refused, kept as it is, and nothing is installed' {
        if (-not $sevenZip) { Skip-Test '7-Zip not installed' }
        $bad = $fakePkg.PSObject.Copy(); $bad.version = '1.1'; $bad.sha256 = ('f' * 64)
        Assert-Throws { Install-BdEmuPackage -Package $bad -NoDownload } 'does not match the pinned release'
        Assert-True (Test-Path $zipPath) 'the mismatching zip was deleted'
        Assert-Equal (Get-BdEmuPackageState $bad).State 'absent'
    }
    Test-Case 'NEGATIVE: a package URL that is not a pinned release location is refused before any download' {
        $bad = $fakePkg.PSObject.Copy(); $bad.url = 'https://example.com/fakeemu-1.0.zip'; $bad.file = 'other.zip'
        Assert-Throws { Get-BdEmuZip -Package $bad } 'not a pinned release location'
    }
    Test-Case 'NEGATIVE: a zip without the exe the registry names fails the install and leaves no installed marker' {
        if (-not $sevenZip) { Skip-Test '7-Zip not installed' }
        $bad = $fakePkg.PSObject.Copy(); $bad.version = '1.2'; $bad.exe = 'missing.exe'
        Assert-Throws { Install-BdEmuPackage -Package $bad -NoDownload } 'missing.exe'
        Assert-True ((Get-BdEmuPackageState $bad).State -ne 'installed') 'a broken install looks installed'
    }
    Test-Case 'NEGATIVE: an installed folder whose marker names another zip is partial, not installed' {
        if (-not $sevenZip) { Skip-Test '7-Zip not installed' }
        $other = $fakePkg.PSObject.Copy(); $other.sha256 = ('e' * 64)
        Assert-Equal (Get-BdEmuPackageState $other).State 'partial'
    }

    # ------------------------------------------------------------------------------------------ compat (spec, result, checks, verdicts)
    Write-Host 'compat'
    $runSpec = @{ Console = 'nes'; Rom = 'C:\r o m\g.nes'; Out = 'C:\out dir'; SaveDir = 'C:\saves'; Frames = 300
                  Reads = @(@{ Name = 'app_state'; Domain = 'ram'; Addr = 0x3D8 }, @{ Name = 'nmi'; Domain = 'ram'; Addr = 0xA }); Input = '120:Start,132:,200:Right+A'; Shots = @(100, 300) }
    Test-Case 'a spec round-trips (paths with spaces, buttons, reads, pictures)' {
        $t = New-BdCompatSpecText -Run $runSpec
        $s = Read-BdCompatSpec -Text $t
        Assert-Equal $s.Rom 'C:\r o m\g.nes'; Assert-Equal $s.Frames 300; Assert-Equal @($s.Reads).Count 2
        Assert-Equal $s.Reads[0].Addr 0x3D8; Assert-Equal @($s.Inputs).Count 3; Assert-Equal (@($s.Inputs[2].Buttons) -join '+') 'Right+A'
        Assert-Equal @($s.Inputs[1].Buttons).Count 0; Assert-Equal (@($s.Shots) -join ',') '100,300'
    }
    Test-Case 'NEGATIVE: a spec that an adapter could misread is never written (wram on a NES, a SNES button on a NES, a picture after the last frame)' {
        $x = $runSpec.Clone(); $x.Reads = @(@{ Name = 'a'; Domain = 'wram'; Addr = 1 })
        Assert-Throws { New-BdCompatSpecText -Run $x } 'does not belong'
        $x = $runSpec.Clone(); $x.Input = '10:Y'
        Assert-Throws { New-BdCompatSpecText -Run $x } "'Y' is not a nes button"
        $x = $runSpec.Clone(); $x.Shots = @(301)
        Assert-Throws { New-BdCompatSpecText -Run $x } 'outside'
    }
    function New-ResultText {
        # app_state 0 until frame $GameAt then 1; nmi counts every frame (or freezes from $FreezeAt); shots at 100 and $Frames
        param([int]$Frames = 300, [int]$GameAt = 130, [int]$FreezeAt = 0, [string]$End = 'done')
        $l = @('# bdcompat-result 1', 'emu synthetic', 'reads app_state nmi')
        for ($f = 1; $f -le $Frames; $f++) { $n = if ($FreezeAt -gt 0 -and $f -ge $FreezeAt) { $FreezeAt % 256 } else { $f % 256 }; $l += "t $f $(if ($f -ge $GameAt) { 1 } else { 0 }) $n" }
        $l += 'shot 100 shot_00100.png'; $l += 'audio 30 1'
        if ($End) { $l += "end $End $Frames" }
        $l -join "`n"
    }
    Test-Case 'a result parses: reads, a value per frame, pictures, sound windows, the end' {
        $r = ConvertFrom-BdCompatResult -Text (New-ResultText)
        Assert-Equal @($r.Errors).Count 0 ($r.Errors -join '; ')
        Assert-Equal (@($r.Reads) -join ',') 'app_state,nmi'; Assert-Equal $r.Frames.Count 300; Assert-Equal $r.Values[130][0] 1
        Assert-Equal $r.Shots[100] 'shot_00100.png'; Assert-Equal $r.Audio[30] 1; Assert-Equal $r.End.State 'done'; Assert-Equal $r.LastFrame 300
    }
    Test-Case 'NEGATIVE: a result with a missing value, a non-number, a frame going backwards, no header or an unknown end state is reported, never scored as complete' {
        foreach ($bad in @("# bdcompat-result 1`nreads a b`nt 1 5", "# bdcompat-result 1`nreads a`nt 1 x", "# bdcompat-result 1`nreads a`nt 2 1`nt 1 1", "reads a`nt 1 1", "# bdcompat-result 1`nend exploded 5", "# bdcompat-result 1`nreads a`nt 1 300")) {
            $r = ConvertFrom-BdCompatResult -Text $bad
            Assert-True (@($r.Errors).Count -gt 0) "accepted: $($bad -replace "`n", ' | ')"
        }
    }
    $canAll = [pscustomobject]@{ memory = $true; pictures = $true }
    $canPic = [pscustomobject]@{ memory = $false; pictures = $true }
    function Invoke-Chk { param($Check, $Res, $Can = $canAll, [hashtable]$Prior = @{}, [hashtable]$Shots = @{}) Test-BdCompatCheck -Check ([pscustomobject]$Check) -Result $Res -Shots $Shots -Can $Can -Prior $Prior }
    $goodRes = ConvertFrom-BdCompatResult -Text (New-ResultText)
    Test-Case 'checks: a state reached in its window passes with its first frame; NEGATIVE: the same state outside the window fails' {
        $c = Invoke-Chk @{ id = 'game'; what = 'x'; read = 'app_state'; is = 1; within = @(121, 200) } $goodRes
        Assert-Equal $c.Status 'pass'; Assert-Equal $c.FirstFrame 130
        $c = Invoke-Chk @{ id = 'game'; what = 'x'; read = 'app_state'; is = 1; within = @(10, 120) } $goodRes
        Assert-Equal $c.Status 'fail'
    }
    Test-Case 'checks: hold passes when the value holds on every frame; NEGATIVE: one frame off fails and names it' {
        Assert-Equal (Invoke-Chk @{ id = 't'; what = 'x'; read = 'app_state'; is = 0; within = @(1, 129); hold = $true } $goodRes).Status 'pass'
        $c = Invoke-Chk @{ id = 't'; what = 'x'; read = 'app_state'; is = 0; within = @(1, 130); hold = $true } $goodRes
        Assert-Equal $c.Status 'fail'; Assert-True ($c.Detail -match 'frame 130') $c.Detail
    }
    Test-Case 'checks: a heartbeat passes while the game runs; NEGATIVE: a game frozen from frame 200 fails it (a hang inside an emulator that keeps drawing)' {
        Assert-Equal (Invoke-Chk @{ id = 'alive'; what = 'x'; changes = 'nmi'; within = @(200, 300); min = 90 } $goodRes).Status 'pass'
        $frozen = ConvertFrom-BdCompatResult -Text (New-ResultText -FreezeAt 200)
        Assert-Equal (Invoke-Chk @{ id = 'alive'; what = 'x'; changes = 'nmi'; within = @(200, 300); min = 90 } $frozen).Status 'fail'
    }
    Test-Case 'checks: after makes a window relative to an earlier check; NEGATIVE: after a check that never happened fails' {
        $c = Invoke-Chk @{ id = 'loaded'; what = 'x'; read = 'app_state'; is = 1; after = 'game'; within = @(-5, 5) } $goodRes -Prior @{ game = 130 }
        Assert-Equal $c.Status 'pass'; Assert-Equal $c.FirstFrame 130
        $c = Invoke-Chk @{ id = 'loaded'; what = 'x'; read = 'app_state'; is = 1; after = 'game'; within = @(-5, 5) } $goodRes -Prior @{ game = $null }
        Assert-Equal $c.Status 'fail'
    }
    Test-Case 'checks: memory checks are n/a on an emulator that cannot read memory (never a silent pass); a soft check turns a failure into a difference' {
        Assert-Equal (Invoke-Chk @{ id = 'g'; what = 'x'; read = 'app_state'; is = 1; within = @(121, 200) } $goodRes -Can $canPic).Status 'n/a'
        Assert-Equal (Invoke-Chk @{ id = 'g'; what = 'x'; read = 'app_state'; is = 7; within = @(121, 200); soft = $true } $goodRes).Status 'differs'
    }
    Test-Case 'checks: a picture with enough colours passes; NEGATIVE: too few colours, or no picture at that frame, fails' {
        $shots = @{ 100 = [pscustomobject]@{ Colors = 7; Hash = 'A' }; 300 = [pscustomobject]@{ Colors = 1; Hash = 'B' } }
        Assert-Equal (Invoke-Chk @{ id = 'p'; what = 'x'; picture = 100; minColors = 4 } $goodRes -Shots $shots).Status 'pass'
        Assert-Equal (Invoke-Chk @{ id = 'p'; what = 'x'; picture = 300; minColors = 4 } $goodRes -Shots $shots).Status 'fail'
        Assert-Equal (Invoke-Chk @{ id = 'p'; what = 'x'; picture = 200; minColors = 4 } $goodRes -Shots $shots).Status 'fail'
    }
    function New-ScRec { param([string]$Id = 'play', [string]$State = 'done', $Checks) [pscustomobject]@{ Id = $Id; End = [pscustomobject]@{ State = $State; Frame = 300; Text = '' }; Checks = @($Checks) } }
    function New-Chk { param([string]$Id, [string]$Status = 'pass', $First = $null, [bool]$Compare = $false) [pscustomobject]@{ Id = $Id; What = $Id; Status = $Status; Detail = 'd'; FirstFrame = $First; Compare = $Compare } }
    Test-Case 'verdicts: works when every check passes and compared states are within the tolerance' {
        $v = Get-BdCompatVerdict -Records @(New-ScRec -Checks @((New-Chk 'title'), (New-Chk 'game' pass 140 $true))) -RefFirst @{ 'play/game' = 127 } -Tolerance 30
        Assert-Equal $v.Verdict 'works' ($v.Why -join '; ')
    }
    Test-Case 'NEGATIVE verdicts: a planted failed check fails; a hung run fails; a state 40 frames late differs; a run where nothing applies cannot be tested' {
        Assert-Equal (Get-BdCompatVerdict -Records @(New-ScRec -Checks @((New-Chk 'title'), (New-Chk 'game' fail)))).Verdict 'fails'
        Assert-Equal (Get-BdCompatVerdict -Records @(New-ScRec -State 'hung' -Checks @((New-Chk 'title')))).Verdict 'fails'
        $v = Get-BdCompatVerdict -Records @(New-ScRec -Checks @((New-Chk 'game' pass 167 $true))) -RefFirst @{ 'play/game' = 127 } -Tolerance 30
        Assert-Equal $v.Verdict 'differs'; Assert-True (($v.Why -join ' ') -match '40 frames') ($v.Why -join '; ')
        Assert-Equal (Get-BdCompatVerdict -Records @(New-ScRec -Checks @((New-Chk 'game' 'n/a')))).Verdict 'cannot be tested'
        Assert-Equal (Get-BdCompatVerdict -NotRun 'not installed').Verdict 'cannot be tested'
        Assert-Equal (Get-BdCompatVerdict -Records @(New-ScRec -Checks @((New-Chk 'stays' 'differs')))).Verdict 'differs'
    }
    Test-Case 'symbols: a NESFab .mlb and a WLA-DX .sym are read; SNES addresses map to work RAM; NEGATIVE: a ROM address is not work RAM, an unknown symbol is an error' {
        $mlb = Join-Path $tmpRoot 'g.mlb'; [System.IO.File]::WriteAllText($mlb, "NesPrgRom:07CF6B:code@31_1:`nR:03D8:app_state@0:`nR:0330:player_x@1:`nR:0397:player_x@0:`n")
        $t = Read-BdSymbols -Path $mlb
        Assert-Equal $t['app_state'] 0x3D8; Assert-Equal $t['player_x'] 0x397; Assert-Equal $t['player_x@1'] 0x330
        $sym = Join-Path $tmpRoot 'g.sym'; [System.IO.File]::WriteAllText($sym, "; header`n007ec9f0 cur_state_seen`n00000037 snes_vblank_count`n00988000 code`n")
        $s = Read-BdSymbols -Path $sym
        Assert-Equal (ConvertTo-BdWramOffset -Address $s['cur_state_seen']) 0xC9F0
        Assert-Equal (ConvertTo-BdWramOffset -Address $s['snes_vblank_count']) 0x37
        Assert-Throws { ConvertTo-BdWramOffset -Address $s['code'] } 'not in SNES work RAM'
        $sec = [pscustomobject]@{ symbols = [pscustomobject]@{ file = $mlb; nesRamInWram = 0 }; reads = [pscustomobject]@{ a = 'app_state' } }
        Assert-Equal (@(Resolve-BdCompatReads -Compat $sec -Console 'snes')[0].Addr) 0x3D8
        $sec.reads = [pscustomobject]@{ a = 'no_such_variable' }
        Assert-Throws { Resolve-BdCompatReads -Compat $sec -Console 'nes' } 'no_such_variable'
    }
    Test-Case 'NEGATIVE: compat sections are validated (a check on an unknown read, a scenario with no checks, after naming a later check)' {
        $sec = [pscustomobject]@{ symbols = [pscustomobject]@{ file = 'x.mlb' }; reads = [pscustomobject]@{ a = 'a' }; scenarios = @(
            [pscustomobject]@{ id = 's'; frames = 10; checks = @([pscustomobject]@{ id = 'c'; what = 'w'; read = 'b'; is = 1; within = @(1, 2) }) },
            [pscustomobject]@{ id = 't'; frames = 10; checks = @() },
            [pscustomobject]@{ id = 'u'; frames = 10; checks = @([pscustomobject]@{ id = 'c1'; what = 'w'; read = 'a'; is = 1; within = @(1, 2); after = 'c2' }, [pscustomobject]@{ id = 'c2'; what = 'w'; read = 'a'; is = 1; within = @(1, 2) }) }) }
        $e = (Test-BdCompatSection -Compat $sec) -join ' | '
        Assert-True ($e -match "'b' is not one of compat.reads") $e
        Assert-True ($e -match 'checks is empty') $e
        Assert-True ($e -match "after 'c2' must name an earlier check") $e
    }
    Test-Case 'Mesen''s uninitialised-read lines become addresses with their symbols (NES $XXXX, SNES $BBXXXX; section markers are not names)' {
        $u = @(Get-BdUninitialisedReads -Output "x`n[CPU] Uninitialized memory read: `$05E0`n[CPU] Uninitialized memory read: `$05E2`n[CPU] Uninitialized memory read: `$05E0" -Symbols @{ redraw_backlog = 0x5E0 })
        Assert-Equal $u.Count 2; Assert-Equal $u[1].Symbol 'redraw_backlog+2'
        $s = @(Get-BdUninitialisedReads -Output '[CPU] Uninitialized memory read: $7EF4F2' -Symbols @{ players_n = 0x7EF4F2; 'SECTIONSTART_.x' = 0x7EF4F0 } -Console snes)
        Assert-Equal $s[0].Addr '$7EF4F2'; Assert-Equal $s[0].Symbol 'players_n'
    }
    Test-Case 'play saves belong to one build: a new ROM moves them aside (kept); NEGATIVE: the same ROM keeps them' {
        $pd = Join-Path $tmpRoot 'play\saves'
        [void](Set-BdPlaySaves -SaveDir $pd -RomSha256 'aaaa1111')
        Set-Content (Join-Path $pd 'game.sav') 'old build'
        Assert-True ($null -eq (Set-BdPlaySaves -SaveDir $pd -RomSha256 'aaaa1111')) 'the same ROM moved the saves'
        Assert-True (Test-Path (Join-Path $pd 'game.sav')) 'the same ROM lost its save'
        $aside = Set-BdPlaySaves -SaveDir $pd -RomSha256 'bbbb2222'
        Assert-True ($aside -and (Test-Path (Join-Path $aside 'game.sav'))) 'the old save was not kept aside'
        Assert-True (-not (Test-Path (Join-Path $pd 'game.sav'))) 'a new build still sees the old save'
    }
    Test-Case 'pictures: the same picture in another palette is structurally the same; NEGATIVE: a different picture is not; a BMP becomes a PNG' {
        Add-Type -AssemblyName System.Drawing
        function New-Pic { param([string]$Path, [scriptblock]$Color) $b = New-Object System.Drawing.Bitmap 32, 30; for ($y = 0; $y -lt 30; $y++) { for ($x = 0; $x -lt 32; $x++) { $b.SetPixel($x, $y, (& $Color $x $y)) } }; $fmt = if ($Path -match '\.bmp$') { [System.Drawing.Imaging.ImageFormat]::Bmp } else { [System.Drawing.Imaging.ImageFormat]::Png }; $b.Save($Path, $fmt); $b.Dispose() }
        $a = Join-Path $tmpRoot 'pa.png'; $b2 = Join-Path $tmpRoot 'pb.bmp'; $c = Join-Path $tmpRoot 'pc.png'
        New-Pic $a { param($x, $y) if ($x -lt 16) { [System.Drawing.Color]::FromArgb(10, 20, 30) } else { [System.Drawing.Color]::FromArgb(200, 0, 0) } }
        New-Pic $b2 { param($x, $y) if ($x -lt 16) { [System.Drawing.Color]::FromArgb(0, 0, 0) } else { [System.Drawing.Color]::FromArgb(250, 30, 30) } }
        New-Pic $c { param($x, $y) if ((($x -shr 2) + ($y -shr 2)) % 2) { [System.Drawing.Color]::FromArgb(10, 20, 30) } else { [System.Drawing.Color]::FromArgb(200, 0, 0) } }
        $sa = Get-BdPictureStats -Path $a; $sb2 = Get-BdPictureStats -Path $b2
        Assert-Equal $sa.Colors 2; Assert-True ($sb2.Path -match '\.png$' -and (Test-Path $sb2.Path) -and -not (Test-Path $b2)) 'the BMP was not converted to a PNG'
        Assert-Equal (Get-BdPictureSimilarity -A $a -B $sb2.Path) 1
        Assert-True ((Get-BdPictureSimilarity -A $a -B $c) -lt 0.8) 'a different picture scored as similar'
    }
    Test-Case 'a watched emulator that keeps writing its result runs to the end; NEGATIVE: one that stops writing is ended as hung, one that outlives its limit as timed out' {
        $rf = Join-Path $tmpRoot 'watch-result.txt'
        $psx = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $writer = "for (`$i = 0; `$i -lt 8; `$i++) { Add-Content -LiteralPath '$rf' 'x'; Start-Sleep -Milliseconds 500 }"
        $r = Invoke-BdWatchedProcess -FilePath $psx -Arguments @('-NoProfile', '-Command', $writer) -ResultFile $rf -TimeoutSec 60 -HangSec 3
        Assert-True (-not $r.Hung -and -not $r.TimedOut) "a writing process was stopped (hung=$($r.Hung) timeout=$($r.TimedOut))"
        $r = Invoke-BdWatchedProcess -FilePath $psx -Arguments @('-NoProfile', '-Command', 'Start-Sleep -Seconds 60') -ResultFile (Join-Path $tmpRoot 'never.txt') -TimeoutSec 60 -HangSec 3
        Assert-True $r.Hung 'a silent process was not ended as hung'; Assert-True ($r.Seconds -lt 30) "the hang took $($r.Seconds) s to see"
        $r = Invoke-BdWatchedProcess -FilePath $psx -Arguments @('-NoProfile', '-Command', $writer.Replace('-lt 8', '-lt 40')) -ResultFile (Join-Path $tmpRoot 'slow.txt') -TimeoutSec 3 -HangSec 30
        Assert-True $r.TimedOut 'a process past its limit was not ended as timed out'
    }
    Test-Case 'a Windows PowerShell 5.1 step started from PowerShell 7 finds its own modules; NEGATIVE: with PS7''s module path forced on it, it does not' {
        $psx = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $f = Join-Path $tmpRoot 'hash-me.txt'; Set-Content -LiteralPath $f 'x'
        $cmd = "(Get-FileHash -LiteralPath '$f' -Algorithm SHA256).Hash"
        $r = Invoke-BdProcess -FilePath $psx -Arguments @('-NoProfile', '-Command', $cmd) -TimeoutSec 60 -Quiet
        Assert-True ($r.ExitCode -eq 0 -and $r.Output -match '[0-9A-F]{64}') "Get-FileHash failed in the 5.1 child: $($r.Output)"
        if ($PSVersionTable.PSEdition -eq 'Core') {
            $bad = Invoke-BdProcess -FilePath $psx -Arguments @('-NoProfile', '-Command', $cmd) -TimeoutSec 60 -Quiet -Env @{ PSModulePath = $env:PSModulePath }
            Assert-True ($bad.Output -notmatch '[0-9A-F]{64}') 'with PowerShell 7''s module path the 5.1 child still hashed: the negative control proves nothing'
        }
    }
    Test-Case 'every adapter file loads and defines its run and play functions' {
        foreach ($ad in 'workshop', 'mesen', 'bizhawk', 'fceux', 'snes9x') {
            $le = Get-BdAdapterLoadError -Adapter $ad
            Assert-True (-not $le) "adapter $ad does not load: $le"
            Assert-True ([bool](Get-BdAdapterFunction -Adapter $ad)) "adapter $ad has no Invoke-BdAdapter function"
            if ($ad -ne 'workshop') { Assert-True ([bool](Get-BdAdapterFunction -Adapter $ad -Prefix 'Start-BdPlay')) "adapter $ad has no Start-BdPlay function" }
        }
    }

    # ------------------------------------------------------------------------------------------ the Game Boy (D2: GBDK-2020, vrun-voc)
    Write-Host 'game boy'
    Restore-Env $savedEnv
    $tcGood = Read-BdJson (Join-Path $bd 'toolchains.json')
    $tcMut = { param([scriptblock]$Edit) $c = ($tcGood | ConvertTo-Json -Depth 30) | ConvertFrom-Json; & $Edit $c; @(Test-BdToolchains -J $c) }
    Test-Case 'the shipped toolchains.json validates: GBDK-2020 4.5.0 pinned by its zip''s size and sha256 and its key files; NEGATIVE: a bad sha256, a path out of BROKENDEV_HOME, no pinned files are rejected' {
        $e = @(Test-BdToolchains -J $tcGood); Assert-True ($e.Count -eq 0) ($e -join '; ')
        $g = @($tcGood.toolchains | Where-Object { $_.id -eq 'gbdk' })
        Assert-Equal $g.Count 1; Assert-Equal $g[0].version '4.5.0'; Assert-Equal $g[0].sha256 '266854ce92e3064871c5b28cd3436cc2a6cb136af9e7cf617140108f8c1c5890'
        Assert-True (@($g[0].files | Where-Object { $_.path -eq 'bin/lcc.exe' }).Count -eq 1) 'lcc.exe is not pinned'
        $c = @($tcGood.toolchains | Where-Object { $_.id -eq 'cc65' })
        Assert-Equal $c.Count 1; Assert-Equal $c[0].sha256 '0c645591bbc5ec1c5d79bfd11723b84b9e24cba6c831ee0d4c559b106b29c24c'
        Assert-True (@($c[0].files | Where-Object { $_.path -eq 'bin/ca65.exe' -or $_.path -eq 'bin/ld65.exe' }).Count -eq 2) 'ca65.exe / ld65.exe are not pinned'
        $e = & $tcMut { param($j) $j.toolchains[0].sha256 = 'abc' }; Assert-True (($e -join ';') -match 'sha256 must be') ($e -join '; ')
        $e = & $tcMut { param($j) $j.toolchains[0].dir = '..\elsewhere' }; Assert-True (($e -join ';') -match 'inside BROKENDEV_HOME') ($e -join '; ')
        $e = & $tcMut { param($j) $j.toolchains[0].files = @() }; Assert-True (($e -join ';') -match 'files is empty') ($e -join '; ')
    }
    Test-Case 'a toolchain whose zip and pinned files match is ready and {toolchain:id} names its folder; NEGATIVE: a changed file is ''changed'', a missing folder ''missing'', and both refuse {toolchain:id} and the build' {
        $h = Join-Path $tmpRoot 'tchome'; $env:BROKENDEV_HOME = $h
        $dir = Join-Path $h 'toolchains\fake\1.0'; New-Item -ItemType Directory -Force -Path (Join-Path $dir 'bin'), (Join-Path $h 'toolchains\fake\_dl') | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $dir 'bin\cc.exe'), 'a compiler'); [System.IO.File]::WriteAllText((Join-Path $dir 'VERSION'), "fake-1.0`n")
        $zip = Join-Path $h 'toolchains\fake\_dl\fake-1.0.zip'; [System.IO.File]::WriteAllText($zip, 'the release zip')
        $reg = [ordered]@{ schema = 1; toolchains = @([ordered]@{ id = 'fake'; title = 'Fake'; version = '1.0'; source = 'a test'; zip = 'toolchains/fake/_dl/fake-1.0.zip'; bytes = (Get-Item $zip).Length; sha256 = (Get-BdSha256 $zip)
                                                                 dir = 'toolchains/fake/1.0'; files = @([ordered]@{ path = 'bin/cc.exe'; bytes = (Get-Item (Join-Path $dir 'bin\cc.exe')).Length; sha256 = (Get-BdSha256 (Join-Path $dir 'bin\cc.exe')) })
                                                                 versionFile = [ordered]@{ path = 'VERSION'; firstLine = 'fake-1.0' } }) }
        $rp = Join-Path $tmpRoot 'toolchains-test.json'; Save-BdJson -Object $reg -Path $rp -Depth 8; $env:BROKENDEV_TOOLCHAINS = $rp
        try {
            $st = Get-BdToolchainState -Spec (Get-BdToolchainSpec -Id fake) -NoCache
            Assert-Equal $st.State 'ready' $st.Detail
            Assert-Equal (Expand-BdPath -Text '{toolchain:fake}\bin' -Context @{}) ((Join-Path $dir 'bin'))
            [System.IO.File]::WriteAllText((Join-Path $dir 'bin\cc.exe'), 'another compiler')
            $st = Get-BdToolchainState -Spec (Get-BdToolchainSpec -Id fake) -NoCache
            Assert-Equal $st.State 'changed' $st.Detail
            Assert-True ($st.Detail -match 'bin/cc.exe') $st.Detail
            $script:BdToolchainCache = @{}
            Assert-Throws { Expand-BdPath -Text '{toolchain:fake}' -Context @{} } 'changed'
            Remove-Item -LiteralPath $dir -Recurse -Force
            $st = Get-BdToolchainState -Spec (Get-BdToolchainSpec -Id fake) -SkipZip -NoCache
            Assert-Equal $st.State 'missing' $st.Detail
            Assert-Throws { Get-BdToolchainSpec -Id 'nope' } 'unknown toolchain'
        } finally { $script:BdToolchainCache = @{}; Restore-Env $savedEnv }
    }
    Test-Case 'the pinned GBDK-2020 on this machine is ready (when it is unpacked)' {
        $st = Get-BdToolchainState -Spec (Get-BdToolchainSpec -Id gbdk) -NoCache
        if ($st.State -eq 'missing') { Skip-Test "GBDK-2020 is not unpacked here ($($st.Detail))" }
        Assert-Equal $st.State 'ready' $st.Detail
    }
    # vrun-voc is the user's live game (today V2: the tutorial) and moves on with it; the hello-world tests use a frozen V0 instead:
    # the fixture manifest tests\fixtures\projects\vrun-voc-v0 and the sources of the game repo's pinned V0 commit (tests\voc-v0.ps1: git archive,
    # read only)
    $gbM = Read-BdJson (Join-Path $bd 'projects\vrun-voc\project.json')
    $gbFixtures = Join-Path $here 'fixtures\projects'
    $gbV0 = Read-BdJson (Join-Path $gbFixtures 'vrun-voc-v0\project.json')
    $script:vocV0Src = $null
    function Get-VocV0Source {
        # The V0 sources in this run's scratch folder (taken once), or a SKIP naming what is missing. The game repo is only read.
        if ($script:vocV0Src) { return $script:vocV0Src }
        $r = Invoke-BdProcess -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $here 'voc-v0.ps1'), '-SourceOnly', '-Out', (Join-Path $tmpRoot 'voc-v0')) -TimeoutSec 180
        $last = @(([string]$r.Output).Trim() -split "`r?`n")[-1].Trim()
        if ($r.ExitCode -eq 2) { Skip-Test ($last -replace '^MISSING: ', '') }
        Assert-True ($r.ExitCode -eq 0 -and (Test-Path -LiteralPath (Join-Path $last 'build.ps1'))) "voc-v0.ps1 -SourceOnly: $(Select-BdTail $r.Output 4)"
        $script:vocV0Src = $last; $last
    }
    Test-Case 'the Game Boy hello-world fixture (kind gb, console gbc, source {live:VRUN_VOC_dev}, toolchain:gbdk; frozen V0) validates; NEGATIVE: kind gb on a SNES console, gbc on a NES kind and an unknown tool form are rejected' {
        $e = @(Test-BdManifest -M $gbV0 -ExpectedId 'vrun-voc-v0'); Assert-True ($e.Count -eq 0) ($e -join '; ')
        $copy = { ($gbV0 | ConvertTo-Json -Depth 30) | ConvertFrom-Json }
        $c = & $copy; $c.console = 'snes'; Assert-True ((@(Test-BdManifest -M $c) -join ';') -match 'kind gb and console gb or gbc') 'gb on a SNES console was accepted'
        $c = & $copy; $c.kind = 'nes'; Assert-True ((@(Test-BdManifest -M $c) -join ';') -match 'kind gb and console gb or gbc|must run on console nes') 'console gbc on kind nes was accepted'
        $c = & $copy; $c.requires.tools = @('toolchain:'); Assert-True ((@(Test-BdManifest -M $c) -join ';') -match "unknown tool 'toolchain:'") 'a toolchain tool without an id was accepted'
        Assert-True (@($gbV0.checks.accepted).Count -eq 0) 'the hello world accepts findings: it should have none'
    }
    Test-Case 'the shipped vrun-voc manifest (the live game, V2: the tutorial) validates: kind gb, console gbc, source {live:VRUN_VOC_dev}, toolchain:gbdk, the Color and original scenarios, its one accepted finding (gb.lag-frame) with a reason; NEGATIVE: an accepted finding without a reason is rejected' {
        $e = @(Test-BdManifest -M $gbM -ExpectedId 'vrun-voc'); Assert-True ($e.Count -eq 0) ($e -join '; ')
        Assert-Equal $gbM.kind 'gb'; Assert-Equal $gbM.console 'gbc'; Assert-Equal $gbM.source.folder '{live:VRUN_VOC_dev}'
        Assert-True (@($gbM.requires.tools) -contains 'toolchain:gbdk') 'the manifest does not need the pinned GBDK'
        Assert-Equal (@($gbM.verify.scenarios | ForEach-Object { $_.id }) -join ',') 'color,original'
        Assert-Equal (@($gbM.checks.accepted | ForEach-Object { $_.rule }) -join ',') 'gb.lag-frame'
        $c = ($gbM | ConvertTo-Json -Depth 30) | ConvertFrom-Json; $c.checks.accepted[0].reason = ''
        Assert-True ((@(Test-BdManifest -M $c -ExpectedId 'vrun-voc') -join ';') -match 'gb.lag-frame.*has no reason') 'an accepted finding without a reason was accepted'
    }
    Test-Case '{live:NAME} is the folder BROKENDEV_LIVE_<NAME> names (and a project on it is a live workspace); NEGATIVE: a name found nowhere, and an env folder that does not exist, are errors' {
        $wsd = Join-Path $tmpRoot 'BDTEST_GB_WS'; New-Item -ItemType Directory -Force -Path $wsd | Out-Null
        $env:BROKENDEV_LIVE_BDTEST_GB_WS = $wsd
        try {
            Assert-Equal (Expand-BdPath -Text '{live:BDTEST_GB_WS}\src' -Context @{}) (Join-Path $wsd 'src')
            $c = ($gbM | ConvertTo-Json -Depth 30) | ConvertFrom-Json; $c.source.folder = '{live:BDTEST_GB_WS}'
            $w = Get-BdProjectWorkspace -Manifest $c
            Assert-Equal $w.Kind 'live'; Assert-Equal $w.Path $wsd
            Assert-Throws { Expand-BdPath -Text '{live:BDTEST_NO_SUCH_WS}' -Context @{} } 'was not found'
            $c.source.folder = '{live:BDTEST_NO_SUCH_WS}'
            Assert-Equal (Get-BdProjectWorkspace -Manifest $c).Kind 'absent'
            $env:BROKENDEV_LIVE_BDTEST_NO_SUCH_WS = (Join-Path $tmpRoot 'not-there')
            Assert-Throws { Expand-BdPath -Text '{live:BDTEST_NO_SUCH_WS}' -Context @{} } 'does not exist'
        } finally { Restore-Env $savedEnv }
    }
    Test-Case 'Game Boy symbols: GBDK''s .noi and the bgb .sym are read; CPU addresses map to wram, hram and cartram; NEGATIVE: a ROM address is not RAM, and the linker''s area names are not symbols' {
        $noi = Join-Path $tmpRoot 'g.noi'; [System.IO.File]::WriteAllText($noi, "DEF _player_x 0xC0B1`nDEF _move_player 0x511`nDEF s__DATA 0xC0A0`nDEF l__DATA 0x3B`nDEF _hi 0xFF90`nDEF _banked 0x2D004`n")
        $t = Read-BdSymbols -Path $noi
        Assert-Equal $t['_player_x'] 0xC0B1; Assert-True (-not $t.ContainsKey('s__DATA')) 'the linker area start s__DATA was read as a symbol'
        $sym = Join-Path $tmpRoot 'gb.sym'; [System.IO.File]::WriteAllText($sym, "; bgb`n00:C0B1 _player_x`n02:D004 _far`n")
        $s = Read-BdSymbols -Path $sym
        $d = ConvertTo-BdGbDomain -Address $s['_player_x']; Assert-Equal $d.Domain 'wram'; Assert-Equal $d.Addr 0xB1
        $d = ConvertTo-BdGbDomain -Address $s['_far']; Assert-Equal $d.Addr (2 * 0x1000 + 4)
        $d = ConvertTo-BdGbDomain -Address $t['_banked']; Assert-Equal $d.Addr (2 * 0x1000 + 4)
        $d = ConvertTo-BdGbDomain -Address $t['_hi']; Assert-Equal $d.Domain 'hram'; Assert-Equal $d.Addr 0x10
        $d = ConvertTo-BdGbDomain -Address 0x1A005; Assert-Equal $d.Domain 'cartram'; Assert-Equal $d.Addr (0x2000 + 5)
        Assert-Throws { ConvertTo-BdGbDomain -Address $t['_move_player'] } 'not Game Boy work RAM'
        $sec = [pscustomobject]@{ symbols = [pscustomobject]@{ file = $noi }; reads = [pscustomobject]@{ x = '_player_x'; h = '$FF90' } }
        $r = @(Resolve-BdCompatReads -Compat $sec -Console 'gbc')
        Assert-Equal $r[0].Domain 'wram'; Assert-Equal $r[0].Addr 0xB1; Assert-Equal $r[1].Domain 'hram'
    }
    Test-Case 'a Game Boy compat spec takes the pad''s eight buttons and the wram / hram / cartram domains; NEGATIVE: a SNES button, the NES ram domain and an offset past high RAM are never written' {
        $run = @{ Console = 'gbc'; Rom = 'C:\x\a b.gbc'; Out = 'C:\o'; SaveDir = 'C:\s'; Frames = 60; Reads = @([pscustomobject]@{ Name = 'x'; Domain = 'wram'; Addr = 0xB1 }, [pscustomobject]@{ Name = 'c'; Domain = 'cartram'; Addr = 4 }); Input = '10:Right+A,20:'; Shots = @(60) }
        $s = Read-BdCompatSpec -Text (New-BdCompatSpecText -Run $run)
        Assert-Equal $s.Console 'gbc'; Assert-Equal @($s.Reads).Count 2; Assert-Equal (@($s.Inputs)[0].Buttons -join '+') 'Right+A'
        $bad = $run.Clone(); $bad.Input = '10:Y'; Assert-Throws { New-BdCompatSpecText -Run $bad } 'not a gbc button'
        $bad = $run.Clone(); $bad.Reads = @([pscustomobject]@{ Name = 'r'; Domain = 'ram'; Addr = 1 }); Assert-Throws { New-BdCompatSpecText -Run $bad } 'does not belong to the gbc'
        $bad = $run.Clone(); $bad.Reads = @([pscustomobject]@{ Name = 'h'; Domain = 'hram'; Addr = 0x7F }); Assert-Throws { New-BdCompatSpecText -Run $bad } 'outside hram'
    }
    Test-Case 'the registry has the Game Boy columns (BrokenNes, Mesen, BizHawk Gambatte and SameBoy); NEGATIVE: a BizHawk id running gb and nes, or gb and gbc without system GB, is rejected' {
        $gbEmus = @($emuGood.emulators | Where-Object { @($_.consoles) -contains 'gbc' } | ForEach-Object { $_.id })
        foreach ($w in 'brokennes', 'mesen', 'bizhawk-gambatte', 'bizhawk-sameboy') { Assert-True ($gbEmus -contains $w) "no gbc column $w (have: $($gbEmus -join ', '))" }
        $e = & $emuMut { param($j) ($j.emulators | Where-Object { $_.id -eq 'bizhawk-gambatte' }).consoles = @('gb', 'nes') }
        Assert-True (($e -join ';') -match 'pins one core') ($e -join '; ')
        $e = & $emuMut { param($j) ($j.emulators | Where-Object { $_.id -eq 'bizhawk-sameboy' }).system = 'GBC' }
        Assert-True (($e -join ';') -match 'pins one core') ($e -join '; ')
        Assert-Equal (Get-BdMesenGbModel -Emu ($emuGood.emulators | Where-Object { $_.id -eq 'mesen' }) -Console 'gbc') 'GameboyColor'
        Assert-Equal (Get-BdMesenGbModel -Emu ($emuGood.emulators | Where-Object { $_.id -eq 'mesen' }) -Console 'nes') ''
    }
    Test-Case 'BizHawk''s Game Boy config pins the core for both GB and GBC and puts the battery save in the run''s folder; NEGATIVE: a NES config is exactly as before (no Game Boy entries)' {
        $sc = Join-Path $tmpRoot 'bhgb'
        $c = New-BdBizhawkConfig -System GB -Core Gambatte -SaveDir (Join-Path $sc 'saves') -Scratch $sc -InstallDir (Join-Path $tmpRoot 'bhinst')
        Assert-Equal $c.PreferredCores.GB 'Gambatte'; Assert-Equal $c.PreferredCores.GBC 'Gambatte'
        $sr = @($c.PathEntries.Paths | Where-Object { $_.System -eq 'GB_GBC_SGB' -and $_.Type -eq 'Save RAM' })
        Assert-Equal $sr.Count 1; Assert-Equal $sr[0].Path (Join-Path $sc 'saves')
        $n = New-BdBizhawkConfig -System NES -Core NesHawk -SaveDir (Join-Path $sc 'nsaves') -Scratch $sc -InstallDir (Join-Path $tmpRoot 'bhinst')
        Assert-Equal (@($n.PreferredCores.Keys) -join ',') 'NES'
        Assert-True (@($n.PathEntries.Paths | Where-Object { $_.System -eq 'GB_GBC_SGB' }).Count -eq 0) 'a NES config gained Game Boy path entries'
    }

    # ------------------------------------------------------------------------------------------ the WideMess devkit (BrokenDev/widemess)
    Write-Host 'widemess devkit'
    Restore-Env $savedEnv
    $env:BROKENDEV_HOME = Join-Path $tmpRoot 'wmshome'
    $wmsProj = Join-Path $tmpRoot 'wmsprojects'
    New-Item -ItemType Directory -Force -Path $wmsProj, $env:BROKENDEV_HOME | Out-Null
    $env:BROKENDEV_PROJECTS = $wmsProj
    $wmsTools = Join-Path $bd 'widemess\tools'
    $wmsHelper = Join-Path $here 'wms-converters.py'
    $wmsPy = Find-BdPython
    $wmsPvs = Find-BdPvsneslib
    $wmsGame = Join-Path $tmpRoot 'wms-new'
    function Invoke-WmsPy {
        param([string[]]$PyArgs)
        $r = Invoke-BdProcess -FilePath $wmsPy.Path -Arguments (@('-I', '-B') + $PyArgs) -TimeoutSec 120   # -B: no __pycache__ in the repository
        [pscustomobject]@{ Exit = $r.ExitCode; Output = $r.Output }
    }
    Test-Case 'a WideMess manifest (kind and console wms, source.folder) validates; NEGATIVE: wms on a SNES console, a folder and an archive together, and neither, are rejected' {
        $m = Read-BdJson (Join-Path $bd 'projects\widemess-sample\project.json')
        $e = @(Test-BdManifest -M $m -ExpectedId 'widemess-sample')
        Assert-True ($e.Count -eq 0) ($e -join '; ')
        $copy = { ($m | ConvertTo-Json -Depth 30) | ConvertFrom-Json }
        $c = & $copy; $c.console = 'snes'
        Assert-True ((@(Test-BdManifest -M $c) -join ';') -match 'kind wms and console wms') 'wms on console snes was accepted'
        $c = & $copy; $c.source | Add-Member -NotePropertyName archive -NotePropertyValue 'x'; $c.source | Add-Member -NotePropertyName path -NotePropertyValue 'y'
        Assert-True ((@(Test-BdManifest -M $c) -join ';') -match 'both a folder and an archive') 'a folder and an archive together were accepted'
        $c = & $copy; $c.source = [pscustomobject]@{}
        Assert-True ((@(Test-BdManifest -M $c) -join ';') -match 'source.archive is required') 'a source with neither was accepted'
    }
    Test-Case 'new widemess makes the template''s folder and a manifest that validates, the name filled in everywhere; NEGATIVE: the same name again, a folder that is not empty and a bad name are refused and write nothing' {
        $p = New-BdWideMessProject -Name 'test-game' -Path $wmsGame -Title 'TEST GAME'
        $m = Read-BdManifest -Id 'test-game'
        Assert-Equal $m.kind 'wms'
        Assert-Equal ([string]$m.source.folder) ($wmsGame.Replace('\', '/'))
        $cfg = Read-BdJson (Join-Path $wmsGame 'wms.json')
        Assert-Equal $cfg.name 'test_game'; Assert-Equal $cfg.title 'TEST GAME'
        foreach ($f in 'wms.json', 'src\main.c', 'src\far.asm') { Assert-True (-not ([System.IO.File]::ReadAllText((Join-Path $wmsGame $f)).Contains('{{'))) "$f still has a placeholder" }
        foreach ($f in '.gitignore', 'gfx\sky.png', 'audio\chime.wav', 'audio\hello.mid') { Assert-True (Test-Path -LiteralPath (Join-Path $wmsGame $f)) "$f was not made" }
        Assert-Throws { New-BdWideMessProject -Name 'test-game' -Path (Join-Path $tmpRoot 'wms-other') } 'exists already'
        $full = Join-Path $tmpRoot 'wms-full'; New-Item -ItemType Directory -Force -Path $full | Out-Null; Set-Content -LiteralPath (Join-Path $full 'mine.txt') 'x'
        Assert-Throws { New-BdWideMessProject -Name 'test-two' -Path $full } 'not empty'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $wmsProj 'test-two'))) 'a refused project left a manifest'
        Assert-Throws { New-BdWideMessProject -Name 'Bad_Name' -Path (Join-Path $tmpRoot 'wms-bad') } 'lower-case'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $tmpRoot 'wms-bad'))) 'a refused name made its folder'
    }
    Test-Case 'a folder project builds in its folder ({src} is it); NEGATIVE: a manifest whose folder does not exist is refused, not built elsewhere' {
        $m = Read-BdManifest -Id 'test-game'
        $w = Get-BdProjectWorkspace -Manifest $m
        Assert-Equal $w.Kind 'folder'; Assert-Equal $w.Path $wmsGame
        Assert-Equal (New-BdContext $m)['src'] $wmsGame
        $g = Read-BdJson (Join-Path $wmsProj 'test-game\project.json'); $g.id = 'test-gone'; $g.source.folder = (Join-Path $tmpRoot 'no-such-folder').Replace('\', '/')
        New-Item -ItemType Directory -Force -Path (Join-Path $wmsProj 'test-gone') | Out-Null
        Save-BdJson -Object $g -Path (Join-Path $wmsProj 'test-gone\project.json') -Depth 30
        Assert-Equal (Get-BdProjectWorkspace -Manifest (Read-BdManifest -Id 'test-gone')).Kind 'absent'
        if (-not $wmsPy) { Skip-Test 'no Python with pillow and numpy (the build checks its tools first)' }
        $env:BROKENDEV_WORKSHOP = $env:ComSpec   # any existing file: the refusal must come before any step runs
        try { Assert-Throws { Invoke-BdProjectBuild -Id 'test-gone' } 'does not exist' } finally { $env:BROKENDEV_WORKSHOP = $null }
        Remove-Item -LiteralPath (Join-Path $wmsProj 'test-gone') -Recurse -Force
    }
    Test-Case 'wmsgfx: its 8-bpp tiles are gfx4snes''s bytes for the same indexed picture; NEGATIVE: one changed byte is caught' {
        if (-not $wmsPy) { Skip-Test 'no Python with pillow and numpy' }
        if (-not $wmsPvs) { Skip-Test 'PVSnesLib (gfx4snes) not found' }
        $d = Join-Path $tmpRoot 'wms-g4s'; New-Item -ItemType Directory -Force -Path $d | Out-Null
        $png = Join-Path $d 'g.png'
        Assert-Equal (Invoke-WmsPy @($wmsHelper, 'indexed', $png)).Exit 0
        $r = Invoke-BdProcess -FilePath (Join-Path $wmsPvs 'devkitsnes\tools\gfx4snes.exe') -Arguments @('-s', '8', '-u', '256', '-R', '-p', '-m', '-t', 'png', '-i', $png) -WorkingDirectory $d -TimeoutSec 60
        Assert-True (Test-Path -LiteralPath (Join-Path $d 'g.pic')) "gfx4snes wrote no g.pic: $(Select-BdTail $r.Output 4)"
        $x = Invoke-WmsPy @($wmsHelper, 'same-tiles', $wmsTools, $png, (Join-Path $d 'g.pic'))
        Assert-Equal $x.Exit 0 $x.Output
        $b = [System.IO.File]::ReadAllBytes((Join-Path $d 'g.pic')); $b[100] = $b[100] -bxor 0x10; [System.IO.File]::WriteAllBytes((Join-Path $d 'bad.pic'), $b)
        Assert-Equal (Invoke-WmsPy @($wmsHelper, 'same-tiles', $wmsTools, $png, (Join-Path $d 'bad.pic'))).Exit 1
    }
    Test-Case 'wmsgfx: a tile and its mirror become one tile and a map entry with the flip bit' {
        if (-not $wmsPy) { Skip-Test 'no Python with pillow and numpy' }
        $d = Join-Path $tmpRoot 'wms-flip'; New-Item -ItemType Directory -Force -Path $d | Out-Null
        $x = Invoke-WmsPy @($wmsHelper, 'flips', $wmsTools, $d)
        Assert-Equal $x.Exit 0 $x.Output
    }
    Test-Case 'wmsgfx: 255 colours fill a bank; NEGATIVE: 256 in one bank and 16 in a 4-bpp sprite are refused, saying what to change' {
        if (-not $wmsPy) { Skip-Test 'no Python with pillow and numpy' }
        $d = Join-Path $tmpRoot 'wms-col'; New-Item -ItemType Directory -Force -Path $d | Out-Null
        foreach ($n in 255, 256, 16) { Assert-Equal (Invoke-WmsPy @($wmsHelper, 'colours', (Join-Path $d "c$n.png"), [string]$n)).Exit 0 }
        Assert-Equal (Invoke-WmsPy @((Join-Path $wmsTools 'wmsgfx.py'), 'layer', (Join-Path $d 'c255.png'), (Join-Path $d 'c255'))).Exit 0
        $x = Invoke-WmsPy @((Join-Path $wmsTools 'wmsgfx.py'), 'layer', (Join-Path $d 'c256.png'), (Join-Path $d 'c256'))
        Assert-Equal $x.Exit 1; Assert-True ($x.Output -match 'bank 0 is full') $x.Output
        $x = Invoke-WmsPy @((Join-Path $wmsTools 'wmsgfx.py'), 'sprite', (Join-Path $d 'c16.png'), (Join-Path $d 'c16'), '--frame', '32x32', '--bpp', '4')
        Assert-Equal $x.Exit 1; Assert-True ($x.Output -match 'a 4-bpp sprite has 15') $x.Output
    }
    Test-Case 'wmssnd: 16-bit PCM is whole groups of 4 samples with its loop on a group; BRR through snesbrr is whole 9-byte blocks' {
        if (-not $wmsPy) { Skip-Test 'no Python with pillow and numpy' }
        $d = Join-Path $tmpRoot 'wms-snd'; New-Item -ItemType Directory -Force -Path $d | Out-Null
        $wav = Join-Path $d 't.wav'
        Assert-Equal (Invoke-WmsPy @($wmsHelper, 'wav', $wav, '1001')).Exit 0
        $x = Invoke-WmsPy @((Join-Path $wmsTools 'wmssnd.py'), 'pcm', $wav, (Join-Path $d 't.pcm'), '--loop', '7')
        Assert-Equal $x.Exit 0 $x.Output
        Assert-Equal (Get-Item -LiteralPath (Join-Path $d 't.pcm')).Length 2008
        Assert-True ($x.Output -match 'loop at 4\b') "the loop was not put on a group of 4: $($x.Output)"
        if ($wmsPvs) {
            $x = Invoke-WmsPy @((Join-Path $wmsTools 'wmssnd.py'), 'brr', $wav, (Join-Path $d 't.brr'), '--snesbrr', (Join-Path $wmsPvs 'devkitsnes\tools\snesbrr.exe'))
            Assert-Equal $x.Exit 0 $x.Output
            $n = (Get-Item -LiteralPath (Join-Path $d 't.brr')).Length
            Assert-True ($n -gt 0 -and $n % 9 -eq 0) "BRR of $n bytes"
        }
    }
    Test-Case 'wmssnd: a song with 24 notes at once builds; NEGATIVE: 25 at once (the driver has 24 channels) and a note above 4x are refused' {
        if (-not $wmsPy) { Skip-Test 'no Python with pillow and numpy' }
        $d = Join-Path $tmpRoot 'wms-song'; New-Item -ItemType Directory -Force -Path $d | Out-Null
        $drv = Join-Path $d 'drv.bin'; [System.IO.File]::WriteAllBytes($drv, (New-Object byte[] 64))
        $try = {
            param([int]$Count, [int]$Note)
            $mid = Join-Path $d "m$Count-$Note.mid"; $p = Join-Path $d "p$Count-$Note"
            Assert-Equal (Invoke-WmsPy @($wmsHelper, 'midi', $mid, [string]$Count, [string]$Note)).Exit 0
            Assert-Equal (Invoke-WmsPy @($wmsHelper, 'project', $p, $mid)).Exit 0
            Invoke-WmsPy @((Join-Path $wmsTools 'wmssnd.py'), 'build', (Join-Path $p 'wms.json'), (Join-Path $p 'gen'), '--driver', $drv)
        }
        $x = & $try 24 50; Assert-Equal $x.Exit 0 $x.Output
        $x = & $try 25 50; Assert-Equal $x.Exit 1; Assert-True ($x.Output -match 'more than 24 notes') $x.Output
        $x = & $try 1 94; Assert-Equal $x.Exit 1; Assert-True ($x.Output -match 'up to 4x') $x.Output
    }

    # ------------------------------------------------------------------------------------------ BrokenNes integration (Workshop)
    Write-Host 'BrokenNes integration'
    Restore-Env $savedEnv
    $ws = $WorkshopExe
    if (-not $ws) { $ws = Get-BdWorkshopExe }
    if ($NoWorkshop) { $ws = $null }
    $wsSkip = if ($ws) { '' } else { 'no Workshop executable (set BROKENDEV_WORKSHOP or run brokendev.ps1 build-workshop)' }
    $repoRoot = Get-BdRepoRoot
    $testRom = Join-Path $repoRoot 'Windows\Resources\test.nes'
    $wsHome = Join-Path $tmpRoot 'wshome'
    New-Item -ItemType Directory -Force -Path (Join-Path $wsHome 'out\wsynth') | Out-Null
    function Invoke-Ws {
        param([string[]]$WsArgs, [hashtable]$ExtraEnv = @{})
        $e = @{ BROKENNES_CONFIG_DIR = (Join-Path $tmpRoot 'noconfig') }
        foreach ($k in $ExtraEnv.Keys) { $e[$k] = $ExtraEnv[$k] }
        Invoke-BdProcess -FilePath $ws -Arguments $WsArgs -TimeoutSec 300 -Env $e
    }
    Test-Case '--brokendev refuses to run unless BrokenDev is enabled (exit 3)' {
        if (-not $ws) { Skip-Test $wsSkip }
        $r = Invoke-Ws -WsArgs @('--brokendev', 'list', '--manifests', (Get-BdProjectsDir))
        Assert-Equal $r.ExitCode 3
        Assert-True ($r.Output -match 'not enabled') 'no explanation was printed'
    }
    Test-Case '--brokendev list works with BROKENNES_DEV=1 and names the shipped projects' {
        if (-not $ws) { Skip-Test $wsSkip }
        $r = Invoke-Ws -WsArgs @('--brokendev', 'list', '--manifests', (Get-BdProjectsDir)) -ExtraEnv @{ BROKENNES_DEV = '1' }
        Assert-Equal $r.ExitCode 0
        Assert-True ($r.Output -match 'vrun-nes') 'vrun-nes is not listed'
    }
    Test-Case 'the config flag brokenDevEnabled switches BrokenDev on without the environment variable' {
        if (-not $ws) { Skip-Test $wsSkip }
        $cfg = Join-Path $tmpRoot 'cfg-on'; New-Item -ItemType Directory -Force -Path $cfg | Out-Null
        Set-Content -LiteralPath (Join-Path $cfg 'config.json') '{ "brokenDevEnabled": true }'
        $r = Invoke-Ws -WsArgs @('--brokendev', 'list', '--manifests', (Get-BdProjectsDir)) -ExtraEnv @{ BROKENNES_CONFIG_DIR = $cfg }
        Assert-Equal $r.ExitCode 0
    }
    Test-Case 'NEGATIVE: brokenDevEnabled false in the config keeps it off' {
        if (-not $ws) { Skip-Test $wsSkip }
        $cfg = Join-Path $tmpRoot 'cfg-off'; New-Item -ItemType Directory -Force -Path $cfg | Out-Null
        Set-Content -LiteralPath (Join-Path $cfg 'config.json') '{ "brokenDevEnabled": false }'
        $r = Invoke-Ws -WsArgs @('--brokendev', 'list', '--manifests', (Get-BdProjectsDir)) -ExtraEnv @{ BROKENNES_CONFIG_DIR = $cfg }
        Assert-Equal $r.ExitCode 3
    }

    # a manifest for the verifier itself; the ROMs are the repo's own smoke-test ROM and synthetic bad ones
    $vProj = Join-Path $tmpRoot 'vprojects'
    function New-VerifyManifest {
        param([string]$Id, $Scenarios, $Compare = @(), [hashtable]$Extra = @{})
        $m = [ordered]@{ schema = 1; id = $Id; title = $Id; family = 'synth'; kind = 'nes'; console = 'nes'; source = @{ archive = 'x'; path = 'x' }; requires = @{ tools = @() }; knownIssues = @()
                         build = @{ steps = @(); outputs = @(@{ id = 'rom'; path = 'x'; publishAs = 'rom.nes' }) }
                         verify = @{ cores = @{ cpu = 'FIX'; ppu = 'FIX'; apu = 'FIX' }; scenarios = $Scenarios; compare = $Compare } }
        foreach ($k in $Extra.Keys) { $m[$k] = $Extra[$k] }
        New-Item -ItemType Directory -Force -Path (Join-Path $vProj $Id) | Out-Null
        Save-BdJson -Object $m -Path (Join-Path (Join-Path $vProj $Id) 'project.json') -Depth 20
    }
    function Invoke-WsVerify {
        param([string]$Id, [string]$Rom)
        $out = Join-Path $tmpRoot "v-$Id.json"
        $r = Invoke-Ws -WsArgs @('--brokendev', 'verify', $Id, '--manifests', $vProj, '--home', $wsHome, '--rom', $Rom, '--out', $out) -ExtraEnv @{ BROKENNES_DEV = '1' }
        $j = $null; if (Test-Path $out) { $j = Read-BdJson $out }
        [pscustomobject]@{ Exit = $r.ExitCode; Json = $j; Output = $r.Output }
    }
    New-VerifyManifest -Id 'v-pass' -Scenarios @(@{ id = 'boot'; frames = 120; checkpoints = @(60, 120); expect = @{ minDistinctColors = 2; minRealtime = 0.5 } })
    Test-Case 'verify PASSES the repo smoke-test ROM against a satisfiable recipe and records hashes' {
        if (-not $ws) { Skip-Test $wsSkip }
        $v = Invoke-WsVerify -Id 'v-pass' -Rom $testRom
        Assert-Equal $v.Exit 0 "output: $(Select-BdTail $v.Output 8)"
        Assert-True $v.Json.ok 'report not ok'
        Assert-Equal @($v.Json.scenarios[0].checkpoints).Count 2
        Assert-True ($v.Json.scenarios[0].checkpoints[0].hash -match '^[0-9A-F]{16}$') 'checkpoint hash is not a 16-digit hex'
    }
    Test-Case 'verify is deterministic: two runs give identical checkpoint hashes' {
        if (-not $ws) { Skip-Test $wsSkip }
        $a = Invoke-WsVerify -Id 'v-pass' -Rom $testRom; $b = Invoke-WsVerify -Id 'v-pass' -Rom $testRom
        Assert-Equal $a.Json.scenarios[0].checkpoints[1].hash $b.Json.scenarios[0].checkpoints[1].hash
    }
    New-VerifyManifest -Id 'v-colors' -Scenarios @(@{ id = 'boot'; frames = 60; expect = @{ minDistinctColors = 30 } })
    Test-Case 'NEGATIVE: verify FAILS (exit 1) when the picture has fewer colours than expected' {
        if (-not $ws) { Skip-Test $wsSkip }
        $v = Invoke-WsVerify -Id 'v-colors' -Rom $testRom
        Assert-Equal $v.Exit 1
        Assert-True (-not $v.Json.ok) 'report says ok'
        Assert-True (@($v.Json.scenarios[0].checks | Where-Object { $_.id -eq 'distinct-colors' -and -not $_.ok }).Count -eq 1) 'the colour check did not fail'
    }
    New-VerifyManifest -Id 'v-audio' -Scenarios @(@{ id = 'boot'; frames = 120; expect = @{ audio = @{ fromFrame = 10; minPeak = 0.2 } } })
    Test-Case 'NEGATIVE: verify FAILS when audio is expected from a silent ROM' {
        if (-not $ws) { Skip-Test $wsSkip }
        $v = Invoke-WsVerify -Id 'v-audio' -Rom $testRom
        Assert-Equal $v.Exit 1
        Assert-True (@($v.Json.scenarios[0].checks | Where-Object { $_.id -eq 'audio-peak' -and -not $_.ok }).Count -eq 1) 'the audio check did not fail'
    }
    New-VerifyManifest -Id 'v-input' -Scenarios @(@{ id = 'idle'; frames = 120; checkpoints = @(120) }, @{ id = 'press'; frames = 120; input = '10:Start,20:A,30:'; checkpoints = @(120) }) -Compare @(@{ a = 'press@120'; b = 'idle@120'; expect = 'differ' })
    Test-Case 'NEGATIVE: "input reached the game" FAILS for a ROM that ignores the pad' {
        if (-not $ws) { Skip-Test $wsSkip }
        $v = Invoke-WsVerify -Id 'v-input' -Rom $testRom
        Assert-Equal $v.Exit 1
        Assert-True (@($v.Json.compare | Where-Object { -not $_.ok }).Count -eq 1) 'the compare check did not fail'
    }
    New-VerifyManifest -Id 'v-flat' -Scenarios @(@{ id = 'boot'; frames = 120; expect = @{ minDistinctColors = 2 } })
    Test-Case 'NEGATIVE: verify FAILS a ROM that never draws (all-zero program)' {
        if (-not $ws) { Skip-Test $wsSkip }
        $zero = Join-Path $tmpRoot 'zero.nes'
        $b = New-Object byte[] (16 + 16384 + 8192); $b[0] = 0x4E; $b[1] = 0x45; $b[2] = 0x53; $b[3] = 0x1A; $b[4] = 1; $b[5] = 1
        [System.IO.File]::WriteAllBytes($zero, $b)
        $v = Invoke-WsVerify -Id 'v-flat' -Rom $zero
        Assert-Equal $v.Exit 1 "output: $(Select-BdTail $v.Output 6)"
    }
    # A tiny ROM that paints the backdrop with the byte it finds at $07FC (work RAM): power-on RAM and pokes become visible in the picture.
    $ramRom = Join-Path $tmpRoot 'ramcolour.nes'
    $rb = New-Object byte[] (16 + 16384 + 8192); $rb[0] = 0x4E; $rb[1] = 0x45; $rb[2] = 0x53; $rb[3] = 0x1A; $rb[4] = 1; $rb[5] = 1
    $code = [byte[]](0x78, 0xA9, 0x3F, 0x8D, 0x06, 0x20, 0xA9, 0x00, 0x8D, 0x06, 0x20, 0xAD, 0xFC, 0x07, 0x8D, 0x07, 0x20, 0xA9, 0x3F, 0x8D, 0x06, 0x20, 0xA9, 0x00, 0x8D, 0x06, 0x20, 0xA9, 0x08, 0x8D, 0x01, 0x20, 0x4C, 0x20, 0xC0)
    [Array]::Copy($code, 0, $rb, 16, $code.Length); $rb[16 + 0x3FFC] = 0x00; $rb[16 + 0x3FFD] = 0xC0
    [System.IO.File]::WriteAllBytes($ramRom, $rb)
    New-VerifyManifest -Id 'v-ram' -Scenarios @(
        @{ id = 'fceux'; frames = 30; powerOnRam = 'fceux'; checkpoints = @(30) },
        @{ id = 'zeros'; frames = 30; powerOnRam = 'zeros'; checkpoints = @(30) },
        @{ id = 'poke1'; frames = 30; powerOnRam = 'zeros'; pokes = @(@{ frame = 0; addr = '0x07FC'; value = 22 }); checkpoints = @(30) },
        @{ id = 'poke2'; frames = 30; powerOnRam = 'zeros'; pokes = @(@{ frame = 0; addr = 2044; value = 42 }); checkpoints = @(30) })
    Test-Case 'powerOnRam changes what a program that reads uninitialised RAM draws (fceux pattern vs zeros)' {
        if (-not $ws) { Skip-Test $wsSkip }
        $v = Invoke-WsVerify -Id 'v-ram' -Rom $ramRom
        Assert-Equal $v.Exit 0 "output: $(Select-BdTail $v.Output 6)"
        $h = @{}; foreach ($s in $v.Json.scenarios) { $h[$s.id] = $s.checkpoints[0].hash }
        Assert-True ($h['fceux'] -ne $h['zeros']) "fceux and zeros drew the same picture ($($h['fceux']))"
        Assert-True ($h['poke1'] -ne $h['zeros']) 'a poke to $07FC did not change the picture'
        Assert-True ($h['poke1'] -ne $h['poke2']) 'two different poked values drew the same picture'
    }
    New-VerifyManifest -Id 'v-info' -Scenarios @(@{ id = 'bad'; info = $true; frames = 30; expect = @{ minDistinctColors = 99 } })
    New-VerifyManifest -Id 'v-notinfo' -Scenarios @(@{ id = 'bad'; frames = 30; expect = @{ minDistinctColors = 99 } })
    Test-Case 'an information scenario that fails is recorded but does not fail the verdict' {
        if (-not $ws) { Skip-Test $wsSkip }
        $v = Invoke-WsVerify -Id 'v-info' -Rom $testRom
        Assert-Equal $v.Exit 0
        Assert-True ($v.Json.scenarios[0].info -and -not $v.Json.scenarios[0].ok) 'the scenario should be flagged info and not ok'
    }
    Test-Case 'NEGATIVE: the same failing scenario WITHOUT the info flag fails the verdict (exit 1)' {
        if (-not $ws) { Skip-Test $wsSkip }
        $v = Invoke-WsVerify -Id 'v-notinfo' -Rom $testRom
        Assert-Equal $v.Exit 1
    }
    Test-Case 'NEGATIVE: an unknown powerOnRam mode is refused, not ignored' {
        if (-not $ws) { Skip-Test $wsSkip }
        New-VerifyManifest -Id 'v-badram' -Scenarios @(@{ id = 'x'; frames = 5; powerOnRam = 'random' })
        $v = Invoke-WsVerify -Id 'v-badram' -Rom $testRom
        Assert-Equal $v.Exit 1
        Assert-True ($v.Json.scenarios[0].result -eq 'refused') "result was $($v.Json.scenarios[0].result)"
    }
    # the hardware checks and budgets (milestone D4): the RAM-colour ROM reads $07FC before it ever writes it, a real nes.ram-uninit finding
    $chkScen = @(@{ id = 'boot'; frames = 60; checkpoints = @(60) })
    New-VerifyManifest -Id 'v-chk-new' -Scenarios $chkScen -Extra @{ checks = @{ accepted = @() } }
    New-VerifyManifest -Id 'v-chk-acc' -Scenarios $chkScen -Extra @{ checks = @{ accepted = @(@{ rule = 'nes.ram-uninit'; at = 'ram:$07FC'; reason = 'test: the ROM paints its backdrop with power-on RAM on purpose' }) } }
    New-VerifyManifest -Id 'v-chk-badrule' -Scenarios $chkScen -Extra @{ checks = @{ accepted = @(@{ rule = 'nes.no-such-rule'; at = '*'; reason = 'x' }, @{ rule = 'nes.ram-uninit'; at = '*'; reason = 'test' }) } }
    Test-Case 'checks: verify FAILS on a new finding (the ROM reads RAM before writing it), naming the rule, place, frame and PC' {
        if (-not $ws) { Skip-Test $wsSkip }
        $v = Invoke-WsVerify -Id 'v-chk-new' -Rom $ramRom
        Assert-Equal $v.Exit 1 "output: $(Select-BdTail $v.Output 8)"
        Assert-True (-not $v.Json.checks.ok) 'the checks say ok'
        $f = @($v.Json.checks.findings | Where-Object { $_.rule -eq 'nes.ram-uninit' })
        Assert-True ($f.Count -eq 1 -and $f[0].place -eq 'ram:$07FC' -and -not $f[0].accepted -and $f[0].pc -match '^\$C0' -and $f[0].firstFrame -le 1) "findings: $($v.Json.checks.findings | ConvertTo-Json -Compress -Depth 4)"
        Assert-Equal $v.Json.checks.newFindings 1
        Assert-True ($v.Output -match 'NEW nes.ram-uninit at ram:\$07FC') 'the summary does not name the new finding'
    }
    Test-Case 'checks: the same finding accepted with a reason PASSES, and the checked run ends on the unchecked run''s picture' {
        if (-not $ws) { Skip-Test $wsSkip }
        $v = Invoke-WsVerify -Id 'v-chk-acc' -Rom $ramRom
        Assert-Equal $v.Exit 0 "output: $(Select-BdTail $v.Output 8)"
        Assert-True ($v.Json.checks.ok -and $v.Json.checks.newFindings -eq 0 -and $v.Json.checks.acceptedFindings -eq 1) 'not ok / counts wrong'
        Assert-True ($v.Json.checks.findings[0].reason -like 'test:*') 'the accepted reason is not in the report'
        Assert-True $v.Json.checks.scenarios[0].frameIdentical "not frame-identical: $($v.Json.checks.scenarios[0].identity)"
    }
    Test-Case 'NEGATIVE: an accepted entry naming no rule of the catalogue fails verify (a typo cannot accept everything)' {
        if (-not $ws) { Skip-Test $wsSkip }
        $v = Invoke-WsVerify -Id 'v-chk-badrule' -Rom $ramRom
        Assert-Equal $v.Exit 1
        Assert-True (@($v.Json.checks.problems | Where-Object { $_ -match 'nes.no-such-rule' }).Count -eq 1) "problems: $($v.Json.checks.problems -join '; ')"
    }
    # checkup (D9): a throwaway project (its own folder; a build step that copies the RAM-colour ROM) through the real checkup
    function New-CheckupProject {
        param([string]$Id, $Checks)
        $src = Join-Path $tmpRoot "cu-src-$Id"; New-Item -ItemType Directory -Force -Path $src | Out-Null
        Copy-Item -LiteralPath $ramRom -Destination (Join-Path $src 'game-src.nes') -Force
        Set-Content -LiteralPath (Join-Path $src 'build.ps1') -Value "`$ErrorActionPreference = 'Stop'; New-Item -ItemType Directory -Force -Path (Join-Path `$PSScriptRoot 'build') | Out-Null; Copy-Item -LiteralPath (Join-Path `$PSScriptRoot 'game-src.nes') -Destination (Join-Path `$PSScriptRoot 'build\game.nes') -Force; (Get-Item (Join-Path `$PSScriptRoot 'build\game.nes')).LastWriteTimeUtc = [datetime]::UtcNow"
        $m = [ordered]@{ schema = 1; id = $Id; title = $Id; family = 'synth'; kind = 'nes'; console = 'nes'; source = @{ folder = $src }; requires = @{ tools = @(); projects = @(); inputs = @() }; knownIssues = @()
                         build = @{ steps = @(@{ name = 'copy'; shell = 'ps5'; cwd = '{src}'; file = '{src}/build.ps1'; timeoutSec = 120 }); outputs = @(@{ id = 'rom'; path = '{src}/build/game.nes'; publishAs = 'game.nes' }) }
                         verify = @{ cores = @{ cpu = 'FIX'; ppu = 'FIX'; apu = 'FIX' }; scenarios = @(@{ id = 'boot'; frames = 60; checkpoints = @(60) }) }; checks = $Checks }
        $pd = Join-Path (Join-Path $tmpRoot 'cuprojects') $Id; New-Item -ItemType Directory -Force -Path $pd | Out-Null
        Save-BdJson -Object $m -Path (Join-Path $pd 'project.json') -Depth 20
    }
    Test-Case 'checkup: NEGATIVE: a project with a new finding (a planted read of RAM before it is written) FAILS checkup (exit 1) and names the rule and place; the same project with it accepted PASSES, and the second checkup lists what changed' {
        if (-not $ws) { Skip-Test $wsSkip }
        New-CheckupProject -Id 'cu-bad' -Checks @{ accepted = @() }
        New-CheckupProject -Id 'cu-good' -Checks @{ accepted = @(@{ rule = 'nes.ram-uninit'; at = 'ram:$07FC'; reason = 'test: the ROM paints its backdrop with power-on RAM on purpose' }) }
        $env:BROKENDEV_HOME = Join-Path $tmpRoot 'cuhome'; $env:BROKENDEV_PROJECTS = Join-Path $tmpRoot 'cuprojects'; $env:BROKENDEV_WORKSHOP = $ws
        $env:BROKENNES_BUGS_DIR = Join-Path $tmpRoot 'cubugs'
        try {
            $bdps = Join-Path $bd 'brokendev.ps1'
            $o = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $bdps checkup cu-bad -Exe $ws -NoSuites 2>&1 | Out-String
            Assert-Equal $LASTEXITCODE 1 "checkup of the planted project: $(Select-BdTail $o 12)"
            Assert-True ($o -match 'cu-bad findings: NEW nes\.ram-uninit at ram:\$07FC') "the summary does not name the finding: $(Select-BdTail $o 12)"
            $j = Read-BdJson (Join-Path $env:BROKENDEV_HOME 'out\checkup\checkup.json')
            Assert-True (-not $j.ok) 'the report says ok'
            $fs = @($j.projects[0].steps | Where-Object { $_.step -eq 'findings' })[0]
            Assert-Equal $fs.state 'FAIL'
            Assert-Equal (@($j.projects[0].steps | Where-Object { $_.step -eq 'build' })[0].state) 'ok'
            $o2 = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $bdps checkup cu-good -Exe $ws -NoSuites 2>&1 | Out-String
            Assert-Equal $LASTEXITCODE 0 "checkup of the accepted project: $(Select-BdTail $o2 12)"
            $j2 = Read-BdJson (Join-Path $env:BROKENDEV_HOME 'out\checkup\checkup.json')
            Assert-True $j2.ok 'the accepted project did not pass'
            Assert-True (Test-Path (Join-Path $env:BROKENDEV_HOME 'out\checkup\checkup-previous.json')) 'the previous report was not kept'
            Assert-True (@($j2.changed) -match 'cu-good: first checkup') "changed: $($j2.changed -join '; ')"
        } finally { Restore-Env $savedEnv }
    }
    Test-Case 'checkup: NEGATIVE: a project that does not build FAILS at build and skips the rest (never a silent pass); an unknown project is refused' {
        if (-not $ws) { Skip-Test $wsSkip }
        New-CheckupProject -Id 'cu-nobuild' -Checks @{ accepted = @() }
        Remove-Item -LiteralPath (Join-Path (Join-Path $tmpRoot 'cu-src-cu-nobuild') 'game-src.nes') -Force
        $env:BROKENDEV_HOME = Join-Path $tmpRoot 'cuhome2'; $env:BROKENDEV_PROJECTS = Join-Path $tmpRoot 'cuprojects'; $env:BROKENDEV_WORKSHOP = $ws
        $env:BROKENNES_BUGS_DIR = Join-Path $tmpRoot 'cubugs'
        try {
            $r = Invoke-BdCheckup -Target 'cu-nobuild' -Exe $ws -NoSuites -Quiet
            Assert-True (-not $r.ok) 'a project that does not build passed checkup'
            $st = @{}; foreach ($s in $r.projects[0].steps) { $st[$s.step] = $s.state }
            Assert-Equal $st['build'] 'FAIL'; Assert-True (@($r.projects[0].steps | Where-Object { $_.step -eq 'build' })[0].detail -match "step 'copy' failed") 'the build did not fail at its step'; Assert-Equal $st['verify'] 'skip'; Assert-Equal $st['findings'] 'skip'
            Assert-Throws { Invoke-BdCheckup -Target 'no-such-project' -Exe $ws -NoSuites -Quiet } 'unknown project'
        } finally { Restore-Env $savedEnv }
    }
    # the coverage map (D9): the RAM-colour ROM runs 35 bytes of code at PRG offset 0 and loops; nothing at offset $100
    $covAcc = @{ accepted = @(@{ rule = 'nes.ram-uninit'; at = 'ram:$07FC'; reason = 'test: the ROM paints its backdrop with power-on RAM on purpose' }) }
    Test-Case 'coverage: verify maps the code run per bank (the ROM''s 35 code bytes, each instruction''s operands included) and a mustRun on code that runs PASSES' {
        if (-not $ws) { Skip-Test $wsSkip }
        New-VerifyManifest -Id 'v-cov-ok' -Scenarios $chkScen -Extra @{ checks = $covAcc; coverage = @{ mustRun = @('rom:$0', 'prg:$20'); minPercent = 1 } }
        $v = Invoke-WsVerify -Id 'v-cov-ok' -Rom $ramRom
        Assert-Equal $v.Exit 0 "output: $(Select-BdTail $v.Output 10)"
        $c = $v.Json.coverage
        Assert-True ($null -ne $c -and $c.ok) "no coverage or not ok: $($c | ConvertTo-Json -Compress -Depth 4)"
        Assert-Equal $c.ranBytes 35 'the bytes run (every instruction of the program, operands included)'
        Assert-Equal @($c.banks).Count 1
        Assert-True (@($c.mustRun | Where-Object { $_.ran }).Count -eq 2) 'the mustRun entries did not run'
    }
    Test-Case 'coverage: NEGATIVE: a mustRun on code that never runs, an unknown symbol and a floor above the share FAIL verify, each named' {
        if (-not $ws) { Skip-Test $wsSkip }
        New-VerifyManifest -Id 'v-cov-bad' -Scenarios $chkScen -Extra @{ checks = $covAcc; coverage = @{ mustRun = @('rom:$100', 'no_such_routine'); minPercent = 99 } }
        $v = Invoke-WsVerify -Id 'v-cov-bad' -Rom $ramRom
        Assert-Equal $v.Exit 1 "output: $(Select-BdTail $v.Output 10)"
        $p = @($v.Json.coverage.problems) -join ' | '
        Assert-True ($p -match "'rom:\`$100' \(rom:\`$100\) never ran") "problems: $p"
        Assert-True ($p -match "'no_such_routine' is no ROM symbol") "problems: $p"
        Assert-True ($p -match 'under the floor of 99%') "problems: $p"
        Assert-True ($v.Output -match '\[coverage\] FAIL') 'the summary does not say coverage failed'
    }
    Test-Case 'coverage: NEGATIVE control of the map itself: with no coverage asked, verify adds no coverage section (it costs nothing when off)' {
        if (-not $ws) { Skip-Test $wsSkip }
        $v = Invoke-WsVerify -Id 'v-chk-acc' -Rom $ramRom
        Assert-Equal $v.Exit 0
        Assert-True ($null -eq (Get-BdProp $v.Json 'coverage' $null)) 'a coverage section appeared without being asked for'
    }
    Test-Case 'budgets: under the limit passes, at 90% of it WARNS, over it FAILS (busyPercent, measured first)' {
        if (-not $ws) { Skip-Test $wsSkip }
        New-VerifyManifest -Id 'v-bud-probe' -Scenarios $chkScen -Extra @{ checks = @{ accepted = @(@{ rule = 'nes.*'; at = '*'; reason = 'test' }) }; budgets = @{ busyPercent = @{ max = 100 } } }
        $p = Invoke-WsVerify -Id 'v-bud-probe' -Rom $testRom
        $busy = [double]($p.Json.checks.budgets | Where-Object { $_.id -eq 'busyPercent' }).value
        Assert-True ($busy -gt 0) "busy $busy"
        foreach ($case in @(@{ max = [math]::Round($busy * 2, 2); want = 'ok'; exit = 0 }, @{ max = [math]::Round($busy / 0.95, 2); want = 'warn'; exit = 0 }, @{ max = [math]::Round($busy / 2, 2); want = 'fail'; exit = 1 })) {
            New-VerifyManifest -Id 'v-bud' -Scenarios $chkScen -Extra @{ checks = @{ accepted = @(@{ rule = 'nes.*'; at = '*'; reason = 'test' }) }; budgets = @{ busyPercent = @{ max = $case.max } } }
            $v = Invoke-WsVerify -Id 'v-bud' -Rom $testRom
            $b = $v.Json.checks.budgets | Where-Object { $_.id -eq 'busyPercent' }
            Assert-Equal $b.status $case.want "max $($case.max) for busy $busy"
            Assert-Equal $v.Exit $case.exit "max $($case.max)"
        }
    }
    Test-Case 'budgets: the report shows the trend since the previous build (the history file keeps one entry per ROM sha256)' {
        if (-not $ws) { Skip-Test $wsSkip }
        New-VerifyManifest -Id 'v-trend' -Scenarios $chkScen -Extra @{ checks = @{ accepted = @(@{ rule = 'nes.*'; at = '*'; reason = 'test' }) }; budgets = @{ romBankFree = @{ min = 0 }; busyPercent = @{ max = 100 } } }
        $hist = Join-Path $tmpRoot 'trend-history.json'
        $romB = Join-Path $tmpRoot 'ramcolour-b.nes'
        $bb = [System.IO.File]::ReadAllBytes($ramRom); $bb[16 + 0x2000] = 0x60; [System.IO.File]::WriteAllBytes($romB, $bb)   # another build: a byte of padding changed
        $outA = Join-Path $tmpRoot 'v-trend-a.json'; $outB = Join-Path $tmpRoot 'v-trend-b.json'
        [void](Invoke-Ws -WsArgs @('--brokendev', 'verify', 'v-trend', '--manifests', $vProj, '--home', $wsHome, '--rom', $ramRom, '--out', $outA, '--history', $hist) -ExtraEnv @{ BROKENNES_DEV = '1' })
        [void](Invoke-Ws -WsArgs @('--brokendev', 'verify', 'v-trend', '--manifests', $vProj, '--home', $wsHome, '--rom', $ramRom, '--out', $outA, '--history', $hist) -ExtraEnv @{ BROKENNES_DEV = '1' })
        $a = Read-BdJson $outA
        Assert-True ($null -eq $a.checks.previousBuild) 'the same ROM verified twice has no previous build'
        [void](Invoke-Ws -WsArgs @('--brokendev', 'verify', 'v-trend', '--manifests', $vProj, '--home', $wsHome, '--rom', $romB, '--out', $outB, '--history', $hist) -ExtraEnv @{ BROKENNES_DEV = '1' })
        $b = Read-BdJson $outB
        $bank = $b.checks.budgets | Where-Object { $_.id -eq 'romBankFree' }
        Assert-True ($null -ne $b.checks.previousBuild -and $null -ne $bank.previous) "no trend: $($b.checks.budgets | ConvertTo-Json -Compress -Depth 4)"
        Assert-True ([double]$bank.delta -lt 0) "the changed byte should cost free space: delta $($bank.delta)"
        $h = Read-BdJson $hist   # (assigned first: Windows PowerShell's ConvertFrom-Json hands an array over as one object)
        Assert-Equal $h.Count 2 'one history entry per build'
    }
    Test-Case 'NEGATIVE: verify exits 2 for a ROM that does not exist' {
        if (-not $ws) { Skip-Test $wsSkip }
        $v = Invoke-WsVerify -Id 'v-pass' -Rom (Join-Path $tmpRoot 'nope.nes')
        Assert-Equal $v.Exit 2
    }
    # the compat runner: the same contract as the other emulators' adapters
    $crOut = Join-Path $tmpRoot 'crun'
    function Invoke-WsRun {
        param($Run, [string[]]$More = @())
        New-Item -ItemType Directory -Force -Path $Run.Out | Out-Null
        $sp = Join-Path $Run.Out 'spec.txt'
        [System.IO.File]::WriteAllText($sp, $(if ($Run.ContainsKey('Text')) { $Run.Text } else { New-BdCompatSpecText -Run $Run }))
        $r = Invoke-Ws -WsArgs (@('--brokendev', 'run', '--spec', $sp) + $More) -ExtraEnv @{ BROKENNES_DEV = '1' }
        $res = $null; $rp = Join-Path $Run.Out 'result.txt'
        if (Test-Path $rp) { $res = ConvertFrom-BdCompatResult -Text ([System.IO.File]::ReadAllText($rp)) }
        [pscustomobject]@{ Exit = $r.ExitCode; Output = $r.Output; Result = $res }
    }
    Test-Case 'compat run: BrokenNes writes the contract (a value per frame, pictures, the end) for the RAM-colour ROM; power-on RAM shows in the reads' {
        if (-not $ws) { Skip-Test $wsSkip }
        $run = @{ Console = 'nes'; Rom = $ramRom; Out = (Join-Path $crOut 'a'); SaveDir = (Join-Path $crOut 'sa'); Frames = 30; Reads = @(@{ Name = 'b7fc'; Domain = 'ram'; Addr = 0x7FC }); Input = ''; Shots = @(30) }
        $x = Invoke-WsRun $run @('--ram', 'zeros')
        if ($x.Exit -eq 2 -and $x.Output -match "Unknown BrokenDev command 'run'") { Skip-Test 'this Workshop predates brokendev run (rebuild it: brokendev.ps1 build-workshop)' }
        Assert-Equal $x.Exit 0 (Select-BdTail $x.Output 5)
        Assert-Equal @($x.Result.Errors).Count 0 ($x.Result.Errors -join '; ')
        Assert-Equal $x.Result.End.State 'done'; Assert-Equal $x.Result.Frames.Count 30; Assert-Equal $x.Result.Values[30][0] 0
        Assert-True (Test-Path (Join-Path $run.Out $x.Result.Shots[30])) 'no picture'
        $run2 = $run.Clone(); $run2.Out = Join-Path $crOut 'b'
        $y = Invoke-WsRun $run2 @('--ram', 'ones')
        Assert-Equal $y.Result.Values[30][0] 255
    }
    Test-Case 'NEGATIVE: the compat runner refuses a spec reading SNES work RAM on a NES (exit 2), and an unknown directive' {
        if (-not $ws) { Skip-Test $wsSkip }
        $x = Invoke-WsRun @{ Out = (Join-Path $crOut 'c'); Text = "console nes`nrom $ramRom`nout $(Join-Path $crOut 'c')`nframes 5`nread a wram 10`n" }
        if ($x.Exit -eq 2 -and $x.Output -match "Unknown BrokenDev command 'run'") { Skip-Test 'this Workshop predates brokendev run' }
        Assert-Equal $x.Exit 2
        $x = Invoke-WsRun @{ Out = (Join-Path $crOut 'd'); Text = "console nes`nrom $ramRom`nout $(Join-Path $crOut 'd')`nframes 5`nteleport 3`n" }
        Assert-Equal $x.Exit 2
    }

    # the WideMess devkit end to end: the project 'new widemess' made above, built with PVSnesLib and verified on the WideMess console
    Test-Case 'a game from new widemess builds and PASSES verify on the WideMess console (unlocked, 400 wide, its layers, sprite and song); NEGATIVE: the same game built without the unlock FAILS every WideMess check' {
        if (-not $ws) { Skip-Test $wsSkip }
        if (-not $wmsPy) { Skip-Test 'no Python with pillow and numpy' }
        if (-not $wmsPvs) { Skip-Test 'PVSnesLib not found (BROKENDEV_PVSNESLIB, or the Super VRUN workspace beside the repository)' }
        if (-not (Test-Path -LiteralPath (Join-Path $wmsGame 'wms.json'))) { Skip-Test 'the new widemess test above did not make its project' }
        $env:BROKENDEV_HOME = Join-Path $tmpRoot 'wmshome'; $env:BROKENDEV_PROJECTS = $wmsProj; $env:BROKENDEV_WORKSHOP = $ws
        try {
            $rec = Invoke-BdProjectBuild -Id 'test-game'
            Assert-True $rec.ok 'the build failed'
            $v = Invoke-BdProjectVerify -Id 'test-game' -Exe $ws
            Assert-True $v.ok ('verify failed: ' + ((@($v.scenarios | ForEach-Object { $_.checks } | Where-Object { -not $_.ok } | ForEach-Object { "$($_.id): $($_.detail)" })) -join '; '))
            $ids = @($v.scenarios[0].checks | ForEach-Object { $_.id })
            foreach ($want in 'wms-unlocked', 'wms-width', 'wms-layers', 'wms-sprites', 'wms-voices') { Assert-True ($ids -contains $want) "verify did not check $want" }
            # the negative control: the same source with -D WMS_NOUNLOCK, through the same recipe
            $no = Join-Path $tmpRoot 'wms-nounlock'
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $wmsTools 'wmsbuild.ps1') -Project $wmsGame -Workshop $ws -Python $wmsPy.Path -Pvsneslib $wmsPvs -Define WMS_NOUNLOCK -Out $no | Out-Null
            Assert-True (Test-Path -LiteralPath (Join-Path $no 'test_game.wms')) 'the no-unlock build made no ROM'
            $out = Join-Path $tmpRoot 'wms-nounlock.json'
            $r = Invoke-Ws -WsArgs @('--brokendev', 'verify', 'test-game', '--manifests', $wmsProj, '--home', $env:BROKENDEV_HOME, '--rom', (Join-Path $no 'test_game.wms'), '--out', $out) -ExtraEnv @{ BROKENNES_DEV = '1' }
            Assert-Equal $r.ExitCode 1
            $bad = @((Read-BdJson $out).scenarios[0].checks | Where-Object { -not $_.ok } | ForEach-Object { $_.id })
            foreach ($want in 'wms-unlocked', 'wms-width', 'wms-layers', 'wms-sprites', 'wms-voices', 'wms-fast-cpu') { Assert-True ($bad -contains $want) "the no-unlock game passed $want" }
        } finally { Restore-Env $savedEnv }
    }

    # the Game Boy end to end (D2): the hello world of VRUN: Vaults of Obsolete Code, frozen at V0 (the game repo's commit $vocV0Rev, taken
    # with git archive into a scratch folder: the live game moves on and its own folders are never written), built with the pinned GBDK,
    # verified with the V0 fixture manifest; the negative control is the same source with a planted read of work RAM before the start-up clears it
    Test-Case 'the Game Boy hello world builds with GBDK and PASSES verify (Color and original, the sprite, the beep, the boot counter over three power-ons, no hardware finding); NEGATIVE: a planted read before the RAM clear FAILS verify with a new gb.ram-uninit' {
        if (-not $ws) { Skip-Test $wsSkip }
        $gst = Get-BdToolchainState -Spec (Get-BdToolchainSpec -Id gbdk) -SkipZip
        if ($gst.State -ne 'ready') { Skip-Test "GBDK-2020 is not ready here ($($gst.Detail))" }
        $voc = Get-VocV0Source
        $ps5 = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $verifyRom = {
            param([string]$Dir, [string]$Json)
            Invoke-Ws -WsArgs @('--brokendev', 'verify', 'vrun-voc-v0', '--manifests', $gbFixtures, '--home', (Join-Path $tmpRoot 'gbhome'), '--rom', (Join-Path $Dir 'vrun_voc.gbc'), '--out', $Json) -ExtraEnv @{ BROKENNES_DEV = '1' }
        }
        $good = Join-Path $tmpRoot 'voc-good'
        $b = Invoke-BdProcess -FilePath $ps5 -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $voc 'build.ps1'), '-Gbdk', $gst.Dir, '-Out', $good) -TimeoutSec 300
        Assert-Equal $b.ExitCode 0 "the build failed: $(Select-BdTail $b.Output 6)"
        $json = Join-Path $tmpRoot 'voc-good.json'
        $r = & $verifyRom $good $json
        $v = Read-BdJson $json
        Assert-True ($r.ExitCode -eq 0 -and $v.ok) ('verify failed: ' + ((@($v.scenarios | ForEach-Object { $_.checks } | Where-Object { -not $_.ok } | ForEach-Object { "$($_.id): $($_.detail)" })) -join '; '))
        Assert-Equal (@($v.scenarios | ForEach-Object { $_.id }) -join ',') 'color,original,idle,second-boot,third-boot-original'
        Assert-Equal $v.checks.newFindings 0
        Assert-True ((@($v.scenarios[0].checks | ForEach-Object { $_.id })) -contains 'memory-player_x') 'verify did not read player_x by its symbol'
        Assert-True ((@($v.scenarios[1].checks | ForEach-Object { $_.id })) -contains 'max-distinct-colors') 'the original Game Boy scenario did not check its shades'
        # the debug port (D7): the dev build's messages reach verify (expect.port: the markers, the prints, the move_player timer)
        $pc = @($v.scenarios[0].checks | Where-Object { $_.id -like 'debug-port-*' })
        Assert-True ($pc.Count -ge 5 -and @($pc | Where-Object { -not $_.ok }).Count -eq 0) ('the debug port checks: ' + ((@($pc | ForEach-Object { "$($_.id)=$($_.ok)" })) -join ', '))
        $bad = Join-Path $tmpRoot 'voc-planted'
        $b = Invoke-BdProcess -FilePath $ps5 -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $voc 'build.ps1'), '-Gbdk', $gst.Dir, '-Out', $bad, '-Define', 'VOC_PLANT_UNINIT') -TimeoutSec 300
        Assert-Equal $b.ExitCode 0 "the planted build failed: $(Select-BdTail $b.Output 6)"
        $json = Join-Path $tmpRoot 'voc-planted.json'
        $r = & $verifyRom $bad $json
        Assert-Equal $r.ExitCode 1
        $v = Read-BdJson $json
        $new = @($v.checks.findings | Where-Object { -not $_.accepted })
        Assert-True (@($new | Where-Object { $_.rule -eq 'gb.ram-uninit' }).Count -ge 1) "the planted read was not a new gb.ram-uninit (new: $((@($new | ForEach-Object { $_.rule + ' ' + $_.place })) -join ', '))"
    }
    # the live game as it is today, with the shipped manifest: built from its live workspace into a scratch folder (its own folders are
    # never written); it moves on with the user's work, so its scenarios are the manifest's own and this test only asks that they pass
    Test-Case 'the live game (vrun-voc, the shipped manifest) builds with GBDK into a scratch folder and PASSES verify with no new hardware finding; SKIP without the workspace, GBDK or a Workshop' {
        if (-not $ws) { Skip-Test $wsSkip }
        $gst = Get-BdToolchainState -Spec (Get-BdToolchainSpec -Id gbdk) -SkipZip
        if ($gst.State -ne 'ready') { Skip-Test "GBDK-2020 is not ready here ($($gst.Detail))" }
        $voc = Find-BdLiveWorkspace -Name 'VRUN_VOC_dev'
        if (-not $voc -or -not (Test-Path -LiteralPath (Join-Path $voc 'build.ps1'))) { Skip-Test 'the VRUN_VOC_dev workspace is not beside the repository' }
        $ps5 = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $live = Join-Path $tmpRoot 'voc-live'
        $b = Invoke-BdProcess -FilePath $ps5 -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $voc 'build.ps1'), '-Gbdk', $gst.Dir, '-Out', $live) -TimeoutSec 600
        Assert-Equal $b.ExitCode 0 "the build failed: $(Select-BdTail $b.Output 6)"
        $json = Join-Path $tmpRoot 'voc-live.json'
        $r = Invoke-Ws -WsArgs @('--brokendev', 'verify', 'vrun-voc', '--manifests', (Join-Path $bd 'projects'), '--home', (Join-Path $tmpRoot 'gbhome'), '--rom', (Join-Path $live 'vrun_voc.gbc'), '--out', $json) -ExtraEnv @{ BROKENNES_DEV = '1' }
        $v = Read-BdJson $json
        Assert-True ($r.ExitCode -eq 0 -and $v.ok) ('verify failed: ' + ((@($v.scenarios | ForEach-Object { $_.checks } | Where-Object { -not $_.ok } | ForEach-Object { "$($_.id): $($_.detail)" })) -join '; '))
        Assert-Equal (@($v.scenarios | ForEach-Object { $_.id }) -join ',') (@($gbM.verify.scenarios | ForEach-Object { $_.id }) -join ',')
        Assert-Equal $v.checks.newFindings 0
    }
    # ------------------------------------------------------------------ the debug port (D7)
    Write-Host 'the debug port (D7)'
    Test-Case 'the debug port on the Game Boy hello world: NEGATIVE: a planted bug the dev build''s assert catches FAILS verify (debug-port-asserts, a port.assert finding no manifest can accept); the GBDK release-strip proof: the release ROM is byte-identical to the source without any DBG_ call' {
        if (-not $ws) { Skip-Test $wsSkip }
        $gst = Get-BdToolchainState -Spec (Get-BdToolchainSpec -Id gbdk) -SkipZip
        if ($gst.State -ne 'ready') { Skip-Test "GBDK-2020 is not ready here ($($gst.Detail))" }
        $voc = Get-VocV0Source
        $ps5 = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $bad = Join-Path $tmpRoot 'voc-assert'
        $b = Invoke-BdProcess -FilePath $ps5 -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $voc 'build.ps1'), '-Gbdk', $gst.Dir, '-Out', $bad, '-Define', 'VOC_PLANT_ASSERT') -TimeoutSec 300
        Assert-Equal $b.ExitCode 0 "the planted build failed: $(Select-BdTail $b.Output 6)"
        $json = Join-Path $tmpRoot 'voc-assert.json'
        $r = Invoke-Ws -WsArgs @('--brokendev', 'verify', 'vrun-voc-v0', '--manifests', $gbFixtures, '--home', (Join-Path $tmpRoot 'gbhome'), '--rom', (Join-Path $bad 'vrun_voc.gbc'), '--out', $json) -ExtraEnv @{ BROKENNES_DEV = '1' }
        Assert-Equal $r.ExitCode 1
        $v = Read-BdJson $json
        $fail = @($v.scenarios[0].checks | Where-Object { $_.id -eq 'debug-port-asserts' -and -not $_.ok })
        Assert-True ($fail.Count -eq 1 -and $fail[0].detail -match 'player_x' -and $fail[0].detail -match '_move_player') "no failed debug-port-asserts naming the assert and move_player: $((@($v.scenarios[0].checks | Where-Object { -not $_.ok } | ForEach-Object { "$($_.id): $($_.detail)" })) -join '; ')"
        $pa = @($v.checks.findings | Where-Object { $_.rule -eq 'port.assert' })
        Assert-True ($pa.Count -ge 1 -and -not $pa[0].accepted) "the checks did not report a new port.assert ($((@($v.checks.findings | ForEach-Object { $_.rule })) -join ', '))"
        $proof = Invoke-BdProcess -FilePath $ps5 -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $bd 'debugport\gbdk\build-proof.ps1'), '-Game', $voc, '-Gbdk', $gst.Dir, '-Out', (Join-Path $tmpRoot 'gbproof')) -TimeoutSec 600
        Assert-True ($proof.ExitCode -eq 0 -and $proof.Output -match 'PASS') "the GBDK proof: $(Select-BdTail $proof.Output 6)"
    }
    Test-Case 'the NESFab library''s release-strip proof: the sample built with brokendev_dbg_off.fab is byte-identical to the sample without any dbg_ call, the dev build differs and its port speaks (prints, the assert, the timer); SKIP without NESFab' {
        if (-not $ws) { Skip-Test $wsSkip }
        $nf = 'C:\Users\philt\OneDrive\Documents\PROJECTS\VRUN_Nes_Dev\sys0\simulations\VRUN\tools\nesfab'
        if ($env:BROKENDEV_NESFAB) { $nf = $env:BROKENDEV_NESFAB }
        if (-not (Test-Path -LiteralPath (Join-Path $nf 'nesfab.exe'))) { Skip-Test "no NESFab at $nf (BROKENDEV_NESFAB)" }
        $ps5 = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $out = Join-Path $tmpRoot 'nfproof'
        $proof = Invoke-BdProcess -FilePath $ps5 -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $bd 'debugport\nesfab\build-proof.ps1'), '-NesFab', $nf, '-Out', $out) -TimeoutSec 600
        Assert-True ($proof.ExitCode -eq 0 -and $proof.Output -match 'PASS') "the NESFab proof: $(Select-BdTail $proof.Output 6)"
        $json = Join-Path $tmpRoot 'nfport.json'
        $r = Invoke-Ws -WsArgs @('--port', '--rom', (Join-Path $out 'dev\game.nes'), '--sym', (Join-Path $out 'dev\game.mlb'), '--frames', '10', '--quiet', '--json', $json)
        Assert-Equal $r.ExitCode 1 '(the sample asserts from its fourth frame)'
        $j = Read-BdJson $json
        Assert-True ($j.counts.print -ge 5 -and $j.counts.assert -ge 1 -and $j.counts.marker -eq 2 -and @($j.timers | Where-Object { $_.name -eq 'timer 1' -or $_.name -eq 'work' }).Count -eq 1) "the dev build's port: $($j.counts | ConvertTo-Json -Compress)"
        $rr = Invoke-Ws -WsArgs @('--port', '--rom', (Join-Path $out 'release\game.nes'), '--frames', '10', '--quiet', '--json', $json)
        Assert-Equal $rr.ExitCode 0 '(the release build sends nothing)'
        Assert-Equal (Read-BdJson $json).writes 0
    }
    Test-Case 'the ca65 include assembled with the pinned cc65: the release build is byte-identical to the sample without any DBG_ line, the dev build''s port speaks (prints with a byte and a word, both markers, the named timer to the cycle) with no assert; NEGATIVE: the planted assert fails the run; SKIP without the cc65 toolchain' {
        if (-not $ws) { Skip-Test $wsSkip }
        $cst = Get-BdToolchainState -Spec (Get-BdToolchainSpec -Id cc65) -SkipZip
        if ($cst.State -ne 'ready') { Skip-Test "the cc65 toolchain is $($cst.State): $($cst.Detail)" }
        $ps5 = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $out = Join-Path $tmpRoot 'caproof'
        $proof = Invoke-BdProcess -FilePath $ps5 -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $bd 'debugport\ca65\build-proof.ps1'), '-Cc65', $cst.Dir, '-Out', $out) -TimeoutSec 300
        Assert-True ($proof.ExitCode -eq 0 -and $proof.Output -match 'PASS') "the ca65 proof: $(Select-BdTail $proof.Output 6)"
        $json = Join-Path $tmpRoot 'caport.json'
        $r = Invoke-Ws -WsArgs @('--port', '--rom', (Join-Path $out 'dev\game.nes'), '--frames', '10', '--quiet', '--json', $json)
        Assert-Equal $r.ExitCode 0 '(the dev build asserts nothing)'
        $j = Read-BdJson $json
        $t = @($j.timers | Where-Object { $_.name -eq 'work' })
        Assert-True ($j.counts.print -eq 17 -and $j.counts.assert -eq 0 -and $j.counts.marker -eq 2 -and $j.counts.error -eq 0 -and $t.Count -eq 1 -and $t[0].calls -eq 8 -and $t[0].worst -eq 24) "the dev build's port: $($j.counts | ConvertTo-Json -Compress) timers $($j.timers | ConvertTo-Json -Compress)"
        $p = Invoke-Ws -WsArgs @('--port', '--rom', (Join-Path $out 'planted\game.nes'), '--frames', '10', '--quiet', '--json', $json)
        Assert-Equal $p.ExitCode 1 '(the planted assert fails from the fourth frame)'
        Assert-True ((Read-BdJson $json).counts.assert -eq 5) 'the planted build did not assert five times'
        $rr = Invoke-Ws -WsArgs @('--port', '--rom', (Join-Path $out 'release\game.nes'), '--frames', '10', '--quiet', '--json', $json)
        Assert-Equal $rr.ExitCode 0 '(the release build sends nothing)'
        Assert-Equal (Read-BdJson $json).writes 0
    }
    Test-Case 'the debug port on the WideMess sample: the dev build PASSES verify with its markers and timers (C and WLA-DX); the release build is byte-identical to the committed devkit_sample.wms (built before the calls were written); NEGATIVE: a planted bug the assert catches FAILS verify' {
        if (-not $ws) { Skip-Test $wsSkip }
        if (-not $wmsPy) { Skip-Test 'no Python with pillow and numpy' }
        if (-not $wmsPvs) { Skip-Test 'PVSnesLib not found (BROKENDEV_PVSNESLIB, or the Super VRUN workspace beside the repository)' }
        $sample = Join-Path (Split-Path -Parent $bd) 'WideMess\sample'
        $build = {
            param([string]$Out, [string[]]$Define)
            $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $wmsTools 'wmsbuild.ps1'), '-Project', $sample, '-Workshop', $ws, '-Python', $wmsPy.Path, '-Pvsneslib', $wmsPvs, '-Out', $Out)
            if ($Define) { $a += @('-Define', ($Define -join ',')) }
            $r = Invoke-BdProcess -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -Arguments $a -TimeoutSec 900
            Assert-True (Test-Path -LiteralPath (Join-Path $Out 'widemess_sample.wms')) "the build made no ROM: $(Select-BdTail $r.Output 6)"
            Join-Path $Out 'widemess_sample.wms'
        }
        $rel = & $build (Join-Path $tmpRoot 'wms-rel') @()
        $committed = Join-Path (Split-Path -Parent $bd) 'WideMess\testroms\bin\devkit_sample.wms'
        Assert-Equal (Get-FileHash $rel).Hash (Get-FileHash $committed).Hash '(the release build is not the committed sample)'
        $dev = & $build (Join-Path $tmpRoot 'wms-dev') @('BROKENDEV_DEBUG')
        Assert-True ((Get-FileHash $dev).Hash -ne (Get-FileHash $rel).Hash) 'the dev build is the release build'
        $json = Join-Path $tmpRoot 'wms-dev.json'
        $r = Invoke-Ws -WsArgs @('--brokendev', 'verify', 'widemess-sample', '--manifests', (Join-Path $bd 'projects'), '--home', (Join-Path $tmpRoot 'wmshome2'), '--rom', $dev, '--only', 'play', '--out', $json) -ExtraEnv @{ BROKENNES_DEV = '1' }
        $v = Read-BdJson $json
        $pc = @($v.scenarios[0].checks | Where-Object { $_.id -like 'debug-port-*' })
        Assert-True ($r.ExitCode -eq 0 -and $pc.Count -ge 5 -and @($pc | Where-Object { -not $_.ok }).Count -eq 0) ('verify: ' + ((@($v.scenarios[0].checks | Where-Object { -not $_.ok -or $_.id -like 'debug-port-*' } | ForEach-Object { "$($_.id)=$($_.ok) $($_.detail)" })) -join '; '))
        $bad = & $build (Join-Path $tmpRoot 'wms-plant') @('BROKENDEV_DEBUG', 'SAMPLE_PLANT_ASSERT')
        $r2 = Invoke-Ws -WsArgs @('--brokendev', 'verify', 'widemess-sample', '--manifests', (Join-Path $bd 'projects'), '--home', (Join-Path $tmpRoot 'wmshome2'), '--rom', $bad, '--only', 'play', '--out', $json) -ExtraEnv @{ BROKENNES_DEV = '1' }
        Assert-Equal $r2.ExitCode 1
        $f = @((Read-BdJson $json).scenarios[0].checks | Where-Object { $_.id -eq 'debug-port-asserts' -and -not $_.ok })
        Assert-True ($f.Count -eq 1 -and $f[0].detail -match 'ship_x') "no failed debug-port-asserts: $((@((Read-BdJson $json).scenarios[0].checks | Where-Object { -not $_.ok } | ForEach-Object { $_.id })) -join ', ')"
    }
    # ------------------------------------------------------------------ movies, promoted movie tests and bisect (D5)
    Write-Host 'movies and bisect (D5)'
    Test-Case 'the movie format''s name and extension come from the Workshop (one constant); NEGATIVE: an unknown --movie verb fails' {
        if (-not $ws) { Skip-Test $wsSkip }
        $env:BROKENDEV_WORKSHOP = $ws
        try {
            $script:BdMovieFormat = $null
            $f = Get-BdMovieFormat
            Assert-True ($f.extension -match '^\.[a-z0-9]+$' -and $f.name -and $f.magic) "format: $($f | ConvertTo-Json -Compress)"
            Assert-Equal (Get-BdMovieExtension) $f.extension
            $r = Invoke-BdWorkshopMovie -Arguments @('nonsense')
            Assert-Equal $r.ExitCode 2
        } finally { Restore-Env $savedEnv }
    }
    Test-Case 'movie record + promote: the movie becomes a verify test that passes; NEGATIVE: the promoted movie with Start one frame late FAILS verify (movie-in-sync), promote refuses a movie that is off course, a movies entry without a why is rejected' {
        if (-not $ws) { Skip-Test $wsSkip }
        $mh = Join-Path $tmpRoot 'moviehome'; $mp = Join-Path $tmpRoot 'movieprojects'; $src = Join-Path $tmpRoot 'moviegame'
        New-Item -ItemType Directory -Force -Path $mh, (Join-Path $mp 'mgame'), $src | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $src 'game.txt'), "version 1`nstart Start`nfloor 1`nbonus 1`n")
        [System.IO.File]::WriteAllText((Join-Path $src 'build.ps1'), @'
param([Parameter(Mandatory)][string]$Workshop)
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
New-Item -ItemType Directory -Force -Path (Join-Path $here 'build') | Out-Null
$p = Start-Process -FilePath $Workshop -ArgumentList @('--movie', 'build-planted', '--settings', "`"$(Join-Path $here 'game.txt')`"", '--out', "`"$(Join-Path $here 'build\game.nes')`"") -Wait -PassThru -NoNewWindow
exit $p.ExitCode
'@)
        $man = [ordered]@{
            schema = 1; id = 'mgame'; title = 'the movie tests'' planted game'; family = 'test'; kind = 'nes'; console = 'nes'; description = 'D5 run-tests'
            source = @{ folder = $src }; requires = @{ tools = @('ps5'); projects = @(); inputs = @() }
            build = @{ steps = @(@{ name = 'build'; shell = 'ps5'; cwd = '{src}'; file = '{src}/build.ps1'; args = @('-Workshop', '{workshop}'); timeoutSec = 120 })
                       outputs = @(@{ id = 'rom'; path = '{src}/build/game.nes'; publishAs = 'game.nes'; kind = 'nes' }, @{ id = 'mlb'; path = '{src}/build/game.mlb'; publishAs = 'game.mlb'; kind = 'symbols' }) }
            verify = @{ scenarios = @(@{ id = 'play'; frames = 600; input = '60:Start,66:,200:A,260:' }) }; knownIssues = @()
        }
        Save-BdJson -Object $man -Path (Join-Path (Join-Path $mp 'mgame') 'project.json')
        $env:BROKENDEV_HOME = $mh; $env:BROKENDEV_PROJECTS = $mp; $env:BROKENDEV_WORKSHOP = $ws; $script:BdMovieFormat = $null
        try {
            [void](Invoke-BdProjectBuild -Id 'mgame')
            $movie = Invoke-BdMovieRecord -Id 'mgame' -Scenario 'play' -Course @('app_state')
            $pr = Invoke-BdMoviePromote -Id 'mgame' -Movie $movie -Name 'first' -Why 'Start, then A: three lives lost down to one'
            Assert-True (Test-Path -LiteralPath $pr.File) 'the promoted movie was not copied beside the manifest'
            $m2 = Read-BdManifest -Id 'mgame'
            Assert-Equal (@($m2.movies).Count) 1
            Assert-Equal ([string]$m2.verify.scenarios[0].input) '60:Start,66:,200:A,260:' 'the manifest''s other fields changed'
            $v = Invoke-BdProjectVerify -Id 'mgame' -Exe $ws
            # the verbs themselves (the dispatcher, its parameters): verify and movie list through brokendev.ps1
            $psExe = if ($PSVersionTable.PSEdition -eq 'Core') { (Get-Process -Id $PID).Path } else { Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe' }
            $vv = Invoke-BdProcess -FilePath $psExe -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $bd 'brokendev.ps1'), 'verify', 'mgame') -TimeoutSec 600 -Env @{ BROKENDEV_HOME = $mh; BROKENDEV_PROJECTS = $mp; BROKENDEV_WORKSHOP = $ws }
            Assert-True ($vv.ExitCode -eq 0 -and $vv.Output -notmatch 'FAIL') "brokendev.ps1 verify mgame: exit $($vv.ExitCode) $(Select-BdTail $vv.Output 4)"
            $vl = Invoke-BdProcess -FilePath $psExe -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $bd 'brokendev.ps1'), 'movie', 'list', 'mgame') -TimeoutSec 120 -Env @{ BROKENDEV_HOME = $mh; BROKENDEV_PROJECTS = $mp; BROKENDEV_WORKSHOP = $ws }
            Assert-True ($vl.ExitCode -eq 0 -and $vl.Output -match 'first') "brokendev.ps1 movie list mgame: exit $($vl.ExitCode) $(Select-BdTail $vl.Output 4)"
            $ms = @($v.scenarios | Where-Object { $_.id -eq 'movie-first' })
            Assert-True ($v.ok -and $ms.Count -eq 1) "verify: ok $($v.ok), scenarios $(@($v.scenarios | ForEach-Object { $_.id }) -join ', ')"
            Assert-True (@($ms[0].checks | Where-Object { $_.id -eq 'movie-in-sync' -and $_.ok }).Count -eq 1) 'no passing movie-in-sync check'
            # the negative control: the same movie with Start one frame late, on the same build
            $txt = [System.IO.File]::ReadAllText($pr.File)
            [System.IO.File]::WriteAllText($pr.File, $txt.Replace("i 60 Start -", "i 61 Start -"))
            $v2 = Invoke-BdProjectVerify -Id 'mgame' -Exe $ws
            $bad = @(@($v2.scenarios | Where-Object { $_.id -eq 'movie-first' })[0].checks | Where-Object { -not $_.ok } | ForEach-Object { $_.id })
            Assert-True (-not $v2.ok -and $bad -contains 'movie-in-sync') "the late-Start movie passed verify (failed checks: $($bad -join ', '))"
            # promote refuses a movie that does not replay in sync on today's build
            $late = Join-Path $tmpRoot 'late.bnm'
            [System.IO.File]::WriteAllText($late, $txt.Replace("i 60 Start -", "i 61 Start -"))
            Assert-Throws { Invoke-BdMoviePromote -Id 'mgame' -Movie $late -Name 'late' -Why 'x' } 'does not replay in sync'
            [System.IO.File]::WriteAllText($pr.File, $txt)
            # a movies entry without its why is rejected by the manifest check
            $bm = Read-BdJson (Get-BdManifestPath 'mgame'); $bm.movies[0].why = ''
            Assert-True (@(Test-BdManifest -M $bm) | Where-Object { $_ -like '*no why*' }) 'a movie without a why was accepted'
            # another (harmless) build: the keyframes are information under sync same-rom, a failure under sync required
            [System.IO.File]::WriteAllText((Join-Path $src 'game.txt'), "version 2`nstart Start`nfloor 1`nbonus 1`n")
            [void](Invoke-BdProjectBuild -Id 'mgame')
            $v3 = Invoke-BdProjectVerify -Id 'mgame' -Exe $ws
            $c3 = @(@($v3.scenarios | Where-Object { $_.id -eq 'movie-first' })[0].checks | Where-Object { $_.id -eq 'movie-in-sync' })[0]
            Assert-True ($v3.ok -and $c3.ok -and $c3.detail -like 'information only*') "another build under same-rom: $($c3.detail)"
            $mpath = Get-BdManifestPath 'mgame'
            [System.IO.File]::WriteAllText($mpath, ([System.IO.File]::ReadAllText($mpath)).Replace('"sync": "same-rom"', '"sync": "required"'))
            $v4 = Invoke-BdProjectVerify -Id 'mgame' -Exe $ws
            Assert-True (-not $v4.ok) 'sync required passed on another build that is off course'
        } finally { Restore-Env $savedEnv; $script:BdMovieFormat = $null }
    }
    Test-Case 'bisect over a throwaway game''s commits with a planted regression names it, skips the commit that does not build, says can''t tell where the menu changed; NEGATIVE: endpoints that do not hold name no commit (tests\bisect-proof.ps1)' {
        if (-not $ws) { Skip-Test $wsSkip }
        if (-not (Find-BdExecutable -Name 'git.exe' -Candidates @("$env:ProgramFiles\Git\cmd\git.exe"))) { Skip-Test 'git is not installed' }
        $exe = if ($PSVersionTable.PSEdition -eq 'Core') { (Get-Process -Id $PID).Path } else { Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe' }
        $r = Invoke-BdProcess -FilePath $exe -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $here 'bisect-proof.ps1'), '-WorkshopExe', $ws) -TimeoutSec 900
        $last = @($r.Output -split "`r?`n" | Where-Object { $_ -like '{*' }) | Select-Object -Last 1
        Assert-True ($r.ExitCode -eq 0 -and $last -match '"pass": true') "bisect proof: $last $(@($r.Output -split "`r?`n" | Where-Object { $_ -like '*FAIL*' }) -join ' | ')"
    }

    # ------------------------------------------------------------------ Timeflow (T1, T2)
    Write-Host 'Timeflow (T1, T2)'
    Test-Case 'timeflow record|list|info|verify|jump|resume|prune|keep|status through brokendev.ps1: a project''s timeline in BROKENDEV_HOME\timeflow\<project>, restore equals record, a replay in sync, a branch under its parent; NEGATIVE: a frame outside the timeline and an unknown timeline fail, a replay of a changed input... (the Workshop suite has the rest)' {
        if (-not $ws) { Skip-Test $wsSkip }
        $th = Join-Path $tmpRoot 'tfhome'; $tp = Join-Path $tmpRoot 'tfprojects'; $src = Join-Path $tmpRoot 'tfgame'
        New-Item -ItemType Directory -Force -Path $th, (Join-Path $tp 'tgame'), $src | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $src 'game.txt'), "version 1`nstart Start`nfloor 1`nbonus 1`n")
        [System.IO.File]::WriteAllText((Join-Path $src 'build.ps1'), @'
param([Parameter(Mandatory)][string]$Workshop)
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
New-Item -ItemType Directory -Force -Path (Join-Path $here 'build') | Out-Null
$p = Start-Process -FilePath $Workshop -ArgumentList @('--movie', 'build-planted', '--settings', "`"$(Join-Path $here 'game.txt')`"", '--out', "`"$(Join-Path $here 'build\game.nes')`"") -Wait -PassThru -NoNewWindow
exit $p.ExitCode
'@)
        $man = [ordered]@{
            schema = 1; id = 'tgame'; title = 'the Timeflow tests'' planted game'; family = 'test'; kind = 'nes'; console = 'nes'; description = 'T1/T2 run-tests'
            source = @{ folder = $src }; requires = @{ tools = @('ps5'); projects = @(); inputs = @() }
            build = @{ steps = @(@{ name = 'build'; shell = 'ps5'; cwd = '{src}'; file = '{src}/build.ps1'; args = @('-Workshop', '{workshop}'); timeoutSec = 120 })
                       outputs = @(@{ id = 'rom'; path = '{src}/build/game.nes'; publishAs = 'game.nes'; kind = 'nes' }, @{ id = 'mlb'; path = '{src}/build/game.mlb'; publishAs = 'game.mlb'; kind = 'symbols' }) }
            verify = @{ scenarios = @(@{ id = 'play'; frames = 600; input = '60:Start,66:,200:A,260:' }) }; knownIssues = @()
        }
        Save-BdJson -Object $man -Path (Join-Path (Join-Path $tp 'tgame') 'project.json')
        $savedTf = Save-Env @('BROKENDEV_HOME', 'BROKENDEV_PROJECTS', 'BROKENDEV_WORKSHOP', 'BROKENDEV_TIMEFLOW')
        $env:BROKENDEV_HOME = $th; $env:BROKENDEV_PROJECTS = $tp; $env:BROKENDEV_WORKSHOP = $ws; $env:BROKENDEV_TIMEFLOW = $null
        $psExe = if ($PSVersionTable.PSEdition -eq 'Core') { (Get-Process -Id $PID).Path } else { Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe' }
        function Bd([string[]]$a) { Invoke-BdProcess -FilePath $psExe -Arguments (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $bd 'brokendev.ps1')) + $a) -TimeoutSec 600 -Env @{ BROKENDEV_HOME = $th; BROKENDEV_PROJECTS = $tp; BROKENDEV_WORKSHOP = $ws } }
        function BdJ([string[]]$a) { $r = Bd ($a + '-Json'); $t = $r.Output; $i = $t.IndexOf('{'); $j = $t.LastIndexOf('}'); [pscustomobject]@{ Code = $r.ExitCode; J = $(if ($i -ge 0 -and $j -gt $i) { try { $t.Substring($i, $j - $i + 1) | ConvertFrom-Json } catch { $null } } else { $null }); Text = $t } }
        try {
            [void](Invoke-BdProjectBuild -Id 'tgame')
            $rec = BdJ @('timeflow', 'record', 'tgame', '-Scenario', 'play', '-Course', 'app_state')
            Assert-True ($rec.Code -eq 0 -and $rec.J.frames -eq 600 -and $rec.J.status -eq 'closed' -and $rec.J.verifyFailures -eq 0) "record: exit $($rec.Code) $(Select-BdTail $rec.Text 4)"
            $tid = [string]$rec.J.id
            Assert-True ($rec.J.project -eq 'tgame' -and ([string]$rec.J.dir).StartsWith((Join-Path $th 'timeflow\tgame'), [StringComparison]::OrdinalIgnoreCase)) "the timeline is not in timeflow\tgame: $($rec.J.dir) (project '$($rec.J.project)')"
            Assert-True (@($rec.J.vars) -contains 'app_state=[app_state]') "the watched variable is not in the timeline: $(@($rec.J.vars) -join ', ')"
            $ls = Bd @('timeflow', 'list', 'tgame')
            Assert-True ($ls.ExitCode -eq 0 -and $ls.Output -match [regex]::Escape($tid)) "list: $(Select-BdTail $ls.Output 4)"
            $vf = Bd @('timeflow', 'verify', $tid)
            Assert-True ($vf.ExitCode -eq 0 -and $vf.Output -match 'restore equals record on 600 of 600') "verify: exit $($vf.ExitCode) $(Select-BdTail $vf.Output 4)"
            $jp = BdJ @('timeflow', 'jump', $tid, '-Frame', '300')
            Assert-True ($jp.Code -eq 0 -and $jp.J.frame -eq 300 -and $null -ne $jp.J.vars.app_state) "jump: exit $($jp.Code) $(Select-BdTail $jp.Text 4)"
            $rp = BdJ @('timeflow', 'resume', $tid, '-Frame', '300')
            Assert-True ($rp.Code -eq 0 -and $rp.J.inSync -and $rp.J.framesRun -eq 300) "resume (replay): exit $($rp.Code) $(Select-BdTail $rp.Text 4)"
            $br = BdJ @('timeflow', 'resume', $tid, '-Frame', '300', '-Script', '0:Left+B', '-Frames', '90', '-Record')
            Assert-True ($br.Code -eq 0 -and $br.J.branch) "resume (a branch): exit $($br.Code) $(Select-BdTail $br.Text 4)"
            $bi = BdJ @('timeflow', 'info', [string]$br.J.branch)
            Assert-True ($bi.J.parent.id -eq $tid -and $bi.J.parent.frame -eq 300 -and $bi.J.startFrame -eq 300 -and $bi.J.frames -eq 90) "the branch: parent $($bi.J.parent.id) at $($bi.J.parent.frame), frames $($bi.J.startFrame)+$($bi.J.frames)"
            $kp = Bd @('timeflow', 'keep', $tid); $st = BdJ @('timeflow', 'status')
            Assert-True ($kp.ExitCode -eq 0 -and @($st.J.games).Count -ge 1 -and @($st.J.games)[0].timelines -eq 2) "keep / status: $(Select-BdTail $kp.Output 2) $(Select-BdTail $st.Text 4)"
            $pr = Bd @('timeflow', 'prune', 'tgame')
            Assert-True ($pr.ExitCode -eq 0 -and $pr.Output -match 'retired 0') "prune under the cap retired something: $(Select-BdTail $pr.Output 3)"
            # the negative controls
            $far = Bd @('timeflow', 'jump', $tid, '-Frame', '99999')
            Assert-True ($far.ExitCode -ne 0 -and $far.Output -match 'not in timeline') "a frame outside the timeline: exit $($far.ExitCode) $(Select-BdTail $far.Output 3)"
            $unk = Bd @('timeflow', 'info', 'no-such-timeline')
            Assert-True ($unk.ExitCode -ne 0 -and $unk.Output -match 'no timeline') "an unknown timeline: exit $($unk.ExitCode) $(Select-BdTail $unk.Output 3)"
            $bad = Bd @('timeflow', 'nonsense')
            Assert-True ($bad.ExitCode -ne 0) 'an unknown timeflow action passed'
        } finally { Restore-Env $savedTf }
    }

    # ------------------------------------------------------------------ bug captures (D6)
    Write-Host 'bug captures (D6)'
    Test-Case 'bugs + bug: the build record names the workspace''s bugs folder, a capture there and one in the default folder are listed, bug replays it exactly and opens it in the debugger; NEGATIVE: a workspace repository that does not ignore bugs\ fails bugs, a capture with Start one frame late fails bug, an unknown id is refused' {
        if (-not $ws) { Skip-Test $wsSkip }
        if (-not (Find-BdExecutable -Name 'git.exe' -Candidates @("$env:ProgramFiles\Git\cmd\git.exe"))) { Skip-Test 'git is not installed' }
        $bh = Join-Path $tmpRoot 'bughome'; $bp = Join-Path $tmpRoot 'bugprojects'; $src = Join-Path $tmpRoot 'buggame'; $dflt = Join-Path $tmpRoot 'bugdefault'
        New-Item -ItemType Directory -Force -Path $bh, (Join-Path $bp 'bgame'), $src, $dflt | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $src 'game.txt'), "version 1`nstart Start`nfloor 1`nbonus 1`n")
        [System.IO.File]::WriteAllText((Join-Path $src 'build.ps1'), @'
param([Parameter(Mandatory)][string]$Workshop)
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
New-Item -ItemType Directory -Force -Path (Join-Path $here 'build') | Out-Null
$p = Start-Process -FilePath $Workshop -ArgumentList @('--movie', 'build-planted', '--settings', "`"$(Join-Path $here 'game.txt')`"", '--out', "`"$(Join-Path $here 'build\game.nes')`"") -Wait -PassThru -NoNewWindow
exit $p.ExitCode
'@)
        $man = [ordered]@{
            schema = 1; id = 'bgame'; title = 'the bug tests'' planted game'; family = 'test'; kind = 'nes'; console = 'nes'; description = 'D6 run-tests'
            source = @{ folder = $src }; requires = @{ tools = @('ps5'); projects = @(); inputs = @() }
            build = @{ steps = @(@{ name = 'build'; shell = 'ps5'; cwd = '{src}'; file = '{src}/build.ps1'; args = @('-Workshop', '{workshop}'); timeoutSec = 120 })
                       outputs = @(@{ id = 'rom'; path = '{src}/build/game.nes'; publishAs = 'game.nes'; kind = 'nes' }) }
            verify = @{ scenarios = @(@{ id = 'play'; frames = 300; input = '60:Start,66:' }) }; knownIssues = @()
        }
        Save-BdJson -Object $man -Path (Join-Path (Join-Path $bp 'bgame') 'project.json')
        $env:BROKENDEV_HOME = $bh; $env:BROKENDEV_PROJECTS = $bp; $env:BROKENDEV_WORKSHOP = $ws; $env:BROKENNES_BUGS_DIR = $dflt
        $childEnv = @{ BROKENDEV_HOME = $bh; BROKENDEV_PROJECTS = $bp; BROKENDEV_WORKSHOP = $ws; BROKENNES_BUGS_DIR = $dflt }
        $psExe = if ($PSVersionTable.PSEdition -eq 'Core') { (Get-Process -Id $PID).Path } else { Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe' }
        function BD([string[]]$A, [int]$T = 600) { Invoke-BdProcess -FilePath $psExe -Arguments (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $bd 'brokendev.ps1')) + $A) -TimeoutSec $T -Env $childEnv }
        try {
            [void](Invoke-BdProjectBuild -Id 'bgame')
            $rec = Read-BdJson (Join-Path (Get-BdOutDir 'bgame') 'build.json')
            $bugs = Join-Path $src 'bugs'
            Assert-Equal $rec.bugs $bugs 'the build record''s bugs folder'
            $rom = @($rec.outputs | Where-Object { $_.id -eq 'rom' })[0].published
            $mk = Invoke-BdProcess -FilePath $ws -Arguments @('--bug', 'make', '--rom', $rom, '--frames', '900', '--input', '60:Start,66:,200:A,260:', '--note', 'run-tests', '--out', $bugs, '--json') -TimeoutSec 300
            $made = Get-BdJsonFromOutput $mk.Output
            Assert-True ($mk.ExitCode -eq 0 -and $made.movie) "--bug make: exit $($mk.ExitCode) $(Select-BdTail $mk.Output 4)"
            $mk2 = Invoke-BdProcess -FilePath $ws -Arguments @('--bug', 'make', '--rom', $rom, '--frames', '400', '--input', '60:Start,66:', '--note', 'in the default folder', '--out', $dflt, '--json') -TimeoutSec 300
            Assert-True ($mk2.ExitCode -eq 0) "--bug make into the default folder: $(Select-BdTail $mk2.Output 4)"
            # the workspace is a git repository that does not ignore bugs\: 'bugs' says so and fails
            [void](Invoke-BdGit -Repo $src -GitArgs @('init', '-q'))
            $l1 = BD @('bugs', 'bgame')
            Assert-True ($l1.ExitCode -eq 1 -and $l1.Output -match 'NOT IGNORED') "bugs on a repository that does not ignore bugs\: exit $($l1.ExitCode) $(Select-BdTail $l1.Output 4)"
            [System.IO.File]::WriteAllText((Join-Path $src '.gitignore'), "/bugs/`n/build/`n")
            $l2 = BD @('bugs', 'bgame')
            Assert-True ($l2.ExitCode -eq 0 -and $l2.Output -match [regex]::Escape($made.id) -and $l2.Output -match 'note: run-tests' -and $l2.Output -match 'note: in the default folder' -and $l2.Output -match 'ignored') "bugs bgame: exit $($l2.ExitCode) $(Select-BdTail $l2.Output 8)"
            $b1 = BD @('bug', $made.id)
            Assert-True ($b1.ExitCode -eq 0 -and $b1.Output -match 'in sync' -and $b1.Output -match 'replay''s last picture') "bug $($made.id): exit $($b1.ExitCode) $(Select-BdTail $b1.Output 4)"
            $b2 = BD @('bug', $made.id, '-Debugger', '-Script', 'run 600; back frames 10; where')
            Assert-True ($b2.ExitCode -eq 0 -and $b2.Output -match 'bug replay: in sync') "bug -Debugger: exit $($b2.ExitCode) $(Select-BdTail $b2.Output 4)"
            # the negative controls: the same capture with Start one frame late; an id no bugs folder has
            $late = Join-Path $tmpRoot 'late-bug'
            Copy-Item -LiteralPath $made.path -Destination $late -Recurse
            $mv = Join-Path $late 'capture.bnm'
            $t = [System.IO.File]::ReadAllText($mv)
            Assert-True ($t.Contains("i 60 Start -")) 'the capture''s movie has no Start press at frame 60'
            [System.IO.File]::WriteAllText($mv, $t.Replace("i 60 Start -", "i 61 Start -"))
            $b3 = BD @('bug', $late)
            Assert-True ($b3.ExitCode -eq 1 -and $b3.Output -match 'OFF COURSE') "the late-Start capture: exit $($b3.ExitCode) $(Select-BdTail $b3.Output 4)"
            $b4 = BD @('bug', 'no-such-capture')
            Assert-True ($b4.ExitCode -eq 1 -and $b4.Output -match 'no bug') "an unknown id: exit $($b4.ExitCode) $(Select-BdTail $b4.Output 3)"
        } finally { Restore-Env $savedEnv }
    }

    # ------------------------------------------------------------------ the MCP server (D3): its scripted MCP clients, over stdio
    $mcpExe = if ($McpExe) { $McpExe } elseif ($env:BROKENDEV_MCP) { $env:BROKENDEV_MCP } else { $null }
    Test-Case 'the MCP server: the client test (headless sessions on every console, with negative controls) and the planted-bug demo pass; SKIP without a built server' {
        if (-not $mcpExe -or -not (Test-Path -LiteralPath $mcpExe)) { Skip-Test 'no MCP server (-McpExe, or BROKENDEV_MCP; brokendev.ps1 mcp build makes one)' }
        foreach ($suite in '--client-test', '--demo') {
            $lines = @(& $mcpExe $suite --repo (Get-BdRepoRoot) 2>&1 | ForEach-Object { "$_" })
            $last = $lines | Where-Object { $_ -like '{*' } | Select-Object -Last 1
            Assert-True ($LASTEXITCODE -eq 0) "$suite failed: $(@($lines | Where-Object { $_ -like '`[FAIL`]*' }) -join ' | ')"
            $j = $last | ConvertFrom-Json
            Assert-True ($j.pass -and $j.checks -ge 6) "$suite reported $last"
        }
        # the tools tell an agent the live-control rule (read freely, control only when the user asked)
        $lines = @(& $mcpExe --list-tools)
        Assert-True (($lines -join "`n") -match 'attach_live .*ONLY when the user asked') 'attach_live does not say that control is only for when the user asked'
    }
}
finally {
    Restore-Env $savedEnv
    try { Remove-Item -LiteralPath $tmpRoot -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

Write-Host ''
Write-Host ("{0} passed, {1} failed, {2} skipped" -f $script:pass, $script:fail, $script:skip) -ForegroundColor $(if ($script:fail -eq 0) { 'Green' } else { 'Red' })
if ($script:fail -gt 0) { exit 1 } else { exit 0 }
