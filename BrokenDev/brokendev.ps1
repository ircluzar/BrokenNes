<#
.SYNOPSIS
  BrokenDev: the developer-only devkit module of BrokenNes. Restore, build and verify the homebrew projects.

.DESCRIPTION
  Verbs
    doctor                       check the machine and the shipped toolchains (see doctor.ps1)
    list                         the projects, whether they are restored / built / verified
    restore <project|archive|all>  extract the project archives into BROKENDEV_HOME (-Full: also the bulky extras, -Force: again)
    build   <project|all>        run the project's build steps (dependencies first); outputs go to BROKENDEV_HOME\out\<id>\
    verify  <project|all>        run the built ROM on BrokenNes (headless Workshop) and record the checks in out\<id>\verify.json
    diff    <project|all>        differential replay of the built ROM, BrokenNes against Mesen 2 (a black-box oracle); out\<id>\diff.json
    run     <project>            launch the built ROM in the BrokenNes desktop app (through its HTTP API)
    build-workshop               build the BrokenNes Workshop (the headless runner verify uses)
    status  <project>            the build and verify records of one project
    tools   [project]            the developer tools (editors, checkers) of every project or of one (devtools.json)
    tool    <id> [args...]       start one: a web UI on its own port in the default browser (-NoBrowser prints the address,
                                 -Stop stops the one BrokenDev started), or a script in the foreground (args are passed to it)
    emulators                    the other emulators BrokenDev tests the games on (emulators.json): installed or not, version, consoles,
                                 what each one's column can check
    emulators install <id|all>   unpack the PINNED release (Mesen, BizHawk, FCEUX, Snes9x) portable into BROKENDEV_HOME\emulators; a zip
                                 already in emulators\_downloads with the pinned size and sha256 is used, anything else is refused
    toolchains                   the pinned toolchains (toolchains.json: GBDK-2020 for the Game Boy): ready, or what is wrong (never downloads)
    play    <project> -Emu <id>  open the built ROM in that emulator for hand testing (saves in work\play\<project>\<emu>; BrokenNes: 'run')
    new widemess <name> [-Path folder] [-Title T]
                                 a new WideMess game from the devkit's template (BrokenDev/widemess): its folder (default
                                 BROKENDEV_HOME\projects\<name>) and its manifest projects\<name>, ready to build / verify / run
                                 (docs/projects/widemess-devkit.md)
    compat  <project|all> [-Emu id,id] [-Scenario id,id]
                                 the project's compat scenarios on BrokenNes (the reference column, always first) and on every installed
                                 emulator of its console: the game's state read every frame, pictures, saves; a verdict per emulator
                                 (works / differs / fails / cannot be tested) in out\<id>\compat\compat.json and compat.html
    movie   record|play|export|promote|list <project>
                                 BrokenNes's own movies (milestone D5; the format's working name and extension: Workshop --movie format):
                                 'record' (-Scenario <verify id>, or -Script "120:Start,..." -Frames N; -Name, -Course sym,sym) a movie of the
                                 built ROM into out\<id>\movies; 'play' (-Movie) replays it: in sync or where it went off course; 'export'
                                 (-Movie, -Format fm2|bk2) for FCEUX / BizHawk; 'promote' (-Movie, -Name, -Why, -Sync same-rom|required|off)
                                 makes it a verify test (projects\<id>\movies + the manifest's movies section; only promoted movies are tests)
    bisect  <project> -Movie <file|name> -Test "<test>" [-Good <commit>] [-Bad <commit>] [-Repo <folder>]
                                 the first of the GAME's commits where the movie's test turns bad, with today's BrokenNes: a scratch clone
                                 (never pushed, no remote), built halfway back each time; a commit that does not build is skipped, one where
                                 the replay loses its course says can't tell. Tests: no-crash | no-lag [from N] | picture at N | <expression> at N
    mcp     install|uninstall|build|status|test
                                 the BrokenNes MCP server (BrokenDev/mcp): 'install' builds it into BROKENDEV_HOME\bnbuild\mcp and registers it
                                 for Claude Code at USER scope ('claude mcp add --scope user brokennes', every session and game repo);
                                 'uninstall' removes that one entry; 'test' runs the scripted MCP client test and the planted-bug demo

  Workspaces
    Every project works on the user's LIVE workspace when one sits beside the repository (VRUN_Nes_Dev, SuperVRUN_dev), else on the
    copy 'restore' extracted into BROKENDEV_HOME. 'list' shows which. BROKENDEV_LIVE_<ARCHIVE> (e.g. BROKENDEV_LIVE_VRUN_NES_DEV) = a
    folder overrides the search; = off uses the restored copy.

  Environment
    BROKENDEV_HOME      working folder, OUTSIDE the repository (default P:\BrokenDev\env)
    BROKENDEV_ARCHIVES  folder holding the project archives (default: the parent of BROKENDEV_HOME)
    BROKENDEV_LIVE_<ARCHIVE>  a live workspace folder, or 'off' (see Workspaces)
    BROKENDEV_PYTHON    force a Python interpreter (default: the first with pillow and numpy)
    BROKENDEV_WORKSHOP  force a Workshop executable
    BROKENDEV_TRACE_LUA another Mesen tracer for 'diff' of the NES projects (default: BrokenDev's copy, tools\vrun\mesen\state_trace.lua)
    BROKENDEV_EMULATORS another emulator registry (the tests use it; default emulators.json)
    SMB1_ORIG_ROM       the user's own original SMB1 ROM (not needed to build the remake; for regenerating its data; never copied anywhere)

  Exit codes: 0 ok, 1 a check or build failed, 2 usage error, 3 prerequisites missing.
  Works in Windows PowerShell 5.1 and PowerShell 7.

.EXAMPLE
  powershell -NoProfile -File BrokenDev\brokendev.ps1 list
  powershell -NoProfile -File BrokenDev\brokendev.ps1 build vrun-nes
  powershell -NoProfile -File BrokenDev\brokendev.ps1 tools
  powershell -NoProfile -File BrokenDev\brokendev.ps1 tool vrun-tile-editor
  powershell -NoProfile -File BrokenDev\brokendev.ps1 emulators install all
  powershell -NoProfile -File BrokenDev\brokendev.ps1 compat vrun-nes
  powershell -NoProfile -File BrokenDev\brokendev.ps1 compat all -Emu mesen,fceux
  powershell -NoProfile -File BrokenDev\brokendev.ps1 play supervrun -Emu snes9x
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Verb = 'help',
    [Parameter(Position = 1)][string]$Target = '',
    [switch]$Full,
    [switch]$Force,
    [switch]$Json,
    [switch]$DryRun,
    [switch]$NoDeps,
    [switch]$NoBrowser,
    [switch]$Stop,
    [string]$ArtifactsPath = '',
    [string]$Exe = '',
    # 'new widemess <name>': the project's folder and title
    [string]$Path = '',
    [string]$Title = '',
    # 'play' / 'compat': the emulator ids (comma list; 'compat' default: every installed one that runs the project's console)
    [string[]]$Emu = @(),
    # 'compat': only these scenarios of the project's compat section (comma list)
    [string[]]$Scenario = @(),
    # 'movie' / 'bisect' (milestone D5)
    [string]$Movie = '',
    [string]$Name = '',
    [string]$Why = '',
    [string]$Sync = 'same-rom',
    [string]$Script = '',
    [int]$Frames = 0,
    [string[]]$Course = @(),
    [string]$Format = 'bk2',
    [string]$Test = '',
    # (-Good / -Bad: aliases; the variables are named apart from the $bad lists the other verbs keep)
    [Alias('Good')][string]$GoodCommit = '',
    [Alias('Bad')][string]$BadCommit = 'HEAD',
    [string]$Repo = '',
    # 'tool <id> <args...>': the arguments after the tool id are passed to the tool
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Rest = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'lib\Common.ps1')
. (Join-Path $here 'lib\Tools.ps1')
. (Join-Path $here 'lib\Archive.ps1')
. (Join-Path $here 'lib\Toolchains.ps1')
. (Join-Path $here 'lib\Manifest.ps1')
. (Join-Path $here 'lib\Build.ps1')
. (Join-Path $here 'lib\Verify.ps1')
. (Join-Path $here 'lib\Diff.ps1')
. (Join-Path $here 'lib\DevTools.ps1')
. (Join-Path $here 'lib\Emulators.ps1')
. (Join-Path $here 'lib\Compat.ps1')
. (Join-Path $here 'lib\WideMess.ps1')
. (Join-Path $here 'lib\Mcp.ps1')
. (Join-Path $here 'lib\Movies.ps1')

function Show-BdHelp { Write-Host (Get-Help (Join-Path $here 'brokendev.ps1') -Full | Out-String) }

function Resolve-BdTargets {
    <# 'all' -> every project; otherwise one project id. Unknown ids are an error, not an empty set. #>
    param([string]$Name)
    $ids = Get-BdProjectIds
    if ($Name -eq 'all') { return $ids }
    if (-not $Name) { throw "this verb needs a project id or 'all' (known: $($ids -join ', '))" }
    if ($ids -notcontains $Name) { throw "unknown project '$Name' (known: $($ids -join ', '))" }
    @($Name)
}

function Get-BdProjectStatus {
    param([string]$Id)
    $m = Read-BdManifest -Id $Id
    $archives = Get-BdArchiveTable
    $ws = Get-BdProjectWorkspace -Manifest $m -Table $archives
    # a project with its own folder (a WideMess project) has nothing to restore
    $st = if ($m.source.PSObject.Properties['folder']) { [pscustomobject]@{ State = 'n/a' } } else { Get-BdRestoreState (Get-BdArchiveSpec -Id ([string]$m.source.archive) -Table $archives) }
    $out = Get-BdOutDir $Id
    $build = $null; $verify = $null
    $bp = Join-Path $out 'build.json'; $vp = Join-Path $out 'verify.json'
    if (Test-Path -LiteralPath $bp) { $build = Read-BdJson $bp }
    if (Test-Path -LiteralPath $vp) { $verify = Read-BdJson $vp }
    [pscustomobject]@{
        Id = $Id; Title = [string]$m.title; Kind = [string]$m.kind
        Workspace = $ws.Kind; WorkspacePath = $ws.Path; Archive = [string](Get-BdProp $m.source 'archive' '')
        Restored = $st.State
        Built = $(if ($build -and $build.ok) { 'yes' } elseif ($build) { 'FAILED' } else { 'no' })
        Rom = $(if ($build -and $build.ok) { ($build.outputs | Where-Object { $_.id -eq 'rom' } | Select-Object -First 1) } else { $null })
        Verify = $(if ($verify) { $(if ($verify.ok) { 'pass' } else { 'FAIL' }) } else { 'no' })
        VerifyRecord = $verify
    }
}

try {
    if ($Rest.Count -gt 0 -and @('tool', 'emulators', 'new', 'movie') -notcontains $Verb.ToLowerInvariant()) { throw "unexpected argument(s) for '$Verb': $($Rest -join ' ')" }
    # -Emu a,b and -Scenario a,b: one comma list or several values, the same either way (pwsh -File passes a,b as one string)
    $Emu = @($Emu | ForEach-Object { ([string]$_) -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $Scenario = @($Scenario | ForEach-Object { ([string]$_) -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    switch ($Verb.ToLowerInvariant()) {
        'help' { Show-BdHelp; exit 0 }
        'doctor' {
            $d = Join-Path $here 'doctor.ps1'
            if ($Json) { & $d -Json } else { & $d }
            exit $LASTEXITCODE
        }
        'list' {
            $rows = foreach ($id in (Get-BdProjectIds)) { Get-BdProjectStatus $id }
            if ($Json) { $rows | Select-Object Id, Title, Kind, Workspace, WorkspacePath, Restored, Built, Verify | ConvertTo-Json -Depth 3; exit 0 }
            Write-Host ("BROKENDEV_HOME = {0}" -f (Get-BdHome))
            Write-Host ''
            '{0,-22} {1,-7} {2,-10} {3,-9} {4,-7} {5,-7} {6}' -f 'PROJECT', 'KIND', 'WORKSPACE', 'RESTORED', 'BUILT', 'VERIFY', 'TITLE' | Write-Host
            foreach ($r in $rows) { '{0,-22} {1,-7} {2,-10} {3,-9} {4,-7} {5,-7} {6}' -f $r.Id, $r.Kind, $r.Workspace, $r.Restored, $r.Built, $r.Verify, $r.Title | Write-Host }
            Write-Host ''
            Write-Host 'WORKSPACES (live = the user''s own folder beside the repository; restored = the copy under BROKENDEV_HOME)'
            $table = Get-BdArchiveTable
            $rootInfo = Get-BdArchiveRoots -Table $table -Detailed
            foreach ($aid in $table.Keys) {
                $w = Get-BdWorkspace -Id $aid -Table $table
                '  {0,-15} {1,-9} {2}' -f $aid, $w.Kind, $w.Path | Write-Host
                $fromRestored = @($rootInfo.Keys | Where-Object { $rootInfo[$_].Archive -eq $aid -and $rootInfo[$_].From -eq 'restored' } | Sort-Object)
                if ($w.Kind -eq 'live' -and $fromRestored.Count -gt 0) { Write-Host ("  {0,-15} {1,-9} root(s) {2} still from the restored copy (no live equivalent)" -f '', '', ($fromRestored -join ', ')) }
                if ($w.Kind -ne 'live') { Write-Host ("  {0,-15} {1,-9} {2}" -f '', '', $w.Detail) }
            }
            Write-Host ("  override: BROKENDEV_LIVE_<ARCHIVE>=<folder> or =off  (e.g. {0})" -f (Get-BdLiveEnvName 'vrun_nes_dev'))
            exit 0
        }
        'tools' {
            if ($Target -and (Get-BdProjectIds) -notcontains $Target) { throw "unknown project '$Target' (known: $((Get-BdProjectIds) -join ', '))" }
            $rows = Get-BdToolRows -Project $Target
            if ($Json) { $rows | ConvertTo-Json -Depth 3; exit 0 }
            $fmt = '{0,-30} {1,-12} {2,-30} {3}'
            $fmt -f 'TOOL', 'PROJECT', 'STARTS', 'WHAT YOU EDIT OR CHECK WITH IT' | Write-Host
            foreach ($r in $rows) {
                $fmt -f $r.Id, $r.Project, $r.Starts, $r.Title | Write-Host
                # the longer description, wrapped under the title column
                $line = ''
                foreach ($word in ($r.Edits -split ' ')) {
                    if ($line.Length + $word.Length + 1 -gt 96 -and $line) { $fmt -f '', '', '', $line | Write-Host -ForegroundColor DarkGray; $line = $word }
                    else { $line = if ($line) { "$line $word" } else { $word } }
                }
                if ($line) { $fmt -f '', '', '', $line | Write-Host -ForegroundColor DarkGray }
            }
            Write-Host ''
            Write-Host 'start one: brokendev.ps1 tool <TOOL> [args...]   (-NoBrowser prints the address; -Stop stops a web UI BrokenDev started)'
            exit 0
        }
        'tool' {
            if (-not $Target) { throw "tool needs a tool id (see: brokendev.ps1 tools)" }
            $code = Invoke-BdTool -Id $Target -NoBrowser:$NoBrowser -Stop:$Stop -Extra $Rest
            exit $code
        }
        'restore' {
            if (-not $Target) { throw "restore needs a project id, an archive id or 'all'" }
            $table = Get-BdArchiveTable
            $archiveIds = @()
            if ($Target -eq 'all') { $archiveIds = @($table.Keys) }
            elseif ($table.Contains($Target)) { $archiveIds = @($Target) }
            else {
                foreach ($pid0 in (Resolve-BdProjectDeps -Id $Target)) { $a0 = [string](Get-BdProp (Read-BdManifest -Id $pid0).source 'archive' ''); if ($a0) { $archiveIds += $a0 } }
                $archiveIds = @($archiveIds | Select-Object -Unique)
            }
            New-Item -ItemType Directory -Force -Path (Get-BdHome) | Out-Null
            foreach ($a in $archiveIds) { [void](Restore-BdArchive -Id $a -Full:$Full -Force:$Force -Table $table) }
            exit 0
        }
        'build-workshop' {
            $exe = Invoke-BdBuildWorkshop -ArtifactsPath $ArtifactsPath
            Write-BdOk "workshop: $exe"
            exit 0
        }
        'build-app' {
            $exe = Invoke-BdBuildBrokenNes -Which app -ArtifactsPath $ArtifactsPath
            Write-BdOk "desktop app: $exe  (set BROKENDEV_APP to it, or leave it under BROKENDEV_HOME\bnbuild\app)"
            exit 0
        }
        'build' {
            $ids = Resolve-BdTargets $Target
            $order = @()
            foreach ($id in $ids) { if ($NoDeps) { $order += $id } else { $order += Resolve-BdProjectDeps -Id $id } }
            $order = @($order | Select-Object -Unique)
            $failed = @()
            foreach ($id in $order) {
                Write-BdInfo "== build $id"
                try { $rec = Invoke-BdProjectBuild -Id $id -DryRun:$DryRun; if (-not $DryRun) { foreach ($o in $rec.outputs) { Write-BdOk ("  {0}: {1} bytes sha256 {2}" -f $o.id, $o.bytes, $o.sha256.Substring(0, 16)) } } }
                catch { Write-BdFail $_.Exception.Message; $failed += $id }
            }
            if ($failed.Count -gt 0) { Write-BdFail ("build failed: " + ($failed -join ', ')); exit 1 }
            exit 0
        }
        'verify' {
            $ids = Resolve-BdTargets $Target
            $bad = @()
            foreach ($id in $ids) {
                Write-BdInfo "== verify $id"
                try { $rec = Invoke-BdProjectVerify -Id $id -Exe $Exe; if (-not $rec.ok) { $bad += $id } }
                catch { Write-BdFail $_.Exception.Message; $bad += $id }
            }
            if ($bad.Count -gt 0) { Write-BdFail ("verify failed: " + ($bad -join ', ')); exit 1 }
            exit 0
        }
        'diff' {
            $ids = Resolve-BdTargets $Target
            $bad = @()
            foreach ($id in $ids) {
                Write-BdInfo "== diff $id (BrokenNes against Mesen)"
                try {
                    $r = Invoke-BdProjectDiff -Id $id
                    switch ($r.status) {
                        'pass'    { Write-BdOk "  $id : pass" }
                        'skipped' { Write-BdWarn "$id : SKIPPED: $($r.reason)" }
                        default   { Write-BdFail "$id : FAIL: $($r.reason)"; $bad += $id }
                    }
                } catch { Write-BdFail $_.Exception.Message; $bad += $id }
            }
            if ($bad.Count -gt 0) { Write-BdFail ("diff failed: " + ($bad -join ', ')); exit 1 }
            exit 0
        }
        'run' {
            [void](Resolve-BdTargets $Target)
            Invoke-BdProjectRun -Id $Target
            exit 0
        }
        'status' {
            $r = Get-BdProjectStatus $Target
            $r | ConvertTo-Json -Depth 8
            exit 0
        }
        'emulators' {
            if (-not $Target -or $Target -eq 'list') {
                $rows = @(Get-BdEmulatorRows)
                if ($Json) { $rows | ConvertTo-Json -Depth 3; exit 0 }
                Write-Host ("emulators folder = {0}" -f (Get-BdEmuHome))
                Write-Host ''
                $fmt = '{0,-16} {1,-9} {2,-15} {3,-30} {4}'
                $fmt -f 'EMULATOR', 'CONSOLES', 'VERSION', 'STATE', 'ITS COLUMN CAN CHECK' | Write-Host
                foreach ($r in $rows) { $fmt -f $r.Id, $r.Consoles, $r.Version, $r.State, ($r.Can + $(if ($r.Reference) { '  (the reference)' } else { '' })) | Write-Host }
                Write-Host ''
                Write-Host 'install: brokendev.ps1 emulators install <emulator|package|all>   (the pinned releases only; a zip already in _downloads is reused)'
                exit 0
            }
            if ($Target -ne 'install') { throw "emulators: unknown action '$Target' (list, install)" }
            if ($Rest.Count -ne 1) { throw 'emulators install needs one emulator id, package id or all' }
            $bad = @()
            foreach ($p in (Resolve-BdEmuInstallTargets -Name $Rest[0])) {
                Write-BdInfo "== install $($p.id) $($p.version)"
                $pre = @(Test-BdEmuPrereqs $p)
                if ($pre.Count -gt 0) { Write-BdWarn ("$($p.id) needs system components this machine lacks: " + ($pre -join ', ') + ' (BrokenDev never installs system components; install them yourself)') }
                try { [void](Install-BdEmuPackage -Package $p -Force:$Force) } catch { Write-BdFail $_.Exception.Message; $bad += [string]$p.id }
            }
            if ($bad.Count -gt 0) { exit 1 }
            exit 0
        }
        'toolchains' {
            # the pinned toolchains (toolchains.json): ready (the zip and every pinned file match) or what is wrong; never downloads
            $rows = foreach ($t in @((Read-BdToolchains).toolchains)) { $st = Get-BdToolchainState -Spec $t; [pscustomobject]@{ Id = [string]$t.id; Version = [string]$t.version; State = $st.State; Dir = $st.Dir; Detail = $st.Detail; Title = [string]$t.title } }
            if ($Json) { $rows | ConvertTo-Json -Depth 3; exit 0 }
            foreach ($r in $rows) { '{0,-8} {1,-8} {2,-8} {3}' -f $r.Id, $r.Version, $r.State, $r.Dir | Write-Host; Write-Host "         $($r.Detail)" -ForegroundColor DarkGray }
            if (@($rows | Where-Object { $_.State -ne 'ready' }).Count -gt 0) { exit 1 }
            exit 0
        }
        'play' {
            [void](Resolve-BdTargets $Target)
            if ($Emu.Count -ne 1) { throw 'play needs one emulator: -Emu <id> (see: brokendev.ps1 emulators)' }
            Invoke-BdPlay -Project $Target -EmuId $Emu[0]
            exit 0
        }
        'compat' {
            $ids = Resolve-BdTargets $Target
            $bad = @()
            foreach ($id in $ids) {
                Write-BdInfo "== compat $id"
                try { $rec = Invoke-BdCompat -Project $id -EmuIds $Emu -Scenarios $Scenario; if (-not $rec.ok) { $bad += $id } }
                catch { Write-BdFail $_.Exception.Message; $bad += $id }
            }
            [void](Write-BdCompatMatrix)
            if ($bad.Count -gt 0) { Write-BdFail ("compat found problems in: " + ($bad -join ', ') + ' (see out\<project>\compat\compat.html)'); exit 1 }
            exit 0
        }
        'mcp' {
            switch ($Target.ToLowerInvariant()) {
                'build'     { Write-BdOk "mcp server: $(Invoke-BdBuildMcp -ArtifactsPath $ArtifactsPath)"; exit 0 }
                'install'   { $r = Install-BdMcp -Exe $Exe -DryRun:$DryRun; $r | Format-List | Out-String | Write-Host; Write-BdOk "registered '$($r.name)' at user scope: $($r.how)"; Write-BdInfo 'new Claude Code sessions have its tools (mcp__brokennes__*)'; exit 0 }
                'uninstall' { $r = Uninstall-BdMcp; Write-BdInfo $r.how; if ($r.removed) { Write-BdOk 'removed' }; exit 0 }
                'status'    { Get-BdMcpStatus | Format-List | Out-String | Write-Host; exit 0 }
                'test'      { if (Invoke-BdMcpTest -Exe $Exe) { Write-BdOk 'mcp tests passed'; exit 0 } else { Write-BdFail 'mcp tests failed'; exit 1 } }
                default     { throw "mcp: install, uninstall, build, status or test" }
            }
        }
        'movie' {
            # brokendev.ps1 movie <action> <project> ...
            if ($Rest.Count -ne 1) { throw 'movie needs an action and a project: brokendev.ps1 movie record|play|export|promote|list <project>' }
            $proj = $Rest[0]
            [void](Resolve-BdTargets $proj)
            switch ($Target.ToLowerInvariant()) {
                'record' { $f = Invoke-BdMovieRecord -Id $proj -Scenario $(if ($Scenario.Count -gt 0) { $Scenario[0] } else { '' }) -Script $Script -Frames $Frames -Name $Name -Course $Course -Exe $Exe; Write-BdOk "movie: $f"; exit 0 }
                'play' {
                    if (-not $Movie) { throw 'movie play needs -Movie <file or name>' }
                    $j = Invoke-BdMoviePlay -Id $proj -Movie $Movie -Exe $Exe
                    if ($Json) { $j | ConvertTo-Json -Depth 6 } elseif ($j.inSync) { Write-BdOk $j.summary } else { Write-BdFail $j.summary }
                    if ($j.inSync) { exit 0 } else { exit 1 }
                }
                'export' { if (-not $Movie) { throw 'movie export needs -Movie' }; Write-BdOk "exported: $(Invoke-BdMovieExport -Id $proj -Movie $Movie -Format $Format -Exe $Exe)"; exit 0 }
                'promote' {
                    if (-not $Movie -or -not $Name -or -not $Why) { throw 'movie promote needs -Movie, -Name and -Why (what it plays and why it is a test)' }
                    $r = Invoke-BdMoviePromote -Id $proj -Movie $Movie -Name $Name -Why $Why -Sync $Sync -Exe $Exe
                    Write-BdOk "promoted '$($r.Id)': $($r.File) (sync $($r.Sync)); $($r.Play)"
                    Write-BdInfo "  verify $proj now replays it; give it expectations in the manifest's movies entry ('expect', as a verify scenario's)"
                    exit 0
                }
                'list' {
                    $m = Read-BdManifest -Id $proj
                    Write-Host 'PROMOTED (verify tests)'
                    foreach ($e in @(Get-BdProp $m 'movies' @())) { '  {0,-22} {1,-9} {2}' -f $e.id, (Get-BdProp $e 'sync' 'same-rom'), $e.why | Write-Host }
                    Write-Host "RECORDED (out\$proj\movies)"
                    $d = Join-Path (Get-BdOutDir $proj) 'movies'
                    if (Test-Path -LiteralPath $d) { foreach ($f in Get-ChildItem -LiteralPath $d -File -Filter ('*' + (Get-BdMovieExtension -Exe $Exe))) { '  {0}' -f $f.Name | Write-Host } }
                    exit 0
                }
                default { throw "movie: record, play, export, promote or list" }
            }
        }
        'bisect' {
            [void](Resolve-BdTargets $Target)
            if (-not $Movie -or -not $Test) { throw 'bisect needs -Movie <file or name> and -Test "<test>" (no-crash | no-lag [from N] | picture at N | <expression> at N)' }
            $r = Invoke-BdBisect -Id $Target -Movie $Movie -Test $Test -Good $GoodCommit -Bad $BadCommit -Repo $Repo -Exe $Exe
            if ($Json) { $r | ConvertTo-Json -Depth 6 }
            if ($r.firstBad) { Write-BdOk $r.verdict } else { Write-BdWarn $r.verdict }
            Write-BdInfo ("  {0} build(s); skipped (no build): {1}; can't tell: {2}" -f $r.builds, @($r.skipped).Count, @($r.cantTell).Count)
            if ($r.firstBad) { exit 0 } else { exit 1 }
        }
        'new' {
            if ($Target -ne 'widemess') { throw "new: the one kind of project BrokenDev makes is 'widemess' (brokendev.ps1 new widemess <name> [-Path folder] [-Title T])" }
            if ($Rest.Count -ne 1) { throw 'new widemess needs one name: brokendev.ps1 new widemess <name> [-Path folder] [-Title T]' }
            $p = New-BdWideMessProject -Name $Rest[0] -Path $Path -Title $Title
            Write-BdOk "new WideMess project '$($p.Id)'"
            Write-BdInfo "  source:   $($p.Folder)   (manifest source.folder: $($p.FolderText))"
            Write-BdInfo "  manifest: $($p.Manifest)"
            Write-BdInfo "  next:     brokendev.ps1 build $($p.Id) ; brokendev.ps1 verify $($p.Id) ; brokendev.ps1 run $($p.Id)"
            Write-BdInfo "  guide:    docs/projects/widemess-devkit.md"
            exit 0
        }
        default { Write-Host "unknown verb '$Verb'. Verbs: doctor, list, restore, build, verify, diff, run, build-workshop, build-app, status, tools, tool, emulators, toolchains, play, compat, movie, bisect, mcp, new"; exit 2 }
    }
} catch {
    Write-BdFail $_.Exception.Message
    exit 1
}
