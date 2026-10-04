<#
.SYNOPSIS
  Builds the black-box driver for Nuked-OPLL (the YM2413 oracle, GPL: run it, never copy it) into the tools folder outside the repository.
  Needs the Visual Studio C++ tools (as build-oracles.ps1 does) and a fetched tools folder (Workshop\Sega\fetch-tools.ps1). Output: <Root>\oracles\harness\opll_oracle.exe.
  Nothing is built into or copied to the repository. A separate script from build-oracles.ps1 so the Master System and Genesis tracks never touch the same file.
#>
[CmdletBinding()]
param([string]$Root = 'C:\BrokenNes-tools\sega')
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$vw = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vw -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio C++ tools not found' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$out = Join-Path $Root 'oracles\harness'
New-Item -ItemType Directory -Force $out | Out-Null
$opll = Join-Path $Root 'oracles\nuked\opll'
if (-not (Test-Path (Join-Path $opll 'opll.c'))) { throw "Nuked-OPLL not in the tools folder: $opll" }
$installer = Split-Path $vw
$cmd = "set `"PATH=%PATH%;$installer`" && call `"$vcvars`" >nul && cl /nologo /O2 /W3 /I$opll /Fo$out\ /Fe$out\opll_oracle.exe $here\oracle\opll_oracle.c $opll\opll.c"
cmd /c $cmd
if ($LASTEXITCODE -ne 0) { throw "cl failed ($LASTEXITCODE)" }
"built $out\opll_oracle.exe"
