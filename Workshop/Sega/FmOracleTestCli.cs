using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using NesEmulator.Sega;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--fm-oracle-test [--only group[,group]] [--prove] [--verbose]</c>: the YM2413 (<see cref="Ym2413"/>) against Nuked-OPLL, sample for sample. The oracle (a die-derived YM2413 emulation, GPL, kept in the tools
/// folder outside the repository) is driven black-box by <c>opll_oracle.exe</c> (<c>Workshop/Sega/oracle/opll_oracle.c</c>, built by <c>build-oracle-opll.ps1</c>): the same register script goes to the oracle
/// and to this chip, and what each channel delivers is compared one chip sample (72 clocks) at a time.
/// <para><b>What the oracle delivers.</b> Its output is the chip's serial DAC stream: each of the nine melodic channels shows up at one fixed one of the 18 OPLL clocks of a sample, and each of the five percussion
/// voices at two. A value is sign and magnitude: the 11-bit exponential-table magnitude <c>res</c> is cut to <c>(res &gt;&gt; 3) + 1</c>, negative for the negative half. <see cref="Ym2413"/> keeps
/// the full magnitude, so each of its outputs is cut the same way before the comparison (<see cref="Melodic"/>, <see cref="Rhythm"/>).</para>
/// <para><b>Alignment.</b> A register write the oracle takes in at OPLL clock <c>g</c> reaches this chip at sample <c>(g + 1) / 18 + 1</c>, and this chip's sample <c>j</c> is compared with the oracle's sample <c>j + 1</c> (<c>j + 2</c> for
/// channels 7-9, which the oracle reads out at the start of the next frame): the oracle's own latency, one constant for every case (the structure group finds it by sweeping the clock of a key-on write: the first sound moves one sample at clock 14 of a sample and nowhere else).</para>
/// <para><b>What "match" means here</b> (each group says which it holds to): <b>exact</b> = every compared sample equal; <b>bounded</b> = the error against the signal (SNR in dB over the whole run, and the largest single difference in the
/// oracle's 9-bit units, where full scale is 256) stays inside the numbers the group states, with the share of exactly equal samples reported.</para>
/// Exit code 0 = everything passed, 1 = a mismatch, 3 = the oracle is not built (reported, never counted as a pass).
/// </summary>
internal static class FmOracleTestCli
{
    private const double ChipClock = 3_579_545.0;

    // the oracle's melodic channel k is at this clock of its 18-clock sample, its percussion voices (bass drum, snare, tom, cymbal, hi-hat) at these (and again at the second)
    private static readonly int[] MelodicClock = { 7, 8, 9, 13, 14, 15, 1, 2, 3 };
    // the oracle's sample j + lag carries this chip's sample j: channels 1-6 one sample later, channels 7-9 (which sit at the start of the next frame, clocks 1-3) two
    private static readonly int[] MelodicLag = { 1, 1, 1, 1, 1, 1, 2, 2, 2 };
    private static readonly int[] RhythmClock = { 1, 2, 0, 3, 4 };
    private static readonly int[] RhythmClockAgain = { 6, 10, 5, 11, 17 };
    private static readonly string[] RhythmName = { "bass drum", "snare", "tom", "cymbal", "hi-hat" };
    private static readonly int[] RhythmKey = { 0x10, 0x08, 0x04, 0x02, 0x01 };
    private static readonly int[] RhythmVoice = { 9, 11, 12, 13, 10 };   // the index of each in Ym2413.ChannelOutput

    private static int passes, failures;
    private static bool verbose;
    private static string? only;
    private static string Exe = "";

    public static int Run(string[] args)
    {
        if (args.Length >= 4 && args[0].Equals("--fm-dump", StringComparison.OrdinalIgnoreCase)) return Dump(args[1], args[2], args[3], args.Skip(4).ToArray());
        verbose = args.Any(a => a.Equals("--verbose", StringComparison.OrdinalIgnoreCase));
        only = SmsTestCli.Opt(args, "only");
        string exe = Path.Combine(SmsTestCli.ToolsRoot(args), @"oracles\harness\opll_oracle.exe");
        if (!File.Exists(exe))
        {
            Console.WriteLine($"SKIPPED: the YM2413 oracle is not built ({exe}). Run Workshop\\Sega\\fetch-tools.ps1 then Workshop\\Sega\\build-oracle-opll.ps1. This is not a pass.");
            return 3;
        }
        Exe = exe;
        if (SmsTestCli.Opt(args, "patch-fit") is { } fitList) return PatchFit(fitList, SmsTestCli.Opt(args, "fields"), SmsTestCli.Opt(args, "start"));
        bool prove = args.Any(a => a.Equals("--prove", StringComparison.OrdinalIgnoreCase));
        bool Wanted(Group g) => only == null || only.Split(',').Contains(g.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var g in Groups.Where(Wanted))
        {
            var sw = Stopwatch.StartNew();
            Console.WriteLine($"== {g.Id}: {g.Title}");
            g.Run(Poison.None);
            Console.WriteLine($"   ({sw.Elapsed.TotalSeconds:F1}s)");
        }
        Console.WriteLine($"\n{passes} passed, {failures} failed");
        if (!prove) return failures == 0 ? 0 : 1;
        if (failures != 0) { Console.WriteLine("prove: the clean run failed, nothing to prove"); return 1; }
        int bad = 0;
        foreach (var g in Groups.Where(Wanted))
        {
            if (g.Poison == Poison.None) { Console.WriteLine($"\n== {g.Id}: {g.PoisonText}"); continue; }
            int before = failures;
            var sw = Stopwatch.StartNew();
            var save = Console.Out; Console.SetOut(TextWriter.Null);
            try { g.Run(g.Poison); } finally { Console.SetOut(save); }
            int hit = failures - before;
            Console.WriteLine($"\n== {g.Id} with a fault injected ({g.PoisonText}): {hit} check(s) failed ({sw.Elapsed.TotalSeconds:F1}s): {(hit > 0 ? "CAN FAIL" : "NOT PROVEN")}");
            if (hit == 0) bad++;
            failures = before;
        }
        Console.WriteLine(bad == 0 ? "prove: every group failed when a fault was injected" : $"prove: {bad} group(s) not proven");
        return bad == 0 ? 0 : 1;
    }

    private static void Info(string name, string detail)
    {
        if (verbose) Console.WriteLine($"  info  {name}  [{detail}]");
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) passes++; else failures++;
        if (!ok || verbose) Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
    }

    // ---------------------------------------------------------------------------------------------- the two sides

    /// <summary>One register write of a script: <paramref name="Clock"/> is the OPLL clock of the sample at which the oracle may first take it in.</summary>
    private readonly record struct Wr(int Sample, int Reg, int Val, int Clock = 0);

    private enum Poison { None, Pitch, KeyScale, Vibrato, Instrument, Rates, Percussion, Latency }

    private sealed class Oracle
    {
        private readonly int[] data;   // 36 per sample: clock c, melodic at [2c], rhythm at [2c + 1]
        public Oracle(int[] d) { data = d; }
        public int At(int sample, int clock, int output = 0) => data[sample * 36 + clock * 2 + output];
    }

    private static Oracle RunOracle(IReadOnlyList<Wr> writes, int samples)
    {
        string dir = Path.Combine(Path.GetTempPath(), "bn_opll_oracle_" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        string script = Path.Combine(dir, "script.txt"), output = Path.Combine(dir, "out.bin");
        var sb = new StringBuilder().AppendLine(samples.ToString());
        foreach (var w in writes) sb.Append(w.Sample).Append(' ').Append(w.Reg).Append(' ').Append(w.Val).Append(' ').Append(w.Clock).AppendLine();
        File.WriteAllText(script, sb.ToString());
        var psi = new ProcessStartInfo(Exe, $"\"{script}\" \"{output}\" --cycles") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        if (!p.WaitForExit(120_000)) { p.Kill(); throw new TimeoutException("opll_oracle did not finish"); }
        if (p.ExitCode != 0) throw new InvalidOperationException("opll_oracle failed: " + p.StandardError.ReadToEnd());
        byte[] raw = File.ReadAllBytes(output);
        var d = new int[raw.Length / 4];
        Buffer.BlockCopy(raw, 0, d, 0, raw.Length);
        return new Oracle(d);
    }

    /// <summary>The clock at which the oracle takes in each write's data byte, as its driver queues them: the address port at the first clock the write is allowed and the previous one is done, the data port 3 clocks later, the next write 21 clocks after that.</summary>
    private static List<(int Clock, Wr W)> Schedule(IReadOnlyList<Wr> writes)
    {
        var list = new List<(int, Wr)>(); long busy = 0;
        foreach (var w in writes)
        {
            long baseClock = Math.Max((long)w.Sample * 18 + w.Clock, busy);
            long data = baseClock + 3; busy = data + 21;
            list.Add(((int)data, w));
        }
        return list;
    }

    /// <summary>What this chip delivers for the same script: per sample the 14 voice outputs, cut to the oracle's units (<see cref="Melodic"/>, <see cref="Rhythm"/>); entry j is this chip's sample j (compare with the oracle's j + 1).</summary>
    private sealed class Mine
    {
        public readonly int[][] Voice;   // [14][samples]
        public Mine(int samples) { Voice = Enumerable.Range(0, 14).Select(_ => new int[samples]).ToArray(); }
    }

    /// <summary>The sample at which this chip takes in each write of a script, in the order it takes them: the oracle's schedule, each register's slot position, and a key-off one sample after the key-on formula.</summary>
    private static List<(int S, Wr W)> PlanWrites(IReadOnlyList<Wr> writes)
    {
        // a key-off (the key bit going from 1 to 0) takes effect one sample later than a key-on at the same clock (measured the same way, with the release of a loud sustained note)
        var lastKey = new Dictionary<int, int>();
        return Schedule(writes).Select(x =>
        {
            int late = 0, slotValue = x.W.Val;
            if (x.W.Reg is >= 0x20 and <= 0x28)
            {
                if ((lastKey.GetValueOrDefault(x.W.Reg) & 0x10) != 0 && (x.W.Val & 0x10) == 0) late = 1;
                lastKey[x.W.Reg] = x.W.Val;
            }
            else if (x.W.Reg == 0x0E)
            {
                int old = lastKey.GetValueOrDefault(0x0E);
                if ((old & ~x.W.Val & 0x1F) != 0) late = 1;           // a percussion voice released
                slotValue = (x.W.Val | old) & 0x1F;                  // the voices this write touches decide its slot
                lastKey[0x0E] = x.W.Val;
            }
            return (S: (x.Clock + 1 - WriteSlot(x.W.Reg, slotValue)) / 18 + 1 + late, x.W);
        }).OrderBy(x => x.S).ToList();
    }

    private static Mine RunMine(IReadOnlyList<Wr> writes, int samples, Poison poison = Poison.None) => Play(PlanWrites(writes), samples, poison);

    private static Mine Play(List<(int S, Wr W)> due, int samples, Poison poison = Poison.None, Mine? into = null)
    {
        var chip = new Ym2413(ChipClock);
        if (poison == Poison.Vibrato) chip.SetLfoPhase(unchecked((uint)(-1 + 7)), -1 + 7);
        var m = into ?? new Mine(samples);
        int next = 0;
        for (int j = 0; j < samples; j++)
        {
            while (next < due.Count && due[next].S <= j) { var w = due[next++].W; chip.WriteRegister(w.Reg, (byte)PoisonValue(poison, w.Reg, w.Val)); }
            chip.Calc();
            for (int v = 0; v < 14; v++) m.Voice[v][j] = v < 9 ? Melodic(chip.ChannelOutput(v)) : Rhythm(chip.ChannelOutput(v));
        }
        return m;
    }

    /// <summary>Where in the oracle's 18-clock frame a register's slot sits: a write to channel k's frequency, key or instrument register is taken at the clock that is 17 + k, so a key-on whose data byte lands from clock 17 + k of a sample onwards shows one sample later
    /// (measured by sweeping the clock of a key-on write on each of the nine channels: the first sound moves by one sample at that clock and nowhere else). The percussion key bits likewise, by the channel their slots belong to (bass drum 6, snare and hi-hat 7, tom and cymbal 8).
    /// Everything else (the user instrument, the rhythm-mode and test bits) is taken as channel 0.</summary>
    private static int WriteSlot(int reg, int val)
    {
        if (reg is >= 0x10 and <= 0x18) return reg - 0x10;
        if (reg is >= 0x20 and <= 0x28) return reg - 0x20;
        if (reg is >= 0x30 and <= 0x38) return reg - 0x30;
        if (reg == 0x0E) return (val & 0x10) != 0 ? -3 : (val & 0x08) != 0 ? -2 : (val & 0x04) != 0 ? -4 : (val & 0x02) != 0 ? 4 : (val & 0x01) != 0 ? -5 : 0;   // measured the same way: bass drum, snare, tom, cymbal, hi-hat
        return 0;
    }

    private static int PoisonValue(Poison p, int reg, int val) => p switch
    {
        Poison.Pitch when reg is >= 0x10 and <= 0x18 => val ^ 0x02,                        // fnum low bits
        Poison.KeyScale when reg is 0x02 or 0x03 => val ^ 0x40,                            // key-scale level
        Poison.Instrument when reg is >= 0x30 and <= 0x38 => (val + 0x10) & 0xFF,          // the next instrument
        Poison.Rates when reg is >= 0x04 and <= 0x07 => val ^ 0x33,                        // attack, decay, sustain and release rates
        Poison.Percussion when reg == 0x0E => val & ~0x04,                                 // the tom never plays
        _ => val,
    };

    /// <summary>A melodic channel's output cut to the oracle's value: a sign and the magnitude cut to (res >> 3) + 1. The chip's output is minus half the exponential-table value, one's complement for the negative half of the sine.</summary>
    public static int Melodic(int raw) => raw > 0 ? -(((raw - 1) >> 3) + 1) : ((-raw) >> 3) + 1;

    /// <summary>A percussion output the same way: not inverted and not halved, so the magnitude is the output over two.</summary>
    public static int Rhythm(int raw) => raw >= 0 ? ((raw >> 1) >> 3) + 1 : -((~(raw >> 1) >> 3) + 1);

    private static int OracleMelodic(Oracle o, int channel, int sample) => o.At(sample, MelodicClock[channel]);
    private static int OracleRhythm(Oracle o, int voice, int sample) => o.At(sample, RhythmClock[voice], 1);

    // ---------------------------------------------------------------------------------------------- statistics

    private readonly record struct Stat(int Compared, int Exact, int MaxError, double SnrDb)
    {
        public double Percent => Compared == 0 ? 0 : 100.0 * Exact / Compared;
        public override string ToString() => $"{Exact}/{Compared} exact ({Percent:F1}%), largest difference {MaxError}, {(SnrDb >= 90 ? "no error" : SnrDb.ToString("F1") + " dB")}";
    }

    private static Stat Compare(Func<int, int> oracle, int[] mine, int from, int to, int lag = 1)
    {
        int exact = 0, n = 0, max = 0; double signal = 0, error = 0;
        for (int i = from; i < to; i++)
        {
            int j = i - lag;
            if (j < 0 || j >= mine.Length) continue;
            int o = oracle(i), m = mine[j];
            n++; if (o == m) exact++;
            max = Math.Max(max, Math.Abs(o - m)); signal += (double)o * o; error += (double)(o - m) * (o - m);
        }
        return new Stat(n, exact, max, error == 0 ? 99.0 : 10 * Math.Log10(signal / error));
    }

    /// <summary>How the loudness of the note follows the oracle's, for patches whose waveform is chaotic (a full-level modulator with strong feedback turns a one-sample difference in an envelope step into a different waveform): the level (RMS) of every window
    /// of <paramref name="window"/> samples where the oracle's is at least 8 units (30 dB under full scale), in dB, mine against the oracle's. Returns the largest and the mean absolute difference, and how many windows were compared.</summary>
    private static (double Max, double Mean, int Windows) LevelError(Func<int, int> oracle, int[] mine, int from, int to, int lag, int window = 128)
    {
        double max = 0, sum = 0; int n = 0;
        for (int s = from; s + window <= to; s += window)
        {
            double so = 0, sm = 0;
            for (int i = s; i < s + window; i++) { double o = oracle(i), m = mine[i - lag]; so += o * o; sm += m * m; }
            double ro = Math.Sqrt(so / window), rm = Math.Sqrt(sm / window);
            if (ro < 8) continue;
            double d = Math.Abs(20 * Math.Log10(Math.Max(rm, 1e-9) / ro));
            max = Math.Max(max, d); sum += d; n++;
        }
        return (max, n == 0 ? 0 : sum / n, n);
    }

    // ---------------------------------------------------------------------------------------------- patches and scripts

    private readonly record struct Op(int Am = 0, int Pm = 0, int Eg = 1, int Kr = 0, int Ml = 1, int Kl = 0, int Tl = 0, int Fb = 0, int Ws = 0, int Ar = 15, int Dr = 0, int Sl = 15, int Rr = 0);

    /// <summary>The user instrument (registers 0-7) from a modulator and a carrier.</summary>
    private static Wr[] UserPatch(Op m, Op c) => new[]
    {
        new Wr(0, 0, m.Am << 7 | m.Pm << 6 | m.Eg << 5 | m.Kr << 4 | m.Ml), new Wr(0, 1, c.Am << 7 | c.Pm << 6 | c.Eg << 5 | c.Kr << 4 | c.Ml),
        new Wr(0, 2, m.Kl << 6 | m.Tl), new Wr(0, 3, c.Kl << 6 | c.Ws << 4 | m.Ws << 3 | m.Fb),
        new Wr(0, 4, m.Ar << 4 | m.Dr), new Wr(0, 5, c.Ar << 4 | c.Dr), new Wr(0, 6, m.Sl << 4 | m.Rr), new Wr(0, 7, c.Sl << 4 | c.Rr),
    };

    /// <summary>The modulator and carrier of a typical test tone: both sustained at full level.</summary>
    private static readonly Op ModBase = new(Eg: 1, Ml: 1, Tl: 16, Fb: 5, Ar: 15, Dr: 0, Sl: 15, Rr: 0);
    private static readonly Op CarBase = new(Eg: 1, Ml: 1, Ar: 15, Dr: 0, Sl: 15, Rr: 0);
    /// <summary>A modulator that makes no sound (its envelope never opens), so the carrier is a plain sine under its own envelope.</summary>
    private static readonly Op ModSilent = new(Eg: 1, Ml: 1, Tl: 63, Ar: 0, Dr: 0, Sl: 15, Rr: 0);

    /// <summary>Nine pitches, one per channel: (block, fnum) from the lowest to the highest octave, with fnum values that cover the key-scale table's sixteen levels.</summary>
    private static readonly (int Block, int Fnum)[] Pitches = { (0, 0x1F0), (1, 0x150), (2, 0x0C8), (3, 0x0AD), (4, 0x120), (5, 0x08A), (5, 0x1A0), (6, 0x0F0), (7, 0x050) };

    /// <summary>The nine channels set to <paramref name="register"/> (instrument and volume) and keyed on at <paramref name="keyOnAt"/>, and off at <paramref name="keyOffAt"/> when that is above 0.</summary>
    private static List<Wr> NineKeys(int keyOnAt, int keyOffAt, Func<int, int> register, int sustain = 0)
    {
        var w = new List<Wr>();
        for (int ch = 0; ch < 9; ch++)
        {
            var (blk, fn) = Pitches[ch];
            w.Add(new Wr(0, 0x30 + ch, register(ch)));
            w.Add(new Wr(0, 0x10 + ch, fn & 0xFF));
            w.Add(new Wr(keyOnAt, 0x20 + ch, 0x10 | sustain << 5 | blk << 1 | fn >> 8));
        }
        if (keyOffAt > 0) for (int ch = 0; ch < 9; ch++) { var (blk, fn) = Pitches[ch]; w.Add(new Wr(keyOffAt, 0x20 + ch, sustain << 5 | blk << 1 | fn >> 8)); }
        return w;
    }

    private static string Worst(Stat[] s) => $"worst of nine channels: {s.OrderBy(x => x.SnrDb).First()}";

    // ---------------------------------------------------------------------------------------------- the groups

    private sealed record Group(string Id, string Title, Action<Poison> Run, Poison Poison, string PoisonText);

    private static readonly Group[] Groups =
    {
        new("structure", "where each channel's value is in the oracle's output, and the oracle's key-on latency (what every other group rests on)", Structure, Poison.Latency, "the key-on latency this chip is aligned with moved by one clock (the slot positions the writes are mapped through shifted by one)"),
        new("oscillator", "steady tones, exact: multipliers, feedback, waveforms, levels, key scaling, tremolo, vibrato; all nine channels at nine pitches", Oscillator, Poison.KeyScale, "the key-scale field of the user instrument flipped on this side only"),
        new("lfo", "tremolo and vibrato over a whole period of their own, exact", Lfo, Poison.Vibrato, "the vibrato and tremolo counters started seven samples late"),
        new("instruments", "the 15 built-in instruments at nine pitches, key-on to release, bounded", Instruments, Poison.Instrument, "every instrument register read as the next instrument"),
        new("envelope", "envelope phases of a lone carrier: attack, decay, sustain level, release, key scaling of the rates, sustain flag; bounded", Envelope, Poison.Rates, "the attack, decay, sustain and release rates altered on this side only"),
        new("rhythm", "the five percussion voices: bass drum and tom by sample; snare, cymbal and hi-hat by level (their noise is not the oracle's); bounded", Rhythm5, Poison.Percussion, "the tom's key-on bit removed on this side only"),
        new("pitch", "all nine channels at nine pitches at once, plain sines, whole-run agreement", Pitch, Poison.Pitch, "fnum bits flipped on this side only"),
    };

    // ---- structure

    private static void Structure(Poison p)
    {
        var map = new List<int>();
        for (int ch = 0; ch < 9; ch++)
        {
            var w = new List<Wr>(UserPatch(ModSilent, CarBase)) { new(0, 0x30 + ch, 0), new(0, 0x10 + ch, 0xAD), new(0, 0x20 + ch, 0x10 | 3 << 1) };
            var o = RunOracle(w, 1200);
            var clocks = new SortedSet<int>();
            for (int i = 800; i < 1200; i++) for (int c = 0; c < 18; c++) if (o.At(i, c) != 1) clocks.Add(c);
            map.Add(clocks.Count == 1 ? clocks.Min : -1);
        }
        int shiftClocks = p == Poison.Latency ? 1 : 0;   // the injected fault: this side believes every slot is one clock later than it is
        Check("each melodic channel appears at exactly one clock of the sample, and it is the one the comparison reads", map.SequenceEqual(MelodicClock.Select(c => (c + shiftClocks) % 18)), "found " + string.Join(",", map));
        var rmap = new List<string>(); bool allTwice = true;
        for (int v = 0; v < 5; v++)
        {
            var w = new List<Wr>
            {
                new(0, 0x16, 0x20), new(0, 0x17, 0x50), new(0, 0x18, 0xC0), new(0, 0x26, 0x05), new(0, 0x27, 0x05), new(0, 0x28, 0x01), new(0, 0x36, 0), new(0, 0x37, 0), new(0, 0x38, 0),
                new(0, 0x0E, 0x20), new(200, 0x0E, 0x20 | RhythmKey[v]),
            };
            var o = RunOracle(w, 1000);
            var clocks = new SortedSet<int>();
            for (int i = 400; i < 1000; i++) for (int c = 0; c < 18; c++) if (o.At(i, c, 1) != 1) clocks.Add(c);
            allTwice &= clocks.SetEquals(new[] { RhythmClock[v], RhythmClockAgain[v] });
            rmap.Add($"{RhythmName[v]}:{string.Join("+", clocks)}");
        }
        Check("each percussion voice appears at exactly two clocks of the rhythm output, the ones the comparison reads", allTwice, string.Join(" ", rmap));
        // a key-on write's first sound moves by one sample exactly when its data byte lands at the last clocks of a sample: the latency this suite's alignment is built on
        // (the carrier alone, its modulator silent, so the first sound is the carrier's first non-zero sample)
        for (int ch = 0; ch < 9; ch++)
        {
            var found = new List<int>(); var expected = new List<int>();
            for (int c = 0; c < 18; c++)
            {
                var w = new List<Wr>(UserPatch(ModSilent, CarBase)) { new(0, 0x30 + ch, 0), new(0, 0x10 + ch, 0xAD), new(100, 0x20 + ch, 0x16, c) };
                var o = RunOracle(w, 190);
                found.Add(Enumerable.Range(95, 90).First(i => OracleMelodic(o, ch, i) != 1) - 100);
                // the chip takes the data byte (3 clocks after the address) in at slot position 17 + ch; channels 7-9 are read out a frame later
                expected.Add(2 + (int)Math.Floor((c + 4 - ch + shiftClocks) / 18.0) + (ch >= 6 ? 1 : 0));
            }
            Check($"channel {ch + 1}: a key-on's first sound moves by one sample at the slot's clock (data byte at clock {(17 + ch) % 18} of the sample)", found.SequenceEqual(expected), "oracle " + string.Join(",", found) + (found.SequenceEqual(expected) ? "" : " expected " + string.Join(",", expected)));
        }
    }

    // ---- steady tones

    private static void SteadyCase(string name, Op m, Op c, Poison p, int volume = 0, int from = 700, int length = 1500)
    {
        var w = new List<Wr>(UserPatch(m, c));
        w.AddRange(NineKeys(0, 0, _ => volume));
        var o = RunOracle(w, length);
        var mine = RunMine(w, length, p);
        var stats = Enumerable.Range(0, 9).Select(ch => Compare(i => OracleMelodic(o, ch, i), mine.Voice[ch], from, length - 3, MelodicLag[ch])).ToArray();
        int bad = stats.Count(s => s.Exact != s.Compared);
        Check(name, bad == 0, bad == 0 ? $"{stats.Sum(s => s.Compared)} samples, all exact" : $"{bad} of 9 channels differ; {Worst(stats)}");
    }

    private static void SteadyInfo(string name, Op m, Op c)
    {
        var w = new List<Wr>(UserPatch(m, c));
        w.AddRange(NineKeys(0, 0, _ => 0));
        var o = RunOracle(w, 1500);
        var mine = RunMine(w, 1500);
        var stats = Enumerable.Range(0, 9).Select(ch => Compare(i => OracleMelodic(o, ch, i), mine.Voice[ch], 700, 1497, MelodicLag[ch])).ToArray();
        Info(name, $"channels exact: {stats.Count(s => s.Exact == s.Compared)} of 9; {Worst(stats)}");
    }

    private static void Oscillator(Poison p)
    {
        for (int ml = 0; ml < 16; ml++) SteadyCase($"modulator multiplier {ml}", ModBase with { Ml = ml }, CarBase, p);
        for (int ml = 0; ml < 16; ml++) SteadyCase($"carrier multiplier {ml}", ModBase, CarBase with { Ml = ml }, p);
        for (int fb = 0; fb < 7; fb++) SteadyCase($"feedback {fb}", ModBase with { Fb = fb }, CarBase, p);
        // the chaotic corner (a full-level modulator feeding itself back at 7, or at total level 0 or 1) is exact on the channels where this chip and the oracle start the two operators on the very same sample, which depends on where the key-on falls: reported, not required
        SteadyInfo("feedback 7 (chaotic)", ModBase with { Fb = 7 }, CarBase);
        SteadyInfo("modulator total level 0 (chaotic with feedback 5)", ModBase with { Tl = 0 }, CarBase);
        for (int ws = 0; ws < 4; ws++) SteadyCase($"waveforms: modulator half-wave {(ws & 1) != 0}, carrier half-wave {(ws & 2) != 0}", ModBase with { Ws = ws & 1 }, CarBase with { Ws = ws >> 1 }, p);
        foreach (int tl in new[] { 2, 7, 16, 33, 63 }) SteadyCase($"modulator total level {tl}", ModBase with { Tl = tl }, CarBase, p);
        for (int v = 0; v < 16; v++) SteadyCase($"volume {v}", ModBase, CarBase, p, volume: v);
        for (int kl = 0; kl < 4; kl++) SteadyCase($"modulator key-scale level {kl}", ModBase with { Kl = kl }, CarBase, p);
        for (int kl = 0; kl < 4; kl++) SteadyCase($"carrier key-scale level {kl}", ModBase, CarBase with { Kl = kl }, p);
        SteadyCase("modulator vibrato", ModBase with { Pm = 1 }, CarBase, p, from: 300, length: 4500);
        SteadyCase("carrier vibrato", ModBase, CarBase with { Pm = 1 }, p, from: 300, length: 4500);
        SteadyCase("modulator tremolo", ModBase with { Am = 1 }, CarBase, p, from: 300, length: 4500);
        SteadyCase("carrier tremolo", ModBase, CarBase with { Am = 1 }, p, from: 300, length: 4500);
        SteadyCase("tremolo and vibrato on both", ModBase with { Am = 1, Pm = 1 }, CarBase with { Am = 1, Pm = 1 }, p, from: 300, length: 4500);
    }

    private static void Lfo(Poison p)
    {
        // the tremolo's whole period is 13,440 samples, the vibrato's 8,192
        SteadyCase("tremolo over its whole period", ModBase with { Am = 1 }, CarBase with { Am = 1 }, p, from: 300, length: 15000);
        SteadyCase("vibrato over its whole period", ModBase with { Pm = 1 }, CarBase with { Pm = 1 }, p, from: 300, length: 9000);
    }

    // ---- instruments

    private static void Instruments(Poison p)
    {
        const int length = 4200, keyOff = 2600;
        for (int inst = 1; inst <= 15; inst++)
        {
            var w = NineKeys(2, keyOff, _ => inst << 4);
            var o = RunOracle(w, length);
            var mine = RunMine(w, length, p);
            var stats = Enumerable.Range(0, 9).Select(ch => Compare(i => OracleMelodic(o, ch, i), mine.Voice[ch], 5, length - 3, MelodicLag[ch])).ToArray();
            var levels = Enumerable.Range(0, 9).Select(ch => LevelError(i => OracleMelodic(o, ch, i), mine.Voice[ch], 8, length - 8, MelodicLag[ch])).ToArray();
            double worstMax = levels.Max(l => l.Max), worstMean = levels.Max(l => l.Mean), meanExact = stats.Average(s => s.Percent);
            // bound: the loudness of every 128-sample window (where it is above 30 dB under full scale) is within 2 dB of the oracle's, and within 0.5 dB on average over the note, on every one of the nine pitches; at least 85% of the samples identical
            Check($"instrument {inst} on nine pitches", worstMax <= 2.0 && worstMean <= 0.5 && meanExact >= 85,
                $"level of 128-sample windows: largest difference {worstMax:F2} dB, worst channel's mean {worstMean:F2} dB; {meanExact:F1}% of samples exact; lowest SNR {stats.Min(s => s.SnrDb):F1} dB");
        }
    }

    // ---- envelope

    private static void EnvelopeCase(string name, Op car, int block, int fnum, int length, int keyOff, int sustain, Poison p, double minSnr, int maxError, double minPercent)
    {
        var w = new List<Wr>(UserPatch(ModSilent, car)) { new(0, 0x30, 0), new(0, 0x10, fnum & 0xFF), new(0, 0x20, 0x10 | sustain << 5 | block << 1 | fnum >> 8) };
        if (keyOff > 0) w.Add(new Wr(keyOff, 0x20, sustain << 5 | block << 1 | fnum >> 8));
        var o = RunOracle(w, length);
        var mine = RunMine(w, length, p);
        var s = Compare(i => OracleMelodic(o, 0, i), mine.Voice[0], 5, length - 3, MelodicLag[0]);
        Check(name, s.SnrDb >= minSnr && s.MaxError <= maxError && s.Percent >= minPercent, s.ToString());
    }

    private static void Envelope(Poison p)
    {
        foreach (int block in new[] { 3, 5 })
        {
            foreach (int ar in new[] { 15, 14, 13, 12, 11, 10, 9, 8, 7, 5, 3 })
                EnvelopeCase($"attack rate {ar}, block {block}", CarBase with { Ar = ar }, block, 0x0AD, ar >= 7 ? 2500 : 5000, 0, 0, p, 26, 30, 85);
            foreach (int dr in new[] { 15, 12, 10, 8, 6, 4, 2 })
                EnvelopeCase($"decay rate {dr} to sustain level 6, then release, block {block}", CarBase with { Eg = 0, Dr = dr, Sl = 6, Rr = 6 }, block, 0x0AD, 3500, 2500, 0, p, 26, 30, 85);
            foreach (int rr in new[] { 15, 12, 9, 6, 3, 1 })
                EnvelopeCase($"release rate {rr} after a sustained note, block {block}", CarBase with { Rr = rr }, block, 0x0AD, 3500, 1200, 0, p, 26, 30, 60);
            EnvelopeCase($"sustain flag on at key-off, block {block}", CarBase with { Rr = 8 }, block, 0x0AD, 3500, 1200, 1, p, 26, 30, 85);
            foreach (int kr in new[] { 0, 1 })
                EnvelopeCase($"rate key scaling {kr} (attack 10, decay 7, sustain 4), block {block}", CarBase with { Eg = 0, Ar = 10, Dr = 7, Sl = 4, Rr = 8, Kr = kr }, block, 0x0AD, 3500, 2500, 0, p, 26, 30, 80);
        }
    }

    // ---- rhythm

    private static void Rhythm5(Poison p)
    {
        for (int v = 0; v < 5; v++)
        {
            var w = new List<Wr>
            {
                new(0, 0x16, 0x20), new(0, 0x17, 0x50), new(0, 0x18, 0xC0), new(0, 0x26, 0x05), new(0, 0x27, 0x05), new(0, 0x28, 0x01), new(0, 0x36, 0), new(0, 0x37, 0), new(0, 0x38, 0),
                new(0, 0x0E, 0x20), new(200, 0x0E, 0x20 | RhythmKey[v]), new(2000, 0x0E, 0x20),
            };
            const int length = 3000;
            var o = RunOracle(w, length);
            var mine = RunMine(w, length, p);
            int voice = RhythmVoice[v];
            bool same = true;
            for (int i = 300; i < length - 2 && same; i++) same = v == 4 || o.At(i, RhythmClock[v], 1) == o.At(i, RhythmClockAgain[v], 1);
            var s = Compare(i => OracleRhythm(o, v, i), mine.Voice[voice], 300, length - 2);
            var held = Compare(i => OracleRhythm(o, v, i), mine.Voice[voice], 300, 1990);   // from the key-on until just before the key-off
            double rmsO = Math.Sqrt(Enumerable.Range(300, 400).Average(i => Math.Pow(OracleRhythm(o, v, i), 2))), rmsM = Math.Sqrt(Enumerable.Range(300, 400).Average(i => Math.Pow(mine.Voice[voice][i - 1], 2)));
            double db = 20 * Math.Log10(Math.Max(rmsM, 1e-9) / rmsO);
            if (v == 0) Check($"{RhythmName[v]} by sample", s.SnrDb >= 30 && s.Percent >= 95, s.ToString() + (same ? "; the oracle's two appearances of it are equal" : "; the oracle's two appearances differ"));
            else if (v == 2) Check($"{RhythmName[v]} by sample, from key-on to key-off (its release is the oracle's own, see the record)", held.SnrDb >= 30 && held.Percent >= 95, held.ToString());
            else Check($"{RhythmName[v]}: level (its noise is not the oracle's)", Math.Abs(db) <= 1.0, $"RMS {rmsO:F1} (oracle) vs {rmsM:F1} (this chip): {db:+0.00;-0.00} dB; {s.Percent:F1}% of samples equal");
        }
    }

    // ---- whole-chip pitch

    private static void Pitch(Poison p)
    {
        // all nine channels keyed at once at nine pitches with a plain sine each: a wrong frequency step shows within a few samples
        var w = new List<Wr>(UserPatch(ModSilent, CarBase));
        w.AddRange(NineKeys(2, 0, _ => 0));
        const int length = 2600;
        var o = RunOracle(w, length);
        var mine = RunMine(w, length, p);
        var stats = Enumerable.Range(0, 9).Select(ch => Compare(i => OracleMelodic(o, ch, i), mine.Voice[ch], 20, length - 3, MelodicLag[ch])).ToArray();
        for (int ch = 0; ch < 9; ch++) Check($"channel {ch + 1} (block {Pitches[ch].Block}, f-number {Pitches[ch].Fnum:X3}) follows the oracle", stats[ch].SnrDb >= 30 && stats[ch].Percent >= 90, stats[ch].ToString());
    }

    // ---------------------------------------------------------------------------------------------- the patch fit

    /// <summary>
    /// <c>--fm-oracle-test --patch-fit 4,5,...</c>: finds the eight bytes of a built-in instrument as the oracle plays it. The oracle plays instrument n on three channels at three pitches (key-on, hold, key-off); this chip plays the user instrument
    /// (registers 0-7) at the same pitches with candidate bytes, field by field (then pair by pair) over the values each field can take, keeping any value that lowers the squared difference, until nothing improves. The starting point is the instrument ROM this
    /// chip carries. Used to find which of the ROM's instruments the oracle's YM2413 plays differently (see the ledger); prints the bytes and how many samples agree.
    /// </summary>
    private static int PatchFit(string list, string? exhaustiveFields = null, string? startBytes = null)
    {
        (int Block, int Fnum)[] pitches = { (3, 0x0AD), (1, 0x150), (5, 0x08A) };
        (int B, int Lo, int W)[] fields =
        {
            (0, 7, 1), (0, 6, 1), (0, 5, 1), (0, 4, 1), (0, 0, 4), (1, 7, 1), (1, 6, 1), (1, 5, 1), (1, 4, 1), (1, 0, 4),
            (2, 6, 2), (2, 0, 6), (3, 6, 2), (3, 4, 1), (3, 3, 1), (3, 0, 3), (4, 4, 4), (4, 0, 4), (5, 4, 4), (5, 0, 4), (6, 4, 4), (6, 0, 4), (7, 4, 4), (7, 0, 4),
        };
        foreach (int inst in list.Split(',').Select(int.Parse))
        {
            byte[] rom = Ym2413.RomPatches.Skip(inst * 8).Take(8).ToArray();
            List<Wr> Script(byte[] patch, int instrument)
            {
                var w = new List<Wr>();
                for (int r = 0; r < 8; r++) w.Add(new Wr(0, r, patch[r]));
                for (int ch = 0; ch < 3; ch++) { w.Add(new Wr(0, 0x30 + ch, instrument << 4)); w.Add(new Wr(0, 0x10 + ch, pitches[ch].Fnum & 0xFF)); }
                for (int ch = 0; ch < 3; ch++) w.Add(new Wr(300, 0x20 + ch, 0x10 | pitches[ch].Block << 1 | pitches[ch].Fnum >> 8));
                for (int ch = 0; ch < 3; ch++) w.Add(new Wr(2600, 0x20 + ch, pitches[ch].Block << 1 | pitches[ch].Fnum >> 8));
                return w;
            }
            const int length = 3400;
            var oracle = RunOracle(Script(rom, inst), length);
            var plan = PlanWrites(Script(rom, 0));
            var mine = new Mine(length);
            (long Error, int Equal) Evaluate(byte[] patch)
            {
                for (int i = 0; i < plan.Count; i++) if (plan[i].W.Reg < 8) plan[i] = (plan[i].S, plan[i].W with { Val = patch[plan[i].W.Reg] });
                Play(plan, length, Poison.None, mine);
                long e = 0; int eq = 0;
                for (int ch = 0; ch < 3; ch++) for (int i = 20; i < length - 3; i++) { int d = OracleMelodic(oracle, ch, i) - mine.Voice[ch][i - MelodicLag[ch]]; e += (long)d * d; if (d == 0) eq++; }
                return (e, eq);
            }
            int total = 3 * (length - 23);
            if (startBytes != null) rom = Convert.FromHexString(startBytes);
            var (bestError, equal) = Evaluate(rom);
            Console.WriteLine($"instrument {inst}: the ROM's bytes {Convert.ToHexString(rom)} agree on {equal}/{total}");
            if (exhaustiveFields != null)
            {
                // every combination of the named fields (indices into the field list), the others as they are
                var idx = exhaustiveFields.Split(',').Select(int.Parse).ToArray();
                var baseBytes = (byte[])rom.Clone();
                long combos = 1; foreach (int i in idx) combos *= 1L << fields[i].W;
                Console.WriteLine($"   exhaustive over fields {exhaustiveFields}: {combos} combinations");
                for (long c = 0; c < combos; c++)
                {
                    var cand = (byte[])baseBytes.Clone(); long rest = c;
                    foreach (int i in idx)
                    {
                        var f = fields[i]; int v = (int)(rest & ((1 << f.W) - 1)); rest >>= f.W;
                        cand[f.B] = (byte)((cand[f.B] & ~(((1 << f.W) - 1) << f.Lo)) | v << f.Lo);
                    }
                    var (e, eq) = Evaluate(cand);
                    if (eq > equal || (eq == equal && e < bestError)) { bestError = e; equal = eq; rom = cand; Console.WriteLine($"   {Convert.ToHexString(rom)}: {eq}/{total} equal"); }
                }
            }
            bool changed = exhaustiveFields == null;
            for (int pass = 0; pass < 5 && changed && equal < total; pass++)
            {
                changed = false;
                foreach (var f in fields)
                {
                    int mask = ((1 << f.W) - 1) << f.Lo;
                    for (int v = 0; v < 1 << f.W; v++)
                    {
                        byte old = rom[f.B]; int cand = (old & ~mask) | v << f.Lo;
                        if (cand == old) continue;
                        rom[f.B] = (byte)cand;
                        var (e, eq) = Evaluate(rom);
                        if (eq > equal || (eq == equal && e < bestError)) { bestError = e; equal = eq; changed = true; Console.WriteLine($"   byte {f.B} bits {f.Lo}+{f.W} = {v}: error {e}, {eq} equal"); }
                        else rom[f.B] = old;
                    }
                }
                if (changed || equal == total) continue;
                // no single field helps: try every pair of fields (two fields wrong at once)
                for (int x = 0; x < fields.Length && !changed; x++)
                    for (int y = x + 1; y < fields.Length && !changed; y++)
                    {
                        var fx = fields[x]; var fy = fields[y];
                        int mx = ((1 << fx.W) - 1) << fx.Lo, my = ((1 << fy.W) - 1) << fy.Lo;
                        byte ox = rom[fx.B], oy = rom[fy.B];
                        for (int vx = 0; vx < 1 << fx.W && !changed; vx++)
                            for (int vy = 0; vy < 1 << fy.W && !changed; vy++)
                            {
                                if (fx.B == fy.B) rom[fx.B] = (byte)((ox & ~mx & ~my) | vx << fx.Lo | vy << fy.Lo);
                                else { rom[fx.B] = (byte)((ox & ~mx) | vx << fx.Lo); rom[fy.B] = (byte)((oy & ~my) | vy << fy.Lo); }
                                if (rom[fx.B] == ox && rom[fy.B] == oy) continue;
                                var (e, eq) = Evaluate(rom);
                                if (eq > equal || (eq == equal && e < bestError)) { bestError = e; equal = eq; changed = true; Console.WriteLine($"   pair {x}/{y}: bytes {Convert.ToHexString(rom)} error {e}, {eq} equal"); }
                                else { rom[fx.B] = ox; rom[fy.B] = oy; }
                            }
                    }
            }
            Evaluate(rom);
            var perChannel = Enumerable.Range(0, 3).Select(ch => Enumerable.Range(20, length - 23).Count(i => OracleMelodic(oracle, ch, i) == mine.Voice[ch][i - MelodicLag[ch]])).ToArray();
            Console.WriteLine($"instrument {inst}: bytes {Convert.ToHexString(rom)} agree on {equal}/{total} ({100.0 * equal / total:F2}%), squared error {bestError}; per pitch {string.Join(" ", perChannel)} of {length - 23}");
        }
        return 0;
    }

    // ---------------------------------------------------------------------------------------------- the dump tool

    private static int Dump(string scriptPath, string outPath, string clock, string[] opts)
    {
        var lines = File.ReadAllLines(scriptPath);
        int samples = int.Parse(lines[0].Trim());
        var writes = new List<(int S, int R, int V)>();
        for (int i = 1; i < lines.Length; i++)
        {
            var p = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 3) writes.Add((int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2])));
        }
        var chip = new Ym2413(double.Parse(clock, System.Globalization.CultureInfo.InvariantCulture));
        uint pmStart = unchecked((uint)-1); int amStart = -1; uint egStart = 0; int noiseStart = 0;
        foreach (var o in opts)
        {
            var kv = o.Split('=');
            if (kv[0] == "pm") pmStart = (uint)int.Parse(kv[1]);
            if (kv[0] == "am") amStart = int.Parse(kv[1]);
            if (kv[0] == "eg") egStart = (uint)int.Parse(kv[1]);
            if (kv[0] == "noise") noiseStart = int.Parse(kv[1]);
        }
        chip.SetLfoPhase(pmStart, amStart); chip.EnvelopeCounter = egStart; chip.AdvanceNoise(noiseStart);
        using var w = new BinaryWriter(File.Create(outPath));
        int next = 0;
        for (int i = 0; i < samples; i++)
        {
            while (next < writes.Count && writes[next].S <= i) { chip.WriteRegister(writes[next].R, (byte)writes[next].V); next++; }
            chip.Calc();
            int m = 0, r = 0;
            for (int v = 0; v < 14; v++) { int o = chip.ChannelOutput(v); w.Write(o); if (v < 9) m += o; else r += o; }
            w.Write(m); w.Write(r);
            var c0 = chip.Operator(0, true); var m0 = chip.Operator(0, false);
            w.Write(c0.Phase); w.Write(c0.Envelope); w.Write(c0.Level); w.Write(c0.State); w.Write(m0.Envelope); w.Write(m0.State);
            w.Write(chip.Operator(7, true).Phase); w.Write(chip.Operator(7, false).Phase); w.Write(chip.Operator(8, false).Phase); w.Write(chip.Operator(8, true).Phase);
        }
        return 0;
    }
}
