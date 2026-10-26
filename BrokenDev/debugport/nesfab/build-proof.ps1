<#
.SYNOPSIS
  The NESFab library's release-strip proof (BrokenDev milestone D7): builds the sample three ways and compares the ROMs.
.DESCRIPTION
  dev      the sample with brokendev_dbg.fab          (BrokenNes hears the port)
  release  the sample with brokendev_dbg_off.fab      (every call compiles to nothing)
  none     the sample with every dbg_ line taken out (Strip-DebugCalls.ps1), and no library at all
  PASS when release is byte-identical to none and dev differs from both. Writes only under -Out. NESFab is read where it is (the VRUN
  workspace ships NESFab 1.8: tools\nesfab), never changed. Exit 0 pass, 1 fail, 2 usage.
#>
param(
    [string]$NesFab = 'C:\Users\philt\OneDrive\Documents\PROJECTS\VRUN_Nes_Dev\sys0\simulations\VRUN\tools\nesfab',
    [string]$Sample = (Join-Path $PSScriptRoot 'sample\main.fab'),
    [Parameter(Mandatory)][string]$Out
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path (Join-Path $NesFab 'nesfab.exe'))) { Write-Host "no nesfab.exe in $NesFab"; exit 2 }
New-Item -ItemType Directory -Force $Out | Out-Null
$fwd = { param($p) $p -replace '\\', '/' }
$results = @{}
foreach ($v in 'dev', 'release', 'none') {
    $dir = Join-Path $Out $v
    New-Item -ItemType Directory -Force $dir | Out-Null
    if ($v -eq 'none') { & (Join-Path $PSScriptRoot '..\Strip-DebugCalls.ps1') -Source $Sample -Destination (Join-Path $dir 'main.fab') }
    else { Copy-Item $Sample (Join-Path $dir 'main.fab') -Force }
    $lib = switch ($v) { 'dev' { "input = $(& $fwd (Join-Path $PSScriptRoot 'brokendev_dbg.fab'))" } 'release' { "input = $(& $fwd (Join-Path $PSScriptRoot 'brokendev_dbg_off.fab'))" } default { '' } }
    @("nesfab-dir = $(& $fwd $NesFab)/", 'input = lib/charmap/ascii.fab', $lib, 'input = main.fab', 'output = game.nes', 'mlb = game.mlb') | Where-Object { $_ } | Set-Content (Join-Path $dir 'game.cfg')
    Push-Location $dir
    try { $log = & (Join-Path $NesFab 'nesfab.exe') 'game.cfg' 2>&1 | Out-String } finally { Pop-Location }
    $rom = Join-Path $dir 'game.nes'
    if (-not (Test-Path $rom)) { Write-Host "FAIL: the $v build made no ROM`n$log"; exit 1 }
    $results[$v] = (Get-FileHash $rom -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host ("{0,-8} {1} bytes, sha256 {2}" -f $v, (Get-Item $rom).Length, $results[$v])
}
$same = $results['release'] -eq $results['none']
$differs = $results['dev'] -ne $results['release']
Write-Host ("release is byte-identical to the build without the calls: {0}; the dev build differs: {1}" -f $same, $differs)
if ($same -and $differs) { Write-Host 'PASS'; exit 0 } else { Write-Host 'FAIL'; exit 1 }
