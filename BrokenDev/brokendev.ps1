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
    SMB1_ORIG_ROM       the user's own original SMB1 ROM (not needed to build the remake; for regenerating its data; never copied anywhere)

  Exit codes: 0 ok, 1 a check or build failed, 2 usage error, 3 prerequisites missing.
  Works in Windows PowerShell 5.1 and PowerShell 7.

.EXAMPLE
  powershell -NoProfile -File BrokenDev\brokendev.ps1 list
  powershell -NoProfile -File BrokenDev\brokendev.ps1 build vrun-nes
  powershell -NoProfile -File BrokenDev\brokendev.ps1 tools
  powershell -NoProfile -File BrokenDev\brokendev.ps1 tool vrun-tile-editor
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
    # 'tool <id> <args...>': the arguments after the tool id are passed to the tool
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Rest = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'lib\Common.ps1')
. (Join-Path $here 'lib\Tools.ps1')
. (Join-Path $here 'lib\Archive.ps1')
. (Join-Path $here 'lib\Manifest.ps1')
. (Join-Path $here 'lib\Build.ps1')
. (Join-Path $here 'lib\Verify.ps1')
. (Join-Path $here 'lib\Diff.ps1')
. (Join-Path $here 'lib\DevTools.ps1')

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
    $spec = Get-BdArchiveSpec -Id ([string]$m.source.archive) -Table $archives
    $st = Get-BdRestoreState $spec
    $ws = Get-BdWorkspace -Id ([string]$m.source.archive) -Table $archives
    $out = Get-BdOutDir $Id
    $build = $null; $verify = $null
    $bp = Join-Path $out 'build.json'; $vp = Join-Path $out 'verify.json'
    if (Test-Path -LiteralPath $bp) { $build = Read-BdJson $bp }
    if (Test-Path -LiteralPath $vp) { $verify = Read-BdJson $vp }
    [pscustomobject]@{
        Id = $Id; Title = [string]$m.title; Kind = [string]$m.kind
        Workspace = $ws.Kind; WorkspacePath = $ws.Path; Archive = [string]$m.source.archive
        Restored = $st.State
        Built = $(if ($build -and $build.ok) { 'yes' } elseif ($build) { 'FAILED' } else { 'no' })
        Rom = $(if ($build -and $build.ok) { ($build.outputs | Where-Object { $_.id -eq 'rom' } | Select-Object -First 1) } else { $null })
        Verify = $(if ($verify) { $(if ($verify.ok) { 'pass' } else { 'FAIL' }) } else { 'no' })
        VerifyRecord = $verify
    }
}

try {
    if ($Rest.Count -gt 0 -and $Verb.ToLowerInvariant() -ne 'tool') { throw "unexpected argument(s) for '$Verb': $($Rest -join ' ')" }
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
            $fmt = '{0,-30} {1,-13} {2,-27} {3}'
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
                foreach ($pid0 in (Resolve-BdProjectDeps -Id $Target)) { $archiveIds += [string](Read-BdManifest -Id $pid0).source.archive }
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
        default { Write-Host "unknown verb '$Verb'. Verbs: doctor, list, restore, build, verify, diff, run, build-workshop, build-app, status, tools, tool"; exit 2 }
    }
} catch {
    Write-BdFail $_.Exception.Message
    exit 1
}
