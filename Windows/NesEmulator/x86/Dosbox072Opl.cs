// DOSBox 0.72's OPL2 engine: the old MAME fmopl.c ("Version 0.70"), YM3812 path only, ported line by line to C# for BrokenNes as an "art instrument".
//
// Source:   DOSBox 0.72, SVN tags/RELEASE_0_72, src/hardware/fmopl.c r2883 (+ fmopl.h, driven by src/hardware/adlib.cpp)
//           https://sourceforge.net/p/dosbox/code-0/HEAD/tree/tags/RELEASE_0_72/src/hardware/
// Authors:  Jarek Burczynski (2002-2003), Tatsuyuki Satoh (1999-2000), MultiArcadeMachineEmulator development; DOSBox Team (adlib.cpp glue)
// Licence:  no licence stated in fmopl.c (MAME-derived) - documented exception, see THIRD_PARTY_NOTICES.md section 2a.
//
// How DOSBox 0.72 drives it (adlib.cpp): YM3812Init(2, OPL2_INTERNAL_FREQ = 3600000, oplrate); oplrate defaults to 22050 in dosbox.conf.
// Register writes reach the chip at once, but samples are only drawn in OPL_CallBack, the mixer's 1 ms tick (22050 / 1000 = 22 samples,
// occasionally 23 from the mixer's fractional carry), so every write within a millisecond lands at the start of the next block:
// BlockSamples = 22. The output goes to AddSamples_m16 unscaled (mixer FM volume 100%).
//
// Faithful port: the flaws are KEPT, not fixed. Characteristic quirks:
//  - Clocked at 3.6 MHz (DOSBox's round number) instead of the AdLib's 3.579545 MHz: everything is ~0.57% sharp/fast.
//  - Runs at 22050 Hz with no resampling: the chip's 49716-ish Hz sample rate is approximated by bigger phase/EG/noise steps
//    (freqbase = 2.27), so high notes and noise alias and the EG takes 2-3 steps between output samples.
//  - Rhythm mode (BDh bit 5) is latched once per YM3812UpdateOne call: toggling it mid-block takes effect at the next 1 ms block.
//  - Modulator -> carrier path uses the modulator's PREVIOUS sample (op1_out[0]), a one-sample delay; feedback averages two samples.
//  - Key-on always resets the phase counter to 0 (click on retrigger); key-off of an attacking slot jumps straight to release.
//  - Waveform-select writes (E0h-F5h) are ignored entirely while reg 01h bit 5 is off, and kept after it is turned off.
//  - Timers are not run by the engine (DOSBox handles them via PIC events), so CSM mode (reg 08h bit 7) never key-ons here;
//    in the original its key-off is also immediate ("not implemented correctly yet").
//  - Status reads fake timer overflow by counting reads (TC = T*20) - kept in the port for completeness, unused by the card.
//  - Vibrato counter lfo_pm_cnt is never wrapped (relies on 32-bit overflow, whose period is not a multiple of 8 steps -> a glitch
//    every ~2^32 / lfo_pm_inc samples).
//  - Hard clip at +/-32767 on the 9-channel sum, no headroom scaling; rhythm voices are doubled (x2).
//  - Noise bit taken one shift ahead of the real register (the documented "trick"), noise reset to 1.

using System;

namespace NesEmulator.X86;

public sealed class Dosbox072OplEngine : IOplEngine
{
    public string Id => "D072";
    public int Rate => 22050;
    public int BlockSamples => 22;

    private const int Clock = 3600000;

    private const int FREQ_SH = 16, EG_SH = 16, LFO_SH = 24;
    private const uint FREQ_MASK = (1u << FREQ_SH) - 1;
    private const int ENV_BITS = 10;
    private const double ENV_STEP = 128.0 / (1 << ENV_BITS);
    private const int MAX_ATT_INDEX = (1 << (ENV_BITS - 1)) - 1;
    private const int MIN_ATT_INDEX = 0;
    private const int SIN_BITS = 10, SIN_LEN = 1 << SIN_BITS, SIN_MASK = SIN_LEN - 1;
    private const int TL_RES_LEN = 256;
    private const int TL_TAB_LEN = 12 * 2 * TL_RES_LEN;
    private const uint ENV_QUIET = TL_TAB_LEN >> 4;
    private const int EG_ATT = 4, EG_DEC = 3, EG_SUS = 2, EG_REL = 1, EG_OFF = 0;
    private const int RATE_STEPS = 8;
    private const int LFO_AM_TAB_ELEMENTS = 210;

    private sealed class Slot
    {
        public uint ar, dr, rr; public byte KSR, ksl, ksr, mul;
        public uint Cnt, Incr; public byte FB; public bool connectOut; public int[] op1_out = new int[2]; public byte CON;
        public byte eg_type, state; public uint TL; public int TLL, volume; public uint sl;
        public byte eg_sh_ar, eg_sel_ar, eg_sh_dr, eg_sel_dr, eg_sh_rr, eg_sel_rr;
        public uint key; public uint AMmask; public byte vib; public uint wavetable;
    }
    private sealed class Channel { public Slot[] SLOT = { new Slot(), new Slot() }; public uint block_fnum, fc, ksl_base; public byte kcode; }

    // ---- static tables ----
    private static readonly int[] slot_array = { 0, 2, 4, 1, 3, 5, -1, -1, 6, 8, 10, 7, 9, 11, -1, -1, 12, 14, 16, 13, 15, 17, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1 };
    private static readonly double[] ksl_db = {
        0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,
        0,0,0,0,0,0,0,0,0,0.750,1.125,1.500,1.875,2.250,2.625,3.000,
        0,0,0,0,0,1.125,1.875,2.625,3.000,3.750,4.125,4.500,4.875,5.250,5.625,6.000,
        0,0,0,1.875,3.000,4.125,4.875,5.625,6.000,6.750,7.125,7.500,7.875,8.250,8.625,9.000,
        0,0,3.000,4.875,6.000,7.125,7.875,8.625,9.000,9.750,10.125,10.500,10.875,11.250,11.625,12.000,
        0,3.000,6.000,7.875,9.000,10.125,10.875,11.625,12.000,12.750,13.125,13.500,13.875,14.250,14.625,15.000,
        0,6.000,9.000,10.875,12.000,13.125,13.875,14.625,15.000,15.750,16.125,16.500,16.875,17.250,17.625,18.000,
        0,9.000,12.000,13.875,15.000,16.125,16.875,17.625,18.000,18.750,19.125,19.500,19.875,20.250,20.625,21.000 };
    private static readonly uint[] ksl_tab = new uint[128];
    private static readonly uint[] sl_tab = new uint[16];
    private static readonly byte[] eg_inc = {
        0,1,0,1,0,1,0,1, 0,1,0,1,1,1,0,1, 0,1,1,1,0,1,1,1, 0,1,1,1,1,1,1,1,
        1,1,1,1,1,1,1,1, 1,1,1,2,1,1,1,2, 1,2,1,2,1,2,1,2, 1,2,2,2,1,2,2,2,
        2,2,2,2,2,2,2,2, 2,2,2,4,2,2,2,4, 2,4,2,4,2,4,2,4, 2,4,4,4,2,4,4,4,
        4,4,4,4,4,4,4,4, 8,8,8,8,8,8,8,8, 0,0,0,0,0,0,0,0 };
    private static readonly byte[] eg_rate_select = new byte[16 + 64 + 16];
    private static readonly byte[] eg_rate_shift = new byte[16 + 64 + 16];
    private static readonly byte[] mul_tab = { 1, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 20, 24, 24, 30, 30 };
    private static readonly int[] tl_tab = new int[TL_TAB_LEN];
    private static readonly uint[] sin_tab = new uint[SIN_LEN * 4];
    private static readonly byte[] lfo_am_table = new byte[LFO_AM_TAB_ELEMENTS];
    private static readonly sbyte[] lfo_pm_table = {
        0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0,
        0,0,0,0,0,0,0,0, 1,0,0,0,-1,0,0,0,
        1,0,0,0,-1,0,0,0, 2,1,0,-1,-2,-1,0,1,
        1,0,0,0,-1,0,0,0, 3,1,0,-1,-3,-1,0,1,
        2,1,0,-1,-2,-1,0,1, 4,2,0,-2,-4,-2,0,2,
        2,1,0,-1,-2,-1,0,1, 5,2,0,-2,-5,-2,0,2,
        3,1,0,-1,-3,-1,0,1, 6,3,0,-3,-6,-3,0,3,
        3,1,0,-1,-3,-1,0,1, 7,3,0,-3,-7,-3,0,3 };

    static Dosbox072OplEngine()
    {
        for (int i = 0; i < 128; i++) ksl_tab[i] = (uint)(ksl_db[i] / (0.1875 / 2.0));
        for (int i = 0; i < 16; i++) sl_tab[i] = (uint)((i == 15 ? 31 : i) * (2.0 / ENV_STEP));
        int k = 0;
        for (int i = 0; i < 16; i++) eg_rate_select[k++] = 14 * RATE_STEPS;
        for (int i = 0; i < 13; i++) for (int j = 0; j < 4; j++) eg_rate_select[k++] = (byte)(j * RATE_STEPS);
        for (int j = 0; j < 4; j++) eg_rate_select[k++] = (byte)((4 + j) * RATE_STEPS);
        for (int j = 0; j < 4; j++) eg_rate_select[k++] = (byte)((8 + j) * RATE_STEPS);
        while (k < eg_rate_select.Length) eg_rate_select[k++] = 12 * RATE_STEPS;
        k = 16;
        for (int i = 0; i < 13; i++) for (int j = 0; j < 4; j++) eg_rate_shift[k++] = (byte)(12 - i);
        k = 0;
        for (int i = 0; i < 7; i++) lfo_am_table[k++] = 0;
        for (int v = 1; v <= 25; v++) for (int j = 0; j < 4; j++) lfo_am_table[k++] = (byte)v;
        for (int j = 0; j < 3; j++) lfo_am_table[k++] = 26;
        for (int v = 25; v >= 1; v--) for (int j = 0; j < 4; j++) lfo_am_table[k++] = (byte)v;
        InitTables();
    }

    private static void InitTables()
    {
        for (int x = 0; x < TL_RES_LEN; x++)
        {
            double m = (1 << 16) / Math.Pow(2.0, (x + 1) * (ENV_STEP / 4.0) / 8.0);
            m = Math.Floor(m);
            int n = (int)m;
            n >>= 4;
            if ((n & 1) != 0) n = (n >> 1) + 1; else n >>= 1;
            n <<= 1;
            tl_tab[x * 2 + 0] = n;
            tl_tab[x * 2 + 1] = -tl_tab[x * 2 + 0];
            for (int i = 1; i < 12; i++)
            {
                tl_tab[x * 2 + 0 + i * 2 * TL_RES_LEN] = tl_tab[x * 2 + 0] >> i;
                tl_tab[x * 2 + 1 + i * 2 * TL_RES_LEN] = -tl_tab[x * 2 + 0 + i * 2 * TL_RES_LEN];
            }
        }
        for (int i = 0; i < SIN_LEN; i++)
        {
            double m = Math.Sin(((i * 2) + 1) * Math.PI / SIN_LEN);
            double o = m > 0.0 ? 8 * Math.Log(1.0 / m) / Math.Log(2.0) : 8 * Math.Log(-1.0 / m) / Math.Log(2.0);
            o = o / (ENV_STEP / 4);
            int n = (int)(2.0 * o);
            if ((n & 1) != 0) n = (n >> 1) + 1; else n >>= 1;
            sin_tab[i] = (uint)(n * 2 + (m >= 0.0 ? 0 : 1));
        }
        for (int i = 0; i < SIN_LEN; i++)
        {
            sin_tab[1 * SIN_LEN + i] = (i & (1 << (SIN_BITS - 1))) != 0 ? TL_TAB_LEN : sin_tab[i];
            sin_tab[2 * SIN_LEN + i] = sin_tab[i & (SIN_MASK >> 1)];
            sin_tab[3 * SIN_LEN + i] = (i & (1 << (SIN_BITS - 2))) != 0 ? TL_TAB_LEN : sin_tab[i & (SIN_MASK >> 2)];
        }
    }

    // ---- chip state (the original's FM_OPL plus its file-scope globals) ----
    private Channel[] P_CH = null!;
    private uint eg_cnt, eg_timer, eg_timer_add, eg_timer_overflow;
    private byte rhythm;
    private readonly uint[] fn_tab = new uint[1024];
    private byte lfo_am_depth, lfo_pm_depth_range;
    private uint lfo_am_cnt, lfo_am_inc, lfo_pm_cnt, lfo_pm_inc;
    private uint noise_rng, noise_p, noise_f;
    private byte wavesel;
    private readonly int[] T = new int[2], TC = new int[2];
    private readonly byte[] st = new byte[2];
    private byte address, status, statusmask, mode;
    private double freqbase, TimerBase;
    private int phase_modulation, output0;
    private uint LFO_AM; private int LFO_PM;

    public Dosbox072OplEngine() => Reset();

    public void Reset()
    {
        // OPLCreate (memset 0) + OPL_initalize + OPLResetChip; the globals start at 0 too.
        P_CH = new Channel[9];
        for (int i = 0; i < 9; i++) P_CH[i] = new Channel();
        eg_cnt = eg_timer = 0; rhythm = 0; lfo_am_depth = lfo_pm_depth_range = 0; lfo_am_cnt = lfo_pm_cnt = 0;
        noise_p = 0; wavesel = 0; T[0] = T[1] = TC[0] = TC[1] = 0; st[0] = st[1] = 0;
        address = status = statusmask = mode = 0; phase_modulation = output0 = 0; LFO_AM = 0; LFO_PM = 0;
        Initialize();
        ResetChip();
    }

    private void Initialize()
    {
        freqbase = ((double)Clock / 72.0) / Rate;
        TimerBase = 1.0 / ((double)Clock / 72.0);
        for (int i = 0; i < 1024; i++) fn_tab[i] = (uint)((double)i * 64 * freqbase * (1 << (FREQ_SH - 10)));
        lfo_am_inc = (uint)((1.0 / 64.0) * (1 << LFO_SH) * freqbase);
        lfo_pm_inc = (uint)((1.0 / 1024.0) * (1 << LFO_SH) * freqbase);
        noise_f = (uint)((1.0 / 1.0) * (1 << FREQ_SH) * freqbase);
        eg_timer_add = (uint)((1 << EG_SH) * freqbase);
        eg_timer_overflow = 1 * (1 << EG_SH);
    }

    private void ResetChip()
    {
        eg_timer = 0; eg_cnt = 0;
        noise_rng = 1; mode = 0;
        StatusReset(0x7f);
        WriteReg(0x01, 0); WriteReg(0x02, 0); WriteReg(0x03, 0); WriteReg(0x04, 0);
        for (int i = 0xff; i >= 0x20; i--) WriteReg(i, 0);
        for (int c = 0; c < 9; c++)
            for (int s = 0; s < 2; s++)
            {
                var sl = P_CH[c].SLOT[s];
                sl.wavetable = 0; sl.state = EG_OFF; sl.volume = MAX_ATT_INDEX;
            }
    }

    private void StatusSet(int flag)
    {
        status |= (byte)flag;
        if ((status & 0x80) == 0 && (status & statusmask) != 0) status |= 0x80;
    }
    private void StatusReset(int flag)
    {
        status &= (byte)~flag;
        if ((status & 0x80) != 0 && (status & statusmask) == 0) status &= 0x7f;
    }
    private void StatusMaskSet(int flag) { statusmask = (byte)flag; StatusSet(0); StatusReset(0); }

    /// <summary>The original's status-port read (fakes timer overflow by counting reads). Unused by the card, kept for completeness.</summary>
    public byte ReadStatus()
    {
        if (st[0] != 0) { if (TC[0] != 0) TC[0]--; else { TC[0] = T[0] * 20; StatusSet(0x40); } }
        if (st[1] != 0) { if (TC[1] != 0) TC[1]--; else { TC[1] = T[1] * 20; StatusSet(0x40); } }
        return (byte)((status & (statusmask | 0x80)) | 0x06);
    }

    /// <summary>The interval the original hands DOSBox's TimerHandler for timer <paramref name="c"/> (seconds). Unused by the card.</summary>
    public double TimerInterval(int c) => T[c & 1] * TimerBase;

    private void AdvanceLfo()
    {
        unchecked
        {
            lfo_am_cnt += lfo_am_inc;
            if (lfo_am_cnt >= ((uint)LFO_AM_TAB_ELEMENTS << LFO_SH)) lfo_am_cnt -= ((uint)LFO_AM_TAB_ELEMENTS << LFO_SH);
            byte tmp = lfo_am_table[lfo_am_cnt >> LFO_SH];
            LFO_AM = lfo_am_depth != 0 ? tmp : (uint)(tmp >> 2);
            lfo_pm_cnt += lfo_pm_inc;
            LFO_PM = (int)((lfo_pm_cnt >> LFO_SH) & 7) | lfo_pm_depth_range;
        }
    }

    private void Advance()
    {
        unchecked
        {
            eg_timer += eg_timer_add;
            while (eg_timer >= eg_timer_overflow)
            {
                eg_timer -= eg_timer_overflow;
                eg_cnt++;
                for (int i = 0; i < 9 * 2; i++)
                {
                    var op = P_CH[i / 2].SLOT[i & 1];
                    switch (op.state)
                    {
                        case EG_ATT:
                            if ((eg_cnt & ((1u << op.eg_sh_ar) - 1)) == 0)
                            {
                                op.volume += (~op.volume * eg_inc[op.eg_sel_ar + ((eg_cnt >> op.eg_sh_ar) & 7)]) >> 3;
                                if (op.volume <= MIN_ATT_INDEX) { op.volume = MIN_ATT_INDEX; op.state = EG_DEC; }
                            }
                            break;
                        case EG_DEC:
                            if ((eg_cnt & ((1u << op.eg_sh_dr) - 1)) == 0)
                            {
                                op.volume += eg_inc[op.eg_sel_dr + ((eg_cnt >> op.eg_sh_dr) & 7)];
                                if ((uint)op.volume >= op.sl) op.state = EG_SUS;
                            }
                            break;
                        case EG_SUS:
                            if (op.eg_type == 0)
                            {
                                if ((eg_cnt & ((1u << op.eg_sh_rr) - 1)) == 0)
                                {
                                    op.volume += eg_inc[op.eg_sel_rr + ((eg_cnt >> op.eg_sh_rr) & 7)];
                                    if (op.volume >= MAX_ATT_INDEX) op.volume = MAX_ATT_INDEX;
                                }
                            }
                            break;
                        case EG_REL:
                            if ((eg_cnt & ((1u << op.eg_sh_rr) - 1)) == 0)
                            {
                                op.volume += eg_inc[op.eg_sel_rr + ((eg_cnt >> op.eg_sh_rr) & 7)];
                                if (op.volume >= MAX_ATT_INDEX) { op.volume = MAX_ATT_INDEX; op.state = EG_OFF; }
                            }
                            break;
                    }
                }
            }
            for (int i = 0; i < 9 * 2; i++)
            {
                var CH = P_CH[i / 2];
                var op = CH.SLOT[i & 1];
                if (op.vib != 0)
                {
                    uint block_fnum = CH.block_fnum;
                    uint fnum_lfo = (block_fnum & 0x0380) >> 7;
                    int off = lfo_pm_table[LFO_PM + 16 * (int)fnum_lfo];
                    if (off != 0)
                    {
                        block_fnum = (uint)((int)block_fnum + off);
                        int block = (int)((block_fnum & 0x1c00) >> 10);
                        op.Cnt += (fn_tab[block_fnum & 0x03ff] >> (7 - block)) * op.mul;
                    }
                    else op.Cnt += op.Incr;
                }
                else op.Cnt += op.Incr;
            }
            noise_p += noise_f;
            int n = (int)(noise_p >> FREQ_SH);
            noise_p &= FREQ_MASK;
            while (n != 0)
            {
                if ((noise_rng & 1) != 0) noise_rng ^= 0x800302;
                noise_rng >>= 1;
                n--;
            }
        }
    }

    private static int OpCalc(uint phase, uint env, int pm, uint wave_tab)
    {
        unchecked
        {
            uint p = (env << 4) + sin_tab[wave_tab + (uint)((((int)((phase & ~FREQ_MASK) + (uint)(pm << 16))) >> FREQ_SH) & SIN_MASK)];
            return p >= TL_TAB_LEN ? 0 : tl_tab[p];
        }
    }
    private static int OpCalc1(uint phase, uint env, int pm, uint wave_tab)
    {
        unchecked
        {
            uint p = (env << 4) + sin_tab[wave_tab + (uint)((((int)((phase & ~FREQ_MASK) + (uint)pm)) >> FREQ_SH) & SIN_MASK)];
            return p >= TL_TAB_LEN ? 0 : tl_tab[p];
        }
    }

    private uint VolumeCalc(Slot op) => unchecked((uint)op.TLL + (uint)op.volume + (LFO_AM & op.AMmask));

    private void CalcCh(Channel CH)
    {
        phase_modulation = 0;
        var SLOT = CH.SLOT[0];
        uint env = VolumeCalc(SLOT);
        int @out = SLOT.op1_out[0] + SLOT.op1_out[1];
        SLOT.op1_out[0] = SLOT.op1_out[1];
        if (SLOT.connectOut) output0 += SLOT.op1_out[0]; else phase_modulation += SLOT.op1_out[0];
        SLOT.op1_out[1] = 0;
        if (env < ENV_QUIET)
        {
            if (SLOT.FB == 0) @out = 0;
            SLOT.op1_out[1] = OpCalc1(SLOT.Cnt, env, @out << SLOT.FB, SLOT.wavetable);
        }
        SLOT = CH.SLOT[1];
        env = VolumeCalc(SLOT);
        if (env < ENV_QUIET) output0 += OpCalc(SLOT.Cnt, env, phase_modulation, SLOT.wavetable);
    }

    private void CalcRh(uint noise)
    {
        phase_modulation = 0;
        var SLOT = P_CH[6].SLOT[0];
        uint env = VolumeCalc(SLOT);
        int @out = SLOT.op1_out[0] + SLOT.op1_out[1];
        SLOT.op1_out[0] = SLOT.op1_out[1];
        if (SLOT.CON == 0) phase_modulation = SLOT.op1_out[0];
        SLOT.op1_out[1] = 0;
        if (env < ENV_QUIET)
        {
            if (SLOT.FB == 0) @out = 0;
            SLOT.op1_out[1] = OpCalc1(SLOT.Cnt, env, @out << SLOT.FB, SLOT.wavetable);
        }
        SLOT = P_CH[6].SLOT[1];
        env = VolumeCalc(SLOT);
        if (env < ENV_QUIET) output0 += OpCalc(SLOT.Cnt, env, phase_modulation, SLOT.wavetable) * 2;

        Slot S71 = P_CH[7].SLOT[0], S72 = P_CH[7].SLOT[1], S81 = P_CH[8].SLOT[0], S82 = P_CH[8].SLOT[1];

        env = VolumeCalc(S71);
        if (env < ENV_QUIET)
        {
            uint bit7 = ((S71.Cnt >> FREQ_SH) >> 7) & 1, bit3 = ((S71.Cnt >> FREQ_SH) >> 3) & 1, bit2 = ((S71.Cnt >> FREQ_SH) >> 2) & 1;
            uint res1 = (bit2 ^ bit7) | bit3;
            uint phase = res1 != 0 ? (0x200u | (0xd0 >> 2)) : 0xd0u;
            uint bit5e = ((S82.Cnt >> FREQ_SH) >> 5) & 1, bit3e = ((S82.Cnt >> FREQ_SH) >> 3) & 1;
            uint res2 = bit3e ^ bit5e;
            if (res2 != 0) phase = 0x200u | (0xd0 >> 2);
            if ((phase & 0x200) != 0) { if (noise != 0) phase = 0x200 | 0xd0; }
            else { if (noise != 0) phase = 0xd0 >> 2; }
            output0 += OpCalc(phase << FREQ_SH, env, 0, S71.wavetable) * 2;
        }
        env = VolumeCalc(S72);
        if (env < ENV_QUIET)
        {
            uint bit8 = ((S71.Cnt >> FREQ_SH) >> 8) & 1;
            uint phase = bit8 != 0 ? 0x200u : 0x100u;
            if (noise != 0) phase ^= 0x100;
            output0 += OpCalc(phase << FREQ_SH, env, 0, S72.wavetable) * 2;
        }
        env = VolumeCalc(S81);
        if (env < ENV_QUIET) output0 += OpCalc(S81.Cnt, env, 0, S81.wavetable) * 2;
        env = VolumeCalc(S82);
        if (env < ENV_QUIET)
        {
            uint bit7 = ((S71.Cnt >> FREQ_SH) >> 7) & 1, bit3 = ((S71.Cnt >> FREQ_SH) >> 3) & 1, bit2 = ((S71.Cnt >> FREQ_SH) >> 2) & 1;
            uint res1 = (bit2 ^ bit7) | bit3;
            uint phase = res1 != 0 ? 0x300u : 0x100u;
            uint bit5e = ((S82.Cnt >> FREQ_SH) >> 5) & 1, bit3e = ((S82.Cnt >> FREQ_SH) >> 3) & 1;
            if ((bit3e ^ bit5e) != 0) phase = 0x300;
            output0 += OpCalc(phase << FREQ_SH, env, 0, S82.wavetable) * 2;
        }
    }

    private static void KeyOn(Slot s, uint key_set)
    {
        if (s.key == 0) { s.Cnt = 0; s.state = EG_ATT; }
        s.key |= key_set;
    }
    private static void KeyOff(Slot s, uint key_clr)
    {
        if (s.key != 0)
        {
            s.key &= key_clr;
            if (s.key == 0 && s.state > EG_REL) s.state = EG_REL;
        }
    }

    private static void CalcFcSlot(Channel CH, Slot SLOT)
    {
        SLOT.Incr = unchecked(CH.fc * SLOT.mul);
        int ksr = CH.kcode >> SLOT.KSR;
        if (SLOT.ksr != ksr)
        {
            SLOT.ksr = (byte)ksr;
            if ((SLOT.ar + SLOT.ksr) < 16 + 62)
            {
                SLOT.eg_sh_ar = eg_rate_shift[SLOT.ar + SLOT.ksr];
                SLOT.eg_sel_ar = eg_rate_select[SLOT.ar + SLOT.ksr];
            }
            else { SLOT.eg_sh_ar = 0; SLOT.eg_sel_ar = 13 * RATE_STEPS; }
            SLOT.eg_sh_dr = eg_rate_shift[SLOT.dr + SLOT.ksr];
            SLOT.eg_sel_dr = eg_rate_select[SLOT.dr + SLOT.ksr];
            SLOT.eg_sh_rr = eg_rate_shift[SLOT.rr + SLOT.ksr];
            SLOT.eg_sel_rr = eg_rate_select[SLOT.rr + SLOT.ksr];
        }
    }

    private Slot SlotOf(int slot, out Channel CH) { CH = P_CH[slot / 2]; return CH.SLOT[slot & 1]; }

    private void SetMul(int slot, int v)
    {
        var SLOT = SlotOf(slot, out var CH);
        SLOT.mul = mul_tab[v & 0x0f];
        SLOT.KSR = (byte)((v & 0x10) != 0 ? 0 : 2);
        SLOT.eg_type = (byte)(v & 0x20);
        SLOT.vib = (byte)(v & 0x40);
        SLOT.AMmask = (v & 0x80) != 0 ? ~0u : 0u;
        CalcFcSlot(CH, SLOT);
    }
    private void SetKslTl(int slot, int v)
    {
        var SLOT = SlotOf(slot, out var CH);
        int ksl = v >> 6;
        SLOT.ksl = (byte)(ksl != 0 ? 3 - ksl : 31);
        SLOT.TL = (uint)((v & 0x3f) << (ENV_BITS - 1 - 7));
        SLOT.TLL = (int)(SLOT.TL + (CH.ksl_base >> SLOT.ksl));
    }
    private void SetArDr(int slot, int v)
    {
        var SLOT = SlotOf(slot, out _);
        SLOT.ar = (v >> 4) != 0 ? (uint)(16 + ((v >> 4) << 2)) : 0;
        if ((SLOT.ar + SLOT.ksr) < 16 + 62)
        {
            SLOT.eg_sh_ar = eg_rate_shift[SLOT.ar + SLOT.ksr];
            SLOT.eg_sel_ar = eg_rate_select[SLOT.ar + SLOT.ksr];
        }
        else { SLOT.eg_sh_ar = 0; SLOT.eg_sel_ar = 13 * RATE_STEPS; }
        SLOT.dr = (v & 0x0f) != 0 ? (uint)(16 + ((v & 0x0f) << 2)) : 0;
        SLOT.eg_sh_dr = eg_rate_shift[SLOT.dr + SLOT.ksr];
        SLOT.eg_sel_dr = eg_rate_select[SLOT.dr + SLOT.ksr];
    }
    private void SetSlRr(int slot, int v)
    {
        var SLOT = SlotOf(slot, out _);
        SLOT.sl = sl_tab[v >> 4];
        SLOT.rr = (v & 0x0f) != 0 ? (uint)(16 + ((v & 0x0f) << 2)) : 0;
        SLOT.eg_sh_rr = eg_rate_shift[SLOT.rr + SLOT.ksr];
        SLOT.eg_sel_rr = eg_rate_select[SLOT.rr + SLOT.ksr];
    }

    public void Write(int reg, byte value) { address = (byte)reg; WriteReg(address, value); }

    private void WriteReg(int r, int v)
    {
        r &= 0xff; v &= 0xff;
        int slot;
        switch (r & 0xe0)
        {
            case 0x00:
                switch (r & 0x1f)
                {
                    case 0x01: wavesel = (byte)(v & 0x20); break;
                    case 0x02: T[0] = (256 - v) * 4; break;
                    case 0x03: T[1] = (256 - v) * 16; break;
                    case 0x04:
                        if ((v & 0x80) != 0) StatusReset(0x7f);
                        else
                        {
                            st[0] = (byte)(v & 1); st[1] = (byte)((v >> 1) & 1);
                            StatusReset(v & 0x78);
                            StatusMaskSet((~v) & 0x78);
                            if (st[0] != 0) TC[0] = T[0] * 20; // TimerHandler: DOSBox's PIC event, not part of the engine
                            if (st[1] != 0) TC[1] = T[1] * 20;
                        }
                        break;
                    case 0x08: mode = (byte)v; break;
                }
                break;
            case 0x20: slot = slot_array[r & 0x1f]; if (slot < 0) return; SetMul(slot, v); break;
            case 0x40: slot = slot_array[r & 0x1f]; if (slot < 0) return; SetKslTl(slot, v); break;
            case 0x60: slot = slot_array[r & 0x1f]; if (slot < 0) return; SetArDr(slot, v); break;
            case 0x80: slot = slot_array[r & 0x1f]; if (slot < 0) return; SetSlRr(slot, v); break;
            case 0xa0:
            {
                if (r == 0xbd)
                {
                    lfo_am_depth = (byte)(v & 0x80);
                    lfo_pm_depth_range = (byte)((v & 0x40) != 0 ? 8 : 0);
                    rhythm = (byte)(v & 0x3f);
                    if ((rhythm & 0x20) != 0)
                    {
                        if ((v & 0x10) != 0) { KeyOn(P_CH[6].SLOT[0], 2); KeyOn(P_CH[6].SLOT[1], 2); }
                        else { KeyOff(P_CH[6].SLOT[0], ~2u); KeyOff(P_CH[6].SLOT[1], ~2u); }
                        if ((v & 0x01) != 0) KeyOn(P_CH[7].SLOT[0], 2); else KeyOff(P_CH[7].SLOT[0], ~2u);
                        if ((v & 0x08) != 0) KeyOn(P_CH[7].SLOT[1], 2); else KeyOff(P_CH[7].SLOT[1], ~2u);
                        if ((v & 0x04) != 0) KeyOn(P_CH[8].SLOT[0], 2); else KeyOff(P_CH[8].SLOT[0], ~2u);
                        if ((v & 0x02) != 0) KeyOn(P_CH[8].SLOT[1], 2); else KeyOff(P_CH[8].SLOT[1], ~2u);
                    }
                    else
                    {
                        KeyOff(P_CH[6].SLOT[0], ~2u); KeyOff(P_CH[6].SLOT[1], ~2u);
                        KeyOff(P_CH[7].SLOT[0], ~2u); KeyOff(P_CH[7].SLOT[1], ~2u);
                        KeyOff(P_CH[8].SLOT[0], ~2u); KeyOff(P_CH[8].SLOT[1], ~2u);
                    }
                    return;
                }
                if ((r & 0x0f) > 8) return;
                var CH = P_CH[r & 0x0f];
                uint block_fnum;
                if ((r & 0x10) == 0) block_fnum = (CH.block_fnum & 0x1f00) | (uint)v;
                else
                {
                    block_fnum = (uint)((v & 0x1f) << 8) | (CH.block_fnum & 0xff);
                    if ((v & 0x20) != 0) { KeyOn(CH.SLOT[0], 1); KeyOn(CH.SLOT[1], 1); }
                    else { KeyOff(CH.SLOT[0], ~1u); KeyOff(CH.SLOT[1], ~1u); }
                }
                if (CH.block_fnum != block_fnum)
                {
                    int block = (int)(block_fnum >> 10);
                    CH.block_fnum = block_fnum;
                    CH.ksl_base = ksl_tab[block_fnum >> 6];
                    CH.fc = fn_tab[block_fnum & 0x03ff] >> (7 - block);
                    CH.kcode = (byte)((CH.block_fnum & 0x1c00) >> 9);
                    if ((mode & 0x40) != 0) CH.kcode |= (byte)((CH.block_fnum & 0x100) >> 8);
                    else CH.kcode |= (byte)((CH.block_fnum & 0x200) >> 9);
                    CH.SLOT[0].TLL = (int)(CH.SLOT[0].TL + (CH.ksl_base >> CH.SLOT[0].ksl));
                    CH.SLOT[1].TLL = (int)(CH.SLOT[1].TL + (CH.ksl_base >> CH.SLOT[1].ksl));
                    CalcFcSlot(CH, CH.SLOT[0]);
                    CalcFcSlot(CH, CH.SLOT[1]);
                }
                break;
            }
            case 0xc0:
            {
                if ((r & 0x0f) > 8) return;
                var CH = P_CH[r & 0x0f];
                CH.SLOT[0].FB = (byte)(((v >> 1) & 7) != 0 ? ((v >> 1) & 7) + 7 : 0);
                CH.SLOT[0].CON = (byte)(v & 1);
                CH.SLOT[0].connectOut = CH.SLOT[0].CON != 0;
                break;
            }
            case 0xe0:
                if (wavesel != 0)
                {
                    slot = slot_array[r & 0x1f];
                    if (slot < 0) return;
                    P_CH[slot / 2].SLOT[slot & 1].wavetable = (uint)((v & 0x03) * SIN_LEN);
                }
                break;
        }
    }

    /// <summary>YM3812UpdateOne: rhythm mode is latched for the whole call, as in the original.</summary>
    public void Generate(short[] dest, int count)
    {
        bool rh = (rhythm & 0x20) != 0;
        for (int i = 0; i < count; i++)
        {
            output0 = 0;
            AdvanceLfo();
            for (int c = 0; c < 6; c++) CalcCh(P_CH[c]);
            if (!rh) { CalcCh(P_CH[6]); CalcCh(P_CH[7]); CalcCh(P_CH[8]); }
            else CalcRh(noise_rng & 1);
            int lt = output0;
            if (lt > 32767) lt = 32767; else if (lt < -32768) lt = -32768;
            dest[i] = (short)lt;
            Advance();
        }
    }
}
