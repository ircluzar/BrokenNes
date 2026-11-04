<#
.SYNOPSIS
  The whole headless test battery in one command: golden suite (parallel), Butchering scenarios and a fuzz campaign on every console.

.DESCRIPTION
  1. builds the Workshop, tools/butcher-fuzz and tools/butcher-scenarios into a scratch folder (never the copy the user runs),
  2. runs the golden suite sharded over -Jobs runners (UAT/run-parallel.ps1),
  3. runs the Butchering scenarios (preset undo, soak, core swaps, mangled JSON) - one process per console, in parallel,
  4. runs the fuzz campaign for -FuzzMinutes on every console, in parallel,
  5. writes <Out>\report.md (and report.json): each group's verdict, every failure, every fuzz finding with its one-line repro.
  Exit code 1 when anything failed. Long: run it in the background (Start-Process / a second terminal).

.EXAMPLE
  powershell -NoProfile -File UAT\battery.ps1                         # ~25 min: suite + scenarios + 10 min of fuzz
  powershell -NoProfile -File UAT\battery.ps1 -FuzzMinutes 60 -Soak 60000
  powershell -NoProfile -File UAT\battery.ps1 -Skip fuzz -Consoles nes,snes
#>
param(
    [int]      $Jobs = [Math]::Max(2, [Environment]::ProcessorCount - 2),
    [int]      $FuzzMinutes = 10,
    [int]      $Soak = 20000,
    [int]      $Games = 6,
    [string[]] $Consoles = @('nes', 'gb', 'gbc', 'sms', 'gg', 'md', 'snes', 'wm'),
    [string[]] $Skip = @(),
    [int]      $Seed = 0,
    [string]   $Out
)
$ErrorActionPreference = 'Stop'
$Repo = Split-Path -Parent $PSScriptRoot
if ($Seed -eq 0) { $Seed = [int](Get-Date -UFormat %j) * 1000 + (Get-Date).Hour * 10 }
if (-not $Out) { $Out = Join-Path $env:LOCALAPPDATA ("VRUN_Nes_Dev_work\battery\" + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$Build = Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\battery-build'
New-Item -ItemType Directory -Force -Path $Out, $Build | Out-Null
$t0 = Get-Date
$groups = [ordered]@{}
function Note($m, $c = 'Gray') { Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $m) -ForegroundColor $c }

# ---- 1. build (scratch output; dotnet must not touch the user's exe)
Note "building into $Build" White
$exe = Join-Path $Build 'bin\BrokenNes.Workshop\release\BrokenNes.Workshop.exe'
& dotnet build (Join-Path $Repo 'Workshop') -c Release --artifacts-path $Build 2>&1 | Select-String -Pattern ' error ' | ForEach-Object { Write-Host $_ -ForegroundColor Red }
if (-not (Test-Path $exe)) { throw "Workshop build failed (no $exe)" }
$fuzz = Join-Path $Build 'bfuzz\bfuzz.exe'; $scen = Join-Path $Build 'bscen\bscen.exe'
& dotnet build (Join-Path $Repo 'tools\butcher-fuzz') -c Release -o (Join-Path $Build 'bfuzz') 2>&1 | Select-String ' error ' | ForEach-Object { Write-Host $_ -ForegroundColor Red }
& dotnet build (Join-Path $Repo 'tools\butcher-scenarios') -c Release -o (Join-Path $Build 'bscen') 2>&1 | Select-String ' error ' | ForEach-Object { Write-Host $_ -ForegroundColor Red }

# ---- 2. golden suite
if ($Skip -notcontains 'suite') {
    Note "golden suite on $Jobs runners" White
    $sj = Join-Path $Out 'suite.json'
    & powershell -NoProfile -File (Join-Path $PSScriptRoot 'run-parallel.ps1') -Exe $exe -Jobs $Jobs -Json $sj -WorkDir (Join-Path $Out 'suite-shards') | Tee-Object -FilePath (Join-Path $Out 'suite.txt') | Select-Object -Last 3 | ForEach-Object { Write-Host $_ }
    $s = Get-Content $sj -Raw | ConvertFrom-Json
    $groups['Golden suite'] = [pscustomobject]@{ ok = ($s.totals.fail -eq 0); line = ("{0} pass, {1} fail, {2} skip ({3:0}s)" -f $s.totals.pass, $s.totals.fail, $s.totals.skip, $s.durationSeconds); details = @($s.cases | Where-Object status -eq 'FAIL' | ForEach-Object { "$($_.id): $($_.note)" }) }
}

function RunPerConsole($tool, $argsFor, $label) {
    $procs = foreach ($c in $Consoles) { Start-Process -FilePath $tool -ArgumentList (& $argsFor $c) -NoNewWindow -PassThru -RedirectStandardOutput (Join-Path $Out "$label-$c.out.txt") -RedirectStandardError (Join-Path $Out "$label-$c.err.txt") }
    $procs | Wait-Process
}

# ---- 3. scenarios
if ($Skip -notcontains 'scenarios') {
    Note ("Butchering scenarios on {0} consoles (games {1}, soak {2} frames)" -f $Consoles.Count, $Games, $Soak) White
    $sd = Join-Path $Out 'scenarios'
    RunPerConsole $scen { param($c) @($c, $sd, '--games', $Games, '--soak', $Soak, '--seed', $Seed) } 'scen'
    $pass = 0; $fail = 0; $skipN = 0; $det = @()
    foreach ($c in $Consoles) {
        $f = Join-Path $sd "$c.txt"
        if (-not (Test-Path $f)) { $fail++; $det += "${c}: no report (crashed? see scen-$c.err.txt)"; continue }
        foreach ($l in Get-Content $f) {
            if ($l -like 'PASS*') { $pass++ } elseif ($l -like 'SKIP*') { $skipN++ } elseif ($l -like 'FAIL*') { $fail++; $det += "${c}: $($l.Substring(6))" }
        }
        if (-not (Select-String -Path $f -Pattern '^DONE' -Quiet)) { $fail++; $det += "${c}: report ends early (the process died mid-run)" }
    }
    $groups['Butchering scenarios'] = [pscustomobject]@{ ok = ($fail -eq 0); line = "$pass pass, $fail fail, $skipN skip"; details = $det }
}

# ---- 4. fuzz
if ($Skip -notcontains 'fuzz' -and $FuzzMinutes -gt 0) {
    Note ("fuzz campaign: {0} min on {1} consoles, seed {2}" -f $FuzzMinutes, $Consoles.Count, $Seed) White
    $fd = Join-Path $Out 'fuzz'; New-Item -ItemType Directory -Force -Path $fd | Out-Null
    Push-Location $fd
    try { RunPerConsole $fuzz { param($c) @($c, ($FuzzMinutes * 60), $fd, $Seed) } 'fuzz' } finally { Pop-Location }
    $trials = 0; $findings = @(); $real = 0
    foreach ($c in $Consoles) {
        $log = Join-Path $fd "$c.log"; if (-not (Test-Path $log)) { $findings += "${c}: no log"; $real++; continue }
        $lines = Get-Content $log
        $done = $lines | Where-Object { $_ -match '^DONE (\d+) trials' } | Select-Object -Last 1
        if ($done -match '^DONE (\d+)') { $trials += [int]$Matches[1] } else { $findings += "${c}: the fuzzer did not finish"; $real++ }
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -notmatch '^NEW (.+)$') { continue }
            $k = $Matches[1]; if ($k -like 'info |*' -or $k -like '*load-save-not-idempotent*' -or $k -like 'create | NotSupportedException*') { continue }   # informational: savestate replay notes, the deliberate audio-filter reset
            $ctx = if ($i + 1 -lt $lines.Count) { $lines[$i + 1] } else { '' }
            $sd2 = if ($ctx -match 'seed (\d+)') { $Matches[1] } else { '?' }
            $findings += ("{0}: {1}  [{2}]  repro: bfuzz {0} 1 out {3}  (minimise: REPRO={3} ...)" -f $c, $k, ($ctx -replace '^seed \d+ ', '').Substring(0, [Math]::Min(90, ($ctx -replace '^seed \d+ ', '').Length)), $sd2)
            $real++
        }
    }
    $groups['Fuzz campaign'] = [pscustomobject]@{ ok = ($real -eq 0); line = "$trials trials, $real finding(s)"; details = $findings }
}

# ---- 5. report
$mins = ((Get-Date) - $t0).TotalMinutes
$allOk = -not ($groups.Values | Where-Object { -not $_.ok })
$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("# BrokenNes test battery - $(Get-Date -Format 'yyyy-MM-dd HH:mm')")
[void]$md.AppendLine('')
[void]$md.AppendLine(("**{0}** in {1:0} min - commit {2}, seed {3}, consoles {4}" -f $(if ($allOk) { 'ALL GREEN' } else { 'FAILURES' }), $mins, (git -C $Repo rev-parse --short HEAD), $Seed, ($Consoles -join ' ')))
[void]$md.AppendLine('')
[void]$md.AppendLine('| Group | Result | |'); [void]$md.AppendLine('|---|---|---|')
foreach ($k in $groups.Keys) { [void]$md.AppendLine("| $k | $($groups[$k].line) | $(if ($groups[$k].ok) { 'OK' } else { '**FAIL**' }) |") }
foreach ($k in $groups.Keys) {
    if ($groups[$k].details.Count -eq 0) { continue }
    [void]$md.AppendLine(''); [void]$md.AppendLine("## $k"); [void]$md.AppendLine('')
    foreach ($d in $groups[$k].details) { [void]$md.AppendLine("- $d") }
}
[void]$md.AppendLine(''); [void]$md.AppendLine("Logs: $Out")
$md.ToString() | Set-Content -LiteralPath (Join-Path $Out 'report.md') -Encoding UTF8
([ordered]@{ ok = $allOk; minutes = [Math]::Round($mins, 1); seed = $Seed; groups = $groups } | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath (Join-Path $Out 'report.json') -Encoding UTF8
Write-Host ''; Write-Host $md.ToString()
if ($allOk) { exit 0 } else { exit 1 }
