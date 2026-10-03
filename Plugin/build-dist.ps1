<#
.SYNOPSIS
  Builds the FL Studio plugin (Native AOT) and puts BrokenNes2_x64.dll in Plugin\dist, where the desktop build picks it up.
.DESCRIPTION
  The plugin needs the MSVC tools to link, so it is built apart from the desktop app. The desktop build (Windows\BrokenNes.Windows.csproj)
  copies Plugin\dist\BrokenNes2_x64.dll next to the program as Plugin\BrokenNes2_x64.dll, and that file is what
  "Config > Synthesizer Mode > Install to FL Studio" (and BrokenNes.Windows.exe --install-vst) installs and what the standalone synth hosts.
  Run this before a desktop publish whenever the plugin or the emulator cores changed:
      pwsh Plugin\build-dist.ps1
      dotnet publish Windows\BrokenNes.Windows.csproj -c Release
  Build output goes outside the repo (same place UAT\certify.ps1 uses); only the finished DLL lands in Plugin\dist (git-ignored).
#>
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$art = Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\bn_build'
$env:PATH += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"   # Native AOT needs vswhere on PATH

& dotnet.exe publish (Join-Path $repo 'Plugin\BrokenNes2.Plugin\BrokenNes2.Plugin.csproj') -c Release --artifacts-path $art --disable-build-servers -nodeReuse:false
if ($LASTEXITCODE -ne 0) { throw "plugin publish failed (exit $LASTEXITCODE)" }

$built = Join-Path $art 'publish\BrokenNes2.Plugin\release_win-x64\BrokenNes2_x64.dll'
if (-not (Test-Path $built)) { throw "publish succeeded but the DLL is not where expected: $built" }
$dist = Join-Path $repo 'Plugin\dist'
New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item $built $dist -Force
$out = Join-Path $dist 'BrokenNes2_x64.dll'
"{0}  {1:N1} MB  sha256 {2}" -f $out, ((Get-Item $out).Length / 1MB), (Get-FileHash $out -Algorithm SHA256).Hash
