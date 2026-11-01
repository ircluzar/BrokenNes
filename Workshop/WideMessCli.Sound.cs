using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes.Workshop;

/// <summary>
/// Milestone 3's checks (the sound chip, docs/projects/widemess-spec.md section 4), run by <c>--wms-test</c>. The test ROM (WideMess/testroms/sound) plays each new voice alone, all 32 at
/// once, a BRR voice at pitch 1.0, the echo with and without a voice's WEON bit, pitch modulation, noise, a one-shot PCM sample and a ring the 65816 rewrites through the port while it
/// plays. Every check measures the samples the session produced (an FFT: the frequency, the level, how pure the tone is, the distortion, how many voices are there) against the ROM's
/// manifest (sound.json, written by the same generator that made the samples), never just "something sounded". The negative controls: the same program without the unlock, on the
/// WideMess (silent new voices, the S-DSP's 8 only) and on a SNES (the same sound to the bit); the SNES image on a SNES.
/// </summary>
internal static partial class WideMessCli
{
    private const int Rate = 32000;

    /// <summary>A run of the sound ROM: each frame's stereo samples and the work RAM bytes the ROM keeps ($00-$0F).</summary>
    private sealed class SoundRun
    {
        public readonly List<short[]> Audio = new();
        public readonly List<byte[]> Ram = new();
        public byte[]? BlobsAtReady;        // the sound RAM regions of the manifest's blobs, at the first frame the ROM said "ready"
        public int Frames => Audio.Count;

        /// <summary>The frames of a step (by the step number the ROM keeps at $02), the first <paramref name="skip"/> left out.</summary>
        public List<int> StepFrames(int step, int skip = 2) => Enumerable.Range(0, Frames).Where(f => Ram[f][2] == step).Skip(skip).ToList();

        /// <summary>One channel (0 left, 1 right) of the given frames, joined.</summary>
        public double[] Channel(IEnumerable<int> frames, int ch)
        {
            var list = new List<double>();
            foreach (int f in frames) { var a = Audio[f]; for (int i = ch; i < a.Length; i += 2) list.Add(a[i]); }
            return list.ToArray();
        }

        public ulong Hash()
        {
            ulong h = 14695981039346656037UL;
            foreach (var a in Audio) foreach (short s in a) h = (h ^ (ushort)s) * 1099511628211UL;
            return h;
        }
    }

    private static readonly short[] soundBuf = new short[65536];

    private static SoundRun RecordSound(SnesSession s, int frames, JsonElement? manifest = null)
    {
        var run = new SoundRun();
        for (int f = 0; f < frames; f++)
        {
            s.RunFrame();
            var chunk = new List<short>();
            int n;
            while ((n = s.ReadSamples(soundBuf)) > 0) chunk.AddRange(soundBuf.AsSpan(0, n).ToArray());
            run.Audio.Add(chunk.ToArray());
            run.Ram.Add(s.Board.Wram.AsSpan(0, 0x10).ToArray());
            if (run.BlobsAtReady == null && run.Ram[f][0x0B] == 1 && manifest is { } m && s.WideMessSound is { } apu)
            {
                var bytes = new List<byte>();
                foreach (var b in m.GetProperty("blobs").EnumerateArray())
                {
                    int a = b.GetProperty("address").GetInt32(), len = b.GetProperty("length").GetInt32();
                    for (int i = 0; i < len; i++) bytes.Add(apu.SoundRam(a + i));
                }
                run.BlobsAtReady = bytes.ToArray();
            }
        }
        return run;
    }

    // ------------------------------------------------------------------------------------------------ spectra

    /// <summary>The one-sided power spectrum (bins 0..n/2) of the first n samples (a power of two), Hann-windowed; also Σw² for amplitudes.</summary>
    private static double[] Spectrum(double[] x, int n, out double windowPower)
    {
        var re = new double[n]; var im = new double[n];
        windowPower = 0;
        for (int i = 0; i < n; i++) { double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n); re[i] = x[i] * w; windowPower += w * w; }
        Fft(re, im);
        var p = new double[n / 2 + 1];
        for (int k = 0; k <= n / 2; k++) p[k] = re[k] * re[k] + im[k] * im[k];
        return p;
    }

    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti; re[a] += tr; im[a] += ti;
                    double nr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = nr;
                }
            }
        }
    }

    private static int Pow2Under(int n) { int p = 1; while (p * 2 <= n) p *= 2; return p; }

    /// <summary>What a stretch of one channel holds: its strongest tone (frequency by parabolic interpolation, amplitude from the power around it), how much of the power that tone holds, the RMS.</summary>
    private sealed record Tone(double Hz, double Amplitude, double Purity, double Rms, double[] Power, int N, double WindowPower)
    {
        /// <summary>The amplitude of a sinusoid near <paramref name="hz"/> (the power within ±<paramref name="bins"/> bins).</summary>
        public double AmplitudeAt(double hz, int bins = 4) => 2 * Math.Sqrt(Band(hz, bins) / (N * WindowPower));
        public double Band(double hz, int bins = 4)
        {
            int c = (int)Math.Round(hz * N / Rate); double s = 0;
            for (int k = Math.Max(1, c - bins); k <= Math.Min(Power.Length - 1, c + bins); k++) s += Power[k];
            return s;
        }
        public double Total => Power.Skip(1).Sum();
    }

    private static Tone Analyse(double[] x, int maxN = 4096)
    {
        int n = Pow2Under(Math.Min(x.Length, maxN));
        double rms = Math.Sqrt(x.Take(n).Sum(v => v * v) / Math.Max(1, n));
        var p = Spectrum(x, n, out double wp);
        int peak = 1;
        for (int k = 2; k < p.Length - 1; k++) if (p[k] > p[peak]) peak = k;
        double hz = peak * (double)Rate / n;
        if (peak > 1 && peak < p.Length - 1 && p[peak] > 0)
        {
            double a = Math.Log(p[peak - 1] + 1e-12), b = Math.Log(p[peak]), c = Math.Log(p[peak + 1] + 1e-12);
            double d = 0.5 * (a - c) / (a - 2 * b + c);
            if (double.IsFinite(d) && Math.Abs(d) <= 1) hz = (peak + d) * Rate / n;
        }
        var t = new Tone(hz, 0, 0, rms, p, n, wp);
        double total = t.Total;
        double band = t.Band(hz);
        // a windowed sinusoid A cos: Σ|X|² over its band ≈ (A/2)² · N · Σw² (both halves of the spectrum hold half), so A = 2·sqrt(band / (N·Σw²))
        double amp = 2 * Math.Sqrt(band / (n * wp));
        return t with { Amplitude = amp, Purity = total > 0 ? band / total : 0 };
    }

    private static double Db(double ratio) => 10 * Math.Log10(Math.Max(ratio, 1e-30));

    // ------------------------------------------------------------------------------------------------ the checks

    /// <summary>The sound checks; false when the ROMs are missing (the caller says so).</summary>
    private static bool SoundTests(string dir)
    {
        string wms = Path.Combine(dir, "sound.wms"), nounlock = Path.Combine(dir, "sound_nounlock.wms"), sfc = Path.Combine(dir, "sound.sfc"), json = Path.Combine(dir, "sound.json");
        foreach (var f in new[] { wms, nounlock, sfc, json })
            if (!File.Exists(f)) { Check(false, $"the sound test ROMs are there", $"missing {f} (build WideMess/testroms/sound/build.ps1)"); return false; }
        using var doc = JsonDocument.Parse(File.ReadAllText(json));
        var m = doc.RootElement;
        var voices = m.GetProperty("voices").EnumerateArray().ToArray();
        int stepCount = m.GetProperty("steps").GetArrayLength();
        int stepFrames = m.GetProperty("step_frames").GetInt32();
        int frames = 40 + stepFrames * (stepCount - 1) + 8;
        double env = m.GetProperty("gain_env").GetInt32() / 2048.0, mvol = m.GetProperty("mvol").GetInt32() / 128.0;
        int soloL = m.GetProperty("solo")[0].GetInt32(), soloR = m.GetProperty("solo")[1].GetInt32();
        int allStep = 25, brrStep = 26, echoOn = 27, echoOff = 28, pmStep = 29, noiseStep = 30, oneShotStep = 31, streamStep = m.GetProperty("stream").GetProperty("step").GetInt32(), offStep = 33;

        Console.WriteLine("S1. The sound ROM on a WideMess (the WideMess sound chip, WMS)");
        SoundRun run;
        string apuName;
        using (var s = Session(ConsoleKind.WideMess, File.ReadAllBytes(wms)))
        {
            apuName = s.Apu.CoreName;
            run = RecordSound(s, frames, m);
            var last = run.Ram[^1];
            Check(apuName == "WMS" && s.WideMessSound is { Unlocked: true }, "the WideMess console plays it on its own sound chip (WMS), unlocked by the 65816's unlock", $"APU {apuName}, {s.Apu.Describe()}");
            Check(last[3] == 0x57 && (last[4] & 0x04) != 0, "the 65816 reads WMID $57 and WMFEAT with bit 2 (sound)", $"WMID ${last[3]:X2}, WMFEAT ${last[4]:X2}");
            Check(last[5] == DSP_SFC.WideId, "the SPC700 reads WID $57 at DSP $9D (the unlock reached the sound unit)", $"${last[5]:X2}");
            var blobs = m.GetProperty("blobs").EnumerateArray().ToArray();
            int ok = 0, at = 0; var bad = new List<string>();
            foreach (var b in blobs)
            {
                int len = b.GetProperty("length").GetInt32();
                uint crc = run.BlobsAtReady == null ? 0 : WmsFile.Crc32(run.BlobsAtReady.AsSpan(at, len));
                if (crc == b.GetProperty("crc").GetUInt32()) ok++; else bad.Add($"${b.GetProperty("address").GetInt32():X5}");
                at += len;
            }
            Check(ok == blobs.Length, "the port's DMA put every sample and the directory at its 20-bit address (CRC-32 of each, read from the sound RAM)", $"{ok} of {blobs.Length}{(bad.Count > 0 ? ", wrong at " + string.Join(" ", bad) : "")}");
            int sum = last[6] | last[7] << 8, want = m.GetProperty("readback").GetProperty("sum").GetInt32();
            Check(sum == want, "the 65816 reads the directory back through the port ($21F3) with the right sum", $"${sum:X4}, expected ${want:X4}");
        }

        // each new voice alone: its frequency, its level on both sides, a clean tone
        var soloLevel = new Dictionary<int, double>();
        for (int v = 8; v < 32; v++)
        {
            var vo = voices[v];
            int step = v - 7;
            var fr = run.StepFrames(step);
            var l = Analyse(run.Channel(fr, 0)); var r = Analyse(run.Channel(fr, 1));
            double hz = vo.GetProperty("freq").GetDouble(), fund = vo.GetProperty("fund").GetDouble();
            double wantL = fund * env * soloL / 128.0 * mvol, ratio = r.Amplitude / Math.Max(1, l.Amplitude);
            soloLevel[v] = l.Amplitude;
            string kind = vo.GetProperty("kind").GetString()!;
            bool good = Math.Abs(l.Hz - hz) < 2 && Math.Abs(l.Amplitude / wantL - 1) < 0.04 && Math.Abs(ratio - soloR / (double)soloL) < 0.03 && l.Purity > 0.9;
            Check(good, $"voice {v} alone ({kind.ToUpperInvariant()} at ${vo.GetProperty("address").GetInt32():X5})",
                $"{l.Hz:0.0} Hz (want {hz:0.0}), level {l.Amplitude:0} (want {wantL:0}, x{l.Amplitude / wantL:0.000}), right/left {ratio:0.000}, tone {l.Purity * 100:0.0}% of the power");
        }

        // all 32 at once: 32 tones at the 32 frequencies, each at its level, and nothing else that loud
        {
            var fr = run.StepFrames(allStep);
            var t = Analyse(run.Channel(fr, 0));
            var expected = voices.Select(vo => (hz: vo.GetProperty("freq").GetDouble(), amp: vo.GetProperty("fund").GetDouble() * env * vo.GetProperty("all_vol").GetInt32() / 128.0 * mvol)).ToArray();
            var found = Peaks(t, expected.Select(e => e.amp).OrderBy(a => a).ElementAt(16) / 4, 100, 4000);
            int matched = 0; double worstDb = 0; var missing = new List<string>();
            foreach (var (hz, amp) in expected)
            {
                var near = found.Where(p => Math.Abs(p - hz) < 3).ToList();
                if (near.Count != 1) { missing.Add($"{hz:0}"); continue; }
                double db = 20 * Math.Log10(t.AmplitudeAt(hz, 3) / amp);
                worstDb = Math.Max(worstDb, Math.Abs(db));
                if (Math.Abs(db) < 1.5) matched++; else missing.Add($"{hz:0} at {db:+0.0;-0.0} dB");
            }
            Check(found.Count == 32 && matched == 32, "all 32 voices at once: 32 tones at the 32 frequencies (200-3300 Hz), each within 1.5 dB of its level, nothing else above a quarter of their level",
                $"{found.Count} tones found, {matched} matched, worst {worstDb:0.00} dB{(missing.Count > 0 ? "; not right: " + string.Join(", ", missing) : "")}");
        }

        // 16-bit PCM against BRR, both at pitch 1.0 (no resampling): the harmonics their sample's precision leaves
        {
            double Thd(int step, double hz)
            {
                var t = Analyse(run.Channel(run.StepFrames(step), 0));
                double harm = 0;
                for (int h = 2; h * hz < 15500; h++) harm += t.Band(h * hz, 3);
                return Db(harm / t.Band(hz, 3));
            }
            double pcm = Thd(1, voices[8].GetProperty("freq").GetDouble()), brr = Thd(brrStep, 1000.0);
            Check(pcm < -55 && brr > -45 && brr - pcm > 20, "16-bit PCM keeps its precision: a PCM sine's harmonics are far below a BRR sine's (both at pitch 1.0)", $"PCM (voice 8) {pcm:0.0} dB, BRR (voice 9) {brr:0.0} dB");
        }

        // the echo takes the new voices: MVOL 0, EVOL $7F, voice 8 with and without its WEON bit
        {
            var on = Analyse(run.Channel(run.StepFrames(echoOn, 3), 0));
            var off = Analyse(run.Channel(run.StepFrames(echoOff, 3), 0));
            double ratio = on.Amplitude / soloLevel[8];
            Check(Math.Abs(on.Hz - 1000) < 2 && Math.Abs(ratio - 1) < 0.06 && off.Rms < 1, "the echo takes a new voice: heard through the echo alone with its WEON bit, silent without it",
                $"with WEON: {on.Hz:0.0} Hz at {ratio:0.000} x its dry level; without: RMS {off.Rms:0.00}");
        }

        // pitch modulation from the voice before (voice 8, silent, at 50 Hz) on voice 9
        {
            double carrier = voices[9].GetProperty("freq").GetDouble(), mod = m.GetProperty("pm").GetProperty("mod_hz").GetDouble();
            var pm = Analyse(run.Channel(run.StepFrames(pmStep), 0), 8192);
            var plain = Analyse(run.Channel(run.StepFrames(9 - 7), 0), 8192);
            double c = pm.Band(carrier, 2) / pm.Total, lo = pm.Band(carrier - mod, 2) / pm.Total, hi = pm.Band(carrier + mod, 2) / pm.Total, c0 = plain.Band(carrier, 2) / plain.Total;
            Check(c < 0.5 && lo > 0.1 && hi > 0.1 && c0 > 0.9, "pitch modulation runs into the new voices: voice 9's tone splits into sidebands 50 Hz apart (alone it is one tone)",
                $"carrier {c * 100:0}% (alone {c0 * 100:0}%), sidebands {lo * 100:0}% and {hi * 100:0}%");
        }

        // noise on a new voice
        {
            var n = Analyse(run.Channel(run.StepFrames(noiseStep), 0));
            double maxBin = n.Power.Skip(1).Max() / n.Total;
            Check(n.Rms > 2000 && maxBin < 0.02, "a new voice plays the noise (WNON): loud and without a tone", $"RMS {n.Rms:0}, the strongest bin {maxBin * 100:0.00}% of the power");
        }

        // a PCM sample without the loop bit plays once (100 ms) and stops
        {
            var fr = run.StepFrames(oneShotStep, 0);
            var during = Analyse(run.Channel(fr.Skip(1).Take(4), 0), 2048);
            double after = Rms(run.Channel(fr.Skip(8), 0));
            Check(during.Rms > 1000 && Math.Abs(during.Hz - 1000) < 4 && after < 1, "a PCM sample without the loop bit plays once and goes silent", $"while it plays: {during.Hz:0.0} Hz, RMS {during.Rms:0}; after: RMS {after:0.00}");
        }

        // the stream: the 65816 rewrites the ring through the port while the voice plays it; the SPC700 does nothing
        {
            var fr = run.StepFrames(streamStep, 0);
            int at = m.GetProperty("stream").GetProperty("rewrite_at").GetInt32();
            var before = Analyse(run.Channel(fr.Skip(2).Take(at - 2), 0), 2048);
            var after = Analyse(run.Channel(fr.Skip(at + 2), 0), 2048);
            Check(Math.Abs(before.Hz - 1000) < 8 && Math.Abs(after.Hz - 2000) < 8 && before.Purity > 0.9 && after.Purity > 0.9, "a sample streams through the port: the ring the voice loops over is rewritten by the 65816's DMA while it plays",
                $"before {before.Hz:0.0} Hz, after {after.Hz:0.0} Hz");
            int ring = m.GetProperty("stream").GetProperty("address").GetInt32(), ringLen = m.GetProperty("stream").GetProperty("length").GetInt32() * 2;
            var addrs = fr.Skip(2).Select(f => run.Ram[f][8] | run.Ram[f][9] << 8 | run.Ram[f][10] << 16).ToList();
            bool inRing = addrs.All(a => a >= ring && a < ring + ringLen);
            Check(inRing && addrs.Distinct().Count() >= addrs.Count / 2, "the voice's playing address (second page $xA/$xB/$xE, read by the SPC700) is inside the ring and moves",
                $"{string.Join(" ", addrs.Take(6).Select(a => $"${a:X5}"))} ...");
        }
        Check(Rms(run.Channel(run.StepFrames(offStep), 0)) < 1, "silence after the last step");

        int allStart = run.StepFrames(allStep, 0).FirstOrDefault(-1);
        if (allStart < 0) { Check(false, "the sound ROM reached its 32-voice step", "it never did"); return true; }

        Console.WriteLine("S2. Negative control: the same program WITHOUT the unlock");
        byte[] nounlockFile = File.ReadAllBytes(nounlock);
        SoundRun ctl;
        using (var s = Session(ConsoleKind.WideMess, nounlockFile))
        {
            ctl = RecordSound(s, frames);
            var last = ctl.Ram[^1];
            Check(s.WideMess is { Unlocked: false } && s.WideMessSound is { Unlocked: false }, "the console and its sound chip stay locked");
            Check(last[5] == 0 && last[3] != 0x57, "the SPC700 reads the S-DSP's mirror at $9D (0), the 65816 no WMID", $"SPC700 ${last[5]:X2}, WMID ${last[3]:X2}");
            bool empty = s.WideMessSound!.SoundRamHigh.All(b => b == 0);
            int sum = last[6] | last[7] << 8;
            Check(empty && sum != m.GetProperty("readback").GetProperty("sum").GetInt32(), "the port's DMA went nowhere: the sound RAM above 64 KB is empty and the read-back is open bus", $"read-back sum ${sum:X4}");
        }
        {
            double loudest = 0; int loudestVoice = 8;
            for (int v = 8; v < 32; v++) { double r = Rms(ctl.Channel(ctl.StepFrames(v - 7), 0)); if (r > loudest) { loudest = r; loudestVoice = v; } }
            Check(loudest < 1, "every new voice's step is silent (their second-page writes went nowhere)", $"the loudest: voice {loudestVoice}, RMS {loudest:0.00}");
            var t = Analyse(ctl.Channel(ctl.StepFrames(allStep), 0));
            var expected = voices.Take(8).Select(vo => vo.GetProperty("fund").GetDouble() * env * vo.GetProperty("all_vol").GetInt32() / 128.0 * mvol).ToArray();
            var found = Peaks(t, expected.Min() / 4, 100, 4000);
            Check(found.Count == 8 && found.All(f => f < 950), "the all-voices step has the S-DSP's 8 tones (200-900 Hz) and nothing else", $"{found.Count} tones: {string.Join(" ", found.Select(f => f.ToString("0")))}");
            Check(Rms(ctl.Channel(ctl.StepFrames(echoOn, 3), 0)) < 1, "the echo step is silent (voice 8 does not exist)");
        }
        using (var s = Session(ConsoleKind.Snes, WmsFile.Payload(nounlockFile, out _)))
        {
            var snes = RecordSound(s, frames);
            Check(snes.Hash() == ctl.Hash(), "the same program on a SNES sounds the same to the bit (every sample of the run)", $"SNES {snes.Hash():X16}, WideMess {ctl.Hash():X16}");
        }

        Console.WriteLine("S3. Negative control: the ROM's SNES image (with the unlock) on a SNES");
        using (var s = Session(ConsoleKind.Snes, File.ReadAllBytes(sfc)))
        {
            var snes = RecordSound(s, frames);
            var last = snes.Ram[^1];
            var t = Analyse(snes.Channel(snes.StepFrames(allStep), 0));
            var found = Peaks(t, 100, 100, 4000);
            double loudest = Enumerable.Range(8, 24).Max(v => Rms(snes.Channel(snes.StepFrames(v - 7), 0)));
            Check(last[3] == 0x42 && last[5] == 0 && found.Count == 8 && loudest < 1, "a SNES has no WideMess sound: WMID is open bus, WID the mirror, the new voices silent, the S-DSP's 8 play",
                $"WMID ${last[3]:X2}, WID ${last[5]:X2}, {found.Count} tones in the all-voices step, the loudest new voice RMS {loudest:0.00}");
        }

        Console.WriteLine("S4. Save states carry the WideMess sound");
        {
            byte[] rom = File.ReadAllBytes(wms);
            int at = allStart + 4;                                   // inside the all-voices step
            using var a = Session(ConsoleKind.WideMess, rom);
            RecordSound(a, at);
            byte[] state = a.SaveState();
            var rest = RecordSound(a, 80);
            using var b = Session(ConsoleKind.WideMess, rom);
            b.LoadState(state);
            var again = RecordSound(b, 80);
            var tone = Analyse(again.Channel(Enumerable.Range(0, 8), 0));
            Check(rest.Hash() == again.Hash() && tone.Rms > 1000, "saved inside the 32-voice step and loaded into a fresh session: the next 80 frames sound the same to the bit",
                $"{state.Length / 1024} KB, {rest.Hash():X16} / {again.Hash():X16}");
        }

        Console.WriteLine("S5. The sound chip on the menu, and swapped");
        {
            var menu = CoreCatalog.Options(ConsoleKind.WideMess, CoreSlot.Apu).Select(o => o.Id).ToList();
            Check(menu.Count > 0 && menu[0] == "WMS" && menu.Contains("SFC") && menu.Contains("HLE") && CoreCatalog.Default(ConsoleKind.WideMess, CoreSlot.Apu) == "WMS",
                "the WideMess APU menu: WMS first (the default), SFC and HLE kept", string.Join(", ", menu));
            using var s = Session(ConsoleKind.WideMess, File.ReadAllBytes(wms));
            RecordSound(s, 60);
            bool swapped = s.TrySwapCore(CoreSlot.Apu, "SFC");
            string said = s.Description;
            bool noPort = s.WideMess!.Sound == null && s.WideMessSound == null;
            bool back = s.TrySwapCore(CoreSlot.Apu, "WMS");
            Check(swapped && noPort && said.Contains("no WideMess sound") && back && s.WideMessSound is { Unlocked: true }, "swapped to the SNES's SFC chip the game has no WideMess sound and the session says so; WMS swapped back is unlocked",
                said[(said.IndexOf("| APU", StringComparison.Ordinal) + 2)..]);
        }

        Console.WriteLine("S6. Cost of the new voices");
        {
            using var s = Session(ConsoleKind.WideMess, File.ReadAllBytes(wms));
            RecordSound(s, allStart + 2);
            var buf = new short[8192];
            for (int i = 0; i < 5; i++) { s.RunFrame(); while (s.ReadSamples(buf) > 0) { } }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 60; i++) { s.RunFrame(); while (s.ReadSamples(buf) > 0) { } }
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Check(bytes == 0, "60 frames with 32 voices playing allocate nothing", $"{bytes} bytes");
        }
        return true;
    }

    private static double Rms(double[] x) => x.Length == 0 ? 0 : Math.Sqrt(x.Sum(v => v * v) / x.Length);

    /// <summary>The frequencies of the spectrum's local maxima whose amplitude is above <paramref name="minAmplitude"/>, between <paramref name="loHz"/> and <paramref name="hiHz"/>.</summary>
    private static List<double> Peaks(Tone t, double minAmplitude, double loHz, double hiHz)
    {
        var list = new List<double>();
        var p = t.Power;
        int lo = Math.Max(2, (int)(loHz * t.N / Rate)), hi = Math.Min(p.Length - 3, (int)(hiHz * t.N / Rate));
        for (int k = lo; k <= hi; k++)
        {
            if (!(p[k] > p[k - 1] && p[k] >= p[k + 1] && p[k] > p[k - 2] && p[k] >= p[k + 2])) continue;
            double hz = k * (double)Rate / t.N;
            if (t.AmplitudeAt(hz, 3) < minAmplitude) continue;
            double a = Math.Log(p[k - 1] + 1e-12), b = Math.Log(p[k]), c = Math.Log(p[k + 1] + 1e-12), d = 0.5 * (a - c) / (a - 2 * b + c);
            list.Add(double.IsFinite(d) && Math.Abs(d) <= 1 ? (k + d) * (double)Rate / t.N : hz);     // between the bins (parabola on the log power)
        }
        return list;
    }
}
