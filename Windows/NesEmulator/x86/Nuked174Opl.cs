// Nuked OPL3 emulator, version 1.7.4, ported to C# as an AdLib engine ("NUK174").
//
// Source:   Nuked-OPL3 1.7.4 by Alexey Khokholov (Nuke.YKT, "nukeykt"), as shipped in AdPlug
//           tag adplug-2.3.1, files src/nukedopl.c + src/nukedopl.h
//           https://github.com/adplug/adplug/tree/adplug-2.3.1/src
//           (upstream project: https://github.com/nukeykt/Nuked-OPL3)
// Authors:  Alexey Khokholov; credits in the original: MAME Development Team (Jarek Burczynski,
//           Tatsuyuki Satoh), forums.submarine.org.uk (carbon14, opl3), Matthew Gambrell and
//           Olli Niemitalo (OPLx decapsulated, OPL2 ROMs).
// Copyright (C) 2013-2016 Alexey Khokholov (Nuke.YKT)
// Licence:  GNU Lesser General Public License, version 2.1 or (at your option) any later version.
//           This file is therefore LGPL-2.1+ (a documented exception, see THIRD_PARTY_NOTICES.md
//           section 2a); it may be replaced or relinked independently of the rest of BrokenNes.
//
// This is an "art instrument" port: a FAITHFUL translation, flaws included. Nothing is fixed.
// 1.7.4 predates the 1.8 rewrite that matched the YMF262 die shot more closely; 1.7.4's
// behaviour is kept on purpose. Characteristic quirks kept:
//  - Envelope generator is per-slot "rate -> increment" logic driven by a 16-bit sample counter,
//    not 1.8's die-shot EG (shared eg_timer, eg_add, eg_state clocked every other sample);
//    attack/decay step patterns and timings therefore differ from real hardware and from 1.8.
//  - Attack curve eg_rout += (~eg_rout * inc) >> 3 with a clamp at 0; key-on with an attack
//    rate whose high bits are 15 jumps straight to decay at full level (instant attack).
//  - Key-on resets the phase accumulator (pg_phase = 0) at key-on time immediately, from the
//    register write, not on the next EG clock as in 1.8.
//  - Total level/KSL/tremolo are summed into eg_out *before* the envelope state advances, so the
//    output lags the envelope by one sample; KSL is recomputed only on A0/B0/40 writes.
//  - Tremolo: 210-step triangle advanced every 64 samples; vibrato: 8-step position every 1024
//    samples, both from the shared 16-bit timer (1.8 uses different counters).
//  - Rhythm mode: hi-hat/top-cymbal/snare phases built from 1.7.4's bit formulas and a noise LFSR
//    (seed 0x306600, taps 0x800302) clocked once per sample; bass drum fed through the channel-6
//    algorithm; channel outputs of 7 and 8 sum both drum slots twice-wired as in the original.
//  - Waveform register masks to 0-3 only when NEW=0 *at the time of the E0 write* (not re-masked
//    if NEW changes later).
//  - Channel accumulators are 16-bit (Bit16s accm) and wrap before the 32-bit mix; final output is
//    clipped to 16 bits; there is no DAC/floating-point shaping stage.
//  - The right output (buf[1]) is the PREVIOUS sample's right mix (one-sample lag vs the left);
//    this engine is mono and takes the LEFT channel. In OPL2 mode (NEW=0) both channels carry the
//    same channels (cha = chb = ~0), so left is the full mix.
//  - The timers/status register ($02-$04 / IRQ) are not emulated at all (1.7.4 has none); the
//    AdLib card in BrokenNes handles timer status itself.
// Rendering: native 49716 Hz with OPL3_Generate. AdPlug 2.3.1's player used OPL3_GenerateResampled
// (linear interpolation, RSM_FRAC 10) to reach the host rate; that resampler is a generic
// convenience, not part of the chip model, so it is not used here (it is still ported below
// for completeness). The buffered-write queue (OPL3_WriteRegBuffered) is not used: writes go
// straight to OPL3_WriteReg, so the queue in OPL3_Generate never fires and is omitted.

namespace NesEmulator.X86;

public sealed class Nuked174OplEngine : IOplEngine
{
    private Nuked174Opl3 _chip = new(49716);
    private readonly short[] _buf = new short[2];
    public string Id => "NUK174";
    public int Rate => 49716;
    public int BlockSamples => 0;
    public void Reset() => _chip = new Nuked174Opl3(49716);
    public void Write(int reg, byte value) => _chip.WriteReg((ushort)(reg & 0xff), value);
    public void Generate(short[] dest, int count)
    {
        for (int i = 0; i < count; i++) { _chip.Generate(_buf); dest[i] = _buf[0]; }
    }
}

/// <summary>Nuked OPL3 1.7.4 chip (opl3_chip). Pointers into Bit16s cells are indices into <see cref="_c"/>; index 0 is zeromod.</summary>
public sealed class Nuked174Opl3
{
    private const int RSM_FRAC = 10;
    private const byte ch_2op = 0, ch_4op = 1, ch_4op2 = 2, ch_drum = 3;
    private const byte egk_norm = 1, egk_drum = 2;
    private const byte eg_off = 0, eg_attack = 1, eg_decay = 2, eg_sustain = 3, eg_release = 4;
    private const int ZERO = 0;

    private static readonly ushort[] logsinrom = {
        0x859, 0x6c3, 0x607, 0x58b, 0x52e, 0x4e4, 0x4a6, 0x471, 0x443, 0x41a, 0x3f5, 0x3d3, 0x3b5, 0x398, 0x37e, 0x365,
        0x34e, 0x339, 0x324, 0x311, 0x2ff, 0x2ed, 0x2dc, 0x2cd, 0x2bd, 0x2af, 0x2a0, 0x293, 0x286, 0x279, 0x26d, 0x261,
        0x256, 0x24b, 0x240, 0x236, 0x22c, 0x222, 0x218, 0x20f, 0x206, 0x1fd, 0x1f5, 0x1ec, 0x1e4, 0x1dc, 0x1d4, 0x1cd,
        0x1c5, 0x1be, 0x1b7, 0x1b0, 0x1a9, 0x1a2, 0x19b, 0x195, 0x18f, 0x188, 0x182, 0x17c, 0x177, 0x171, 0x16b, 0x166,
        0x160, 0x15b, 0x155, 0x150, 0x14b, 0x146, 0x141, 0x13c, 0x137, 0x133, 0x12e, 0x129, 0x125, 0x121, 0x11c, 0x118,
        0x114, 0x10f, 0x10b, 0x107, 0x103, 0x0ff, 0x0fb, 0x0f8, 0x0f4, 0x0f0, 0x0ec, 0x0e9, 0x0e5, 0x0e2, 0x0de, 0x0db,
        0x0d7, 0x0d4, 0x0d1, 0x0cd, 0x0ca, 0x0c7, 0x0c4, 0x0c1, 0x0be, 0x0bb, 0x0b8, 0x0b5, 0x0b2, 0x0af, 0x0ac, 0x0a9,
        0x0a7, 0x0a4, 0x0a1, 0x09f, 0x09c, 0x099, 0x097, 0x094, 0x092, 0x08f, 0x08d, 0x08a, 0x088, 0x086, 0x083, 0x081,
        0x07f, 0x07d, 0x07a, 0x078, 0x076, 0x074, 0x072, 0x070, 0x06e, 0x06c, 0x06a, 0x068, 0x066, 0x064, 0x062, 0x060,
        0x05e, 0x05c, 0x05b, 0x059, 0x057, 0x055, 0x053, 0x052, 0x050, 0x04e, 0x04d, 0x04b, 0x04a, 0x048, 0x046, 0x045,
        0x043, 0x042, 0x040, 0x03f, 0x03e, 0x03c, 0x03b, 0x039, 0x038, 0x037, 0x035, 0x034, 0x033, 0x031, 0x030, 0x02f,
        0x02e, 0x02d, 0x02b, 0x02a, 0x029, 0x028, 0x027, 0x026, 0x025, 0x024, 0x023, 0x022, 0x021, 0x020, 0x01f, 0x01e,
        0x01d, 0x01c, 0x01b, 0x01a, 0x019, 0x018, 0x017, 0x017, 0x016, 0x015, 0x014, 0x014, 0x013, 0x012, 0x011, 0x011,
        0x010, 0x00f, 0x00f, 0x00e, 0x00d, 0x00d, 0x00c, 0x00c, 0x00b, 0x00a, 0x00a, 0x009, 0x009, 0x008, 0x008, 0x007,
        0x007, 0x007, 0x006, 0x006, 0x005, 0x005, 0x005, 0x004, 0x004, 0x004, 0x003, 0x003, 0x003, 0x002, 0x002, 0x002,
        0x002, 0x001, 0x001, 0x001, 0x001, 0x001, 0x001, 0x001, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000
    };

    private static readonly ushort[] exprom = {
        0x000, 0x003, 0x006, 0x008, 0x00b, 0x00e, 0x011, 0x014, 0x016, 0x019, 0x01c, 0x01f, 0x022, 0x025, 0x028, 0x02a,
        0x02d, 0x030, 0x033, 0x036, 0x039, 0x03c, 0x03f, 0x042, 0x045, 0x048, 0x04b, 0x04e, 0x051, 0x054, 0x057, 0x05a,
        0x05d, 0x060, 0x063, 0x066, 0x069, 0x06c, 0x06f, 0x072, 0x075, 0x078, 0x07b, 0x07e, 0x082, 0x085, 0x088, 0x08b,
        0x08e, 0x091, 0x094, 0x098, 0x09b, 0x09e, 0x0a1, 0x0a4, 0x0a8, 0x0ab, 0x0ae, 0x0b1, 0x0b5, 0x0b8, 0x0bb, 0x0be,
        0x0c2, 0x0c5, 0x0c8, 0x0cc, 0x0cf, 0x0d2, 0x0d6, 0x0d9, 0x0dc, 0x0e0, 0x0e3, 0x0e7, 0x0ea, 0x0ed, 0x0f1, 0x0f4,
        0x0f8, 0x0fb, 0x0ff, 0x102, 0x106, 0x109, 0x10c, 0x110, 0x114, 0x117, 0x11b, 0x11e, 0x122, 0x125, 0x129, 0x12c,
        0x130, 0x134, 0x137, 0x13b, 0x13e, 0x142, 0x146, 0x149, 0x14d, 0x151, 0x154, 0x158, 0x15c, 0x160, 0x163, 0x167,
        0x16b, 0x16f, 0x172, 0x176, 0x17a, 0x17e, 0x181, 0x185, 0x189, 0x18d, 0x191, 0x195, 0x199, 0x19c, 0x1a0, 0x1a4,
        0x1a8, 0x1ac, 0x1b0, 0x1b4, 0x1b8, 0x1bc, 0x1c0, 0x1c4, 0x1c8, 0x1cc, 0x1d0, 0x1d4, 0x1d8, 0x1dc, 0x1e0, 0x1e4,
        0x1e8, 0x1ec, 0x1f0, 0x1f5, 0x1f9, 0x1fd, 0x201, 0x205, 0x209, 0x20e, 0x212, 0x216, 0x21a, 0x21e, 0x223, 0x227,
        0x22b, 0x230, 0x234, 0x238, 0x23c, 0x241, 0x245, 0x249, 0x24e, 0x252, 0x257, 0x25b, 0x25f, 0x264, 0x268, 0x26d,
        0x271, 0x276, 0x27a, 0x27f, 0x283, 0x288, 0x28c, 0x291, 0x295, 0x29a, 0x29e, 0x2a3, 0x2a8, 0x2ac, 0x2b1, 0x2b5,
        0x2ba, 0x2bf, 0x2c4, 0x2c8, 0x2cd, 0x2d2, 0x2d6, 0x2db, 0x2e0, 0x2e5, 0x2e9, 0x2ee, 0x2f3, 0x2f8, 0x2fd, 0x302,
        0x306, 0x30b, 0x310, 0x315, 0x31a, 0x31f, 0x324, 0x329, 0x32e, 0x333, 0x338, 0x33d, 0x342, 0x347, 0x34c, 0x351,
        0x356, 0x35b, 0x360, 0x365, 0x36a, 0x370, 0x375, 0x37a, 0x37f, 0x384, 0x38a, 0x38f, 0x394, 0x399, 0x39f, 0x3a4,
        0x3a9, 0x3ae, 0x3b4, 0x3b9, 0x3bf, 0x3c4, 0x3c9, 0x3cf, 0x3d4, 0x3da, 0x3df, 0x3e4, 0x3ea, 0x3ef, 0x3f5, 0x3fa
    };

    private static readonly byte[] mt = { 1, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 20, 24, 24, 30, 30 };
    private static readonly byte[] kslrom = { 0, 32, 40, 45, 48, 51, 53, 55, 56, 58, 59, 60, 61, 62, 63, 64 };
    private static readonly byte[] kslshift = { 8, 1, 2, 0 };
    private static readonly byte[,,] eg_incstep = {
        { { 0, 0, 0, 0, 0, 0, 0, 0 }, { 0, 0, 0, 0, 0, 0, 0, 0 }, { 0, 0, 0, 0, 0, 0, 0, 0 }, { 0, 0, 0, 0, 0, 0, 0, 0 } },
        { { 0, 1, 0, 1, 0, 1, 0, 1 }, { 0, 1, 0, 1, 1, 1, 0, 1 }, { 0, 1, 1, 1, 0, 1, 1, 1 }, { 0, 1, 1, 1, 1, 1, 1, 1 } },
        { { 1, 1, 1, 1, 1, 1, 1, 1 }, { 2, 2, 1, 1, 1, 1, 1, 1 }, { 2, 2, 1, 1, 2, 2, 1, 1 }, { 2, 2, 2, 2, 2, 2, 1, 1 } }
    };
    private static readonly byte[] eg_incdesc = { 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2 };
    private static readonly sbyte[] eg_incsh = { 0, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0, 0, -1, -2 };
    private static readonly sbyte[] ad_slot = {
        0, 1, 2, 3, 4, 5, -1, -1, 6, 7, 8, 9, 10, 11, -1, -1,
        12, 13, 14, 15, 16, 17, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1
    };
    private static readonly byte[] ch_slot = { 0, 1, 2, 6, 7, 8, 12, 13, 14, 18, 19, 20, 24, 25, 26, 30, 31, 32 };

    private sealed class Slot
    {
        public Channel channel = null!;
        public int outIdx, fbmodIdx; // cells in _c
        public int mod;              // Bit16s *mod
        public short prout, eg_rout, eg_out;
        public byte eg_inc, eg_gen, eg_rate, eg_ksl;
        public bool trem;            // Bit8u *trem: &chip->tremolo (true) or (Bit8u*)&zeromod (false, reads 0)
        public byte reg_vib, reg_type, reg_ksr, reg_mult, reg_ksl, reg_tl, reg_ar, reg_dr, reg_sl, reg_rr, reg_wf, key;
        public uint pg_phase;
    }

    private sealed class Channel
    {
        public readonly Slot[] slots = new Slot[2];
        public Channel? pair;
        public readonly int[] @out = new int[4]; // Bit16s *out[4]
        public byte chtype;
        public ushort f_num;
        public byte block, fb, con, alg, ksv;
        public ushort cha, chb;
    }

    private readonly short[] _c = new short[1 + 36 * 2];
    private readonly Channel[] channel = new Channel[18];
    private readonly Slot[] slot = new Slot[36];
    private ushort timer;
    private byte newm, nts, rhy, vibpos, vibshift, tremolo, tremolopos, tremoloshift;
    private uint noise;
    private readonly int[] mixbuff = new int[2];
    private readonly int rateratio;
    private int samplecnt;
    private readonly short[] oldsamples = new short[2];
    private readonly short[] samples = new short[2];

    /// <summary>OPL3_Reset.</summary>
    public Nuked174Opl3(uint samplerate)
    {
        for (int i = 0; i < 36; i++)
        {
            slot[i] = new Slot
            {
                outIdx = 1 + 2 * i, fbmodIdx = 2 + 2 * i, mod = ZERO,
                eg_rout = 0x1ff, eg_out = 0x1ff, eg_gen = eg_off, trem = false
            };
        }
        for (int i = 0; i < 18; i++) channel[i] = new Channel();
        for (int ch = 0; ch < 18; ch++)
        {
            var c = channel[ch];
            c.slots[0] = slot[ch_slot[ch]];
            c.slots[1] = slot[ch_slot[ch] + 3];
            slot[ch_slot[ch]].channel = c;
            slot[ch_slot[ch] + 3].channel = c;
            if ((ch % 9) < 3) c.pair = channel[ch + 3];
            else if ((ch % 9) < 6) c.pair = channel[ch - 3];
            c.@out[0] = c.@out[1] = c.@out[2] = c.@out[3] = ZERO;
            c.chtype = ch_2op;
            c.cha = 0xffff;
            c.chb = 0xffff;
            ChannelSetupAlg(c);
        }
        noise = 0x306600;
        rateratio = (int)((samplerate << RSM_FRAC) / 49716);
        tremoloshift = 4;
        vibshift = 1;
    }

    // ---- Envelope generator ----

    private static short EnvelopeCalcExp(uint level)
    {
        if (level > 0x1fff) level = 0x1fff;
        return (short)(((exprom[(level & 0xff) ^ 0xff] | 0x400) << 1) >> (int)(level >> 8));
    }

    private static short EnvelopeCalcSin(int wf, ushort phase, ushort envelope)
    {
        ushort o = 0;
        ushort neg = 0;
        phase &= 0x3ff;
        switch (wf)
        {
            case 0:
                if ((phase & 0x200) != 0) neg = 0xffff;
                o = (phase & 0x100) != 0 ? logsinrom[(phase & 0xff) ^ 0xff] : logsinrom[phase & 0xff];
                return (short)(EnvelopeCalcExp((uint)(o + (envelope << 3))) ^ neg);
            case 1:
                if ((phase & 0x200) != 0) o = 0x1000;
                else if ((phase & 0x100) != 0) o = logsinrom[(phase & 0xff) ^ 0xff];
                else o = logsinrom[phase & 0xff];
                return EnvelopeCalcExp((uint)(o + (envelope << 3)));
            case 2:
                o = (phase & 0x100) != 0 ? logsinrom[(phase & 0xff) ^ 0xff] : logsinrom[phase & 0xff];
                return EnvelopeCalcExp((uint)(o + (envelope << 3)));
            case 3:
                o = (phase & 0x100) != 0 ? (ushort)0x1000 : logsinrom[phase & 0xff];
                return EnvelopeCalcExp((uint)(o + (envelope << 3)));
            case 4:
                if ((phase & 0x300) == 0x100) neg = 0xffff;
                if ((phase & 0x200) != 0) o = 0x1000;
                else if ((phase & 0x80) != 0) o = logsinrom[((phase ^ 0xff) << 1) & 0xff];
                else o = logsinrom[(phase << 1) & 0xff];
                return (short)(EnvelopeCalcExp((uint)(o + (envelope << 3))) ^ neg);
            case 5:
                if ((phase & 0x200) != 0) o = 0x1000;
                else if ((phase & 0x80) != 0) o = logsinrom[((phase ^ 0xff) << 1) & 0xff];
                else o = logsinrom[(phase << 1) & 0xff];
                return EnvelopeCalcExp((uint)(o + (envelope << 3)));
            case 6:
                if ((phase & 0x200) != 0) neg = 0xffff;
                return (short)(EnvelopeCalcExp((uint)(envelope << 3)) ^ neg);
            default: // 7
                if ((phase & 0x200) != 0)
                {
                    neg = 0xffff;
                    phase = (ushort)((phase & 0x1ff) ^ 0x1ff);
                }
                o = (ushort)(phase << 3);
                return (short)(EnvelopeCalcExp((uint)(o + (envelope << 3))) ^ neg);
        }
    }

    private static byte EnvelopeCalcRate(Slot s, byte reg_rate)
    {
        if (reg_rate == 0) return 0;
        byte rate = (byte)((reg_rate << 2) + (s.reg_ksr != 0 ? s.channel.ksv : (s.channel.ksv >> 2)));
        if (rate > 0x3c) rate = 0x3c;
        return rate;
    }

    private static void EnvelopeUpdateKSL(Slot s)
    {
        short ksl = (short)((kslrom[s.channel.f_num >> 6] << 2) - ((0x08 - s.channel.block) << 5));
        if (ksl < 0) ksl = 0;
        s.eg_ksl = (byte)ksl;
    }

    private static void EnvelopeUpdateRate(Slot s)
    {
        switch (s.eg_gen)
        {
            case eg_off:
            case eg_attack: s.eg_rate = EnvelopeCalcRate(s, s.reg_ar); break;
            case eg_decay: s.eg_rate = EnvelopeCalcRate(s, s.reg_dr); break;
            case eg_sustain:
            case eg_release: s.eg_rate = EnvelopeCalcRate(s, s.reg_rr); break;
        }
    }

    private static void EnvelopeGen(Slot s)
    {
        switch (s.eg_gen)
        {
            case eg_off:
                s.eg_rout = 0x1ff;
                break;
            case eg_attack:
                if (s.eg_rout == 0)
                {
                    s.eg_gen = eg_decay;
                    EnvelopeUpdateRate(s);
                    return;
                }
                s.eg_rout = (short)(s.eg_rout + (((~s.eg_rout) * s.eg_inc) >> 3));
                if (s.eg_rout < 0) s.eg_rout = 0;
                break;
            case eg_decay:
                if (s.eg_rout >= s.reg_sl << 4)
                {
                    s.eg_gen = eg_sustain;
                    EnvelopeUpdateRate(s);
                    return;
                }
                s.eg_rout = (short)(s.eg_rout + s.eg_inc);
                break;
            case eg_sustain:
                if (s.reg_type == 0) EnvelopeGenRelease(s);
                break;
            case eg_release:
                EnvelopeGenRelease(s);
                break;
        }
    }

    private static void EnvelopeGenRelease(Slot s)
    {
        if (s.eg_rout >= 0x1ff)
        {
            s.eg_gen = eg_off;
            s.eg_rout = 0x1ff;
            EnvelopeUpdateRate(s);
            return;
        }
        s.eg_rout = (short)(s.eg_rout + s.eg_inc);
    }

    private void EnvelopeCalc(Slot s)
    {
        int rate_h = s.eg_rate >> 2;
        int rate_l = s.eg_rate & 3;
        byte inc = 0;
        if (eg_incsh[rate_h] > 0)
        {
            if ((timer & ((1 << eg_incsh[rate_h]) - 1)) == 0)
                inc = eg_incstep[eg_incdesc[rate_h], rate_l, (timer >> eg_incsh[rate_h]) & 0x07];
        }
        else
        {
            inc = (byte)(eg_incstep[eg_incdesc[rate_h], rate_l, timer & 0x07] << (-eg_incsh[rate_h]));
        }
        s.eg_inc = inc;
        s.eg_out = (short)(s.eg_rout + (s.reg_tl << 2) + (s.eg_ksl >> kslshift[s.reg_ksl]) + (s.trem ? tremolo : 0));
        EnvelopeGen(s);
    }

    private static void EnvelopeKeyOn(Slot s, byte type)
    {
        if (s.key == 0)
        {
            s.eg_gen = eg_attack;
            EnvelopeUpdateRate(s);
            if ((s.eg_rate >> 2) == 0x0f)
            {
                s.eg_gen = eg_decay;
                EnvelopeUpdateRate(s);
                s.eg_rout = 0;
            }
            s.pg_phase = 0;
        }
        s.key |= type;
    }

    private static void EnvelopeKeyOff(Slot s, byte type)
    {
        if (s.key != 0)
        {
            s.key &= (byte)~type;
            if (s.key == 0)
            {
                s.eg_gen = eg_release;
                EnvelopeUpdateRate(s);
            }
        }
    }

    // ---- Phase generator ----

    private void PhaseGenerate(Slot s)
    {
        ushort f_num = s.channel.f_num;
        if (s.reg_vib != 0)
        {
            sbyte range = (sbyte)((f_num >> 7) & 7);
            byte vp = vibpos;
            if ((vp & 3) == 0) range = 0;
            else if ((vp & 1) != 0) range >>= 1;
            range >>= vibshift;
            if ((vp & 4) != 0) range = (sbyte)-range;
            f_num = (ushort)(f_num + range);
        }
        uint basefreq = (uint)(f_num << s.channel.block) >> 1;
        s.pg_phase += (basefreq * mt[s.reg_mult]) >> 1;
    }

    private void NoiseGenerate()
    {
        if ((noise & 1) != 0) noise ^= 0x800302;
        noise >>= 1;
    }

    // ---- Slot ----

    private void SlotWrite20(Slot s, byte data)
    {
        s.trem = ((data >> 7) & 1) != 0;
        s.reg_vib = (byte)((data >> 6) & 1);
        s.reg_type = (byte)((data >> 5) & 1);
        s.reg_ksr = (byte)((data >> 4) & 1);
        s.reg_mult = (byte)(data & 0x0f);
        EnvelopeUpdateRate(s);
    }

    private static void SlotWrite40(Slot s, byte data)
    {
        s.reg_ksl = (byte)((data >> 6) & 3);
        s.reg_tl = (byte)(data & 0x3f);
        EnvelopeUpdateKSL(s);
    }

    private static void SlotWrite60(Slot s, byte data)
    {
        s.reg_ar = (byte)((data >> 4) & 0x0f);
        s.reg_dr = (byte)(data & 0x0f);
        EnvelopeUpdateRate(s);
    }

    private static void SlotWrite80(Slot s, byte data)
    {
        s.reg_sl = (byte)((data >> 4) & 0x0f);
        if (s.reg_sl == 0x0f) s.reg_sl = 0x1f;
        s.reg_rr = (byte)(data & 0x0f);
        EnvelopeUpdateRate(s);
    }

    private void SlotWriteE0(Slot s, byte data)
    {
        s.reg_wf = (byte)(data & 7);
        if (newm == 0) s.reg_wf &= 3;
    }

    private void SlotGeneratePhase(Slot s, ushort phase) =>
        _c[s.outIdx] = EnvelopeCalcSin(s.reg_wf, phase, (ushort)s.eg_out);

    private void SlotGenerate(Slot s) => SlotGeneratePhase(s, (ushort)((ushort)(s.pg_phase >> 9) + _c[s.mod]));

    private void SlotGenerateZM(Slot s) => SlotGeneratePhase(s, (ushort)(s.pg_phase >> 9));

    private void SlotCalcFB(Slot s)
    {
        if (s.channel.fb != 0)
            _c[s.fbmodIdx] = (short)((s.prout + _c[s.outIdx]) >> (0x09 - s.channel.fb));
        else
            _c[s.fbmodIdx] = 0;
        s.prout = _c[s.outIdx];
    }

    // ---- Channel ----

    private void ChannelUpdateRhythm(byte data)
    {
        rhy = (byte)(data & 0x3f);
        if ((rhy & 0x20) != 0)
        {
            Channel c6 = channel[6], c7 = channel[7], c8 = channel[8];
            c6.@out[0] = c6.slots[1].outIdx;
            c6.@out[1] = c6.slots[1].outIdx;
            c6.@out[2] = ZERO;
            c6.@out[3] = ZERO;
            c7.@out[0] = c7.slots[0].outIdx;
            c7.@out[1] = c7.slots[0].outIdx;
            c7.@out[2] = c7.slots[1].outIdx;
            c7.@out[3] = c7.slots[1].outIdx;
            c8.@out[0] = c8.slots[0].outIdx;
            c8.@out[1] = c8.slots[0].outIdx;
            c8.@out[2] = c8.slots[1].outIdx;
            c8.@out[3] = c8.slots[1].outIdx;
            for (int ch = 6; ch < 9; ch++) channel[ch].chtype = ch_drum;
            ChannelSetupAlg(c6);
            if ((rhy & 0x01) != 0) EnvelopeKeyOn(c7.slots[0], egk_drum); else EnvelopeKeyOff(c7.slots[0], egk_drum); // hh
            if ((rhy & 0x02) != 0) EnvelopeKeyOn(c8.slots[1], egk_drum); else EnvelopeKeyOff(c8.slots[1], egk_drum); // tc
            if ((rhy & 0x04) != 0) EnvelopeKeyOn(c8.slots[0], egk_drum); else EnvelopeKeyOff(c8.slots[0], egk_drum); // tom
            if ((rhy & 0x08) != 0) EnvelopeKeyOn(c7.slots[1], egk_drum); else EnvelopeKeyOff(c7.slots[1], egk_drum); // sd
            if ((rhy & 0x10) != 0) // bd
            {
                EnvelopeKeyOn(c6.slots[0], egk_drum);
                EnvelopeKeyOn(c6.slots[1], egk_drum);
            }
            else
            {
                EnvelopeKeyOff(c6.slots[0], egk_drum);
                EnvelopeKeyOff(c6.slots[1], egk_drum);
            }
        }
        else
        {
            for (int ch = 6; ch < 9; ch++)
            {
                channel[ch].chtype = ch_2op;
                ChannelSetupAlg(channel[ch]);
                EnvelopeKeyOff(channel[ch].slots[0], egk_drum);
                EnvelopeKeyOff(channel[ch].slots[1], egk_drum);
            }
        }
    }

    private void ChannelWriteA0(Channel c, byte data)
    {
        if (newm != 0 && c.chtype == ch_4op2) return;
        c.f_num = (ushort)((c.f_num & 0x300) | data);
        c.ksv = (byte)((c.block << 1) | ((c.f_num >> (0x09 - nts)) & 0x01));
        EnvelopeUpdateKSL(c.slots[0]);
        EnvelopeUpdateKSL(c.slots[1]);
        EnvelopeUpdateRate(c.slots[0]);
        EnvelopeUpdateRate(c.slots[1]);
        if (newm != 0 && c.chtype == ch_4op)
        {
            var p = c.pair!;
            p.f_num = c.f_num;
            p.ksv = c.ksv;
            EnvelopeUpdateKSL(p.slots[0]);
            EnvelopeUpdateKSL(p.slots[1]);
            EnvelopeUpdateRate(p.slots[0]);
            EnvelopeUpdateRate(p.slots[1]);
        }
    }

    private void ChannelWriteB0(Channel c, byte data)
    {
        if (newm != 0 && c.chtype == ch_4op2) return;
        c.f_num = (ushort)((c.f_num & 0xff) | ((data & 0x03) << 8));
        c.block = (byte)((data >> 2) & 0x07);
        c.ksv = (byte)((c.block << 1) | ((c.f_num >> (0x09 - nts)) & 0x01));
        EnvelopeUpdateKSL(c.slots[0]);
        EnvelopeUpdateKSL(c.slots[1]);
        EnvelopeUpdateRate(c.slots[0]);
        EnvelopeUpdateRate(c.slots[1]);
        if (newm != 0 && c.chtype == ch_4op)
        {
            var p = c.pair!;
            p.f_num = c.f_num;
            p.block = c.block;
            p.ksv = c.ksv;
            EnvelopeUpdateKSL(p.slots[0]);
            EnvelopeUpdateKSL(p.slots[1]);
            EnvelopeUpdateRate(p.slots[0]);
            EnvelopeUpdateRate(p.slots[1]);
        }
    }

    private static void ChannelSetupAlg(Channel c)
    {
        if (c.chtype == ch_drum)
        {
            switch (c.alg & 0x01)
            {
                case 0x00:
                    c.slots[0].mod = c.slots[0].fbmodIdx;
                    c.slots[1].mod = c.slots[0].outIdx;
                    break;
                case 0x01:
                    c.slots[0].mod = c.slots[0].fbmodIdx;
                    c.slots[1].mod = ZERO;
                    break;
            }
            return;
        }
        if ((c.alg & 0x08) != 0) return;
        if ((c.alg & 0x04) != 0)
        {
            var p = c.pair!;
            p.@out[0] = p.@out[1] = p.@out[2] = p.@out[3] = ZERO;
            switch (c.alg & 0x03)
            {
                case 0x00:
                    p.slots[0].mod = p.slots[0].fbmodIdx;
                    p.slots[1].mod = p.slots[0].outIdx;
                    c.slots[0].mod = p.slots[1].outIdx;
                    c.slots[1].mod = c.slots[0].outIdx;
                    c.@out[0] = c.slots[1].outIdx;
                    c.@out[1] = ZERO; c.@out[2] = ZERO; c.@out[3] = ZERO;
                    break;
                case 0x01:
                    p.slots[0].mod = p.slots[0].fbmodIdx;
                    p.slots[1].mod = p.slots[0].outIdx;
                    c.slots[0].mod = ZERO;
                    c.slots[1].mod = c.slots[0].outIdx;
                    c.@out[0] = p.slots[1].outIdx;
                    c.@out[1] = c.slots[1].outIdx;
                    c.@out[2] = ZERO; c.@out[3] = ZERO;
                    break;
                case 0x02:
                    p.slots[0].mod = p.slots[0].fbmodIdx;
                    p.slots[1].mod = ZERO;
                    c.slots[0].mod = p.slots[1].outIdx;
                    c.slots[1].mod = c.slots[0].outIdx;
                    c.@out[0] = p.slots[0].outIdx;
                    c.@out[1] = c.slots[1].outIdx;
                    c.@out[2] = ZERO; c.@out[3] = ZERO;
                    break;
                case 0x03:
                    p.slots[0].mod = p.slots[0].fbmodIdx;
                    p.slots[1].mod = ZERO;
                    c.slots[0].mod = p.slots[1].outIdx;
                    c.slots[1].mod = ZERO;
                    c.@out[0] = p.slots[0].outIdx;
                    c.@out[1] = c.slots[0].outIdx;
                    c.@out[2] = c.slots[1].outIdx;
                    c.@out[3] = ZERO;
                    break;
            }
        }
        else
        {
            switch (c.alg & 0x01)
            {
                case 0x00:
                    c.slots[0].mod = c.slots[0].fbmodIdx;
                    c.slots[1].mod = c.slots[0].outIdx;
                    c.@out[0] = c.slots[1].outIdx;
                    c.@out[1] = ZERO; c.@out[2] = ZERO; c.@out[3] = ZERO;
                    break;
                case 0x01:
                    c.slots[0].mod = c.slots[0].fbmodIdx;
                    c.slots[1].mod = ZERO;
                    c.@out[0] = c.slots[0].outIdx;
                    c.@out[1] = c.slots[1].outIdx;
                    c.@out[2] = ZERO; c.@out[3] = ZERO;
                    break;
            }
        }
    }

    private void ChannelWriteC0(Channel c, byte data)
    {
        c.fb = (byte)((data & 0x0e) >> 1);
        c.con = (byte)(data & 0x01);
        c.alg = c.con;
        if (newm != 0)
        {
            if (c.chtype == ch_4op)
            {
                c.pair!.alg = (byte)(0x04 | (c.con << 1) | c.pair.con);
                c.alg = 0x08;
                ChannelSetupAlg(c.pair);
            }
            else if (c.chtype == ch_4op2)
            {
                c.alg = (byte)(0x04 | (c.pair!.con << 1) | c.con);
                c.pair.alg = 0x08;
                ChannelSetupAlg(c);
            }
            else ChannelSetupAlg(c);
        }
        else ChannelSetupAlg(c);
        if (newm != 0)
        {
            c.cha = ((data >> 4) & 0x01) != 0 ? (ushort)0xffff : (ushort)0;
            c.chb = ((data >> 5) & 0x01) != 0 ? (ushort)0xffff : (ushort)0;
        }
        else
        {
            c.cha = c.chb = 0xffff;
        }
    }

    private void ChannelKeyOn(Channel c)
    {
        if (newm != 0)
        {
            if (c.chtype == ch_4op)
            {
                EnvelopeKeyOn(c.slots[0], egk_norm);
                EnvelopeKeyOn(c.slots[1], egk_norm);
                EnvelopeKeyOn(c.pair!.slots[0], egk_norm);
                EnvelopeKeyOn(c.pair.slots[1], egk_norm);
            }
            else if (c.chtype == ch_2op || c.chtype == ch_drum)
            {
                EnvelopeKeyOn(c.slots[0], egk_norm);
                EnvelopeKeyOn(c.slots[1], egk_norm);
            }
        }
        else
        {
            EnvelopeKeyOn(c.slots[0], egk_norm);
            EnvelopeKeyOn(c.slots[1], egk_norm);
        }
    }

    private void ChannelKeyOff(Channel c)
    {
        if (newm != 0)
        {
            if (c.chtype == ch_4op)
            {
                EnvelopeKeyOff(c.slots[0], egk_norm);
                EnvelopeKeyOff(c.slots[1], egk_norm);
                EnvelopeKeyOff(c.pair!.slots[0], egk_norm);
                EnvelopeKeyOff(c.pair.slots[1], egk_norm);
            }
            else if (c.chtype == ch_2op || c.chtype == ch_drum)
            {
                EnvelopeKeyOff(c.slots[0], egk_norm);
                EnvelopeKeyOff(c.slots[1], egk_norm);
            }
        }
        else
        {
            EnvelopeKeyOff(c.slots[0], egk_norm);
            EnvelopeKeyOff(c.slots[1], egk_norm);
        }
    }

    private void ChannelSet4Op(byte data)
    {
        for (int bit = 0; bit < 6; bit++)
        {
            int chnum = bit;
            if (bit >= 3) chnum += 9 - 3;
            if (((data >> bit) & 0x01) != 0)
            {
                channel[chnum].chtype = ch_4op;
                channel[chnum + 3].chtype = ch_4op2;
            }
            else
            {
                channel[chnum].chtype = ch_2op;
                channel[chnum + 3].chtype = ch_2op;
            }
        }
    }

    private static short ClipSample(int sample)
    {
        if (sample > 32767) sample = 32767;
        else if (sample < -32768) sample = -32768;
        return (short)sample;
    }

    private void GenerateRhythm1()
    {
        Channel c6 = channel[6], c7 = channel[7], c8 = channel[8];
        SlotGenerate(c6.slots[0]);
        ushort phase14 = (ushort)((c7.slots[0].pg_phase >> 9) & 0x3ff);
        ushort phase17 = (ushort)((c8.slots[1].pg_phase >> 9) & 0x3ff);
        // hh tc phase bit
        ushort phasebit = (ushort)(((phase14 & 0x08) | (((phase14 >> 5) ^ phase14) & 0x04)
                         | (((phase17 >> 2) ^ phase17) & 0x08)) != 0 ? 1 : 0);
        // hh
        ushort phase = (ushort)((phasebit << 9) | (0x34 << ((phasebit ^ (int)(noise & 0x01)) << 1)));
        SlotGeneratePhase(c7.slots[0], phase);
        // tt
        SlotGenerateZM(c8.slots[0]);
    }

    private void GenerateRhythm2()
    {
        Channel c6 = channel[6], c7 = channel[7], c8 = channel[8];
        SlotGenerate(c6.slots[1]);
        ushort phase14 = (ushort)((c7.slots[0].pg_phase >> 9) & 0x3ff);
        ushort phase17 = (ushort)((c8.slots[1].pg_phase >> 9) & 0x3ff);
        ushort phasebit = (ushort)(((phase14 & 0x08) | (((phase14 >> 5) ^ phase14) & 0x04)
                         | (((phase17 >> 2) ^ phase17) & 0x08)) != 0 ? 1 : 0);
        // sd
        ushort phase = (ushort)((0x100 << ((phase14 >> 8) & 0x01)) ^ ((int)(noise & 0x01) << 8));
        SlotGeneratePhase(c7.slots[1], phase);
        // tc
        phase = (ushort)(0x100 | (phasebit << 9));
        SlotGeneratePhase(c8.slots[1], phase);
    }

    private void SlotStep(int i, bool gen)
    {
        var s = slot[i];
        SlotCalcFB(s);
        PhaseGenerate(s);
        EnvelopeCalc(s);
        if (gen) SlotGenerate(s);
    }

    private int Mix(bool right)
    {
        int m = 0;
        for (int ii = 0; ii < 18; ii++)
        {
            var c = channel[ii];
            short accm = 0;
            for (int jj = 0; jj < 4; jj++) accm = (short)(accm + _c[c.@out[jj]]);
            m += (short)(accm & (right ? c.chb : c.cha));
        }
        return m;
    }

    /// <summary>OPL3_Generate: one native-rate sample; buf[0] = left, buf[1] = right (previous sample's right mix).</summary>
    public void Generate(short[] buf)
    {
        buf[1] = ClipSample(mixbuff[1]);

        for (int ii = 0; ii < 12; ii++) SlotStep(ii, true);
        for (int ii = 12; ii < 15; ii++) SlotStep(ii, false);
        if ((rhy & 0x20) != 0) GenerateRhythm1();
        else { SlotGenerate(slot[12]); SlotGenerate(slot[13]); SlotGenerate(slot[14]); }

        mixbuff[0] = Mix(false);

        for (int ii = 15; ii < 18; ii++) SlotStep(ii, false);
        if ((rhy & 0x20) != 0) GenerateRhythm2();
        else { SlotGenerate(slot[15]); SlotGenerate(slot[16]); SlotGenerate(slot[17]); }

        buf[0] = ClipSample(mixbuff[0]);

        for (int ii = 18; ii < 33; ii++) SlotStep(ii, true);

        mixbuff[1] = Mix(true);

        for (int ii = 33; ii < 36; ii++) SlotStep(ii, true);

        NoiseGenerate();

        if ((timer & 0x3f) == 0x3f) tremolopos = (byte)((tremolopos + 1) % 210);
        if (tremolopos < 105) tremolo = (byte)(tremolopos >> tremoloshift);
        else tremolo = (byte)((210 - tremolopos) >> tremoloshift);

        if ((timer & 0x3ff) == 0x3ff) vibpos = (byte)((vibpos + 1) & 7);

        timer++;
        // (buffered-write queue drain omitted: OPL3_WriteRegBuffered is never used, so it never fires)
    }

    /// <summary>OPL3_GenerateResampled (unused by the engine; kept for completeness).</summary>
    public void GenerateResampled(short[] buf)
    {
        while (samplecnt >= rateratio)
        {
            oldsamples[0] = samples[0];
            oldsamples[1] = samples[1];
            Generate(samples);
            samplecnt -= rateratio;
        }
        buf[0] = (short)((oldsamples[0] * (rateratio - samplecnt) + samples[0] * samplecnt) / rateratio);
        buf[1] = (short)((oldsamples[1] * (rateratio - samplecnt) + samples[1] * samplecnt) / rateratio);
        samplecnt += 1 << RSM_FRAC;
    }

    /// <summary>OPL3_WriteReg.</summary>
    public void WriteReg(ushort reg, byte v)
    {
        int high = (reg >> 8) & 0x01;
        int regm = reg & 0xff;
        switch (regm & 0xf0)
        {
            case 0x00:
                if (high != 0)
                {
                    switch (regm & 0x0f)
                    {
                        case 0x04: ChannelSet4Op(v); break;
                        case 0x05: newm = (byte)(v & 0x01); break;
                    }
                }
                else if ((regm & 0x0f) == 0x08) nts = (byte)((v >> 6) & 0x01);
                break;
            case 0x20:
            case 0x30:
                if (ad_slot[regm & 0x1f] >= 0) SlotWrite20(slot[18 * high + ad_slot[regm & 0x1f]], v);
                break;
            case 0x40:
            case 0x50:
                if (ad_slot[regm & 0x1f] >= 0) SlotWrite40(slot[18 * high + ad_slot[regm & 0x1f]], v);
                break;
            case 0x60:
            case 0x70:
                if (ad_slot[regm & 0x1f] >= 0) SlotWrite60(slot[18 * high + ad_slot[regm & 0x1f]], v);
                break;
            case 0x80:
            case 0x90:
                if (ad_slot[regm & 0x1f] >= 0) SlotWrite80(slot[18 * high + ad_slot[regm & 0x1f]], v);
                break;
            case 0xe0:
            case 0xf0:
                if (ad_slot[regm & 0x1f] >= 0) SlotWriteE0(slot[18 * high + ad_slot[regm & 0x1f]], v);
                break;
            case 0xa0:
                if ((regm & 0x0f) < 9) ChannelWriteA0(channel[9 * high + (regm & 0x0f)], v);
                break;
            case 0xb0:
                if (regm == 0xbd && high == 0)
                {
                    tremoloshift = (byte)((((v >> 7) ^ 1) << 1) + 2);
                    vibshift = (byte)(((v >> 6) & 0x01) ^ 1);
                    ChannelUpdateRhythm(v);
                }
                else if ((regm & 0x0f) < 9)
                {
                    var c = channel[9 * high + (regm & 0x0f)];
                    ChannelWriteB0(c, v);
                    if ((v & 0x20) != 0) ChannelKeyOn(c); else ChannelKeyOff(c);
                }
                break;
            case 0xc0:
                if ((regm & 0x0f) < 9) ChannelWriteC0(channel[9 * high + (regm & 0x0f)], v);
                break;
        }
    }
}
