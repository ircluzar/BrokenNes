<#
.SYNOPSIS
  The frozen Game Boy hello world (VRUN: Vaults of Obsolete Code at milestone V0) for the tests that need it: its sources taken from the game
  repo's commit f828fbe, and optionally built. The live game moves on (V2 and later); these tests do not move with it.
.DESCRIPTION
  The sources come from `git archive` of the pinned commit of the game's own repository (PROJECTS\VRUN_VOC_dev, or BROKENDEV_LIVE_VRUN_VOC_DEV):
  the repository is only read, never checked out, committed to or built in. They are unpacked to <Out>\src and, unless -SourceOnly, built with
  their own build.ps1 and the pinned GBDK-2020 into <Out> (vrun_voc.gbc, .noi, .sym, .map).

  -Out defaults to BROKENDEV_HOME\out\vrun-voc-v0 (P:\BrokenDev\env\out\vrun-voc-v0): the known place the Workshop's --port-test and the
  UAT scripts look for the V0 build. Build it once with:   powershell -File BrokenDev\tests\voc-v0.ps1
  -Profile dev (the default) is the debug-port build; -Define takes the planted-bug negative controls (VOC_PLANT_UNINIT, VOC_PLANT_ASSERT).

  Prints the folder (sources with -SourceOnly, else the build) as its last line. Exit 0 done, 1 the build failed, 2 a missing prerequisite
  (the game repo, its commit, git or GBDK), named.
#>
[CmdletBinding()]
param(
    [string]$Out = '',
    [string]$Gbdk = '',
    [ValidateSet('dev', 'release')][string]$Profile = 'dev',
    [string[]]$Define = @(),
    [switch]$SourceOnly
)
$ErrorActionPreference = 'Stop'
$bd = Split-Path -Parent $PSScriptRoot
. (Join-Path $bd 'lib\Common.ps1')
. (Join-Path $bd 'lib\Tools.ps1')
. (Join-Path $bd 'lib\Toolchains.ps1')

$rev = 'f828fbe223f2645ed7ccf8c873776f4afd1eab51'   # V0: the hello world with the D7 debug port, VOC_PLANT_UNINIT and VOC_PLANT_ASSERT
if (-not $Out) { $Out = Join-Path (Get-BdHome) 'out\vrun-voc-v0' }
$Out = [System.IO.Path]::GetFullPath($Out)

$voc = Find-BdLiveWorkspace -Name 'VRUN_VOC_dev'
if (-not $voc -or -not (Test-Path -LiteralPath (Join-Path $voc '.git'))) { Write-Host 'MISSING: the VRUN_VOC_dev game repository is not beside the repository (BROKENDEV_LIVE_VRUN_VOC_DEV names another)'; exit 2 }
$git = Find-BdExecutable -Name 'git.exe' -Candidates @("$env:ProgramFiles\Git\cmd\git.exe")
if (-not $git) { Write-Host 'MISSING: git'; exit 2 }

$src = Join-Path $Out 'src'
if (Test-Path -LiteralPath $src) { Remove-Item -LiteralPath $src -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$zip = Join-Path $Out 'vrun-voc-v0-src.zip'
$r = Invoke-BdProcess -FilePath $git -Arguments @('-C', $voc, 'archive', '--format=zip', '-o', $zip, $rev) -TimeoutSec 120
if ($r.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $zip)) { Write-Host "MISSING: the game repository has no commit $($rev.Substring(0, 7)) (the V0 hello world): $(Select-BdTail $r.Output 2)"; exit 2 }
Expand-Archive -LiteralPath $zip -DestinationPath $src -Force
Remove-Item -LiteralPath $zip -Force
if (-not (Test-Path -LiteralPath (Join-Path $src 'build.ps1'))) { Write-Host "FAIL: commit $rev has no build.ps1"; exit 1 }
if ($SourceOnly) { Write-Host $src; exit 0 }

if (-not $Gbdk) {
    $gst = Get-BdToolchainState -Spec (Get-BdToolchainSpec -Id gbdk) -SkipZip
    if ($gst.State -ne 'ready') { Write-Host "MISSING: GBDK-2020 is not ready here ($($gst.Detail))"; exit 2 }
    $Gbdk = $gst.Dir
}
$ps5 = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $src 'build.ps1'), '-Gbdk', $Gbdk, '-Profile', $Profile, '-Out', $Out)
if ($Define.Count) { $a += @('-Define', ($Define -join ',')) }
$b = Invoke-BdProcess -FilePath $ps5 -Arguments $a -TimeoutSec 300
if ($b.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $Out 'vrun_voc.gbc'))) { Write-Host "FAIL: the V0 build failed: $(Select-BdTail $b.Output 8)"; exit 1 }
Write-Host "the V0 hello world ($($rev.Substring(0, 7)), $Profile$(if ($Define.Count) { ', ' + ($Define -join ',') })) built in $Out"
Write-Host $Out
exit 0
