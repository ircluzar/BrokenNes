# D1 (Timeflow T6, the Sega VRUN ports' cross-console comparison): brokendev.ps1 timeflow play-compare <port project>.
# The port manifest's comparison.play section names a fixed-seed test build of each side and a pad script that really plays; both are
# built into BROKENNES_WORK (C:\BrokenNes-work)\play-compare\<port>\<side> (never a project's own build folder: the shipping ROMs are not touched), the
# original is recorded on the script and compared with the port by Workshop --timeflow compare (the comparison section's mapping),
# then the 'plays' check: each side reached comparison.play.expect's event counts (rooms, hits, cash...).

function Get-BdBash {
    foreach ($c in @("$env:ProgramFiles\Git\bin\bash.exe", "$env:ProgramFiles\Git\usr\bin\bash.exe")) { if (Test-Path -LiteralPath $c) { return $c } }
    $g = Get-Command bash -ErrorAction SilentlyContinue
    if ($g) { return $g.Source }
    throw 'bash (Git for Windows) is needed for an archive seed build'
}

function Invoke-BdSeedBuild {
    <#
      One side's fixed-seed test build. $Spec kinds:
        ps1:     { "script": "<build.ps1>", "args": [...], "defines": [...], "rom": "<file in the out folder>", "symbols": [...] }
                 run with -Out <folder> -Define <defines> (the ports' and Vaults' build.ps1 all take both).
        archive: { "repo": "<git folder>", "commit": "<rev>" | "commitFile": "<file holding a 40-hex commit>", "into": "<subfolder>",
                   "links": { "<subfolder>": "<folder>" }, "bash": "<command>", "cwd": "<subfolder>", "rom": "<file>", "symbols": [...] }
                 the commit unpacked fresh by git archive (never the live tree), junctions for what its build reads outside the
                 repository, then the command. A tree already unpacked is reused (the same commit gives the same ROM).
      Returns Rom, Symbols, Detail, Sha.
    #>
    param([Parameter(Mandatory)]$Spec, [Parameter(Mandatory)][string]$Dir, [Parameter(Mandatory)][hashtable]$Ctx, [switch]$NoBuild, [string]$Label = '')
    $x = { param($t) Expand-BdPath -Text ([string]$t) -Context $Ctx }
    $kind = [string](Get-BdProp $Spec 'kind' 'ps1')
    New-Item -ItemType Directory -Force -Path $Dir | Out-Null
    $log = Join-Path $Dir 'build.log'
    if ($kind -eq 'ps1') {
        $out = Join-Path $Dir 'out'
        $rom = Join-Path $out ([string]$Spec.rom)
        $defs = @(@(Get-BdProp $Spec 'defines' @()) | ForEach-Object { [string]$_ })
        if (-not ($NoBuild -and (Test-Path -LiteralPath $rom))) {
            New-Item -ItemType Directory -Force -Path $out | Out-Null
            $script = & $x $Spec.script
            $a = @(@(Get-BdProp $Spec 'args' @()) | ForEach-Object { & $x $_ })
            Write-BdInfo "  $Label seed build: $script -Out $out -Define $($defs -join ',')"
            $global:LASTEXITCODE = 0
            $named = @{ Out = $out; Define = $defs }   # "-Name value" pairs as named parameters (an array splat would pass them positionally)
            for ($i = 0; $i -lt $a.Count; $i++) { if ($a[$i] -match '^-(\w+)$') { $named[$Matches[1]] = $(if ($i + 1 -lt $a.Count -and $a[$i + 1] -notmatch '^-\w+$') { $i++; $a[$i] } else { $true }) } }
            # a child PowerShell, as from a shell: in this session '*> $log' turns a native tool's stderr (sdcc's warnings) into error
            # records, and a build.ps1 with ErrorActionPreference Stop then dies on its first warning
            $cl = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script)
            foreach ($k in $named.Keys) { $v = $named[$k]; if ($v -is [bool]) { $cl += "-$k" } else { $cl += "-$k"; $cl += ($(if ($v -is [array]) { $v -join ',' } else { [string]$v })) } }
            $eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
            try { & powershell.exe @cl *> $log; $ec = $LASTEXITCODE } finally { $ErrorActionPreference = $eap }
            if ($ec -ne 0 -or -not (Test-Path -LiteralPath $rom)) { throw "$Label seed build failed (exit $ec), see $log" }
        }
        $detail = "$(Split-Path -Leaf (Split-Path -Parent (& $x $Spec.script))) -Define $($defs -join ',')"
        $tree = $out
        $syms = @(@(Get-BdProp $Spec 'symbols' @()) | ForEach-Object { Join-Path $out ([string]$_) })
    }
    elseif ($kind -eq 'archive') {
        $repo = & $x $Spec.repo
        $rev = [string](Get-BdProp $Spec 'commit' '')
        $cf = [string](Get-BdProp $Spec 'commitFile' '')
        if ($cf) {
            $t = [System.IO.File]::ReadAllText((& $x $cf))
            if ($t -match '\b([0-9a-f]{40})\b') { $rev = $Matches[1] } else { throw "${Label}: no commit in $cf" }
        }
        if (-not $rev) { $rev = 'HEAD' }
        $full = [string](& git -c safe.directory=* -C $repo rev-parse "$rev^{commit}")
        if ($LASTEXITCODE -ne 0 -or -not $full) { throw "${Label}: $repo has no commit $rev" }
        $full = $full.Trim(); $short = $full.Substring(0, 12)
        $tree = Join-Path $Dir $short
        $rom = Join-Path $tree ([string]$Spec.rom)
        if (-not ($NoBuild -and (Test-Path -LiteralPath $rom))) {
            if (-not (Test-Path -LiteralPath $tree)) {
                $into = Join-Path $tree ([string](Get-BdProp $Spec 'into' '.'))
                New-Item -ItemType Directory -Force -Path $into | Out-Null
                $tar = Join-Path $Dir "$short.tar"
                & git -c safe.directory=* -C $repo archive --format=tar -o $tar $full
                if ($LASTEXITCODE -ne 0) { throw "${Label}: git archive failed" }
                & tar -x -f $tar -C $into
                if ($LASTEXITCODE -ne 0) { throw "${Label}: unpacking $tar failed" }
                Remove-Item -LiteralPath $tar -Force
                $links = Get-BdProp $Spec 'links' $null
                if ($links) {
                    foreach ($p in $links.PSObject.Properties) {
                        $at = Join-Path $tree $p.Name
                        if (Test-Path -LiteralPath $at) { continue }
                        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $at) | Out-Null
                        New-Item -ItemType Junction -Path $at -Target (& $x $p.Value) | Out-Null
                    }
                }
            }
            $cwd = Join-Path $tree ([string](Get-BdProp $Spec 'cwd' '.'))
            Write-BdInfo "  $Label seed build: $(Split-Path -Leaf $repo) at $short (git archive), '$($Spec.bash)' in $cwd"
            $bash = Get-BdBash
            Push-Location -LiteralPath $cwd
            try { $global:LASTEXITCODE = 0; & $bash -c ([string]$Spec.bash) *> $log; $ec = $LASTEXITCODE } finally { Pop-Location }
            if ($ec -ne 0 -or -not (Test-Path -LiteralPath $rom)) { throw "$Label seed build failed (exit $ec), see $log" }
        }
        $detail = "$(Split-Path -Leaf $repo) at $short"
        $syms = @(@(Get-BdProp $Spec 'symbols' @()) | ForEach-Object { Join-Path $tree ([string]$_) })
    }
    else { throw "${Label}: comparison.play kind must be ps1 or archive (got '$kind')" }
    foreach ($s in $syms) { if (-not (Test-Path -LiteralPath $s)) { throw "${Label}: the symbol file $s was not built" } }
    [pscustomobject]@{ Rom = $rom; Tree = $tree; Symbols = $syms; Detail = $detail; Sha = (Get-FileHash -Algorithm SHA256 -LiteralPath $rom).Hash.ToLowerInvariant() }
}

function Invoke-BdPlayCompare {
    <# Both seed builds, the original recorded on the play script, compared with the port, then the 'plays' check. Exit 0 = no drift and it plays. #>
    param([Parameter(Mandatory)][string]$Id, [string]$Script = '', [int]$Frames = 0, [switch]$NoBuild, [switch]$NoOpen, [switch]$Json, [string]$Exe = '')
    [void](Resolve-BdTargets $Id)
    $pm = Read-BdManifest -Id $Id
    $cmp = Get-BdProp $pm 'comparison' $null
    $play = if ($cmp) { Get-BdProp $cmp 'play' $null } else { $null }
    if (-not $play) { throw "project $Id has no comparison.play section" }
    $ctx = New-BdContext -Manifest $pm
    # the seed builds' folder: on C: (BROKENNES_WORK, else C:\BrokenNes-work): the archive builds need junctions, which T: does not take
    $dir = Join-Path $(if ($env:BROKENNES_WORK) { $env:BROKENNES_WORK } else { 'C:\BrokenNes-work' }) "play-compare\$Id"
    $o = Invoke-BdSeedBuild -Spec $play.original -Dir (Join-Path $dir 'original') -Ctx $ctx -NoBuild:$NoBuild -Label 'original'
    $ctx['originalTree'] = $o.Tree   # {originalTree}: the original's unpacked sources (the SMS port translates the same NES commit)
    $p = Invoke-BdSeedBuild -Spec $play.port -Dir (Join-Path $dir 'port') -Ctx $ctx -NoBuild:$NoBuild -Label 'port'
    if (-not $Script) { $Script = [string]$play.input }
    if ($Frames -le 0) { $Frames = [int]$play.frames }
    $man = [string](Get-BdProp $play 'manifest' '')
    $man = if ($man) { Expand-BdPath -Text $man -Context $ctx } else { Get-BdManifestPath $Id }
    $a = @('compare', '--original-rom', $o.Rom, '--input', $Script, '--frames', [string]$Frames, '--port', $p.Rom, '--manifest', $man, '--original-project', [string]$cmp.original, '--port-project', $Id)
    foreach ($s in $o.Symbols) { $a += @('--original-sym', $s) }
    foreach ($s in $p.Symbols) { $a += @('--port-sym', $s) }
    $a += @('--port-console', [string]$pm.console)
    if ($NoOpen) { $a += '--no-open' }
    $r = Invoke-BdTimeflowJson -Arguments $a -Exe $Exe
    $j = $r.Json
    $short = @()
    $exp = Get-BdProp $play 'expect' $null
    if ($exp) {
        foreach ($e in $exp.PSObject.Properties) {
            foreach ($side in 'original', 'port') {
                $n = [int](Get-BdProp $j.eventCounts.$side $e.Name 0)
                if ($n -lt [int]$e.Value) { $short += "$side $($e.Name) $n < $($e.Value)" }
            }
        }
    }
    $j | Add-Member -NotePropertyName play -NotePropertyValue ([pscustomobject]@{ original = $o.Detail; originalSha256 = $o.Sha; port = $p.Detail; portSha256 = $p.Sha; frames = $Frames; plays = ($short.Count -eq 0); short = $short }) -Force
    $code = if ($r.ExitCode -eq 0 -and $short.Count -eq 0) { 0 } else { 1 }
    if ($Json) { Write-Host ($j | ConvertTo-Json -Depth 16); return $code }
    Write-BdInfo "  original $($j.original.console) $($o.Detail): $($j.original.frames) frames, $($j.original.events) events $($j.eventCounts.original | ConvertTo-Json -Compress)"
    Write-BdInfo "  port     $($j.port.console) $($p.Detail): $($j.port.frames) frames, $($j.port.events) events $($j.eventCounts.port | ConvertTo-Json -Compress)"
    if ($short.Count -gt 0) { Write-BdFail "the script does not play enough: $($short -join '; ')" } else { Write-BdOk 'it plays: every expected event count reached on both sides' }
    if (Get-BdProp $j 'warning' $null) { Write-BdWarn $j.warning }
    if ($r.ExitCode -eq 0) { Write-BdOk $j.verdict }
    else {
        Write-BdFail $j.verdict
        if ($j.drift) {
            Write-BdInfo ("  original at frame {0} (tick {1}): {2}" -f $j.drift.original.frame, $j.drift.original.tick, ($j.drift.original.state | ConvertTo-Json -Compress))
            Write-BdInfo ("  port     at frame {0} (tick {1}): {2}" -f $j.drift.port.frame, $j.drift.port.tick, ($j.drift.port.state | ConvertTo-Json -Compress))
            $op = Get-BdProp $j.drift 'opened' $null
            if ($op) { foreach ($side in 'original', 'port') { $s = $op.$side; Write-BdInfo ("  opened {0}: timeline {1} frames {2}-{3}; {4}" -f $side, $s.timeline, $s.from, $s.to, $s.resume) } }
        }
    }
    $code
}
