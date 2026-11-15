# queue.ps1 - the coordinator side of the compat queue (runs on this PC).
#
#   queue.ps1 init     [-QueueRoot T:\BrokenDev\CompatQueue]
#   queue.ps1 enqueue  -Console nes -Tier T1 -RomDir "X:\EMULATION\Nintendo Entertainment System" [-Filter *.nes] [-Limit 50] [-Priority 5]
#   queue.ps1 enqueue  -Console snes -Tier T1 -RomList roms.txt          (one path per line)
#   queue.ps1 status                                                      jobs per state + every box's heartbeat age
#   queue.ps1 reap                                                        stale leases back to pending / failed
#   queue.ps1 ingest   [-CatalogInbox T:\BrokenDev\CompatCatalog\inbox]   validated results -> the catalog's inbox
#   queue.ps1 stop-box -Box BOX3                                          ask one box to finish its jobs and exit
#   queue.ps1 retry-failed                                                failed jobs back to pending (attempts reset)
#   queue.ps1 serve    [-IntervalSec 60]                                  loop: reap + ingest, for leaving running
param(
    [Parameter(Position = 0, Mandatory)][ValidateSet('init', 'enqueue', 'status', 'reap', 'ingest', 'stop-box', 'retry-failed', 'serve')][string]$Command,
    [string]$QueueRoot = 'T:\BrokenDev\CompatQueue',
    [string]$Console = '',
    [ValidateSet('T1', 'T2', 'T3')][string]$Tier = 'T1',
    [string]$RomDir = '',
    [string]$RomList = '',
    [string]$Filter = '*',
    [int]$Limit = 0,
    [int]$Priority = 5,
    [switch]$Force,
    [string]$CatalogInbox = 'T:\BrokenDev\CompatCatalog\inbox',
    [string]$Box = '',
    [int]$IntervalSec = 60
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'CompatQueue.psm1') -Force -DisableNameChecking

$romExt = @{ nes = '.nes', '.unf', '.fds'; snes = '.sfc', '.smc'; gb = '.gb'; gbc = '.gbc', '.gb'; sms = '.sms', '.7z'; gg = '.gg', '.7z'; md = '.md', '.gen', '.bin', '.smd' }

function Invoke-Ingest {
    # The interface to the catalog lane (see docs/projects/compat-fleet.md, "Catalog interface"): every validated result record is
    # copied as <inbox>\<console>\<sha1>.<tier>.<jobid>.json; the catalog importer owns everything after that. Ingested records are moved
    # to ingested\ so a record is delivered once; delivering twice is harmless (same file name, same content).
    $n = 0
    foreach ($f in (Get-ChildItem (Join-Path $QueueRoot 'results') -Filter *.json -ErrorAction SilentlyContinue)) {
        $r = Read-Json $f.FullName
        $problem = Test-CompatResult $r $null
        if ($problem) { Write-Warning "$($f.Name): $problem (left in results\)"; continue }
        $dir = Join-Path $CatalogInbox $r.console
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        Copy-Item -Force $f.FullName (Join-Path $dir ('{0}.{1}.{2}.json' -f $r.rom.sha1, $r.tier, $r.jobId))
        Move-Item -Force $f.FullName (Join-Path $QueueRoot "ingested\$($f.Name)")
        $n++
    }
    "ingested $n result(s) into $CatalogInbox"
}

switch ($Command) {
    'init' { Initialize-CompatQueue -Root $QueueRoot | Out-Null; "queue ready at $QueueRoot" }
    'enqueue' {
        if (-not $Console) { throw '-Console is required (nes, snes, gb, gbc, sms, gg, md)' }
        $c = $Console.ToLowerInvariant(); if ($c -eq 'genesis') { $c = 'md' }
        if (@('n64', 'x86', 'pc', 'dos') -contains $c) { throw "$c is out of the catalog's scope" }
        $paths = @()
        if ($RomList) { $paths = Get-Content $RomList | Where-Object { $_.Trim() } }
        elseif ($RomDir) {
            $ext = $romExt[$c]
            $paths = Get-ChildItem -LiteralPath $RomDir -Filter $Filter -File -Recurse | Where-Object { -not $ext -or $ext -contains $_.Extension.ToLowerInvariant() } | Sort-Object Name | ForEach-Object FullName
        } else { throw '-RomDir or -RomList is required' }
        if ($Limit -gt 0) { $paths = @($paths | Select-Object -First $Limit) }
        $added = 0; $skipped = 0
        foreach ($p in $paths) {
            $r = New-CompatJob -Root $QueueRoot -RomPath $p -Console $c -Tier $Tier -Priority $Priority -Force:$Force
            if ($r.added) { $added++ } else { $skipped++ }
        }
        "enqueued $added, already queued/done $skipped ($c $Tier)"
    }
    'status' {
        $s = Get-CompatQueueStatus -Root $QueueRoot
        $s.jobs | Format-Table -AutoSize | Out-String
        if ($s.workers.Count) { $s.workers | Format-Table -AutoSize | Out-String } else { 'no workers registered' }
    }
    'reap' { $r = @(Invoke-CompatReaper -Root $QueueRoot); if ($r.Count) { $r | Format-Table -AutoSize | Out-String } else { 'nothing stale' } }
    'ingest' { Invoke-Ingest }
    'stop-box' { if (-not $Box) { throw '-Box is required' }; Set-Content (Join-Path $QueueRoot "workers\$Box.stop") (Get-Date -Format o); "asked $Box to stop after its running jobs (delete workers\$Box.stop to let it run again)" }
    'retry-failed' {
        $n = 0
        foreach ($f in (Get-ChildItem (Join-Path $QueueRoot 'jobs\failed') -Filter *.json -ErrorAction SilentlyContinue)) {
            $j = Read-Json $f.FullName; $j.attempts = 0
            Write-JsonAtomic (Join-Path $QueueRoot "jobs\pending\$($f.Name)") $j; Remove-Item $f.FullName; $n++
        }
        "re-queued $n failed job(s)"
    }
    'serve' {
        while ($true) {
            $r = @(Invoke-CompatReaper -Root $QueueRoot); foreach ($x in $r) { "{0:u} reaped {1} -> {2} ({3})" -f (Get-Date), $x.id, $x.to, $x.reason }
            $i = Invoke-Ingest; if ($i -notmatch '^ingested 0 ') { "{0:u} {1}" -f (Get-Date), $i }
            Start-Sleep -Seconds $IntervalSec
        }
    }
}
