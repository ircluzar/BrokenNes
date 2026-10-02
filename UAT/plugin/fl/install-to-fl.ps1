<#
.SYNOPSIS
  Installs BrokenNes2_x64.dll into FL Studio's native generator folder.
.DESCRIPTION
  FL Studio loads native ("Fruity") plugins from  <FL>\Plugins\Fruity\Generators\<Name>\<Name>_x64.dll.
  That folder is under Program Files, so the copy needs administrator rights: when not elevated this script
  re-launches itself elevated (one UAC prompt) and waits for it. It refuses to run while FL Studio is open
  (a loaded DLL is locked, and FL would not see the new build until restarted anyway).
  Nothing else on the machine is touched.
.EXAMPLE
  install-to-fl.cmd        (double-click it; no arguments needed: installs the latest build)
  powershell -ExecutionPolicy Bypass -File install-to-fl.ps1 [-Dll <path to BrokenNes2_x64.dll>]
#>
param(
    [string]$Dll = (Join-Path $PSScriptRoot '..\..\..\Plugin\dist\BrokenNes2_x64.dll'),
    [string]$FlRoot = "C:\Program Files\Image-Line\FL Studio 2026",
    [switch]$Elevated
)
$ErrorActionPreference = 'Stop'

$dest = Join-Path $FlRoot 'Plugins\Fruity\Generators\BrokenNes2'
$target = Join-Path $dest 'BrokenNes2_x64.dll'
if (-not (Test-Path $Dll)) { throw "plugin DLL not found: $Dll" }
$Dll = (Resolve-Path $Dll).Path

if (Get-Process -Name FL64 -ErrorAction SilentlyContinue) {
    throw "FL Studio is running: close it first (it keeps the plugin DLL locked and only scans plugins at start-up)."
}

function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

$same = (Test-Path $target) -and ((Get-FileHash $target).Hash -eq (Get-FileHash $Dll).Hash)
if ($same) { Write-Host "already installed and identical: $target"; exit 0 }

if (-not (Test-Admin)) {
    if ($Elevated) { throw "still not elevated after the UAC prompt" }
    Write-Host "administrator rights are needed to write to $dest : approve the UAC prompt..."
    $p = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Dll', "`"$Dll`"", '-FlRoot', "`"$FlRoot`"", '-Elevated')
    if ($p.ExitCode -ne 0) { throw "the elevated install failed (exit $($p.ExitCode))" }
} else {
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item $Dll $target -Force
    $pdb = [IO.Path]::ChangeExtension($Dll, '.pdb')
    if (Test-Path $pdb) { Copy-Item $pdb $dest -Force }
}

if (-not (Test-Path $target) -or (Get-FileHash $target).Hash -ne (Get-FileHash $Dll).Hash) { throw "install verification failed: $target" }
Write-Host "installed: $target"
exit 0
