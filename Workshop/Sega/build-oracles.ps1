<#
.SYNOPSIS
  Builds the small black-box drivers that run the GPL/oracle-only emulators in the tools folder (outside the repo).
  Needs the Visual Studio C++ tools (the same ones the Native AOT plugin build needs) and a fetched tools folder
  (Workshop\Sega\fetch-tools.ps1). Output: <Root>\oracles\harness\*.exe. Nothing is built into or copied to the repository.
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
$psg = Join-Path $Root 'oracles\nuked\psg'
if (-not (Test-Path (Join-Path $psg 'ympsg.c'))) { throw "Nuked-PSG not in the tools folder: $psg" }
# vcvars64.bat itself calls vswhere, so the Installer folder must be on PATH inside the cmd session. /Fo takes a trailing backslash,
# which must not sit before a closing quote, so the output paths are passed bare (the tools folder has no spaces in its path).
$installer = Split-Path $vw
$cmd = "set `"PATH=%PATH%;$installer`" && call `"$vcvars`" >nul && cl /nologo /O2 /W3 /I$psg /Fo$out\ /Fe$out\psg_oracle.exe $here\oracle\psg_oracle.c $psg\ympsg.c"
cmd /c $cmd
if ($LASTEXITCODE -ne 0) { throw "cl failed ($LASTEXITCODE)" }
"built $out\psg_oracle.exe"
