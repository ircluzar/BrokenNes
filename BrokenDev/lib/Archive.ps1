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

function Get-BdArchiveRoots {
    <# name -> absolute folder, for every root of every archive (used by Expand-BdPath {root:name}). #>
    param($Table = (Get-BdArchiveTable))
    $roots = @{}
    foreach ($id in $Table.Keys) {
        $spec = $Table[$id]
        $target = Get-BdArchiveTarget $spec
        foreach ($r in $spec.roots.PSObject.Properties) {
            $rel = ([string]$r.Value) -replace '/', '\'
            $full = if ($rel -eq '.') { $target } else { Join-Path $target $rel }
            $roots[$r.Name] = $full
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
