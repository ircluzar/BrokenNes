<#
.SYNOPSIS
  Builds the CTW SMS .flp test ROM template the BrokenNes2 plugin carries (Plugin/BrokenNes2.Plugin/Resources/flpctwsms.sms + .txt).

.DESCRIPTION
  THE GAME ITSELF (VRUN_CTW_SMS_dev, its own build.ps1: the VRUN NES game translated by fab2c, its music engine, nes_apu.c's PSG
  mapping) built from a copy of the VRUN tree (VRUN_Nes_Dev ... VRUN: its src and engine as they are, generated files included) in which only the TITLE song's four stream arrays
  (music_rosbif_*, the title screen's music) are padded to 1280 bytes (the VRUN converter's per-channel maximum; each with its own
  filler after the loop, as BrokenDev/tools/music/pad_streams.py does). The plugin writes a song's streams over them: the game boots
  to its title screen and plays the song with its real engine. The manifest gives each array's ROM offset (fab2c's FAR_ address =
  bank * 16384 + offset = the file offset), the region size and the commits. Exit 0 when written.
#>
param([string]$Game = 'C:\Users\philt\OneDrive\Documents\PROJECTS\VRUN_CTW_SMS_dev',
      [string]$Vrun = 'C:\Users\philt\OneDrive\Documents\PROJECTS\VRUN_Nes_Dev\sys0\simulations\VRUN',
      [Parameter(Mandatory)][string]$Out, [string]$Commit = 'e9e3a61^1', [int]$Region = 1280, [string]$Song = 'rosbif')
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
# the tracked files as of -Commit (default: the last VRUN commit before the bend decoders, which fab2c cannot translate: their SSF variables)
if ($Commit) {
    $tarFile = Join-Path $Out 'vrun.tar'
    & git -c safe.directory=* -C $Vrun archive --format=tar -o $tarFile $Commit -- src engine
    if ($LASTEXITCODE -ne 0) { throw "git archive $Commit" }
    & "$env:SystemRoot\System32\tar.exe" -xf $tarFile -C $tree
    if ($LASTEXITCODE -ne 0) { throw 'tar' }
}
$fab = Join-Path $tree 'src\game\music_data.fab'
$text = [IO.File]::ReadAllText($fab)
$fill = @{ square1 = 0xA1; square2 = 0xA2; triangle = 0xA3; noise = 0xA4 }
$n = 0
$text = [regex]::Replace($text, "\[\]\s+music_($Song)_(square1|square2|triangle|noise)\s*\n\s*U\[(\d+)\]\(([^)]*)\)", {
    param($m)
    $ch = $m.Groups[2].Value
    $data = @($m.Groups[4].Value -split ',' | ForEach-Object { [int]$_.Trim() })
    if ($data.Count -gt $Region) { throw "$ch has $($data.Count) bytes, more than $Region" }
    $data += @([int]$fill[$ch]) * ($Region - $data.Count)
    $script:n++
    "[] music_$($m.Groups[1].Value)_$ch`n        U[$Region](" + ($data -join ', ') + ')'
})
if ($n -ne 4) { throw "found $n of the 4 $Song stream arrays" }
[IO.File]::WriteAllText($fab, $text)
$build = Join-Path $Out 'build'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Game 'build.ps1') -FabRoot $tree -Out $build
if ($LASTEXITCODE -ne 0) { throw 'the game''s build failed' }
$rom = Get-ChildItem -LiteralPath $build -Filter '*.sms' | Select-Object -First 1
$hdr = [IO.File]::ReadAllText((Join-Path $build 'gen\fab_game.h'))
$bytes = [IO.File]::ReadAllBytes($rom.FullName)
$sha = { param($b) -join ([Security.Cryptography.SHA256]::HashData($b) | ForEach-Object { $_.ToString('x2') }) }
$m = @('# BrokenNes2 .flp test ROM (CTW SMS): the game itself, its title song''s four streams padded. Made by BrokenDev/tools/music/ctwsms/build-ctwsms.ps1. Offsets are ROM file offsets.',
    'format = bn2-flp-ctwsms', 'version = 1',
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
