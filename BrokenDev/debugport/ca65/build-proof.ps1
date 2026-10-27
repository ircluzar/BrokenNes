<#
.SYNOPSIS
  The ca65 library's release-strip proof (BrokenDev milestone D7): assembles and links the sample with the pinned cc65 and compares the ROMs.
.DESCRIPTION
  dev      ca65 -D BROKENDEV_DEBUG                      (BrokenNes hears the port)
  planted  ca65 -D BROKENDEV_DEBUG -D PLANT_ASSERT      (the negative control: an assert that fails from the fourth frame)
  release  ca65 without the define                      (every macro assembles to nothing)
  none     the sample with every DBG_ line and the .include taken out (Strip-DebugCalls.ps1), no library at all
  PASS when release is byte-identical to none and dev differs from both. Writes only under -Out. cc65 is read where it is
  (-Cc65: the pinned toolchain's folder, toolchains.json 'cc65'), never changed. Exit 0 pass, 1 fail, 2 usage.
#>
param(
    [Parameter(Mandatory)][string]$Cc65,
    [Parameter(Mandatory)][string]$Out
)
$ErrorActionPreference = 'Stop'
$ca65 = Join-Path $Cc65 'bin\ca65.exe'; $ld65 = Join-Path $Cc65 'bin\ld65.exe'
if (-not (Test-Path $ca65) -or -not (Test-Path $ld65)) { Write-Host "no ca65.exe / ld65.exe in $Cc65\bin"; exit 2 }
$sample = Join-Path $PSScriptRoot 'sample\main.s'
$cfg = Join-Path $PSScriptRoot 'sample\nrom.cfg'
New-Item -ItemType Directory -Force $Out | Out-Null
$results = @{}
foreach ($v in 'dev', 'planted', 'release', 'none') {
    $dir = Join-Path $Out $v
    New-Item -ItemType Directory -Force $dir | Out-Null
    $src = Join-Path $dir 'main.s'
    if ($v -eq 'none') { & (Join-Path $PSScriptRoot '..\Strip-DebugCalls.ps1') -Source $sample -Destination $src }
    else { Copy-Item $sample $src -Force }
    $defs = switch ($v) { 'dev' { @('-D', 'BROKENDEV_DEBUG') } 'planted' { @('-D', 'BROKENDEV_DEBUG', '-D', 'PLANT_ASSERT') } default { @() } }
    $obj = Join-Path $dir 'main.o'; $rom = Join-Path $dir 'game.nes'
    $inc = if ($v -eq 'none') { @() } else { @('-I', $PSScriptRoot) }
    $log = & $ca65 @defs @inc '-o' $obj $src 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { Write-Host "FAIL: ca65 ($v)`n$log"; exit 1 }
    $log = & $ld65 '-C' $cfg '-o' $rom '-m' (Join-Path $dir 'game.map') $obj 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $rom)) { Write-Host "FAIL: ld65 ($v)`n$log"; exit 1 }
    $results[$v] = (Get-FileHash $rom -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host ("{0,-8} {1} bytes, sha256 {2}" -f $v, (Get-Item $rom).Length, $results[$v])
}
$same = $results['release'] -eq $results['none']
$differs = $results['dev'] -ne $results['release']
Write-Host ("release is byte-identical to the build without the calls: {0}; the dev build differs: {1}" -f $same, $differs)
if ($same -and $differs) { Write-Host 'PASS'; exit 0 } else { Write-Host 'FAIL'; exit 1 }
