using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>A sound unit that forwards to an SMS-FM unit but drops the FM data port ($F1) and reads $F2 back with bit 0 wrong: the proof that the fm suite can fail.</summary>
internal sealed class FmFault : ISmsSound
{
    public SmsFmSound Inner { get; }
    public FmFault(SmsFmSound inner) { Inner = inner; }
    public string CoreName => Inner.CoreName + " (poisoned)";
    public int SampleRate => Inner.SampleRate;
    public void Reset() => Inner.Reset();
    public void Run(int cpuClocks) => Inner.Run(cpuClocks);
    public void WritePsg(byte value) => Inner.WritePsg(value);
    public void WriteStereo(byte value) => Inner.WriteStereo(value);
    public bool HasFm => true;
    public void WriteFmAddress(byte value) => Inner.WriteFmAddress(value);
    public void WriteFmData(byte value) { }
    public void WriteAudioControl(byte value) => Inner.WriteAudioControl(value);
    public byte ReadAudioControl() => (byte)(Inner.ReadAudioControl() ^ 0x01);   // and bit 0 of the read-back wrong
    public int ReadSamples(Span<short> dest) => Inner.ReadSamples(dest);
    public int DescribeVoices(Span<HubVoice> voices) => Inner.DescribeVoices(voices);
    public void SaveState(BinaryWriter w) => Inner.SaveState(w);
    public void LoadState(BinaryReader r) => Inner.LoadState(r);
    public string Describe() => Inner.Describe();
}

/// <summary>
/// The YM2413 on the board (<c>SMS-FM</c>): how a game finds it and talks to it, what comes out of the delivered audio, and the chip against the die-derived oracle.
/// <list type="bullet">
/// <item><b>Detection and ports</b>, against what the oracle (Mesen 2.1.1, with and without its FM chip) answers to the same probe programs (FmProbes; the numbers below are the oracle's, each measured with the probe
/// run on it: all 256 values written to $F2 and read back, the power-on reads, the pad mirrors, the sequences of $F2/$F0/$F1 writes).</item>
/// <item><b>Registers</b>: the register file after a program equals the oracle's after the same program (every one of the 64 registers, the address latch, the key state).</item>
/// <item><b>Audio</b> on the delivered stream: pitch (the chip's formula, F-number x 2^block x multiplier x clock / 72 / 2^19), 3 dB per volume step, the full-scale sine against the amplitude the oracle's chip shows,
/// the audio-control bits gating FM and PSG, rhythm mode, the voices the hub sees, an APU swap in a running game, the unit's state round trip, no allocation, and speed.
/// What the two audio-control bits do to the MIX is the documented convention (bit 0 FM on, bit 1 PSG off), not compared with a recording (the oracle's audio cannot be captured here).</item>
/// <item><b>The chip</b> against Nuked-OPLL, sample for sample: the groups of <c>--fm-oracle-test</c> run here too (skipped, not passed, when the oracle is not built).</item>
/// </list>
/// </summary>
internal static class FmSuite
{
    private const double Z80Clock = 53693175.0 / 15;
    private const double ChipRate = Z80Clock / 72;

    private static SmsSession Make(SmsSuiteContext ctx, byte[] rom, string apu = "SMS-FM", ConsoleKind console = ConsoleKind.MasterSystem)
    {
        var s = ctx.Session(rom, "fm.sms", console, null, apu);
        if (ctx.Poison == "fm" && s.Board.Sound is SmsFmSound inner) s.Board.SwapSound(new FmFault(inner));
        return s;
    }

    private static Ym2413? FmOf(SmsSession s) => s.Board.Sound switch { SmsFmSound f => f.Fm, FmFault p => p.Inner.Fm, _ => null };

    private static SmsSession Run(SmsSuiteContext ctx, byte[] rom, string apu, int frames, PadButtons pad = 0)
    {
        var s = Make(ctx, rom, apu);
        s.SetPad(0, pad);
        for (int f = 0; f < frames; f++) s.RunFrame();
        return s;
    }

    private static double Hz(int fnum, int block, int ml) => fnum * (1 << block) * ChipRate / 524288.0 * ml;

    /// <summary>The peak amplitude of the component at <paramref name="hz"/> (correlation with a sine and a cosine over the whole window).</summary>
    private static double Amp(short[] x, double hz, int rate)
    {
        double s = 0, c = 0;
        for (int i = 0; i < x.Length; i++) { double ph = 2 * Math.PI * hz * i / rate; s += x[i] * Math.Sin(ph); c += x[i] * Math.Cos(ph); }
        return 2 * Math.Sqrt(s * s + c * c) / x.Length;
    }

    private static List<short> Render(ISmsSound u, int totalClocks, int chunk = 500)
    {
        var all = new List<short>(); var buf = new short[4096];
        for (int done = 0; done < totalClocks; done += chunk)
        {
            u.Run(chunk);
            int n;
            while ((n = u.ReadSamples(buf)) > 0) for (int i = 0; i < n; i++) all.Add(buf[i]);
        }
        return all;
    }

    private static void Write(ISmsSound u, IEnumerable<(int Reg, int Val)> regs) { foreach (var (r, v) in regs) { u.WriteFmAddress((byte)r); u.WriteFmData((byte)v); } }

    // the register file the oracle holds after FmProbes.Tone(1, 290, 4) (state dump, frame 20): registers 0-7, 0x10, 0x20, 0x30; everything else 0; address latch $20; slots 0 and 1 keyed
    private static readonly (int Reg, int Val)[] OracleToneRegs = { (0, 33), (1, 33), (2, 63), (3, 0), (4, 240), (5, 240), (6, 0), (7, 0), (16, 34), (32, 25), (48, 0) };

    public static IEnumerable<SmsCheck> Run(SmsSuiteContext ctx)
    {
        // ---------------------------------------------------------------- detection and ports (the oracle's answers)
        {
            var s = Run(ctx, FmProbes.F2Readback(true), "SMS-FM", 5); var ram = s.Board.Ram;
            yield return new SmsCheck("I/O chip off, FM unit present: the power-on reads are $DC $DD $F0 $F1 = $FF and $F2 = 0 (oracle: Mesen with FM)", ram[0xFA] == 0xFF && ram[0xFB] == 0xFF && ram[0xFC] == 0xFF && ram[0xFD] == 0xFF && ram[0xFE] == 0x00, $"{ram[0xFA]:X2} {ram[0xFB]:X2} {ram[0xFC]:X2} {ram[0xFD]:X2} {ram[0xFE]:X2}");
            int bad = Enumerable.Range(0, 256).Count(v => ram[0x100 + v] != (v & 3));
            yield return new SmsCheck("I/O chip off, FM unit present: every value 0..255 written to $F2 reads back masked to bits 0-1 (oracle: all 256)", bad == 0, $"{bad} of 256 differ");
            var n = Run(ctx, FmProbes.F2Readback(true), "SMS", 5); var nram = n.Board.Ram;
            int notFf = Enumerable.Range(0, 256).Count(v => nram[0x100 + v] != 0xFF) + Enumerable.Range(0xFA, 5).Count(i => nram[i] != 0xFF);
            yield return new SmsCheck("I/O chip off, no FM unit (the stock SMS sound): $F2 reads $FF whatever was written, and so do $F0, $F1, $DC, $DD (oracle: Mesen without FM)", notFf == 0, $"{notFf} reads not $FF");
        }
        {
            // I/O chip on: $F0 and $F2 are mirrors of $DC, $F1 of $DD, with the unit or without
            foreach (string apu in new[] { "SMS-FM", "SMS" })
            {
                var s = Run(ctx, FmProbes.SequenceAfter(true, false, (0xF2, 0x01)), apu, 45, PadButtons.Right | PadButtons.A); var ram = s.Board.Ram;
                yield return new SmsCheck($"I/O chip on, {(apu == "SMS" ? "stock unit" : "FM unit")}, Right and button 1 held: $F2 and $F0 read as $DC ($E7), $F1 as $DD ($FF), whatever $F2 was set to (oracle)", ram[0xF0] == 0xE7 && ram[0xF1] == 0xE7 && ram[0xF2] == 0xFF && ram[0xF3] == 0xE7, $"F2 {ram[0xF0]:X2} F0 {ram[0xF1]:X2} F1 {ram[0xF2]:X2} DC {ram[0xF3]:X2}");
            }
            // sequences with the I/O chip off: what $F2 reads after each (the oracle's RAM images matched on all six)
            var expect = new Dictionary<string, int> { ["none"] = 0x00, ["f2-0-reg"] = 0x00, ["f2-1-addr"] = 0x01, ["f2-1-reg"] = 0x01, ["f2-3-reg"] = 0x03, ["reg-only"] = 0x00 };
            int wrong = 0; string first = "";
            foreach (var (name, seq) in FmProbes.SequenceProbes)
            {
                var s = Run(ctx, FmProbes.SequenceAfter(false, true, seq), "SMS-FM", 5); var ram = s.Board.Ram;
                bool ok = ram[0xF0] == expect[name] && ram[0xF1] == 0xFF && ram[0xF2] == 0xFF && ram[0xF3] == 0xFF;
                if (!ok) { wrong++; if (first.Length == 0) first = $"{name}: {ram[0xF0]:X2} {ram[0xF1]:X2} {ram[0xF2]:X2} {ram[0xF3]:X2}"; }
            }
            yield return new SmsCheck("six write sequences ($F2, $F0, $F1 in the orders games use) with the I/O chip off: $F2 reads what was written to it (0 after none or a register write alone), $F0, $F1, $DC read $FF (oracle)", wrong == 0, first);
        }

        // ---------------------------------------------------------------- the register path
        {
            var s = Run(ctx, FmProbes.Tone(1, 290, 4), "SMS-FM", 10);
            var fm = FmOf(s);
            var want = new int[64]; foreach (var (r, v) in OracleToneRegs) want[r] = v;
            int diff = fm == null ? -1 : Enumerable.Range(0, 64).Count(i => fm.Register(i) != want[i]);
            yield return new SmsCheck("a program writing the user instrument, F-number, block, key-on through $F0/$F1 leaves all 64 registers as the oracle holds them", diff == 0, diff < 0 ? "no FM unit" : $"{diff} of 64 registers differ");
            yield return new SmsCheck("the address latch holds the last address written ($20) and only channel 0 is keyed (oracle: address 32, key status of slots 0 and 1)", fm != null && fm.AddressLatch == 0x20 && fm.ChannelKeyOn(0) && Enumerable.Range(1, 8).All(c => !fm.ChannelKeyOn(c)) && !fm.RhythmMode, fm == null ? "" : $"latch {fm.AddressLatch:X2}");
            var n = Run(ctx, FmProbes.Tone(1, 290, 4), "SMS", 10);
            yield return new SmsCheck("a console without the FM unit ignores $F0-$F2 (no sound unit state to change, no crash)", !n.Board.Sound.HasFm && FmOf(n) == null, n.Board.Sound.CoreName);
        }

        // ---------------------------------------------------------------- the chip's scale: a full-volume sine peaks where the oracle's chip does
        {
            var chip = new Ym2413(Z80Clock); chip.Reset(); chip.ResetPatch();
            foreach (var (r, v) in FmProbes.ToneRegisters(290, 4, 0, 1)) chip.WriteRegister(r, (byte)v);
            int max = int.MinValue, min = int.MaxValue;
            for (int i = 0; i < 60000; i++) { int x = chip.Calc(); if (i > 2000) { max = Math.Max(max, x); min = Math.Min(min, x); } }
            yield return new SmsCheck("a full-volume sine peaks at +-2036 to 2047 in the chip's output scale (the oracle's emu2413 chip shows +2043 and -2037 over 141 sampled instants)", max is >= 2036 and <= 2047 && min is <= -2030 and >= -2047, $"max {max}, min {min}");
        }

        // ---------------------------------------------------------------- pitch and volume on the delivered audio
        foreach (var (fnum, block, ml) in new[] { (290, 4, 1), (511, 2, 1), (290, 4, 2), (100, 6, 1) })
        {
            var s = Run(ctx, FmProbes.Tone(1, fnum, block, 0, ml), "SMS-FM", 0);
            var (l, r) = PsgSuite.Capture(s);
            double want = Hz(fnum, block, ml), f = PsgSuite.Frequency(l, s.SampleRate);
            yield return new SmsCheck($"FM tone, F-number {fnum}, block {block}, multiplier {ml}: {want:F1} Hz (F-number x 2^block x multiplier x clock / 72 / 2^19)", Math.Abs(f - want) < want * 0.006 && PsgSuite.Peak(l) > 3000, $"measured {f:F1} Hz, peak-to-peak {PsgSuite.Peak(l)}");
            if (fnum == 290 && block == 4 && ml == 1)
            {
                double amp = Amp(l, want, s.SampleRate);
                yield return new SmsCheck("the delivered full-volume sine carries the chip's peak (2043) to within 4% (the resampler is flat at 440 Hz)", amp is > 1960 and < 2110, $"amplitude {amp:F0}");
                yield return new SmsCheck("the Master System is mono: both channels carry the FM sound equally", l.SequenceEqual(r), "");
            }
        }
        {
            double a0 = 0;
            foreach (int v in new[] { 0, 2, 4, 8 })
            {
                var s = Run(ctx, FmProbes.Tone(1, 290, 4, v), "SMS-FM", 0);
                var (l, _) = PsgSuite.Capture(s);
                double a = Amp(l, Hz(290, 4, 1), s.SampleRate);
                if (v == 0) { a0 = a; continue; }
                double want = Math.Pow(10, -3.0 * v / 20);
                yield return new SmsCheck($"volume {v}: {-3 * v} dB = {want:F3} of full (3 dB per step of the YM2413's volume nibble)", Math.Abs(a / a0 - want) < 0.06 * want, $"ratio {a / Math.Max(1, a0):F3}");
            }
        }

        // ---------------------------------------------------------------- the audio control: FM 440 Hz and PSG 330 Hz together
        {
            var amps = new Dictionary<int, (double Fm, double Psg)>();
            foreach (int c in new[] { 0, 1, 2, 3 })
            {
                var s = Run(ctx, FmProbes.Tone(c, 290, 4, 0, 1, 339), "SMS-FM", 0);
                var (l, _) = PsgSuite.Capture(s);
                amps[c] = (Amp(l, Hz(290, 4, 1), s.SampleRate), Amp(l, Z80Clock / (32.0 * 339), s.SampleRate));
            }
            static bool On(double a) => a > 800;
            static bool Off(double a) => a < 30;
            yield return new SmsCheck("$F2 = 0 (power-on): the PSG plays, the FM chip is silent", On(amps[0].Psg) && Off(amps[0].Fm), $"FM {amps[0].Fm:F0} PSG {amps[0].Psg:F0}");
            yield return new SmsCheck("$F2 = 1: FM and PSG play together", On(amps[1].Psg) && On(amps[1].Fm), $"FM {amps[1].Fm:F0} PSG {amps[1].Psg:F0}");
            yield return new SmsCheck("$F2 = 2: FM off and PSG off, silence", Off(amps[2].Psg) && Off(amps[2].Fm), $"FM {amps[2].Fm:F0} PSG {amps[2].Psg:F0}");
            yield return new SmsCheck("$F2 = 3: FM only", Off(amps[3].Psg) && On(amps[3].Fm), $"FM {amps[3].Fm:F0} PSG {amps[3].Psg:F0}");
            yield return new SmsCheck("the mix adds the two without changing either (each part within 3% of what it is alone)", Math.Abs(amps[1].Fm / Math.Max(1, amps[3].Fm) - 1) < 0.03 && Math.Abs(amps[1].Psg / Math.Max(1, amps[0].Psg) - 1) < 0.03, $"FM {amps[1].Fm / Math.Max(1, amps[3].Fm):F3}, PSG {amps[1].Psg / Math.Max(1, amps[0].Psg):F3}");
        }

        // ---------------------------------------------------------------- rhythm mode
        {
            var on = Run(ctx, FmProbes.BassDrum(true), "SMS-FM", 0); var off = Run(ctx, FmProbes.BassDrum(false), "SMS-FM", 0);
            double rOn = PsgSuite.Rms(PsgSuite.Capture(on, 2, 12).L), rOff = PsgSuite.Rms(PsgSuite.Capture(off, 2, 12).L);
            yield return new SmsCheck("rhythm mode: the bass drum sounds when keyed and is silent when it is not", rOn > 100 && rOff < 5 && FmOf(on)?.RhythmMode == true, $"rms keyed {rOn:F0}, not keyed {rOff:F1}");
        }

        // ---------------------------------------------------------------- what the hub sees
        {
            var s = Run(ctx, FmProbes.NineChannels(out int[] fnums), "SMS-FM", 8);
            var v = new HubVoice[24]; int n = s.Board.Sound.DescribeVoices(v);
            bool pitches = n == 18;
            string worst = "";
            if (pitches) for (int k = 0; k < 9; k++) { double want = Hz(fnums[k], 3, 1), got = v[4 + k].Hz; if (Math.Abs(got - want) > want * 0.003 || !v[4 + k].Audible) { pitches = false; worst = $"channel {k + 1}: {got:F1} Hz, want {want:F1}"; break; } }
            yield return new SmsCheck("DescribeVoices lists 4 PSG + 14 FM voices, and nine sounding channels report their pitch (the hub / plugin contract)", pitches, n != 18 ? $"{n} voices" : worst);
            var silent = Run(ctx, FmProbes.NineChannels(out _), "SMS-FM", 8);
            silent.Board.Sound.WriteAudioControl(0);                 // the program wrote $F2 = 1; clear it and the FM voices read silent
            for (int f = 0; f < 2; f++) silent.RunFrame();
            var v2 = new HubVoice[24]; silent.Board.Sound.DescribeVoices(v2);
            yield return new SmsCheck("with FM switched off ($F2 bit 0 clear) the FM voices report silent", Enumerable.Range(4, 14).All(i => !v2[i].Audible), "");
        }

        // ---------------------------------------------------------------- an APU swap in a running game
        {
            var s = Make(ctx, FmProbes.Tone(1, 290, 4, 0, 1, 339), "SMS");
            for (int f = 0; f < 3; f++) s.RunFrame();
            bool toFm = s.TrySwapCore(CoreSlot.Apu, "SMS-FM");
            bool hasFm = s.Board.Sound.HasFm;
            var (l, _) = PsgSuite.Capture(s, 2, 20);
            double psg = Amp(l, Z80Clock / (32.0 * 339), s.SampleRate), fmA = Amp(l, Hz(290, 4, 1), s.SampleRate);
            yield return new SmsCheck("APU swap SMS to SMS-FM in a running game: the unit has the chip, the PSG tone carries on, the FM chip starts silent (the game wrote its registers before)", toFm && hasFm && psg > 800 && fmA < 30, $"PSG {psg:F0}, FM {fmA:F0}");
            bool back = s.TrySwapCore(CoreSlot.Apu, "SMS");
            var (l2, _) = PsgSuite.Capture(s, 2, 20);
            yield return new SmsCheck("and back to SMS: no chip, the PSG tone still plays; an unknown unit id is refused, not replaced", back && !s.Board.Sound.HasFm && Amp(l2, Z80Clock / (32.0 * 339), s.SampleRate) > 800 && !s.TrySwapCore(CoreSlot.Apu, "NO-SUCH-UNIT"), "");
        }

        // ---------------------------------------------------------------- state, allocation, speed
        {
            var a = new SmsFmSound(Z80Clock, 44100);
            a.WriteAudioControl(1);
            Write(a, FmProbes.ToneRegisters(290, 4, 0, 1));
            a.WritePsg(0x83); a.WritePsg(0x15); a.WritePsg(0x90);
            Render(a, 60000);
            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) a.SaveState(w);
            var after = Render(a, 120000);                                     // what the running unit does next
            var b = new SmsFmSound(Z80Clock, 44100);
            ms.Position = 0;
            using (var r = new BinaryReader(ms)) b.LoadState(r);
            var again = Render(b, 120000);                                     // what the restored unit does
            bool same = after.Count == again.Count && after.SequenceEqual(again);
            yield return new SmsCheck("the sound unit's state round trip: a unit restored mid-note produces the same samples, bit for bit, as the one that carried on (FM, PSG, resampler)", same && after.Count > 1000, $"{after.Count} vs {again.Count} samples");
        }
        {
            var u = new SmsFmSound(Z80Clock, 44100);
            u.WriteAudioControl(1);
            for (int k = 0; k < 9; k++) Write(u, new[] { (0x30 + k, 0x10), (0x10 + k, (0x50 + 0x20 * k) & 0xFF), (0x20 + k, 0x16 + ((0x50 + 0x20 * k) >> 8)) });
            var buf = new short[4096];
            for (int i = 0; i < 20; i++) { u.Run(59736); while (u.ReadSamples(buf) > 0) { } }   // warm up (JIT)
            var sw = new System.Diagnostics.Stopwatch();
            long before = GC.GetAllocatedBytesForCurrentThread();
            sw.Start();
            const int frames = 300;
            for (int i = 0; i < frames; i++) { u.Run(59736); while (u.ReadSamples(buf) > 0) { } }
            double secs = sw.Elapsed.TotalSeconds; long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            double times = frames / 59.92 / secs;
            yield return new SmsCheck($"the sound unit with nine FM channels sounding runs {times:F0}x real time alone and allocates nothing while it runs (allocated {bytes} bytes over {frames} frames)", bytes == 0 && times >= 25, $"{times:F0}x, {bytes} bytes");
        }
        {
            double best = 0;
            for (int run = 0; run < 3; run++)
            {
                var s = Make(ctx, FmProbes.NineChannels(out _), "SMS-FM");
                var buf = new short[16384];
                for (int f = 0; f < 60; f++) { s.RunFrame(); while (s.ReadSamples(buf) > 0) { } }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int f = 0; f < 400; f++) { s.RunFrame(); while (s.ReadSamples(buf) > 0) { } }
                best = Math.Max(best, 400 / sw.Elapsed.TotalSeconds / s.FramesPerSecond);
            }
            double target = ctx.Poison == "fm" ? 1e6 : 8.0;
            yield return new SmsCheck($"throughput of the whole machine with the FM unit and nine channels sounding: {best:F1}x real time on one core (target {target:F0}x or more)", best >= target, $"{best:F1}x");
        }

        // ---------------------------------------------------------------- the chip against the oracle
        if (ctx.Poison != "fm")
        {
            var groups = FmOracleTestCli.RunForSuite(ctx.Tools);
            if (groups == null) yield return SmsCheck.Skipped("YM2413 against Nuked-OPLL, sample for sample", "the oracle is not built (Workshop\\Sega\\build-oracle-opll.ps1): not a pass");
            else foreach (var g in groups) yield return new SmsCheck($"YM2413 against Nuked-OPLL, sample for sample: group {g.Id} ({g.Passed + g.Failed} checks)", g.Failed == 0 && g.Passed > 0, $"{g.Passed} pass, {g.Failed} fail");
        }
    }
}
