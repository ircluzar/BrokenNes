using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NesEmulator.Sega;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// The sound-unit group of <c>--md-test</c>: the real unit (<see cref="MdSoundUnit"/>: the Z80, the PSG and the <see cref="Ym2612"/>) on the board's buses. The YM2612 through the 68000's window and the Z80's bus
/// (status, busy flag, the timers, the echo on the unused ports), the reset line, an FM note and its pitch, the DAC played by a real Z80 loop (its pitch is the Z80's timing in master clocks), the mute mask, the
/// hub voices, the state round trip, the two chip models, and that a second of sound costs no allocation. Every number is a measurement of the unit's audio output, not of a register.
/// </summary>
internal static class MdSoundTest
{
    private static Action<string, bool, string> check = (_, _, _) => { };
    private static void Check(string name, bool ok, string detail = "") => check(name, ok, detail);

    private static byte[] MakeRom()
    {
        var rom = new byte[0x20000];
        void L(int at, uint v) { rom[at] = (byte)(v >> 24); rom[at + 1] = (byte)(v >> 16); rom[at + 2] = (byte)(v >> 8); rom[at + 3] = (byte)v; }
        L(0, 0xFFFE00); L(4, 0x200);
        for (int v = 2; v < 64; v++) L(v * 4, 0x400);
        Encoding.ASCII.GetBytes("SEGA MEGA DRIVE ").CopyTo(rom, 0x100);
        Encoding.ASCII.GetBytes("BOARD TEST ROM".PadRight(48)).CopyTo(rom, 0x120);
        Encoding.ASCII.GetBytes("BOARD TEST ROM".PadRight(48)).CopyTo(rom, 0x150);
        Encoding.ASCII.GetBytes("GM 00000000-00").CopyTo(rom, 0x180);
        Encoding.ASCII.GetBytes("JUE".PadRight(16)).CopyTo(rom, 0x1F0);
        rom[0x200] = 0x60; rom[0x201] = 0xFE;           // BRA *: the 68000 spins
        rom[0x400] = 0x60; rom[0x401] = 0xFE;
        return rom;
    }

    private static BOARD_MD Board(GenesisModel model = GenesisModel.Model1) => new(new MdCartridge(MakeRom()), SegaRegion.Ntsc, model, 44100);

    private static byte R8(BOARD_MD b, uint a) { ushort w = b.BusRead(a & ~1u, (a & 1) == 0, (a & 1) != 0, 4); return (byte)((a & 1) == 0 ? w >> 8 : w); }
    private static void W8(BOARD_MD b, uint a, byte v) => b.BusWrite(a & ~1u, (a & 1) == 0, (a & 1) != 0, (ushort)(v << 8 | v), 4);
    private static void W16(BOARD_MD b, uint a, ushort v) => b.BusWrite(a, true, true, v, 4);
    private static void Idle(BOARD_MD b, int cycles) { b.BusIdle(cycles); b.Vdp.RunTo(b.MasterClock); }

    /// <summary>The 68000 takes the Z80 bus (the Z80 is held in reset at power-on, so the grant is at once).</summary>
    private static void TakeBus(BOARD_MD b) { W16(b, 0xA11100, 0x0100); Idle(b, 20); }

    private static void YmWrite(BOARD_MD b, int part, int reg, int value)
    {
        W8(b, (uint)(0xA04000 + part * 2), (byte)reg);
        W8(b, (uint)(0xA04001 + part * 2), (byte)value);
        Idle(b, 220);          // longer than the busy flag (204 chip clocks = 204 68000 cycles of 7 master clocks each)
    }

    /// <summary>A pure tone on channel 1: algorithm 7 with only operator 1 audible (total level <paramref name="tl"/>), multiple 1, attack 31, held.</summary>
    private static void FmTone(BOARD_MD b, int fnum, int block, int tl = 16)
    {
        for (int n = 0; n < 4; n++)
        {
            int off = new[] { 0, 8, 4, 12 }[n];
            YmWrite(b, 0, 0x30 + off, 0x01);
            YmWrite(b, 0, 0x40 + off, n == 0 ? tl : 127);
            YmWrite(b, 0, 0x50 + off, 0x1F);
            YmWrite(b, 0, 0x60 + off, 0x00);
            YmWrite(b, 0, 0x70 + off, 0x00);
            YmWrite(b, 0, 0x80 + off, 0x0F);
        }
        YmWrite(b, 0, 0xB0, 0x07);
        YmWrite(b, 0, 0xB4, 0xC0);
        YmWrite(b, 0, 0xA4, block << 3 | fnum >> 8);
        YmWrite(b, 0, 0xA0, fnum & 0xFF);
        YmWrite(b, 0, 0x28, 0xF0);
    }

    /// <summary>Runs frames and collects the left channel of what the unit delivers.</summary>
    private static List<short> Capture(BOARD_MD b, int frames)
    {
        var left = new List<short>();
        var buf = new short[16384];
        for (int i = 0; i < frames; i++)
        {
            b.RunFrame();
            int n;
            while ((n = b.Sound.ReadSamples(buf)) > 0) for (int k = 0; k < n; k += 2) left.Add(buf[k]);
        }
        return left;
    }

    /// <summary>The frequency of a signal from its upward zero crossings (the signal has no DC: the mixer removes it), over the given stretch.</summary>
    private static double ZeroCrossingHz(IList<short> s, int from, int to, int sampleRate)
    {
        int crossings = 0; int first = -1, last = -1;
        for (int i = Math.Max(1, from); i < to && i < s.Count; i++)
            if (s[i - 1] < 0 && s[i] >= 0) { crossings++; if (first < 0) first = i; last = i; }
        return crossings < 3 ? 0 : (crossings - 1) * (double)sampleRate / (last - first);
    }

    private static int Peak(IList<short> s, int from, int to) { int p = 0; for (int i = from; i < to && i < s.Count; i++) p = Math.Max(p, Math.Abs((int)s[i])); return p; }

    public static void Run(Action<string, bool, string> report, Action<string> group)
    {
        check = report;
        group("the real sound unit: YM2612 on the 68000's and the Z80's buses, FM, DAC and PSG mixed, voices, models");
        YmThroughTheWindow();
        Timers();
        Reset();
        FmNote();
        DacFromTheZ80();
        MuteAndMix();
        Voices();
        StateAndModels();
        NoAllocation();
    }

    private static void YmThroughTheWindow()
    {
        var b = Board();
        var unit = (MdSoundUnit)b.Sound;
        TakeBus(b);
        W8(b, 0xA04000, 0x30); W8(b, 0xA04001, 0x71);
        Check("a 68000 write to $A04000/1 reaches the YM2612's register file (reg $30 = $71)", unit.Fm.Register(0x30) == 0x71 && unit.YmWrites == 1);
        Idle(b, 25);       // 25 x 7 = 175 master clocks = 25 chip clocks: past the 12 clocks the flag takes to rise
        Check("the busy flag (status bit 7) is up about 25 microseconds after a data write", (R8(b, 0xA04000) & 0x80) != 0, $"{R8(b, 0xA04000):X2}");
        Idle(b, 250);
        Check("... and drops 204 chip clocks after the write", (R8(b, 0xA04000) & 0x80) == 0);
        YmWrite(b, 1, 0xA4, 0x22); YmWrite(b, 1, 0xA0, 0x55);
        Check("part 2 ($A04002/3) writes the other register bank: the frequency latch pair of channel 4 (the high byte applies with the low one)", unit.Fm.Register(0x1A0) == 0x55 && unit.Fm.Register(0x1A4) == 0x22 && unit.Fm.Register(0xA0) == 0);
        // 8-bit DAC data and enable do not touch the FM registers
        YmWrite(b, 0, 0x2B, 0x80); YmWrite(b, 0, 0x2A, 0xFF);
        Check("register $2B enables the DAC, $2A gives it a value (+127), neither lands in the FM register file", unit.Fm.DacEnabled && unit.Fm.DacValue == 254 && unit.Fm.Register(0x2B) == 0);
    }

    private static void Timers()
    {
        var b = Board();
        var unit = (MdSoundUnit)b.Sound;
        TakeBus(b);
        // timer A = 1000: 24 FM samples = 24 x 1008 master clocks = 3456 68000 cycles (+ the load phase)
        YmWrite(b, 0, 0x24, 1000 >> 2); YmWrite(b, 0, 0x25, 1000 & 3);
        YmWrite(b, 0, 0x27, 0x15);
        Idle(b, 3000);
        bool before = (R8(b, 0xA04000) & 1) != 0;
        Idle(b, 900);
        bool after = (R8(b, 0xA04000) & 1) != 0;
        Check("timer A (reload 1000) sets status bit 0 after 24 FM samples, not before", !before && after, $"{before} {after}");
        YmWrite(b, 0, 0x27, 0x15);          // reset the flag, keep the timer loaded
        Check("writing $27 with bit 4 set clears timer A's flag", (R8(b, 0xA04000) & 1) == 0);
        Idle(b, 3600);
        Check("a loaded timer A runs on and sets the flag again", (R8(b, 0xA04000) & 1) != 0);
        // timer B = 250: 96 samples
        YmWrite(b, 0, 0x27, 0x00);
        YmWrite(b, 0, 0x26, 250);
        YmWrite(b, 0, 0x27, 0x2A);
        Idle(b, 60 * 144 - 220);
        bool bBefore = (R8(b, 0xA04000) & 2) != 0;
        Idle(b, 40 * 144);
        bool bAfter = (R8(b, 0xA04000) & 2) != 0;
        Check("timer B (reload 250, 96 FM samples, the first period up to 15 shorter as its prescaler runs free) is clear at 60 samples and set by 100", !bBefore && bAfter, $"{bBefore} {bAfter}");
        // the echo of the last status read on ports 1-3 (the discrete chip), and the live status on every port (the integrated one)
        var m1 = Board(GenesisModel.Model1); var m3 = Board(GenesisModel.Model3);
        foreach (var bb in new[] { m1, m3 })
        {
            TakeBus(bb);
            YmWrite(bb, 0, 0x26, 255); YmWrite(bb, 0, 0x27, 0x2A);
            Idle(bb, 17 * 144 + 200);
        }
        byte p1 = R8(m1, 0xA04001), p3 = R8(m3, 0xA04001);
        byte s1 = R8(m1, 0xA04000), t1 = R8(m1, 0xA04001);
        Check("Model 1 (discrete YM2612): a read of port 1 before any status read answers 0, after one it echoes it; Model 3 (YM3438): every port reads the live status",
            p1 == 0 && (s1 & 2) != 0 && t1 == s1 && (p3 & 2) != 0, $"model 1 {p1:X2}/{s1:X2}/{t1:X2}, model 3 {p3:X2}");
    }

    private static void Reset()
    {
        var b = Board();
        var unit = (MdSoundUnit)b.Sound;
        TakeBus(b);
        YmWrite(b, 0, 0x40, 0x23); YmWrite(b, 0, 0x2B, 0x80);
        W16(b, 0xA11200, 0x0000);        // assert RESET
        Idle(b, 40);
        Check("RESET asserted clears the YM2612's registers (TL, the DAC enable) as it holds the Z80", unit.Fm.Register(0x40) == 0 && !unit.Fm.DacEnabled && unit.IsInReset);
        Check("... and both pan bits come back on (the chip's reset state)", unit.Fm.Register(0xB4) == 0xC0);
        long samplesBefore = unit.Fm.Samples;
        Idle(b, 2000);
        b.Vdp.RunTo(b.MasterClock); R8(b, 0xA04000);
        Check("the chip keeps its sample clock through a reset (the audio stream does not restart)", unit.Fm.Samples > samplesBefore);
    }

    private static void FmNote()
    {
        var b = Board();
        var unit = (MdSoundUnit)b.Sound;
        TakeBus(b);
        FmTone(b, 0x28C, 4);
        W16(b, 0xA11100, 0x0000);        // give the bus back; the YM2612 keeps playing without the Z80
        var s = Capture(b, 24);
        int rate = 44100;
        double hz = ZeroCrossingHz(s, 4 * 735, s.Count, rate);
        double expected = 652 * 16 * unit.Fm.InputClockHz / 144 / 2097152.0;
        Check($"an FM note (fnum $28C, block 4) plays at {expected:F1} Hz as measured in the unit's 44.1 kHz output", Math.Abs(hz - expected) / expected < 0.01, $"{hz:F2} Hz");
        int peak = Peak(s, 4 * 735, s.Count);
        Check("... with a clear, unclipped level (peak between 500 and 20000 of 32768)", peak > 500 && peak < 20000, $"peak {peak}");
        int dc = (int)s.Skip(4 * 735).Average(x => (double)x);
        Check("... and no DC offset (the mixer's high-pass removes the chip's)", Math.Abs(dc) < 60, $"mean {dc}");
    }

    private static void DacFromTheZ80()
    {
        var b = Board();
        var unit = (MdSoundUnit)b.Sound;
        TakeBus(b);
        byte[] prog =
        {
            0x21, 0x00, 0x40,          // LD HL,$4000
            0x36, 0x2B, 0x23, 0x36, 0x80, 0x2B,     // LD (HL),$2B ; INC HL ; LD (HL),$80 ; DEC HL   (DAC on)
            0x36, 0x2A, 0x23,          // LD (HL),$2A ; INC HL   (HL = data port)
            0x36, 0xFF, 0x06, 0x14, 0x10, 0xFE,     // loop: LD (HL),$FF ; LD B,20 ; DJNZ $
            0x36, 0x00, 0x06, 0x14, 0x10, 0xFE,     //       LD (HL),$00 ; LD B,20 ; DJNZ $
            0x18, 0xF2,                // JR loop
        };
        for (int i = 0; i < prog.Length; i++) W8(b, (uint)(0xA00000 + i), prog[i]);
        W16(b, 0xA11200, 0x0100); W16(b, 0xA11100, 0x0000);
        var s = Capture(b, 24);
        double hz = ZeroCrossingHz(s, 4 * 735, s.Count, 44100);
        // a 556 T-state loop on a Z80 at master / 15
        double expected = b.Timeline.MasterHz / 15.0 / 556.0;
        Check($"a Z80 loop writing the DAC every 556 T-states plays a square wave at {expected:F1} Hz (the Z80 timed in master clocks, the DAC sampled at 53 kHz)", Math.Abs(hz - expected) / expected < 0.01, $"{hz:F2} Hz");
        int peak = Peak(s, 4 * 735, s.Count);
        // the band-limited square wave overshoots its edges by up to about 40 percent (Gibbs): the peak depends on where the edges fall against the output samples, which moves with the 68000's start time
        Check("... at the DAC's amplitude (+-127 on the 9-bit scale, about +-5400 of 32768 at the output)", peak > 4000 && peak < 7900, $"peak {peak}");
    }

    private static void MuteAndMix()
    {
        var b = Board();
        var unit = (MdSoundUnit)b.Sound;
        TakeBus(b);
        FmTone(b, 0x28C, 4);
        W16(b, 0xA11100, 0x0000);
        unit.FmMuteMask = 1;
        var muted = Capture(b, 10);
        int peakMuted = Peak(muted, 6 * 735, muted.Count);          // after the 5 Hz DC filter has settled from the step the mute makes
        unit.FmMuteMask = 0;
        var open = Capture(b, 10);
        int peakOpen = Peak(open, 3 * 735, open.Count);
        Check("the FM mute mask silences channel 1 and unmuting brings it back", peakMuted < 50 && peakOpen > 500, $"muted peak {peakMuted}, open peak {peakOpen}");
        // the PSG on top: tone 1 at period 254 (about 440 Hz) at full volume, written by the 68000 through the VDP's PSG port
        unit.FmMuteMask = 1;
        b.BusWrite(0xC00010, false, true, 0x90, 4);          // tone 1 volume 0 (loudest)
        b.BusWrite(0xC00010, false, true, 0x8E, 4);          // tone 1 period low nibble
        b.BusWrite(0xC00010, false, true, 0x0F, 4);          // tone 1 period high bits: period 254
        var psgOnly = Capture(b, 10);
        double hz = ZeroCrossingHz(psgOnly, 3 * 735, psgOnly.Count, 44100);
        double expected = b.Timeline.MasterHz / 15.0 / (32.0 * 254);
        Check($"with FM muted the PSG alone plays tone 1 at {expected:F1} Hz in the same output", Math.Abs(hz - expected) / expected < 0.01 && Peak(psgOnly, 3 * 735, psgOnly.Count) > 500, $"{hz:F2} Hz");
        unit.FmMuteMask = 0;
        var both = Capture(b, 10);
        Check("with both the output carries more than either alone (the two streams add)", Peak(both, 3 * 735, both.Count) > Math.Max(peakOpen, Peak(psgOnly, 3 * 735, psgOnly.Count)) * 0.9, $"{Peak(both, 3 * 735, both.Count)}");
    }

    private static void Voices()
    {
        var b = Board();
        var unit = (MdSoundUnit)b.Sound;
        var v = new HubVoice[16];
        int n = unit.DescribeVoices(v);
        Check("the unit describes 11 voices: 6 FM, the DAC, 3 PSG tones, the PSG noise", n == 11 && n == MdSoundUnit.VoiceCount);
        Check("at power-on every voice is silent", Enumerable.Range(0, n).All(i => !v[i].Audible));
        TakeBus(b);
        FmTone(b, 0x28C, 4);
        W16(b, 0xA11100, 0x0000);
        b.RunFrame();
        n = unit.DescribeVoices(v);
        double expected = 652 * 16 * unit.Fm.InputClockHz / 144 / 2097152.0;
        Check("FM channel 1, keyed, is voice 0: a pulse at the note's pitch with a level; the other five FM voices stay silent",
            v[0].Audible && v[0].Kind == HubVoiceKind.Pulse && Math.Abs(v[0].Hz - expected) / expected < 0.01 && Enumerable.Range(1, 6).All(i => !v[i].Audible), $"{v[0].Hz:F1} Hz level {v[0].Level:F3}");
        YmWriteLater(b, 0x28, 0x00);
        b.RunFrame();
        n = unit.DescribeVoices(v);
        Check("keying the channel off silences its voice (the Z80 is not needed: the voice describes the chip)", !v[0].Audible);
        unit.Psg.Write(0x90); unit.Psg.Write(0x8E); unit.Psg.Write(0x0F);
        n = unit.DescribeVoices(v);
        Check("PSG tone 1 at period 254 is voice 7, tone 3 a triangle, the noise last", v[7].Audible && v[9].Kind == HubVoiceKind.Triangle && v[10].Kind == HubVoiceKind.Noise && Math.Abs(v[7].Hz - b.Timeline.MasterHz / 15.0 / (32.0 * 254)) < 2, $"{v[7].Hz:F1} Hz");
    }

    private static void YmWriteLater(BOARD_MD b, int reg, int value)
    {
        TakeBus(b);
        YmWrite(b, 0, reg, value);
        W16(b, 0xA11100, 0x0000);
    }

    private static void StateAndModels()
    {
        var b = Board();
        var unit = (MdSoundUnit)b.Sound;
        TakeBus(b);
        FmTone(b, 0x28C, 4);
        W16(b, 0xA11100, 0x0000);
        for (int i = 0; i < 6; i++) b.RunFrame();
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true)) b.SaveState(w);
        for (int i = 0; i < 10; i++) b.RunFrame();
        string a = unit.Fm.Describe() + " z80pc=" + unit.Cpu.PC.ToString("X4");
        long aSamples = unit.Fm.Samples; int aLeft = unit.Fm.LastLeft;
        var b2 = Board();
        ms.Position = 0;
        using (var r = new BinaryReader(ms, Encoding.UTF8, true)) b2.LoadState(r);
        for (int i = 0; i < 10; i++) b2.RunFrame();
        var u2 = (MdSoundUnit)b2.Sound;
        string a2 = u2.Fm.Describe() + " z80pc=" + u2.Cpu.PC.ToString("X4");
        Check("a saved board state restores the sound unit: ten frames later it is in the same state, the same sample and the same output level (FM phases, envelopes, Z80, timers)", a2 == a && u2.Fm.Samples == aSamples && u2.Fm.LastLeft == aLeft, $"{a2} vs {a}");
        var m1 = Board(GenesisModel.Model1); var m3 = Board(GenesisModel.Model3);
        m1.RunFrame(); m3.RunFrame();
        Check("Model 1 plays through the discrete YM2612 (the ladder effect: an idle chip rests at a DC offset), Models 2 and 3 through the YM3438 (it rests at zero)",
            ((MdSoundUnit)m1.Sound).Fm.Ladder && !((MdSoundUnit)m3.Sound).Fm.Ladder && ((MdSoundUnit)m1.Sound).Fm.LastLeft != 0 && ((MdSoundUnit)m3.Sound).Fm.LastLeft == 0,
            $"{((MdSoundUnit)m1.Sound).Fm.LastLeft} / {((MdSoundUnit)m3.Sound).Fm.LastLeft}");
        Check("the unit's name says which chip it plays through", m1.Sound.CoreName.Contains("YM2612") && m3.Sound.CoreName.Contains("YM3438"), m1.Sound.CoreName + " | " + m3.Sound.CoreName);
    }

    private static void NoAllocation()
    {
        // the sound unit alone (no VDP, no 68000): a second of master clock with the FM keyed and the PSG sounding, drained as a host would
        var machine = new SegaMachine(SegaTimeline.Genesis(SegaRegion.Ntsc), GenesisModel.Model1, 44100);
        var unit = new MdSoundUnit(machine);
        unit.Reset();
        long t = 0;
        void Step(long dt) { t += dt; unit.RunTo(t); }
        unit.WriteReset(true, t); unit.WriteBusRequest(true, t);
        Step(100000);
        var fm = unit.Fm;
        void Y(int part, int reg, int val) { unit.Write(0x4000 + part * 2, (byte)reg, t); Step(2000); unit.Write(0x4001 + part * 2, (byte)val, t); Step(2000); }
        for (int n = 0; n < 4; n++) { int off = new[] { 0, 8, 4, 12 }[n]; Y(0, 0x30 + off, 1); Y(0, 0x40 + off, 20); Y(0, 0x50 + off, 0x1F); Y(0, 0x80 + off, 0x0F); }
        Y(0, 0xB0, 4); Y(0, 0xB4, 0xC0); Y(0, 0xA4, 0x22); Y(0, 0xA0, 0x80); Y(0, 0x28, 0xF0); Y(0, 0x22, 0x0C);
        unit.WritePsg(0x90, t); unit.WritePsg(0x8E, t); unit.WritePsg(0x0F, t);
        var buf = new short[4096];
        for (int warm = 0; warm < 30; warm++) { Step(900000); while (unit.ReadSamples(buf) > 0) { } }
        long before = GC.GetAllocatedBytesForCurrentThread();
        long produced = 0;
        for (int frame = 0; frame < 60; frame++)
        {
            Step(53693175 / 60);
            int n; while ((n = unit.ReadSamples(buf)) > 0) produced += n / 2;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check($"one second of FM + PSG sound (60 frames, {produced:N0} samples out) allocates nothing", allocated == 0 && produced > 40000, $"{allocated} bytes, {produced} samples");
    }
}
