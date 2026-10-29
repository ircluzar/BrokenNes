<#
.SYNOPSIS
  No playback device, one appearing, one going away mid-game: the desktop app keeps running silently (no dialog), opens the
  device when it appears and reopens it when it comes back, without a restart, and the frame rate does not move.
  Exit 0 = pass. Prints ONE JSON result line as the last line of stdout.

.DESCRIPTION
  The machine's real sound settings are never touched: the app's test hook BROKENNES_TEST_AUDIO_DEVICE_FILE names a scratch file
  whose text "none" makes Windows look as if it had no playback device (Windows/AudioManager.cs, PlaybackDeviceCount). The app
  starts with scratch BROKENNES_CONFIG_DIR / SAVES / BUGS folders and its emulation sound muted (the device really opens, but
  plays silence). GET /api/audio/device reports the state (Open / NoDevice / Lost) and how often it was opened and lost.
    REAL         first, no hook file at all: the real device opens at start-up (state Open, opened once); its frame rate is
                 the yardstick for the phases with a device (they are paced by the device's clock, not the app's).
    START_NONE   the file says none at start-up: state NoDevice, never opened, no dialog of the process, the title says
                 "[no sound: ...]", the game runs at ~60 fps.
      control    for 4 s more with the file still none: still NoDevice (the device is not opened by anything but its arrival).
    APPEAR       the file says present: within 6 s state Open (opened once), the note leaves the title, the frame rate of REAL.
    LOSS         a Game Boy Color game is loaded and plays, then the file says none: within 6 s state Lost (lost once), no
                 dialog, the process alive, ~60 fps while silent.
      control    for 4 s more: still Lost (not reopened while there is no device).
    RETURN       the file says present: within 6 s Open again (opened twice), the frame rate of REAL.
  NEGATIVE CONTROL: with -Exe <a build of the unfixed code> it must FAIL (there is no device state and no silent start).
  Rule zero (UAT\README.md): never kill by process name; only the PIDs started here are touched.

.EXAMPLE
  pwsh -File UAT\entrypoints\audio-device-desktop-smoke.ps1 -Exe C:\path\BrokenNes.Windows.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [string]$GbcRom = 'P:\BrokenDev\env\out\vrun-voc-v2\vrun_voc.gbc',
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\uat_audio_device_desktop')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$checks = [ordered]@{}
$result = [ordered]@{ entrypoint = 'audio-device-desktop'; pass = $false }

function Check($name, [bool]$ok, $detail) {
    $script:checks[$name] = [ordered]@{ ok = $ok; detail = "$detail" }
    Write-Host ("[{0}] {1} - {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}
function Listing([string]$dir) {
    if (-not (Test-Path -LiteralPath $dir)) { return '' }
    (@(Get-ChildItem -LiteralPath $dir -Recurse -File -Force -ErrorAction SilentlyContinue | Sort-Object FullName | ForEach-Object { "$($_.FullName.Substring($dir.Length))|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" })) -join "`n"
}
function Api([int]$procId, [string]$method, [string]$path, $body = $null) { Invoke-BrokenNesApi -ProcessId $procId -Method $method -Path $path -Body $body -TimeoutSec 60 }
function Device([int]$procId) { $r = Invoke-BrokenNesApi -ProcessId $procId -Method GET -Path '/api/audio/device' -TimeoutSec 30 -Raw; if ($r.StatusCode -eq 200) { $r.Body.device } else { $null } }
function WaitState([int]$procId, [string]$state, [int]$seconds) {
    $sw = [Diagnostics.Stopwatch]::StartNew(); $d = $null
    while ($sw.Elapsed.TotalSeconds -lt $seconds) { $d = Device $procId; if ($d -and $d.state -eq $state) { break }; Start-Sleep -Milliseconds 200 }
    [pscustomobject]@{ Device = $d; Ms = $sw.ElapsedMilliseconds }
}
function Fps([int]$procId, [int]$seconds) {
    $v = @(); for ($i = 0; $i -lt $seconds; $i++) { Start-Sleep -Seconds 1; $v += [double](Api $procId GET '/api/console').status.fps }
    ($v | Measure-Object -Average).Average
}
function Title([int]$procId) { (Get-Process -Id $procId).MainWindowTitle }
function Dialogs([int]$procId) { @(Get-BrokenNesDialogs -ProcessId $procId | ForEach-Object { $_.Title }) }
function Start-App([string]$name, [string]$hookFile) {
    $rd = Join-Path $WorkDir $name
    $cfgDir = Join-Path $rd 'config'
    New-Item -ItemType Directory -Force -Path $cfgDir, (Join-Path $rd 'saves'), (Join-Path $rd 'bugs') | Out-Null
    Set-Content -LiteralPath (Join-Path $cfgDir 'config.json') -Value '{ "emulationMuted": true }' -Encoding UTF8
    $env:BROKENNES_CONFIG_DIR = $cfgDir; $env:BROKENNES_SAVES_DIR = (Join-Path $rd 'saves'); $env:BROKENNES_BUGS_DIR = (Join-Path $rd 'bugs')
    $env:BROKENNES_TEST_AUDIO_DEVICE_FILE = $hookFile
    $i = Start-BrokenNesInstance -ExePath $Exe -KeepDialogs   # a start-up dialog must be seen by the checks, not clicked away
    Start-Sleep -Seconds 3
    return $i
}
function Line($d) { if ($d) { "state $($d.state), opens $($d.opens), losses $($d.losses), bufferedMs $($d.bufferedMs), Windows devices $($d.windowsDevices), last error '$($d.lastError)'" } else { 'no /api/audio/device' } }

$saved = @{ BROKENNES_CONFIG_DIR = $env:BROKENNES_CONFIG_DIR; BROKENNES_SAVES_DIR = $env:BROKENNES_SAVES_DIR; BROKENNES_BUGS_DIR = $env:BROKENNES_BUGS_DIR; BROKENNES_TEST_AUDIO_DEVICE_FILE = $env:BROKENNES_TEST_AUDIO_DEVICE_FILE }
$userDir = Join-Path $env:APPDATA 'BrokenNes'
$before = Listing $userDir
$inst = $null
try {
    . (Join-Path $repo 'UAT\lib\ApiClient.ps1')
    foreach ($r in $GbcRom, $Exe) { if (-not (Test-Path -LiteralPath $r)) { throw "missing $r" } }
    if (Test-Path -LiteralPath $WorkDir) { Remove-Item -LiteralPath $WorkDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
    $rom = Join-Path $WorkDir 'game.gbc'; Copy-Item -LiteralPath $GbcRom -Destination $rom
    $hook = Join-Path $WorkDir 'audio-device.txt'
    Set-Content -LiteralPath $hook -Value 'none'

    # ---------------------------------------------------------------- REAL (no hook)
    $inst = Start-App 'real' ''
    $id = $inst.ProcessId
    $d = Device $id
    $fps = Fps $id 4
    Check 'real_device_opens_at_start' ($d -and $d.state -eq 'Open' -and $d.opens -eq 1 -and $d.windowsDevices -gt 0) (Line $d)
    $realFps = $fps
    Check 'real_fps' ($fps -gt 30) ("{0:F2} fps with the device opened at start-up (the yardstick for the phases with a device: the emulation is paced by the device's own clock then)" -f $fps)
    Stop-BrokenNesInstance -ProcessId $id | Out-Null; $inst = $null

    # ---------------------------------------------------------------- START_NONE
    $inst = Start-App 'hooked' $hook; $id = $inst.ProcessId
    $d = Device $id; $dl = Dialogs $id; $t = Title $id
    $fps = Fps $id 4
    Check 'start_none_silent' ($d -and $d.state -eq 'NoDevice' -and $d.opens -eq 0 -and $d.bufferedMs -eq -1) (Line $d)
    Check 'start_none_no_dialog' ($dl.Count -eq 0) $(if ($dl.Count) { "dialog(s): $($dl -join ', ')" } else { 'no dialog' })
    Check 'start_none_title_note' ($t -like '*`[no sound:*') "title: $t"
    Check 'start_none_fps' ($fps -gt 58 -and $fps -lt 62.5) ("{0:F2} fps with no device" -f $fps)
    Start-Sleep -Seconds 4
    $d = Device $id
    Check 'start_none_control_stays_silent' ($d -and $d.state -eq 'NoDevice' -and $d.opens -eq 0) ("4 s later, still none: " + (Line $d))

    # ---------------------------------------------------------------- APPEAR
    Set-Content -LiteralPath $hook -Value 'present'
    $w = WaitState $id 'Open' 6
    $t = Title $id; $fps = Fps $id 3
    Check 'appear_opens' ($w.Device -and $w.Device.state -eq 'Open' -and $w.Device.opens -eq 1 -and $w.Device.bufferedMs -ge 0) ("after $($w.Ms) ms: " + (Line $w.Device))
    Check 'appear_title_clean' ($t -notlike '*no sound*') "title: $t"
    Check 'appear_fps' ([math]::Abs($fps - $realFps) -lt 5) ("{0:F2} fps with the device (a normal start with it: {1:F2})" -f $fps, $realFps)

    # ---------------------------------------------------------------- LOSS (mid-game)
    $l = Api $id POST '/api/emulator/load-rom' @{ path = $rom }
    Start-Sleep -Seconds 3
    $st = (Api $id GET '/api/console').status
    Check 'loss_game_running' ($l.success -and $st.console -eq 'gbc') "load $($l.success); console $($st.console), game '$($st.game)'"
    Set-Content -LiteralPath $hook -Value 'none'
    $w = WaitState $id 'Lost' 6
    $fps = Fps $id 3; $dl = Dialogs $id; $t = Title $id
    Check 'loss_detected' ($w.Device -and $w.Device.state -eq 'Lost' -and $w.Device.losses -eq 1 -and $w.Device.bufferedMs -eq -1) ("after $($w.Ms) ms: " + (Line $w.Device))
    Check 'loss_keeps_running' ((Get-Process -Id $id -ErrorAction SilentlyContinue) -and $dl.Count -eq 0 -and $fps -gt 58 -and $fps -lt 62.5 -and $t -like '*`[no sound:*') ("alive, dialogs: $($dl.Count), {0:F2} fps silent, title: $t" -f $fps)
    Start-Sleep -Seconds 4
    $d = Device $id
    Check 'loss_control_stays_silent' ($d -and $d.state -eq 'Lost' -and $d.opens -eq 1) ("4 s later, still none: " + (Line $d))

    # ---------------------------------------------------------------- RETURN
    Set-Content -LiteralPath $hook -Value 'present'
    $w = WaitState $id 'Open' 6
    $fps = Fps $id 3
    Check 'return_reopens' ($w.Device -and $w.Device.state -eq 'Open' -and $w.Device.opens -eq 2 -and $w.Device.losses -eq 1) ("after $($w.Ms) ms: " + (Line $w.Device))
    Check 'return_fps' ([math]::Abs($fps - $realFps) -lt 5) ("{0:F2} fps with the device back (a normal start with it: {1:F2})" -f $fps, $realFps)
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
    if ($inst) { Dismiss-BrokenNesDialogs -ProcessId $inst.ProcessId | Out-Null; Stop-BrokenNesInstance -ProcessId $inst.ProcessId | Out-Null }
    foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
}
$result.checks = $checks
$result | ConvertTo-Json -Compress -Depth 5
if ($result.pass) { exit 0 } else { exit 1 }
