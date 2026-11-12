<#
.SYNOPSIS
  Vaults 95's music test with an FL project in its FL TEST slot: <Out>\<safe>.vaults-95-mt.exe + .fence.txt.
.DESCRIPTION
  The player is the game repo's own target (VRUN_VOC_95_dev musictest/build_mt.ps1: the game's music driver voc_sound.c on its
  PSG-on-OPL2 layer win_opl.c + Nuked OPL3, every song, mute / solo, looping). The FL song: FruityHost flp-vocdos (Music/Flp/VocDosBackend.cs: the
  Vaults Game Gear back end's song bytes, which the PC ports' unchanged driver plays, + the PC's fence) -> <name>.vocfl.c. Without -Flp
  the slot is a silent placeholder. A song with no Game Boy instance plays its NES instances (the back end's rule since 2026-11-12, said in the fence;
  -AsGb is no longer needed for NES songs). -AsGb: the .flp's BrokenNes2 instances are read as Game Boy instances (for NES-composed songs; the
  fence says so). The game tree is only read (all output in -Work / -Out).
.EXAMPLE
  pwsh BrokenDev/tools/music/flp/musictest/build-vaults-95-mt.ps1 -Flp "C:\Users\philt\Desktop\Reverse Coaster\battle.flp" -AsGb -Out C:\BrokenNes-work\flp_out
#>
param([string]$Flp = '', [string]$Out = 'C:\BrokenNes-work\flp_out', [string]$Work = 'C:\BrokenNes-work\flp_musictest',
      [string]$Projects = 'C:\Users\philt\OneDrive\Documents\PROJECTS', [string]$FruityHost = '', [switch]$AsGb)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'musictest-common.ps1')
$id = 'vaults-95-mt'; $t = '95'; $game = Join-Path $Projects 'VRUN_VOC_95_dev'; $exe = 'VMT95.EXE'
$safe = Get-MtSafe $Flp
$w = Join-Path $Work "$safe-$id-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
New-Item -ItemType Directory -Force $w, $Out | Out-Null
$fence = Join-Path $Out "$safe.$id.fence.txt"
$args2 = @('-NoProfile', '-File', (Join-Path $game 'musictest\build_mt.ps1'), '-Out', (Join-Path $w 'mt'))
if ($Flp) {
    if (-not (Test-Path $Flp)) { Fail "no such .flp: $Flp" }
    $fh = Find-FruityHost $FruityHost
    $fa = @('flp-vocdos', $Flp, (Join-Path $w 'fl'), $t); if ($AsGb) { $fa += '--as-gb' }
    $log = & $fh @fa 2>&1
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 2) { $log | ForEach-Object { Write-Host "   $_" }; Fail "FruityHost flp-vocdos (exit $LASTEXITCODE)" }
    $name = [IO.Path]::GetFileNameWithoutExtension($Flp)
    $c = Join-Path $w "fl\$name.vocfl.c"; $f = Join-Path $w "fl\$name.vaults-$t.fence.txt"
    if (-not (Test-Path $c) -or -not (Test-Path $f)) { Fail 'flp-vocdos made no song / fence' }
    $lines = Get-Content $f
    $lines[0] = $lines[0] -replace '^[^:]+:', "$([IO.Path]::GetFileName($Flp)) for ${id}:"
    $lines | Set-Content $fence
    $args2 += @('-FlC', $c)
} else { Write-PlaceholderFence $fence $id }
$log = & pwsh @args2 2>&1
if ($LASTEXITCODE -ne 0) { $log | Select-Object -Last 15 | ForEach-Object { Write-Host "   $_" }; Fail 'musictest/build_mt.ps1' }
Get-Content $fence | Select-Object -First 1 | ForEach-Object { Write-Host "   $_" }
Publish-Mt (Join-Path $w "mt\$exe") $Out $safe $id
exit 0
