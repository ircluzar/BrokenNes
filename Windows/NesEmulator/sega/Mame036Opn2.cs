using System;

namespace NesEmulator.Sega;

// =====================================================================================================================
//  MAME 0.36 OPN/OPN2 (YM2612) FM engine, ported to C#.
//
//  Source: MAME 0.36 (1999), src/sound/fm.c "Version 0.36e" + fm.h (the OPN common part and the YM2612 section only:
//  no OPM, OPNA/OPNB, ADPCM or SSG), https://github.com/mamedev/historic-mame (tag mame036), raw files at
//  https://raw.githubusercontent.com/mamedev/historic-mame/mame036/src/sound/fm.c ; the rate and clock as
//  src/sound/2612intf.c runs it (Machine->sample_rate, MAME's default 22,050 Hz; prescaler 12*12).
//  Copyright (C) 1998 Tatsuyuki Satoh, MultiArcadeMachineEmulator development (hiro-shi's changes included).
//  License: the MAME license of the time (non-commercial; source must accompany). This is a documented exception to the
//  repository's license policy - see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
//
//  A quick port. The original's shortcuts are kept on purpose, they are the sound:
//   - 22,050 Hz output, 16-bit, phase/envelope steps scaled by clock/rate/144 (no internal 53 kHz clock)
//   - the "preliminary" attack curve (x^8 table) and attack/decay times (OPM rates reused for OPN), no key scale level
//   - FM_SEG_SUPPORT 0: SSG-EG register stored and ignored
//   - the LFO steps once per stream-update call (not per sample), and only while CSM/3-slot mode bits (reg $27 & $C0)
//     are set; PMS is read from bits 4-6 of $B4 (the AMS bits) instead of bits 0-2
//   - operator 1 output is not refreshed while operator 1 is below the cut-off (stale feedback), signed phase / 8192
//     truncates toward zero
//   - frequency/rate changes are applied at the start of an update chunk (each register write flushes one chunk; the
//     host also flushes once per frame); pan 0 channels go to a dead accumulator; DAC replaces channel 6 at (v-$80)<<19
//  Left out: the timers, status and CSM auto key-on (the stock YM2612 keeps the timers for the game).
// =====================================================================================================================

/// <summary>The MAME 0.36 YM2612 FM engine: feed it the four ports, pull 22,050 Hz stereo samples one at a time.</summary>
public sealed class Mame036Opn2
{
    public const int DefaultRate = 22050;

    // ---- defines (fm.c / fm.h) ----
    private const int SIN_ENT = 2048, ENV_BITS = 16, EG_ENT = 4096;
    private const double EG_STEP = 96.0 / EG_ENT;
    private const int LFO_ENT = 512, LFO_SHIFT = 32 - 9, LFO_RATE = 0x10000, PMS_RATE = 0x400;
    private const int OPN_ARRATE = 399128, OPN_DRRATE = 5514396;
    private static readonly int PG_CUT_OFF = (int)(78.0 / EG_STEP);
    private static readonly int EG_CUT_OFF = (int)(68.0 / EG_STEP);
    private const int FREQ_BITS = 24, FREQ_RATE = 1 << (FREQ_BITS - 21), TL_BITS = FREQ_BITS + 2, TL_SHIFT = TL_BITS + 1 - (14 - 16);
    private const int FM_OUTPUT_BIT = 16, FM_OUTSB = TL_SHIFT - FM_OUTPUT_BIT;
    private const int FM_MAXOUT = (1 << (TL_SHIFT - 1)) - 1, FM_MINOUT = -(1 << (TL_SHIFT - 1));
    private const int EG_AST = 0, EG_AED = EG_ENT << ENV_BITS, EG_DST = EG_AED, EG_DED = EG_DST + ((EG_ENT - 1) << ENV_BITS), EG_OFF = EG_DED;
    private const int SLOT1 = 0, SLOT2 = 2, SLOT3 = 1, SLOT4 = 3;
    private const int OUTD_RIGHT = 1, OUTD_LEFT = 2, OUTD_CENTER = 3;

    // ---- shared tables ----
    private static readonly int TL_MAX = PG_CUT_OFF + EG_CUT_OFF + 1;
    private static readonly int[] TL_TABLE = new int[2 * TL_MAX];
    private static readonly int[] SIN_TABLE = new int[SIN_ENT];      // offsets into TL_TABLE (the C keeps pointers)
    private static readonly int[] ENV_CURVE = new int[2 * EG_ENT + 1];
    private static readonly int[] RATE_0 = new int[32];
    private static readonly int[] SL_TABLE = new int[16];
    private static readonly byte[] OPN_DTTABLE =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 6, 6, 7, 8, 8, 8, 8,
        1, 1, 1, 1, 2, 2, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 6, 6, 7, 8, 8, 9,10,11,12,13,14,16,16,16,16,
        2, 2, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 6, 6, 7, 8, 8, 9,10,11,12,13,14,16,17,19,20,22,22,22,22,
    };
    // MUL_TABLE ML(n)=n*2 for DT2=0 (the only row OPN uses)
    private static readonly int[] MUL_TABLE = { 1, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30 };
    private static readonly byte[] OPN_FKTABLE = { 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 3, 3, 3, 3, 3, 3 };

    static Mame036Opn2()
    {
        for (int t = 0; t < TL_MAX; t++)
        {
            double rate = t >= PG_CUT_OFF ? 0 : ((1 << TL_BITS) - 1) / Math.Pow(10, EG_STEP * t / 20);
            TL_TABLE[t] = (int)rate;
            TL_TABLE[TL_MAX + t] = -TL_TABLE[t];
        }
        for (int s = 1; s <= SIN_ENT / 4; s++)
        {
            double pom = Math.Sin(2.0 * Math.PI * s / SIN_ENT);
            pom = 20 * Math.Log10(1 / pom);
            int j = (int)(pom / EG_STEP);
            if (j > PG_CUT_OFF) j = PG_CUT_OFF;
            SIN_TABLE[s] = SIN_TABLE[SIN_ENT / 2 - s] = j;
            SIN_TABLE[SIN_ENT / 2 + s] = SIN_TABLE[SIN_ENT - s] = TL_MAX + j;
        }
        SIN_TABLE[0] = SIN_TABLE[SIN_ENT / 2] = PG_CUT_OFF;
        for (int i = 0; i < EG_ENT; i++)
        {
            ENV_CURVE[i] = (int)(Math.Pow((double)(EG_ENT - 1 - i) / EG_ENT, 8) * EG_ENT);   // !!!!! preliminary !!!!!
            ENV_CURVE[(EG_DST >> ENV_BITS) + i] = i;
        }
        ENV_CURVE[EG_OFF >> ENV_BITS] = EG_ENT - 1;
        for (int i = 0; i < 15; i++) SL_TABLE[i] = (int)(i * ((3 / EG_STEP) * (1 << ENV_BITS))) + EG_DST;
        SL_TABLE[15] = (int)(31 * ((3 / EG_STEP) * (1 << ENV_BITS))) + EG_DST;
    }

    private enum Eg : byte { Release, SR, DR, AR }

    private sealed class Slot
    {
        public int[] DT = RATE_0;
        public int TL, KSR;
        public int[] ARt = RATE_0, DRt = RATE_0, SRt = RATE_0, RRt = RATE_0;
        public int ARo, DRo, SRo, RRo;
        public int SL, SEG, ksr;
        public uint mul, Cnt, Incr;
        public Eg eg_next;
        public int evc, eve, evs, evsa, evsd, evss, evsr, TLL;
        public uint amon, ams;
    }

    private sealed class Chan
    {
        public readonly Slot[] S = { new(), new(), new(), new() };
        public int PAN, ALGO, FB;
        public readonly int[] op1_out = new int[2];
        public int c1, c2, c3, c4;           // indices into acc[]: 0-3 out_ch, 5-7 pg_in2..4, -1 the algorithm-5 special case
        public int pms; public uint ams;
        public uint fc; public int fn_h, kcode;
    }

    private readonly Chan[] ch = { new(), new(), new(), new(), new(), new() };
    private readonly int[] acc = new int[8];  // out_ch[0..3], pg_in1..4 at 4..7
    // FM_ST / FM_OPN
    private readonly int clock, rate;
    private double freqbase;
    private int address, address1;
    private int mode;
    private readonly int[][] DT_TABLE = new int[8][];
    private readonly int[] AR_TABLE = new int[94], DR_TABLE = new int[94];
    private readonly uint[] FN_TABLE = new uint[2048];
    private readonly uint[] SL3_fc = new uint[3];
    private readonly int[] SL3_fn_h = new int[3], SL3_kcode = new int[3];
    private uint LFOCnt, LFOIncr;
    private readonly uint[] LFO_FREQ = new uint[8];
    private readonly int[] LFO_wave = new int[LFO_ENT];
    private int dacen, dacout;
    // the C's cached globals (refreshed when cur_chip is cleared)
    private bool curChipDirty = true, chunkStart = true;
    private int dacenC; private uint LFOIncrC;
    private uint lfo_amd; private int lfo_pmd;
    private int lastCh;

    public int Rate => rate;

    public Mame036Opn2(int clock, int rate = DefaultRate)
    {
        this.clock = clock; this.rate = rate;
        for (int i = 0; i < 8; i++) DT_TABLE[i] = new int[32];
        Reset();
    }

    /// <summary>Starts a new update chunk on the next sample (MAME's stream_update / frame-end flush).</summary>
    public void Flush() => chunkStart = true;

    // ---------------------------------------------------------------- reset / prescaler
    public void Reset()
    {
        foreach (var c in ch) foreach (var s in c.S) { s.DT = DT_TABLE[0]; s.ARt = s.DRt = s.SRt = s.RRt = RATE_0; }
        OPNSetPris(12 * 12);
        mode = 0x30;   // OPNWriteMode(0x27, 0x30): timer reset; then reset_channel clears the mode
        mode = 0;
        foreach (var c in ch)
        {
            c.fc = 0; c.PAN = OUTD_CENTER;
            foreach (var s in c.S) { s.SEG = 0; s.eg_next = Eg.Release; s.evc = EG_OFF; s.eve = EG_OFF + 1; s.evs = 0; }
        }
        for (int i = 0xb6; i >= 0xb4; i--) { WriteReg(i, 0xc0); WriteReg(i | 0x100, 0xc0); }
        for (int i = 0xb2; i >= 0x30; i--) { WriteReg(i, 0); WriteReg(i | 0x100, 0); }
        for (int i = 0x26; i >= 0x20; i--) WriteReg(i, 0);
        dacen = 0;
        curChipDirty = true; chunkStart = true;
    }

    private void OPNSetPris(int pris)
    {
        freqbase = rate != 0 ? ((double)clock / rate) / pris : 0;
        for (int d = 0; d <= 3; d++)
            for (int i = 0; i <= 31; i++)
            {
                double r = OPN_DTTABLE[d * 32 + i] * freqbase * FREQ_RATE;
                DT_TABLE[d][i] = (int)r;
                DT_TABLE[d + 4][i] = (int)-r;
            }
        for (int i = 0; i < 4; i++) AR_TABLE[i] = DR_TABLE[i] = 0;
        for (int i = 4; i < 64; i++)
        {
            double r = freqbase;
            if (i < 60) r *= 1.0 + (i & 3) * 0.25;
            r *= 1 << ((i >> 2) - 1);
            r *= (double)(EG_ENT << ENV_BITS);
            AR_TABLE[i] = (int)(r / OPN_ARRATE);
            DR_TABLE[i] = (int)(r / OPN_DRRATE);
        }
        AR_TABLE[62] = EG_AED - 1; AR_TABLE[63] = EG_AED - 1;
        for (int i = 64; i < 94; i++) { AR_TABLE[i] = AR_TABLE[63]; DR_TABLE[i] = DR_TABLE[63]; }
        for (int i = 0; i < 2048; i++) FN_TABLE[i] = (uint)((double)i * freqbase * FREQ_RATE * (1 << 7) / 2);
        for (int i = 0; i < LFO_ENT; i++) LFO_wave[i] = i < LFO_ENT / 2 ? i * LFO_RATE / (LFO_ENT / 2) : (LFO_ENT - i) * LFO_RATE / (LFO_ENT / 2);
        double[] freq_table = { 3.98, 5.56, 6.02, 6.37, 6.88, 9.63, 48.1, 72.2 };
        for (int i = 0; i < 8; i++)
            LFO_FREQ[i] = rate != 0 ? (uint)((double)LFO_ENT * (1 << LFO_SHIFT) / (rate / freq_table[i] * (freqbase * rate / (8000000.0 / 144)))) : 0;
    }

    // ---------------------------------------------------------------- writes (YM2612Write)
    public void Write(int port, int v)
    {
        v &= 0xff;
        switch (port & 3)
        {
            case 0: address = v; break;
            case 1:
            {
                int addr = address;
                if ((addr & 0xf0) == 0x20)
                {
                    if (addr == 0x2a) { chunkStart = true; dacout = (v - 0x80) << (TL_BITS - 7); }
                    else if (addr == 0x2b) { dacen = v & 0x80; curChipDirty = true; }
                    else { chunkStart = true; WriteMode(addr, v); }
                }
                else { chunkStart = true; WriteReg(addr, v); }
                break;
            }
            case 2: address1 = v; break;
            case 3: chunkStart = true; WriteReg(address1 | 0x100, v); break;
        }
    }

    private void WriteMode(int r, int v)
    {
        switch (r)
        {
            case 0x22: LFOIncr = (v & 0x08) != 0 ? LFO_FREQ[v & 7] : 0; curChipDirty = true; break;
            case 0x27: mode = v; break;   // FMSetMode: the timers are the stock chip's job
            case 0x28:
            {
                int c = v & 0x03;
                if (c == 3) break;
                if ((v & 0x04) != 0) c += 3;
                var CH = ch[c];
                if (c == 2 && (mode & 0x80) != 0) break;
                if ((v & 0x10) != 0) KeyOn(CH, SLOT1); else KeyOff(CH, SLOT1);
                if ((v & 0x20) != 0) KeyOn(CH, SLOT2); else KeyOff(CH, SLOT2);
                if ((v & 0x40) != 0) KeyOn(CH, SLOT3); else KeyOff(CH, SLOT3);
                if ((v & 0x80) != 0) KeyOn(CH, SLOT4); else KeyOff(CH, SLOT4);
                break;
            }
        }
    }

    private void WriteReg(int r, int v)
    {
        int c = r & 3;
        if (c == 3) return;
        if (r >= 0x100) c += 3;
        var CH = ch[c];
        var SLOT = CH.S[(r >> 2) & 3];
        switch (r & 0xf0)
        {
            case 0x30:
                SLOT.mul = (uint)MUL_TABLE[v & 0x0f];
                SLOT.DT = DT_TABLE[(v >> 4) & 7];
                CH.S[SLOT1].Incr = unchecked((uint)-1);
                break;
            case 0x40:
            {
                int t = v & 0x7f; t = (t << 7) | t;
                SLOT.TL = (t * EG_ENT) >> 14;
                if (!((c == 2) && (mode & 0x80) != 0)) SLOT.TLL = SLOT.TL;
                break;
            }
            case 0x50:
                SLOT.KSR = 3 - (v >> 6);
                v &= 0x1f;
                if (v != 0) { SLOT.ARt = AR_TABLE; SLOT.ARo = v << 1; } else { SLOT.ARt = RATE_0; SLOT.ARo = 0; }
                SLOT.evsa = SLOT.ARt[SLOT.ARo + SLOT.ksr];
                if (SLOT.eg_next == Eg.AR) SLOT.evs = SLOT.evsa;
                CH.S[SLOT1].Incr = unchecked((uint)-1);
                break;
            case 0x60:
            {
                int d = v & 0x1f;
                if (d != 0) { SLOT.DRt = DR_TABLE; SLOT.DRo = d << 1; } else { SLOT.DRt = RATE_0; SLOT.DRo = 0; }
                SLOT.evsd = SLOT.DRt[SLOT.DRo + SLOT.ksr];
                if (SLOT.eg_next == Eg.DR) SLOT.evs = SLOT.evsd;
                SLOT.amon = (uint)(v >> 7);
                SLOT.ams = CH.ams * SLOT.amon;
                break;
            }
            case 0x70:
            {
                int d = v & 0x1f;
                if (d != 0) { SLOT.SRt = DR_TABLE; SLOT.SRo = d << 1; } else { SLOT.SRt = RATE_0; SLOT.SRo = 0; }
                SLOT.evss = SLOT.SRt[SLOT.SRo + SLOT.ksr];
                if (SLOT.eg_next == Eg.SR) SLOT.evs = SLOT.evss;
                break;
            }
            case 0x80:
                SLOT.SL = SL_TABLE[v >> 4];
                SLOT.RRt = DR_TABLE; SLOT.RRo = ((v & 0x0f) << 2) | 2;
                SLOT.evsr = SLOT.RRt[SLOT.RRo + SLOT.ksr];
                if (SLOT.eg_next == Eg.Release) SLOT.evs = SLOT.evsr;
                break;
            case 0x90: SLOT.SEG = v & 0x0f; break;   // stored, not supported (FM_SEG_SUPPORT 0)
            case 0xa0:
                switch ((r >> 2) & 3)
                {
                    case 0:
                    {
                        uint fn = ((uint)(CH.fn_h & 7) << 8) + (uint)v;
                        int blk = CH.fn_h >> 3;
                        CH.kcode = (blk << 2) | OPN_FKTABLE[fn >> 7];
                        CH.fc = FN_TABLE[fn] >> (7 - blk);
                        CH.S[SLOT1].Incr = unchecked((uint)-1);
                        break;
                    }
                    case 1: CH.fn_h = v & 0x3f; break;
                    case 2:
                        if (r < 0x100)
                        {
                            uint fn = ((uint)(SL3_fn_h[c] & 7) << 8) + (uint)v;
                            int blk = SL3_fn_h[c] >> 3;
                            SL3_kcode[c] = (blk << 2) | OPN_FKTABLE[fn >> 7];
                            SL3_fc[c] = FN_TABLE[fn] >> (7 - blk);
                            ch[2].S[SLOT1].Incr = unchecked((uint)-1);
                        }
                        break;
                    case 3: if (r < 0x100) SL3_fn_h[c] = v & 0x3f; break;
                }
                break;
            case 0xb0:
                switch ((r >> 2) & 3)
                {
                    case 0:
                    {
                        int feedback = (v >> 3) & 7;
                        CH.ALGO = v & 7;
                        CH.FB = feedback != 0 ? 8 + 1 - feedback : 0;
                        SetupConnection(CH);
                        break;
                    }
                    case 1:
                    {
                        // b0-2 PMS ... but the original reads (v>>4)&7 (kept)
                        double[] pmd_table = { 0, 3.4, 6.7, 10, 14, 20, 40, 80 };
                        int[] amd_table = { (int)(0 / EG_STEP), (int)(1.4 / EG_STEP), (int)(5.9 / EG_STEP), (int)(11.8 / EG_STEP) };
                        CH.pms = (int)((1.5 / 1200.0) * pmd_table[(v >> 4) & 0x07] * PMS_RATE);
                        CH.ams = (uint)amd_table[(v >> 4) & 0x03];
                        foreach (var s in CH.S) s.ams = CH.ams * s.amon;
                        CH.PAN = (v >> 6) & 0x03;
                        SetupConnection(CH);
                        break;
                    }
                }
                break;
        }
    }

    private static void SetupConnection(Chan CH)
    {
        int carrier = CH.PAN;
        const int in2 = 5, in3 = 6, in4 = 7;
        switch (CH.ALGO)
        {
            case 0: CH.c1 = in2; CH.c2 = in3; CH.c3 = in4; break;
            case 1: CH.c1 = in3; CH.c2 = in3; CH.c3 = in4; break;
            case 2: CH.c1 = in4; CH.c2 = in3; CH.c3 = in4; break;
            case 3: CH.c1 = in2; CH.c2 = in4; CH.c3 = in4; break;
            case 4: CH.c1 = in2; CH.c2 = carrier; CH.c3 = in4; break;
            case 5: CH.c1 = -1; CH.c2 = carrier; CH.c3 = carrier; break;
            case 6: CH.c1 = in2; CH.c2 = carrier; CH.c3 = carrier; break;
            case 7: CH.c1 = carrier; CH.c2 = carrier; CH.c3 = carrier; break;
        }
        CH.c4 = carrier;
    }

    // ---------------------------------------------------------------- envelope
    private static void EgNext(Slot s)
    {
        switch (s.eg_next)
        {
            case Eg.Release:
            case Eg.SR: s.evc = EG_OFF; s.eve = EG_OFF + 1; s.evs = 0; break;
            case Eg.DR: s.eg_next = Eg.SR; s.evc = s.SL; s.eve = EG_DED; s.evs = s.evss; break;
            case Eg.AR: s.eg_next = Eg.DR; s.evc = EG_DST; s.eve = s.SL; s.evs = s.evsd; break;
        }
    }

    private static void KeyOn(Chan CH, int n)
    {
        var s = CH.S[n];
        if (s.eg_next == Eg.Release)
        {
            s.Cnt = 0;
            s.eg_next = Eg.AR;
            s.evs = s.evsa;
            s.evc = EG_AST;
            s.eve = EG_AED;
        }
    }

    private static void KeyOff(Chan CH, int n)
    {
        var s = CH.S[n];
        if (s.eg_next != Eg.Release)
        {
            if (s.evc < EG_DST) s.evc = (ENV_CURVE[s.evc >> ENV_BITS] << ENV_BITS) + EG_DST;
            s.eg_next = Eg.Release;
            s.eve = EG_DED;
            s.evs = s.evsr;
        }
    }

    private uint CalcEg(Slot s)
    {
        if ((s.evc += s.evs) >= s.eve) EgNext(s);
        uint o = (uint)(s.TLL + ENV_CURVE[s.evc >> ENV_BITS]);
        if (s.ams != 0) o += s.ams * lfo_amd / LFO_RATE;
        return o;
    }

    private static int OpOut(int pg, uint eg) => TL_TABLE[SIN_TABLE[(pg / (0x1000000 / SIN_ENT)) & (SIN_ENT - 1)] + (int)eg];

    private void CalcCh(Chan CH)
    {
        int pms = lfo_pmd * CH.pms / LFO_RATE;
        var S = CH.S;
        unchecked
        {
            if (pms != 0)
            {
                for (int k = 0; k < 4; k++)
                {
                    var s = S[k == 0 ? SLOT1 : k == 1 ? SLOT2 : k == 2 ? SLOT3 : SLOT4];
                    acc[4 + k] = (int)(s.Cnt += s.Incr + (uint)((int)((uint)pms * s.Incr) / PMS_RATE));
                }
            }
            else
            {
                acc[4] = (int)(S[SLOT1].Cnt += S[SLOT1].Incr);
                acc[5] = (int)(S[SLOT2].Cnt += S[SLOT2].Incr);
                acc[6] = (int)(S[SLOT3].Cnt += S[SLOT3].Incr);
                acc[7] = (int)(S[SLOT4].Cnt += S[SLOT4].Incr);
            }
            uint e1 = CalcEg(S[SLOT1]), e2 = CalcEg(S[SLOT2]), e3 = CalcEg(S[SLOT3]), e4 = CalcEg(S[SLOT4]);
            if (e1 < (uint)EG_CUT_OFF)
            {
                if (CH.FB != 0)
                {
                    acc[4] += (CH.op1_out[0] + CH.op1_out[1]) >> CH.FB;
                    CH.op1_out[1] = CH.op1_out[0];
                }
                CH.op1_out[0] = OpOut(acc[4], e1);
                if (CH.c1 < 0) { acc[5] += CH.op1_out[0]; acc[6] += CH.op1_out[0]; acc[7] += CH.op1_out[0]; }
                else acc[CH.c1] += CH.op1_out[0];
            }
            if (e2 < (uint)EG_CUT_OFF) acc[CH.c2] += OpOut(acc[5], e2);
            if (e3 < (uint)EG_CUT_OFF) acc[CH.c3] += OpOut(acc[6], e3);
            if (e4 < (uint)EG_CUT_OFF) acc[CH.c4] += OpOut(acc[7], e4);
        }
    }

    private static void CalcFcSlot(Slot s, uint fc, int kc)
    {
        unchecked { s.Incr = fc * s.mul + (uint)s.DT[kc]; }
        int ksr = kc >> s.KSR;
        if (s.ksr != ksr)
        {
            s.ksr = ksr;
            s.evsa = s.ARt[s.ARo + ksr];
            s.evsd = s.DRt[s.DRo + ksr];
            s.evss = s.SRt[s.SRo + ksr];
            s.evsr = s.RRt[s.RRo + ksr];
        }
        s.TLL = s.TL;
    }

    private static void CalcFcount(Chan CH)
    {
        if (CH.S[SLOT1].Incr == unchecked((uint)-1))
            foreach (var s in CH.S) CalcFcSlot(s, CH.fc, CH.kcode);
    }

    /// <summary>The prologue of YM2612UpdateOne: refresh the cached chip state, recompute frequencies, step the LFO once.</summary>
    private void BeginUpdate()
    {
        if (curChipDirty)
        {
            curChipDirty = false;
            dacenC = dacen;
            LFOIncrC = LFOIncr;
            if (LFOIncrC == 0) { lfo_amd = 0; lfo_pmd = 0; }
        }
        CalcFcount(ch[0]); CalcFcount(ch[1]);
        if ((mode & 0xc0) != 0)
        {
            if (LFOIncrC != 0)
            {
                lfo_amd = (uint)LFO_wave[(LFOCnt += LFOIncrC) >> LFO_SHIFT];
                lfo_pmd = (int)lfo_amd - (LFO_RATE / 2);
            }
            var c2 = ch[2];
            if (c2.S[SLOT1].Incr == unchecked((uint)-1))
            {
                CalcFcSlot(c2.S[SLOT1], SL3_fc[1], SL3_kcode[1]);
                CalcFcSlot(c2.S[SLOT2], SL3_fc[2], SL3_kcode[2]);
                CalcFcSlot(c2.S[SLOT3], SL3_fc[0], SL3_kcode[0]);
                CalcFcSlot(c2.S[SLOT4], c2.fc, c2.kcode);
            }
        }
        else CalcFcount(ch[2]);
        CalcFcount(ch[3]); CalcFcount(ch[4]); CalcFcount(ch[5]);
        lastCh = dacenC != 0 ? 4 : 5;
    }

    /// <summary>One output sample (16-bit stereo), as one iteration of YM2612UpdateOne's loop.</summary>
    public void Sample(out short left, out short right)
    {
        if (chunkStart) { chunkStart = false; BeginUpdate(); }
        acc[OUTD_LEFT] = acc[OUTD_RIGHT] = acc[OUTD_CENTER] = 0;
        for (int c = 0; c <= lastCh; c++) CalcCh(ch[c]);
        if (dacenC != 0) unchecked { acc[ch[5].c4] += dacout; }
        int l = unchecked(acc[OUTD_LEFT] + acc[OUTD_CENTER]);
        if (l > FM_MAXOUT) l = FM_MAXOUT; else if (l < FM_MINOUT) l = FM_MINOUT;
        int r = unchecked(acc[OUTD_RIGHT] + acc[OUTD_CENTER]);
        if (r > FM_MAXOUT) r = FM_MAXOUT; else if (r < FM_MINOUT) r = FM_MINOUT;
        left = (short)(l >> FM_OUTSB); right = (short)(r >> FM_OUTSB);
    }
}
