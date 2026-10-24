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
param([string]$WorkshopExe = '', [switch]$NoWorkshop)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$bd = Split-Path -Parent $here
. (Join-Path $bd 'lib\Common.ps1')
. (Join-Path $bd 'lib\Tools.ps1')
. (Join-Path $bd 'lib\Archive.ps1')
. (Join-Path $bd 'lib\Manifest.ps1')
. (Join-Path $bd 'lib\Build.ps1')
. (Join-Path $bd 'lib\Verify.ps1')
. (Join-Path $bd 'lib\Diff.ps1')
. (Join-Path $bd 'lib\DevTools.ps1')
. (Join-Path $bd 'lib\Emulators.ps1')
. (Join-Path $bd 'lib\Compat.ps1')
. (Join-Path $bd 'lib\WideMess.ps1')

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
              'BROKENDEV_DEVTOOLS', 'BROKENDEV_TRACE_LUA', 'BROKENDEV_LIVE_SYNTH', 'BROKENDEV_LIVE_MISSING', 'BD_TEST_TOOL_VAR', 'BROKENDEV_EMULATORS')
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
        Assert-Equal (Invoke-BdScriptTool -Tool $t -Ctx $ctx -Extra @('3')) 3
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
        Assert-Equal (Invoke-BdScriptTool -Tool $t -Ctx $ctx -Extra @('2')) 2
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
        param([string]$Id, $Scenarios, $Compare = @())
        $m = [ordered]@{ schema = 1; id = $Id; title = $Id; family = 'synth'; kind = 'nes'; console = 'nes'; source = @{ archive = 'x'; path = 'x' }; requires = @{ tools = @() }; knownIssues = @()
                         build = @{ steps = @(); outputs = @(@{ id = 'rom'; path = 'x'; publishAs = 'rom.nes' }) }
                         verify = @{ cores = @{ cpu = 'FIX'; ppu = 'FIX'; apu = 'FIX' }; scenarios = $Scenarios; compare = $Compare } }
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
}
finally {
    Restore-Env $savedEnv
    try { Remove-Item -LiteralPath $tmpRoot -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

Write-Host ''
Write-Host ("{0} passed, {1} failed, {2} skipped" -f $script:pass, $script:fail, $script:skip) -ForegroundColor $(if ($script:fail -eq 0) { 'Green' } else { 'Red' })
if ($script:fail -gt 0) { exit 1 } else { exit 0 }
