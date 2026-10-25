<#
.SYNOPSIS
  The MCP server's live sessions against the real desktop app (BrokenDev milestone D3). Exit 0 = pass.

.DESCRIPTION
  Starts two BrokenNes desktop apps, each with its own scratch settings folder (BROKENNES_CONFIG_DIR: the user's config and saves are
  never read or written), on the debugger's NES test ROM:
    A  the default config (no "enableDebuggerControl")
    B  a config with "enableDebuggerControl": true
  then runs the scripted MCP client (BrokenNes.Mcp.exe --live-test), which starts the server over stdio and checks: live sessions read
  freely; a read-only session refuses control (even on B); a control session on A is refused by the app; a control session on B breaks,
  steps, writes and presses; closing it lets the game go; nothing saves. Only the two PIDs started here are stopped (never by name).

.EXAMPLE
  pwsh -File BrokenDev\tests\mcp-live.ps1 -App C:\...\BrokenNes.Windows.exe -Mcp C:\...\BrokenNes.Mcp.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$App,
    [Parameter(Mandatory)][string]$Mcp,
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\uat_mcp_live')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $repo 'UAT\lib\ApiClient.ps1')

function Start-Scratch([string]$name, [string]$configJson) {
    $cfg = Join-Path $WorkDir "config_$name"
    if (Test-Path -LiteralPath $cfg) { Remove-Item -LiteralPath $cfg -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $cfg | Out-Null
    if ($configJson) { Set-Content -LiteralPath (Join-Path $cfg 'config.json') -Value $configJson -Encoding UTF8 }
    $env:BROKENNES_CONFIG_DIR = $cfg
    $i = Start-BrokenNesInstance -ExePath $App
    Start-Sleep -Seconds 4   # the app loads its embedded test ROM a few seconds after the API answers
    $i
}

function Load-Rom([int]$procId, [string]$rom) {
    Invoke-BrokenNesApi -ProcessId $procId -Method POST -Path '/api/emulator/load-rom' -Body @{ path = $rom } -TimeoutSec 60 | Out-Null
    $end = (Get-Date).AddSeconds(15)
    do { Start-Sleep -Milliseconds 300; $s = Invoke-BrokenNesApi -ProcessId $procId -Method GET -Path '/api/debug/state' -TimeoutSec 30 } while ((-not $s.success -or $s.console -ne 'nes') -and (Get-Date) -lt $end)
    Start-Sleep -Milliseconds 500
}

$saved = $env:BROKENNES_CONFIG_DIR
$a = $null; $b = $null; $code = 1
try {
    New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
    $roms = Join-Path $WorkDir 'roms'
    & $Mcp --write-test-roms $roms | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "BrokenNes.Mcp.exe --write-test-roms failed ($LASTEXITCODE)" }
    $rom = Join-Path $roms 'debugtest.nes'
    $a = Start-Scratch 'a' $null
    $b = Start-Scratch 'b' '{ "enableDebuggerControl": true }'
    Write-Host "started A (control off) PID $($a.ProcessId), B (control on) PID $($b.ProcessId)"
    Load-Rom $a.ProcessId $rom
    Load-Rom $b.ProcessId $rom
    & $Mcp --live-test --ro-pid $a.ProcessId --ctl-pid $b.ProcessId --roms $roms --repo $repo | Out-Host
    $code = $LASTEXITCODE
}
catch { Write-Host "[FAIL] run - $($_.Exception.Message)"; $code = 1 }
finally {
    foreach ($i in @($a, $b)) { if ($i) { Stop-BrokenNesInstance -ProcessId $i.ProcessId | Out-Null } }
    $env:BROKENNES_CONFIG_DIR = $saved
}
exit $code
