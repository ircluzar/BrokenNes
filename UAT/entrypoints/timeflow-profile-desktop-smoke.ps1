<#
.SYNOPSIS
  BrokenDev Timeflow T3 (profiled spans) in the real desktop app: the Timeflow window's "Profile selection" re-runs a span on a headless machine
  while the user's game plays on, keeps it in the game's profiles folder, and opens it in the Debugger window and the Event View (profile mode);
  an unkept profile is gone when the app closes, a kept one stays, a killed app's profile is swept at the next start. Exit 0 = pass. Prints ONE
  JSON result line as the last line of stdout.

.DESCRIPTION
  Starts the shipped WinForms app with scratch BROKENNES_CONFIG_DIR, BROKENNES_SAVES_DIR, BROKENNES_BUGS_DIR and BROKENDEV_HOME (the user's
  config, saves and BrokenDev home are never read or written: a listing of %APPDATA%\BrokenNes before and after proves it). The timeline is
  recorded headless by the Workshop (an agent's own machine), on the same ROM the app runs (its symbols beside it).
    PROFILE    the Timeflow window lists the timeline; frame and span typed, Profile selection: the result says in sync, the profile is in
               BROKENDEV_HOME\timeflow\...\profiles; the game kept running meanwhile; the app's own debugger stays detached and unarmed.
    WINDOWS    the Debugger window (profile mode) shows the frame's trace, a line's registers and call stack; the Event View (profile mode)
               shows the frame's register writes; the frame scrubber moves both to another frame.
    KEEP       a second profile with "keep after the app closes".
    CLOSE      the app closes (its main window): the unkept profile is gone, the kept one stays.
    CRASH      the app again, a profile, then the process killed (by its PID): the profile stays; the app again: its start-up sweep removes it.
  Rule zero (UAT\README.md): never kill by process name; only the PIDs started here are touched.

.EXAMPLE
  pwsh -File UAT\entrypoints\timeflow-profile-desktop-smoke.ps1 -Exe C:\path\BrokenNes.Windows.exe -WorkshopExe C:\path\BrokenNes.Workshop.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$WorkshopExe,
    [string]$Rom = 'P:\BrokenDev\env\out\vrun-nes\vrun_game.nes',
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\uat_timeflow_profile_desktop')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$checks = [ordered]@{}
$result = [ordered]@{ entrypoint = 'timeflow-profile-desktop'; pass = $false }

function Check($name, [bool]$ok, $detail) {
    $script:checks[$name] = [ordered]@{ ok = $ok; detail = "$detail" }
    Write-Host ("[{0}] {1} - {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}
function Api([int]$procId, [string]$method, [string]$path, $body = $null) { Invoke-BrokenNesApi -ProcessId $procId -Method $method -Path $path -Body $body -TimeoutSec 60 }
function Listing([string]$dir) {
    if (-not (Test-Path -LiteralPath $dir)) { return '' }
    (@(Get-ChildItem -LiteralPath $dir -Recurse -File -Force -ErrorAction SilentlyContinue | Sort-Object FullName | ForEach-Object { "$($_.FullName.Substring($dir.Length))|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" })) -join "`n"
}
function Frames([int]$procId) { $d = Api $procId GET '/api/audio/device'; $n = if ($null -ne $d.device) { $d.device.framesEmulated } else { $d.framesEmulated }; [long]$n }
function Workshop([string[]]$a) {
    $out = Join-Path $WorkDir ("ws_" + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.txt')
    $p = Start-Process -FilePath $WorkshopExe -ArgumentList $a -Wait -PassThru -NoNewWindow -RedirectStandardOutput $out -RedirectStandardError "$out.err"
    $t = (Get-Content -LiteralPath $out -Raw) + (Get-Content -LiteralPath "$out.err" -Raw)
    $j = $null; $i = $t.IndexOf('{'); $e = $t.LastIndexOf('}'); if ($i -ge 0 -and $e -gt $i) { try { $j = $t.Substring($i, $e - $i + 1) | ConvertFrom-Json } catch { } }
    [pscustomobject]@{ Code = $p.ExitCode; Json = $j; Text = $t }
}
function ProfileDirs([string]$root) { @(Get-ChildItem -LiteralPath $root -Recurse -Directory -Filter 'profiles' -ErrorAction SilentlyContinue | ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Directory } | ForEach-Object { $_.FullName }) }

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
function SetSpin($win, [string]$id, [double]$v) {
    $spin = FindById $win $id
    $rv = $null
    if ($spin.TryGetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern, [ref]$rv)) { $rv.SetValue($v) }
    else { SetText ($spin.FindFirst($TS::Descendants, (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Edit)))) "$v" }
    Start-Sleep -Milliseconds 300
}
function Items($el) { @($el.FindAll($TS::Children, (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::ListItem)))) }
function WindowOf([int]$procId, [string]$like) {
    # owned windows may sit under the main window in the UI Automation tree: look among the process's windows at any depth
    $cond = New-Object System.Windows.Automation.AndCondition((Cond $AE::ProcessIdProperty $procId), (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Window)))
    WaitFor { @($AE::RootElement.FindAll($TS::Descendants, $cond)) | Where-Object { $_.Current.Name -like $like } | Select-Object -First 1 } 15000
}
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
    Start-Sleep -Milliseconds 900
    return $w
}
function Start-App { $i = Start-BrokenNesInstance -ExePath $Exe; Api $i.ProcessId POST '/api/emulator/load-rom' @{ path = $game } | Out-Null; Start-Sleep -Seconds 2; return $i }
function Profile-Selection($win, [long]$from, [int]$n, [bool]$keep) {
    SetSpin $win 'timeflowFrame' $from
    SetSpin $win 'timeflowProfileFrames' $n
    $kb = FindById $win 'timeflowProfileKeep'
    $tp = $kb.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if (($tp.Current.ToggleState -eq 'On') -ne $keep) { $tp.Toggle() }
    Invoke-El (FindById $win 'timeflowProfile')
    WaitFor { $t = TextOf (FindById $win 'timeflowResult'); if ($t -like 'profiled frames*' -or $t -like '*refused*') { $t } } 120000
}

$inst = $null; $inst2 = $null; $inst3 = $null
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
    $mlb = [IO.Path]::ChangeExtension($Rom, '.mlb'); if (Test-Path -LiteralPath $mlb) { Copy-Item -LiteralPath $mlb -Destination ([IO.Path]::ChangeExtension($game, '.mlb')) }
    $tfRoot = Join-Path $bdHome 'timeflow'
    $noDev = Join-Path $WorkDir 'audio-device.txt'; Set-Content -LiteralPath $noDev -Value 'none' -Encoding ASCII
    $env:BROKENNES_CONFIG_DIR = $cfgDir; $env:BROKENNES_SAVES_DIR = $saveDir; $env:BROKENDEV_HOME = $bdHome; $env:BROKENNES_BUGS_DIR = $bugs; $env:BROKENNES_TEST_AUDIO_DEVICE_FILE = $noDev

    # the timeline: an agent's own headless recording of the same ROM (the user's play is not recorded here)
    $rec = Workshop @('--timeflow', 'record', '--rom', $game, '--frames', '400', '--input', 'auto:1', '--json')
    $tid = [string]$rec.Json.id
    Check 'timeline_recorded_headless' ($rec.Code -eq 0 -and $rec.Json.frames -eq 400) "$tid, $($rec.Json.frames) frames in $($rec.Json.dir)"

    $inst = Start-App; $id = $inst.ProcessId
    $win = Open-Timeflow $id
    $list = FindById $win 'timeflowTimelines'
    $names = @(Items $list | ForEach-Object { $_.Current.Name })
    Check 'window_lists_the_timeline' (@($names | Where-Object { $_ -like "$tid*" }).Count -eq 1) "$($names -join ' | ')"
    $f1 = Frames $id
    $msg = Profile-Selection $win 200 30 $false
    $f2 = Frames $id
    $st = Api $id GET '/api/timeflow/profile'
    $pdir = [string]$st.profileDir
    Check 'profile_selection' ($msg -like 'profiled frames 200-229*in sync with the recording*' -and $pdir -and $pdir.StartsWith($tfRoot, [StringComparison]::OrdinalIgnoreCase) -and $pdir -like '*\profiles\*' -and (Test-Path -LiteralPath $pdir)) "'$msg'; $pdir"
    Check 'the_game_ran_on_meanwhile' (($f2 - $f1) -gt 30) "$($f2 - $f1) frames of the user's game while the profile ran"
    $ds = Api $id GET '/api/debug/state'
    Check 'the_apps_debugger_untouched' (-not $ds.attached -and -not $ds.armed) "the app's own debugger: attached $($ds.attached), armed $($ds.armed) (the profile ran on its own headless machine)"
    Check 'profile_windows_open' (@($st.windows) -contains 'DebuggerWindowProfile' -and @($st.windows) -contains 'EventsWindowProfile') "windows: $(@($st.windows) -join ', ')"

    # the Debugger window in profile mode
    $dw = WindowOf $id 'BrokenNes Debugger: profile*'
    $trace = WaitFor { $t = FindById $dw 'profileTrace'; if ($t -and (Items $t).Count -gt 100) { $t } } 15000
    $tlines = if ($trace) { @(Items $trace) } else { @() }
    Check 'debugger_shows_the_trace' ($tlines.Count -gt 1000 -and $tlines[0].Current.Name -like '*Fr:200*') "$($tlines.Count) lines of frame 200; first: $(if ($tlines.Count) { $tlines[0].Current.Name })"
    if ($tlines.Count -gt 500) { $tlines[500].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 800 }
    $regs = TextOf (FindById $dw 'profileRegisters')
    $stk = @(Items (FindById $dw 'profileStack') | ForEach-Object { $_.Current.Name })
    Check 'debugger_instruction_registers_and_stack' ($regs -like '*A:*X:*Y:*' -and $stk.Count -ge 1) "registers: $($regs -replace "`r?`n", ' / '); stack: $($stk -join ' | ')"
    SetSpin $dw 'profileFrame' 215
    # (a label's UI Automation name is its AccessibleName, not its text: the first trace line says which frame is shown)
    $first215 = WaitFor { $t = FindById $dw 'profileTrace'; $f = $t.FindFirst($TS::Children, (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::ListItem))); if ($f -and $f.Current.Name -like '*Fr:215*') { $f.Current.Name } } 20000
    Check 'debugger_frame_scrubber' ([bool]$first215) "frame 215's first line: $first215"

    # the Event View in profile mode
    $ew = WindowOf $id 'BrokenNes Events: profile*'
    $ev = WaitFor { $l = @(Items (FindById $ew 'profileEvents')); if ($l.Count -gt 0) { $l } } 10000
    $einfo = TextOf (FindById $ew 'profileEventsInfo')
    Check 'events_show_the_frame' ($ev -and @($ev | Where-Object { $_.Current.Name -like '*write $20*' }).Count -gt 0 -and $einfo -like '*frame 200*') "$(if ($ev) { $ev.Count } else { 0 }) lines; $einfo"
    SetSpin $ew 'profileEventsFrame' 220
    $einfo2 = WaitFor { $t = TextOf (FindById $ew 'profileEventsInfo'); if ($t -like '*frame 220*') { $t } }
    Check 'events_frame_scrubber' ([bool]$einfo2) "$einfo2"

    # keep: a second profile kept after the app closes
    $msg2 = Profile-Selection $win 300 10 $true
    $st2 = Api $id GET '/api/timeflow/profile'
    $kdir = [string]$st2.profileDir
    Check 'profile_kept' ($msg2 -like '*kept after the app closes*' -and (Test-Path -LiteralPath $kdir) -and $kdir -ne $pdir) "'$msg2'"
    Check 'first_profile_still_there_while_open' (Test-Path -LiteralPath $pdir) "the unkept one lives as long as the app: $pdir"

    # close the app: the unkept one goes, the kept one stays
    Stop-BrokenNesInstance -ProcessId $id | Out-Null; $inst = $null
    Start-Sleep -Milliseconds 800
    Check 'app_close_deletes_unkept' (-not (Test-Path -LiteralPath $pdir) -and (Test-Path -LiteralPath $kdir)) "unkept $(if (Test-Path -LiteralPath $pdir) { 'STILL THERE' } else { 'gone' }); kept $(if (Test-Path -LiteralPath $kdir) { 'stays' } else { 'GONE' })"

    # a killed app: its profile stays until the next start's sweep
    $inst2 = Start-App; $id2 = $inst2.ProcessId
    $win2 = Open-Timeflow $id2
    $msg3 = Profile-Selection $win2 100 5 $false
    $cdir = [string](Api $id2 GET '/api/timeflow/profile').profileDir
    Stop-Process -Id $id2 -Force; $inst2 = $null
    Start-Sleep -Milliseconds 800
    Check 'killed_app_leaves_its_profile' ($msg3 -like 'profiled frames*' -and (Test-Path -LiteralPath $cdir)) "$cdir after the app was killed"
    $inst3 = Start-App; $id3 = $inst3.ProcessId
    $gone = WaitFor { -not (Test-Path -LiteralPath $cdir) } 15000
    Check 'next_start_sweeps_it' ($gone -and (Test-Path -LiteralPath $kdir)) "killed app's profile $(if (Test-Path -LiteralPath $cdir) { 'STILL THERE' } else { 'swept' }); the kept one $(if (Test-Path -LiteralPath $kdir) { 'stays' } else { 'GONE' })"
    Stop-BrokenNesInstance -ProcessId $id3 | Out-Null; $inst3 = $null

    # drop the kept one: nothing left
    $dr = Workshop @('--timeflow', 'profile-drop', '--all', '--json')
    $left = ProfileDirs $tfRoot
    Check 'nothing_left' ($dr.Code -eq 0 -and $left.Count -eq 0) "$($left.Count) profile folder(s) left $($left -join ', ')"

    $result.pass = -not ($checks.Values | Where-Object { -not $_.ok })
}
catch { Check 'run' $false ($_.Exception.Message + ' ' + $_.ScriptStackTrace) }
finally {
    foreach ($i in @($inst, $inst2, $inst3)) { if ($i) { Stop-BrokenNesInstance -ProcessId $i.ProcessId | Out-Null } }
    foreach ($k in $saved.Keys) { Set-Item -Path "env:$k" -Value $saved[$k] -ErrorAction SilentlyContinue; if (-not $saved[$k]) { Remove-Item -Path "env:$k" -ErrorAction SilentlyContinue } }
}
$after = Listing $userDir
Check 'user_appdata_unchanged' ($before -eq $after) "the user's $userDir $(if ($before -eq $after) { 'is unchanged' } else { 'CHANGED' })"
if ($before -ne $after) { $result.pass = $false }
$result.checks = $checks
$result | ConvertTo-Json -Compress -Depth 5
if ($result.pass) { exit 0 } else { exit 1 }
