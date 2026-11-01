using System;

namespace NesEmulator.Sega;

// =====================================================================================================================
//  PicoDrive 1.85 YM2612 (FM) engine, ported to C#.
//
//  Source: PicoDrive v1.85 by notaz (tag v1.85, https://github.com/notaz/picodrive/tree/v1.85), files
//  pico/sound/ym2612.c + ym2612.h (the C path, not the optional ARM ym2612.S), with the DAC / register-port handling of
//  pico/memory.c (ym2612_write_local) and the frame rendering of pico/sound/sound.c (PsndRerate, PsndRender, PsndDoDAC).
//  https://raw.githubusercontent.com/notaz/picodrive/v1.85/pico/sound/ym2612.c
//  Copyright (C) 2001-2003 Jarek Burczynski; (C) 1998 Tatsuyuki Satoh, MAME development (fm.c, "MAME license");
//  PicoDrive (c) Copyright Dave, 2004; (C) notaz, 2006-2009.
//  License: PicoDrive's COPYING (MAME-style, non-commercial: redistributions may not be sold or used in a commercial
//  product; modified redistributions must include complete source). This is a documented exception to the repository's
//  license policy - see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
//
//  A quick port (quick and dirty first). The bugs and shortcuts of the original are kept on purpose; they are the sound:
//   - SSG-EG removed (writes to $90-$9F are ignored), CSM mode not emulated, no per-channel output clipping
//   - DAC level is a guess: (data - 0x80) << 6 ("notaz: 8 seems to be too much"); a $2A write lands whatever the A1 latch
//   - the C path's ch6 "disabled" flag is (dacen << 2) & 0x35 = 0, so FM channel 6 keeps sounding under the DAC
//   - fixed native rate 44,100 Hz (clock master/7, prescaler 144), whole-frame blocks: register writes land on block
//     boundaries, the LFO PM increment is computed once per block and per channel
//   - EG / LFO counters are advanced per rendered channel and the last rendered channel's counters are kept
//   - a channel with all four slots off stops being rendered (slot_mask) until a key-on
//  Left out: timers/status/busy (the stock chip answers the game), the GP2X 940 / ARM paths, save-state packing.
// =====================================================================================================================

/// <summary>The PicoDrive 1.85 YM2612: feed it the port writes, pull 44.1 kHz stereo blocks (DAC not mixed: see <see cref="DacLevel"/>).</summary>
public sealed class PicoYm2612
{
    public const int Rate = 44100;

    const int FREQ_SH = 16, EG_SH = 16, LFO_SH = 25;
    const int ENV_BITS = 10, ENV_LEN = 1 << ENV_BITS;
    const double ENV_STEP = 128.0 / ENV_LEN;
    const int MAX_ATT_INDEX = ENV_LEN - 1, MIN_ATT_INDEX = 0;
    const int EG_ATT = 4, EG_DEC = 3, EG_SUS = 2, EG_REL = 1, EG_OFF = 0;
    const int SIN_LEN = 1 << 10;
    const int TL_RES_LEN = 256;
    const uint EG_TIMER_OVERFLOW = 3u * (1u << EG_SH);
    const int TL_TAB_LEN = 13 * TL_RES_LEN * 256 / 8;
    const uint ENV_QUIET = 2 * 13 * TL_RES_LEN / 8;
    const int SLOT1 = 0, SLOT2 = 2, SLOT3 = 1, SLOT4 = 3;

    // ---- static tables (init_tables)
    static readonly ushort[] ym_tl_tab = new ushort[TL_TAB_LEN];
    static readonly ushort[] ym_tl_tab2 = new ushort[13 * TL_RES_LEN];
    static readonly ushort[] ym_sin_tab = new ushort[256];
    static readonly int[] lfo_pm_table = new int[128 * 8 * 32];

    static uint SC(int db) => (uint)(db * (4.0 / ENV_STEP));
    static readonly uint[] sl_table = { SC(0), SC(1), SC(2), SC(3), SC(4), SC(5), SC(6), SC(7), SC(8), SC(9), SC(10), SC(11), SC(12), SC(13), SC(14), SC(31) };

    static uint PACK(uint a0, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6, uint a7) => (a7 << 21) | (a6 << 18) | (a5 << 15) | (a4 << 12) | (a3 << 9) | (a2 << 6) | (a1 << 3) | a0;
    static readonly uint[] eg_inc_pack =
    {
        PACK(0,1,0,1,0,1,0,1), PACK(0,1,0,1,1,1,0,1), PACK(0,1,1,1,0,1,1,1), PACK(0,1,1,1,1,1,1,1),
        PACK(1,1,1,1,1,1,1,1), PACK(1,1,1,2,1,1,1,2), PACK(1,2,1,2,1,2,1,2), PACK(1,2,2,2,1,2,2,2),
        PACK(2,2,2,2,2,2,2,2), PACK(2,2,2,3,2,2,2,3), PACK(2,3,2,3,2,3,2,3), PACK(2,3,3,3,2,3,3,3),
        PACK(3,3,3,3,3,3,3,3), PACK(3,3,3,4,3,3,3,4), PACK(3,4,3,4,3,4,3,4), PACK(3,4,4,4,3,4,4,4),
        PACK(4,4,4,4,4,4,4,4), PACK(5,5,5,5,5,5,5,5), PACK(0,0,0,0,0,0,0,0),
    };

    static readonly byte[] eg_rate_select = BuildRateSelect();
    static readonly byte[] eg_rate_shift = BuildRateShift();

    static byte[] BuildRateSelect()
    {
        var t = new byte[128];
        for (int i = 0; i < 32; i++) t[i] = 18;
        for (int i = 0; i < 48; i++) t[32 + i] = (byte)(i & 3);
        for (int i = 0; i < 4; i++) { t[80 + i] = (byte)(4 + i); t[84 + i] = (byte)(8 + i); t[88 + i] = (byte)(12 + i); t[92 + i] = 16; }
        for (int i = 96; i < 128; i++) t[i] = 16;
        return t;
    }

    static byte[] BuildRateShift()
    {
        var t = new byte[128];
        for (int i = 0; i < 48; i++) t[32 + i] = (byte)(11 - i / 4);
        return t;   // the rest are 0
    }

    static readonly byte[] dt_tab =
    {
        0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,
        0,0,0,0,1,1,1,1,1,1,1,1,2,2,2,2, 2,3,3,3,4,4,4,5,5,6,6,7,8,8,8,8,
        1,1,1,1,2,2,2,2,2,3,3,3,4,4,4,5, 5,6,6,7,8,8,9,10,11,12,13,14,16,16,16,16,
        2,2,2,2,2,3,3,3,4,4,4,5,5,6,6,7, 8,8,9,10,11,12,13,14,16,17,19,20,22,22,22,22,
    };

    static readonly byte[] opn_fktable = { 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 3, 3, 3, 3, 3, 3 };
    static readonly uint[] lfo_samples_per_step = { 108, 77, 71, 67, 62, 44, 8, 5 };
    static readonly byte[] lfo_ams_depth_shift = { 8, 3, 1, 0 };

    static readonly byte[,] lfo_pm_output =
    {
        {0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,1,1,1,1},
        {0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,1,1,1,1},{0,0,1,1,2,2,2,3},
        {0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,1},{0,0,0,0,1,1,1,1},{0,0,1,1,2,2,2,3},{0,0,2,3,4,4,5,6},
        {0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,0,0,1,1},{0,0,0,0,1,1,1,1},{0,0,0,1,1,1,1,2},{0,0,1,1,2,2,2,3},{0,0,2,3,4,4,5,6},{0,0,4,6,8,8,0xa,0xc},
        {0,0,0,0,0,0,0,0},{0,0,0,0,1,1,1,1},{0,0,0,1,1,1,2,2},{0,0,1,1,2,2,3,3},{0,0,1,2,2,2,3,4},{0,0,2,3,4,4,5,6},{0,0,4,6,8,8,0xa,0xc},{0,0,8,0xc,0x10,0x10,0x14,0x18},
        {0,0,0,0,0,0,0,0},{0,0,0,0,2,2,2,2},{0,0,0,2,2,2,4,4},{0,0,2,2,4,4,6,6},{0,0,2,4,4,4,6,8},{0,0,4,6,8,8,0xa,0xc},{0,0,8,0xc,0x10,0x10,0x14,0x18},{0,0,0x10,0x18,0x20,0x20,0x28,0x30},
        {0,0,0,0,0,0,0,0},{0,0,0,0,4,4,4,4},{0,0,0,4,4,4,8,8},{0,0,4,4,8,8,0xc,0xc},{0,0,4,8,8,8,0xc,0x10},{0,0,8,0xc,0x10,0x10,0x14,0x18},{0,0,0x10,0x18,0x20,0x20,0x28,0x30},{0,0,0x20,0x30,0x40,0x40,0x50,0x60},
    };

    static PicoYm2612() => InitTables();

    static void InitTables()
    {
        for (int i = 0; i < 256; i++)
        {
            double m = Math.Sin(((i * 2) + 1) * Math.PI / SIN_LEN);
            double o = m > 0.0 ? 8 * Math.Log(1.0 / m) / Math.Log(2) : 8 * Math.Log(-1.0 / m) / Math.Log(2);
            o = o / (ENV_STEP / 4);
            int n = (int)(2.0 * o);
            n = (n & 1) != 0 ? (n >> 1) + 1 : n >> 1;
            ym_sin_tab[i] = (ushort)n;
        }
        for (int x = 0; x < TL_RES_LEN; x++)
        {
            double m = (1 << 16) / Math.Pow(2, (x + 1) * (ENV_STEP / 4.0) / 8.0);
            m = Math.Floor(m);
            int n = (int)m;
            n >>= 4;
            n = (n & 1) != 0 ? (n >> 1) + 1 : n >> 1;
            n <<= 2;
            ym_tl_tab2[x] = (ushort)n;
            for (int i = 1; i < 13; i++) ym_tl_tab2[x + i * TL_RES_LEN] = (ushort)(n >> i);
        }
        for (int x = 0; x < 256; x++)
        {
            int sin = ym_sin_tab[x];
            for (int y = 0; y < 2 * 13 * TL_RES_LEN / 8; y += 2)
            {
                int p = (y << 2) + sin;
                ym_tl_tab[(y << 7) | x] = p >= 13 * TL_RES_LEN ? (ushort)0 : ym_tl_tab2[p];
            }
        }
        for (int i = 0; i < 8; i++)
            for (int fnum = 0; fnum < 128; fnum++)
                for (int step = 0; step < 8; step++)
                {
                    int value = 0;
                    for (int bit = 0; bit < 7; bit++)
                        if ((fnum & (1 << bit)) != 0) value += lfo_pm_output[bit * 8 + i, step];
                    value &= 0xff;   // UINT8 value
                    lfo_pm_table[(fnum * 32 * 8) + (i * 32) + step + 0] = value;
                    lfo_pm_table[(fnum * 32 * 8) + (i * 32) + (step ^ 7) + 8] = value;
                    lfo_pm_table[(fnum * 32 * 8) + (i * 32) + step + 16] = -value;
                    lfo_pm_table[(fnum * 32 * 8) + (i * 32) + (step ^ 7) + 24] = -value;
                }
    }

    // ---- state (YM2612 / FM_OPN / FM_ST)
    sealed class Slot
    {
        public int DT;          // row in dtTab (0..7)
        public byte ar, d1r, d2r, rr;
        public uint mul;
        public uint phase, Incr;
        public byte KSR, ksr, key, state;
        public ushort tl;
        public short volume;
        public uint sl;
        public uint eg_pack_ar, eg_pack_d1r, eg_pack_d2r, eg_pack_rr;
    }

    sealed class Chan
    {
        public readonly Slot[] SLOT = { new(), new(), new(), new() };
        public byte ALGO, FB;
        public int op1_out, mem_value, pms;
        public byte ams, kcode, fn_h;
        public uint fc, block_fnum;
        public byte AMmasks;
    }

    readonly Chan[] CH = { new(), new(), new(), new(), new(), new() };
    readonly int clock;
    readonly double freqbase;
    readonly int[,] dtTab = new int[8, 32];
    readonly uint[] fn_table = new uint[4096];
    readonly uint[] lfo_freq = new uint[8];
    readonly uint eg_timer_add;
    byte address, mode;
    int addr_A1;
    uint pan, eg_cnt, eg_timer, lfo_cnt, lfo_inc;
    readonly uint[] sl3_fc = new uint[3];
    byte sl3_fn_h;
    readonly byte[] sl3_kcode = new byte[3];
    readonly uint[] sl3_block_fnum = new uint[3];
    int dacen, dacout;
    uint slot_mask;
    int g_lfo_ampm;

    /// <summary>The DAC's level as PicoDrive writes it into the output: <c>(data - 0x80) &lt;&lt; 6</c> while $2B bit 7 is set, else 0.</summary>
    public int DacLevel => dacen != 0 ? dacout : 0;

    /// <param name="clock">The YM2612 clock (PicoDrive: OSC_NTSC/7 or OSC_PAL/7).</param>
    public PicoYm2612(int clock)
    {
        this.clock = clock;
        // OPNSetPres(6*24)
        freqbase = (double)clock / Rate / 144;
        eg_timer_add = (uint)((1 << EG_SH) * freqbase);
        for (int d = 0; d <= 3; d++)
            for (int i = 0; i <= 31; i++)
            {
                double rate = dt_tab[d * 32 + i] * (double)SIN_LEN * freqbase * (1 << FREQ_SH) / (1 << 20);
                dtTab[d, i] = (int)rate;
                dtTab[d + 4, i] = -dtTab[d, i];
            }
        for (int i = 0; i < 4096; i++) fn_table[i] = (uint)(i * 32 * freqbase * (1 << (FREQ_SH - 10)));
        for (int i = 0; i < 8; i++) lfo_freq[i] = (uint)((1.0 / lfo_samples_per_step[i]) * (1 << LFO_SH) * freqbase);
        ResetChip();
    }

    // ---- register helpers
    void SetTimers(int v) { mode = (byte)v; }

    void KeyOn(int c, int s)
    {
        var SLOT = CH[c].SLOT[s];
        if (SLOT.key == 0)
        {
            SLOT.key = 1; SLOT.phase = 0; SLOT.state = EG_ATT;
            slot_mask |= (1u << s) << (c * 4);
        }
    }

    void KeyOff(int c, int s)
    {
        var SLOT = CH[c].SLOT[s];
        if (SLOT.key != 0)
        {
            SLOT.key = 0;
            if (SLOT.state > EG_REL) SLOT.state = EG_REL;
        }
    }

    static uint RatePack(int idx, out int sh)
    {
        sh = eg_rate_shift[idx];
        return eg_inc_pack[eg_rate_select[idx]] | ((uint)sh << 24);
    }

    void SetDetMul(Chan ch, Slot slot, int v)
    {
        slot.mul = (uint)((v & 0x0f) != 0 ? (v & 0x0f) * 2 : 1);
        slot.DT = (v >> 4) & 7;
        ch.SLOT[SLOT1].Incr = uint.MaxValue;
    }

    static void SetTl(Slot slot, int v) => slot.tl = (ushort)((v & 0x7f) << (ENV_BITS - 7));

    static void SetArKsr(Chan ch, Slot slot, int v)
    {
        byte old = slot.KSR;
        slot.ar = (byte)((v & 0x1f) != 0 ? 32 + ((v & 0x1f) << 1) : 0);
        slot.KSR = (byte)(3 - (v >> 6));
        if (slot.KSR != old) ch.SLOT[SLOT1].Incr = uint.MaxValue;
        else
        {
            int sh, sel;
            if (slot.ar + slot.ksr < 32 + 62) { sh = eg_rate_shift[slot.ar + slot.ksr]; sel = eg_rate_select[slot.ar + slot.ksr]; }
            else { sh = 0; sel = 17; }
            slot.eg_pack_ar = eg_inc_pack[sel] | ((uint)sh << 24);
        }
    }

    static void SetDr(Slot slot, int v) { slot.d1r = (byte)((v & 0x1f) != 0 ? 32 + ((v & 0x1f) << 1) : 0); slot.eg_pack_d1r = RatePack(slot.d1r + slot.ksr, out _); }
    static void SetSr(Slot slot, int v) { slot.d2r = (byte)((v & 0x1f) != 0 ? 32 + ((v & 0x1f) << 1) : 0); slot.eg_pack_d2r = RatePack(slot.d2r + slot.ksr, out _); }
    static void SetSlRr(Slot slot, int v) { slot.sl = sl_table[v >> 4]; slot.rr = (byte)(34 + ((v & 0x0f) << 2)); slot.eg_pack_rr = RatePack(slot.rr + slot.ksr, out _); }

    static int OpCalc(uint phase, uint env, int pm)
    {
        int sin = (int)(phase >> 16) + (pm >> 1);
        int neg = sin & 0x200;
        if ((sin & 0x100) != 0) sin ^= 0xff;
        sin &= 0xff;
        env &= ~1u;
        int ret = ym_tl_tab[sin | (int)(env << 7)];
        return neg != 0 ? -ret : ret;
    }

    static int OpCalc1(uint phase, uint env, int pm)
    {
        int sin = (int)((phase + (uint)pm) >> 16);
        int neg = sin & 0x200;
        if ((sin & 0x100) != 0) sin ^= 0xff;
        sin &= 0xff;
        env &= ~1u;
        int ret = ym_tl_tab[sin | (int)(env << 7)];
        return neg != 0 ? -ret : ret;
    }

    static int AdvanceLfo(int lfo_ampm, uint lfo_cnt_old, uint lfo_cnt)
    {
        byte prev_pos = (byte)((lfo_cnt_old >> LFO_SH) & 127);
        byte pos = (byte)((lfo_cnt >> LFO_SH) & 127);
        if (prev_pos != pos)
        {
            lfo_ampm &= 0xff;
            if (pos < 64) lfo_ampm |= ((pos & 63) * 2) << 8;
            else lfo_ampm |= (126 - (pos & 63) * 2) << 8;
        }
        else return lfo_ampm;
        prev_pos >>= 2; pos >>= 2;
        if (prev_pos != pos) { lfo_ampm &= ~0xff; lfo_ampm |= pos; }
        return lfo_ampm;
    }

    static uint EgIncVal(uint pack, uint eg_cnt, int shift) => (1u << (int)((pack >> (int)(((eg_cnt >> shift) & 7) * 3)) & 7)) >> 1;

    static uint UpdateEgPhase(Slot SLOT, uint eg_cnt)
    {
        int volume = SLOT.volume;
        switch (SLOT.state)
        {
            case EG_ATT:
            {
                uint pack = SLOT.eg_pack_ar; int shift = (int)(pack >> 24);
                if ((eg_cnt & ((1u << shift) - 1)) == 0)
                {
                    volume += (int)(~volume * (int)EgIncVal(pack, eg_cnt, shift)) >> 4;
                    if (volume <= MIN_ATT_INDEX) { volume = MIN_ATT_INDEX; SLOT.state = EG_DEC; }
                }
                break;
            }
            case EG_DEC:
            {
                uint pack = SLOT.eg_pack_d1r; int shift = (int)(pack >> 24);
                if ((eg_cnt & ((1u << shift) - 1)) == 0)
                {
                    volume += (int)EgIncVal(pack, eg_cnt, shift);
                    if (volume >= (int)SLOT.sl) SLOT.state = EG_SUS;
                }
                break;
            }
            case EG_SUS:
            {
                uint pack = SLOT.eg_pack_d2r; int shift = (int)(pack >> 24);
                if ((eg_cnt & ((1u << shift) - 1)) == 0)
                {
                    volume += (int)EgIncVal(pack, eg_cnt, shift);
                    if (volume >= MAX_ATT_INDEX) volume = MAX_ATT_INDEX;
                }
                break;
            }
            case EG_REL:
            {
                uint pack = SLOT.eg_pack_rr; int shift = (int)(pack >> 24);
                if ((eg_cnt & ((1u << shift) - 1)) == 0)
                {
                    volume += (int)EgIncVal(pack, eg_cnt, shift);
                    if (volume >= MAX_ATT_INDEX) { volume = MAX_ATT_INDEX; SLOT.state = EG_OFF; }
                }
                break;
            }
        }
        SLOT.volume = (short)volume;
        return (uint)(SLOT.tl + volume);
    }

    // ---- chan_rend_context (kept between calls, like the C file's static crct)
    ushort cv1, cv2, cv3, cv4;
    uint cphase1, cphase2, cphase3, cphase4, cincr1, cincr2, cincr3, cincr4;
    uint clfo_cnt, clfo_inc;
    int cmem;
    uint ceg_cnt, ceg_timer, ceg_timer_add;
    Chan cCH = null!;
    uint cpack, calgo;
    int cop1_out;

    void ChanRenderLoop(int[] buffer, int length)
    {
        for (int sc = 0; sc < length; sc++)
        {
            int smp = 0;
            uint eg_out, eg_out2, eg_out4;

            if ((cpack & 8) != 0)
            {
                cpack = (cpack & 0xffff) | ((uint)AdvanceLfo((int)(cpack >> 16), clfo_cnt, clfo_cnt + clfo_inc) << 16);
                clfo_cnt += clfo_inc;
            }

            ceg_timer += ceg_timer_add;
            while (ceg_timer >= EG_TIMER_OVERFLOW)
            {
                ceg_timer -= EG_TIMER_OVERFLOW;
                ceg_cnt++;
                if (cCH.SLOT[SLOT1].state != EG_OFF) cv1 = (ushort)UpdateEgPhase(cCH.SLOT[SLOT1], ceg_cnt);
                if (cCH.SLOT[SLOT2].state != EG_OFF) cv2 = (ushort)UpdateEgPhase(cCH.SLOT[SLOT2], ceg_cnt);
                if (cCH.SLOT[SLOT3].state != EG_OFF) cv3 = (ushort)UpdateEgPhase(cCH.SLOT[SLOT3], ceg_cnt);
                if (cCH.SLOT[SLOT4].state != EG_OFF) cv4 = (ushort)UpdateEgPhase(cCH.SLOT[SLOT4], ceg_cnt);
            }

            if ((cpack & 4) != 0) continue;   // output disabled (phases not advanced either)

            eg_out = cv1;
            if ((cpack & 8) != 0 && (cpack & (1u << (SLOT1 + 8))) != 0) eg_out += cpack >> (int)(((cpack & 0xc0) >> 6) + 24);

            if (eg_out < ENV_QUIET)
            {
                int o = 0;
                if ((cpack & 0xf000) != 0) o = ((cop1_out >> 16) + ((cop1_out << 16) >> 16)) << (int)((cpack & 0xf000) >> 12);
                cop1_out <<= 16;
                cop1_out |= (ushort)OpCalc1(cphase1, eg_out, o);
            }
            else cop1_out <<= 16;

            eg_out = cv3; eg_out2 = cv2; eg_out4 = cv4;
            if ((cpack & 8) != 0)
            {
                uint add = cpack >> (int)(((cpack & 0xc0) >> 6) + 24);
                if ((cpack & (1u << (SLOT3 + 8))) != 0) eg_out += add;
                if ((cpack & (1u << (SLOT2 + 8))) != 0) eg_out2 += add;
                if ((cpack & (1u << (SLOT4 + 8))) != 0) eg_out4 += add;
            }

            switch (cCH.ALGO)
            {
                case 0:
                {
                    int m2 = cmem, c1 = cop1_out >> 16, c2 = 0;
                    if (eg_out < ENV_QUIET) c2 = OpCalc(cphase3, eg_out, m2);
                    if (eg_out2 < ENV_QUIET) cmem = OpCalc(cphase2, eg_out2, c1); else cmem = 0;
                    if (eg_out4 < ENV_QUIET) smp = OpCalc(cphase4, eg_out4, c2);
                    break;
                }
                case 1:
                {
                    int m2 = cmem, c2 = 0;
                    cmem = cop1_out >> 16;
                    if (eg_out < ENV_QUIET) c2 = OpCalc(cphase3, eg_out, m2);
                    if (eg_out2 < ENV_QUIET) cmem += OpCalc(cphase2, eg_out2, 0);
                    if (eg_out4 < ENV_QUIET) smp = OpCalc(cphase4, eg_out4, c2);
                    break;
                }
                case 2:
                {
                    int m2 = cmem, c2 = cop1_out >> 16;
                    if (eg_out < ENV_QUIET) c2 += OpCalc(cphase3, eg_out, m2);
                    if (eg_out2 < ENV_QUIET) cmem = OpCalc(cphase2, eg_out2, 0); else cmem = 0;
                    if (eg_out4 < ENV_QUIET) smp = OpCalc(cphase4, eg_out4, c2);
                    break;
                }
                case 3:
                {
                    int c2 = cmem, c1 = cop1_out >> 16;
                    if (eg_out < ENV_QUIET) c2 += OpCalc(cphase3, eg_out, 0);
                    if (eg_out2 < ENV_QUIET) cmem = OpCalc(cphase2, eg_out2, c1); else cmem = 0;
                    if (eg_out4 < ENV_QUIET) smp = OpCalc(cphase4, eg_out4, c2);
                    break;
                }
                case 4:
                {
                    int c1 = cop1_out >> 16, c2 = 0;
                    if (eg_out < ENV_QUIET) c2 = OpCalc(cphase3, eg_out, 0);
                    if (eg_out2 < ENV_QUIET) smp = OpCalc(cphase2, eg_out2, c1);
                    if (eg_out4 < ENV_QUIET) smp += OpCalc(cphase4, eg_out4, c2);
                    break;
                }
                case 5:
                {
                    int m2 = cmem, c1, c2;
                    cmem = c1 = c2 = cop1_out >> 16;
                    if (eg_out < ENV_QUIET) smp = OpCalc(cphase3, eg_out, m2);
                    if (eg_out2 < ENV_QUIET) smp += OpCalc(cphase2, eg_out2, c1);
                    if (eg_out4 < ENV_QUIET) smp += OpCalc(cphase4, eg_out4, c2);
                    break;
                }
                case 6:
                {
                    int c1 = cop1_out >> 16;
                    if (eg_out < ENV_QUIET) smp = OpCalc(cphase3, eg_out, 0);
                    if (eg_out2 < ENV_QUIET) smp += OpCalc(cphase2, eg_out2, c1);
                    if (eg_out4 < ENV_QUIET) smp += OpCalc(cphase4, eg_out4, 0);
                    break;
                }
                case 7:
                {
                    smp = cop1_out >> 16;
                    if (eg_out < ENV_QUIET) smp += OpCalc(cphase3, eg_out, 0);
                    if (eg_out2 < ENV_QUIET) smp += OpCalc(cphase2, eg_out2, 0);
                    if (eg_out4 < ENV_QUIET) smp += OpCalc(cphase4, eg_out4, 0);
                    break;
                }
            }

            if (smp != 0)
            {
                if ((cpack & 1) != 0)
                {
                    if ((cpack & 0x20) != 0) buffer[sc * 2] += smp;
                    if ((cpack & 0x10) != 0) buffer[sc * 2 + 1] += smp;
                }
                else buffer[sc] += smp;
                calgo = 8;
            }

            cphase1 += cincr1; cphase2 += cincr2; cphase3 += cincr3; cphase4 += cincr4;
        }
    }

    int ChanRender(int[] buffer, int length, int c, uint flags)
    {
        cCH = CH[c];
        cmem = cCH.mem_value;
        clfo_cnt = lfo_cnt;
        flags &= 0x35;
        if (clfo_inc != 0)
        {
            flags |= 8;
            flags |= (uint)g_lfo_ampm << 16;
            flags |= (uint)cCH.AMmasks << 8;
            if (cCH.ams == 8) flags &= ~0xf00u;
            else flags |= (uint)(cCH.ams & 3) << 6;
        }
        flags |= (uint)(cCH.FB & 0xf) << 12;
        cpack = flags;
        ceg_cnt = eg_cnt;
        ceg_timer = eg_timer;
        cphase1 = cCH.SLOT[SLOT1].phase; cphase2 = cCH.SLOT[SLOT2].phase; cphase3 = cCH.SLOT[SLOT3].phase; cphase4 = cCH.SLOT[SLOT4].phase;
        cv1 = (ushort)(cCH.SLOT[SLOT1].tl + cCH.SLOT[SLOT1].volume);
        cv2 = (ushort)(cCH.SLOT[SLOT2].tl + cCH.SLOT[SLOT2].volume);
        cv3 = (ushort)(cCH.SLOT[SLOT3].tl + cCH.SLOT[SLOT3].volume);
        cv4 = (ushort)(cCH.SLOT[SLOT4].tl + cCH.SLOT[SLOT4].volume);
        cop1_out = cCH.op1_out;
        calgo = (uint)(cCH.ALGO & 7);

        int offset = 0;
        if (cCH.pms != 0)
        {
            uint block_fnum = cCH.block_fnum;
            uint fnum_lfo = ((block_fnum & 0x7f0) >> 4) * 32 * 8;
            offset = lfo_pm_table[fnum_lfo + (uint)cCH.pms + ((cpack >> 16) & 0xff)];
            if (offset != 0)
            {
                byte blk = (byte)(block_fnum >> 11);
                block_fnum = (uint)(block_fnum * 2 + offset);
                uint fn = block_fnum & 0xfff;
                int kc = (blk << 2) | opn_fktable[fn >> 8];
                int fc = (int)(fn_table[fn] >> (7 - blk));
                cincr1 = ((uint)(fc + dtTab[cCH.SLOT[SLOT1].DT, kc]) * cCH.SLOT[SLOT1].mul) >> 1;
                cincr2 = ((uint)(fc + dtTab[cCH.SLOT[SLOT2].DT, kc]) * cCH.SLOT[SLOT2].mul) >> 1;
                cincr3 = ((uint)(fc + dtTab[cCH.SLOT[SLOT3].DT, kc]) * cCH.SLOT[SLOT3].mul) >> 1;
                cincr4 = ((uint)(fc + dtTab[cCH.SLOT[SLOT4].DT, kc]) * cCH.SLOT[SLOT4].mul) >> 1;
            }
        }
        if (offset == 0)
        {
            cincr1 = cCH.SLOT[SLOT1].Incr; cincr2 = cCH.SLOT[SLOT2].Incr; cincr3 = cCH.SLOT[SLOT3].Incr; cincr4 = cCH.SLOT[SLOT4].Incr;
        }

        ChanRenderLoop(buffer, length);

        cCH.op1_out = cop1_out;
        cCH.mem_value = cmem;
        if ((cCH.SLOT[SLOT1].state | cCH.SLOT[SLOT2].state | cCH.SLOT[SLOT3].state | cCH.SLOT[SLOT4].state) != 0)
        {
            cCH.SLOT[SLOT1].phase = cphase1; cCH.SLOT[SLOT2].phase = cphase2; cCH.SLOT[SLOT3].phase = cphase3; cCH.SLOT[SLOT4].phase = cphase4;
        }
        else slot_mask &= ~(0xfu << (c * 4));
        return (int)((calgo & 8) >> 3);
    }

    void RefreshFcEgSlot(Slot SLOT, int fc, int kc)
    {
        int fdt = fc + dtTab[SLOT.DT, kc];
        if (fdt < 0) fdt += (int)(fn_table[0x7ff * 2] >> 2);
        SLOT.Incr = (uint)fdt * SLOT.mul >> 1;
        int ksr = kc >> SLOT.KSR;
        if (SLOT.ksr != ksr)
        {
            SLOT.ksr = (byte)ksr;
            int sh, sel;
            if (SLOT.ar + ksr < 32 + 62) { sh = eg_rate_shift[SLOT.ar + ksr]; sel = eg_rate_select[SLOT.ar + ksr]; }
            else { sh = 0; sel = 17; }
            SLOT.eg_pack_ar = eg_inc_pack[sel] | ((uint)sh << 24);
            SLOT.eg_pack_d1r = RatePack(SLOT.d1r + ksr, out _);
            SLOT.eg_pack_d2r = RatePack(SLOT.d2r + ksr, out _);
            SLOT.eg_pack_rr = RatePack(SLOT.rr + ksr, out _);
        }
    }

    void RefreshFcEgChan(Chan ch)
    {
        if (ch.SLOT[SLOT1].Incr == uint.MaxValue)
        {
            int fc = (int)ch.fc, kc = ch.kcode;
            RefreshFcEgSlot(ch.SLOT[SLOT1], fc, kc); RefreshFcEgSlot(ch.SLOT[SLOT2], fc, kc);
            RefreshFcEgSlot(ch.SLOT[SLOT3], fc, kc); RefreshFcEgSlot(ch.SLOT[SLOT4], fc, kc);
        }
    }

    void RefreshFcEgChanSl3()
    {
        if (CH[2].SLOT[SLOT1].Incr == uint.MaxValue)
        {
            RefreshFcEgSlot(CH[2].SLOT[SLOT1], (int)sl3_fc[1], sl3_kcode[1]);
            RefreshFcEgSlot(CH[2].SLOT[SLOT2], (int)sl3_fc[2], sl3_kcode[2]);
            RefreshFcEgSlot(CH[2].SLOT[SLOT3], (int)sl3_fc[0], sl3_kcode[0]);
            RefreshFcEgSlot(CH[2].SLOT[SLOT4], (int)CH[2].fc, CH[2].kcode);
        }
    }

    void ResetChannels()
    {
        mode = 0;
        for (int c = 0; c < 6; c++)
        {
            CH[c].fc = 0;
            for (int s = 0; s < 4; s++) { CH[c].SLOT[s].state = EG_OFF; CH[c].SLOT[s].volume = MAX_ATT_INDEX; }
            CH[c].mem_value = CH[c].op1_out = 0;
        }
        slot_mask = 0;
    }

    int OpnWriteReg(int r, int v)
    {
        int ret = 1;
        int c = r & 3;
        if (c == 3) return 0;
        if (r >= 0x100) c += 3;
        var ch = CH[c];
        var slot = ch.SLOT[(r >> 2) & 3];
        switch (r & 0xf0)
        {
            case 0x30: SetDetMul(ch, slot, v); break;
            case 0x40: SetTl(slot, v); break;
            case 0x50: SetArKsr(ch, slot, v); break;
            case 0x60:
                SetDr(slot, v);
                if ((v & 0x80) != 0) ch.AMmasks |= (byte)(1 << ((r >> 2) & 3));
                else ch.AMmasks &= (byte)~(1 << ((r >> 2) & 3));
                break;
            case 0x70: SetSr(slot, v); break;
            case 0x80: SetSlRr(slot, v); break;
            case 0x90: ret = 0; break;   // SSG-EG: removed
            case 0xa0:
                switch ((r >> 2) & 3)
                {
                    case 0:
                    {
                        uint fn = ((uint)(ch.fn_h & 7) << 8) + (uint)v;
                        byte blk = (byte)(ch.fn_h >> 3);
                        ch.kcode = (byte)((blk << 2) | opn_fktable[fn >> 7]);
                        ch.fc = fn_table[fn * 2] >> (7 - blk);
                        ch.block_fnum = ((uint)blk << 11) | fn;
                        ch.SLOT[SLOT1].Incr = uint.MaxValue;
                        break;
                    }
                    case 1: ch.fn_h = (byte)(v & 0x3f); ret = 0; break;
                    case 2:
                        if (r < 0x100)
                        {
                            uint fn = ((uint)(sl3_fn_h & 7) << 8) + (uint)v;
                            byte blk = (byte)(sl3_fn_h >> 3);
                            sl3_kcode[c] = (byte)((blk << 2) | opn_fktable[fn >> 7]);
                            sl3_fc[c] = fn_table[fn * 2] >> (7 - blk);
                            sl3_block_fnum[c] = ((uint)blk << 11) | fn;
                            CH[2].SLOT[SLOT1].Incr = uint.MaxValue;
                        }
                        break;
                    case 3: if (r < 0x100) sl3_fn_h = (byte)(v & 0x3f); ret = 0; break;
                }
                break;
            case 0xb0:
                switch ((r >> 2) & 3)
                {
                    case 0:
                    {
                        int feedback = (v >> 3) & 7;
                        ch.ALGO = (byte)(v & 7);
                        ch.FB = (byte)(feedback != 0 ? feedback + 6 : 0);
                        break;
                    }
                    case 1:
                    {
                        int panshift = c << 1;
                        ch.pms = (v & 7) * 32;
                        ch.ams = lfo_ams_depth_shift[(v >> 4) & 3];
                        pan &= ~(3u << panshift);
                        pan |= (uint)((v & 0xc0) >> 6) << panshift;
                        break;
                    }
                    default: ret = 0; break;
                }
                break;
            default: ret = 0; break;
        }
        return ret;
    }

    /// <summary>YM2612ResetChip_.</summary>
    public void ResetChip()
    {
        SetTimers(0x30);
        eg_timer = 0; eg_cnt = 0;
        ResetChannels();
        for (int i = 0xb6; i >= 0xb4; i--) { OpnWriteReg(i, 0xc0); OpnWriteReg(i | 0x100, 0xc0); }
        for (int i = 0xb2; i >= 0x30; i--) { OpnWriteReg(i, 0); OpnWriteReg(i | 0x100, 0); }
        for (int i = 0x26; i >= 0x20; i--) OpnWriteReg(i, 0);
        dacen = 0;
        addr_A1 = 0;
    }

    /// <summary>A port write the way pico/memory.c's ym2612_write_local hands it on (DAC data, $27, $2B and the timers there; the rest to YM2612Write_).</summary>
    public void Write(int a, int d)
    {
        a &= 3; d &= 0xff;
        if (a == 1 && address == 0x2a) { dacout = (d - 0x80) << 6; return; }   // DAC data, whatever A1 says
        switch (a)
        {
            case 0: address = (byte)d; addr_A1 = 0; return;
            case 1:
                if (addr_A1 != 0) return;
                switch (address)
                {
                    case 0x24: case 0x25: case 0x26: return;            // timers: the stock chip's
                    case 0x27: mode = (byte)d; return;                   // 3-slot mode (timers: the stock chip's)
                    case 0x2b: dacen = d & 0x80; return;
                }
                break;
            case 2: address = (byte)d; addr_A1 = 1; return;
            case 3: if (addr_A1 != 1) return; break;
        }
        Ym2612Write(a, d);
    }

    void Ym2612Write(int a, int v)
    {
        switch (a & 3)
        {
            case 1:
            {
                int addr = address;
                if ((addr & 0xf0) == 0x20)
                {
                    switch (addr)
                    {
                        case 0x22: lfo_inc = (v & 0x08) != 0 ? lfo_freq[v & 7] : 0; break;
                        case 0x27: SetTimers(v); break;
                        case 0x28:
                        {
                            int c = v & 0x03;
                            if (c == 3) break;
                            if ((v & 0x04) != 0) c += 3;
                            if ((v & 0x10) != 0) KeyOn(c, SLOT1); else KeyOff(c, SLOT1);
                            if ((v & 0x20) != 0) KeyOn(c, SLOT2); else KeyOff(c, SLOT2);
                            if ((v & 0x40) != 0) KeyOn(c, SLOT3); else KeyOff(c, SLOT3);
                            if ((v & 0x80) != 0) KeyOn(c, SLOT4); else KeyOff(c, SLOT4);
                            break;
                        }
                        case 0x2a: dacout = (v - 0x80) << 6; break;
                        case 0x2b: dacen = v & 0x80; break;
                    }
                }
                else OpnWriteReg(addr, v);
                break;
            }
            case 3: OpnWriteReg(address | 0x100, v); break;
        }
    }

    /// <summary>YM2612UpdateOne_ (stereo, buffer cleared first): <paramref name="length"/> interleaved L/R FM samples at 44.1 kHz, unclipped.</summary>
    public int Render(int[] buffer, int length)
    {
        Array.Clear(buffer, 0, length * 2);
        RefreshFcEgChan(CH[0]);
        RefreshFcEgChan(CH[1]);
        if ((mode & 0xc0) != 0) RefreshFcEgChanSl3(); else RefreshFcEgChan(CH[2]);
        RefreshFcEgChan(CH[3]);
        RefreshFcEgChan(CH[4]);
        RefreshFcEgChan(CH[5]);

        uint p = pan;
        const uint stereo = 1;
        int active = 0;
        // chan_render_prep
        ceg_timer_add = eg_timer_add;
        clfo_inc = lfo_inc;
        if ((slot_mask & 0x00000f) != 0) active |= ChanRender(buffer, length, 0, stereo | ((p & 0x003) << 4)) << 0;
        if ((slot_mask & 0x0000f0) != 0) active |= ChanRender(buffer, length, 1, stereo | ((p & 0x00c) << 2)) << 1;
        if ((slot_mask & 0x000f00) != 0) active |= ChanRender(buffer, length, 2, stereo | (p & 0x030)) << 2;
        if ((slot_mask & 0x00f000) != 0) active |= ChanRender(buffer, length, 3, stereo | ((p & 0x0c0) >> 2)) << 3;
        if ((slot_mask & 0x0f0000) != 0) active |= ChanRender(buffer, length, 4, stereo | ((p & 0x300) >> 4)) << 4;
        if ((slot_mask & 0xf00000) != 0) active |= ChanRender(buffer, length, 5, stereo | ((p & 0xc00) >> 6) | ((uint)dacen << 2)) << 5;   // (dacen<<2) is masked off by "flags &= 0x35": kept
        // chan_render_finish
        eg_cnt = ceg_cnt;
        eg_timer = ceg_timer;
        g_lfo_ampm = (int)(cpack >> 16);
        lfo_cnt = clfo_cnt;
        return active;
    }
}
