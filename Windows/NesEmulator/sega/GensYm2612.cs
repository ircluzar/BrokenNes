using System;

namespace NesEmulator.Sega;

// =====================================================================================================================
//  Gens 2.10 YM2612 emulator, ported to C#.
//
//  Source: Gens 2.10 (2002), Gens210Src.zip, file Gens/ym2612.c (+ ym2612.h), and the way Gens drives it
//  (Gens.cpp: per-scanline YM2612_DacAndTimers_Update / YM_Len, G_dsound.cpp: Sound_Extrapol, Write_Sound_Stereo;
//  save.cpp: the default settings). https://sourceforge.net/projects/gens/files/OldFiles/Gens210Src.zip
//  Copyright (c) 2002 Stephane Dallongeville. The files carry no license line; the SourceForge Gens project states
//  GPL-2.0. This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2
//  and docs/projects/foreign-apu-project.md.
//
//  A quick port. Gens' shipped defaults are used: Sound_Rate 22050, YM2612_Improv = 0 (no interpolation: the chip is
//  computed straight at 22,050 Hz, so the _Int update variants are not ported), DAC_Enable = 1. Quirks kept:
//   - the whole chip runs at the output rate (Frequence = clock / rate / 144), table-driven, 12-bit sine/env
//   - envelope: x^8 attack curve, linear decay, 96 dB range, SL steps of 3 dB (not 6), output cut at 78 dB
//   - attack/decay rate constants AR_RATE 399128 / DR_RATE 5514396; release uses DR_TAB[(RR << 2) | 2]
//   - feedback 0 is still feedback (FB = 9 - fb: "Real thing ?"), SSG-EG ignored, the slot 2/3 swap ("Stupid typo")
//   - key-on from release maps the decay level back onto the attack curve, masked by ChgEnM (the "Ecco 2" fix):
//     an algorithm change zeroes ChgEnM so the next key-on restarts the envelope from silence
//   - register writes of the same value to $30+ are dropped (REG cache); a few writes flush the pending samples first
//     (YM2612_Special_Update), the rest take effect at the next flush
//   - the DAC ((data - $80) << 7) is added once per scanline slice, sample-and-hold over the scanline; channel 6 is
//     silenced while the DAC is on; channel output limited to +-1.5x in algorithms 4-7 only
//   - LFO FM is crude (Finc * lfo >> 9), AM table 11.8 dB; timers count per scanline (TimerBase * samples)
// =====================================================================================================================

/// <summary>The Gens 2.10 YM2612: <see cref="Write"/> the ports, <see cref="Update"/> renders added into int buffers at the chosen rate.</summary>
public sealed class GensYm2612
{
    // ---- ym2612.c defines
    private const int ATTACK = 0, DECAY = 1, SUBSTAIN = 2, RELEASE = 3;
    private const int SIN_HBITS = 12, SIN_LBITS = 26 - SIN_HBITS;
    private const int ENV_HBITS = 12, ENV_LBITS = 28 - ENV_HBITS;
    private const int LFO_HBITS = 10, LFO_LBITS = 28 - LFO_HBITS;
    private const int SIN_LENGHT = 1 << SIN_HBITS, ENV_LENGHT = 1 << ENV_HBITS, LFO_LENGHT = 1 << LFO_HBITS;
    private const int TL_LENGHT = ENV_LENGHT * 3;
    private const int SIN_MASK = SIN_LENGHT - 1, ENV_MASK = ENV_LENGHT - 1, LFO_MASK = LFO_LENGHT - 1;
    private const double ENV_STEP = 96.0 / ENV_LENGHT;
    private const int ENV_ATTACK = (ENV_LENGHT * 0) << ENV_LBITS;
    private const int ENV_DECAY = (ENV_LENGHT * 1) << ENV_LBITS;
    private const int ENV_END = (ENV_LENGHT * 2) << ENV_LBITS;
    private const int MAX_OUT_BITS = SIN_HBITS + SIN_LBITS + 2;
    private const int MAX_OUT = (1 << MAX_OUT_BITS) - 1;
    private const int OUTPUT_BITS = 16;
    private const int OUT_BITS = OUTPUT_BITS - 2;
    private const int OUT_SHIFT = MAX_OUT_BITS - OUT_BITS;
    private const int LIMIT_CH_OUT = (int)(((1 << OUT_BITS) * 1.5) - 1);
    private static readonly int PG_CUT_OFF = (int)(78.0 / ENV_STEP);
    private const int AR_RATE = 399128, DR_RATE = 5514396;
    private const int LFO_FMS_LBITS = 9;
    private static readonly int LFO_FMS_BASE = (int)(0.05946309436 * 0.0338 * (double)(1 << LFO_FMS_LBITS));
    private const int S0 = 0, S1 = 2, S2 = 1, S3 = 3;   // Stupid typo of the YM2612
    public const int MAX_UPDATE_LENGHT = 2000;

    // ---- tables (per instance: they depend on the rate)
    private readonly int[] SIN_TAB = new int[SIN_LENGHT];          // offsets into TL_TAB (the C keeps pointers)
    private readonly int[] TL_TAB = new int[TL_LENGHT * 2];
    private readonly int[] ENV_TAB = new int[2 * ENV_LENGHT + 8];
    private readonly int[] DECAY_TO_ATTACK = new int[ENV_LENGHT];
    private readonly int[] FINC_TAB = new int[2048];
    private readonly int[] AR_TAB = new int[128];
    private readonly int[] DR_TAB = new int[96];
    private readonly int[][] DT_TAB = new int[8][];
    private readonly int[] SL_TAB = new int[16];
    private readonly int[] NULL_RATE = new int[32];
    private readonly int[] LFO_ENV_TAB = new int[LFO_LENGHT];
    private readonly int[] LFO_FREQ_TAB = new int[LFO_LENGHT];
    private readonly int[] LFO_ENV_UP = new int[MAX_UPDATE_LENGHT];
    private readonly int[] LFO_FREQ_UP = new int[MAX_UPDATE_LENGHT];
    private readonly int[] LFO_INC_TAB = new int[8];

    private static readonly int[] DT_DEF_TAB =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 6, 6, 7, 8, 8, 8, 8,
        1, 1, 1, 1, 2, 2, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 6, 6, 7, 8, 8, 9, 10, 11, 12, 13, 14, 16, 16, 16, 16,
        2, 2, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 6, 6, 7, 8, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 20, 22, 22, 22, 22,
    };
    private static readonly int[] FKEY_TAB = { 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 3, 3, 3, 3, 3, 3 };
    private static readonly int[] LFO_AMS_TAB = { 31, 4, 1, 0 };
    private readonly int[] LFO_FMS_TAB;

    private sealed class Slot
    {
        public int[] DT = null!; public int MUL, TL, TLL, SLL, KSR_S, KSR;
        public int[] AR = null!, DR = null!, SR = null!, RR = null!;   // table + offset (the C keeps pointers)
        public int ARo, DRo, SRo, RRo;
        public int Fcnt, Finc, Ecurp, Ecnt, Einc, Ecmp, EincA, EincD, EincS, EincR;
        public int ChgEnM, AMS, AMSon;
    }

    private sealed class Channel
    {
        public readonly int[] S0_OUT = new int[4];
        public int Old_OUTd, OUTd, LEFT, RIGHT, ALGO, FB, FMS, AMS;
        public readonly int[] FNUM = new int[4], FOCT = new int[4], KC = new int[4];
        public readonly Slot[] SLOT = { new(), new(), new(), new() };
    }

    // ---- struct ym2612__
    private readonly int Clock, Rate;
    private readonly int TimerBase;
    public int Status;
    private int OPNAadr, OPNBadr, LFOcnt, LFOinc, TimerA, TimerAL, TimerAcnt, TimerB, TimerBL, TimerBcnt, Mode, DAC, DACdata;
    private readonly double Frequence;
    private readonly int Inter_Step;
    private readonly Channel[] CHANNEL = { new(), new(), new(), new(), new(), new() };
    private readonly int[][] REG = { new int[0x100], new int[0x100] };

    private int in0, in1, in2, in3, en0, en1, en2, en3;

    // ---- Gens' globals around the chip: the segment buffer the pending samples go to (YM_Buf / YM_Len)
    public bool Enable = true;      // YM2612_Enable (default 1)
    public bool DacEnable = true;   // DAC_Enable (default 1)
    public int[] BufL = Array.Empty<int>(), BufR = Array.Empty<int>();
    public int BufOff;              // YM_Buf = Seg + BufOff
    public int Len;                 // YM_Len

    public GensYm2612(int clock, int rate, bool interpolation = false)
    {
        LFO_FMS_TAB = new[] { LFO_FMS_BASE * 0, LFO_FMS_BASE * 1, LFO_FMS_BASE * 2, LFO_FMS_BASE * 3, LFO_FMS_BASE * 4, LFO_FMS_BASE * 6, LFO_FMS_BASE * 12, LFO_FMS_BASE * 24 };
        int i, j; double x;
        Clock = clock; Rate = rate;
        Frequence = ((double)Clock / Rate) / 144.0;
        TimerBase = (int)(Frequence * 4096.0);
        if (interpolation && Frequence > 1.0) throw new NotSupportedException("Gens' YM2612_Improv mode is not ported (its default is off)");
        Inter_Step = 0x4000;

        for (i = 0; i < TL_LENGHT; i++)
        {
            if (i >= PG_CUT_OFF) TL_TAB[TL_LENGHT + i] = TL_TAB[i] = 0;
            else
            {
                x = MAX_OUT;
                x /= Math.Pow(10, (ENV_STEP * i) / 20);
                TL_TAB[i] = (int)x;
                TL_TAB[TL_LENGHT + i] = -TL_TAB[i];
            }
        }

        SIN_TAB[0] = SIN_TAB[SIN_LENGHT / 2] = PG_CUT_OFF;
        for (i = 1; i <= SIN_LENGHT / 4; i++)
        {
            x = Math.Sin(2.0 * Math.PI * i / SIN_LENGHT);
            x = 20 * Math.Log10(1 / x);
            j = (int)(x / ENV_STEP);
            if (j > PG_CUT_OFF) j = PG_CUT_OFF;
            SIN_TAB[i] = SIN_TAB[(SIN_LENGHT / 2) - i] = j;
            SIN_TAB[(SIN_LENGHT / 2) + i] = SIN_TAB[SIN_LENGHT - i] = TL_LENGHT + j;
        }

        for (i = 0; i < LFO_LENGHT; i++)
        {
            x = Math.Sin(2.0 * Math.PI * i / LFO_LENGHT);
            x += 1.0; x /= 2.0; x *= 11.8 / ENV_STEP;
            LFO_ENV_TAB[i] = (int)x;
            x = Math.Sin(2.0 * Math.PI * i / LFO_LENGHT);
            x *= (double)((1 << (LFO_HBITS - 1)) - 1);
            LFO_FREQ_TAB[i] = (int)x;
        }

        for (i = 0; i < ENV_LENGHT; i++)
        {
            x = Math.Pow((double)((ENV_LENGHT - 1) - i) / ENV_LENGHT, 8); x *= ENV_LENGHT;
            ENV_TAB[i] = (int)x;
            x = Math.Pow((double)i / ENV_LENGHT, 1); x *= ENV_LENGHT;
            ENV_TAB[ENV_LENGHT + i] = (int)x;
        }
        ENV_TAB[ENV_END >> ENV_LBITS] = ENV_LENGHT - 1;

        for (i = 0, j = ENV_LENGHT - 1; i < ENV_LENGHT; i++)
        {
            while (j != 0 && ENV_TAB[j] < i) j--;
            DECAY_TO_ATTACK[i] = j << ENV_LBITS;
        }

        for (i = 0; i < 15; i++)
        {
            x = i * 3; x /= ENV_STEP;
            j = (int)x; j <<= ENV_LBITS;
            SL_TAB[i] = j + ENV_DECAY;
        }
        j = ENV_LENGHT - 1; j <<= ENV_LBITS; SL_TAB[15] = j + ENV_DECAY;

        for (i = 0; i < 2048; i++)
        {
            x = i * Frequence;
            x *= (double)(1 << (SIN_LBITS + SIN_HBITS - (21 - 7)));
            x /= 2.0;
            FINC_TAB[i] = (int)(uint)x;
        }

        for (i = 0; i < 4; i++) { AR_TAB[i] = 0; DR_TAB[i] = 0; }
        for (i = 0; i < 60; i++)
        {
            x = Frequence;
            x *= 1.0 + ((i & 3) * 0.25);
            x *= (double)(1 << (i >> 2));
            x *= (double)(ENV_LENGHT << ENV_LBITS);
            AR_TAB[i + 4] = (int)(uint)(x / AR_RATE);
            DR_TAB[i + 4] = (int)(uint)(x / DR_RATE);
        }
        for (i = 64; i < 96; i++) { AR_TAB[i] = AR_TAB[63]; DR_TAB[i] = DR_TAB[63]; NULL_RATE[i - 64] = 0; }

        for (i = 0; i < 8; i++) DT_TAB[i] = new int[32];
        for (i = 0; i < 4; i++)
            for (j = 0; j < 32; j++)
            {
                x = (double)DT_DEF_TAB[(i << 5) + j] * Frequence * (double)(1 << (SIN_LBITS + SIN_HBITS - 21));
                DT_TAB[i + 0][j] = (int)x;
                DT_TAB[i + 4][j] = (int)-x;
            }

        j = (int)((long)Rate * Inter_Step / 0x4000);
        double[] lfoHz = { 3.98, 5.56, 6.02, 6.37, 6.88, 9.63, 48.1, 72.2 };
        for (i = 0; i < 8; i++) LFO_INC_TAB[i] = (int)(uint)(lfoHz[i] * (double)(1 << (LFO_HBITS + LFO_LBITS)) / j);

        foreach (var ch in CHANNEL) foreach (var sl in ch.SLOT) { sl.DT = DT_TAB[0]; sl.AR = sl.DR = sl.SR = sl.RR = NULL_RATE; }
        Reset();
    }

    // =================================================================================================================
    //  fonctions calcul param
    // =================================================================================================================

    private void CALC_FINC_SL(Slot SL, int finc, int kc)
    {
        SL.Finc = (finc + SL.DT[kc]) * SL.MUL;
        int ksr = kc >> SL.KSR_S;
        if (SL.KSR != ksr)
        {
            SL.KSR = ksr;
            SL.EincA = SL.AR[SL.ARo + ksr];
            SL.EincD = SL.DR[SL.DRo + ksr];
            SL.EincS = SL.SR[SL.SRo + ksr];
            SL.EincR = SL.RR[SL.RRo + ksr];
            if (SL.Ecurp == ATTACK) SL.Einc = SL.EincA;
            else if (SL.Ecurp == DECAY) SL.Einc = SL.EincD;
            else if (SL.Ecnt < ENV_END)
            {
                if (SL.Ecurp == SUBSTAIN) SL.Einc = SL.EincS;
                else if (SL.Ecurp == RELEASE) SL.Einc = SL.EincR;
            }
        }
    }

    private void CALC_FINC_CH(Channel CH)
    {
        int finc = FINC_TAB[CH.FNUM[0]] >> (7 - CH.FOCT[0]);
        int kc = CH.KC[0];
        CALC_FINC_SL(CH.SLOT[0], finc, kc);
        CALC_FINC_SL(CH.SLOT[1], finc, kc);
        CALC_FINC_SL(CH.SLOT[2], finc, kc);
        CALC_FINC_SL(CH.SLOT[3], finc, kc);
    }

    // =================================================================================================================
    //  fonctions setting
    // =================================================================================================================

    private void KEY_ON(Channel CH, int nsl)
    {
        Slot SL = CH.SLOT[nsl];
        if (SL.Ecurp == RELEASE)
        {
            SL.Fcnt = 0;
            // Fix Ecco 2 splash sound
            SL.Ecnt = (DECAY_TO_ATTACK[ENV_TAB[SL.Ecnt >> ENV_LBITS]] + ENV_ATTACK) & SL.ChgEnM;
            SL.ChgEnM = unchecked((int)0xFFFFFFFF);
            SL.Einc = SL.EincA;
            SL.Ecmp = ENV_DECAY;
            SL.Ecurp = ATTACK;
        }
    }

    private void KEY_OFF(Channel CH, int nsl)
    {
        Slot SL = CH.SLOT[nsl];
        if (SL.Ecurp != RELEASE)
        {
            if (SL.Ecnt < ENV_DECAY) SL.Ecnt = (ENV_TAB[SL.Ecnt >> ENV_LBITS] << ENV_LBITS) + ENV_DECAY;
            SL.Einc = SL.EincR;
            SL.Ecmp = ENV_END;
            SL.Ecurp = RELEASE;
        }
    }

    private void CSM_Key_Control()
    {
        KEY_ON(CHANNEL[2], 0); KEY_ON(CHANNEL[2], 1); KEY_ON(CHANNEL[2], 2); KEY_ON(CHANNEL[2], 3);
    }

    private int SLOT_SET(int Adr, int data)
    {
        int nch, nsl;
        if ((nch = Adr & 3) == 3) return 1;
        nsl = (Adr >> 2) & 3;
        if ((Adr & 0x100) != 0) nch += 3;
        Channel CH = CHANNEL[nch];
        Slot SL = CH.SLOT[nsl];

        switch (Adr & 0xF0)
        {
            case 0x30:
                if ((SL.MUL = data & 0x0F) != 0) SL.MUL <<= 1;
                else SL.MUL = 1;
                SL.DT = DT_TAB[(data >> 4) & 7];
                CH.SLOT[0].Finc = -1;
                break;
            case 0x40:
                SL.TL = data & 0x7F;
                // SOR2 do a lot of TL adjustement and this fix R.Shinobi jump sound...
                SpecialUpdate();
                SL.TLL = SL.TL << (ENV_HBITS - 7);
                break;
            case 0x50:
                SL.KSR_S = 3 - (data >> 6);
                CH.SLOT[0].Finc = -1;
                if ((data &= 0x1F) != 0) { SL.AR = AR_TAB; SL.ARo = data << 1; }
                else { SL.AR = NULL_RATE; SL.ARo = 0; }
                SL.EincA = SL.AR[SL.ARo + SL.KSR];
                if (SL.Ecurp == ATTACK) SL.Einc = SL.EincA;
                break;
            case 0x60:
                if ((SL.AMSon = data & 0x80) != 0) SL.AMS = CH.AMS;
                else SL.AMS = 31;
                if ((data &= 0x1F) != 0) { SL.DR = DR_TAB; SL.DRo = data << 1; }
                else { SL.DR = NULL_RATE; SL.DRo = 0; }
                SL.EincD = SL.DR[SL.DRo + SL.KSR];
                if (SL.Ecurp == DECAY) SL.Einc = SL.EincD;
                break;
            case 0x70:
                if ((data &= 0x1F) != 0) { SL.SR = DR_TAB; SL.SRo = data << 1; }
                else { SL.SR = NULL_RATE; SL.SRo = 0; }
                SL.EincS = SL.SR[SL.SRo + SL.KSR];
                if (SL.Ecurp == SUBSTAIN && SL.Ecnt < ENV_END) SL.Einc = SL.EincS;
                break;
            case 0x80:
                SL.SLL = SL_TAB[data >> 4];
                SL.RR = DR_TAB; SL.RRo = ((data & 0xF) << 2) | 2;
                SL.EincR = SL.RR[SL.RRo + SL.KSR];
                if (SL.Ecurp == RELEASE && SL.Ecnt < ENV_END) SL.Einc = SL.EincR;
                break;
            case 0x90:  // SSG-EG ... not supported
                break;
        }
        return 0;
    }

    private void SetAms(Slot s, Channel CH) { s.AMS = s.AMSon != 0 ? CH.AMS : 31; }

    private int CHANNEL_SET(int Adr, int data)
    {
        Channel CH;
        int num;
        if ((num = Adr & 3) == 3) return 1;

        switch (Adr & 0xFC)
        {
            case 0xA0:
                if ((Adr & 0x100) != 0) num += 3;
                CH = CHANNEL[num];
                SpecialUpdate();
                CH.FNUM[0] = (CH.FNUM[0] & 0x700) + data;
                CH.KC[0] = (CH.FOCT[0] << 2) | FKEY_TAB[CH.FNUM[0] >> 7];
                CH.SLOT[0].Finc = -1;
                break;
            case 0xA4:
                if ((Adr & 0x100) != 0) num += 3;
                CH = CHANNEL[num];
                SpecialUpdate();
                CH.FNUM[0] = (CH.FNUM[0] & 0x0FF) + ((data & 0x07) << 8);
                CH.FOCT[0] = (data & 0x38) >> 3;
                CH.KC[0] = (CH.FOCT[0] << 2) | FKEY_TAB[CH.FNUM[0] >> 7];
                CH.SLOT[0].Finc = -1;
                break;
            case 0xA8:
                if (Adr < 0x100)
                {
                    num++;
                    SpecialUpdate();
                    var c2 = CHANNEL[2];
                    c2.FNUM[num] = (c2.FNUM[num] & 0x700) + data;
                    c2.KC[num] = (c2.FOCT[num] << 2) | FKEY_TAB[c2.FNUM[num] >> 7];
                    c2.SLOT[0].Finc = -1;
                }
                break;
            case 0xAC:
                if (Adr < 0x100)
                {
                    num++;
                    SpecialUpdate();
                    var c2 = CHANNEL[2];
                    c2.FNUM[num] = (c2.FNUM[num] & 0x0FF) + ((data & 0x07) << 8);
                    c2.FOCT[num] = (data & 0x38) >> 3;
                    c2.KC[num] = (c2.FOCT[num] << 2) | FKEY_TAB[c2.FNUM[num] >> 7];
                    c2.SLOT[0].Finc = -1;
                }
                break;
            case 0xB0:
                if ((Adr & 0x100) != 0) num += 3;
                CH = CHANNEL[num];
                if (CH.ALGO != (data & 7))
                {
                    // Fix VectorMan 2 heli sound (level 1)
                    SpecialUpdate();
                    CH.ALGO = data & 7;
                    CH.SLOT[0].ChgEnM = 0; CH.SLOT[1].ChgEnM = 0; CH.SLOT[2].ChgEnM = 0; CH.SLOT[3].ChgEnM = 0;
                }
                CH.FB = 9 - ((data >> 3) & 7);   // Real thing ?
                break;
            case 0xB4:
                if ((Adr & 0x100) != 0) num += 3;
                CH = CHANNEL[num];
                SpecialUpdate();
                CH.LEFT = (data & 0x80) != 0 ? -1 : 0;
                CH.RIGHT = (data & 0x40) != 0 ? -1 : 0;
                CH.AMS = LFO_AMS_TAB[(data >> 4) & 3];
                CH.FMS = LFO_FMS_TAB[data & 7];
                SetAms(CH.SLOT[0], CH); SetAms(CH.SLOT[1], CH); SetAms(CH.SLOT[2], CH); SetAms(CH.SLOT[3], CH);
                break;
        }
        return 0;
    }

    private int YM_SET(int Adr, int data)
    {
        Channel CH;
        int nch;
        switch (Adr)
        {
            case 0x22:
                if ((data & 8) != 0) LFOinc = LFO_INC_TAB[data & 7];   // Cool Spot music 1 ...
                else LFOinc = LFOcnt = 0;
                break;
            case 0x24:
                TimerA = (TimerA & 0x003) | (data << 2);
                if (TimerAL != (1024 - TimerA) << 12) TimerAcnt = TimerAL = (1024 - TimerA) << 12;
                break;
            case 0x25:
                TimerA = (TimerA & 0x3fc) | (data & 3);
                if (TimerAL != (1024 - TimerA) << 12) TimerAcnt = TimerAL = (1024 - TimerA) << 12;
                break;
            case 0x26:
                TimerB = data;
                if (TimerBL != (256 - TimerB) << (4 + 12)) TimerBcnt = TimerBL = (256 - TimerB) << (4 + 12);
                break;
            case 0x27:
                if (((data ^ Mode) & 0x40) != 0)
                {
                    // This fix the punch sound in Street of Rage 2
                    SpecialUpdate();
                    CHANNEL[2].SLOT[0].Finc = -1;
                }
                Status &= (~data >> 4) & (data >> 2);   // Reset Status
                Mode = data;
                break;
            case 0x28:
                if ((nch = data & 3) == 3) return 1;
                if ((data & 4) != 0) nch += 3;
                CH = CHANNEL[nch];
                SpecialUpdate();
                if ((data & 0x10) != 0) KEY_ON(CH, S0); else KEY_OFF(CH, S0);
                if ((data & 0x20) != 0) KEY_ON(CH, S1); else KEY_OFF(CH, S1);
                if ((data & 0x40) != 0) KEY_ON(CH, S2); else KEY_OFF(CH, S2);
                if ((data & 0x80) != 0) KEY_ON(CH, S3); else KEY_OFF(CH, S3);
                break;
            case 0x2A:
                DACdata = (data - 0x80) << 7;
                break;
            case 0x2B:
                if ((DAC ^ (data & 0x80)) != 0) SpecialUpdate();
                DAC = data & 0x80;
                break;
        }
        return 0;
    }

    // =================================================================================================================
    //  fonctions de generation
    // =================================================================================================================

    private void ENV_NEXT_EVENT(Slot SL)
    {
        switch (SL.Ecurp)
        {
            case ATTACK: SL.Ecnt = ENV_DECAY; SL.Einc = SL.EincD; SL.Ecmp = SL.SLL; SL.Ecurp = DECAY; break;
            case DECAY: SL.Ecnt = SL.SLL; SL.Einc = SL.EincS; SL.Ecmp = ENV_END; SL.Ecurp = SUBSTAIN; break;
            case SUBSTAIN:
            case RELEASE: SL.Ecnt = ENV_END; SL.Einc = 0; SL.Ecmp = ENV_END + 1; break;
        }
    }

    private int SIN(int phase, int env) => TL_TAB[SIN_TAB[(phase >> SIN_LBITS) & SIN_MASK] + env];

    /// <summary>One channel through one of the sixteen non-interpolated Update_Chan_AlgoN[_LFO] routines.</summary>
    private void UpdateChan(Channel CH, bool lfo, int[] bufL, int[] bufR, int off, int lenght)
    {
        var s = CH.SLOT;
        int algo = CH.ALGO;
        bool end0 = s[S0].Ecnt == ENV_END, end1 = s[S1].Ecnt == ENV_END, end2 = s[S2].Ecnt == ENV_END, end3 = s[S3].Ecnt == ENV_END;
        switch (algo)
        {
            case 0: case 1: case 2: case 3: if (end3) return; break;
            case 4: if (end1 && end3) return; break;
            case 5: case 6: if (end1 && end2 && end3) return; break;
            case 7: if (end0 && end1 && end2 && end3) return; break;
        }

        for (int i = 0; i < lenght; i++)
        {
            // GET_CURRENT_PHASE(_LFO)
            int freq_LFO;
            if (lfo && (freq_LFO = (CH.FMS * LFO_FREQ_UP[i]) >> (LFO_HBITS - 1)) != 0)
            {
                in0 = (s[S0].Fcnt += s[S0].Finc + ((s[S0].Finc * freq_LFO) >> LFO_FMS_LBITS));
                in1 = (s[S1].Fcnt += s[S1].Finc + ((s[S1].Finc * freq_LFO) >> LFO_FMS_LBITS));
                in2 = (s[S2].Fcnt += s[S2].Finc + ((s[S2].Finc * freq_LFO) >> LFO_FMS_LBITS));
                in3 = (s[S3].Fcnt += s[S3].Finc + ((s[S3].Finc * freq_LFO) >> LFO_FMS_LBITS));
            }
            else
            {
                in0 = (s[S0].Fcnt += s[S0].Finc);
                in1 = (s[S1].Fcnt += s[S1].Finc);
                in2 = (s[S2].Fcnt += s[S2].Finc);
                in3 = (s[S3].Fcnt += s[S3].Finc);
            }

            // GET_CURRENT_ENV(_LFO)
            en0 = ENV_TAB[s[S0].Ecnt >> ENV_LBITS] + s[S0].TLL;
            en1 = ENV_TAB[s[S1].Ecnt >> ENV_LBITS] + s[S1].TLL;
            en2 = ENV_TAB[s[S2].Ecnt >> ENV_LBITS] + s[S2].TLL;
            en3 = ENV_TAB[s[S3].Ecnt >> ENV_LBITS] + s[S3].TLL;
            if (lfo)
            {
                int env_LFO = LFO_ENV_UP[i];
                en0 += env_LFO >> s[S0].AMS; en1 += env_LFO >> s[S1].AMS; en2 += env_LFO >> s[S2].AMS; en3 += env_LFO >> s[S3].AMS;
            }

            // UPDATE_ENV
            if ((s[S0].Ecnt += s[S0].Einc) >= s[S0].Ecmp) ENV_NEXT_EVENT(s[S0]);
            if ((s[S1].Ecnt += s[S1].Einc) >= s[S1].Ecmp) ENV_NEXT_EVENT(s[S1]);
            if ((s[S2].Ecnt += s[S2].Einc) >= s[S2].Ecmp) ENV_NEXT_EVENT(s[S2]);
            if ((s[S3].Ecnt += s[S3].Einc) >= s[S3].Ecmp) ENV_NEXT_EVENT(s[S3]);

            // DO_FEEDBACK
            in0 += (CH.S0_OUT[0] + CH.S0_OUT[1]) >> CH.FB;
            CH.S0_OUT[1] = CH.S0_OUT[0];
            CH.S0_OUT[0] = SIN(in0, en0);

            switch (algo)
            {
                case 0:
                    in1 += CH.S0_OUT[0];
                    in2 += SIN(in1, en1);
                    in3 += SIN(in2, en2);
                    CH.OUTd = SIN(in3, en3) >> OUT_SHIFT;
                    break;
                case 1:
                    in2 += CH.S0_OUT[0] + SIN(in1, en1);
                    in3 += SIN(in2, en2);
                    CH.OUTd = SIN(in3, en3) >> OUT_SHIFT;
                    break;
                case 2:
                    in2 += SIN(in1, en1);
                    in3 += CH.S0_OUT[0] + SIN(in2, en2);
                    CH.OUTd = SIN(in3, en3) >> OUT_SHIFT;
                    break;
                case 3:
                    in1 += CH.S0_OUT[0];
                    in3 += SIN(in1, en1) + SIN(in2, en2);
                    CH.OUTd = SIN(in3, en3) >> OUT_SHIFT;
                    break;
                case 4:
                    in1 += CH.S0_OUT[0];
                    in3 += SIN(in2, en2);
                    CH.OUTd = (SIN(in3, en3) + SIN(in1, en1)) >> OUT_SHIFT;
                    Limit(CH);
                    break;
                case 5:
                    in1 += CH.S0_OUT[0];
                    in2 += CH.S0_OUT[0];
                    in3 += CH.S0_OUT[0];
                    CH.OUTd = (SIN(in3, en3) + SIN(in1, en1) + SIN(in2, en2)) >> OUT_SHIFT;
                    Limit(CH);
                    break;
                case 6:
                    in1 += CH.S0_OUT[0];
                    CH.OUTd = (SIN(in3, en3) + SIN(in1, en1) + SIN(in2, en2)) >> OUT_SHIFT;
                    Limit(CH);
                    break;
                default:
                    CH.OUTd = (SIN(in3, en3) + SIN(in1, en1) + SIN(in2, en2) + CH.S0_OUT[0]) >> OUT_SHIFT;
                    Limit(CH);
                    break;
            }

            // DO_OUTPUT
            bufL[off + i] += CH.OUTd & CH.LEFT;
            bufR[off + i] += CH.OUTd & CH.RIGHT;
        }
    }

    private static void Limit(Channel CH)
    {
        if (CH.OUTd > LIMIT_CH_OUT) CH.OUTd = LIMIT_CH_OUT;
        else if (CH.OUTd < -LIMIT_CH_OUT) CH.OUTd = -LIMIT_CH_OUT;
    }

    // =================================================================================================================
    //  fonctions publiques
    // =================================================================================================================

    public void Reset()
    {
        int i, j;
        LFOcnt = 0; TimerA = 0; TimerAL = 0; TimerAcnt = 0; TimerB = 0; TimerBL = 0; TimerBcnt = 0;
        DAC = 0; DACdata = 0; Status = 0; OPNAadr = 0; OPNBadr = 0;
        for (i = 0; i < 6; i++)
        {
            var ch = CHANNEL[i];
            ch.Old_OUTd = 0; ch.OUTd = 0; ch.LEFT = -1; ch.RIGHT = -1; ch.ALGO = 0; ch.FB = 31; ch.FMS = 0; ch.AMS = 0;
            for (j = 0; j < 4; j++)
            {
                ch.S0_OUT[j] = 0; ch.FNUM[j] = 0; ch.FOCT[j] = 0; ch.KC[j] = 0;
                var sl = ch.SLOT[j];
                sl.Fcnt = 0; sl.Finc = 0; sl.Ecnt = ENV_END; sl.Einc = 0; sl.Ecmp = 0; sl.Ecurp = RELEASE; sl.ChgEnM = 0;
            }
        }
        for (i = 0; i < 0x100; i++) { REG[0][i] = -1; REG[1][i] = -1; }
        for (i = 0xB6; i >= 0xB4; i--) { Write(0, (byte)i); Write(2, (byte)i); Write(1, 0xC0); Write(3, 0xC0); }
        for (i = 0xB2; i >= 0x22; i--) { Write(0, (byte)i); Write(2, (byte)i); Write(1, 0); Write(3, 0); }
        Write(0, 0x2A); Write(1, 0x80);
    }

    public int Read() => Status;

    public int Write(int adr, byte data)
    {
        int d;
        adr &= 3;
        switch (adr)
        {
            case 0: OPNAadr = data; break;
            case 1:
                if (OPNAadr == 0x2A) { DACdata = (data - 0x80) << 7; return 0; }   // Trivial optimisation
                d = OPNAadr & 0xF0;
                if (d >= 0x30)
                {
                    if (REG[0][OPNAadr] == data) return 2;
                    REG[0][OPNAadr] = data;
                    if (d < 0xA0) SLOT_SET(OPNAadr, data);
                    else CHANNEL_SET(OPNAadr, data);
                }
                else
                {
                    REG[0][OPNAadr] = data;
                    YM_SET(OPNAadr, data);
                }
                break;
            case 2: OPNBadr = data; break;
            case 3:
                d = OPNBadr & 0xF0;
                if (d >= 0x30)
                {
                    if (REG[1][OPNBadr] == data) return 2;
                    REG[1][OPNBadr] = data;
                    if (d < 0xA0) SLOT_SET(OPNBadr + 0x100, data);
                    else CHANNEL_SET(OPNBadr + 0x100, data);
                }
                else return 1;
                break;
        }
        return 0;
    }

    /// <summary>YM2612_Update: adds <paramref name="length"/> samples into the buffers at <paramref name="off"/>.</summary>
    public void Update(int[] bufL, int[] bufR, int off, int length)
    {
        int i, j;
        if (CHANNEL[0].SLOT[0].Finc == -1) CALC_FINC_CH(CHANNEL[0]);
        if (CHANNEL[1].SLOT[0].Finc == -1) CALC_FINC_CH(CHANNEL[1]);
        if (CHANNEL[2].SLOT[0].Finc == -1)
        {
            var c = CHANNEL[2];
            if ((Mode & 0x40) != 0)
            {
                CALC_FINC_SL(c.SLOT[S0], FINC_TAB[c.FNUM[2]] >> (7 - c.FOCT[2]), c.KC[2]);
                CALC_FINC_SL(c.SLOT[S1], FINC_TAB[c.FNUM[3]] >> (7 - c.FOCT[3]), c.KC[3]);
                CALC_FINC_SL(c.SLOT[S2], FINC_TAB[c.FNUM[1]] >> (7 - c.FOCT[1]), c.KC[1]);
                CALC_FINC_SL(c.SLOT[S3], FINC_TAB[c.FNUM[0]] >> (7 - c.FOCT[0]), c.KC[0]);
            }
            else CALC_FINC_CH(c);
        }
        if (CHANNEL[3].SLOT[0].Finc == -1) CALC_FINC_CH(CHANNEL[3]);
        if (CHANNEL[4].SLOT[0].Finc == -1) CALC_FINC_CH(CHANNEL[4]);
        if (CHANNEL[5].SLOT[0].Finc == -1) CALC_FINC_CH(CHANNEL[5]);

        bool lfo = false;
        if (LFOinc != 0)
        {
            for (i = 0; i < length; i++)
            {
                j = ((LFOcnt += LFOinc) >> LFO_LBITS) & LFO_MASK;
                LFO_ENV_UP[i] = LFO_ENV_TAB[j];
                LFO_FREQ_UP[i] = LFO_FREQ_TAB[j];
            }
            lfo = true;
        }

        UpdateChan(CHANNEL[0], lfo, bufL, bufR, off, length);
        UpdateChan(CHANNEL[1], lfo, bufL, bufR, off, length);
        UpdateChan(CHANNEL[2], lfo, bufL, bufR, off, length);
        UpdateChan(CHANNEL[3], lfo, bufL, bufR, off, length);
        UpdateChan(CHANNEL[4], lfo, bufL, bufR, off, length);
        if (DAC == 0) UpdateChan(CHANNEL[5], lfo, bufL, bufR, off, length);
    }

    /// <summary>YM2612_DacAndTimers_Update: called once per scanline with that line's slice of the segment.</summary>
    public void DacAndTimersUpdate(int[] bufL, int[] bufR, int off, int length)
    {
        int i;
        if (DAC != 0 && DACdata != 0 && DacEnable)
        {
            for (i = 0; i < length; i++)
            {
                bufL[off + i] += DACdata & CHANNEL[5].LEFT;
                bufR[off + i] += DACdata & CHANNEL[5].RIGHT;
            }
        }

        i = TimerBase * length;
        if ((Mode & 1) != 0)
        {
            if ((TimerAcnt -= i) <= 0)
            {
                Status |= (Mode & 0x04) >> 2;
                TimerAcnt += TimerAL;
                if ((Mode & 0x80) != 0) CSM_Key_Control();
            }
        }
        if ((Mode & 2) != 0)
        {
            if ((TimerBcnt -= i) <= 0)
            {
                Status |= (Mode & 0x08) >> 2;
                TimerBcnt += TimerBL;
            }
        }
    }

    /// <summary>YM2612_Special_Update: renders the pending scanlines (Len samples at BufOff) and moves the segment pointer past them.</summary>
    public void SpecialUpdate()
    {
        if (Len != 0 && Enable)
        {
            Update(BufL, BufR, BufOff, Len);
            BufOff += Len;
            Len = 0;
        }
    }
}
