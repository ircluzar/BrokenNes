using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator;
using NesEmulator.Mix;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--sega-stems-test [--md-rom file] [--gg-rom file] [--verbose]</c> (round 3, track C3b): the per-voice stems of the Sega sound units that the FL plugin's ROM mode plays one channel at a time. Three claims, each measured:
/// <list type="bullet">
/// <item>the stems add up to the mix: from the same run of the chips, frame for frame, to the rounding of the mix's 16-bit samples (the largest difference is printed in 16-bit steps);</item>
/// <item>each stem is its voice and nothing else: the same register script is played on a second unit with every other voice silenced (the PSG's volume registers, the YM2612's <c>MuteMask</c>, the YM2413's <c>Mask</c>) and the
/// stem equals that unit's mix;</item>
/// <item>asking for stems changes nothing: the mix is bit-identical with them on and off, they allocate nothing while running, a stem started in the middle of a note joins the mix within a tenth of a second.</item>
/// </list>
/// then real games (a Genesis game and a Game Gear game when the files are there). Exit code 0 = every check passed.
/// </summary>
internal static class SegaStemsTestCli
{
    private static int passes, failures;
    private static bool verbose;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) passes++; else failures++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
    }
    private static void Guard(Action group, string name)
    {
        try { group(); }
        catch (Exception e) { Check($"{name} ran to the end", false, e.GetType().Name + ": " + e.Message); }
    }
    private static string? Arg(string[] a, string n) { int i = Array.FindIndex(a, x => x.Equals(n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

    private const double LsbF = 32768.0;

    public static int Run(string[] args)
    {
        verbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);
        string? mdRom = Arg(args, "--md-rom") ?? Environment.GetEnvironmentVariable("BROKENNES_MD_ROM");
        if (string.IsNullOrEmpty(mdRom)) { const string sonic = @"X:\EMULATION\Sega Genesis\Sonic the Hedgehog (JUE) [!].bin"; if (File.Exists(sonic)) mdRom = sonic; }
        string? ggRom = Arg(args, "--gg-rom");
        if (string.IsNullOrEmpty(ggRom) && Directory.Exists(@"X:\EMULATION\Sega Game Gear")) ggRom = Directory.EnumerateFiles(@"X:\EMULATION\Sega Game Gear", "*.gg").OrderBy(f => f).Skip(6).FirstOrDefault();
        string only = Arg(args, "--only") ?? "";
        bool Want(string s) => only.Length == 0 || only.Equals(s, StringComparison.OrdinalIgnoreCase);

        if (Want("psg")) { Console.WriteLine("== PSG stems (SmsSound, the SN76489's four voices)"); Guard(PsgSum, "PsgSum"); Guard(PsgIsolation, "PsgIsolation"); Guard(PsgStereo, "PsgStereo"); Guard(PsgMidStream, "PsgMidStream"); Guard(PsgNoCost, "PsgNoCost"); }
        if (Want("md")) { Console.WriteLine("\n== Genesis unit stems (MdSoundUnit: the PSG, FM 1-6 and the DAC)"); Guard(MdSum, "MdSum"); Guard(MdIsolation, "MdIsolation"); Guard(MdModel2, "MdModel2"); Guard(MdMidStream, "MdMidStream"); Guard(MdNoCost, "MdNoCost"); }
        if (Want("fm")) { Console.WriteLine("\n== Master System FM unit stems (SmsFmSound: the PSG, FM 1-9 and the rhythm voices)"); Guard(FmSum, "FmSum"); Guard(FmIsolation, "FmIsolation"); Guard(FmGating, "FmGating"); Guard(FmMidStream, "FmMidStream"); Guard(FmNoCost, "FmNoCost"); }
        if (Want("games")) { Console.WriteLine("\n== real games"); Guard(() => GenesisGame(mdRom), "GenesisGame"); Guard(() => GameGearGame(ggRom), "GameGearGame"); }
        Console.WriteLine($"\n{passes} passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    // ============================================================================================ measuring

    private static double MaxDiffLsb(IReadOnlyList<float> a, IReadOnlyList<float> b, int from = 0, int skipFrom = -1, int skipTo = -1)
    {
        int n = Math.Min(a.Count, b.Count);
        double worst = 0;
        for (int i = from; i < n; i++)
        {
            if (i >= skipFrom && i < skipTo) continue;
            worst = Math.Max(worst, Math.Abs(a[i] - b[i]) * LsbF);
        }
        return worst;
    }

    private static double Rms(IReadOnlyList<float> x, int from = 0)
    {
        if (x.Count <= from) return 0;
        double s = 0; for (int i = from; i < x.Count; i++) s += x[i] * (double)x[i];
        return Math.Sqrt(s / (x.Count - from));
    }

    private static List<float> Sum(IEnumerable<List<float>> stems)
    {
        var list = stems.ToList();
        int n = list.Min(l => l.Count);
        var sum = new List<float>(n);
        for (int i = 0; i < n; i++) { float s = 0; foreach (var l in list) s += l[i]; sum.Add(s); }
        return sum;
    }

    private abstract class Rig
    {
        public readonly List<float> Mix = new();
        public readonly List<short> Shorts = new();      // the mix as the unit delivered it, left then right
        public abstract IVoiceStems Stems { get; }
        public List<float>[] StemData = [];
        private readonly float[] tmp = new float[16384];
        private bool grabbing;

        protected void Collect(short[] buf, int n)
        {
            for (int i = 0; i + 1 < n; i += 2) { Mix.Add((buf[i] + buf[i + 1]) * (0.5f / 32768f)); Shorts.Add(buf[i]); Shorts.Add(buf[i + 1]); }
        }

        /// <summary>Takes the stems that finished since the last call (after the mix has been drained).</summary>
        protected void GrabStems()
        {
            var st = Stems;
            if (StemData.Length != st.StemCount) StemData = Enumerable.Range(0, st.StemCount).Select(_ => new List<float>()).ToArray();
            for (int s = 0; s < st.StemCount; s++)
            {
                if ((st.StemMask >> s & 1) == 0) continue;
                int n;
                while ((n = st.ReadStem(s, tmp)) > 0) for (int i = 0; i < n; i++) StemData[s].Add(tmp[i]);
            }
            grabbing = true;
        }
        public bool Grabbed => grabbing;
    }

    private static string StemWord(IVoiceStems u, int s) => u.StemName(s);

    // ============================================================================================ the PSG

    private sealed class PsgRig : Rig
    {
        public readonly SmsSound Unit;
        private readonly short[] buf = new short[16384];
        public PsgRig() { Unit = new SmsSound(3579545.0, 44100); }
        public override IVoiceStems Stems => Unit;
        public void Frame(int clocks = 59736)
        {
            Unit.Run(clocks);
            int n; while ((n = Unit.ReadSamples(buf)) > 0) Collect(buf, n);
            GrabStems();
        }
    }

    /// <summary>One frame's register writes for a random-looking but fixed script: the bytes to send for the PSG, voices in 0 to 3.</summary>
    private static IEnumerable<byte[]> PsgScript(int frame, Random rng, Func<int, bool> keep)
    {
        byte Vol(int v, int vol) => (byte)(0x90 | v << 5 | (keep(v) ? vol : 15));
        if (frame == 0)
        {
            int[] periods = [254, 338, 508];
            for (int v = 0; v < 3; v++) yield return [(byte)(0x80 | v << 5 | periods[v] & 15), (byte)(periods[v] >> 4)];
            yield return [0xE5];
            yield return [Vol(0, 3)]; yield return [Vol(1, 5)]; yield return [Vol(2, 7)]; yield return [Vol(3, 9)];
            yield break;
        }
        if (frame % 3 != 0) yield break;
        int voice = rng.Next(4), op = rng.Next(4), val = rng.Next(0, 1000);
        if (op == 0 && voice < 3) { int p = 40 + val % 700; yield return [(byte)(0x80 | voice << 5 | p & 15), (byte)(p >> 4)]; }
        else if (op == 1) yield return [Vol(voice, rng.Next(0, 12))];
        else if (op == 2) yield return [(byte)(0xE0 | rng.Next(0, 8))];
        else if (voice < 3) { int p = 100 + val % 500; yield return [(byte)(0x80 | voice << 5 | p & 15), (byte)(p >> 4)]; }
    }

    private static PsgRig RunPsg(int frames, Func<int, bool> keep, int stemMask, int stereo = 0xFF, int enableAt = 0, int seed = 7)
    {
        var r = new PsgRig(); var rng = new Random(seed);
        r.Unit.WriteStereo((byte)stereo);
        if (stemMask != 0 && enableAt == 0) r.Unit.StemMask = stemMask;
        for (int f = 0; f < frames; f++)
        {
            if (stemMask != 0 && enableAt > 0 && f == enableAt) r.Unit.StemMask = stemMask;
            foreach (var w in PsgScript(f, rng, keep)) foreach (byte b in w) r.Unit.WritePsg(b);
            r.Frame();
        }
        return r;
    }

    private static void PsgSum()
    {
        var r = RunPsg(90, _ => true, 15);
        var sum = Sum(r.StemData.Take(4));
        double d = MaxDiffLsb(sum, r.Mix);
        Check($"the four PSG stems add up to the mix over 1.5 s ({r.Mix.Count} frames): largest difference {d:F2} of the mix's 16-bit steps (limit 1.5: its samples are rounded to 16 bits)", r.Mix.Count > 60000 && d <= 1.5);
        Check("... and each stem has exactly the mix's number of frames", r.StemData.Take(4).All(s => s.Count == r.Mix.Count), string.Join("/", r.StemData.Take(4).Select(s => s.Count)));
        Check($"... every voice is audible in its stem (RMS {string.Join(", ", r.StemData.Take(4).Select(s => Rms(s).ToString("F4")))})", r.StemData.Take(4).All(s => Rms(s) > 0.002));
    }

    private static void PsgIsolation()
    {
        var all = RunPsg(90, _ => true, 15);
        for (int v = 0; v < 4; v++)
        {
            int vv = v;
            var solo = RunPsg(90, k => k == vv, 0);         // the same script with the other voices' volumes forced off, no stems
            double d = MaxDiffLsb(all.StemData[v], solo.Mix);
            Check($"{SegaStems.PsgNames[v]}: its stem equals the mix of the same script with every other voice silenced (largest difference {d:F2} steps, limit 1.5)", d <= 1.5 && Rms(solo.Mix) > 0.002);
        }
    }

    private static void PsgStereo()
    {
        // Game Gear stereo: tone 1 right only, tone 2 left only, tone 3 both, noise left only (mask $E5); a stem is the voice's two sides averaged, as the mix is
        var all = RunPsg(90, _ => true, 15, stereo: 0xE5);
        var sum = Sum(all.StemData.Take(4));
        Check($"with a stereo mask the stems still add up to the mix (largest difference {MaxDiffLsb(sum, all.Mix):F2} steps)", MaxDiffLsb(sum, all.Mix) <= 1.5);
        bool ok = true; var worst = new List<string>();
        for (int v = 0; v < 4; v++)
        {
            int vv = v;
            var solo = RunPsg(90, k => k == vv, 0, stereo: 0xE5);
            double d = MaxDiffLsb(all.StemData[v], solo.Mix);
            if (d > 1.5) { ok = false; worst.Add($"{SegaStems.PsgNames[v]} {d:F2}"); }
        }
        Check("... and each stem equals its voice played alone through the same stereo mask", ok, string.Join(", ", worst));
        var plain = RunPsg(90, _ => true, 15, stereo: 0xFF);
        Check("... a voice panned to one side is half as loud in its stem as the same voice panned to both (the mono mix's own halving)", Rms(all.StemData[0], 20000) < Rms(plain.StemData[0], 20000) * 0.8, $"{Rms(all.StemData[0], 20000):F4} vs {Rms(plain.StemData[0], 20000):F4}");
    }

    private static void PsgMidStream()
    {
        var r = RunPsg(120, _ => true, 15, enableAt: 40);
        int skip = 40 * 735 + 6000;       // ten frames later: a tenth of a second and more
        var sum = Sum(r.StemData.Take(4).Select(s => s)); // stems start at the frame they were asked for: align by the count they have
        int offset = r.Mix.Count - sum.Count;
        double worst = 0;
        for (int i = 14000; i < sum.Count; i++) worst = Math.Max(worst, Math.Abs(sum[i] - r.Mix[i + offset]) * LsbF);
        Check($"stems asked for in the middle of the notes (frame 40 of 120) join the mix: largest difference {worst:F2} steps from 0.32 s later (limit 1.5; the mix's own filter forgets the notes already sounding with a time constant of 32 ms)", worst <= 1.5 && sum.Count > 30000, $"{sum.Count} frames, {offset} before them");
    }

    private static void PsgNoCost()
    {
        var off = RunPsg(60, _ => true, 0); var on = RunPsg(60, _ => true, 15);
        Check("asking for stems does not change the mix: bit-identical samples with them on and off", off.Shorts.SequenceEqual(on.Shorts), $"{off.Shorts.Count} vs {on.Shorts.Count} samples");
        var r = new PsgRig(); var rng = new Random(3); r.Unit.StemMask = 15;
        var writes = Enumerable.Range(0, 80).Select(f => PsgScript(f, rng, _ => true).SelectMany(w => w).ToArray()).ToArray();      // the script's bytes made beforehand: only the unit is measured
        var buf = new short[16384]; var fl = new float[16384];
        for (int f = 0; f < 20; f++)        // warm-up with the very calls that are measured (the first call of a method is compiled, which allocates)
        {
            foreach (byte b in writes[f]) r.Unit.WritePsg(b);
            r.Unit.Run(59736); while (r.Unit.ReadSamples(buf) > 0) { }
            for (int s = 0; s < 4; s++) while (r.Unit.ReadStem(s, fl) > 0) { }
        }
        // steady state: the least over three windows of twenty frames (a method compiled for the second time, as the runtime tiers it up, allocates once, never again)
        long bytes = long.MaxValue;
        for (int w = 0; w < 3; w++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int f = 20 + w * 20; f < 40 + w * 20; f++)
            {
                foreach (byte b in writes[f]) r.Unit.WritePsg(b);
                r.Unit.Run(59736); while (r.Unit.ReadSamples(buf) > 0) { }
                for (int s = 0; s < 4; s++) while (r.Unit.ReadStem(s, fl) > 0) { }
            }
            bytes = Math.Min(bytes, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        Check($"the PSG with all four stems wanted and read allocates {bytes} bytes in a third of a second (limit 0; the least of three windows)", bytes == 0);
        // without anyone reading, the queues are bounded and say what they lost
        var q = new PsgRig(); q.Unit.StemMask = 1;
        for (int f = 0; f < 80; f++) { q.Unit.Run(59736); int n; var b2 = new short[16384]; while ((n = q.Unit.ReadSamples(b2)) > 0) { } }
        Check($"a stem nobody reads keeps its newest frames and counts the rest as dropped ({q.Unit.Psg.VoiceStemDropped(0)} frames)", q.Unit.Psg.VoiceStemDropped(0) > 0);
    }

    // ============================================================================================ the Genesis unit

    private sealed class MdRig : Rig
    {
        public readonly MdSoundUnit Unit;
        public long Master;
        private readonly short[] buf = new short[16384];
        public MdRig(GenesisModel model = GenesisModel.Model1)
        {
            var m = new SegaMachine(SegaTimeline.Genesis(SegaRegion.Ntsc), model, 44100);
            Unit = new MdSoundUnit(m); Unit.Reset();
        }
        public override IVoiceStems Stems => Unit;
        public void Ym(int part, int reg, int val) { Unit.WriteYm(part * 2, (byte)reg, Master); Unit.WriteYm(part * 2 + 1, (byte)val, Master); }
        public void Psg(int b) => Unit.WritePsg((byte)b, Master);
        public void Step(long clocks) { Master += clocks; Unit.RunTo(Master); }
        public void Drain()
        {
            int n; while ((n = Unit.ReadSamples(buf)) > 0) Collect(buf, n);
            GrabStems();
        }
    }

    private static void LoadPatch(MdRig r, int ch, FmPatch p, int pan)
    {
        int part = ch / 3, c = ch % 3;
        for (int n = 0; n < 4; n++)
        {
            var o = p.Operators[n]; int off = c + FmPatchBank.SlotOffset(n);
            r.Ym(part, 0x30 + off, o.Dt << 4 | o.Mul); r.Ym(part, 0x40 + off, o.Tl); r.Ym(part, 0x50 + off, o.Rs << 6 | o.Ar);
            r.Ym(part, 0x60 + off, o.D1r); r.Ym(part, 0x70 + off, o.D2r); r.Ym(part, 0x80 + off, o.Sl << 4 | o.Rr); r.Ym(part, 0x90 + off, 0);
        }
        r.Ym(part, 0xB0 + c, p.Feedback << 3 | p.Algorithm); r.Ym(part, 0xB4 + c, pan);
    }

    private static void Note(MdRig r, int ch, double hz, bool on)
    {
        int part = ch / 3, c = ch % 3;
        if (FmPatchBank.TryPitch(hz, 7_670_453.0, out int blk, out int fnum, out _)) { r.Ym(part, 0xA4 + c, blk << 3 | fnum >> 8); r.Ym(part, 0xA0 + c, fnum & 0xFF); }
        r.Ym(0, 0x28, (on ? 0xF0 : 0) | (ch < 3 ? ch : ch + 1));
    }

    /// <summary>Plays a fixed script on the Genesis unit: FM 1 to 4 on different patches and pans with notes changing, the PSG, and from 0.5 s the DAC (FM 6 turns into it) playing a sine at about 8 kHz.
    /// <paramref name="keepPsg"/> and <paramref name="keepFm"/> choose which voices the script leaves audible (the others get volume 15 / MuteMask, for the isolation runs).</summary>
    private static MdRig RunMd(double seconds, long stemMask, int keepPsg, int keepFm, GenesisModel model = GenesisModel.Model1, double enableAt = 0, int seed = 5)
    {
        var r = new MdRig(model); var rng = new Random(seed);
        double masterHz = SegaTimeline.Genesis(SegaRegion.Ntsc).MasterHz;
        r.Unit.FmMuteMask = ~keepFm & 0x7F;
        bool stemsOn = false;
        if (stemMask != 0 && enableAt == 0) { r.Unit.StemMask = stemMask; stemsOn = true; }      // before the first write: the stems see everything the mix does
        LoadPatch(r, 0, FmPatchBank.Patches[0], 0xC0); LoadPatch(r, 1, FmPatchBank.Patches[5], 0x80); LoadPatch(r, 2, FmPatchBank.Patches[14], 0x40); LoadPatch(r, 3, FmPatchBank.Patches[4], 0xC0); LoadPatch(r, 5, FmPatchBank.Patches[2], 0xC0);
        // PSG: tones and noise
        int[] periods = [254, 338, 508];
        for (int v = 0; v < 3; v++) { r.Psg(0x80 | v << 5 | periods[v] & 15); r.Psg(periods[v] >> 4); }
        r.Psg(0xE5);
        for (int v = 0; v < 4; v++) r.Psg(0x90 | v << 5 | ((keepPsg >> v & 1) != 0 ? 4 + v : 15));
        long step = 2016, total = (long)(seconds * masterHz);
        bool dac = false; int dacPhase = 0;
        double[] notes = [196, 262, 330, 440];
        for (long t = 0, i = 0; t < total; t += step, i++)
        {
            if (!stemsOn && stemMask != 0 && t >= enableAt * masterHz) { r.Unit.StemMask = stemMask; stemsOn = true; }
            if (i == 20) { for (int c = 0; c < 4; c++) Note(r, c, notes[c], true); }
            if (i > 20 && i % 700 == 0) { int c = rng.Next(4); double hz = notes[c] * Math.Pow(2, rng.Next(-5, 8) / 12.0); Note(r, c, hz, rng.Next(5) != 0); }
            if (i % 2900 == 0 && i > 0) { int v = rng.Next(3); int p = 80 + rng.Next(500); r.Psg(0x80 | v << 5 | p & 15); r.Psg(p >> 4); }
            if (!dac && t > 0.5 * masterHz) { r.Ym(0, 0x2B, 0x80); r.Ym(1, 0xB4 + 2, 0xC0); dac = true; }
            if (dac && i % 3 == 0) { r.Ym(0, 0x2A, 128 + (int)(100 * Math.Sin(dacPhase++ * 2 * Math.PI / 20))); }     // about 8.3 kHz writes of a 415 Hz sine
            r.Step(step);
            if (i % 40 == 39) r.Drain();
        }
        r.Drain();
        return r;
    }

    private static void MdSum()
    {
        var r = RunMd(1.5, (1L << SegaStems.MdCount) - 1, 15, 0x7F);
        var sum = Sum(r.StemData.Take(SegaStems.MdCount));
        double d = MaxDiffLsb(sum, r.Mix);
        Check($"the eleven stems (PSG tone 1-3 and noise, FM 1-6, the DAC) add up to the mix over 1.5 s ({r.Mix.Count} frames): largest difference {d:F2} of the mix's 16-bit steps (limit 3: the mix rounds each chip's output)", r.Mix.Count > 60000 && d <= 3.0);
        // the DAC switches on at 0.5 s: FM 6 stops (its offset goes) and the DAC starts; the sum has to follow
        Check($"... across the moment the DAC takes FM 6's place (0.5 s) too: largest difference {MaxDiffLsb(Sum(r.StemData.Take(SegaStems.MdCount)), r.Mix, 21000, 0, 0):F2} steps from 0.48 s on", MaxDiffLsb(Sum(r.StemData.Take(SegaStems.MdCount)), r.Mix, 21000) <= 3.0);
        Check("... each stem has exactly the mix's number of frames", r.StemData.Take(SegaStems.MdCount).All(s => s.Count == r.Mix.Count));
        var rms = r.StemData.Select((s, i) => (i, Rms(s, 30000))).ToList();
        Check($"... the voices that play are in their stems: FM 1-4, the DAC and the PSG tones are audible, FM 5 is silent (RMS {string.Join(" ", rms.Select(x => $"{r.Stems.StemName(x.i)} {x.Item2:F4}"))})",
            new[] { 0, 1, 2, 4, 5, 6, 7, 10 }.All(i => rms[i].Item2 > 0.001) && rms[8].Item2 < 0.0005);
    }

    private static void MdIsolation()
    {
        long all = (1L << SegaStems.MdCount) - 1;
        var full = RunMd(1.5, all, 15, 0x7F);
        var bad = new List<string>(); double worst = 0;
        for (int s = 0; s < SegaStems.MdCount; s++)
        {
            int psgKeep = s < 4 ? 1 << s : 0, fmKeep = s >= 4 ? 1 << (s - 4) : 0;
            var solo = RunMd(1.5, 0, psgKeep, fmKeep);
            // after the first third of a second the constant offsets of the chip's muted channels have settled out of the reference; the tenth of a second after the DAC switch (0.5 s) is left out: the muted reference has FM 6's
            // offset step away there, which belongs to the FM 6 stem, not to the others
            double d = MaxDiffLsb(full.StemData[s], solo.Mix, 15000, 22050, 27500);
            worst = Math.Max(worst, d);
            if (d > 4.0 || Rms(solo.Mix, 15000) < 0.0001 && s != 8 && s != 9) bad.Add($"{full.Stems.StemName(s)} {d:F2} (solo rms {Rms(solo.Mix, 15000):F4})");
        }
        Check($"every stem equals the same script played with every other voice muted (the PSG's volumes, the FM chip's MuteMask): largest difference {worst:F2} steps over all eleven, from 0.34 s on (limit 4: the DAC, a large swing, sits at the chip's ladder steps)", bad.Count == 0, string.Join(", ", bad));
    }

    private static void MdModel2()
    {
        var r = RunMd(1.0, (1L << SegaStems.MdCount) - 1, 15, 0x7F, GenesisModel.Model2);
        var d = MaxDiffLsb(Sum(r.StemData.Take(SegaStems.MdCount)), r.Mix);
        Check($"on the integrated chip (no ladder offsets) the stems add up to the mix too (largest difference {d:F2} steps)", d <= 3.0);
    }

    private static void MdMidStream()
    {
        var r = RunMd(2.0, (1L << SegaStems.MdCount) - 1, 15, 0x7F, enableAt: 0.7);
        var sum = Sum(r.StemData.Take(SegaStems.MdCount));
        int offset = r.Mix.Count - sum.Count; double worst = 0;
        for (int i = 14000; i < sum.Count; i++) worst = Math.Max(worst, Math.Abs(sum[i] - r.Mix[i + offset]) * LsbF);
        Check($"stems asked for at 0.7 s, in the middle of the notes, join the mix: largest difference {worst:F2} steps from 0.32 s later (limit 3)", sum.Count > 30000 && worst <= 3.0, $"{sum.Count} frames");
    }

    private static void MdNoCost()
    {
        var off = RunMd(1.0, 0, 15, 0x7F); var on = RunMd(1.0, (1L << SegaStems.MdCount) - 1, 15, 0x7F);
        Check("asking for stems does not change the mix: bit-identical samples with them on and off", off.Shorts.SequenceEqual(on.Shorts), $"{off.Shorts.Count} vs {on.Shorts.Count} samples");
        var r = new MdRig(); r.Unit.StemMask = (1L << SegaStems.MdCount) - 1;
        LoadPatch(r, 0, FmPatchBank.Patches[0], 0xC0); Note(r, 0, 262, true);
        var buf = new short[16384]; var fl = new float[16384];
        for (int i = 0; i < 300; i++) { r.Step(2016 * 40); r.Unit.ReadSamples(buf); for (int s = 0; s < 11; s++) while (r.Unit.ReadStem(s, fl) > 0) { } }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++)
        {
            if (i % 10 == 0) Note(r, 0, 262 + i, true);
            r.Step(2016 * 40); r.Unit.ReadSamples(buf); for (int s = 0; s < 11; s++) while (r.Unit.ReadStem(s, fl) > 0) { }
        }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check($"{bytes} bytes allocated by 300 steps of the Genesis unit with all eleven stems wanted and read (limit 0)", bytes == 0);
        // the cost: the same second of music with and without the stems
        double Time(long mask)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            RunMd(2.0, mask, 15, 0x7F);
            return sw.Elapsed.TotalSeconds;
        }
        double t0 = Math.Min(Time(0), Time(0)), t1 = Math.Min(Time((1L << SegaStems.MdCount) - 1), Time((1L << SegaStems.MdCount) - 1));
        Console.WriteLine($"        cost of the stems on the Genesis sound unit: {t0 * 1000:F0} ms without, {t1 * 1000:F0} ms with all eleven for 2 s of sound (+{(t1 - t0) / 2 * 100:F1}% of a core, script included)");
        Check("... all eleven stems cost less than a tenth of a core on top of the unit (measured above)", (t1 - t0) / 2.0 < 0.1);
    }

    // ============================================================================================ the Master System FM unit

    private sealed class FmRig : Rig
    {
        public readonly SmsFmSound Unit;
        private readonly short[] buf = new short[16384];
        public FmRig() { Unit = new SmsFmSound(3579545.0, 44100); Unit.WriteAudioControl(1); }
        public override IVoiceStems Stems => Unit;
        public void Reg(int r, int v) { Unit.WriteFmAddress((byte)r); Unit.WriteFmData((byte)v); }
        public void Frame(int clocks = 59736)
        {
            Unit.Run(clocks);
            int n; while ((n = Unit.ReadSamples(buf)) > 0) Collect(buf, n);
            GrabStems();
        }
    }

    /// <summary>A fixed script for the YM2413: six melodic channels on different instruments with notes changing, then rhythm mode (bass drum, snare, tom, cymbal, hi-hat hit in turn) from 0.5 s, plus PSG tones.
    /// <paramref name="keepPsg"/> and <paramref name="keepFm"/> choose the voices left audible: the PSG's bits 0 to 3 (volume 15 for the others), the FM chip's bits 0 to 13 (its <c>Mask</c> for the others).</summary>
    private static FmRig RunFm(int frames, long stemMask, int keepPsg, int keepFm, int enableAt = 0, int audioControl = 1, int seed = 9)
    {
        var r = new FmRig(); var rng = new Random(seed);
        r.Unit.WriteAudioControl((byte)audioControl);
        r.Unit.Fm.Mask = (uint)(~keepFm & 0x3FFF);
        bool stemsOn = false;
        if (stemMask != 0 && enableAt == 0) { r.Unit.StemMask = stemMask; stemsOn = true; }       // before the first write: the stems see everything the mix does
        for (int rr = 0; rr < 8; rr++) r.Reg(rr, new byte[] { 0x21, 0x21, 0x14, 0x07, 0xF0, 0xF0, 0x08, 0x08 }[rr]);        // the user instrument (a bright lead)
        int[] instruments = [0, 1, 4, 5, 7, 9];                        // the user instrument and sustained ROM ones: violin, flute, clarinet, trumpet, horn... all hold their level
        for (int c = 0; c < 6; c++)
        {
            r.Reg(0x30 + c, instruments[c] << 4 | (3 + c % 3));
            int fn = 150 + c * 30; r.Reg(0x10 + c, fn & 0xFF); r.Reg(0x20 + c, 0x10 | 3 << 1 | fn >> 8);
        }
        int[] periods = [254, 338, 508];
        for (int v = 0; v < 3; v++) { r.Unit.WritePsg((byte)(0x80 | v << 5 | periods[v] & 15)); r.Unit.WritePsg((byte)(periods[v] >> 4)); }
        r.Unit.WritePsg(0xE5);
        for (int v = 0; v < 4; v++) r.Unit.WritePsg((byte)(0x90 | v << 5 | ((keepPsg >> v & 1) != 0 ? 4 + v : 15)));
        bool rhythm = false;
        for (int f = 0; f < frames; f++)
        {
            if (!stemsOn && stemMask != 0 && f >= enableAt) { r.Unit.StemMask = stemMask; stemsOn = true; }
            if (f % 9 == 1)
            {
                int c = rng.Next(6), fnum = 120 + rng.Next(200), blk = 2 + rng.Next(3);
                r.Reg(0x10 + c, fnum & 0xFF); r.Reg(0x20 + c, (rng.Next(6) == 0 ? 0 : 0x10) | blk << 1 | fnum >> 8);
            }
            if (f == 30 && !rhythm) { rhythm = true; r.Reg(0x16, 0x20); r.Reg(0x17, 0x50); r.Reg(0x18, 0xC0); r.Reg(0x26, 0x05); r.Reg(0x27, 0x05); r.Reg(0x28, 0x01); r.Reg(0x36, 0x03); r.Reg(0x37, 0x33); r.Reg(0x38, 0x33); }
            if (rhythm && f % 6 == 0) r.Reg(0x0E, 0x20 | 1 << (f / 6 % 5));
            if (rhythm && f % 6 == 3) r.Reg(0x0E, 0x20);
            if (f % 7 == 2) { int v = rng.Next(3); int p = 90 + rng.Next(500); r.Unit.WritePsg((byte)(0x80 | v << 5 | p & 15)); r.Unit.WritePsg((byte)(p >> 4)); }
            r.Frame();
        }
        return r;
    }

    private static void FmSum()
    {
        long all = (1L << SegaStems.SmsFmCount) - 1;
        var r = RunFm(90, all, 15, 0x3FFF);
        var sum = Sum(r.StemData.Take(SegaStems.SmsFmCount));
        double d = MaxDiffLsb(sum, r.Mix);
        Check($"the eighteen stems (PSG tone 1-3 and noise, FM 1-9, the five rhythm voices) add up to the mix over 1.5 s ({r.Mix.Count} frames): largest difference {d:F2} of the mix's 16-bit steps (limit 8: the converter rounds each voice's output to 16 bits)", r.Mix.Count > 60000 && d <= 8.0);
        Check("... each stem has exactly the mix's number of frames", r.StemData.Take(SegaStems.SmsFmCount).All(s => s.Count == r.Mix.Count));
        var rms = r.StemData.Select((s, i) => (i, Rms(s, 30000))).ToList();
        Check($"... voices that play are in their stems: FM 1-6 and the PSG tones; the rhythm voices sound (RMS {string.Join(" ", rms.Select(x => $"{r.Stems.StemName(x.i)} {x.Item2:F4}"))})",
            new[] { 0, 1, 2, 4, 5, 6, 7, 8, 9 }.All(i => rms[i].Item2 > 0.0005) && rms[10].Item2 < 0.0002 && rms.Skip(SegaStems.SmsRhythm1).Count(x => x.Item2 > 0.0005) >= 3);
        // the unit's own names
        Check("the stems are named: PSG tone 1, FM 1 ... FM 9, Bass drum, Hi-hat, Snare, Tom, Cymbal", r.Stems.StemName(0) == "PSG tone 1" && r.Stems.StemName(4) == "FM 1" && r.Stems.StemName(12) == "FM 9" && r.Stems.StemName(13) == "Bass drum" && r.Stems.StemName(17) == "Cymbal");
    }

    /// <summary>The bit of <c>Ym2413.Mask</c> that silences voice <paramref name="v"/> (the chip's output order: nine melodic, then bass drum, hi-hat, snare, tom, cymbal; its mask bits for the drums are in another order).</summary>
    private static int MaskBitOfVoice(int v) => v < 9 ? v : v switch { 9 => 13, 10 => 9, 11 => 12, 12 => 11, _ => 10 };

    private static void FmIsolation()
    {
        long all = (1L << SegaStems.SmsFmCount) - 1;
        var full = RunFm(90, all, 15, 0x3FFF);
        var bad = new List<string>(); double worst = 0;
        for (int s = 0; s < SegaStems.SmsFmCount; s++)
        {
            int psgKeep = s < 4 ? 1 << s : 0, fmKeep = s >= 4 ? 1 << MaskBitOfVoice(s - 4) : 0;
            var solo = RunFm(90, 0, psgKeep, fmKeep);
            double d = MaxDiffLsb(full.StemData[s], solo.Mix, 8000);
            worst = Math.Max(worst, d);
            if (d > 8.0) bad.Add($"{full.Stems.StemName(s)} {d:F2}");
        }
        Check($"every stem equals the same script played with every other voice muted (the PSG's volumes, the YM2413's Mask): largest difference {worst:F2} steps over all eighteen (limit 8)", bad.Count == 0, string.Join(", ", bad));
    }

    private static void FmGating()
    {
        // the audio control register ($F2): bit 0 turns the FM on, bit 1 mutes the PSG; the stems follow the mix
        var fmOff = RunFm(60, (1L << SegaStems.SmsFmCount) - 1, 15, 0x3FFF, audioControl: 0);
        Check($"with the FM chip switched off (audio control 0) the FM stems are silent and the PSG's are not (FM 1 rms {Rms(fmOff.StemData[4]):F5}, PSG tone 1 {Rms(fmOff.StemData[0]):F4})", Rms(fmOff.StemData[4]) < 1e-6 && Rms(fmOff.StemData[0]) > 0.001 && MaxDiffLsb(Sum(fmOff.StemData.Take(SegaStems.SmsFmCount)), fmOff.Mix) <= 8.0);
        var psgOff = RunFm(60, (1L << SegaStems.SmsFmCount) - 1, 15, 0x3FFF, audioControl: 3);
        Check($"with the PSG muted (audio control 3) the PSG stems are silent (tone 1 rms {Rms(psgOff.StemData[0]):F5}) and the FM ones are not (FM 1 {Rms(psgOff.StemData[4], 20000):F4})", Rms(psgOff.StemData[0]) < 1e-6 && Rms(psgOff.StemData[4], 20000) > 0.001 && MaxDiffLsb(Sum(psgOff.StemData.Take(SegaStems.SmsFmCount)), psgOff.Mix) <= 8.0);
    }

    private static void FmMidStream()
    {
        var r = RunFm(120, (1L << SegaStems.SmsFmCount) - 1, 15, 0x3FFF, enableAt: 45);
        var sum = Sum(r.StemData.Take(SegaStems.SmsFmCount));
        int offset = r.Mix.Count - sum.Count; double worst = 0;
        for (int i = 14000; i < sum.Count; i++) worst = Math.Max(worst, Math.Abs(sum[i] - r.Mix[i + offset]) * LsbF);
        Check($"stems asked for in the middle of the notes (frame 45 of 120) join the mix: largest difference {worst:F2} steps from 0.32 s later (limit 8)", sum.Count > 30000 && worst <= 8.0, $"{sum.Count} frames");
    }

    private static void FmNoCost()
    {
        var off = RunFm(60, 0, 15, 0x3FFF); var on = RunFm(60, (1L << SegaStems.SmsFmCount) - 1, 15, 0x3FFF);
        Check("asking for stems does not change the mix: bit-identical samples with them on and off", off.Shorts.SequenceEqual(on.Shorts), $"{off.Shorts.Count} vs {on.Shorts.Count} samples");
        var r = new FmRig(); r.Unit.StemMask = (1L << SegaStems.SmsFmCount) - 1;
        r.Reg(0x30, 0x13); r.Reg(0x10, 0xA0); r.Reg(0x20, 0x16);
        var buf = new short[16384]; var fl = new float[16384];
        for (int i = 0; i < 40; i++) { r.Unit.Run(59736); while (r.Unit.ReadSamples(buf) > 0) { } for (int s = 0; s < 18; s++) while (r.Unit.ReadStem(s, fl) > 0) { } }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 120; i++)
        {
            if (i % 10 == 0) { r.Reg(0x10, 0x80 + i); r.Reg(0x0E, 0x20 | (i / 10 & 31)); }
            r.Unit.Run(59736); while (r.Unit.ReadSamples(buf) > 0) { } for (int s = 0; s < 18; s++) while (r.Unit.ReadStem(s, fl) > 0) { }
        }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check($"{bytes} bytes allocated by 2 s of the Master System FM unit with all eighteen stems wanted and read (limit 0)", bytes == 0);
        double Time(long mask)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            RunFm(120, mask, 15, 0x3FFF);
            return sw.Elapsed.TotalSeconds;
        }
        double t0 = Math.Min(Time(0), Time(0)), t1 = Math.Min(Time((1L << SegaStems.SmsFmCount) - 1), Time((1L << SegaStems.SmsFmCount) - 1));
        Console.WriteLine($"        cost of the stems on the Master System FM unit: {t0 * 1000:F0} ms without, {t1 * 1000:F0} ms with all eighteen for 2 s of sound (+{(t1 - t0) / 2 * 100:F1}% of a core, script included)");
        Check("... all eighteen stems cost less than 15% of a core on top of the unit (measured above; the plugin pays for the ones an instance has taken, not for all of them)", (t1 - t0) / 2.0 < 0.15);
    }

    // ============================================================================================ real games

    private static void GenesisGame(string? rom)
    {
        if (string.IsNullOrEmpty(rom) || !File.Exists(rom)) { Console.WriteLine("        (no Genesis ROM: the game run is skipped)"); return; }
        var data = File.ReadAllBytes(rom);
        var session = new MdSession(data, "M68K", "MD", "MD");
        var stems = (IVoiceStems)session.Board.Sound;
        stems.StemMask = (1L << stems.StemCount) - 1;
        var mix = new List<float>(); var sd = Enumerable.Range(0, stems.StemCount).Select(_ => new List<float>()).ToArray();
        var buf = new short[16384]; var fl = new float[16384];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int frames = 60 * 20;
        for (int f = 0; f < frames; f++)
        {
            if (f == 120) session.SetPad(0, PadButtons.Start);
            if (f == 125) session.SetPad(0, 0);
            if (f > 400) session.SetPad(0, f % 90 < 60 ? PadButtons.Right : 0);
            session.RunFrame();
            int n; while ((n = session.ReadSamples(buf)) > 0) for (int i = 0; i + 1 < n; i += 2) mix.Add((buf[i] + buf[i + 1]) * (0.5f / 32768f));
            for (int s = 0; s < stems.StemCount; s++) { int k; while ((k = stems.ReadStem(s, fl)) > 0) for (int i = 0; i < k; i++) sd[s].Add(fl[i]); }
        }
        sw.Stop();
        var sum = Sum(sd);
        double d = MaxDiffLsb(sum, mix);
        var rms = sd.Select((s, i) => $"{stems.StemName(i)} {Rms(s):F3}");
        Check($"{Path.GetFileName(rom)} for {frames} frames ({mix.Count} sound frames): the eleven stems add up to the game's mix, largest difference {d:F1} 16-bit steps (limit 6; the mix's two chips round separately)", mix.Count > 800000 && d <= 6.0, $"stem RMS: {string.Join(", ", rms)}");
        Check($"... at least three different stems carry sound (FM and PSG voices are separated: {sd.Count(s => Rms(s) > 0.003)} of 11 above 0.003 RMS)", sd.Count(s => Rms(s) > 0.003) >= 3);
        Console.WriteLine($"        {frames} frames with all stems wanted and read took {sw.Elapsed.TotalSeconds:F1} s ({frames / 60.0 / sw.Elapsed.TotalSeconds:F1}x real time on this machine, shared)");
    }

    private static void GameGearGame(string? rom)
    {
        if (string.IsNullOrEmpty(rom) || !File.Exists(rom)) { Console.WriteLine("        (no Game Gear ROM: the game run is skipped)"); return; }
        var data = File.ReadAllBytes(rom);
        var session = new GgSession(data, "Z80", "GG", "GG", Path.GetFileName(rom));
        var stems = (IVoiceStems)session.Board.Sound;
        stems.StemMask = 15;
        var mix = new List<float>(); var sd = Enumerable.Range(0, 4).Select(_ => new List<float>()).ToArray();
        var buf = new short[16384]; var fl = new float[16384];
        for (int f = 0; f < 60 * 15; f++)
        {
            if (f == 200) session.SetPad(0, PadButtons.Start);
            if (f == 205) session.SetPad(0, 0);
            session.RunFrame();
            int n; while ((n = session.ReadSamples(buf)) > 0) for (int i = 0; i + 1 < n; i += 2) mix.Add((buf[i] + buf[i + 1]) * (0.5f / 32768f));
            for (int s = 0; s < 4; s++) { int k; while ((k = stems.ReadStem(s, fl)) > 0) for (int i = 0; i < k; i++) sd[s].Add(fl[i]); }
        }
        double d = MaxDiffLsb(Sum(sd), mix);
        Check($"{Path.GetFileName(rom)} (Game Gear, stereo) for 15 s: the four PSG stems add up to the game's mono mix, largest difference {d:F2} steps (limit 1.5); RMS {string.Join(", ", sd.Select(s => Rms(s).ToString("F3")))}", mix.Count > 600000 && d <= 1.5);
    }
}
