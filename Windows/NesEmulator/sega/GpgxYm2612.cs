using System;

namespace NesEmulator.Sega;

// =====================================================================================================================
//  Genesis Plus GX 1.3.0 YM2612 (MAME fm.c 1.4 + Eke-Eke's fixes), ported to C#.
//
//  Source: Genesis Plus GX 1.3.0 (Eke-Eke, 2008-12-14), tree at commit 6f65ac3, source/sound/fm.c + fm.h,
//  https://raw.githubusercontent.com/ekeeke/Genesis-Plus-GX/6f65ac3/source/sound/fm.c . 1.3.0 also shipped Gens'
//  ym2612.c, but its default (ngc/config.c: config.fm_core = 0, config.hq_fm = 1) is this fm.c at the chip's own rate
//  (VCLK/144, freqbase forced to 1.0) - the setting ported here. 1.3.1 (4eb8d17) dropped this core.
//  Copyright (C) 2001-2003 Jarek Burczynski, (C) 1998 Tatsuyuki Satoh / the MAME development team; 2006-2008 changes
//  by Eke-Eke (with Nemesis' hardware tests). License: the MAME license (non-commercial; source must accompany), inside
//  Genesis Plus (Charles MacDonald, non-commercial terms). This is a documented exception to the repository's license
//  policy - see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
//
//  A quick port; the original's shortcuts are kept, they are the sound:
//   - half-done SSG-EG: x6 decay steps, the level runs past MAX_ATT_INDEX while "holding", inverted output XORed
//     with 1023 (the pre-1.3.1 SSG-EG that 1.3.1 rewrote after Nemesis' later tests)
//   - "Nemesis's tests" EG rate row (rates 0-1 infinite, 2-3 select 0/0/2/2), the attack check ar+ksr < 94
//   - the DAC replaces channel 6 at (v - 0x80) << 6 ("level unknown"), added to the channel 6 sum unfiltered
//   - phase/envelope increments are only refreshed at the start of a rendered sample after a write (fm.c refreshes at
//     the start of each update chunk; Genesis Plus split chunks at each data write)
//   - the output limiter at +16383/-16384, no ladder effect, no 9-bit DAC truncation
//   - timer A runs inside the engine only for CSM key-on; status/IRQ come from the stock chip
// =====================================================================================================================

/// <summary>The Genesis Plus GX 1.3.0 fm.c YM2612 at the chip's own rate (one sample per 144 YM clocks): feed it port writes, pull one stereo sample at a time.</summary>
public sealed class GpgxYm2612
{
    private const int FREQ_SH = 16, EG_SH = 16, LFO_SH = 24;
    private const uint FREQ_MASK = (1u << FREQ_SH) - 1;
    private const int ENV_BITS = 10, ENV_LEN = 1 << ENV_BITS;
    private const double ENV_STEP = 128.0 / ENV_LEN;
    private const int MAX_ATT_INDEX = ENV_LEN - 1, MIN_ATT_INDEX = 0;
    private const int EG_ATT = 4, EG_DEC = 3, EG_SUS = 2, EG_REL = 1, EG_OFF = 0;
    private const int SIN_BITS = 10, SIN_LEN = 1 << SIN_BITS, SIN_MASK = SIN_LEN - 1;
    private const int TL_RES_LEN = 256, TL_TAB_LEN = 13 * 2 * TL_RES_LEN, ENV_QUIET = TL_TAB_LEN >> 3;
    private const int MAXOUT = 16383, MINOUT = -16384;
    private const int RATE_STEPS = 8;
    private const int SLOT1 = 0, SLOT2 = 2, SLOT3 = 1, SLOT4 = 3;

    private static readonly int[] tl_tab = new int[TL_TAB_LEN];
    private static readonly uint[] sin_tab = new uint[SIN_LEN];
    private static readonly int[] lfo_pm_table = new int[128 * 8 * 32];

    private static uint SC(double db) => (uint)(db * (4.0 / ENV_STEP));
    private static readonly uint[] sl_table = { SC(0), SC(1), SC(2), SC(3), SC(4), SC(5), SC(6), SC(7), SC(8), SC(9), SC(10), SC(11), SC(12), SC(13), SC(14), SC(31) };

    private static readonly byte[] eg_inc = {
        0,1, 0,1, 0,1, 0,1,  0,1, 0,1, 1,1, 0,1,  0,1, 1,1, 0,1, 1,1,  0,1, 1,1, 1,1, 1,1,
        1,1, 1,1, 1,1, 1,1,  1,1, 1,2, 1,1, 1,2,  1,2, 1,2, 1,2, 1,2,  1,2, 2,2, 1,2, 2,2,
        2,2, 2,2, 2,2, 2,2,  2,2, 2,4, 2,2, 2,4,  2,4, 2,4, 2,4, 2,4,  2,4, 4,4, 2,4, 4,4,
        4,4, 4,4, 4,4, 4,4,  4,4, 4,8, 4,4, 4,8,  4,8, 4,8, 4,8, 4,8,  4,8, 8,8, 4,8, 8,8,
        8,8, 8,8, 8,8, 8,8,  16,16,16,16,16,16,16,16,  0,0, 0,0, 0,0, 0,0,
    };

    private static readonly byte[] eg_rate_select = BuildRateSelect();
    private static byte[] BuildRateSelect()
    {
        static byte O(int a) => (byte)(a * RATE_STEPS);
        var t = new byte[128]; int k = 0;
        for (int i = 0; i < 32; i++) t[k++] = O(18);
        // rates 00-11 ("Nemesis's tests" first two rows)
        foreach (var a in new[] { 18, 18, 0, 0, 0, 0, 2, 2 }) t[k++] = O(a);
        for (int r = 0; r < 10; r++) for (int i = 0; i < 4; i++) t[k++] = O(i);
        for (int i = 0; i < 4; i++) t[k++] = O(4 + i);
        for (int i = 0; i < 4; i++) t[k++] = O(8 + i);
        for (int i = 0; i < 4; i++) t[k++] = O(12 + i);
        for (int i = 0; i < 4; i++) t[k++] = O(16);
        while (k < 128) t[k++] = O(16);
        return t;
    }

    private static readonly byte[] eg_rate_shift = BuildRateShift();
    private static byte[] BuildRateShift()
    {
        var t = new byte[128]; int k = 32;
        for (int s = 11; s >= 0; s--) for (int i = 0; i < 4; i++) t[k++] = (byte)s;
        return t;   // the rest (rates 12-15 and the 32 dummies) are 0
    }

    private static readonly byte[] dt_tab = {
        0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,
        0,0,0,0,1,1,1,1,1,1,1,1,2,2,2,2, 2,3,3,3,4,4,4,5,5,6,6,7,8,8,8,8,
        1,1,1,1,2,2,2,2,2,3,3,3,4,4,4,5, 5,6,6,7,8,8,9,10,11,12,13,14,16,16,16,16,
        2,2,2,2,2,3,3,3,4,4,4,5,5,6,6,7, 8,8,9,10,11,12,13,14,16,17,19,20,22,22,22,22,
    };
    private static readonly byte[] opn_fktable = { 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 3, 3, 3, 3, 3, 3 };
    private static readonly uint[] lfo_samples_per_step = { 108, 77, 71, 67, 62, 44, 8, 5 };
    private static readonly byte[] lfo_ams_depth_shift = { 8, 3, 1, 0 };
    private static readonly byte[,] lfo_pm_output = {
        {0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,1,1,1,1},
        {0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,1,1,1,1},{0,0,1,1,2,2,2,3},
        {0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,1},{0,0,0,0,1,1,1,1},{0,0,1,1,2,2,2,3},{0,0,2,3,4,4,5,6},
        {0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,0},{0,0,0,0,0,0,1,1},{0,0,0,0,1,1,1,1},{0,0,0,1,1,1,1,2},{0,0,1,1,2,2,2,3},{0,0,2,3,4,4,5,6},{0,0,4,6,8,8,0xa,0xc},
        {0,0,0,0,0,0,0,0},{0,0,0,0,1,1,1,1},{0,0,0,1,1,1,2,2},{0,0,1,1,2,2,3,3},{0,0,1,2,2,2,3,4},{0,0,2,3,4,4,5,6},{0,0,4,6,8,8,0xa,0xc},{0,0,8,0xc,0x10,0x10,0x14,0x18},
        {0,0,0,0,0,0,0,0},{0,0,0,0,2,2,2,2},{0,0,0,2,2,2,4,4},{0,0,2,2,4,4,6,6},{0,0,2,4,4,4,6,8},{0,0,4,6,8,8,0xa,0xc},{0,0,8,0xc,0x10,0x10,0x14,0x18},{0,0,0x10,0x18,0x20,0x20,0x28,0x30},
        {0,0,0,0,0,0,0,0},{0,0,0,0,4,4,4,4},{0,0,0,4,4,4,8,8},{0,0,4,4,8,8,0xc,0xc},{0,0,4,8,8,8,0xc,0x10},{0,0,8,0xc,0x10,0x10,0x14,0x18},{0,0,0x10,0x18,0x20,0x20,0x28,0x30},{0,0,0x20,0x30,0x40,0x40,0x50,0x60},
    };

    static GpgxYm2612()
    {
        // init_tables()
        for (int x = 0; x < TL_RES_LEN; x++)
        {
            double m = (1 << 16) / Math.Pow(2, (x + 1) * (ENV_STEP / 4.0) / 8.0);
            m = Math.Floor(m);
            int n = (int)m;
            n >>= 4;
            if ((n & 1) != 0) n = (n >> 1) + 1; else n >>= 1;
            n <<= 2;
            tl_tab[x * 2 + 0] = n;
            tl_tab[x * 2 + 1] = -tl_tab[x * 2 + 0];
            for (int i = 1; i < 13; i++)
            {
                tl_tab[x * 2 + 0 + i * 2 * TL_RES_LEN] = tl_tab[x * 2 + 0] >> i;
                tl_tab[x * 2 + 1 + i * 2 * TL_RES_LEN] = -tl_tab[x * 2 + 0 + i * 2 * TL_RES_LEN];
            }
        }
        for (int i = 0; i < SIN_LEN; i++)
        {
            double m = Math.Sin(((i * 2) + 1) * Math.PI / SIN_LEN);
            double o = m > 0.0 ? 8 * Math.Log(1.0 / m) / Math.Log(2) : 8 * Math.Log(-1.0 / m) / Math.Log(2);
            o = o / (ENV_STEP / 4);
            int n = (int)(2.0 * o);
            if ((n & 1) != 0) n = (n >> 1) + 1; else n >>= 1;
            sin_tab[i] = (uint)(n * 2 + (m >= 0.0 ? 0 : 1));
        }
        for (int i = 0; i < 8; i++)
            for (int fnum = 0; fnum < 128; fnum++)
                for (int step = 0; step < 8; step++)
                {
                    int value = 0;
                    for (int bit = 0; bit < 7; bit++)
                        if ((fnum & (1 << bit)) != 0) value += lfo_pm_output[bit * 8 + i, step];
                    value &= 0xff;   // UINT8 accumulator
                    lfo_pm_table[(fnum * 32 * 8) + (i * 32) + step + 0] = value;
                    lfo_pm_table[(fnum * 32 * 8) + (i * 32) + (step ^ 7) + 8] = value;
                    lfo_pm_table[(fnum * 32 * 8) + (i * 32) + step + 16] = -value;
                    lfo_pm_table[(fnum * 32 * 8) + (i * 32) + (step ^ 7) + 24] = -value;
                }
    }

    private sealed class Slot
    {
        public int DT; public byte KSR; public uint ar, d1r, d2r, rr; public byte ksr; public uint mul;
        public uint phase; public int Incr;
        public byte state; public uint tl; public int volume; public uint sl; public uint vol_out;
        public byte eg_sh_ar, eg_sel_ar, eg_sh_d1r, eg_sel_d1r, eg_sh_d2r, eg_sel_d2r, eg_sh_rr, eg_sel_rr;
        public byte ssg, ssgn; public uint key; public uint AMmask;
    }

    private sealed class Channel
    {
        public readonly Slot[] SLOT = { new(), new(), new(), new() };
        public byte ALGO, FB; public readonly int[] op1_out = new int[2];
        public int connect1, connect2, connect3, connect4, mem_connect;   // indexes into v[] (-1 = algorithm 5's special mark)
        public int mem_value; public int pms; public byte ams; public uint fc; public byte kcode; public uint block_fnum;
    }

    // v[]: c1, c2, m2, mem, then out_fm[0..5]
    private const int C1 = 0, C2 = 1, M2 = 2, MEM = 3, OUT = 4;
    private readonly int[] v = new int[10];

    private readonly Channel[] CH = { new(), new(), new(), new(), new(), new() };
    private byte dacen; private int dacout;
    // FM_ST
    private double freqbase; private readonly byte[] address = new byte[2]; private byte status; private uint mode; private byte fn_h;
    private int TimerBase, TA, TAL, TAC, TB, TBL, TBC;
    private readonly int[,] st_dt_tab = new int[8, 32];
    // FM_3SLOT
    private readonly uint[] sl3_fc = new uint[3]; private byte sl3_fn_h; private readonly byte[] sl3_kcode = new byte[3]; private readonly uint[] sl3_block_fnum = new uint[3];
    // FM_OPN
    private readonly uint[] pan = new uint[12];
    private uint eg_cnt, eg_timer, eg_timer_add, eg_timer_overflow;
    private readonly uint[] fn_table = new uint[4096]; private uint fn_max;
    private uint lfo_cnt, lfo_inc; private readonly uint[] lfo_freq = new uint[8];
    private uint LFO_AM; private int LFO_PM;

    public GpgxYm2612() { ResetChip(); }

    // ---- registers --------------------------------------------------------------------------------------------------

    private void set_timers(int val)
    {
        if (((mode ^ (uint)val) & 0xC0) != 0) CH[2].SLOT[SLOT1].Incr = -1;
        if ((val & 1) != 0 && (mode & 1) == 0) TAC = TAL;
        if ((val & 2) != 0 && (mode & 2) == 0) TBC = TBL;
        status = (byte)(status & (~val >> 4));
        mode = (uint)val;
    }

    private static void FM_KEYON(Channel ch, int s)
    {
        var SLOT = ch.SLOT[s];
        if (SLOT.key == 0)
        {
            SLOT.key = 1;
            SLOT.phase = 0;
            SLOT.ssgn = (byte)((SLOT.ssg & 0x04) >> 1);
            if (SLOT.ar + SLOT.ksr < 94) SLOT.state = EG_ATT;
            else { SLOT.volume = MIN_ATT_INDEX; SLOT.state = EG_DEC; }
        }
    }

    private static void FM_KEYOFF(Channel ch, int s)
    {
        var SLOT = ch.SLOT[s];
        if (SLOT.key != 0)
        {
            SLOT.key = 0;
            if (SLOT.state > EG_REL) SLOT.state = EG_REL;
        }
    }

    private static void setup_connection(Channel CH, int ch)
    {
        int carrier = OUT + ch;
        switch (CH.ALGO)
        {
            case 0: CH.connect1 = C1; CH.connect2 = MEM; CH.connect3 = C2; CH.mem_connect = M2; break;
            case 1: CH.connect1 = MEM; CH.connect2 = MEM; CH.connect3 = C2; CH.mem_connect = M2; break;
            case 2: CH.connect1 = C2; CH.connect2 = MEM; CH.connect3 = C2; CH.mem_connect = M2; break;
            case 3: CH.connect1 = C1; CH.connect2 = MEM; CH.connect3 = C2; CH.mem_connect = C2; break;
            case 4: CH.connect1 = C1; CH.connect2 = carrier; CH.connect3 = C2; CH.mem_connect = MEM; break;
            case 5: CH.connect1 = -1; CH.connect2 = carrier; CH.connect3 = carrier; CH.mem_connect = M2; break;
            case 6: CH.connect1 = C1; CH.connect2 = carrier; CH.connect3 = carrier; CH.mem_connect = MEM; break;
            case 7: CH.connect1 = carrier; CH.connect2 = carrier; CH.connect3 = carrier; CH.mem_connect = MEM; break;
        }
        CH.connect4 = carrier;
    }

    private static void set_det_mul(Channel CH, Slot SLOT, int val)
    {
        SLOT.mul = (val & 0x0f) != 0 ? (uint)(val & 0x0f) * 2 : 1;
        SLOT.DT = (val >> 4) & 7;
        CH.SLOT[SLOT1].Incr = -1;
    }

    private static void set_tl(Slot SLOT, int val) => SLOT.tl = (uint)((val & 0x7f) << (ENV_BITS - 7));

    private static void set_ar_ksr(Channel CH, Slot SLOT, int val)
    {
        byte old_KSR = SLOT.KSR;
        SLOT.ar = (val & 0x1f) != 0 ? 32 + (uint)((val & 0x1f) << 1) : 0;
        SLOT.KSR = (byte)(3 - (val >> 6));
        if (SLOT.KSR != old_KSR) CH.SLOT[SLOT1].Incr = -1;
        if (SLOT.ar + SLOT.ksr < 94)
        {
            SLOT.eg_sh_ar = eg_rate_shift[SLOT.ar + SLOT.ksr];
            SLOT.eg_sel_ar = eg_rate_select[SLOT.ar + SLOT.ksr];
        }
        else { SLOT.eg_sh_ar = 0; SLOT.eg_sel_ar = 17 * RATE_STEPS; }
    }

    private static void set_dr(Slot SLOT, int val)
    {
        SLOT.d1r = (val & 0x1f) != 0 ? 32 + (uint)((val & 0x1f) << 1) : 0;
        SLOT.eg_sh_d1r = eg_rate_shift[SLOT.d1r + SLOT.ksr];
        SLOT.eg_sel_d1r = eg_rate_select[SLOT.d1r + SLOT.ksr];
    }

    private static void set_sr(Slot SLOT, int val)
    {
        SLOT.d2r = (val & 0x1f) != 0 ? 32 + (uint)((val & 0x1f) << 1) : 0;
        SLOT.eg_sh_d2r = eg_rate_shift[SLOT.d2r + SLOT.ksr];
        SLOT.eg_sel_d2r = eg_rate_select[SLOT.d2r + SLOT.ksr];
    }

    private static void set_sl_rr(Slot SLOT, int val)
    {
        SLOT.sl = sl_table[val >> 4];
        SLOT.rr = 34 + (uint)((val & 0x0f) << 2);
        SLOT.eg_sh_rr = eg_rate_shift[SLOT.rr + SLOT.ksr];
        SLOT.eg_sel_rr = eg_rate_select[SLOT.rr + SLOT.ksr];
    }

    private static int op_calc(uint phase, uint env, int pm)
    {
        uint p = (env << 3) + sin_tab[((int)((phase & ~FREQ_MASK) + (uint)(pm << 15)) >> FREQ_SH) & SIN_MASK];
        return p >= TL_TAB_LEN ? 0 : tl_tab[p];
    }

    private static int op_calc1(uint phase, uint env, int pm)
    {
        uint p = (env << 3) + sin_tab[((int)((phase & ~FREQ_MASK) + (uint)pm) >> FREQ_SH) & SIN_MASK];
        return p >= TL_TAB_LEN ? 0 : tl_tab[p];
    }

    // ---- per sample --------------------------------------------------------------------------------------------------

    private void advance_lfo()
    {
        if (lfo_inc != 0)
        {
            lfo_cnt += lfo_inc;
            int pos = (int)((lfo_cnt >> LFO_SH) & 127);
            LFO_AM = pos < 64 ? (uint)((pos & 63) * 2) : (uint)(126 - ((pos & 63) * 2));
            pos >>= 2;
            LFO_PM = pos;
        }
        else { LFO_AM = 0; LFO_PM = 0; }
    }

    private void advance_eg_channel(Channel ch)
    {
        for (int k = 0; k < 4; k++)
        {
            var SLOT = ch.SLOT[k];
            int swap_flag = 0;
            switch (SLOT.state)
            {
                case EG_ATT:
                    if ((eg_cnt & ((1u << SLOT.eg_sh_ar) - 1)) == 0)
                    {
                        SLOT.volume += (~SLOT.volume * eg_inc[SLOT.eg_sel_ar + ((eg_cnt >> SLOT.eg_sh_ar) & 7)]) >> 4;
                        if (SLOT.volume <= MIN_ATT_INDEX) { SLOT.volume = MIN_ATT_INDEX; SLOT.state = EG_DEC; }
                    }
                    break;
                case EG_DEC:
                    if ((eg_cnt & ((1u << SLOT.eg_sh_d1r) - 1)) == 0)
                    {
                        int inc = eg_inc[SLOT.eg_sel_d1r + ((eg_cnt >> SLOT.eg_sh_d1r) & 7)];
                        SLOT.volume += (SLOT.ssg & 0x08) != 0 ? 6 * inc : inc;
                    }
                    if (SLOT.volume >= (int)SLOT.sl) { SLOT.volume = (int)SLOT.sl; SLOT.state = EG_SUS; }
                    break;
                case EG_SUS:
                    if ((SLOT.ssg & 0x08) != 0)
                    {
                        if ((eg_cnt & ((1u << SLOT.eg_sh_d2r) - 1)) == 0)
                            SLOT.volume += 6 * eg_inc[SLOT.eg_sel_d2r + ((eg_cnt >> SLOT.eg_sh_d2r) & 7)];
                        if (SLOT.volume >= ENV_QUIET)
                        {
                            if ((SLOT.ssg & 0x01) != 0)
                            {
                                if ((SLOT.ssgn & 1) == 0) swap_flag = (SLOT.ssg & 0x02) | 1;
                            }
                            else
                            {
                                SLOT.phase = 0;
                                if (SLOT.ar + SLOT.ksr < 94) SLOT.state = EG_ATT;
                                else
                                {
                                    SLOT.volume = MIN_ATT_INDEX;
                                    SLOT.state = SLOT.sl == MIN_ATT_INDEX ? (byte)EG_SUS : (byte)EG_DEC;
                                }
                                swap_flag = SLOT.ssg & 0x02;
                            }
                        }
                    }
                    else if ((eg_cnt & ((1u << SLOT.eg_sh_d2r) - 1)) == 0)
                    {
                        SLOT.volume += eg_inc[SLOT.eg_sel_d2r + ((eg_cnt >> SLOT.eg_sh_d2r) & 7)];
                        if (SLOT.volume >= MAX_ATT_INDEX) SLOT.volume = MAX_ATT_INDEX;
                    }
                    break;
                case EG_REL:
                    if ((eg_cnt & ((1u << SLOT.eg_sh_rr) - 1)) == 0)
                    {
                        int inc = eg_inc[SLOT.eg_sel_rr + ((eg_cnt >> SLOT.eg_sh_rr) & 7)];
                        SLOT.volume += (SLOT.ssg & 0x08) != 0 ? 6 * inc : inc;
                        if (SLOT.volume >= MAX_ATT_INDEX) { SLOT.volume = MAX_ATT_INDEX; SLOT.state = EG_OFF; }
                    }
                    break;
            }
            uint o = (uint)SLOT.volume;
            if ((SLOT.ssg & 0x08) != 0 && (SLOT.ssgn & 2) != 0 && SLOT.state > EG_REL) o ^= MAX_ATT_INDEX;
            SLOT.vol_out = o + SLOT.tl;
            SLOT.ssgn ^= (byte)swap_flag;
        }
    }

    private void update_phase_lfo_slot(Slot SLOT, int pms, uint block_fnum)
    {
        uint fnum_lfo = ((block_fnum & 0x7f0) >> 4) * 32 * 8;
        int off = lfo_pm_table[fnum_lfo + pms + LFO_PM];
        if (off != 0)
        {
            block_fnum = (uint)(block_fnum * 2 + off);
            int blk = (int)((block_fnum & 0x7000) >> 12);
            uint fn = block_fnum & 0xfff;
            int kc = (blk << 2) | opn_fktable[fn >> 8];
            int fc = (int)(fn_table[fn] >> (7 - blk)) + st_dt_tab[SLOT.DT, kc];
            if (fc < 0) fc += (int)fn_max;
            SLOT.phase += ((uint)fc * SLOT.mul) >> 1;
        }
        else SLOT.phase += (uint)SLOT.Incr;
    }

    private void update_phase_lfo_channel(Channel ch)
    {
        uint block_fnum = ch.block_fnum;
        uint fnum_lfo = ((block_fnum & 0x7f0) >> 4) * 32 * 8;
        int off = lfo_pm_table[fnum_lfo + ch.pms + LFO_PM];
        if (off != 0)
        {
            block_fnum = (uint)(block_fnum * 2 + off);
            int blk = (int)((block_fnum & 0x7000) >> 12);
            uint fn = block_fnum & 0xfff;
            int kc = (blk << 2) | opn_fktable[fn >> 8];
            int fc = (int)(fn_table[fn] >> (7 - blk));
            for (int s = 0; s < 4; s++)   // SLOT1, SLOT2, SLOT3, SLOT4 - each touches only its own phase
            {
                var S = ch.SLOT[s];
                int finc = fc + st_dt_tab[S.DT, kc];
                if (finc < 0) finc += (int)fn_max;
                S.phase += ((uint)finc * S.mul) >> 1;
            }
        }
        else
            for (int s = 0; s < 4; s++) ch.SLOT[s].phase += (uint)ch.SLOT[s].Incr;
    }

    private void chan_calc(Channel ch, bool isCh2)
    {
        uint AM = LFO_AM >> ch.ams;
        v[M2] = v[C1] = v[C2] = v[MEM] = 0;
        v[ch.mem_connect] = ch.mem_value;

        uint eg_out = ch.SLOT[SLOT1].vol_out + (AM & ch.SLOT[SLOT1].AMmask);
        {
            int o = ch.op1_out[0] + ch.op1_out[1];
            ch.op1_out[0] = ch.op1_out[1];
            if (ch.connect1 < 0) v[MEM] = v[C1] = v[C2] = ch.op1_out[0];
            else v[ch.connect1] += ch.op1_out[0];
            ch.op1_out[1] = 0;
            if (eg_out < ENV_QUIET)
            {
                if (ch.FB == 0) o = 0;
                ch.op1_out[1] = op_calc1(ch.SLOT[SLOT1].phase, eg_out, o << ch.FB);
            }
        }
        eg_out = ch.SLOT[SLOT3].vol_out + (AM & ch.SLOT[SLOT3].AMmask);
        if (eg_out < ENV_QUIET) v[ch.connect3] += op_calc(ch.SLOT[SLOT3].phase, eg_out, v[M2]);
        eg_out = ch.SLOT[SLOT2].vol_out + (AM & ch.SLOT[SLOT2].AMmask);
        if (eg_out < ENV_QUIET) v[ch.connect2] += op_calc(ch.SLOT[SLOT2].phase, eg_out, v[C1]);
        eg_out = ch.SLOT[SLOT4].vol_out + (AM & ch.SLOT[SLOT4].AMmask);
        if (eg_out < ENV_QUIET) v[ch.connect4] += op_calc(ch.SLOT[SLOT4].phase, eg_out, v[C2]);

        ch.mem_value = v[MEM];

        if (ch.pms != 0)
        {
            if ((mode & 0xC0) != 0 && isCh2)
            {
                update_phase_lfo_slot(ch.SLOT[SLOT1], ch.pms, sl3_block_fnum[1]);
                update_phase_lfo_slot(ch.SLOT[SLOT2], ch.pms, sl3_block_fnum[2]);
                update_phase_lfo_slot(ch.SLOT[SLOT3], ch.pms, sl3_block_fnum[0]);
                update_phase_lfo_slot(ch.SLOT[SLOT4], ch.pms, ch.block_fnum);
            }
            else update_phase_lfo_channel(ch);
        }
        else
            for (int s = 0; s < 4; s++) ch.SLOT[s].phase += (uint)ch.SLOT[s].Incr;
    }

    private void refresh_fc_eg_slot(Slot SLOT, int fc, int kc)
    {
        int ksr = kc >> SLOT.KSR;
        fc += st_dt_tab[SLOT.DT, kc];
        if (fc < 0) fc += (int)fn_max;
        SLOT.Incr = (int)(((uint)fc * SLOT.mul) >> 1);
        if (SLOT.ksr != ksr)
        {
            SLOT.ksr = (byte)ksr;
            if (SLOT.ar + SLOT.ksr < 94)
            {
                SLOT.eg_sh_ar = eg_rate_shift[SLOT.ar + SLOT.ksr];
                SLOT.eg_sel_ar = eg_rate_select[SLOT.ar + SLOT.ksr];
            }
            else { SLOT.eg_sh_ar = 0; SLOT.eg_sel_ar = 17 * RATE_STEPS; }
            SLOT.eg_sh_d1r = eg_rate_shift[SLOT.d1r + SLOT.ksr]; SLOT.eg_sel_d1r = eg_rate_select[SLOT.d1r + SLOT.ksr];
            SLOT.eg_sh_d2r = eg_rate_shift[SLOT.d2r + SLOT.ksr]; SLOT.eg_sel_d2r = eg_rate_select[SLOT.d2r + SLOT.ksr];
            SLOT.eg_sh_rr = eg_rate_shift[SLOT.rr + SLOT.ksr]; SLOT.eg_sel_rr = eg_rate_select[SLOT.rr + SLOT.ksr];
        }
    }

    private void refresh_fc_eg_chan(Channel ch)
    {
        if (ch.SLOT[SLOT1].Incr == -1)
        {
            int fc = (int)ch.fc, kc = ch.kcode;
            refresh_fc_eg_slot(ch.SLOT[SLOT1], fc, kc);
            refresh_fc_eg_slot(ch.SLOT[SLOT2], fc, kc);
            refresh_fc_eg_slot(ch.SLOT[SLOT3], fc, kc);
            refresh_fc_eg_slot(ch.SLOT[SLOT4], fc, kc);
        }
    }

    private void CSMKeyControll(Channel ch)
    {
        for (int s = 0; s < 4; s++)
            if (ch.SLOT[s].key == 0) { FM_KEYON(ch, s); FM_KEYOFF(ch, s); }
    }

    private void INTERNAL_TIMER_A()
    {
        if ((mode & 0x01) != 0)
        {
            if ((TAC -= TimerBase) <= 0)
            {
                if ((mode & 0x04) != 0) status |= 0x01;
                if (TAL != 0) TAC += TAL; else TAC = TAL;
                if ((mode & 0xC0) == 0x80) CSMKeyControll(CH[2]);
            }
        }
    }

    private void INTERNAL_TIMER_B(int step)
    {
        if ((mode & 0x02) != 0)
        {
            if ((TBC -= TimerBase * step) <= 0)
            {
                if ((mode & 0x08) != 0) status |= 0x02;
                if (TBL != 0) TBC += TBL; else TBC = TBL;
            }
        }
    }

    private void OPNSetPres()
    {
        freqbase = 1.0;   // config.hq_fm: the YM2612 runs at its own rate (VCLK/144)
        eg_timer_add = (uint)((1 << EG_SH) * freqbase);
        eg_timer_overflow = 3 * (1u << EG_SH);
        TimerBase = (int)(freqbase * 4096.0);
        for (int d = 0; d <= 3; d++)
            for (int i = 0; i <= 31; i++)
            {
                double rate = dt_tab[d * 32 + i] * (double)SIN_LEN * freqbase * (1 << FREQ_SH) / (double)(1 << 20);
                st_dt_tab[d, i] = (int)rate;
                st_dt_tab[d + 4, i] = -st_dt_tab[d, i];
            }
        for (int i = 0; i < 4096; i++) fn_table[i] = (uint)(i * 32 * freqbase * (1 << (FREQ_SH - 10)));
        fn_max = (uint)(0x20000 * freqbase * (1 << (FREQ_SH - 10)));
        for (int i = 0; i < 8; i++) lfo_freq[i] = (uint)((1.0 / lfo_samples_per_step[i]) * (1 << LFO_SH) * freqbase);
    }

    private void OPNWriteMode(int r, int val)
    {
        switch (r)
        {
            case 0x21: break;
            case 0x22: lfo_inc = (val & 0x08) != 0 ? lfo_freq[val & 7] : 0; break;
            case 0x24: TA = (TA & 0x03) | (val << 2); TAL = (1024 - TA) << 12; break;
            case 0x25: TA = (TA & 0x3fc) | (val & 3); TAL = (1024 - TA) << 12; break;
            case 0x26: TB = val; TBL = (256 - TB) << 16; break;
            case 0x27: set_timers(val); break;
            case 0x28:
            {
                int c = val & 0x03;
                if (c == 3) break;
                if ((val & 0x04) != 0) c += 3;
                var ch = CH[c];
                if ((val & 0x10) != 0) FM_KEYON(ch, SLOT1); else FM_KEYOFF(ch, SLOT1);
                if ((val & 0x20) != 0) FM_KEYON(ch, SLOT2); else FM_KEYOFF(ch, SLOT2);
                if ((val & 0x40) != 0) FM_KEYON(ch, SLOT3); else FM_KEYOFF(ch, SLOT3);
                if ((val & 0x80) != 0) FM_KEYON(ch, SLOT4); else FM_KEYOFF(ch, SLOT4);
                break;
            }
        }
    }

    private void OPNWriteReg(int r, int val)
    {
        int c = r & 3;
        if (c == 3) return;
        if (r >= 0x100) c += 3;
        var ch = CH[c];
        var SLOT = ch.SLOT[(r >> 2) & 3];
        switch (r & 0xf0)
        {
            case 0x30: set_det_mul(ch, SLOT, val); break;
            case 0x40: set_tl(SLOT, val); break;
            case 0x50: set_ar_ksr(ch, SLOT, val); break;
            case 0x60: set_dr(SLOT, val); SLOT.AMmask = (val & 0x80) != 0 ? ~0u : 0; break;
            case 0x70: set_sr(SLOT, val); break;
            case 0x80: set_sl_rr(SLOT, val); break;
            case 0x90: SLOT.ssg = (byte)(val & 0x0f); SLOT.ssgn = (byte)((val & 0x04) >> 1); break;
            case 0xa0:
                switch ((r >> 2) & 3)
                {
                    case 0:
                    {
                        uint fn = ((uint)(fn_h & 7) << 8) + (uint)val;
                        int blk = fn_h >> 3;
                        ch.kcode = (byte)((blk << 2) | opn_fktable[fn >> 7]);
                        ch.fc = fn_table[fn * 2] >> (7 - blk);
                        ch.block_fnum = ((uint)blk << 11) | fn;
                        ch.SLOT[SLOT1].Incr = -1;
                        break;
                    }
                    case 1: fn_h = (byte)(val & 0x3f); break;
                    case 2:
                        if (r < 0x100)
                        {
                            uint fn = ((uint)(sl3_fn_h & 7) << 8) + (uint)val;
                            int blk = sl3_fn_h >> 3;
                            sl3_kcode[c] = (byte)((blk << 2) | opn_fktable[fn >> 7]);
                            sl3_fc[c] = fn_table[fn * 2] >> (7 - blk);
                            sl3_block_fnum[c] = ((uint)blk << 11) | fn;
                            CH[2].SLOT[SLOT1].Incr = -1;
                        }
                        break;
                    case 3: if (r < 0x100) sl3_fn_h = (byte)(val & 0x3f); break;
                }
                break;
            case 0xb0:
                switch ((r >> 2) & 3)
                {
                    case 0:
                    {
                        int feedback = (val >> 3) & 7;
                        ch.ALGO = (byte)(val & 7);
                        ch.FB = (byte)(feedback != 0 ? feedback + 6 : 0);
                        setup_connection(ch, c);
                        break;
                    }
                    case 1:
                        ch.pms = (val & 7) * 32;
                        ch.ams = lfo_ams_depth_shift[(val >> 4) & 0x03];
                        pan[c * 2] = (val & 0x80) != 0 ? ~0u : 0;
                        pan[c * 2 + 1] = (val & 0x40) != 0 ? ~0u : 0;
                        break;
                }
                break;
        }
    }

    // ---- public API ---------------------------------------------------------------------------------------------------

    /// <summary>YM2612ResetChip.</summary>
    public void ResetChip()
    {
        OPNSetPres();
        OPNWriteMode(0x27, 0x30);
        eg_timer = 0; eg_cnt = 0; status = 0; mode = 0;
        TA = TAL = TAC = TB = TBL = TBC = 0;
        foreach (var ch in CH)
        {
            ch.fc = 0;
            foreach (var s in ch.SLOT) { s.ssg = 0; s.ssgn = 0; s.state = EG_OFF; s.volume = MAX_ATT_INDEX; s.vol_out = MAX_ATT_INDEX; }
        }
        for (int i = 0xb6; i >= 0xb4; i--) { OPNWriteReg(i, 0xc0); OPNWriteReg(i | 0x100, 0xc0); }
        for (int i = 0xb2; i >= 0x30; i--) { OPNWriteReg(i, 0); OPNWriteReg(i | 0x100, 0); }
        for (int i = 0x26; i >= 0x20; i--) OPNWriteMode(i, 0);
        dacen = 0; dacout = 0;
    }

    /// <summary>YM2612Write: port 0-3 (address/data of part I, address/data of part II).</summary>
    public void Write(int a, int val)
    {
        val &= 0xff;
        switch (a & 3)
        {
            case 0: address[0] = (byte)val; break;
            case 1:
            {
                int addr = address[0];
                if ((addr & 0xf0) == 0x20)
                {
                    switch (addr)
                    {
                        case 0x2a: dacout = (val - 0x80) << 6; break;
                        case 0x2b: dacen = (byte)(val & 0x80); break;
                        default: OPNWriteMode(addr, val); break;
                    }
                }
                else OPNWriteReg(addr, val);
                break;
            }
            case 2: address[1] = (byte)val; break;
            case 3: OPNWriteReg(address[1] | 0x100, val); break;
        }
    }

    public int Status => status;

    /// <summary>One sample of YM2612UpdateOne (the refresh of phase/EG increments that fm.c does at the start of each chunk is done here before the sample).</summary>
    public void Sample(out int lt, out int rt)
    {
        refresh_fc_eg_chan(CH[0]);
        refresh_fc_eg_chan(CH[1]);
        if ((mode & 0xC0) != 0)
        {
            var c2 = CH[2];
            if (c2.SLOT[SLOT1].Incr == -1)
            {
                refresh_fc_eg_slot(c2.SLOT[SLOT1], (int)sl3_fc[1], sl3_kcode[1]);
                refresh_fc_eg_slot(c2.SLOT[SLOT2], (int)sl3_fc[2], sl3_kcode[2]);
                refresh_fc_eg_slot(c2.SLOT[SLOT3], (int)sl3_fc[0], sl3_kcode[0]);
                refresh_fc_eg_slot(c2.SLOT[SLOT4], (int)c2.fc, c2.kcode);
            }
        }
        else refresh_fc_eg_chan(CH[2]);
        refresh_fc_eg_chan(CH[3]);
        refresh_fc_eg_chan(CH[4]);
        refresh_fc_eg_chan(CH[5]);

        advance_lfo();
        for (int i = 0; i < 6; i++) v[OUT + i] = 0;
        chan_calc(CH[0], false);
        chan_calc(CH[1], false);
        chan_calc(CH[2], true);
        chan_calc(CH[3], false);
        chan_calc(CH[4], false);
        if (dacen != 0) v[CH[5].connect4] += dacout;
        else chan_calc(CH[5], false);

        eg_timer += eg_timer_add;
        while (eg_timer >= eg_timer_overflow)
        {
            eg_timer -= eg_timer_overflow;
            eg_cnt++;
            for (int i = 0; i < 6; i++) advance_eg_channel(CH[i]);
        }

        lt = 0; rt = 0;
        for (int i = 0; i < 6; i++)
        {
            lt += (int)((uint)v[OUT + i] & pan[i * 2]);
            rt += (int)((uint)v[OUT + i] & pan[i * 2 + 1]);
        }
        if (lt > MAXOUT) lt = MAXOUT; else if (lt < MINOUT) lt = MINOUT;
        if (rt > MAXOUT) rt = MAXOUT; else if (rt < MINOUT) rt = MINOUT;

        INTERNAL_TIMER_A();
        INTERNAL_TIMER_B(1);   // fm.c steps timer B once per chunk by the chunk's length
    }
}
