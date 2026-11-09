/*
 * APU_AY124 - NES sound re-keyed onto MAME 0.124's AY-3-8910 (see Mame0124Ay8910.cs), on top of the stock APU_FIX.
 * Source of the chip: MAME tag mame0124, src/emu/sound/ay8910.c - https://github.com/mamedev/mame/blob/mame0124/src/emu/sound/ay8910.c
 * Authors of the chip code: Ville Hallik, Michael Cuddy, Tatsuyuki Satoh, Fabrice Frances, Nicola Salmoria.
 * License: MAME license (non-commercial), see docs/license.txt of mame0124. The translator below is new code (not ported).
 * This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2a and docs/projects/apu-harvest-catalog.md
 *
 * Translation (new code; the NES state is read from APU_FIX after every register write and every Step, and only changed AY registers are written):
 *  - Pulse 1 -> tone A, pulse 2 -> tone B, triangle -> tone C. The AY only makes 50% squares: the pulse duty cannot be expressed and is
 *    dropped, and the triangle becomes a square at the right pitch (no ramp).
 *  - Pitch: AY f = clock/(16*TP) with the AY clocked at 1789773 Hz (the NES CPU clock). NES pulse f = 1789773/(16*(p+1)) so TP = p+1;
 *    triangle f = 1789773/(32*(p+1)) so TP = 2*(p+1), clamped to the 12-bit maximum 4095 (only p = 2047 is affected, ~0.02% sharp).
 *  - Volume: NES 0-15 (constant volume or the envelope's current decay level) -> AY fixed amplitude 0-15 (no AY envelope used). A pulse
 *    is silent when its length counter is 0 or its period is below 8; sweep-overflow muting is not translated.
 *    Triangle: fixed amplitude 12 while length and linear counters are both non-zero, else 0 (the NES triangle's held DC step is lost).
 *  - Noise: the single AY noise generator, NES period (CPU cycles) / 16 -> the 5-bit AY noise period (clamped 1..31: the four fastest NES
 *    rates all become 1, the slowest ones all 31); the NES short-loop mode is dropped. It is mixed into the first free channel, C, then B,
 *    then A (a channel is free when its own NES channel is silent); its amplitude is the NES noise volume. When none is free it is gated into
 *    channel C on top of the triangle (AY mixing ANDs tone and noise, so the triangle note turns into noise-chopped square), at the louder of the two.
 *  - DMC / $4011: while a DMC sample plays, or within one frame (29780 CPU cycles) of a $4011 write, channel C is a 4-bit DAC: tone and noise
 *    off (the AY then outputs a constant 1, scaled by the amplitude) and the DAC level's top 4 bits (7-bit level >> 3) written as its amplitude -
 *    the classic AY sample trick. The triangle is dropped while that lasts; updates land at the CPU-step granularity of the emulator loop.
 *  - SetEnabledChannels masks the NES source channels (bit0 pulse1, bit1 pulse2, bit2 triangle, bit3 noise, bit4 dmc) before translation.
 */
using System;

namespace NesEmulator
{
    public sealed class APU_AY124 : APU_FIX, IAPU, IPortedEngineState
    {
        private Mame0124Ay8910? engine;
        private int mask = 0x1F;
        private readonly float[] drain = new float[2048];
        private readonly int[] shadow = new int[14];
        private long cycle, last4011 = long.MinValue / 2;

        private static readonly int[] NoisePeriods = { 4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068 };

        public APU_AY124(Bus bus) : base(bus) { }

        private Mame0124Ay8910 E
        {
            get
            {
                if (engine == null) { engine = new Mame0124Ay8910(); Array.Fill(shadow, -1); }
                return engine;
            }
        }

        public new string CoreName => "MAME 0.124 AY-3-8910";
        public new string Description => "NES register writes re-keyed onto MAME 0.124's AY-3-8910 (pre-0.124u4 code, 1.5 dB volume table, no resistor mixing): squares only, no duty, triangle as a square, noise on a free channel, DMC as a 4-bit AY DAC";
        public new int Performance => 4;
        public new int Rating => 1;
        public new string Category => "Unstable";

        private void W(int r, int v)
        {
            if (shadow[r] == v) return;
            shadow[r] = v;
            E.WriteReg(r, v);
        }

        private void Sync()
        {
            var s = base.ProbeControl();
            bool on1 = (mask & 1) != 0, on2 = (mask & 2) != 0, onT = (mask & 4) != 0, onN = (mask & 8) != 0, onD = (mask & 16) != 0;

            int v1 = on1 && s.P1Length > 0 && s.P1Timer >= 8 ? (s.P1Const ? s.P1Vol : s.P1EnvDecay) & 15 : 0;
            int v2 = on2 && s.P2Length > 0 && s.P2Timer >= 8 ? (s.P2Const ? s.P2Vol : s.P2EnvDecay) & 15 : 0;
            int vt = onT && s.TLength > 0 && s.TLinear > 0 ? 12 : 0;
            int vn = onN && s.NLength > 0 ? (s.NConst ? s.NVol : s.NEnvDecay) & 15 : 0;
            bool dmc = onD && ((s.DmcEnabled && s.DmcRemaining > 0) || cycle - last4011 < 29780);

            int tpA = s.P1Timer + 1, tpB = s.P2Timer + 1, tpC = Math.Min(4095, 2 * (s.TTimer + 1));
            W(0, tpA & 0xFF); W(1, tpA >> 8);
            W(2, tpB & 0xFF); W(3, tpB >> 8);

            int np = NoisePeriods[s.NPeriodReg & 15] / 16;
            if (np < 1) np = 1; else if (np > 31) np = 31;
            W(6, np);

            // mixer reg 7: bits 0-2 tone disable A/B/C, bits 3-5 noise disable A/B/C (1 = off); ports as inputs
            int toneOff = 0, noiseOff = 0x38;
            int aA = v1, aB = v2, aC;
            if (dmc)
            {
                toneOff |= 4; aC = (s.DmcOutput & 0x7F) >> 3;
            }
            else
            {
                aC = vt;
                W(4, tpC & 0xFF); W(5, tpC >> 8);
            }
            if (vn > 0)
            {
                if (!dmc && vt == 0) { toneOff |= 4; noiseOff &= ~0x20; aC = vn; }
                else if (v2 == 0) { toneOff |= 2; noiseOff &= ~0x10; aB = vn; }
                else if (v1 == 0) { toneOff |= 1; noiseOff &= ~0x08; aA = vn; }
                else if (!dmc) { noiseOff &= ~0x20; aC = Math.Max(vt, vn); }
            }
            W(7, toneOff | noiseOff);
            W(8, aA); W(9, aB); W(10, aC);
        }

        public new void SetState(object state)
        {
            base.SetState(state);
            // the AY's own state is not in the save: a fresh chip is re-keyed from the restored NES registers (SoundRegisterReplay is not needed: the
            // translator reads APU_FIX, which base.SetState already restored)
            engine = null;
            Sync();
            ApplyEngineState();   // FA-ST-1: the engine's saved state replaces the re-key (exact)
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
            E.Run(cpuCycles);
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
        public new int GetSampleRate() => Mame0124Ay8910.SampleRate;

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
