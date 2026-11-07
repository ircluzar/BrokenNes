/*
 * Resid016 - reSID 0.16, a MOS6581 SID emulator engine, ported to C#.
 * Source project: reSID 0.16 by Dag Lem (2004), the last release before reSID 1.0's transistor-level filter model.
 *   Mirror used: https://github.com/Olzaq/reSID commit 13152b680ecb7845005433a2d17d5fd8cb60261d (configure.in: VERSION 0.16)
 *   Files: sid.cc/h, voice.cc/h, wave.cc/h, wave6581__ST/_P_T/_PS_/_PST.cc (tables in Resid016Tables.cs), envelope.cc/h,
 *   filter.cc/h, extfilt.cc/h, pot.cc/h (not needed: no paddles), spline.h.
 *   Original distribution: reSID was published by Dag Lem at http://www.nimrod.no/ and is mirrored with the C64 archives at
 *   http://www.zimmers.net/anonftp/pub/cbm/crossplatform/emulators/resid/ (see the README/NEWS there).
 * Copyright (C) 2004 Dag Lem <resid@nimrod.no>.
 * License: GNU General Public License version 2 or (at your option) any later version (COPYING in the source).
 * This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2a and docs/projects/apu-harvest-catalog.md
 *
 * Port notes: MOS6581 model only (the 8580 tables/branches are left out). Sampling method SAMPLE_FAST - the 0.16 constructor's default
 * (SID::SID() calls set_sampling_parameters(985248, SAMPLE_FAST, 44100)): delta clocking, the nearest cycle's output is taken as the sample,
 * no interpolation and no resampling FIR (the SAMPLE_INTERPOLATE / RESAMPLE paths are not ported). Clock 1022727 Hz (NTSC C64), 44100 Hz out.
 * Integer arithmetic follows the C++ types: reg* are unsigned int (uint here), sound_sample/cycle_count are int.
 *
 * Characteristic flaws kept on purpose:
 *  - The 0.16 filter: a linear two-integrator-loop biquad with a spline-mapped 6581 cutoff curve (with its FC 0x80 discontinuity), cutoff
 *    clamped to 4 kHz in the delta-clocked path, Q = 0.707..1.7 linear in RES; no op-amp non-linearity, no distortion (that is the point).
 *  - SAMPLE_FAST: the sample is the output at the nearest whole cycle - aliasing above ~22 kHz is not filtered (only the 16 kHz external RC).
 *  - The 6581 DC offsets: wave_zero 0x380, voice DC 0x800*0xff, mixer DC -1/18 - so writing the master volume makes an audible click,
 *    which is exactly the "$D418 sample" trick the translator uses for the DMC.
 *  - ADSR delay bug (rate counter wraps at 0x8000 when the period is lowered below it) and the sustain comparator: in DECAY_SUSTAIN the
 *    counter only steps DOWN toward the sustain level, so raising sustain without a re-gate makes the envelope fall to zero and freeze.
 *  - The one-cycle exponential-counter delay, the 2-cycle noise shift delay, the 1-cycle pulse compare delay are not modeled (source "NB!").
 *  - Noise combined with any other waveform outputs 0 (0.16 does not model the shift-register write-back / lock-up).
 *  - Test bit clears the shift register to 0 and releasing it reloads 0x7ffff8 immediately (source "NB!").
 *  - Delta-clocked oscillators are only split at MSB toggles of sync sources; the external filter and the main filter step in chunks of 8 cycles.
 *  - SID::output() divides by the integer 11 (734130/65536 truncated), so full-scale is slightly hot and clipped at 16 bits.
 */
using System;

namespace NesEmulator
{
    public sealed class Resid016
    {
        public const int Clock = 1022727;
        public const int SampleRate = 44100;

        // ===================================================================== WaveformGenerator (wave.h / wave.cc)
        private sealed class Wave
        {
            public Wave sync_source = null!, sync_dest = null!;
            public bool msb_rising;
            public uint accumulator, shift_register, freq, pw, waveform, test, ring_mod, sync;

            public static readonly byte[] W_ST = Hex(Resid016Tables.Wave__ST), W_P_T = Hex(Resid016Tables.Wave_P_T), W_PS_ = Hex(Resid016Tables.Wave_PS_), W_PST = Hex(Resid016Tables.Wave_PST);
            private static byte[] Hex(string s) { var b = new byte[s.Length / 2]; for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16); return b; }

            public Wave() { sync_source = this; Reset(); }

            public void Reset() { accumulator = 0; shift_register = 0x7ffff8; freq = 0; pw = 0; test = 0; ring_mod = 0; sync = 0; msb_rising = false; }

            public void Clock1()
            {
                if (test != 0) return;
                uint prev = accumulator;
                accumulator += freq; accumulator &= 0xffffff;
                msb_rising = (prev & 0x800000) == 0 && (accumulator & 0x800000) != 0;
                if ((prev & 0x080000) == 0 && (accumulator & 0x080000) != 0)
                {
                    uint bit0 = ((shift_register >> 22) ^ (shift_register >> 17)) & 0x1;
                    shift_register <<= 1; shift_register &= 0x7fffff; shift_register |= bit0;
                }
            }

            public void Clock(int delta_t)
            {
                if (test != 0) return;
                uint prev = accumulator;
                uint delta_accumulator = (uint)delta_t * freq;
                accumulator += delta_accumulator; accumulator &= 0xffffff;
                msb_rising = (prev & 0x800000) == 0 && (accumulator & 0x800000) != 0;
                uint shift_period = 0x100000;
                while (delta_accumulator != 0)
                {
                    if (delta_accumulator < shift_period)
                    {
                        shift_period = delta_accumulator;
                        if (shift_period <= 0x080000)
                        {
                            if (((accumulator - shift_period) & 0x080000) != 0 || (accumulator & 0x080000) == 0) break;
                        }
                        else
                        {
                            if (((accumulator - shift_period) & 0x080000) != 0 && (accumulator & 0x080000) == 0) break;
                        }
                    }
                    uint bit0 = ((shift_register >> 22) ^ (shift_register >> 17)) & 0x1;
                    shift_register <<= 1; shift_register &= 0x7fffff; shift_register |= bit0;
                    delta_accumulator -= shift_period;
                }
            }

            public void Synchronize()
            {
                if (msb_rising && sync_dest.sync != 0 && !(sync != 0 && sync_source.msb_rising)) sync_dest.accumulator = 0;
            }

            private uint O___T() { uint msb = (ring_mod != 0 ? accumulator ^ sync_source.accumulator : accumulator) & 0x800000; return ((msb != 0 ? ~accumulator : accumulator) >> 11) & 0xfff; }
            private uint O__S_() => accumulator >> 12;
            private uint O_P__() => (test != 0 || (accumulator >> 12) >= pw) ? 0xfffu : 0x000u;
            private uint ON___() =>
                ((shift_register & 0x400000) >> 11) | ((shift_register & 0x100000) >> 10) | ((shift_register & 0x010000) >> 7) | ((shift_register & 0x002000) >> 5) |
                ((shift_register & 0x000800) >> 4) | ((shift_register & 0x000080) >> 1) | ((shift_register & 0x000010) << 1) | ((shift_register & 0x000004) << 2);

            public uint Output()
            {
                switch (waveform)
                {
                    default: case 0x0: return 0;
                    case 0x1: return O___T();
                    case 0x2: return O__S_();
                    case 0x3: return (uint)W_ST[O__S_()] << 4;
                    case 0x4: return O_P__();
                    case 0x5: return ((uint)W_P_T[O___T() >> 1] << 4) & O_P__();
                    case 0x6: return ((uint)W_PS_[O__S_()] << 4) & O_P__();
                    case 0x7: return ((uint)W_PST[O__S_()] << 4) & O_P__();
                    case 0x8: return ON___();
                    case 0x9: case 0xa: case 0xb: case 0xc: case 0xd: case 0xe: case 0xf: return 0;
                }
            }

            public void WriteFREQ_LO(uint v) { freq = (freq & 0xff00) | (v & 0x00ff); }
            public void WriteFREQ_HI(uint v) { freq = ((v << 8) & 0xff00) | (freq & 0x00ff); }
            public void WritePW_LO(uint v) { pw = (pw & 0xf00) | (v & 0x0ff); }
            public void WritePW_HI(uint v) { pw = ((v << 8) & 0xf00) | (pw & 0x0ff); }
            public void WriteCONTROL_REG(uint control)
            {
                waveform = (control >> 4) & 0x0f; ring_mod = control & 0x04; sync = control & 0x02;
                uint test_next = control & 0x08;
                if (test_next != 0) { accumulator = 0; shift_register = 0; }
                else if (test != 0) { shift_register = 0x7ffff8; }
                test = test_next;
            }
        }

        // ===================================================================== EnvelopeGenerator (envelope.h / envelope.cc)
        private sealed class Envelope
        {
            private enum St { ATTACK, DECAY_SUSTAIN, RELEASE }
            private static readonly uint[] rate_counter_period = { 9, 32, 63, 95, 149, 220, 267, 313, 392, 977, 1954, 3126, 3907, 11720, 19532, 31251 };
            private static readonly uint[] sustain_level = { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff };

            private uint rate_counter, rate_period, exponential_counter, exponential_counter_period, envelope_counter;
            private bool hold_zero;
            private uint attack, decay, sustain, release, gate;
            private St state;

            public Envelope() { Reset(); }

            public void Reset()
            {
                envelope_counter = 0; attack = 0; decay = 0; sustain = 0; release = 0; gate = 0;
                rate_counter = 0; exponential_counter = 0; exponential_counter_period = 1;
                state = St.RELEASE; rate_period = rate_counter_period[release]; hold_zero = true;
            }

            private void Step()
            {
                switch (state)
                {
                    case St.ATTACK:
                        envelope_counter = (envelope_counter + 1) & 0xff;
                        if (envelope_counter == 0xff) { state = St.DECAY_SUSTAIN; rate_period = rate_counter_period[decay]; }
                        break;
                    case St.DECAY_SUSTAIN:
                        if (envelope_counter != sustain_level[sustain]) --envelope_counter;
                        break;
                    case St.RELEASE:
                        envelope_counter = (envelope_counter - 1) & 0xff;
                        break;
                }
                switch (envelope_counter)
                {
                    case 0xff: exponential_counter_period = 1; break;
                    case 0x5d: exponential_counter_period = 2; break;
                    case 0x36: exponential_counter_period = 4; break;
                    case 0x1a: exponential_counter_period = 8; break;
                    case 0x0e: exponential_counter_period = 16; break;
                    case 0x06: exponential_counter_period = 30; break;
                    case 0x00: exponential_counter_period = 1; hold_zero = true; break;
                }
            }

            public void Clock(int delta_t)
            {
                int rate_step = (int)rate_period - (int)rate_counter;
                if (rate_step <= 0) rate_step += 0x7fff;
                while (delta_t != 0)
                {
                    if (delta_t < rate_step)
                    {
                        rate_counter += (uint)delta_t;
                        if ((rate_counter & 0x8000) != 0) { ++rate_counter; rate_counter &= 0x7fff; }
                        return;
                    }
                    rate_counter = 0;
                    delta_t -= rate_step;
                    if (state == St.ATTACK || ++exponential_counter == exponential_counter_period)
                    {
                        exponential_counter = 0;
                        if (hold_zero) { rate_step = (int)rate_period; continue; }
                        Step();
                    }
                    rate_step = (int)rate_period;
                }
            }

            public uint Output() => envelope_counter;

            public void WriteCONTROL_REG(uint control)
            {
                uint gate_next = control & 0x01;
                if (gate == 0 && gate_next != 0) { state = St.ATTACK; rate_period = rate_counter_period[attack]; hold_zero = false; }
                else if (gate != 0 && gate_next == 0) { state = St.RELEASE; rate_period = rate_counter_period[release]; }
                gate = gate_next;
            }
            public void WriteATTACK_DECAY(uint v)
            {
                attack = (v >> 4) & 0x0f; decay = v & 0x0f;
                if (state == St.ATTACK) rate_period = rate_counter_period[attack];
                else if (state == St.DECAY_SUSTAIN) rate_period = rate_counter_period[decay];
            }
            public void WriteSUSTAIN_RELEASE(uint v)
            {
                sustain = (v >> 4) & 0x0f; release = v & 0x0f;
                if (state == St.RELEASE) rate_period = rate_counter_period[release];
            }
        }

        // ===================================================================== Voice (voice.h / voice.cc), MOS6581
        private sealed class Voice
        {
            public readonly Wave wave = new Wave();
            public readonly Envelope envelope = new Envelope();
            private const int wave_zero = 0x380;
            private const int voice_DC = 0x800 * 0xff;
            public void WriteCONTROL_REG(uint c) { wave.WriteCONTROL_REG(c); envelope.WriteCONTROL_REG(c); }
            public void Reset() { wave.Reset(); envelope.Reset(); }
            public int Output() => ((int)wave.Output() - wave_zero) * (int)envelope.Output() + voice_DC;
        }

        // ===================================================================== Filter (filter.h / filter.cc), MOS6581
        private sealed class Filter
        {
            public bool enabled = true;
            public uint fc, res, filt, voice3off, hp_bp_lp, vol;
            private const int mixer_DC = -0xfff * 0xff / 18 >> 7;
            private int Vhp, Vbp, Vlp, Vnf;
            private int w0, w0_ceil_1, w0_ceil_dt, _1024_div_Q;

            private static readonly int[,] f0_points_6581 =
            {
                { 0, 220 }, { 0, 220 }, { 128, 230 }, { 256, 250 }, { 384, 300 }, { 512, 420 }, { 640, 780 }, { 768, 1600 }, { 832, 2300 },
                { 896, 3200 }, { 960, 4300 }, { 992, 5000 }, { 1008, 5400 }, { 1016, 5700 }, { 1023, 6000 }, { 1023, 6000 }, { 1024, 4600 },
                { 1024, 4600 }, { 1032, 4800 }, { 1056, 5300 }, { 1088, 6000 }, { 1120, 6600 }, { 1152, 7200 }, { 1280, 9500 }, { 1408, 12000 },
                { 1536, 14500 }, { 1664, 16000 }, { 1792, 17100 }, { 1920, 17700 }, { 2047, 18000 }, { 2047, 18000 }
            };

            private static readonly int[] f0 = BuildF0();   // after f0_points_6581 (static initialisers run in order)

            // spline.h: interpolate() with interpolate_forward_difference and PointPlotter<sound_sample> (negative y clamped to 0, truncating casts)
            private static int[] BuildF0()
            {
                var f = new int[2048];
                var p = f0_points_6581;
                int n = p.GetLength(0);
                double X(int i) => p[i, 0]; double Y(int i) => p[i, 1];
                for (int i0 = 0; i0 + 2 != n - 1; i0++)
                {
                    int i1 = i0 + 1, i2 = i0 + 2, i3 = i0 + 3;
                    double k1, k2;
                    if (X(i1) == X(i2)) continue;
                    if (X(i0) == X(i1) && X(i2) == X(i3)) { k1 = k2 = (Y(i2) - Y(i1)) / (X(i2) - X(i1)); }
                    else if (X(i0) == X(i1)) { k2 = (Y(i3) - Y(i1)) / (X(i3) - X(i1)); k1 = (3 * (Y(i2) - Y(i1)) / (X(i2) - X(i1)) - k2) / 2; }
                    else if (X(i2) == X(i3)) { k1 = (Y(i2) - Y(i0)) / (X(i2) - X(i0)); k2 = (3 * (Y(i2) - Y(i1)) / (X(i2) - X(i1)) - k1) / 2; }
                    else { k1 = (Y(i2) - Y(i0)) / (X(i2) - X(i0)); k2 = (Y(i3) - Y(i1)) / (X(i3) - X(i1)); }
                    ForwardDifference(X(i1), Y(i1), X(i2), Y(i2), k1, k2, f, 1.0);
                }
                return f;
            }

            private static void ForwardDifference(double x1, double y1, double x2, double y2, double k1, double k2, int[] f, double res)
            {
                double dx = x2 - x1, dyy = y2 - y1;
                double a = ((k1 + k2) - 2 * dyy / dx) / (dx * dx);
                double b = ((k2 - k1) / dx - 3 * (x1 + x2) * a) / 2;
                double c = k1 - (3 * x1 * a + 2 * b) * x1;
                double d = y1 - ((x1 * a + b) * x1 + c) * x1;
                double y = ((a * x1 + b) * x1 + c) * x1 + d;
                double dy = (3 * a * (x1 + res) + 2 * b) * x1 * res + ((a * res + b) * res + c) * res;
                double d2y = (6 * a * (x1 + res) + 2 * b) * res * res;
                double d3y = 6 * a * res * res * res;
                for (double x = x1; x <= x2; x += res)
                {
                    double yy = y < 0 ? 0 : y;
                    f[(int)x] = (int)yy;
                    y += dy; dy += d2y; d2y += d3y;
                }
            }

            public Filter() { Reset(); }

            public void Reset() { fc = 0; res = 0; filt = 0; voice3off = 0; hp_bp_lp = 0; vol = 0; Vhp = Vbp = Vlp = Vnf = 0; SetW0(); SetQ(); }

            public void WriteFC_LO(uint v) { fc = (fc & 0x7f8) | (v & 0x007); SetW0(); }
            public void WriteFC_HI(uint v) { fc = ((v << 3) & 0x7f8) | (fc & 0x007); SetW0(); }
            public void WriteRES_FILT(uint v) { res = (v >> 4) & 0x0f; SetQ(); filt = v & 0x0f; }
            public void WriteMODE_VOL(uint v) { voice3off = v & 0x80; hp_bp_lp = (v >> 4) & 0x07; vol = v & 0x0f; }

            private void SetW0()
            {
                const double pi = 3.1415926535897932385;
                w0 = (int)(2 * pi * f0[fc] * 1.048576);
                int w0_max_1 = (int)(2 * pi * 16000 * 1.048576);
                w0_ceil_1 = w0 <= w0_max_1 ? w0 : w0_max_1;
                int w0_max_dt = (int)(2 * pi * 4000 * 1.048576);
                w0_ceil_dt = w0 <= w0_max_dt ? w0 : w0_max_dt;
            }
            private void SetQ() { _1024_div_Q = (int)(1024.0 / (0.707 + 1.0 * res / 0x0f)); }

            public void Clock(int delta_t, int voice1, int voice2, int voice3, int ext_in)
            {
                voice1 >>= 7; voice2 >>= 7;
                if (voice3off != 0 && (filt & 0x04) == 0) voice3 = 0; else voice3 >>= 7;
                ext_in >>= 7;
                if (!enabled) { Vnf = voice1 + voice2 + voice3 + ext_in; Vhp = Vbp = Vlp = 0; return; }
                // (filt1 ? Vi : Vnf) += voice1; ... - the 16-case switch of the source, folded
                int Vi = 0; Vnf = 0;
                if ((filt & 1) != 0) Vi += voice1; else Vnf += voice1;
                if ((filt & 2) != 0) Vi += voice2; else Vnf += voice2;
                if ((filt & 4) != 0) Vi += voice3; else Vnf += voice3;
                if ((filt & 8) != 0) Vi += ext_in; else Vnf += ext_in;
                int delta_t_flt = 8;
                while (delta_t != 0)
                {
                    if (delta_t < delta_t_flt) delta_t_flt = delta_t;
                    int w0_delta_t = w0_ceil_dt * delta_t_flt >> 6;
                    int dVbp = (w0_delta_t * Vhp >> 14);
                    int dVlp = (w0_delta_t * Vbp >> 14);
                    Vbp -= dVbp; Vlp -= dVlp;
                    Vhp = (Vbp * _1024_div_Q >> 10) - Vlp - Vi;
                    delta_t -= delta_t_flt;
                }
            }

            public int Output()
            {
                if (!enabled) return (Vnf + mixer_DC) * (int)vol;
                int Vf = 0;
                if ((hp_bp_lp & 1) != 0) Vf += Vlp;
                if ((hp_bp_lp & 2) != 0) Vf += Vbp;
                if ((hp_bp_lp & 4) != 0) Vf += Vhp;
                return (Vnf + Vf + mixer_DC) * (int)vol;
            }
        }

        // ===================================================================== ExternalFilter (extfilt.h / extfilt.cc), MOS6581
        private sealed class ExtFilter
        {
            public bool enabled = true;
            private const int mixer_DC = ((((0x800 - 0x380) + 0x800) * 0xff * 3 - 0xfff * 0xff / 18) >> 7) * 0x0f;
            private int Vlp, Vhp, Vo;
            private const int w0lp = 104858, w0hp = 105;
            public void Reset() { Vlp = Vhp = Vo = 0; }
            public void Clock(int delta_t, int Vi)
            {
                if (!enabled) { Vlp = Vhp = 0; Vo = Vi - mixer_DC; return; }
                int delta_t_flt = 8;
                while (delta_t != 0)
                {
                    if (delta_t < delta_t_flt) delta_t_flt = delta_t;
                    int dVlp = (w0lp * delta_t_flt >> 8) * (Vi - Vlp) >> 12;
                    int dVhp = w0hp * delta_t_flt * (Vlp - Vhp) >> 20;
                    Vo = Vlp - Vhp; Vlp += dVlp; Vhp += dVhp;
                    delta_t -= delta_t_flt;
                }
            }
            public int Output() => Vo;
        }

        // ===================================================================== SID (sid.h / sid.cc)
        private readonly Voice[] voice = { new Voice(), new Voice(), new Voice() };
        private readonly Filter filter = new Filter();
        private readonly ExtFilter extfilt = new ExtFilter();
        private uint bus_value; private int bus_value_ttl;
        private int ext_in;
        private const int FIXP_SHIFT = 16, FIXP_MASK = 0xffff;
        private readonly int cycles_per_sample = (int)((double)Clock / SampleRate * (1 << FIXP_SHIFT) + 0.5);
        private int sample_offset;

        public Resid016()
        {
            voice[0].wave.sync_source = voice[2].wave; voice[2].wave.sync_dest = voice[0].wave;
            voice[1].wave.sync_source = voice[0].wave; voice[0].wave.sync_dest = voice[1].wave;
            voice[2].wave.sync_source = voice[1].wave; voice[1].wave.sync_dest = voice[2].wave;
        }

        public void Reset()
        {
            for (int i = 0; i < 3; i++) voice[i].Reset();
            filter.Reset(); extfilt.Reset();
            bus_value = 0; bus_value_ttl = 0;
        }

        public void Write(int offset, int value)
        {
            uint v = (uint)value & 0xff;
            bus_value = v; bus_value_ttl = 0x2000;
            switch (offset)
            {
                case 0x00: voice[0].wave.WriteFREQ_LO(v); break;
                case 0x01: voice[0].wave.WriteFREQ_HI(v); break;
                case 0x02: voice[0].wave.WritePW_LO(v); break;
                case 0x03: voice[0].wave.WritePW_HI(v); break;
                case 0x04: voice[0].WriteCONTROL_REG(v); break;
                case 0x05: voice[0].envelope.WriteATTACK_DECAY(v); break;
                case 0x06: voice[0].envelope.WriteSUSTAIN_RELEASE(v); break;
                case 0x07: voice[1].wave.WriteFREQ_LO(v); break;
                case 0x08: voice[1].wave.WriteFREQ_HI(v); break;
                case 0x09: voice[1].wave.WritePW_LO(v); break;
                case 0x0a: voice[1].wave.WritePW_HI(v); break;
                case 0x0b: voice[1].WriteCONTROL_REG(v); break;
                case 0x0c: voice[1].envelope.WriteATTACK_DECAY(v); break;
                case 0x0d: voice[1].envelope.WriteSUSTAIN_RELEASE(v); break;
                case 0x0e: voice[2].wave.WriteFREQ_LO(v); break;
                case 0x0f: voice[2].wave.WriteFREQ_HI(v); break;
                case 0x10: voice[2].wave.WritePW_LO(v); break;
                case 0x11: voice[2].wave.WritePW_HI(v); break;
                case 0x12: voice[2].WriteCONTROL_REG(v); break;
                case 0x13: voice[2].envelope.WriteATTACK_DECAY(v); break;
                case 0x14: voice[2].envelope.WriteSUSTAIN_RELEASE(v); break;
                case 0x15: filter.WriteFC_LO(v); break;
                case 0x16: filter.WriteFC_HI(v); break;
                case 0x17: filter.WriteRES_FILT(v); break;
                case 0x18: filter.WriteMODE_VOL(v); break;
            }
        }

        /// <summary>SID::output(): 16-bit.</summary>
        private int Output16()
        {
            const int range = 1 << 16, half = range >> 1;
            int s = extfilt.Output() / ((4095 * 255 >> 7) * 3 * 15 * 2 / range);
            if (s >= half) return half - 1;
            if (s < -half) return -half;
            return s;
        }

        /// <summary>SID::clock(cycle_count delta_t).</summary>
        private void ClockDelta(int delta_t)
        {
            if (delta_t <= 0) return;
            bus_value_ttl -= delta_t;
            if (bus_value_ttl <= 0) { bus_value = 0; bus_value_ttl = 0; }
            for (int i = 0; i < 3; i++) voice[i].envelope.Clock(delta_t);
            int delta_t_osc = delta_t;
            while (delta_t_osc != 0)
            {
                int delta_t_min = delta_t_osc;
                for (int i = 0; i < 3; i++)
                {
                    var wave = voice[i].wave;
                    if (!(wave.sync_dest.sync != 0 && wave.freq != 0)) continue;
                    uint freq = wave.freq, acc = wave.accumulator;
                    uint delta_accumulator = ((acc & 0x800000) != 0 ? 0x1000000u : 0x800000u) - acc;
                    int delta_t_next = (int)(delta_accumulator / freq);
                    if (delta_accumulator % freq != 0) ++delta_t_next;
                    if (delta_t_next < delta_t_min) delta_t_min = delta_t_next;
                }
                for (int i = 0; i < 3; i++) voice[i].wave.Clock(delta_t_min);
                for (int i = 0; i < 3; i++) voice[i].wave.Synchronize();
                delta_t_osc -= delta_t_min;
            }
            filter.Clock(delta_t, voice[0].Output(), voice[1].Output(), voice[2].Output(), ext_in);
            extfilt.Clock(delta_t, filter.Output());
        }

        // ---- output ring (port glue) ----
        private readonly float[] ring = new float[16384];
        private int head, count;

        /// <summary>SID::clock_fast(delta_t, buf, n): SAMPLE_FAST; every sample goes to the ring.</summary>
        public void Run(int delta_t)
        {
            for (;;)
            {
                int next_sample_offset = sample_offset + cycles_per_sample + (1 << (FIXP_SHIFT - 1));
                int delta_t_sample = next_sample_offset >> FIXP_SHIFT;
                if (delta_t_sample > delta_t) break;
                ClockDelta(delta_t_sample);
                delta_t -= delta_t_sample;
                sample_offset = (next_sample_offset & FIXP_MASK) - (1 << (FIXP_SHIFT - 1));
                Push(Output16() / 32768f);
            }
            ClockDelta(delta_t);
            sample_offset -= delta_t << FIXP_SHIFT;
        }

        private void Push(float s)
        {
            if (count == ring.Length) { head = (head + 1) % ring.Length; count--; }
            ring[(head + count) % ring.Length] = s; count++;
        }

        public int Queued => count;
        public int Read(Span<float> dest)
        {
            int n = Math.Min(dest.Length, count);
            for (int i = 0; i < n; i++) { dest[i] = ring[head]; head = (head + 1) % ring.Length; }
            count -= n;
            return n;
        }
        public void ClearOutput() { head = count = 0; }
    }
}
