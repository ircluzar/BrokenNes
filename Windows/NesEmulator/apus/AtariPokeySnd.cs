/*
 * AtariPokeySnd - Ron Fries' PokeySound (POKEY Chip Emulator V2.4) as shipped in atari800 0.9.8a, ported to C#.
 * Source project: atari800 0.9.8a (Debian package atari800 0.9.8a-2), files pokeysnd.c (796 lines), pokeysnd.h, pokeysnd.txt.
 * URL: https://sources.debian.org/src/atari800/0.9.8a-2/pokeysnd.c
 * Authors: Ron Fries (1996-1998), with Brad Oliver's cross-platform changes and Perry's (atari800 team) poly4/poly5 tables.
 * Licence: GNU Library General Public License version 2 (pokeysnd.c / pokeysnd.h header: "under the terms of version 2 of the
 *   GNU Library General Public License"). Legend kept: "PokeySound is Copyright(c) 1996-1998 by Ron Fries".
 * This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2a and docs/projects/apu-harvest-catalog.md
 *
 * Build options reproduced: one chip (Num_pokeys = 1), unsigned 8-bit samples (SIGNED_SAMPLES off, SAMP_MID 128), CLIP off, the
 * "new" Perry poly tables (POKEY23_POLY off), full 17-bit poly (not COMP16), little-endian Samp_n_cnt layout, no USE_DOSSOUND speaker.
 * freq17 = FREQ_17_EXACT 1789790 Hz. Playback rate 44100 Hz: the rate is not in pokeysnd.c/.txt (pokeysnd.txt only recommends
 * 15960-63840 for the approximate clock); 44100 is atari800's own default dsprate (sound.c), so that is what is used here.
 * Gain 4 per channel: pokeysnd.txt's recommendation for one chip with all four channels at full volume.
 * bit17 is filled from C rand() like the original; here rand() is glibc's (TYPE_3 additive generator, unseeded = seed 1), the
 * libc atari800 0.9.8a was built against on Debian, so the "random" poly17 table is deterministic.
 *
 * Characteristic flaws kept on purpose:
 *  - Event-driven divide-by-N: output only changes on counter events and is point-sampled at the playback rate (no band-limiting,
 *    heavy aliasing on high tones, as pokeysnd.txt admits).
 *  - Any channel whose Div_n_max is below the sample period (Samp_n_max >> 8, ~40 ticks at 44100) is not processed: it is frozen
 *    "on" (Outvol = 1) and its counter parked at 0x7fffffff - very high notes become a DC offset, not a tone.
 *  - Volume-only mode (and volume 0) is faked the same way: Outvol forced to 1 and the counter set to max.
 *  - Channels 1+2 always parked when silent/vol-only (even with the filter on); channels 3/4 kept running only if they clock a filter.
 *  - High-pass "filters" on channels 1/2: channel 3 (4) events simply force channel 1 (2) off, the chip's flip-flop crudely imitated.
 *  - Poly17 is a table of rand() bits, not the real LFSR; 9-bit poly mode indexes the SAME bit17 table with the poly9 position.
 *  - Poly counters advance by elapsed 1.79 MHz ticks shared by all channels, applied only on channel events (Poly_adjust).
 *  - The running sum cur_val is rebuilt from Outvol at every Pokey_process call: volume changes on a channel that is "on" only
 *    reach the output at the next call; mixing is 128 - vol/2 + (on ? vol : 0) per channel, truncating vol/2.
 *  - 8-bit unsigned output, no clipping (wraps if it ever overflowed), no DC filter, mono.
 *  - Samp_n_cnt 24.8 fixed point with the 40-bit overlapping-pointer trick: the fraction add is a 32-bit add (carry into the 5th
 *    byte lost), the whole-part subtract works on bytes 1-4.
 */
using System;

namespace NesEmulator
{
    public sealed class AtariPokeySnd
    {
        public const int PlaybackFreq = 44100;      // atari800 default dsprate
        public const uint Freq17Exact = 1789790;    // FREQ_17_EXACT
        public const byte Gain = 4;

        private const byte NOTPOLY5 = 0x80, POLY4 = 0x40, PURE = 0x20, VOL_ONLY = 0x10, VOLUME_MASK = 0x0f;
        private const byte POLY9 = 0x80, CH1_179 = 0x40, CH3_179 = 0x20, CH1_CH2 = 0x10, CH3_CH4 = 0x08, CH1_FILTER = 0x04, CH2_FILTER = 0x02, CLOCK_15 = 0x01;
        private const uint DIV_64 = 28, DIV_15 = 114;
        private const uint POLY4_SIZE = 0x000f, POLY5_SIZE = 0x001f, POLY9_SIZE = 0x01ff, POLY17_SIZE = 0x0001ffff;
        private const int CHAN1 = 0, CHAN2 = 1, CHAN3 = 2, CHAN4 = 3, SAMPLE = 127;
        private const int SAMP_MID = 128;

        private readonly byte[] AUDF = new byte[4], AUDC = new byte[4], AUDV = new byte[4], Outvol = new byte[4];
        private byte AUDCTL;
        private static readonly byte[] bit4 = { 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 1, 1, 0, 1, 0 };
        private static readonly byte[] bit5 = { 1, 1, 1, 1, 0, 1, 1, 0, 1, 0, 0, 1, 1, 0, 0, 0, 0, 0, 1, 1, 1, 0, 0, 1, 0, 0, 0, 1, 0, 1, 0 };
        private static readonly byte[] bit17 = BuildBit17();
        private uint Poly_adjust, P4, P5, P9, P17;
        private readonly uint[] Div_n_cnt = new uint[4], Div_n_max = new uint[4];
        private uint Samp_n_max;
        private ulong sampCnt;   // Samp_n_cnt[0..1] low 40 bits: 8 fraction bits + 32 whole bits (little-endian layout)
        private uint Base_mult;

        public AtariPokeySnd() { Init(Freq17Exact, PlaybackFreq); }

        /// <summary>glibc rand() from the default seed (random_r TYPE_3), &amp; 1, as Pokey_sound_init fills bit17.</summary>
        private static byte[] BuildBit17()
        {
            var r = new int[34 + 344 + (int)POLY17_SIZE];
            r[0] = 1;
            for (int i = 1; i < 31; i++) { long v = (16807L * r[i - 1]) % 2147483647; if (v < 0) v += 2147483647; r[i] = (int)v; }
            for (int i = 31; i < 34; i++) r[i] = r[i - 31];
            for (int i = 34; i < r.Length; i++) r[i] = unchecked(r[i - 31] + r[i - 3]);
            var b = new byte[POLY17_SIZE];
            for (int n = 0; n < POLY17_SIZE; n++) b[n] = (byte)(((uint)r[n + 344] >> 1) & 1);
            return b;
        }

        /// <summary>Pokey_sound_init(freq17, playback_freq, 1). (bit17 is filled once: rand() would continue its sequence on a second call.)</summary>
        public void Init(uint freq17, ushort playback)
        {
            Poly_adjust = 0; P4 = P5 = P9 = P17 = 0;
            Samp_n_max = (freq17 << 8) / playback;
            sampCnt = 0;
            for (int c = 0; c < 4; c++) { Outvol[c] = 0; Div_n_cnt[c] = 0; Div_n_max[c] = 0x7fffffff; AUDC[c] = 0; AUDF[c] = 0; AUDV[c] = 0; }
            AUDCTL = 0; Base_mult = DIV_64;
        }

        /// <summary>Update_pokey_sound(addr, val, chip 0, gain).</summary>
        public void Update(int addr, byte val, byte gain = Gain)
        {
            uint new_val; int chan_mask;
            switch (addr & 0x0f)
            {
                case 0: AUDF[CHAN1] = val; chan_mask = 1 << CHAN1; if ((AUDCTL & CH1_CH2) != 0) chan_mask |= 1 << CHAN2; break;
                case 1: AUDC[CHAN1] = val; AUDV[CHAN1] = (byte)((val & VOLUME_MASK) * gain); chan_mask = 1 << CHAN1; break;
                case 2: AUDF[CHAN2] = val; chan_mask = 1 << CHAN2; break;
                case 3: AUDC[CHAN2] = val; AUDV[CHAN2] = (byte)((val & VOLUME_MASK) * gain); chan_mask = 1 << CHAN2; break;
                case 4: AUDF[CHAN3] = val; chan_mask = 1 << CHAN3; if ((AUDCTL & CH3_CH4) != 0) chan_mask |= 1 << CHAN4; break;
                case 5: AUDC[CHAN3] = val; AUDV[CHAN3] = (byte)((val & VOLUME_MASK) * gain); chan_mask = 1 << CHAN3; break;
                case 6: AUDF[CHAN4] = val; chan_mask = 1 << CHAN4; break;
                case 7: AUDC[CHAN4] = val; AUDV[CHAN4] = (byte)((val & VOLUME_MASK) * gain); chan_mask = 1 << CHAN4; break;
                case 8: AUDCTL = val; chan_mask = 15; Base_mult = (AUDCTL & CLOCK_15) != 0 ? DIV_15 : DIV_64; break;
                default: chan_mask = 0; break;
            }

            if ((chan_mask & (1 << CHAN1)) != 0)
            {
                new_val = (AUDCTL & CH1_179) != 0 ? AUDF[CHAN1] + 4u : (AUDF[CHAN1] + 1u) * Base_mult;
                SetMax(CHAN1, new_val);
            }
            if ((chan_mask & (1 << CHAN2)) != 0)
            {
                if ((AUDCTL & CH1_CH2) != 0)
                    new_val = (AUDCTL & CH1_179) != 0 ? AUDF[CHAN2] * 256u + AUDF[CHAN1] + 7u : (AUDF[CHAN2] * 256u + AUDF[CHAN1] + 1u) * Base_mult;
                else new_val = (AUDF[CHAN2] + 1u) * Base_mult;
                SetMax(CHAN2, new_val);
            }
            if ((chan_mask & (1 << CHAN3)) != 0)
            {
                new_val = (AUDCTL & CH3_179) != 0 ? AUDF[CHAN3] + 4u : (AUDF[CHAN3] + 1u) * Base_mult;
                SetMax(CHAN3, new_val);
            }
            if ((chan_mask & (1 << CHAN4)) != 0)
            {
                if ((AUDCTL & CH3_CH4) != 0)
                    new_val = (AUDCTL & CH3_179) != 0 ? AUDF[CHAN4] * 256u + AUDF[CHAN3] + 7u : (AUDF[CHAN4] * 256u + AUDF[CHAN3] + 1u) * Base_mult;
                else new_val = (AUDF[CHAN4] + 1u) * Base_mult;
                SetMax(CHAN4, new_val);
            }

            for (int chan = CHAN1; chan <= CHAN4; chan++)
            {
                if ((chan_mask & (1 << chan)) == 0) continue;
                if ((AUDC[chan] & VOL_ONLY) != 0 || (AUDC[chan] & VOLUME_MASK) == 0 || Div_n_max[chan] < (Samp_n_max >> 8))
                {
                    Outvol[chan] = 1;
                    if ((chan == CHAN3 && (AUDCTL & CH1_FILTER) == 0) || (chan == CHAN4 && (AUDCTL & CH2_FILTER) == 0) ||
                        chan == CHAN1 || chan == CHAN2 || Div_n_max[chan] < (Samp_n_max >> 8))
                    {
                        Div_n_max[chan] = 0x7fffffff;
                        Div_n_cnt[chan] = 0x7fffffff;
                    }
                }
            }
        }

        private void SetMax(int ch, uint new_val)
        {
            if (new_val != Div_n_max[ch]) { Div_n_max[ch] = new_val; if (Div_n_cnt[ch] > new_val) Div_n_cnt[ch] = new_val; }
        }

        private uint SampWhole { get => (uint)(sampCnt >> 8); set => sampCnt = (sampCnt & 0xFF) | ((ulong)value << 8); }

        /// <summary>Pokey_process(buffer, n): fills n unsigned 8-bit samples.</summary>
        public void Process(Span<byte> buffer)
        {
            int n = buffer.Length, o = 0;
            byte cur_val = SAMP_MID;   // uint8 (CLIP off)
            for (int c = 0; c < 4; c++) { cur_val = (byte)(cur_val - AUDV[c] / 2); if (Outvol[c] != 0) cur_val = (byte)(cur_val + AUDV[c]); }

            while (n > 0)
            {
                int next_event = SAMPLE;
                uint event_min = SampWhole;
                for (int c = 0; c < 4; c++) if (Div_n_cnt[c] <= event_min) { event_min = Div_n_cnt[c]; next_event = c; }
                for (int c = 3; c >= 0; c--) Div_n_cnt[c] -= event_min;
                SampWhole = SampWhole - event_min;
                Poly_adjust += event_min;

                if (next_event != SAMPLE)
                {
                    P4 = (P4 + Poly_adjust) % POLY4_SIZE;
                    P5 = (P5 + Poly_adjust) % POLY5_SIZE;
                    P9 = (P9 + Poly_adjust) % POLY9_SIZE;
                    P17 = (P17 + Poly_adjust) % POLY17_SIZE;
                    Poly_adjust = 0;
                    Div_n_cnt[next_event] += Div_n_max[next_event];
                    byte audc = AUDC[next_event];
                    bool toggle = false;
                    bool outOn = Outvol[next_event] != 0;
                    if ((audc & VOL_ONLY) == 0)
                    {
                        if ((audc & NOTPOLY5) != 0 || bit5[P5] != 0)
                        {
                            if ((audc & PURE) != 0) toggle = true;
                            else if ((audc & POLY4) != 0) toggle = bit4[P4] == (outOn ? 0 : 1);
                            else if ((AUDCTL & POLY9) != 0) toggle = bit17[P9] == (outOn ? 0 : 1);
                            else toggle = bit17[P17] == (outOn ? 0 : 1);
                        }
                    }
                    if ((AUDCTL & CH1_FILTER) != 0 && (next_event & 3) == CHAN3)
                    {
                        int f = next_event & 0xfd;
                        if (Outvol[f] != 0) { Outvol[f] = 0; cur_val = (byte)(cur_val - AUDV[f]); }
                    }
                    if ((AUDCTL & CH2_FILTER) != 0 && (next_event & 3) == CHAN4)
                    {
                        int f = next_event & 0xfd;
                        if (Outvol[f] != 0) { Outvol[f] = 0; cur_val = (byte)(cur_val - AUDV[f]); }
                    }
                    if (toggle)
                    {
                        if (Outvol[next_event] != 0) { cur_val = (byte)(cur_val - AUDV[next_event]); Outvol[next_event] = 0; }
                        else { Outvol[next_event] = 1; cur_val = (byte)(cur_val + AUDV[next_event]); }
                    }
                }
                else
                {
                    // *Samp_n_cnt += Samp_n_max: a 32-bit add on the low word, the carry into the 5th byte is lost
                    uint lo = unchecked((uint)sampCnt + Samp_n_max);
                    sampCnt = (sampCnt & 0xFF00000000UL) | lo;
                    buffer[o++] = cur_val;
                    n--;
                }
            }
        }
    }
}
