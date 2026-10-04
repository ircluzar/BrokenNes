<#
.SYNOPSIS
  Builds the small black-box drivers that run the GPL/LGPL oracle-only emulators in the tools folder (outside the repo), and the reference build of the permissively licensed ymfm.
  Needs the Visual Studio C++ tools (the same ones the Native AOT plugin build needs) and a fetched tools folder
  (Workshop\Sega\fetch-tools.ps1). Output: <Root>\oracles\harness\*.exe. Nothing is built into or copied to the repository.
    psg_oracle.exe   Nuked-PSG (the Genesis VDP's SN76489) driven from a script
    opn2_oracle.exe  Nuked-OPN2 (YM3438 / YM2612, LGPL-2.1) driven from a script
    ymfm_ref.exe     ymfm's ym2612 / ym3438 (BSD-3-Clause, the source of the C# port) driven from the same kind of script
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
# vcvars64.bat itself calls vswhere, so the Installer folder must be on PATH inside the cmd session. /Fo takes a trailing backslash,
# which must not sit before a closing quote, so the output paths are passed bare (the tools folder has no spaces in its path).
$installer = Split-Path $vw
function Invoke-Cl([string]$clArgs)
{
    $cmd = "set `"PATH=%PATH%;$installer`" && call `"$vcvars`" >nul && cl /nologo $clArgs"
    cmd /c $cmd
    if ($LASTEXITCODE -ne 0) { throw "cl failed ($LASTEXITCODE)" }
}

$psg = Join-Path $Root 'oracles\nuked\psg'
if (Test-Path (Join-Path $psg 'ympsg.c'))
{
    Invoke-Cl "/O2 /W3 /I$psg /Fo$out\ /Fe$out\psg_oracle.exe $here\oracle\psg_oracle.c $psg\ympsg.c"
    "built $out\psg_oracle.exe"
}
else { Write-Warning "Nuked-PSG not in the tools folder ($psg): psg_oracle.exe not built" }

$opn2 = Join-Path $Root 'oracles\nuked\opn2'
if (Test-Path (Join-Path $opn2 'ym3438.c'))
{
    Invoke-Cl "/O2 /W3 /I$opn2 /Fo$out\ /Fe$out\opn2_oracle.exe $here\oracle\opn2_oracle.c $opn2\ym3438.c"
    "built $out\opn2_oracle.exe"
}
else { Write-Warning "Nuked-OPN2 not in the tools folder ($opn2): opn2_oracle.exe not built" }

$ymfm = Join-Path $Root 'port-sources\ymfm\src'
if (Test-Path (Join-Path $ymfm 'ymfm_opn.cpp'))
{
    Invoke-Cl "/O2 /EHsc /std:c++17 /W3 /I$ymfm /Fo$out\ /Fe$out\ymfm_ref.exe $here\oracle\ymfm_ref.cpp $ymfm\ymfm_opn.cpp $ymfm\ymfm_adpcm.cpp $ymfm\ymfm_ssg.cpp"
    "built $out\ymfm_ref.exe"
}
else { Write-Warning "ymfm not in the tools folder ($ymfm): ymfm_ref.exe not built" }
