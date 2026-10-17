<#
.SYNOPSIS
  Does the BrokenNes desktop app show nothing of BrokenDev unless it is switched on, and does the BrokenDev menu open a built ROM?

.DESCRIPTION
  Starts the real desktop app three times, each with its own throw-away config folder (BROKENNES_CONFIG_DIR: the user's config.json is never
  read or rewritten), and looks at the native menu bar through UI Automation:

    1. neither BROKENNES_DEV nor the config flag      -> there must be NO "BrokenDev" menu (the negative control)
    2. BROKENNES_DEV=1                                -> a "BrokenDev" menu exists and lists a build record's project
    3. "brokenDevEnabled": true in config.json only    -> the same (the config flag works without the environment variable)

  Then, in run 2, it invokes the menu item and checks through the app's HTTP API that the ROM it names is the loaded ROM.
  Each app is closed by PID (never by name). Exit 0 = every check passed.

  Needs a built desktop exe (BROKENDEV_APP, -Exe, or 'brokendev.ps1 build-app'). Not run by run-tests.ps1 (it opens windows).
#>
[CmdletBinding()]
param(
    [string]$Exe = $env:BROKENDEV_APP,
    [string]$Rom = '',
    [string]$WorkDir = (Join-Path ([System.IO.Path]::GetTempPath()) ('bd-appsmoke-' + [guid]::NewGuid().ToString('N').Substring(0, 8)))
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $repo 'UAT\lib\ApiClient.ps1')
. (Join-Path $repo 'UAT\lib\UiaHelpers.ps1')
if (-not $Exe -or -not (Test-Path -LiteralPath $Exe)) { Write-Host 'no desktop exe: pass -Exe or set BROKENDEV_APP (brokendev.ps1 build-app builds one)'; exit 2 }
if (-not $Rom) { $Rom = Join-Path $repo 'Windows\Resources\test.nes' }
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

$fail = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail) {
    Write-Host ("[{0}] {1} - {2}" -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Name, $Detail)
    if (-not $Ok) { $script:fail++ }
}

# A BROKENDEV_HOME with one finished build record pointing at a real ROM, so the menu has an item to show.
$bdHome = Join-Path $WorkDir 'bdhome'
$outDir = Join-Path $bdHome 'out\smoke-project'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
Copy-Item -LiteralPath $Rom -Destination (Join-Path $outDir 'smoke.nes') -Force
$romPath = Join-Path $outDir 'smoke.nes'
$rec = @{ project = 'smoke-project'; title = 'BrokenDev smoke project'; ok = $true; outputs = @(@{ id = 'rom'; published = $romPath }) }
[System.IO.File]::WriteAllText((Join-Path $outDir 'build.json'), ($rec | ConvertTo-Json -Depth 5))

function Start-Isolated {
    param([hashtable]$EnvVars, [string]$ConfigJson = '')
    # BROKENNES_CONFIG_DIR moves config.json (EmulatorConfig and the BrokenDev gate both honour it): the user's own config is never read or rewritten.
    $cfgDir = Join-Path $WorkDir ('config-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
    New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
    if ($ConfigJson) { [System.IO.File]::WriteAllText((Join-Path $cfgDir 'config.json'), $ConfigJson) }
    $saved = @{}
    $all = @{ BROKENNES_CONFIG_DIR = $cfgDir; BROKENNES_DEV = $null; BROKENDEV_HOME = $bdHome; BROKENNES_NO_DIALOGS = '1' }
    foreach ($k in $EnvVars.Keys) { $all[$k] = $EnvVars[$k] }
    foreach ($k in $all.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $all[$k]) }
    try { $inst = Start-BrokenNesInstance -ExePath $Exe } finally { foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) } }
    Start-Sleep -Seconds 4
    $inst
}
function Get-TopMenus([int]$Pid0) {
    $root = Get-AppRoot -ProcessId $Pid0
    $bar = Get-NativeMenuBar -AppRoot $root
    if (-not $bar) { return @() }
    $kids = $bar.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    @($kids | ForEach-Object { $_.Current.Name } | Where-Object { $_ })
}

try {
    # 1. off
    $a = Start-Isolated -EnvVars @{}
    $menus = Get-TopMenus $a.ProcessId
    Check 'off: the menu bar has the normal menus' ($menus -contains 'Emulator') ("menus: " + ($menus -join ' | '))
    Check 'off (negative control): there is no BrokenDev menu' (-not ($menus | Where-Object { $_ -match 'BrokenDev' })) ("menus: " + ($menus -join ' | '))
    [void](Stop-BrokenNesInstance -ProcessId $a.ProcessId)

    # 2. environment variable
    $b = Start-Isolated -EnvVars @{ BROKENNES_DEV = '1' }
    $menus = Get-TopMenus $b.ProcessId
    Check 'BROKENNES_DEV=1: a BrokenDev menu exists' ([bool]($menus | Where-Object { $_ -match 'BrokenDev' })) ("menus: " + ($menus -join ' | '))
    $root = Get-AppRoot -ProcessId $b.ProcessId
    $bar = Get-NativeMenuBar -AppRoot $root
    $menu = Open-NativeMenu -MenuBar $bar -MenuName 'BrokenDev'
    $items = @()
    if ($menu) { $items = @($menu.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) }
    $item = $items | Where-Object { $_ -like 'Open BrokenDev smoke project*' } | Select-Object -First 1
    Check 'the menu lists the built project' ([bool]$item) ("items: " + ($items -join ' | '))
    if ($item) {
        $el = $menu.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $item)))
        [void](Click-Element $el)
        Start-Sleep -Seconds 3
        $cur = Get-BrokenNesCurrentRom -ProcessId $b.ProcessId
        Check 'clicking the item loads that ROM' ([bool]$cur.success -and ([string]$cur.path -ieq $romPath)) ("current rom: $($cur.path)")
    }
    [void](Stop-BrokenNesInstance -ProcessId $b.ProcessId)

    # 3. config flag only
    $c = Start-Isolated -EnvVars @{} -ConfigJson '{ "brokenDevEnabled": true }'
    $menus = Get-TopMenus $c.ProcessId
    Check 'config flag brokenDevEnabled: a BrokenDev menu exists' ([bool]($menus | Where-Object { $_ -match 'BrokenDev' })) ("menus: " + ($menus -join ' | '))
    [void](Stop-BrokenNesInstance -ProcessId $c.ProcessId)

    # 4. power-on RAM: the dev lever fills work RAM before the first frame; byte $07FC is $FF under the FCEUX pattern ($07FC and 4 is set).
    #    The probe ROM must not clear RAM (the repo's test.nes does): 16 KB of NROM that is just  SEI / JMP *  and never writes a byte.
    $idle = New-Object byte[] (16 + 16384 + 8192)
    $idle[0] = 0x4E; $idle[1] = 0x45; $idle[2] = 0x53; $idle[3] = 0x1A; $idle[4] = 1; $idle[5] = 1
    $idle[16] = 0x78; $idle[17] = 0x4C; $idle[18] = 0x01; $idle[19] = 0xC0       # SEI ; JMP $C001
    $idle[16 + 0x3FFC] = 0x00; $idle[16 + 0x3FFD] = 0xC0                          # reset vector -> $C000
    $idlePath = Join-Path $WorkDir 'idle.nes'
    [System.IO.File]::WriteAllBytes($idlePath, $idle)
    function Get-LoadedRamByte([hashtable]$EnvVars) {
        $i = Start-Isolated -EnvVars $EnvVars
        try {
            [void](Show-BrokenNesEmulator -ProcessId $i.ProcessId)
            [void](Load-BrokenNesRom -ProcessId $i.ProcessId -Path $idlePath)
            [void](Suspend-BrokenNesEmulation -ProcessId $i.ProcessId)
            Get-BrokenNesMemory -ProcessId $i.ProcessId -Domain 'System RAM' -Address 0x7FC
        } finally { [void](Stop-BrokenNesInstance -ProcessId $i.ProcessId) }
    }
    $def = Get-LoadedRamByte @{ BROKENNES_DEV = '1' }
    Check 'power-on RAM default: the FCEUX pattern ($07FC = $FF)' ($def -eq 255) "byte = $def"
    $zer = Get-LoadedRamByte @{ BROKENNES_DEV = '1'; BROKENDEV_POWERON_RAM = 'zeros' }
    Check 'power-on RAM zeros (BrokenDev on): $07FC = 0' ($zer -eq 0) "byte = $zer"
    $inert = Get-LoadedRamByte @{ BROKENDEV_POWERON_RAM = 'zeros' }
    Check 'NEGATIVE: the power-on RAM lever does nothing when BrokenDev is off ($07FC stays $FF)' ($inert -eq 255) "byte = $inert"
}
finally {
    try { Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}
if ($fail -eq 0) { Write-Host 'app-smoke: PASS'; exit 0 } else { Write-Host "app-smoke: $fail check(s) FAILED"; exit 1 }
