using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>Hand-assembled Z80 programs for the YM2413 on the board: probes whose results are read from work RAM (and run on the oracle, Mesen 2.1.1, to learn what it answers) and the programs the audio checks play.</summary>
internal static class FmProbes
{
    /// <summary>
    /// The audio-control port. Stores the power-on reads of $DC, $DD, $F0, $F1, $F2 at $C0FA..$C0FE, then writes every value 0..255 to $F2 and reads $F2 straight back into $C100 + value.
    /// </summary>
    public static byte[] F2Readback(bool ioOff = false)
    {
        var m = SmsProg.Cat(
            SmsProg.Prolog,
            ioOff ? IoOff : Array.Empty<byte>(),
            SmsProg.B(0xDB, 0xDC, 0x32, 0xFA, 0xC0, 0xDB, 0xDD, 0x32, 0xFB, 0xC0, 0xDB, 0xF0, 0x32, 0xFC, 0xC0, 0xDB, 0xF1, 0x32, 0xFD, 0xC0, 0xDB, 0xF2, 0x32, 0xFE, 0xC0),
            SmsProg.B(0x21, 0x00, 0xC1, 0x0E, 0x00),                                        // LD HL,$C100; LD C,0
            SmsProg.B(0x79, 0xD3, 0xF2, 0xDB, 0xF2, 0x77, 0x23, 0x0C, 0x20, 0xF6),          // loop: LD A,C; OUT ($F2),A; IN A,($F2); LD (HL),A; INC HL; INC C; JR NZ,loop
            SmsProg.Halt);
        return SmsProg.Rom(m);
    }

    /// <summary>
    /// A tone on one melodic channel through ports $F0/$F1 and the audio control: $F2 = <paramref name="control"/>, instrument 0 (the user instrument) with a sine-like modulator-less patch: carrier multiplier 1, full attack,
    /// no decay, sustain level 0, volume 0, channel 0 at block <paramref name="block"/> / F-number <paramref name="fnum"/>, key on. Optionally also a PSG tone (channel 0, period <paramref name="psgPeriod"/>, volume 0).
    /// </summary>
    public static byte[] Tone(int control, int fnum, int block, int volume = 0, int ml = 1, int psgPeriod = -1)
    {
        var w = new List<(int Port, int Value)> { (0xF2, control) };
        foreach (var (reg, val) in ToneRegisters(fnum, block, volume, ml)) { w.Add((0xF0, reg)); w.Add((0xF1, val)); }
        if (psgPeriod >= 0) { w.Add((0x7F, 0x80 | (psgPeriod & 0xF))); w.Add((0x7F, psgPeriod >> 4)); w.Add((0x7F, 0x90)); }
        return SmsProg.Rom(SmsProg.Cat(SmsProg.Prolog, SmsProg.Out(w.ToArray()), SmsProg.Halt));
    }

    /// <summary>The register writes of <see cref="Tone"/>: the user instrument as a plain sine (modulator multiplier 1 with total level 63, carrier multiplier <paramref name="ml"/>, sustained, attack 15, decay 0, sustain level 0,
    /// release 0), instrument 0 at <paramref name="volume"/>, channel 0 at F-number / block, key on.</summary>
    public static List<(int Reg, int Val)> ToneRegisters(int fnum, int block, int volume, int ml) => new()
    {
        (0x00, 0x21), (0x01, 0x20 | ml), (0x02, 0x3F), (0x03, 0x00), (0x04, 0xF0), (0x05, 0xF0), (0x06, 0x00), (0x07, 0x00),
        (0x30, volume), (0x10, fnum & 0xFF), (0x20, 0x10 | block << 1 | fnum >> 8),
    };

    /// <summary>All nine melodic channels sounding (instrument 1 at block 3, F-numbers 0x80 + 0x20 k): the most work the chip has, for the speed measurement and the voice listing. $F2 = 1.</summary>
    public static byte[] NineChannels(out int[] fnums)
    {
        fnums = Enumerable.Range(0, 9).Select(k => 0x80 + 0x20 * k).ToArray();
        var w = new List<(int Port, int Value)> { (0xF2, 1) };
        for (int k = 0; k < 9; k++)
        {
            foreach (var (reg, val) in new[] { (0x30 + k, 0x10), (0x10 + k, fnums[k] & 0xFF), (0x20 + k, 0x10 | 3 << 1 | fnums[k] >> 8) }) { w.Add((0xF0, reg)); w.Add((0xF1, val)); }
        }
        return SmsProg.Rom(SmsProg.Cat(SmsProg.Prolog, SmsProg.Out(w.ToArray()), SmsProg.Halt));
    }

    /// <summary>Rhythm mode with the bass drum (channel 6's F-number, volume 0), keyed or not.</summary>
    public static byte[] BassDrum(bool keyed)
    {
        var w = new List<(int Port, int Value)> { (0xF2, 1) };
        foreach (var (reg, val) in new[] { (0x16, 0x20), (0x26, 0x05), (0x36, 0x00), (0x0E, keyed ? 0x30 : 0x20) }) { w.Add((0xF0, reg)); w.Add((0xF1, val)); }
        return SmsProg.Rom(SmsProg.Cat(SmsProg.Prolog, SmsProg.Out(w.ToArray()), SmsProg.Halt));
    }

    public static readonly (string Name, (int Port, int Value)[] Writes)[] SequenceProbes =
    {
        ("none", Array.Empty<(int, int)>()),
        ("f2-0-reg", new[] { (0xF2, 0x00), (0xF0, 0x30), (0xF1, 0x00) }),
        ("f2-1-addr", new[] { (0xF2, 0x01), (0xF0, 0x30) }),
        ("f2-1-reg", new[] { (0xF2, 0x01), (0xF0, 0x30), (0xF1, 0x00) }),
        ("f2-3-reg", new[] { (0xF2, 0x03), (0xF0, 0x30), (0xF1, 0x00) }),
        ("reg-only", new[] { (0xF0, 0x30), (0xF1, 0x00) }),
    };

    /// <summary>One write to $F2, then idle (the oracle's state is read after the write; fm-f2w-VV.sms).</summary>
    public static byte[] F2Write(int value) => SmsProg.Rom(SmsProg.Cat(SmsProg.Prolog, SmsProg.Out((0xF2, value)), SmsProg.Halt));

    /// <summary>The given port writes, then the reads of $F2, $F0, $F1, $DC into $C0F0..$C0F3 (what the unit answers after that sequence).</summary>
    public static byte[] Sequence(params (int Port, int Value)[] writes) => SequenceAfter(false, false, writes);

    /// <summary>LD A,$0C; OUT ($3E),A: the I/O chip off (bit 2) and the BIOS off (bit 3): what a game does before it probes the FM unit (Phantasy Star's detection starts with it).</summary>
    public static readonly byte[] IoOff = SmsProg.B(0x3E, 0x0C, 0xD3, 0x3E);

    /// <summary>As <see cref="Sequence"/>, optionally after a wait of about 30 frames (so a pad held from frame 0 is there to be read) and with the I/O chip off.</summary>
    public static byte[] SequenceAfter(bool wait, bool ioOff, params (int Port, int Value)[] writes) =>
        SmsProg.Rom(SmsProg.Cat(SmsProg.Prolog, wait ? SmsProg.B(0x11, 0x00, 0x00, 0x1B, 0x7A, 0xB3, 0x20, 0xFB) : Array.Empty<byte>(), ioOff ? IoOff : Array.Empty<byte>(), SmsProg.Out(writes), SmsProg.B(0xDB, 0xF2, 0x32, 0xF0, 0xC0, 0xDB, 0xF0, 0x32, 0xF1, 0xC0, 0xDB, 0xF1, 0x32, 0xF2, 0xC0, 0xDB, 0xDC, 0x32, 0xF3, 0xC0), SmsProg.Halt));

    /// <summary>--write-fm-probes: the probe ROMs to a directory (to run on the oracle and on this emulator with the same scripts).</summary>
    public static int Write(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "fm-f2.sms"), F2Readback());
        foreach (int v in new[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x07, 0x80, 0xFF }) File.WriteAllBytes(Path.Combine(dir, $"fm-f2w-{v:X2}.sms"), F2Write(v));
        foreach (var (name, seq) in SequenceProbes) File.WriteAllBytes(Path.Combine(dir, $"fm-seq-{name}.sms"), Sequence(seq));
        File.WriteAllBytes(Path.Combine(dir, "fm-pad.sms"), SequenceAfter(true, false));
        File.WriteAllBytes(Path.Combine(dir, "fm-pad-f2-1.sms"), SequenceAfter(true, false, (0xF2, 0x01)));
        File.WriteAllBytes(Path.Combine(dir, "fm-io-f2.sms"), F2Readback(true));
        foreach (var (name, seq) in SequenceProbes) File.WriteAllBytes(Path.Combine(dir, $"fm-io-seq-{name}.sms"), SequenceAfter(false, true, seq));
        File.WriteAllBytes(Path.Combine(dir, "fm-io-pad.sms"), SequenceAfter(true, true));
        for (int c = 0; c < 4; c++) File.WriteAllBytes(Path.Combine(dir, $"fm-tone-c{c}.sms"), Tone(c, 290, 4, psgPeriod: 254));
        File.WriteAllBytes(Path.Combine(dir, "fm-tone-1.sms"), Tone(1, 290, 4));
        File.WriteAllBytes(Path.Combine(dir, "fm-tone-3-psg.sms"), Tone(3, 290, 4, psgPeriod: 254));
        Console.WriteLine($"wrote probes to {dir}");
        return 0;
    }
}
