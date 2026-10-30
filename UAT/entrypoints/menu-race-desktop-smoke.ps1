<#
.SYNOPSIS
  The start-up race: a ROM loaded over HTTP the moment the instance file appears (and again right after) must load on the UI
  thread, after the window is up - never beside the constructor's own embedded-ROM load. Exit 0 = pass. Prints ONE JSON result
  line as the last line of stdout.

.DESCRIPTION
  The bug (2026-10-29): the Web API starts at the top of the MainForm constructor, before the window has a handle. Until it has
  one, Control.InvokeRequired is false on every thread, so POST /api/emulator/load-rom ran the load on the HTTP thread while the
  UI thread was still in LoadEmbeddedRom: two threads rebuilt the core menus at once ("Error loading embedded ROM: Index was out
  of range" from FlowLayout, or "Collection was modified"), and a second load then failed with "Failed to set Win32 parent window
  of the Control" (a control made on the HTTP thread).
  Each round starts the app with scratch BROKENNES_CONFIG_DIR / SAVES / BUGS folders, polls for its discovery file every 10 ms and
  POSTs load-rom with a Game Boy Color ROM at once, then once more. Per round:
    no_error_dialog      no dialog owned by the process (any "#32770": the error boxes are MessageBoxes)
    no_error_in_log      stdout has no "Error loading embedded ROM", "Fatal Error", "Collection was modified", "Win32 parent"
    load_after_window    the log has "Embedded ROM loaded", then "[WebApi] window ready", then the game's "[Console]" line
    loads_succeeded      both POSTs answered success
    gbc_running          GET /api/console says gbc and the game is the ROM; the process is alive
  Then: user_config_untouched (a listing of %APPDATA%\BrokenNes before and after).
  NEGATIVE CONTROL: run it with -Exe <a build of the unfixed code> (main before the fix, 311aec8): it must FAIL (it did: see the
  commit message for the rounds and the errors it showed).
  Rule zero (UAT\README.md): never kill by process name; only the PIDs started here are touched.

.EXAMPLE
  pwsh -File UAT\entrypoints\menu-race-desktop-smoke.ps1 -Exe C:\path\BrokenNes.Windows.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [string]$GbcRom = 'P:\BrokenDev\env\out\vrun-voc-v2\vrun_voc.gbc',
    [int]$Rounds = 4,
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\uat_menu_race_desktop')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$checks = [ordered]@{}
$result = [ordered]@{ entrypoint = 'menu-race-desktop'; pass = $false }

function Check($name, [bool]$ok, $detail) {
    $script:checks[$name] = [ordered]@{ ok = $ok; detail = "$detail" }
    Write-Host ("[{0}] {1} - {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}
function Listing([string]$dir) {
    if (-not (Test-Path -LiteralPath $dir)) { return '' }
    (@(Get-ChildItem -LiteralPath $dir -Recurse -File -Force -ErrorAction SilentlyContinue | Sort-Object FullName | ForEach-Object { "$($_.FullName.Substring($dir.Length))|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" })) -join "`n"
}
function Post([int]$port, [string]$path, $body) {
    try {
        $r = Invoke-WebRequest -Uri "http://127.0.0.1:$port$path" -Method POST -ContentType 'application/json' -Body ($body | ConvertTo-Json -Compress) -TimeoutSec 150 -SkipHttpErrorCheck
        return [pscustomobject]@{ Status = [int]$r.StatusCode; Body = $(try { $r.Content | ConvertFrom-Json } catch { $r.Content }) }
    } catch { return [pscustomobject]@{ Status = -1; Body = $_.Exception.Message } }
}

$saved = @{ BROKENNES_CONFIG_DIR = $env:BROKENNES_CONFIG_DIR; BROKENNES_SAVES_DIR = $env:BROKENNES_SAVES_DIR; BROKENNES_BUGS_DIR = $env:BROKENNES_BUGS_DIR }
$userDir = Join-Path $env:APPDATA 'BrokenNes'
$before = Listing $userDir
$procs = @()
try {
    . (Join-Path $repo 'UAT\lib\ApiClient.ps1')
    foreach ($r in $GbcRom, $Exe) { if (-not (Test-Path -LiteralPath $r)) { throw "missing $r" } }
    if (Test-Path -LiteralPath $WorkDir) { Remove-Item -LiteralPath $WorkDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
    $rom = Join-Path $WorkDir 'race.gbc'; Copy-Item -LiteralPath $GbcRom -Destination $rom
    $instDir = Join-Path $env:LOCALAPPDATA 'BrokenNes\instances'
    $failedRounds = 0

    for ($round = 1; $round -le $Rounds; $round++) {
        $rd = Join-Path $WorkDir "round$round"
        $cfgDir = Join-Path $rd 'config'; $saveDir = Join-Path $rd 'saves'; $bugDir = Join-Path $rd 'bugs'
        New-Item -ItemType Directory -Force -Path $cfgDir, $saveDir, $bugDir | Out-Null
        Set-Content -LiteralPath (Join-Path $cfgDir 'config.json') -Value '{ "emulationMuted": true }' -Encoding UTF8
        $env:BROKENNES_CONFIG_DIR = $cfgDir; $env:BROKENNES_SAVES_DIR = $saveDir; $env:BROKENNES_BUGS_DIR = $bugDir
        $out = Join-Path $rd 'stdout.txt'
        $p = Start-Process -FilePath $Exe -WorkingDirectory (Split-Path -Parent $Exe) -PassThru -RedirectStandardOutput $out -RedirectStandardError "$out.err"
        $procs += $p
        # the discovery file: the earliest moment a harness can know the port (written as soon as the server binds)
        $file = Join-Path $instDir "$($p.Id).json"; $port = 0; $sw = [Diagnostics.Stopwatch]::StartNew()
        while ($sw.Elapsed.TotalSeconds -lt 60 -and $port -eq 0 -and -not $p.HasExited) {
            if (Test-Path -LiteralPath $file) { try { $port = [int](Get-Content -LiteralPath $file -Raw | ConvertFrom-Json).httpPort } catch { } }
            if ($port -eq 0) { Start-Sleep -Milliseconds 10 }
        }
        if ($port -eq 0) { Check "r${round}_started" $false "no discovery file in 60 s (exited: $($p.HasExited))"; $failedRounds++; continue }
        $tFile = $sw.ElapsedMilliseconds
        $l1 = Post $port '/api/emulator/load-rom' @{ path = $rom }
        $tLoad = $sw.ElapsedMilliseconds
        $l2 = Post $port '/api/emulator/load-rom' @{ path = $rom }
        Start-Sleep -Seconds 3
        $alive = -not $p.HasExited
        $dialogs = if ($alive) { @(Get-BrokenNesDialogs -ProcessId $p.Id) } else { @() }
        $dlgText = ($dialogs | ForEach-Object { $_.Title }) -join ', '
        $st = $null
        if ($alive) { try { $st = (Invoke-RestMethod -Uri "http://127.0.0.1:$port/api/console" -TimeoutSec 30).status } catch { } }
        # close it (PID only), then read its log
        if ($alive) {
            if ($dialogs.Count -gt 0) { Dismiss-BrokenNesDialogs -ProcessId $p.Id | Out-Null; Start-Sleep -Milliseconds 500; Dismiss-BrokenNesDialogs -ProcessId $p.Id | Out-Null }
            Stop-BrokenNesInstance -ProcessId $p.Id | Out-Null
        }
        $log = (Get-Content -LiteralPath $out -Raw -ErrorAction SilentlyContinue) + (Get-Content -LiteralPath "$out.err" -Raw -ErrorAction SilentlyContinue)
        $bad = @('Error loading embedded ROM', 'Fatal Error', 'Unhandled Exception', 'Collection was modified', 'Win32 parent', 'off the UI thread') | Where-Object { $log -like "*$_*" }
        $okRound = $true
        $c = ($dialogs.Count -eq 0); $okRound = $okRound -and $c
        Check "r${round}_no_error_dialog" $c $(if ($c) { 'no dialog' } else { "dialog(s): $dlgText" })
        $c = ($bad.Count -eq 0); $okRound = $okRound -and $c
        $firstBad = if ($bad.Count) { ($log -split "`n" | Where-Object { $l = $_; $bad | Where-Object { $l -like "*$_*" } } | Select-Object -First 2) -join ' | ' } else { '' }
        Check "r${round}_no_error_in_log" $c $(if ($c) { 'the log is clean' } else { "log: $($bad -join '; ') :: $firstBad" })
        $iReady = $log.IndexOf('[WebApi] window ready'); $iEmbedded = $log.IndexOf('Embedded ROM loaded successfully'); $iGame = $log.IndexOf('[Console] ')
        $c = ($iReady -ge 0 -and $iEmbedded -ge 0 -and $iGame -gt $iReady -and $iReady -gt $iEmbedded); $okRound = $okRound -and $c
        Check "r${round}_load_after_window" $c "log offsets: embedded ROM loaded $iEmbedded, window ready $iReady, the GBC game started $iGame (must be in that order)"
        $c = ($l1.Status -eq 200 -and $l1.Body.success -and $l2.Status -eq 200 -and $l2.Body.success); $okRound = $okRound -and $c
        Check "r${round}_loads_succeeded" $c ("discovery file after {0} ms; load 1 answered {1} after {2} ms ({3}), load 2 {4} ({5})" -f $tFile, $l1.Status, ($tLoad - $tFile), ($l1.Body | ConvertTo-Json -Compress -Depth 2), $l2.Status, ($l2.Body | ConvertTo-Json -Compress -Depth 2))
        $c = ($alive -and $st -and $st.console -eq 'gbc' -and "$($st.game)".Length -gt 0); $okRound = $okRound -and $c
        Check "r${round}_gbc_running" $c "alive $alive; console $($st.console), game '$($st.game)'"
        if (-not $okRound) { $failedRounds++ }
    }
    Start-Sleep -Seconds 1
    $after = Listing $userDir
    Check 'user_config_untouched' ($before -eq $after) "$userDir : $(@($before -split "`n").Count) files, $(if ($before -eq $after) { 'the same names, sizes and times' } else { 'CHANGED' })"
    $result.failedRounds = $failedRounds
    $result.pass = -not ($checks.Values | Where-Object { -not $_.ok })
}
catch {
    Check 'run' $false ($_.Exception.Message + ' ' + $_.ScriptStackTrace)
}
finally {
    foreach ($p in $procs) { if (-not $p.HasExited) { Stop-BrokenNesInstance -ProcessId $p.Id -TimeoutSeconds 10 | Out-Null } }
    foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
}
$result.checks = $checks
$result | ConvertTo-Json -Compress -Depth 5
if ($result.pass) { exit 0 } else { exit 1 }
