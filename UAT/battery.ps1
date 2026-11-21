<#
.SYNOPSIS
  The whole headless test battery in one command: golden suite (parallel), Butchering scenarios and a fuzz campaign on every console.

.DESCRIPTION
  1. builds the Workshop, tools/butcher-fuzz and tools/butcher-scenarios into a scratch folder (never the copy the user runs),
  2. runs the golden suite sharded over -Jobs runners (UAT/run-parallel.ps1),
  3. runs the Butchering scenarios (preset undo, soak, core swaps, mangled JSON) - one process per console, in parallel,
  4. runs the fuzz campaign for -FuzzMinutes on every console, in parallel,
  4b. the x86 PC console ('pc' in -Consoles; Butchering mode and the fuzzer do not cover it): the CPU checks (--x86test), the device programs
      (--x86dev), the sound engines and cards with their save states (--x86engines, --x86cards), and a FreeDOS session on each x86 CPU core
      (I386, I386_D074: boot, menus, state round trip) when the toolkit's FreeDOS image is here,
  5. writes <Out>\report.md (and report.json): each group's verdict, every failure, every fuzz finding with its one-line repro.
  Exit code 1 when anything failed. Long: run it in the background (Start-Process / a second terminal).

.EXAMPLE
  powershell -NoProfile -File UAT\battery.ps1                         # ~25 min: suite + scenarios + 10 min of fuzz
  powershell -NoProfile -File UAT\battery.ps1 -FuzzMinutes 60 -Soak 60000
  powershell -NoProfile -File UAT\battery.ps1 -Skip fuzz -Consoles nes,snes
  powershell -NoProfile -Command "& .\UAT\battery.ps1 -Skip suite,scenarios,fuzz -Consoles pc"   # the x86 checks alone
#>
param(
    [int]      $Jobs = [Math]::Max(2, [Environment]::ProcessorCount - 2),
    [int]      $FuzzMinutes = 10,
    [int]      $Soak = 20000,
    [int]      $Games = 6,
    [string[]] $Consoles = @('nes', 'gb', 'gbc', 'sms', 'gg', 'md', 'snes', 'wm', 'pc', 'n64'),
    [string[]] $Skip = @(),
    [int]      $Seed = 0,
    [string]   $Out
)
$ErrorActionPreference = 'Stop'
$Repo = Split-Path -Parent $PSScriptRoot
$env:BN_REPO = $Repo   # bscen runs from the scratch build folder: tell it where the repo (and the NES ROM corpus) is
if ($Seed -eq 0) { $Seed = [int](Get-Date -UFormat %j) * 1000 + (Get-Date).Hour * 10 }
if (-not $Out) { $Out = Join-Path $(if ($env:BROKENNES_WORK) { $env:BROKENNES_WORK } else { 'C:\BrokenNes-work' }) ("battery\" + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$Build = Join-Path $(if ($env:BROKENNES_WORK) { $env:BROKENNES_WORK } else { 'C:\BrokenNes-work' }) 'battery-build'
New-Item -ItemType Directory -Force -Path $Out, $Build | Out-Null
$t0 = Get-Date
$groups = [ordered]@{}
# the x86 PC console has its own checks (4b); Butchering scenarios and the fuzzer run on the others
$runPc = $Consoles -contains 'pc'
$runN64 = $Consoles -contains 'n64'
$Consoles = @($Consoles | Where-Object { $_ -ne 'pc' -and $_ -ne 'n64' })
function Note($m, $c = 'Gray') { Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $m) -ForegroundColor $c }

# ---- 1. build (scratch output; dotnet must not touch the user's exe)
Note "building into $Build" White
$exe = Join-Path $Build 'bin\BrokenNes.Workshop\release\BrokenNes.Workshop.exe'
& dotnet build (Join-Path $Repo 'Workshop') -c Release --artifacts-path $Build 2>&1 | Select-String -Pattern ' error ' | ForEach-Object { Write-Host $_ -ForegroundColor Red }
$buildRc = $LASTEXITCODE
if (-not (Test-Path $exe)) { throw "Workshop build failed (no $exe)" }
# a failed build leaves the OLD exe in place (a locked exe, a compile error): never test that and report green
if ($buildRc -ne 0) { throw "Workshop build failed (dotnet exit $buildRc): the battery would test a stale $exe" }
$dll = Join-Path (Split-Path $exe) 'BrokenNes.Workshop.dll'
$newest = Get-ChildItem -Recurse -File -Include *.cs, *.csproj -Path (Join-Path $Repo 'Workshop'), (Join-Path $Repo 'Core'), (Join-Path $Repo 'Windows') -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if ($newest -and (Test-Path $dll) -and (Get-Item $dll).LastWriteTimeUtc -lt $newest.LastWriteTimeUtc) { throw "Workshop build is stale: $dll is older than $($newest.FullName)" }
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
if ($Skip -notcontains 'scenarios' -and $Consoles.Count -gt 0) {
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
if ($Skip -notcontains 'fuzz' -and $FuzzMinutes -gt 0 -and $Consoles.Count -gt 0) {
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
            $k = $Matches[1]; if ($k -like 'info |*' -or $k -like 'create | NotSupportedException*') { continue }   # informational: savestate replay notes
            $ctx = if ($i + 1 -lt $lines.Count) { $lines[$i + 1] } else { '' }
            $sd2 = if ($ctx -match 'seed (\d+)') { $Matches[1] } else { '?' }
            $findings += ("{0}: {1}  [{2}]  repro: bfuzz {0} 1 out {3}  (minimise: REPRO={3} ...)" -f $c, $k, ($ctx -replace '^seed \d+ ', '').Substring(0, [Math]::Min(90, ($ctx -replace '^seed \d+ ', '').Length)), $sd2)
            $real++
        }
    }
    $groups['Fuzz campaign'] = [pscustomobject]@{ ok = ($real -eq 0); line = "$trials trials, $real finding(s)"; details = $findings }
}

# ---- 4a. replay determinism (compat catalog P0 item 2): movie replay, lock-step twins, save-state round trips on every console's default cores
if ($Skip -notcontains 'determinism' -and $Consoles.Count -gt 0) {
    Note "replay determinism" White
    $dd = Join-Path $Out 'determinism'; New-Item -ItemType Directory -Force -Path $dd | Out-Null
    $dc = @($Consoles | ForEach-Object { if ($_ -eq 'wm') { 'wms' } else { $_ } })
    # two shards (at most 2 heavy jobs on the shared machine)
    $half = [Math]::Ceiling($dc.Count / 2)
    $shards = @(@($dc[0..($half - 1)]), @(if ($dc.Count -gt $half) { $dc[$half..($dc.Count - 1)] }))
    $dps = for ($i = 0; $i -lt $shards.Count; $i++) {
        if ($shards[$i].Count -eq 0) { continue }
        $p = Start-Process -FilePath $exe -ArgumentList @('--determinism-test', '--frames', '1200', '--roms', (Join-Path $PSScriptRoot 'determinism-roms.txt'), '--consoles', ($shards[$i] -join ','), '--keep', (Join-Path $dd "movies$i")) -NoNewWindow -PassThru -RedirectStandardOutput (Join-Path $dd "shard$i.txt") -RedirectStandardError (Join-Path $dd "shard$i.err.txt")
        $null = $p.Handle; $p
    }
    $dps | Wait-Process
    $dp = 0; $df = 0; $dn = 0; $det = @()
    foreach ($i in 0..($shards.Count - 1)) {
        $txt = Get-Content (Join-Path $dd "shard$i.txt") -ErrorAction SilentlyContinue
        foreach ($l in $txt) {
            if ($l -match '^determinism tests: (\d+) passed, (\d+) failed, (\d+) not run') { $dp += [int]$Matches[1]; $df += [int]$Matches[2]; $dn += [int]$Matches[3] }
            elseif ($l -like '  FAILED *') { $det += $l.Trim().Substring(7) }
        }
    }
    $groups['Replay determinism'] = [pscustomobject]@{ ok = ($df -eq 0 -and $dp -gt 0); line = "$dp pass, $df fail, $dn not run"; details = $det }
}

# ---- 4c. the Nintendo 64 (cor64): --n64session on each ROM in BROKENNES_N64_ROMS (a folder; ROMs are never in the repo): detection, menus, 1000 frames (SM64 is black from power-on until about frame 700),
# a picture, a save state round trip (the same frames after a load give the same pictures and sound), the battery. One ROM at a time (an N64 session is large).
if ($runN64 -and $Skip -notcontains 'n64') {
    Note "N64 console checks" White
    $nd = Join-Path $Out 'n64'; New-Item -ItemType Directory -Force -Path $nd | Out-Null
    $roms = if ($env:BROKENNES_N64_ROMS -and (Test-Path $env:BROKENNES_N64_ROMS)) { @(Get-ChildItem $env:BROKENNES_N64_ROMS -File | Where-Object { $_.Extension -in '.z64', '.n64', '.v64' }) } else { @() }
    $det = @(); $okN = 0
    foreach ($r in $roms) {
        $log = Join-Path $nd ($r.BaseName + '.txt')
        & $exe --n64session $r.FullName --frames 1000 --ppu MADCAT *> $log
        $txt = Get-Content $log -ErrorAction SilentlyContinue
        if ($LASTEXITCODE -ne 0 -or @(@($txt) -cmatch 'FAIL').Count -gt 0) { $det += "$($r.Name): exit $LASTEXITCODE"; $det += @($txt | Where-Object { $_ -cmatch 'FAIL' } | Select-Object -First 8 | ForEach-Object { "  $_" }) } else { $okN++ }
    }
    $groups['N64 console'] = [pscustomobject]@{ ok = ($roms.Count -gt 0 -and $det.Count -eq 0); line = $(if ($roms.Count -eq 0) { 'no ROMs (set BROKENNES_N64_ROMS)' } else { "$okN of $($roms.Count) ROMs pass" }); details = $det }
}

# ---- 4b. the x86 PC console
if ($runPc -and $Skip -notcontains 'x86') {
    Note "x86 PC console checks" White
    $xd = Join-Path $Out 'x86'; New-Item -ItemType Directory -Force -Path $xd | Out-Null
    $runs = [ordered]@{ 'x86test' = @('--x86test'); 'x86dev' = @('--x86dev'); 'x86video' = @('--x86video'); 'x86ide' = @('--x86ide'); 'x86pci' = @('--x86pci'); 'x86voodoo' = @('--x86voodoo'); 'x86engines' = @('--x86engines'); 'x86cards' = @('--x86cards'); 'x86fdc' = @('--x86fdc'); 'x86cpu' = @('--x86cpu', '--vec', (Join-Path $Repo 'tools\x86-cpu\cpuvec.bin')) }
    $fd = if ($env:BROKENNES_FREEDOS) { $env:BROKENNES_FREEDOS } else { 'C:\BrokenNes-tools\dos\freedos\FD14LITE.img' }
    if (Test-Path $fd) { foreach ($cpu in 'I386', 'I386_D074') { $runs["x86session-$cpu"] = @('--x86session', $fd, '--frames', '600', '--cpu', $cpu) } }
    if (Test-Path $fd) { $runs['x86bugrt'] = @('--x86bugrt', $fd) }   # the bug capture's x86 round trip: keys recorded, replayed in sync, off course without them
    if (Test-Path $fd) { $runs['x86bugrt-jitdelay'] = @('--x86bugrt', $fd) }   # the same with X86_JITDELAY=8: compiled code takes over at random moments, async JIT must stay exact
    $procs = foreach ($k in $runs.Keys) { if ($k -eq 'x86bugrt-jitdelay') { $env:X86_JITDELAY = '8' }; $p = Start-Process -FilePath $exe -ArgumentList $runs[$k] -NoNewWindow -PassThru -RedirectStandardOutput (Join-Path $xd "$k.txt") -RedirectStandardError (Join-Path $xd "$k.err.txt"); $null = $p.Handle; Remove-Item Env:X86_JITDELAY -ErrorAction SilentlyContinue; [pscustomobject]@{ k = $k; p = $p } }
    $procs.p | Wait-Process
    $det = @(); $okN = 0
    foreach ($x in $procs) {
        $txt = Get-Content (Join-Path $xd "$($x.k).txt") -ErrorAction SilentlyContinue
        $last = ($txt | Where-Object { $_ -match '\S' } | Select-Object -Last 1)
        # the CLIs end with a summary: "... 0 flagged", "checks N/N pass", "x86session: ok" / "N FAILED"
        $bad = ($x.p.ExitCode -ne 0) -or (@(@($txt) -cmatch 'FAILED|CRASH|NOT EXACT').Count -gt 0) -or ($last -match '[1-9]\d* flagged')
        if ($bad) { $det += "$($x.k): exit $($x.p.ExitCode); $last"; $det += @($txt | Where-Object { $_ -cmatch '^\s*FAIL|NOT EXACT|CRASH' } | Select-Object -First 8 | ForEach-Object { "  $_" }) } else { $okN++ }
    }
    if (-not (Test-Path $fd)) { $det += "x86session: not run (no FreeDOS image at $fd)" }
    $groups['x86 PC console'] = [pscustomobject]@{ ok = (($det | Where-Object { $_ -notlike 'x86session: not run*' }).Count -eq 0); line = "$okN of $($runs.Count) runs pass"; details = $det }
}

# ---- 5. report
$mins = ((Get-Date) - $t0).TotalMinutes
$allOk = -not ($groups.Values | Where-Object { -not $_.ok })
$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("# BrokenNes test battery - $(Get-Date -Format 'yyyy-MM-dd HH:mm')")
[void]$md.AppendLine('')
[void]$md.AppendLine(("**{0}** in {1:0} min - commit {2}, seed {3}, consoles {4}" -f $(if ($allOk) { 'ALL GREEN' } else { 'FAILURES' }), $mins, (git -C $Repo rev-parse --short HEAD), $Seed, ((@($Consoles) + @(if ($runPc) { 'pc' }) + @(if ($runN64) { 'n64' })) -join ' ')))
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
