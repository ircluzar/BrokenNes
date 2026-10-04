using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator;
using NesEmulator.Mix;
using NesEmulator.Sega;

namespace BrokenNes.Workshop.Sega;

/// <summary>Spectral measurements shared by the patch lab and the bridge verifier: a windowed single-frequency transform, a peak finder that refines to a fraction of a cent, harmonic levels.</summary>
internal static class SegaMeasure
{
    public static double Rms(float[] x, int from, int to)
    {
        if (to <= from) return 0;
        double mean = 0; for (int i = from; i < to; i++) mean += x[i]; mean /= to - from;
        double s = 0; for (int i = from; i < to; i++) s += (x[i] - mean) * (x[i] - mean);
        return Math.Sqrt(s / (to - from));
    }

    /// <summary>Amplitude (peak, in the units of the samples) of the component at <paramref name="hz"/> over a Hann-windowed stretch.</summary>
    public static double Component(float[] x, int rate, double hz, int from, int to)
    {
        int n = to - from;
        double w = 2 * Math.PI * hz / rate, re = 0, im = 0, norm = 0;
        for (int i = 0; i < n; i++)
        {
            double win = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i + 0.5) / n);
            double ph = w * i;
            re += x[from + i] * win * Math.Cos(ph); im -= x[from + i] * win * Math.Sin(ph);
            norm += win;
        }
        return 2 * Math.Sqrt(re * re + im * im) / norm;
    }

    /// <summary>The frequency of the strongest component within <paramref name="spanFraction"/> of <paramref name="centre"/>, refined by a golden-section search of the windowed transform; also its amplitude.</summary>
    public static (double Hz, double Amp) Peak(float[] x, int rate, double centre, int from, int to, double spanFraction = 0.04)
    {
        double lo = centre * (1 - spanFraction), hi = centre * (1 + spanFraction);
        // coarse scan, then refine around the best point
        const int steps = 48;
        double bestHz = centre, best = -1;
        for (int k = 0; k <= steps; k++)
        {
            double f = lo + (hi - lo) * k / steps, a = Component(x, rate, f, from, to);
            if (a > best) { best = a; bestHz = f; }
        }
        double step = (hi - lo) / steps;
        double a0 = Math.Max(lo, bestHz - step), b0 = Math.Min(hi, bestHz + step);
        const double phi = 0.6180339887498949;
        double c = b0 - phi * (b0 - a0), d = a0 + phi * (b0 - a0);
        double fc = Component(x, rate, c, from, to), fd = Component(x, rate, d, from, to);
        for (int it = 0; it < 28; it++)
        {
            if (fc > fd) { b0 = d; d = c; fd = fc; c = b0 - phi * (b0 - a0); fc = Component(x, rate, c, from, to); }
            else { a0 = c; c = d; fc = fd; d = a0 + phi * (b0 - a0); fd = Component(x, rate, d, from, to); }
        }
        double hz = (a0 + b0) / 2;
        return (hz, Component(x, rate, hz, from, to));
    }

    public static double Cents(double hz, double want) => 1200 * Math.Log2(hz / want);
    public static double Db(double a, double b) => 20 * Math.Log10(a / b);

    /// <summary>The lag (in samples) of the first strong peak of the normalised autocorrelation between <paramref name="minLag"/> and <paramref name="maxLag"/>, and the peak's value (1 is a perfect repeat, near 0 is noise).</summary>
    public static (int Lag, double Peak) Periodicity(float[] x, int minLag, int maxLag)
    {
        double mean = 0; foreach (float v in x) mean += v; mean /= x.Length;
        var y = new double[x.Length]; for (int i = 0; i < x.Length; i++) y[i] = x[i] - mean;
        int n = Math.Min(y.Length - maxLag, 8000);
        double e = 0; for (int i = 0; i < n; i++) e += y[i] * y[i];
        if (e <= 0) return (0, 0);
        var r = new double[maxLag + 2];
        double best = 0;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double s = 0; for (int i = 0; i < n; i++) s += y[i] * y[i + lag];
            r[lag] = s / e; best = Math.Max(best, r[lag]);
        }
        for (int lag = minLag; lag <= maxLag; lag++)
            if (r[lag] >= 0.9 * best && (lag == maxLag || r[lag] >= r[lag + 1]) && (lag == minLag || r[lag] >= r[lag - 1])) return (lag, r[lag]);
        return (0, best);
    }

    /// <summary>The loudest frequency in a stretch, scanned on a grid of four steps a semitone between <paramref name="lowHz"/> and <paramref name="highHz"/>; 0 when the stretch is silent.</summary>
    public static double DominantHz(float[] x, int rate, int from, int to, double lowHz = 55, double highHz = 3500, double silence = 1e-4)
    {
        double best = 0, bestHz = 0;
        for (double f = lowHz; f <= highHz; f *= Math.Pow(2, 1 / 48.0))
        {
            double a = Component(x, rate, f, from, to);
            if (a > best) { best = a; bestHz = f; }
        }
        return best < silence ? 0 : bestHz;
    }
}

/// <summary>
/// <c>--md-patches [--patch N] [--note MIDI] [--seconds S] [--wav-dir DIR]</c>: the lab for <see cref="FmPatchBank"/> (APU_MD's built-in YM2612 timbres). For each patch it plays one NES pulse note through the real <see cref="APU_MD"/> and
/// prints what the patch is, measured: the pitch of the fundamental (cents from the NES note), the harmonic series in dB below the strongest, the spectral centroid, the level against the same registers on APU_FIX, and how the loudness moves
/// over the note (the modulator envelopes make a tone start bright and settle). With <c>--wav-dir</c> it writes a short phrase per patch (a scale and a chord, whole APU_MD path, mono 44.1 kHz) so the timbres can be heard.
/// </summary>
internal static class MdPatchCli
{
    private const double NesHz = NesVoiceWriter.NesCpuHz;
    private static string? Arg(string[] a, string n) { int i = Array.FindIndex(a, x => x.Equals(n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

    /// <summary>A NES-facing chip on its own, driven by register writes and stepped by hand.</summary>
    internal sealed class Rig
    {
        public readonly IAPU Apu;
        private readonly float[] buf = new float[8192];
        private readonly List<float> samples = new();
        public int Rate => Apu.GetSampleRate();
        public Rig(string id) { Apu = new NesApuHost(id).Apu; W(0x4017, 0x40); W(0x4015, 0x0F); W(0x4001, 0x08); W(0x4005, 0x08); }
        public void W(ushort a, byte v) => Apu.WriteAPURegister(a, v);
        public void Run(double seconds, bool keep)
        {
            long cycles = (long)(NesHz * seconds), done = 0;
            while (done < cycles)
            {
                int chunk = (int)Math.Min(1024, cycles - done);
                for (int k = 0; k < chunk; k += 32) Apu.Step(Math.Min(32, chunk - k));
                done += chunk;
                int n;
                while ((n = Apu.ReadSamples(buf)) > 0) if (keep) for (int i = 0; i < n; i++) samples.Add(buf[i]);
            }
        }
        public float[] Take() { var a = samples.ToArray(); samples.Clear(); return a; }
        public void Pulse(int ch, int timer, int volume, int duty = 2)
        {
            ushort b = (ushort)(0x4000 + ch * 4);
            W(b, (byte)(duty << 6 | 0x30 | volume)); W((ushort)(b + 2), (byte)timer); W((ushort)(b + 3), (byte)(0xF8 | timer >> 8));
        }
        public void Triangle(int timer) { W(0x4008, 0xFF); W(0x400A, (byte)timer); W(0x400B, (byte)(0xF8 | timer >> 8)); }
        public void Noise(int volume, int index, bool shortMode) { W(0x400C, (byte)(0x30 | volume)); W(0x400E, (byte)((shortMode ? 0x80 : 0) | index)); W(0x400F, 0xF8); }
        public void Silence() { W(0x4000, 0x30); W(0x4004, 0x30); W(0x4008, 0x80); W(0x400C, 0x30); }
    }

    private static int TimerFor(double hz) => (int)Math.Round(NesHz / (16.0 * hz)) - 1;

    public static int Run(string[] args)
    {
        double seconds = double.TryParse(Arg(args, "--seconds"), out double s) ? s : 2.0;
        int midi = int.TryParse(Arg(args, "--note"), out int mn) ? mn : 60;
        int only = int.TryParse(Arg(args, "--patch"), out int op) ? op : -1;
        string? wavDir = Arg(args, "--wav-dir");
        if (args.Any(a => a.Equals("--probe", StringComparison.OrdinalIgnoreCase))) return Probe();
        if (args.Any(a => a.Equals("--calibrate", StringComparison.OrdinalIgnoreCase))) return Calibrate();
        double hz0 = 440.0 * Math.Pow(2, (midi - 69) / 12.0);
        int timer = TimerFor(hz0);
        double nesHz = NesHz / (16.0 * (timer + 1));
        Console.WriteLine($"== APU_MD patches: note {midi} (NES pulse timer {timer} = {nesHz:F2} Hz), {seconds:F1} s, volume 15, duty 50%");
        Console.WriteLine($"   chip clock {APU_MD.YmClockHz:N1} Hz, FM {APU_MD.YmClockHz / 144:N1} samples/s; NES pulse 15 AC RMS {APU_MD.PulseRms(2, 15):F5}");
        if (wavDir != null) Directory.CreateDirectory(wavDir);

        // the NES chip's level at the same note, the reference
        var fixRig = new Rig("FIX"); fixRig.Pulse(0, timer, 15);
        fixRig.Run(0.3, false); fixRig.Run(seconds, true); var fixAudio = fixRig.Take();
        int rate = fixRig.Rate;
        double fixRms = SegaMeasure.Rms(fixAudio, rate / 2, fixAudio.Length);
        Console.WriteLine($"   APU_FIX at the same registers: RMS {fixRms:F5}");
        Console.WriteLine();

        for (int p = 0; p < FmPatchBank.Count; p++)
        {
            if (only >= 0 && p != only) continue;
            var patch = FmPatchBank.Patches[p];
            var rig = new Rig("MD"); var md = (APU_MD)rig.Apu; md.Patch = p;
            rig.Pulse(0, timer, 15);
            rig.Run(0.0, false);
            // the note, in 100 ms RMS slices for the envelope
            var slices = new List<double>();
            var all = new List<float>();
            for (int k = 0; k < (int)(seconds * 10); k++) { rig.Run(0.1, true); var part = rig.Take(); all.AddRange(part); slices.Add(SegaMeasure.Rms(part, 0, part.Length)); }
            var x = all.ToArray();
            int from = (int)(rate * 0.8), to = Math.Min(x.Length, from + 32768);
            double rms = SegaMeasure.Rms(x, from, to);
            var (f0, a1) = SegaMeasure.Peak(x, rate, nesHz, from, to, 0.03);
            // the strongest harmonic and the series
            var amps = new double[13];
            double centroidNum = 0, centroidDen = 0;
            for (int h = 1; h <= 12; h++)
            {
                if (nesHz * h > rate * 0.45) break;
                amps[h] = SegaMeasure.Component(x, rate, f0 * h, from, to);
                centroidNum += amps[h] * amps[h] * f0 * h; centroidDen += amps[h] * amps[h];
            }
            double top = amps.Max();
            double sub = SegaMeasure.Component(x, rate, f0 / 2, from, to);
            string series = string.Join(" ", Enumerable.Range(1, 12).Select(h => amps[h] > 0 ? $"{20 * Math.Log10(Math.Max(1e-9, amps[h] / top)),4:F0}" : "   -"));
            Console.WriteLine($"[{p,2}] {patch.Name,-11} alg {patch.Algorithm} fb {patch.Feedback}  fundamental {f0:F2} Hz ({SegaMeasure.Cents(f0, nesHz):+0.00;-0.00} c; chip set to {md.FmHz(0):F2} Hz, {md.FmCents(0):+0.00;-0.00} c)  RMS {rms:F5} ({SegaMeasure.Db(rms, fixRms):+0.0;-0.0} dB vs FIX; attenuation {md.FmAttenuation(0)} steps, patch full RMS {patch.Rms():F4})");
            Console.WriteLine($"       harmonics 1-12 (dB re strongest): {series}   | sub-octave {SegaMeasure.Db(Math.Max(1e-9, sub), top):F0} dB, centroid {centroidNum / Math.Max(1e-12, centroidDen):F0} Hz");
            Console.WriteLine($"       loudness per 100 ms: {string.Join(" ", slices.Take(12).Select(v => (v * 1000).ToString("F1")))}  (x1000)   | {patch.Description}");

            if (wavDir != null)
            {
                var phrase = Phrase(p);
                string path = Path.Combine(wavDir, $"md_patch_{p:00}_{patch.Name.Replace(' ', '_').Replace('%', 'p')}.wav");
                var pcm = new List<short>(phrase.Length * 2);
                foreach (float v in phrase) { short sv = (short)Math.Clamp(v * 32767f * 3f, -32768, 32767); pcm.Add(sv); pcm.Add(sv); }
                WavFile.Write(path, pcm, rate);
                Console.WriteLine($"       wrote {path} ({phrase.Length / (double)rate:F1} s)");
            }
        }
        return 0;
    }

    /// <summary>Does a carrier's loudness depend on how hard it is modulated? (It should not: a phase-modulated sine keeps its amplitude.) Plays alg 4 with a constant modulator at several total levels and ratios.</summary>
    private static int Probe()
    {
        int timer = TimerFor(261.63);
        foreach (int mul in new[] { 1, 2, 3 })
            foreach (int tl in new[] { 127, 60, 50, 40, 30, 24, 16, 8, 0 })
            {
                var patch = new FmPatch("probe", "", 4, 0, new[] { new FmOperator(mul, tl), new FmOperator(1, 0), new FmOperator(1, 127), new FmOperator(1, 127) });
                var rig = new Rig("MD"); var md = (APU_MD)rig.Apu; md.UseCustomPatch(patch);
                rig.Pulse(0, timer, 15); rig.Run(0.4, false); rig.Run(0.5, true);
                var x = rig.Take();
                double rms = SegaMeasure.Rms(x, 0, x.Length), peak = x.Max(v => Math.Abs(v));
                Console.WriteLine($"   modulator x{mul} TL {tl,3}: RMS {rms:F5} peak {peak:F4} (carrier alone would be {patch.Rms(md.FmAttenuation(0)):F5})");
            }
        return 0;
    }

    /// <summary>The steady-state RMS of a patch at its own total levels: a held C4 through the whole APU_MD, mean over seconds 1.5 to 7.5, divided back by the attenuation the bridge applied.</summary>
    internal static (double Rms, double MinSlice, double MaxSlice) SteadyRms(FmPatch patch, double hz = 261.63)
    {
        var rig = new Rig("MD"); var md = (APU_MD)rig.Apu; md.UseCustomPatch(patch);
        rig.Pulse(0, TimerFor(hz), 15); rig.Run(1.5, false);
        var slices = new List<double>(); var all = new List<float>();
        for (int k = 0; k < 60; k++) { rig.Run(0.1, true); var part = rig.Take(); all.AddRange(part); slices.Add(SegaMeasure.Rms(part, 0, part.Length)); }
        double scale = FmPatchBank.Amplitude(md.FmAttenuation(0));
        var x = all.ToArray();
        return (SegaMeasure.Rms(x, 0, x.Length) / scale, slices.Min() / scale, slices.Max() / scale);
    }

    private static int Calibrate()
    {
        Console.WriteLine("// steady-state RMS at the patch's own total levels (mean over 1.5-7.5 s of a held C4); slices show the swing");
        var vals = new List<string>();
        for (int p = 0; p < FmPatchBank.Count; p++)
        {
            var patch = FmPatchBank.Patches[p];
            if (patch.Psg) { vals.Add("0"); continue; }
            var (rms, lo, hi) = SteadyRms(patch);
            vals.Add(rms.ToString("F5", System.Globalization.CultureInfo.InvariantCulture));
            Console.WriteLine($"//   [{p,2}] {patch.Name,-11} RMS {rms:F5} (100 ms slices {lo:F5} to {hi:F5}: {SegaMeasure.Db(hi, lo):F1} dB swing); formula said {FmPatchBank.CarrierRms * Math.Sqrt(patch.PowerSum):F5}");
        }
        Console.WriteLine("private static readonly double[] Calibration = { " + string.Join(", ", vals) + " };");
        return 0;
    }

    /// <summary>A short phrase through the whole path: a rising scale on pulse 1 with a triangle bass and a noise tick on every note, then a three-note chord on both pulses and the triangle.</summary>
    private static float[] Phrase(int patch)
    {
        var rig = new Rig("MD"); var md = (APU_MD)rig.Apu; md.Patch = patch;
        var outp = new List<float>();
        int[] scale = { 60, 62, 64, 65, 67, 69, 71, 72 };
        foreach (int n in scale)
        {
            rig.Pulse(0, TimerFor(440.0 * Math.Pow(2, (n - 69) / 12.0)), 13);
            double tri = 440.0 * Math.Pow(2, (n - 12 - 69) / 12.0);
            rig.Triangle((int)Math.Round(NesHz / (32.0 * tri)) - 1);
            rig.Noise(6, 5, false);
            rig.Run(0.14, true); outp.AddRange(rig.Take());
            rig.W(0x400C, 0x30); rig.Run(0.01, true); outp.AddRange(rig.Take());
        }
        rig.Silence(); rig.Run(0.15, true); outp.AddRange(rig.Take());
        rig.Pulse(0, TimerFor(261.63), 12); rig.Pulse(1, TimerFor(329.63), 12); rig.Triangle((int)Math.Round(NesHz / (32.0 * 130.81)) - 1);
        rig.Run(1.6, true); outp.AddRange(rig.Take());
        rig.Silence(); rig.Run(0.3, true); outp.AddRange(rig.Take());
        return outp.ToArray();
    }
}
