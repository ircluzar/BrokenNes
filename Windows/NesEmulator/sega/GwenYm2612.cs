using System;

namespace NesEmulator.Sega;

// =====================================================================================================================
//  Gwenesis (retro-go) YM2612: MAME fm.c 1.4 (final beta) with Eke-Eke's Genesis Plus GX changes (2006-2012), as
//  trimmed by bzhxx for microcontrollers, ported to C#.
//
//  Source: retro-go, commit a0f857bf5a2f078f9f621355650c65cb96191eef, gwenesis/components/gwenesis/src/sound/
//  ym2612.c + ym2612.h, https://github.com/ducalex/retro-go/tree/a0f857bf5a2f078f9f621355650c65cb96191eef/gwenesis/components/gwenesis/src/sound
//  (upstream gwenesis by bzhxx, https://github.com/bzhxx/gwenesis).
//  Authors: Jarek Burczynski (2001-2003), Tatsuyuki Satoh (1998) / the MAME development team; Eke-Eke (Genesis Plus GX,
//  with Nemesis' hardware tests); bzhxx (gwenesis). Licence: AGPL-3.0 (the gwenesis tree's LICENSE).
//  This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2a and
//  docs/projects/apu-harvest-catalog.md.
//
//  Characteristic flaws kept on purpose (this is an art instrument; nothing here is "fixed"):
//   - MONO: the L/R panning is under #if 0 and the right channel sum is commented out line by line - every channel is
//     summed into one output whatever $B4-$B6 say (pan bits ignored, a hard-left/right channel plays in the middle)
//   - the mono sum of six +-8191-clamped channels is stored straight into an int16_t buffer with no final limiter:
//     loud chords wrap around (two's-complement overflow crackle) instead of clipping
//   - GW_TARGET: the LFO PM table cut to 16 UINT8 entries per depth (128*8*16), sign taken from LFO_PM bit 4,
//     pms = depth * 16
//   - no DAC precision bitmask (YM2612Config's 9-bit mask only reaches the unused pan masks): full 14-bit output,
//     no ladder effect
//   - the DAC replaces channel 6 at (v - 0x80) << 6
//   - phase/EG increments are refreshed only at the start of each rendered chunk (here: each sample, as the chunks
//     are split at writes); timer B is stepped once per chunk by the chunk's length
//   - timers run inside the engine only for CSM key-on; status/IRQ come from the stock chip
// =====================================================================================================================

/// <summary>Gwenesis's YM2612 at the chip's own rate (one sample per 144 YM clocks): feed it port writes, pull one mono sample at a time.</summary>
public sealed class GwenYm2612
{
    private const int ENV_BITS = 10, ENV_LEN = 1 << ENV_BITS;
    private const double ENV_STEP = 128.0 / ENV_LEN;
    private const int MAX_ATT_INDEX = ENV_LEN - 1, MIN_ATT_INDEX = 0;
    private const int EG_ATT = 4, EG_DEC = 3, EG_SUS = 2, EG_REL = 1, EG_OFF = 0;
    private const int DT_BITS = 17, DT_LEN = 1 << DT_BITS, DT_MASK = DT_LEN - 1;
    private const int SIN_BITS = 10, SIN_LEN = 1 << SIN_BITS, SIN_MASK = SIN_LEN - 1;
    private const int TL_RES_LEN = 256, TL_TAB_LEN = 13 * 2 * TL_RES_LEN, ENV_QUIET = TL_TAB_LEN >> 3;
    private const int RATE_STEPS = 8;
    private const int SLOT1 = 0, SLOT2 = 2, SLOT3 = 1, SLOT4 = 3;

    private static readonly int[] tl_tab = new int[TL_TAB_LEN];
    private static readonly uint[] sin_tab = new uint[SIN_LEN];
    private static readonly byte[] lfo_pm_table = new byte[128 * 8 * 16];   // GW_TARGET

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
        foreach (var a in new[] { 18, 18, 0, 0, 0, 0, 2, 2 }) t[k++] = O(a);   // "Nemesis's tests"
        for (int r = 0; r < 10; r++) for (int i = 0; i < 4; i++) t[k++] = O(i);
        for (int i = 0; i < 4; i++) t[k++] = O(4 + i);
        for (int i = 0; i < 4; i++) t[k++] = O(8 + i);
        for (int i = 0; i < 4; i++) t[k++] = O(12 + i);
        while (k < 128) t[k++] = O(16);
        return t;
    }

    private static readonly byte[] eg_rate_shift = BuildRateShift();
    private static byte[] BuildRateShift()
    {
        var t = new byte[128]; int k = 0;
        for (int i = 0; i < 32; i++) t[k++] = 11;   // "fixed (should be the same as rate 0 ...)"
        for (int s = 11; s >= 0; s--) for (int i = 0; i < 4; i++) t[k++] = (byte)s;
        return t;   // rates 12-15 and the 32 dummies are 0
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

    static GwenYm2612()
    {
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
                    byte value = 0;
                    for (int bit = 0; bit < 7; bit++)
                        if ((fnum & (1 << bit)) != 0) value += lfo_pm_output[bit * 8 + i, step];
                    lfo_pm_table[(fnum * 16 * 8) + (i * 16) + step + 0] = value;
                    lfo_pm_table[(fnum * 16 * 8) + (i * 16) + (step ^ 7) + 8] = value;
                }
    }

    private sealed class Slot
    {
        public int DT; public byte KSR; public uint ar, d1r, d2r, rr; public byte ksr; public uint mul;
        public uint phase; public int Incr;
        public byte state; public uint tl; public int volume; public uint sl; public uint vol_out;
        public byte eg_sh_ar, eg_sel_ar, eg_sh_d1r, eg_sel_d1r, eg_sh_d2r, eg_sel_d2r, eg_sh_rr, eg_sel_rr;
        public byte ssg, ssgn; public byte key; public uint AMmask;
    }

    private sealed class Channel
    {
        public readonly Slot[] SLOT = { new(), new(), new(), new() };
        public byte ALGO, FB; public readonly int[] op1_out = new int[2];
        public int connect1, connect2, connect3, connect4, mem_connect;   // indexes into v[] (-1 = algorithm 5's null mark)
        public int mem_value; public int pms; public byte ams; public uint fc; public byte kcode; public uint block_fnum;
    }

    // v[]: m2, c1, c2, mem, then out_fm[0..5]
    private const int M2 = 0, C1 = 1, C2 = 2, MEM = 3, OUT = 4;
    private readonly int[] v = new int[4 + 8];

    private readonly Channel[] CH = { new(), new(), new(), new(), new(), new() };
    private byte dacen; private int dacout;
    // FM_ST
    private int address; private byte status; private uint mode; private byte fn_h;
    private int TA, TAL, TAC, TB, TBL, TBC;
    private readonly int[,] st_dt_tab = new int[8, 32];
    // FM_3SLOT
    private readonly uint[] sl3_fc = new uint[3]; private byte sl3_fn_h; private readonly byte[] sl3_kcode = new byte[3]; private readonly uint[] sl3_block_fnum = new uint[3]; private byte key_csm;
    // FM_OPN
    private uint eg_cnt, eg_timer;
    private byte lfo_cnt; private uint lfo_timer, lfo_timer_overflow, LFO_AM, LFO_PM;

    public GwenYm2612()
    {
        // YM2612Init: memset 0, init_tables (the DT table lives in the chip state)
        for (int d = 0; d <= 3; d++)
            for (int i = 0; i <= 31; i++) { st_dt_tab[d, i] = dt_tab[d * 32 + i]; st_dt_tab[d + 4, i] = -st_dt_tab[d, i]; }
        ResetChip();
    }

    private static uint SsgOut(Slot S) => ((uint)(0x200 - S.volume) & MAX_ATT_INDEX) + S.tl;

    private void FM_KEYON(Channel ch, int s)
    {
        var SLOT = ch.SLOT[s];
        if (SLOT.key == 0 && key_csm == 0)
        {
            SLOT.phase = 0;
            SLOT.ssgn = 0;
            if (SLOT.ar + SLOT.ksr < 94)
                SLOT.state = SLOT.volume <= MIN_ATT_INDEX ? (SLOT.sl == MIN_ATT_INDEX ? (byte)EG_SUS : (byte)EG_DEC) : (byte)EG_ATT;
            else { SLOT.volume = MIN_ATT_INDEX; SLOT.state = SLOT.sl == MIN_ATT_INDEX ? (byte)EG_SUS : (byte)EG_DEC; }
            if ((SLOT.ssg & 0x08) != 0 && (SLOT.ssgn ^ (SLOT.ssg & 0x04)) != 0) SLOT.vol_out = SsgOut(SLOT);
            else SLOT.vol_out = (uint)SLOT.volume + SLOT.tl;
        }
        SLOT.key = 1;
    }

    private void FM_KEYOFF(Channel ch, int s)
    {
        var SLOT = ch.SLOT[s];
        if (SLOT.key != 0 && key_csm == 0)
        {
            if (SLOT.state > EG_REL)
            {
                SLOT.state = EG_REL;
                if ((SLOT.ssg & 0x08) != 0)
                {
                    if ((SLOT.ssgn ^ (SLOT.ssg & 0x04)) != 0) SLOT.volume = 0x200 - SLOT.volume;
                    if (SLOT.volume >= 0x200) { SLOT.volume = MAX_ATT_INDEX; SLOT.state = EG_OFF; }
                    SLOT.vol_out = (uint)SLOT.volume + SLOT.tl;
                }
            }
        }
        SLOT.key = 0;
    }

    private void FM_KEYON_CSM(Channel ch, int s)
    {
        var SLOT = ch.SLOT[s];
        if (SLOT.key == 0 && key_csm == 0)
        {
            SLOT.phase = 0;
            SLOT.ssgn = 0;
            if (SLOT.ar + SLOT.ksr < 94)
                SLOT.state = SLOT.volume <= MIN_ATT_INDEX ? (SLOT.sl == MIN_ATT_INDEX ? (byte)EG_SUS : (byte)EG_DEC) : (byte)EG_ATT;
            else { SLOT.volume = MIN_ATT_INDEX; SLOT.state = SLOT.sl == MIN_ATT_INDEX ? (byte)EG_SUS : (byte)EG_DEC; }
            if ((SLOT.ssg & 0x08) != 0 && (SLOT.ssgn ^ (SLOT.ssg & 0x04)) != 0) SLOT.vol_out = SsgOut(SLOT);
            else SLOT.vol_out = (uint)SLOT.volume + SLOT.tl;
        }
    }

    private static void FM_KEYOFF_CSM(Channel ch, int s)
    {
        var SLOT = ch.SLOT[s];
        if (SLOT.key == 0)
        {
            if (SLOT.state > EG_REL)
            {
                SLOT.state = EG_REL;
                if ((SLOT.ssg & 0x08) != 0)
                {
                    if ((SLOT.ssgn ^ (SLOT.ssg & 0x04)) != 0) SLOT.volume = 0x200 - SLOT.volume;
                    if (SLOT.volume >= 0x200) { SLOT.volume = MAX_ATT_INDEX; SLOT.state = EG_OFF; }
                    SLOT.vol_out = (uint)SLOT.volume + SLOT.tl;
                }
            }
        }
    }

    private void CSMKeyControll(Channel ch)
    {
        FM_KEYON_CSM(ch, SLOT1); FM_KEYON_CSM(ch, SLOT2); FM_KEYON_CSM(ch, SLOT3); FM_KEYON_CSM(ch, SLOT4);
        key_csm = 1;
    }

    private void INTERNAL_TIMER_A()
    {
        if ((mode & 0x01) != 0)
        {
            TAC--;
            if (TAC <= 0)
            {
                if ((mode & 0x04) != 0) status |= 0x01;
                TAC = TAL;
                if ((mode & 0xC0) == 0x80) CSMKeyControll(CH[2]);
            }
        }
    }

    private void INTERNAL_TIMER_B(int step)
    {
        if ((mode & 0x02) != 0)
        {
            TBC -= step;
            if (TBC <= 0)
            {
                if ((mode & 0x08) != 0) status |= 0x02;
                if (TBL != 0) TBC += TBL; else TBC = TBL;
            }
        }
    }

    private void set_timers(int val)
    {
        if (((mode ^ (uint)val) & 0xC0) != 0)
        {
            CH[2].SLOT[SLOT1].Incr = -1;
            if ((val & 0xC0) != 0x80 && key_csm != 0)
            {
                FM_KEYOFF_CSM(CH[2], SLOT1); FM_KEYOFF_CSM(CH[2], SLOT2); FM_KEYOFF_CSM(CH[2], SLOT3); FM_KEYOFF_CSM(CH[2], SLOT4);
                key_csm = 0;
            }
        }
        if ((val & 1) != 0 && (mode & 1) == 0) TAC = TAL;
        if ((val & 2) != 0 && (mode & 2) == 0) TBC = TBL;
        status = (byte)(status & (~val >> 4));
        mode = (uint)val;
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

    private static void set_tl(Slot SLOT, int val)
    {
        SLOT.tl = (uint)((val & 0x7f) << (ENV_BITS - 7));
        if ((SLOT.ssg & 0x08) != 0 && (SLOT.ssgn ^ (SLOT.ssg & 0x04)) != 0 && SLOT.state > EG_REL) SLOT.vol_out = SsgOut(SLOT);
        else SLOT.vol_out = (uint)SLOT.volume + SLOT.tl;
    }

    private static void set_ar_ksr(Channel CH, Slot SLOT, int val)
    {
        byte old_KSR = SLOT.KSR;
        SLOT.ar = (val & 0x1f) != 0 ? 32 + (uint)((val & 0x1f) << 1) : 0;
        SLOT.KSR = (byte)(3 - (val >> 6));
        if (SLOT.KSR != old_KSR) CH.SLOT[SLOT1].Incr = -1;
        if (SLOT.ar + SLOT.ksr < 32 + 62)
        {
            SLOT.eg_sh_ar = eg_rate_shift[SLOT.ar + SLOT.ksr];
            SLOT.eg_sel_ar = eg_rate_select[SLOT.ar + SLOT.ksr];
        }
        else { SLOT.eg_sh_ar = 0; SLOT.eg_sel_ar = 18 * RATE_STEPS; }
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
        if (SLOT.state == EG_DEC && SLOT.volume >= (int)SLOT.sl) SLOT.state = EG_SUS;
        SLOT.rr = 34 + (uint)((val & 0x0f) << 2);
        SLOT.eg_sh_rr = eg_rate_shift[SLOT.rr + SLOT.ksr];
        SLOT.eg_sel_rr = eg_rate_select[SLOT.rr + SLOT.ksr];
    }

    private void advance_lfo()
    {
        if (lfo_timer_overflow != 0)
        {
            lfo_timer++;
            if (lfo_timer >= lfo_timer_overflow)
            {
                lfo_timer = 0;
                lfo_cnt = (byte)((lfo_cnt + 1) & 127);
                LFO_AM = lfo_cnt < 64 ? (uint)((lfo_cnt ^ 63) << 1) : (uint)((lfo_cnt & 63) << 1);
                LFO_PM = (uint)(lfo_cnt >> 2);
            }
        }
    }

    private void advance_eg_channels(uint eg_cnt)
    {
        for (int c = 0; c < 6; c++)
            for (int k = 0; k < 4; k++)   // SLOT[0..3] in memory order
            {
                var SLOT = CH[c].SLOT[k];
                switch (SLOT.state)
                {
                    case EG_ATT:
                        if ((eg_cnt & ((1u << SLOT.eg_sh_ar) - 1)) == 0)
                        {
                            SLOT.volume += (~SLOT.volume * eg_inc[SLOT.eg_sel_ar + ((eg_cnt >> SLOT.eg_sh_ar) & 7)]) >> 4;
                            if (SLOT.volume <= MIN_ATT_INDEX)
                            {
                                SLOT.volume = MIN_ATT_INDEX;
                                SLOT.state = SLOT.sl == MIN_ATT_INDEX ? (byte)EG_SUS : (byte)EG_DEC;
                            }
                            if ((SLOT.ssg & 0x08) != 0 && (SLOT.ssgn ^ (SLOT.ssg & 0x04)) != 0) SLOT.vol_out = SsgOut(SLOT);
                            else SLOT.vol_out = (uint)SLOT.volume + SLOT.tl;
                        }
                        break;
                    case EG_DEC:
                        if ((eg_cnt & ((1u << SLOT.eg_sh_d1r) - 1)) == 0)
                        {
                            if ((SLOT.ssg & 0x08) != 0)
                            {
                                if (SLOT.volume < 0x200)
                                {
                                    SLOT.volume += 4 * eg_inc[SLOT.eg_sel_d1r + ((eg_cnt >> SLOT.eg_sh_d1r) & 7)];
                                    if ((SLOT.ssgn ^ (SLOT.ssg & 0x04)) != 0) SLOT.vol_out = SsgOut(SLOT);
                                    else SLOT.vol_out = (uint)SLOT.volume + SLOT.tl;
                                }
                            }
                            else
                            {
                                SLOT.volume += eg_inc[SLOT.eg_sel_d1r + ((eg_cnt >> SLOT.eg_sh_d1r) & 7)];
                                SLOT.vol_out = (uint)SLOT.volume + SLOT.tl;
                            }
                            if (SLOT.volume >= (int)SLOT.sl) SLOT.state = EG_SUS;
                        }
                        break;
                    case EG_SUS:
                        if ((eg_cnt & ((1u << SLOT.eg_sh_d2r) - 1)) == 0)
                        {
                            if ((SLOT.ssg & 0x08) != 0)
                            {
                                if (SLOT.volume < 0x200)
                                {
                                    SLOT.volume += 4 * eg_inc[SLOT.eg_sel_d2r + ((eg_cnt >> SLOT.eg_sh_d2r) & 7)];
                                    if ((SLOT.ssgn ^ (SLOT.ssg & 0x04)) != 0) SLOT.vol_out = SsgOut(SLOT);
                                    else SLOT.vol_out = (uint)SLOT.volume + SLOT.tl;
                                }
                            }
                            else
                            {
                                SLOT.volume += eg_inc[SLOT.eg_sel_d2r + ((eg_cnt >> SLOT.eg_sh_d2r) & 7)];
                                if (SLOT.volume >= MAX_ATT_INDEX) SLOT.volume = MAX_ATT_INDEX;
                                SLOT.vol_out = (uint)SLOT.volume + SLOT.tl;
                            }
                        }
                        break;
                    case EG_REL:
                        if ((eg_cnt & ((1u << SLOT.eg_sh_rr) - 1)) == 0)
                        {
                            if ((SLOT.ssg & 0x08) != 0)
                            {
                                if (SLOT.volume < 0x200) SLOT.volume += 4 * eg_inc[SLOT.eg_sel_rr + ((eg_cnt >> SLOT.eg_sh_rr) & 7)];
                                if (SLOT.volume >= 0x200) { SLOT.volume = MAX_ATT_INDEX; SLOT.state = EG_OFF; }
                            }
                            else
                            {
                                SLOT.volume += eg_inc[SLOT.eg_sel_rr + ((eg_cnt >> SLOT.eg_sh_rr) & 7)];
                                if (SLOT.volume >= MAX_ATT_INDEX) { SLOT.volume = MAX_ATT_INDEX; SLOT.state = EG_OFF; }
                            }
                            SLOT.vol_out = (uint)SLOT.volume + SLOT.tl;
                        }
                        break;
                }
            }
    }

    private void update_ssg_eg_channels()
    {
        for (int c = 0; c < 6; c++)
            for (int k = 0; k < 4; k++)
            {
                var SLOT = CH[c].SLOT[k];
                if ((SLOT.ssg & 0x08) != 0 && SLOT.volume >= 0x200 && SLOT.state > EG_REL)
                {
                    if ((SLOT.ssg & 0x01) != 0)
                    {
                        if ((SLOT.ssg & 0x02) != 0) SLOT.ssgn = 4;
                        if (SLOT.state != EG_ATT && (SLOT.ssgn ^ (SLOT.ssg & 0x04)) == 0) SLOT.volume = MAX_ATT_INDEX;
                    }
                    else
                    {
                        if ((SLOT.ssg & 0x02) != 0) SLOT.ssgn ^= 4;
                        else SLOT.phase = 0;
                        if (SLOT.state != EG_ATT)
                        {
                            if (SLOT.ar + SLOT.ksr < 94)
                                SLOT.state = SLOT.volume <= MIN_ATT_INDEX ? (SLOT.sl == MIN_ATT_INDEX ? (byte)EG_SUS : (byte)EG_DEC) : (byte)EG_ATT;
                            else { SLOT.volume = MIN_ATT_INDEX; SLOT.state = SLOT.sl == MIN_ATT_INDEX ? (byte)EG_SUS : (byte)EG_DEC; }
                        }
                    }
                    if ((SLOT.ssgn ^ (SLOT.ssg & 0x04)) != 0) SLOT.vol_out = SsgOut(SLOT);
                    else SLOT.vol_out = (uint)SLOT.volume + SLOT.tl;
                }
            }
    }

    private int PmOffset(uint block_fnum, int pms)
    {
        int off = lfo_pm_table[(((block_fnum & 0x7f0) >> 4) << 7) + pms + (LFO_PM & 0xF)];
        if ((LFO_PM & 0x10) != 0) off = -off;
        return off;
    }

    private void update_phase_lfo_slot(Slot SLOT, int pms, uint block_fnum)
    {
        int off = PmOffset(block_fnum, pms);
        if (off != 0)
        {
            block_fnum = (uint)(block_fnum * 2 + off);
            byte blk = (byte)((block_fnum & 0x7000) >> 12);
            block_fnum &= 0xfff;
            uint kc = (uint)((blk << 2) | opn_fktable[block_fnum >> 8]);
            uint fc = (uint)(((block_fnum << 5) >> (7 - blk)) + st_dt_tab[SLOT.DT, kc]) & DT_MASK;
            SLOT.phase += (fc * SLOT.mul) >> 1;
        }
        else SLOT.phase += (uint)SLOT.Incr;
    }

    private void update_phase_lfo_channel(Channel ch)
    {
        uint block_fnum = ch.block_fnum;
        int off = PmOffset(block_fnum, ch.pms);
        if (off != 0)
        {
            block_fnum = (uint)(block_fnum * 2 + off);
            byte blk = (byte)((block_fnum & 0x7000) >> 12);
            block_fnum &= 0xfff;
            uint kc = (uint)((blk << 2) | opn_fktable[block_fnum >> 8]);
            uint fc = (block_fnum << 5) >> (7 - blk);
            foreach (int s in new[] { SLOT1, SLOT2, SLOT3, SLOT4 })
            {
                var S = ch.SLOT[s];
                uint finc = (uint)(fc + st_dt_tab[S.DT, kc]) & DT_MASK;
                S.phase += (finc * S.mul) >> 1;
            }
        }
        else
            for (int s = 0; s < 4; s++) ch.SLOT[s].phase += (uint)ch.SLOT[s].Incr;
    }

    private void refresh_fc_eg_slot(Slot SLOT, uint fc, uint kc)
    {
        fc = (uint)(fc + st_dt_tab[SLOT.DT, kc]);
        fc &= DT_MASK;
        SLOT.Incr = (int)((fc * SLOT.mul) >> 1);
        kc >>= SLOT.KSR;
        if (SLOT.ksr != kc)
        {
            SLOT.ksr = (byte)kc;
            if (SLOT.ar + kc < 32 + 62)
            {
                SLOT.eg_sh_ar = eg_rate_shift[SLOT.ar + kc];
                SLOT.eg_sel_ar = eg_rate_select[SLOT.ar + kc];
            }
            else { SLOT.eg_sh_ar = 0; SLOT.eg_sel_ar = 18 * RATE_STEPS; }
            SLOT.eg_sh_d1r = eg_rate_shift[SLOT.d1r + kc]; SLOT.eg_sel_d1r = eg_rate_select[SLOT.d1r + kc];
            SLOT.eg_sh_d2r = eg_rate_shift[SLOT.d2r + kc]; SLOT.eg_sel_d2r = eg_rate_select[SLOT.d2r + kc];
            SLOT.eg_sh_rr = eg_rate_shift[SLOT.rr + kc]; SLOT.eg_sel_rr = eg_rate_select[SLOT.rr + kc];
        }
    }

    private void refresh_fc_eg_chan(Channel ch)
    {
        if (ch.SLOT[SLOT1].Incr == -1)
        {
            int fc = (int)ch.fc, kc = ch.kcode;
            refresh_fc_eg_slot(ch.SLOT[SLOT1], (uint)fc, (uint)kc);
            refresh_fc_eg_slot(ch.SLOT[SLOT2], (uint)fc, (uint)kc);
            refresh_fc_eg_slot(ch.SLOT[SLOT3], (uint)fc, (uint)kc);
            refresh_fc_eg_slot(ch.SLOT[SLOT4], (uint)fc, (uint)kc);
        }
    }

    private static int op_calc(uint phase, uint env, uint pm)
    {
        uint p = (env << 3) + sin_tab[((phase >> SIN_BITS) + (pm >> 1)) & SIN_MASK];
        return p >= TL_TAB_LEN ? 0 : tl_tab[p];
    }

    private static int op_calc1(uint phase, uint env, uint pm)
    {
        uint p = (env << 3) + sin_tab[((phase + pm) >> SIN_BITS) & SIN_MASK];
        return p >= TL_TAB_LEN ? 0 : tl_tab[p];
    }

    private void chan_calc(int first, int num)
    {
        for (int c = first; c < first + num; c++)
        {
            var ch = CH[c];
            uint AM = LFO_AM >> ch.ams;
            uint eg_out = ch.SLOT[SLOT1].vol_out + (AM & ch.SLOT[SLOT1].AMmask);
            v[M2] = v[C1] = v[C2] = v[MEM] = 0;
            v[ch.mem_connect] = ch.mem_value;
            {
                int o = ch.op1_out[0] + ch.op1_out[1];
                ch.op1_out[0] = ch.op1_out[1];
                if (ch.connect1 < 0) v[MEM] = v[C1] = v[C2] = ch.op1_out[0];
                else v[ch.connect1] += ch.op1_out[0];
                ch.op1_out[1] = 0;
                if (eg_out < ENV_QUIET)
                {
                    if (ch.FB == 0) o = 0;
                    ch.op1_out[1] = op_calc1(ch.SLOT[SLOT1].phase, eg_out, (uint)(o << ch.FB));
                }
            }
            eg_out = ch.SLOT[SLOT3].vol_out + (AM & ch.SLOT[SLOT3].AMmask);
            if (eg_out < ENV_QUIET) v[ch.connect3] += op_calc(ch.SLOT[SLOT3].phase, eg_out, (uint)v[M2]);
            eg_out = ch.SLOT[SLOT2].vol_out + (AM & ch.SLOT[SLOT2].AMmask);
            if (eg_out < ENV_QUIET) v[ch.connect2] += op_calc(ch.SLOT[SLOT2].phase, eg_out, (uint)v[C1]);
            eg_out = ch.SLOT[SLOT4].vol_out + (AM & ch.SLOT[SLOT4].AMmask);
            if (eg_out < ENV_QUIET) v[ch.connect4] += op_calc(ch.SLOT[SLOT4].phase, eg_out, (uint)v[C2]);

            ch.mem_value = v[MEM];

            if (ch.pms != 0)
            {
                if ((mode & 0xC0) != 0 && c == 2)
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
    }

    private void OPNWriteMode(int r, int val)
    {
        switch (r)
        {
            case 0x21: break;
            case 0x22:
                if ((val & 8) != 0) lfo_timer_overflow = lfo_samples_per_step[val & 7];
                else { lfo_timer_overflow = 0; lfo_timer = 0; lfo_cnt = 0; LFO_PM = 0; LFO_AM = 126; }
                break;
            case 0x24: TA = (TA & 0x03) | (val << 2); TAL = 1024 - TA; break;
            case 0x25: TA = (TA & 0x3fc) | (val & 3); TAL = 1024 - TA; break;
            case 0x26: TB = val; TBL = (256 - val) << 4; break;
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
            case 0x90:
                SLOT.ssg = (byte)(val & 0x0f);
                if (SLOT.state > EG_REL)
                {
                    if ((SLOT.ssg & 0x08) != 0 && (SLOT.ssgn ^ (SLOT.ssg & 0x04)) != 0) SLOT.vol_out = SsgOut(SLOT);
                    else SLOT.vol_out = (uint)SLOT.volume + SLOT.tl;
                }
                break;
            case 0xa0:
                switch ((r >> 2) & 3)
                {
                    case 0:
                    {
                        uint fn = ((uint)(fn_h & 7) << 8) + (uint)val;
                        byte blk = (byte)(fn_h >> 3);
                        ch.kcode = (byte)((blk << 2) | opn_fktable[fn >> 7]);
                        ch.fc = (fn << 6) >> (7 - blk);
                        ch.block_fnum = ((uint)blk << 11) | fn;
                        ch.SLOT[SLOT1].Incr = -1;
                        break;
                    }
                    case 1: fn_h = (byte)(val & 0x3f); break;
                    case 2:
                        if (r < 0x100)
                        {
                            uint fn = ((uint)(sl3_fn_h & 7) << 8) + (uint)val;
                            byte blk = (byte)(sl3_fn_h >> 3);
                            sl3_kcode[c] = (byte)((blk << 2) | opn_fktable[fn >> 7]);
                            sl3_fc[c] = (fn << 6) >> (7 - blk);
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
                        ch.ALGO = (byte)(val & 7);
                        ch.FB = (byte)((val >> 3) & 7);
                        setup_connection(ch, c);
                        break;
                    case 1:
                        ch.pms = (val & 7) * 16;   // GW_TARGET: was * 32
                        ch.ams = lfo_ams_depth_shift[(val >> 4) & 0x03];
                        // PAN: b7 = L, b6 = R - commented out in gwenesis (mono)
                        break;
                }
                break;
        }
    }

    private void reset_channels()
    {
        foreach (var ch in CH)
        {
            ch.mem_value = 0; ch.op1_out[0] = 0; ch.op1_out[1] = 0;
            foreach (var s in ch.SLOT) { s.Incr = -1; s.key = 0; s.phase = 0; s.ssgn = 0; s.state = EG_OFF; s.volume = MAX_ATT_INDEX; s.vol_out = MAX_ATT_INDEX; }
        }
    }

    /// <summary>YM2612ResetChip.</summary>
    public void ResetChip()
    {
        eg_timer = 0; eg_cnt = 0;
        lfo_timer_overflow = 0; lfo_timer = 0; lfo_cnt = 0; LFO_AM = 126; LFO_PM = 0;
        TAC = 0; TBC = 0;
        key_csm = 0;
        dacen = 0; dacout = 0;
        set_timers(0x30);
        TB = 0; TBL = 256 << 4; TA = 0; TAL = 1024;
        reset_channels();
        for (int i = 0xb6; i >= 0xb4; i--) { OPNWriteReg(i, 0xc0); OPNWriteReg(i | 0x100, 0xc0); }
        for (int i = 0xb2; i >= 0x30; i--) { OPNWriteReg(i, 0); OPNWriteReg(i | 0x100, 0); }
    }

    /// <summary>YM2612Write: port 0 / 2 = address of part I / II, 1 / 3 = data (one shared address latch, as Nemesis verified).</summary>
    public void Write(int a, int val)
    {
        val &= 0xff;
        switch (a)
        {
            case 0: address = val; break;
            case 2: address = val | 0x100; break;
            default:
            {
                int addr = address;
                if ((addr & 0x1f0) == 0x20)
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
        }
    }

    public int Status => status;

    /// <summary>One sample of YM2612Update (a chunk of length 1: the chunk-start refresh, then the sample, then timer B by 1). Mono, wrapped to int16 as the buffer stored it.</summary>
    public short Sample()
    {
        refresh_fc_eg_chan(CH[0]);
        refresh_fc_eg_chan(CH[1]);
        if ((mode & 0xC0) == 0) refresh_fc_eg_chan(CH[2]);
        else
        {
            var c2 = CH[2];
            if (c2.SLOT[SLOT1].Incr == -1)
            {
                refresh_fc_eg_slot(c2.SLOT[SLOT1], sl3_fc[1], sl3_kcode[1]);
                refresh_fc_eg_slot(c2.SLOT[SLOT2], sl3_fc[2], sl3_kcode[2]);
                refresh_fc_eg_slot(c2.SLOT[SLOT3], sl3_fc[0], sl3_kcode[0]);
                refresh_fc_eg_slot(c2.SLOT[SLOT4], c2.fc, c2.kcode);
            }
        }
        refresh_fc_eg_chan(CH[3]);
        refresh_fc_eg_chan(CH[4]);
        refresh_fc_eg_chan(CH[5]);

        for (int i = 0; i < 6; i++) v[OUT + i] = 0;
        update_ssg_eg_channels();
        if (dacen == 0) chan_calc(0, 6);
        else { v[OUT + 5] = dacout; chan_calc(0, 5); }

        advance_lfo();
        eg_timer++;
        if (eg_timer >= 3)
        {
            eg_timer = 0;
            eg_cnt++;
            advance_eg_channels(eg_cnt);
        }
        int lt = 0;
        for (int i = 0; i < 6; i++)
        {
            int o = v[OUT + i];
            if (o > 8191) o = 8191; else if (o < -8192) o = -8192;
            lt += o;   // mono: the right channel is commented out
        }
        short sample = unchecked((short)lt);   // int16_t buffer, no limiter

        key_csm <<= 1;
        INTERNAL_TIMER_A();
        if ((key_csm & 2) != 0)
        {
            FM_KEYOFF_CSM(CH[2], SLOT1); FM_KEYOFF_CSM(CH[2], SLOT2); FM_KEYOFF_CSM(CH[2], SLOT3); FM_KEYOFF_CSM(CH[2], SLOT4);
            key_csm = 0;
        }
        INTERNAL_TIMER_B(1);
        return sample;
    }
}
