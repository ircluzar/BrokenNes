<#
.SYNOPSIS
  Builds the CTW SMS .flp test ROM template the BrokenNes2 plugin carries (Plugin/BrokenNes2.Plugin/Resources/flpctwsms.sms + .txt).

.DESCRIPTION
  THE GAME ITSELF (VRUN_CTW_SMS_dev, its own build.ps1: the VRUN NES game translated by fab2c, its music engine, nes_apu.c's PSG
  mapping) built from a copy of the VRUN tree (VRUN_Nes_Dev ... VRUN: its src and engine as they are, generated files included) in which only the TITLE song's four stream arrays
  (music_rosbif_*, the title screen's music) are padded to the song room (measured, see version 4; each with its own
  filler after the loop, as BrokenDev/tools/music/pad_streams.py does). The plugin writes a song's streams over them: the game boots
  to its title screen and plays the song with its real engine. The manifest gives each array's ROM offset (fab2c's FAR_ address =
  bank * 16384 + offset = the file offset), the region size and the commits. Exit 0 when written. Run it with pwsh (PowerShell 7: SHA256.HashData).
  Version 2 (2026-11-10): the live VRUN tree (fab2c translates the bend decoders) and the title song regenerated with VRUN's bend + duty decoders (regen_decoders.py).
  Version 3 (2026-11-10): the game is translated from the MMC3 main build (VRUN_CTW_SMS_dev tools/fab2c.json: mapper_mmc3.fab + snes_pad_stub_mmc3.fab), not mapper 30.
  Version 4 (2026-11-10): the title song's noise has VRUN's noise-volume decoder (opcode 0xD4), and the region is the song room, measured: a probe build at
  -Region (1280), then ctwsms_room.py re-runs fab2c's far-data packing for the largest length the four arrays can have in the probe's data banks, and the
  template is built at it (-Region 0 = measure; a number = that region, no probe).
#>
param([string]$Game = 'C:\Users\philt\OneDrive\Documents\PROJECTS\VRUN_CTW_SMS_dev',
      [string]$Vrun = 'C:\Users\philt\OneDrive\Documents\PROJECTS\VRUN_Nes_Dev\sys0\simulations\VRUN',
      [Parameter(Mandatory)][string]$Out, [string]$Commit = '', [int]$Region = 0, [int]$ProbeRegion = 1280, [string]$Song = 'rosbif')
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$tree = Join-Path $Out 'vrun'
if (Test-Path -LiteralPath $tree) { throw "$tree exists: use a fresh -Out" }
New-Item -ItemType Directory -Path $tree | Out-Null
# a copy of the live tree's src and engine (what the game's own build translates, generated files included: map_data.fab, the PNGs ...)
foreach ($d in 'src', 'engine') {
    & robocopy (Join-Path $Vrun $d) (Join-Path $tree $d) /E /NFL /NDL /NJH /NJS /NP /XD .git build | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy $d" }
}
$global:LASTEXITCODE = 0
# the tracked files as of -Commit (default none: the live tree; fab2c translates the bend decoders' SSF variables since 2026-11-10)
if ($Commit) {
    $tarFile = Join-Path $Out 'vrun.tar'
    & git -c safe.directory=* -C $Vrun archive --format=tar -o $tarFile $Commit -- src engine
    if ($LASTEXITCODE -ne 0) { throw "git archive $Commit" }
    & "$env:SystemRoot\System32\tar.exe" -xf $tarFile -C $tree
    if ($LASTEXITCODE -ne 0) { throw 'tar' }
}
$fab = Join-Path $tree 'src\game\music_data.fab'
$orig = [IO.File]::ReadAllText($fab)
$fill = @{ square1 = 0xA1; square2 = 0xA2; triangle = 0xA3; noise = 0xA4 }
# the title song gets VRUN's bend decoders (squares, triangle), duty decoders (squares) and noise-volume decoder, as VRUN NES's template has: any song can be patched over it
& python -B (Join-Path $PSScriptRoot 'regen_decoders.py') $tree $Song
 if ($LASTEXITCODE -ne 0) { throw 'regen_decoders.py' }
function Build-At([int]$R, [string]$Dir) {
$script:Region = $R
$script:n = 0
$text = [regex]::Replace($orig, "\[\]\s+music_($Song)_(square1|square2|triangle|noise)\s*\n\s*U\[(\d+)\]\(([^)]*)\)", {
    param($m)
    $ch = $m.Groups[2].Value
    $data = @($m.Groups[4].Value -split ',' | ForEach-Object { [int]$_.Trim() })
    if ($data.Count -gt $Region) { throw "$ch has $($data.Count) bytes, more than $Region" }
    $data += @([int]$fill[$ch]) * ($Region - $data.Count)
    $script:n++
    "[] music_$($m.Groups[1].Value)_$ch`n        U[$Region](" + ($data -join ', ') + ')'
})
if ($script:n -ne 4) { throw "found $($script:n) of the 4 $Song stream arrays" }
[IO.File]::WriteAllText($fab, $text)
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Game 'build.ps1') -FabRoot $tree -Out $Dir
if ($LASTEXITCODE -ne 0) { throw 'the game''s build failed' }
}
$roomLine = "region = given (-Region $Region), not measured"
if ($Region -le 0) {
    Build-At $ProbeRegion (Join-Path $Out 'probe')
    $room = & python -I (Join-Path $PSScriptRoot 'ctwsms_room.py') (Join-Path $Out 'probe\gen') $Song $ProbeRegion
    if ($LASTEXITCODE -ne 0 -or "$room" -notmatch '^region (\d+) free (\d+) banks (\d+)-(\d+)') { throw "ctwsms_room.py: $room" }
    $Region = [int]$Matches[1]
    $roomLine = "region-measured = probe $ProbeRegion per channel; $($Matches[2]) free bytes in data banks $($Matches[3])-$($Matches[4]); $Region fills them (ctwsms_room.py)"
    Write-Host "song room: $room"
}
$build = Join-Path $Out 'build'
Build-At $Region $build
$rom = Get-ChildItem -LiteralPath $build -Filter '*.sms' | Select-Object -First 1
$hdr = [IO.File]::ReadAllText((Join-Path $build 'gen\fab_game.h'))
$bytes = [IO.File]::ReadAllBytes($rom.FullName)
$sha = { param($b) -join ([Security.Cryptography.SHA256]::HashData($b) | ForEach-Object { $_.ToString('x2') }) }
$m = @('# BrokenNes2 .flp test ROM (CTW SMS): the game itself, its title song''s four streams padded. Made by BrokenDev/tools/music/ctwsms/build-ctwsms.ps1. Offsets are ROM file offsets.',
    'format = bn2-flp-ctwsms', 'version = 4', 'decoders = bends square1 square2 triangle; duty square1 square2; noise-volume noise', $roomLine, 'source = the MMC3 main build (main.fab + mapper_mmc3.fab, fab2c.json)',
    "game = VRUN_CTW_SMS_dev $((& git -c safe.directory=* -C $Game rev-parse --short HEAD).Trim())",
    "vrun = $(if ($Commit) { (& git -c safe.directory=* -C $Vrun rev-parse --short $Commit).Trim() + ' (tracked src + engine), untracked generated files of ' } else { 'the live tree at ' })$((& git -c safe.directory=* -C $Vrun rev-parse --short HEAD).Trim())", "song = $Song")
foreach ($ch in 'square1', 'square2', 'triangle', 'noise') {
    if ($hdr -notmatch "#define FAR_music_${Song}_$ch \(\(farp\)(\d+)UL\)") { throw "FAR_music_${Song}_$ch not in fab_game.h" }
    $off = [long]$Matches[1]
    if ($bytes[$off + $Region - 1] -ne $fill[$ch]) { throw "the $ch array is not at $off (its filler is not there)" }
    $m += ('{0} = 0x{1:X6} {2}' -f $ch, $off, $Region)
}
$m += "rom-bytes = $($bytes.Length)"
$m += "rom-sha256 = $(& $sha $bytes)"
Copy-Item -LiteralPath $rom.FullName -Destination (Join-Path $Out 'flpctwsms.sms')
Set-Content -LiteralPath (Join-Path $Out 'flpctwsms.txt') -Value $m -Encoding ascii
Write-Host "ok    $(Join-Path $Out 'flpctwsms.sms')"
