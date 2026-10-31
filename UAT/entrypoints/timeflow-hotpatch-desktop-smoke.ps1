<#
.SYNOPSIS
  BrokenDev Timeflow T5 (hotpatch and resume) in the real desktop app: the Timeflow window's Patch + resume applies a byte patch from the chosen
  frame of a timeline on headless machines while the user's game plays on, replays the recorded inputs on the patched ROM (recorded as a branch),
  and shows the divergence and the verdict of the test written beside it. Exit 0 = pass. Prints ONE JSON result line as the last line of stdout.

.DESCRIPTION
  Starts the shipped WinForms app with scratch BROKENNES_CONFIG_DIR, BROKENNES_SAVES_DIR, BROKENNES_BUGS_DIR and BROKENDEV_HOME (the user's
  config, saves and BrokenDev home are never read or written: a listing of %APPDATA%\BrokenNes before and after proves it). The game is T5's
  test game (Workshop --timeflow hotpatch-rom: T3's planted CPX bug, with its .mlb beside it); its timeline is recorded headless by the Workshop.
    PATCH      frame 10, "ft_limit+1=21>10", verdict "never write hp by fill_table": PASS on the patched future, FAIL on the old one; the divergence
               (the CPUs part at ft_limit); the branch listed under its parent; GET /api/timeflow/hotpatch has the report
    NEGATIVE   a wrong old byte ("ft_limit+1=10>21") is refused, nothing applied, no branch; a malformed patch is refused
    MEANWHILE  the game ran on during the hotpatch; the app's own debugger stayed detached and unarmed
  Rule zero (UAT\README.md): never kill by process name; only the PIDs started here are touched.

.EXAMPLE
  pwsh -File UAT\entrypoints\timeflow-hotpatch-desktop-smoke.ps1 -Exe C:\path\BrokenNes.Windows.exe -WorkshopExe C:\path\BrokenNes.Workshop.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$WorkshopExe,
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\uat_timeflow_hotpatch_desktop')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$checks = [ordered]@{}
$result = [ordered]@{ entrypoint = 'timeflow-hotpatch-desktop'; pass = $false }

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
    $main = WaitFor { Main $procId } 20000
    $emu = WaitFor { FindByName $main 'Emulator' } 20000
    if (-not $emu) { throw 'the Emulator menu was not found (the main window was not ready)' }
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
function Patch($win, [int]$procId, [string]$patch, [string]$verdict) {
    SetText (FindById $win 'timeflowPatchText') $patch
    SetText (FindById $win 'timeflowVerdictText') $verdict
    Invoke-El (FindById $win 'timeflowPatch')
    $msg = WaitFor { $t = TextOf (FindById $win 'timeflowPatchResult'); if ($t -like 'patched at*' -or $t -like '*REFUSED*' -or $t -like '*refused*' -or $t -like '*not a byte patch*' -or $t -like '*must be as many*') { $t } } 180000
    $st = WaitFor { $s = Api $procId GET '/api/timeflow/hotpatch'; if (-not $s.busy) { $s } } 30000
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
    $game = Join-Path $WorkDir 't5.nes'; $mlb = Join-Path $WorkDir 't5.mlb'
    $w = Workshop @('--timeflow', 'hotpatch-rom', '--build', 'planted', '--out', $game)
    Check 'test_game_written' ($w.Code -eq 0 -and (Test-Path -LiteralPath $game) -and (Test-Path -LiteralPath $mlb)) "$game and its .mlb"
    $rec = Workshop @('--timeflow', 'record', '--rom', $game, '--frames', '160', '--sym', $mlb, '--coverage', '--accesses', '--json')
    $tid = [string]$rec.Json.id
    Check 'timeline_recorded_headless' ($rec.Code -eq 0 -and $rec.Json.frames -eq 160) "$tid, $($rec.Json.frames) frames"

    $inst = Start-BrokenNesInstance -ExePath $Exe; $id = $inst.ProcessId
    Api $id POST '/api/emulator/load-rom' @{ path = $game } | Out-Null; Start-Sleep -Seconds 2
    $win = Open-Timeflow $id
    $tl = FindById $win 'timeflowTimelines'
    $items = @(Items $tl)
    $mine = @($items | Where-Object { $_.Current.Name -like "$tid*" })
    Check 'window_lists_the_timeline' ($mine.Count -eq 1) "$(@($items | ForEach-Object { $_.Current.Name }) -join ' | ')"
    if ($mine.Count -eq 1) { $mine[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 500 }
    $spin = FindById $win 'timeflowFrame'
    $edit = $spin.FindFirst($TS::Descendants, (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Edit)))
    if ($edit) { SetText $edit '10' } else { SetText $spin '10' }
    Start-Sleep -Milliseconds 400

    $f1 = Frames $id
    $p = Patch $win $id 'ft_limit+1=21>10' 'never write hp by fill_table'
    $f2 = Frames $id
    $rep = $p.Status.report
    $v = @($rep.verdicts)[0]
    Check 'patch_resumes_and_the_verdict_passes' ($p.Message -like 'patched at frame 10*' -and $rep.resumed -and $v.pass -and $v.verdict -eq 'PASS') "'$($p.Message)'"
    Check 'the_verdict_fails_on_the_old_future' ($v.old -and -not $v.old.pass -and ([string]$v.old.evidence) -match 'ft_store') "old future: $($v.old.verdict) ($($v.old.evidence))"
    Check 'the_divergence_shown' ($rep.divergence.diverged -and $rep.divergence.firstInstruction.pcSymbolOld -eq 'ft_limit' -and $p.Message -like '*diverges*') "$($rep.divergence.summary)"
    $branchShown = WaitFor { $n = @(Items (FindById $win 'timeflowTimelines') | Where-Object { $_.Current.Name -like "*branch of $tid at 10*" }); if ($n.Count -ge 1) { $n } } 5000
    Check 'the_branch_listed_under_its_parent' ([bool]$branchShown -and [string]$rep.branch -ne '') "branch $($rep.branch)"
    Check 'the_game_ran_on_meanwhile' (($f2 - $f1) -gt 5) "$($f2 - $f1) frames of the user's game while the hotpatch ran"

    $before2 = @(Items (FindById $win 'timeflowTimelines')).Count
    $wr = Patch $win $id 'ft_limit+1=10>21' 'never write hp by fill_table'
    Start-Sleep -Milliseconds 1200
    $after2 = @(Items (FindById $win 'timeflowTimelines')).Count
    Check 'wrong_old_bytes_refused' ($wr.Message -like '*REFUSED*nothing applied*' -and -not $wr.Status.report.resumed -and $after2 -eq $before2) "'$($wr.Message)'; timelines $before2 -> $after2"
    $bad = Patch $win $id 'ft_limit+1=21>1000' ''
    Check 'malformed_patch_refused' ($bad.Message -like '*must be as many*') "'$($bad.Message)'"
    $ds = Api $id GET '/api/debug/state'
    Check 'the_apps_debugger_untouched' (-not $ds.attached -and -not $ds.armed) "the app's own debugger: attached $($ds.attached), armed $($ds.armed)"
    Stop-BrokenNesInstance -ProcessId $id | Out-Null; $inst = $null
    $result.pass = -not ($checks.Values | Where-Object { -not $_.ok })
}
catch { Check 'run' $false ($_.Exception.Message + ' ' + $_.ScriptStackTrace) }
finally {
    if ($inst) { Stop-BrokenNesInstance -ProcessId $inst.ProcessId | Out-Null }
    foreach ($k in $saved.Keys) { Set-Item -Path "env:$k" -Value $saved[$k] -ErrorAction SilentlyContinue; if (-not $saved[$k]) { Remove-Item -Path "env:$k" -ErrorAction SilentlyContinue } }
}$after = Listing $userDir
Check 'user_appdata_unchanged' ($before -eq $after) "the user's $userDir $(if ($before -eq $after) { 'is unchanged' } else { 'CHANGED' })"
if ($before -ne $after) { $result.pass = $false }
$result.checks = $checks
$result | ConvertTo-Json -Compress -Depth 5
if ($result.pass) { exit 0 } else { exit 1 }
