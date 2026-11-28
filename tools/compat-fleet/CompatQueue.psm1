# CompatQueue.psm1 - the Great Compatibility Catalog job queue (P0 scope item 6).
#
# A file-based queue on a folder this PC shares to the worker boxes (default C:\BrokenNes-work\compat-fleet\queue since the NAS died 2026-11,
# shared as \\<this PC>\CompatQueue). Plain files over SMB on purpose: no server process to keep alive,
# every state is visible in Explorer, and a claim is an atomic rename (two workers racing for one job:
# exactly one Move-Item wins, the other gets "file not found" and tries the next job).
#
#   jobs\pending\<id>.json        waiting (picked in name order: priority prefix, then enqueue time)
#   jobs\claimed\<id>.json        taken by a worker; lease\<id>.json holds worker + last beat
#   jobs\done\<id>.json           finished, result in results\<id>.json
#   jobs\failed\<id>.json         gave up after maxAttempts (timeouts, crashes of the runner itself)
#   lease\<id>.json               per-job lease, renewed by the worker every heartbeat
#   workers\<box>.json            box registration + heartbeat (slots, running jobs, version)
#   results\<id>.json             the worker's result record (see docs/projects/compat-fleet.md, "Result record")
#   ingested\<id>.json            result records the coordinator already pushed into the catalog
#
# Must run on Windows PowerShell 5.1 (the i3 boxes) as well as PowerShell 7: no ternaries, no ??.

Set-StrictMode -Version 2

$script:Dirs = 'jobs\pending', 'jobs\claimed', 'jobs\done', 'jobs\failed', 'lease', 'workers', 'results', 'ingested', 'logs'

function Initialize-CompatQueue {
    param([Parameter(Mandatory)][string]$Root)
    foreach ($d in $script:Dirs) { New-Item -ItemType Directory -Force -Path (Join-Path $Root $d) | Out-Null }
    $cfg = Join-Path $Root 'queue.json'
    if (-not (Test-Path $cfg)) {
        @{ schema = 'compat-queue/1'; leaseTimeoutSec = 300; maxAttempts = 3
           tierTimeoutSec = @{ T1 = 600; T2 = 3600; T3 = 43200 } } | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 $cfg
    }
    $Root
}

function Get-QueueConfig { param([string]$Root) Get-Content -Raw (Join-Path $Root 'queue.json') | ConvertFrom-Json }

function Write-JsonAtomic {
    # write to a temp file next to the target, then rename: readers never see half a file
    param([string]$Path, $Object)
    $tmp = "$Path.$PID.tmp"
    $Object | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 -LiteralPath $tmp
    # Windows PowerShell 5.1's Move-Item -Force does not replace an existing file: File.Replace does, atomically (NTFS and SMB)
    for ($try = 0; ; $try++) {
        try { if ([IO.File]::Exists($Path)) { [IO.File]::Replace($tmp, $Path, [NullString]::Value) } else { [IO.File]::Move($tmp, $Path) }; break }
        catch { if ($try -ge 5) { throw }; Start-Sleep -Milliseconds (50 + 100 * $try) }   # another writer or a virus scanner holds it a moment
    }
}

function ConvertTo-Utc {
    # PowerShell 7's ConvertFrom-Json turns ISO strings into DateTime, 5.1 leaves strings: accept both
    param($v)
    if ($v -is [datetime]) { return $v.ToUniversalTime() }
    [datetime]::Parse([string]$v, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
}

function Read-Json { param([string]$Path) try { Get-Content -Raw -Path $Path -ErrorAction Stop | ConvertFrom-Json } catch { $null } }

function Get-FileDigest {
    # .NET hashing, not Get-FileHash: a Windows PowerShell child started from PowerShell 7 inherits PS7's PSModulePath and then
    # cannot load Microsoft.PowerShell.Utility's Get-FileHash. LiteralPath semantics (ROM names are full of [!]).
    param([string]$Path, [string]$Algorithm = 'SHA1')
    $h = [Security.Cryptography.HashAlgorithm]::Create($Algorithm)
    $fs = [IO.File]::OpenRead($Path)
    try { ([BitConverter]::ToString($h.ComputeHash($fs)) -replace '-', '').ToLowerInvariant() } finally { $fs.Dispose(); $h.Dispose() }
}
function Get-RomSha1 { param([string]$Path) Get-FileDigest -Path $Path -Algorithm SHA1 }

function New-CompatJob {
    # One job = one ROM on one console at one tier. The id is stable (sha1 + tier) so enqueuing twice is a no-op
    # unless -Force: the catalog is keyed by ROM hash, re-running a game is an explicit decision.
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$RomPath, [Parameter(Mandatory)][string]$Console,
          [ValidateSet('T1','T2','T3')][string]$Tier = 'T1', [int]$Priority = 5, [string]$Sha1 = '', [hashtable]$Options = @{}, [switch]$Force)
    if (-not $Sha1) { $Sha1 = Get-RomSha1 $RomPath }
    $id = '{0}-{1}-{2}' -f $Priority, $Tier.ToLowerInvariant(), $Sha1.Substring(0, 16)
    foreach ($st in 'pending', 'claimed', 'done', 'failed') {
        if ((Test-Path (Join-Path $Root "jobs\$st\$id.json")) -and -not $Force) { return [pscustomobject]@{ id = $id; state = $st; added = $false } }
    }
    foreach ($st in 'done', 'failed') { Remove-Item -ErrorAction SilentlyContinue (Join-Path $Root "jobs\$st\$id.json") }
    $job = [ordered]@{
        schema = 'compat-job/1'; id = $id; console = $Console.ToLowerInvariant(); tier = $Tier; priority = $Priority
        rom = @{ path = $RomPath; name = [IO.Path]::GetFileName($RomPath); sha1 = $Sha1 }
        options = $Options; attempts = 0; history = @(); enqueuedAt = (Get-Date).ToUniversalTime().ToString('o')
    }
    Write-JsonAtomic (Join-Path $Root "jobs\pending\$id.json") $job
    [pscustomobject]@{ id = $id; state = 'pending'; added = $true }
}

function Register-CompatWorker {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Box, [int]$Slots = 1, [string]$Version = '', [hashtable]$Info = @{})
    $w = [ordered]@{ schema = 'compat-worker/1'; box = $Box; slots = $Slots; version = $Version; info = $Info
                     registeredAt = (Get-Date).ToUniversalTime().ToString('o'); beatAt = (Get-Date).ToUniversalTime().ToString('o'); running = @(); state = 'idle' }
    $old = Read-Json (Join-Path $Root "workers\$Box.json")
    if ($old) { $w.registeredAt = $old.registeredAt }
    Write-JsonAtomic (Join-Path $Root "workers\$Box.json") $w
}

function Send-CompatHeartbeat {
    # Renews the box record and every lease the box holds. A lease that is not renewed for leaseTimeoutSec is reaped.
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Box, [string[]]$Running = @(), [string]$State = 'running')
    $now = (Get-Date).ToUniversalTime().ToString('o')
    $p = Join-Path $Root "workers\$Box.json"
    $w = Read-Json $p
    if (-not $w) { Register-CompatWorker -Root $Root -Box $Box | Out-Null; $w = Read-Json $p }
    $w.beatAt = $now; $w.running = @($Running); $w.state = $State
    Write-JsonAtomic $p $w
    foreach ($id in $Running) {
        $lp = Join-Path $Root "lease\$id.json"
        $l = Read-Json $lp
        if ($l -and $l.box -eq $Box) { $l.beatAt = $now; Write-JsonAtomic $lp $l }
    }
}

function Get-NextCompatJob {
    # Claims the first pending job (optionally only some consoles/tiers). Returns the job or $null.
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Box, [string[]]$Consoles = @(), [string[]]$Tiers = @())
    foreach ($f in (Get-ChildItem -Path (Join-Path $Root 'jobs\pending') -Filter '*.json' -ErrorAction SilentlyContinue | Sort-Object Name)) {
        $job = Read-Json $f.FullName
        if (-not $job) { continue }
        if ($Consoles.Count -and ($Consoles -notcontains $job.console)) { continue }
        if ($Tiers.Count -and ($Tiers -notcontains $job.tier)) { continue }
        $dest = Join-Path $Root "jobs\claimed\$($f.Name)"
        try { Move-Item -Path $f.FullName -Destination $dest -ErrorAction Stop } catch { continue }   # lost the race
        $now = (Get-Date).ToUniversalTime().ToString('o')
        $job.attempts = [int]$job.attempts + 1
        $job.history = @($job.history) + @(@{ box = $Box; claimedAt = $now })
        Write-JsonAtomic $dest $job
        Write-JsonAtomic (Join-Path $Root "lease\$($job.id).json") @{ id = $job.id; box = $Box; claimedAt = $now; beatAt = $now; attempt = $job.attempts }
        return $job
    }
    $null
}

function Complete-CompatJob {
    # The worker hands back its result record. The record is validated before the job counts as done; a bad record fails the attempt.
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Box, [Parameter(Mandatory)]$Job, [Parameter(Mandatory)]$Result)
    $problem = Test-CompatResult $Result $Job
    if ($problem) { return Fail-CompatJob -Root $Root -Box $Box -Job $Job -Reason "invalid result: $problem" }
    $lease = Read-Json (Join-Path $Root "lease\$($Job.id).json")
    if (-not $lease -or $lease.box -ne $Box) { return 'lease-lost' }   # reaped while we ran: someone else may have it now, drop our result
    Write-JsonAtomic (Join-Path $Root "results\$($Job.id).json") $Result
    $claimed = Join-Path $Root "jobs\claimed\$($Job.id).json"
    $j = Read-Json $claimed; if (-not $j) { $j = $Job }
    $j.history = @($j.history) + @(@{ box = $Box; endedAt = (Get-Date).ToUniversalTime().ToString('o'); outcome = 'done' })
    Write-JsonAtomic (Join-Path $Root "jobs\done\$($Job.id).json") $j
    Remove-Item -ErrorAction SilentlyContinue $claimed, (Join-Path $Root "lease\$($Job.id).json")
    'done'
}

function Fail-CompatJob {
    # One failed attempt: back to pending, or to failed after maxAttempts.
    param([Parameter(Mandatory)][string]$Root, [string]$Box = '', [Parameter(Mandatory)]$Job, [string]$Reason = '')
    $cfg = Get-QueueConfig $Root
    $claimed = Join-Path $Root "jobs\claimed\$($Job.id).json"
    $j = Read-Json $claimed; if (-not $j) { $j = $Job }
    $j.history = @($j.history) + @(@{ box = $Box; endedAt = (Get-Date).ToUniversalTime().ToString('o'); outcome = 'failed'; reason = $Reason })
    $to = 'pending'; if ([int]$j.attempts -ge [int]$cfg.maxAttempts) { $to = 'failed' }
    Write-JsonAtomic (Join-Path $Root "jobs\$to\$($Job.id).json") $j
    Remove-Item -ErrorAction SilentlyContinue $claimed, (Join-Path $Root "lease\$($Job.id).json")
    $to
}

function Invoke-CompatReaper {
    # Coordinator side: jobs whose lease went stale (box died, network gone, runner hung past its own timeout) go back to pending.
    param([Parameter(Mandatory)][string]$Root)
    $cfg = Get-QueueConfig $Root
    $now = (Get-Date).ToUniversalTime()
    $out = @()
    foreach ($f in (Get-ChildItem -Path (Join-Path $Root 'jobs\claimed') -Filter '*.json' -ErrorAction SilentlyContinue)) {
        $job = Read-Json $f.FullName; if (-not $job) { continue }
        $lease = Read-Json (Join-Path $Root "lease\$($job.id).json")
        $age = 1e9; if ($lease) { $age = ($now - (ConvertTo-Utc $lease.beatAt)).TotalSeconds }
        $limit = [int]$cfg.leaseTimeoutSec
        $tierMax = $cfg.tierTimeoutSec.($job.tier); $claimedAge = 0
        if ($lease) { $claimedAge = ($now - (ConvertTo-Utc $lease.claimedAt)).TotalSeconds }
        $reason = ''
        if ($age -gt $limit) { $reason = "lease stale ${age}s" }
        elseif ($tierMax -and $claimedAge -gt (2 * [int]$tierMax)) { $reason = "running ${claimedAge}s, over 2x the $($job.tier) timeout" }
        if ($reason) { $box = ''; if ($lease) { $box = $lease.box }; $out += [pscustomobject]@{ id = $job.id; to = (Fail-CompatJob -Root $Root -Box $box -Job $job -Reason "reaped: $reason") ; reason = $reason } }
    }
    $out
}

function Test-CompatResult {
    # Returns '' when the record is acceptable, else what is wrong. The catalog never sees a record that fails this.
    param($Result, $Job)
    if (-not $Result) { return 'empty' }
    foreach ($k in 'schema', 'jobId', 'rom', 'console', 'tier', 'grade', 'core', 'box', 'finishedAt') { if (-not ($Result.PSObject.Properties.Name -contains $k)) { return "missing $k" } }
    if ($Result.schema -ne 'compat-result/1') { return "schema $($Result.schema)" }
    if ($Job -and $Result.jobId -ne $Job.id) { return 'jobId mismatch' }
    if ($Job -and $Result.rom.sha1 -ne $Job.rom.sha1) { return 'rom sha1 mismatch' }
    if (@('Nothing', 'Broken', 'Jank', 'OK', 'Certified', 'NotTested') -notcontains $Result.grade) { return "grade $($Result.grade)" }
    ''
}

function Get-CompatQueueStatus {
    param([Parameter(Mandatory)][string]$Root)
    $c = [ordered]@{}
    foreach ($st in 'pending', 'claimed', 'done', 'failed') { $c[$st] = @(Get-ChildItem (Join-Path $Root "jobs\$st") -Filter *.json -ErrorAction SilentlyContinue).Count }
    $now = (Get-Date).ToUniversalTime()
    $lease = [int](Get-QueueConfig $Root).leaseTimeoutSec
    $workers = foreach ($f in (Get-ChildItem (Join-Path $Root 'workers') -Filter *.json -ErrorAction SilentlyContinue)) {
        $w = Read-Json $f.FullName; if (-not $w) { continue }
        $age = [int]($now - (ConvertTo-Utc $w.beatAt)).TotalSeconds
        $state = $w.state; if ($state -ne 'stopped' -and $age -gt $lease) { $state = "LOST ($state)" }   # no beat within the lease: the box is down
        [pscustomobject]@{ box = $w.box; slots = $w.slots; state = $state; running = (@($w.running) -join ','); version = $w.version; beatAgeSec = $age }
    }
    [pscustomobject]@{ jobs = [pscustomobject]$c; workers = @($workers) }
}

Export-ModuleMember -Function Get-FileDigest, Initialize-CompatQueue, Get-QueueConfig, Write-JsonAtomic, Read-Json, Get-RomSha1, New-CompatJob, Register-CompatWorker,
    Send-CompatHeartbeat, Get-NextCompatJob, Complete-CompatJob, Fail-CompatJob, Invoke-CompatReaper, Test-CompatResult, Get-CompatQueueStatus
