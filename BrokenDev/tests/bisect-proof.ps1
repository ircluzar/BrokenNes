<#
.SYNOPSIS
  The proof of BrokenDev bisect (milestone D5 of docs/projects/brokendev-debugging-plan.md): a throwaway game repository with a planted
  regression, bisected with today's BrokenNes.

.DESCRIPTION
  Builds, in a scratch folder (never a game of the user's), a git repository of a small NES game (the movie tests' planted game:
  Workshop --movie build-planted builds it from its settings file, game.txt, the repository's "source") with 17 local commits:
    c01        the game (a title that waits for Start, then a life lost every 64 frames down to 1)
    c02-c08    harmless changes (the version byte, the score bonus)
    c09        a typo in game.txt: the commit does not build
    c10        the typo fixed, but the title now waits for Select: a different menu (the movie's Start does nothing)
    c11        the title waits for Start again
    c12        THE REGRESSION: lives go down to 0 (the floor is 0)
    c13-c17    harmless changes on top
  A BrokenDev project (a scratch manifest) builds it; a movie is recorded on c01's build (Start, then A held a while) with the course
  variable app_state; then 'bisect' runs the test "[lives] >= 1 at 600" from c01 to c17.
  It must name c12 as the first bad commit, report c09 as skipped (no build) and c10 as can't tell (the replay lost its course at the menu),
  build no more than about log2(17) + the endpoints, leave the throwaway repository as it was and give the clone no remote.
  Negative controls: a test that holds on every commit (no-crash) and a good commit after the regression both stop with "the endpoints do
  not hold", naming no commit.

  Exit 0: every check passed. Prints one JSON line at the end: { pass, checks, failed }.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$WorkshopExe, [string]$Keep = '')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$bd = Split-Path -Parent $here
foreach ($l in 'Common', 'Tools', 'Archive', 'Toolchains', 'Manifest', 'Build', 'Verify', 'Movies') { . (Join-Path $bd "lib\$l.ps1") }

$script:checks = 0; $script:failed = @()
function Check { param([string]$Name, [bool]$Ok, [string]$Detail) $script:checks++; if ($Ok) { Write-Host "  PASS  ${Name}: $Detail" -ForegroundColor Green } else { $script:failed += $Name; Write-Host "  FAIL  ${Name}: $Detail" -ForegroundColor Red } }

$tmp = if ($Keep) { $Keep } else { Join-Path ([System.IO.Path]::GetTempPath()) ('bdbisect-' + [guid]::NewGuid().ToString('N').Substring(0, 8)) }
if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force }
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$saved = @{}; foreach ($n in 'BROKENDEV_HOME', 'BROKENDEV_PROJECTS', 'BROKENDEV_WORKSHOP') { $saved[$n] = [Environment]::GetEnvironmentVariable($n) }
try {
    $env:BROKENDEV_HOME = Join-Path $tmp 'home'
    $env:BROKENDEV_PROJECTS = Join-Path $tmp 'projects'
    $env:BROKENDEV_WORKSHOP = (Resolve-Path -LiteralPath $WorkshopExe).ProviderPath
    New-Item -ItemType Directory -Force -Path $env:BROKENDEV_HOME | Out-Null

    # ---- the throwaway game repository (local commits only, no remote)
    $repo = Join-Path $tmp 'planted-game'
    New-Item -ItemType Directory -Force -Path $repo | Out-Null
    $git = Find-BdExecutable -Name 'git.exe' -Candidates @("$env:ProgramFiles\Git\cmd\git.exe")
    function G { param([string[]]$A) $r = Invoke-BdProcess -FilePath $git -Arguments (@('-C', $repo) + $A) -TimeoutSec 120; if ($r.ExitCode -ne 0) { throw "git $($A -join ' '): $($r.Output)" }; $r.Output }
    [void](G @('init', '--quiet', '-b', 'main'))
    [void](G @('config', 'user.name', 'bisect proof')); [void](G @('config', 'user.email', 'bisect-proof@localhost')); [void](G @('config', 'commit.gpgsign', 'false'))
    $build = @'
param([Parameter(Mandatory)][string]$Workshop)
# the planted game's build: game.txt -> build\game.nes and build\game.mlb (Workshop --movie build-planted); exit 1 on a bad game.txt
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
New-Item -ItemType Directory -Force -Path (Join-Path $here 'build') | Out-Null
$p = Start-Process -FilePath $Workshop -ArgumentList @('--movie', 'build-planted', '--settings', "`"$(Join-Path $here 'game.txt')`"", '--out', "`"$(Join-Path $here 'build\game.nes')`"") -Wait -PassThru -NoNewWindow
exit $p.ExitCode
'@
    [System.IO.File]::WriteAllText((Join-Path $repo 'build.ps1'), $build)
    [System.IO.File]::WriteAllText((Join-Path $repo '.gitignore'), "build/`n")
    $history = @(
        @('the game', 'version 1', 'Start', '1', '1'),
        @('version 2', 'version 2', 'Start', '1', '1'),
        @('score bonus 2', 'version 2', 'Start', '1', '2'),
        @('version 3', 'version 3', 'Start', '1', '2'),
        @('score bonus 1 again', 'version 3', 'Start', '1', '1'),
        @('version 4', 'version 4', 'Start', '1', '1'),
        @('version 5', 'version 5', 'Start', '1', '1'),
        @('version 6', 'version 6', 'Start', '1', '1'),
        @('BROKEN: a typo in game.txt', 'BROKEN', 'Start', '1', '1'),
        @('typo fixed; the title waits for Select', 'version 7', 'Select', '1', '1'),
        @('the title waits for Start again', 'version 8', 'Start', '1', '1'),
        @('REGRESSION: lives down to 0', 'version 9', 'Start', '0', '1'),
        @('version 10', 'version 10', 'Start', '0', '1'),
        @('score bonus 3', 'version 10', 'Start', '0', '3'),
        @('version 11', 'version 11', 'Start', '0', '3'),
        @('version 12', 'version 12', 'Start', '0', '3'),
        @('version 13', 'version 13', 'Start', '0', '3')
    )
    $shas = @()
    foreach ($h in $history) {
        $text = if ($h[1] -eq 'BROKEN') { "version 6`nstart Start`nflor 1`nbonus 1`n" } else { "$($h[1])`nstart $($h[2])`nfloor $($h[3])`nbonus $($h[4])`n" }
        [System.IO.File]::WriteAllText((Join-Path $repo 'game.txt'), $text)
        [void](G @('add', '-A')); [void](G @('commit', '--quiet', '-m', $h[0]))
        $shas += ((G @('rev-parse', 'HEAD')) -split "`r?`n")[0].Trim()
    }
    $headBefore = $shas[-1]

    # ---- the scratch BrokenDev project that builds it
    $pdir = Join-Path $env:BROKENDEV_PROJECTS 'planted'
    New-Item -ItemType Directory -Force -Path $pdir | Out-Null
    $man = [ordered]@{
        schema = 1; id = 'planted'; title = 'the bisect proof''s planted game'; family = 'test'; kind = 'nes'; console = 'nes'
        description = 'a throwaway repository with a planted regression (BrokenDev/tests/bisect-proof.ps1)'
        source = @{ folder = $repo }; requires = @{ tools = @('ps5'); projects = @(); inputs = @() }
        build = @{ steps = @(@{ name = 'build'; shell = 'ps5'; cwd = '{src}'; file = '{src}/build.ps1'; args = @('-Workshop', '{workshop}'); timeoutSec = 120 })
                   outputs = @(@{ id = 'rom'; path = '{src}/build/game.nes'; publishAs = 'game.nes'; kind = 'nes' }, @{ id = 'mlb'; path = '{src}/build/game.mlb'; publishAs = 'game.mlb'; kind = 'symbols' }) }
        verify = @{ scenarios = @(@{ id = 'play'; frames = 600; input = '60:Start,66:,200:A,260:' }) }; knownIssues = @()
    }
    Save-BdJson -Object $man -Path (Join-Path $pdir 'project.json')

    # ---- the movie, recorded on c01's build (the game as it was good)
    [void](G @('checkout', '--quiet', $shas[0]))
    $rec = Invoke-BdProjectBuild -Id 'planted'
    $movie = Invoke-BdMovieRecord -Id 'planted' -Scenario 'play' -Course @('app_state')
    [void](G @('checkout', '--quiet', 'main'))
    $mtext = [System.IO.File]::ReadAllText($movie)
    Check 'the movie names the commit it was recorded on (bisect''s default good commit)' ($mtext -match "(?m)^build commit $($shas[0])") ($mtext -split "`n" | Where-Object { $_ -like 'build *' } | Select-Object -First 1)

    # ---- bisect
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $r = Invoke-BdBisect -Id 'planted' -Movie $movie -Test '[lives] >= 1 at 600'
    $secs = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    $idx = @{}; for ($i = 0; $i -lt $shas.Count; $i++) { $idx[$shas[$i]] = $i + 1 }
    Check 'bisect names the planted regression (c12) as the first bad commit' ($r.firstBad -eq $shas[11]) "$($r.verdict) ($secs s)"
    $byIndex = @{}; foreach ($t in $r.tested) { $byIndex[$idx[$t.commit]] = $t }
    Check 'the commit that does not build (c09) is skipped and reported' ($byIndex.ContainsKey(9) -and $byIndex[9].verdict -eq 'skip' -and @($r.skipped) -contains $shas[8]) $(if ($byIndex.ContainsKey(9)) { $byIndex[9].why } else { 'c09 was not tested' })
    Check 'the commit with another menu (c10) says can''t tell, not good or bad' ($byIndex.ContainsKey(10) -and $byIndex[10].verdict -eq 'cant-tell' -and @($r.cantTell) -contains $shas[9]) $(if ($byIndex.ContainsKey(10)) { $byIndex[10].why } else { 'c10 was not tested' })
    $tested = (@($r.tested | ForEach-Object { 'c{0:D2} {1}' -f $idx[$_.commit], $_.verdict })) -join ', '
    Check 'about log2(17) builds, the endpoints included (not one per commit)' ($r.builds -le 8 -and @($r.tested).Count -le 9) "$($r.builds) builds: $tested"
    Check 'c11 (the last good) and c12 (the first bad) were both tested' ($byIndex.ContainsKey(11) -and $byIndex[11].verdict -eq 'good' -and $byIndex.ContainsKey(12) -and $byIndex[12].verdict -eq 'bad') $tested
    $status = (G @('status', '--porcelain')).Trim(); $headAfter = ((G @('rev-parse', 'HEAD')) -split "`r?`n")[0].Trim(); $remotes = (G @('remote')).Trim()
    Check 'the game''s own repository is left as it was (same HEAD, clean, no remote added)' ($headAfter -eq $headBefore -and -not $status -and -not $remotes) "HEAD $($headAfter.Substring(0, 10)), status '$status', remotes '$remotes'"
    $cr = Invoke-BdProcess -FilePath $git -Arguments @('-C', $r.clone, 'remote') -TimeoutSec 60
    Check 'the bisect clone has no remote (it cannot push anywhere)' ($cr.ExitCode -eq 0 -and -not $cr.Output.Trim()) "clone $($r.clone)"

    # ---- negative controls
    $n1 = Invoke-BdBisect -Id 'planted' -Movie $movie -Test 'no-crash'
    Check 'NEGATIVE: a test that holds on every commit stops with "the endpoints do not hold", naming no commit' (-not $n1.firstBad -and $n1.verdict -like 'the endpoints do not hold*') $n1.verdict
    $n2 = Invoke-BdBisect -Id 'planted' -Movie $movie -Test '[lives] >= 1 at 600' -Good $shas[12]
    Check 'NEGATIVE: a "good" commit after the regression stops the same way' (-not $n2.firstBad -and $n2.verdict -like 'the endpoints do not hold*') $n2.verdict
}
finally {
    foreach ($n in $saved.Keys) { [Environment]::SetEnvironmentVariable($n, $saved[$n]) }
    if (-not $Keep) { try { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue } catch { } }
}
$ok = $script:failed.Count -eq 0
Write-Host ''
Write-Host ('{{"pass": {0}, "checks": {1}, "failed": {2}}}' -f $ok.ToString().ToLowerInvariant(), $script:checks, $script:failed.Count)
if ($ok) { exit 0 } else { exit 1 }
