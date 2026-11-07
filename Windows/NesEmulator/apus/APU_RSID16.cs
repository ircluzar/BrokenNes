/*
 * APU_RSID16 - NES sound re-keyed onto reSID 0.16's MOS 6581 SID (see Resid016.cs), on top of the stock APU_FIX.
 * Source of the chip: reSID 0.16 by Dag Lem (2004), mirror https://github.com/Olzaq/reSID @13152b680ecb7845005433a2d17d5fd8cb60261d.
 * License of the chip code: GPL-2 or later. The translator below is new code (not ported).
 * This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2a and docs/projects/apu-harvest-catalog.md
 *
 * Translation (new code; the NES state is read from APU_FIX after every register write and every Step, only changed SID registers are written):
 *  - Pulse 1 -> voice 1 pulse, pulse 2 -> voice 2 pulse, triangle -> voice 3 triangle. NES duty -> SID pulse width of 0xFFF:
 *    12.5% = 0x200, 25% = 0x400, 50% = 0x800, 75% = 0xC00 (the SID output is high while acc>>12 >= PW, i.e. the inverted duty - same sound).
 *  - Pitch: SID f = Fn * 1022727 / 16777216 (NTSC C64 clock). NES pulse f = 1789773/(16*(p+1)), triangle 1789773/(32*(p+1)); Fn is rounded
 *    and clamped to 0xFFFF (~3.9 kHz, the SID's top: NES notes above that are flattened to it). The SID is clocked at 1022727/1789773 of the CPU.
 *  - Envelopes: ADSR attack 0, decay 0, release 0, sustain = the NES volume in force (constant volume or the envelope's decay level, 0-15;
 *    triangle 15). Gate on while the NES channel sounds (length > 0, pulse period >= 8, triangle linear counter > 0), off otherwise
 *    (release 0 = ~6 ms fade, not an instant stop). The 6581 sustain quirk is faithful: in DECAY_SUSTAIN the envelope only steps DOWN toward
 *    the sustain level, so a lower volume is simply written; a HIGHER volume would make the envelope fall to zero and freeze, so the
 *    translator re-gates (gate off then on in the same cycle: attack 0 runs back up to 0xFF in ~2.3 ms and decays to the new level) - every
 *    volume increase, i.e. every step of an NES envelope "loop" restart, is an audible re-attack. Sweep-overflow muting is not translated.
 *  - Noise: the SID noise waveform, NES noise rate (1789773/period LFSR clocks/s) -> Fn = rate * 2^20 / 1022727 (one SID shift per 2^20 of
 *    accumulator), clamped to 0xFFFF: the eight fastest NES rates collapse onto the SID's top noise rate. Short-loop mode is dropped. It plays
 *    on voice 3 when the triangle is silent, else on voice 2 when pulse 2 is silent, else on voice 1 when pulse 1 is silent, else not at all.
 *    A voice that changes role keeps its envelope state (the role switch is a waveform/frequency rewrite, re-gated only if the level rises).
 *  - DMC / $4011: while a DMC sample plays, or within one frame (29780 CPU cycles) of a $4011 write, the master volume ($D418 low nibble)
 *    is the 7-bit DAC level >> 3 - the classic C64 "$D418 sample" trick, which works through the 6581's DC offsets. It modulates EVERYTHING:
 *    the three voices are scaled by the sample too. Otherwise master volume 15. Filter routing none ($D417 = 0), filter mode none.
 *  - SetEnabledChannels masks the NES source channels (bit0 pulse1, bit1 pulse2, bit2 triangle, bit3 noise, bit4 dmc) before translation.
 */
using System;

namespace NesEmulator
{
    public sealed class APU_RSID16 : APU_FIX, IAPU
    {
        private Resid016? engine;
        private int mask = 0x1F;
        private readonly float[] drain = new float[2048];
        private readonly int[] shadow = new int[0x19];
        private readonly int[] sustain = new int[3];
        private readonly bool[] gated = new bool[3];
        private long cycle, last4011 = long.MinValue / 2;
        private long sidFrac;

        private static readonly int[] NoisePeriods = { 4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068 };
        private static readonly int[] DutyPw = { 0x200, 0x400, 0x800, 0xC00 };

        public APU_RSID16(Bus bus) : base(bus) { }

        private Resid016 E
        {
            get
            {
                if (engine == null)
                {
                    engine = new Resid016(); Array.Fill(shadow, -1); Array.Clear(sustain); Array.Clear(gated);
                    for (int v = 0; v < 3; v++) { W(v * 7 + 5, 0x00); W(v * 7 + 6, 0x00); }
                    W(0x15, 0); W(0x16, 0); W(0x17, 0x00); W(0x18, 0x0F);
                }
                return engine;
            }
        }

        public new string CoreName => "reSID 0.16";
        public new string Description => "NES register writes re-keyed onto reSID 0.16's MOS 6581 SID (Dag Lem, 2004, the pre-1.0 linear filter): pulses with SID pulse widths, SID triangle, noise on a free voice, gate/sustain envelopes with the 6581 re-attack on every volume rise, DMC through the $D418 volume-nibble trick";
        public new int Performance => 4;
        public new int Rating => 1;
        public new string Category => "Unstable";

        private void W(int r, int v)
        {
            if (shadow[r] == v) return;
            shadow[r] = v;
            engine!.Write(r, v);
        }

        private static int Fn(double hz) { int f = (int)Math.Round(hz * 16777216.0 / Resid016.Clock); return f < 0 ? 0 : (f > 0xFFFF ? 0xFFFF : f); }

        /// <summary>One voice: waveform bits (0x10 tri, 0x40 pulse, 0x80 noise), frequency, pulse width, wanted level (-1 = gate off).</summary>
        private void Voice(int v, int wave, int fn, int pw, int level)
        {
            int b = v * 7;
            W(b + 0, fn & 0xFF); W(b + 1, fn >> 8);
            W(b + 2, pw & 0xFF); W(b + 3, pw >> 8);
            if (level < 0)
            {
                if (gated[v]) { gated[v] = false; }
                W(b + 4, wave);
                return;
            }
            if (!gated[v])
            {
                sustain[v] = level; W(b + 6, level << 4);
                gated[v] = true; W(b + 4, wave | 1);
                return;
            }
            if (level < sustain[v]) { sustain[v] = level; W(b + 6, level << 4); }
            else if (level > sustain[v])
            {
                // 6581 sustain quirk: a raise is not followed without a re-gate (see the header)
                sustain[v] = level; W(b + 6, level << 4);
                W(b + 4, wave); W(b + 4, wave | 1);
            }
            W(b + 4, wave | 1);
        }

        private void Sync()
        {
            _ = E;
            var s = base.ProbeControl();
            bool on1 = (mask & 1) != 0, on2 = (mask & 2) != 0, onT = (mask & 4) != 0, onN = (mask & 8) != 0, onD = (mask & 16) != 0;

            bool s1 = on1 && s.P1Length > 0 && s.P1Timer >= 8, s2 = on2 && s.P2Length > 0 && s.P2Timer >= 8;
            bool sT = onT && s.TLength > 0 && s.TLinear > 0, sN = onN && s.NLength > 0;
            int v1 = (s.P1Const ? s.P1Vol : s.P1EnvDecay) & 15, v2 = (s.P2Const ? s.P2Vol : s.P2EnvDecay) & 15, vn = (s.NConst ? s.NVol : s.NEnvDecay) & 15;
            bool dmc = onD && ((s.DmcEnabled && s.DmcRemaining > 0) || cycle - last4011 < 29780);

            int noiseVoice = !sN ? -1 : !sT ? 2 : !s2 ? 1 : !s1 ? 0 : -1;
            int nfn = Fn(1789773.0 / NoisePeriods[s.NPeriodReg & 15] / 16.0);   // Fn(hz) = hz*2^24/clock, so rate*2^20/clock = Fn(rate/16)

            if (noiseVoice == 0) Voice(0, 0x80, nfn, 0, vn);
            else Voice(0, 0x40, Fn(1789773.0 / (16.0 * (s.P1Timer + 1))), DutyPw[s.P1Duty & 3], s1 ? v1 : -1);
            if (noiseVoice == 1) Voice(1, 0x80, nfn, 0, vn);
            else Voice(1, 0x40, Fn(1789773.0 / (16.0 * (s.P2Timer + 1))), DutyPw[s.P2Duty & 3], s2 ? v2 : -1);
            if (noiseVoice == 2) Voice(2, 0x80, nfn, 0, vn);
            else Voice(2, 0x10, Fn(1789773.0 / (32.0 * (s.TTimer + 1))), 0, sT ? 15 : -1);

            W(0x18, dmc ? (s.DmcOutput & 0x7F) >> 3 : 0x0F);
        }

        public new void SetState(object state)
        {
            base.SetState(state);
            // the SID's own state is not in the save: a fresh chip is re-keyed from the restored NES registers (read through APU_FIX)
            engine = null;
            Sync();
        }

        public new void WriteAPURegister(ushort address, byte value)
        {
            base.WriteAPURegister(address, value);
            if (address == 0x4011) last4011 = cycle;
            Sync();
        }

        public new void Step(int cpuCycles)
        {
            base.Step(cpuCycles);
            if (base.GetQueuedSampleCount() > 4096) while (base.ReadSamples(drain) > 0) { }
            cycle += cpuCycles;
            Sync();
            sidFrac += (long)cpuCycles * Resid016.Clock;
            int sidCycles = (int)(sidFrac / 1789773);
            sidFrac -= (long)sidCycles * 1789773;
            E.Run(sidCycles);
        }

        public new float[] GetAudioSamples(int maxSamples = 0)
        {
            int n = E.Queued;
            if (maxSamples > 0 && maxSamples < n) n = maxSamples;
            if (n > 4096 && maxSamples == 0) n = 4096;
            var r = new float[n];
            E.Read(r);
            return r;
        }

        public new int ReadSamples(Span<float> dest) => E.Read(dest);
        public new float[] GetAudioBuffer() => GetAudioSamples();
        public new int GetQueuedSampleCount() => E.Queued;
        public new int GetSampleRate() => Resid016.SampleRate;

        public new void SetEnabledChannels(int channelMask)
        {
            mask = channelMask & 0x1F;
            base.SetEnabledChannels(channelMask);
            Sync();
        }

        public new void ClearAudioBuffers()
        {
            base.ClearAudioBuffers();
            engine?.ClearOutput();
        }

        public new void Reset()
        {
            base.Reset();
            engine?.ClearOutput();
        }
    }
}
