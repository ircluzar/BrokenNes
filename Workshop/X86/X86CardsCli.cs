using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator.X86;

namespace BrokenNes.Workshop.X86;

/// <summary>
/// <c>--x86cards [--wav dir]</c>: every x86 sound card is driven through its ports from C# on a bare PcMachine (no DOS program, no CPU run: machine time passes
/// with <see cref="PcMachine.IdleFor"/>) and must make sound (AC rms above a floor):
/// SB_D072 DSP reset / version, direct DAC (10h), an 8-bit single-cycle DMA sine (14h) that must also raise the IRQ (and its FM at 228h);
/// GUS_D074 a DMA upload + poke read-back, a looping voice with a volume ramp; TANDY_D072 each tone channel and the noise; COVOX a square on 378h;
/// SPEAKER a PIT channel 2 tone (and no AdLib at 388h). Each card is also saved mid-sound and loaded into a fresh machine, which must keep sounding.
/// Exit code 0 = nothing flagged.
/// </summary>
internal static class X86CardsCli
{
    private const long Ips = 20_000_000;
    private static int _flagged;
    private static string? _wavDir;

    private static PcMachine Make(string id)
    {
        var m = new PcMachine(4 * 1024 * 1024) { InstructionsPerSecond = Ips };
        if (!PcSound.Apply(m, id)) throw new InvalidOperationException("unknown id " + id);
        m.Speaker.Enabled = true; m.Opl.Enabled = true;
        m.Speaker.Restart(); m.Opl.Restart(); m.Card?.Restart();
        return m;
    }

    private static void Idle(PcMachine m, double s) => m.IdleFor((long)(s * Ips));

    /// <summary>What the machine played since the last call (speaker + AdLib + card), mono.</summary>
    private static short[] Take(PcMachine m)
    {
        m.Speaker.CatchUp(); m.Opl.CatchUp(); m.Card?.Advance();
        var b = new short[Math.Max(m.Speaker.Pending, Math.Max(m.Opl.Pending, m.Card?.Pending ?? 0)) + 2];
        int n = m.Speaker.Read(b);
        n = Math.Max(n, m.Opl.MixInto(b, b.Length));
        n = Math.Max(n, m.Card?.MixInto(b, b.Length) ?? 0);
        return b.Take(n).Where((_, i) => i % 2 == 0).ToArray();
    }

    private static double Rms(short[] s)
    {
        if (s.Length == 0) return 0;
        double mean = s.Average(x => (double)x);
        return Math.Sqrt(s.Average(x => (x - mean) * (x - mean)));
    }

    private static readonly Dictionary<string, List<short>> Wavs = new();

    private static void Cell(string card, string cell, short[] s, double floor = 200, string extra = "", bool ok = true)
    {
        double r = Rms(s);
        string v = !ok ? "FAIL" : r < floor ? "SILENT" : "sounds";
        if (v != "sounds") _flagged++;
        Console.WriteLine($"{card,-11} {cell,-22} {r,7:F0}  {v}{(extra.Length > 0 ? "  " + extra : "")}");
        if (!Wavs.TryGetValue(card, out var l)) Wavs[card] = l = new List<short>();
        l.AddRange(s);
    }

    private static PcMachine Reload(PcMachine m, string id)
    {
        var st = m.SaveState();
        var m2 = Make(id);
        m2.LoadState(st);
        m2.Speaker.Restart(); m2.Opl.Restart(); m2.Card?.Restart();
        return m2;
    }

    public static int Run(string[] args)
    {
        _wavDir = BrokenNes.Workshop.Sega.M68kTestCli.Arg(args, "--wav");
        _flagged = 0;
        Console.WriteLine("card        cell                       rms  verdict");
        foreach (var (name, test) in new (string, Action)[] { ("SB_D072", Sb), ("GUS_D074", Gus), ("TANDY_D072", Tandy), ("COVOX", Covox), ("SPEAKER", Speaker) })
        {
            try { test(); }
            catch (Exception e) { _flagged++; Console.WriteLine($"{name,-11} CRASH {e.GetType().Name}: {e.Message}\n{e.StackTrace}"); }
        }
        if (_wavDir != null) { Directory.CreateDirectory(_wavDir); foreach (var (k, v) in Wavs) WriteWav(Path.Combine(_wavDir, $"card_{k}.wav"), v); }
        Console.WriteLine($"\n{_flagged} flagged");
        return _flagged == 0 ? 0 : 1;
    }

    // ================= Sound Blaster =================

    private static byte DspRead(PcMachine m) { for (int i = 0; i < 100 && (m.In8(0x22e) & 0x80) == 0; i++) Idle(m, 0.00001); return m.In8(0x22a); }
    private static void Dsp(PcMachine m, params int[] b) { foreach (var x in b) m.Out8(0x22c, (byte)x); }

    private static void SbDmaSine(PcMachine m, int len, int rate)
    {
        for (int i = 0; i < len; i++) m.PhysW8((uint)(0x10000 + i), (byte)(128 + 100 * Math.Sin(2 * Math.PI * 440 * i / rate)));
        m.Out8(0x0a, 0x05); m.Out8(0x0c, 0); m.Out8(0x0b, 0x49);
        m.Out8(0x02, 0x00); m.Out8(0x02, 0x00); m.Out8(0x83, 0x01);
        m.Out8(0x03, (byte)(len - 1)); m.Out8(0x03, (byte)((len - 1) >> 8));
        m.Out8(0x0a, 0x01);
        Dsp(m, 0x40, 256 - 1000000 / rate);
        Dsp(m, 0x14, (len - 1) & 0xff, (len - 1) >> 8);
    }

    private static void Sb()
    {
        const string id = "SB_D072";
        var m = Make(id);
        m.Out8(0x226, 1); Idle(m, 0.00001); m.Out8(0x226, 0);
        byte aa = DspRead(m);
        Dsp(m, 0xe1); byte maj = DspRead(m), min = DspRead(m);
        bool resetOk = aa == 0xaa && maj == 3 && min == 2;
        Console.WriteLine($"{id,-11} {"DSP reset / version",-22}          {(resetOk ? "ok" : "FAIL")}  (reset {aa:X2}, version {maj}.{min:D2})");
        if (!resetOk) _flagged++;
        Dsp(m, 0xd1);   // speaker on
        Take(m);
        // direct DAC: 8 kHz of a 440 Hz sine for 0.25 s
        for (int i = 0; i < 2000; i++) { Dsp(m, 0x10, (int)(128 + 100 * Math.Sin(2 * Math.PI * 440 * i / 8000.0))); Idle(m, 1 / 8000.0); }
        Cell(id, "direct DAC (10h)", Take(m));
        // single-cycle 8-bit DMA
        long irq0 = m.Card!.IrqsRaised;
        SbDmaSine(m, 4000, 8000);
        Idle(m, 0.6);
        bool irq = m.Card.IrqsRaised > irq0 && (m.Pic.MasterIrr & 0x80) != 0;
        Cell(id, "8-bit DMA (14h)", Take(m), extra: irq ? "IRQ 7 raised" : "NO IRQ", ok: irq);
        bool tc = (m.In8(0x08) & 0x02) != 0;
        Console.WriteLine($"{id,-11} {"DMA terminal count",-22}          {(tc ? "ok" : "FAIL")}  (channel 1 TC bit)");
        if (!tc) _flagged++;
        // FM through 228h/229h (the AdLib's chip)
        void Fm(int r, int v) { m.Out8(0x228, (byte)r); m.Out8(0x229, (byte)v); }
        Fm(0x20, 1); Fm(0x23, 1); Fm(0x40, 0x10); Fm(0x43, 0); Fm(0x60, 0xf0); Fm(0x63, 0xf0); Fm(0x80, 0x77); Fm(0x83, 0x77); Fm(0xa0, 0x41); Fm(0xb0, 0x32);
        Idle(m, 0.3);
        Cell(id, "FM at 228h", Take(m));
        Fm(0xb0, 0x12);
        // state: save in the middle of a DMA transfer, load into a fresh machine
        var m2 = Make(id);
        m2.Out8(0x226, 1); m2.Out8(0x226, 0); DspRead(m2); Dsp(m2, 0xd1);
        SbDmaSine(m2, 8000, 8000);
        Idle(m2, 0.2); Take(m2);
        var m3 = Reload(m2, id);
        long i3 = m3.Card!.IrqsRaised;
        Idle(m3, 0.3);
        var after = Take(m3);
        Idle(m3, 0.6); Take(m3);
        Cell(id, "state mid-DMA", after, extra: m3.Card.IrqsRaised > i3 ? "IRQ after load" : "no IRQ after load", ok: m3.Card.IrqsRaised > i3);
    }

    // ================= GUS =================

    private static void G8(PcMachine m, int reg, int v) { m.Out8(0x343, (byte)reg); m.Out8(0x345, (byte)v); }
    private static void G16(PcMachine m, int reg, int v) { m.Out8(0x343, (byte)reg); m.Out16(0x344, (ushort)v); }

    private static void GusVoice(PcMachine m, int voice, int addr, int len)
    {
        m.Out8(0x342, (byte)voice);
        G16(m, 0x01, 2616);                                         // ~440 Hz through a 256-sample cycle at 14 voices
        G16(m, 0x02, (addr >> 7) & 0x1fff); G16(m, 0x03, (addr & 0x7f) << 9);
        G16(m, 0x04, ((addr + len - 1) >> 7) & 0x1fff); G16(m, 0x05, ((addr + len - 1) & 0x7f) << 9);
        G16(m, 0x0a, (addr >> 7) & 0x1fff); G16(m, 0x0b, (addr & 0x7f) << 9);
        G8(m, 0x0c, 7);
        G16(m, 0x09, 0x0000);                                       // volume 0, then a ramp up
        G8(m, 0x07, 0x10); G8(m, 0x08, 0xf0); G8(m, 0x06, 0x3f);
        G8(m, 0x0d, 0x00);                                          // ramp running, up, no loop
        G8(m, 0x00, 0x08);                                          // voice: running, 8-bit, loop
    }

    private static void Gus()
    {
        const string id = "GUS_D074";
        var m = Make(id);
        G8(m, 0x0e, 13);                                            // 14 active voices
        // a 256-byte sine by DMA (channel 3) to DRAM 1000h
        for (int i = 0; i < 256; i++) m.PhysW8((uint)(0x20000 + i), (byte)(sbyte)(110 * Math.Sin(2 * Math.PI * i / 256)));
        m.Out8(0x0a, 0x07); m.Out8(0x0c, 0); m.Out8(0x0b, 0x4b);
        m.Out8(0x06, 0x00); m.Out8(0x06, 0x00); m.Out8(0x82, 0x02);
        m.Out8(0x07, 0xff); m.Out8(0x07, 0x00);
        G16(m, 0x42, 0x1000 >> 4);
        long irq0 = m.Card!.IrqsRaised;
        G8(m, 0x41, 0x21);                                          // DMA on, to the GUS, 8-bit, TC IRQ
        m.Out8(0x0a, 0x03);                                         // unmask channel 3: the upload happens now
        G16(m, 0x43, 0x1040); G8(m, 0x44, 0x00);
        byte peek = m.In8(0x347);
        byte want = (byte)(sbyte)(110 * Math.Sin(2 * Math.PI * 0x40 / 256));
        bool dmaOk = peek == want;
        Console.WriteLine($"{id,-11} {"DMA upload / peek",-22}          {(dmaOk ? "ok" : "FAIL")}  (DRAM 1040h = {peek:X2}, want {want:X2}; IRQs {m.Card.IrqsRaised - irq0})");
        if (!dmaOk) _flagged++;
        // poke a second copy at 2000h
        for (int i = 0; i < 256; i++) { G16(m, 0x43, 0x2000 + i); G8(m, 0x44, 0); m.Out8(0x347, (byte)(sbyte)(110 * Math.Sin(2 * Math.PI * i / 256))); }
        Take(m);
        GusVoice(m, 0, 0x1000, 256);
        Idle(m, 0.05); var start = Take(m);
        Idle(m, 0.3); var on = Take(m);
        Cell(id, "voice 0 (DMA wave)", on, extra: $"ramp: first 50 ms rms {Rms(start):F0}");
        GusVoice(m, 1, 0x2000, 256);
        Idle(m, 0.3);
        Cell(id, "voices 0+1 (poked)", Take(m));
        var m2 = Reload(m, id);
        Idle(m2, 0.3);
        Cell(id, "state mid-note", Take(m2));
    }

    // ================= Tandy =================

    private static void Tandy()
    {
        const string id = "TANDY_D072";
        for (int ch = 0; ch < 3; ch++)
        {
            var m = Make(id);
            int period = 254;
            m.Out8(0xc0, (byte)(0x80 | (ch << 5) | (period & 0xf))); m.Out8(0xc0, (byte)(period >> 4));
            m.Out8(0xc0, (byte)(0x90 | (ch << 5) | 0));
            Idle(m, 0.3);
            Cell(id, $"tone {ch}", Take(m));
        }
        {
            var m = Make(id);
            m.Out8(0x1e0, 0xe4); m.Out8(0x1e0, 0xf0);                   // white noise N/512 at full volume (through 1E0h)
            Idle(m, 0.3);
            Cell(id, "noise (1E0h)", Take(m));
        }
        {
            var m = Make(id);
            m.Out8(0xc0, 0x80 | 0x0e); m.Out8(0xc0, 0x0f); m.Out8(0xc0, 0x90);
            Idle(m, 0.2); Take(m);
            var m2 = Reload(m, id);
            Idle(m2, 0.3);
            Cell(id, "state mid-tone", Take(m2), extra: m2.Dma.SecondOpen ? "second DMA OPEN (wrong)" : "second DMA closed", ok: !m2.Dma.SecondOpen);
        }
    }

    // ================= Covox =================

    private static void Square(PcMachine m, double secs)
    {
        int n = (int)(secs * 2000);
        for (int i = 0; i < n; i++) { m.Out8(0x378, (byte)((i & 1) != 0 ? 0xc0 : 0x40)); Idle(m, 1 / 2000.0); }
    }

    private static void Covox()
    {
        const string id = "COVOX";
        var m = Make(id);
        bool noAdlib = m.In8(0x388) == 0xff;
        Square(m, 0.3);
        Cell(id, "square on 378h", Take(m), extra: noAdlib ? "388h FFh" : "AdLib answers (wrong)", ok: noAdlib);
        m.Out8(0x378, 0x5a);
        var m2 = Reload(m, id);
        bool latch = m2.In8(0x378) == 0x5a;
        Square(m2, 0.3);
        Cell(id, "state", Take(m2), extra: latch ? "latch restored" : "latch lost", ok: latch);
    }

    // ================= PC speaker =================

    private static void Speaker()
    {
        const string id = "SPEAKER";
        var m = Make(id);
        bool noAdlib = m.In8(0x388) == 0xff;
        int div = (int)(1193182 / 440);
        m.Out8(0x43, 0xb6); m.Out8(0x42, (byte)div); m.Out8(0x42, (byte)(div >> 8)); m.Out8(0x61, 0x03);
        Idle(m, 0.3);
        Cell(id, "PIT ch2 tone", Take(m), extra: noAdlib ? "388h FFh" : "AdLib answers (wrong)", ok: noAdlib);
        var m2 = Reload(m, id);
        Idle(m2, 0.3);
        Cell(id, "state mid-tone", Take(m2));
    }

    private static void WriteWav(string path, List<short> s)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + s.Count * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(44100); w.Write(88200); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(s.Count * 2); foreach (var x in s) w.Write(x);
    }
}
