<#
  Nested certification, the Mesen side: one ROM (the NES game or its Nested SNES ROM) on a PRIVATE Mesen 2.1.1 copy, driven by the per-poll
  buttons a Workshop --nested-cert run recorded (its <stem>.nested-cert.polls.csv), writing the NES work RAM per poll (mesen-poll-lockstep.lua).
  Compare with nested-cert-compare.py. Measures only; the private copy's settings are whatever -MesenDir holds (never the user's own Mesen).

    nested-cert-mesen.ps1 -Rom game.nes|game.smc -System nes|snes -Polls x.polls.csv -MesenDir <private Mesen copy> -Out dir -Tag t [-MaxPolls N] [-PollOffset n]
#>
param(
    [Parameter(Mandatory)][string]$Rom, [Parameter(Mandatory)][ValidateSet('nes', 'snes')][string]$System,
    [Parameter(Mandatory)][string]$Polls, [Parameter(Mandatory)][string]$MesenDir, [Parameter(Mandatory)][string]$Out,
    [string]$Tag = 'mesen', [int]$MaxPolls = 0, [string]$PollOffset = '', [int]$TimeoutSec = 900
)
$lua = Join-Path $PSScriptRoot 'mesen-poll-lockstep.lua'
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$env:MESEN_OUT = $Out; $env:MESEN_TAG = $Tag; $env:MESEN_SYSTEM = $System; $env:MESEN_POLLS = $Polls
$env:MESEN_MAXPOLLS = if ($MaxPolls -gt 0) { [string]$MaxPolls } else { '' }
$env:MESEN_POLL_OFFSET = $PollOffset
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process -FilePath (Join-Path $MesenDir 'Mesen.exe') -ArgumentList "`"$Rom`" --luaScriptsToLoad `"$lua`" --testRunner --timeout=$TimeoutSec" -PassThru -WindowStyle Hidden
if (-not $p.WaitForExit(($TimeoutSec + 60) * 1000)) { & taskkill.exe /PID $p.Id /T /F | Out-Null; Write-Host "Mesen (pid $($p.Id)) did not finish"; exit 2 }
$n = if (Test-Path (Join-Path $Out "$Tag.ram")) { (Get-Item (Join-Path $Out "$Tag.ram")).Length / 2048 } else { 0 }
Write-Host "nested-cert-mesen: $System exit $($p.ExitCode) in $([math]::Round($sw.Elapsed.TotalSeconds, 1)) s, $n polls -> $Out\$Tag.ram"
