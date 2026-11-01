<#
.SYNOPSIS
  The bug capture (BrokenDev milestone D6) in the real desktop app: the F9 key, the HTTP routes, where the bundles land, and that they replay
  exactly in the Workshop. Exit 0 = pass. Prints ONE JSON result line as the last line of stdout.

.DESCRIPTION
  Starts the shipped WinForms app with a scratch settings folder (BROKENNES_CONFIG_DIR), a scratch BrokenDev home (BROKENDEV_HOME: one build
  record that makes VRUN a project whose game workspace is a scratch folder) and a scratch default bugs folder (BROKENNES_BUGS_DIR): the
  user's config, saves, BrokenDev home and workspaces are never read or written (a listing of %APPDATA%\BrokenNes before and after proves it).
    RECORD     VRUN (BrokenDev's build) plays 35 s with input; GET /api/bugs/status: recording, at least 30 s held, a state costs a few ms.
    KEY        F9 posted to the main window: the note box opens (the game paused), the note is typed and saved through UI Automation; the
               bundle lands in <scratch workspace>\bugs\<date_time>\ with the note, the project, the symbols; Workshop --bug play: in sync.
    API        POST /api/bugs/capture with a note: the same, no box.
    BREAK      a quick state load, then a memory write through the API: each starts the history again, the bundle says why, and it replays.
    OTHER      Super VRUN (no project in the scratch home): the bundle goes to the default folder and replays.
    OFF        POST /api/bugs/enabled false: nothing is recorded and a capture is refused; the frame rate on and off (10 s each).
  Rule zero (UAT\README.md): never kill by process name; only the PIDs started here are touched.

.EXAMPLE
  pwsh -File UAT\entrypoints\bug-capture-desktop-smoke.ps1 -Exe C:\path\BrokenNes.Windows.exe -WorkshopExe C:\path\BrokenNes.Workshop.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$WorkshopExe,
    [string]$VrunRom = 'P:\BrokenDev\env\out\vrun-nes\vrun_game.nes',
    [string]$SnesRom = 'P:\BrokenDev\env\out\supervrun\supervrun.sfc',
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\uat_bug_capture_desktop')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$checks = [ordered]@{}
$result = [ordered]@{ entrypoint = 'bug-capture-desktop'; pass = $false }

function Check($name, [bool]$ok, $detail) {
    $script:checks[$name] = [ordered]@{ ok = $ok; detail = "$detail" }
    Write-Host ("[{0}] {1} - {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}
function Api([int]$procId, [string]$method, [string]$path, $body = $null) { Invoke-BrokenNesApi -ProcessId $procId -Method $method -Path $path -Body $body -TimeoutSec 60 }
function Listing([string]$dir) {
    if (-not (Test-Path -LiteralPath $dir)) { return '' }
    (@(Get-ChildItem -LiteralPath $dir -Recurse -File -Force -ErrorAction SilentlyContinue | Sort-Object FullName | ForEach-Object { "$($_.FullName.Substring($dir.Length))|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" })) -join "`n"
}
function Replay([string]$bundle) {
    $out = Join-Path $WorkDir ("replay_" + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.json')
    $p = Start-Process -FilePath $WorkshopExe -ArgumentList @('--bug', 'play', '--bug', "`"$bundle`"", '--json') -Wait -PassThru -NoNewWindow -RedirectStandardOutput $out -RedirectStandardError "$out.err"
    $j = $null; try { $j = Get-Content -LiteralPath $out -Raw | ConvertFrom-Json } catch { }
    [pscustomobject]@{ Code = $p.ExitCode; Json = $j; Text = (Get-Content -LiteralPath $out -Raw) + (Get-Content -LiteralPath "$out.err" -Raw) }
}
function Play([int]$procId, [int]$seconds) {
    # a little input all along: Right held, A tapped, so the movie has something to replay
    $end = (Get-Date).AddSeconds($seconds)
    $k = 0
    while ((Get-Date) -lt $end) {
        $b = if ($k % 3 -eq 0) { @('Right', 'A') } elseif ($k % 3 -eq 1) { @('Right') } else { @() }
        Api $procId POST '/api/input/set-buttons' @{ player = 1; buttons = $b } | Out-Null
        Start-Sleep -Milliseconds 450; $k++
    }
    Api $procId POST '/api/input/clear' | Out-Null
}
function Start-App {
    $env:BROKENNES_CONFIG_DIR = $script:cfgDir
    $env:BROKENNES_SAVES_DIR = $script:saveDir
    $env:BROKENDEV_HOME = $script:bdHome
    $env:BROKENNES_BUGS_DIR = $script:defaultBugs
    $i = Start-BrokenNesInstance -ExePath $Exe
    Start-Sleep -Seconds 4   # the app loads its embedded test.nes a few seconds after the API answers
    return $i
}
function Load([int]$procId, [string]$rom) {
    Api $procId POST '/api/emulator/load-rom' @{ path = $rom } | Out-Null
    Start-Sleep -Seconds 2
}
function Bundles([string]$dir) { if (Test-Path -LiteralPath $dir) { @(Get-ChildItem -LiteralPath $dir -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'bug.json') } | Sort-Object Name) } else { @() } }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class BugKeys {
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    public static void Key(IntPtr h, int vk) { PostMessage(h, 0x0100, (IntPtr)vk, (IntPtr)1); PostMessage(h, 0x0101, (IntPtr)vk, unchecked((IntPtr)(int)0xC0000001)); }
}
'@

$inst = $null
$saved = @{ BROKENNES_CONFIG_DIR = $env:BROKENNES_CONFIG_DIR; BROKENNES_SAVES_DIR = $env:BROKENNES_SAVES_DIR; BROKENDEV_HOME = $env:BROKENDEV_HOME; BROKENNES_BUGS_DIR = $env:BROKENNES_BUGS_DIR }
$userDir = Join-Path $env:APPDATA 'BrokenNes'
$before = Listing $userDir
try {
    . (Join-Path $repo 'UAT\lib\ApiClient.ps1')
    foreach ($r in $VrunRom, $SnesRom, $Exe, $WorkshopExe) { if (-not (Test-Path -LiteralPath $r)) { throw "missing $r" } }
    if (Test-Path -LiteralPath $WorkDir) { Remove-Item -LiteralPath $WorkDir -Recurse -Force }
    $cfgDir = Join-Path $WorkDir 'config'; $saveDir = Join-Path $WorkDir 'saves'; $bdHome = Join-Path $WorkDir 'bdhome'
    $ws = Join-Path $WorkDir 'VRUN_ws'; $defaultBugs = Join-Path $WorkDir 'default-bugs'
    New-Item -ItemType Directory -Force -Path $cfgDir, $saveDir, $ws, (Join-Path $bdHome 'out\vrun-nes') | Out-Null
    Set-Content -LiteralPath (Join-Path $cfgDir 'config.json') -Value '{ "enableWebmoduleSavestateDebugShortcuts": true }' -Encoding UTF8
    $vrun = Join-Path $WorkDir 'vrun_game.nes'; Copy-Item -LiteralPath $VrunRom -Destination $vrun
    $mlb = Join-Path $WorkDir 'vrun_game.mlb'; Copy-Item -LiteralPath ([IO.Path]::ChangeExtension($VrunRom, '.mlb')) -Destination $mlb
    $snes = Join-Path $WorkDir 'supervrun.sfc'; Copy-Item -LiteralPath $SnesRom -Destination $snes
    $sha = (Get-FileHash -LiteralPath $vrun -Algorithm SHA256).Hash.ToLowerInvariant()
    $record = [ordered]@{ project = 'vrun-nes'; title = 'VRUN (the bug-capture smoke test''s scratch record)'; ok = $true; startedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
                          workspace = [ordered]@{ kind = 'live'; path = $ws; source = 'scratch' }
                          outputs = @([ordered]@{ id = 'rom'; published = $vrun; sha256 = $sha }, [ordered]@{ id = 'mlb'; published = $mlb }) }
    $record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bdHome 'out\vrun-nes\build.json') -Encoding UTF8
    $wsBugs = Join-Path $ws 'bugs'

    # ---------------------------------------------------------------- RECORD
    $inst = Start-App; $id = $inst.ProcessId
    $st0 = Api $id GET '/api/bugs/status'
    Check 'idle_not_recorded' (-not $st0.recording -and $st0.enabled) "on the idle screen: enabled $($st0.enabled), recording $($st0.recording)"
    Load $id $vrun
    Api $id POST '/api/input/set-buttons' @{ player = 1; buttons = @('Start'); holdMs = 150 } | Out-Null
    Play $id 35
    $st = Api $id GET '/api/bugs/status'
    Check 'recording' ($st.recording -and $st.console -eq 'nes' -and $st.heldSeconds -ge 30 -and $st.project -eq 'vrun-nes' -and $st.folder -eq $wsBugs) "held $($st.heldSeconds) s of $($st.game), a state $($st.lastStateBytes) bytes in $($st.lastStateMs) ms (worst $($st.maxStateMs)); folder $($st.folder) ($($st.folderWhy))"

    # ---------------------------------------------------------------- KEY (F9, the note box through UI Automation)
    $hwnd = (Get-Process -Id $id).MainWindowHandle
    [BugKeys]::Key($hwnd, 0x78)   # VK_F9
    $box = $null
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.AndCondition (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $id)), (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty, 'Capture Bug'))
    for ($t = 0; $t -lt 40 -and -not $box; $t++) { Start-Sleep -Milliseconds 250; $box = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond) }
    Check 'key_opens_note_box' ($null -ne $box) "F9 posted to the main window: $(if ($box) { 'the Capture Bug box opened' } else { 'no box within 10 s' })"
    $paused = (Api $id GET '/api/bugs/status').frames
    Start-Sleep -Milliseconds 600
    $paused2 = (Api $id GET '/api/bugs/status').frames
    Check 'key_pauses_game' ($box -and $paused -eq $paused2) "frames $paused, then $paused2 0.6 s later (the game waits while the note is typed)"
    if ($box) {
        $edit = $box.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
        $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('F9 smoke: the key and the note box')
        $btn = $box.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty, 'Save')))
        $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    $b = $null
    for ($t = 0; $t -lt 40 -and -not $b; $t++) { Start-Sleep -Milliseconds 250; $b = Bundles $wsBugs | Select-Object -Last 1 }
    $info = if ($b) { Get-Content -LiteralPath (Join-Path $b.FullName 'bug.json') -Raw | ConvertFrom-Json } else { $null }
    Check 'key_bundle_in_workspace' ($info -and $info.note -eq 'F9 smoke: the key and the note box' -and $info.project.id -eq 'vrun-nes' -and $info.symbols.Count -eq 1 -and $info.history.seconds -ge 30 -and $info.history.start -eq 'state') "$(if ($b) { $b.FullName + ': note ''' + $info.note + ''', project ' + $info.project.id + ', ' + $info.history.seconds + ' s, symbols ' + ($info.symbols -join ',') } else { 'no bundle in ' + $wsBugs })"
    if ($b) {
        $files = @('capture.bnm', 'audio.wav', 'last.png', 'strip.png', 'rom\vrun_game.nes') | Where-Object { -not (Test-Path (Join-Path $b.FullName $_)) }
        $pics = @(Get-ChildItem (Join-Path $b.FullName 'pictures') -Filter *.png).Count
        Check 'key_bundle_files' ($files.Count -eq 0 -and $pics -ge 30 -and (Get-Item (Join-Path $b.FullName 'audio.wav')).Length -gt 30 * 44100) "missing: $($files -join ', ') ; $pics pictures; audio $((Get-Item (Join-Path $b.FullName 'audio.wav')).Length) bytes"
        $rp = Replay $b.FullName
        Check 'key_bundle_replays' ($rp.Code -eq 0 -and $rp.Json.inSync) "Workshop --bug play exit $($rp.Code): $($rp.Json.summary)"
    }
    # the negative control: F9, then Discard: no bundle
    $n0 = @(Bundles $wsBugs).Count
    [BugKeys]::Key($hwnd, 0x78)
    $box = $null
    for ($t = 0; $t -lt 40 -and -not $box; $t++) { Start-Sleep -Milliseconds 250; $box = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond) }
    if ($box) { $box.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty, 'Discard'))).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    Start-Sleep -Seconds 2
    $f0 = (Api $id GET '/api/bugs/status').frames; Start-Sleep -Milliseconds 500; $f1 = (Api $id GET '/api/bugs/status').frames
    Check 'key_discard_saves_nothing' ($box -and @(Bundles $wsBugs).Count -eq $n0 -and $f1 -gt $f0) "F9 then Discard: $(@(Bundles $wsBugs).Count) bundle(s), as before ($n0); the game runs again (frames $f0 -> $f1)"

    # ---------------------------------------------------------------- API
    Play $id 3
    $c = Api $id POST '/api/bugs/capture' @{ note = 'API smoke' }
    $info = if ($c.success) { Get-Content -LiteralPath (Join-Path $c.path 'bug.json') -Raw | ConvertFrom-Json } else { $null }
    Check 'api_capture' ($c.success -and $c.path.StartsWith($wsBugs) -and $info.note -eq 'API smoke' -and $c.start -eq 'state' -and $c.seconds -ge 30) "$($c.path): $($c.message)"
    if ($c.success) { $rp = Replay $c.path; Check 'api_bundle_replays' ($rp.Code -eq 0) "exit $($rp.Code): $($rp.Json.summary)" }

    # ---------------------------------------------------------------- BREAK: a state load, then a memory write
    $q = Api $id POST '/api/emulator/quick-save-state'
    Play $id 4
    $q2 = Api $id POST '/api/emulator/quick-load-state'
    Play $id 5
    $st = Api $id GET '/api/bugs/status'
    $c = Api $id POST '/api/bugs/capture' @{ note = 'after a load' }
    $info = if ($c.success) { Get-Content -LiteralPath (Join-Path $c.path 'bug.json') -Raw | ConvertFrom-Json } else { $null }
    Check 'break_state_load' ($q.success -and $q2.success -and $st.breakReason -eq 'a state was loaded' -and $c.success -and $c.seconds -lt 8 -and $info.history.why -like '*a state was loaded*') "quick save $($q.success), load $($q2.success); status: $($st.breakReason); bundle $($c.seconds) s: $($info.history.why)"
    if ($c.success) { $rp = Replay $c.path; Check 'break_state_load_replays' ($rp.Code -eq 0) "exit $($rp.Code): $($rp.Json.summary)" }
    Api $id POST '/api/memory/poke' @{ domain = 'RAM'; address = 1792; value = 0 } | Out-Null
    Play $id 2
    $st = Api $id GET '/api/bugs/status'
    Check 'break_memory_write' ($st.breakReason -like '*memory was written*' -and $st.heldSeconds -lt 3) "status: $($st.breakReason), held $($st.heldSeconds) s"

    # ---------------------------------------------------------------- OFF (and the frame rate on / off)
    $fpsOn = @(); for ($i = 0; $i -lt 10; $i++) { Start-Sleep -Seconds 1; $fpsOn += [double](Api $id GET '/api/console').status.fps }
    $off = Api $id POST '/api/bugs/enabled' @{ enabled = $false }
    Start-Sleep -Milliseconds 500
    $st = Api $id GET '/api/bugs/status'
    $r = Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/bugs/capture' -Body @{ note = 'off' } -TimeoutSec 30 -Raw
    $refused = $r.StatusCode -eq 400 -and -not $r.Body.success -and $r.Body.error -like '*off*'
    Check 'off_not_recording' (-not $st.enabled -and -not $st.recording -and $refused) "enabled $($st.enabled), recording $($st.recording); the capture answered $($r.StatusCode): $($r.Body.error)"
    $fpsOff = @(); for ($i = 0; $i -lt 10; $i++) { Start-Sleep -Seconds 1; $fpsOff += [double](Api $id GET '/api/console').status.fps }
    $mOn = ($fpsOn | Measure-Object -Average).Average; $mOff = ($fpsOff | Measure-Object -Average).Average
    Check 'fps_on_and_off' ($mOn -gt 58 -and $mOff -gt 58) ("VRUN at {0:F2} fps recording, {1:F2} fps off (10 s each, the app's own frame counter)" -f $mOn, $mOff)
    Api $id POST '/api/bugs/enabled' @{ enabled = $true } | Out-Null

    # ---------------------------------------------------------------- OTHER: a SNES game of no project
    Load $id $snes
    Api $id POST '/api/input/set-buttons' @{ player = 1; buttons = @('Start'); holdMs = 150 } | Out-Null
    Play $id 12
    $c = Api $id POST '/api/bugs/capture' @{ note = 'Super VRUN' }
    Check 'other_rom_default_folder' ($c.success -and $c.path.StartsWith($defaultBugs) -and $c.message -like '*no BrokenDev project*') "$($c.path): $($c.message)"
    if ($c.success) { $rp = Replay $c.path; Check 'other_rom_replays' ($rp.Code -eq 0) "exit $($rp.Code): $($rp.Json.summary)" }
    Stop-BrokenNesInstance -ProcessId $id | Out-Null; $inst = $null
    Start-Sleep -Seconds 1

    $after = Listing $userDir
    Check 'user_config_untouched' ($before -eq $after) "$userDir : $(@($before -split "`n").Count) files, $(if ($before -eq $after) { 'the same names, sizes and times' } else { 'CHANGED' })"
    $result.pass = -not ($checks.Values | Where-Object { -not $_.ok })
}
catch {
    Check 'run' $false ($_.Exception.Message + ' ' + $_.ScriptStackTrace)
}
finally {
    if ($inst) { Stop-BrokenNesInstance -ProcessId $inst.ProcessId | Out-Null }
    foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
}
$result.checks = $checks
$result | ConvertTo-Json -Compress -Depth 5
if ($result.pass) { exit 0 } else { exit 1 }
