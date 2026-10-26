<#
.SYNOPSIS
  The BrokenDev debug port (milestone D7) in the real desktop app: Emulator > Debug Port's config, the HTTP routes, the log, the pause on a
  failed assert, the bug capture's bundle carrying the markers. Exit 0 = pass. Prints ONE JSON result line as the last line of stdout.

.DESCRIPTION
  Starts the shipped WinForms app with a scratch settings folder (BROKENNES_CONFIG_DIR), saves folder (BROKENNES_SAVES_DIR), BrokenDev home
  (BROKENDEV_HOME, empty) and bugs folder (BROKENNES_BUGS_DIR): the user's %APPDATA%\BrokenNes is never read or written (a listing before and
  after proves it). The ROMs are the Workshop's debug-port test ROMs (Workshop --port-test --write-roms) and, when built, the Game Boy hello
  world's dev build.
    OFF        the port off (the default): the NES test ROM runs, GET /api/debug/port says off and nothing listens.
    NES        POST /api/debug/port/enabled true, the NES test ROM again: its prints (with values), markers and the numbered timer to the
               cycle (57) come through the API; the failed assert paused the game (the frame counter stops); the log has the [port] lines.
    GB         the Game Boy test ROM: the bgb messages, EMU_printf's message, the timer.
    CAPTURE    POST /api/bugs/capture: the bundle's bug.json lists the port's events and its movie the markers.
    NEGATIVE   the NES ROM that writes beside the port: nothing heard; the port switched off again: nothing listens.
  Rule zero (UAT\README.md): never kill by process name; only the PIDs started here are touched.

.EXAMPLE
  pwsh -File UAT\entrypoints\debug-port-desktop-smoke.ps1 -Exe C:\path\BrokenNes.Windows.exe -WorkshopExe C:\path\BrokenNes.Workshop.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$WorkshopExe,
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\uat_debug_port_desktop')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$checks = [ordered]@{}
$result = [ordered]@{ entrypoint = 'debug-port-desktop'; pass = $false }

function Check($name, [bool]$ok, $detail) {
    $script:checks[$name] = [ordered]@{ ok = $ok; detail = "$detail" }
    Write-Host ("[{0}] {1} - {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}
function Api([int]$procId, [string]$method, [string]$path, $body = $null) { Invoke-BrokenNesApi -ProcessId $procId -Method $method -Path $path -Body $body -TimeoutSec 60 }
function Listing([string]$dir) {
    if (-not (Test-Path -LiteralPath $dir)) { return '' }
    (@(Get-ChildItem -LiteralPath $dir -Recurse -File -Force -ErrorAction SilentlyContinue | Sort-Object FullName | ForEach-Object { "$($_.FullName.Substring($dir.Length))|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" })) -join "`n"
}
function Load([int]$procId, [string]$rom) {
    # the app may still be loading its embedded test.nes, which would replace the ROM: load until the API names it
    for ($t = 0; $t -lt 4; $t++) {
        Api $procId POST '/api/emulator/load-rom' @{ path = $rom } | Out-Null; Start-Sleep -Seconds 3
        $c = Get-BrokenNesCurrentRom -ProcessId $procId
        if ($c.path -and ([IO.Path]::GetFileName($c.path) -eq [IO.Path]::GetFileName($rom))) { return }
    }
    throw "$rom did not load"
}

$inst = $null
$saved = @{ BROKENNES_CONFIG_DIR = $env:BROKENNES_CONFIG_DIR; BROKENNES_SAVES_DIR = $env:BROKENNES_SAVES_DIR; BROKENDEV_HOME = $env:BROKENDEV_HOME; BROKENNES_BUGS_DIR = $env:BROKENNES_BUGS_DIR }
$userDir = Join-Path $env:APPDATA 'BrokenNes'
$before = Listing $userDir
try {
    . (Join-Path $repo 'UAT\lib\ApiClient.ps1')
    foreach ($r in $Exe, $WorkshopExe) { if (-not (Test-Path -LiteralPath $r)) { throw "missing $r" } }
    if (Test-Path -LiteralPath $WorkDir) { Remove-Item -LiteralPath $WorkDir -Recurse -Force }
    $cfgDir = Join-Path $WorkDir 'config'; $romDir = Join-Path $WorkDir 'roms'; $bugs = Join-Path $WorkDir 'bugs'
    New-Item -ItemType Directory -Force -Path $cfgDir, $romDir, $bugs, (Join-Path $WorkDir 'saves'), (Join-Path $WorkDir 'bdhome') | Out-Null
    Set-Content -LiteralPath (Join-Path $cfgDir 'config.json') -Value '{ "enableWebmoduleSavestateDebugShortcuts": true }' -Encoding UTF8
    $p = Start-Process -FilePath $WorkshopExe -ArgumentList @('--port-test', '--write-roms', "`"$romDir`"") -Wait -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $WorkDir 'roms.txt')
    if ($p.ExitCode -ne 0) { throw 'Workshop --port-test --write-roms failed' }
    # the negative control's NES ROM: the same code writing beside the port ($4018-$401B)
    $neg = Join-Path $romDir 'porttest_neg.nes'
    $env:BROKENNES_CONFIG_DIR = $cfgDir; $env:BROKENNES_SAVES_DIR = Join-Path $WorkDir 'saves'; $env:BROKENDEV_HOME = Join-Path $WorkDir 'bdhome'; $env:BROKENNES_BUGS_DIR = $bugs

    function New-App { $i = Start-BrokenNesInstance -ExePath $Exe; Start-Sleep -Seconds 4; return $i }
    function End-App { if ($script:inst) { Stop-BrokenNesInstance -ProcessId $script:inst.ProcessId | Out-Null; $script:inst = $null } }
    # ---------------------------------------------------------------- OFF
    $inst = New-App; $id = $inst.ProcessId
    Load $id (Join-Path $romDir 'porttest.nes')
    $off = Api $id GET '/api/debug/port'
    Check 'off_by_default' ($off.success -and -not $off.enabled -and -not $off.listening) "enabled $($off.enabled), listening $($off.listening): $($off.why)"
    # ---------------------------------------------------------------- NES (the switch through the API; the ROM again from its power-on)
    Api $id POST '/api/debug/port/enabled' @{ enabled = $true } | Out-Null
    Load $id (Join-Path $romDir 'porttest.nes')
    Start-Sleep -Seconds 2
    $s = Api $id GET '/api/debug/port?limit=100'
    $texts = @($s.events | ForEach-Object { $_.text })
    Check 'nes_events' ($s.listening -and $texts -contains 'hello from the port' -and $texts -contains 'room 3 of 4660, x=0a, s=-2' -and $texts -contains 'start') "listening $($s.listening) at $($s.where); $($s.total) events: $($texts -join ' | ')"
    $work = @($s.timers | Where-Object { $_.name -eq 'work' })
    Check 'nes_timer' ($work.Count -eq 1 -and $work[0].worst -eq 57 -and $work[0].best -eq 57) "work: $($work | ConvertTo-Json -Compress)"
    $assert = @($s.events | Where-Object { $_.kind -eq 'assert' })
    Start-Sleep -Seconds 2
    $s2 = Api $id GET '/api/debug/port?limit=100'
    Check 'nes_assert_pauses' ($assert.Count -eq 1 -and $assert[0].text -eq 'frame counter < 3 (count=3)' -and $s2.total -eq $s.total -and -not (@($s2.events | ForEach-Object { $_.text }) -contains 'five frames')) "assert: $($assert | ConvertTo-Json -Compress); events $($s.total) then $($s2.total) (the fifth frame's marker never came: paused)"
    # ---------------------------------------------------------------- CAPTURE (while paused at the assert: the ring holds frames 0-2)
    $cap = Api $id POST '/api/bugs/capture' @{ note = 'debug port smoke' }
    $bj = if ($cap.success) { Get-Content -LiteralPath (Join-Path $cap.path 'bug.json') -Raw | ConvertFrom-Json } else { $null }
    $mv = if ($cap.success) { Get-Content -LiteralPath (Join-Path $cap.path 'capture.bnm') -Raw } else { '' }
    Check 'capture_carries_events' ($cap.success -and @($bj.debugPort | Where-Object { $_.kind -eq 'marker' -and $_.text -eq 'start' }).Count -eq 1 -and @($bj.debugPort | Where-Object { $_.kind -eq 'assert' }).Count -eq 1 -and $mv -match "`nm \d+ start") "$($cap.message); debugPort entries: $(@($bj.debugPort | ForEach-Object { "$($_.frame) $($_.kind) $($_.text)" }) -join ' | ')"
    # ---------------------------------------------------------------- switched off: nothing listens, at once (the game is paused)
    Api $id POST '/api/debug/port/enabled' @{ enabled = $false } | Out-Null
    Start-Sleep -Seconds 1
    $o2 = Api $id GET '/api/debug/port'
    Check 'switched_off' (-not $o2.enabled -and -not $o2.listening) "enabled $($o2.enabled), listening $($o2.listening)"
    End-App
    # ---------------------------------------------------------------- GB (a new app; the port on from the saved config)
    Set-Content -LiteralPath (Join-Path $cfgDir 'config.json') -Value '{ "enableWebmoduleSavestateDebugShortcuts": true, "debugPortEnabled": true }' -Encoding UTF8
    $inst = New-App; $id = $inst.ProcessId
    Load $id (Join-Path $romDir 'porttest.gb')
    Start-Sleep -Seconds 2
    $g = Api $id GET '/api/debug/port?limit=100'
    $gt = @($g.events | ForEach-Object { $_.text })
    Check 'gb_events' ($g.listening -and $gt -contains 'hello from the port' -and $gt -contains 'v=1234 c=ab s=ok' -and $gt -contains 'main.c:42 frame counter < 3' -and -not ($gt -contains 'five frames') -and @($g.timers | Where-Object { $_.name -eq 'work' -and $_.worst -eq 45 }).Count -eq 1) "$($g.total) events: $($gt -join ' | ')"
    End-App
    # ---------------------------------------------------------------- NEGATIVE (a new app: the ROM that writes beside the port)
    $inst = New-App; $id = $inst.ProcessId
    Load $id $neg
    Start-Sleep -Seconds 3
    $n = Api $id GET '/api/debug/port'
    Check 'negative_beside_the_port' ($n.listening -and $n.total -eq 0) "listening $($n.listening), $($n.total) events $($n.why)"
    End-App
    $cfg = Get-Content -LiteralPath (Join-Path $cfgDir 'config.json') -Raw | ConvertFrom-Json
    Check 'config_in_scratch' ($cfg.debugPortEnabled -eq $true) "the scratch config's debugPortEnabled: $($cfg.debugPortEnabled)"
} catch {
    Check 'ran_to_the_end' $false ($_.Exception.Message + ' ' + $_.ScriptStackTrace)
} finally {
    if ($inst) { try { Stop-BrokenNesInstance -ProcessId $inst.ProcessId | Out-Null } catch { } }
    foreach ($k in $saved.Keys) { Set-Item -Path "env:$k" -Value $saved[$k] -ErrorAction SilentlyContinue; if ($null -eq $saved[$k]) { Remove-Item "env:$k" -ErrorAction SilentlyContinue } }
}
$after = Listing $userDir
Check 'user_appdata_untouched' ($before -eq $after) "%APPDATA%\BrokenNes: $(@($before -split "`n").Count) files, unchanged: $($before -eq $after)"
$result.pass = -not ($checks.Values | Where-Object { -not $_.ok })
$result.checks = $checks
$result | ConvertTo-Json -Depth 5 -Compress
exit $(if ($result.pass) { 0 } else { 1 })
