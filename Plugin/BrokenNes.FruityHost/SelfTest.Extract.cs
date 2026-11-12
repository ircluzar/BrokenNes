// Instrument extraction (2026-11-10): the round trip. A note is captured from a channel of a "game" (a scripted driver on the FIX chip, always; a real NES ROM with `extract-roundtrip`),
// turned into a preset by the shared capture core (Extract/), loaded into the real plugin DLL on the Presets chip and played by FL's note at the captured pitch for the captured length.
// The plugin's sound is compared with the game's channel alone: pitch (median cents of the pitch track), envelope (per-frame RMS, both normalised to their peak, mean absolute difference),
// spectrum (cosine similarity of the magnitude spectra of the whole note). Scale-free: the game's mix level and the plugin's Volume do not matter.
using System.Numerics;
using BrokenNes2.Extract;
using BrokenNes2.Instruments;
using NesEmulator;
using NesEmulator.Gb;
using NesEmulator.Plugin;

namespace BrokenNes.FruityHost;

public sealed record ExtractScore(double PitchCents, double EnvelopeError, double Spectral, int Frames, double Key)
{
    public override string ToString() => $"key {Key:0.00}, {Frames} frames: pitch {PitchCents:+0.0;-0.0} cents, envelope error {EnvelopeError * 100:0.0}%, spectral similarity {Spectral:0.000}";
}

public sealed unsafe partial class SelfTest
{
    public const double NesFrameHz = 60.0988;

    /// <summary>The `extract` check: the synthetic game's pulse 1 and triangle notes, captured, made presets, played through the DLL, measured; plus the definition's edge cases.</summary>
    private void ExtractTest(T t)
    {
        var (feed, pcm, perFrame) = SyntheticGame(0b0001);
        // the core: the note at frame 10 is the one keyed at frame 4 (decaying, duty change at 12, vibrato), ended by the silence at frame 40
        var n = feed.Recorder.Pick(0, 10, out string? why);
        t.Expect(n != null, "no note picked: " + why);
        if (n == null) return;
        string levels = string.Join(",", feed.Recorder.FramesOf(0).Take(60).Select(f => (f.KeyOn ? "*" : "") + f.Level));
        if (n.Frames < 36) { t.Expect(false, $"pulse 1's note is {n.Frames} frames, keyed at {n.KeyOnFrame}; levels by frame: {levels}"); return; }
        t.Expect(n.KeyOnFrame == 4 && n.End == CaptureEnd.Silenced && n.Frames == 36, $"pulse 1's note: keyed at {n.KeyOnFrame} (4), {n.Frames} frames (36), end {n.End} (Silenced)");
        t.Expect(n.Life[0].Level == 15 && n.Life[^1].Level < n.Life[0].Level && n.Life[0].Duty == 2 && n.Life[10].Duty == 1, $"the life: levels {n.Life[0].Level}..{n.Life[^1].Level}, duty {n.Life[0].Duty} then {n.Life[10].Duty}");
        t.Expect(Math.Abs(n.CentsAt(8)) > 5, $"the vibrato is not in the captured pitch ({n.CentsAt(8):0.0} cents at frame 8)");
        var again = CapturedNote.FromBytes(n.ToBytes());
        t.Expect(again != null && again.ToBytes().AsSpan().SequenceEqual(n.ToBytes()), "the captured note does not survive its own bytes");
        t.Expect(feed.Recorder.Pick(0, 50, out _) is { KeyOnFrame: 52 }, "a silent frame does not pick the next key-on");
        t.Expect(feed.Recorder.Pick(0, 95, out var none) == null && none != null, "a frame after the last note picked something");

        var score = RoundTrip(t, n, pcm, perFrame, "synthetic pulse 1");
        if (score != null)
        {
            t.Note("extract round trip (synthetic driver, pulse 1): " + score);
            t.Expect(Math.Abs(score.PitchCents) < 10, $"pitch off by {score.PitchCents:0.0} cents");
            t.Expect(score.EnvelopeError < 0.12, $"envelope error {score.EnvelopeError:P1}");
            t.Expect(score.Spectral > 0.85, $"spectral similarity {score.Spectral:0.000}");
        }
        // a preset without its envelope (the deliberate bug: the note behaviour dropped) must score worse on the envelope
        var flat = CapturedNote.FromBytes(n.ToBytes())!;
        for (int i = 0; i < flat.Life.Length; i++) { flat.Life[i].Level = 15; flat.Life[i].Period = flat.Life[0].Period; }
        var bad = RoundTrip(t, flat, pcm, perFrame, "flat", n);
        t.Expect(bad != null && bad.EnvelopeError > (score?.EnvelopeError ?? 0) * 2 + 0.05, $"the deliberate bug (the envelope flattened) was not caught: {bad}");

        var (tf, tpcm, tper) = SyntheticGame(0b0100);
        var tn = tf.Recorder.Pick(2, 20, out why);
        t.Expect(tn != null, "triangle: " + why);
        if (tn != null && RoundTrip(t, tn, tpcm, tper, "synthetic triangle") is { } ts)
        {
            t.Note("extract round trip (synthetic driver, triangle): " + ts);
            t.Expect(Math.Abs(ts.PitchCents) < 10 && ts.Spectral > 0.85, "triangle: " + ts);
        }

        // the plugin in ROM mode (the built-in game, or --rom): "Capture next note" through the DLL's hooks, on the first channel that plays within 10 s; the preset it makes loads and plays
        ExtractRomMode(t);

        // the Game Boy: the hardware envelope comes from the key-on snapshot, the legato pitch change from the captured life
        var (gf, gpcm, gper) = SyntheticGb();
        var gn = gf.Recorder.Pick(0, 10, out why);
        t.Expect(gn != null && gn.KeyOnFrame == 3 && gn.End == CaptureEnd.Silenced && gn.Frames == 27 && Math.Abs(gn.CentsAt(20) - 297) < 15, $"Game Boy square 1: {gn?.Describe() ?? why}, {gn?.CentsAt(20):0} cents at frame 23");
        if (gn != null && RoundTrip(t, gn, gpcm, gper, "synthetic Game Boy square 1") is { } gs)
        {
            t.Note("extract round trip (synthetic Game Boy, square 1): " + gs);
            t.Expect(Math.Abs(gs.PitchCents) < 10 && gs.EnvelopeError < 0.15 && gs.Spectral > 0.85, "Game Boy square 1: " + gs);
        }

        // the SNES, the Master System / Game Gear and the Genesis (SelfTest.ExtractMore.cs)
        ExtractMoreTest(t);
    }

    private void ExtractRomMode(T t)
    {
        using var p = RomPlugin();
        RenderBlocks(p, 0.5);
        string Words() { var b = new char[400]; long n; fixed (char* c = b) n = p.Test(1143, b.Length, (nint)c); return new string(b, 0, (int)Math.Clamp(n, 0, b.Length)); }
        for (int ch = 0; ch < 4; ch++)
        {
            t.Expect(p.Test(1140, ch) == 0, $"ROM mode: the capture of channel {ch} was refused: {Words()}");
            long state = 0;
            for (int i = 0; i < 40 && (state = p.Test(1141)) == 0; i++) RenderBlocks(p, 0.25);
            if (state != 1) continue;
            long len = p.Test(1142, 0);
            var file = new byte[Math.Max(0, len)];
            fixed (byte* b = file) p.Test(1142, file.Length, (nint)b);
            var read = BrokenNes2.Presets.PresetFile.Read(file);
            t.Expect(read.Ok && BrokenNes2.Presets.PresetNote.CapturedOf(read.Data!) != null, $"ROM mode: the captured preset does not read back with its envelope: {read.Problem}");
            using var q = NewOn(2, 0);
            t.Expect(PLoad(q, file, out string text) == 0, "ROM mode: the captured preset does not load: " + text);
            var s = new Scenario { Name = "x", Tempo = 120, LengthBeats = 2 };
            s.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 1, Key = 60, Velocity = 1, Channel = 0 });
            double rms = ImodelData.Rms(new Mixer { SampleRate = 44100 }.Run(q, s, 0));
            t.Expect(rms > 1e-4, $"ROM mode: the captured preset is silent (rms {rms})");
            t.Note($"ROM mode capture: {Words()} ({file.Length} bytes, plays at rms {rms:0.0000})");
            return;
        }
        t.Warn(false, "ROM mode: no channel played a whole note within 10 s of the game: the ROM-mode capture was not exercised");
    }

    /// <summary>A 60-frame Game Boy "game" on APU_GB: square 1 triggered at frame 3 (NR12 $F2: volume 15 decaying, duty 50%, A4 = 1750), its frequency moved to C5 (1797) at frame 15 without a
    /// trigger, the envelope reaches 0 at frame 30 (the note ends there), the DAC switched off at frame 40. Only square 1 is heard. Returns the feed and the chip's sound (mono, 48 kHz resampled to 44.1) with each frame's sample offset.</summary>
    private static (GbExtractFeed Feed, float[] Pcm, int[] FrameAt) SyntheticGb()
    {
        var apu = new APU_GB(GbModel.Dmg);
        apu.ResetPostBoot();
        var feed = new GbExtractFeed(apu);
        apu.WriteRegister(0x24, 0x77); apu.WriteRegister(0x25, 0x11);
        var raw = new List<float>(); var at = new int[61]; var buf = new short[8192];
        const int frameCycles = 70224, fsCycles = 8192;
        int fsLeft = fsCycles;
        for (int f = 0; f < 60; f++)
        {
            at[f] = raw.Count;
            if (f == 3) { apu.WriteRegister(0x11, 0x80); apu.WriteRegister(0x12, 0xF2); apu.WriteRegister(0x13, 1750 & 0xFF); apu.WriteRegister(0x14, (byte)(0x80 | 1750 >> 8)); }
            if (f == 15) { apu.WriteRegister(0x13, 1797 & 0xFF); apu.WriteRegister(0x14, (byte)(1797 >> 8)); }
            if (f == 40) apu.WriteRegister(0x12, 0x00);
            int left = frameCycles;
            while (left > 0) { int c = Math.Min(left, fsLeft); apu.Tick(c); left -= c; fsLeft -= c; if (fsLeft == 0) { apu.FrameSequencerStep(); fsLeft = fsCycles; } }
            int n; while ((n = apu.ReadSamples(buf)) > 0) for (int i = 0; i + 1 < n; i += 2) raw.Add((buf[i] + buf[i + 1]) / 65536f);
            feed.EndFrame();
        }
        at[60] = raw.Count;
        feed.Detach();
        int rate = apu.SampleRate;
        return (feed, Resample(raw.ToArray(), rate, 44100), at.Select(x => (int)((long)x * 44100 / rate)).ToArray());
    }

    /// <summary>A 100-frame "game" on the FIX chip: pulse 1 keyed at 4 (A4, volume 15 decaying by 1 every 3 frames, duty 50% then 25% at 14, vibrato from 8), silenced at 40, keyed again at 52 (E5) until 80;
    /// the triangle keyed at 10 (C4) for 40 frames. Only the channels in <paramref name="mask"/> are heard (the others still run). Returns the feed and the chip's sound with each frame's sample offset.</summary>
    private static (NesExtractFeed Feed, float[] Pcm, int[] FrameAt) SyntheticGame(int mask)
    {
        var chip = new NesApuInstrument("FIX", 44100, 0x0F);
        var apu = (APU_FIX)chip.Apu;
        apu.SetEnabledChannels(mask);
        var feed = new NesExtractFeed(apu, latch => { for (int i = 0; i < latch.Length; i++) latch[i] = chip.Shadow(i); return 0xFFFFFF; });
        chip.Write(0x15, 0x0F);
        var pcm = new List<float>();
        var at = new int[101];
        var buf = new float[4096];
        double carry = 0;
        for (int f = 0; f < 100; f++)
        {
            at[f] = pcm.Count;
            if (f == 4) { chip.Write(0, 0xB0 | 15); chip.Write(1, 0x08); chip.Write(2, 253); chip.Write(3, 0x08); }
            if (f > 4 && f < 40) { int vol = Math.Max(1, 15 - (f - 4) / 3); int duty = f >= 14 ? 1 : 2; chip.Write(0, (byte)(duty << 6 | 0x30 | vol)); if (f >= 8) chip.Write(2, (byte)(253 + (f % 4 < 2 ? 2 : -2))); }
            if (f == 40) chip.Write(0, 0xB0);
            if (f == 52) { chip.Write(0, 0xB0 | 12); chip.Write(2, 169); chip.Write(3, 0x08); }
            if (f == 80) chip.Write(0, 0xB0);
            if (f == 10) { chip.Write(8, 0xFF); chip.Write(0xA, 0xAB); chip.Write(0xB, 0x01); }
            if (f == 50) chip.Write(8, 0x80);
            double cycles = 1789773.0 / NesFrameHz + carry;
            long whole = (long)cycles; carry = cycles - whole;
            chip.Advance(whole);
            int got; while ((got = chip.ReadAvailable(buf)) > 0) pcm.AddRange(buf.AsSpan(0, got).ToArray());
            feed.EndFrame();
        }
        at[100] = pcm.Count;
        feed.Detach();
        return (feed, pcm.ToArray(), at);
    }

    /// <summary>Makes <paramref name="n"/> a preset, plays it through the DLL at its pitch for its length, and compares with <paramref name="gamePcm"/> over the note's frames (the game's channel alone).
    /// <paramref name="truth"/>: the note the game really played (default <paramref name="n"/>; the deliberate-bug run passes the real one).</summary>
    private ExtractScore? RoundTrip(T t, CapturedNote n, float[] gamePcm, int[] frameAt, string what, CapturedNote? truth = null, string? savePreset = null)
    {
        var file = CapturePreset.Write(n, what, "extract test", "", 0, out string? problem);
        t.Expect(file != null, $"{what}: no preset: {problem}");
        if (file == null) return null;
        if (savePreset != null) File.WriteAllBytes(savePreset, file);
        var real = truth ?? n;
        int from = frameAt[real.KeyOnFrame], to = frameAt[Math.Min(frameAt.Length - 1, real.KeyOnFrame + real.Frames)];
        var game = gamePcm[from..to];
        double fhz = ExtractChips.FrameHz(real.Console);
        double seconds = real.Frames / fhz;
        float[] played;
        using (var p = NewOn(2, ConsoleParam(n.Console)))
        {
            t.Expect(PLoad(p, file, out string text) == 0, $"{what}: the plugin refused the preset: {text}");
            var s = new Scenario { Name = "extract", Tempo = 120, LengthBeats = seconds * 2 + 1 };
            s.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = seconds * 2, Key = real.MidiKey, Velocity = 1.0, Channel = 0 });
            played = Audio.Mono(new Mixer { SampleRate = 44100 }.Run(p, s, 0), 2);
        }
        int len = Math.Min(game.Length, played.Length);
        return Score(game[..len], played[..len], 44100, real.Frames, real.MidiKey, !real.IsNoise, fhz, spectralPitch: real.Wide);
    }

    /// <summary><paramref name="spectralPitch"/>: the pitch difference is the shift of the log-frequency magnitude spectra that matches them best (FM voices, samples and the SNES: a pitch
    /// tracker locks onto different partials of such timbres), else the median of the two pitch tracks (the NES and Game Boy figures in the doc).</summary>
    public static ExtractScore Score(float[] game, float[] played, int rate, int frames, double key, bool pitched, double frameHz = NesFrameHz, bool spectralPitch = false)
    {
        // pitch: the median difference of the two pitch tracks over the note (the first 40 ms skipped: the track's window)
        double pitch = 0;
        if (pitched && spectralPitch) pitch = SpectralShiftCents(game, played, rate);
        else if (pitched)
        {
            double ms = game.Length * 1000.0 / rate;
            var a = Audio.Track(game, rate, 40, ms - 20); var b = Audio.Track(played, rate, 40, ms - 20);
            var d = a.Zip(b).Where(x => !double.IsNaN(x.First.Hz) && !double.IsNaN(x.Second.Hz) && x.First.Hz > 0 && x.Second.Hz > 0).Select(x => Audio.Cents(x.Second.Hz, x.First.Hz)).OrderBy(x => x).ToList();
            pitch = d.Count == 0 ? double.NaN : d[d.Count / 2];
        }
        // envelope: per-frame RMS, each normalised to its own peak
        double[] Env(float[] x) { var e = new double[frames]; for (int f = 0; f < frames; f++) { int s = (int)(f * rate / frameHz), u = Math.Min(x.Length, (int)((f + 1) * rate / frameHz)); e[f] = s < u ? Audio.Rms(x, s, u) : 0; } double m = e.Max(); return m > 0 ? e.Select(v => v / m).ToArray() : e; }
        var eg = Env(game); var ep = Env(played);
        double env = eg.Zip(ep).Average(x => Math.Abs(x.First - x.Second));
        // spectrum: magnitude spectra (Hann, power-of-two FFT of the whole note), cosine similarity
        double[] Spec(float[] x)
        {
            int nfft = 1; while (nfft < x.Length && nfft < 1 << 16) nfft <<= 1;
            var c = new Complex[nfft];
            for (int i = 0; i < Math.Min(x.Length, nfft); i++) c[i] = x[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Math.Max(1, Math.Min(x.Length, nfft) - 1)));
            Fft(c);
            return c.Take(nfft / 2).Select(v => v.Magnitude).ToArray();
        }
        var sg = Spec(game); var sp = Spec(played);
        double dot = 0, ng = 0, np = 0;
        for (int i = 0; i < Math.Min(sg.Length, sp.Length); i++) { dot += sg[i] * sp[i]; ng += sg[i] * sg[i]; np += sp[i] * sp[i]; }
        double spectral = ng > 0 && np > 0 ? dot / Math.Sqrt(ng * np) : 0;
        return new ExtractScore(pitch, env, spectral, frames, key);
    }

    /// <summary>The pitch of <paramref name="played"/> against <paramref name="game"/> in cents: the shift (within half an octave either way, 5-cent steps, refined by a parabola) of their magnitude spectra on a log-frequency
    /// axis (55 Hz to 11 kHz) that gives the largest correlation.</summary>
    internal static double SpectralShiftCents(float[] game, float[] played, int rate)
    {
        double[] LogSpec(float[] x)
        {
            int nfft = 1; while (nfft < x.Length && nfft < 1 << 16) nfft <<= 1;
            nfft = Math.Max(nfft, 8192);
            var c = new Complex[nfft];
            int m = Math.Min(x.Length, nfft);
            for (int i = 0; i < m; i++) c[i] = x[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Math.Max(1, m - 1)));
            Fft(c);
            const double step = 5, lo = 55;
            int bins = (int)(1200 * Math.Log2(11000 / lo) / step);
            // each log bin samples the magnitude spectrum at its centre (interpolated): bins that merely fall between FFT bins must not form a comb both spectra share
            var mag = new double[nfft / 2];
            for (int k = 0; k < nfft / 2; k++) mag[k] = c[k].Magnitude;
            var o = new double[bins];
            for (int b = 0; b < bins; b++)
            {
                double k = lo * Math.Pow(2, b * step / 1200) * nfft / rate;
                int k0 = (int)k; double fr = k - k0;
                o[b] = Math.Log(1 + 100 * (mag[Math.Min(k0, mag.Length - 1)] * (1 - fr) + mag[Math.Min(k0 + 1, mag.Length - 1)] * fr));
            }
            double mean = o.Average();
            for (int i = 0; i < bins; i++) o[i] -= mean;
            return o;
        }
        var a = LogSpec(game); var p = LogSpec(played);
        // (normalised over the overlap: a raw sum favours no shift, where the two spectra overlap most)
        double Corr(int s) { double d = 0, ea = 0, ep = 0; for (int i = 0; i < a.Length; i++) { int j = i + s; if (j >= 0 && j < p.Length) { d += a[i] * p[j]; ea += a[i] * a[i]; ep += p[j] * p[j]; } } return ea > 0 && ep > 0 ? d / Math.Sqrt(ea * ep) : 0; }
        int best = 0; double bv = double.MinValue;
        for (int s = -120; s <= 120; s++) { double v = Corr(s); if (v > bv) { bv = v; best = s; } }
        double y0 = Corr(best - 1), y2 = Corr(best + 1), den = y0 - 2 * bv + y2;
        double frac = den < 0 ? 0.5 * (y0 - y2) / den : 0;
        return (best + Math.Clamp(frac, -0.5, 0.5)) * 5;
    }

    private static void Fft(Complex[] a)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++) { int bit = n >> 1; for (; (j & bit) != 0; bit >>= 1) j ^= bit; j ^= bit; if (i < j) (a[i], a[j]) = (a[j], a[i]); }
        for (int len = 2; len <= n; len <<= 1)
        {
            var w = Complex.FromPolarCoordinates(1, -2 * Math.PI / len);
            for (int i = 0; i < n; i += len) { var wn = Complex.One; for (int k = 0; k < len / 2; k++) { var u = a[i + k]; var v = a[i + k + len / 2] * wn; a[i + k] = u + v; a[i + k + len / 2] = u - v; wn *= w; } }
        }
    }

    /// <summary>`extract-roundtrip <plugin.dll> <game.nes> --frame N --channel C [--out preset] [--input script] [--frames M]`: the same round trip on a real NES game (FIX cores), printed.</summary>
    public static int ExtractRoundTripCli(string[] args)
    {
        static string? O(string[] a, string n) { int i = Array.IndexOf(a, n); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
        if (args.Length < 3) { Console.Error.WriteLine("usage: extract-roundtrip <plugin.dll> <game.nes> --frame N --channel C [--out preset] [--frames M]"); return 2; }
        if (!args[2].EndsWith(".nes", StringComparison.OrdinalIgnoreCase)) return ExtractRoundTripMoreCli(args, n => O(args, n));
        int frame = int.Parse(O(args, "--frame") ?? "300"), channel = int.Parse(O(args, "--channel") ?? "0");
        int total = int.Parse(O(args, "--frames") ?? (frame + CapturedNote.MaxFrames + 2).ToString());
        var nes = new NES { RomName = Path.GetFileName(args[2]), RomPath = args[2] };
        nes.LoadROM(File.ReadAllBytes(args[2]));
        nes.SetCpuCore("FIX"); nes.SetPpuCore("FIX"); nes.SetApuCore("FIX");
        // --start F: press Start for 4 frames at frame F (default 40: past a title screen); --start -1 presses nothing
        int start = int.Parse(O(args, "--start") ?? "40");
        var held = new bool[8];
        var fix = NesExtractFeed.FixOf(nes);
        if (fix == null) { Console.Error.WriteLine("the game's APU is not APU_FIX"); return 1; }
        fix.SetEnabledChannels(1 << channel);
        var feed = new NesExtractFeed(fix, nes.CopyApuLatch);
        var pcm = new List<float>(); var at = new int[total + 1];
        for (int f = 0; f < total; f++)
        {
            at[f] = pcm.Count;
            held[3] = start >= 0 && f >= start && f < start + 4;
            nes.SetInputs(held, null);
            nes.RunFrame();
            int q = fix.GetQueuedSampleCount(); if (q > 0) pcm.AddRange(fix.GetAudioSamples(q));
            feed.EndFrame();
        }
        at[total] = pcm.Count;
        feed.Detach();
        Console.WriteLine("recorded " + total + " frames; per channel key-ons / sounding frames: " + string.Join(", ", Enumerable.Range(0, 4).Select(c => $"{CapturedNote.ChannelName(ConsoleId.Nes, c)} {feed.Recorder.FramesOf(c).Count(x => x.KeyOn)}/{feed.Recorder.FramesOf(c).Count(x => x.Level > 0)}")));
        var n = feed.Recorder.Pick(channel, frame, out string? why);
        if (n == null) { Console.Error.WriteLine(why); return 1; }
        Console.WriteLine("captured: " + n.Describe() + $"; levels {string.Join(",", n.Life.Take(24).Select(x => x.Level))}{(n.Frames > 24 ? ",..." : "")}");
        int rate = fix.GetSampleRate();
        var game = rate == 44100 ? pcm.ToArray() : Resample(pcm.ToArray(), rate, 44100);
        var scaled = at.Select(x => (int)((long)x * 44100 / rate)).ToArray();
        var suite = new SelfTest(args[1]);
        var t = new T();
        var score = suite.RoundTrip(t, n, game, scaled, Path.GetFileNameWithoutExtension(args[2]) + " ch" + channel, savePreset: O(args, "--out"));
        foreach (var f in t.Failures) Console.WriteLine("FAIL: " + f);
        if (score == null) return 1;
        Console.WriteLine("round trip: " + score);
        return t.Failures.Count == 0 ? 0 : 1;
    }

    private static float[] Resample(float[] x, int from, int to)
    {
        var o = new float[(long)x.Length * to / from];
        for (int i = 0; i < o.Length; i++) { double p = (double)i * from / to; int a = (int)p; double fr = p - a; o[i] = (float)(x[Math.Min(a, x.Length - 1)] * (1 - fr) + x[Math.Min(a + 1, x.Length - 1)] * fr); }
        return o;
    }
}
