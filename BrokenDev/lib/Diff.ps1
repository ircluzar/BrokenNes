# BrokenDev differential replay against Mesen (a black-box oracle): runs tools\diff-nes.ps1 or tools\diff-snes.ps1 with the parameters of a
# manifest's "oracle" section and records out\<id>\diff.json. Requires Common, Tools, Archive, Manifest, Build, Verify.

Set-StrictMode -Version Latest

function Get-BdTraceLua {
    <#
      The Mesen tracer the NES diff runs: BROKENDEV_TRACE_LUA when set, else BrokenDev's own copy of the user's tracer
      (tools\vrun\mesen\state_trace.lua, brought over from the older sys0 clone where it was the only copy).
    #>
    if ($env:BROKENDEV_TRACE_LUA) { return $env:BROKENDEV_TRACE_LUA }
    Join-Path (Get-BdRoot) 'tools\vrun\mesen\state_trace.lua'
}

function Invoke-BdProjectDiff {
    <#
      Returns a record { project; kind; status = pass | fail | skipped; reason; summary }. 'skipped' always says why (a missing external tool is never a silent pass).
      Throws only on a broken setup (project not built, Workshop not built, no oracle section).
    #>
    param([Parameter(Mandatory)][string]$Id)
    $m = Read-BdManifest -Id $Id
    $oracle = Get-BdProp $m 'oracle' $null
    if (-not $oracle) { throw "project $Id has no 'oracle' section in its manifest" }
    $ctx = New-BdContext $m
    $out = [string]$ctx['Out']
    $bp = Join-Path $out 'build.json'
    if (-not (Test-Path -LiteralPath $bp)) { throw "project $Id has not been built. Run: brokendev.ps1 build $Id" }
    $build = Read-BdJson $bp
    $rom = $build.outputs | Where-Object { $_.id -eq 'rom' } | Select-Object -First 1
    if (-not $rom -or -not (Test-Path -LiteralPath $rom.published)) { throw "the built ROM is missing for $Id" }
    $ws = Get-BdWorkshopExe
    if (-not $ws) { throw 'BrokenNes Workshop is not built. Run: brokendev.ps1 build-workshop' }
    $kind = [string]$oracle.kind
    $work = Join-Path (Join-Path (Join-Path (Get-BdHome) 'work') 'diff') $Id
    $record = [ordered]@{ project = $Id; kind = $kind; status = 'fail'; reason = ''; romSha256 = [string]$rom.sha256; ranAtUtc = (Get-Date).ToUniversalTime().ToString('o'); summary = $null }
    $mesenDir = Expand-BdPath -Text ([string]$oracle.mesenDir) -Context $ctx
    $tools = Join-Path (Get-BdRoot) 'tools'

    if ($kind -eq 'mesen-trace') {
        $lua = Get-BdTraceLua
        $record['traceLua'] = $lua
        if (-not $lua -or -not (Test-Path -LiteralPath $lua)) {
            $record.status = 'skipped'
            $record.reason = "the Mesen tracer state_trace.lua was not found (BROKENDEV_TRACE_LUA='$env:BROKENDEV_TRACE_LUA', BrokenDev's copy $(Join-Path (Get-BdRoot) 'tools\vrun\mesen\state_trace.lua'))"
        } elseif (-not (Test-Path -LiteralPath (Join-Path $mesenDir 'Mesen.exe'))) {
            $record.status = 'skipped'; $record.reason = "Mesen not found at $mesenDir (is the project restored?)"
        } else {
            $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $tools 'diff-nes.ps1'), '-Rom', [string]$rom.published, '-Work', $work, '-MesenDir', $mesenDir, '-Workshop', $ws,
                   '-TraceLua', $lua, '-Frames', [string]$oracle.frames, '-RamFill', [string](Get-BdProp $oracle 'ramFill' 'fceux'), '-Tag', 'diff')
            if (Get-BdProp $oracle 'input' $null) { $a += @('-InputScript', [string]$oracle.input) }
            $psx = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
            $r = Invoke-BdProcess -FilePath $psx -Arguments $a -TimeoutSec 1800 -LogFile (Join-Path $out 'logs\diff.log')
            $sumPath = Join-Path $work 'diff.summary.json'
            if (Test-Path -LiteralPath $sumPath) { $record.summary = Read-BdJson $sumPath }
            if ($r.ExitCode -eq 0) { $record.status = 'pass' } elseif ($r.ExitCode -eq 2) { $record.status = 'skipped'; $record.reason = "a tool failed: $(Select-BdTail $r.Output 6)" } else { $record.status = 'fail'; $record.reason = (Select-BdTail $r.Output 4) }
        }
    }
    elseif ($kind -eq 'mesen-screens') {
        $settings = Expand-BdPath -Text ([string]$oracle.mesenSettings) -Context $ctx
        if (-not $ctx.ContainsKey('python')) { throw 'no suitable Python (3.10+ with pillow and numpy)' }
        if (-not (Test-Path -LiteralPath (Join-Path $mesenDir 'Mesen.exe')) -or -not (Test-Path -LiteralPath $settings)) {
            $record.status = 'skipped'; $record.reason = "Mesen ($mesenDir) or its settings ($settings) not found: restore the supervrun_dev archive"
        } else {
            $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $tools 'diff-snes.ps1'), '-Rom', [string]$rom.published, '-Work', $work, '-MesenDir', $mesenDir, '-MesenSettings', $settings,
                   '-Workshop', $ws, '-Python', [string]$ctx['python'], '-Frames', (@($oracle.frames) -join ','), '-Tag', 'snes',
                   '-InputShift', [string](Get-BdProp $oracle 'inputShift' 2), '-MaxPixels', [string](Get-BdProp $oracle 'maxPixels' 0), '-Tolerance', [string](Get-BdProp $oracle 'tolerance' 1))
            if (Get-BdProp $oracle 'input' $null) { $a += @('-InputScript', [string]$oracle.input) }
            $crop = @(Get-BdProp $oracle 'crop' @())
            if ($crop.Count -eq 2) { $a += @('-CropTop', [string]$crop[0], '-CropBottom', [string]$crop[1]) }
            $win = @(Get-BdProp $oracle 'window' @(1, 4))
            $a += @('-WindowBefore', [string]$win[0], '-WindowAfter', [string]$win[1])
            $psx = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
            $r = Invoke-BdProcess -FilePath $psx -Arguments $a -TimeoutSec 1800 -LogFile (Join-Path $out 'logs\diff.log')
            $sumPath = Join-Path $work 'snes.imgdiff.json'
            if (Test-Path -LiteralPath $sumPath) { $record.summary = Read-BdJson $sumPath }
            if ($r.ExitCode -eq 0) { $record.status = 'pass' } elseif ($r.ExitCode -eq 2) { $record.status = 'skipped'; $record.reason = "a tool failed: $(Select-BdTail $r.Output 6)" } else { $record.status = 'fail'; $record.reason = (Select-BdTail $r.Output 4) }
        }
    }
    else { throw "unknown oracle kind '$kind'" }

    Save-BdJson -Object $record -Path (Join-Path $out 'diff.json') -Depth 12
    [pscustomobject]$record
}
