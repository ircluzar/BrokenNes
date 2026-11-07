/*
 * PceGoPsg - retro-go PCE-GO's HuC6280 PSG (psg.c), ported to C#, plus the BrokenNes NES-to-PSG translator.
 * Source project: retro-go (ducalex), component pce-go (PC Engine emulator; lineage Hu-Go! by Zeograd -> HuExpress -> PCE-GO).
 * Version: commit 05e97e8 (2022-11-18), the last one before 404025b removed the audio downsampling option.
 * Files ported: retro-core/components/pce-go/psg.c (psg_update_chan, psg_init, psg_update), psg.h, the PSG part of pce.h
 *   (psg_chan_t, PSG_* flags, CLOCK_PSG = 21477270 / 6).
 * URL: https://github.com/ducalex/retro-go/tree/05e97e8/retro-core/components/pce-go
 * Authors: Alex Duchesne (ducalex) and the retro-go contributors; earlier Hu-Go!/HuExpress/PCE-GO authors (Zeograd and others).
 * License: GNU GPL v2 (retro-go COPYING).
 * This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2a and docs/projects/apu-harvest-catalog.md
 *
 * Ported part (PceGoPsg): 6 channels, 32-sample 5-bit wave RAM, 256-byte DDA ring, output at 22050 Hz (psg.c's static default
 * samplerate), mono (psg_init(_, false)): each channel averages its L and R volumes.
 * Downsampling option: kept as a constructor flag, default OFF. The flag is a psg_update() argument chosen by the retro-go front end
 * (not in the three harvested files), so its default could not be checked here; OFF is the plain int16 path.
 * Characteristic flaws kept on purpose:
 *  - Pitch: fixed_inc = ((CLOCK_PSG / samplerate) << 16) / Tp in integer maths: 3579545/22050 truncates to 162 (-0.2%), all notes flat;
 *    no interpolation, nearest wave step (aliasing). Wave index is the accumulator's top 5 bits, accumulator masked to 0x1FFFFF.
 *  - Volume: per-channel (balance nibble * 1.1 * volume) / 32 (0..15, truncated); master volume is a straight multiply (no log table);
 *    the 32-step vol_tbl is only used by DDA, at even indexes (vol_tbl[v << 1]).
 *  - Wave/DDA samples are unsigned 0..31 recentred as (s - 16), then +1 if >= 0 (no zero level: -16..-1, 1..16).
 *  - DDA: no timing at all. Each psg_update call plays the samples queued since the last call, each repeated exactly 3 output samples,
 *    then (while DDA stays enabled) pads with level 0, not the last sample. DDA plays even with the channel enable bit clear.
 *    The C writes up to 2 samples past the buffer end when a repeat straddles it (stack overflow into mix_buffer[length+1]...);
 *    here the extra samples are dropped (the only deviation; C# arrays cannot be overrun).
 *  - Noise only on channels 4 and 5, 1-bit +-15 output, period from accum += 3000 + Np*512 per output sample (higher Np = higher pitch),
 *    LFSR seeds 0x51F63101 / 0x1F631042 from psg_init, tap ((r ^ 4) << 1) + 1 on bit 19.
 *  - No LFO (registers 8/9 are stored but psg.c never reads them).
 *  - Mono quirk: psg_update still mixes in L/R pairs (output[j] * master-left, output[j+1] * master-right) over a mono buffer, so
 *    even and odd output samples use different master nibbles. The int16 sum wraps on overflow.
 *  - Disabling a channel resets its wave accumulator (phase) to 0.
 *
 * BrokenNes translator (NesToPceTranslator, new code, NOT ported; pce.c's register-write side is not part of the harvest):
 *  - NES-side logic is not duplicated: the translator reads APU_FIX's own channel state (ProbeControl: length counters, envelopes,
 *    sweep, linear counter, DMC output level) once per PSG update (~ every 37 output samples, ~10x per NES frame as in PCE-GO).
 *  - Pulse 1 / 2 -> PSG ch0 / ch1, wave RAM = 32-step square holding the NES duty (4/8/16/24 high steps of 32 = 12.5/25/50/75%).
 *  - Triangle -> ch2, 32-step triangle wave at full volume (31), keyed while length and linear counters are non-zero.
 *  - Noise -> ch4 in noise mode, NES period index 0..15 -> PSG noise frequency Np = 31 - 2*index (5 bits). ch5 unused.
 *  - DMC / $4011 -> ch3 in DDA mode: the 7-bit DAC level is sampled every ~243 CPU cycles (one DDA sample per 3 PSG output
 *    samples, matching the fixed repeat) and fed as a DDA sample (top 5 bits, level >> 2), volume 31.
 *  - Pitch: Tp = round(3579545 / (32 * f)); pulse f = 1789773 / (16 * (p + 1)), triangle f = 1789773 / (32 * (p + 1)); Tp clamped 1..4095.
 *    Pulse period < 8 or a sweep target > $7FF mutes; triangle period < 2 mutes (ultrasonic).
 *  - Volume: NES 0..15 (constant or envelope decay) -> PSG channel volume x2 (0..30). Master volume $FF, balance $FF (centre).
 *  - Channel mask: a masked NES channel clears its PSG channel's enable bit (and stops DDA feeding).
 */
using System;

namespace NesEmulator
{
    /// <summary>The ported PCE-GO PSG (psg.c @05e97e8).</summary>
    public sealed class PceGoPsg
    {
        public const int PSG_CHANNELS = 6;
        public const int CLOCK_PSG = 21477270 / 6;
        public const byte PSG_CHAN_ENABLE = 0x80, PSG_DDA_ENABLE = 0x40, PSG_NOISE_ENABLE = 0x80;

        public sealed class Chan
        {
            public byte freq_lsb, freq_msb, control, balance, wave_index, noise_ctrl;
            public readonly byte[] wave_data = new byte[32];
            public readonly byte[] dda_data = new byte[256];
            public uint dda_count, dda_index, wave_accum;
            public int noise_accum, noise_level, noise_rand;
        }

        public byte ch, volume, lfo_freq, lfo_ctrl;
        public readonly Chan[] chan = new Chan[PSG_CHANNELS];

        private static readonly int[] vol_tbl = {
            100 >> 8, 451 >> 8, 508 >> 8, 573 >> 8, 646 >> 8, 728 >> 8, 821 >> 8, 925 >> 8,
            1043 >> 8, 1175 >> 8, 1325 >> 8, 1493 >> 8, 1683 >> 8, 1898 >> 8, 2139 >> 8, 2411 >> 8,
            2718 >> 8, 3064 >> 8, 3454 >> 8, 3893 >> 8, 4388 >> 8, 4947 >> 8, 5576 >> 8, 6285 >> 8,
            7085 >> 8, 7986 >> 8, 9002 >> 8, 10148 >> 8, 11439 >> 8, 12894 >> 8, 14535 >> 8, 16384 >> 8 };

        private int samplerate = 22050;
        private bool stereo = true;
        private short[] mix_buffer = new short[64];

        public PceGoPsg() { for (int i = 0; i < PSG_CHANNELS; i++) chan[i] = new Chan(); }

        public int psg_init(int _samplerate, bool _stereo)
        {
            chan[4].noise_rand = 0x51F63101;
            chan[5].noise_rand = 0x1F631042;
            samplerate = _samplerate;
            stereo = _stereo;
            return 0;
        }

        private void psg_update_chan(short[] buf, int ch, int dwSize)
        {
            Chan c = chan[ch];
            int sample = 0;
            uint Tp;
            int p = 0, buf_end = dwSize;

            int lvol = (int)((((c.balance >> 4) * 1.1) * (c.control & 0x1F)) / 32);
            int rvol = (int)((((c.balance & 0xF) * 1.1) * (c.control & 0x1F)) / 32);

            if (!stereo) lvol = (lvol + rvol) / 2;

            if (c.dda_count != 0)
            {
                int start = (int)c.dda_index - (int)c.dda_count;
                if (start < 0) start += 0x100;
                int repeat = 3;
                lvol = vol_tbl[lvol << 1];
                rvol = vol_tbl[rvol << 1];

                while (p < buf_end && (c.dda_count != 0 || (c.control & PSG_DDA_ENABLE) != 0))
                {
                    if (c.dda_count != 0)
                    {
                        if ((sample = (c.dda_data[(start++) & 0xFF] - 16)) >= 0) sample++;
                        c.dda_count--;
                    }
                    for (int i = 0; i < repeat; i++)
                    {
                        if (p < buf_end) buf[p] = (short)(sample * lvol); p++;   // C overruns here; dropped
                        if (stereo) { if (p < buf_end) buf[p] = (short)(sample * rvol); p++; }
                    }
                }
                if (p > buf_end) p = buf_end;
            }

            if ((c.control & PSG_CHAN_ENABLE) == 0)
            {
                c.wave_accum = 0;
            }
            else if ((ch == 4 || ch == 5) && (c.noise_ctrl & PSG_NOISE_ENABLE) != 0)
            {
                int Np = c.noise_ctrl & 0x1F;
                while (p < buf_end)
                {
                    c.noise_accum += 3000 + Np * 512;
                    if ((Tp = (uint)(c.noise_accum / samplerate)) >= 1)
                    {
                        if ((c.noise_rand & 0x00080000) != 0) { c.noise_rand = ((c.noise_rand ^ 0x0004) << 1) + 1; c.noise_level = -15; }
                        else { c.noise_rand <<= 1; c.noise_level = 15; }
                        c.noise_accum -= samplerate * (int)Tp;
                    }
                    buf[p++] = (short)(c.noise_level * lvol);
                    if (stereo) buf[p++] = (short)(c.noise_level * rvol);
                }
            }
            else if ((c.control & PSG_DDA_ENABLE) != 0)
            {
            }
            else if ((Tp = (uint)(c.freq_lsb + (c.freq_msb << 8))) > 0)
            {
                uint fixed_inc = ((uint)(CLOCK_PSG / samplerate) << 16) / Tp;
                while (p < buf_end)
                {
                    if ((sample = (c.wave_data[c.wave_index] - 16)) >= 0) sample++;
                    buf[p++] = (short)(sample * lvol);
                    if (stereo) buf[p++] = (short)(sample * rvol);
                    c.wave_accum += fixed_inc;
                    c.wave_accum &= 0x1FFFFF;
                    c.wave_index = (byte)(c.wave_accum >> 16);
                }
            }

            if (p < buf_end) Array.Clear(buf, p, buf_end - p);
        }

        public void psg_update(short[] output, int length, bool downsample)
        {
            int lvol = volume >> 4;
            int rvol = volume & 0x0F;
            if (stereo) length *= 2;
            Array.Clear(output, 0, length);
            if (mix_buffer.Length < length + 1) mix_buffer = new short[length + 1];

            for (int i = 0; i < PSG_CHANNELS; i++)
            {
                psg_update_chan(mix_buffer, i, length);
                if (downsample)
                {
                    for (int j = 0; j < length; j += 2)
                    {
                        output[j] += (short)((byte)mix_buffer[j] * lvol);
                        if (j + 1 < length) output[j + 1] += (short)((byte)mix_buffer[j + 1] * rvol);
                    }
                }
                else
                {
                    for (int j = 0; j < length; j += 2)
                    {
                        output[j] += (short)(mix_buffer[j] * lvol);
                        if (j + 1 < length) output[j + 1] += (short)(mix_buffer[j + 1] * rvol);
                    }
                }
            }
        }
    }

    /// <summary>BrokenNes translator (new code, not ported): plays APU_FIX's NES channel state on the PCE-GO PSG. See the file header.</summary>
    public sealed class NesToPceTranslator
    {
        public const int SampleRate = 22050;
        private const double NesClock = 1789773.0, PceClock = 3579545.0;
        private const int Chunk = 37;                 // output samples per psg_update (22050 / 60 / 10)
        private const double DdaCycles = NesClock * 3 / SampleRate;   // one DDA sample per 3 output samples

        private readonly PceGoPsg psg = new PceGoPsg();
        private readonly Func<APU_FIX.ApuControlSnapshot> probe;
        private readonly bool downsample;
        private readonly short[] buf = new short[Chunk];
        private readonly float[] ring = new float[1 << 15];
        private int head, count;
        private double sampleAcc, ddaAcc;
        private readonly int[] lastDuty = { -1, -1 };
        public int ChannelMask { get; set; } = 0x1F;

        private static readonly int[] DutySteps = { 4, 8, 16, 24 };

        public NesToPceTranslator(Func<APU_FIX.ApuControlSnapshot> probe, bool downsample = false)
        {
            this.probe = probe; this.downsample = downsample;
            HardReset();
        }

        public void HardReset()
        {
            for (int i = 0; i < PceGoPsg.PSG_CHANNELS; i++)
            {
                var c = psg.chan[i];
                c.freq_lsb = c.freq_msb = c.control = c.wave_index = c.noise_ctrl = 0; c.balance = 0xFF;
                Array.Clear(c.wave_data); Array.Clear(c.dda_data);
                c.dda_count = c.dda_index = c.wave_accum = 0; c.noise_accum = c.noise_level = 0;
            }
            psg.psg_init(SampleRate, false);
            psg.volume = 0xFF;
            for (int i = 0; i < 32; i++) psg.chan[2].wave_data[i] = (byte)(i < 16 ? i * 2 : (31 - i) * 2 + 1);
            lastDuty[0] = lastDuty[1] = -1;
            sampleAcc = ddaAcc = 0;
            ClearOutput();
        }

        /// <summary>A register write: only $4011 matters directly (an immediate DDA sample); the rest is read from APU_FIX at each update.</summary>
        public void Write(ushort addr, byte v)
        {
            if (addr == 0x4011 && (ChannelMask & 0x10) != 0) PushDda(v & 0x7F);
        }

        private void PushDda(int level)
        {
            var c = psg.chan[3];
            c.dda_data[c.dda_index & 0xFF] = (byte)((level >> 2) & 0x1F);
            c.dda_index = (c.dda_index + 1) & 0xFF;
            if (c.dda_count < 256) c.dda_count++;
        }

        public void Clock(int cpuCycles)
        {
            if ((ChannelMask & 0x10) != 0)
            {
                ddaAcc += cpuCycles;
                while (ddaAcc >= DdaCycles) { ddaAcc -= DdaCycles; PushDda(probe().DmcOutput & 0x7F); }
            }
            sampleAcc += cpuCycles * (SampleRate / NesClock);
            while (sampleAcc >= Chunk) { sampleAcc -= Chunk; Apply(probe()); Render(); }
        }

        private static void SetFreq(PceGoPsg.Chan c, double f)
        {
            int tp = (int)Math.Round(PceClock / (32 * f));
            tp = Math.Clamp(tp, 1, 4095);
            c.freq_lsb = (byte)tp; c.freq_msb = (byte)(tp >> 8);
        }

        private static bool SweepMute(int timer, bool negate, int shift, bool ch1)
        {
            int change = timer >> shift;
            int target = negate ? timer - change - (ch1 ? 1 : 0) : timer + change;
            return target > 0x7FF;
        }

        private void Pulse(int i, bool on, int duty, int timer, int vol)
        {
            var c = psg.chan[i];
            if (duty != lastDuty[i])
            {
                lastDuty[i] = duty;
                for (int k = 0; k < 32; k++) c.wave_data[k] = (byte)(k < DutySteps[duty & 3] ? 31 : 0);
            }
            if (on && vol > 0) { SetFreq(c, NesClock / (16.0 * (timer + 1))); c.control = (byte)(PceGoPsg.PSG_CHAN_ENABLE | (vol * 2)); }
            else c.control = 0;
        }

        private void Apply(APU_FIX.ApuControlSnapshot s)
        {
            int m = ChannelMask;
            bool p1 = (m & 1) != 0 && s.P1Enabled && s.P1Length > 0 && s.P1Timer >= 8 && !SweepMute(s.P1Timer, s.P1SweepNegate, s.P1SweepShift, true);
            Pulse(0, p1, s.P1Duty, s.P1Timer, s.P1Const ? s.P1Vol : s.P1EnvDecay);
            bool p2 = (m & 2) != 0 && s.P2Enabled && s.P2Length > 0 && s.P2Timer >= 8 && !SweepMute(s.P2Timer, s.P2SweepNegate, s.P2SweepShift, false);
            Pulse(1, p2, s.P2Duty, s.P2Timer, s.P2Const ? s.P2Vol : s.P2EnvDecay);

            var t = psg.chan[2];
            if ((m & 4) != 0 && s.TEnabled && s.TLength > 0 && s.TLinear > 0 && s.TTimer >= 2)
            { SetFreq(t, NesClock / (32.0 * (s.TTimer + 1))); t.control = PceGoPsg.PSG_CHAN_ENABLE | 31; }
            else t.control = 0;

            var n = psg.chan[4];
            int nv = s.NConst ? s.NVol : s.NEnvDecay;
            if ((m & 8) != 0 && s.NEnabled && s.NLength > 0 && nv > 0)
            { n.noise_ctrl = (byte)(PceGoPsg.PSG_NOISE_ENABLE | (31 - 2 * (s.NPeriodReg & 15))); n.control = (byte)(PceGoPsg.PSG_CHAN_ENABLE | (nv * 2)); }
            else { n.control = 0; n.noise_ctrl = 0; }

            var d = psg.chan[3];
            d.control = (m & 0x10) != 0 ? (byte)(PceGoPsg.PSG_CHAN_ENABLE | PceGoPsg.PSG_DDA_ENABLE | 31) : (byte)0;
            if ((m & 0x10) == 0) d.dda_count = 0;
        }

        private void Render()
        {
            psg.psg_update(buf, Chunk, downsample);
            for (int i = 0; i < Chunk; i++)
            {
                if (count == ring.Length) { head = (head + 1) & (ring.Length - 1); count--; }
                ring[(head + count) & (ring.Length - 1)] = buf[i] / 32768f;
                count++;
            }
        }

        public int Queued => count;
        public int Read(Span<float> dest)
        {
            int n = Math.Min(count, dest.Length);
            for (int i = 0; i < n; i++) dest[i] = ring[(head + i) & (ring.Length - 1)];
            head = (head + n) & (ring.Length - 1); count -= n;
            return n;
        }
        public void ClearOutput() { head = count = 0; }
    }
}
