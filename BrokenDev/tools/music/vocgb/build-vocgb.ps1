<#
.SYNOPSIS
  Builds the Vaults GB .flp test ROM template the BrokenNes2 plugin carries (Plugin/BrokenNes2.Plugin/Resources/flpvocgb.gbc + .txt).

.DESCRIPTION
  The game's OWN music driver (VRUN_VOC_dev src/music/voc_music.c + voc_music_asm.s, compiled unchanged, the game's sfx stand-in
  tests/music/sfx_stub.c) + flp_data.c (one song: reserved instrument / wave / noise tables) + flp_streams.c x4 (one 16,376-byte
  reserved stream per bank 57-60) + flp_main.c (plays song 0 from power-on). The manifest gives the ROM offset of every reserved
  table and stream (from the linker's .noi), the game commit and the hashes of the driver sources, so the plugin can say which
  driver it carries. Exit 0 when written.
#>
param([string]$Game = 'C:\Users\philt\OneDrive\Documents\PROJECTS\VRUN_VOC_dev', [string]$Gbdk = 'P:\BrokenDev\env\toolchains\gbdk\4.5.0\gbdk',
      [Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$lcc = Join-Path $Gbdk 'bin\lcc.exe'
$obj = Join-Path $Out 'obj'
New-Item -ItemType Directory -Force -Path $obj | Out-Null
Get-ChildItem -LiteralPath $obj -File | Remove-Item -Force
$inc = '-I' + (Join-Path $Game 'src\music')
$objs = @()
function Cc([string]$src, [string]$o, [string[]]$extra = @()) {
    $a = @('-c', $inc) + $extra + @('-o', $o, $src)
    & $lcc @a
    if ($LASTEXITCODE -ne 0) { throw "lcc $src" }
    $script:objs += $o
}
$drv = @('voc_music.c', 'voc_music_asm.s') | ForEach-Object { Join-Path $Game "src\music\$_" }
foreach ($s in $drv) { Cc $s (Join-Path $obj ('drv_' + [IO.Path]::GetFileNameWithoutExtension($s) + '.o')) }
Cc (Join-Path $Game 'tests\music\sfx_stub.c') (Join-Path $obj 'sfx_stub.o')
Cc (Join-Path $here 'flp_data.c') (Join-Path $obj 'flp_data.o')
Cc (Join-Path $here 'flp_main.c') (Join-Path $obj 'flp_main.o')
for ($i = 0; $i -lt 4; $i++) {
    # (SDCC does not expand a macro in #pragma bank: one source per bank, written from flp_streams.c)
    $s = Join-Path $obj "flp_s$i.c"
    Set-Content -LiteralPath $s -Encoding ascii -Value ((Get-Content -Raw (Join-Path $here 'flp_streams.c')).Replace('FLP_BANK', [string](57 + $i)).Replace('FLP_NAME', "flp_s$i"))
    Cc $s (Join-Path $obj "flp_s$i.o")
}
$rom = Join-Path $Out 'flpvocgb.gbc'
$a = @('-autobank', '-Wm-yc', '-Wm-yt0x1B', '-Wm-yo64', '-Wm-ya4', '-Wm-ynVOC_FLP', '-Wl-m', '-Wl-j', '-Wl-w', '-o', $rom) + $objs
& $lcc @a
if ($LASTEXITCODE -ne 0) { throw 'link' }
$noi = @{}
foreach ($l in Get-Content ([IO.Path]::ChangeExtension($rom, '.noi'))) { if ($l -match '^DEF (\S+) 0x([0-9A-Fa-f]+)') { $noi[$Matches[1]] = [Convert]::ToInt64($Matches[2], 16) } }
function Off([string]$sym) {
    if (-not $noi.ContainsKey($sym)) { throw "symbol $sym not in the .noi" }
    $v = $noi[$sym]; $bank = [int]($v -shr 16); $addr = [int]($v -band 0xFFFF)
    if ($addr -lt 0x4000) { return $addr }
    return $bank * 0x4000 + $addr - 0x4000
}
$bytes = [IO.File]::ReadAllBytes($rom)
$sha = { param($b) -join ([Security.Cryptography.SHA256]::HashData($b) | ForEach-Object { $_.ToString('x2') }) }
$commit = (& git -c safe.directory=* -C $Game rev-parse --short HEAD).Trim()
$drvSha = & $sha ([byte[]]($drv | ForEach-Object { [IO.File]::ReadAllBytes($_) } | ForEach-Object { $_ }))
$m = @(
    '# BrokenNes2 .flp test ROM (Vaults GB): the game''s own music driver + one reserved song. Made by BrokenDev/tools/music/vocgb/build-vocgb.ps1. Offsets are ROM file offsets.',
    'format = bn2-flp-vocgb', 'version = 1', "game = VRUN_VOC_dev $commit", "driver-sha256 = $drvSha",
    ('instab = 0x{0:X6} 128' -f (Off '_mus_instab')), ('wavetab = 0x{0:X6} 256' -f (Off '_mus_wavetab')), ('noisetab = 0x{0:X6} 128' -f (Off '_mus_noisetab')),
    ('songtab = 0x{0:X6} 14' -f (Off '_mus_songtab')))
for ($i = 0; $i -lt 4; $i++) { $m += ('stream{0} = 0x{1:X6} 16376' -f $i, (Off "_flp_s$i")) }
$m += ('frames = 0x{0:X4}' -f ($noi['_flp_frames'] -band 0xFFFF))
$m += "rom-bytes = $($bytes.Length)"
$m += "rom-sha256 = $(& $sha $bytes)"
Set-Content -LiteralPath (Join-Path $Out 'flpvocgb.txt') -Value $m -Encoding ascii
Write-Host "ok    $rom"
