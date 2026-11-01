<#
.SYNOPSIS
  The GBDK library's release-strip proof (BrokenDev milestone D7), on the Game Boy hello world (VRUN: Vaults of Obsolete Code).
.DESCRIPTION
  -Game defaults to the frozen V0 hello world (tests\voc-v0.ps1 -SourceOnly into <Out>\v0).
  Copies the game's build.ps1 and src\ (read only: the game's folder is never written) to three folders under -Out and builds:
  dev      build.ps1 -Profile dev       (VOC_DEV: the debug port on)
  release  build.ps1 -Profile release   (every DBG_ macro compiles to nothing)
  none     build.ps1 -Profile release on the sources with every DBG_ line taken out (Strip-DebugCalls.ps1)
  PASS when release is byte-identical to none and dev differs. Exit 0 pass, 1 fail, 2 usage.
#>
param(
    [string]$Game = '',
    [string]$Gbdk = '',
    [Parameter(Mandatory)][string]$Out
)
$ErrorActionPreference = 'Stop'
# -Game: a folder with the game's build.ps1 and a flat src\. Without one, the frozen V0 hello world (the game repo's commit f828fbe, taken
# read only by BrokenDev\tests\voc-v0.ps1): the live game has moved on (V2 builds from src\maps and src\phys too, which this proof does not copy)
if (-not $Game) {
    $log = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot '..\..\tests\voc-v0.ps1') -SourceOnly -Out (Join-Path $Out 'v0') 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { Write-Host "no -Game, and the frozen V0 sources are not available:`n$log"; exit 2 }
    $Game = (@($log.Trim() -split "`r?`n")[-1]).Trim()
}
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
