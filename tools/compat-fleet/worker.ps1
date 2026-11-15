# worker.ps1 - the agent runner on a worker box (or on this PC with -DryRun).
# Pulls jobs from the queue, runs up to -Slots of them at once (each in its own process), heartbeats, enforces the tier
# timeout, and hands the result record back. Windows PowerShell 5.1 compatible.
#
#   powershell -File worker.ps1                       uses C:\CompatWorker\worker.json written by install-worker.ps1
#   pwsh -File worker.ps1 -DryRun -QueueRoot <dir> -Workshop <exe> -Slots 2 -ExitWhenEmpty     a simulated box on this PC
param(
    [string]$ConfigPath = 'C:\CompatWorker\worker.json',
    [string]$QueueRoot = '',
    [string]$Workshop = '',
    [string]$Box = '',
    [int]$Slots = 0,
    [string]$WorkDir = '',
    [string[]]$Consoles = @(),
    [string[]]$Tiers = @(),
    [int]$MaxJobs = 0,              # stop after this many jobs (0 = forever)
    [int]$HeartbeatSec = 30,
    [switch]$ExitWhenEmpty,
    [switch]$DryRun                 # simulate a box on this PC: box name gets a "-sim" suffix, nothing outside -WorkDir / -QueueRoot is touched
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Core' -and $env:PSModulePath) {
    # started from PowerShell 7 (the dry run, a hand launch): drop PS7's module folders, or the Windows PowerShell children load the
    # wrong Microsoft.PowerShell.Utility and lose Get-FileHash & co.
    $env:PSModulePath = (($env:PSModulePath -split ';') | Where-Object { $_ -and $_ -notmatch '\\PowerShell\\7|\\Documents\\PowerShell\\Modules' }) -join ';'
}
$here =Split-Path -Parent $MyInvocation.MyCommand.Path
Import-Module (Join-Path $here 'CompatQueue.psm1') -Force -DisableNameChecking

$cfg = $null
if (Test-Path $ConfigPath) { $cfg = Get-Content -Raw $ConfigPath | ConvertFrom-Json }
function Pick($a, $b, $c) { if ($a) { return $a }; if ($b) { return $b }; $c }
$QueueRoot = Pick $QueueRoot $(if ($cfg) { $cfg.queueRoot }) ''
$Workshop = Pick $Workshop $(if ($cfg) { $cfg.workshop }) ''
$Box = Pick $Box $(if ($cfg) { $cfg.box }) $env:COMPUTERNAME
if ($DryRun -and $Box -notmatch '-sim$') { $Box = "$Box-sim" }
$Slots = [int](Pick $Slots $(if ($cfg) { $cfg.slots }) 1)
$WorkDir = Pick $WorkDir $(if ($cfg) { $cfg.workDir }) (Join-Path $env:TEMP 'CompatWorker')
$version = ''; if ($cfg) { $version = $cfg.version }
if (-not $Consoles.Count -and $cfg -and $cfg.consoles) { $Consoles = @($cfg.consoles) }
if (-not $QueueRoot -or -not (Test-Path (Join-Path $QueueRoot 'queue.json'))) { throw "queue not found at '$QueueRoot'" }
if (-not $Workshop -or -not (Test-Path $Workshop)) { throw "Workshop exe not found at '$Workshop'" }
if ($Slots -lt 1 -or $Slots -gt 2) { throw 'Slots must be 1 or 2 (an i3-7100T has 2 cores / 4 threads)' }
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$qcfg = Get-QueueConfig $QueueRoot
$ps = (Get-Process -Id $PID).Path
$log = Join-Path $QueueRoot "logs\$Box.log"
function Log($m) { $line = '{0:u} {1} {2}' -f (Get-Date), $Box, $m; Write-Host $line; Add-Content -Path $log -Value $line }

Register-CompatWorker -Root $QueueRoot -Box $Box -Slots $Slots -Version $version -Info @{ dryRun = [bool]$DryRun; host = $env:COMPUTERNAME; pid = $PID; cpus = [Environment]::ProcessorCount } | Out-Null
Log "up: slots=$Slots queue=$QueueRoot workshop=$Workshop dryRun=$([bool]$DryRun)"

$running = @{}          # job id -> @{ job; proc; started; timeout; result; jobFile }
$taken = 0; $lastBeat = [datetime]::MinValue; $idleSince = $null
while ($true) {
  try {
    # 1. finished or overdue children
    foreach ($id in @($running.Keys)) {
        $r = $running[$id]
        $overdue = ((Get-Date) - $r.started).TotalSeconds -gt ($r.timeout + 60)
        if (-not $r.proc.HasExited -and -not $overdue) { continue }
        if (-not $r.proc.HasExited) { try { $r.proc.Kill() } catch {}; Log "$id killed: over the $($r.timeout)s timeout" }
        $res = $null; if (Test-Path $r.result) { try { $res = Get-Content -Raw $r.result | ConvertFrom-Json } catch {} }
        if ($res) { $o = Complete-CompatJob -Root $QueueRoot -Box $Box -Job $r.job -Result $res; Log "$id -> $o ($($res.grade) / $($res.t1.verdict): $($res.t1.why))" }
        else { $o = Fail-CompatJob -Root $QueueRoot -Box $Box -Job $r.job -Reason 'runner produced no result'; Log "$id -> no result, $o" }
        $running.Remove($id)
    }
    # 2. heartbeat
    if (((Get-Date) - $lastBeat).TotalSeconds -ge $HeartbeatSec) {
        $state = 'idle'; if ($running.Count) { $state = 'running' }
        Send-CompatHeartbeat -Root $QueueRoot -Box $Box -Running @($running.Keys) -State $state; $lastBeat = Get-Date
    }
    # 3. fill free slots
    $stopTaking = ($MaxJobs -gt 0 -and $taken -ge $MaxJobs) -or (Test-Path (Join-Path $QueueRoot "workers\$Box.stop"))
    $gotOne = $false
    while (-not $stopTaking -and $running.Count -lt $Slots) {
        $job = Get-NextCompatJob -Root $QueueRoot -Box $Box -Consoles $Consoles -Tiers $Tiers
        if (-not $job) { break }
        $gotOne = $true; $taken++
        $jobDir = Join-Path $WorkDir ("{0}\a{1}" -f $job.id, $job.attempts)
        New-Item -ItemType Directory -Force -Path $jobDir | Out-Null
        $jobFile = Join-Path $jobDir 'job.json'; $job | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 $jobFile
        $resultFile = Join-Path $jobDir 'result.json'
        $timeout = [int]$qcfg.tierTimeoutSec.($job.tier); if (-not $timeout) { $timeout = 600 }
        $runner = Join-Path $here ("tiers\{0}.ps1" -f $job.tier)
        if (-not (Test-Path $runner)) { $o = Fail-CompatJob -Root $QueueRoot -Box $Box -Job $job -Reason "no $($job.tier) runner installed"; Log "$($job.id) -> no runner, $o"; continue }
        $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$runner`"", '-JobPath', "`"$jobFile`"", '-ResultPath', "`"$resultFile`"",
               '-Workshop', "`"$Workshop`"", '-OutDir', "`"$jobDir`"", '-Box', $Box, '-TimeoutSec', "$timeout")
        if ($version) { $a += @('-Version', $version) }
        $proc = Start-Process -FilePath $ps -ArgumentList $a -NoNewWindow -PassThru -RedirectStandardOutput (Join-Path $jobDir 'runner.out.txt') -RedirectStandardError (Join-Path $jobDir 'runner.err.txt')
        $running[$job.id] = @{ job = $job; proc = $proc; started = Get-Date; timeout = $timeout; result = $resultFile }
        Log "$($job.id) started: $($job.console) $($job.tier) $($job.rom.name) (attempt $($job.attempts))"
        Send-CompatHeartbeat -Root $QueueRoot -Box $Box -Running @($running.Keys); $lastBeat = Get-Date
    }
    if ($running.Count -eq 0) {
        if ($stopTaking) { break }
        if (-not $gotOne) { if (-not $idleSince) { $idleSince = Get-Date }; if ($ExitWhenEmpty) { break } } else { $idleSince = $null }
    }
  } catch {
    # a share hiccup or a racing writer must not kill the box: log, back off, carry on (the leases keep the jobs safe)
    try { Log "loop error: $($_.Exception.Message)" } catch {}
    Start-Sleep -Seconds 5
  }
    Start-Sleep -Milliseconds 500
}
Send-CompatHeartbeat -Root $QueueRoot -Box $Box -Running @() -State 'stopped'
Log "down: $taken job(s) taken"
