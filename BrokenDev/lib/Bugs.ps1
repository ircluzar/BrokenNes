# BrokenDev bug captures (milestone D6 of docs/projects/brokendev-debugging-plan.md). Requires Common, Tools, Manifest, Build, Movies.
#
#   bugs [project]              the bug bundles of one project (or of every project) and the default folder's that belong to it: when, the note,
#                               how long, whether it has a movie; and whether the game workspace's bugs\ folder is kept out of git
#   bug <id|folder|zip> [-Debugger] [-Script "cmds"] [-Json]
#                               replays one headless (Workshop --bug play: every keyframe and the last picture checked, the hardware checks on,
#                               the replay's last picture written beside the work folder); -Debugger opens it in the headless debugger instead
#                               (Workshop --debug --bug: 'run' replays it, 'back' steps back through it; -Script gives the commands)
#
# Where the desktop app saves them (Windows/NesEmulator/debug/capture/BugBundle.cs, BugPlaces): <game workspace>\bugs\<date_time>\ for a ROM that is
# a BrokenDev project's build (the build record's 'bugs', else its live workspace), else %LOCALAPPDATA%\BrokenNes\bugs (BROKENNES_BUGS_DIR
# replaces it). Lite gives a .zip download: 'bug <zip>' replays it.

Set-StrictMode -Version Latest

function Get-BdBugsDefaultDir {
    <# The desktop app's default bugs folder (a ROM of no project): BROKENNES_BUGS_DIR, else %LOCALAPPDATA%\BrokenNes\bugs. #>
    if ($env:BROKENNES_BUGS_DIR) { return $env:BROKENNES_BUGS_DIR }
    Join-Path (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'BrokenNes') 'bugs'
}

function Get-BdProjectBugsDir {
    <# A project's bugs folder: <its live / own workspace>\bugs, or $null with the reason (a restored copy is not the user's workspace). #>
    param([Parameter(Mandatory)]$Manifest)
    $ws = Get-BdProjectWorkspace -Manifest $Manifest
    if ($ws.Kind -in 'live', 'folder' -and $ws.Path) { return [pscustomobject]@{ Dir = (Join-Path $ws.Path 'bugs'); Workspace = $ws.Path; Why = "the game workspace ($($ws.Kind), $($ws.Source))" } }
    [pscustomobject]@{ Dir = $null; Workspace = $null; Why = "no live workspace ($($ws.Kind): $($ws.Detail)): its captures go to the default folder" }
}

function Test-BdBugsIgnored {
    <#
      Whether a workspace's bugs\ folder is kept out of git: 'outside git' (no repository holds it), 'ignored' (git check-ignore says so), or
      'NOT IGNORED' (a repository would see the captures: add '/bugs/' to its .gitignore). The workspace is only read.
    #>
    param([Parameter(Mandatory)][string]$Workspace)
    $git = Find-BdExecutable -Name 'git.exe' -Candidates @("$env:ProgramFiles\Git\cmd\git.exe")
    if (-not $git) { return [pscustomobject]@{ State = 'unknown'; Detail = 'git.exe not found' } }
    $r = Invoke-BdProcess -FilePath $git -Arguments @('-C', $Workspace, 'rev-parse', '--show-toplevel') -TimeoutSec 60
    if ($r.ExitCode -ne 0) { return [pscustomobject]@{ State = 'outside git'; Detail = "no git repository holds $Workspace" } }
    $top = (($r.Output -split "`r?`n") | Where-Object { $_ -and $_ -notlike '--- stderr ---*' } | Select-Object -First 1).Trim()
    $probe = Join-Path (Join-Path $Workspace 'bugs') '2000-01-01_000000\bug.json'
    $c = Invoke-BdProcess -FilePath $git -Arguments @('-C', $Workspace, 'check-ignore', '-q', '--no-index', $probe) -TimeoutSec 60
    if ($c.ExitCode -eq 0) { return [pscustomobject]@{ State = 'ignored'; Detail = "git ignores it in the repository $top" } }
    [pscustomobject]@{ State = 'NOT IGNORED'; Detail = "the repository $top would see the captures: add '/bugs/' to its .gitignore" }
}

function Get-BdBugList {
    <# The bundles of a folder (Workshop --bug list --json): newest first; an empty list when the folder does not exist. #>
    param([Parameter(Mandatory)][string]$Dir, [string]$Exe = '')
    if (-not (Test-Path -LiteralPath $Dir)) { return @() }
    if (-not $Exe) { $Exe = Get-BdWorkshopExe }
    if (-not $Exe) { throw 'BrokenNes Workshop is not built. Run: brokendev.ps1 build-workshop' }
    $r = Invoke-BdProcess -FilePath $Exe -Arguments @('--bug', 'list', '--dir', $Dir, '--json') -TimeoutSec 300 -Env (Get-BdChildEnv)
    $j = Get-BdJsonFromOutput $r.Output
    if ($r.ExitCode -ne 0 -or -not $j) { throw "Workshop --bug list failed (exit $($r.ExitCode)): $(Select-BdTail $r.Output 4) (an older Workshop? run: brokendev.ps1 build-workshop)" }
    @($j.bugs)
}

function Get-BdBugs {
    <#
      The captures of one project (or all): its workspace's bugs\ folder, and the default folder's bundles whose bug.json names the project or
      whose ROM is its built ROM. Each row: project, where, the Workshop's summary. Also the git state of each workspace's bugs\.
    #>
    param([string]$Id = '', [string]$Exe = '')
    $ids = if ($Id) { @($Id) } else { Get-BdProjectIds }
    $rows = New-Object System.Collections.Generic.List[object]
    $folders = New-Object System.Collections.Generic.List[object]
    $default = Get-BdBugsDefaultDir
    $defaultBugs = @(Get-BdBugList -Dir $default -Exe $Exe)
    foreach ($p in $ids) {
        $m = Read-BdManifest -Id $p
        $b = Get-BdProjectBugsDir -Manifest $m
        $git = if ($b.Workspace) { Test-BdBugsIgnored -Workspace $b.Workspace } else { $null }
        $folders.Add([pscustomobject]@{ Project = $p; Dir = $b.Dir; Why = $b.Why; Git = $git })
        if ($b.Dir) { foreach ($x in @(Get-BdBugList -Dir $b.Dir -Exe $Exe)) { $rows.Add([pscustomobject]@{ Project = $p; Where = 'workspace'; Bug = $x }) } }
        $sha = $null
        $bp = Join-Path (Get-BdOutDir $p) 'build.json'
        if (Test-Path -LiteralPath $bp) { $rec = Read-BdJson $bp; if ($rec.ok) { $sha = @($rec.outputs | Where-Object { $_.id -eq 'rom' })[0].sha256 } }
        foreach ($x in $defaultBugs) {
            $xp = Get-BdProp $x 'project' $null; $xs = Get-BdProp $x 'romSha256' ''
            if (($xp -and $xp -eq $p) -or ($sha -and $xs -eq $sha)) { $rows.Add([pscustomobject]@{ Project = $p; Where = 'default folder'; Bug = $x }) }
        }
    }
    [pscustomobject]@{ Rows = $rows; Folders = $folders; Default = $default }
}

function Resolve-BdBug {
    <# A bundle by path (folder, bug.json or zip), or by its id (the folder's name) among every project's bugs folders and the default folder. #>
    param([Parameter(Mandatory)][string]$Bug)
    if (Test-Path -LiteralPath $Bug) { return (Resolve-Path -LiteralPath $Bug).ProviderPath }
    $dirs = @((Get-BdBugsDefaultDir))
    foreach ($p in Get-BdProjectIds) { $b = Get-BdProjectBugsDir -Manifest (Read-BdManifest -Id $p); if ($b.Dir) { $dirs += $b.Dir } }
    $hits = @()
    foreach ($d in $dirs) { foreach ($n in @($Bug, ($Bug + '.zip'))) { $c = Join-Path $d $n; if (Test-Path -LiteralPath $c) { $hits += $c } } }
    if ($hits.Count -eq 1) { return $hits[0] }
    if ($hits.Count -gt 1) { throw "bug '$Bug' is in more than one bugs folder: $($hits -join '; ') (give its path)" }
    throw "no bug '$Bug' (a bundle folder or zip, or a bundle's id in: $($dirs -join '; '))"
}

function Invoke-BdBug {
    <# Replays one bundle (Workshop --bug play --checks), or opens it in the headless debugger (-Debugger). Returns the Workshop's report. #>
    param([Parameter(Mandatory)][string]$Bug, [switch]$Debugger, [string]$Script = '', [string]$Exe = '')
    $path = Resolve-BdBug -Bug $Bug
    if (-not $Exe) { $Exe = Get-BdWorkshopExe }
    if (-not $Exe) { throw 'BrokenNes Workshop is not built. Run: brokendev.ps1 build-workshop' }
    if ($Debugger) {
        $a = @('--debug', '--bug', $path)
        if ($Script) { $a += @('--do', $Script) }
        $r = Invoke-BdProcess -FilePath $Exe -Arguments $a -TimeoutSec 1800 -Env (Get-BdChildEnv)
        return [pscustomobject]@{ Path = $path; ExitCode = $r.ExitCode; Output = $r.Output; InSync = ($r.Output -match 'bug replay: in sync') }
    }
    $work = Join-Path (Join-Path (Get-BdHome) 'work') 'bugs'
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    $r = Invoke-BdProcess -FilePath $Exe -Arguments @('--bug', 'play', '--bug', $path, '--checks', '--out', $work, '--json') -TimeoutSec 1800 -Env (Get-BdChildEnv)
    $j = Get-BdJsonFromOutput $r.Output
    if (-not $j) { throw "Workshop --bug play failed (exit $($r.ExitCode)): $(Select-BdTail $r.Output 6)" }
    $j | Add-Member -NotePropertyName exitCode -NotePropertyValue $r.ExitCode -Force
    $j
}
