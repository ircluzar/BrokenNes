<#
.SYNOPSIS
  The golden suite (run-suite.ps1), sharded across N parallel runners.

.DESCRIPTION
  Every case is its own headless Workshop process, so the cases are split into N shards (balanced by frame count), each shard is written as a
  copy of the case manifests holding only its cases, and N run-suite.ps1 processes run them side by side with -CasesDir / -Json. The results are
  merged into one table and one JSON. Verdicts are run-suite.ps1's own: a green run here means exactly what a green serial run means.

  Cases that share generated files (a ROM under the {gen} root, written by the Workshop itself) stay together in shard 1, in file order.
  -UpdateGolden is not supported here (rewriting manifests from shards would race): re-record with run-suite.ps1.

.EXAMPLE
  powershell -NoProfile -File UAT\run-parallel.ps1 -Exe <BrokenNes.Workshop.exe> -Jobs 10
  powershell -NoProfile -File UAT\run-parallel.ps1 -Exe <exe> -Filter '*sega*' -Json out\sega.json
#>
param(
    [string] $Exe,
    [string] $Filter,
    [int]    $Jobs = [Math]::Max(2, [Environment]::ProcessorCount - 2),
    [string] $Json,
    [string] $CasesDir,
    [int]    $TimeoutSeconds = 300,
    [string] $WorkDir
)
$ErrorActionPreference = 'Stop'
$UatRoot = $PSScriptRoot
if (-not $CasesDir) { $CasesDir = Join-Path $UatRoot 'cases' }
if (-not $WorkDir) { $WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) ("bn-uat-parallel-" + [Guid]::NewGuid().ToString('N').Substring(0, 8)) }
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$started = Get-Date

function Prop($o, $n, $d = $null) { if ($null -ne $o -and $o.PSObject.Properties[$n]) { return $o.$n } return $d }

# ---- collect cases (same filter rule as run-suite.ps1)
$manifests = Get-ChildItem -LiteralPath $CasesDir -Filter '*.json' -File | Sort-Object Name | ForEach-Object { [pscustomobject]@{ Name = $_.BaseName; File = $_; Doc = (Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json) } }
$items = @()
foreach ($m in $manifests) {
    $suite = [string](Prop $m.Doc 'suite' $m.Name)
    $idx = 0
    foreach ($c in @(Prop $m.Doc 'cases' @())) {
        $id = [string](Prop $c 'id' '(unnamed)'); $tags = @(Prop $c 'tags' @())
        if ($Filter) {
            $hit = ($id -like $Filter) -or ($suite -like $Filter) -or ($m.Name -like $Filter)
            foreach ($t in $tags) { if ([string]$t -like $Filter) { $hit = $true } }
            if (-not $hit) { $idx++; continue }
        }
        $frames = [int](Prop $c 'frames' 0) + [int](Prop $c 'warmupFrames' 0) + [int](Prop $c 'continueFrames' 0)
        if ($frames -le 0) { $frames = 600 }
        $pinned = ([string](Prop $c 'rom' '')) -like '*{gen}*'
        $items += [pscustomobject]@{ Manifest = $m; Case = $c; Id = $id; Order = $idx; Cost = $frames; Pinned = $pinned }
        $idx++
    }
}
if ($items.Count -eq 0) { Write-Host "No cases matched$(if ($Filter) { " filter '$Filter'" })." -ForegroundColor Yellow; exit 0 }
$Jobs = [Math]::Min($Jobs, $items.Count)

# ---- balance: pinned cases on shard 0, then longest-first onto the lightest shard
$load = New-Object double[] $Jobs; $shardOf = @{}
foreach ($it in $items | Where-Object Pinned) { $shardOf[$it] = 0; $load[0] += $it.Cost }
foreach ($it in $items | Where-Object { -not $_.Pinned } | Sort-Object Cost -Descending) {
    $best = 0; for ($s = 1; $s -lt $Jobs; $s++) { if ($load[$s] -lt $load[$best]) { $best = $s } }
    $shardOf[$it] = $best; $load[$best] += $it.Cost
}

# ---- write shard manifests (each manifest keeps its own roots / suite / description; only the case list shrinks)
for ($s = 0; $s -lt $Jobs; $s++) {
    $dir = Join-Path $WorkDir "shard$s"; New-Item -ItemType Directory -Force -Path $dir | Out-Null
    foreach ($m in $manifests) {
        $mine = @($items | Where-Object { $_.Manifest -eq $m -and $shardOf[$_] -eq $s } | Sort-Object Order | ForEach-Object Case)
        if ($mine.Count -eq 0) { continue }
        $doc = $m.Doc | ConvertTo-Json -Depth 30 | ConvertFrom-Json
        $doc.cases = $mine
        ($doc | ConvertTo-Json -Depth 30) | Set-Content -LiteralPath (Join-Path $dir $m.File.Name) -Encoding UTF8
    }
}

# ---- run the shards
Write-Host ("BrokenNes parallel suite  |  {0} case(s) on {1} runners  |  {2}" -f $items.Count, $Jobs, $(if ($Filter) { "filter '$Filter'" } else { 'all cases' })) -ForegroundColor White
$runner = Join-Path $UatRoot 'run-suite.ps1'
$procs = @()
for ($s = 0; $s -lt $Jobs; $s++) {
    $dir = Join-Path $WorkDir "shard$s"
    $argList = @('-NoProfile', '-File', $runner, '-CasesDir', $dir, '-Json', (Join-Path $WorkDir "shard$s.json"), '-TimeoutSeconds', $TimeoutSeconds, '-Yes')
    if ($Exe) { $argList += @('-Exe', $Exe) }
    $procs += Start-Process -FilePath 'powershell' -ArgumentList $argList -NoNewWindow -PassThru `
        -RedirectStandardOutput (Join-Path $WorkDir "shard$s.out.txt") -RedirectStandardError (Join-Path $WorkDir "shard$s.err.txt")
}
$procs | Wait-Process

# ---- merge
$rows = @(); $missing = @()
for ($s = 0; $s -lt $Jobs; $s++) {
    $jf = Join-Path $WorkDir "shard$s.json"
    if (-not (Test-Path -LiteralPath $jf)) { $missing += $s; continue }
    $rows += @((Get-Content -LiteralPath $jf -Raw | ConvertFrom-Json).cases)
}
$order = @{}; $i = 0; foreach ($it in $items) { $order[$it.Id] = $i++ }
$rows = @($rows | Sort-Object { if ($order.ContainsKey([string]$_.id)) { $order[[string]$_.id] } else { 1e9 } })
$count = { param($st) @($rows | Where-Object { $_.status -eq $st }).Count }
$pass = & $count 'PASS'; $fail = & $count 'FAIL'; $skip = & $count 'SKIP'; $info = & $count 'INFO'
foreach ($r in $rows | Where-Object { $_.status -eq 'FAIL' }) {
    Write-Host ("FAIL    {0}" -f $r.id) -ForegroundColor Red
    if ($r.note) { Write-Host ("        > {0}" -f $r.note) -ForegroundColor Red }
}
foreach ($s in $missing) {
    Write-Host ("RUNNER {0} produced no results - see {1}" -f $s, (Join-Path $WorkDir "shard$s.out.txt")) -ForegroundColor Red
    $fail++
}
if ($rows.Count + 0 -lt $items.Count -and $missing.Count -eq 0) { Write-Host ("{0} case(s) missing from the results" -f ($items.Count - $rows.Count)) -ForegroundColor Red; $fail++ }
$elapsed = ((Get-Date) - $started).TotalSeconds
Write-Host ''
Write-Host ('{0} pass  {1} fail  {2} skip  {3} info   ({4:0.0}s, {5} runners)' -f $pass, $fail, $skip, $info, $elapsed, $Jobs) -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($Json) {
    $d = Split-Path -Parent $Json; if ($d) { New-Item -ItemType Directory -Force -Path $d | Out-Null }
    ([ordered]@{ durationSeconds = [Math]::Round($elapsed, 1); runners = $Jobs; filter = $Filter; totals = [ordered]@{ pass = $pass; fail = $fail; skip = $skip; info = $info }; exitCode = $(if ($fail -gt 0) { 1 } else { 0 }); cases = $rows } |
        ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $Json -Encoding UTF8
}
Write-Host "Shard logs: $WorkDir" -ForegroundColor DarkGray
if ($fail -gt 0) { exit 1 } else { exit 0 }
