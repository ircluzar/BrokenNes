using System;
using System.Collections.Generic;
using System.Linq;
using NesEmulator;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// The sound half of <c>--swap-matrix-test</c> for the three Sega consoles: every entry of the APU menu, swapped into a running game that plays one steady tone (or, where the swap restarts the game, a fresh session on the
/// entry, as the app builds it), must deliver <b>the sound the game is playing, at the session's rate</b>:
/// <list type="bullet">
/// <item>the session reports the same sample rate as the console's own unit, and delivers the same number of samples for the same frames (a NES chip that runs at 32 or 48 kHz and hands those over unconverted gives 72 or 109 percent of the samples, which the apps play at the wrong speed
/// or pitch when they trust the rate - and which any host that fixed its audio device on the first rate it saw would play wrongly after a swap);</item>
/// <item>the tone it plays is the game's pitch (the lowest strong component of the spectrum, within 2 percent: a unit that plays it an octave away is wrong whatever its level), and audible.</item>
/// </list>
/// The pitch is read from the spectrum, not from zero crossings: a unit whose waveform crosses zero twice a cycle (an FM voice with a strong second harmonic) must not read as an octave up.
/// </summary>
internal static partial class SwapMatrixTestCli
{
    private const double ToneHz = 3579545.0 / (32 * 254);       // the PSG's period $0FE on the NTSC clock: 440.4 Hz
    private const int SoundWarm = 90, SoundAfter = 90;

    /// <summary>Chips that play the bridged tone at another pitch on a fresh session too (measured without any swap): NES:QLQ plays the PSG tone an octave up (the QuickNES-style pulse). Named, so the check fails if the chip is ever corrected.</summary>
    private static readonly Dictionary<string, double> PitchFactor = new(StringComparer.OrdinalIgnoreCase) { ["NES:QLQ"] = 2.0 };

    /// <summary>A Genesis program that plays one PSG tone (tone 1, period $0FE, loudest) and loops: three byte writes to the PSG port from the 68000.</summary>
    internal static byte[] GenesisTone() => GenesisRom("13fc008e00c00011" + "13fc000f00c00011" + "13fc009000c00011" + "60fe");

    private static byte[] GenesisRom(string codeHex)
    {
        var md = new byte[0x20000];
        md[1] = 0xFF; md[2] = 0xFE; md[3] = 0x00; md[6] = 0x02; md[7] = 0x00;
        System.Text.Encoding.ASCII.GetBytes("SEGA MEGA DRIVE ").CopyTo(md, 0x100);
        System.Text.Encoding.ASCII.GetBytes("GM 00000000-00").CopyTo(md, 0x180);
        for (int i = 0; i < 16; i++) md[0x1F0 + i] = 0x20; md[0x1F0] = (byte)'U';
        for (int i = 0; i < codeHex.Length / 2; i++) md[0x200 + i] = Convert.ToByte(codeHex.Substring(i * 2, 2), 16);
        return md;
    }

    private static (float[] Audio, int Rate) PlayTone(IConsoleSession s, int frames)
    {
        var mono = new List<float>(frames * 800); var buf = new short[16384];
        for (int f = 0; f < frames; f++)
        {
            s.RunFrame();
            int n; while ((n = s.ReadSamples(buf)) > 0) for (int i = 0; i + 1 < n; i += 2) mono.Add((buf[i] + buf[i + 1]) * (0.5f / 32768f));
        }
        return (mono.ToArray(), s.SampleRate);
    }

    /// <summary>The pitch of a steady tone: the lowest strong spectral component (at least a third of the strongest, a quarter-semitone grid from 60 Hz to 3 kHz, refined to a fraction of a cent), and the peak amplitude of the tone's samples.</summary>
    internal static (double Hz, double Peak) Fundamental(float[] x, int rate)
    {
        if (x.Length < rate / 4) return (0, 0);
        int from = Math.Min(x.Length / 4, rate / 2), to = Math.Min(x.Length, from + rate / 2);      // half a second, after the start
        double mean = 0; for (int i = from; i < to; i++) mean += x[i]; mean /= to - from;
        double peak = 0; for (int i = from; i < to; i++) peak = Math.Max(peak, Math.Abs(x[i] - mean));
        if (peak < 0.01) return (0, peak);
        var freqs = new List<double>(); var amps = new List<double>();
        for (double f = 60; f < 3000; f *= Math.Pow(2, 1.0 / 48)) { freqs.Add(f); amps.Add(SegaMeasure.Component(x, rate, f, from, to)); }
        double max = amps.Max();
        for (int i = 1; i < freqs.Count - 1; i++)
            if (amps[i] >= max / 3 && amps[i] >= amps[i - 1] && amps[i] >= amps[i + 1])
                return (SegaMeasure.Peak(x, rate, freqs[i], from, to, 0.02).Hz, peak);
        return (0, peak);
    }

    // ---------------------------------------------------------------------------------------------------- the converter the hub units use

    private static float[] Sine(double hz, int rate, int count, double amp = 0.5) { var x = new float[count]; for (int i = 0; i < count; i++) x[i] = (float)(amp * Math.Sin(2 * Math.PI * hz * i / rate)); return x; }

    private static float[] Resample(float[] input, int from, int to, int block = 0)
    {
        var r = new NesEmulator.Sega.StreamResampler(from, to); var outp = new List<float>(); var tmp = new float[8192];
        var rng = new Random(5);
        for (int i = 0; i < input.Length;)
        {
            int n = block > 0 ? block : rng.Next(1, 4000); n = Math.Min(n, input.Length - i);
            r.Push(input.AsSpan(i, n)); i += n;
            int got; while ((got = r.Pull(tmp)) > 0) for (int k = 0; k < got; k++) outp.Add(tmp[k]);
        }
        return outp.ToArray();
    }

    /// <summary>The converter that brings a NES chip's own rate (the SNES's 32 kHz, the Game Boy's 48 kHz) to the machine's 44.1 kHz: the right amount of sound, the right pitch and level, nothing folded back, the same answer however the input arrives.</summary>
    private static void ResamplerChecks()
    {
        foreach (var (from, to) in new[] { (32000, 44100), (48000, 44100), (44100, 48000) })
        {
            var x = Sine(1000, from, from * 2);
            var y = Resample(x, from, to);
            double wantLen = 2.0 * to;
            var (hz, _) = (SegaMeasure.Peak(y, to, 1000, to / 4, to / 4 + to / 2, 0.02).Hz, 0);
            double amp = SegaMeasure.Component(y, to, hz, to / 4, to / 4 + to / 2);
            Check($"resampler {from} -> {to} Hz: two seconds of a 1 kHz tone give {y.Length} samples (want {wantLen:F0} within 2), the tone is {hz:F2} Hz at {amp:F3} (want 1000 Hz, 0.500)", Math.Abs(y.Length - wantLen) <= 2 && Math.Abs(hz - 1000) < 0.5 && Math.Abs(amp / 0.5 - 1) < 0.01);
        }
        {
            // a tone near the source's top passes and its image (the source rate minus the tone: 32 - 14 = 18 kHz) is removed
            var up = Resample(Sine(14000, 32000, 64000), 32000, 44100);
            double a = SegaMeasure.Component(up, 44100, 14000, 22050, 66150), img = SegaMeasure.Component(up, 44100, 18000, 22050, 66150);
            Check($"resampler 32000 -> 44100 Hz: a 14 kHz tone passes at {SegaMeasure.Db(a, 0.5):F2} dB (want within 0.5 dB), its image at 18 kHz is {SegaMeasure.Db(img, 0.5):F1} dB (want below -60)", Math.Abs(SegaMeasure.Db(a, 0.5)) < 0.5 && SegaMeasure.Db(img, 0.5) < -60);
        }
        {
            // down to 44.1 kHz from 48 kHz: a 20 kHz tone passes, a 23 kHz one is above the target's Nyquist rate and must not fold back to 21.1 kHz
            var y = Resample(Sine(23000, 48000, 96000), 48000, 44100); var z = Resample(Sine(20000, 48000, 96000), 48000, 44100);
            double fold = SegaMeasure.Component(y, 44100, 44100 - 23000, 22050, 66150), keep = SegaMeasure.Component(z, 44100, 20000, 22050, 66150);
            Check($"resampler 48000 -> 44100 Hz: a 20 kHz tone passes at {SegaMeasure.Db(keep, 0.5):F2} dB (want within 1.5 dB), a 23 kHz tone (above 44.1 kHz's Nyquist rate) folds back to 21.1 kHz at {SegaMeasure.Db(fold, 0.5):F1} dB (want below -50)", Math.Abs(SegaMeasure.Db(keep, 0.5)) < 1.5 && SegaMeasure.Db(fold, 0.5) < -50);
        }
        {
            var x = Sine(1000, 32000, 32000);
            var a = Resample(x, 32000, 44100, 1); var b = Resample(x, 32000, 44100, 2048); var c = Resample(x, 32000, 44100);
            int n = Math.Min(a.Length, Math.Min(b.Length, c.Length));
            bool same = a.Length == b.Length && a.Length == c.Length; for (int i = 0; i < n && same; i++) same = a[i] == b[i] && a[i] == c[i];
            Check($"resampler: the output does not depend on how the input is cut into blocks (one sample at a time, 2048 at a time, random blocks: {a.Length} / {b.Length} / {c.Length} samples)", same);
        }
        {
            // it runs inside the audio path of a game: nothing may be allocated once it is warm
            var r = new NesEmulator.Sega.StreamResampler(32000, 44100); var tmp = new float[4096]; var block = Sine(1000, 32000, 533);
            for (int i = 0; i < 300; i++) { r.Push(block); while (r.Pull(tmp) > 0) { } }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 3000; i++) { r.Push(block); while (r.Pull(tmp) > 0) { } }
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Check($"resampler: 3000 blocks (50 seconds of sound) allocate {bytes} bytes (limit 0)", bytes == 0);
        }
        {
            // the stream has no latency in samples: the output of N input samples is N x rate ratio samples at every point, not N x ratio less a kernel (a unit started in the middle of a game must hand out as many samples as time has passed)
            var r = new NesEmulator.Sega.StreamResampler(32000, 44100); var tmp = new float[4096]; var one = new float[32];
            int total = 0; for (int i = 0; i < 100; i++) { r.Push(one); int got; while ((got = r.Pull(tmp)) > 0) total += got; }
            Check($"resampler: 3200 samples at 32 kHz have given {total} at 44.1 kHz (want 4410 within 1)", Math.Abs(total - 4410) <= 1);
        }
    }

    /// <summary>For one Sega console: the sound slot of the menu against the console's own unit, the same tone program on each (see the type's summary).</summary>
    private static void SoundRateAndPitch(ConsoleKind console)
    {
        bool gg = console == ConsoleKind.GameGear;
        string label = Consoles.DisplayName(console);
        byte[] rom = console == ConsoleKind.Genesis ? GenesisTone() : SwapSuite.ToneScroller(gg);
        string file = console == ConsoleKind.Genesis ? "tone.md" : gg ? "tone.gg" : "tone.sms";
        string def(CoreSlot slot) => CoreCatalog.Default(console, slot);
        IConsoleSession Make(string apu) => ConsoleSessions.Create(console, rom, def(CoreSlot.Cpu), def(CoreSlot.Ppu), apu, null, file);

        using var control = Make(def(CoreSlot.Apu));
        PlayTone(control, SoundWarm);
        var (ctl, ctlRate) = PlayTone(control, SoundAfter);
        var (ctlHz, ctlPeak) = Fundamental(ctl, ctlRate);
        Check($"{label}: the console's own unit plays the tone at {ctlRate} Hz ({ctl.Length} samples in {SoundAfter} frames, {ctlHz:F1} Hz, wanted {ToneHz:F1})", Math.Abs(ctlHz / ToneHz - 1) < 0.01 && ctlPeak > 0.05);

        var bad = new List<string>(); int measured = 0, restarted = 0;
        foreach (var opt in CoreCatalog.Options(console, CoreSlot.Apu))
        {
            try
            {
                var s = Make(def(CoreSlot.Apu));
                PlayTone(s, SoundWarm);
                bool swapped = s.TrySwapCore(CoreSlot.Apu, opt.Id);
                IConsoleSession? fresh = null;
                if (!swapped) { s.Dispose(); restarted++; fresh = s = Make(opt.Id); PlayTone(s, SoundWarm); }     // the app restarts the game on the entry: the same frames, from a fresh session
                var (audio, rate) = PlayTone(s, SoundAfter);
                string id = opt.Id;
                if (audio.Length == 0) { if (id.Equals("NES:WF", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine($"  (NES:WF sent its notes to the system MIDI synth and made no samples to measure)"); fresh?.Dispose(); s.Dispose(); continue; } }
                measured++;
                var (hz, peak) = Fundamental(audio, rate);
                double wantHz = ToneHz * PitchFactor.GetValueOrDefault(id, 1.0);
                var problems = new List<string>();
                if (rate != ctlRate) problems.Add($"rate {rate} Hz, the console's {ctlRate}");
                if (Math.Abs(audio.Length - ctl.Length) > 8) problems.Add($"{audio.Length} samples, the console's own unit gives {ctl.Length}");
                if (Math.Abs(hz / wantHz - 1) > 0.02 || peak < 0.015) problems.Add($"tone {hz:F1} Hz at {peak:F3}, wanted {wantHz:F1} Hz audible");
                if (verbose)
                {
                    int a = Math.Min(audio.Length / 4, rate / 2), b = Math.Min(audio.Length, a + rate / 2);
                    double h2 = hz > 0 ? SegaMeasure.Component(audio, rate, 2 * hz, a, b) / Math.Max(1e-9, SegaMeasure.Component(audio, rate, hz, a, b)) : 0;
                    Console.WriteLine($"        {label} APU {id,-10} {(swapped ? "in place" : "restart "),-8} {rate,6} Hz {audio.Length,6} samples {hz,8:F1} Hz peak {peak:F3} 2nd harmonic {h2:F2}x{(problems.Count > 0 ? "   <-- " + string.Join("; ", problems) : "")}");
                }
                if (problems.Count > 0) bad.Add($"{id}: " + string.Join(", ", problems));
                s.Dispose();
            }
            catch (Exception ex) { bad.Add($"{opt.Id}: threw {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
        }
        Check($"{label}: every sound unit of the menu ({measured} measured, {restarted} of them by a restart) plays the game's tone at the console's rate, the same number of samples and the same pitch", bad.Count == 0, bad.Count == 0 ? "" : string.Join("; ", bad.Take(5)) + (bad.Count > 5 ? $" (+{bad.Count - 5} more)" : ""));
    }
}
