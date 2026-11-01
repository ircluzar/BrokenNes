using System;

namespace NesEmulator.Sega;

// =====================================================================================================================
//  BlastEm 0.3.1 YM2612 FM engine, ported to C#.
//
//  Source: BlastEm v0.3.1 (Mercurial tag v0.3.1, 2015), ym2612.c + ym2612.h (and the clocking in blastem.c:
//  ym_init(sample_rate, MCLKS_NTSC, MCLKS_PER_YM = 7, ...), one operator per 6 YM clocks = 42 master clocks).
//  URL: https://retrodev.com/repos/blastem/archive/v0.3.1.tar.gz
//  Copyright 2013 Michael Pavone. License: GPL-3.0-or-later. This is a documented exception to the
//  repository's license policy - see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
//
//  A quick port. The bugs and shortcuts of 0.3.1 (all fixed in 0.4.0 / 0.5.0) are kept on purpose; they are the sound:
//   - attack curve: env += (~env * inc) >> 4 on a 12-bit 4.8 envelope (wrong shape; "overflow" snaps it to 0)
//   - sustain level compared against (SL << 4) << 4 with no special case for SL = 15 (wrong sustain level)
//   - key-on bits 0x10..0x80 mapped to the operators in register (slot) order 1,3,2,4: bit 5 keys op3, bit 6 keys op2
//   - LFO: AM step/shift table {8,3,1,0}, PM table indexed by fnum & 0x7F0, PM added to the phase counter raw (broken)
//   - no SSG-EG, no CSM; the ch3 supplemental fnum write always recomputes with the wrong operator index (2*4)
//   - 9-bit-ish output (& 0x3FE0 with a lopsided clamp), * 2 / 3 per channel, summed into int16 (wraps, no clip)
//   - DAC replaces channel 6's output with (value - 0x80) << 6 at write time; channel 6 FM stops updating
//  Left out: the timers, the busy flag and the status register (BrokenNes's stock YM2612 keeps running beside this
//  engine and answers the game), the WAVE logs and the debug prints. One stereo sample per 24 operator updates
//  (1008 master clocks, the stock hook's rate) instead of BlastEm's own resampling to the host rate.
// =====================================================================================================================

/// <summary>The BlastEm 0.3.1 YM2612: feed it port writes, pull one stereo sample per 144 YM clocks.</summary>
public sealed class BlastemYm2612
{
    private const int NUM_CHANNELS = 6, NUM_OPERATORS = 24;
    private const int YM_PART1_START = 0x21, YM_PART2_START = 0x30, YM_REG_END = 0xB8;
    private const int PHASE_ATTACK = 0, PHASE_DECAY = 1, PHASE_SUSTAIN = 2, PHASE_RELEASE = 3;
    private const int MAX_ENVELOPE = 0xFFC;
    private const int YM_VOLUME_MULTIPLIER = 2, YM_VOLUME_DIVIDER = 3, YM_MOD_SHIFT = 1;

    private sealed class Op
    {
        public uint phase_inc, phase_counter;
        public ushort envelope;
        public short output;
        public ushort total_level, sustain_level;
        public readonly byte[] rates = new byte[4];
        public byte key_scaling, multiple, detune, am, env_phase;
    }
    private sealed class Chan
    {
        public ushort fnum;
        public short output, op1_old;
        public byte block_fnum_latch, block, keycode, algorithm, feedback, ams, pms, lr;
    }
    private sealed class Supp { public ushort fnum; public byte block, block_fnum_latch, keycode; }

    private readonly Op[] operators = new Op[NUM_OPERATORS];
    private readonly Chan[] channels = new Chan[NUM_CHANNELS];
    // BlastEm 0.3.1 declares ch3_supp[3] but UpdatePhaseInc indexes it with (op - 8) ^ 2, which is 3 for operator 9: C reads past the
    // array into the next fields (ch3_mode, current_op, ...). C# would throw, so a 4th slot that is never written stands in for that
    // memory (it reads as zeros: operator 9 in channel-3 special mode gets frequency 0). Found by Workshop --apu-fuzz.
    private readonly Supp[] ch3_supp = new Supp[4];
    private ushort env_counter;
    private byte ch3_mode, current_op, current_env_op, dac_enable, lfo_enable, lfo_freq, lfo_counter, lfo_am_step, lfo_pm_step;
    private byte selected_reg, selected_part;

    // ---- tables (ym_init)
    private static readonly ushort[] sine_table = new ushort[512];
    private static readonly ushort[] pow_table = new ushort[1 << 13];
    private static readonly ushort[] rate_table = new ushort[64 * 8];
    private static readonly short[] lfo_pm_table = new short[128 * 32 * 8];
    private static readonly ushort[] rate_table_base = {
        0,1,0,1,0,1,0,1, 0,1,0,1,1,1,0,1, 0,1,1,1,0,1,1,1, 0,1,1,1,1,1,1,1,
        1,1,1,1,1,1,1,1, 1,1,1,2,1,1,1,2, 1,2,1,2,1,2,1,2, 1,2,2,2,1,2,2,2,
    };
    private static readonly byte[] lfo_timer_values = { 108, 77, 71, 67, 62, 44, 8, 5 };
    private static readonly byte[,] lfo_pm_base = {
        {0,0,0,0,0,0,0,0}, {0,0,0,0,4,4,4,4}, {0,0,0,4,4,4,8,8}, {0,0,4,4,8,8,0xc,0xc},
        {0,0,4,8,8,8,0xc,0x10}, {0,0,8,0xc,0x10,0x10,0x14,0x18}, {0,0,0x10,0x18,0x20,0x20,0x28,0x30}, {0,0,0x20,0x30,0x40,0x40,0x50,0x60},
    };
    private static readonly ushort[] ams_shift = { 8, 3, 1, 0 };
    private static readonly byte[] fnum_to_keycode = { 0,0,0,0,0,0,0,1, 2,3,3,3,3,3,3,3 };
    private static readonly uint[,] detune_table = {
        {0,0,1,2},{0,0,1,2},{0,0,1,2},{0,0,1,2},{0,1,2,2},{0,1,2,3},{0,1,2,3},{0,1,2,3},
        {0,1,2,4},{0,1,3,4},{0,1,3,4},{0,1,3,5},{0,2,4,5},{0,2,4,6},{0,2,4,6},{0,2,5,7},
        {0,2,5,8},{0,3,6,8},{0,3,6,9},{0,3,7,10},{0,4,8,11},{0,4,8,12},{0,4,9,13},{0,5,10,14},
        {0,5,11,16},{0,6,12,17},{0,6,13,19},{0,7,14,20},{0,8,16,22},{0,8,16,22},{0,8,16,22},{0,8,16,22},
    };

    private static ushort RoundFixed(double v, int bits) => (ushort)(v * (1 << bits) + 0.5);

    static BlastemYm2612()
    {
        for (int i = 0; i < 512; i++)
        {
            double sine = Math.Sin(((double)(i * 2 + 1) / 512) * (Math.PI / 2));
            sine_table[i] = RoundFixed(-Math.Log2(sine), 8);
        }
        for (int i = 0; i < pow_table.Length; i++)
        {
            double linear = Math.Pow(2, -((double)((i & 0xFF) + 1) / 256.0));
            int tmp = RoundFixed(linear, 11);
            int shift = (i >> 8) - 2;
            if (shift < 0) tmp <<= -shift; else tmp >>= shift;
            pow_table[i] = (ushort)tmp;
        }
        for (int rate = 0; rate < 64; rate++)
            for (int cycle = 0; cycle < 8; cycle++)
            {
                int value;
                if (rate < 2) value = 0;
                else if (rate >= 60) value = 8;
                else if (rate < 8) value = rate_table_base[((rate & 6) == 6 ? 16 : 0) + cycle];
                else if (rate < 48) value = rate_table_base[(rate & 0x3) * 8 + cycle];
                else value = rate_table_base[32 + (rate & 0x3) * 8 + cycle] << ((rate - 48) >> 2);
                rate_table[rate * 8 + cycle] = (ushort)value;
            }
        for (int freq = 0; freq < 128; freq++)
            for (int pms = 0; pms < 8; pms++)
                for (int step = 0; step < 32; step++)
                {
                    short value = 0;
                    for (int bit = 0x40, shift = 0; bit > 0; bit >>= 1, shift++)
                        if ((freq & bit) != 0)
                            value += (short)(lfo_pm_base[pms, (step & 0x8) != 0 ? (7 - step) & 7 : step & 7] >> shift);
                    if ((step & 0x10) != 0) value = (short)-value;
                    lfo_pm_table[freq * 256 + pms * 32 + step] = value;
                }
    }

    public BlastemYm2612()
    {
        for (int i = 0; i < NUM_OPERATORS; i++) operators[i] = new Op();
        for (int i = 0; i < NUM_CHANNELS; i++) channels[i] = new Chan();
        for (int i = 0; i < ch3_supp.Length; i++) ch3_supp[i] = new Supp();
        Reset();
    }

    /// <summary>ym_init's state (memset, envelopes at max, release phase, LR on).</summary>
    public void Reset()
    {
        foreach (var o in operators)
        {
            o.phase_inc = o.phase_counter = 0; o.output = 0; o.total_level = o.sustain_level = 0; Array.Clear(o.rates);
            o.key_scaling = o.multiple = o.detune = o.am = 0;
            o.envelope = MAX_ENVELOPE; o.env_phase = PHASE_RELEASE;
        }
        foreach (var c in channels)
        {
            c.fnum = 0; c.output = c.op1_old = 0; c.block_fnum_latch = c.block = c.keycode = c.algorithm = c.feedback = c.ams = c.pms = 0;
            c.lr = 0xC0;
        }
        foreach (var s in ch3_supp) { s.fnum = 0; s.block = s.block_fnum_latch = s.keycode = 0; }
        env_counter = 0; ch3_mode = current_op = current_env_op = dac_enable = lfo_enable = lfo_freq = lfo_counter = lfo_am_step = lfo_pm_step = 0;
        selected_reg = selected_part = 0;
    }

    /// <summary>A write to port 0-3 (as blastem.c maps it: bit 0 data, bit 1 part II).</summary>
    public void WritePort(int port, byte value)
    {
        if ((port & 1) != 0) DataWrite(value);
        else { selected_reg = value; selected_part = (byte)((port & 2) != 0 ? 1 : 0); }
    }

    /// <summary>24 operator updates (one 144-YM-clock period of ym_run), then the sample ym_run would write to its buffer.</summary>
    public void RunSample(out int left, out int right)
    {
        for (int i = 0; i < NUM_OPERATORS; i++) Step();
        int l = 0, r = 0;
        for (int i = 0; i < NUM_CHANNELS; i++)
        {
            short value = channels[i].output;
            if (value > 0x1FE0) value = 0x1FE0;
            else if (value < -0x1FF0) value = -0x1FF0;
            else
            {
                value = (short)(value & 0x3FE0);
                if ((value & 0x2000) != 0) value = (short)(value | unchecked((short)0xC000));
            }
            if ((channels[i].lr & 0x80) != 0) l = (short)(l + value * YM_VOLUME_MULTIPLIER / YM_VOLUME_DIVIDER);
            if ((channels[i].lr & 0x40) != 0) r = (short)(r + value * YM_VOLUME_MULTIPLIER / YM_VOLUME_DIVIDER);
        }
        left = l; right = r;
    }

    private void Step()
    {
        if (current_op == 0)
        {
            // (timers left to the stock chip)
            if (lfo_enable != 0)
            {
                if (lfo_counter != 0) lfo_counter--;
                else
                {
                    lfo_counter = lfo_timer_values[lfo_freq];
                    lfo_am_step += 2;
                    lfo_am_step &= 0xFE;
                    lfo_pm_step = (byte)(lfo_am_step / 8);
                }
            }
        }
        // envelope generator
        if (current_op % 3 == 0)
        {
            uint env_cyc = env_counter;
            int opn = current_env_op;
            var oper = operators[opn];
            var channel = channels[opn / 4];
            int rate;
            for (;;)
            {
                rate = oper.rates[oper.env_phase];
                if (rate != 0)
                {
                    int ks = channel.keycode >> oper.key_scaling;
                    rate = (byte)(rate * 2 + ks);
                    if (rate > 63) rate = 63;
                }
                if (rate >= 62 && oper.env_phase == PHASE_ATTACK) { oper.env_phase = PHASE_DECAY; oper.envelope = 0; }
                else break;
            }
            int cycle_shift = rate < 0x30 ? ((0x2F - rate) >> 2) : 0;
            if ((env_cyc & ((1u << cycle_shift) - 1)) == 0)
            {
                uint update_cycle = env_cyc >> cycle_shift & 0x7;
                ushort envelope_inc = (ushort)(rate_table[rate * 8 + update_cycle] << 2);
                if (oper.env_phase == PHASE_ATTACK)
                {
                    ushort old_env = oper.envelope;
                    oper.envelope = (ushort)(oper.envelope + ((~(int)oper.envelope * envelope_inc) >> 4));
                    if (oper.envelope > old_env) oper.envelope = 0;
                    if (oper.envelope == 0) oper.env_phase = PHASE_DECAY;
                }
                else
                {
                    oper.envelope = (ushort)(oper.envelope + envelope_inc);
                    if (oper.envelope > MAX_ENVELOPE) oper.envelope = MAX_ENVELOPE;
                    if (oper.env_phase == PHASE_DECAY && oper.envelope >= oper.sustain_level) oper.env_phase = PHASE_SUSTAIN;
                }
            }
            current_env_op++;
            if (current_env_op == NUM_OPERATORS) { current_env_op = 0; env_counter++; }
        }

        // phase generator / operator output
        int chn = current_op / 4;
        if (chn != 5 || dac_enable == 0)
        {
            int op = current_op;
            var o = operators[op];
            var chan = channels[chn];
            ushort phase = (ushort)(o.phase_counter >> 10 & 0x3FF);
            o.phase_counter += o.phase_inc;
            if (chan.pms != 0)
            {
                short lfo_mod = lfo_pm_table[(chan.fnum & 0x7F0) * 16 + chan.pms + lfo_pm_step];
                if (o.multiple != 0) lfo_mod = (short)(lfo_mod * o.multiple);
                else lfo_mod >>= 1;
                o.phase_counter = unchecked(o.phase_counter + (uint)lfo_mod);
            }
            short mod = 0;
            switch (op % 4)
            {
                case 0:
                    if (chan.feedback != 0) mod = (short)((chan.op1_old + o.output) >> (10 - chan.feedback));
                    break;
                case 1:
                    switch (chan.algorithm)
                    {
                        case 0: case 2: mod = (short)(operators[op + 1].output >> YM_MOD_SHIFT); break;
                        case 1: mod = (short)((operators[op - 1].output + operators[op + 1].output) >> YM_MOD_SHIFT); break;
                        case 5: mod = (short)(operators[op - 1].output >> YM_MOD_SHIFT); break;
                    }
                    break;
                case 2:
                    if (chan.algorithm != 1 && chan.algorithm != 2 && chan.algorithm != 7) mod = (short)(operators[op - 2].output >> YM_MOD_SHIFT);
                    break;
                case 3:
                    switch (chan.algorithm)
                    {
                        case 0: case 1: case 4: mod = (short)(operators[op - 2].output >> YM_MOD_SHIFT); break;
                        case 2: mod = (short)((operators[op - 3].output + operators[op - 2].output) >> YM_MOD_SHIFT); break;
                        case 3: mod = (short)((operators[op - 1].output + operators[op - 2].output) >> YM_MOD_SHIFT); break;
                        case 5: mod = (short)(operators[op - 3].output >> YM_MOD_SHIFT); break;
                    }
                    break;
            }
            ushort env = (ushort)(o.envelope + o.total_level);
            if (o.am != 0)
            {
                int base_am = ((lfo_am_step & 0x80) != 0 ? lfo_am_step : ~lfo_am_step) & 0x7E;
                env = (ushort)(env + (base_am >> ams_shift[chan.ams]));
            }
            if (env > MAX_ENVELOPE) env = MAX_ENVELOPE;
            phase = (ushort)(phase + mod);
            short output = (short)pow_table[sine_table[phase & 0x1FF] + env];
            if ((phase & 0x200) != 0) output = (short)-output;
            if (op % 4 == 0) chan.op1_old = o.output;
            o.output = output;
            if (op % 4 == 3)
            {
                if (chan.algorithm < 4) chan.output = o.output;
                else if (chan.algorithm == 4) chan.output = (short)(o.output + operators[chn * 4 + 2].output);
                else
                {
                    output = 0;
                    for (int k = (chan.algorithm == 7 ? 0 : 1) + chn * 4; k < (chn + 1) * 4; k++) output = (short)(output + operators[k].output);
                    chan.output = output;
                }
            }
        }
        current_op++;
        if (current_op == NUM_OPERATORS) current_op = 0;
    }

    private void UpdatePhaseInc(Op oper, int op)
    {
        int chan_num = op / 4;
        var channel = channels[chan_num];
        uint inc, detune;
        if (chan_num == 2 && ch3_mode != 0 && op < 2 * 4 + 3)
        {
            int index = (op - 2 * 4) ^ 2;
            inc = ch3_supp[index].fnum;
            if (ch3_supp[index].block == 0) inc >>= 1; else inc <<= ch3_supp[index].block - 1;
            detune = detune_table[ch3_supp[index].keycode, oper.detune & 0x3];
        }
        else
        {
            inc = channel.fnum;
            if (channel.block == 0) inc >>= 1; else inc <<= channel.block - 1;
            detune = detune_table[channel.keycode, oper.detune & 0x3];
        }
        if ((oper.detune & 0x4) != 0) { inc = unchecked(inc - detune); inc &= 0x1FFFF; }
        else inc += detune;
        if (oper.multiple != 0) { inc *= oper.multiple; inc &= 0xFFFFF; }
        else inc >>= 1;
        oper.phase_inc = inc;
    }

    private void DataWrite(byte value)
    {
        int reg = selected_reg;
        if (reg >= YM_REG_END) return;
        if (selected_part != 0) { if (reg < YM_PART2_START) return; }
        else { if (reg < YM_PART1_START) return; }
        if (reg < 0x30)
        {
            switch (reg)
            {
                case 0x22:
                    lfo_enable = (byte)(value & 0x8);
                    if (lfo_enable == 0) lfo_am_step = lfo_pm_step = 0;
                    lfo_freq = (byte)(value & 0x7);
                    break;
                case 0x27:
                {
                    byte old_mode = ch3_mode;
                    ch3_mode = (byte)(value & 0xC0);
                    if (ch3_mode != old_mode)
                    {
                        UpdatePhaseInc(operators[8], 8); UpdatePhaseInc(operators[9], 9); UpdatePhaseInc(operators[10], 10);
                    }
                    break;
                }
                case 0x28:
                {
                    int channel = value & 0x7;
                    if (channel != 3 && channel != 7)
                    {
                        if (channel > 2) channel--;
                        // 0.3.1's mapping: bit 4..7 to the operators in slot order (1, 3, 2, 4)
                        for (int op = channel * 4, bit = 0x10; op < (channel + 1) * 4; op++, bit <<= 1)
                        {
                            if ((value & bit) != 0)
                            {
                                if (operators[op].env_phase == PHASE_RELEASE) { operators[op].phase_counter = 0; operators[op].env_phase = PHASE_ATTACK; }
                            }
                            else operators[op].env_phase = PHASE_RELEASE;
                        }
                    }
                    break;
                }
                case 0x2A:
                    if (dac_enable != 0) channels[5].output = (short)((value - 0x80) << 6);
                    break;
                case 0x2B:
                    dac_enable = (byte)(value & 0x80);
                    break;
            }
        }
        else if (reg < 0xA0)
        {
            int op = selected_part != 0 ? NUM_OPERATORS / 2 : 0;
            if ((reg & 0x3) != 0x3)
            {
                op += 4 * (reg & 0x3) + ((reg & 0xC) / 4);
                var o = operators[op];
                switch (reg & 0xF0)
                {
                    case 0x30: o.detune = (byte)(value >> 4 & 0x7); o.multiple = (byte)(value & 0xF); UpdatePhaseInc(o, op); break;
                    case 0x40: o.total_level = (ushort)((value & 0x7F) << 5); break;
                    case 0x50: o.key_scaling = (byte)(3 - (value >> 6)); o.rates[PHASE_ATTACK] = (byte)(value & 0x1F); break;
                    case 0x60: o.am = (byte)(value & 0x80); o.rates[PHASE_DECAY] = (byte)(value & 0x1F); break;
                    case 0x70: o.rates[PHASE_SUSTAIN] = (byte)(value & 0x1F); break;
                    case 0x80: o.rates[PHASE_RELEASE] = (byte)((value & 0xF) << 1 | 1); o.sustain_level = (ushort)((value & 0xF0) << 4); break;
                }
            }
        }
        else
        {
            int channel = reg & 0x3;
            if (channel != 3)
            {
                if (selected_part != 0) channel += 3;
                var c = channels[channel];
                switch (reg & 0xFC)
                {
                    case 0xA0:
                        c.block = (byte)(c.block_fnum_latch >> 3 & 0x7);
                        c.fnum = (ushort)((c.block_fnum_latch & 0x7) << 8 | value);
                        c.keycode = (byte)(c.block << 2 | fnum_to_keycode[c.fnum >> 7]);
                        for (int k = 0; k < 4; k++) UpdatePhaseInc(operators[channel * 4 + k], channel * 4 + k);
                        break;
                    case 0xA4: c.block_fnum_latch = value; break;
                    case 0xA8:
                        if (channel < 3)
                        {
                            var s = ch3_supp[channel];
                            s.block = (byte)(s.block_fnum_latch >> 3 & 0x7);
                            s.fnum = (ushort)((s.block_fnum_latch & 0x7) << 8 | value);
                            s.keycode = (byte)(s.block << 2 | fnum_to_keycode[s.fnum >> 7]);
                            // 0.3.1 passes op index 2*4 for operator 2*4 + channel (kept)
                            if (ch3_mode != 0) UpdatePhaseInc(operators[2 * 4 + channel], 2 * 4);
                        }
                        break;
                    case 0xAC:
                        if (channel < 3) ch3_supp[channel].block_fnum_latch = value;
                        break;
                    case 0xB0: c.algorithm = (byte)(value & 0x7); c.feedback = (byte)(value >> 3 & 0x7); break;
                    case 0xB4: c.pms = (byte)((value & 0x7) * 32); c.ams = (byte)(value >> 4 & 0x3); c.lr = (byte)(value & 0xC0); break;
                }
            }
        }
    }
}
