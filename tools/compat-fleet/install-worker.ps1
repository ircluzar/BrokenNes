# install-worker.ps1 - the ONE script a worker box runs to install or update itself (Windows PowerShell 5.1 is enough).
#
#   From the box, in an elevated-or-not PowerShell (no admin needed unless -RegisterTask asks for "run whether logged on or not"):
#     powershell -ExecutionPolicy Bypass -File \<FleetRoot>\install-worker.ps1   (copy it to the box with the fleet's own sync, or from the new NAS share) -Slots 2 -RegisterTask
#   Update = run it again (it takes whatever current.json pins; the old version stays one round for rollback).
#   Simulate on the coordinator PC:  install-worker.ps1 -DryRun -InstallDir <scratch> -FleetRoot <scratch fleet> -QueueRoot <scratch queue>
#
# NAS retired 2026-11 (T:/X: gone). Defaults are now C:\BrokenNes-work\compat-fleet\{fleet,queue} (override: BROKENNES_COMPAT_HOME or
#   -FleetRoot/-QueueRoot). A worker on ANOTHER box can only read them if they are reachable from that box (new NAS, or the fleet's own
#   sync); this script does not create a share. NAS drive maps are now opt-in: -MapShares.
# Steps: 1. (only with -MapShares) map X: (ROMs, read only) and T: (fleet + queue) from the NAS if they are not mapped already
#        2. copy the pinned release + reference emulators to <InstallDir>\versions\<version> and check every sha256 in the manifest
#        3. write <InstallDir>\worker.json (box, slots, paths) and register the box with the queue
#        4. optional: a scheduled task "BrokenNes Compat Worker" that starts the runner at logon and restarts it if it stops
# It never stores a password: drive maps reuse the credentials Windows already has for \\STORAGE (enter them once with the
# "net use" prompt or Credential Manager, see docs/projects/compat-fleet.md).
param(
    [string]$Nas = '\\STORAGE',
    [string]$RomShare = 'Software',            # mapped as X:  (job ROM paths are X:\EMULATION\...)
    [string]$WorkShare = 'Work',               # mapped as T:
    [string]$FleetRoot = (Join-Path $(if ($env:BROKENNES_COMPAT_HOME) { $env:BROKENNES_COMPAT_HOME } else { 'C:\BrokenNes-work\compat-fleet' }) 'fleet'),
    [string]$QueueRoot = (Join-Path $(if ($env:BROKENNES_COMPAT_HOME) { $env:BROKENNES_COMPAT_HOME } else { 'C:\BrokenNes-work\compat-fleet' }) 'queue'),
    [string]$InstallDir = 'C:\CompatWorker',
    [ValidateRange(1, 2)][int]$Slots = 1,
    [string]$Box = $env:COMPUTERNAME,
    [string[]]$Consoles = @(),                 # empty = every console
    [string]$Version = '',                     # empty = current.json's pin
    [switch]$MapShares,                        # old NAS design: net use X:/T: from $Nas (needs a NAS to exist)
    [switch]$RegisterTask,
    [switch]$Start,                            # start the runner now in this window
    [switch]$DryRun                            # no drive maps, no scheduled task, box name "<name>-sim"
)
$ErrorActionPreference = 'Stop'
function Step($m) { Write-Host "== $m" -ForegroundColor Cyan }
if ($DryRun -and $Box -notmatch '-sim$') { $Box = "$Box-sim" }

# 1. shares
Step 'shares'
if (-not $MapShares) { Write-Host 'share maps skipped (use -MapShares once a NAS exists)' }
foreach ($m in $(if ($MapShares) { @(@{ d = 'X:'; s = "$Nas\$RomShare" }, @{ d = 'T:'; s = "$Nas\$WorkShare" }) } else { @() })) {
    $have = $null; try { $have = (Get-PSDrive -Name $m.d.TrimEnd(':') -ErrorAction Stop).DisplayRoot } catch {}
    if ($have -eq $m.s) { Write-Host "$($m.d) already $($m.s)"; continue }
    if ($DryRun) { Write-Host "dry run: would run  net use $($m.d) $($m.s) /persistent:yes  (now: '$have')"; continue }
    if ($have) { throw "$($m.d) is mapped to '$have', not $($m.s): unmap it or pick other letters by hand" }
    & net use $m.d $m.s /persistent:yes
    if ($LASTEXITCODE -ne 0) { throw "net use $($m.d) $($m.s) failed: store the NAS credentials first (cmdkey /add:STORAGE /user:<user> /pass)" }
}

# 2. pinned release
Step 'release'
if (-not (Test-Path (Join-Path $FleetRoot 'current.json')) -and -not $Version) { throw "no fleet release folder at $FleetRoot (current.json missing). This box cannot see the coordinator's fleet folder: run stage-release.ps1 on the coordinator, then make that folder reachable here (the fleet's own sync, or the new NAS share, then pass -FleetRoot/-QueueRoot or set BROKENNES_COMPAT_HOME). No Windows share is created by these scripts." }
if (-not $Version) { $Version = (Get-Content -Raw (Join-Path $FleetRoot 'current.json') | ConvertFrom-Json).version }
$src = Join-Path $FleetRoot "releases\$Version"
$manifest = Get-Content -Raw (Join-Path $src 'manifest.json') | ConvertFrom-Json
$dst = Join-Path $InstallDir "versions\$Version"
New-Item -ItemType Directory -Force -Path $dst | Out-Null
& robocopy $src $dst /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "copy of $src failed (robocopy $LASTEXITCODE)" }
Import-Module (Join-Path $dst 'runner\CompatQueue.psm1') -Force -DisableNameChecking
$bad = @()
foreach ($f in $manifest.files) {
    $p = Join-Path $dst $f.path
    if (-not (Test-Path $p) -or (Get-FileDigest -Path $p -Algorithm SHA256) -ne $f.sha256) { $bad += $f.path }
}
if ($bad.Count) { throw "$($bad.Count) file(s) do not match the manifest (first: $($bad[0])): the copy is damaged or the release changed under us; rerun" }
Write-Host "$Version ok: $(@($manifest.files).Count) files checked"
foreach ($e in $manifest.emulators) {
    $es = Join-Path $FleetRoot $e.path; $ed = Join-Path $InstallDir $e.path
    & robocopy $es $ed /E /XO /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "copy of $($e.name) failed (robocopy $LASTEXITCODE)" }
    if ($e.exe -and -not (Test-Path (Join-Path $ed $e.exe))) { throw "$($e.name): $($e.exe) missing after copy" }
    Write-Host "$($e.name) ok"
}
# keep the newest two versions (rollback = rerun with -Version <old>)
Get-ChildItem (Join-Path $InstallDir 'versions') -Directory | Sort-Object LastWriteTime -Descending | Select-Object -Skip 2 | ForEach-Object { Remove-Item -Recurse -Force $_.FullName }

# 3. config + registration
Step 'register'
$cfg = [ordered]@{ schema = 'compat-worker-config/1'; box = $Box; slots = $Slots; version = $Version; consoles = $Consoles
                   queueRoot = $QueueRoot; workDir = (Join-Path $InstallDir 'work')
                   workshop = (Join-Path $dst $manifest.workshop); mcp = (Join-Path $dst $manifest.mcp); runner = (Join-Path $dst $manifest.runner)
                   emulators = @($manifest.emulators | ForEach-Object { @{ name = $_.name; exe = (Join-Path (Join-Path $InstallDir $_.path) $_.exe) } })
                   installedAt = (Get-Date).ToUniversalTime().ToString('o'); dryRun = [bool]$DryRun }
$cfgPath = Join-Path $InstallDir 'worker.json'
$cfg | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $cfgPath
Import-Module (Join-Path $dst 'runner\CompatQueue.psm1') -Force -DisableNameChecking
if (-not (Test-Path (Join-Path $QueueRoot 'queue.json'))) { throw "no queue at $QueueRoot (run queue.ps1 init on the coordinator)" }
Register-CompatWorker -Root $QueueRoot -Box $Box -Slots $Slots -Version $Version -Info @{ host = $env:COMPUTERNAME; cpus = [Environment]::ProcessorCount
    ramGB = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB); dryRun = [bool]$DryRun } | Out-Null
Write-Host "registered $Box ($Slots slot(s)) with $QueueRoot"

# 4. scheduled task
if ($RegisterTask -and -not $DryRun) {
    Step 'scheduled task'
    $act = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$($cfg.runner)`" -ConfigPath `"$cfgPath`""
    $trg = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
    $set = New-ScheduledTaskSettingsSet -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -StartWhenAvailable
    Register-ScheduledTask -TaskName 'BrokenNes Compat Worker' -Action $act -Trigger $trg -Settings $set -Force | Out-Null
    Write-Host 'task "BrokenNes Compat Worker" registered (starts at logon; update = rerun this script, then: Stop-ScheduledTask / Start-ScheduledTask "BrokenNes Compat Worker")'
} elseif ($RegisterTask) { Write-Host 'dry run: scheduled task not registered' }

if ($Start) { Step 'runner'; & (Join-Path $dst 'runner\worker.ps1') -ConfigPath $cfgPath -DryRun:$DryRun }
Write-Host "done. runner: powershell -File `"$($cfg.runner)`" -ConfigPath `"$cfgPath`""
exit 0
