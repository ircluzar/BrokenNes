# BrokenDev Timeflow (milestones T1 and T2 of docs/projects/brokendev-timeflow-plan.md). Requires Common, Tools, Manifest, Verify, Movies.
#
#   timeflow record <project> [-Scenario <verify id> | -Script "120:Start,..." -Frames N | -Movie <movie>] [-Course sym,sym] [-Coverage]
#                   records the project's built ROM headless into a timeline (a state every frame: keyframes + page deltas, the per-frame
#                   index): Workshop --timeflow record. Headless only: the desktop app records the user's play only when the user switches
#                   it on (Emulator > Debug Windows > Timeflow), never from here.
#   timeflow list   [project]                       the timelines (a branch under its parent) and each game folder's space against its cap
#   timeflow info   <timeline>                      one timeline
#   timeflow verify <timeline>                      restore equals record on every frame (and loaded into a machine)
#   timeflow jump   <timeline> -Frame F [-Path out.png]     restores frame F in a headless machine (proved equal to the record)
#   timeflow resume <timeline> -Frame F [-Script S -Frames N [-Record]]
#                   from frame F: with no -Script, replays the recorded inputs (in sync to the end, or the first frame off); with -Script,
#                   new input for N frames (a branch; -Record keeps it as a timeline whose parent is this one at F)
#   timeflow prune  [project]                       retires the oldest timelines until each game folder is within its cap (keep: never)
#   timeflow keep | unkeep <timeline>
#   timeflow status                                 the root (BROKENDEV_HOME\timeflow) and every game folder's use
#   timeflow search [project|timeline] -Query "CONDITION" [-From A] [-To B] [-MaxHits N] [-CoarseOnly]
#                   T4: the frames where a condition held (Workshop --timeflow search; the project's symbols); see the plan's "T4 as built"
#   timeflow record ... -Index                      also the search index (coverage, per-page access counts, the hardware checks)
#
# Timelines live in BROKENDEV_HOME\timeflow\<project>\ (a ROM of no project: timeflow\_roms\<name>-<sha>\), never in git, never in
# %APPDATA%\BrokenNes; each game folder has a cap (5 GB by default, timeflow.json).

Set-StrictMode -Version Latest

function Invoke-BdWorkshopTimeflow {
    <# Workshop --timeflow <args>; returns the process result (ExitCode, Output). #>
    param([Parameter(Mandatory)][string[]]$Arguments, [string]$Exe = '', [int]$TimeoutSec = 3600)
    if (-not $Exe) { $Exe = Get-BdWorkshopExe }
    if (-not $Exe -or -not (Test-Path -LiteralPath $Exe)) { throw 'BrokenNes Workshop is not built. Run: brokendev.ps1 build-workshop' }
    Invoke-BdProcess -FilePath $Exe -Arguments (@('--timeflow') + $Arguments) -TimeoutSec $TimeoutSec -Env (Get-BdChildEnv)
}

function Invoke-BdTimeflowJson {
    <# A --timeflow verb with --json: the parsed object, or an error with the Workshop's own words. Exit 1 (a check failed) still returns the object. #>
    param([Parameter(Mandatory)][string[]]$Arguments, [string]$Exe = '')
    $r = Invoke-BdWorkshopTimeflow -Arguments ($Arguments + '--json') -Exe $Exe
    $text = $r.Output
    $i = $text.IndexOf('{'); $j = $text.LastIndexOf('}')
    $o = $null
    if ($i -ge 0 -and $j -gt $i) { try { $o = $text.Substring($i, $j - $i + 1) | ConvertFrom-Json } catch { $o = $null } }
    if ($null -eq $o) { throw "timeflow $($Arguments[0]) failed (exit $($r.ExitCode)): $(Select-BdTail $text 6)" }
    [pscustomobject]@{ ExitCode = $r.ExitCode; Json = $o }
}

function Invoke-BdTimeflowRecord {
    param([Parameter(Mandatory)][string]$Id, [string]$Scenario = '', [string]$Script = '', [int]$Frames = 0, [string]$Movie = '', [string[]]$Course = @(), [switch]$Coverage, [switch]$Index, [string]$Exe = '')
    $p = Get-BdProjectRomAndSymbols -Id $Id
    $m = $p.Manifest
    $a = @('record', '--rom', $p.Rom, '--note', "brokendev timeflow record $Id")
    if ($Scenario) {
        $sc = @($m.verify.scenarios | Where-Object { $_.id -eq $Scenario })
        if ($sc.Count -eq 0) { throw "project $Id has no verify scenario '$Scenario'" }
        if (-not $Script) { $Script = [string](Get-BdProp $sc[0] 'input' '') }
        if ($Frames -le 0) { $Frames = [int]$sc[0].frames }
        if ([bool](Get-BdProp $sc[0] 'ntscFrameTiming' $false)) { $a += '--ntsc' }
        $a += @('--power-on', [string](Get-BdProp $sc[0] 'powerOnRam' 'fceux'))
    }
    if ($Movie) { $a += @('--movie', (Resolve-BdMoviePath -Id $Id -Movie $Movie)) }
    elseif ($Frames -le 0) { throw 'timeflow record needs -Scenario <verify id>, -Movie <movie>, or -Frames N (and -Script)' }
    if ($Frames -gt 0) { $a += @('--frames', [string]$Frames) }
    if ($Script) { $a += @('--input', $Script) }
    $cores = Get-BdProp $m.verify 'cores' $null
    if ($cores -and [string]$m.console -eq 'nes') { $a += @('--cores', ('{0}/{1}/{2}' -f (Get-BdProp $cores 'cpu' 'FIX'), (Get-BdProp $cores 'ppu' 'FIX'), (Get-BdProp $cores 'apu' 'FIX'))) }
    elseif ($cores) { $a += @('--cores', ('{0}/{1}/{2}' -f (Get-BdProp $cores 'cpu' ''), (Get-BdProp $cores 'ppu' ''), (Get-BdProp $cores 'apu' ''))) }
    if (@('gb', 'gbc', 'wms') -contains [string]$m.console) { $a += @('--console', [string]$m.console) }
    foreach ($s in $p.Symbols) { $a += @('--sym', $s) }
    foreach ($c in @($Course | ForEach-Object { ([string]$_) -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })) { $a += @('--var', "$c=[$c]") }
    if ($Coverage -or $Index) { $a += '--coverage' }
    if ($Index) { $a += @('--accesses', '--checks') }
    (Invoke-BdTimeflowJson -Arguments $a -Exe $Exe)
}

function Show-BdTimeline {
    param($J)
    $line = 'timeline {0}: {1} {2}, frames {3}-{4} ({5}), {6}, {7:N0} bytes on disk ({8} a frame)' -f $J.id, $J.console, $J.rom, $J.startFrame, ($J.endFrame - 1), $J.frames, $J.status, $J.folderBytes, $J.bytesPerFrame
    if ($J.status -eq 'stopped') { Write-BdFail "$line; stopped: $($J.stopReason)" } else { Write-BdOk $line }
    Write-BdInfo ("  restore = record checked on {0} frames while recording ({1} failed); N {2}, page {3}, {4}" -f $J.verifiedFrames, $J.verifyFailures, $J.keyEvery, $J.pageSize, $J.codec)
    if (Get-BdProp $J 'fps' $null) { Write-BdInfo ("  {0} fps recording; the emulation thread spent {1} ms a frame on it (worst {2})" -f $J.fps, $J.captureMsAverage, $J.captureMsMax) }
    if (Get-BdProp $J 'parent' $null) { Write-BdInfo ("  a branch of {0} at frame {1}" -f $J.parent.id, $J.parent.frame) }
    Write-BdInfo "  $($J.dir)"
}

function Get-BdTimelineSymbols {
    <# The symbol files of the project a timeline was recorded from (its build's published symbols), or none for a ROM of no project. #>
    param([Parameter(Mandatory)][string]$Timeline, [string]$Exe = '')
    $info = (Invoke-BdTimeflowJson -Arguments @('info', '--timeline', $Timeline) -Exe $Exe).Json
    $project = [string](Get-BdProp $info 'project' '')
    if (-not $project) { return @() }
    try { return @((Get-BdProjectRomAndSymbols -Id $project).Symbols) } catch { Write-BdInfo "  (no symbols: $($_.Exception.Message))"; return @() }
}

function Show-BdProfileAnswer {
    param($Q)
    if (Get-BdProp $Q 'error' $null) { Write-BdFail "  $($Q.query): $($Q.error)"; return }
    Write-Host "  $($Q.query):"
    $copy = $Q | Select-Object -Property * -ExcludeProperty query
    foreach ($line in (($copy | ConvertTo-Json -Depth 8) -split "`n")) { Write-Host "    $line" }
}

function Show-BdProfile {
    param($J)
    $c = $J.cost
    $ins = ($J.instructions.PSObject.Properties | ForEach-Object { "$($_.Name) $($_.Value)" }) -join ', '
    $line = 'profiled frames {0}-{1} of {2} ({3}): {4} frames, instructions {5}, {6:N0} accesses; {7} fps = {8}x slower than real time' -f $J.from, $J.to, $J.timeline, $J.console, $J.framesProfiled, $ins, $J.accesses, $c.profiledFps, $c.timesSlowerThanRealTime
    if ($J.inSync) { Write-BdOk "$line; in sync with the recording" } else { Write-BdFail "$line; OUT OF SYNC at frame $($J.offFrame) ($($J.offWhat))" }
    if ($J.profile) { Write-BdInfo ("  kept as {0} ({1}, {2:N0} bytes, {3} a frame): {4}; deleted when {5}" -f $J.profile.id, $J.profile.status, $J.profile.bytes, $J.profile.bytesPerFrame, $J.profile.dir, $J.profile.deletedWhen) }
    else { Write-BdInfo "  $($J.stored)" }
    foreach ($q in @($J.queries)) { Show-BdProfileAnswer $q }
}

function Invoke-BdTimeflow {
    <# brokendev.ps1 timeflow <action> [target] ...: returns the exit code. #>
    param([string]$Action, [string]$Target = '', [string]$Scenario = '', [string]$Script = '', [int]$Frames = 0, [string]$Movie = '', [string[]]$Course = @(),
          [long]$Frame = -1, [string]$Png = '', [switch]$Record, [switch]$Coverage, [switch]$Json, [string]$Exe = '',
          [long]$From = -1, [long]$To = -1, [string[]]$Query = @(), [switch]$Store, [switch]$Keep, [int]$TtlMinutes = 0, [int]$LimitMb = 0,
          [int]$MaxHits = 100, [switch]$CoarseOnly, [switch]$Index)
    switch ($Action.ToLowerInvariant()) {
        'search' {
            # T4: a condition over the corpus (a project's timelines, one timeline, or all)
            $cond = (@($Query | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ }) -join ' and ')
            if (-not $cond) { throw 'timeflow search needs -Query "CONDITION" (exec <spec> [if <expr>], write <spec>, state <expr>, finding, picture changed, color #RRGGBB, sound silent, channel <name> playing; and / not)' }
            $a = @('search', '--q', $cond, '--max-hits', [string]$MaxHits)
            $syms = @()
            if ($Target) {
                $isProject = $false
                try { [void](Resolve-BdTargets $Target); $isProject = $true } catch { $isProject = $false }
                if ($isProject) { $a += @('--project', $Target); try { $syms = @((Get-BdProjectRomAndSymbols -Id $Target).Symbols) } catch { $syms = @() } }
                else { $a += @('--timeline', $Target); $syms = @(Get-BdTimelineSymbols -Timeline $Target -Exe $Exe) }
            }
            foreach ($s in $syms) { $a += @('--sym', $s) }
            if ($From -ge 0) { $a += @('--from', [string]$From) }
            if ($To -ge 0) { $a += @('--to', [string]$To) }
            if ($CoarseOnly) { $a += '--coarse' }
            $r = Invoke-BdTimeflowJson -Arguments $a -Exe $Exe
            $j = $r.Json
            if ($Json) { Write-Host ($j | ConvertTo-Json -Depth 12); return $(if ($j.totalHits -gt 0) { 0 } else { 1 }) }
            $c = $j.cost
            $line = "search '{0}': {1} hit(s) in {2} timeline(s); coarse {3} frames at {4} fps -> {5} candidate(s); {6} frame(s) re-run; first hit {7} ms, total {8} ms" -f $j.condition, $j.totalHits, $j.searched, $c.coarseFrames, $c.coarseFps, $c.candidates, $c.reRunFrames, $c.firstHitMs, $c.totalMs
            if ($j.totalHits -gt 0) { Write-BdOk $line } else { Write-BdInfo $line }
            foreach ($cl in @($j.clauses)) { Write-BdInfo ("  '{0}': {1}; false candidates {2:P1} of {3}" -f $cl.clause, $cl.coarse, [double]$cl.falseCandidateRate, $cl.maybeChecked) }
            foreach ($h in @($j.hits)) {
                $where = @(); if ($h.cycle) { $where += "cycle $($h.cycle)" }; if ($h.pc) { $where += "pc $($h.pc)" }; if ($null -ne $h.scanline) { $where += "line $($h.scanline)" }
                $what = @(); if ($h.kind) { $what += $h.kind }; if ($h.address) { $what += "$($h.address) = $($h.value)" }; if ($h.instruction) { $what += "by $($h.instruction)" }; if ($h.in) { $what += "in $($h.in)" }
                Write-Host ("  HIT {0} frame {1} ({2}) {3} {4}  [{5}]" -f $h.timeline, $h.frame, $h.via, ($where -join ' '), ($what -join ' '), (@($h.context) -join '; '))
            }
            foreach ($t in @($j.timelines)) { if ($t.skipped) { Write-BdInfo "  timeline $($t.id): skipped: $($t.skipped)" } }
            if (@($j.hits).Count -gt 0) { $h0 = @($j.hits)[0]; Write-BdInfo "  next: timeflow jump $($h0.timeline) -Frame $($h0.frame); timeflow profile $($h0.timeline) -From $($h0.frame) -To $($h0.frame) -Query summary" }
            return $(if ($j.totalHits -gt 0) { 0 } else { 1 })
        }
        'profile' {
            # T3: a span re-run at full depth with streaming queries (and kept when -Store)
            if (-not $Target -or $From -lt 0 -or $To -lt 0) { throw 'timeflow profile needs a timeline, -From A and -To B (and -Query "writes <symbol>" ...)' }
            $a = @('profile', '--timeline', $Target, '--from', [string]$From, '--to', [string]$To, '--note', 'brokendev timeflow profile')
            foreach ($q in @($Query | ForEach-Object { ([string]$_) -split ";" } | ForEach-Object { $_.Trim() } | Where-Object { $_ })) { $a += @("--q", $q) }
            foreach ($s in (Get-BdTimelineSymbols -Timeline $Target -Exe $Exe)) { $a += @('--sym', $s) }
            if ($Store -or $Keep) { $a += '--store' }
            if ($Keep) { $a += '--keep' }
            if ($TtlMinutes -gt 0) { $a += @('--ttl-min', [string]$TtlMinutes) }
            if ($LimitMb -gt 0) { $a += @('--limit-mb', [string]$LimitMb) }
            $r = Invoke-BdTimeflowJson -Arguments $a -Exe $Exe
            if ($Json) { Write-Host ($r.Json | ConvertTo-Json -Depth 12); return $(if ($r.ExitCode -eq 0) { 0 } else { 1 }) }
            Show-BdProfile $r.Json
            return $(if ($r.ExitCode -eq 0) { 0 } else { 1 })
        }
        'profile-query' {
            if (-not $Target -or $Query.Count -eq 0) { throw 'timeflow profile-query needs a kept profile (its id) and -Query Q' }
            $a = @('profile-query', '--profile', $Target); foreach ($q in @($Query | ForEach-Object { ([string]$_) -split ";" } | ForEach-Object { $_.Trim() } | Where-Object { $_ })) { $a += @("--q", $q) }
            $r = Invoke-BdTimeflowJson -Arguments $a -Exe $Exe
            if ($Json) { Write-Host ($r.Json | ConvertTo-Json -Depth 12); return 0 }
            Write-BdInfo ("profile {0} ({1} frames {2}-{3}, {4}): read back in {5} ms" -f $r.Json.profile.id, $r.Json.profile.console, $r.Json.profile.from, $r.Json.profile.to, $r.Json.profile.status, $r.Json.queryMs)
            foreach ($q in @($r.Json.queries)) { Show-BdProfileAnswer $q }
            return 0
        }
        'profiles' {
            $r = Invoke-BdTimeflowJson -Arguments @('profiles') -Exe $Exe
            if ($Json) { Write-Host ($r.Json | ConvertTo-Json -Depth 8); return 0 }
            foreach ($s in @($r.Json.swept)) { Write-BdInfo "  swept $($s.id): $($s.why)" }
            foreach ($p in @($r.Json.profiles)) { Write-Host ('  {0}  {1} timeline {2} frames {3}-{4}  {5}  {6:N0} KB  {7}expires {8}' -f $p.id, $p.console, $p.timeline, $p.from, $p.to, $p.status, ($p.bytes / 1KB), $(if ($p.keep) { 'KEEP ' } else { '' }), $p.expires) }
            Write-BdInfo "$(@($r.Json.profiles).Count) kept profile(s) under $($r.Json.root)"
            return 0
        }
        { $_ -in 'profile-keep', 'profile-unkeep' } {
            if (-not $Target) { throw "timeflow $Action needs a profile" }
            $a = @('profile-keep', '--profile', $Target); if ($Action -eq 'profile-unkeep') { $a += '--off' }; if ($TtlMinutes -gt 0) { $a += @('--ttl-min', [string]$TtlMinutes) }
            $r = Invoke-BdTimeflowJson -Arguments $a -Exe $Exe
            Write-BdOk "$($r.Json.id): keep $($r.Json.keep), expires $($r.Json.expires)"; return 0
        }
        'profile-drop' {
            if (-not $Target) { throw 'timeflow profile-drop needs a profile (or all)' }
            $a = if ($Target -eq 'all') { @('profile-drop', '--all') } else { @('profile-drop', '--profile', $Target) }
            $r = Invoke-BdTimeflowJson -Arguments $a -Exe $Exe
            Write-BdOk "dropped $(if ($Target -eq 'all') { @($r.Json.dropped).Count } else { $r.Json.dropped })"; return $(if ($r.ExitCode -eq 0) { 0 } else { 1 })
        }
        'profile-sweep' {
            $r = Invoke-BdTimeflowJson -Arguments @('profile-sweep') -Exe $Exe
            if ($Json) { Write-Host ($r.Json | ConvertTo-Json -Depth 8); return 0 }
            foreach ($s in @($r.Json.swept)) { Write-BdInfo "  swept $($s.id): $($s.why)" }
            Write-BdOk "swept $(@($r.Json.swept).Count) profile(s); $($r.Json.left) left"; return 0
        }
        'record' {
            if (-not $Target) { throw 'timeflow record needs a project' }
            [void](Resolve-BdTargets $Target)
            $r = Invoke-BdTimeflowRecord -Id $Target -Scenario $Scenario -Script $Script -Frames $Frames -Movie $Movie -Course $Course -Coverage:$Coverage -Index:$Index -Exe $Exe
            if ($Json) { Write-Host ($r.Json | ConvertTo-Json -Depth 8) } else { Show-BdTimeline $r.Json }
            return $(if ($r.ExitCode -eq 0) { 0 } else { 1 })
        }
        'list' {
            $a = @('list'); if ($Target) { [void](Resolve-BdTargets $Target); $a += @('--project', $Target) }
            $r = Invoke-BdTimeflowJson -Arguments $a -Exe $Exe
            if ($Json) { Write-Host ($r.Json | ConvertTo-Json -Depth 8); return 0 }
            Write-BdInfo "Timeflow root: $($r.Json.root)"
            foreach ($g in @($r.Json.games)) {
                Write-Host ('{0}  ({1:N1} MB of {2:N2} GB)' -f $g.gameDir, ($g.usedBytes / 1MB), ($g.capBytes / 1GB))
                foreach ($t in @($g.timelines)) {
                    $pad = '  ' * ([int]$t.depth + 1)
                    $br = if ($t.parent) { "  (branch of $($t.parent) at $($t.parentFrame))" } else { '' }
                    Write-Host ('{0}{1}  {2} {3}  frames {4}-{5}  {6}{7}  {8:N0} KB  {9}{10}' -f $pad, $t.id, $t.console, $t.rom, $t.startFrame, ($t.startFrame + $t.frames - 1), $t.status, $(if ($t.keep) { ' KEEP' } else { '' }), ($t.bytes / 1KB), $t.source, $br)
                }
            }
            return 0
        }
        'info' {
            if (-not $Target) { throw 'timeflow info needs a timeline (its id or folder)' }
            $r = Invoke-BdTimeflowJson -Arguments @('info', '--timeline', $Target) -Exe $Exe
            if ($Json) { Write-Host ($r.Json | ConvertTo-Json -Depth 8) } else { Show-BdTimeline $r.Json }
            return 0
        }
        'verify' {
            if (-not $Target) { throw 'timeflow verify needs a timeline' }
            $r = Invoke-BdTimeflowJson -Arguments @('verify', '--timeline', $Target, '--machine') -Exe $Exe
            if ($Json) { Write-Host ($r.Json | ConvertTo-Json -Depth 8) }
            $j = $r.Json
            $msg = "timeline $($j.id): restore equals record on $($j.restoreEqualsRecord) of $($j.checked) frames; loaded into a machine: $($j.machineChecked - $j.machineFailed) of $($j.machineChecked)"
            if ($r.ExitCode -eq 0) { Write-BdOk $msg; return 0 }
            Write-BdFail $msg; foreach ($p in @($j.problems)) { Write-BdInfo "  $p" }; return 1
        }
        'jump' {
            if (-not $Target -or $Frame -lt 0) { throw 'timeflow jump needs a timeline and -Frame F' }
            $a = @('jump', '--timeline', $Target, '--frame', [string]$Frame); if ($Png) { $a += @('--png', $Png) }
            $r = Invoke-BdTimeflowJson -Arguments $a -Exe $Exe
            if ($Json) { Write-Host ($r.Json | ConvertTo-Json -Depth 8); return 0 }
            $j = $r.Json
            Write-BdOk "timeline $($j.id) frame $($j.frame): restored in $($j.restoreMs) ms; the machine's state is the recorded one ($($j.state))"
            Write-BdInfo "  pads $($j.pads); picture $($j.picture); RAM $($j.ram)$(if ($j.png) { "; picture saved to $($j.png)" })"
            foreach ($p in $j.vars.PSObject.Properties) { Write-BdInfo "  $($p.Name) = $($p.Value)" }
            return 0
        }
        'resume' {
            if (-not $Target -or $Frame -lt 0) { throw 'timeflow resume needs a timeline and -Frame F (with -Script S -Frames N for new input, else the recorded inputs replay)' }
            $a = @('resume', '--timeline', $Target, '--frame', [string]$Frame)
            if ($Script) { $a += @('--input', $Script); if ($Frames -le 0) { throw 'resume with new input needs -Frames N' }; $a += @('--frames', [string]$Frames); if ($Record) { $a += '--record' } }
            else { $a += '--replay'; if ($Frames -gt 0) { $a += @('--frames', [string]$Frames) } }
            $r = Invoke-BdTimeflowJson -Arguments $a -Exe $Exe
            if ($Json) { Write-Host ($r.Json | ConvertTo-Json -Depth 8) }
            $j = $r.Json
            if ($j.mode -eq 'replay') {
                if ($j.inSync) { Write-BdOk "replayed frames $($j.from)-$($j.to - 1) of $($j.id): in sync with the recording ($($j.checks) checks)"; return 0 }
                Write-BdFail "replayed from $($j.from): off the recording at frame $($j.offFrame) ($($j.offWhat))"; return 1
            }
            Write-BdOk "resumed $($j.id) at frame $($j.from) with new input for $($j.framesRun) frames$(if ($j.branch) { ": recorded as branch $($j.branch)" } else { ' (not recorded: -Record keeps it)' })"
            return $(if ($r.ExitCode -eq 0) { 0 } else { 1 })
        }
        'prune' {
            $a = @('prune'); if ($Target) { [void](Resolve-BdTargets $Target); $a += @('--project', $Target) }
            $r = Invoke-BdTimeflowJson -Arguments $a -Exe $Exe
            if ($Json) { Write-Host ($r.Json | ConvertTo-Json -Depth 8); return 0 }
            foreach ($g in @($r.Json.games)) { Write-BdInfo ('{0}: retired {1} ({2}); {3:N1} MB of {4:N2} GB' -f $g.gameDir, @($g.retired).Count, (@($g.retired) -join ', '), ($g.usedBytes / 1MB), ($g.capBytes / 1GB)) }
            return 0
        }
        { $_ -in 'keep', 'unkeep' } {
            if (-not $Target) { throw "timeflow $Action needs a timeline" }
            $a = @('keep', '--timeline', $Target); if ($Action -eq 'unkeep') { $a += '--off' }
            $r = Invoke-BdWorkshopTimeflow -Arguments $a -Exe $Exe
            if ($r.ExitCode -ne 0) { throw (Select-BdTail $r.Output 3) }
            Write-BdOk (Select-BdTail $r.Output 1); return 0
        }
        'status' {
            $r = Invoke-BdTimeflowJson -Arguments @('status') -Exe $Exe
            if ($Json) { Write-Host ($r.Json | ConvertTo-Json -Depth 8); return 0 }
            Write-BdInfo "Timeflow root: $($r.Json.root)$(if (-not $r.Json.exists) { ' (nothing recorded yet)' })"
            foreach ($g in @($r.Json.games)) { Write-Host ('  {0}: {1} timeline(s), {2:N1} MB of {3:N2} GB' -f $g.gameDir, $g.timelines, ($g.usedBytes / 1MB), ($g.capBytes / 1GB)) }
            return 0
        }
        default { throw 'timeflow: record, list, info, verify, jump, resume, prune, keep, unkeep, status, search, profile, profile-query, profiles, profile-keep, profile-unkeep, profile-drop or profile-sweep' }
    }
}
