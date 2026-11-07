// DOSBox 0.74 "compat" OPL2 emulator, ported to C# as an art instrument (faithful, flaws included).
//
// Source:   DOSBox 0.74, src/hardware/opl.cpp + opl.h (OPL2 build, i.e. without OPLTYPE_IS_OPL3), driven the way
//           src/hardware/adlib.cpp's OPL2::Handler drives it (oplemu=compat, oplmode=opl2).
// Version:  DOSBox SVN tags/RELEASE_0_74 (opl.cpp r3548; adlib.cpp $Id: adlib.cpp,v 1.42 2009-11-03).
// URL:      https://sourceforge.net/p/dosbox/code-0/HEAD/tree/tags/RELEASE_0_74/src/hardware/
// Authors:  The DOSBox Team (2002-2010); originally based on ADLIBEMU.C by Ken Silverman (1998-2001,
//           http://www.advsys.net/ken).
// Licence:  opl.cpp/opl.h: GNU LGPL 2.1 or later. (adlib.cpp, only consulted for the driver behaviour, is GPL 2+.)
//           Documented exception, see THIRD_PARTY_NOTICES.md section 2a.
//
// Rate: 44100 Hz, DOSBox 0.74's default "oplrate" (dosbox.cpp; adlib.cpp clamps it to >= 8000).
// BlockSamples: 44. adlib.cpp applies each register write to the chip at once, but the chip only renders when the
//   0.74 mixer calls OPL_CallBack, once per 1 ms timer tick (44.1 samples at 44100). Every write made during a tick
//   therefore lands at the start of the next rendered block: a ~1 ms write quantisation, modelled as 44-sample blocks.
// Timer registers 02h-04h are swallowed by adlib.cpp's Chip::Write and never reach opl.cpp; that is kept.
//
// Quirks kept (do NOT fix):
//  - Floating-point (double) envelopes: attack is a cubic polynomial in amp, decay/release are per-sample
//    multipliers; envelope "steps" only latch the displayed amplitude (step_amp) every 2^n standard samples, so
//    envelopes are staircased and the attack skip mask is approximate.
//  - Release stops only when amp <= 1e-8; release/decay multipliers start at 0.0 until their register is written
//    (an operator keyed with no AR/DR/SL/RR writes stays silent or collapses at once; a0..a3 = 0 means amp = 0).
//  - Envelope rates are recomputed only on frequency/KSR/AR/DR/RR writes; sustain level re-read only on 80h writes.
//  - Waveform tables: the half-length table for waveform 1 (abs-sine region 0-511) is the sine sampled at double
//    speed; waveform 3 ("pulse sine") is built from shifted sine pieces with +/-16384 offsets; waveform-6 mask is
//    WAVEPREC>>1 (512, not 511) - all as in the original table builder. Unbuilt table regions read as zero.
//  - Waveform select (E0h) is ignored unless 01h bit 5 was set when E0h was written (no re-apply later).
//  - Feedback = (last+current)*2^(fb+8)/2 integer, modulation added to the unsigned phase in 16.16 fixed point.
//  - Key-on resets the phase to the waveform's start offset; re-key while still active does nothing (act_state).
//  - Percussion: enable_operator ignores a PERC key-on when the operator is already NORMAL-active (the PERC bit is
//    never added), so drums layered over a held melodic note do not retrigger.
//  - Hi-hat/snare/cymbal use C rand()&1 noise (here: the MSVC rand() LCG, seed 1, for reproducibility) and a
//    phase-bit formula of the original; their vibrato tables are computed only when the operator is OFF
//    (the "== OF_TYPE_OFF" typo), so snare/cymbal vibrato never applies.
//  - Bass drum, tom, and the hh/sd/cy group are doubled (*2); the bass drum in AM mode plays only its carrier.
//  - Tremolo 3.7 Hz from a 53-entry table with two depths (4.8 dB / 1.2 dB, the latter with coarse stepping);
//    vibrato is an 8-step table scaled by freq_high*70/50000 integer maths.
//  - KSL table built by subtracting 8 per octave from the octave-7 row (clamped at 0).
//  - Output: hard clip to 16 bits, no filtering, mono.
//  - Global LFO counters and the noise generator advance per sample for the whole chip.

using System;

namespace NesEmulator.X86;

public sealed class Dosbox074CompatOplEngine : IOplEngine
{
    public string Id => "D074C";
    public int Rate => 44100;
    public int BlockSamples => 44;

    private const int FIXEDPT = 0x10000;
    private const int FIXEDPT_LFO = 0x1000000;
    private const int WAVEPREC = 1024;
    private const double INTFREQU = 14318180.0 / 288.0;
    private const int OF_TYPE_ATT = 0, OF_TYPE_DEC = 1, OF_TYPE_REL = 2, OF_TYPE_SUS = 3, OF_TYPE_SUS_NOKEEP = 4, OF_TYPE_OFF = 5;
    private const int ARC_TVS_KSR_MUL = 0x20, ARC_KSL_OUTLEV = 0x40, ARC_ATTR_DECR = 0x60, ARC_SUSL_RELR = 0x80,
        ARC_FREQ_NUM = 0xa0, ARC_KON_BNUM = 0xb0, ARC_PERC_MODE = 0xbd, ARC_FEEDBACK = 0xc0, ARC_WAVE_SEL = 0xe0;
    private const uint OP_ACT_OFF = 0, OP_ACT_NORMAL = 1, OP_ACT_PERC = 2;
    private const int BLOCKBUF_SIZE = 512;
    private const int VIBTAB_SIZE = 8;
    private const int TREMTAB_SIZE = 53;
    private const double TREM_FREQ = 3.7;

    private sealed class Op
    {
        public int cval, lastcval;
        public uint tcount, wfpos, tinc;
        public double amp, step_amp, vol, sustain_level;
        public int mfbi;
        public double a0, a1, a2, a3, decaymul, releasemul;
        public int op_state;
        public uint toff;
        public int freq_high;
        public int cur_wform; // offset into wavtable
        public uint cur_wmask;
        public uint act_state;
        public bool sus_keep, vibrato, tremolo;
        public uint generator_pos;
        public long cur_env_step, env_step_a, env_step_d, env_step_r;
        public byte step_skip_pos_a;
        public long env_step_skip_a;
    }

    private static readonly double[] kslmul = { 0.0, 0.5, 0.25, 1.0 };
    private static readonly double[] frqmul_tab = { 0.5, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 10, 12, 12, 15, 15 };
    private static readonly byte[] modulatorbase = { 0, 1, 2, 8, 9, 10, 16, 17, 18 };
    private static readonly byte[] regbase2modop = { 0, 1, 2, 0, 1, 2, 0, 0, 3, 4, 5, 3, 4, 5, 0, 0, 6, 7, 8, 6, 7, 8 };
    private static readonly byte[] regbase2op = { 0, 1, 2, 9, 10, 11, 0, 0, 3, 4, 5, 12, 13, 14, 0, 0, 6, 7, 8, 15, 16, 17 };
    private static readonly uint[] waveform = { WAVEPREC, WAVEPREC >> 1, WAVEPREC, (WAVEPREC * 3) >> 2, 0, 0, (WAVEPREC * 5) >> 2, WAVEPREC << 1 };
    private static readonly uint[] wavemask = { WAVEPREC - 1, WAVEPREC - 1, (WAVEPREC >> 1) - 1, (WAVEPREC >> 1) - 1, WAVEPREC - 1, ((WAVEPREC * 3) >> 2) - 1, WAVEPREC >> 1, WAVEPREC - 1 };
    private static readonly uint[] wavestart = { 0, WAVEPREC >> 1, 0, WAVEPREC >> 2, 0, 0, 0, WAVEPREC >> 3 };
    private static readonly double[] attackconst = { 1 / 2.82624, 1 / 2.25280, 1 / 1.88416, 1 / 1.59744 };
    private static readonly double[] decrelconst = { 1 / 39.28064, 1 / 31.41608, 1 / 26.17344, 1 / 22.44608 };
    private static readonly byte[] step_skip_mask = { 0xff, 0xfe, 0xee, 0xba, 0xaa };

    private static readonly short[] wavtable = new short[WAVEPREC * 3];
    private static readonly byte[,] kslev = new byte[8, 16];

    static Dosbox074CompatOplEngine()
    {
        for (int i = 0; i < (WAVEPREC >> 1); i++)
        {
            wavtable[(i << 1) + WAVEPREC] = (short)(16384 * Math.Sin((double)(i << 1) * Math.PI * 2 / WAVEPREC));
            wavtable[(i << 1) + 1 + WAVEPREC] = (short)(16384 * Math.Sin((double)((i << 1) + 1) * Math.PI * 2 / WAVEPREC));
            wavtable[i] = wavtable[(i << 1) + WAVEPREC];
        }
        for (int i = 0; i < (WAVEPREC >> 3); i++)
        {
            wavtable[i + (WAVEPREC << 1)] = (short)(wavtable[i + (WAVEPREC >> 3)] - 16384);
            wavtable[i + ((WAVEPREC * 17) >> 3)] = (short)(wavtable[i + (WAVEPREC >> 2)] + 16384);
        }
        byte[] k7 = { 0, 24, 32, 37, 40, 43, 45, 47, 48 };
        for (int i = 0; i < 9; i++) kslev[7, i] = k7[i];
        for (int i = 9; i < 16; i++) kslev[7, i] = (byte)(i + 41);
        for (int j = 6; j >= 0; j--)
            for (int i = 0; i < 16; i++)
            {
                int oct = kslev[j + 1, i] - 8;
                if (oct < 0) oct = 0;
                kslev[j, i] = (byte)oct;
            }
    }

    private readonly Op[] op = new Op[18];
    private readonly byte[] adlibreg = new byte[256];
    private readonly byte[] wave_sel = new byte[22];
    private readonly double[] frqmul = new double[16];
    private readonly int[] vib_table = new int[VIBTAB_SIZE];
    private readonly int[] trem_table = new int[TREMTAB_SIZE * 2];
    private readonly int[] vibval_const = new int[BLOCKBUF_SIZE];
    private readonly int[] tremval_const = new int[BLOCKBUF_SIZE];
    private readonly int[] vibval_var1 = new int[BLOCKBUF_SIZE];
    private readonly int[] vibval_var2 = new int[BLOCKBUF_SIZE];
    private readonly int[] outbufl = new int[BLOCKBUF_SIZE];
    private readonly int[] vib_lut = new int[BLOCKBUF_SIZE];
    private readonly int[] trem_lut = new int[BLOCKBUF_SIZE];
    private double recipsamp;
    private uint generator_add, vibtab_pos, vibtab_add, tremtab_pos, tremtab_add;
    private byte status;
    private uint randState = 1;

    public Dosbox074CompatOplEngine() { Reset(); }

    public void Reset() => Init(Rate);

    // MSVC rand(): the C library behind DOSBox's Windows builds.
    private int Rand() { randState = randState * 214013 + 2531011; return (int)((randState >> 16) & 0x7fff); }

    private void Init(int samplerate)
    {
        generator_add = (uint)(INTFREQU * FIXEDPT / samplerate);
        Array.Clear(adlibreg);
        Array.Clear(wave_sel);
        for (int i = 0; i < op.Length; i++)
            op[i] = new Op { op_state = OF_TYPE_OFF, act_state = OP_ACT_OFF, cur_wmask = wavemask[0], cur_wform = (int)waveform[0] };
        recipsamp = 1.0 / samplerate;
        for (int i = 15; i >= 0; i--) frqmul[i] = frqmul_tab[i] * INTFREQU / WAVEPREC * FIXEDPT * recipsamp;
        status = 0;
        vib_table[0] = 8; vib_table[1] = 4; vib_table[2] = 0; vib_table[3] = -4;
        for (int i = 4; i < VIBTAB_SIZE; i++) vib_table[i] = vib_table[i - 4] * -1;
        vibtab_add = (uint)(VIBTAB_SIZE * FIXEDPT_LFO / 8192 * INTFREQU / samplerate);
        vibtab_pos = 0;
        Array.Clear(vibval_const);
        var trem_table_int = new int[TREMTAB_SIZE];
        for (int i = 0; i < 14; i++) trem_table_int[i] = i - 13;
        for (int i = 14; i < 41; i++) trem_table_int[i] = -i + 14;
        for (int i = 41; i < 53; i++) trem_table_int[i] = i - 40 - 26;
        for (int i = 0; i < TREMTAB_SIZE; i++)
        {
            double trem_val1 = trem_table_int[i] * 4.8 / 26.0 / 6.0;
            double trem_val2 = (double)(trem_table_int[i] / 4) * 1.2 / 6.0 / 6.0;
            trem_table[i] = (int)(Math.Pow(2.0, trem_val1) * FIXEDPT);
            trem_table[TREMTAB_SIZE + i] = (int)(Math.Pow(2.0, trem_val2) * FIXEDPT);
        }
        tremtab_add = (uint)((double)TREMTAB_SIZE * TREM_FREQ * FIXEDPT_LFO / samplerate);
        tremtab_pos = 0;
        for (int i = 0; i < BLOCKBUF_SIZE; i++) tremval_const[i] = FIXEDPT;
        randState = 1;
    }

    // ---- operator stepping ----

    private void OperatorAdvance(Op o, int vib)
    {
        unchecked
        {
            o.wfpos = o.tcount;
            o.tcount += o.tinc;
            o.tcount += (uint)((int)o.tinc * vib / FIXEDPT);
            o.generator_pos += generator_add;
        }
    }

    private void OperatorAdvanceDrums(Op o1, int vib1, Op o2, int vib2, Op o3, int vib3)
    {
        unchecked
        {
            uint c1 = o1.tcount / FIXEDPT;
            uint c3 = o3.tcount / FIXEDPT;
            uint phasebit = (((c1 & 0x88) ^ ((c1 << 5) & 0x80)) | ((c3 ^ (c3 << 2)) & 0x20)) != 0 ? 0x02u : 0x00u;
            uint noisebit = (uint)(Rand() & 1);
            uint snare_phase_bit = ((o1.tcount / FIXEDPT) / 0x100) & 1;

            uint inttm = (phasebit << 8) | (0x34u << (int)(phasebit ^ (noisebit << 1)));
            o1.wfpos = inttm * FIXEDPT;
            o1.tcount += o1.tinc;
            o1.tcount += (uint)((int)o1.tinc * vib1 / FIXEDPT);
            o1.generator_pos += generator_add;

            inttm = ((1 + snare_phase_bit) ^ noisebit) << 8;
            o2.wfpos = inttm * FIXEDPT;
            o2.tcount += o2.tinc;
            o2.tcount += (uint)((int)o2.tinc * vib2 / FIXEDPT);
            o2.generator_pos += generator_add;

            inttm = (1 + phasebit) << 8;
            o3.wfpos = inttm * FIXEDPT;
            o3.tcount += o3.tinc;
            o3.tcount += (uint)((int)o3.tinc * vib3 / FIXEDPT);
            o3.generator_pos += generator_add;
        }
    }

    private static void OperatorOutput(Op o, int modulator, int trem)
    {
        if (o.op_state != OF_TYPE_OFF)
        {
            o.lastcval = o.cval;
            uint i = unchecked(o.wfpos + (uint)modulator) / FIXEDPT;
            o.cval = (int)(o.step_amp * o.vol * wavtable[o.cur_wform + (int)(i & o.cur_wmask)] * trem / 16.0);
        }
    }

    private static void OperatorSustain(Op o)
    {
        uint n = o.generator_pos / FIXEDPT;
        for (uint ct = 0; ct < n; ct++) o.cur_env_step++;
        o.generator_pos -= n * FIXEDPT;
    }

    private static void OperatorRelease(Op o)
    {
        if (o.amp > 0.00000001) o.amp *= o.releasemul;
        uint n = o.generator_pos / FIXEDPT;
        for (uint ct = 0; ct < n; ct++)
        {
            o.cur_env_step++;
            if ((o.cur_env_step & o.env_step_r) == 0)
            {
                if (o.amp <= 0.00000001)
                {
                    o.amp = 0.0;
                    if (o.op_state == OF_TYPE_REL) o.op_state = OF_TYPE_OFF;
                }
                o.step_amp = o.amp;
            }
        }
        o.generator_pos -= n * FIXEDPT;
    }

    private static void OperatorDecay(Op o)
    {
        if (o.amp > o.sustain_level) o.amp *= o.decaymul;
        uint n = o.generator_pos / FIXEDPT;
        for (uint ct = 0; ct < n; ct++)
        {
            o.cur_env_step++;
            if ((o.cur_env_step & o.env_step_d) == 0)
            {
                if (o.amp <= o.sustain_level)
                {
                    if (o.sus_keep) { o.op_state = OF_TYPE_SUS; o.amp = o.sustain_level; }
                    else o.op_state = OF_TYPE_SUS_NOKEEP;
                }
                o.step_amp = o.amp;
            }
        }
        o.generator_pos -= n * FIXEDPT;
    }

    private static void OperatorAttack(Op o)
    {
        o.amp = ((o.a3 * o.amp + o.a2) * o.amp + o.a1) * o.amp + o.a0;
        uint n = o.generator_pos / FIXEDPT;
        for (uint ct = 0; ct < n; ct++)
        {
            o.cur_env_step++;
            if ((o.cur_env_step & o.env_step_a) == 0)
            {
                if (o.amp > 1.0) { o.op_state = OF_TYPE_DEC; o.amp = 1.0; o.step_amp = 1.0; }
                o.step_skip_pos_a <<= 1;
                if (o.step_skip_pos_a == 0) o.step_skip_pos_a = 1;
                if ((o.step_skip_pos_a & o.env_step_skip_a) != 0) o.step_amp = o.amp;
            }
        }
        o.generator_pos -= n * FIXEDPT;
    }

    private static void OpFunc(Op o)
    {
        switch (o.op_state)
        {
            case OF_TYPE_ATT: OperatorAttack(o); break;
            case OF_TYPE_DEC: OperatorDecay(o); break;
            case OF_TYPE_REL: OperatorRelease(o); break;
            case OF_TYPE_SUS: OperatorSustain(o); break;
            case OF_TYPE_SUS_NOKEEP: OperatorRelease(o); break;
            default: break;
        }
    }

    // ---- parameter changes ----

    private void ChangeAttackrate(int regbase, Op o)
    {
        int attackrate = adlibreg[ARC_ATTR_DECR + regbase] >> 4;
        if (attackrate != 0)
        {
            double f = Math.Pow(2.0, (double)attackrate + (o.toff >> 2) - 1) * attackconst[o.toff & 3] * recipsamp;
            o.a0 = 0.0377 * f; o.a1 = 10.73 * f + 1; o.a2 = -17.57 * f; o.a3 = 7.42 * f;
            long step_skip = attackrate * 4 + o.toff;
            long steps = step_skip >> 2;
            o.env_step_a = (1L << (int)(steps <= 12 ? 12 - steps : 0)) - 1;
            long step_num = (step_skip <= 48) ? (4 - (step_skip & 3)) : 0;
            o.env_step_skip_a = step_skip_mask[step_num];
            if (step_skip >= 62) { o.a0 = 2.0; o.a1 = 0.0; o.a2 = 0.0; o.a3 = 0.0; }
        }
        else
        {
            o.a0 = 0.0; o.a1 = 1.0; o.a2 = 0.0; o.a3 = 0.0;
            o.env_step_a = 0; o.env_step_skip_a = 0;
        }
    }

    private void ChangeDecayrate(int regbase, Op o)
    {
        int decayrate = adlibreg[ARC_ATTR_DECR + regbase] & 15;
        if (decayrate != 0)
        {
            double f = -7.4493 * decrelconst[o.toff & 3] * recipsamp;
            o.decaymul = Math.Pow(2.0, f * Math.Pow(2.0, (double)(decayrate + (o.toff >> 2))));
            long steps = (decayrate * 4 + o.toff) >> 2;
            o.env_step_d = (1L << (int)(steps <= 12 ? 12 - steps : 0)) - 1;
        }
        else { o.decaymul = 1.0; o.env_step_d = 0; }
    }

    private void ChangeReleaserate(int regbase, Op o)
    {
        int releaserate = adlibreg[ARC_SUSL_RELR + regbase] & 15;
        if (releaserate != 0)
        {
            double f = -7.4493 * decrelconst[o.toff & 3] * recipsamp;
            o.releasemul = Math.Pow(2.0, f * Math.Pow(2.0, (double)(releaserate + (o.toff >> 2))));
            long steps = (releaserate * 4 + o.toff) >> 2;
            o.env_step_r = (1L << (int)(steps <= 12 ? 12 - steps : 0)) - 1;
        }
        else { o.releasemul = 1.0; o.env_step_r = 0; }
    }

    private void ChangeSustainlevel(int regbase, Op o)
    {
        int sl = adlibreg[ARC_SUSL_RELR + regbase] >> 4;
        o.sustain_level = sl < 15 ? Math.Pow(2.0, sl * -0.5) : 0.0;
    }

    private void ChangeWaveform(int regbase, Op o)
    {
        o.cur_wmask = wavemask[wave_sel[regbase]];
        o.cur_wform = (int)waveform[wave_sel[regbase]];
    }

    private void ChangeKeepsustain(int regbase, Op o)
    {
        o.sus_keep = (adlibreg[ARC_TVS_KSR_MUL + regbase] & 0x20) > 0;
        if (o.op_state == OF_TYPE_SUS) { if (!o.sus_keep) o.op_state = OF_TYPE_SUS_NOKEEP; }
        else if (o.op_state == OF_TYPE_SUS_NOKEEP) { if (o.sus_keep) o.op_state = OF_TYPE_SUS; }
    }

    private void ChangeVibrato(int regbase, Op o)
    {
        o.vibrato = (adlibreg[ARC_TVS_KSR_MUL + regbase] & 0x40) != 0;
        o.tremolo = (adlibreg[ARC_TVS_KSR_MUL + regbase] & 0x80) != 0;
    }

    private void ChangeFeedback(int chanbase, Op o)
    {
        int feedback = adlibreg[ARC_FEEDBACK + chanbase] & 14;
        o.mfbi = feedback != 0 ? (int)Math.Pow(2.0, (feedback >> 1) + 8) : 0;
    }

    private void ChangeFrequency(int chanbase, int regbase, Op o)
    {
        uint frn = (((uint)adlibreg[ARC_KON_BNUM + chanbase] & 3) << 8) + adlibreg[ARC_FREQ_NUM + chanbase];
        uint oct = ((uint)adlibreg[ARC_KON_BNUM + chanbase] >> 2) & 7;
        o.freq_high = (int)((frn >> 7) & 7);
        uint note_sel = ((uint)adlibreg[8] >> 6) & 1;
        o.toff = ((frn >> 9) & (note_sel ^ 1)) | ((frn >> 8) & note_sel);
        o.toff += oct << 1;
        if ((adlibreg[ARC_TVS_KSR_MUL + regbase] & 0x10) == 0) o.toff >>= 2;
        o.tinc = (uint)((double)(frn << (int)oct) * frqmul[adlibreg[ARC_TVS_KSR_MUL + regbase] & 15]);
        double vol_in = (adlibreg[ARC_KSL_OUTLEV + regbase] & 63) +
                        kslmul[adlibreg[ARC_KSL_OUTLEV + regbase] >> 6] * kslev[oct, frn >> 6];
        o.vol = Math.Pow(2.0, vol_in * -0.125 - 14);
        ChangeAttackrate(regbase, o);
        ChangeDecayrate(regbase, o);
        ChangeReleaserate(regbase, o);
    }

    private void EnableOperator(int regbase, Op o, uint act_type)
    {
        if (o.act_state == OP_ACT_OFF)
        {
            o.tcount = wavestart[wave_sel[regbase]] * FIXEDPT;
            o.op_state = OF_TYPE_ATT;
            o.act_state |= act_type;
        }
    }

    private static void DisableOperator(Op o, uint act_type)
    {
        if (o.act_state != OP_ACT_OFF)
        {
            o.act_state &= ~act_type;
            if (o.act_state == OP_ACT_OFF && o.op_state != OF_TYPE_OFF) o.op_state = OF_TYPE_REL;
        }
    }

    public void Write(int reg, byte val)
    {
        int idx = reg & 0xff;
        // adlib.cpp: Chip::Write takes the timer registers; they never reach opl.cpp.
        if (idx == 0x02 || idx == 0x03 || idx == 0x04) return;
        adlibreg[idx] = val;
        switch (idx & 0xf0)
        {
            case 0x00:
                if (idx == 0x04) { if ((val & 0x80) != 0) status = (byte)(status & ~0x60); else status = 0; }
                break;
            case ARC_TVS_KSR_MUL:
            case ARC_TVS_KSR_MUL + 0x10:
            {
                int num = idx & 7, b = (idx - ARC_TVS_KSR_MUL) & 0xff;
                if (num < 6 && b < 22)
                {
                    int modop = regbase2modop[b];
                    Op p = op[modop + (num < 3 ? 0 : 9)];
                    ChangeKeepsustain(b, p);
                    ChangeVibrato(b, p);
                    ChangeFrequency(modop, b, p);
                }
                break;
            }
            case ARC_KSL_OUTLEV:
            case ARC_KSL_OUTLEV + 0x10:
            {
                int num = idx & 7, b = (idx - ARC_KSL_OUTLEV) & 0xff;
                if (num < 6 && b < 22)
                {
                    int modop = regbase2modop[b];
                    ChangeFrequency(modop, b, op[modop + (num < 3 ? 0 : 9)]);
                }
                break;
            }
            case ARC_ATTR_DECR:
            case ARC_ATTR_DECR + 0x10:
            {
                int num = idx & 7, b = (idx - ARC_ATTR_DECR) & 0xff;
                if (num < 6 && b < 22)
                {
                    Op p = op[regbase2op[b]];
                    ChangeAttackrate(b, p);
                    ChangeDecayrate(b, p);
                }
                break;
            }
            case ARC_SUSL_RELR:
            case ARC_SUSL_RELR + 0x10:
            {
                int num = idx & 7, b = (idx - ARC_SUSL_RELR) & 0xff;
                if (num < 6 && b < 22)
                {
                    Op p = op[regbase2op[b]];
                    ChangeReleaserate(b, p);
                    ChangeSustainlevel(b, p);
                }
                break;
            }
            case ARC_FREQ_NUM:
            {
                int b = idx - ARC_FREQ_NUM;
                if (b < 9)
                {
                    int modbase = modulatorbase[b];
                    ChangeFrequency(b, modbase, op[b]);
                    ChangeFrequency(b, modbase + 3, op[b + 9]);
                }
                break;
            }
            case ARC_KON_BNUM:
            {
                if (idx == ARC_PERC_MODE)
                {
                    if ((val & 0x30) == 0x30)
                    {
                        EnableOperator(16, op[6], OP_ACT_PERC); ChangeFrequency(6, 16, op[6]);
                        EnableOperator(16 + 3, op[6 + 9], OP_ACT_PERC); ChangeFrequency(6, 16 + 3, op[6 + 9]);
                    }
                    else { DisableOperator(op[6], OP_ACT_PERC); DisableOperator(op[6 + 9], OP_ACT_PERC); }
                    if ((val & 0x28) == 0x28) { EnableOperator(17 + 3, op[16], OP_ACT_PERC); ChangeFrequency(7, 17 + 3, op[16]); }
                    else DisableOperator(op[16], OP_ACT_PERC);
                    if ((val & 0x24) == 0x24) { EnableOperator(18, op[8], OP_ACT_PERC); ChangeFrequency(8, 18, op[8]); }
                    else DisableOperator(op[8], OP_ACT_PERC);
                    if ((val & 0x22) == 0x22) { EnableOperator(18 + 3, op[8 + 9], OP_ACT_PERC); ChangeFrequency(8, 18 + 3, op[8 + 9]); }
                    else DisableOperator(op[8 + 9], OP_ACT_PERC);
                    if ((val & 0x21) == 0x21) { EnableOperator(17, op[7], OP_ACT_PERC); ChangeFrequency(7, 17, op[7]); }
                    else DisableOperator(op[7], OP_ACT_PERC);
                    break;
                }
                int b = idx - ARC_KON_BNUM;
                if (b < 9)
                {
                    int modbase = modulatorbase[b];
                    if ((val & 32) != 0)
                    {
                        EnableOperator(modbase, op[b], OP_ACT_NORMAL);
                        EnableOperator(modbase + 3, op[b + 9], OP_ACT_NORMAL);
                    }
                    else
                    {
                        DisableOperator(op[b], OP_ACT_NORMAL);
                        DisableOperator(op[b + 9], OP_ACT_NORMAL);
                    }
                    ChangeFrequency(b, modbase, op[b]);
                    ChangeFrequency(b, modbase + 3, op[b + 9]);
                }
                break;
            }
            case ARC_FEEDBACK:
            {
                int b = idx - ARC_FEEDBACK;
                if (b < 9) ChangeFeedback(b, op[b]);
                break;
            }
            case ARC_WAVE_SEL:
            case ARC_WAVE_SEL + 0x10:
            {
                int num = idx & 7, b = (idx - ARC_WAVE_SEL) & 0xff;
                if (num < 6 && b < 22 && (adlibreg[0x01] & 0x20) != 0)
                {
                    wave_sel[b] = (byte)(val & 3);
                    ChangeWaveform(b, op[regbase2modop[b] + (num < 3 ? 0 : 9)]);
                }
                break;
            }
        }
    }

    // ---- rendering ----

    private int[] VibFill(int[] buf, Op o, int n)
    {
        for (int i = 0; i < n; i++) buf[i] = (vib_lut[i] * o.freq_high / 8) * FIXEDPT * 70 / 50000;
        return buf;
    }

    public void Generate(short[] dest, int count)
    {
        int pos = 0;
        for (int cursmp = 0; cursmp < count; cursmp += BLOCKBUF_SIZE)
        {
            int endsamples = Math.Min(count - cursmp, BLOCKBUF_SIZE);
            Array.Clear(outbufl, 0, endsamples);

            int vib_tshift = (adlibreg[ARC_PERC_MODE] & 0x40) == 0 ? 1 : 0;
            for (int i = 0; i < endsamples; i++)
            {
                unchecked
                {
                    vibtab_pos += vibtab_add;
                    if (vibtab_pos / FIXEDPT_LFO >= VIBTAB_SIZE) vibtab_pos -= VIBTAB_SIZE * FIXEDPT_LFO;
                    vib_lut[i] = vib_table[vibtab_pos / FIXEDPT_LFO] >> vib_tshift;
                    tremtab_pos += tremtab_add;
                    if (tremtab_pos / FIXEDPT_LFO >= TREMTAB_SIZE) tremtab_pos -= TREMTAB_SIZE * FIXEDPT_LFO;
                    trem_lut[i] = (adlibreg[ARC_PERC_MODE] & 0x80) != 0
                        ? trem_table[tremtab_pos / FIXEDPT_LFO]
                        : trem_table[TREMTAB_SIZE + tremtab_pos / FIXEDPT_LFO];
                }
            }

            int[] vibval1, vibval2, vibval3, vibval4, tremval1, tremval2, tremval3, tremval4;

            if ((adlibreg[ARC_PERC_MODE] & 0x20) != 0)
            {
                // BassDrum
                Op m = op[6], c = op[15];
                if ((adlibreg[ARC_FEEDBACK + 6] & 1) != 0)
                {
                    if (c.op_state != OF_TYPE_OFF)
                    {
                        vibval1 = c.vibrato ? VibFill(vibval_var1, c, endsamples) : vibval_const;
                        tremval1 = c.tremolo ? trem_lut : tremval_const;
                        for (int i = 0; i < endsamples; i++)
                        {
                            OperatorAdvance(c, vibval1[i]); OpFunc(c); OperatorOutput(c, 0, tremval1[i]);
                            outbufl[i] += c.cval * 2;
                        }
                    }
                }
                else if (c.op_state != OF_TYPE_OFF || m.op_state != OF_TYPE_OFF)
                {
                    vibval1 = (m.vibrato && m.op_state != OF_TYPE_OFF) ? VibFill(vibval_var1, m, endsamples) : vibval_const;
                    vibval2 = (c.vibrato && c.op_state != OF_TYPE_OFF) ? VibFill(vibval_var2, c, endsamples) : vibval_const;
                    tremval1 = m.tremolo ? trem_lut : tremval_const;
                    tremval2 = c.tremolo ? trem_lut : tremval_const;
                    for (int i = 0; i < endsamples; i++)
                    {
                        OperatorAdvance(m, vibval1[i]); OpFunc(m);
                        OperatorOutput(m, unchecked((m.lastcval + m.cval) * m.mfbi / 2), tremval1[i]);
                        OperatorAdvance(c, vibval2[i]); OpFunc(c);
                        OperatorOutput(c, unchecked(m.cval * FIXEDPT), tremval2[i]);
                        outbufl[i] += c.cval * 2;
                    }
                }

                // TomTom
                if (op[8].op_state != OF_TYPE_OFF)
                {
                    Op t = op[8];
                    vibval3 = t.vibrato ? VibFill(vibval_var1, t, endsamples) : vibval_const;
                    tremval3 = t.tremolo ? trem_lut : tremval_const;
                    for (int i = 0; i < endsamples; i++)
                    {
                        OperatorAdvance(t, vibval3[i]); OpFunc(t); OperatorOutput(t, 0, tremval3[i]);
                        outbufl[i] += t.cval * 2;
                    }
                }

                // Snare/Hihat, Cymbal
                if (op[7].op_state != OF_TYPE_OFF || op[16].op_state != OF_TYPE_OFF || op[17].op_state != OF_TYPE_OFF)
                {
                    Op hh = op[7], sd = op[16], cy = op[17];
                    vibval1 = (hh.vibrato && hh.op_state != OF_TYPE_OFF) ? VibFill(vibval_var1, hh, endsamples) : vibval_const;
                    vibval2 = (sd.vibrato && sd.op_state == OF_TYPE_OFF) ? VibFill(vibval_var2, sd, endsamples) : vibval_const;
                    tremval1 = hh.tremolo ? trem_lut : tremval_const;
                    tremval2 = sd.tremolo ? trem_lut : tremval_const;
                    vibval4 = (cy.vibrato && cy.op_state == OF_TYPE_OFF) ? VibFill(vibval_var2, cy, endsamples) : vibval_const;
                    tremval4 = cy.tremolo ? trem_lut : tremval_const;
                    for (int i = 0; i < endsamples; i++)
                    {
                        OperatorAdvanceDrums(hh, vibval1[i], sd, vibval2[i], cy, vibval4[i]);
                        OpFunc(hh); OperatorOutput(hh, 0, tremval1[i]);
                        OpFunc(sd); OperatorOutput(sd, 0, tremval2[i]);
                        OpFunc(cy); OperatorOutput(cy, 0, tremval4[i]);
                        outbufl[i] += (hh.cval + sd.cval + cy.cval) * 2;
                    }
                }
            }

            for (int cur_ch = 8; cur_ch >= 0; cur_ch--)
            {
                if ((adlibreg[ARC_PERC_MODE] & 0x20) != 0 && cur_ch >= 6) continue;
                Op m = op[cur_ch], c = op[cur_ch + 9];
                if (c.op_state == OF_TYPE_OFF && m.op_state == OF_TYPE_OFF) continue;
                vibval1 = (m.vibrato && m.op_state != OF_TYPE_OFF) ? VibFill(vibval_var1, m, endsamples) : vibval_const;
                vibval2 = (c.vibrato && c.op_state != OF_TYPE_OFF) ? VibFill(vibval_var2, c, endsamples) : vibval_const;
                tremval1 = m.tremolo ? trem_lut : tremval_const;
                tremval2 = c.tremolo ? trem_lut : tremval_const;
                if ((adlibreg[ARC_FEEDBACK + cur_ch] & 1) != 0)
                {
                    // 2op additive synthesis
                    for (int i = 0; i < endsamples; i++)
                    {
                        OperatorAdvance(m, vibval1[i]); OpFunc(m);
                        OperatorOutput(m, unchecked((m.lastcval + m.cval) * m.mfbi / 2), tremval1[i]);
                        OperatorAdvance(c, vibval2[i]); OpFunc(c);
                        OperatorOutput(c, 0, tremval2[i]);
                        outbufl[i] += c.cval + m.cval;
                    }
                }
                else
                {
                    // 2op frequency modulation
                    for (int i = 0; i < endsamples; i++)
                    {
                        OperatorAdvance(m, vibval1[i]); OpFunc(m);
                        OperatorOutput(m, unchecked((m.lastcval + m.cval) * m.mfbi / 2), tremval1[i]);
                        OperatorAdvance(c, vibval2[i]); OpFunc(c);
                        OperatorOutput(c, unchecked(m.cval * FIXEDPT), tremval2[i]);
                        outbufl[i] += c.cval;
                    }
                }
            }

            for (int i = 0; i < endsamples; i++)
            {
                int v = outbufl[i];
                dest[pos++] = v < 32768 ? (v > -32769 ? (short)v : (short)-32768) : (short)32767;
            }
        }
    }
}
