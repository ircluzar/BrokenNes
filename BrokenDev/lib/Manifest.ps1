# BrokenDev project manifests: projects/<id>/project.json. Requires Common.ps1 and Archive.ps1.
#
# A manifest is DATA the verbs interpret, not a script: what the project is, where its source comes from, which tools it
# needs, the build steps and outputs, how to verify it on BrokenNes, and what is known to be wrong with it.

Set-StrictMode -Version Latest

$script:BdKinds    = @('nes', 'snes', 'nested')
$script:BdShells   = @('bash', 'ps5', 'python', 'exe')
$script:BdToolIds  = @('gitbash', 'python', 'node', 'dotnet', 'ps5', '7zip', 'workshop')

function Get-BdProjectsDir {
    <# BROKENDEV_PROJECTS overrides the folder (the tests point it at synthetic manifests). #>
    if ($env:BROKENDEV_PROJECTS) { return $env:BROKENDEV_PROJECTS }
    Join-Path (Get-BdRoot) 'projects'
}

function Get-BdProjectIds {
    $d = Get-BdProjectsDir
    if (-not (Test-Path -LiteralPath $d)) { return @() }
    @(Get-ChildItem -LiteralPath $d -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'project.json') } | Sort-Object Name | ForEach-Object { $_.Name })
}

function Get-BdManifestPath { param([Parameter(Mandatory)][string]$Id) Join-Path (Join-Path (Get-BdProjectsDir) $Id) 'project.json' }

function Test-BdManifest {
    <# Structural validation. Returns an array of error strings (empty = valid). Pure: reads nothing from the disk but the object. #>
    param([Parameter(Mandatory)]$M, [string]$ExpectedId = '', [hashtable]$ArchiveIds = $null)
    $e = New-Object System.Collections.Generic.List[string]
    foreach ($f in 'schema', 'id', 'title', 'family', 'kind', 'console', 'source', 'requires', 'build', 'verify', 'knownIssues') {
        if ($null -eq $M.PSObject.Properties[$f]) { $e.Add("missing field '$f'") }
    }
    if ($e.Count -gt 0) { return $e.ToArray() }
    if ([int]$M.schema -ne 1) { $e.Add("schema must be 1, found $($M.schema)") }
    if ($ExpectedId -and [string]$M.id -ne $ExpectedId) { $e.Add("id '$($M.id)' does not match its folder '$ExpectedId'") }
    if ([string]$M.id -notmatch '^[a-z0-9][a-z0-9\-]*$') { $e.Add("id '$($M.id)' must be lower-case letters, digits and dashes") }
    if ($script:BdKinds -notcontains [string]$M.kind) { $e.Add("kind '$($M.kind)' must be one of: $($script:BdKinds -join ', ')") }
    if (@('nes', 'snes') -notcontains [string]$M.console) { $e.Add("console '$($M.console)' must be nes or snes") }
    # nes -> runs as a NES; snes and nested -> run as a SNES
    if ($M.kind -eq 'nes' -and $M.console -ne 'nes') { $e.Add("kind nes must run on console nes") }
    if ($M.kind -in 'snes', 'nested' -and $M.console -ne 'snes') { $e.Add("kind $($M.kind) must run on console snes") }

    $src = $M.source
    foreach ($f in 'archive', 'path') { if ($null -eq $src.PSObject.Properties[$f] -or -not [string]$src.$f) { $e.Add("source.$f is required") } }
    if ($ArchiveIds -and $src.PSObject.Properties['archive'] -and -not $ArchiveIds.ContainsKey([string]$src.archive)) { $e.Add("source.archive '$($src.archive)' is not in archives.json") }

    foreach ($t in @($M.requires.tools)) { if ($script:BdToolIds -notcontains [string]$t) { $e.Add("requires.tools: unknown tool '$t' (known: $($script:BdToolIds -join ', '))") } }

    $steps = @($M.build.steps)
    if ($steps.Count -eq 0) { $e.Add('build.steps is empty') }
    $names = @{}
    foreach ($s in $steps) {
        $n = [string](Get-BdProp $s 'name' '')
        if (-not $n) { $e.Add('a build step has no name'); continue }
        if ($names.ContainsKey($n)) { $e.Add("duplicate build step name '$n'") }
        $names[$n] = $true
        $sh = [string](Get-BdProp $s 'shell' '')
        if ($script:BdShells -notcontains $sh) { $e.Add("step '$n': shell '$sh' must be one of: $($script:BdShells -join ', ')") }
        if ($sh -eq 'bash' -and -not [string](Get-BdProp $s 'run' '')) { $e.Add("step '$n': a bash step needs 'run'") }
        if ($sh -in 'ps5', 'python', 'exe' -and -not [string](Get-BdProp $s 'file' '')) { $e.Add("step '$n': a $sh step needs 'file'") }
        if (-not (Get-BdProp $s 'cwd' $null)) { $e.Add("step '$n': 'cwd' is required") }
        $to = Get-BdProp $s 'timeoutSec' 0
        if ([int]$to -le 0) { $e.Add("step '$n': timeoutSec must be a positive number (no step may run unattended without a timeout)") }
    }
    $outs = @($M.build.outputs)
    if ($outs.Count -eq 0) { $e.Add('build.outputs is empty') }
    $hasMain = $false
    foreach ($o in $outs) {
        foreach ($f in 'id', 'path', 'publishAs') { if (-not [string](Get-BdProp $o $f '')) { $e.Add("an output lacks '$f'") } }
        if ([string](Get-BdProp $o 'id' '') -eq 'rom') { $hasMain = $true }
    }
    if (-not $hasMain) { $e.Add("build.outputs needs one output with id 'rom' (the ROM BrokenNes runs)") }

    $v = $M.verify
    if ($null -eq $v.PSObject.Properties['scenarios'] -or @($v.scenarios).Count -eq 0) { $e.Add('verify.scenarios is empty') }
    else {
        $ids = @{}
        foreach ($sc in @($v.scenarios)) {
            $sid = [string](Get-BdProp $sc 'id' '')
            if (-not $sid) { $e.Add('a verify scenario has no id'); continue }
            if ($ids.ContainsKey($sid)) { $e.Add("duplicate verify scenario '$sid'") }
            $ids[$sid] = $true
            if ([int](Get-BdProp $sc 'frames' 0) -le 0) { $e.Add("verify scenario '$sid': frames must be positive") }
        }
    }
    # optional: the differential-replay recipe against Mesen
    $oracle = Get-BdProp $M 'oracle' $null
    if ($oracle) {
        $ok = [string](Get-BdProp $oracle 'kind' '')
        if (@('mesen-trace', 'mesen-screens') -notcontains $ok) { $e.Add("oracle.kind '$ok' must be mesen-trace or mesen-screens") }
        if (-not [string](Get-BdProp $oracle 'mesenDir' '')) { $e.Add('oracle.mesenDir is required') }
        if ($null -eq (Get-BdProp $oracle 'frames' $null)) { $e.Add('oracle.frames is required') }
        if ($ok -eq 'mesen-screens' -and -not [string](Get-BdProp $oracle 'mesenSettings' '')) { $e.Add('oracle.mesenSettings is required for mesen-screens') }
        if ($ok -eq 'mesen-trace' -and $M.console -ne 'nes') { $e.Add('a mesen-trace oracle is for NES projects (the trace format is the NES one)') }
    }
    @($e)
}

function Read-BdManifest {
    param([Parameter(Mandatory)][string]$Id, [switch]$NoValidate)
    $path = Get-BdManifestPath $Id
    if (-not (Test-Path -LiteralPath $path)) { throw "unknown project '$Id' (known: $((Get-BdProjectIds) -join ', '))" }
    $m = Read-BdJson -Path $path
    if (-not $NoValidate) {
        $ids = @{}
        foreach ($k in (Get-BdArchiveTable).Keys) { $ids[$k] = $true }
        $errs = @(Test-BdManifest -M $m -ExpectedId $Id -ArchiveIds $ids)
        if ($errs.Count -gt 0) { throw ("manifest $path is invalid:`n  " + ($errs -join "`n  ")) }
    }
    $m
}

function New-BdContext {
    <# Placeholder context for one project: archive roots, the project's output folder, the chosen Python. #>
    param([Parameter(Mandatory)]$Manifest)
    $ctx = @{ Roots = (Get-BdArchiveRoots); Out = (Join-Path (Join-Path (Get-BdHome) 'out') ([string]$Manifest.id)); Id = [string]$Manifest.id }
    $ctx['Work'] = Join-Path (Join-Path (Get-BdHome) 'work') ([string]$Manifest.id)
    $py = Find-BdPython
    if ($py) { $ctx['python'] = $py.Path; $ctx['pythondir'] = Split-Path -Parent $py.Path }
    $ws = Get-BdWorkshopExe
    if ($ws) { $ctx['workshop'] = $ws }
    $ctx
}

function Resolve-BdProjectDeps {
    <# The projects that must be built before $Id (transitively), in build order, ending with $Id itself. #>
    param([Parameter(Mandatory)][string]$Id, [hashtable]$Seen = @{})
    if ($Seen.ContainsKey($Id)) { return @() }
    $Seen[$Id] = $true
    $m = Read-BdManifest -Id $Id
    $order = @()
    foreach ($d in @($m.requires.projects)) { $order += Resolve-BdProjectDeps -Id ([string]$d) -Seen $Seen }
    $order + $Id
}
