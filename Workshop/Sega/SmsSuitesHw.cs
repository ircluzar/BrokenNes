using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>Small Z80 programs assembled by hand for the verifiers (no assembler, no ROM from outside: the bytes are in the source, with the instruction beside them).</summary>
internal static class SmsProg
{
    /// <summary>A 32 KB cartridge: <c>JP mainAt</c> at $0000, the IM 1 handler at $0038, the NMI handler at $0066, the program at <paramref name="mainAt"/>.</summary>
    public static byte[] Rom(byte[] main, byte[]? irq = null, byte[]? nmi = null, int mainAt = 0x100)
    {
        var b = new byte[0x8000];
        b[0] = 0xC3; b[1] = (byte)mainAt; b[2] = (byte)(mainAt >> 8);
        irq?.CopyTo(b, 0x38); nmi?.CopyTo(b, 0x66);
        main.CopyTo(b, mainAt);
        return b;
    }

    public static byte[] B(params int[] v) => v.Select(x => (byte)x).ToArray();
    public static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>DI; IM 1; LD SP,$DFF0.</summary>
    public static readonly byte[] Prolog = B(0xF3, 0xED, 0x56, 0x31, 0xF0, 0xDF);
    /// <summary>JR $ (the program is done).</summary>
    public static readonly byte[] Halt = B(0x18, 0xFE);

    /// <summary>LD A,value; OUT (port),A for each write.</summary>
    public static byte[] Out(params (int Port, int Value)[] writes) => Cat(writes.Select(w => B(0x3E, w.Value, 0xD3, w.Port)).ToArray());

    /// <summary>The IM 1 handler of the interrupt-phase probes: the first time it runs it latches the H counter (TH 0 to 1 on port $3F), reads it and the V counter into $C000/$C001 and sets $C002; every time it reads
    /// the status port (which acknowledges the interrupt) and returns with interrupts on.</summary>
    public static readonly byte[] CountingHandler = B(0x3A, 0x02, 0xC0, 0xB7, 0x20, 0x17, 0x3E, 0x55, 0xD3, 0x3F, 0x3E, 0xF5, 0xD3, 0x3F, 0xDB, 0x7F, 0x32, 0x00, 0xC0, 0xDB, 0x7E, 0x32, 0x01, 0xC0,
                                                      0x3E, 0x01, 0x32, 0x02, 0xC0, 0xDB, 0xBF, 0xFB, 0xED, 0x4D);
}

/// <summary>A sound unit that forwards to a real one and can be made wrong on purpose: the proof that the audio checks can fail.</summary>
internal sealed class PoisonedSound : ISmsSound
{
    private readonly ISmsSound inner;
    private readonly bool dropStereo;
    public PoisonedSound(ISmsSound inner, bool dropStereo) { this.inner = inner; this.dropStereo = dropStereo; }
    public string CoreName => inner.CoreName + " (poisoned)";
    public int SampleRate => inner.SampleRate;
    public void Reset() => inner.Reset();
    public void Run(int cpuClocks) => inner.Run(cpuClocks);
    public void WritePsg(byte value) => inner.WritePsg(value);
    public void WriteStereo(byte value) { if (!dropStereo) inner.WriteStereo(value); }
    public bool HasFm => inner.HasFm;
    public void WriteFmAddress(byte value) => inner.WriteFmAddress(value);
    public void WriteFmData(byte value) => inner.WriteFmData(value);
    public void WriteAudioControl(byte value) => inner.WriteAudioControl(value);
    public byte ReadAudioControl() => inner.ReadAudioControl();
    public int ReadSamples(Span<short> dest) => inner.ReadSamples(dest);
    public int DescribeVoices(Span<HubVoice> voices) => inner.DescribeVoices(voices);
    public void SaveState(BinaryWriter w) => inner.SaveState(w);
    public void LoadState(BinaryReader r) => inner.LoadState(r);
    public string Describe() => inner.Describe();
}

/// <summary>
/// Interrupt and flag timing against the oracle. Two probes, each run with the oracle (Mesen 2.1.1) at a recorded start line and with this emulator at the same one; the oracle's numbers are the Z80 cycle
/// counts it reported (exec callback, <c>cpu.cycleCount</c>) and are written here as constants (how they were taken: Workshop/Sega/mesen/README in the commit message of this suite, and the
/// scripts mesen-capture.ps1 -Watch). (1) a NOP sled after a padding of k x LD A,n: the cycle at which the frame interrupt's handler is entered, for the first three frames, k = 0..3 (the padding slides
/// the sled's 4-cycle instruction grid against the interrupt, so each k tests another phase); (2) a status-port polling loop: the cycle at which the loop sees the frame flag.
/// </summary>
internal static class IrqPhase
{
    private static readonly (int Pad, int Line, long[] Entry)[] Sled =
    {
        (0, 27, new long[] { 37844, 97582, 157317 }),
        (1, 63, new long[] { 29635, 89373, 149108 }),
        (2, 172, new long[] { 4786, 64520, 124255 }),
        (3, 178, new long[] { 3417, 63151, 122890 }),
    };
    private static readonly (int Nops, int Line, long Seen)[] Poll =
    {
        (0, 68, 88236), (1, 81, 85292), (2, 168, 65467), (3, 138, 72285),
    };

    private static byte[] SledRom(int pad)
    {
        var m = new List<byte>(SmsProg.Prolog);
        m.AddRange(SmsProg.B(0x3E, 0xE2, 0xD3, 0xBF, 0x3E, 0x81, 0xD3, 0xBF, 0xDB, 0xBF));      // R1 = $E2 (picture and frame interrupt on), clear the status
        for (int i = 0; i < pad; i++) m.AddRange(SmsProg.B(0x3E, 0x00));                          // padding: LD A,0 is 7 cycles
        m.Add(0xFB);                                                                              // EI
        for (int i = 0; i < 100; i++) m.Add(0x00);                                                // NOP sled
        m.AddRange(SmsProg.B(0x18, 0x9A));                                                        // JR back to the sled's start
        return SmsProg.Rom(m.ToArray(), SmsProg.CountingHandler, null, 0x60);
    }

    private static byte[] PollRom(int nops, out int marker)
    {
        var m = new List<byte>(SmsProg.B(0xF3, 0x3E, 0xE2, 0xD3, 0xBF, 0x3E, 0x81, 0xD3, 0xBF, 0xDB, 0xBF));   // DI; R1 = $E2; clear the status
        for (int loop = 0; loop < 2; loop++)
        {
            int start = 0x60 + m.Count;
            m.AddRange(SmsProg.B(0xDB, 0xBF, 0xB7));                                              // IN A,($BF); OR A  (sets the sign flag from the frame flag)
            for (int i = 0; i < nops; i++) m.Add(0x00);
            m.AddRange(SmsProg.B(0xF2, start & 0xFF, start >> 8));                                // JP P,start: leaves when the flag is seen
        }
        marker = 0x60 + m.Count;
        m.Add(0x00);                                                                              // the instruction after the second loop (the first loop catches frame 1's flag)
        m.AddRange(SmsProg.Halt);
        return SmsProg.Rom(m.ToArray(), null, null, 0x60);
    }

    public static IEnumerable<SmsCheck> Run(SmsSuiteContext ctx)
    {
        foreach (var (pad, line, entry) in Sled)
        {
            var s = ctx.Session(SledRom(pad), "irq.sms");
            s.Board.StartLine = line; s.Board.Reset();
            var seen = new List<long>();
            s.Board.InstructionHook = pc => { if (pc == 0x38 && seen.Count < 3) seen.Add(s.Board.Cycles); };
            for (int f = 0; f < 6 && seen.Count < 3; f++) s.RunFrame();
            yield return new SmsCheck($"frame interrupt entry cycles, sled padding {pad}, start line {line}", seen.SequenceEqual(entry),
                                      $"this emulator {string.Join(",", seen)}; oracle {string.Join(",", entry)}");
        }
        foreach (var (nops, line, expect) in Poll)
        {
            var s = ctx.Session(PollRom(nops, out int marker), "poll.sms");
            s.Board.StartLine = line; s.Board.Reset();
            long got = -1;
            s.Board.InstructionHook = pc => { if (pc == marker && got < 0) got = s.Board.Cycles; };
            for (int f = 0; f < 6 && got < 0; f++) s.RunFrame();
            yield return new SmsCheck($"status-polling loop sees the frame flag at cycle, {nops} NOPs in the loop, start line {line}", got == expect, $"this emulator {got}; oracle {expect}");
        }
    }
}

/// <summary>
/// The PSG and the Game Gear's stereo port, measured on the audio the session delivers. Each check runs a hand-assembled program that programs the chip through the ports and then idles, collects the samples of 30
/// frames, and measures: the tone frequency (rising crossings of the mid level, first to last), the peak-to-peak level against the 2 dB steps of the volume register, the noise, the left and right levels.
/// What is checked is the board's wiring (port decode, the chip's clock, the stereo port) and the chip's documented behaviour, not a comparison with a recording.
/// </summary>
internal static class PsgSuite
{
    private const double Z80Clock = 53693175.0 / 15;   // NTSC

    private static byte[] Program(params (int Port, int Value)[] writes) => SmsProg.Rom(SmsProg.Cat(SmsProg.Prolog, SmsProg.Out(writes), SmsProg.Halt));

    private static (short[] L, short[] R) Capture(SmsSession s, int settle = 4, int frames = 30)
    {
        var buf = new short[8192];
        var l = new List<short>(); var r = new List<short>();
        for (int f = 0; f < settle + frames; f++)
        {
            s.RunFrame();
            int n;
            while ((n = s.ReadSamples(buf)) > 0)
                if (f >= settle) for (int i = 0; i + 1 < n; i += 2) { l.Add(buf[i]); r.Add(buf[i + 1]); }
        }
        return (l.ToArray(), r.ToArray());
    }

    private static double Frequency(short[] x, int rate)
    {
        int min = x.Min(), max = x.Max();
        if (max - min < 200) return 0;
        double mid = (min + max) / 2.0, hyst = (max - min) / 4.0;
        bool high = false; int crossings = 0, first = -1, last = -1;
        for (int i = 0; i < x.Length; i++)
        {
            if (!high && x[i] > mid + hyst) { high = true; crossings++; if (first < 0) first = i; last = i; }
            else if (high && x[i] < mid - hyst) high = false;
        }
        return crossings < 3 ? 0 : (crossings - 1) * (double)rate / (last - first);
    }

    private static double Peak(short[] x) => (double)(x.Max() - x.Min());
    private static double Rms(short[] x) { double m = x.Average(s => (double)s); return Math.Sqrt(x.Average(s => (s - m) * (s - m))); }

    private static SmsSession Make(SmsSuiteContext ctx, byte[] rom, ConsoleKind console = ConsoleKind.MasterSystem)
    {
        var s = ctx.Session(rom, console == ConsoleKind.GameGear ? "psg.gg" : "psg.sms", console);
        if (ctx.Poison == "psg") s.Board.SwapSound(new PoisonedSound(new SmsSound(Z80Clock * 1.06, 44100), dropStereo: true));
        return s;
    }

    private static int[] Tone(int channel, int period, int volume) => new[] { 0x80 | channel << 5 | (period & 0xF), period >> 4, 0x90 | channel << 5 | volume };

    private static byte[] ToneRom(int channel, int period, int volume, params (int, int)[] extra)
    {
        var t = Tone(channel, period, volume);
        var w = new List<(int, int)> { (0x7F, t[0]), (0x7F, t[1]), (0x7F, t[2]) };
        w.AddRange(extra);
        return Program(w.ToArray());
    }

    public static IEnumerable<SmsCheck> Run(SmsSuiteContext ctx)
    {
        // tone frequency: clock / (32 N), on each of the three tone channels, at three periods
        foreach (var (ch, n) in new[] { (0, 0x100), (0, 0x040), (1, 0x0A0), (2, 0x180), (0, 0x3FF) })
        {
            var s = Make(ctx, ToneRom(ch, n, 0));
            var (l, r) = Capture(s);
            double f = Frequency(l, s.SampleRate), want = Z80Clock / (32.0 * n);
            yield return new SmsCheck($"tone {ch + 1}, period ${n:X3}: frequency {want:F1} Hz", Math.Abs(f - want) < want * 0.012, $"measured {f:F1} Hz");
            yield return new SmsCheck($"tone {ch + 1}, period ${n:X3}: both channels carry it (the Master System is mono)", Math.Abs(Peak(l) - Peak(r)) < 2 && Peak(l) > 3000, $"peak L {Peak(l)} R {Peak(r)}");
        }

        // volume: 2 dB per step, 15 is off
        double full = 0;
        foreach (int v in new[] { 0, 2, 4, 8, 15 })
        {
            var s = Make(ctx, ToneRom(0, 0x100, v));
            var (l, _) = Capture(s);
            double pp = Peak(l);
            if (v == 0) { full = pp; yield return new SmsCheck("volume 0 is loud (a full-volume channel is about 20% of full scale)", pp > 4000 && pp < 12000, $"peak-to-peak {pp}"); continue; }
            double want = v == 15 ? 0 : Math.Pow(10, -2.0 * v / 20);
            double got = pp / full;
            yield return new SmsCheck($"volume {v}: {(v == 15 ? "silent" : $"{-2 * v} dB = {want:F3} of full")}", v == 15 ? pp < 60 : Math.Abs(got - want) < 0.12 * want, $"ratio {got:F3}");
        }

        // noise: white and periodic produce sound at volume 0; with the chip untouched nothing is heard
        foreach (var (name, reg) in new[] { ("white noise, rate 2", 0xE6), ("periodic noise, rate 2", 0xE2) })
        {
            var s = Make(ctx, Program((0x7F, reg), (0x7F, 0xF0)));
            var (l, _) = Capture(s);
            yield return new SmsCheck($"noise: {name} is audible at volume 0", Rms(l) > 400, $"rms {Rms(l):F0}");
        }
        {
            var s = Make(ctx, Program());
            var (l, r) = Capture(s);
            yield return new SmsCheck("silent after power-on (all four volumes are off)", Peak(l) < 60 && Peak(r) < 60, $"peak L {Peak(l)} R {Peak(r)}");
        }

        // the Game Gear's stereo port: bits 4-7 left, 0-3 right (tone 1 = bits 4 and 0)
        foreach (var (mask, wantL, wantR) in new[] { (0x10, true, false), (0x01, false, true), (0xFF, true, true), (0x00, false, false), (0x20, false, false) })
        {
            var s = Make(ctx, Program((0x06, mask), (0x7F, 0x80 | 0x00), (0x7F, 0x10), (0x7F, 0x90)), ConsoleKind.GameGear);
            var (l, r) = Capture(s);
            bool heardL = Peak(l) > 3000, heardR = Peak(r) > 3000, quietL = Peak(l) < 60, quietR = Peak(r) < 60;
            bool ok = (wantL ? heardL : quietL) && (wantR ? heardR : quietR);
            yield return new SmsCheck($"Game Gear stereo port ${mask:X2} (tone 1 plays): left {(wantL ? "heard" : "silent")}, right {(wantR ? "heard" : "silent")}", ok, $"peak L {Peak(l)} R {Peak(r)}");
        }
    }
}

/// <summary>Battery saves: what the cartridge RAM holds goes out through ExportSave and comes back through ImportSave to a program that reads it; a cartridge that never maps RAM in is not offered a save.</summary>
internal static class SaveSuite
{
    // maps cartridge RAM bank 0 into slot 2, stores $5A and $A5 at its first two bytes, maps it out again
    private static byte[] Writer() => SmsProg.Rom(SmsProg.Cat(SmsProg.Prolog, SmsProg.B(0x3E, 0x08, 0x32, 0xFC, 0xFF, 0x3E, 0x5A, 0x32, 0x00, 0x80, 0x3E, 0xA5, 0x32, 0x01, 0x80, 0x3E, 0x00, 0x32, 0xFC, 0xFF), SmsProg.Halt));
    // maps it in and copies its first two bytes to work RAM $C100/$C101
    private static byte[] Reader() => SmsProg.Rom(SmsProg.Cat(SmsProg.Prolog, SmsProg.B(0x3E, 0x08, 0x32, 0xFC, 0xFF, 0x3A, 0x00, 0x80, 0x32, 0x00, 0xC1, 0x3A, 0x01, 0x80, 0x32, 0x01, 0xC1), SmsProg.Halt));
    private static byte[] NoRam() => SmsProg.Rom(SmsProg.Cat(SmsProg.Prolog, SmsProg.Halt));

    public static IEnumerable<SmsCheck> Run(SmsSuiteContext ctx)
    {
        var w = ctx.Session(Writer(), "writer.sms");
        for (int f = 0; f < 3; f++) w.RunFrame();
        yield return new SmsCheck("a game that maps RAM in is offered a save (HasBattery)", w.HasBattery, "");
        byte[] saved = w.ExportSave();
        yield return new SmsCheck("the save holds the 32 KB cartridge RAM with what the game wrote", saved.Length == 0x8000 && saved[0] == 0x5A && saved[1] == 0xA5, $"length {saved.Length}, first bytes {saved.ElementAtOrDefault(0):X2} {saved.ElementAtOrDefault(1):X2}");

        var fresh = ctx.Session(Reader(), "reader.sms");
        for (int f = 0; f < 3; f++) fresh.RunFrame();
        yield return new SmsCheck("without a save the reader finds empty RAM (the control for the next check)", fresh.Board.Ram[0x100] == 0 && fresh.Board.Ram[0x101] == 0, "");

        var r = ctx.Session(Reader(), "reader.sms");
        byte[] toLoad = (byte[])saved.Clone();
        if (ctx.Poison == "save") toLoad[0] ^= 0xFF;
        r.ImportSave(toLoad);
        for (int f = 0; f < 3; f++) r.RunFrame();
        yield return new SmsCheck("a save imported before the game runs is what the game reads", r.Board.Ram[0x100] == 0x5A && r.Board.Ram[0x101] == 0xA5, $"read {r.Board.Ram[0x100]:X2} {r.Board.Ram[0x101]:X2}");

        var n = ctx.Session(NoRam(), "noram.sms");
        for (int f = 0; f < 3; f++) n.RunFrame();
        yield return new SmsCheck("a game that never maps RAM in is not offered a save", !n.HasBattery, "");

        // the dirty flag follows writes (the app writes the file when it is set)
        bool dirty = w.Board.Cart.RamDirty;
        w.Board.Cart.ClearDirty();
        yield return new SmsCheck("the cartridge knows its RAM was written (RamDirty) until the app has saved it", dirty && !w.Board.Cart.RamDirty, "");
    }
}

/// <summary>The cartridge mappers: bank registers, the protected first KB, RAM banking, page wrap, Codemasters, detection. Direct reads and writes on a cartridge built from a ROM whose every 16 KB bank is filled with its own number.</summary>
internal static class MapperSuite
{
    private static byte[] Banks(int count, int fixedFirstKb = 0xF0)
    {
        var rom = new byte[count * 0x4000];
        for (int b = 0; b < count; b++) Array.Fill(rom, (byte)b, b * 0x4000, 0x4000);
        if (fixedFirstKb >= 0) Array.Fill(rom, (byte)fixedFirstKb, 0, 0x400);
        return rom;
    }

    public static IEnumerable<SmsCheck> Run(SmsSuiteContext ctx)
    {
        var kind = ctx.Poison == "mapper" ? SmsMapperKind.Plain : SmsMapperKind.Sega;
        var c = new SmsCartridge(Banks(4), kind);
        yield return new SmsCheck("Sega mapper at reset: slots hold banks 0, 1, 2; the first KB is the ROM's own", c.Read(0x0000) == 0xF0 && c.Read(0x0400) == 0 && c.Read(0x4000) == 1 && c.Read(0x8000) == 2, $"{c.Read(0):X2} {c.Read(0x400):X2} {c.Read(0x4000):X2} {c.Read(0x8000):X2}");
        c.Write(0xFFFF, 3); c.Write(0xFFFE, 2); c.Write(0xFFFD, 1);
        yield return new SmsCheck("registers $FFFD, $FFFE, $FFFF select the banks of slots 0, 1, 2", c.Read(0x0400) == 1 && c.Read(0x4000) == 2 && c.Read(0x8000) == 3, $"{c.Read(0x400):X2} {c.Read(0x4000):X2} {c.Read(0x8000):X2}");
        yield return new SmsCheck("the first KB stays fixed when slot 0 is switched", c.Read(0x03FF) == 0xF0 && c.Read(0x0000) == 0xF0, "");
        c.Write(0xFFFF, 7);
        yield return new SmsCheck("a bank number past the ROM's end wraps (7 of 4 banks is bank 3)", c.Read(0x8000) == 3, $"{c.Read(0x8000):X2}");

        var m = new SmsCartridge(Banks(4), kind);
        m.Write(0xFFFC, 0x08); m.Write(0x8005, 0x77);
        bool inRam = m.Read(0x8005) == 0x77;
        m.Write(0xFFFC, 0x00);
        bool outAgain = m.Read(0x8005) == 2;
        m.Write(0xFFFC, 0x08);
        bool kept = m.Read(0x8005) == 0x77;
        m.Write(0xFFFC, 0x0C);
        bool otherBank = m.Read(0x8005) == 0;
        yield return new SmsCheck("cartridge RAM: mapped in by bit 3 of $FFFC, the ROM is back when it is cleared, the data stays, bit 2 selects the other 16 KB",
                                  inRam && outAgain && kept && otherBank, $"in {inRam} out {outAgain} kept {kept} other {otherBank}");

        // Codemasters detection and banking
        var cm = Banks(4, -1);
        cm[0x7FE1] = 0x15; cm[0x7FE2] = 0x03; cm[0x7FE6] = 0x34; cm[0x7FE7] = 0x12; cm[0x7FE8] = 0xCC; cm[0x7FE9] = 0xED;   // build date 15/03, checksum $1234 and its complement $EDCC
        yield return new SmsCheck("a ROM with the Codemasters checksum pair and a BCD build date is a Codemasters cartridge", SmsCartridge.DetectMapper(cm, MasterSystemModel.Sms2) == SmsMapperKind.Codemasters, "");
        yield return new SmsCheck("a ROM without it gets the Sega mapper", SmsCartridge.DetectMapper(Banks(4), MasterSystemModel.Sms2) == SmsMapperKind.Sega, "");
        var cc = new SmsCartridge(cm, SmsMapperKind.Codemasters);
        cc.Write(0x0000, 2); cc.Write(0x4000, 3); cc.Write(0x8000, 1);
        yield return new SmsCheck("Codemasters: the first byte of each 16 KB slot is its bank register (no protected first KB)", cc.Read(0x0100) == 2 && cc.Read(0x4100) == 3 && cc.Read(0x8100) == 1, $"{cc.Read(0x100):X2} {cc.Read(0x4100):X2} {cc.Read(0x8100):X2}");
        cc.Write(0x4000, 0x80 | 3); cc.Write(0xA010, 0x66);
        yield return new SmsCheck("Codemasters: bit 7 of the slot 1 register maps 8 KB of RAM at $A000", cc.Read(0xA010) == 0x66, $"{cc.Read(0xA010):X2}");

        // SG-1000 cartridges are wired straight in
        yield return new SmsCheck("an SG-1000 ROM of 32 KB has no mapper", SmsCartridge.DetectMapper(new byte[0x8000], MasterSystemModel.Sg1000) == SmsMapperKind.Plain, "");
        var p = new SmsCartridge(Banks(2, -1), SmsMapperKind.Plain);
        yield return new SmsCheck("the plain cartridge reads the ROM at $0000 and open bus past its end", p.Read(0x0000) == 0 && p.Read(0x4000) == 1 && p.Read(0x8000) == 0xFF, $"{p.Read(0):X2} {p.Read(0x4000):X2} {p.Read(0x8000):X2}");
        // a 512-byte copier header is stripped before the ROM is used
        var withHeader = new byte[0x8000 + 0x200]; withHeader[0x200] = 0xAB;
        yield return new SmsCheck("a 512-byte copier header is stripped", SegaRomFormat.StripCopierHeader(withHeader).Length == 0x8000 && SegaRomFormat.StripCopierHeader(withHeader)[0] == 0xAB, "");
    }
}

/// <summary>The console layer: model and region from the name, frame rates, picture sizes, the pad and the Pause button as the program sees them through ports $DC, $DD and the NMI.</summary>
internal static class SessionSuite
{
    private static byte[] PadReader() => SmsProg.Rom(SmsProg.Cat(SmsProg.Prolog, SmsProg.B(0xDB, 0xDC, 0x32, 0x00, 0xC1, 0xDB, 0xDD, 0x32, 0x01, 0xC1, 0x18, 0xF4)));   // loop: in a,($DC); ld ($C100),a; in a,($DD); ld ($C101),a
    // NMI handler: increments $C102; main: loops
    private static byte[] NmiCounter() => SmsProg.Rom(SmsProg.Cat(SmsProg.Prolog, SmsProg.Halt), null, SmsProg.B(0x3A, 0x02, 0xC1, 0x3C, 0x32, 0x02, 0xC1, 0xED, 0x45));
    private static byte[] Blank() => SmsProg.Rom(SmsProg.Cat(SmsProg.Prolog, SmsProg.Halt));

    private static (byte Dc, byte Dd) Read(SmsSuiteContext ctx, PadButtons p1, PadButtons p2)
    {
        var s = ctx.Session(PadReader(), "pad.sms");
        s.SetPad(0, p1); s.SetPad(1, p2);
        for (int f = 0; f < 2; f++) s.RunFrame();
        return (s.Board.Ram[0x100], s.Board.Ram[0x101]);
    }

    public static IEnumerable<SmsCheck> Run(SmsSuiteContext ctx)
    {
        // ---- the pad: $DC bits 0-3 player 1 up/down/left/right, 4-5 its buttons 1 and 2, 6-7 player 2 up/down; $DD bits 0-3 player 2 left/right/1/2, 4 reset (low when pressed)
        var bad = ctx.Poison == "session" ? PadButtons.B : PadButtons.A;     // the poison presses the wrong button: the checks below must notice
        var (dc, dd) = Read(ctx, 0, 0);
        yield return new SmsCheck("no button pressed: $DC and $DD read all ones in the pad bits", dc == 0xFF && (dd & 0x3F) == 0x3F, $"DC {dc:X2} DD {dd:X2}");
        (dc, _) = Read(ctx, bad | PadButtons.Right, 0);
        yield return new SmsCheck("player 1 A (button 1) and Right pull $DC bits 4 and 3 low", dc == 0xE7, $"DC {dc:X2}");
        (dc, _) = Read(ctx, PadButtons.B | PadButtons.Up | PadButtons.Down | PadButtons.Left, 0);
        yield return new SmsCheck("player 1 B (button 2), Up, Down and Left pull bits 5, 0, 1 and 2 low", dc == 0xD8, $"DC {dc:X2}");
        (dc, dd) = Read(ctx, 0, PadButtons.Up | PadButtons.Down | PadButtons.Left | PadButtons.Right | PadButtons.A | PadButtons.B);
        yield return new SmsCheck("player 2 Up and Down are $DC bits 6 and 7; Left, Right, 1, 2 are $DD bits 0-3", dc == 0x3F && (dd & 0x0F) == 0x00, $"DC {dc:X2} DD {dd:X2}");
        (_, dd) = Read(ctx, PadButtons.Mode, 0);
        yield return new SmsCheck("the Mode button is the Reset button: $DD bit 4 low", (dd & 0x10) == 0, $"DD {dd:X2}");

        // ---- Pause is an NMI on the rising edge
        {
            var s = ctx.Session(NmiCounter(), "nmi.sms");
            for (int f = 0; f < 2; f++) s.RunFrame();
            int before = s.Board.Ram[0x102];
            s.SetPad(0, PadButtons.Select); for (int f = 0; f < 5; f++) s.RunFrame();
            int held = s.Board.Ram[0x102];
            s.SetPad(0, 0); s.RunFrame(); s.SetPad(0, PadButtons.Select); s.RunFrame(); s.SetPad(0, 0); s.RunFrame();
            int after = s.Board.Ram[0x102];
            yield return new SmsCheck("Pause (Select) raises one NMI per press, however long it is held", before == 0 && held == 1 && after == 2, $"count {before} -> {held} -> {after}");
        }

        // ---- model and region from the file name
        foreach (var (name, pal) in new[] { ("Bubble Bobble (Europe).sms", true), ("Alex Kidd in Miracle World (USA, Europe).sms", false), ("Fantasy Zone (World).sms", false), ("Game (E).sms", true), ("Game (Japan).sms", false), ("Game.sms", false), ("Game (Australia).sms", true) })
        {
            var s = new SmsSession(Blank(), ConsoleKind.MasterSystem, "Z80", "SMS", "SMS", name);
            double fps = s.FramesPerSecond;
            yield return new SmsCheck($"\"{name}\" runs at {(pal ? "50" : "60")} Hz", pal ? Math.Abs(fps - 49.70) < 0.1 : Math.Abs(fps - 59.92) < 0.1, $"{fps:F2} fps");
        }
        {
            var s = new GgSession(Blank(), "Z80", "GG", "GG", "Tails Adventure (Europe).gg");
            yield return new SmsCheck("a Game Gear game runs at 60 Hz whatever its name says", Math.Abs(s.FramesPerSecond - 59.92) < 0.1, $"{s.FramesPerSecond:F2} fps");
            yield return new SmsCheck("the Game Gear picture is the LCD's 160 x 144", s.FrameWidth == 160 && s.FrameHeight == 144 && s.Frame.Length >= 160 * 144, $"{s.FrameWidth} x {s.FrameHeight}");
        }
        {
            var s = new SmsSession(Blank(), ConsoleKind.MasterSystem, "Z80", "SMS", "SMS", "x.sms");
            s.RunFrame();
            yield return new SmsCheck("the Master System picture is 256 x 192", s.FrameWidth == 256 && s.FrameHeight == 192, $"{s.FrameWidth} x {s.FrameHeight}");
            yield return new SmsCheck("an .sg file is the SG-1000, .sms an SMS 2", new SmsSession(Blank(), ConsoleKind.MasterSystem, "Z80", "SMS", "SMS", "x.sg").Model == MasterSystemModel.Sg1000 && s.Model == MasterSystemModel.Sms2, "");
        }
        yield return new SmsCheck("a .gg file whose header carries a Master System code is still a Game Gear cartridge",
            SegaRomFormat.Detect(HeaderRom(0x4C), "game.gg") == ConsoleKind.GameGear && SegaRomFormat.Detect(HeaderRom(0x4C), "game.sms") == ConsoleKind.MasterSystem, "");
    }

    private static byte[] HeaderRom(int regionAndSize)
    {
        var rom = new byte[0x8000];
        "TMR SEGA"u8.CopyTo(rom.AsSpan(0x7FF0));
        rom[0x7FFF] = (byte)regionAndSize;     // system code 4 (SMS export) in the high nibble
        return rom;
    }
}

/// <summary>
/// A regression guard on the picture of the climb's games: the hash of the frame at fixed frame counts of a scripted play-through, against the hashes recorded when each game matched the oracle (the notes in the
/// golden file say which frames the oracle's own picture matched and which differences are explained). It does not say the picture is right, only that it has not changed. The ROMs are the user's (never in the
/// repository): pass <c>--roms &lt;dir&gt;</c> (folders sms and gg inside) or set BROKENNES_SMS_ROMS; without them the suite is skipped, and says so.
/// </summary>
internal static class GoldenSuite
{
    public const string PadSms = "150:A,156:,210:A,216:,270:A,276:,330:A,336:,400:Right,460:Right+A,470:Right,520:Right+B,530:Right,580:Left+A,590:Left,640:Down,700:Up+A,710:Right,760:Right+A,770:Right";
    public const string PadGg = "150:Start,156:,210:A,216:,270:Start,276:,330:A,336:,400:Right,460:Right+A,470:Right,520:Right+B,530:Right,580:Left+A,590:Left,640:Down,700:Up+A,710:Right,760:Right+A,770:Right";
    public static readonly int[] Frames = { 300, 600, 899 };

    public static readonly (string Console, string File)[] Roster =
    {
        ("sms", "Astro Warrior (USA).sms"), ("sms", "Fantasy Zone (World) (v1.2).sms"), ("sms", "Alex Kidd in Miracle World (USA, Europe) (v1.1).sms"), ("sms", "Black Belt (USA, Europe).sms"),
        ("sms", "Columns (USA, Europe).sms"), ("sms", "Bubble Bobble (Europe).sms"), ("sms", "Penguin Land (USA, Europe).sms"), ("sms", "Wonder Boy (USA, Europe).sms"),
        ("sms", "Psycho Fox (USA, Europe).sms"), ("sms", "Shinobi (USA, Europe).sms"),
        ("gg", "GG Demo by Charles MacDonald (PD).gg"), ("gg", "Columns (U) [!].gg"), ("gg", "Fantasy Zone Gear (JU) [!].gg"), ("gg", "Bubble Bobble (U) [!].gg"),
        ("gg", "Pac-Man (J).gg"), ("gg", "Wonder Boy (E) [!].gg"), ("gg", "Castle of Illusion Starring Mickey Mouse (U) [S][!].gg"), ("gg", "Land of Illusion Starring Mickey Mouse (U) [!].gg"),
    };

    public static string? RomDir(string[] args)
    {
        string? d = SmsTestCli.Opt(args, "roms") ?? Environment.GetEnvironmentVariable("BROKENNES_SMS_ROMS");
        return d != null && Directory.Exists(d) ? d : null;
    }

    public static string? FindGolden(string[] args)
    {
        if (SmsTestCli.Opt(args, "golden") is { } g) return g;
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
        {
            string p = Path.Combine(dir.FullName, "Workshop", "Sega", "golden", "sms-golden.tsv");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static string Hash(IConsoleSession s)
    {
        int n = s.FrameWidth * s.FrameHeight;
        return Convert.ToHexString(SHA1.HashData(MemoryMarshal.AsBytes(s.Frame.AsSpan(0, n))));
    }

    /// <summary>Runs a game with the scripted pad and returns the frame hash at each of the frame counts.</summary>
    public static Dictionary<int, string> Play(SmsSuiteContext ctx, string path, bool gg, int[] frames)
    {
        byte[] rom = SmsRomIo.Load(path, out string inner);
        var s = gg ? ctx.GgSession(rom, inner) : ctx.Session(rom, inner);
        var script = SmsRomIo.ParseInput(gg ? PadGg : PadSms);
        PadButtons held = 0;
        var result = new Dictionary<int, string>();
        int last = frames.Max();
        for (int f = 0; f < last; f++)
        {
            if (script.TryGetValue(f, out var h)) held = h;
            s.SetPad(0, held);
            s.RunFrame();
            if (frames.Contains(f + 1)) result[f + 1] = Hash(s);
        }
        return result;
    }

    public static IEnumerable<SmsCheck> Run(SmsSuiteContext ctx)
    {
        string? dir = ctx.RomDir, golden = ctx.GoldenFile;
        if (dir == null) { yield return SmsCheck.Skipped("golden frame hashes", "no ROM directory (pass --roms <dir> with sms and gg folders, or set BROKENNES_SMS_ROMS)"); yield break; }
        if (golden == null || !File.Exists(golden)) { yield return SmsCheck.Skipped("golden frame hashes", "no golden file (Workshop/Sega/golden/sms-golden.tsv)"); yield break; }
        var table = new Dictionary<(string, int), string>();
        foreach (var line in File.ReadLines(golden))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var p = line.Split('\t');
            if (p.Length >= 4) table[(p[1], int.Parse(p[2]))] = p[3];
        }
        foreach (var (console, file) in Roster)
        {
            string path = Path.Combine(dir, console, file);
            if (!File.Exists(path)) { yield return SmsCheck.Skipped(file, "ROM not found in " + Path.Combine(dir, console)); continue; }
            var got = Play(ctx, path, console == "gg", Frames);
            foreach (int fr in Frames)
            {
                bool have = table.TryGetValue((file, fr), out var want);
                yield return new SmsCheck($"{file} frame {fr}", have && got[fr] == want, have ? $"hash {got[fr][..12]}, recorded {want![..12]}" : "no recorded hash");
            }
        }
    }

    /// <summary>--golden-write &lt;file&gt;: records the hashes of every roster ROM found (a header of comments is kept from the existing file).</summary>
    public static int Write(SmsSuiteContext ctx, string dir, string outPath)
    {
        var lines = new List<string>();
        if (File.Exists(outPath)) lines.AddRange(File.ReadLines(outPath).TakeWhile(l => l.StartsWith('#')));
        foreach (var (console, file) in Roster)
        {
            string path = Path.Combine(dir, console, file);
            if (!File.Exists(path)) { Console.WriteLine($"missing {path}"); continue; }
            var got = Play(ctx, path, console == "gg", Frames);
            foreach (int fr in Frames) lines.Add($"{console}\t{file}\t{fr}\t{got[fr]}");
            Console.WriteLine($"{file}: {string.Join(" ", Frames.Select(f => got[f][..8]))}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllLines(outPath, lines);
        return 0;
    }
}

/// <summary>Throughput: frames per second on one core against the machine's own frame rate. The target is eight times real time. It is a measurement of a shared machine, so it takes the best of three runs, and it
/// is reported whatever it is (the name of the check carries the number).</summary>
internal static class SpeedSuite
{
    public static IEnumerable<SmsCheck> Run(SmsSuiteContext ctx)
    {
        string label; byte[] rom; string name = "speed.sms";
        string? game = ctx.RomDir is { } d ? Path.Combine(d, "sms", GoldenSuite.Roster[0].File) : null;
        if (game != null && File.Exists(game)) { rom = SmsRomIo.Load(game, out name); label = name; }
        else
        {
            string vt = ctx.TestRom("sms-roms", "smsvdptest-1.31", "VDPTEST.sms");
            if (!File.Exists(vt)) { yield return SmsCheck.Skipped("throughput", "no game and no VDPTEST.sms to run"); yield break; }
            rom = File.ReadAllBytes(vt); label = "VDPTEST.sms"; name = "VDPTEST.sms";
        }
        double best = 0;
        for (int run = 0; run < 3; run++)
        {
            var s = ctx.Session(rom, name);
            for (int f = 0; f < 120; f++) s.RunFrame();    // warm up (JIT, caches)
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int f = 0; f < 600; f++) s.RunFrame();
            best = Math.Max(best, 600 / sw.Elapsed.TotalSeconds / s.FramesPerSecond);
        }
        double target = ctx.Poison == "speed" ? 1e6 : 8.0;
        yield return new SmsCheck($"throughput on {label}: {best:F1}x real time on one core (target {target:F0}x or more)", best >= target, $"{best:F1}x");
    }
}
