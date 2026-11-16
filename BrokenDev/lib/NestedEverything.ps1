# Nested Everything: pipelines that turn a game into a ROM for a console it was not made for (docs/projects/nested-everything-research.md).
# Each pipeline is a folder projects/nested-<id> with a pipeline.json manifest (name, source / target console, settings with defaults,
# the grades) that the BrokenDev UI lists; its tool lives in tools/nested-everything. The NES -> SNES Nested ('brokendev.ps1 nested')
# is the older member of the family and keeps its own command.

function Get-BdNestedEverythingPipelines {
    $root = Get-BdProjectsDir
    @(Get-ChildItem -Path $root -Directory -Filter 'nested-*' | ForEach-Object {
        $m = Join-Path $_.FullName 'pipeline.json'
        if (Test-Path $m) { $p = Read-BdJson -Path $m; $p | Add-Member -NotePropertyName folder -NotePropertyValue $_.FullName -Force; $p }
    })
}

function Resolve-BdNestedEverythingProfile([object]$Pipeline, [string]$Profile) {
    if (-not $Profile -or $Profile -eq 'vrun') { return '' }     # 'vrun' is the nested command's default, not a profile here
    if (Test-Path $Profile) { return (Resolve-Path $Profile).Path }
    $p = Join-Path (Join-Path $Pipeline.folder 'profiles') ($Profile + '.json')
    if (Test-Path $p) { return $p }
    throw "nested-everything: no profile '$Profile' (a file, or a game id under $($Pipeline.folder)\profiles)"
}

function Invoke-BdNestedEverything([string]$Id, [string[]]$Rest, [string]$Profile, [string]$Feedback, [string[]]$Set, [switch]$Json) {
    $pipes = Get-BdNestedEverythingPipelines
    if (-not $Id -or $Id -eq 'list') {
        if ($Json) { return ($pipes | Select-Object id, name, source, target, settings, grades, cli | ConvertTo-Json -Depth 6) }
        foreach ($p in $pipes) {
            Write-BdInfo ('{0,-8} {1}  ({2} -> {3})' -f $p.id, $p.name, $p.source.console, $p.target.console)
            foreach ($s in $p.settings) { Write-BdInfo ('    {0,-22} = {1,-8} {2}' -f $s.id, $s.default, ((Get-BdProp $s 'choices' @()) -join '|')) }
        }
        return
    }
    $pipe = $pipes | Where-Object { $_.id -eq $Id } | Select-Object -First 1
    if (-not $pipe) { throw "nested-everything: no pipeline '$Id' (have: $(($pipes | ForEach-Object id) -join ', '))" }
    if ($Rest.Count -ne 2) { throw "nested-everything $Id needs <in> <out>: $($pipe.cli)" }
    $py = Find-BdPython
    if (-not $py) { throw 'nested-everything: no Python 3.10+ found' }
    $tool = Join-Path (Split-Path -Parent (Get-BdProjectsDir)) ($pipe.tool -replace '^BrokenDev[\/]', '')
    $prof = Resolve-BdNestedEverythingProfile $pipe $Profile
    $mode = if ($Feedback -eq 'brokennes') { 'feedback' } else { 'convert' }
    $args = @($tool, $mode, (Resolve-Path $Rest[0]).Path, [IO.Path]::GetFullPath($Rest[1]))
    if ($prof) { $args += @('--profile', $prof) }
    foreach ($kv in @($Set | ForEach-Object { ([string]$_) -split ',' } | Where-Object { $_ })) { $args += @('--set', $kv.Trim()) }
    $report = [IO.Path]::ChangeExtension([IO.Path]::GetFullPath($Rest[1]), '.report.json')
    $args += @('--report', $report)
    & $py.Path @args | Out-Host
    $code = $LASTEXITCODE
    [pscustomobject]@{ pipeline = $Id; mode = $mode; out = $Rest[1]; report = $report; exitCode = $code }
}
