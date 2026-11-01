<#
.SYNOPSIS
  BrokenDev Timeflow T4 (search) in the real desktop app: the Timeflow window's search box searches the running game's timelines on headless
  machines while the user's game plays on; the hits are listed and marked on the scrubber, and a hit puts the scrubber at its frame (a jump
  target). Exit 0 = pass. Prints ONE JSON result line as the last line of stdout.

.DESCRIPTION
  Starts the shipped WinForms app with scratch BROKENNES_CONFIG_DIR, BROKENNES_SAVES_DIR, BROKENNES_BUGS_DIR and BROKENDEV_HOME (the user's
  config, saves and BrokenDev home are never read or written: a listing of %APPDATA%\BrokenNes before and after proves it). The game is T4's
  search test game (Workshop --timeflow search-rom: an enemy alive but not drawn, planted, with its .mlb beside it); its timeline is recorded
  headless by the Workshop with the search index (an agent's own machine, never the user's play).
    SEARCH     "exec hide_enemy": the 16 planted frames (33-40, 161-168), at hide_enemy's first instruction; the hit list and GET /api/timeflow/search
    HIT        choosing the first hit puts the window's frame at 33 (the scrubber's hit strip is drawn)
    EXAMPLE    the user's example "state [enemy_alive] != 0 and not color #982220": the alive-but-not-drawn frames (36 among them, 20 not)
    NEGATIVE   "exec never_called": 0 hits; "enemy alive" (not a condition): refused with the language
    MEANWHILE  the game ran on during the searches; the app's own debugger stayed detached and unarmed
  Rule zero (UAT\README.md): never kill by process name; only the PIDs started here are touched.

.EXAMPLE
  pwsh -File UAT\entrypoints\timeflow-search-desktop-smoke.ps1 -Exe C:\path\BrokenNes.Windows.exe -WorkshopExe C:\path\BrokenNes.Workshop.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$WorkshopExe,
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\uat_timeflow_search_desktop')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$checks = [ordered]@{}
$result = [ordered]@{ entrypoint = 'timeflow-search-desktop'; pass = $false }

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
function Items($el) { @($el.FindAll($TS::Children, (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::ListItem)))) }
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
function Search($win, [int]$procId, [string]$cond) {
    SetText (FindById $win 'timeflowSearchText') $cond
    Invoke-El (FindById $win 'timeflowSearch')
    $msg = WaitFor { $t = TextOf (FindById $win 'timeflowResult'); if ($t -like '*hit(s)*' -or $t -like '*refused*' -or $t -like '*does not read*') { $t } } 120000
    $st = WaitFor { $s = Api $procId GET '/api/timeflow/search'; if (-not $s.busy) { $s } } 30000
    [pscustomobject]@{ Message = $msg; Status = $st }
}

$inst = $null
$saved = @{ BROKENNES_CONFIG_DIR = $env:BROKENNES_CONFIG_DIR; BROKENNES_SAVES_DIR = $env:BROKENNES_SAVES_DIR; BROKENDEV_HOME = $env:BROKENDEV_HOME; BROKENNES_BUGS_DIR = $env:BROKENNES_BUGS_DIR; BROKENNES_TEST_AUDIO_DEVICE_FILE = $env:BROKENNES_TEST_AUDIO_DEVICE_FILE }
$userDir = Join-Path $env:APPDATA 'BrokenNes'
$before = Listing $userDir
try {
    . (Join-Path $repo 'UAT\lib\ApiClient.ps1')
    foreach ($r in $Exe, $WorkshopExe) { if (-not (Test-Path -LiteralPath $r)) { throw "missing $r" } }
    if (Test-Path -LiteralPath $WorkDir) { Remove-Item -LiteralPath $WorkDir -Recurse -Force }
    $cfgDir = Join-Path $WorkDir 'config'; $saveDir = Join-Path $WorkDir 'saves'; $bdHome = Join-Path $WorkDir 'bdhome'; $bugs = Join-Path $WorkDir 'bugs'
    New-Item -ItemType Directory -Force -Path $cfgDir, $saveDir, $bdHome, $bugs | Out-Null
    Set-Content -LiteralPath (Join-Path $cfgDir 'config.json') -Value '{ }' -Encoding UTF8
    $noDev = Join-Path $WorkDir 'audio-device.txt'; Set-Content -LiteralPath $noDev -Value 'none' -Encoding ASCII
    $env:BROKENNES_CONFIG_DIR = $cfgDir; $env:BROKENNES_SAVES_DIR = $saveDir; $env:BROKENDEV_HOME = $bdHome; $env:BROKENNES_BUGS_DIR = $bugs; $env:BROKENNES_TEST_AUDIO_DEVICE_FILE = $noDev
    $game = Join-Path $WorkDir 'enemy.nes'; $mlb = Join-Path $WorkDir 'enemy.mlb'
    $w = Workshop @('--timeflow', 'search-rom', '--out', $game)
    Check 'search_game_written' ($w.Code -eq 0 -and (Test-Path -LiteralPath $game) -and (Test-Path -LiteralPath $mlb)) "$game and its .mlb"

    # the timeline: an agent's own headless recording with the search index (the user's play is not recorded here)
    $rec = Workshop @('--timeflow', 'record', '--rom', $game, '--frames', '200', '--sym', $mlb, '--coverage', '--accesses', '--checks', '--json')
    $tid = [string]$rec.Json.id
    Check 'timeline_recorded_headless_with_the_index' ($rec.Code -eq 0 -and $rec.Json.frames -eq 200 -and $rec.Json.coverage -and $rec.Json.accesses) "$tid, $($rec.Json.frames) frames, coverage $($rec.Json.coverage), accesses $($rec.Json.accesses)"

    $inst = Start-BrokenNesInstance -ExePath $Exe; $id = $inst.ProcessId
    Api $id POST '/api/emulator/load-rom' @{ path = $game } | Out-Null; Start-Sleep -Seconds 2
    $win = Open-Timeflow $id
    $names = @(Items (FindById $win 'timeflowTimelines') | ForEach-Object { $_.Current.Name })
    Check 'window_lists_the_timeline' (@($names | Where-Object { $_ -like "$tid*" }).Count -eq 1) "$($names -join ' | ')"

    $f1 = Frames $id
    $s = Search $win $id 'exec hide_enemy'
    $f2 = Frames $id
    $rep = $s.Status.report
    $frames = @($rep.hits | ForEach-Object { [long]$_.frame })
    Check 'search_exec_finds_the_planted_frames' ($s.Message -like '16 hit(s)*' -and ($frames -join ',') -eq '33,34,35,36,37,38,39,40,161,162,163,164,165,166,167,168' -and @($rep.hits)[0].in -eq 'hide_enemy' -and @($rep.hits)[0].via -eq 're-run') "'$($s.Message)'; frames $($frames -join ','); first in $(@($rep.hits)[0].in) at $(@($rep.hits)[0].pc) cycle $(@($rep.hits)[0].cycle)"
    $hl = @(Items (FindById $win 'timeflowHits'))
    Check 'hits_listed' ($hl.Count -eq 16 -and $hl[0].Current.Name -like "*frame     33*") "$($hl.Count) items; first: $(if ($hl.Count) { $hl[0].Current.Name })"
    Check 'the_game_ran_on_meanwhile' (($f2 - $f1) -gt 5) "$($f2 - $f1) frames of the user's game while the search ran"
    if ($hl.Count -gt 0) { $hl[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 700 }
    $spin = FindById $win 'timeflowFrame'
    $edit = $spin.FindFirst($TS::Descendants, (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Edit)))
    $fr = if ($edit) { TextOf $edit } else { TextOf $spin }
    $res = TextOf (FindById $win 'timeflowResult')
    Check 'a_hit_is_a_jump_target' ($fr -eq '33' -and $res -like 'hit: timeline*frame 33*Jump*') "frame box '$fr'; '$res'"
    Check 'hit_strip_present' ([bool](FindById $win 'timeflowHitStrip')) 'the strip under the scrubber that marks the hits'

    $u = Search $win $id 'state [enemy_alive] != 0 and not color #982220'
    $uf = @($u.Status.report.hits | ForEach-Object { [long]$_.frame })
    Check 'the_users_example' (($uf -contains 36) -and ($uf -contains 40) -and -not ($uf -contains 20) -and -not ($uf -contains 100) -and @($uf | Select-Object -Unique).Count -eq $uf.Count) "'$($u.Message)'; frames $($uf -join ',') (each once)"
    $n = Search $win $id 'exec never_called'
    Check 'never_holds_0_hits' ($n.Message -like '0 hit(s)*' -and $n.Status.report.totalHits -eq 0) "'$($n.Message)'"
    $b = Search $win $id 'enemy alive'
    Check 'bad_condition_refused' ($b.Message -like '*does not read*unknown clause*') "'$($b.Message)'"
    $ds = Api $id GET '/api/debug/state'
    Check 'the_apps_debugger_untouched' (-not $ds.attached -and -not $ds.armed) "the app's own debugger: attached $($ds.attached), armed $($ds.armed)"
    Stop-BrokenNesInstance -ProcessId $id | Out-Null; $inst = $null
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
