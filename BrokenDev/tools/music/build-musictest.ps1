<#
.SYNOPSIS
  Builds the VRUN AudioPlayer ROMs that BrokenNes2's "Test MIDI export" uses, from the user's VRUN tree, in a scratch copy.

.DESCRIPTION
  Two modes, both built by VRUN's own toolchain (its build.sh, NESFab, the C# converter, the decoder templates), never inside the
  VRUN tree itself: the parts the AudioPlayer needs are copied to a scratch folder and built there.

  -Mode template  (the default) the TEMPLATE the plugin carries as Plugin/BrokenNes2.Plugin/Resources/musictest.nes: the AudioPlayer
                  with a one-song roster (the synthetic FL-style song of make_test_mid.py) whose four stream arrays are padded to
                  1280 bytes (pad_streams.py; the converter's per-channel maximum), so a song's streams can be written over them by
                  PRG offset. Its song title is 24 characters (the player's maximum) and its loop length a sentinel, so the plugin
                  can write the song's own name and loop length too (the template is built three times with three sentinels: the
                  bytes that hold them are found by comparison). Its square 1, square 2 and triangle decoders are VRUN's pitch-bend
                  decoders (roster "bends": true, "bend_decoders": "all"; checked in the generated decoders), so a song with bend
                  opcodes plays. Writes <Out>\musictest.nes, <Out>\musictest.txt (the manifest: format, version 2, the opcode
                  format, the bend decoders, the PRG offset of each array, the title and the loop-length bytes, the region size, the
                  period table's hash, the ROM's hash) and <Out>\musictest.mlb. NESFab does not build byte-for-byte reproducibly (a few
                  strings change places), so FruityHost midi-test compares the bundled template with a fresh build by what it does.
  -Mode song      a FULL REBUILD of a one-song AudioPlayer for -Mid (no padding, bends on, bend decoders where its streams have
                  bend opcodes, as the game would build it): the reference the template + patch path is compared with. Writes <Out>\<Name>.nes, <Out>\<Name>.mlb, <Out>\music_data.fab, <Out>\build_report.json.

  Needs: the restored VRUN tree (BrokenDev\brokendev.ps1 restore vrun_nes_dev), Git Bash, Python 3 (stdlib only), the .NET SDK
  (VRUN's converter is built by build_music.py in the scratch copy). Exit code 0 built, 1 the build failed, 2 a prerequisite is missing.

.EXAMPLE
  BrokenDev\tools\music\build-musictest.ps1 -Out C:\temp\tmpl
  BrokenDev\tools\music\build-musictest.ps1 -Mode song -Mid P:\...\think.mid -Name think -Out C:\temp\think
#>
param(
    [ValidateSet('template', 'song')][string]$Mode = 'template',
    [string]$Vrun = '',
    [string]$Out = '',
    [string]$Mid = '',
    [string]$Name = 'song',
    [string]$Work = ''
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$bd = Split-Path -Parent (Split-Path -Parent $here)
. (Join-Path $bd 'lib\Common.ps1')
. (Join-Path $bd 'lib\Tools.ps1')

# ---- what this template format is (the plugin refuses a manifest whose format or version it does not know) ----
# Version 2 (round 9, pitch bends): the opcode format is vrun-music-2 (the converter's 0xD1 pitch / 0xD2 slide), and the template's square 1,
# square 2 and triangle decoders are VRUN's bend decoders (roster "bend_decoders": "all"), so a song whose streams hold bend opcodes plays.
# A version-1 template has the plain decoders, which read a bend opcode as the loop: the plugin refuses it.
$FormatName = 'bn2-musictest'
$FormatVersion = 2
$Opcodes = 'vrun-music-2'
$BendChannels = @('square1', 'square2', 'triangle')
$Region = 1280                                   # Midi2Nes.MaxChannelBytes
$Channels = @('square1', 'square2', 'triangle', 'noise')
$Fillers = @{ square1 = 0xA1; square2 = 0xA2; triangle = 0xA3; noise = 0xA4 }   # pad_streams.py
$TemplateTitle = 'MIDI EXPORT FROM FL TEST'                                   # 24 characters, the player's maximum
$SentinelA = 0x6D29; $SentinelB = 0x92D6; $SentinelC = 0x5AA5               # six different bytes: a byte that holds the loop length differs in all three builds

if (-not $Vrun) { $Vrun = Join-Path (Get-BdHome) 'vrun_nes_dev\sys0\simulations\VRUN' }
$workRoot = if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\musictest' } else { throw 'LOCALAPPDATA is not set' }
if (-not $Work) { $Work = Join-Path $workRoot 'scratch' }
if (-not $Out) { $Out = Join-Path $workRoot $Mode }
if ($Name -notmatch '^[a-z][a-z0-9_]{0,23}$') { Write-Host "-Name must be a lowercase identifier (it becomes part of NESFab names): '$Name'"; exit 2 }

# ---- prerequisites, each named when missing (no silent fallback) ----
$need = @('build.sh', 'engine\audio\build_music.py', 'engine\audio\gen_music_decoders.py', 'engine\audio\audio_engine.fab', 'engine\audio\player_ui.fab',
    'engine\audio\sfx_decoders.fab', 'engine\audio\converter\Midi2Nes.cs', 'engine\audio\converter\cli\midi2nes.csproj', 'src\audioplayer\main.fab',
    'src\audioplayer\audioplayer.cfg', 'src\audioplayer\prebuild.sh', 'src\hello\font.png', 'tools\nesfab\nesfab_legal.exe', 'tools\nesfab\lib\nes.fab')
$missing = @($need | Where-Object { -not (Test-Path -LiteralPath (Join-Path $Vrun $_)) })
if ($missing.Count -gt 0) { Write-Host "the VRUN tree at $Vrun lacks: $($missing -join ', ') (restore it: BrokenDev\brokendev.ps1 restore vrun_nes_dev)"; exit 2 }
$bash = Find-BdGitBash
if (-not $bash) { Write-Host 'missing prerequisite: Git Bash'; exit 2 }
$py = $null
foreach ($cand in (Get-BdPythonCandidates)) { $r = Invoke-BdProcess -FilePath $cand -Arguments @('-I', '-c', 'import sys; print(sys.version_info[0])') -TimeoutSec 60; if ($r.ExitCode -eq 0 -and $r.Output -match '^3') { $py = $cand; break } }
if (-not $py) { Write-Host 'missing prerequisite: Python 3'; exit 2 }
if ($Mode -eq 'song') {
    if (-not $Mid -or -not (Test-Path -LiteralPath $Mid)) { Write-Host "-Mode song needs -Mid <file.mid> (got '$Mid')"; exit 2 }
    $Mid = (Resolve-Path -LiteralPath $Mid).Path
}

# ---- the scratch copy: only what the AudioPlayer build reads (the VRUN tree itself is never written) ----
$scratch = Join-Path $Work 'VRUN'
function Copy-Fresh([string]$rel, [switch]$Dir) {
    $src = Join-Path $Vrun $rel; $dst = Join-Path $scratch $rel
    if ($Dir) {
        if (Test-Path -LiteralPath $dst) { Remove-Item -LiteralPath $dst -Recurse -Force }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dst) | Out-Null
        Copy-Item -LiteralPath $src -Destination $dst -Recurse -Force
    } else {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dst) | Out-Null
        Copy-Item -LiteralPath $src -Destination $dst -Force
    }
}
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
# engine/audio: every file but the converter's own build output (build_music.py builds VRUN's converter in the scratch copy, cached by mtime)
$audioDst = Join-Path $scratch 'engine\audio'
foreach ($f in Get-ChildItem -LiteralPath (Join-Path $Vrun 'engine\audio') -Recurse -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }) {
    $rel = $f.FullName.Substring((Join-Path $Vrun 'engine\audio').Length + 1)
    $dst = Join-Path $audioDst $rel
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dst) | Out-Null
    if (-not (Test-Path -LiteralPath $dst) -or (Get-BdSha256 $dst) -ne (Get-BdSha256 $f.FullName)) { Copy-Item -LiteralPath $f.FullName -Destination $dst -Force }
}
foreach ($f in @('build.sh', 'src\audioplayer\main.fab', 'src\audioplayer\audioplayer.cfg', 'src\audioplayer\prebuild.sh', 'src\hello\font.png', 'tools\verify_rom.py')) {
    if (Test-Path -LiteralPath (Join-Path $Vrun $f)) { Copy-Fresh $f }
}
$fabExe = Join-Path $scratch 'tools\nesfab\nesfab_legal.exe'
if (-not (Test-Path -LiteralPath $fabExe) -or (Get-BdSha256 $fabExe) -ne (Get-BdSha256 (Join-Path $Vrun 'tools\nesfab\nesfab_legal.exe'))) { Copy-Fresh 'tools\nesfab\nesfab_legal.exe' }
Copy-Fresh 'tools\nesfab\lib' -Dir

# ---- the one-song project ----
$projName = if ($Mode -eq 'template') { 'musictest' } else { "bn2_$Name" }
$proj = Join-Path $scratch "src\$projName"
if (Test-Path -LiteralPath $proj) { Remove-Item -LiteralPath $proj -Recurse -Force }
New-Item -ItemType Directory -Force -Path $proj | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)
Copy-Item -LiteralPath (Join-Path $scratch 'src\audioplayer\main.fab') -Destination $proj -Force
$cfg = [System.IO.File]::ReadAllText((Join-Path $scratch 'src\audioplayer\audioplayer.cfg')).Replace('vrun_audioplayer.nes', "vrun_$projName.nes")
[System.IO.File]::WriteAllText((Join-Path $proj "$projName.cfg"), $cfg, $utf8)
$track = if ($Mode -eq 'template') { 'song' } else { $Name }
if ($Mode -eq 'template') {
    $r = Invoke-BdProcess -FilePath $py -Arguments @('-I', (Join-Path $here 'make_test_mid.py'), (Join-Path $proj 'song.mid')) -TimeoutSec 60
    if ($r.ExitCode -ne 0) { Write-Host $r.Output; exit 1 }
} else { Copy-Item -LiteralPath $Mid -Destination (Join-Path $proj 'song.mid') -Force }
# The template's title is the longest the player draws (24 characters): the plugin writes the song's own name over it.
$title = if ($Mode -eq 'template') { $TemplateTitle } else { $Name.ToUpperInvariant() }
# Pitch bends: the plugin converts every song with bends on (a song without bends converts exactly as without), so both modes ask VRUN's build
# for them. The template gets the bend decoders on its three tonal channels whatever its own song holds (any song can be patched in); a full
# rebuild gets them where its streams have bend opcodes, as the game would.
$bendKeys = if ($Mode -eq 'template') { ', "bends": true, "bend_decoders": "all"' } else { ', "bends": true' }
[System.IO.File]::WriteAllText((Join-Path $proj 'roster.json'), ('{ "title": "BROKENNES2 MIDI TEST", "tracks": [ {"name": "' + $track + '", "title": "' + $title + '", "midi": "song.mid"' + $bendKeys + '} ] }'), $utf8)
$basePre = [System.IO.File]::ReadAllText((Join-Path $scratch 'src\audioplayer\prebuild.sh')).Replace("`r`n", "`n")

# ---- build with VRUN's own build.sh (the template twice, with two loop-length sentinels: see set_loop_frames.py) ----
$rom = Join-Path $scratch "build\vrun_$projName.nes"; $mlb = Join-Path $scratch "build\vrun_$projName.mlb"
New-Item -ItemType Directory -Force -Path $Out | Out-Null
function Invoke-Build([int]$Sentinel, [string]$LogName) {
    $pre = $basePre
    if ($Mode -eq 'template') {
        $pre += "`npython '" + (ConvertTo-BdBashPath (Join-Path $here 'pad_streams.py')) + "' music_data.fab $Region`n"
        $pre += "python '" + (ConvertTo-BdBashPath (Join-Path $here 'set_loop_frames.py')) + "' music_roster.fab $Sentinel`n"
    }
    [System.IO.File]::WriteAllText((Join-Path $proj 'prebuild.sh'), $pre, $utf8)
    foreach ($f in @($rom, $mlb)) { if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force } }      # a stale ROM must never pass for a fresh build
    $b = Invoke-BdProcess -FilePath $bash -Arguments @('-c', "cd '$(ConvertTo-BdBashPath $scratch)' && ./build.sh $projName") -TimeoutSec 900 `
        -LogFile (Join-Path $Out $LogName) -PathPrepend @((Split-Path -Parent $py))
    if ($b.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $rom) -or -not (Test-Path -LiteralPath $mlb)) {
        Write-Host "build failed (exit $($b.ExitCode)); see $(Join-Path $Out $LogName)"; Write-Host (Select-BdTail $b.Output 20); exit 1
    }
    $b
}
$b = Invoke-Build $SentinelA 'build.log'
$romBytes = [System.IO.File]::ReadAllBytes($rom)

if ($Mode -eq 'song') {
    Copy-Item -LiteralPath $rom -Destination (Join-Path $Out "$Name.nes") -Force
    Copy-Item -LiteralPath $mlb -Destination (Join-Path $Out "$Name.mlb") -Force
    Copy-Item -LiteralPath (Join-Path $proj 'music_data.fab') -Destination (Join-Path $Out 'music_data.fab') -Force
    if (Test-Path -LiteralPath (Join-Path $proj 'build_report.json')) { Copy-Item -LiteralPath (Join-Path $proj 'build_report.json') -Destination (Join-Path $Out 'build_report.json') -Force }
    Write-Host ("built {0}.nes ({1} bytes) in {2} s: {3}" -f $Name, $romBytes.Length, $b.Seconds, (Get-BdSha256 $rom))
    exit 0
}

# ---- the template's manifest: where the four arrays and the title are, read from the .mlb, and checked against the ROM's own bytes ----
$sym = @{}
foreach ($line in [System.IO.File]::ReadAllLines($mlb)) {
    if ($line -match '^NesPrgRom:([0-9A-Fa-f]+)-([0-9A-Fa-f]+):(music_song_(square1|square2|triangle|noise)|msg_roster_track_song)@') {
        $key = if ($Matches[4]) { $Matches[4] } else { 'title' }
        $sym[$key] = @([Convert]::ToInt32($Matches[1], 16), ([Convert]::ToInt32($Matches[2], 16) - [Convert]::ToInt32($Matches[1], 16) + 1))
    }
}
if (-not $sym.ContainsKey('title') -or $sym['title'][1] -ne $TemplateTitle.Length + 1) { Write-Host "the template's .mlb has no 25-byte msg_roster_track_song"; exit 1 }
for ($i = 0; $i -lt $TemplateTitle.Length; $i++) {
    if ($romBytes[16 + $sym['title'][0] + $i] -ne ([int][char]$TemplateTitle[$i] - 32)) { Write-Host 'msg_roster_track_song does not hold the template title (charmap: ASCII - 32)'; exit 1 }
}
$mlbA = [System.IO.File]::ReadAllText($mlb)
$fabA = [System.IO.File]::ReadAllText((Join-Path $proj 'music_data.fab'))
# the template's square 1, square 2 and triangle decoders must be VRUN's bend decoders (they hold the slide state mus_<ch>_vel): read from the generated decoders
$decText = [System.IO.File]::ReadAllText((Join-Path $proj 'music_decoders.fab')).Replace("`r`n", "`n")
foreach ($pair in @(@('square1', 'mus_sq1_vel'), @('square2', 'mus_sq2_vel'), @('triangle', 'mus_tri_vel'))) {
    $fn = [regex]::Match($decText, "(?ms)^fn update_$($pair[0])_song\(\)\n(.*?)(?=^fn |\z)")
    if (-not $fn.Success -or $fn.Groups[1].Value -notmatch [regex]::Escape($pair[1])) { Write-Host "the template's update_$($pair[0])_song is not a bend decoder (no $($pair[1])): VRUN's tree has no pitch-bend engine, or build_music.py ignored ""bend_decoders"": ""all"""; exit 1 }
}
# The loop-length constant: built again with two other sentinels, a byte of the template holds the constant's low (high) byte when the three builds hold the three
# sentinels' low (high) bytes there. (NESFab lays the /strings group out differently when the constant changes, so other bytes move between the builds too: they
# are counted, not used. Only the template itself, build A, is ever patched, at the offsets found in it.)
$b2 = Invoke-Build $SentinelB 'build-b.log'
$romB = [System.IO.File]::ReadAllBytes($rom)
$b3 = Invoke-Build $SentinelC 'build-c.log'
$romC = [System.IO.File]::ReadAllBytes($rom)
if ($romB.Length -ne $romBytes.Length -or $romC.Length -ne $romBytes.Length) { Write-Host 'the sentinel builds of the template have another size'; exit 1 }
$loLo = New-Object System.Collections.Generic.List[int]; $loHi = New-Object System.Collections.Generic.List[int]
$moved = 0
for ($i = 16; $i -lt $romBytes.Length; $i++) {
    if ($romBytes[$i] -eq $romB[$i] -and $romBytes[$i] -eq $romC[$i]) { continue }
    if ($romBytes[$i] -eq ($SentinelA -band 0xFF) -and $romB[$i] -eq ($SentinelB -band 0xFF) -and $romC[$i] -eq ($SentinelC -band 0xFF)) { $loLo.Add($i - 16) }
    elseif ($romBytes[$i] -eq ($SentinelA -shr 8) -and $romB[$i] -eq ($SentinelB -shr 8) -and $romC[$i] -eq ($SentinelC -shr 8)) { $loHi.Add($i - 16) }
    else { $moved++ }
}
if ($loLo.Count -eq 0 -or $loLo.Count -ne $loHi.Count) { Write-Host "the loop length was not found in the ROM ($($loLo.Count) low, $($loHi.Count) high bytes)"; exit 1 }
# NOTE on reproducibility: NESFab 1.8 is not byte-reproducible. Two builds of the same sources place a few strings of the /strings group in another order (and
# the pointers to them follow), with one compiler thread or many. Everything else is identical. So the bundled template is compared with a fresh build by what
# it does (FruityHost midi-test, check "template"): the same manifest apart from the ROM's hash, every differing byte inside the strings or a pointer to one,
# and the same sound and picture frame by frame when both are patched with the same songs.
foreach ($ch in $Channels) {
    if (-not $sym.ContainsKey($ch)) { Write-Host "the template's .mlb names no music_song_$ch"; exit 1 }
    $off = $sym[$ch][0]; $len = $sym[$ch][1]
    if ($len -ne $Region) { Write-Host "music_song_$ch is $len bytes, expected $Region (padding failed)"; exit 1 }
    $last = $romBytes[16 + $off + $len - 1]
    if ($last -ne $Fillers[$ch]) { Write-Host ("music_song_{0} ends with 0x{1:X2}, expected the filler 0x{2:X2}: the offset does not point at the array" -f $ch, $last, $Fillers[$ch]); exit 1 }
}
# the period table the template's decoders read (baked by VRUN's converter); the plugin's converter must make the same one
$fabText = $fabA
$lo = [regex]::Match($fabText, 'PERIOD_LO\s*=\s*U\[128\]\(([^)]*)\)').Groups[1].Value -split ',\s*' | ForEach-Object { [byte][int]$_ }
$hi = [regex]::Match($fabText, 'PERIOD_HI\s*=\s*U\[128\]\(([^)]*)\)').Groups[1].Value -split ',\s*' | ForEach-Object { [byte][int]$_ }
if (@($lo).Count -ne 128 -or @($hi).Count -ne 128) { Write-Host 'could not read the period tables from music_data.fab'; exit 1 }
$sha = [System.Security.Cryptography.SHA256]::Create()
$periods = ([BitConverter]::ToString($sha.ComputeHash([byte[]](@($lo) + @($hi))))).Replace('-', '').ToLowerInvariant()
$romSha = ([BitConverter]::ToString($sha.ComputeHash($romBytes))).Replace('-', '').ToLowerInvariant()
# what the template was built from (provenance: VRUN's engine and AudioPlayer sources, hashed in a fixed order)
$srcFiles = @(Get-ChildItem -LiteralPath (Join-Path $Vrun 'engine\audio') -Recurse -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Extension -in '.cs', '.csproj', '.py', '.fab' } | Sort-Object { $_.FullName.Substring($Vrun.Length).ToLowerInvariant() })
$srcFiles += @('src\audioplayer\main.fab', 'src\audioplayer\audioplayer.cfg', 'src\audioplayer\prebuild.sh') | ForEach-Object { Get-Item -LiteralPath (Join-Path $Vrun $_) }
$acc = New-Object System.Text.StringBuilder
foreach ($f in $srcFiles) { [void]$acc.Append($f.FullName.Substring($Vrun.Length + 1).Replace('\', '/').ToLowerInvariant()).Append(' ').Append((Get-BdSha256 $f.FullName)).Append("`n") }
$srcSha = ([BitConverter]::ToString($sha.ComputeHash($utf8.GetBytes($acc.ToString())))).Replace('-', '').ToLowerInvariant()

$m = New-Object System.Text.StringBuilder
[void]$m.Append("# BrokenNes2 Test MIDI export: the template ROM's manifest. Made by BrokenDev/tools/music/build-musictest.ps1 from VRUN's AudioPlayer (src/audioplayer)`n")
[void]$m.Append("# with a one-song roster whose four stream arrays are padded to the converter's maximum. Offsets are PRG-ROM offsets (after the 16-byte iNES header).`n")
[void]$m.Append("format = $FormatName`n")
[void]$m.Append("version = $FormatVersion`n")
[void]$m.Append("opcodes = $Opcodes`n")
[void]$m.Append("bend-decoders = $($BendChannels -join ' ')`n")
[void]$m.Append("region = $Region`n")
foreach ($ch in $Channels) { [void]$m.Append(("{0} = 0x{1:X6}`n" -f $ch, $sym[$ch][0])) }
[void]$m.Append(("filler = {0:X2} {1:X2} {2:X2} {3:X2}`n" -f $Fillers.square1, $Fillers.square2, $Fillers.triangle, $Fillers.noise))
[void]$m.Append(("title = 0x{0:X6}`n" -f $sym['title'][0]))
[void]$m.Append("title-bytes = $($sym['title'][1])`n")
[void]$m.Append("loop-lo = " + (($loLo | ForEach-Object { '0x{0:X6}' -f $_ }) -join ' ') + "`n")
[void]$m.Append("loop-hi = " + (($loHi | ForEach-Object { '0x{0:X6}' -f $_ }) -join ' ') + "`n")
[void]$m.Append(("loop-template = {0}`n" -f $SentinelA))
[void]$m.Append("periods-sha256 = $periods`n")
[void]$m.Append("rom-bytes = $($romBytes.Length)`n")
[void]$m.Append("rom-sha256 = $romSha`n")
[void]$m.Append("vrun-sources-sha256 = $srcSha`n")
[System.IO.File]::WriteAllText((Join-Path $Out 'musictest.txt'), $m.ToString(), $utf8)
[System.IO.File]::WriteAllBytes((Join-Path $Out 'musictest.nes'), $romBytes)
[System.IO.File]::WriteAllText((Join-Path $Out 'musictest.mlb'), $mlbA, $utf8)
[System.IO.File]::WriteAllText((Join-Path $Out 'music_data.fab'), $fabA, $utf8)
Write-Host ("built the template musictest.nes ({0} bytes) in {1} s (and twice more for the loop-length sentinels), sha256 {2}" -f $romBytes.Length, $b.Seconds, $romSha)
foreach ($ch in $Channels) { Write-Host ("  {0,-8} PRG 0x{1:X6}, {2} bytes" -f $ch, $sym[$ch][0], $sym[$ch][1]) }
Write-Host ("  title    PRG 0x{0:X6}, {1} bytes; loop length low byte at {2}, high byte at {3} ({4} other bytes moved between the sentinel builds: the string layout)" -f $sym['title'][0], $sym['title'][1], (($loLo | ForEach-Object { '0x{0:X6}' -f $_ }) -join ' '), (($loHi | ForEach-Object { '0x{0:X6}' -f $_ }) -join ' '), $moved)
exit 0
