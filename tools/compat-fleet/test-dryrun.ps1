# test-dryrun.ps1 - the whole fleet flow on THIS PC, in a scratch folder: stage -> install (dry run) -> enqueue T1 boot checks ->
# two simulated boxes (1 slot each, so at most 2 emulators run) -> ingest -> checks. Then a tier-timeout job and a dead-box reap.
#
#   pwsh -File tools\compat-fleet\test-dryrun.ps1 -Scratch <empty dir> [-SkipBuild] [-RomList nes=a.txt,snes=b.txt]
# Without -RomList it uses a fixed handful from X:\EMULATION (read only): two NES boards, one unsupported NES mapper, SNES, GB, Genesis.
# Exit 0 = every check passed. Nothing outside -Scratch is written; no shares, drive maps or scheduled tasks are touched.
param(
    [Parameter(Mandatory)][string]$Scratch,
    [switch]$SkipBuild,
    [string]$Version = 'dryrun'
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$roms = [ordered]@{
    nes  = @('X:\EMULATION\Nintendo Entertainment System\1942 (JU) [!].nes', 'X:\EMULATION\Nintendo Entertainment System\Abadox (U).nes',
             'X:\EMULATION\Nintendo Entertainment System\Batman - Return of the Joker (U).nes')
    snes = @('X:\EMULATION\Super Nintendo\Super Nintendo (U)\Super Mario World (U) [!].smc')
    gb   = @('X:\EMULATION\GameBoy\Tetris (W) (V1.1) [!].zip')
    md   = @('X:\EMULATION\Sega Genesis\3 Ninjas Kick Back (U) [!].bin')
}
$fails = @()
function Check($name, $ok) { if ($ok) { Write-Host "PASS $name" -ForegroundColor Green } else { Write-Host "FAIL $name" -ForegroundColor Red; $script:fails += $name } }
New-Item -ItemType Directory -Force $Scratch | Out-Null
$share = Join-Path $Scratch 'share'; $q = Join-Path $Scratch 'queue'; $box = Join-Path $Scratch 'box'; $inbox = Join-Path $Scratch 'inbox'
Remove-Item -Recurse -Force $q, $inbox, (Join-Path $box 'work') -ErrorAction SilentlyContinue

& (Join-Path $here 'stage-release.ps1') -FleetRoot $share -Version $Version -Pin -SkipBuild:$SkipBuild | Out-Host
& (Join-Path $here 'queue.ps1') init -QueueRoot $q | Out-Host
foreach ($c in $roms.Keys) {
    $list = Join-Path $Scratch "$c.txt"; $roms[$c] | Set-Content $list
    & (Join-Path $here 'queue.ps1') enqueue -QueueRoot $q -Console $c -RomList $list | Out-Host
}
$again = & (Join-Path $here 'queue.ps1') enqueue -QueueRoot $q -Console nes -RomList (Join-Path $Scratch 'nes.txt')
Check 'enqueue is idempotent' ($again -match 'enqueued 0,')
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $share 'install-worker.ps1') -DryRun -FleetRoot $share -QueueRoot $q -InstallDir $box -Slots 1 -Box sim | Out-Host
$cfg = Join-Path $box 'worker.json'
Check 'install wrote worker.json' (Test-Path $cfg)
$runner = (Get-Content -Raw $cfg | ConvertFrom-Json).runner

$procs = foreach ($b in 'simA', 'simB') {
    Start-Process powershell.exe -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $Scratch "$b.txt") -RedirectStandardError (Join-Path $Scratch "$b.err") `
        -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$runner`" -ConfigPath `"$cfg`" -Box $b -DryRun -ExitWhenEmpty -HeartbeatSec 5"
}
foreach ($p in $procs) { $null = $p.Handle; if (-not $p.WaitForExit(900000)) { $p.Kill(); Check "worker $($p.Id) finished" $false } }
& (Join-Path $here 'queue.ps1') ingest -QueueRoot $q -CatalogInbox $inbox | Out-Host
Import-Module (Join-Path $here 'CompatQueue.psm1') -Force -DisableNameChecking
$st = Get-CompatQueueStatus $q
$total = @($roms.Values | ForEach-Object { $_ }).Count
Check "all $total jobs done" ($st.jobs.done -eq $total -and $st.jobs.pending -eq 0 -and $st.jobs.claimed -eq 0 -and $st.jobs.failed -eq 0)
$results = @(Get-ChildItem -Recurse -File $inbox -Filter *.json | ForEach-Object { Get-Content -Raw $_.FullName | ConvertFrom-Json })
Check "$total records in the catalog inbox" ($results.Count -eq $total)
Check 'both boxes took jobs' (@($results | Select-Object -ExpandProperty box -Unique).Count -eq 2)
$bat = $results | Where-Object { $_.rom.name -like 'Batman*' }
Check 'unsupported mapper graded Nothing / mapper/board' ($bat.grade -eq 'Nothing' -and @($bat.tags) -contains 'mapper/board')
$booted = @($results | Where-Object { $_.t1.verdict -eq 'pass' })
Check 'the 5 supported games boot and react' ($booted.Count -eq ($total - 1))
foreach ($r in $results) { Write-Host ('  {0,-6} {1,-45} {2,-9} {3,-4} {4}' -f $r.console, $r.rom.name, $r.grade, $r.t1.verdict, $r.t1.why) }

# tier timeout + dead box
$q2 = Join-Path $Scratch 'queue2'; Remove-Item -Recurse -Force $q2 -ErrorAction SilentlyContinue
Initialize-CompatQueue $q2 | Out-Null
$c2 = Get-QueueConfig $q2; $c2.leaseTimeoutSec = 10; $c2.tierTimeoutSec.T1 = 4; Write-JsonAtomic (Join-Path $q2 'queue.json') $c2
New-CompatJob -Root $q2 -RomPath $roms.snes[0] -Console snes -Priority 1 | Out-Null
New-CompatJob -Root $q2 -RomPath $roms.md[0] -Console md -Priority 2 | Out-Null
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -ConfigPath $cfg -QueueRoot $q2 -Box simT -DryRun -MaxJobs 1 -HeartbeatSec 2 | Out-Null
$t = Get-Content -Raw (Get-ChildItem (Join-Path $q2 'results') -Filter *.json | Select-Object -First 1).FullName | ConvertFrom-Json
Check 'tier timeout kills the run and reports Broken' ($t.grade -eq 'Broken' -and $t.t1.timedOut)
$dead = Start-Process powershell.exe -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $Scratch 'dead.txt') `
    -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$runner`" -ConfigPath `"$cfg`" -QueueRoot `"$q2`" -Box simDead -DryRun -HeartbeatSec 2"
for ($i = 0; $i -lt 60 -and -not (Get-ChildItem (Join-Path $q2 'lease') -Filter *.json); $i++) { Start-Sleep -Milliseconds 500 }   # until it holds a job
Start-Sleep -Seconds 1
foreach ($child in (Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -eq $dead.Id })) {
    Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -eq $child.ProcessId } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Stop-Process -Id $child.ProcessId -Force -ErrorAction SilentlyContinue
}
Stop-Process -Id $dead.Id -Force
Start-Sleep -Seconds 12
$reaped = @(Invoke-CompatReaper $q2)
Check 'a dead box''s job is reaped back to pending' ($reaped.Count -eq 1 -and $reaped[0].to -eq 'pending')
Check 'the dead box shows as LOST' (@((Get-CompatQueueStatus $q2).workers | Where-Object { $_.box -eq 'simDead-sim' -and $_.state -like 'LOST*' }).Count -eq 1)

if ($fails.Count) { Write-Host "$($fails.Count) check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host 'all checks passed' -ForegroundColor Green; exit 0
