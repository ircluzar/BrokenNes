# T1.ps1 - the scripted boot check (no model use): boot, wait for the title, press Start, a short generic input script,
# checkpoint pictures. Writes a compat-result/1 record to -ResultPath. Windows PowerShell 5.1 compatible.
#
# What T1 can say on its own: Broken (the core threw / the runner hung), Nothing (unsupported board, or a blank /
# frozen picture the whole run), otherwise NotTested with t1.verdict = pass: the game booted and the picture moves.
# OK / Jank need the reference comparison (scope item 5) or an agent (T2) - T1 never claims them.
param(
    [Parameter(Mandatory)][string]$JobPath,
    [Parameter(Mandatory)][string]$ResultPath,
    [Parameter(Mandatory)][string]$Workshop,      # BrokenNes.Workshop.exe of the pinned release
    [Parameter(Mandatory)][string]$OutDir,        # checkpoint pictures + logs for this job
    [string]$Box = $env:COMPUTERNAME,
    [string]$Version = '',
    [int]$TimeoutSec = 600
)
$ErrorActionPreference = 'Stop'
function Get-FileDigest {
    # .NET hashing, not Get-FileHash: a Windows PowerShell child started from PowerShell 7 inherits PS7's PSModulePath and then
    # cannot load Microsoft.PowerShell.Utility's Get-FileHash. LiteralPath semantics (ROM names are full of [!]).
    param([string]$Path, [string]$Algorithm = 'SHA1')
    $h = [Security.Cryptography.HashAlgorithm]::Create($Algorithm)
    $fs = [IO.File]::OpenRead($Path)
    try { ([BitConverter]::ToString($h.ComputeHash($fs)) -replace '-', '').ToLowerInvariant() } finally { $fs.Dispose(); $h.Dispose() }
}
$job = Get-Content -Raw $JobPath | ConvertFrom-Json
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$frames = 1800
if ($job.options -and $job.options.PSObject.Properties.Name -contains 'frames') { $frames = [int]$job.options.frames }
# Title by ~10 s, Start twice (some titles ignore the first press during a fade), then walk right + jump: enough to leave the title
# and enter play on most games. The same script for every console so results are comparable.
$inputScript = '600:Start,610:,900:Start,910:,1100:Right,1200:Right+A,1215:Right,1300:Right+B,1320:Right,1400:Left,1500:A,1510:,1600:Start,1610:'
$checkpoints = @(300, 600, 900, 1200, 1500, $frames) | Where-Object { $_ -le $frames } | Sort-Object -Unique
$console = $job.console
$reportJson = Join-Path $OutDir 'romtest.json'
if ($console -eq 'nes') {
    # the NES runs on its own class (not a console session): --romtest, on the FIX family (the accuracy target and default)
    $args2 = @('--romtest', '--rom', $job.rom.path, '--cpu', 'FIX', '--ppu', 'FIX', '--apu', 'FIX', '--frames', "$frames", '--input', $inputScript,
               '--png-at', ($checkpoints -join ','), '--png-dir', $OutDir, '--json', '--out', $reportJson)
} else {
    $args2 = @('--console-run', '--rom', $job.rom.path, '--frames', "$frames", '--png-at', ($checkpoints -join ','), '--input', $inputScript,
               '--out', $OutDir, '--tag', 'cp')
    if ($console) { $args2 += @('--console', $console) }
}

$stdout = Join-Path $OutDir 'run.out.txt'; $stderr = Join-Path $OutDir 'run.err.txt'
$sw = [Diagnostics.Stopwatch]::StartNew()
# Workshop is a WinExe: Start-Process + WaitForExit is what gives a real exit code (piping is the other way).
$p = Start-Process -FilePath $Workshop -ArgumentList ($args2 | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) `
        -NoNewWindow -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
$null = $p.Handle   # Windows PowerShell 5.1: without touching Handle, ExitCode stays empty after the process ends
$timedOut = -not $p.WaitForExit($TimeoutSec * 1000)
if ($timedOut) { try { $p.Kill() } catch {} ; $p.WaitForExit(5000) | Out-Null }
$sw.Stop()
$exit = $null; if (-not $timedOut) { $exit = $p.ExitCode }
$out = ''; if (Test-Path $stdout) { $out = Get-Content -Raw $stdout }
$err = ''; if (Test-Path $stderr) { $err = Get-Content -Raw $stderr }
if ($null -eq $out) { $out = '' }; if ($null -eq $err) { $err = '' }

if ($console -eq 'nes' -and $exit -eq 3) {
    # --romtest refuses the core request (exit 3) before it says why the cart did not load: probe once on the default cores for the reason
    $probe = Join-Path $OutDir 'probe.json'
    $pp = Start-Process -FilePath $Workshop -ArgumentList @('--romtest', '--rom', ('"' + $job.rom.path + '"'), '--frames', '1', '--json', '--out', ('"' + $probe + '"')) -NoNewWindow -PassThru `
            -RedirectStandardOutput (Join-Path $OutDir 'probe.out.txt') -RedirectStandardError (Join-Path $OutDir 'probe.err.txt')
    $null = $pp.Handle; $pp.WaitForExit(60000) | Out-Null
    if (Test-Path $probe) { $pr = Get-Content -Raw $probe | ConvertFrom-Json; if ($pr.CrashInfo) { $err += "`n" + $pr.CrashInfo } }
}
$hash = ''; $colors = -1; $fps = 0; $audioPeak = -1
if ($out -match 'hash=([0-9A-F]{16})') { $hash = $Matches[1] }
if ($out -match 'colors=(\d+)') { $colors = [int]$Matches[1] }
if ($out -match '(\d+) fps') { $fps = [int]$Matches[1] }
if ($out -match 'peak=(\d+)') { $audioPeak = [int]$Matches[1] }
$running = ''; if ($out -match 'running (.+)') { $running = $Matches[1].Trim() }
if ($console -eq 'nes') {
    $running = 'NES | CPU FIX | PPU FIX | APU FIX'
    if (Test-Path $reportJson) {
        $rep = Get-Content -Raw $reportJson | ConvertFrom-Json
        $hash = [string]$rep.FrameBufferHash; $colors = [int]$rep.DistinctColors
        foreach ($k in 'Mapper', 'MapperName', 'Result', 'CrashInfo') { if ($rep.PSObject.Properties.Name -contains $k -and $rep.$k) { $running += " | ${k}: $($rep.$k)" } }
    }
}

$shots = @()
foreach ($f in $checkpoints) {
    $png = Get-ChildItem -Path $OutDir -Filter ("*_f{0:D5}.png" -f $f) -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($png) { $shots += [pscustomobject]@{ frame = $f; file = $png.Name; sha1 = (Get-FileDigest -Path $png.FullName) } }
}
$distinct = @($shots | Select-Object -ExpandProperty sha1 -Unique).Count
$beforeStart = @($shots | Where-Object { $_.frame -le 600 } | Select-Object -ExpandProperty sha1)
$afterStart = @($shots | Where-Object { $_.frame -gt 600 } | Select-Object -ExpandProperty sha1)
$respondsToInput = $false
foreach ($a in $afterStart) { if ($beforeStart -notcontains $a) { $respondsToInput = $true } }

$tags = @(); $grade = 'NotTested'; $verdict = 'pass'; $why = 'booted, the picture changes and reacts to the input script'
if ($timedOut) { $grade = 'Broken'; $verdict = 'fail'; $tags += 'timing'; $tags += 'crash'; $why = "runner hung past ${TimeoutSec}s" }
elseif ($exit -eq 4 -or ($err + $out) -match '(?i)unsupported mapper|mapper .*not supported') { $grade = 'Nothing'; $verdict = 'fail'; $tags += 'mapper/board'; $why = 'unsupported board: ' + (([regex]::Match($err + $out, '(?i)mapper[^"\r\n]*not supported')).Value) }
elseif ($exit -ne 0) { $grade = 'Broken'; $verdict = 'fail'; $tags += 'crash'; $why = "exit $exit" }
elseif ($colors -le 1 -and $distinct -le 1) { $grade = 'Nothing'; $verdict = 'fail'; $tags += 'picture'; $why = 'blank picture at every checkpoint' }
elseif ($distinct -le 1) { $grade = 'Nothing'; $verdict = 'fail'; $tags += 'picture'; $why = 'picture frozen at every checkpoint' }
elseif (-not $respondsToInput) { $verdict = 'warn'; $tags += 'input'; $why = 'boots but no checkpoint after Start differs from the title: no reaction to input?' }
if ($audioPeak -eq 0 -and $verdict -ne 'fail') { $tags += 'sound'; if ($verdict -eq 'pass') { $verdict = 'warn' }; $why += '; silent the whole run' }

$result = [ordered]@{
    schema = 'compat-result/1'; jobId = $job.id; tier = 'T1'; console = $console
    rom = $job.rom; grade = $grade; tags = @($tags | Select-Object -Unique)
    core = [ordered]@{ description = $running; version = $Version }
    t1 = [ordered]@{ verdict = $verdict; why = $why; frames = $frames; exit = $exit; timedOut = $timedOut; seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
                     fps = $fps; finalHash = $hash; finalColors = $colors; audioPeak = $audioPeak; distinctCheckpoints = $distinct; respondsToInput = $respondsToInput
                     input = $inputScript; checkpoints = $shots; stderrTail = ($err -split "`n" | Select-Object -Last 5) -join "`n" }
    notes = ''; tas = $null; reference = $null      # filled by T2/T3 and the reference lane
    artifacts = $OutDir; box = $Box; finishedAt = (Get-Date).ToUniversalTime().ToString('o')
}
$result | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 $ResultPath
if ($verdict -eq 'fail' -and $grade -eq 'Broken' -and $timedOut) { exit 3 }
exit 0
