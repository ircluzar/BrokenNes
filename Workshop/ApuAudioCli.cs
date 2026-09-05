using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NesEmulator;

namespace BrokenNes.Workshop;

/// <summary>
/// <c>--apu-audio</c>: capture what BrokenNes' APU actually SOUNDS like, and the waveform phase
/// behind it, for a deterministic replay.
///
/// WHY THIS EXISTS SEPARATELY FROM --trace
/// --trace already emits 24 APU columns and four audio fingerprint columns. Both stop short of the
/// thing a player actually hears:
///   * its APU columns are the deliberately lag-immune set (periods, resolved volumes, length
///     counters, sweep bits, DMC pointer, frame step). Every field that carries the WAVEFORM -
///     duty position, triangle sequence position, the noise LFSR, the timer dividers, the per-cycle
///     channel output - is excluded by design, because Mesen reports those lazily.
///   * its audio columns are four scalars per frame (sample count, RMS, peak, zero crossings).
///     Those tell you a frame was loud. They cannot tell you it was the RIGHT note, played with the
///     right timbre, at the right instant.
///
/// This command emits both of the missing halves:
///   1. a per-frame PHASE trace whose columns match tools/apu_phase_trace.lua digit for digit, so
///      the two emulators can be diffed on the waveform state itself; and
///   2. the real PCM the mixer produced, as a 32-bit-float mono WAV, losslessly drained from the
///      APU's ring every frame. Nothing in this repo could previously get audio out of a headless
///      run at all, so there was no way to LISTEN to a regression, let alone measure one.
///
/// WHAT THE PHASE TRACE CAN AND CANNOT PROVE - read before quoting a number from it.
/// Mesen's copies of these fields lag its CPU by a bounded but variable amount (measured elsewhere
/// in this project: mean ~1582, max ~2505 CPU cycles at a frame boundary). So:
///   * a DIFFERENCE of a few duty steps, or a timer counter that is off by a few hundred, is
///     exactly what that lag predicts and proves nothing either way;
///   * a field that AGREES is strong evidence, since a lagged sample agreeing by chance is
///     unlikely to keep happening;
///   * a field that disagrees WITHOUT BOUND - an LFSR that never resynchronises, an output level
///     stuck at 0 for thousands of frames while the reference keeps moving - cannot be explained by
///     any lag, and is a real defect.
/// The differ (UAT/apu-phase-diff.ps1) classifies on exactly that basis; this file only produces
/// the measurement.
///
/// WAV FORMAT: IEEE float32, mono, at the APU's own sample rate (44100 for APU_FIX). Float, not
/// 16-bit PCM, on purpose - the point is to compare mixer output, and quantising it first would
/// throw away the low-level detail (DC offset, filter tails) that a mixing defect shows up in.
///
/// USAGE
///   --apu-audio --rom &lt;path.nes&gt; [--cpu ID] [--ppu ID] [--apu ID] [--frames N]
///               [--input "60:Start,66:,120:Right+A"] [--ntsc-frame-timing on|off]
///               [--wav &lt;out.wav&gt;] [--phase &lt;out.txt&gt;] [--per-frame-audio &lt;out.txt&gt;]
/// </summary>
internal static class ApuAudioCli
{
    private const string Usage =
        "Usage: --apu-audio --rom <path.nes> [--cpu ID] [--ppu ID] [--apu ID] [--frames N]\n" +
        "                   [--input \"60:Start,66:\"] [--ntsc-frame-timing on|off]\n" +
        "                   [--wav <out.wav>] [--phase <out.txt>] [--per-frame-audio <out.txt>]\n" +
        "\n" +
        "  --wav              32-bit float mono WAV of the mixer's real output. Omit to skip.\n" +
        "  --phase            per-frame waveform-phase trace, column-compatible with the Mesen\n" +
        "                     side (VRUN tools/apu_phase_trace.lua). Omit to skip.\n" +
        "  --per-frame-audio  per-frame audio statistics (sample count, RMS, peak, min, max,\n" +
        "                     zero crossings) as text, for a quick census without the WAV.\n" +
        "  At least one of --wav / --phase / --per-frame-audio must be given.\n";

    public static int Run(string[] args)
    {
        string? romPath = null, cpu = null, ppu = null, apu = null;
        string? wavPath = null, phasePath = null, perFramePath = null, inputScript = null, writeLogPath = null;
        int frames = 1200;
        bool ntscFrameTiming = true;

        try
        {
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--rom": romPath = args[++i]; break;
                    case "--cpu": cpu = args[++i]; break;
                    case "--ppu": ppu = args[++i]; break;
                    case "--apu": apu = args[++i]; break;
                    case "--frames": frames = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--input": inputScript = args[++i]; break;
                    case "--wav": wavPath = args[++i]; break;
                    case "--phase": phasePath = args[++i]; break;
                    case "--per-frame-audio": perFramePath = args[++i]; break;
                    case "--write-log": writeLogPath = args[++i]; break;
                    case "--ntsc-frame-timing": ntscFrameTiming = args[++i].ToLowerInvariant() is "on" or "true" or "1"; break;
                    case "--help": case "-h": Console.WriteLine(Usage); return 0;
                    default:
                        Console.Error.WriteLine($"Unknown argument '{args[i]}'.\n{Usage}");
                        return 2;
                }
            }
        }
        catch (IndexOutOfRangeException)
        {
            Console.Error.WriteLine($"Missing value for the last argument.\n{Usage}");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(romPath)) { Console.Error.WriteLine($"--rom is required.\n{Usage}"); return 2; }
        if (wavPath == null && phasePath == null && perFramePath == null && writeLogPath == null)
        {
            Console.Error.WriteLine($"Nothing to do: give at least one of --wav / --phase / --per-frame-audio / --write-log.\n{Usage}");
            return 2;
        }
        if (frames <= 0) { Console.Error.WriteLine("--frames must be positive."); return 2; }

        string romFullPath = Path.GetFullPath(romPath);
        if (!File.Exists(romFullPath)) { Console.Error.WriteLine($"ROM not found: {romFullPath}"); return 2; }
        byte[] romBytes = File.ReadAllBytes(romFullPath);

        List<RomTestCli.InputStep> script;
        try { script = RomTestCli.ParseInputScript(inputScript); }
        catch (Exception ex) { Console.Error.WriteLine($"--input: {ex.Message}"); return 2; }

        var nes = new NES { RomName = Path.GetFileName(romFullPath), RomPath = romFullPath };
        nes.LoadROM(romBytes);

        var cpuApply = RomTestCli.ApplyCore("CPU", cpu, nes.SetCpuCore, nes.GetCpuCoreId);
        var ppuApply = RomTestCli.ApplyCore("PPU", ppu, nes.SetPpuCore, nes.GetPpuCoreId);
        var apuApply = RomTestCli.ApplyCore("APU", apu, nes.SetApuCore, nes.GetApuCoreId);
        if (!cpuApply.Applied || !ppuApply.Applied || !apuApply.Applied)
        {
            foreach (var c in new[] { cpuApply, ppuApply, apuApply })
                if (!c.Applied)
                    Console.Error.WriteLine(
                        $"{c.Kind} core '{c.Requested}' not applied (accepted={c.Accepted}, effective='{c.Effective}').");
            return 2;
        }

        // Same pacing decision --trace makes and for the same reason: BrokenNes' default frame
        // budget is CpuFrequency/60 = 29829.55 cycles, but real NTSC is 29780.5. Leaving the ~49
        // cycle/frame surplus in would drift this capture away from Mesen's within a few frames and
        // every later comparison would be measuring the drift instead of the audio.
        var speed = nes.GetSpeedConfig();
        if (speed != null) speed.NtscAccurateFrameRate = ntscFrameTiming;

        // The phase probe lives on APU_FIX because APU_FIX is the accuracy target; every other core
        // is frozen and none of them implements it. Say so rather than silently emitting nothing.
        APU_FIX? fix = nes.GetApuStateProbe() as APU_FIX;
        if (phasePath != null && fix == null)
        {
            Console.Error.WriteLine(
                $"--phase needs APU_FIX (active core is {apuApply.Effective}); no other core exposes waveform phase.");
            return 2;
        }

        // Audio has to be drained EVERY frame whether or not a WAV was asked for. The APU's ring is
        // 32768 samples and a frame produces ~734, so leaving it undrained would wrap after ~45
        // frames and silently corrupt any statistic taken afterwards.
        var pcm = new List<float>(frames * 800);
        int sampleRate = fix?.GetSampleRate() ?? 44100;

        StreamWriter? phaseW = null, perFrameW = null, writeLogW = null;
        int curFrame = 0;
        try
        {
            if (writeLogPath != null)
            {
                if (fix == null)
                {
                    Console.Error.WriteLine(
                        $"--write-log needs APU_FIX (active core is {apuApply.Effective}).");
                    return 2;
                }
                writeLogW = new StreamWriter(writeLogPath, false, new UTF8Encoding(false)) { NewLine = "\n" };
                writeLogW.WriteLine("# ApuAudioCli --write-log v1.0 - BROKENNES side");
                writeLogW.WriteLine($"# rom: {Path.GetFileName(romFullPath)}  input: {inputScript ?? string.Empty}");
                writeLogW.WriteLine("# columns: frame|addr|value   (hex addr, hex value, one line per APU register write)");
                var wl = writeLogW;
                fix.RegisterWriteObserver = (addr, val) =>
                    wl.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{curFrame}|{addr:x4}|{val:x2}"));
            }

            if (phasePath != null)
            {
                phaseW = new StreamWriter(phasePath, false, new UTF8Encoding(false)) { NewLine = "\n" };
                phaseW.WriteLine("# ApuAudioCli --phase v1.0 - BROKENNES side");
                phaseW.WriteLine($"# rom: {Path.GetFileName(romFullPath)}");
                phaseW.WriteLine($"# cores: {cpuApply.Effective} / {ppuApply.Effective} / {apuApply.Effective}");
                phaseW.WriteLine($"# frames: {frames}  ntsc-frame-timing: {(ntscFrameTiming ? "on" : "off")}");
                phaseW.WriteLine($"# input: {inputScript ?? string.Empty}");
                phaseW.WriteLine("# NOTE these fields are LAG-SENSITIVE on the Mesen side; see ApuAudioCli's header");
                phaseW.WriteLine("# for what a difference in each column may and may not be taken to mean.");
                phaseW.WriteLine("# columns: frame|cpuCycle|apuPrevCycle|p1duty|p1dutyPos|p1timer|p1period|p1out|" +
                                 "p2duty|p2dutyPos|p2timer|p2period|p2out|tseq|ttimer|tperiod|tout|" +
                                 "nshift|ntimer|nperiod|nout|nmode|dout|dshift|dbits|dsilence|dtimer|fcstep|fcprev");
            }
            if (perFramePath != null)
            {
                perFrameW = new StreamWriter(perFramePath, false, new UTF8Encoding(false)) { NewLine = "\n" };
                perFrameW.WriteLine($"# ApuAudioCli --per-frame-audio  sampleRate={sampleRate}");
                perFrameW.WriteLine("# columns: frame|samples|rms|peak|min|max|zeroCrossings");
            }

            var held = new bool[8];
            int nextStep = 0;
            var line = new StringBuilder(256);
            float lastSample = 0f;

            for (int f = 0; f < frames; f++)
            {
                curFrame = f;
                // while, not if: several steps can name the same frame, and the last one wins - the
                // same rule RomTestCli and TraceCli use, so an input script means one thing here.
                while (nextStep < script.Count && script[nextStep].Frame <= f)
                {
                    held = (bool[])script[nextStep].Held.Clone();
                    nextStep++;
                }
                nes.SetInputs(held, null);

                nes.RunFrame();

                // Drain losslessly: ask for exactly what is queued, rather than NES.GetAudioBuffer(),
                // which caps at 2048 and deliberately DROPS the oldest samples once the backlog
                // passes 6144. Those policies are right for a live audio device that must not fall
                // behind, and wrong for a capture that must not lose a sample.
                int queued = fix?.GetQueuedSampleCount() ?? 0;
                float[] chunk = fix != null && queued > 0 ? fix.GetAudioSamples(queued) : Array.Empty<float>();
                if (fix == null) chunk = nes.GetAudioBuffer();
                if (wavPath != null) pcm.AddRange(chunk);

                if (perFrameW != null)
                {
                    double sumSq = 0; float peak = 0, mn = 0, mx = 0;
                    int zc = 0; float prev = lastSample;
                    for (int i = 0; i < chunk.Length; i++)
                    {
                        float s = chunk[i];
                        sumSq += (double)s * s;
                        float a = Math.Abs(s);
                        if (a > peak) peak = a;
                        if (s < mn) mn = s;
                        if (s > mx) mx = s;
                        // Seeded from the previous frame's last sample so a crossing straddling the
                        // boundary is counted once overall, not zero times.
                        if ((prev < 0f && s >= 0f) || (prev >= 0f && s < 0f)) zc++;
                        prev = s;
                    }
                    if (chunk.Length > 0) lastSample = chunk[^1];
                    double rms = chunk.Length > 0 ? Math.Sqrt(sumSq / chunk.Length) : 0.0;
                    perFrameW.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"{f}|{chunk.Length}|{rms:F6}|{peak:F6}|{mn:F6}|{mx:F6}|{zc}"));
                }

                if (phaseW != null && fix != null)
                {
                    var ph = fix.ProbeApuPhase();
                    line.Clear();
                    // cpuCycle / apuPrevCycle are emitted as '-' rather than faked: BrokenNes has no
                    // single free-running CPU cycle counter exposed here, and its APU is never
                    // behind its CPU (it is stepped every cycle), so there is no lag to report. A
                    // dash keeps the columns aligned with the Mesen side without inventing a number.
                    line.Append(f).Append("|-|-|")
                        .Append(ph.Pulse1Duty).Append('|')
                        .Append(ph.Pulse1SeqIndex).Append('|')
                        .Append(ph.Pulse1TimerCounter).Append('|')
                        .Append(ph.Pulse1Period).Append('|')
                        .Append(ph.Pulse1Output).Append('|')
                        .Append(ph.Pulse2Duty).Append('|')
                        .Append(ph.Pulse2SeqIndex).Append('|')
                        .Append(ph.Pulse2TimerCounter).Append('|')
                        .Append(ph.Pulse2Period).Append('|')
                        .Append(ph.Pulse2Output).Append('|')
                        .Append(ph.TriangleSeqIndex).Append('|')
                        .Append(ph.TriangleTimerCounter).Append('|')
                        .Append(ph.TrianglePeriod).Append('|')
                        .Append(ph.TriangleOutput).Append('|')
                        .Append(ph.NoiseShift).Append('|')
                        .Append(ph.NoiseTimerCounter).Append('|')
                        .Append(ph.NoisePeriod).Append('|')
                        .Append(ph.NoiseOutput).Append('|')
                        .Append(ph.NoiseMode ? 1 : 0).Append('|')
                        .Append(ph.DmcOutput).Append('|')
                        .Append(ph.DmcShiftReg).Append('|')
                        .Append(ph.DmcBitsRemaining).Append('|')
                        .Append(ph.DmcSilence ? 1 : 0).Append('|')
                        .Append(ph.DmcTimer).Append('|')
                        .Append(ph.FrameStep).Append("|-");
                    phaseW.WriteLine(line.ToString());
                }
            }
        }
        finally
        {
            // Detach before disposing: the observer closes over the writer, and a stray write
            // during teardown would throw on a disposed stream.
            if (fix != null) fix.RegisterWriteObserver = null;
            phaseW?.Dispose();
            perFrameW?.Dispose();
            writeLogW?.Dispose();
        }

        if (wavPath != null)
        {
            WriteFloatWav(wavPath, pcm, sampleRate);
            Console.WriteLine($"WAV    : {wavPath}  ({pcm.Count} samples, {pcm.Count / (double)sampleRate:F2}s @ {sampleRate}Hz)");
        }
        if (phasePath != null) Console.WriteLine($"Phase  : {phasePath}");
        if (perFramePath != null) Console.WriteLine($"Audio  : {perFramePath}");
        Console.WriteLine($"ROM    : {Path.GetFileName(romFullPath)}");
        Console.WriteLine($"Cores  : {cpuApply.Effective} / {ppuApply.Effective} / {apuApply.Effective}");
        Console.WriteLine($"Frames : {frames}");
        return 0;
    }

    /// <summary>
    /// Canonical 32-bit-float mono WAV (WAVE_FORMAT_IEEE_FLOAT, tag 3). Written by hand rather than
    /// pulled from a library because Workshop has no audio dependency and should not grow one for
    /// 40 lines of header.
    /// </summary>
    private static void WriteFloatWav(string path, List<float> samples, int sampleRate)
    {
        const int channels = 1, bitsPerSample = 32;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        short blockAlign = (short)(channels * bitsPerSample / 8);
        int dataBytes = samples.Count * 4;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        w.Write(new[] { 'R', 'I', 'F', 'F' });
        w.Write(36 + dataBytes);                 // RIFF chunk size
        w.Write(new[] { 'W', 'A', 'V', 'E' });
        w.Write(new[] { 'f', 'm', 't', ' ' });
        w.Write(16);                             // PCM/float fmt chunk size
        w.Write((short)3);                       // WAVE_FORMAT_IEEE_FLOAT
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write(blockAlign);
        w.Write((short)bitsPerSample);
        w.Write(new[] { 'd', 'a', 't', 'a' });
        w.Write(dataBytes);
        foreach (float s in samples) w.Write(s);
    }
}
