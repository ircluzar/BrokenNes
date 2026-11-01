// =====================================================================================================================
//  Emu2413v055 - a quick C# port of emu2413 v0.55 (2002-03-02), Mitsutaka Okazaki's very early YM2413 (OPLL) emulator.
//
//  Source project : emu2413-legacy (Mitsutaka Okazaki), tag `0.55` (commit 2d92835 "v0.55"; the later commits only add a README)
//  Files ported   : emu2413.c, emu2413.h, 2413tone.h (the YM2413 instrument ROM dump; vrc7tone.h and the stereo/pan path left out)
//  URL            : https://github.com/digital-sound-antiques/emu2413-legacy
//  Copyright      : (c) 2001-2002 Mitsutaka Okazaki
//  License        : license not stated (emu2413-legacy, Mitsutaka Okazaki); documented exception.
//  This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
//
//  Its author's README: "Very early version of emu2413. Its quality is extremely low". Kept as is:
//   - the default player setup of its sample2413.c: OPLL_new(3579545, 44100), quality 0 (no internal 49,716 Hz + linear
//     interpolation): every table is rate_adjust()ed to 44,100 Hz and one OPLL_calc() is one 44.1 kHz output sample;
//   - a log-curve attack table (AR_ADJUST_TABLE), not the chip's exponential attack; a 9-bit sine, 8-bit dB steps;
//   - the "LPF": each carrier/modulator outputs the average of its last two samples ((out[1] + out[0]) >> 1);
//   - early drums: white-noise snare, a two-square + noise hi-hat, a cymbal that never advances its phase, the rhythm
//     section doubled (perc << 1); a 17-bit noise LFSR with taps 0x24000;
//   - sine-table LFOs (6.4 Hz PM / 3.7 Hz AM) instead of the chip's counters; its own instrument ROM;
//   - the mix truncated to a signed 16-bit value.
// =====================================================================================================================
using System;

namespace NesEmulator.Sega;

public sealed class Emu2413v055
{
    // ---- constants (emu2413.c) ----
    private const int PG_BITS = 9, PG_WIDTH = 1 << PG_BITS;
    private const int DP_BITS = 18, DP_WIDTH = 1 << DP_BITS, DP_BASE_BITS = DP_BITS - PG_BITS;
    private const int DB_BITS = 8, DB_MUTE = 1 << DB_BITS;
    private const double DB_STEP = 48.0 / (1 << DB_BITS);
    private const double EG_STEP = 0.375; private const int EG_BITS = 7;
    private const double TL_STEP = 0.75; private const int TL_BITS = 6;
    private const double SL_STEP = 3.0;
    private const int DB2LIN_AMP_BITS = 11;
    private const int EG_DP_BITS = 22, EG_DP_WIDTH = 1 << EG_DP_BITS;
    private const int PM_PG_BITS = 8, PM_PG_WIDTH = 1 << PM_PG_BITS, PM_DP_BITS = 16, PM_DP_WIDTH = 1 << PM_DP_BITS;
    private const int AM_PG_BITS = 8, AM_PG_WIDTH = 1 << AM_PG_BITS, AM_DP_BITS = 16, AM_DP_WIDTH = 1 << AM_DP_BITS;
    private const int PM_AMP_BITS = 8, PM_AMP = 1 << PM_AMP_BITS;
    private const double PM_SPEED = 6.4, PM_DEPTH = 13.75, AM_SPEED = 3.7, AM_DEPTH = 2.4;

    private static int EG2DB(int d) => d * (int)(EG_STEP / DB_STEP);
    private static int TL2EG(int d) => d * (int)(TL_STEP / EG_STEP);
    private static int SL2EG(int d) => d * (int)(SL_STEP / EG_STEP);
    private static uint DB_POS(double x) => (uint)(x / DB_STEP);
    private static uint DB_NEG(double x) => (uint)(DB_MUTE + DB_MUTE + x / DB_STEP);

    private const int SETTLE = 0, ATTACK = 1, DECAY = 2, SUSHOLD = 3, SUSTINE = 4, RELEASE = 5, FINISH = 6;
    private const int SLOT_BD1 = 12, SLOT_BD2 = 13, SLOT_HH = 14, SLOT_SD = 15, SLOT_TOM = 16, SLOT_CYM = 17;
    public const int MASK_HH = 1 << 9, MASK_CYM = 1 << 10, MASK_TOM = 1 << 11, MASK_SD = 1 << 12, MASK_BD = 1 << 13;

    // 2413tone.h
    private static readonly byte[] DefaultInst =
    {
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0,0,0,0,0,0,0,0,
        0x61,0x61,0x1e,0x17,0xf0,0x7f,0x07,0x17,0,0,0,0,0,0,0,0,
        0x13,0x41,0x0f,0x0d,0xce,0xf5,0x43,0x23,0,0,0,0,0,0,0,0,
        0x03,0x01,0x9a,0x04,0xf3,0xf4,0x13,0x23,0,0,0,0,0,0,0,0,
        0x21,0x61,0x1d,0x07,0xfa,0x64,0x30,0x28,0,0,0,0,0,0,0,0,
        0x22,0x21,0x1e,0x06,0xf0,0x76,0x18,0x28,0,0,0,0,0,0,0,0,
        0x31,0x02,0x16,0x05,0x90,0x71,0x00,0x10,0,0,0,0,0,0,0,0,
        0x21,0x61,0x1d,0x07,0x82,0x80,0x10,0x17,0,0,0,0,0,0,0,0,
        0x23,0x21,0x2d,0x16,0xc0,0x70,0x07,0x07,0,0,0,0,0,0,0,0,
        0x61,0x21,0x1b,0x06,0x64,0x65,0x18,0x18,0,0,0,0,0,0,0,0,
        0x61,0x61,0x0c,0x18,0x85,0xa0,0x79,0x07,0,0,0,0,0,0,0,0,
        0x23,0x21,0x87,0x11,0xf0,0xa4,0x00,0xf7,0,0,0,0,0,0,0,0,
        0x97,0xe1,0x28,0x07,0xff,0xf3,0x02,0xf8,0,0,0,0,0,0,0,0,
        0x61,0x10,0x0c,0x05,0xf2,0xc4,0x40,0xc8,0,0,0,0,0,0,0,0,
        0x01,0x01,0x56,0x03,0xb4,0xb2,0x23,0x58,0,0,0,0,0,0,0,0,
        0x61,0x41,0x89,0x03,0xf1,0xf4,0xf0,0x13,0,0,0,0,0,0,0,0,
        0x07,0x21,0x14,0x00,0xee,0xf8,0xff,0xf8,0,0,0,0,0,0,0,0,
        0x23,0x22,0x00,0x00,0xd8,0xf8,0xf8,0xf7,0,0,0,0,0,0,0,0,
        0x25,0x18,0x00,0x00,0xf8,0xda,0xf8,0x55,0,0,0,0,0,0,0,0,
    };

    private sealed class Patch { public int TL, FB, EG, ML, AR, DR, SL, RR, KR, KL, AM, PM, WF; public void CopyFrom(Patch p) { TL = p.TL; FB = p.FB; EG = p.EG; ML = p.ML; AR = p.AR; DR = p.DR; SL = p.SL; RR = p.RR; KR = p.KR; KL = p.KL; AM = p.AM; PM = p.PM; WF = p.WF; } }

    private sealed class Slot
    {
        public Patch patch = null!;
        public int type;
        public int feedback;
        public int out0, out1;
        public ushort[] sintbl = null!;
        public uint phase, dphase, pgout;
        public int fnum, block, volume, sustine;
        public uint tll, rks;
        public int eg_mode;
        public uint eg_phase, eg_dphase, egout;
    }

    private sealed class Ch { public int patch_number, key_status; public Slot mod = null!, car = null!; }

    // ---- "static" tables (per instance here) ----
    private readonly uint rate, clk;
    private readonly ushort[] fullsintable = new ushort[PG_WIDTH], halfsintable = new ushort[PG_WIDTH];
    private readonly ushort[][] waveform;
    private readonly uint[,] dphaseNoiseTable = new uint[512, 8];
    private readonly int[] pmtable = new int[PM_PG_WIDTH], amtable = new int[AM_PG_WIDTH];
    private uint pm_dphase, am_dphase;
    private readonly short[] DB2LIN_TABLE = new short[(DB_MUTE + DB_MUTE) * 2];
    private readonly ushort[] AR_ADJUST_TABLE = new ushort[1 << EG_BITS];
    private static readonly Patch null_patch = new();
    private readonly Patch[] default_patch = new Patch[19 * 2];
    private readonly uint[,] dphaseARTable = new uint[16, 16], dphaseDRTable = new uint[16, 16];
    private readonly uint[,,,] tllTable = new uint[16, 8, 1 << TL_BITS, 4];
    private readonly int[,,] rksTable = new int[2, 8, 2];
    private readonly uint[,,] dphaseTable = new uint[512, 8, 16];
    private static readonly uint[] SL = BuildSl();

    // ---- OPLL state ----
    private uint realstep, oplltime, opllstep;
    private uint adr;
    private int outv, prev, next;
    private readonly byte[] reg = new byte[0x40];
    private readonly int[] slot_on_flag = new int[18];
    private uint pm_phase; private int lfo_pm;
    private int am_phase; private int lfo_am;
    public uint Quality;
    private uint noise_seed, whitenoise, noiseA, noiseB, noiseA_phase, noiseB_phase, noiseA_dphase, noiseB_dphase;
    private readonly Ch[] ch = new Ch[9];
    private readonly Slot[] slot = new Slot[18];
    private readonly Patch[] patch = new Patch[19 * 2];
    public uint Mask;

    private uint RateAdjust(double x) => rate == 49716 ? (uint)x : (uint)(x * clk / 72 / rate + 0.5);

    /// <summary>OPLL_new(clk, rate).</summary>
    public Emu2413v055(uint clock = 3579545, uint sampleRate = 44100)
    {
        clk = clock; rate = sampleRate;
        waveform = new[] { fullsintable, halfsintable };
        // maketables
        MakePmTable(); MakeAmTable(); MakeDB2LinTable(); MakeAdjustTable(); MakeTllTable(); MakeRksTable(); MakeSinTable(); MakeDefaultPatch();
        InternalRefresh();
        for (int i = 0; i < 19 * 2; i++) patch[i] = new Patch();
        for (int i = 0; i < 9; i++)
        {
            ch[i] = new Ch { mod = new Slot { type = 0 }, car = new Slot { type = 1 } };
            slot[i * 2] = ch[i].mod; slot[i * 2 + 1] = ch[i].car;
        }
        Mask = 0;
        Reset();
        ResetPatch();
    }

    // ================================ tables ================================
    private void MakeAdjustTable()
    {
        AR_ADJUST_TABLE[0] = 1 << EG_BITS;
        for (int i = 1; i < 128; i++)
            AR_ADJUST_TABLE[i] = (ushort)(int)((double)(1 << EG_BITS) - 1 - (1 << EG_BITS) * Math.Log(i) / Math.Log(128));
    }

    private void MakeDB2LinTable()
    {
        for (int i = 0; i < DB_MUTE + DB_MUTE; i++)
        {
            DB2LIN_TABLE[i] = (short)((double)((1 << DB2LIN_AMP_BITS) - 1) * Math.Pow(10, -(double)i * DB_STEP / 20));
            if (i >= DB_MUTE) DB2LIN_TABLE[i] = 0;
            DB2LIN_TABLE[i + DB_MUTE + DB_MUTE] = (short)-DB2LIN_TABLE[i];
        }
    }

    private static int Lin2Db(double d)
    {
        if (d == 0) return DB_MUTE - 1;
        return Math.Min(-(int)(20.0 * Math.Log10(d) / DB_STEP), DB_MUTE - 1);
    }

    private void MakeSinTable()
    {
        for (int i = 0; i < PG_WIDTH / 4; i++) fullsintable[i] = (ushort)Lin2Db(Math.Sin(2.0 * Math.PI * i / PG_WIDTH));
        for (int i = 0; i < PG_WIDTH / 4; i++) fullsintable[PG_WIDTH / 2 - 1 - i] = fullsintable[i];
        for (int i = 0; i < PG_WIDTH / 2; i++) fullsintable[PG_WIDTH / 2 + i] = (ushort)(DB_MUTE + DB_MUTE + fullsintable[i]);
        for (int i = 0; i < PG_WIDTH / 2; i++) halfsintable[i] = fullsintable[i];
        for (int i = PG_WIDTH / 2; i < PG_WIDTH; i++) halfsintable[i] = fullsintable[0];
    }

    private void MakeDphaseNoiseTable()
    {
        for (int i = 0; i < 512; i++) for (int j = 0; j < 8; j++) dphaseNoiseTable[i, j] = RateAdjust(i << j);
    }

    private void MakePmTable()
    {
        for (int i = 0; i < PM_PG_WIDTH; i++)
            pmtable[i] = (int)((double)PM_AMP * Math.Pow(2, PM_DEPTH * Math.Sin(2.0 * Math.PI * i / PM_PG_WIDTH) / 1200));
    }

    private void MakeAmTable()
    {
        for (int i = 0; i < AM_PG_WIDTH; i++)
            amtable[i] = (int)(AM_DEPTH / 2 / DB_STEP * (1.0 + Math.Sin(2.0 * Math.PI * i / PM_PG_WIDTH)));
    }

    private void MakeDphaseTable()
    {
        uint[] mltable = { 1, 1 * 2, 2 * 2, 3 * 2, 4 * 2, 5 * 2, 6 * 2, 7 * 2, 8 * 2, 9 * 2, 10 * 2, 10 * 2, 12 * 2, 12 * 2, 15 * 2, 15 * 2 };
        for (uint fnum = 0; fnum < 512; fnum++)
            for (int block = 0; block < 8; block++)
                for (int ML = 0; ML < 16; ML++)
                    dphaseTable[fnum, block, ML] = RateAdjust(((fnum * mltable[ML]) << block) >> (20 - DP_BITS));
    }

    private void MakeTllTable()
    {
        static double dB2(double x) => x * 2;
        double[] kltable = {
            dB2( 0.000),dB2( 9.000),dB2(12.000),dB2(13.875),dB2(15.000),dB2(16.125),dB2(16.875),dB2(17.625),
            dB2(18.000),dB2(18.750),dB2(19.125),dB2(19.500),dB2(19.875),dB2(20.250),dB2(20.625),dB2(21.000) };
        for (int fnum = 0; fnum < 16; fnum++)
            for (int block = 0; block < 8; block++)
                for (int TL = 0; TL < 64; TL++)
                    for (int KL = 0; KL < 4; KL++)
                    {
                        if (KL == 0) tllTable[fnum, block, TL, KL] = (uint)TL2EG(TL);
                        else
                        {
                            int tmp = (int)(kltable[fnum] - dB2(3.000) * (7 - block));
                            if (tmp <= 0) tllTable[fnum, block, TL, KL] = (uint)TL2EG(TL);
                            else tllTable[fnum, block, TL, KL] = (uint)((tmp >> (3 - KL)) / EG_STEP) + (uint)TL2EG(TL);
                        }
                    }
    }

    private void MakeDphaseARTable()
    {
        for (int AR = 0; AR < 16; AR++)
            for (int Rks = 0; Rks < 16; Rks++)
            {
                int RM = AR + (Rks >> 2), RL = Rks & 3;
                if (RM > 15) RM = 15;
                dphaseARTable[AR, Rks] = AR switch
                {
                    0 => 0u,
                    15 => (uint)EG_DP_WIDTH,
                    _ => RateAdjust((3 * (RL + 4)) << (RM + 1)),
                };
            }
    }

    private void MakeDphaseDRTable()
    {
        for (int DR = 0; DR < 16; DR++)
            for (int Rks = 0; Rks < 16; Rks++)
            {
                int RM = DR + (Rks >> 2), RL = Rks & 3;
                if (RM > 15) RM = 15;
                dphaseDRTable[DR, Rks] = DR == 0 ? 0 : RateAdjust((RL + 4) << (RM - 1));
            }
    }

    private void MakeRksTable()
    {
        for (int fnum8 = 0; fnum8 < 2; fnum8++)
            for (int block = 0; block < 8; block++)
                for (int KR = 0; KR < 2; KR++)
                    rksTable[fnum8, block, KR] = KR != 0 ? (block << 1) + fnum8 : block >> 1;
    }

    private static void Dump2Patch(byte[] dump, int o, Patch p0, Patch p1)
    {
        p0.AM = (dump[o + 0] >> 7) & 1; p1.AM = (dump[o + 1] >> 7) & 1;
        p0.PM = (dump[o + 0] >> 6) & 1; p1.PM = (dump[o + 1] >> 6) & 1;
        p0.EG = (dump[o + 0] >> 5) & 1; p1.EG = (dump[o + 1] >> 5) & 1;
        p0.KR = (dump[o + 0] >> 4) & 1; p1.KR = (dump[o + 1] >> 4) & 1;
        p0.ML = dump[o + 0] & 15; p1.ML = dump[o + 1] & 15;
        p0.KL = (dump[o + 2] >> 6) & 3; p1.KL = (dump[o + 3] >> 6) & 3;
        p0.TL = dump[o + 2] & 63;
        p0.FB = dump[o + 3] & 7;
        p0.WF = (dump[o + 3] >> 3) & 1; p1.WF = (dump[o + 3] >> 4) & 1;
        p0.AR = (dump[o + 4] >> 4) & 15; p1.AR = (dump[o + 5] >> 4) & 15;
        p0.DR = dump[o + 4] & 15; p1.DR = dump[o + 5] & 15;
        p0.SL = (dump[o + 6] >> 4) & 15; p1.SL = (dump[o + 7] >> 4) & 15;
        p0.RR = dump[o + 6] & 15; p1.RR = dump[o + 7] & 15;
    }

    private void MakeDefaultPatch()
    {
        for (int j = 0; j < 19; j++)
        {
            default_patch[j * 2] = new Patch(); default_patch[j * 2 + 1] = new Patch();
            Dump2Patch(DefaultInst, j * 16, default_patch[j * 2], default_patch[j * 2 + 1]);
        }
    }

    private static uint[] BuildSl()
    {
        static uint S2E(double x) => (uint)(SL2EG((int)(x / SL_STEP)) << (EG_DP_BITS - EG_BITS));
        return new[] { S2E(0.0), S2E(3.0), S2E(6.0), S2E(9.0), S2E(12.0), S2E(15.0), S2E(18.0), S2E(21.0),
                       S2E(24.0), S2E(27.0), S2E(30.0), S2E(33.0), S2E(36.0), S2E(39.0), S2E(42.0), S2E(48.0) };
    }

    private void InternalRefresh()
    {
        MakeDphaseTable(); MakeDphaseARTable(); MakeDphaseDRTable(); MakeDphaseNoiseTable();
        pm_dphase = RateAdjust(PM_SPEED * PM_DP_WIDTH / (clk / 72));   // clk/72 is an integer division in the original
        am_dphase = RateAdjust(AM_SPEED * AM_DP_WIDTH / (clk / 72));
    }

    // ================================ parameters ================================
    private uint CalcEgDphase(Slot s) => s.eg_mode switch
    {
        ATTACK => dphaseARTable[s.patch.AR, s.rks],
        DECAY => dphaseDRTable[s.patch.DR, s.rks],
        SUSHOLD => 0,
        SUSTINE => dphaseDRTable[s.patch.RR, s.rks],
        RELEASE => s.sustine != 0 ? dphaseDRTable[5, s.rks] : s.patch.EG != 0 ? dphaseDRTable[s.patch.RR, s.rks] : dphaseDRTable[7, s.rks],
        _ => 0,
    };

    private void UpdatePg(Slot s) => s.dphase = dphaseTable[s.fnum, s.block, s.patch.ML];
    private void UpdateTll(Slot s) => s.tll = s.type == 0 ? tllTable[s.fnum >> 5, s.block, s.patch.TL, s.patch.KL] : tllTable[s.fnum >> 5, s.block, s.volume, s.patch.KL];
    private void UpdateRks(Slot s) => s.rks = (uint)rksTable[s.fnum >> 8, s.block, s.patch.KR];
    private void UpdateWf(Slot s) => s.sintbl = waveform[s.patch.WF];
    private void UpdateEg(Slot s) => s.eg_dphase = CalcEgDphase(s);
    private void UpdateAll(Slot s) { UpdatePg(s); UpdateTll(s); UpdateRks(s); UpdateWf(s); UpdateEg(s); }

    private static void SlotOn(Slot s) { s.eg_mode = ATTACK; s.phase = 0; s.eg_phase = 0; }
    private void SlotOff(Slot s)
    {
        if (s.eg_mode == ATTACK)
            s.eg_phase = (uint)AR_ADJUST_TABLE[s.eg_phase >> (EG_DP_BITS - EG_BITS)] << (EG_DP_BITS - EG_BITS);
        s.eg_mode = RELEASE;
    }

    private void KeyOn(int i)
    {
        if (slot_on_flag[i * 2] == 0) SlotOn(ch[i].mod);
        if (slot_on_flag[i * 2 + 1] == 0) SlotOn(ch[i].car);
        ch[i].key_status = 1;
    }
    private void KeyOff(int i)
    {
        if (slot_on_flag[i * 2 + 1] != 0) SlotOff(ch[i].car);
        ch[i].key_status = 0;
    }
    private void KeyOnBD() => KeyOn(6);
    private void KeyOnSD() { if (slot_on_flag[SLOT_SD] == 0) SlotOn(ch[7].car); }
    private void KeyOnTOM() { if (slot_on_flag[SLOT_TOM] == 0) SlotOn(ch[8].mod); }
    private void KeyOnHH() { if (slot_on_flag[SLOT_HH] == 0) SlotOn(ch[7].mod); }
    private void KeyOnCYM() { if (slot_on_flag[SLOT_CYM] == 0) SlotOn(ch[8].car); }
    private void KeyOffBD() => KeyOff(6);
    private void KeyOffSD() { if (slot_on_flag[SLOT_SD] != 0) SlotOff(ch[7].car); }
    private void KeyOffTOM() { if (slot_on_flag[SLOT_TOM] != 0) SlotOff(ch[8].mod); }
    private void KeyOffHH() { if (slot_on_flag[SLOT_HH] != 0) SlotOff(ch[7].mod); }
    private void KeyOffCYM() { if (slot_on_flag[SLOT_CYM] != 0) SlotOff(ch[8].car); }

    private void SetPatch(int i, int num)
    {
        ch[i].patch_number = num;
        ch[i].mod.patch = patch[num * 2 + 0];
        ch[i].car.patch = patch[num * 2 + 1];
    }

    private void SetSustine(int c, int sustine)
    {
        ch[c].car.sustine = sustine;
        if (ch[c].mod.type != 0) ch[c].mod.sustine = sustine;
    }
    private void SetFnumber(int c, int fnum) { ch[c].car.fnum = fnum; ch[c].mod.fnum = fnum; }
    private void SetBlock(int c, int block) { ch[c].car.block = block; ch[c].mod.block = block; }

    private void UpdateRythmMode()
    {
        if ((ch[6].patch_number & 0x10) != 0)
        {
            if ((slot_on_flag[SLOT_BD2] | (reg[0x0e] & 32)) == 0)
            {
                slot[SLOT_BD1].eg_mode = FINISH; slot[SLOT_BD2].eg_mode = FINISH;
                SetPatch(6, reg[0x36] >> 4);
            }
        }
        else if ((reg[0x0e] & 32) != 0)
        {
            ch[6].patch_number = 16;
            slot[SLOT_BD1].eg_mode = FINISH; slot[SLOT_BD2].eg_mode = FINISH;
            slot[SLOT_BD1].patch = patch[16 * 2 + 0]; slot[SLOT_BD2].patch = patch[16 * 2 + 1];
        }

        if ((ch[7].patch_number & 0x10) != 0)
        {
            if ((((slot_on_flag[SLOT_HH] != 0 && slot_on_flag[SLOT_SD] != 0) ? 1 : 0) | (reg[0x0e] & 32)) == 0)
            {
                slot[SLOT_HH].type = 0;
                slot[SLOT_HH].eg_mode = FINISH; slot[SLOT_SD].eg_mode = FINISH;
                SetPatch(7, reg[0x37] >> 4);
            }
        }
        else if ((reg[0x0e] & 32) != 0)
        {
            ch[7].patch_number = 17;
            slot[SLOT_HH].type = 1;
            slot[SLOT_HH].eg_mode = FINISH; slot[SLOT_SD].eg_mode = FINISH;
            slot[SLOT_HH].patch = patch[17 * 2 + 0]; slot[SLOT_SD].patch = patch[17 * 2 + 1];
        }

        if ((ch[8].patch_number & 0x10) != 0)
        {
            if ((((slot_on_flag[SLOT_CYM] != 0 && slot_on_flag[SLOT_TOM] != 0) ? 1 : 0) | (reg[0x0e] & 32)) == 0)
            {
                slot[SLOT_TOM].type = 0;
                slot[SLOT_TOM].eg_mode = FINISH; slot[SLOT_CYM].eg_mode = FINISH;
                SetPatch(8, reg[0x38] >> 4);
            }
        }
        else if ((reg[0x0e] & 32) != 0)
        {
            ch[8].patch_number = 18;
            slot[SLOT_TOM].type = 1;
            slot[SLOT_TOM].eg_mode = FINISH; slot[SLOT_CYM].eg_mode = FINISH;
            slot[SLOT_TOM].patch = patch[18 * 2 + 0]; slot[SLOT_CYM].patch = patch[18 * 2 + 1];
        }
    }

    private void UpdateKeyStatus()
    {
        for (int c = 0; c < 9; c++) slot_on_flag[c * 2] = slot_on_flag[c * 2 + 1] = reg[0x20 + c] & 0x10;
        if ((reg[0x0e] & 32) != 0)
        {
            slot_on_flag[SLOT_BD1] |= reg[0x0e] & 0x10;
            slot_on_flag[SLOT_BD2] |= reg[0x0e] & 0x10;
            slot_on_flag[SLOT_SD] |= reg[0x0e] & 0x08;
            slot_on_flag[SLOT_HH] |= reg[0x0e] & 0x01;
            slot_on_flag[SLOT_TOM] |= reg[0x0e] & 0x04;
            slot_on_flag[SLOT_CYM] |= reg[0x0e] & 0x02;
        }
    }

    // ================================ init ================================
    private void SlotReset(Slot s)
    {
        s.sintbl = waveform[0]; s.phase = 0; s.dphase = 0; s.out0 = 0; s.out1 = 0; s.feedback = 0;
        s.eg_mode = SETTLE; s.eg_phase = EG_DP_WIDTH; s.eg_dphase = 0; s.rks = 0; s.tll = 0; s.sustine = 0;
        s.fnum = 0; s.block = 0; s.volume = 0; s.pgout = 0; s.egout = 0; s.patch = null_patch;
    }

    /// <summary>OPLL_reset_patch(opll, 0): the YM2413 tone set.</summary>
    public void ResetPatch() { for (int i = 0; i < 19 * 2; i++) patch[i].CopyFrom(default_patch[i]); }

    /// <summary>OPLL_reset: everything except the patches.</summary>
    public void Reset()
    {
        adr = 0; outv = 0; pm_phase = 0; am_phase = 0;
        noise_seed = 0xffff; noiseA = noiseB = 0; noiseA_phase = noiseB_phase = 0; noiseA_dphase = noiseB_dphase = 0;
        Mask = 0;
        for (int i = 0; i < 9; i++) { SlotReset(ch[i].mod); SlotReset(ch[i].car); ch[i].key_status = 0; SetPatch(i, 0); }
        for (int i = 0; i < 0x40; i++) WriteReg((uint)i, 0);
        realstep = 0x80000000u / rate;               // (1<<31)/rate
        opllstep = 0x80000000u / (clk / 72);
        oplltime = 0;
    }

    // ================================ synthesis ================================
    private void UpdateNoise()
    {
        if ((noise_seed & 1) != 0) noise_seed ^= 0x24000;
        noise_seed >>= 1;
        whitenoise = (noise_seed & 1) != 0 ? DB_POS(6.0) : DB_NEG(6.0);
        noiseA_phase += noiseA_dphase; noiseB_phase += noiseB_dphase;
        noiseA_phase &= (0x40 << 11) - 1;
        noiseA = (noiseA_phase & (0x03 << 11)) != 0 ? DB_POS(3.0) : DB_NEG(3.0);
        noiseB_phase &= (0x10 << 11) - 1;
        noiseB = (noiseB_phase & (0x0A << 11)) != 0 ? DB_POS(3.0) : DB_NEG(3.0);
    }

    private void UpdateAmPm()
    {
        pm_phase = (pm_phase + pm_dphase) & (PM_DP_WIDTH - 1);
        am_phase = (int)((am_phase + am_dphase) & (AM_DP_WIDTH - 1));
        lfo_am = amtable[am_phase >> (AM_DP_BITS - AM_PG_BITS)];
        lfo_pm = pmtable[pm_phase >> (PM_DP_BITS - PM_PG_BITS)];
    }

    private uint CalcPhase(Slot s)
    {
        if (s.patch.PM != 0) s.phase += (s.dphase * (uint)lfo_pm) >> PM_AMP_BITS;
        else s.phase += s.dphase;
        s.phase &= DP_WIDTH - 1;
        return s.phase >> DP_BASE_BITS;
    }

    private uint CalcEnvelope(Slot s)
    {
        uint egout;
        switch (s.eg_mode)
        {
            case ATTACK:
                egout = AR_ADJUST_TABLE[s.eg_phase >> (EG_DP_BITS - EG_BITS)];
                s.eg_phase += s.eg_dphase;
                if ((EG_DP_WIDTH & s.eg_phase) != 0) { egout = 0; s.eg_phase = 0; s.eg_mode = DECAY; UpdateEg(s); }
                break;
            case DECAY:
                egout = s.eg_phase >> (EG_DP_BITS - EG_BITS);
                s.eg_phase += s.eg_dphase;
                if (s.eg_phase >= SL[s.patch.SL])
                {
                    s.eg_phase = SL[s.patch.SL];
                    s.eg_mode = s.patch.EG != 0 ? SUSHOLD : SUSTINE;
                    UpdateEg(s);
                }
                break;
            case SUSHOLD:
                egout = s.eg_phase >> (EG_DP_BITS - EG_BITS);
                if (s.patch.EG == 0) { s.eg_mode = SUSTINE; UpdateEg(s); }
                break;
            case SUSTINE:
            case RELEASE:
                egout = s.eg_phase >> (EG_DP_BITS - EG_BITS);
                s.eg_phase += s.eg_dphase;
                if (egout >= (1 << EG_BITS)) { s.eg_mode = FINISH; egout = (1 << EG_BITS) - 1; }
                break;
            default:
                egout = (1 << EG_BITS) - 1;
                break;
        }
        if (s.patch.AM != 0) egout = (uint)(EG2DB((int)(egout + s.tll)) + lfo_am);
        else egout = (uint)EG2DB((int)(egout + s.tll));
        if (egout >= DB_MUTE) egout = DB_MUTE - 1;
        return egout;
    }

    private int CalcSlotCar(Slot s, int fm)
    {
        s.egout = CalcEnvelope(s);
        s.pgout = CalcPhase(s);
        s.out1 = s.out0;
        if (s.egout >= DB_MUTE - 1) s.out0 = 0;
        else s.out0 = DB2LIN_TABLE[s.sintbl[(int)(s.pgout + (uint)fm) & (PG_WIDTH - 1)] + s.egout];   // wave2_8pi(fm) == fm
        return (s.out1 + s.out0) >> 1;
    }

    private int CalcSlotMod(Slot s)
    {
        s.out1 = s.out0;
        s.egout = CalcEnvelope(s);
        s.pgout = CalcPhase(s);
        if (s.egout >= DB_MUTE - 1) s.out0 = 0;
        else if (s.patch.FB != 0)
        {
            int fm = (s.feedback >> 1) >> (7 - s.patch.FB);   // wave2_4pi(e) == e >> 1
            s.out0 = DB2LIN_TABLE[s.sintbl[(int)(s.pgout + (uint)fm) & (PG_WIDTH - 1)] + s.egout];
        }
        else s.out0 = DB2LIN_TABLE[s.sintbl[s.pgout] + s.egout];
        s.feedback = (s.out1 + s.out0) >> 1;
        return s.feedback;
    }

    private int CalcSlotTom(Slot s)
    {
        s.egout = CalcEnvelope(s);
        s.pgout = CalcPhase(s);
        if (s.egout >= DB_MUTE - 1) return 0;
        return DB2LIN_TABLE[s.sintbl[s.pgout] + s.egout];
    }

    private int CalcSlotSnare(Slot s, uint wn)
    {
        s.egout = CalcEnvelope(s);
        s.pgout = CalcPhase(s);
        if (s.egout >= DB_MUTE - 1) return 0;
        if ((s.pgout & (1 << (PG_BITS - 1))) != 0)
            return (DB2LIN_TABLE[s.egout] + DB2LIN_TABLE[s.egout + wn]) >> 1;
        return (DB2LIN_TABLE[DB_MUTE + DB_MUTE + s.egout] + DB2LIN_TABLE[s.egout + wn]) >> 1;
    }

    private int CalcSlotCym(Slot s, uint a, uint b)
    {
        s.egout = CalcEnvelope(s);   // the phase is never advanced
        if (s.egout >= DB_MUTE - 1) return 0;
        return (DB2LIN_TABLE[s.egout + a] + DB2LIN_TABLE[s.egout + b]) >> 1;
    }

    private int CalcSlotHat(Slot s, uint a, uint b, uint wn)
    {
        s.egout = CalcEnvelope(s);
        if (s.egout >= DB_MUTE - 1) return 0;
        return (DB2LIN_TABLE[s.egout + wn] >> 2) + (DB2LIN_TABLE[s.egout + a] >> 3) + (DB2LIN_TABLE[s.egout + b] >> 3);
    }

    private short CalcInternal()
    {
        int inst = 0, perc = 0;
        UpdateAmPm();
        UpdateNoise();
        for (int i = 0; i < 6; i++)
            if ((Mask & (1u << i)) == 0 && ch[i].car.eg_mode != FINISH)
                inst += CalcSlotCar(ch[i].car, CalcSlotMod(ch[i].mod));

        if (ch[6].patch_number <= 15)
        {
            if ((Mask & (1u << 6)) == 0 && ch[6].car.eg_mode != FINISH) inst += CalcSlotCar(ch[6].car, CalcSlotMod(ch[6].mod));
        }
        else if ((Mask & MASK_BD) == 0 && ch[6].car.eg_mode != FINISH) perc += CalcSlotCar(ch[6].car, CalcSlotMod(ch[6].mod));

        if (ch[7].patch_number <= 15)
        {
            if ((Mask & (1u << 7)) == 0 && ch[7].car.eg_mode != FINISH) inst += CalcSlotCar(ch[7].car, CalcSlotMod(ch[7].mod));
        }
        else
        {
            if ((Mask & MASK_HH) == 0 && ch[7].mod.eg_mode != FINISH) perc += CalcSlotHat(ch[7].mod, noiseA, noiseB, whitenoise);
            if ((Mask & MASK_SD) == 0 && ch[7].car.eg_mode != FINISH) perc += CalcSlotSnare(ch[7].car, whitenoise);
        }

        if (ch[8].patch_number <= 15)
        {
            if ((Mask & (1u << 8)) == 0 && ch[8].car.eg_mode != FINISH) inst += CalcSlotCar(ch[8].car, CalcSlotMod(ch[8].mod));
        }
        else
        {
            if ((Mask & MASK_TOM) == 0 && ch[8].mod.eg_mode != FINISH) perc += CalcSlotTom(ch[8].mod);
            if ((Mask & MASK_CYM) == 0 && ch[8].car.eg_mode != FINISH) perc += CalcSlotCym(ch[8].car, noiseA, noiseB);
        }

        return (short)(inst + (perc << 1));
    }

    /// <summary>OPLL_calc: one output sample at the rate given to the constructor.</summary>
    public short Calc()
    {
        if (Quality == 0) return CalcInternal();
        while (realstep > oplltime)
        {
            oplltime += opllstep;
            prev = next;
            next = CalcInternal();
        }
        oplltime -= realstep;
        outv = (short)(((double)next * (opllstep - oplltime) + (double)prev * oplltime) / opllstep);
        return (short)outv;
    }

    // ================================ I/O ================================
    public void WriteReg(uint r, uint data)
    {
        data &= 0xff; r &= 0x3f;
        reg[r] = (byte)data;
        int i, v, c;
        switch (r)
        {
            case 0x00:
                patch[0].AM = (int)(data >> 7) & 1; patch[0].PM = (int)(data >> 6) & 1; patch[0].EG = (int)(data >> 5) & 1; patch[0].KR = (int)(data >> 4) & 1; patch[0].ML = (int)data & 15;
                for (i = 0; i < 9; i++) if (ch[i].patch_number == 0) { UpdatePg(ch[i].mod); UpdateRks(ch[i].mod); UpdateEg(ch[i].mod); }
                break;
            case 0x01:
                patch[1].AM = (int)(data >> 7) & 1; patch[1].PM = (int)(data >> 6) & 1; patch[1].EG = (int)(data >> 5) & 1; patch[1].KR = (int)(data >> 4) & 1; patch[1].ML = (int)data & 15;
                for (i = 0; i < 9; i++) if (ch[i].patch_number == 0) { UpdatePg(ch[i].car); UpdateRks(ch[i].car); UpdateEg(ch[i].car); }
                break;
            case 0x02:
                patch[0].KL = (int)(data >> 6) & 3; patch[0].TL = (int)data & 63;
                for (i = 0; i < 9; i++) if (ch[i].patch_number == 0) UpdateTll(ch[i].mod);
                break;
            case 0x03:
                patch[1].KL = (int)(data >> 6) & 3; patch[1].WF = (int)(data >> 4) & 1; patch[0].WF = (int)(data >> 3) & 1; patch[0].FB = (int)data & 7;
                for (i = 0; i < 9; i++) if (ch[i].patch_number == 0) { UpdateWf(ch[i].mod); UpdateWf(ch[i].car); }
                break;
            case 0x04:
                patch[0].AR = (int)(data >> 4) & 15; patch[0].DR = (int)data & 15;
                for (i = 0; i < 9; i++) if (ch[i].patch_number == 0) UpdateEg(ch[i].mod);
                break;
            case 0x05:
                patch[1].AR = (int)(data >> 4) & 15; patch[1].DR = (int)data & 15;
                for (i = 0; i < 9; i++) if (ch[i].patch_number == 0) UpdateEg(ch[i].car);
                break;
            case 0x06:
                patch[0].SL = (int)(data >> 4) & 15; patch[0].RR = (int)data & 15;
                for (i = 0; i < 9; i++) if (ch[i].patch_number == 0) UpdateEg(ch[i].mod);
                break;
            case 0x07:
                patch[1].SL = (int)(data >> 4) & 15; patch[1].RR = (int)data & 15;
                for (i = 0; i < 9; i++) if (ch[i].patch_number == 0) UpdateEg(ch[i].car);
                break;
            case 0x0e:
                UpdateRythmMode();
                if ((data & 32) != 0)
                {
                    if ((data & 0x10) != 0) KeyOnBD(); else KeyOffBD();
                    if ((data & 0x8) != 0) KeyOnSD(); else KeyOffSD();
                    if ((data & 0x4) != 0) KeyOnTOM(); else KeyOffTOM();
                    if ((data & 0x2) != 0) KeyOnCYM(); else KeyOffCYM();
                    if ((data & 0x1) != 0) KeyOnHH(); else KeyOffHH();
                }
                UpdateKeyStatus();
                UpdateAll(ch[6].mod); UpdateAll(ch[6].car);
                UpdateAll(ch[7].mod); UpdateAll(ch[7].car);
                UpdateAll(ch[8].mod); UpdateAll(ch[8].car);
                break;
            case 0x0f:
                break;
            case >= 0x10 and <= 0x18:
                c = (int)r - 0x10;
                SetFnumber(c, (int)data + ((reg[0x20 + c] & 1) << 8));
                UpdateAll(ch[c].mod); UpdateAll(ch[c].car);
                if (r == 0x17) noiseA_dphase = dphaseNoiseTable[data + ((reg[0x27] & 1u) << 8), (reg[0x27] >> 1) & 7];
                else if (r == 0x18) noiseB_dphase = dphaseNoiseTable[data + ((reg[0x28] & 1u) << 8), (reg[0x28] >> 1) & 7];
                break;
            case >= 0x20 and <= 0x28:
                c = (int)r - 0x20;
                SetFnumber(c, (int)((data & 1) << 8) + reg[0x10 + c]);
                SetBlock(c, (int)(data >> 1) & 7);
                SetSustine(c, (int)(data >> 5) & 1);
                if ((data & 0x10) != 0) KeyOn(c); else KeyOff(c);
                UpdateAll(ch[c].mod); UpdateAll(ch[c].car);
                if (r == 0x27) noiseA_dphase = dphaseNoiseTable[((data & 1) << 8) + reg[0x17], (data >> 1) & 7];
                else if (r == 0x28) noiseB_dphase = dphaseNoiseTable[((data & 1) << 8) + reg[0x18], (data >> 1) & 7];
                UpdateKeyStatus();
                UpdateRythmMode();
                break;
            case >= 0x30 and <= 0x38:
                i = (int)(data >> 4) & 15;
                v = (int)data & 15;
                c = (int)r - 0x30;
                if ((reg[0x0e] & 32) != 0 && r >= 0x36)
                {
                    if (r == 0x37) ch[7].mod.volume = i << 2;
                    else if (r == 0x38) ch[8].mod.volume = i << 2;
                }
                else SetPatch(c, i);
                ch[c].car.volume = v << 2;
                UpdateAll(ch[c].mod); UpdateAll(ch[c].car);
                break;
        }
    }

    /// <summary>OPLL_writeIO: odd = data to the latched register, even = the address latch.</summary>
    public void WriteIO(uint a, uint val)
    {
        if ((a & 1) != 0) WriteReg(adr, val);
        else adr = val;
    }
}
