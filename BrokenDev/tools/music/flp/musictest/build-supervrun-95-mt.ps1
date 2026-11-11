<#
.SYNOPSIS
  Super VRUN 95's music test with an FL project in its FL TEST slot: <Out>\<safe>.supervrun-95-mt.exe + .fence.txt.
.DESCRIPTION
  The player is the game repo's own target (SuperVRUN_95_dev musictest/build_mt.ps1: the game's driver win_snd.c on its software OPL2, every song,
  mute / solo, looping). The FL song takes the music pipeline's Super VRUN path (../build-supervrun.ps1 -Engine 95 -FenceOnly:
  FruityHost flp-backend -> sf2it.py -> the .it + the fence), then the game's oplconv.py makes the slot's stream. Without -Flp the slot
  is a silent placeholder. The game tree is only read (all output in -Work / -Out).
.EXAMPLE
  pwsh BrokenDev/tools/music/flp/musictest/build-supervrun-95-mt.ps1 -Flp "C:\Users\philt\Desktop\Reverse Coaster\overworld.flp" -Out C:\BrokenNes-work\flp_out
#>
param([string]$Flp = '', [string]$Out = 'C:\BrokenNes-work\flp_out', [string]$Work = 'C:\BrokenNes-work\flp_musictest',
      [string]$Projects = 'C:\Users\philt\OneDrive\Documents\PROJECTS', [string]$FruityHost = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'musictest-common.ps1')
$id = 'supervrun-95-mt'; $game = Join-Path $Projects 'SuperVRUN_95_dev'
$safe = Get-MtSafe $Flp
$w = Join-Path $Work "$safe-$id-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
New-Item -ItemType Directory -Force $w, $Out | Out-Null
$fence = Join-Path $Out "$safe.$id.fence.txt"
$it = ''; $title = ''
if ($Flp) {
    if (-not (Test-Path $Flp)) { Fail "no such .flp: $Flp" }
    $fh = Find-FruityHost $FruityHost
    $log = & pwsh -NoProfile -File (Join-Path $PSScriptRoot '..\build-supervrun.ps1') -Flp $Flp -Engine 95 -FenceOnly -Out (Join-Path $w 'fence') -Work (Join-Path $w 'flp') -FruityHost $fh -Projects $Projects 2>&1
    if ($LASTEXITCODE -ne 0) { $log | ForEach-Object { Write-Host "   $_" }; Fail 'build-supervrun.ps1 -FenceOnly' }
    $f = Get-ChildItem (Join-Path $w 'fence') -Filter '*.95.fence.txt' | Select-Object -First 1
    $it = (Get-ChildItem (Join-Path $w 'flp') -Recurse -Filter 'rosbif.it' | Select-Object -First 1).FullName
    if (-not $f -or -not $it) { Fail 'the pipeline made no fence / .it' }
    $lines = Get-Content $f.FullName
    $lines[0] = $lines[0] -replace '^[^:]+:', "$([IO.Path]::GetFileName($Flp)) for ${id}:"
    $lines | Set-Content $fence
    $title = [IO.Path]::GetFileNameWithoutExtension($Flp)
} else { Write-PlaceholderFence $fence $id }
$args2 = @('-NoProfile', '-File', (Join-Path $game 'musictest\build_mt.ps1'), '-Out', (Join-Path $w 'mt'), '-Snes', (Join-Path $Projects 'SuperVRUN_dev\snes-dev\supervrun'))
if ($it) { $args2 += @('-FlIt', $it, '-Title', $title) }
$log = & pwsh @args2 2>&1
if ($LASTEXITCODE -ne 0) { $log | Select-Object -Last 15 | ForEach-Object { Write-Host "   $_" }; Fail 'musictest/build_mt.ps1' }
Get-Content $fence | Select-Object -First 1 | ForEach-Object { Write-Host "   $_" }
Publish-Mt (Join-Path $w 'mt\SVMT95.EXE') $Out $safe $id
exit 0
