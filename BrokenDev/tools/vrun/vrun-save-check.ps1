<#
.SYNOPSIS
  VRUN CTW (NES) save persistence through the real BrokenNes desktop app: new game -> pause -> Select (VRUN's save AND QUIT) -> the app
  writes the mapper-30 flash to <gameId>.flash.sav -> the app is closed -> a fresh app loads the ROM -> CONTINUE is enabled -> CONTINUE
  resumes the saved room.

.DESCRIPTION
  Every check reads the game's own variables through the app's HTTP API (UAT\lib\ApiClient.ps1), at addresses taken from the build's .mlb
  (NESFab moves them on every build): app_state, title_cursor, save_exists, paused, current_room. Input goes through /api/input/set-buttons
  (real time, not frame exact: the waits are generous). The app runs with its config in -ConfigDir (BROKENNES_CONFIG_DIR); battery saves
  still go to %APPDATA%\BrokenNes\BatterySaves, so the save files this run creates are deleted at the end unless -KeepSave (files that
  existed before are never touched). Instances are closed by PID through the window (the graceful path flushes battery RAM).

  pwsh -File BrokenDev\tools\vrun\vrun-save-check.ps1 -Exe <BrokenNes.Windows.exe> -Rom <vrun_game.nes> [-Mlb <vrun_game.mlb>] -ConfigDir <scratch>
  Exit code: 0 every check passed | 1 a check failed | 2 usage.
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Rom,
    [string]$Mlb = '',
    [Parameter(Mandatory)][string]$ConfigDir,
    [switch]$KeepSave,
    # a save of this exact ROM already on disk would be overwritten by the test: refused unless this is given
    [switch]$AllowExistingSave
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)))
. (Join-Path $repo 'UAT\lib\ApiClient.ps1')
if (-not $Mlb) { $Mlb = [System.IO.Path]::ChangeExtension($Rom, '.mlb') }
foreach ($p in $Exe, $Rom, $Mlb) { if (-not (Test-Path -LiteralPath $p)) { Write-Host "missing: $p"; exit 2 } }

# ---- addresses from the .mlb ("R:0420:save_exists@0:") ---------------------------------------------------------------------
$A = @{}
foreach ($line in Get-Content -LiteralPath $Mlb) {
    if ($line -match '^R:([0-9A-Fa-f]+):([A-Za-z_0-9]+)@0:') { if (-not $A.ContainsKey($Matches[2])) { $A[$Matches[2]] = [Convert]::ToInt32($Matches[1], 16) } }
}
foreach ($n in 'app_state', 'title_cursor', 'save_exists', 'paused', 'current_room') { if (-not $A.ContainsKey($n)) { Write-Host "the .mlb has no $n@0"; exit 2 } }
# main.fab: APP_TITLE 0, APP_GAME 1; TITLE_OPT_CONTINUE 1
New-Item -ItemType Directory -Force -Path $ConfigDir | Out-Null
$env:BROKENNES_CONFIG_DIR = (Resolve-Path -LiteralPath $ConfigDir).Path

$results = [ordered]@{}
function Peek([int]$procId, [string]$name) { Get-BrokenNesMemory -ProcessId $procId -Domain 'System RAM' -Address $A[$name] }
function Press([int]$procId, [string[]]$buttons, [int]$ms = 120) {
    Invoke-BrokenNesApi -ProcessId $procId -Method POST -Path '/api/input/set-buttons' -Body @{ player = 1; buttons = $buttons; holdMs = $ms } | Out-Null
    Start-Sleep -Milliseconds ($ms + 250)
}
function Check([string]$name, [bool]$ok, [string]$detail) { $results[$name] = $ok; Write-Host ('{0} {1}  {2}' -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail) }

$saveDir = Join-Path $env:APPDATA 'BrokenNes\BatterySaves'
$existing = @{}; if (Test-Path $saveDir) { Get-ChildItem -LiteralPath $saveDir -File | ForEach-Object { $existing[$_.FullName] = $true } }
$before = Get-Date

$i1 = Start-BrokenNesInstance -ExePath $Exe
try {
    Start-Sleep -Seconds 5
    Load-BrokenNesRom -ProcessId $i1.ProcessId -Path $Rom | Out-Null
    Start-Sleep -Seconds 4
    $cur = Get-BrokenNesCurrentRom -ProcessId $i1.ProcessId
    Check 'rom-loaded' ($cur.name -eq (Split-Path -Leaf $Rom)) "current ROM $($cur.name)"
    Check 'boots-to-title' ((Peek $i1.ProcessId 'app_state') -eq 0) "app_state $(Peek $i1.ProcessId 'app_state')"
    if ((Peek $i1.ProcessId 'save_exists') -ne 0 -and -not $AllowExistingSave) {
        throw 'REFUSED: a save of this exact ROM is already on disk (save_exists = 1 at boot) and the test would overwrite it; pass -AllowExistingSave to run anyway'
    }
    Press $i1.ProcessId @('Start'); Start-Sleep -Seconds 4
    Check 'new-game-started' ((Peek $i1.ProcessId 'app_state') -eq 1) "app_state $(Peek $i1.ProcessId 'app_state')"
    Press $i1.ProcessId @('Left') 600; Start-Sleep -Seconds 1
    $room = Peek $i1.ProcessId 'current_room'
    Press $i1.ProcessId @('Start'); Start-Sleep -Seconds 1
    Check 'paused' ((Peek $i1.ProcessId 'paused') -ne 0) "paused $(Peek $i1.ProcessId 'paused')"
    Press $i1.ProcessId @('Select'); Start-Sleep -Seconds 3
    Check 'save-and-quit-to-title' ((Peek $i1.ProcessId 'app_state') -eq 0 -and (Peek $i1.ProcessId 'save_exists') -eq 1) "app_state $(Peek $i1.ProcessId 'app_state'), save_exists $(Peek $i1.ProcessId 'save_exists'), saved room $room"
    Check 'title-cursor-on-continue' ((Peek $i1.ProcessId 'title_cursor') -eq 1) "title_cursor $(Peek $i1.ProcessId 'title_cursor')"
    Start-Sleep -Seconds 12   # past the ~600-frame battery autosave
}
finally { $s = Stop-BrokenNesInstance -ProcessId $i1.ProcessId; Write-Host "closed run 1 ($($s.Method))" }

$new = @(if (Test-Path $saveDir) { Get-ChildItem -LiteralPath $saveDir -File | Where-Object { $_.LastWriteTime -ge $before } })
$flash = @($new | Where-Object { $_.Name -like '*.flash.sav' })
Check 'flash-save-file-written' ($flash.Count -ge 1) (($flash | ForEach-Object { "$($_.Name) $($_.Length) bytes" }) -join '; ')

$i2 = Start-BrokenNesInstance -ExePath $Exe
try {
    Start-Sleep -Seconds 5
    Load-BrokenNesRom -ProcessId $i2.ProcessId -Path $Rom | Out-Null
    Start-Sleep -Seconds 4
    Check 'save-exists-after-restart' ((Peek $i2.ProcessId 'save_exists') -eq 1) "save_exists $(Peek $i2.ProcessId 'save_exists'), app_state $(Peek $i2.ProcessId 'app_state')"
    Press $i2.ProcessId @('Down'); Start-Sleep -Milliseconds 500
    Check 'cursor-on-continue' ((Peek $i2.ProcessId 'title_cursor') -eq 1) "title_cursor $(Peek $i2.ProcessId 'title_cursor')"
    Press $i2.ProcessId @('Start'); Start-Sleep -Seconds 4
    Check 'continue-resumes-saved-room' ((Peek $i2.ProcessId 'app_state') -eq 1 -and (Peek $i2.ProcessId 'current_room') -eq $room) "app_state $(Peek $i2.ProcessId 'app_state'), room $(Peek $i2.ProcessId 'current_room') (saved $room)"
}
finally { $s = Stop-BrokenNesInstance -ProcessId $i2.ProcessId; Write-Host "closed run 2 ($($s.Method))" }

if (-not $KeepSave) {
    foreach ($f in $new) { if (-not $existing.ContainsKey($f.FullName)) { Remove-Item -LiteralPath $f.FullName; Write-Host "removed the test's own save file $($f.Name)" } }
}
$failed = @($results.Keys | Where-Object { -not $results[$_] })
Write-Host ('vrun-save-check: {0} of {1} checks pass' -f ($results.Count - $failed.Count), $results.Count)
if ($failed.Count) { exit 1 } else { exit 0 }
