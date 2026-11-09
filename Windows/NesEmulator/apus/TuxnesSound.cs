/*
 * TuxnesSound - a quick port of TuxNES's sound engine (src sound.c, "New Sound code" by Paul Zaremba et al.).
 *
 * Source project : TuxNES (Linux NES emulator, 1999-2001)
 * Version        : 0.75 - commit f537b4b ("tuxnes 0.75") of https://github.com/tuxnes/tuxnes
 *                  (sound.c $Id: sound.c,v 1.36 2001/04/11 21:45:47 tmmm Exp $)
 * Files ported   : sound.c (UpdateAudio, SoundEvent, InitAudio's tables, shift_register15), sound.h; the call sites that
 *                  shape it: io.c (every $4000-$4017 write except $4014 -> SoundEvent stamped with CLOCK), consts.h
 *                  (VBL 27428, CPF 29781), x86.S (CLOCK = CPU cycles into the frame, wraps at CPF), renderer.c / x11.c
 *                  (UpdateAudio once per frame from the display update at V-blank), emu.c (default format "8" = unsigned 8-bit)
 * URL            : https://github.com/tuxnes/tuxnes
 * Copyright      : Copyright (C) the TuxNES authors (Paul Zaremba, Eric Jacobs, Ben Sittler, Mike Melanson, tmmm et al.,
 *                  see AUTHORS); sound ideas from DarcNES (Alastair Bridgewater), noise shift register from Nosefart.
 * License        : GNU General Public License, version 2 or (at your option) any later version. This file is a modified
 *                  (translated to C#) version of sound.c.
 *
 * This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
 *
 * Quirks kept (they ARE the sound):
 *  - Defaults: 44100 Hz, unsigned 8-bit ("8"), mono, 735 samples rendered in one go once per frame at V-blank (CLOCK 27428).
 *  - Register writes are stamped with CLOCK (CPU cycles into the frame, 0..29780) but compared against a sample counter
 *    that advances 152 per sample (111860.78*60/44100): a whole frame's writes are squeezed into the first ~196 of the
 *    735 samples (time compressed 3.7x), and the queue is consumed in FIFO order (a late V-blank write blocks the rest).
 *  - Counters run per output sample: 60 Hz = 735 samples, "120 Hz" = 368, "240 Hz" = 186 samples, re-synchronised on the
 *    60 Hz tick. Length table straight from NESSOUND.txt, counted at 60 Hz, not 120.
 *  - Envelopes: volume 0x78 counted down in steps of 8 (15 steps) whenever the per-channel counter hits 0 - including
 *    in constant-volume mode, where the counter (period 0+1...) still ticks the hidden decay. Writes to $4003/$4007/$400F
 *    restart at 0x78 even in constant mode (the register value is used for output then).
 *  - Square 2's negative sweep has no "+1" (square 1's has). Sweeps stop at a carry and squares mute below period 8.
 *  - Squares/triangle keep running after they are silenced while the phase is greater than one step (the pop "fix").
 *  - Unsigned rendering: squares and noise add volume or 0 (never negative), triangle 0..120 in 32 steps, DMC delta<<3
 *    (0..504), the sum / 5 truncated into an unsigned byte.
 *  - Triangle: the nosefart-inspired delay hack (linear counter starts counting half a frame after a $400B write);
 *    the linear counter reloads every sample until counting mode starts.
 *  - Noise: Nosefart's 15-bit shift register with taps 0x02 / 0x40, at most one shift per output sample (rates above
 *    44.1 kHz alias down), static seed 0x4000 never reset.
 *  - DMC: own sample reader at a "fetch rate" table in its own units; $4013 length = value*16 (no +1); address wraps
 *    $FFFF -> $0000; the output is only added while the sample plays, so it drops to 0 when a sample ends ("DMC pops
 *    periodically").
 * Deviations (to fit the host, not to improve the sound):
 *  - The frame is a 29781-cycle counter driven by the host's CPU cycles, not the real PPU's V-blank.
 *  - The unsigned 8-bit stream is centred by a one-pole DC blocker (a sound card's AC coupling).
 *  - 64-bit "unsigned long" (as on a modern Linux build).
 */
using System;

namespace NesEmulator
{
    /// <summary>The TuxNES 0.75 sound engine (see the file header). Fed register writes, clocked in CPU cycles.</summary>
    public sealed class TuxnesSound
    {
        public const int SampleRate = 44100;
        private const int VBL = 27428, CPF = 29781;
        private const int SND_BUF_SIZE = 20000;
        private const int CYCLES_PER_SAMPLE = 41;

        private static readonly ushort[] dmc_samples_wait = {
            0x0D60, 0x0BE0, 0x0AA0, 0x0A00, 0x08F0, 0x07F0, 0x0710, 0x06B0,
            0x05F0, 0x0500, 0x0470, 0x0400, 0x0350, 0x02A0, 0x0240, 0x01B0 };
        private static readonly ulong[] squ_duty = { 0x04000000, 0x08000000, 0x10000000, 0x18000000 };
        private static readonly byte[] length_precalc = {
            0x05, 0x7F, 0x0A, 0x01, 0x14, 0x02, 0x28, 0x03, 0x50, 0x04, 0x1E, 0x05, 0x07, 0x06, 0x0E, 0x07,
            0x06, 0x08, 0x0C, 0x09, 0x18, 0x0A, 0x30, 0x0B, 0x60, 0x0C, 0x24, 0x0D, 0x08, 0x0E, 0x10, 0x0F };
        private static readonly ushort[] noi_wavelen = {
            0x002, 0x004, 0x008, 0x010, 0x020, 0x030, 0x040, 0x050, 0x065, 0x07F, 0x0BE, 0x0FE, 0x17D, 0x1FC, 0x3F9, 0x7F2 };

        private const ulong MAGIC_squ = 0x1FFFFFF, MAGIC_tri = 0xFFFFFF, MAGIC_dmc = 0x1FFFFF, MAGIC_noi = 0xFFFFFFF;

        private readonly short[] triangle_50 = new short[0x20];
        private readonly ulong[] freq_buffer_squ = new ulong[2048], freq_buffer_tri = new ulong[2048];
        private readonly ulong[] freq_buffer_noi = new ulong[16], freq_buffer_dmc = new ulong[16];

        private ushort hz_60 = 1, hz_60_set, hz_120, hz_120_set, hz_240, hz_240_set;
        private const int bytes_per_sample = 1;
        private const float magic_adjust = 44100.0f / 44100;
        private uint cycles_per_sample, samples_per_vsync;
        private int tri_count_delay_max;

        private readonly uint[] evCount = new uint[SND_BUF_SIZE]; private readonly ushort[] evAddr = new ushort[SND_BUF_SIZE];
        private readonly byte[] evValue = new byte[SND_BUF_SIZE];
        private uint head, tail;
        private ushort volume_max = 0x78, volume_adjust = 0x08;

        private byte sq1_enabled, sq2_enabled, tri_enabled, noi_enabled, dmc_enabled;
        private ulong sq1_index, sq2_index, tri_index, noi_index, dmc_index, dmcs_index;
        private ulong step_sq1, step_sq2, step_tri, step_noi, step_dmc, step_dmcs;
        // square 1
        private ulong sq1_duty_cycle; private short wavelen_sq1; private ushort sq1_volume_reg, sq1_env_dec_volume;
        private byte sq1_len_counter, sq1_len_cnt_ld_reg, sq1_env_dec_cycle, sq1_env_dec_counter, sq1_env_dec_disable, sq1_lcc_disable,
            sq1_right_shift, sq1_inc_dec_wavelen, sq1_sw_counter, sq1_sw_update_rate, sq1_sweep_enabled, sq1_sw_no_carry = 1;
        // square 2
        private ulong sq2_duty_cycle; private short wavelen_sq2; private ushort sq2_volume_reg, sq2_env_dec_volume;
        private byte sq2_len_counter, sq2_len_cnt_ld_reg, sq2_env_dec_cycle, sq2_env_dec_counter, sq2_env_dec_disable, sq2_lcc_disable,
            sq2_right_shift, sq2_inc_dec_wavelen, sq2_sw_counter, sq2_sw_update_rate, sq2_sweep_enabled, sq2_sw_no_carry = 1;
        // triangle
        private byte tri_lin_counter, tri_lin_cnt_ld_reg, tri_lin_cnt_strt, tri_len_cnt_ld_reg, tri_len_counter, tri_mode_count, tri_will_count;
        private short wavelen_tri; private int tri_count_delay;   // hack inspired by nosefart (pez)
        // noise
        private byte noi_sample_rate, noi_number_type, noi_len_counter, noi_env_dec_cycle, noi_env_dec_counter;
        private ushort noi_env_dec_volume, noi_volume_reg;
        private byte noi_env_dec_disable, noi_lcc_disable, noi_len_cnt_ld_reg, noi_state_reg = 1;
        private uint sreg = 0x4000;
        // DMC
        private ushort dmc_load_register, dmc_dta_ftch_addr, dmc_len_freq, dmc_len_counter, dmc_clk_register, dmc_clk_for_fetch, dmc_delta;
        private byte dmc_gen_interrupt, dmc_loop_sample, dmc_shiftcnt;

        private readonly Func<ushort, byte> read;
        public TuxnesSound(Func<ushort, byte> cpuRead) { read = cpuRead; InitAudio(); }

        private void InitAudio()
        {
            cycles_per_sample = (uint)(111860.78 * 60.0 / SampleRate);
            samples_per_vsync = (uint)(SampleRate * bytes_per_sample / 60.0);
            for (int i = 1; i < 32; ++i)
                triangle_50[i] = i < 16 ? (short)(volume_adjust * i) : triangle_50[31 - i];
            sq1_duty_cycle = squ_duty[1]; sq2_duty_cycle = squ_duty[1];
            sq1_env_dec_volume = sq2_env_dec_volume = noi_env_dec_volume = volume_max;
            hz_60_set = (ushort)(SampleRate / 60.0);
            hz_120_set = (ushort)(hz_60_set / 2 + hz_60_set % 2);
            hz_240_set = (ushort)(hz_60_set / 4 + hz_60_set % 4);
            for (int i = 1; i < 2048; ++i)
            {
                freq_buffer_squ[i] = (ulong)((float)(MAGIC_squ * CYCLES_PER_SAMPLE) * magic_adjust / i);
                freq_buffer_tri[i] = (ulong)((float)(MAGIC_tri * CYCLES_PER_SAMPLE) * magic_adjust / i);
            }
            tri_count_delay_max = (int)(CYCLES_PER_SAMPLE * magic_adjust * samples_per_vsync / 2);
        }

        // ---- host side ----
        private int clock;   // CLOCK: CPU cycles into the frame
        private readonly float[] outRing = new float[16384]; private int outW, outR, outN;
        private float dcIn, dcOut;
        public int ChannelMask = 0x1F;

        /// <summary>SoundEvent: io.c sends every $4000-$4017 write except $4014.</summary>
        public void Write(ushort addr, byte value)
        {
            if (addr < 0x4000 || addr > 0x4017 || addr == 0x4014) return;
            evCount[tail] = (uint)clock; evAddr[tail] = addr; evValue[tail] = value;
            if (++tail == SND_BUF_SIZE) tail = 0;
        }

        public void Clock(int cpuCycles)
        {
            int prev = clock;
            clock += cpuCycles;
            if (prev < VBL && clock >= VBL) UpdateAudio();
            if (clock >= CPF) { clock -= CPF; if (clock >= VBL) UpdateAudio(); }
        }

        public int Queued => outN;
        public int Read(Span<float> dest)
        {
            int n = Math.Min(dest.Length, outN);
            for (int i = 0; i < n; i++) { dest[i] = outRing[outR]; outR = (outR + 1) & (outRing.Length - 1); }
            outN -= n; return n;
        }
        public void ClearOutput() { outW = outR = outN = 0; dcIn = dcOut = 0; }
        public void DropOutput() { outW = outR = outN = 0; }

        private byte ShiftRegister15(byte xor_tap)
        {
            uint bit0 = sreg & 1;
            uint tap = (sreg & xor_tap) != 0 ? 1u : 0u;
            uint bit14 = bit0 ^ tap;
            sreg >>= 1;
            sreg |= bit14 << 14;
            return (byte)(bit0 ^ 1);
        }

        private void UpdateAudio()
        {
            uint count = 0, sample = 0;
            byte dmc_shift = 0;
            int m = ChannelMask;
            while (sample < samples_per_vsync)
            {
                --hz_60; --hz_120; --hz_240;
                if (hz_60 == 0)
                {
                    hz_60 = hz_60_set; hz_120 = 0;
                    if (sq1_lcc_disable == 0 && sq1_len_counter != 0) --sq1_len_counter;
                    if (sq2_lcc_disable == 0 && sq2_len_counter != 0) --sq2_len_counter;
                    if (tri_lin_cnt_strt == 0 && tri_len_counter != 0) --tri_len_counter;
                    if (noi_lcc_disable == 0 && noi_len_counter != 0) --noi_len_counter;
                }
                if (hz_120 == 0)
                {
                    hz_120 = hz_120_set; hz_240 = 0;
                    if (sq1_sweep_enabled != 0 && sq1_len_counter != 0 && sq1_right_shift != 0 && sq1_sw_no_carry != 0 && wavelen_sq1 > 0x07)
                    {
                        --sq1_sw_counter;
                        if (sq1_sw_counter == 0)
                        {
                            sq1_sw_counter = sq1_sw_update_rate;
                            if (sq1_inc_dec_wavelen != 0)
                            {
                                wavelen_sq1 -= (short)((wavelen_sq1 >> sq1_right_shift) + 1);
                                if (wavelen_sq1 < 0) wavelen_sq1 = 0;
                            }
                            else
                            {
                                short t = (short)(wavelen_sq1 + (wavelen_sq1 >> sq1_right_shift));
                                if (t >= 0x0800) sq1_sw_no_carry = 0; else wavelen_sq1 = t;
                            }
                            step_sq1 = freq_buffer_squ[wavelen_sq1];
                        }
                    }
                    if (sq2_sweep_enabled != 0 && sq2_len_counter != 0 && sq2_right_shift != 0 && sq2_sw_no_carry != 0 && wavelen_sq2 > 0x07)
                    {
                        --sq2_sw_counter;
                        if (sq2_sw_counter == 0)
                        {
                            sq2_sw_counter = sq2_sw_update_rate;
                            if (sq2_inc_dec_wavelen != 0)
                            {
                                wavelen_sq2 -= (short)(wavelen_sq2 >> sq2_right_shift);
                                if (wavelen_sq2 < 0) wavelen_sq2 = 0;
                            }
                            else
                            {
                                short t = (short)(wavelen_sq2 + (wavelen_sq2 >> sq2_right_shift));
                                if (t >= 0x0800) sq2_sw_no_carry = 0; else wavelen_sq2 = t;
                            }
                            step_sq2 = freq_buffer_squ[wavelen_sq2];
                        }
                    }
                }
                if (hz_240 == 0)
                {
                    hz_240 = hz_240_set;
                    if (sq1_env_dec_disable == 0) --sq1_env_dec_counter;
                    if (sq2_env_dec_disable == 0) --sq2_env_dec_counter;
                    if (noi_env_dec_disable == 0) --noi_env_dec_counter;
                    if (sq1_env_dec_counter == 0)
                    {
                        sq1_env_dec_counter = sq1_env_dec_cycle;
                        if (sq1_env_dec_volume != 0) sq1_env_dec_volume -= volume_adjust;
                        else if (sq1_lcc_disable != 0) sq1_env_dec_volume = volume_max;
                    }
                    if (sq2_env_dec_counter == 0)
                    {
                        sq2_env_dec_counter = sq2_env_dec_cycle;
                        if (sq2_env_dec_volume != 0) sq2_env_dec_volume -= volume_adjust;
                        else if (sq2_lcc_disable != 0) sq2_env_dec_volume = volume_max;
                    }
                    if (noi_env_dec_counter == 0)
                    {
                        noi_env_dec_counter = noi_env_dec_cycle;
                        if (noi_env_dec_volume != 0) noi_env_dec_volume -= volume_adjust;
                        else if (noi_lcc_disable != 0) noi_env_dec_volume = volume_max;
                    }
                    if (tri_mode_count != 0 && tri_lin_counter != 0 && tri_lin_cnt_strt == 0) --tri_lin_counter;
                }

                while (head != tail && evCount[head] < count)
                {
                    Event(evAddr[head], evValue[head]);
                    if (++head == SND_BUF_SIZE) head = 0;
                }

                // triangle
                long tri = 0, s1 = 0, s2 = 0, noi = 0, dmc = 0;
                if (tri_count_delay > 0) tri_count_delay -= (int)(CYCLES_PER_SAMPLE * magic_adjust);
                else if (tri_will_count != 0) { tri_mode_count = 1; tri_will_count = 0; }
                if (tri_mode_count == 0) tri_lin_counter = tri_lin_cnt_ld_reg;
                if ((tri_enabled != 0 && tri_len_counter != 0 && tri_lin_counter != 0) || tri_index > step_tri)
                {
                    tri_index += step_tri; tri_index &= 0x1FFFFFFF;
                    tri = triangle_50[tri_index >> 24];
                }
                // square 1
                if ((sq1_enabled != 0 && sq1_len_counter != 0 && wavelen_sq1 > 0x07 && sq1_sw_no_carry != 0) || sq1_index > step_sq1)
                {
                    sq1_index += step_sq1; sq1_index &= 0x1FFFFFFF;
                    if (sq1_index <= sq1_duty_cycle) s1 = sq1_env_dec_disable != 0 ? sq1_volume_reg : sq1_env_dec_volume;
                }
                // square 2
                if ((sq2_enabled != 0 && sq2_len_counter != 0 && wavelen_sq2 > 0x07 && sq2_sw_no_carry != 0) || sq2_index > step_sq2)
                {
                    sq2_index += step_sq2; sq2_index &= 0x1FFFFFFF;
                    if (sq2_index <= sq2_duty_cycle) s2 = sq2_env_dec_disable != 0 ? sq2_volume_reg : sq2_env_dec_volume;
                }
                // noise
                if (noi_enabled != 0)
                {
                    noi_index += step_noi;
                    if (noi_index > 0x1FFFFFFF)
                    {
                        noi_state_reg = ShiftRegister15((byte)(noi_number_type != 0 ? 0x40 : 0x02));
                        noi_index &= 0x1FFFFFFF;
                    }
                    if (noi_len_counter != 0 && noi_state_reg != 0)
                        noi = noi_env_dec_disable != 0 ? noi_volume_reg : noi_env_dec_volume;
                }
                // DMC
                if (dmc_enabled != 0 && dmc_len_counter != 0)
                {
                    if (dmc_index >= MAGIC_dmc)
                    {
                        dmc_shift = read(dmc_dta_ftch_addr);
                        dmc_dta_ftch_addr++;   // unsigned short: "== 0x10000" never hits, wraps to $0000
                        dmc_index &= MAGIC_dmc;
                        dmcs_index = 0;
                        dmc_shiftcnt = 8;
                        --dmc_len_counter;
                    }
                    dmc_index += step_dmc;
                    dmcs_index += step_dmcs;
                    if (dmc_len_counter == 0)
                    {
                        if (dmc_loop_sample != 0) { dmc_len_counter = dmc_len_freq; dmc_dta_ftch_addr = dmc_load_register; }
                    }
                    while (dmc_shiftcnt > 0 && (dmcs_index >= MAGIC_dmc || dmc_index >= MAGIC_dmc))
                    {
                        if ((dmc_shift & 1) != 0) { if (dmc_delta != 0x3F) ++dmc_delta; }
                        else if (dmc_delta != 0) --dmc_delta;
                        dmc_shift >>= 1;
                        dmcs_index -= MAGIC_dmc;
                        --dmc_shiftcnt;
                    }
                    dmc = dmc_delta << 3;
                }

                long samp = ((m & 1) != 0 ? s1 : 0) + ((m & 2) != 0 ? s2 : 0) + ((m & 4) != 0 ? tri : 0)
                          + ((m & 8) != 0 ? noi : 0) + ((m & 16) != 0 ? dmc : 0);
                byte b = (byte)(samp / 5);   // average the waves (unsigned 8-bit)
                float x = b / 128f;
                float y = x - dcIn + 0.995f * dcOut; dcIn = x; dcOut = y;
                if (outN == outRing.Length) { outR = (outR + 1) & (outRing.Length - 1); outN--; }
                outRing[outW] = y; outW = (outW + 1) & (outRing.Length - 1); outN++;

                count += cycles_per_sample;
                sample += bytes_per_sample;
            }
        }

        private void Event(ushort addr, byte v)
        {
            switch (addr)
            {
                case 0x4000:
                    sq1_env_dec_disable = (byte)(v & 0x10); sq1_lcc_disable = (byte)(v & 0x20);
                    sq1_duty_cycle = squ_duty[v >> 6];
                    if (sq1_env_dec_disable != 0) sq1_env_dec_volume = sq1_volume_reg = (ushort)(volume_adjust * (v & 0x0F));
                    else sq1_env_dec_counter = sq1_env_dec_cycle = (byte)((v & 0x0F) + 1);
                    break;
                case 0x4001:
                    sq1_right_shift = (byte)(v & 0x07); sq1_inc_dec_wavelen = (byte)(v & 0x08);
                    sq1_sw_counter = sq1_sw_update_rate = (byte)(((v >> 4) & 0x07) + 1);
                    sq1_sweep_enabled = (byte)(v & 0x80); sq1_sw_no_carry = 1;
                    break;
                case 0x4002:
                    wavelen_sq1 = (short)((wavelen_sq1 & 0x0700) | v); sq1_sw_no_carry = 1;
                    step_sq1 = freq_buffer_squ[wavelen_sq1];
                    break;
                case 0x4003:
                    wavelen_sq1 = (short)((wavelen_sq1 & 0x00FF) | ((v & 0x07) << 8)); sq1_sw_no_carry = 1;
                    sq1_len_cnt_ld_reg = (byte)(v >> 3); sq1_len_counter = length_precalc[sq1_len_cnt_ld_reg];
                    sq1_env_dec_volume = volume_max; step_sq1 = freq_buffer_squ[wavelen_sq1];
                    break;
                case 0x4004:
                    sq2_env_dec_disable = (byte)(v & 0x10); sq2_lcc_disable = (byte)(v & 0x20);
                    sq2_duty_cycle = squ_duty[v >> 6];
                    if (sq2_env_dec_disable != 0) sq2_env_dec_volume = sq2_volume_reg = (ushort)(volume_adjust * (v & 0x0F));
                    else sq2_env_dec_counter = sq2_env_dec_cycle = (byte)((v & 0x0F) + 1);
                    break;
                case 0x4005:
                    sq2_right_shift = (byte)(v & 0x07); sq2_inc_dec_wavelen = (byte)(v & 0x08);
                    sq2_sw_counter = sq2_sw_update_rate = (byte)(((v >> 4) & 0x07) + 1);
                    sq2_sweep_enabled = (byte)(v & 0x80); sq2_sw_no_carry = 1;
                    break;
                case 0x4006:
                    wavelen_sq2 = (short)((wavelen_sq2 & 0x0700) | v); sq2_sw_no_carry = 1;
                    step_sq2 = freq_buffer_squ[wavelen_sq2];
                    break;
                case 0x4007:
                    wavelen_sq2 = (short)((wavelen_sq2 & 0x00FF) | ((v & 0x07) << 8));
                    sq2_len_cnt_ld_reg = (byte)(v >> 3); sq2_len_counter = length_precalc[sq2_len_cnt_ld_reg];
                    sq2_env_dec_volume = volume_max; sq2_sw_no_carry = 1; step_sq2 = freq_buffer_squ[wavelen_sq2];
                    break;
                case 0x4008:
                    tri_lin_cnt_ld_reg = (byte)(v & 0x7F);
                    if (tri_mode_count == 0) tri_lin_counter = tri_lin_cnt_ld_reg;
                    if (tri_lin_cnt_strt == 0) { tri_mode_count = 1; tri_will_count = 0; tri_count_delay = 0; }
                    else if ((v & 0x80) == 0) { tri_will_count = 1; tri_count_delay = tri_count_delay_max; }
                    else { tri_will_count = 0; tri_count_delay = 0; }
                    tri_lin_cnt_strt = (byte)(v & 0x80);
                    break;
                case 0x400A:
                    wavelen_tri = (short)((wavelen_tri & 0x0700) | v); step_tri = freq_buffer_tri[wavelen_tri];
                    break;
                case 0x400B:
                    wavelen_tri = (short)((wavelen_tri & 0x00FF) | ((v & 0x07) << 8));
                    tri_len_cnt_ld_reg = (byte)(v >> 3); tri_len_counter = length_precalc[tri_len_cnt_ld_reg];
                    if (tri_lin_cnt_strt != 0) { tri_will_count = 0; tri_count_delay = 0; }
                    else { tri_will_count = 1; tri_count_delay = tri_count_delay_max; }
                    tri_mode_count = 0; tri_lin_counter = tri_lin_cnt_ld_reg; step_tri = freq_buffer_tri[wavelen_tri];
                    break;
                case 0x400C:
                    noi_env_dec_disable = (byte)(v & 0x10); noi_lcc_disable = (byte)(v & 0x20);
                    if (noi_env_dec_disable != 0) noi_env_dec_volume = noi_volume_reg = (ushort)(volume_adjust * (v & 0x0F));
                    else noi_env_dec_counter = noi_env_dec_cycle = (byte)((v & 0x0F) + 1);
                    break;
                case 0x400E:
                    noi_sample_rate = (byte)(v & 0x0F); noi_number_type = (byte)(v & 0x80);
                    if (freq_buffer_noi[noi_sample_rate] == 0)
                        freq_buffer_noi[noi_sample_rate] = (ulong)((float)(MAGIC_noi * CYCLES_PER_SAMPLE) * magic_adjust / noi_wavelen[noi_sample_rate]);
                    step_noi = freq_buffer_noi[noi_sample_rate];
                    break;
                case 0x400F:
                    noi_len_cnt_ld_reg = (byte)(v >> 3); noi_len_counter = length_precalc[noi_len_cnt_ld_reg];
                    noi_env_dec_volume = volume_max;
                    break;
                case 0x4010:
                    dmc_loop_sample = (byte)(v & 0x40);
                    dmc_gen_interrupt = (byte)(dmc_loop_sample != 0 ? 0 : v & 0x80);
                    dmc_clk_for_fetch = 1; dmc_clk_register = dmc_samples_wait[v & 0x0F];
                    if (freq_buffer_dmc[v & 0x0F] == 0)
                        freq_buffer_dmc[v & 0x0F] = (ulong)((float)(MAGIC_dmc * CYCLES_PER_SAMPLE) * magic_adjust / dmc_samples_wait[v & 0x0F]);
                    step_dmc = freq_buffer_dmc[v & 0x0F];
                    step_dmcs = step_dmc << 3;
                    break;
                case 0x4011: dmc_delta = (ushort)((v & 0x7E) >> 1); break;
                case 0x4012: dmc_dta_ftch_addr = dmc_load_register = (ushort)((v << 6) | 0xC000); break;
                case 0x4013: dmc_len_counter = dmc_len_freq = (ushort)(v << 4); break;
                case 0x4015:
                    sq1_enabled = (byte)(v & 0x01); sq2_enabled = (byte)(v & 0x02); tri_enabled = (byte)(v & 0x04);
                    noi_enabled = (byte)(v & 0x08); dmc_enabled = (byte)(v & 0x10);
                    if (sq1_enabled == 0) sq1_len_counter = 0;
                    if (sq2_enabled == 0) sq2_len_counter = 0;
                    if (tri_enabled == 0) tri_len_counter = 0;
                    if (noi_enabled == 0) noi_len_counter = 0;
                    break;
            }
        }
    }
}
