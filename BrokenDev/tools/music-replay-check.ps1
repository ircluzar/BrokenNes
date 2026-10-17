<#
.SYNOPSIS
  Phase 0 of the music pipeline (docs\projects\brokendev-music-pipeline.md), run end to end TODAY with no plugin: a MIDI file goes
  through VRUN's own converter and NESFab into a one-song AudioPlayer ROM, the ROM runs headless on BrokenNes, and a model of the
  ROM's decoders checks that what the emulated APU does is what the converter's streams say.

.DESCRIPTION
  1. makes (or takes with -Mid) a Standard MIDI File: the stand-in for FL Studio's "export MIDI";
  2. adds a throwaway project src\bd_<Name> to the restored VRUN checkout under BROKENDEV_HOME (outside the repository): the
     AudioPlayer's own main.fab / prebuild.sh / cfg and a one-track roster, and builds it with the project's own build.sh
     (converter -> music_data.fab -> NESFab -> mapper-30 ROM);
  3. runs the ROM on BrokenNes (Workshop --trace, CPU/PPU/APU FIX, no input) for -Frames frames;
  4. tools\music\decoder_model.py predicts the APU state for every frame from the streams in music_data.fab and compares it with the
     trace's APU columns;
  5. NEGATIVE CONTROLS, each must make the check fail or this script fails: (a) one bit of the model's square1 stream flipped,
     (b) the model compared one frame out of step with the ROM (a wrong decode start), (c) a different song's streams.

  What it proves: the ROM on BrokenNes plays exactly the opcode streams the converter produced, frame for frame, including the loop
  point. What it does not: that the converter's rules suit your ear, or anything about the FL plugin (not involved here).

  The throwaway project is removed afterwards unless -Keep. Needs a restored vrun_nes_dev (brokendev.ps1 restore vrun_nes_dev) and the
  Workshop (brokendev.ps1 build-workshop, or BROKENDEV_WORKSHOP).
  Exit code: 0 the model matches the ROM and every negative control failed as it must; 1 otherwise; 2 a tool or prerequisite is missing.
#>
param(
    [string]$Mid = '',
    [string]$Name = 'bdpreview',
    [int]$Frames = 560,
    [switch]$Keep
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$bd = Split-Path -Parent $here
. (Join-Path $bd 'lib\Common.ps1')
. (Join-Path $bd 'lib\Tools.ps1')

if ($Name -notmatch '^[a-z][a-z0-9]{0,15}$') { Write-Host "-Name must be lowercase letters and digits (it becomes a NESFab identifier): '$Name'"; exit 2 }
$bdHome = Get-BdHome
$vrun = Join-Path $bdHome 'vrun_nes_dev\sys0\simulations\VRUN'
$ws = Get-BdWorkshopExe
$py = Find-BdPython
$bash = Find-BdGitBash
foreach ($pair in @(@('restored VRUN checkout', $(if (Test-Path -LiteralPath (Join-Path $vrun 'build.sh')) { $vrun } else { $null })), @('Workshop', $ws), @('Python', $(if ($py) { $py.Path } else { $null })), @('Git Bash', $bash))) {
    if (-not $pair[1]) { Write-Host "missing prerequisite: $($pair[0]) (see BrokenDev\docs\START_HERE.md)"; exit 2 }
}
$outDir = Join-Path $bdHome "out\music-replay\$Name"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$proj = Join-Path $vrun "src\bd_$Name"
$result = [ordered]@{ name = $Name; frames = $Frames }

try {
    # ---- 1. the MIDI ------------------------------------------------------------------------------------------------
    if (-not $Mid) {
        $Mid = Join-Path $outDir "$Name.mid"
        $r = Invoke-BdProcess -FilePath $py.Path -Arguments @('-I', (Join-Path $here 'music\make_test_mid.py'), $Mid) -TimeoutSec 60
        if ($r.ExitCode -ne 0) { Write-Host $r.Output; exit 2 }
    } elseif (-not (Test-Path -LiteralPath $Mid)) { Write-Host "no such MIDI file: $Mid"; exit 2 }
    $result.midi = $Mid
    $result.midiSha256 = Get-BdSha256 $Mid

    # ---- 2. the throwaway project and its build ----------------------------------------------------------------------
    New-Item -ItemType Directory -Force -Path $proj | Out-Null
    foreach ($f in 'main.fab', 'prebuild.sh') { Copy-Item -LiteralPath (Join-Path $vrun "src\audioplayer\$f") -Destination $proj -Force }
    $cfg = [System.IO.File]::ReadAllText((Join-Path $vrun 'src\audioplayer\audioplayer.cfg')).Replace('vrun_audioplayer.nes', "vrun_bd_$Name.nes")
    [System.IO.File]::WriteAllText((Join-Path $proj "bd_$Name.cfg"), $cfg, (New-Object System.Text.UTF8Encoding($false)))
    Copy-Item -LiteralPath $Mid -Destination (Join-Path $proj 'song.mid') -Force
    $roster = '{ "title": "' + $Name.ToUpper() + '", "tracks": [ {"name": "song", "title": "SONG", "midi": "song.mid"} ] }'
    [System.IO.File]::WriteAllText((Join-Path $proj 'roster.json'), $roster, (New-Object System.Text.UTF8Encoding($false)))
    $rom = Join-Path $vrun "build\vrun_bd_$Name.nes"
    if (Test-Path -LiteralPath $rom) { Remove-Item -LiteralPath $rom -Force }       # a stale ROM must never pass for a fresh build
    $vrunBash = ConvertTo-BdBashPath $vrun
    $b = Invoke-BdProcess -FilePath $bash -Arguments @('-c', "cd '$vrunBash' && ./build.sh bd_$Name") -TimeoutSec 900 `
        -LogFile (Join-Path $outDir 'build.log') -PathPrepend @((Split-Path -Parent $py.Path))
    $result.buildSeconds = $b.Seconds
    if ($b.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $rom)) { Write-Host "build failed (exit $($b.ExitCode)); see $(Join-Path $outDir 'build.log')"; Write-Host (Select-BdTail $b.Output 15); exit 1 }
    Copy-Item -LiteralPath $rom -Destination (Join-Path $outDir "$Name.nes") -Force   # kept: open it in BrokenNes to listen
    $result.rom = (Join-Path $outDir "$Name.nes")
    $result.romBytes = (Get-Item -LiteralPath $rom).Length
    $result.romSha256 = Get-BdSha256 $rom
    $report = Read-BdJson (Join-Path $proj 'build_report.json')
    $tr = $report.tracks[0]
    $result.converter = [ordered]@{ channelMap = $tr.channel_map; notes = $tr.notes_total; dropped = $tr.notes_dropped; loopFrames = $tr.loop_frames; bytes = $tr.bytes }
    Copy-Item -LiteralPath (Join-Path $proj 'music_data.fab') -Destination (Join-Path $outDir 'music_data.fab') -Force
    Write-Host ("built {0} ({1} bytes) in {2} s; converter: {3}; loop {4} frames" -f (Split-Path -Leaf $rom), $result.romBytes, $b.Seconds, $tr.channel_map, $tr.loop_frames)

    # ---- 3. run on BrokenNes ------------------------------------------------------------------------------------------
    $trace = Join-Path $outDir 'replay.trace'
    $t = Invoke-BdProcess -FilePath $ws -Arguments @('--trace', '--rom', $rom, '--out', $trace, '--frames', [string]($Frames + 130), '--cpu', 'FIX', '--ppu', 'FIX', '--apu', 'FIX') -TimeoutSec 600 -LogFile (Join-Path $outDir 'trace.log')
    if ($t.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $trace)) { Write-Host "BrokenNes trace failed (exit $($t.ExitCode))"; Write-Host (Select-BdTail $t.Output 10); exit 1 }

    # ---- 4. the model vs the emulated ROM ----------------------------------------------------------------------------
    $fab = Join-Path $proj 'music_data.fab'
    $model = Join-Path $here 'music\decoder_model.py'
    $m = Invoke-BdProcess -FilePath $py.Path -Arguments @('-I', $model, $fab, $trace, '--song', 'song', '--scan-start', '0:120', '--frames', [string]$Frames, '--json', (Join-Path $outDir 'model.json')) -TimeoutSec 300 -LogFile (Join-Path $outDir 'model.log')
    Write-Host (Select-BdTail $m.Output 14)
    $modelJson = Read-BdJson (Join-Path $outDir 'model.json')
    $result.model = [ordered]@{ match = ($m.ExitCode -eq 0); decodeStartFrame = $modelJson.start; agree = $modelJson.total; of = $modelJson.of; worstField = $modelJson.worst }
    $ok = ($m.ExitCode -eq 0)

    # ---- 5. negative controls -----------------------------------------------------------------------------------------
    $controls = @()
    $c1 = Invoke-BdProcess -FilePath $py.Path -Arguments @('-I', $model, $fab, $trace, '--song', 'song', '--scan-start', '0:120', '--frames', [string]$Frames, '--corrupt', '8') -TimeoutSec 300
    $controls += [ordered]@{ control = 'the model''s first 8 square1 notes one semitone off'; failedAsRequired = ($c1.ExitCode -eq 1) }
    $c2 = Invoke-BdProcess -FilePath $py.Path -Arguments @('-I', $model, $fab, $trace, '--song', 'song', '--start', [string]($modelJson.start + 1), '--frames', [string]$Frames) -TimeoutSec 300
    $controls += [ordered]@{ control = 'decode start one frame off'; failedAsRequired = ($c2.ExitCode -eq 1) }
    # a different song's streams: the AudioPlayer's roster ships in the same checkout (the tool picks the first song whose streams differ)
    $other = Join-Path $vrun 'src\audioplayer\music_data.fab'
    if (Test-Path -LiteralPath $other) {
        $c3 = Invoke-BdProcess -FilePath $py.Path -Arguments @('-I', $model, $fab, $trace, '--song', 'song', '--other-fab', $other, '--scan-start', '0:120', '--frames', [string]$Frames) -TimeoutSec 300
        $controls += [ordered]@{ control = 'the streams of a different song'; failedAsRequired = ($c3.ExitCode -eq 1) }
    }
    foreach ($c in $controls) { Write-Host ("  negative control: {0,-50} {1}" -f $c.control, $(if ($c.failedAsRequired) { 'failed as it must' } else { 'DID NOT FAIL - the check cannot be trusted' })) }
    $result.negativeControls = $controls
    if (@($controls | Where-Object { -not $_.failedAsRequired }).Count -gt 0) { $ok = $false }
    $result.pass = $ok
    Save-BdJson -Object $result -Path (Join-Path $outDir 'result.json')
    Write-Host $(if ($ok) { "PASS  music-replay-check ($Name): the ROM on BrokenNes plays what the converter made" } else { "FAIL  music-replay-check ($Name)" })
    if ($ok) { exit 0 } else { exit 1 }
}
finally {
    if (-not $Keep) {
        if (Test-Path -LiteralPath $proj) { Remove-Item -LiteralPath $proj -Recurse -Force -ErrorAction SilentlyContinue }
        foreach ($ext in 'nes', 'mlb') { $f = Join-Path $vrun "build\vrun_bd_$Name.$ext"; if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue } }
    }
}
