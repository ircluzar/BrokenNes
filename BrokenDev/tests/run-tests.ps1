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
$envNames = @('BROKENDEV_HOME', 'BROKENDEV_ARCHIVES', 'BROKENDEV_PROJECTS', 'BROKENDEV_ARCHIVES_JSON', 'BROKENDEV_PYTHON', 'BROKENDEV_WORKSHOP', 'BROKENNES_DEV', 'BROKENNES_CONFIG_DIR')
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
}
finally {
    Restore-Env $savedEnv
    try { Remove-Item -LiteralPath $tmpRoot -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

Write-Host ''
Write-Host ("{0} passed, {1} failed, {2} skipped" -f $script:pass, $script:fail, $script:skip) -ForegroundColor $(if ($script:fail -eq 0) { 'Green' } else { 'Red' })
if ($script:fail -gt 0) { exit 1 } else { exit 0 }
