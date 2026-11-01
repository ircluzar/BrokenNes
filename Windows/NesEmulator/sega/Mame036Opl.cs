using System;

namespace NesEmulator.Sega;

// =====================================================================================================================
//  MAME 0.36 YM3812 (OPL2) FM sound generator, ported to C#.
//
//  Source: MAME 0.36 (historic-mame, tag mame036), src/sound/fmopl.c + fmopl.h, "Version 0.36f"
//  (https://github.com/mamedev/historic-mame/tree/mame036/src/sound). Clock / rate as src/sound/3812intf.c sets them up.
//  Copyright (C) 1999 Tatsuyuki Satoh, MultiArcadeMachineEmurator development (and the MAME team).
//  License: the MAME license of the time (free, non-commercial use, source must accompany redistribution).
//  This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
//
//  A quick port (quick and dirty first, polish later); only the YM3812 path (no Y8950 DELTA-T, keyboard or I/O port).
//  The original's quirks are kept on purpose; they are the sound:
//   - log-table synthesis with a 2048-entry sine, 4096-step (0.0234 dB) envelope, attack curve = ((4095-i)/4096)^8
//   - LFO table pointers are cached on the first update and never refreshed (a later $BD AM/VIB depth change is not heard)
//   - vibrato multiplies the 32-bit phase increment by the LFO value before dividing (wraps on high notes)
//   - rhythm: snare plays through the hi-hat slot's phase, top cymbal and hi-hat both through the snare slot's phase, a
//     1-bit rand() "white noise" of 6 dB on the envelope; bass drum doubled, snare x8
//   - key-off jumps the envelope straight to the decay start (the "set from envelope output" line is commented out)
//   - the rate tables overrun by one entry (AR_TABLE[75] is DR_TABLE[0]): release rate 0 releases instantly at key scale 0
//   - timers are kept as registers only (nothing here needs the IRQ)
// =====================================================================================================================

/// <summary>MAME 0.36's fmopl.c YM3812 core: write registers, pull mono 16-bit samples at the rate it was created with.</summary>
public sealed class Mame036Opl
{
    // ---- defines
    private const int OPL_ARRATE = 141280;
    private const int OPL_DRRATE = 1956000;
    private const int FREQ_BITS = 24;
    private const int FREQ_RATE = 1 << (FREQ_BITS - 20);
    private const int TL_BITS = FREQ_BITS + 2;
    private const int OPL_OUTSB = TL_BITS + 3 - 16;
    private const int OPL_MAXOUT = 0x7fff << OPL_OUTSB;
    private const int OPL_MINOUT = -0x8000 << OPL_OUTSB;
    private const int SIN_ENT = 2048;
    private const int ENV_BITS = 16;
    private const int EG_ENT = 4096;
    private const int EG_OFF = (2 * EG_ENT) << ENV_BITS;
    private const int EG_DED = EG_OFF;
    private const int EG_DST = EG_ENT << ENV_BITS;
    private const int EG_AED = EG_DST;
    private const int EG_AST = 0;
    private const double EG_STEP = 96.0 / EG_ENT;
    private const int VIB_ENT = 512, VIB_SHIFT = 32 - 9, AMS_ENT = 512, AMS_SHIFT = 32 - 9;
    private const int VIB_RATE = 256;
    private const int SLOT1 = 0, SLOT2 = 1;
    private const int ENV_MOD_RR = 0, ENV_MOD_DR = 1, ENV_MOD_AR = 2;
    private const int TL_MAX = EG_ENT * 2;

    private static readonly int[] slot_array =
    {
         0, 2, 4, 1, 3, 5,-1,-1,
         6, 8,10, 7, 9,11,-1,-1,
        12,14,16,13,15,17,-1,-1,
        -1,-1,-1,-1,-1,-1,-1,-1
    };

    private static readonly uint[] KSL_TABLE = BuildKsl();
    private static uint[] BuildKsl()
    {
        double ML = 0.1875 * 2 / EG_STEP;
        double[] t =
        {
             0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000,
             0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.750, 1.125, 1.500, 1.875, 2.250, 2.625, 3.000,
             0.000, 0.000, 0.000, 0.000, 0.000, 1.125, 1.875, 2.625, 3.000, 3.750, 4.125, 4.500, 4.875, 5.250, 5.625, 6.000,
             0.000, 0.000, 0.000, 1.875, 3.000, 4.125, 4.875, 5.625, 6.000, 6.750, 7.125, 7.500, 7.875, 8.250, 8.625, 9.000,
             0.000, 0.000, 3.000, 4.875, 6.000, 7.125, 7.875, 8.625, 9.000, 9.750,10.125,10.500,10.875,11.250,11.625,12.000,
             0.000, 3.000, 6.000, 7.875, 9.000,10.125,10.875,11.625,12.000,12.750,13.125,13.500,13.875,14.250,14.625,15.000,
             0.000, 6.000, 9.000,10.875,12.000,13.125,13.875,14.625,15.000,15.750,16.125,16.500,16.875,17.250,17.625,18.000,
             0.000, 9.000,12.000,13.875,15.000,16.125,16.875,17.625,18.000,18.750,19.125,19.500,19.875,20.250,20.625,21.000,
        };
        var r = new uint[t.Length];
        for (int i = 0; i < t.Length; i++) r[i] = (uint)(t[i] * ML);
        return r;
    }

    private static readonly int[] SL_TABLE = BuildSl();
    private static int[] BuildSl()
    {
        int[] db = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 31 };
        var r = new int[16];
        for (int i = 0; i < 16; i++) r[i] = (int)(db[i] * ((3 / EG_STEP) * (1 << ENV_BITS)) + EG_DST);
        return r;
    }

    private static readonly uint[] MUL_TABLE = { 1, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 20, 24, 24, 30, 30 };   // (x * 2), 1/2 -> 1
    private static readonly int[] RATE_0 = new int[16];

    // ---- shared tables (OPLOpenTable). SIN_TABLE holds offsets into TL_TABLE instead of pointers.
    private static readonly int[] TL_TABLE = new int[TL_MAX * 2];
    private static readonly int[] SIN_TABLE = new int[SIN_ENT * 4];
    private static readonly int[] AMS_TABLE = new int[AMS_ENT * 2];
    private static readonly int[] VIB_TABLE = new int[VIB_ENT * 2];
    private static readonly int[] ENV_CURVE = new int[2 * EG_ENT + 1];

    static Mame036Opl()
    {
        for (int t = 0; t < EG_ENT - 1; t++)
        {
            double rate = ((1 << TL_BITS) - 1) / Math.Pow(10, EG_STEP * t / 20);
            TL_TABLE[t] = (int)rate;
            TL_TABLE[TL_MAX + t] = -TL_TABLE[t];
        }
        for (int t = EG_ENT - 1; t < TL_MAX; t++) TL_TABLE[t] = TL_TABLE[TL_MAX + t] = 0;

        SIN_TABLE[0] = SIN_TABLE[SIN_ENT / 2] = EG_ENT - 1;
        for (int s = 1; s <= SIN_ENT / 4; s++)
        {
            double pom = Math.Sin(2 * Math.PI * s / SIN_ENT);
            pom = 20 * Math.Log10(1 / pom);
            int j = (int)(pom / EG_STEP);
            SIN_TABLE[s] = SIN_TABLE[SIN_ENT / 2 - s] = j;
            SIN_TABLE[SIN_ENT / 2 + s] = SIN_TABLE[SIN_ENT - s] = TL_MAX + j;
        }
        for (int s = 0; s < SIN_ENT; s++)
        {
            SIN_TABLE[SIN_ENT * 1 + s] = s < (SIN_ENT / 2) ? SIN_TABLE[s] : EG_ENT;
            SIN_TABLE[SIN_ENT * 2 + s] = SIN_TABLE[s % (SIN_ENT / 2)];
            SIN_TABLE[SIN_ENT * 3 + s] = ((s / (SIN_ENT / 4)) & 1) != 0 ? EG_ENT : SIN_TABLE[SIN_ENT * 2 + s];
        }
        for (int i = 0; i < EG_ENT; i++)
        {
            double pom = Math.Pow((double)(EG_ENT - 1 - i) / EG_ENT, 8) * EG_ENT;
            ENV_CURVE[i] = (int)pom;
            ENV_CURVE[(EG_DST >> ENV_BITS) + i] = i;
        }
        ENV_CURVE[EG_OFF >> ENV_BITS] = EG_ENT - 1;
        for (int i = 0; i < AMS_ENT; i++)
        {
            double pom = (1.0 + Math.Sin(2 * Math.PI * i / AMS_ENT)) / 2;
            AMS_TABLE[i] = (int)((1.0 / EG_STEP) * pom);
            AMS_TABLE[AMS_ENT + i] = (int)((4.8 / EG_STEP) * pom);
        }
        for (int i = 0; i < VIB_ENT; i++)
        {
            double pom = (double)VIB_RATE * 0.06 * Math.Sin(2 * Math.PI * i / VIB_ENT);
            VIB_TABLE[i] = (int)(VIB_RATE + (pom * 0.07));
            VIB_TABLE[VIB_ENT + i] = (int)(VIB_RATE + (pom * 0.14));
        }
    }

    // ---- state
    private sealed class Slot
    {
        public int TL, TLL;
        public byte KSR;
        public int[] AR = RATE_0, DR = RATE_0, RR = RATE_0;
        public int ARo, DRo, RRo;
        public int SL;
        public byte ksl, ksr;
        public uint mul, Cnt, Incr;
        public byte eg_typ, evm;
        public int evc, eve, evs, evsa, evsd, evsr;
        public byte ams, vib;
        public int wavetable;   // offset into SIN_TABLE
    }

    private sealed class Channel
    {
        public readonly Slot[] SLOT = { new Slot(), new Slot() };
        public byte CON, FB;
        public bool connect1Carrier;   // connect1 = CON ? carrier : feedback2
        public readonly int[] op1_out = new int[2];
        public uint block_fnum;
        public byte kcode;
        public uint fc, ksl_base;
        public byte keyon;
    }

    private readonly Channel[] P_CH = new Channel[9];
    private readonly int clock, rate;
    private readonly double freqbase;
    private byte address, status, statusmask, rythm, wavesel;
    private uint mode;
    private readonly int[] T = new int[2];
    private readonly byte[] st = new byte[2];
    private readonly int[] AR_TABLE = new int[76], DR_TABLE = new int[76];   // 75 in the original + the overrun slot (see InitTimeTables)
    private readonly uint[] FN_TABLE = new uint[1024];
    private int ams_table, vib_table;   // offsets into AMS_TABLE / VIB_TABLE
    private uint amsCnt, vibCnt;
    private readonly int amsIncrInit, vibIncrInit;

    // the "cur_chip" cache of YM3812UpdateOne: filled on the first update, never refreshed
    private bool cached;
    private int c_ams_table, c_vib_table, c_amsIncr, c_vibIncr;

    // per-sample work (file statics in the original)
    private int outd, ams, vib, feedback2;
    private uint randSeed = 1;

    public int Rate => rate;

    public Mame036Opl(int clock, int rate)
    {
        for (int c = 0; c < 9; c++) P_CH[c] = new Channel();
        this.clock = clock; this.rate = rate;
        // OPL_initalize
        freqbase = rate != 0 ? ((double)clock / rate) / 72 : 0;
        InitTimeTables();
        for (int fn = 0; fn < 1024; fn++) FN_TABLE[fn] = (uint)(freqbase * fn * FREQ_RATE * (1 << 7) / 2);
        amsIncrInit = rate != 0 ? (int)((double)AMS_ENT * (1 << AMS_SHIFT) / rate * 3.7 * ((double)clock / 3600000)) : 0;
        vibIncrInit = rate != 0 ? (int)((double)VIB_ENT * (1 << VIB_SHIFT) / rate * 6.4 * ((double)clock / 3600000)) : 0;
        ResetChip();
    }

    private void InitTimeTables()
    {
        for (int i = 0; i < 4; i++) AR_TABLE[i] = DR_TABLE[i] = 0;
        for (int i = 4; i <= 60; i++)
        {
            double r = freqbase;
            if (i < 60) r *= 1.0 + (i & 3) * 0.25;
            r *= 1 << ((i >> 2) - 1);
            r *= (double)(EG_ENT << ENV_BITS);
            AR_TABLE[i] = (int)(r / OPL_ARRATE);
            DR_TABLE[i] = (int)(r / OPL_DRRATE);
        }
        for (int i = 60; i < 76; i++) { AR_TABLE[i] = EG_AED - 1; DR_TABLE[i] = DR_TABLE[60]; }
        // the original's AR_TABLE/DR_TABLE are 75 entries and this loop runs to 75: AR_TABLE[75] lands on DR_TABLE[0] (so a release
        // rate of 0 at key scale 0 releases instantly) and DR_TABLE[75] on FN_TABLE[0] (rewritten to 0 straight after). Kept.
        DR_TABLE[0] = EG_AED - 1;
        DR_TABLE[75] = 0;
    }

    private static int Limit(int val, int max, int min) => val > max ? max : val < min ? min : val;

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

    private static void KeyOn(Slot s) { s.Cnt = 0; s.evm = ENV_MOD_AR; s.evs = s.evsa; s.evc = EG_AST; s.eve = EG_AED; }
    private static void KeyOff(Slot s)
    {
        if (s.evm > ENV_MOD_RR)
        {
            s.evm = ENV_MOD_RR;
            if ((s.evc & EG_DST) == 0) s.evc = EG_DST;
            s.eve = EG_DED;
            s.evs = s.evsr;
        }
    }

    private uint CalcSlot(Slot s)
    {
        if ((s.evc += s.evs) >= s.eve)
        {
            switch (s.evm)
            {
                case ENV_MOD_AR: s.evm = ENV_MOD_DR; s.evc = EG_DST; s.eve = s.SL; s.evs = s.evsd; break;
                case ENV_MOD_DR:
                    s.evc = s.SL; s.eve = EG_DED;
                    if (s.eg_typ != 0) s.evs = 0;
                    else { s.evm = ENV_MOD_RR; s.evs = s.evsr; }
                    break;
                case ENV_MOD_RR: s.evc = EG_OFF; s.eve = EG_OFF + 1; s.evs = 0; break;
            }
        }
        return (uint)(s.TLL + ENV_CURVE[s.evc >> ENV_BITS] + (s.ams != 0 ? ams : 0));
    }

    private static void CalcFcSlot(Channel ch, Slot s)
    {
        s.Incr = ch.fc * s.mul;
        int ksr = ch.kcode >> s.KSR;
        if (s.ksr != ksr)
        {
            s.ksr = (byte)ksr;
            s.evsa = s.AR[s.ARo + ksr];
            s.evsd = s.DR[s.DRo + ksr];
            s.evsr = s.RR[s.RRo + ksr];
        }
        s.TLL = (int)(s.TL + (ch.ksl_base >> s.ksl));
    }

    private void SetMul(int slot, int v)
    {
        var ch = P_CH[slot / 2]; var s = ch.SLOT[slot & 1];
        s.mul = MUL_TABLE[v & 0x0f];
        s.KSR = (byte)((v & 0x10) != 0 ? 0 : 2);
        s.eg_typ = (byte)((v & 0x20) >> 5);
        s.vib = (byte)(v & 0x40);
        s.ams = (byte)(v & 0x80);
        CalcFcSlot(ch, s);
    }

    private void SetKslTl(int slot, int v)
    {
        var ch = P_CH[slot / 2]; var s = ch.SLOT[slot & 1];
        int ksl = v >> 6;
        s.ksl = (byte)(ksl != 0 ? 3 - ksl : 31);
        s.TL = (int)((v & 0x3f) * (0.75 / EG_STEP));
        if ((mode & 0x80) == 0) s.TLL = (int)(s.TL + (ch.ksl_base >> s.ksl));
    }

    private void SetArDr(int slot, int v)
    {
        var s = P_CH[slot / 2].SLOT[slot & 1];
        int ar = v >> 4, dr = v & 0x0f;
        if (ar != 0) { s.AR = AR_TABLE; s.ARo = ar << 2; } else { s.AR = RATE_0; s.ARo = 0; }
        s.evsa = s.AR[s.ARo + s.ksr];
        if (s.evm == ENV_MOD_AR) s.evs = s.evsa;
        if (dr != 0) { s.DR = DR_TABLE; s.DRo = dr << 2; } else { s.DR = RATE_0; s.DRo = 0; }
        s.evsd = s.DR[s.DRo + s.ksr];
        if (s.evm == ENV_MOD_DR) s.evs = s.evsd;
    }

    private void SetSlRr(int slot, int v)
    {
        var s = P_CH[slot / 2].SLOT[slot & 1];
        int sl = v >> 4, rr = v & 0x0f;
        s.SL = SL_TABLE[sl];
        if (s.evm == ENV_MOD_DR) s.eve = s.SL;
        s.RR = DR_TABLE; s.RRo = rr << 2;
        s.evsr = s.RR[s.RRo + s.ksr];
        if (s.evm == ENV_MOD_RR) s.evs = s.evsr;
    }

    // OP_OUT(slot,env,con): slot->wavetable[((slot->Cnt+con)/(0x1000000/SIN_ENT))&(SIN_ENT-1)][env]
    private static int OpOut(Slot s, uint env, int con) => TL_TABLE[SIN_TABLE[s.wavetable + (int)(((s.Cnt + (uint)con) / (0x1000000 / SIN_ENT)) & (SIN_ENT - 1))] + (int)env];

    private void CalcCh(Channel ch)
    {
        feedback2 = 0;
        var s = ch.SLOT[SLOT1];
        uint env = CalcSlot(s);
        if (env < EG_ENT - 1)
        {
            if (s.vib != 0) s.Cnt += (uint)(s.Incr * (uint)vib / VIB_RATE); else s.Cnt += s.Incr;
            int o;
            if (ch.FB != 0)
            {
                int fb1 = (ch.op1_out[0] + ch.op1_out[1]) >> ch.FB;
                ch.op1_out[1] = ch.op1_out[0];
                o = ch.op1_out[0] = OpOut(s, env, fb1);
            }
            else o = OpOut(s, env, 0);
            if (ch.connect1Carrier) outd += o; else feedback2 += o;
        }
        else { ch.op1_out[1] = ch.op1_out[0]; ch.op1_out[0] = 0; }
        s = ch.SLOT[SLOT2];
        env = CalcSlot(s);
        if (env < EG_ENT - 1)
        {
            if (s.vib != 0) s.Cnt += (uint)(s.Incr * (uint)vib / VIB_RATE); else s.Cnt += s.Incr;
            outd += OpOut(s, env, feedback2);
        }
    }

    private int Rand() { randSeed = randSeed * 214013 + 2531011; return (int)((randSeed >> 16) & 0x7fff); }

    private void CalcRh()
    {
        int whitenoise = (int)((Rand() & 1) * (6.0 / EG_STEP));
        var ch6 = P_CH[6];
        Slot s7_1 = P_CH[7].SLOT[SLOT1], s7_2 = P_CH[7].SLOT[SLOT2], s8_1 = P_CH[8].SLOT[SLOT1], s8_2 = P_CH[8].SLOT[SLOT2];

        feedback2 = 0;
        var s = ch6.SLOT[SLOT1];
        uint env = CalcSlot(s);
        if (env < EG_ENT - 1)
        {
            if (s.vib != 0) s.Cnt += (uint)(s.Incr * (uint)vib / VIB_RATE); else s.Cnt += s.Incr;
            if (ch6.FB != 0)
            {
                int fb1 = (ch6.op1_out[0] + ch6.op1_out[1]) >> ch6.FB;
                ch6.op1_out[1] = ch6.op1_out[0];
                feedback2 = ch6.op1_out[0] = OpOut(s, env, fb1);
            }
            else feedback2 = OpOut(s, env, 0);
        }
        else { feedback2 = 0; ch6.op1_out[1] = ch6.op1_out[0]; ch6.op1_out[0] = 0; }
        s = ch6.SLOT[SLOT2];
        env = CalcSlot(s);
        if (env < EG_ENT - 1)
        {
            if (s.vib != 0) s.Cnt += (uint)(s.Incr * (uint)vib / VIB_RATE); else s.Cnt += s.Incr;
            outd += OpOut(s, env, feedback2) * 2;
        }

        uint env_sd = CalcSlot(s7_2) + (uint)whitenoise;
        uint env_tam = CalcSlot(s8_1);
        uint env_top = CalcSlot(s8_2);
        uint env_hh = CalcSlot(s7_1) + (uint)whitenoise;

        if (s7_1.vib != 0) s7_1.Cnt += (uint)(2 * s7_1.Incr * (uint)vib / VIB_RATE); else s7_1.Cnt += 2 * s7_1.Incr;
        if (s7_2.vib != 0) s7_2.Cnt += (uint)((P_CH[7].fc * 8) * (uint)vib / VIB_RATE); else s7_2.Cnt += P_CH[7].fc * 8;
        if (s8_1.vib != 0) s8_1.Cnt += (uint)(s8_1.Incr * (uint)vib / VIB_RATE); else s8_1.Cnt += s8_1.Incr;
        if (s8_2.vib != 0) s8_2.Cnt += (uint)((P_CH[8].fc * 48) * (uint)vib / VIB_RATE); else s8_2.Cnt += P_CH[8].fc * 48;

        int tone8 = OpOut(s8_2, (uint)whitenoise, 0);

        if (env_sd < EG_ENT - 1) outd += OpOut(s7_1, env_sd, 0) * 8;
        if (env_tam < EG_ENT - 1) outd += OpOut(s8_1, env_tam, 0) * 2;
        if (env_top < EG_ENT - 1) outd += OpOut(s7_2, env_top, tone8) * 2;
        if (env_hh < EG_ENT - 1) outd += OpOut(s7_2, env_hh, tone8) * 2;
    }

    // ---------- write a OPL register
    private void WriteReg(int r, int v)
    {
        r &= 0xff; v &= 0xff;
        int slot;
        switch (r & 0xe0)
        {
            case 0x00:
                switch (r & 0x1f)
                {
                    case 0x01:
                        // YM3812: wave selector enable
                        wavesel = (byte)(v & 0x20);
                        if (wavesel == 0)
                            for (int c = 0; c < 9; c++) { P_CH[c].SLOT[SLOT1].wavetable = 0; P_CH[c].SLOT[SLOT2].wavetable = 0; }
                        return;
                    case 0x02: T[0] = (256 - v) * 4; break;
                    case 0x03: T[1] = (256 - v) * 16; return;
                    case 0x04:
                        if ((v & 0x80) != 0) StatusReset(0x7f);
                        else
                        {
                            StatusReset(v & 0x78);
                            statusmask = (byte)(((~v) & 0x78) | 0x01);
                            StatusSet(0); StatusReset(0);
                            st[1] = (byte)((v >> 1) & 1);
                            st[0] = (byte)(v & 1);
                        }
                        return;
                }
                break;
            case 0x20: slot = slot_array[r & 0x1f]; if (slot == -1) return; SetMul(slot, v); return;
            case 0x40: slot = slot_array[r & 0x1f]; if (slot == -1) return; SetKslTl(slot, v); return;
            case 0x60: slot = slot_array[r & 0x1f]; if (slot == -1) return; SetArDr(slot, v); return;
            case 0x80: slot = slot_array[r & 0x1f]; if (slot == -1) return; SetSlRr(slot, v); return;
            case 0xa0:
            {
                if (r == 0xbd)
                {
                    int rkey = rythm ^ v;
                    ams_table = (v & 0x80) != 0 ? AMS_ENT : 0;
                    vib_table = (v & 0x40) != 0 ? VIB_ENT : 0;
                    rythm = (byte)(v & 0x3f);
                    if ((rythm & 0x20) != 0)
                    {
                        if ((rkey & 0x10) != 0)
                        {
                            if ((v & 0x10) != 0) { P_CH[6].op1_out[0] = P_CH[6].op1_out[1] = 0; KeyOn(P_CH[6].SLOT[SLOT1]); KeyOn(P_CH[6].SLOT[SLOT2]); }
                            else { KeyOff(P_CH[6].SLOT[SLOT1]); KeyOff(P_CH[6].SLOT[SLOT2]); }
                        }
                        if ((rkey & 0x08) != 0) { if ((v & 0x08) != 0) KeyOn(P_CH[7].SLOT[SLOT2]); else KeyOff(P_CH[7].SLOT[SLOT2]); }
                        if ((rkey & 0x04) != 0) { if ((v & 0x04) != 0) KeyOn(P_CH[8].SLOT[SLOT1]); else KeyOff(P_CH[8].SLOT[SLOT1]); }
                        if ((rkey & 0x02) != 0) { if ((v & 0x02) != 0) KeyOn(P_CH[8].SLOT[SLOT2]); else KeyOff(P_CH[8].SLOT[SLOT2]); }
                        if ((rkey & 0x01) != 0) { if ((v & 0x01) != 0) KeyOn(P_CH[7].SLOT[SLOT1]); else KeyOff(P_CH[7].SLOT[SLOT1]); }
                    }
                    return;
                }
                if ((r & 0x0f) > 8) return;
                var ch = P_CH[r & 0x0f];
                uint block_fnum;
                if ((r & 0x10) == 0) block_fnum = (ch.block_fnum & 0x1f00) | (uint)v;
                else
                {
                    byte keyon = (byte)((v >> 5) & 1);
                    block_fnum = ((uint)(v & 0x1f) << 8) | (ch.block_fnum & 0xff);
                    if (ch.keyon != keyon)
                    {
                        if ((ch.keyon = keyon) != 0) { ch.op1_out[0] = ch.op1_out[1] = 0; KeyOn(ch.SLOT[SLOT1]); KeyOn(ch.SLOT[SLOT2]); }
                        else { KeyOff(ch.SLOT[SLOT1]); KeyOff(ch.SLOT[SLOT2]); }
                    }
                }
                if (ch.block_fnum != block_fnum)
                {
                    int blockRv = 7 - (int)(block_fnum >> 10);
                    int fnum = (int)(block_fnum & 0x3ff);
                    ch.block_fnum = block_fnum;
                    ch.ksl_base = KSL_TABLE[block_fnum >> 6];
                    ch.fc = FN_TABLE[fnum] >> blockRv;
                    ch.kcode = (byte)(ch.block_fnum >> 9);
                    if ((mode & 0x40) != 0 && (ch.block_fnum & 0x100) != 0) ch.kcode |= 1;
                    CalcFcSlot(ch, ch.SLOT[SLOT1]);
                    CalcFcSlot(ch, ch.SLOT[SLOT2]);
                }
                return;
            }
            case 0xc0:
            {
                if ((r & 0x0f) > 8) return;
                var ch = P_CH[r & 0x0f];
                int feedback = (v >> 1) & 7;
                ch.FB = (byte)(feedback != 0 ? (8 + 1) - feedback : 0);
                ch.CON = (byte)(v & 1);
                ch.connect1Carrier = ch.CON != 0;
                return;
            }
            case 0xe0:
                slot = slot_array[r & 0x1f];
                if (slot == -1) return;
                if (wavesel != 0) P_CH[slot / 2].SLOT[slot & 1].wavetable = (v & 0x03) * SIN_ENT;
                return;
        }
    }

    /// <summary>OPLResetChip.</summary>
    public void ResetChip()
    {
        mode = 0;
        StatusReset(0x7f);
        WriteReg(0x01, 0); WriteReg(0x02, 0); WriteReg(0x03, 0); WriteReg(0x04, 0);
        for (int i = 0xff; i >= 0x20; i--) WriteReg(i, 0);
        for (int c = 0; c < 9; c++)
            for (int s = 0; s < 2; s++)
            {
                var sl = P_CH[c].SLOT[s];
                sl.wavetable = 0; sl.evc = EG_OFF; sl.eve = EG_OFF + 1; sl.evs = 0;
            }
        cached = false;   // a new chip in the original (cur_chip reset by OPL_LockTable)
    }

    /// <summary>OPLWrite: a = 0 address port, 1 data port.</summary>
    public void Write(int a, int v)
    {
        if ((a & 1) == 0) address = (byte)v;
        else WriteReg(address, v);
    }

    public byte ReadStatus() => (byte)(status & (statusmask | 0x80));

    /// <summary>YM3812UpdateOne for one sample.</summary>
    public short Update()
    {
        if (!cached)
        {
            cached = true;
            c_amsIncr = amsIncrInit; c_vibIncr = vibIncrInit;
            c_ams_table = ams_table; c_vib_table = vib_table;
        }
        bool rh = (rythm & 0x20) != 0;
        int rEnd = rh ? 6 : 9;
        ams = AMS_TABLE[c_ams_table + (int)((amsCnt += (uint)c_amsIncr) >> AMS_SHIFT)];
        vib = VIB_TABLE[c_vib_table + (int)((vibCnt += (uint)c_vibIncr) >> VIB_SHIFT)];
        outd = 0;
        for (int c = 0; c < rEnd; c++) CalcCh(P_CH[c]);
        if (rh) CalcRh();
        int data = Limit(outd, OPL_MAXOUT, OPL_MINOUT);
        return (short)(data >> OPL_OUTSB);
    }
}
