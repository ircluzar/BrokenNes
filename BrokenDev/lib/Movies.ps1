# BrokenDev movies and bisect (milestone D5 of docs/projects/brokendev-debugging-plan.md). Requires Common, Tools, Archive, Manifest, Build, Verify.
#
#   movie record  <project> -Scenario <verify id> | -Script "120:Start,..." -Frames N   [-Name n] [-Course sym,sym]
#                 records a movie of the project's built ROM in BrokenNes's own format (Workshop --movie record) into out\<id>\movies\
#   movie play    <project> -Movie <file>          replays it on today's build: in sync, or the frame it went off course
#   movie export  <project> -Movie <file> -Format fm2|bk2      for FCEUX / BizHawk (cross-check only; BrokenNes is where agents debug)
#   movie promote <project> -Movie <file> -Name <id> -Why <text> [-Sync same-rom|required|off]
#                 the movie becomes a test: copied to projects\<id>\movies\<name>.bnm and listed in the manifest's movies section; verify
#                 replays it every time (only promoted movies are verify tests: the user's decision)
#   movie list    <project>
#   bisect <project> -Movie <file> -Test "<test>" [-Good <commit>] [-Bad <commit>] [-Repo <git folder>]
#                 over the GAME's commits, with today's BrokenNes: a scratch clone of the game's repository (never the user's own folder,
#                 never pushed: its remote is removed), built halfway back, the movie replayed and tested, halving toward the first bad
#                 commit. A commit that does not build is skipped and reported; a commit where the replay loses its course (the movie's course
#                 variables stop matching before the test frame) is "can't tell", never a guess.
#
# The movie format's name and extension are a working name (the plan's open question 2): Workshop's MovieFormat holds them; the extension
# used here comes from 'Workshop --movie info' so a rename needs no change in this file.

Set-StrictMode -Version Latest

$script:BdMovieFormat = $null
function Get-BdMovieFormat {
    <# The movie format's name and extension, from the Workshop (its MovieFormat is the one place they are written: a rename changes nothing here). #>
    param([string]$Exe = '')
    if ($script:BdMovieFormat) { return $script:BdMovieFormat }
    $r = Invoke-BdWorkshopMovie -Arguments @('format') -Exe $Exe -TimeoutSec 60
    $j = Get-BdJsonFromOutput $r.Output
    if ($r.ExitCode -ne 0 -or -not $j) { throw "the Workshop did not say its movie format (exit $($r.ExitCode)): $(Select-BdTail $r.Output 3) (an older Workshop? run: brokendev.ps1 build-workshop)" }
    $script:BdMovieFormat = $j
    $j
}
function Get-BdMovieExtension { [string](Get-BdMovieFormat).extension }

function Get-BdProjectRomAndSymbols {
    <# The built ROM and its published symbol files (build.json), or an error that says to build first. #>
    param([Parameter(Mandatory)][string]$Id)
    $out = Get-BdOutDir $Id
    $bp = Join-Path $out 'build.json'
    if (-not (Test-Path -LiteralPath $bp)) { throw "project $Id has not been built (no $bp). Run: brokendev.ps1 build $Id" }
    $b = Read-BdJson $bp
    if (-not $b.ok) { throw "the last build of $Id failed; fix it first" }
    $rom = @($b.outputs | Where-Object { $_.id -eq 'rom' })[0].published
    $m = Read-BdManifest -Id $Id
    $syms = @()
    foreach ($o in @($m.build.outputs)) {
        if ([string](Get-BdProp $o 'kind' '') -eq 'symbols') { $p = Join-Path $out ([string]$o.publishAs); if (Test-Path -LiteralPath $p) { $syms += $p } }
    }
    [pscustomobject]@{ Rom = $rom; Symbols = $syms; Manifest = $m; Out = $out }
}

function Invoke-BdWorkshopMovie {
    <# Workshop --movie <args>; returns the process result (ExitCode, Output). #>
    param([Parameter(Mandatory)][string[]]$Arguments, [string]$Exe = '', [int]$TimeoutSec = 1800, [string]$LogFile = '')
    if (-not $Exe) { $Exe = Get-BdWorkshopExe }
    if (-not $Exe -or -not (Test-Path -LiteralPath $Exe)) { throw 'BrokenNes Workshop is not built. Run: brokendev.ps1 build-workshop' }
    $a = @('--movie') + $Arguments
    if ($LogFile) { Invoke-BdProcess -FilePath $Exe -Arguments $a -TimeoutSec $TimeoutSec -Env (Get-BdChildEnv) -LogFile $LogFile }
    else { Invoke-BdProcess -FilePath $Exe -Arguments $a -TimeoutSec $TimeoutSec -Env (Get-BdChildEnv) }
}

function Get-BdJsonFromOutput {
    <# The JSON object a Workshop --json run printed (the first '{' to the last '}'). #>
    param([string]$Text)
    $i = $Text.IndexOf('{'); $j = $Text.LastIndexOf('}')
    if ($i -lt 0 -or $j -le $i) { return $null }
    try { $Text.Substring($i, $j - $i + 1) | ConvertFrom-Json } catch { $null }
}

function Invoke-BdMovieRecord {
    param([Parameter(Mandatory)][string]$Id, [string]$Scenario = '', [string]$Script = '', [int]$Frames = 0, [string]$Name = '', [string[]]$Course = @(), [string]$Exe = '')
    $p = Get-BdProjectRomAndSymbols -Id $Id
    $m = $p.Manifest
    $ntsc = $false; $powerOn = 'fceux'
    if ($Scenario) {
        $sc = @($m.verify.scenarios | Where-Object { $_.id -eq $Scenario })
        if ($sc.Count -eq 0) { throw "project $Id has no verify scenario '$Scenario' (it has: $(@($m.verify.scenarios | ForEach-Object { $_.id }) -join ', '))" }
        $sc = $sc[0]
        if (-not $Script) { $Script = [string](Get-BdProp $sc 'input' '') }
        if ($Frames -le 0) { $Frames = [int]$sc.frames }
        $ntsc = [bool](Get-BdProp $sc 'ntscFrameTiming' $false)
        $powerOn = [string](Get-BdProp $sc 'powerOnRam' 'fceux')
        if (Get-BdProp $sc 'batteryFrom' $null) { throw "scenario '$Scenario' powers on with another scenario's battery save; a movie starts from a fresh power-on" }
        if (-not $Name) { $Name = $Scenario }
    }
    if ($Frames -le 0) { throw 'movie record needs -Scenario <verify id>, or -Frames N (and -Script)' }
    if (-not $Name) { $Name = 'movie-' + (Get-Date -Format 'yyyyMMdd-HHmmss') }
    $dir = Join-Path $p.Out 'movies'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $file = Join-Path $dir ($Name + (Get-BdMovieExtension))
    # the build line names the game's commit when its workspace is a git repository (bisect's default good commit)
    $build = "$Id build of $((Get-Item -LiteralPath $p.Rom).LastWriteTime.ToString('s'))"
    try {
        $ws = Get-BdProjectWorkspace -Manifest $m
        if ($ws.Kind -ne 'absent') {
            $sha = @(Get-BdGitOut -Repo $ws.Path -GitArgs @('rev-parse', 'HEAD'))[0]
            $dirty = @(Get-BdGitOut -Repo $ws.Path -GitArgs @('status', '--porcelain', '--untracked-files=no')).Count -gt 0
            if ($sha -match '^[0-9a-f]{40}$') { $build = "commit $sha$(if ($dirty) { ' with uncommitted changes' }) ($build)" }
        }
    } catch { }
    $a = @('record', '--rom', $p.Rom, '--frames', [string]$Frames, '--out', $file, '--power-on', $powerOn, '--build', $build)
    if ($Script) { $a += @('--input', $Script) }
    if ($ntsc) { $a += '--ntsc' }
    $cores = Get-BdProp $m.verify 'cores' $null
    if ($cores -and [string]$m.console -eq 'nes') { $a += @('--cores', ('{0}/{1}/{2}' -f (Get-BdProp $cores 'cpu' 'FIX'), (Get-BdProp $cores 'ppu' 'FIX'), (Get-BdProp $cores 'apu' 'FIX'))) }
    elseif ($cores) { $a += @('--cores', ('{0}/{1}/{2}' -f (Get-BdProp $cores 'cpu' ''), (Get-BdProp $cores 'ppu' ''), (Get-BdProp $cores 'apu' ''))) }
    if ([string]$m.console -eq 'gbc' -or [string]$m.console -eq 'gb' -or [string]$m.console -eq 'wms') { $a += @('--console', [string]$m.console) }
    foreach ($s in $p.Symbols) { $a += @('--sym', $s) }
    foreach ($c in @($Course | ForEach-Object { ([string]$_) -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })) { $a += @('--var', "$c=[$c]") }
    $r = Invoke-BdWorkshopMovie -Arguments $a -Exe $Exe
    if ($r.ExitCode -ne 0) { throw "movie record failed (exit $($r.ExitCode)): $(Select-BdTail $r.Output 6)" }
    Write-BdOk (Select-BdTail $r.Output 1)
    $file
}

function Invoke-BdMoviePlay {
    param([Parameter(Mandatory)][string]$Id, [Parameter(Mandatory)][string]$Movie, [string]$Exe = '')
    $p = Get-BdProjectRomAndSymbols -Id $Id
    $a = @('play', '--movie', (Resolve-BdMoviePath -Id $Id -Movie $Movie), '--rom', $p.Rom, '--json')
    foreach ($s in $p.Symbols) { $a += @('--sym', $s) }
    $r = Invoke-BdWorkshopMovie -Arguments $a -Exe $Exe
    $j = Get-BdJsonFromOutput $r.Output
    if (-not $j) { throw "movie play failed (exit $($r.ExitCode)): $(Select-BdTail $r.Output 6)" }
    $j
}

function Resolve-BdMoviePath {
    <# A movie by path, or by name among the project's promoted movies and its out\<id>\movies. #>
    param([Parameter(Mandatory)][string]$Id, [Parameter(Mandatory)][string]$Movie)
    if (Test-Path -LiteralPath $Movie) { return (Resolve-Path -LiteralPath $Movie).ProviderPath }
    $ext = Get-BdMovieExtension
    foreach ($d in @((Join-Path (Join-Path (Get-BdProjectsDir) $Id) 'movies'), (Join-Path (Get-BdOutDir $Id) 'movies'))) {
        foreach ($n in @($Movie, ($Movie + $ext))) { $c = Join-Path $d $n; if (Test-Path -LiteralPath $c) { return $c } }
    }
    throw "no movie '$Movie' (a path, or a name in projects\$Id\movies or out\$Id\movies)"
}

function Invoke-BdMovieExport {
    param([Parameter(Mandatory)][string]$Id, [Parameter(Mandatory)][string]$Movie, [ValidateSet('fm2', 'bk2')][string]$Format = 'bk2', [string]$Exe = '')
    $p = Get-BdProjectRomAndSymbols -Id $Id
    $src = Resolve-BdMoviePath -Id $Id -Movie $Movie
    $dst = [System.IO.Path]::ChangeExtension($src, $Format)
    if ($src -like "$(Get-BdProjectsDir)*") { $dst = Join-Path (Join-Path $p.Out 'movies') ([System.IO.Path]::GetFileNameWithoutExtension($src) + '.' + $Format) }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dst) | Out-Null
    $r = Invoke-BdWorkshopMovie -Arguments @('export', '--movie', $src, '--rom', $p.Rom, "--$Format", $dst) -Exe $Exe
    if ($r.ExitCode -ne 0) { throw "movie export failed (exit $($r.ExitCode)): $(Select-BdTail $r.Output 6)" }
    foreach ($l in ($r.Output -split "`r?`n" | Where-Object { $_ -like 'note:*' })) { Write-BdWarn $l }
    $dst
}

function Add-BdManifestMovie {
    <#
      Adds one entry to a manifest's "movies" list as a TEXT edit (the manifests are hand-formatted; a JSON round trip would rewrite every
      line): into the existing list, or a new "movies" list before the manifest's last closing brace. The result must parse and keep every
      other field, or nothing is written.
    #>
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$EntryJson)
    $text = [System.IO.File]::ReadAllText($Path)
    $before = $text | ConvertFrom-Json
    $m = [regex]::Match($text, '"movies"\s*:\s*\[')
    if ($m.Success) {
        $at = $m.Index + $m.Length
        $rest = $text.Substring($at)
        $empty = $rest -match '^\s*\]'
        $new = $text.Substring(0, $at) + "`n    " + $EntryJson + $(if ($empty) { "`n  " } else { ',' }) + $rest
    } else {
        $close = $text.LastIndexOf('}')
        if ($close -lt 0) { throw "$Path is not a JSON object" }
        $head = $text.Substring(0, $close).TrimEnd()
        $new = $head + ",`n  `"movies`": [`n    " + $EntryJson + "`n  ]`n" + $text.Substring($close)
    }
    $after = $new | ConvertFrom-Json
    foreach ($prop in $before.PSObject.Properties) {
        if ($prop.Name -eq 'movies') { continue }
        if (($prop.Value | ConvertTo-Json -Depth 30 -Compress) -ne ($after.($prop.Name) | ConvertTo-Json -Depth 30 -Compress)) { throw "adding the movie would change the manifest's '$($prop.Name)' (not written)" }
    }
    [System.IO.File]::WriteAllText($Path, $new, (New-Object System.Text.UTF8Encoding($false)))
}

function Invoke-BdMoviePromote {
    param([Parameter(Mandatory)][string]$Id, [Parameter(Mandatory)][string]$Movie, [Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Why,
          [ValidateSet('same-rom', 'required', 'off')][string]$Sync = 'same-rom', [string]$Exe = '', [switch]$AllowOffCourse)
    if ($Name -notmatch '^[A-Za-z0-9_.\-]+$') { throw "movie name '$Name': letters, digits, - _ ." }
    $src = Resolve-BdMoviePath -Id $Id -Movie $Movie
    $m = Read-BdManifest -Id $Id
    if (@(Get-BdProp $m 'movies' @() | Where-Object { [string]$_.id -eq $Name }).Count -gt 0) { throw "project $Id already has a promoted movie '$Name'" }
    $info = Invoke-BdWorkshopMovie -Arguments @('info', '--movie', $src) -Exe $Exe
    $ij = Get-BdJsonFromOutput $info.Output
    if (-not $ij) { throw "not a movie: $src ($(Select-BdTail $info.Output 3))" }
    if ($ij.start -ne 'power-on') { throw "the movie starts from a save state; a verify test starts at power-on (record it from power-on)" }
    # it must replay in sync on today's build (a test that fails the day it is promoted tests nothing)
    $play = Invoke-BdMoviePlay -Id $Id -Movie $src -Exe $Exe
    if (-not $play.inSync -and -not $AllowOffCourse) { throw "the movie does not replay in sync on today's build of ${Id}: $($play.summary)" }
    $dir = Join-Path (Join-Path (Get-BdProjectsDir) $Id) 'movies'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $dst = Join-Path $dir ($Name + [string]$ij.extension)
    if (Test-Path -LiteralPath $dst) { throw "$dst already exists" }
    Copy-Item -LiteralPath $src -Destination $dst
    $entry = [ordered]@{ id = $Name; file = "movies/$Name$([string]$ij.extension)"; why = $Why; sync = $Sync; frames = [long]$ij.frames; recordedOn = [string]$ij.romSha256 }
    # one line, in the manifests' own style ({ "key": value, ... })
    $inline = '{ ' + ((@($entry.Keys | ForEach-Object { '"{0}": {1}' -f $_, ($entry[$_] | ConvertTo-Json -Compress) })) -join ', ') + ' }'
    try { Add-BdManifestMovie -Path (Get-BdManifestPath $Id) -EntryJson $inline }
    catch { Remove-Item -LiteralPath $dst -Force; throw }
    [void](Read-BdManifest -Id $Id)   # still valid
    [pscustomobject]@{ Id = $Name; File = $dst; Sync = $Sync; Play = $play.summary }
}

# ---------------------------------------------------------------------------------------------------- bisect

function Invoke-BdGit {
    param([Parameter(Mandatory)][string]$Repo, [Parameter(Mandatory)][string[]]$GitArgs, [switch]$AllowFail)
    $git = Find-BdExecutable -Name 'git.exe' -Candidates @("$env:ProgramFiles\Git\cmd\git.exe")
    if (-not $git) { throw 'git.exe not found' }
    $r = Invoke-BdProcess -FilePath $git -Arguments (@('-C', $Repo) + $GitArgs) -TimeoutSec 600
    if ($r.ExitCode -ne 0 -and -not $AllowFail) { throw "git $($GitArgs -join ' ') failed in ${Repo}: $(Select-BdTail $r.Output 4)" }
    $r
}

function Get-BdGitOut { param([string]$Repo, [string[]]$GitArgs) ((Invoke-BdGit -Repo $Repo -GitArgs $GitArgs).Output -split "`r?`n" | Where-Object { $_ -and $_ -notlike '--- stderr ---*' } | ForEach-Object { $_.Trim() }) }

function Invoke-BdBuildAtCommit {
    <#
      Builds one commit of the game in the scratch clone: the manifest's build steps with the project's source folder (or its archive roots)
      moved into the clone, the outputs collected into $OutDir (never the project's own out folder). Returns { Ok; Rom; Symbols; Error }.
    #>
    param([Parameter(Mandatory)]$Manifest, [Parameter(Mandatory)][string]$Clone, [Parameter(Mandatory)][string]$RepoTop, [Parameter(Mandatory)][string]$OutDir)
    $ctx = New-BdContext $Manifest
    $topFull = ([System.IO.Path]::GetFullPath($RepoTop)).TrimEnd('\') + '\'
    $move = { param([string]$p) $f = [System.IO.Path]::GetFullPath($p); if (($f.TrimEnd('\') + '\').StartsWith($topFull, [System.StringComparison]::OrdinalIgnoreCase)) { Join-Path $Clone ($f.Substring($topFull.Length - 1).TrimStart('\')) } else { $f } }
    if ($ctx.ContainsKey('src')) { $ctx['src'] = & $move $ctx['src'] }
    $roots = @{}
    foreach ($k in @($ctx['Roots'].Keys)) { $roots[$k] = & $move $ctx['Roots'][$k] }
    $ctx['Roots'] = $roots
    $ctx['Out'] = $OutDir
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $logDir = Join-Path $OutDir 'logs'
    $baseEnv = @{}
    if ($ctx.ContainsKey('python')) { $baseEnv['BROKENDEV_PYTHON'] = [string]$ctx['python'] }
    $started = (Get-Date).ToUniversalTime()
    foreach ($s in @($Manifest.build.steps)) {
        try { $r = Invoke-BdBuildStep -Step $s -Ctx $ctx -LogDir $logDir -BaseEnv $baseEnv }
        catch { return [pscustomobject]@{ Ok = $false; Rom = ''; Symbols = @(); Error = "step '$($s.name)': $($_.Exception.Message)" } }
        if ($r.TimedOut -or $r.ExitCode -ne 0) { return [pscustomobject]@{ Ok = $false; Rom = ''; Symbols = @(); Error = "step '$($s.name)' exit $($r.ExitCode): $($r.Tail -replace "`r?`n", ' | ')" } }
    }
    $rom = ''; $syms = @()
    foreach ($o in @($Manifest.build.outputs)) {
        $pth = Expand-BdPath -Text ([string]$o.path) -Context $ctx
        if (-not (Test-Path -LiteralPath $pth)) { if ($o.id -eq 'rom') { return [pscustomobject]@{ Ok = $false; Rom = ''; Symbols = @(); Error = "the build made no ROM ($pth)" } } else { continue } }
        if ((Get-Item -LiteralPath $pth).LastWriteTimeUtc -lt $started.AddSeconds(-2)) { if ($o.id -eq 'rom') { return [pscustomobject]@{ Ok = $false; Rom = ''; Symbols = @(); Error = "the ROM was not rewritten by this build ($pth)" } } else { continue } }
        $dst = Join-Path $OutDir ([string]$o.publishAs)
        Copy-Item -LiteralPath $pth -Destination $dst -Force
        if ($o.id -eq 'rom') { $rom = $dst } elseif ([string](Get-BdProp $o 'kind' '') -eq 'symbols') { $syms += $dst }
    }
    [pscustomobject]@{ Ok = $true; Rom = $rom; Symbols = $syms; Error = '' }
}

function Invoke-BdBisect {
    <#
      Bisects a game's commits for the first one where a movie's test turns bad (Workshop --movie test: good, bad, can't tell, cannot run).
      Returns the report object (also written to out\<id>\bisect\bisect.json): the first bad commit, every commit tested with its verdict,
      the ones skipped (no build) and the ones that can't tell.
    #>
    param([Parameter(Mandatory)][string]$Id, [Parameter(Mandatory)][string]$Movie, [Parameter(Mandatory)][string]$Test, [string]$Good = '', [string]$Bad = 'HEAD',
          [string]$Repo = '', [string]$Exe = '', [int]$MaxSteps = 40)
    $m = Read-BdManifest -Id $Id
    $movieFile = Resolve-BdMoviePath -Id $Id -Movie $Movie
    if (-not $Repo) {
        $ws = Get-BdProjectWorkspace -Manifest $m
        if ($ws.Kind -eq 'absent') { throw "project ${Id}: $($ws.Detail)" }
        $Repo = $ws.Path
    }
    $gitTop = @(Get-BdGitOut -Repo $Repo -GitArgs @('rev-parse', '--show-toplevel'))[0]
    if (-not $gitTop) { throw "$Repo is not inside a git repository" }
    $gitTop = [System.IO.Path]::GetFullPath($gitTop.Replace('/', '\'))
    $work = Join-Path (Join-Path (Join-Path (Get-BdHome) 'work') $Id) 'bisect'
    $clone = Join-Path $work 'repo'
    if (Test-Path -LiteralPath $clone) { Remove-Item -LiteralPath $clone -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    $git = Find-BdExecutable -Name 'git.exe' -Candidates @("$env:ProgramFiles\Git\cmd\git.exe")
    $cl = Invoke-BdProcess -FilePath $git -Arguments @('clone', '--quiet', '--no-hardlinks', $gitTop, $clone) -TimeoutSec 900
    if ($cl.ExitCode -ne 0) { throw "git clone of $gitTop failed: $(Select-BdTail $cl.Output 4)" }
    # the clone can never push anything back: it has no remote
    [void](Invoke-BdGit -Repo $clone -GitArgs @('remote', 'remove', 'origin'))
    # the revisions the user named exist in the original; the clone has them all (a full clone), branches as origin/* are gone with the remote,
    # so they are resolved in the original first
    $badSha = @(Get-BdGitOut -Repo $gitTop -GitArgs @('rev-parse', '--verify', "$Bad^{commit}"))[0]
    if (-not $Good) {
        $mvText = [System.IO.File]::ReadAllText($movieFile)
        if ($mvText -match '(?m)^build .*?\b([0-9a-f]{7,40})\b') { $Good = $Matches[1] } else { throw "bisect needs -Good <commit> (a commit where the test holds; the movie's build line names none)" }
    }
    $goodSha = @(Get-BdGitOut -Repo $gitTop -GitArgs @('rev-parse', '--verify', "$Good^{commit}"))[0]
    $anc = Invoke-BdGit -Repo $clone -GitArgs @('merge-base', '--is-ancestor', $goodSha, $badSha) -AllowFail
    if ($anc.ExitCode -ne 0) { throw "the good commit $($goodSha.Substring(0, 10)) is not an ancestor of the bad one $($badSha.Substring(0, 10))" }
    $list = @(Get-BdGitOut -Repo $clone -GitArgs @('rev-list', '--reverse', '--first-parent', '--ancestry-path', "$goodSha..$badSha"))
    $commits = @($goodSha) + $list
    if ($commits[-1] -ne $badSha) { throw "the bad commit is not on the first-parent path from the good one" }
    $subjects = @{}
    foreach ($c in $commits) { $subjects[$c] = @(Get-BdGitOut -Repo $clone -GitArgs @('log', '-1', '--format=%s', $c))[0] }
    $results = @{}
    $order = New-Object System.Collections.Generic.List[object]
    $sym = @()
    $tryOne = {
        param([int]$i)
        $c = $commits[$i]
        if ($results.ContainsKey($c)) { return $results[$c] }
        [void](Invoke-BdGit -Repo $clone -GitArgs @('checkout', '--quiet', '--force', $c))
        [void](Invoke-BdGit -Repo $clone -GitArgs @('clean', '-fdxq'))
        $odir = Join-Path $work ("c{0:D3}-{1}" -f $i, $c.Substring(0, 10))
        if (Test-Path -LiteralPath $odir) { Remove-Item -LiteralPath $odir -Recurse -Force }
        $b = Invoke-BdBuildAtCommit -Manifest $m -Clone $clone -RepoTop $gitTop -OutDir $odir
        $rec = [ordered]@{ index = $i; commit = $c; subject = $subjects[$c]; built = $b.Ok; verdict = ''; why = '' }
        if (-not $b.Ok) { $rec.verdict = 'skip'; $rec.why = "does not build: $($b.Error)" }
        else {
            $a = @('test', '--movie', $movieFile, '--rom', $b.Rom, '--test', $Test, '--json')
            foreach ($s in $b.Symbols) { $a += @('--sym', $s) }
            $fw = Get-BdProp (Get-BdProp $m 'checks' $null) 'frameWait' @()
            foreach ($w in @($fw)) { $a += @('--frame-wait', [string]$w) }
            $t = Invoke-BdWorkshopMovie -Arguments $a -Exe $Exe -LogFile (Join-Path $odir 'test.log')
            $j = Get-BdJsonFromOutput $t.Output
            $rec.verdict = switch ($t.ExitCode) { 0 { 'good' } 1 { 'bad' } 4 { 'cant-tell' } 3 { 'skip' } default { 'error' } }
            $rec.why = if ($j) { [string]$j.why } else { Select-BdTail $t.Output 3 }
            if ($rec.verdict -eq 'error') { throw "the test could not run on $($c.Substring(0, 10)) (exit $($t.ExitCode)): $($rec.why)" }
        }
        $results[$c] = [pscustomobject]$rec
        $order.Add($results[$c])
        Write-BdInfo ("  {0,-9} {1} {2}  ({3})" -f $rec.verdict, $c.Substring(0, 10), $rec.subject, $rec.why)
        $results[$c]
    }
    Write-BdInfo "bisect $Id over $($commits.Count) commits ($($goodSha.Substring(0, 10))..$($badSha.Substring(0, 10))): test '$Test' on $([System.IO.Path]::GetFileName($movieFile))"
    $g = & $tryOne 0
    $bd = & $tryOne ($commits.Count - 1)
    $report = [ordered]@{ project = $Id; movie = $movieFile; test = $Test; repository = $gitTop; clone = $clone; good = $goodSha; bad = $badSha; commits = $commits.Count; firstBad = $null; firstBadSubject = $null; candidates = @(); verdict = ''; tested = $null; builds = 0; skipped = @(); cantTell = @() }
    if ($g.verdict -ne 'good' -or $bd.verdict -ne 'bad') {
        $report.verdict = "the endpoints do not hold: the good commit is '$($g.verdict)' ($($g.why)), the bad one '$($bd.verdict)' ($($bd.why)); bisect does not guess"
    } else {
        $lo = 0; $hi = $commits.Count - 1
        $untestable = @{}
        for ($step = 0; $step -lt $MaxSteps; $step++) {
            $cands = @(($lo + 1)..($hi - 1) | Where-Object { $_ -gt $lo -and $_ -lt $hi -and -not $untestable.ContainsKey($_) })
            if ($hi - $lo -le 1 -or $cands.Count -eq 0) { break }
            $mid = [math]::Floor(($lo + $hi) / 2)
            $pick = @($cands | Sort-Object { [math]::Abs($_ - $mid) }, { $_ })[0]
            $r = & $tryOne $pick
            switch ($r.verdict) { 'good' { $lo = $pick } 'bad' { $hi = $pick } default { $untestable[$pick] = $true } }
        }
        $between = @(($lo + 1)..($hi - 1) | Where-Object { $_ -gt $lo -and $_ -lt $hi })
        if ($between.Count -eq 0) {
            $report.firstBad = $commits[$hi]; $report.firstBadSubject = $subjects[$commits[$hi]]
            $report.verdict = "the first bad commit is $($commits[$hi].Substring(0, 10)) '$($subjects[$commits[$hi]])' (the last good: $($commits[$lo].Substring(0, 10)) '$($subjects[$commits[$lo]])')"
        } else {
            $report.candidates = @(@($between + $hi) | ForEach-Object { $commits[$_] })
            $report.verdict = "can't tell which: the first bad commit is one of $($report.candidates.Count) (the ones between can't be tested: $((@($between | ForEach-Object { $commits[$_].Substring(0, 10) + ' ' + $results[$commits[$_]].verdict })) -join ', '))"
        }
    }
    $report.tested = $order.ToArray()
    $report.builds = @($order | Where-Object { $_.built }).Count
    $report.skipped = @($order | Where-Object { $_.verdict -eq 'skip' } | ForEach-Object { $_.commit })
    $report.cantTell = @($order | Where-Object { $_.verdict -eq 'cant-tell' } | ForEach-Object { $_.commit })
    Save-BdJson -Object $report -Path (Join-Path $work 'bisect.json') -Depth 8
    [pscustomobject]$report
}
