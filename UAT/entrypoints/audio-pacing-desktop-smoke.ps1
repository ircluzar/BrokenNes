<#
.SYNOPSIS
  With the machine's REAL default playback device, the desktop app runs at the console's own frame rate (60.0988 NES, 59.73 Game
  Boy) with continuous sound: no underruns in steady state, the device taking sound at its real rate, a bounded queue, and no more
  dropouts in what the speakers get than the headless Workshop render of the same ROM has. Exit 0 = pass. Prints ONE JSON result
  line as the last line of stdout.

.DESCRIPTION
  The bug (2026-10-29): with a sound device open the app ran ~44 fps and the sound was choppy; with none it ran 60. The WinMM
  output (WaveOutEvent, 3 x 750-sample buffers) was taken by the Focusrite USB device in whole 512-sample periods - every 17 ms
  buffer cost 23.2 ms, so the device took 32,150 samples a second of the 44,100 made (73 %) and left ~6 ms of silence after each
  buffer - and the emulation loop made frames only as that queue drained (audio-driven pacing), so the game ran at 73 % speed.
  The fix (Windows/AudioManager.cs, MainForm.Emulation.cs): WASAPI shared-mode output, frames paced by the clock, the queue held
  by +-0.5 % rate control. BROKENNES_AUDIO_LEGACY=1 restores the old output + pacing: that is the NEGATIVE CONTROL here.

  For each of NES (VRUN) and Game Boy (the VRUN: VOC sound-effects test ROM), FIXED first, then LEGACY:
    the app starts with scratch BROKENNES_CONFIG_DIR / SAVES / BUGS, emulation volume -Volume % (quiet, but not muted: the
    loopback has to hear it), the ROM loads, -WarmupSeconds pass, then for -Seconds:
      fps        frames the emulation ran / wall time (GET /api/audio/device framesEmulated), against pacedFps
      underruns  device reads that found the queue short, over the window (the app's own meter, AudioManager.UnderrunMeter)
      readRate   samples a second the device really took over the window (44100 = real time)
      queue      the app's queue depth (bufferedMs), sampled every 50 ms: min / avg / max
      submitted  the app's submitted-sample stream (BROKENNES_AUDIO_SUBMIT_LOG): the longest gap between submissions
      capture    WASAPI loopback of the default device (UAT/lib/AudioLoopback.cs, NAudio from the app's own folder, no
                 download): dropouts = runs of digital silence 0.5-30 ms inside sound, against the same count in the
                 Workshop render (--apu-audio / --console-run --wav) of the same number of frames, scaled and quantised
                 to 16 bits the way the app does.
  Checks, FIXED:  fps within 1 % of pacedFps; 0 underruns in the window; readRate within 1.5 % of the output rate; queue max
                  <= 150 ms; capture dropouts <= 1.5 x reference + 5 (unverified if the device was not quiet before the run).
  Checks, LEGACY (negative control, -SkipLegacy to leave out): the old pacing must reproduce the drop on this machine - fps
                  below 90 % of pacedFps. (On a device whose WinMM path is not broken it would not; then the control is
                  reported as not reproducible rather than passed.)
  Then: %APPDATA%\BrokenNes is untouched. Rule zero (UAT\README.md): never kill by process name; only PIDs started here.

.EXAMPLE
  pwsh -File UAT\entrypoints\audio-pacing-desktop-smoke.ps1 -Exe C:\path\BrokenNes.Windows.exe -Workshop C:\path\BrokenNes.Workshop.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Workshop,
    [string]$NesRom = 'C:\Users\philt\OneDrive\Documents\PROJECTS\VRUN_Nes_Dev\builds\2026-09-27_nes\vrun_game.nes',
    [string]$GbRom = 'C:\Users\philt\OneDrive\Documents\PROJECTS\VRUN_VOC_dev_sfx\build\sfx_test\vrun_voc_sfx_test.gbc',
    [int]$Seconds = 60,
    [int]$WarmupSeconds = 6,
    [int]$Volume = 10,
    [switch]$SkipLegacy,
    [ValidateSet('nes', 'gb')][string[]]$Games = @('nes', 'gb'),
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\uat_audio_pacing_desktop')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$checks = [ordered]@{}
$result = [ordered]@{ entrypoint = 'audio-pacing-desktop'; pass = $false; runs = [ordered]@{} }
$inv = [Globalization.CultureInfo]::InvariantCulture

function Check($name, [bool]$ok, $detail) {
    $script:checks[$name] = [ordered]@{ ok = $ok; detail = "$detail" }
    Write-Host ("[{0}] {1} - {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}
function Info($name, $detail) { $script:checks[$name] = [ordered]@{ ok = $true; detail = "$detail" }; Write-Host "[INFO] $name - $detail" }
function Listing([string]$dir) {
    if (-not (Test-Path -LiteralPath $dir)) { return '' }
    (@(Get-ChildItem -LiteralPath $dir -Recurse -File -Force -ErrorAction SilentlyContinue | Sort-Object FullName | ForEach-Object { "$($_.FullName.Substring($dir.Length))|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" })) -join "`n"
}
function Device([int]$procId) { $r = Invoke-BrokenNesApi -ProcessId $procId -Method GET -Path '/api/audio/device' -TimeoutSec 30 -Raw; if ($r.StatusCode -eq 200) { $r.Body.device } else { $null } }
function RunWs([string[]]$a, [string]$log) {
    $p = Start-Process -FilePath $Workshop -ArgumentList $a -Wait -PassThru -NoNewWindow -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    if ($p.ExitCode -ne 0) { throw "Workshop $($a[0]) exit $($p.ExitCode): $(Get-Content -Raw $log) $(Get-Content -Raw "$log.err")" }
}

$saved = @{}
foreach ($k in 'BROKENNES_CONFIG_DIR', 'BROKENNES_SAVES_DIR', 'BROKENNES_BUGS_DIR', 'BROKENNES_TEST_AUDIO_DEVICE_FILE', 'BROKENNES_AUDIO_LEGACY', 'BROKENNES_AUDIO_SUBMIT_LOG', 'BROKENNES_AUDIO_SUBMIT_WAV', 'BROKENNES_AUDIO_OUTPUT') { $saved[$k] = [Environment]::GetEnvironmentVariable($k) }
$userDir = Join-Path $env:APPDATA 'BrokenNes'
$before = Listing $userDir
$inst = $null
try {
    . (Join-Path $repo 'UAT\lib\ApiClient.ps1')
    foreach ($r in $Exe, $Workshop, $NesRom, $GbRom) { if (-not (Test-Path -LiteralPath $r)) { throw "missing $r" } }
    if (Test-Path -LiteralPath $WorkDir) { Remove-Item -LiteralPath $WorkDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

    # the loopback recorder and the dropout counter, built against the NAudio the app ships with
    $appDir = Split-Path -Parent $Exe
    $naudio = @((Join-Path $appDir 'NAudio.Core.dll'), (Join-Path $appDir 'NAudio.Wasapi.dll'))
    foreach ($a in $naudio) { Add-Type -Path $a }
    Add-Type -Path (Join-Path $repo 'UAT\lib\AudioLoopback.cs') -ReferencedAssemblies ($naudio + 'netstandard', 'System.Runtime', 'System.Threading', 'System.Threading.Thread', 'System.Console')

    # is the device quiet without us? (other programs' sound in the loopback would be counted too)
    $idleWav = Join-Path $WorkDir 'idle.wav'
    $rec = [LoopbackRecorder]::new($idleWav); $rec.Start(); Start-Sleep -Seconds 2; $rec.Stop(); $rec.Dispose()
    $idle = [GapAnalyzer]::Analyze($idleWav, 0, 0)
    $quiet = $idle.AudibleSeconds -lt 0.05
    Info 'device_quiet_before' ("device loopback $($rec.Format); 2 s before any run: $idle" + $(if ($quiet) { '' } else { ' - other sound on the device: only a process loopback of our PID can be judged' }))

    $gameList = @(
        [ordered]@{ key = 'nes'; rom = $NesRom; ext = '.nes' },
        [ordered]@{ key = 'gb'; rom = $GbRom; ext = '.gbc' }
    )
    $modes = @('fixed'); if (-not $SkipLegacy) { $modes += 'legacy' }

    foreach ($g in @($gameList | Where-Object { $Games -contains $_.key })) {
        # the reference: the same ROM headless, for the frames the window covers (+ the warm-up), scaled + quantised like the app
        $frames = [int](($WarmupSeconds + $Seconds + 8) * 60)
        $refWav = Join-Path $WorkDir "$($g.key)_ref.wav"
        if ($g.key -eq 'nes') { RunWs @('--apu-audio', '--rom', $g.rom, '--cpu', 'FIX', '--ppu', 'FIX', '--apu', 'FIX', '--frames', "$frames", '--wav', $refWav) (Join-Path $WorkDir 'ref_nes.log') }
        else { RunWs @('--console-run', '--rom', $g.rom, '--frames', "$frames", '--out', $WorkDir, '--wav', $refWav) (Join-Path $WorkDir 'ref_gb.log') }

        foreach ($mode in $modes) {
            $tag = "$($g.key)_$mode"
            $rd = Join-Path $WorkDir $tag
            $cfgDir = Join-Path $rd 'config'
            New-Item -ItemType Directory -Force -Path $cfgDir, (Join-Path $rd 'saves'), (Join-Path $rd 'bugs') | Out-Null
            Set-Content -LiteralPath (Join-Path $cfgDir 'config.json') -Value ('{ "emulationVolume": ' + $Volume + ', "emulationMuted": false }') -Encoding UTF8
            $rom = Join-Path $rd ('game' + $g.ext); Copy-Item -LiteralPath $g.rom -Destination $rom   # the user's ROM is only read
            $env:BROKENNES_CONFIG_DIR = $cfgDir; $env:BROKENNES_SAVES_DIR = (Join-Path $rd 'saves'); $env:BROKENNES_BUGS_DIR = (Join-Path $rd 'bugs')
            $env:BROKENNES_TEST_AUDIO_DEVICE_FILE = ''; $env:BROKENNES_AUDIO_OUTPUT = ''
            $env:BROKENNES_AUDIO_LEGACY = $(if ($mode -eq 'legacy') { '1' } else { '' })
            $submitLog = Join-Path $rd 'submit.log'; $env:BROKENNES_AUDIO_SUBMIT_LOG = $submitLog
            $submitWav = Join-Path $rd 'submitted.wav'; $env:BROKENNES_AUDIO_SUBMIT_WAV = $submitWav   # what the emulation handed over (fixed path only)

            $inst = Start-BrokenNesInstance -ExePath $Exe
            $id = $inst.ProcessId
            Show-BrokenNesEmulator -ProcessId $id | Out-Null
            Start-Sleep -Milliseconds 800
            $l = Load-BrokenNesRom -ProcessId $id -Path $rom
            Start-Sleep -Seconds $WarmupSeconds

            $capWav = Join-Path $rd 'capture.wav'
            # our process only (the user's own windows may be playing on the same device); the whole device if that is not available
            $capKind = 'process'
            try { $rec = [ProcessLoopbackRecorder]::new($id, $capWav) } catch { $capKind = 'device'; Write-Host "[WARN] process loopback: $($_.Exception.Message); recording the whole device"; $rec = [LoopbackRecorder]::new($capWav) }
            $d0 = Device $id; $sw = [Diagnostics.Stopwatch]::StartNew(); $rec.Start()
            $q = New-Object System.Collections.Generic.List[double]
            while ($sw.Elapsed.TotalSeconds -lt $Seconds) { $d = Device $id; if ($d) { $q.Add([double]$d.bufferedMs) }; Start-Sleep -Milliseconds 50 }
            $d1 = Device $id; $dt = $sw.Elapsed.TotalSeconds
            $rec.Stop(); $rec.Dispose()
            $st = (Invoke-BrokenNesApi -ProcessId $id -Method GET -Path '/api/console').status
            Stop-BrokenNesInstance -ProcessId $id | Out-Null; $inst = $null

            $fps = ([double]$d1.framesEmulated - [double]$d0.framesEmulated) / $dt
            $paced = [double]$d1.pacedFps
            $under = [long]$d1.stats.underruns - [long]$d0.stats.underruns
            $rate = ([double]$d1.stats.bytesRead - [double]$d0.stats.bytesRead) / 2 / $dt
            $outRate = [double]$d1.stats.sampleRate
            $qs = $q | Measure-Object -Minimum -Maximum -Average
            # the submitted stream, inside the window: the longest wait between two submissions
            $gapMax = 0.0; $prev = $null; $t0ms = [double]$d0.stats.seconds * 1000; $t1ms = [double]$d1.stats.seconds * 1000
            if (Test-Path -LiteralPath $submitLog) {
                foreach ($line in [IO.File]::ReadLines($submitLog)) {
                    $p = $line.Split(' '); $t = [double]::Parse($p[0], $inv)
                    if ($t -lt $t0ms -or $t -gt $t1ms) { continue }
                    if ($null -ne $prev -and $t - $prev -gt $gapMax) { $gapMax = $t - $prev }; $prev = $t
                }
            }
            $cap = [GapAnalyzer]::Analyze($capWav, 0, 0)
            $ref = [GapAnalyzer]::Analyze($refWav, $Volume / 100.0, $WarmupSeconds + 2)
            $sub = $null; if (Test-Path -LiteralPath $submitWav) { $sub = [GapAnalyzer]::Analyze($submitWav, $Volume / 100.0, $WarmupSeconds + 2) }
            $run = [ordered]@{
                game = "$($st.console) $($st.game)"; load = $l.success; output = $d1.stats.output; pacing = $d1.stats.pacing
                fps = [math]::Round($fps, 2); pacedFps = $paced; underruns = $under; readRateHz = [math]::Round($rate, 0); outputRate = $outRate
                queueMs = "min $($qs.Minimum) avg $([math]::Round($qs.Average, 1)) max $($qs.Maximum)"; queueMaxMs = $qs.Maximum
                submitGapMaxMs = [math]::Round($gapMax, 1); primes = [long]$d1.stats.primes - [long]$d0.stats.primes; trims = [long]$d1.stats.overflows - [long]$d0.stats.overflows
                captureKind = $capKind; capture = "$cap"; submitted = "$sub"; reference = "$ref"; captureDropouts = $cap.Dropouts; referenceDropouts = $ref.Dropouts
            }
            $result.runs[$tag] = $run
            $line = "{0}: {1} fps (paced {2}), {3} underruns, device took {4} samples/s of {5}, queue {6} ms, longest submit gap {7} ms, output {8}/{9}" -f $tag, $run.fps, $paced, $under, $run.readRateHz, $outRate, $run.queueMs, $run.submitGapMaxMs, $run.output, $run.pacing
            if ($mode -eq 'fixed') {
                Check "${tag}_loaded" ([bool]$l.success -and $st.game) "load $($l.success), console $($st.console), game '$($st.game)'"
                Check "${tag}_fps" ([math]::Abs($fps - $paced) / $paced -lt 0.01) $line
                Check "${tag}_no_underruns" ($under -eq 0) "$under underruns in $([math]::Round($dt, 1)) s of steady state (primes $($run.primes), trims $($run.trims))"
                Check "${tag}_device_rate" ([math]::Abs($rate - $outRate) / $outRate -lt 0.015) ("the device took {0:F0} samples/s at {1} Hz ({2:F1} %)" -f $rate, $outRate, (100 * $rate / $outRate))
                Check "${tag}_latency_bounded" ($qs.Maximum -le 150) "queue $($run.queueMs) ms (+ the 30 ms WASAPI buffer)"
                $capOk = $cap.Dropouts -le 1.5 * $ref.Dropouts + 5
                if ($capKind -eq 'process' -or $quiet) { Check "${tag}_capture_vs_reference" $capOk "$capKind loopback: $cap | Workshop render: $ref" }
                else { Info "${tag}_capture_vs_reference" "unjudged (the device was not quiet): speakers: $cap | Workshop render: $ref" }
            } else {
                $drop = $fps -lt 0.9 * $paced
                Check "${tag}_negative_control_reproduces_drop" $drop ("old output + pacing: $line | $capKind loopback: $cap | Workshop render: $ref")
            }
        }
    }

    Start-Sleep -Seconds 1
    $after = Listing $userDir
    Check 'user_config_untouched' ($before -eq $after) "$userDir : $(@($before -split "`n").Count) files, $(if ($before -eq $after) { 'the same names, sizes and times' } else { 'CHANGED' })"
    $result.pass = -not ($checks.Values | Where-Object { -not $_.ok })
}
catch {
    Check 'run' $false ($_.Exception.Message + ' ' + $_.ScriptStackTrace)
}
finally {
    if ($inst) { Stop-BrokenNesInstance -ProcessId $inst.ProcessId | Out-Null }
    foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
}
$result.checks = $checks
$result | ConvertTo-Json -Compress -Depth 5
if ($result.pass) { exit 0 } else { exit 1 }
