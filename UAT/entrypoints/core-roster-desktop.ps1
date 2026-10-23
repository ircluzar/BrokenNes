<#
.SYNOPSIS
  The stock / enhanced core roster in the real desktop app: a config.json made by an older build (the retired Game Boy picture chip DMGX) is moved to the
  enhanced chip DMGS and written back, the menus the app serves are in the new order with the new labels, and the Game Boy look's toggles reach a Sega game
  drawn by a Game Boy chip.

.DESCRIPTION
  Each run starts the desktop app with its own throw-away config folder (BROKENNES_CONFIG_DIR: the user's config.json is never read or rewritten):

    1. migration: config.json says selectedPpuCore "DMGX", the SNES's picture "NES:DMGX", the Genesis's "NES:DMGXI" (an even older chip, with inverted sprites)
       -> after start-up the file says DMGS, NES:DMGS, NES:DMGS and gbInvertSprites true; GET /api/console reports the NES on PPU_DMGS.
    2. negative control: config.json says selectedPpuCore "NOSUCH" (an id nobody knows) -> the app falls back to FIX: what DMGX would have become without the migration.
    3. the menus: GET /api/console/cores for the NES lists FIX (stock), FIX (enhanced), then the other consoles' best cores, then the gimmick cores; the labels say stock /
       enhanced. POST /api/console/core/ppu/DMGX (an old id through the API) selects DMGS.

  Each app is closed by PID (never by name). Exit 0 = every check passed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$WorkDir = (Join-Path ([System.IO.Path]::GetTempPath()) ('bn-roster-' + [guid]::NewGuid().ToString('N').Substring(0, 8)))
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $repo 'UAT\lib\ApiClient.ps1')
if (-not (Test-Path -LiteralPath $Exe)) { Write-Host "no desktop exe at $Exe"; exit 2 }
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

$fail = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail) {
    Write-Host ("[{0}] {1} - {2}" -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Name, $Detail)
    if (-not $Ok) { $script:fail++ }
}

function Start-Isolated([string]$ConfigJson) {
    $cfgDir = Join-Path $WorkDir ('config-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
    New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $cfgDir 'config.json'), $ConfigJson)
    $saved = @{}
    $all = @{ BROKENNES_CONFIG_DIR = $cfgDir; BROKENNES_DEV = $null; BROKENNES_NO_DIALOGS = '1' }
    foreach ($k in $all.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $all[$k]) }
    try { $inst = Start-BrokenNesInstance -ExePath $Exe } finally { foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) } }
    Start-Sleep -Seconds 4
    [PSCustomObject]@{ Inst = $inst; ConfigPath = (Join-Path $cfgDir 'config.json') }
}

function Read-Config([string]$Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }

$base = '"bootToEmulator": true, "legacyProgressionLocks": false, "selectedConsole": "nes", "selectedCpuCore": "FIX", "selectedApuCore": "FIX"'
try {
    # 1. migration of an older config.json
    $r = Start-Isolated ('{ ' + $base + ', "selectedPpuCore": "DMGX", "gbInvertSprites": false, "consoleCores": { "snes": { "ppu": "NES:DMGX" }, "md": { "ppu": "NES:DMGXI" }, "sms": { "ppu": "NES:FIXS" } } }')
    try {
        $status = (Invoke-BrokenNesApi -ProcessId $r.Inst.ProcessId -Path '/api/console').status
        $cfg = Read-Config $r.ConfigPath
        Check 'migration: the NES picture pick DMGX became DMGS in config.json' ($cfg.selectedPpuCore -eq 'DMGS') "selectedPpuCore=$($cfg.selectedPpuCore)"
        Check 'migration: the SNES pick NES:DMGX became NES:DMGS' ($cfg.consoleCores.snes.ppu -eq 'NES:DMGS') "snes ppu=$($cfg.consoleCores.snes.ppu)"
        Check 'migration: the retired NES:DMGXI became NES:DMGS with the sprite inversion on' ($cfg.consoleCores.md.ppu -eq 'NES:DMGS' -and $cfg.gbInvertSprites -eq $true) "md ppu=$($cfg.consoleCores.md.ppu) gbInvertSprites=$($cfg.gbInvertSprites)"
        Check 'migration: a current pick (NES:FIXS) is left as it is' ($cfg.consoleCores.sms.ppu -eq 'NES:FIXS') "sms ppu=$($cfg.consoleCores.sms.ppu)"
        Check 'migration: the NES runs on the enhanced Game Boy chip' ("$($status.ppu)" -eq 'PPU_DMGS') "ppu=$($status.ppu)"
    } finally { [void](Stop-BrokenNesInstance -ProcessId $r.Inst.ProcessId) }

    # 2. negative control: an id nobody knows is reset to the default (what DMGX would have been without the migration)
    $r = Start-Isolated ('{ ' + $base + ', "selectedPpuCore": "NOSUCH" }')
    try {
        $status = (Invoke-BrokenNesApi -ProcessId $r.Inst.ProcessId -Path '/api/console').status
        $cfg = Read-Config $r.ConfigPath
        Check 'negative control: an unknown picture id falls back to FIX (the loss the migration prevents)' ($cfg.selectedPpuCore -eq 'FIX' -and "$($status.ppu)" -eq 'PPU_FIX') "selectedPpuCore=$($cfg.selectedPpuCore) ppu=$($status.ppu)"
    } finally { [void](Stop-BrokenNesInstance -ProcessId $r.Inst.ProcessId) }

    # 3. the menus the app serves, and an old id through the API
    $r = Start-Isolated ('{ ' + $base + ', "selectedPpuCore": "FIX" }')
    try {
        $menus = (Invoke-BrokenNesApi -ProcessId $r.Inst.ProcessId -Path '/api/console/cores').menus
        $ppu = @($menus.ppu)
        $head = ($ppu | Select-Object -First 7 | ForEach-Object { $_.id }) -join ','
        Check 'menus: the NES picture menu starts FIX, FIXS, SNES, DMG, DMGS, SMS, MD' ($head -eq 'FIX,FIXS,SNES,DMG,DMGS,SMS,MD') $head
        $labels = @($ppu | Select-Object -First 5 | ForEach-Object { $_.label })
        Check 'menus: the labels say FIX (stock), FIX (enhanced), Game Boy (stock), Game Boy (enhanced)' ($labels[0] -like 'FIX (stock)*' -and $labels[1] -like 'FIX (enhanced)*' -and $labels[3] -like 'Game Boy (stock)*' -and $labels[4] -like 'Game Boy (enhanced)*') ($labels -join ' | ')
        Check 'menus: no DMGX on offer' (-not ($ppu | Where-Object { $_.id -eq 'DMGX' })) ''
        $set = Invoke-BrokenNesApi -ProcessId $r.Inst.ProcessId -Method POST -Path '/api/console/core/ppu/DMGX'
        Start-Sleep -Seconds 2
        $status = (Invoke-BrokenNesApi -ProcessId $r.Inst.ProcessId -Path '/api/console').status
        $cfg = Read-Config $r.ConfigPath
        Check 'API: selecting the old id DMGX selects the enhanced Game Boy chip (DMGS) and stores DMGS' ("$($status.ppu)" -eq 'PPU_DMGS' -and $cfg.selectedPpuCore -eq 'DMGS') "ppu=$($status.ppu) stored=$($cfg.selectedPpuCore) response=$($set | ConvertTo-Json -Compress)"
    } finally { [void](Stop-BrokenNesInstance -ProcessId $r.Inst.ProcessId) }
}
catch { Check 'the script ran to the end' $false $_.Exception.Message }

Write-Host ""
if ($fail -eq 0) { Write-Host 'core-roster-desktop: all checks passed'; exit 0 } else { Write-Host "core-roster-desktop: $fail check(s) failed"; exit 1 }
