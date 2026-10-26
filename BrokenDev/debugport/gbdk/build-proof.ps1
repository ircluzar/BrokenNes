<#
.SYNOPSIS
  The GBDK library's release-strip proof (BrokenDev milestone D7), on the Game Boy hello world (VRUN: Vaults of Obsolete Code).
.DESCRIPTION
  Copies the game's build.ps1 and src\ (read only: the game's folder is never written) to three folders under -Out and builds:
  dev      build.ps1 -Profile dev       (VOC_DEV: the debug port on)
  release  build.ps1 -Profile release   (every DBG_ macro compiles to nothing)
  none     build.ps1 -Profile release on the sources with every DBG_ line taken out (Strip-DebugCalls.ps1)
  PASS when release is byte-identical to none and dev differs. Exit 0 pass, 1 fail, 2 usage.
#>
param(
    [string]$Game = 'C:\Users\philt\OneDrive\Documents\PROJECTS\VRUN_VOC_dev',
    [string]$Gbdk = '',
    [Parameter(Mandatory)][string]$Out
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path (Join-Path $Game 'build.ps1'))) { Write-Host "no build.ps1 in $Game"; exit 2 }
$strip = Join-Path $PSScriptRoot '..\Strip-DebugCalls.ps1'
$hash = @{}
foreach ($v in 'dev', 'release', 'none') {
    $dir = Join-Path $Out $v
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    New-Item -ItemType Directory -Force (Join-Path $dir 'src') | Out-Null
    Copy-Item (Join-Path $Game 'build.ps1') $dir
    foreach ($f in Get-ChildItem (Join-Path $Game 'src') -File) {
        $to = Join-Path $dir "src\$($f.Name)"
        if ($v -eq 'none' -and $f.Extension -eq '.c') { & $strip -Source $f.FullName -Destination $to } else { Copy-Item $f.FullName $to }
    }
    $args = @('-Profile', $(if ($v -eq 'dev') { 'dev' } else { 'release' }), '-Out', (Join-Path $dir 'build'))
    if ($Gbdk) { $args += @('-Gbdk', $Gbdk) }
    $ErrorActionPreference = 'Continue'   # the build's notes on stderr (makebin's "Converted ...") are not failures: its exit code is
    $log = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $dir 'build.ps1') @args 2>&1 | Out-String
    $ErrorActionPreference = 'Stop'
    $rom = Join-Path $dir 'build\vrun_voc.gbc'
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $rom)) { Write-Host "FAIL: the $v build failed`n$log"; exit 1 }
    $hash[$v] = (Get-FileHash $rom -Algorithm SHA256).Hash.ToLowerInvariant()
    $calls = (Select-String -Path (Join-Path $dir 'src\*.c') -Pattern 'DBG_\w+\s*\(' | Measure-Object).Count
    Write-Host ("{0,-8} sha256 {1} ({2} DBG_ calls in the C sources)" -f $v, $hash[$v], $calls)
}
$same = $hash['release'] -eq $hash['none']; $differs = $hash['dev'] -ne $hash['release']
Write-Host ("release is byte-identical to the build without the calls: {0}; the dev build differs: {1}" -f $same, $differs)
if ($same -and $differs) { Write-Host 'PASS'; exit 0 } else { Write-Host 'FAIL'; exit 1 }
