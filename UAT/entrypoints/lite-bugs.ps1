<#
.SYNOPSIS
  Entrypoint test: the bug capture (BrokenDev milestone D6) in BrokenNes Lite (the Blazor WebAssembly AOT build), in a real Edge/Chrome window over
  the DevTools protocol. Exit 0 = every check passed. Ends with ONE JSON result line (entrypoint, pass, checks, seconds).

.DESCRIPTION
  1. Publishes WebLite as AOT (`dotnet publish -c Release -p:EnableWasmAot=true`, under %LOCALAPPDATA%\VRUN_Nes_Dev_work\uat_lite_bugs) unless
     -Publish names an existing publish folder.
  2. Runs UAT\lite\test-lite-bugs.mjs (node 22 or newer) on VRUN and the user's Game Boy game (BrokenDev's builds; any version of vrun-voc: a capture replays exactly whatever the game is): the Bug button's zip (the note
     prompt, the download) replayed exactly by Workshop --bug play, the prompt cancelled and the capture off (negative controls), a console
     session's zip, and the frame times with the capture on and off.
  NOT VERIFIED: the browser's own download bar, a real phone.

.EXAMPLE
  pwsh -File UAT\entrypoints\lite-bugs.ps1 -WorkshopExe C:\path\BrokenNes.Workshop.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WorkshopExe,
    [string]$Publish,
    [string]$NesRom = 'P:\BrokenDev\env\out\vrun-nes\vrun_game.nes',
    [string]$GbRom = 'P:\BrokenDev\env\out\vrun-voc\vrun_voc.gbc',
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\uat_lite_bugs')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sw = [Diagnostics.Stopwatch]::StartNew()
$result = [ordered]@{ entrypoint = 'lite-bugs'; pass = $false }
New-Item -ItemType Directory -Force $WorkDir | Out-Null
try {
    $node = (Get-Command node -ErrorAction SilentlyContinue).Source
    if (-not $node) { throw 'node (22 or newer) is needed' }
    foreach ($f in $WorkshopExe, $NesRom, $GbRom) { if (-not (Test-Path -LiteralPath $f)) { throw "missing $f" } }
    if (-not $Publish) {
        $Publish = Join-Path $WorkDir 'publish'
        $log = Join-Path $WorkDir 'publish.log'
        Write-Host "dotnet publish WebLite (AOT; log: $log) ..."
        & dotnet publish (Join-Path $repo 'WebLite\BrokenNes.Lite.csproj') -c Release -p:EnableWasmAot=true --artifacts-path (Join-Path $WorkDir 'artifacts') -o $Publish -nologo -v q *> $log
        if ($LASTEXITCODE -ne 0) { throw "publish failed, see $log" }
    }
    $roms = Join-Path $WorkDir ('roms-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $zips = Join-Path $WorkDir 'zips'
    New-Item -ItemType Directory -Force $roms, $zips | Out-Null
    $json = Join-Path $WorkDir 'result.json'
    & $node (Join-Path $repo 'UAT\lite\test-lite-bugs.mjs') --publish $Publish --roms $roms --nes $NesRom --gb $GbRom --workshop $WorkshopExe --out $zips --json $json 2>&1 | ForEach-Object { Write-Host $_ }
    $code = $LASTEXITCODE
    $checks = if (Test-Path $json) { @(Get-Content $json -Raw | ConvertFrom-Json) } else { @() }
    $result.checks = $checks
    $result.pass = ($code -eq 0 -and $checks.Count -gt 0)
}
catch { $result.error = $_.Exception.Message }
$result.seconds = [int]$sw.Elapsed.TotalSeconds
$result | ConvertTo-Json -Compress -Depth 6
if ($result.pass) { exit 0 } else { exit 1 }
