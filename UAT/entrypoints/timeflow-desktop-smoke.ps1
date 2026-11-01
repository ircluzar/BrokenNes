<#
.SYNOPSIS
  BrokenDev Timeflow (milestones T1 / T2) in the real desktop app: recording is off until the user switches it on, an agent's request records
  nothing until the user allows it, the REC mark, the timeline written and proved (restore equals record), the Timeflow window's jump and replay.
  Exit 0 = pass. Prints ONE JSON result line as the last line of stdout.

.DESCRIPTION
  Starts the shipped WinForms app with scratch BROKENNES_CONFIG_DIR, BROKENNES_SAVES_DIR, BROKENNES_BUGS_DIR and BROKENDEV_HOME (the user's
  config, saves and BrokenDev home are never read or written: a listing of %APPDATA%\BrokenNes before and after proves it).
    OFF        a game plays 6 s: GET /api/timeflow/status says off, no timeflow folder exists (nothing recorded, nothing written).
    NO SWITCH  POST /api/timeflow/enabled and /api/timeflow/start do not exist (404): an agent cannot switch recording on.
    DECLINE    POST /api/timeflow/request: the prompt shows (UI Automation finds it), the game keeps running, nothing records; Decline: declined,
               still nothing written.
    ALLOW      a second request; Allow: recording on, the title says [REC], the REC mark in the menu bar; 12 s of play; the status names the
               timeline and the space used. A third request while on is "not needed".
    OFF AGAIN  the user's REC box in Emulator > Debug Windows > Timeflow (UI Automation): off; the timeline is closed.
    PROOF      Workshop --timeflow verify --machine on the desktop's timeline: every frame restored equals the record and loads into a machine;
               --timeflow resume --replay from its middle: in sync to its end.
    WINDOW     the Timeflow window lists the timeline; frame typed, Jump: the game is at that frame (paused); Replay recorded: in sync.
    COST       the frames emulated in 8 s with recording on and off (the app paces at 60 fps: both must hold it).
  Rule zero (UAT\README.md): never kill by process name; only the PIDs started here are touched.

.EXAMPLE
  pwsh -File UAT\entrypoints\timeflow-desktop-smoke.ps1 -Exe C:\path\BrokenNes.Windows.exe -WorkshopExe C:\path\BrokenNes.Workshop.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$WorkshopExe,
    [string]$Rom = 'P:\BrokenDev\env\out\vrun-nes\vrun_game.nes',
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\uat_timeflow_desktop')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$checks = [ordered]@{}
$result = [ordered]@{ entrypoint = 'timeflow-desktop'; pass = $false }

function Check($name, [bool]$ok, $detail) {
    $script:checks[$name] = [ordered]@{ ok = $ok; detail = "$detail" }
    Write-Host ("[{0}] {1} - {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}
function Api([int]$procId, [string]$method, [string]$path, $body = $null) { Invoke-BrokenNesApi -ProcessId $procId -Method $method -Path $path -Body $body -TimeoutSec 60 }
function Listing([string]$dir) {
    if (-not (Test-Path -LiteralPath $dir)) { return '' }
    (@(Get-ChildItem -LiteralPath $dir -Recurse -File -Force -ErrorAction SilentlyContinue | Sort-Object FullName | ForEach-Object { "$($_.FullName.Substring($dir.Length))|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" })) -join "`n"
}
function Files([string]$dir) { if (Test-Path -LiteralPath $dir) { @(Get-ChildItem -LiteralPath $dir -Recurse -File -Force).Count } else { 0 } }
function Play([int]$procId, [int]$seconds) {
    $end = (Get-Date).AddSeconds($seconds); $k = 0
    while ((Get-Date) -lt $end) {
        $b = if ($k % 3 -eq 0) { @('Right', 'A') } elseif ($k % 3 -eq 1) { @('Right') } else { @() }
        Api $procId POST '/api/input/set-buttons' @{ player = 1; buttons = $b } | Out-Null
        Start-Sleep -Milliseconds 450; $k++
    }
    Api $procId POST '/api/input/clear' | Out-Null
}
function Frames([int]$procId) { $d = Api $procId GET '/api/audio/device'; $n = if ($null -ne $d.device) { $d.device.framesEmulated } else { $d.framesEmulated }; if ($null -eq $n) { throw "GET /api/audio/device has no framesEmulated: $($d | ConvertTo-Json -Compress -Depth 4)" }; [long]$n }
function Workshop([string[]]$a) {
    $out = Join-Path $WorkDir ("ws_" + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.txt')
    $p = Start-Process -FilePath $WorkshopExe -ArgumentList $a -Wait -PassThru -NoNewWindow -RedirectStandardOutput $out -RedirectStandardError "$out.err"
    $t = (Get-Content -LiteralPath $out -Raw) + (Get-Content -LiteralPath "$out.err" -Raw)
    $j = $null; $i = $t.IndexOf('{'); $e = $t.LastIndexOf('}'); if ($i -ge 0 -and $e -gt $i) { try { $j = $t.Substring($i, $e - $i + 1) | ConvertFrom-Json } catch { } }
    [pscustomobject]@{ Code = $p.ExitCode; Json = $j; Text = $t }
}
function NotFound([int]$procId, [string]$path) {
    $r = Invoke-BrokenNesApi -ProcessId $procId -Method POST -Path $path -Body @{ enabled = $true } -TimeoutSec 20 -Raw
    return $r.StatusCode -eq 404
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
function Cond($prop, $value) { New-Object System.Windows.Automation.PropertyCondition($prop, $value) }
function WaitFor([scriptblock]$probe, [int]$ms = 8000) {
    $end = (Get-Date).AddMilliseconds($ms)
    do { $r = & $probe; if ($r) { return $r }; Start-Sleep -Milliseconds 150 } while ((Get-Date) -lt $end)
    return $null
}
function FindByName($root, [string]$name) { $root.FindFirst($TS::Descendants, (Cond $AE::NameProperty $name)) }
function FindById($root, [string]$id) { $root.FindFirst($TS::Descendants, (Cond $AE::AutomationIdProperty $id)) }
function Invoke-El($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Expand-El($el) { $el.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand() }
function TextOf($el) { $vp = $null; if ($el.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) { return $vp.Current.Value }; return $el.Current.Name }
function SetText($el, [string]$t) { $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($t) }
function Main([int]$procId) { WaitFor { $AE::RootElement.FindFirst($TS::Children, (New-Object System.Windows.Automation.AndCondition((Cond $AE::ProcessIdProperty $procId), (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Window))))) } }
function Open-Timeflow([int]$procId) {
    $main = Main $procId
    $emu = WaitFor { FindByName $main 'Emulator' }
    Expand-El $emu; Start-Sleep -Milliseconds 400
    $sub = WaitFor { FindByName $AE::RootElement 'Debug Windows' }
    if (-not $sub) { $sub = WaitFor { FindByName $main 'Debug Windows' } }
    Expand-El $sub; Start-Sleep -Milliseconds 400
    $item = WaitFor { FindByName $AE::RootElement 'Timeflow (timelines, scrubber, jump, resume)' }
    if (-not $item) { throw "the menu item Timeflow was not found" }
    Invoke-El $item
    $w = WaitFor { $AE::RootElement.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition((Cond $AE::ProcessIdProperty $procId), (Cond $AE::NameProperty 'BrokenNes Timeflow')))) }
    if (-not $w) { throw "the Timeflow window did not open" }
    Start-Sleep -Milliseconds 700
    return $w
}

$inst = $null
$saved = @{ BROKENNES_CONFIG_DIR = $env:BROKENNES_CONFIG_DIR; BROKENNES_SAVES_DIR = $env:BROKENNES_SAVES_DIR; BROKENDEV_HOME = $env:BROKENDEV_HOME; BROKENNES_BUGS_DIR = $env:BROKENNES_BUGS_DIR; BROKENNES_TEST_AUDIO_DEVICE_FILE = $env:BROKENNES_TEST_AUDIO_DEVICE_FILE }
$userDir = Join-Path $env:APPDATA 'BrokenNes'
$before = Listing $userDir
try {
    . (Join-Path $repo 'UAT\lib\ApiClient.ps1')
    foreach ($r in $Rom, $Exe, $WorkshopExe) { if (-not (Test-Path -LiteralPath $r)) { throw "missing $r" } }
    if (Test-Path -LiteralPath $WorkDir) { Remove-Item -LiteralPath $WorkDir -Recurse -Force }
    $cfgDir = Join-Path $WorkDir 'config'; $saveDir = Join-Path $WorkDir 'saves'; $bdHome = Join-Path $WorkDir 'bdhome'; $bugs = Join-Path $WorkDir 'bugs'
    New-Item -ItemType Directory -Force -Path $cfgDir, $saveDir, $bdHome, $bugs | Out-Null
    Set-Content -LiteralPath (Join-Path $cfgDir 'config.json') -Value '{ }' -Encoding UTF8
    $game = Join-Path $WorkDir (Split-Path $Rom -Leaf); Copy-Item -LiteralPath $Rom -Destination $game
    $tfRoot = Join-Path $bdHome 'timeflow'
    # no sound from the test instance (the user's speakers stay quiet): the app behaves as with no playback device, paced by its clock
    $noDev = Join-Path $WorkDir 'audio-device.txt'; Set-Content -LiteralPath $noDev -Value 'none' -Encoding ASCII
    $env:BROKENNES_CONFIG_DIR = $cfgDir; $env:BROKENNES_SAVES_DIR = $saveDir; $env:BROKENDEV_HOME = $bdHome; $env:BROKENNES_BUGS_DIR = $bugs; $env:BROKENNES_TEST_AUDIO_DEVICE_FILE = $noDev
    $inst = Start-BrokenNesInstance -ExePath $Exe; $id = $inst.ProcessId
    Api $id POST '/api/emulator/load-rom' @{ path = $game } | Out-Null
    Start-Sleep -Seconds 2
    Api $id POST '/api/input/set-buttons' @{ player = 1; buttons = @('Start'); holdMs = 150 } | Out-Null

    # ---------------------------------------------------------------- OFF
    Play $id 6
    $s0 = Api $id GET '/api/timeflow/status'
    Check 'off_by_default' (-not $s0.recording -and -not $s0.timeline) "recording $($s0.recording) after 6 s of play (the rule: $($s0.rule))"
    Check 'off_writes_nothing' ((Files $tfRoot) -eq 0) "$(Files $tfRoot) files under $tfRoot"
    $off1 = Frames $id; Start-Sleep -Seconds 8; $offFrames = (Frames $id) - $off1

    # ---------------------------------------------------------------- NO SWITCH for agents
    Check 'no_route_switches_on' ((NotFound $id '/api/timeflow/enabled') -and (NotFound $id '/api/timeflow/start')) "POST /api/timeflow/enabled and /api/timeflow/start answer 404"
    Check 'no_route_switches_on_status' (-not (Api $id GET '/api/timeflow/status').recording) "still off after those calls"

    # ---------------------------------------------------------------- DECLINE
    $r1 = Api $id POST '/api/timeflow/request' @{ reason = 'the smoke test asks (decline it)'; from = 'the timeflow smoke' }
    $main = Main $id
    $prompt = WaitFor { FindById $main 'timeflowPrompt' }
    $ptext = if ($prompt) { TextOf (FindById $main 'timeflowPromptText') } else { '' }
    Check 'request_shows_prompt' ($r1.request -eq 'pending' -and $prompt -and $ptext -like '*the timeflow smoke asks*') "request $($r1.request); prompt: $ptext"
    $f1 = Frames $id; Start-Sleep -Seconds 3; $f2 = Frames $id
    Check 'prompt_does_not_stop_the_game' (($f2 - $f1) -gt 100) "$($f2 - $f1) frames in 3 s while the prompt shows"
    Check 'request_records_nothing' (-not (Api $id GET '/api/timeflow/status').recording -and (Files $tfRoot) -eq 0) "while the request waits: recording off, $(Files $tfRoot) files"
    Invoke-El (FindById $main 'timeflowDecline'); Start-Sleep -Milliseconds 800
    $s1 = Api $id GET '/api/timeflow/status'
    Play $id 3
    Check 'decline_records_nothing' ($s1.request.state -eq 'declined' -and -not (Api $id GET '/api/timeflow/status').recording -and (Files $tfRoot) -eq 0 -and -not (FindById $main 'timeflowPrompt')) "request $($s1.request.state); recording off; $(Files $tfRoot) files; the prompt is gone"

    # ---------------------------------------------------------------- ALLOW
    $r2 = Api $id POST '/api/timeflow/request' @{ reason = 'the smoke test asks (allow it)'; from = 'the timeflow smoke' }
    $allow = WaitFor { FindById $main 'timeflowAllow' }
    Invoke-El $allow; Start-Sleep -Milliseconds 800
    $on1 = Frames $id
    Play $id 12
    $onFrames = [long]((Frames $id) - $on1) * 8 / 12
    $s2 = Api $id GET '/api/timeflow/status'
    $title = (Get-Process -Id $id).MainWindowTitle
    $mark = WaitFor { @($main.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) | Where-Object { $_.Current.Name -like '*REC*of*GB*' } | Select-Object -First 1 }
    $markText = if ($mark) { $mark.Current.Name } else { '' }
    Check 'allow_records' ($s2.recording -and $s2.timeline -and $s2.frames -gt 300 -and $s2.request.state -eq 'allowed' -and $s2.origin -like '*allowed*') "recording $($s2.recording) ($($s2.origin)): timeline $($s2.timeline), $($s2.frames) frames, $($s2.storedBytes) bytes written, the folder $($s2.gameBytes) of $($s2.capBytes)"
    Check 'rec_mark_shows' ($title -like '*`[REC`]*' -and $markText -like '*REC*MB*') "title '$title'; the menu bar mark '$markText'"
    Check 'timeline_in_brokendev_home' ($s2.dir -and $s2.dir.StartsWith($tfRoot, [StringComparison]::OrdinalIgnoreCase) -and (Files $tfRoot) -gt 0) "$($s2.dir)"
    $r3 = Api $id POST '/api/timeflow/request' @{ reason = 'again'; from = 'the timeflow smoke' }
    Check 'request_while_on' ($r3.request -eq 'not needed') "a request while recording: $($r3.request)"

    # ---------------------------------------------------------------- OFF AGAIN (the user's REC box)
    $win = Open-Timeflow $id
    $rec = FindById $win 'timeflowRec'
    $rec.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Seconds 2
    $s3 = Api $id GET '/api/timeflow/status'
    $tdir = $s2.dir
    $meta = Get-Content -LiteralPath (Join-Path $tdir 'timeline.json') -Raw | ConvertFrom-Json
    $title2 = (Get-Process -Id $id).MainWindowTitle
    Check 'switch_off' (-not $s3.recording -and $meta.status -eq 'closed' -and $meta.frames -gt 300 -and $title2 -notlike '*`[REC`]*') "recording $($s3.recording); the timeline $($meta.id): $($meta.status), $($meta.frames) frames, $($meta.verifiedFrames) checked while recording ($($meta.verifyFailures) failed); title '$title2'"
    $n1 = Files $tfRoot; Play $id 3; $n2 = Files $tfRoot
    Check 'off_again_writes_nothing' ($n1 -eq $n2) "$n1 files, then $n2 after 3 s more"

    # ---------------------------------------------------------------- PROOF (Workshop on the desktop's timeline)
    $v = Workshop @('--timeflow', 'verify', '--timeline', $tdir, '--every', '7', '--machine', '--json')
    Check 'restore_equals_record' ($v.Code -eq 0 -and $v.Json.failed -eq 0 -and $v.Json.machineFailed -eq 0 -and $v.Json.checked -gt 40) "$($v.Json.restoreEqualsRecord) of $($v.Json.checked) frames restored equal the record; $($v.Json.machineChecked - $v.Json.machineFailed) of $($v.Json.machineChecked) load into a machine $(if ($v.Code -ne 0) { $v.Text })"
    $mid = [long]($meta.startFrame + [math]::Floor($meta.frames / 2))
    $rp = Workshop @('--timeflow', 'resume', '--timeline', $tdir, '--frame', "$mid", '--replay', '--json')
    Check 'replay_in_sync' ($rp.Code -eq 0 -and $rp.Json.inSync) "replay from $mid to the end: in sync $($rp.Json.inSync) ($($rp.Json.framesRun) frames, $($rp.Json.checks) checks) $(if (-not $rp.Json.inSync) { 'off at ' + $rp.Json.offFrame + ' ' + $rp.Json.offWhat })"

    # ---------------------------------------------------------------- WINDOW: jump and replay
    $list = FindById $win 'timeflowTimelines'
    $items = @($list.FindAll($TS::Children, (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::ListItem))) | ForEach-Object { $_.Current.Name })
    Check 'window_lists_timeline' (@($items | Where-Object { $_ -like "$($meta.id)*" }).Count -eq 1) "$($items.Count) item(s): $($items -join ' | ')"
    $jf = [long]($meta.startFrame + 200)
    $spin = FindById $win 'timeflowFrame'
    $rv = $null
    if ($spin.TryGetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern, [ref]$rv)) { $rv.SetValue([double]$jf) }
    else { SetText ($spin.FindFirst($TS::Descendants, (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Edit)))) "$jf" }
    Start-Sleep -Milliseconds 400
    Invoke-El (FindById $win 'timeflowJump')
    $jr = WaitFor { $t = TextOf (FindById $win 'timeflowResult'); if ($t -like 'at frame*' -or $t -like '*refused*') { $t } } 10000
    $p1 = Frames $id; Start-Sleep -Seconds 1; $p2 = Frames $id
    Check 'window_jump' ($jr -like "at frame $jf of $($meta.id)*" -and ($p2 - $p1) -le 1) "'$jr'; paused there ($($p2 - $p1) frames in 1 s)"
    Invoke-El (FindById $win 'timeflowReplay')
    $end = [long]($meta.startFrame + $meta.frames)
    $st = WaitFor { $x = Api $id GET '/api/timeflow/status'; if (-not $x.replay -and $x.lastReplay) { $x } } 30000
    Check 'window_replay_in_sync' ($st -and $st.lastReplay -like '*in sync*') "$(if ($st) { $st.lastReplay } else { 'the replay did not finish in 30 s' })"

    # ---------------------------------------------------------------- COST
    Check 'cost_holds_60fps' ($offFrames -ge 440 -and $onFrames -ge 440) "frames in 8 s: $offFrames off, about $onFrames recording (60 fps = 480)"

    $result.pass = -not ($checks.Values | Where-Object { -not $_.ok })
}
catch { Check 'run' $false ($_.Exception.Message + ' ' + $_.ScriptStackTrace) }
finally {
    if ($inst) { Stop-BrokenNesInstance -ProcessId $inst.ProcessId | Out-Null }
    foreach ($k in $saved.Keys) { Set-Item -Path "env:$k" -Value $saved[$k] -ErrorAction SilentlyContinue; if (-not $saved[$k]) { Remove-Item -Path "env:$k" -ErrorAction SilentlyContinue } }
}
$after = Listing $userDir
Check 'user_appdata_unchanged' ($before -eq $after) "the user's $userDir $(if ($before -eq $after) { 'is unchanged' } else { 'CHANGED' })"
if ($before -ne $after) { $result.pass = $false }
$result.checks = $checks
$result | ConvertTo-Json -Compress -Depth 5
if ($result.pass) { exit 0 } else { exit 1 }
