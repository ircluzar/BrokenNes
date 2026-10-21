# BrokenDev archive restore: archives.json -> folders under BROKENDEV_HOME, with a marker file and sentinel checks.
# Requires Common.ps1 and Tools.ps1 to be dot-sourced first.

Set-StrictMode -Version Latest

$script:BdMarkerName = '.brokendev-restore.json'

function Get-BdArchiveTable {
    # BROKENDEV_ARCHIVES_JSON overrides the table (the tests restore synthetic archives).
    param([string]$Path = $(if ($env:BROKENDEV_ARCHIVES_JSON) { $env:BROKENDEV_ARCHIVES_JSON } else { Join-Path (Get-BdRoot) 'archives.json' }))
    $j = Read-BdJson -Path $Path
    $t = [ordered]@{}
    foreach ($p in $j.archives.PSObject.Properties) { $t[$p.Name] = $p.Value }
    $errs = @(Test-BdArchiveLive -Table $t)
    if ($errs.Count -gt 0) { throw ("archive table $Path is invalid:`n  " + ($errs -join "`n  ")) }
    $t
}

function Get-BdArchiveSpec {
    param([Parameter(Mandatory)][string]$Id, $Table = (Get-BdArchiveTable))
    if (-not $Table.Contains($Id)) { throw "unknown archive '$Id' (known: $($Table.Keys -join ', '))" }
    $Table[$Id]
}

function Get-BdArchiveFile {
    param([Parameter(Mandatory)]$Spec)
    Join-Path (Get-BdArchivesDir) ([string]$Spec.file)
}

function Get-BdArchiveTarget {
    param([Parameter(Mandatory)]$Spec)
    Join-Path (Get-BdHome) ([string]$Spec.extractTo)
}

# ---------------------------------------------------------------------------------------------------- live workspaces
#
# The restored copies under BROKENDEV_HOME are snapshots of the archives. The user develops in his LIVE workspaces (VRUN_Nes_Dev,
# SuperVRUN_dev beside the BrokenNes repository), and the tools must edit those. An archive's 'live' section says how to find its live
# workspace and where each root is inside it; the restored copy stays the fallback for a machine that has no live workspace.

function Get-BdLiveEnvName {
    <# BROKENDEV_LIVE_<ARCHIVE ID IN CAPITALS>: a folder (the live workspace to use) or 'off' (use the restored copy). #>
    param([Parameter(Mandatory)][string]$Id)
    'BROKENDEV_LIVE_' + ($Id.ToUpperInvariant() -replace '[^A-Z0-9]', '_')
}

function Join-BdRel {
    param([Parameter(Mandatory)][string]$Base, [string]$Rel)
    $r = ([string]$Rel) -replace '/', '\'
    if (-not $r -or $r -eq '.') { return $Base }
    Join-Path $Base $r
}

function Test-BdArchiveLive {
    <# Structural validation of the archives' 'live' sections. Returns an array of error strings (empty = valid). #>
    param($Table = (Get-BdArchiveTable))
    $e = New-Object System.Collections.Generic.List[string]
    foreach ($id in $Table.Keys) {
        $spec = $Table[$id]
        $live = Get-BdProp $spec 'live' $null
        if (-not $live) { continue }
        if (-not [string](Get-BdProp $live 'folder' '')) { $e.Add("archive ${id}: live.folder is required") }
        if (@(Get-BdProp $live 'sentinels' @()).Count -eq 0) { $e.Add("archive ${id}: live.sentinels is empty (a folder that merely has the right name must not pass for the workspace)") }
        $lr = Get-BdProp $live 'roots' $null
        if (-not $lr -or @($lr.PSObject.Properties).Count -eq 0) { $e.Add("archive ${id}: live.roots is empty") }
        else { foreach ($p in $lr.PSObject.Properties) { if ($null -eq $spec.roots.PSObject.Properties[$p.Name]) { $e.Add("archive ${id}: live root '$($p.Name)' is not one of the archive's roots ($((@($spec.roots.PSObject.Properties | ForEach-Object { $_.Name })) -join ', '))") } } }
    }
    @($e)
}

function Get-BdLiveMissing {
    <# The live sentinels (archive spec 'live.sentinels') that $Folder lacks. Empty = the folder is a valid live workspace. #>
    param([Parameter(Mandatory)]$Spec, [Parameter(Mandatory)][string]$Folder)
    $live = Get-BdProp $Spec 'live' $null
    $missing = @()
    foreach ($s in @(Get-BdProp $live 'sentinels' @())) { if (-not (Test-Path -LiteralPath (Join-BdRel $Folder ([string]$s)))) { $missing += [string]$s } }
    $missing
}

function Find-BdLiveFolder {
    <#
      The live workspace of one archive: a folder named live.folder beside $SearchFrom (default: the repository) or beside one of its
      parent folders, that has every live sentinel. A git worktree under BrokenNes\.claude\worktrees therefore finds the same workspace
      as the main checkout. $null when there is none (a new machine: the restored copy is used).
    #>
    param([Parameter(Mandatory)]$Spec, [string]$SearchFrom = (Get-BdRepoRoot))
    $live = Get-BdProp $Spec 'live' $null
    if (-not $live) { return $null }
    $name = [string](Get-BdProp $live 'folder' '')
    if (-not $name) { return $null }
    $dir = Split-Path -Parent ([System.IO.Path]::GetFullPath($SearchFrom).TrimEnd('\'))
    while ($dir) {
        $cand = Join-Path $dir $name
        if ((Test-Path -LiteralPath $cand -PathType Container) -and @(Get-BdLiveMissing -Spec $Spec -Folder $cand).Count -eq 0) { return $cand }
        $parent = Split-Path -Parent $dir
        if (-not $parent -or $parent -eq $dir) { break }
        $dir = $parent
    }
    $null
}

function Get-BdWorkspace {
    <#
      Which copy of an archive's projects BrokenDev works on. Returns { Id; Kind = live | restored | partial | absent; Path; Source; Detail }.
        1. BROKENDEV_LIVE_<ID> = a folder: that folder, which must have the live sentinels (an explicit choice is never silently replaced);
           BROKENDEV_LIVE_<ID> = off: the restored copy.
        2. a live workspace found beside the repository (Find-BdLiveFolder).
        3. the restored copy under BROKENDEV_HOME (its restore state: restored, partial or absent).
    #>
    param([Parameter(Mandatory)][string]$Id, $Table = (Get-BdArchiveTable), [string]$SearchFrom = (Get-BdRepoRoot))
    $spec = Get-BdArchiveSpec -Id $Id -Table $Table
    $envName = Get-BdLiveEnvName $Id
    $ev = [Environment]::GetEnvironmentVariable($envName)
    $live = Get-BdProp $spec 'live' $null
    $off = ($ev -and @('off', 'restored', '0', 'no') -contains $ev.Trim().ToLowerInvariant())
    if ($ev -and -not $off) {
        if (-not $live) { throw "$envName is set, but archive '$Id' has no 'live' layout in archives.json" }
        $full = [System.IO.Path]::GetFullPath($ev).TrimEnd('\')
        if (-not (Test-Path -LiteralPath $full -PathType Container)) { throw "$envName points at $full, which does not exist" }
        $miss = @(Get-BdLiveMissing -Spec $spec -Folder $full)
        if ($miss.Count -gt 0) { throw "$envName points at $full, which is not a live workspace of '$Id' (missing: $($miss -join ', '))" }
        return [pscustomobject]@{ Id = $Id; Kind = 'live'; Path = $full; Source = $envName; Detail = "live workspace from $envName" }
    }
    if (-not $off) {
        $found = Find-BdLiveFolder -Spec $spec -SearchFrom $SearchFrom
        if ($found) { return [pscustomobject]@{ Id = $Id; Kind = 'live'; Path = $found; Source = 'beside the repository'; Detail = "live workspace (set $envName=off to use the restored copy)" } }
    }
    $st = Get-BdRestoreState $spec
    $why = if ($off) { "$envName=off" } elseif ($live) { "no live workspace '$([string]$live.folder)' found beside the repository" } else { 'this archive has no live layout' }
    [pscustomobject]@{ Id = $Id; Kind = [string]$st.State; Path = (Get-BdArchiveTarget $spec); Source = 'restored copy'; Detail = "$why; restored copy: $($st.State) ($($st.Detail))" }
}

function Get-BdArchiveRoots {
    <#
      name -> absolute folder, for every root of every archive (used by Expand-BdPath {root:name}). A root comes from the archive's live
      workspace when there is one and its 'live.roots' names that root, else from the restored copy. -Detailed returns name -> object
      { Path; From = live | restored; Archive } instead, for 'list' and the build record.
    #>
    param($Table = (Get-BdArchiveTable), [switch]$Detailed)
    $roots = @{}
    foreach ($id in $Table.Keys) {
        $spec = $Table[$id]
        $target = Get-BdArchiveTarget $spec
        $ws = Get-BdWorkspace -Id $id -Table $Table
        $liveRoots = $null
        if ($ws.Kind -eq 'live') { $liveRoots = Get-BdProp (Get-BdProp $spec 'live' $null) 'roots' $null }
        foreach ($r in $spec.roots.PSObject.Properties) {
            $liveRel = if ($liveRoots) { Get-BdProp $liveRoots $r.Name $null } else { $null }
            if ($null -ne $liveRel) { $full = Join-BdRel $ws.Path ([string]$liveRel); $from = 'live' }
            else { $full = Join-BdRel $target ([string]$r.Value); $from = 'restored' }
            if ($Detailed) { $roots[$r.Name] = [pscustomobject]@{ Path = $full; From = $from; Archive = $id } } else { $roots[$r.Name] = $full }
        }
    }
    $roots
}

function Get-BdRestoreState {
    <# 'restored' | 'partial' | 'absent' for one archive, with the reason. Never touches the disk beyond reading. #>
    param([Parameter(Mandatory)]$Spec)
    $target = Get-BdArchiveTarget $Spec
    $markerPath = Join-Path $target $script:BdMarkerName
    if (-not (Test-Path -LiteralPath $target)) { return [pscustomobject]@{ State = 'absent'; Detail = "$target does not exist"; Missing = @() } }
    $missing = @()
    foreach ($s in $Spec.sentinels) {
        $p = Join-Path $target (([string]$s) -replace '/', '\')
        if (-not (Test-Path -LiteralPath $p)) { $missing += [string]$s }
    }
    if (-not (Test-Path -LiteralPath $markerPath)) { return [pscustomobject]@{ State = 'partial'; Detail = 'no restore marker (extracted by hand, or an interrupted restore)'; Missing = $missing } }
    if ($missing.Count -gt 0) { return [pscustomobject]@{ State = 'partial'; Detail = "marker present but $($missing.Count) sentinel file(s) missing"; Missing = $missing } }
    [pscustomobject]@{ State = 'restored'; Detail = 'marker and all sentinels present'; Missing = @() }
}

function Restore-BdArchive {
    <#
      Extracts one archive into BROKENDEV_HOME\<extractTo> with 7-Zip.
        -Full   ignores the spec's exclude list (restores everything) and, for specs with an include list, extracts all of it.
        -Force  extracts again even if a valid marker exists.
      The extraction goes to a staging folder first and is moved into place only when 7-Zip reported success, so an
      interrupted run never leaves a half-filled target that looks restored. Returns the final state object.
    #>
    param([Parameter(Mandatory)][string]$Id, [switch]$Full, [switch]$Force, $Table = (Get-BdArchiveTable))
    $spec = Get-BdArchiveSpec -Id $Id -Table $Table
    $zip = Get-BdArchiveFile $spec
    $target = Get-BdArchiveTarget $spec
    $markerPath = Join-Path $target $script:BdMarkerName

    if (-not (Test-Path -LiteralPath $zip)) { throw "archive not found: $zip (set BROKENDEV_ARCHIVES to the folder that holds it)" }
    $zipItem = Get-Item -LiteralPath $zip
    $sevenZip = Find-Bd7Zip
    if (-not $sevenZip) { throw '7-Zip not found (7z.exe). Install it: winget install 7zip.7zip' }

    $wantInclude = @(); $wantExclude = @()
    if (-not $Full) { $wantInclude = @($spec.include); $wantExclude = @($spec.exclude) }

    $state = Get-BdRestoreState $spec
    if ($state.State -eq 'restored' -and -not $Force) {
        $m = Read-BdJson -Path $markerPath
        $same = ([int64]$m.archiveBytes -eq $zipItem.Length) -and ((@($m.include) -join '|') -eq ($wantInclude -join '|')) -and ((@($m.exclude) -join '|') -eq ($wantExclude -join '|'))
        if ($same) { Write-BdInfo "restore $Id : already restored ($target)"; return $state }
    }

    $home0 = Get-BdHome
    $staging = Join-Path $home0 ".staging\$Id"
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    New-Item -ItemType Directory -Force -Path $target | Out-Null

    $top = [string]$spec.stripTop
    $prefix = if ($top) { $top + '\' } else { '' }
    $zargs = @('x', $zip, "-o$staging", '-y', '-bso0', '-bsp0')
    foreach ($e in $wantExclude) { $zargs += ('-x!' + $prefix + ($e -replace '^\\+', '')) }
    # Include patterns come last: 7-Zip treats trailing bare names as the files to extract.
    foreach ($i in $wantInclude) { $zargs += ($prefix + $i) }
    # An excluded pattern for a stripped archive is already prefixed above; for an unstripped archive the spec holds full archive paths.

    Write-BdInfo "restore $Id : extracting $([math]::Round($zipItem.Length / 1MB)) MB archive into $target ..."
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $r = Invoke-BdProcess -FilePath $sevenZip -Arguments $zargs -TimeoutSec 7200 -LogFile (Join-Path $home0 "logs\restore-$Id.log")
    if ($r.TimedOut -or $r.ExitCode -ne 0) {
        throw "7-Zip failed for $Id (exit $($r.ExitCode)$(if ($r.TimedOut) { ', timed out' })). Log: $($r.LogFile). Staging left at $staging for inspection."
    }

    $src = if ($top) { Join-Path $staging $top } else { $staging }
    if (-not (Test-Path -LiteralPath $src)) { throw "after extraction the expected folder is missing: $src" }
    $moved = 0
    foreach ($child in (Get-ChildItem -LiteralPath $src -Force)) {
        $dest = Join-Path $target $child.Name
        if (Test-Path -LiteralPath $dest) {
            # Merge into an existing tree (a -Force or -Full re-run): overwrite file by file, never delete the target.
            # robocopy /MOVE is far faster than Copy-Item over a network share, and its exit codes below 8 mean success.
            if ($child.PSIsContainer) {
                $rc = Invoke-BdProcess -FilePath (Join-Path $env:SystemRoot 'System32\robocopy.exe') -Arguments @($child.FullName, $dest, '/E', '/MOVE', '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/R:2', '/W:2') -TimeoutSec 3600
                if ($rc.ExitCode -ge 8) { throw "robocopy could not merge $($child.FullName) into $dest (exit $($rc.ExitCode))`n$(Select-BdTail $rc.Output 10)" }
                if (Test-Path -LiteralPath $child.FullName) { Remove-Item -LiteralPath $child.FullName -Recurse -Force }
            } else {
                Move-Item -LiteralPath $child.FullName -Destination $dest -Force
            }
        } else {
            Move-Item -LiteralPath $child.FullName -Destination $dest
        }
        $moved++
    }
    Remove-Item -LiteralPath $staging -Recurse -Force

    $marker = [ordered]@{
        archive = $Id; file = [string]$spec.file; archiveBytes = $zipItem.Length
        archiveWriteTimeUtc = $zipItem.LastWriteTimeUtc.ToString('o')
        include = $wantInclude; exclude = $wantExclude; full = [bool]$Full
        restoredAt = (Get-Date).ToUniversalTime().ToString('o'); seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
        topLevelEntries = $moved; sevenZip = $sevenZip
    }
    Save-BdJson -Object $marker -Path $markerPath
    $state = Get-BdRestoreState $spec
    if ($state.State -ne 'restored') { throw "restore of $Id finished but the result is '$($state.State)': $($state.Detail); missing: $($state.Missing -join ', ')" }
    Write-BdOk "restore $Id : done in $([math]::Round($sw.Elapsed.TotalSeconds)) s"
    $state
}
