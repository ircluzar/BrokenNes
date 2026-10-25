# BrokenDev project manifests: projects/<id>/project.json. Requires Common.ps1 and Archive.ps1.
#
# A manifest is DATA the verbs interpret, not a script: what the project is, where its source comes from, which tools it
# needs, the build steps and outputs, how to verify it on BrokenNes, and what is known to be wrong with it.

Set-StrictMode -Version Latest

$script:BdKinds    = @('nes', 'snes', 'nested', 'wms', 'gb')
$script:BdShells   = @('bash', 'ps5', 'python', 'exe')
# and 'toolchain:<id>': a pinned toolchain of toolchains.json (lib\Toolchains.ps1), which must be there and unchanged
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
    if (@('nes', 'snes', 'wms', 'gb', 'gbc') -notcontains [string]$M.console) { $e.Add("console '$($M.console)' must be nes, snes, wms, gb or gbc") }
    # nes -> runs as a NES; snes and nested -> run as a SNES; wms (a WideMess game, BrokenDev/widemess) -> runs on the WideMess console;
    # gb (a Game Boy game) -> runs on the Game Boy (console gb) or the Game Boy Color (console gbc: a dual-mode or Color game)
    if ($M.kind -eq 'nes' -and $M.console -ne 'nes') { $e.Add("kind nes must run on console nes") }
    if ($M.kind -in 'snes', 'nested' -and $M.console -ne 'snes') { $e.Add("kind $($M.kind) must run on console snes") }
    if (($M.kind -eq 'wms') -ne ($M.console -eq 'wms')) { $e.Add("kind wms and console wms go together (a WideMess game runs on the WideMess console only)") }
    if (($M.kind -eq 'gb') -ne ($M.console -in 'gb', 'gbc')) { $e.Add("kind gb and console gb or gbc go together (a Game Boy game runs on the Game Boy or the Game Boy Color)") }

    # the source: an archive and a path in it (the user's games), or a folder ({repo}, {home}, {env:...} or an absolute path: a WideMess
    # project made by 'brokendev new widemess', or the devkit's sample game in the repository)
    $src = $M.source
    $hasFolder = $null -ne $src.PSObject.Properties['folder']
    if ($hasFolder) {
        if (-not [string]$src.folder) { $e.Add('source.folder is empty') }
        if ($src.PSObject.Properties['archive']) { $e.Add('source has both a folder and an archive: name one') }
    } else {
        foreach ($f in 'archive', 'path') { if ($null -eq $src.PSObject.Properties[$f] -or -not [string]$src.$f) { $e.Add("source.$f is required (or source.folder)") } }
    }
    if ($ArchiveIds -and $src.PSObject.Properties['archive'] -and -not $ArchiveIds.ContainsKey([string]$src.archive)) { $e.Add("source.archive '$($src.archive)' is not in archives.json") }

    foreach ($t in @($M.requires.tools)) { if ($script:BdToolIds -notcontains [string]$t -and [string]$t -notmatch '^toolchain:[a-z0-9][a-z0-9\-]*$') { $e.Add("requires.tools: unknown tool '$t' (known: $($script:BdToolIds -join ', '), toolchain:<id>)") } }

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
    # optional: the hardware checks and the budgets (milestone D4; the Workshop's verify checks the rule ids against its catalogue)
    $checks = Get-BdProp $M 'checks' $null
    if ($null -ne $checks) {
        $vids = @(@(Get-BdProp $v 'scenarios' @()) | ForEach-Object { [string](Get-BdProp $_ 'id' '') })
        foreach ($s in @(Get-BdProp $checks 'scenarios' @())) { if ($vids -notcontains [string]$s) { $e.Add("checks.scenarios: no verify scenario '$s'") } }
        foreach ($a in @(Get-BdProp $checks 'accepted' @())) {
            $r = [string](Get-BdProp $a 'rule' '')
            if (-not $r) { $e.Add('checks.accepted: an entry has no rule') }
            if ($r -and $r -notmatch '^(nes|snes|gb)\.[a-z0-9*?-]+$') { $e.Add("checks.accepted: '$r' is not a rule id (nes.*, snes.*, gb.*)") }
            if (-not [string](Get-BdProp $a 'reason' '')) { $e.Add("checks.accepted: '$r' at '$([string](Get-BdProp $a 'at' '*'))' has no reason (an accepted finding always says why)") }
        }
        foreach ($o in @(Get-BdProp $checks 'off' @())) {
            if (-not [string](Get-BdProp $o 'rule' '')) { $e.Add('checks.off: an entry has no rule') }
            if (-not [string](Get-BdProp $o 'reason' '')) { $e.Add("checks.off: '$([string](Get-BdProp $o 'rule' ''))' has no reason (an opt-out always says why)") }
        }
    }
    $budgets = Get-BdProp $M 'budgets' $null
    if ($null -ne $budgets) {
        foreach ($p in $budgets.PSObject.Properties) {
            switch ($p.Name) {
                { $_ -in 'lagFrames', 'busyPercent' } { if ($null -eq (Get-BdProp $p.Value 'max' $null)) { $e.Add("budgets.$($p.Name) needs 'max'") } }
                'stackFree' { if ($null -eq (Get-BdProp $p.Value 'min' $null)) { $e.Add("budgets.stackFree needs 'min'") } }
                'romBankFree' { if ($null -eq (Get-BdProp $p.Value 'min' $null) -and $null -eq (Get-BdProp $p.Value 'banks' $null)) { $e.Add("budgets.romBankFree needs 'min' or 'banks'") } }
                'memoryFree' { }
                'what' { }
                default { $e.Add("budgets: unknown budget '$($p.Name)' (lagFrames, busyPercent, romBankFree, memoryFree, stackFree)") }
            }
        }
    }
    # optional: the multi-emulator compatibility recipe (lib\Compat.ps1 interprets it; validated when that library is loaded)
    $compat = Get-BdProp $M 'compat' $null
    if ($compat -and (Get-Command Test-BdCompatSection -ErrorAction SilentlyContinue)) {
        $vids = @(@(Get-BdProp $v 'scenarios' @()) | ForEach-Object { [string](Get-BdProp $_ 'id' '') })
        foreach ($x in @(Test-BdCompatSection -Compat $compat -VerifyIds $vids)) { $e.Add($x) }
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

function Get-BdProjectWorkspace {
    <#
      Where a project's source is: its archive's workspace (Get-BdWorkspace: live, restored, partial, absent), or, for a project with
      source.folder, that folder (Kind folder, or absent when it does not exist). The same shape either way: { Id; Kind; Path; Source; Detail }.
    #>
    param([Parameter(Mandatory)]$Manifest, $Table = $null)
    $src = $Manifest.source
    if ($src.PSObject.Properties['folder']) {
        # {live:<name>}: the game's live workspace, found beside the repository (Find-BdLiveWorkspace); 'absent' when there is none
        if ([string]$src.folder -match '^\{live:([A-Za-z0-9_\-\.]+)\}') {
            $name = $Matches[1]
            $found = Find-BdLiveWorkspace -Name $name
            if (-not $found) { return [pscustomobject]@{ Id = [string]$Manifest.id; Kind = 'absent'; Path = ''; Source = 'source.folder {live:}'; Detail = "no live workspace '$name' beside the repository (or set $(Get-BdLiveWorkspaceEnv $name))" } }
            $p = [System.IO.Path]::GetFullPath((Expand-BdPath -Text ([string]$src.folder)))
            $how = if ([Environment]::GetEnvironmentVariable((Get-BdLiveWorkspaceEnv $name))) { Get-BdLiveWorkspaceEnv $name } else { 'beside the repository' }
            return [pscustomobject]@{ Id = [string]$Manifest.id; Kind = 'live'; Path = $p; Source = $how; Detail = "the game's live workspace ($how)" }
        }
        $p = [System.IO.Path]::GetFullPath((Expand-BdPath -Text ([string]$src.folder)))
        $ok = Test-Path -LiteralPath $p -PathType Container
        return [pscustomobject]@{ Id = [string]$Manifest.id; Kind = $(if ($ok) { 'folder' } else { 'absent' }); Path = $p; Source = 'source.folder'
                                  Detail = $(if ($ok) { 'the project''s own folder' } else { "source.folder $p does not exist" }) }
    }
    if ($Table) { return Get-BdWorkspace -Id ([string]$src.archive) -Table $Table }
    Get-BdWorkspace -Id ([string]$src.archive)
}

function New-BdContext {
    <# Placeholder context for one project: archive roots, the project's output folder, the chosen Python; {src} = source.folder when it has one. #>
    param([Parameter(Mandatory)]$Manifest)
    $ctx = @{ Roots = (Get-BdArchiveRoots); Out = (Join-Path (Join-Path (Get-BdHome) 'out') ([string]$Manifest.id)); Id = [string]$Manifest.id }
    if ($Manifest.source.PSObject.Properties['folder']) {
        # a {live:} workspace that is not there leaves {src} unset (the build says so; list and validation still work)
        try { $ctx['src'] = [System.IO.Path]::GetFullPath((Expand-BdPath -Text ([string]$Manifest.source.folder))) } catch { if ([string]$Manifest.source.folder -notmatch '^\{live:') { throw } }
    }
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
