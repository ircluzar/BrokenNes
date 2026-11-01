using System;
using System.Collections.Generic;
using System.Linq;
using NesEmulator.Sega;

namespace BrokenNes.Workshop.Sega;

/// <summary>One operator's registers (algorithm order, operator 1 to 4).</summary>
internal struct OpPatch
{
    public int Mul, Dt, Tl, Ks, Ar, D1r, D2r, Sl, Rr, Ssg, Am;
    public static OpPatch Default => new() { Mul = 1, Dt = 0, Tl = 0, Ks = 0, Ar = 31, D1r = 0, D2r = 0, Sl = 0, Rr = 15, Ssg = 0, Am = 0 };
    public OpPatch With(Func<OpPatch, OpPatch> f) => f(this);
}

/// <summary>
/// The scripted cases of <c>--ym-oracle-test</c> that run against Nuked-OPN2: the same register writes at the same chip clocks to our chip (with its slot pipeline, see <see cref="Ym2612.SlotPipeline"/>)
/// and to the oracle, compared sample by sample. What "match" means here, stated once:
/// <list type="bullet">
/// <item>The oracle is time-multiplexed and pipelined, so its output lags ours by a constant number of samples (4 for channels 1 to 5, 3 for channel 6 as its multiplexer slot falls in the logged window);
/// every comparison finds that shift by itself and the lines print it.</item>
/// <item>Channel outputs are compared in the oracle's own unit, the 9-bit value its channel accumulator holds (<c>ch_out</c>), against ours before panning: <b>exact</b> means the same integer.</item>
/// <item>Where a case cannot be exact (the oracle applies a register write to each of its 24 operator slots at that slot's own moment inside the 24-clock cycle, so a note-on, a rate change or a
/// frequency change can land a sample apart for different operators; ours applies it to all operators at once) the bound is stated in the case with the numbers it measured.</item>
/// <item>The mixed output of several channels is compared only where it can be: the oracle outputs the six channels one after another within a sample while we sum them at once, so the sum of a note that
/// changes differs by the skew between channels. Single channels, the DAC, the pans and the ladder are compared on the mix, exactly.</item>
/// </list>
/// </summary>
internal static class NukedCases
{
    /// <summary>How well two sample streams agree after the best whole-sample alignment.</summary>
    internal readonly record struct Cmp(int Shift, int Compared, int Exact, int MaxAbs, double RmsDiff, double RmsSignal, int[]? WrongAt = null)
    {
        public double SnrDb => RmsDiff == 0 ? double.PositiveInfinity : 20 * Math.Log10(Math.Max(RmsSignal, 1e-9) / RmsDiff);
        public int Wrong => Compared - Exact;
        public double ExactPercent => Compared == 0 ? 100 : 100.0 * Exact / Compared;
        public override string ToString() => $"{Exact}/{Compared} exact, max {MaxAbs}, SNR {(double.IsInfinity(SnrDb) ? "inf" : SnrDb.ToString("F1"))} dB, shift {Shift:+0;-0;0}";
    }

    /// <summary>Compares <paramref name="mine"/> with <paramref name="nuked"/> over [from, to) at the whole-sample shift (of Nuked relative to ours, within +-maxShift) with the smallest squared error.</summary>
    internal static Cmp Compare(int[] nuked, int[] mine, int from, int to, int maxShift = 3)
    {
        Cmp best = default; double bestErr = double.MaxValue;
        for (int shift = -maxShift; shift <= maxShift; shift++)
        {
            double err = 0, sig = 0; int exact = 0, max = 0, n = 0;
            var wrong = new List<int>();
            for (int i = from; i < to; i++)
            {
                int j = i + shift;
                if (j < 0 || j >= nuked.Length) continue;
                int d = mine[i] - nuked[j];
                err += (double)d * d; sig += (double)mine[i] * mine[i]; n++;
                if (d == 0) exact++; else if (wrong.Count < 12) wrong.Add(i);
                max = Math.Max(max, Math.Abs(d));
            }
            if (n == 0) continue;
            if (err < bestErr) { bestErr = err; best = new Cmp(shift, n, exact, max, Math.Sqrt(err / n), Math.Sqrt(sig / n), wrong.ToArray()); }
        }
        return best;
    }

    // ---------------------------------------------------------------------------------------------- patch helpers

    private static readonly int[] OpOffset = { 0, 8, 4, 12 };      // algorithm operator 1, 2, 3, 4 -> register offset within the channel's block

    internal static void Channel(YmScript s, int ch, int alg, int fb, OpPatch[] ops, int fnum, int block, int pan = 3, int ams = 0, int pms = 0)
    {
        int part = ch / 3, c = ch % 3;
        for (int n = 0; n < 4; n++)
        {
            int off = c + OpOffset[n];
            var o = ops[n];
            s.Reg(part, 0x30 + off, (o.Dt & 7) << 4 | (o.Mul & 15));
            s.Reg(part, 0x40 + off, o.Tl & 127);
            s.Reg(part, 0x50 + off, (o.Ks & 3) << 6 | (o.Ar & 31));
            s.Reg(part, 0x60 + off, (o.Am & 1) << 7 | (o.D1r & 31));
            s.Reg(part, 0x70 + off, o.D2r & 31);
            s.Reg(part, 0x80 + off, (o.Sl & 15) << 4 | (o.Rr & 15));
            s.Reg(part, 0x90 + off, o.Ssg & 15);
        }
        s.Reg(part, 0xB0 + c, (fb & 7) << 3 | (alg & 7));
        s.Reg(part, 0xB4 + c, (pan & 3) << 6 | (ams & 3) << 4 | (pms & 7));
        SetFreq(s, ch, fnum, block);
    }

    internal static void SetFreq(YmScript s, int ch, int fnum, int block)
    {
        int part = ch / 3, c = ch % 3;
        s.Reg(part, 0xA4 + c, (block & 7) << 3 | (fnum >> 8 & 7));
        s.Reg(part, 0xA0 + c, fnum & 0xFF);
    }

    internal static void Key(YmScript s, int ch, int opMask) => s.Reg(0, 0x28, (opMask & 15) << 4 | (ch < 3 ? ch : ch + 1));

    internal static OpPatch[] Ops(Func<int, OpPatch> f) => new[] { f(0), f(1), f(2), f(3) };

    private static Random Rng(int seed) => new(seed);

    // ---------------------------------------------------------------------------------------------- running a scenario on both chips

    /// <summary>The result of one scenario: per-channel comparisons of the 9-bit channel output and the mixed output.</summary>
    internal sealed class Outcome
    {
        public Cmp[] Channel = new Cmp[6];
        public Cmp MixLeft, MixRight;
        public bool[] Used = new bool[6];
        public long Exact => Enumerable.Range(0, 6).Where(c => Used[c]).Sum(c => (long)Channel[c].Exact);
        public long Compared => Enumerable.Range(0, 6).Where(c => Used[c]).Sum(c => (long)Channel[c].Compared);
        public int MaxAbs => Enumerable.Range(0, 6).Where(c => Used[c]).Max(c => Channel[c].MaxAbs);
        public double MinExactPercent => Enumerable.Range(0, 6).Where(c => Used[c]).Min(c => Channel[c].ExactPercent);
        public double MinSnrDb => Enumerable.Range(0, 6).Where(c => Used[c]).Min(c => Channel[c].SnrDb);
        /// <summary>The signal-to-error ratio over all the channels together (summed energies).</summary>
        public double OverallSnrDb
        {
            get
            {
                double sig = 0, err = 0;
                for (int c = 0; c < 6; c++) if (Used[c]) { sig += Channel[c].RmsSignal * Channel[c].RmsSignal * Channel[c].Compared; err += Channel[c].RmsDiff * Channel[c].RmsDiff * Channel[c].Compared; }
                return err == 0 ? double.PositiveInfinity : 10 * Math.Log10(sig / err);
            }
        }
    }

    /// <summary>Runs the script on our chip and on Nuked, and compares channels <paramref name="channels"/> from sample <paramref name="from"/> on.</summary>
    internal static Outcome Scenario(YmScript s, int samples, bool ladder, int from, int channels = 0x3F, int shiftRange = 6, bool pipeline = true)
    {
        var chan = new int[6][];
        for (int c = 0; c < 6; c++) chan[c] = new int[samples];
        var buf = new int[6];
        var mine = s.RunMine(ladder, samples, setup: c => { c.SlotPipeline = pipeline; c.DebugChannelOutputs = buf; }, perSample: (c, i) => { for (int k = 0; k < 6; k++) chan[k][i] = buf[k]; });
        var nk = s.RunNuked(ladder ? 1 : 0, samples, state: true);
        var o = new Outcome();
        for (int c = 0; c < 6; c++)
        {
            if ((channels >> c & 1) == 0) continue;
            var theirs = nk.ChOut.Select(x => (int)x[c]).ToArray();
            o.Channel[c] = Compare(theirs, chan[c], from, samples - 24, shiftRange);
            o.Used[c] = true;
        }
        o.MixLeft = Compare(nk.AccLeft.Select(a => YmScript.Scale(ladder, a)).ToArray(), mine.Left, from, samples - 24, shiftRange);
        o.MixRight = Compare(nk.AccRight.Select(a => YmScript.Scale(ladder, a)).ToArray(), mine.Right, from, samples - 24, shiftRange);
        return o;
    }

    private static string Summ(Outcome o)
    {
        var parts = new List<string>();
        for (int c = 0; c < 6; c++) if (o.Used[c]) parts.Add($"ch{c + 1} {o.Channel[c].Exact}/{o.Channel[c].Compared}{(o.Channel[c].Wrong > 0 ? $" (max {o.Channel[c].MaxAbs}, {o.Channel[c].SnrDb:F0} dB)" : "")}");
        return string.Join(", ", parts);
    }

    // ---------------------------------------------------------------------------------------------- the cases

    private static Action<string, bool, string> check = (_, _, _) => { };

    public static void Run(Action<string, bool, string> report)
    {
        check = report;
        Algorithms();
        DetuneAndMultiple();
        Lfo();
        EnvelopeRates();
        EnvelopeScenarios();
        SsgEg();
        ChannelThree();
        Csm();
        TimersAndStatus();
        MixDacAndLadder();
        WithoutThePipeline();
    }

    // ---------------------------------------------------------------- algorithms, feedback

    private static void Algorithms()
    {
        Console.WriteLine("-- algorithms and feedback: all 8 algorithms with feedback 1, 4 and 7, six channels at a time, 1600 samples each");
        var fbs = new[] { 1, 4, 7 };
        int combo = 0;
        for (int sc = 0; sc < 4; sc++)
        {
            var s = new YmScript();
            var info = new List<string>();
            for (int ch = 0; ch < 6; ch++, combo++)
            {
                int alg = combo % 8, fb = fbs[combo / 8];
                var ops = Ops(n => OpPatch.Default.With(o => { o.Tl = alg == 7 ? 22 + 3 * n : (n == 3 ? 6 : 24 + n); o.Mul = n + 1 + (ch & 1); return o; }));
                Channel(s, ch, alg, fb, ops, 0x280 + 0x30 * ch, 3 + ch % 3);
                info.Add($"alg{alg}/fb{fb}");
            }
            s.Align(2);
            long on = s.T / YmScript.Sample;
            for (int ch = 0; ch < 6; ch++) Key(s, ch, 15);
            var o2 = Scenario(s, 1600, false, (int)on + 6);
            check($"algorithms/feedback, channels {string.Join(" ", info)}: every channel exact for all but at most 1% of its samples", o2.MinExactPercent >= 99.0 && o2.MaxAbs <= 8,
                $"{o2.Exact}/{o2.Compared} samples exact over the six channels, worst single sample off by {o2.MaxAbs} (9-bit steps); {Summ(o2)}");
        }
    }

    private static void DetuneAndMultiple()
    {
        Console.WriteLine("-- detune and multiple: every operator its own, all key codes (algorithm 7, six channels at a time)");
        for (int seed = 1; seed <= 4; seed++)
        {
            var rng = Rng(seed);
            var s = new YmScript();
            for (int ch = 0; ch < 6; ch++)
                Channel(s, ch, 7, 0, Ops(n => OpPatch.Default.With(o => { o.Tl = 20 + rng.Next(0, 12); o.Mul = rng.Next(0, 16); o.Dt = rng.Next(0, 8); o.Ks = rng.Next(0, 4); return o; })), 0x100 + rng.Next(0, 0x600), rng.Next(0, 8));
            s.Align(2);
            long on = s.T / YmScript.Sample;
            for (int ch = 0; ch < 6; ch++) Key(s, ch, 15);
            var o2 = Scenario(s, 1500, false, (int)on + 12);
            check($"detune/multiple, seed {seed}: all six channels identical to the oracle", o2.Exact == o2.Compared, $"{o2.Exact}/{o2.Compared}");
        }
    }

    // ---------------------------------------------------------------- LFO

    private static void Lfo()
    {
        Console.WriteLine("-- LFO: amplitude and vibrato, every operator slot on its own (one audible operator at a time), then random patches at every LFO rate");
        foreach (var (what, ams, pms, am) in new[] { ("amplitude modulation (sensitivity 3)", 3, 0, 1), ("vibrato (sensitivity 7)", 0, 7, 0) })
        {
            long exact = 0, total = 0;
            for (int audible = 0; audible < 4; audible++)
            {
                var rng = Rng(200 + audible);
                var s = new YmScript();
                s.Reg(0, 0x22, 8 | 6);
                for (int ch = 0; ch < 6; ch++)
                    Channel(s, ch, 7, 0, Ops(n => OpPatch.Default.With(o => { o.Tl = n == audible ? 22 : 127; o.Mul = 1 + rng.Next(0, 3); o.Am = am; o.Ar = 31; return o; })), 0x100 + rng.Next(0, 0x600), rng.Next(1, 6), 3, ams, pms);
                s.Align(2);
                long on = s.T / YmScript.Sample;
                for (int ch = 0; ch < 6; ch++) Key(s, ch, 15);
                var o2 = Scenario(s, 6000, false, (int)on + 12);
                exact += o2.Exact; total += o2.Compared;
            }
            check($"LFO {what}: all 24 operator slots, LFO rate 6, 6000 samples each", exact == total, $"{exact}/{total} samples exact");
        }
        long ex = 0, tot = 0; int worst = 0;
        for (int rate = 0; rate < 8; rate++)
        {
            var rng = Rng(rate + 1);
            var s = new YmScript();
            s.Reg(0, 0x22, 8 | rate);
            for (int ch = 0; ch < 6; ch++)
                Channel(s, ch, rng.Next(0, 8), rng.Next(0, 3), Ops(n => OpPatch.Default.With(o => { o.Tl = 20 + rng.Next(0, 8); o.Mul = 1 + rng.Next(0, 3); o.Am = rng.Next(0, 2); o.Ar = 31; return o; })), 0x100 + rng.Next(0, 0x600), rng.Next(1, 6), 3, 1 + rate % 3, rate);
            s.Align(2);
            long on = s.T / YmScript.Sample;
            for (int ch = 0; ch < 6; ch++) Key(s, ch, 15);
            var o2 = Scenario(s, 24000, false, (int)on + 12);
            ex += o2.Exact; tot += o2.Compared; worst = Math.Max(worst, o2.MaxAbs);
        }
        check("LFO, random patches at all 8 LFO rates, 24000 samples each, six channels", ex == tot, $"{ex}/{tot} samples exact");
    }

    // ---------------------------------------------------------------- envelope

    /// <summary>Every envelope rate on its own, the 24 operators of a run each given one: the attenuation of every operator against the oracle's envelope level (eg_level), after its channel's own key-on.</summary>
    private static void EnvelopeRates()
    {
        Console.WriteLine("-- envelope generator: every attack, decay, sustain and release rate (0 to 63, with key scaling for the odd ones), operator by operator");
        int[] kOrder = { 0, 2, 1, 3 };
        foreach (var kind in new[] { "attack", "decay", "sustain", "release" })
        {
            int rows = 0, fullyEqual = 0; long compared = 0, equal = 0; var worst = new List<string>();
            foreach (int keycodeOffset in new[] { 0, 1 })
            {
                const int samples = 1500;
                var wanted = new List<int>();
                for (int r = 2; r < 64; r++)
                {
                    if (r < keycodeOffset) continue;
                    if (kind == "release" ? (r - keycodeOffset) % 4 != 2 : (r - keycodeOffset) % 2 != 0) continue;
                    wanted.Add(r);
                }
                for (int batch = 0; batch * 24 < wanted.Count; batch++)
                {
                    var s = new YmScript();
                    var assigned = new List<(int ch, int n, int rate)>();
                    for (int ch = 0; ch < 6; ch++)
                    {
                        var ops = new OpPatch[4];
                        for (int n = 0; n < 4; n++)
                        {
                            int idx = batch * 24 + ch * 4 + n;
                            int rate = idx < wanted.Count ? wanted[idx] : -1;
                            var o = OpPatch.Default; o.Tl = 40; o.Ks = keycodeOffset > 0 ? 3 : 0;
                            if (rate >= 0)
                            {
                                int baseRate = rate - keycodeOffset;
                                switch (kind)
                                {
                                    case "attack": o.Ar = baseRate / 2; break;
                                    case "decay": o.Sl = 15; o.D1r = baseRate / 2; break;
                                    case "sustain": o.Sl = 0; o.D2r = baseRate / 2; break;
                                    case "release": o.Rr = Math.Max(0, (baseRate - 2) / 4); break;
                                }
                            }
                            if (kind == "release") { o.Ar = 31; o.D1r = 0; o.Sl = 0; o.D2r = 0; }
                            ops[n] = o;
                            assigned.Add((ch, n, rate));
                        }
                        Channel(s, ch, 7, 0, ops, keycodeOffset > 0 ? 0x380 : 0x080, 0);
                    }
                    s.Align(2);
                    long on = s.T / YmScript.Sample;
                    var keyAt = new long[6];
                    for (int ch = 0; ch < 6; ch++) { keyAt[ch] = s.T / YmScript.Sample; Key(s, ch, 15); }
                    long offAt = on + 70;
                    if (kind == "release") { s.T = offAt * YmScript.Sample; for (int ch = 0; ch < 6; ch++) Key(s, ch, 0); }
                    var mineEnv = new int[samples][];
                    s.RunMine(false, samples, perSample: (c, i) =>
                    {
                        var e = new int[24];
                        for (int ch = 0; ch < 6; ch++) for (int n = 0; n < 4; n++) e[ch * 4 + n] = c.DebugEnvelope(ch, n);
                        mineEnv[i] = e;
                    });
                    var nk = s.RunNuked(0, samples, state: true);
                    foreach (var (ch, n, rate) in assigned)
                    {
                        if (rate < 0) continue;
                        int slot = 6 * kOrder[n] + ch;
                        int from = (int)(kind == "release" ? offAt + 14 : keyAt[ch] + 14);
                        int best = -1;
                        for (int shift = -4; shift <= 8; shift++)
                        {
                            int match = 0;
                            for (int i = from; i < samples - 10; i++) if (mineEnv[i][ch * 4 + n] == nk.EgLevel[i + shift][slot]) match++;
                            best = Math.Max(best, match);
                        }
                        int total = samples - 10 - from;
                        rows++; compared += total; equal += best;
                        if (best == total) fullyEqual++;
                        else worst.Add($"{rate}:{100.0 * best / total:F0}%");
                    }
                }
            }
            check($"envelope {kind} rates: at least 90% of all attenuation samples equal", compared > 0 && 100.0 * equal / compared >= 90,
                $"{fullyEqual} of {rows} rate/operator runs entirely equal; {100.0 * equal / compared:F2}% of {compared:N0} samples equal{(worst.Count > 0 ? "; differing rates (rate:% equal): " + string.Join(" ", worst) : "")}");
        }
    }

    private static Outcome RandomScenario(int seed, int samples, Func<Random, int, OpPatch[]> patch, Func<Random, int, (int alg, int fb)> algFb, int keyOffAfter = 0, int keyOnAgainAfter = 0)
    {
        var rng = Rng(seed);
        var s = new YmScript();
        for (int ch = 0; ch < 6; ch++)
        {
            var (alg, fb) = algFb(rng, ch);
            Channel(s, ch, alg, fb, patch(rng, ch), 0x100 + rng.Next(0, 0x600), rng.Next(1, 6));
        }
        s.Align(2);
        long on = s.T / YmScript.Sample;
        for (int ch = 0; ch < 6; ch++) Key(s, ch, 15);
        if (keyOffAfter > 0)
        {
            s.T = (on + keyOffAfter) * YmScript.Sample;
            for (int ch = 0; ch < 6; ch++) Key(s, ch, 0);
            if (keyOnAgainAfter > 0)
            {
                s.T = (on + keyOffAfter + keyOnAgainAfter) * YmScript.Sample;
                for (int ch = 0; ch < 6; ch++) Key(s, ch, 15);
            }
        }
        return Scenario(s, samples, false, (int)on + 12);
    }

    private static void EnvelopeScenarios()
    {
        Console.WriteLine("-- envelopes in notes: random rates, key scaling and levels on all operators, key on, off and on again (6 channels x 4 seeds x 5200 samples)");
        long exact = 0, total = 0; int worst = 0; double snr = double.PositiveInfinity, sigE = 0, errE = 0; var lines = new List<string>();
        for (int seed = 1; seed <= 4; seed++)
        {
            var o = RandomScenario(seed, 5200,
                (r, ch) => Ops(n => OpPatch.Default.With(oo => { oo.Tl = r.Next(8, 30); oo.Mul = r.Next(1, 5); oo.Ks = r.Next(0, 4); oo.Ar = r.Next(12, 32); oo.D1r = r.Next(4, 32); oo.Sl = r.Next(0, 16); oo.D2r = r.Next(0, 32); oo.Rr = r.Next(2, 16); return oo; })),
                (r, ch) => (r.Next(0, 8), r.Next(0, 4)), keyOffAfter: 2200, keyOnAgainAfter: 1200);
            exact += o.Exact; total += o.Compared; worst = Math.Max(worst, o.MaxAbs); snr = Math.Min(snr, o.MinSnrDb);
            for (int c = 0; c < 6; c++) { sigE += o.Channel[c].RmsSignal * o.Channel[c].RmsSignal * o.Channel[c].Compared; errE += o.Channel[c].RmsDiff * o.Channel[c].RmsDiff * o.Channel[c].Compared; }
            lines.Add($"seed {seed}: {100.0 * o.Exact / o.Compared:F1}%");
        }
        double overall = errE == 0 ? double.PositiveInfinity : 10 * Math.Log10(sigE / errE);
        check("envelope scenarios: at least 85% of channel samples exact, no sample off by more than 40 steps, 25 dB SNR over all channels", 100.0 * exact / total >= 85 && worst <= 40 && overall >= 25,
            $"{exact}/{total} exact ({100.0 * exact / total:F2}%), worst sample off by {worst} (9-bit steps = {worst / 256.0 * 100:F0}% of full scale), {overall:F1} dB SNR over all channels (weakest channel {snr:F1} dB); per seed: {string.Join(", ", lines)}. The differences are samples next to a note-on or note-off where the oracle's operators started one sample apart");
    }

    private static void SsgEg()
    {
        Console.WriteLine("-- SSG-EG: random patches with every mode (8 to 15) on all operators (6 channels x 3 seeds x 4000 samples)");
        long exact = 0, total = 0; int worst = 0; double snr = double.PositiveInfinity;
        for (int seed = 1; seed <= 3; seed++)
        {
            var o = RandomScenario(seed, 4000,
                (r, ch) => Ops(n => OpPatch.Default.With(oo => { oo.Tl = r.Next(8, 26); oo.Mul = r.Next(1, 4); oo.Ar = r.Next(20, 32); oo.D1r = r.Next(12, 28); oo.Sl = r.Next(0, 6); oo.D2r = r.Next(8, 28); oo.Rr = r.Next(4, 16); oo.Ssg = 8 | r.Next(0, 8); return oo; })),
                (r, ch) => (7, 0), keyOffAfter: 2600, keyOnAgainAfter: 600);
            exact += o.Exact; total += o.Compared; worst = Math.Max(worst, o.MaxAbs); snr = Math.Min(snr, o.MinSnrDb);
        }
        // not exact: the loop restarts and holds come out a tick apart for some operators (known gap, see the ledger); the bound keeps it from getting worse
        check("SSG-EG: at least 50% of channel samples exact (not exact: a known gap)", 100.0 * exact / total >= 50,
            $"{exact}/{total} exact ({100.0 * exact / total:F2}%), worst sample off by {worst}, weakest channel {snr:F1} dB SNR");
    }

    // ---------------------------------------------------------------- channel 3 special mode, CSM

    private static void ChannelThree()
    {
        Console.WriteLine("-- channel 3 special mode: a frequency per operator");
        foreach (int rewrites in new[] { 0, 1, 6 })
        {
            long exact = 0, total = 0; int worst = 0; double snr = double.PositiveInfinity;
            for (int seed = 1; seed <= 3; seed++)
            {
                var rng = Rng(300 + seed);
                var s = new YmScript();
                Channel(s, 2, 7, 0, Ops(n => OpPatch.Default.With(o => { o.Tl = 22 + 3 * n; o.Mul = 1 + rng.Next(0, 3); o.Dt = rng.Next(0, 8); o.Ar = 31; return o; })), 0x2A0, 4);
                s.Reg(0, 0x27, 0x40);
                foreach (int off in new[] { 0, 1, 2 }) { s.Reg(0, 0xAC + off, 0x08 | rng.Next(0, 8)); s.Reg(0, 0xA8 + off, rng.Next(0, 256)); }
                s.Align(2);
                long on = s.T / YmScript.Sample;
                Key(s, 2, 15);
                for (int k = 0; k < rewrites; k++)
                {
                    s.T = (on + 300 + 300 * k) * YmScript.Sample;
                    int which = rng.Next(0, 3);
                    s.Reg(0, 0xAC + which, 0x08 * rng.Next(1, 6) | rng.Next(0, 8)); s.Reg(0, 0xA8 + which, rng.Next(0, 256));
                }
                var o2 = Scenario(s, 3000, false, (int)on + 12, 1 << 2, shiftRange: 4);
                exact += o2.Exact; total += o2.Compared; worst = Math.Max(worst, o2.MaxAbs); snr = Math.Min(snr, o2.MinSnrDb);
            }
            if (rewrites == 0) check("channel 3 special mode, four operator frequencies (3 seeds): identical to the oracle", exact == total, $"{exact}/{total} exact");
            else check($"channel 3 special mode with {rewrites} operator frequenc{(rewrites == 1 ? "y" : "ies")} rewritten while playing (3 seeds): the rewrite lands a sample apart per operator, so the phase differs by that sample's step for the rest of the note; SNR at least 25 dB", snr >= 25,
                $"{exact}/{total} exact, worst sample off by {worst}, weakest note {snr:F1} dB SNR");
        }
    }

    private static void Csm()
    {
        Console.WriteLine("-- CSM: timer A keys on channel 3's four operators (one-sample key-on pulses)");
        long exact = 0, total = 0; double snr = double.PositiveInfinity;
        foreach (int ta in new[] { 900, 960, 1000 })
        {
            var s = new YmScript();
            Channel(s, 2, 7, 0, Ops(n => OpPatch.Default.With(o => { o.Tl = 22 + 3 * n; o.Mul = 1 + n; o.Ar = 31; o.D1r = 0; o.Sl = 0; o.Rr = 9; return o; })), 0x2A0, 4);
            s.Reg(0, 0x24, ta >> 2); s.Reg(0, 0x25, ta & 3);
            s.Align(2);
            long on = s.T / YmScript.Sample;
            s.Reg(0, 0x27, 0x80 | 0x15);
            var o2 = Scenario(s, 3000, false, (int)on + 6, 1 << 2, shiftRange: 4);
            exact += o2.Exact; total += o2.Compared; snr = Math.Min(snr, o2.MinSnrDb);
        }
        // ymfm leaves a CSM note keyed on; the chip keys it for one sample. With the pulse model the notes line up with the oracle's but not sample for sample
        check("CSM (timer A 900, 960, 1000; attack rate 31): at least 60% of samples exact (the notes line up; where a pulse falls relative to the envelope tick decides the rest)", 100.0 * exact / total >= 60, $"{exact}/{total} exact ({100.0 * exact / total:F1}%), weakest case {snr:F1} dB SNR");
    }

    // ---------------------------------------------------------------- timers, busy, status

    private static void TimersAndStatus()
    {
        Console.WriteLine("-- timers, busy flag and status reads: a status read every 6 chip clocks around each event, compared to the clock");
        foreach (bool isB in new[] { false, true })
        {
            int cases = 0, equalCases = 0; var bad = new List<string>();
            foreach (int val in isB ? new[] { 255, 100, 1 } : new[] { 1023, 900, 100 })
                foreach (int phase in new[] { 0, 12, 18, 30, 138 })
                {
                    var s = new YmScript();
                    if (isB) s.Reg(0, 0x26, val); else { s.Reg(0, 0x24, val >> 2); s.Reg(0, 0x25, val & 3); }
                    s.Align(3);
                    s.T += phase;
                    s.Reg(0, 0x27, isB ? 0x2A : 0x15);
                    long dataClock = s.T - YmScript.Gap + YmScript.AddressToData;
                    int period = isB ? 16 * (256 - val) : 1024 - val;
                    long expect = dataClock + (long)period * 144;
                    for (long c = expect - 2 * 144; c < expect + 2 * 144; c += 6) s.Read(c, 0);
                    int samples = (int)(expect / 144) + 6;
                    var nk = s.RunNuked(0, samples);
                    var mine = s.RunMine(false, samples);
                    long OracleFirst() { foreach (var (clock, _, value) in nk.Reads) if ((value & (isB ? 2 : 1)) != 0) return clock * 6; return -1; }
                    long MineFirst() { foreach (var (clock, _, value) in mine.Reads) if ((value & (isB ? 2 : 1)) != 0) return clock; return -1; }
                    long of = OracleFirst(), mf = MineFirst();
                    cases++;
                    if (of == mf && of > 0) equalCases++; else bad.Add($"{val}@{phase / 6}: {of}/{mf}");
                }
            check($"timer {(isB ? "B" : "A")}: the flag rises at the same chip clock as the oracle's, for 3 reload values and 5 write phases", equalCases == cases, $"{equalCases}/{cases} identical{(bad.Count > 0 ? "; differing (value@phase: oracle/ours): " + string.Join(" ", bad.Take(6)) : "")}");
        }
        // the busy flag
        int busyCases = 0, busyEqual = 0; var busyBad = new List<string>();
        foreach (int phase in new[] { 0, 6, 30, 84, 138 })
        {
            var s = new YmScript();
            s.Align(3);
            s.T += phase;
            long addr = s.T;
            s.Write(addr, 0, 0x30); s.Write(addr + 24, 1, 0x00);
            for (long c = addr - 12; c < addr + 24 + 400; c += 6) s.Read(c, 0);
            var nk = s.RunNuked(0, 12);
            var mine = s.RunMine(false, 12);
            busyCases++;
            bool same = nk.Reads.Count == mine.Reads.Count;
            for (int k = 0; same && k < nk.Reads.Count; k++) if (nk.Reads[k].Clock * 6 != mine.Reads[k].Clock || (nk.Reads[k].Value >> 7 & 1) != (mine.Reads[k].Value >> 7 & 1)) same = false;
            if (same) busyEqual++; else busyBad.Add($"phase {phase / 6}");
        }
        check("busy flag: raised 12 chip clocks after a data write and held 192, identical to the oracle on every status read, 5 write phases", busyEqual == busyCases, $"{busyEqual}/{busyCases} read series identical{(busyBad.Count > 0 ? "; differ at " + string.Join(", ", busyBad) : "")}");
        // which ports answer
        foreach (var (type, anyPort, name) in new[] { (1, false, "discrete YM2612 (Model 1)"), (2, true, "integrated YM3438 (Models 2 and 3)") })
        {
            int[][] orders = { new[] { 3, 1, 2, 0, 3, 2, 1, 0 }, new[] { 1, 0, 1, 2, 0, 2, 3, 3 } };
            int ok = 0;
            foreach (var order in orders)
            {
                var s = new YmScript();
                s.Reg(0, 0x26, 0xF8);
                s.Align(3);
                s.Reg(0, 0x27, 0x2A);
                s.T += 144 * 200;
                long t0 = s.T;
                for (int k = 0; k < order.Length; k++) s.Read(t0 + k * 600, order[k]);
                s.Write(t0 + 7 * 600 + 300, 0, 0x30); s.Write(t0 + 7 * 600 + 324, 1, 0);
                s.Read(t0 + 7 * 600 + 348, order[0]); s.Read(t0 + 7 * 600 + 372, 0);
                int samples = (int)(t0 / 144) + 40;
                var nk = s.RunNuked(type, samples);
                var mine = s.RunMine(!anyPort, samples, anyPort: anyPort);
                bool same = nk.Reads.Count == mine.Reads.Count;
                for (int k = 0; same && k < nk.Reads.Count; k++) if (nk.Reads[k].Value != mine.Reads[k].Value) same = false;
                if (same) ok++;
            }
            check($"status reads on all four ports, {name}: the same values as the oracle (an echo of the last status read on ports 1 to 3 for the discrete chip, the live status for the integrated one)", ok == orders.Length, $"{ok}/{orders.Length} read orders identical");
        }
    }

    // ---------------------------------------------------------------- the mix: single channels, the DAC, the ladder

    private static void MixDacAndLadder()
    {
        Console.WriteLine("-- the mix: single channels with every pan setting, the DAC (replacing channel 6, with the pans), the ladder effect of the discrete chip");
        foreach (bool ladder in new[] { false, true })
        {
            long exact = 0, total = 0; int worst = 0;
            foreach (int pan in new[] { 3, 2, 1, 0 })
                for (int ch = 0; ch < 6; ch++)
                {
                    var rng = Rng(400 + ch);
                    var s = new YmScript();
                    Channel(s, ch, 4, 3, Ops(n => OpPatch.Default.With(o => { o.Tl = n == 3 ? 4 : 30; o.Mul = 1 + n; o.Ar = 31; return o; })), 0x200 + rng.Next(0, 0x300), 3, pan);
                    s.Align(2);
                    long on = s.T / YmScript.Sample;
                    Key(s, ch, 15);
                    var o2 = Scenario(s, 1000, ladder, (int)on + 12, shiftRange: 6);
                    exact += o2.MixLeft.Exact + o2.MixRight.Exact; total += o2.MixLeft.Compared + o2.MixRight.Compared; worst = Math.Max(worst, Math.Max(o2.MixLeft.MaxAbs, o2.MixRight.MaxAbs));
                }
            check($"mixed output of one FM channel at a time, all six channels and the four pan settings, {(ladder ? "discrete YM2612 with the ladder effect" : "YM3438")}: both sides identical to the oracle", exact == total, $"{exact}/{total} samples exact, worst difference {worst}");
        }
        foreach (bool ladder in new[] { false, true })
        {
            long exact = 0, total = 0; int worst = 0;
            foreach (int pan in new[] { 3, 2, 1, 0 })
            {
                var s = new YmScript();
                s.Reg(1, 0xB4 + 2, pan << 6);              // channel 6's pan steers the DAC
                s.Reg(0, 0x2B, 0x80);
                s.Align(2);
                s.Write(s.T, 0, 0x2A);
                s.T += 48;
                var rng = Rng(500 + pan);
                long on = s.T / YmScript.Sample;
                int[] values = { 0x80, 0x81, 0x7F, 0x00, 0xFF, 0x90, 0x70, 0x40, 0xC0, 0x82, 0x7E, 0x80 };
                for (int k = 0; k < 150; k++) s.Write(s.T + k * 8 * YmScript.Sample, 1, k < values.Length ? values[k] : rng.Next(0, 256));
                int samples = (int)on + 150 * 8 + 10;
                var o2 = Scenario(s, samples, ladder, (int)on + 4, 0, shiftRange: 6);
                exact += o2.MixLeft.Exact + o2.MixRight.Exact; total += o2.MixLeft.Compared + o2.MixRight.Compared; worst = Math.Max(worst, Math.Max(o2.MixLeft.MaxAbs, o2.MixRight.MaxAbs));
            }
            check($"DAC (register 2A data, 2B enable, the 4 pan settings of channel 6), {(ladder ? "discrete YM2612 with the ladder effect" : "YM3438")}: both sides identical to the oracle", exact == total, $"{exact}/{total} samples exact, worst difference {worst}");
        }
    }

    // ---------------------------------------------------------------- what the pipeline buys

    private static void WithoutThePipeline()
    {
        Console.WriteLine("-- for the record: one channel at a time through the mixed output, every algorithm, with the slot pipeline on and off (off = ymfm's arithmetic exactly)");
        var on = new List<string>(); var off = new List<string>();
        long exactOn = 0, exactOff = 0, total = 0;
        for (int alg = 0; alg < 8; alg++)
        {
            var s = new YmScript();
            var ops = Ops(n => OpPatch.Default.With(o => { o.Tl = alg == 7 ? 24 : (n == 3 ? 6 : 28); o.Mul = n + 1; return o; }));
            Channel(s, 0, alg, 4, ops, 0x300, 4);
            s.Align(2);
            long start = s.T / YmScript.Sample;
            Key(s, 0, 15);
            var a = Scenario(s, 1500, false, (int)start + 12, 1, 6, pipeline: true);
            var b = Scenario(s, 1500, false, (int)start + 12, 1, 6, pipeline: false);
            on.Add($"{alg}: {a.MixLeft.ExactPercent:F0}% {(double.IsInfinity(a.MixLeft.SnrDb) ? "inf" : a.MixLeft.SnrDb.ToString("F0"))} dB"); off.Add($"{alg}: {b.MixLeft.ExactPercent:F0}% {(double.IsInfinity(b.MixLeft.SnrDb) ? "inf" : b.MixLeft.SnrDb.ToString("F0"))} dB");
            exactOn += a.MixLeft.Exact; exactOff += b.MixLeft.Exact; total += a.MixLeft.Compared;
        }
        Console.WriteLine($"   slot pipeline on : algorithm: percent exact, SNR  {string.Join("; ", on)}");
        Console.WriteLine($"   slot pipeline off: algorithm: percent exact, SNR  {string.Join("; ", off)}");
        check("the slot pipeline brings the chip closer to the oracle for every algorithm than ymfm's arithmetic does (information with a floor: on beats off in total)", exactOn > exactOff, $"{exactOn}/{total} samples exact with the pipeline, {exactOff}/{total} without");
    }
}
