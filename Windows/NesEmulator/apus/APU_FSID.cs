/*
 * APU_FSID - the NES sound re-keyed onto a MOS6581 SID (VICE 3.7's FastSID, see ViceFastSid.cs) on top of the stock APU_FIX.
 * Source of the engine: https://sourceforge.net/p/vice-emu/code/HEAD/tree/tags/v3.7/vice/src/sid/fastsid.c
 * (Teemu Rantanen, Michael Schwendt, Ettore Perazzoli), GPL-2+.
 * This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2a and docs/projects/apu-harvest-catalog.md
 * The translator below is NEW code (not ported): no NES emulator ever drove a SID. It reads the stock APU's lag-immune state
 * (ProbeApuState) after every Step / register write and writes the SID registers that changed.
 * Mapping (choices documented here):
 *  - Pulse 1 -> voice 1 pulse, pulse 2 -> voice 2 pulse; pulse width from the NES duty (12.5% = $200, 25% = $400, 50% = $800,
 *    75% = $C00). Muted on length 0, period < 8 or sweep target > $7FF.
 *  - Triangle -> voice 3 triangle (sounds while length and linear counters run and period >= 2), sustain 10 (fixed).
 *  - Noise -> voice 3 noise when the triangle is silent, else the first free pulse voice (1, then 2); when all three voices are
 *    busy the noise is dropped. SID noise has no short mode: both NES modes map to the same SID noise.
 *  - Pitch: SID f = Fn * 1022727 / 16777216, so Fn = f * 16777216 / 1022727 (clamped to 16 bits). NES pulse f = 1789773/(16(p+1)),
 *    triangle f = 1789773/(32(p+1)). Noise: FastSID shifts its noise register 16 times per oscillator period, the NES noise
 *    produces one bit per timer period T, so the SID noise is keyed at f = 1789773 / (16 T).
 *  - ADSR: attack 0, decay 0, sustain = NES volume in force, release 0; gate on while the NES channel sounds (volume > 0).
 *    A SID sustain level can only be reached from above without a new gate (fastsid's "XXX: fix this" SUSTAIN->DECAY path),
 *    so when the volume RISES on a gated voice the gate is dropped and re-raised in the same sample (FastSID's gateflip
 *    restarts ATTACK); falls ride the decay.
 *  - DMC / $4011 -> the $D418 volume-nibble sample trick: while the DMC plays (bytes remaining, or the output level changed in
 *    the last ~1/60 s, which catches $4011 PCM) the master volume is the DMC level >> 3; otherwise 15. As on a C64 this also
 *    scales the three voices and FastSID's output DC offset is what carries the sample.
 *  - Filter off ($D417 = 0, no voice routed), master volume 15. Mono, 44100 Hz, int16 turned into float.
 */
using System;

namespace NesEmulator
{
    public sealed class APU_FSID : APU_FIX, IAPU
    {
        private const long NesClock = 1789773;
        private ViceFastSid? engine;   // lazy: the base constructor may call Reset before this class's fields exist
        private int mask = 0x1F;
        private readonly float[] drain = new float[2048];
        private readonly float[] ring = new float[16384];
        private int rHead, rCount;
        private long sampAcc;
        private short[] tmp = new short[1024];
        private readonly int[] shadow = new int[32];
        private int lastDmcLevel = -1;
        private long digiHold;
        private static readonly int[] DutyPw = { 0x200, 0x400, 0x800, 0xC00 };

        public APU_FSID(Bus bus) : base(bus) { ResetShadow(); }

        private ViceFastSid E => engine ??= new ViceFastSid();

        public new string CoreName => "VICE FastSID";
        public new string Description => "NES sound re-keyed onto a C64 SID (VICE 3.7 FastSID, 6581): pulses as SID pulse waves with the NES duty, triangle as SID triangle, noise from the 23-bit SID noise, envelopes as SID sustain levels, DMC as the $D418 volume-nibble trick; 44.1 kHz, per-sample oscillators, aliased";
        public new int Performance => 5;
        public new int Rating => 1;
        public new string Category => "Unstable";

        private void ResetShadow() { for (int i = 0; i < shadow.Length; i++) shadow[i] = -1; }

        private void Put(int reg, int val)
        {
            if (shadow[reg] == val) return;
            shadow[reg] = val;
            E.Store(reg, (byte)val);
        }

        public new void SetState(object state)
        {
            base.SetState(state);
            engine = new ViceFastSid();
            ResetShadow();
            lastDmcLevel = -1; digiHold = 0;
            Sync();
        }

        public new void WriteAPURegister(ushort address, byte value)
        {
            base.WriteAPURegister(address, value);
            Sync();
        }

        public new void Step(int cpuCycles)
        {
            base.Step(cpuCycles);
            if (base.GetQueuedSampleCount() > 4096) while (base.ReadSamples(drain) > 0) { }
            if (digiHold > 0) digiHold -= cpuCycles;
            Render(cpuCycles);
            Sync();
        }

        private void Render(int cpuCycles)
        {
            sampAcc += (long)cpuCycles * ViceFastSid.SampleRate;
            int n = (int)(sampAcc / NesClock);
            if (n <= 0) return;
            sampAcc -= n * NesClock;
            if (tmp.Length < n) tmp = new short[n];
            var span = tmp.AsSpan(0, n);
            E.CalculateSamples(span);
            for (int i = 0; i < n; i++)
            {
                float s = span[i] / 32768f;
                if (rCount == ring.Length) { rHead = (rHead + 1) % ring.Length; rCount--; }
                ring[(rHead + rCount) % ring.Length] = s; rCount++;
            }
        }

        private static int Fn(double hz) => (int)Math.Clamp(Math.Round(hz * 16777216.0 / ViceFastSid.ClockNtsc), 0, 0xFFFF);

        private static bool SweepMutes(int p, bool neg, int shift, bool ch1)
        {
            int ch = p >> shift;
            return (neg ? p - ch - (ch1 ? 1 : 0) : p + ch) > 0x7FF;
        }

        /// <summary>One voice's wanted state; vol 0 = gate off.</summary>
        private void Voice(int vi, int fn, int pw, int wave, int vol)
        {
            int b = vi * 7;
            Put(b + 0, fn & 0xFF); Put(b + 1, fn >> 8);
            Put(b + 2, pw & 0xFF); Put(b + 3, (pw >> 8) & 0x0F);
            Put(b + 5, 0x00);                         // attack 0, decay 0
            int oldSus = shadow[b + 6] < 0 ? 0 : shadow[b + 6] >> 4;
            bool wasGated = shadow[b + 4] >= 0 && (shadow[b + 4] & 1) != 0;
            Put(b + 6, (vol & 15) << 4);              // sustain = volume, release 0
            int ctrl = vol > 0 ? (wave | 1) : (shadow[b + 4] < 0 ? 0 : shadow[b + 4] & 0xF0);
            if (vol > 0 && wasGated && vol > oldSus && shadow[b + 4] == ctrl)
            {
                E.Store(b + 4, (byte)(ctrl & ~1));    // re-gate: the gate flip before the next sample restarts ATTACK
                E.Store(b + 4, (byte)ctrl);
                return;
            }
            Put(b + 4, ctrl);
        }

        private void Sync()
        {
            var s = ProbeApuState();

            int v1 = 0, v2 = 0, vt = 0, vn = 0;
            if ((mask & 1) != 0 && s.Pulse1Length > 0 && s.Pulse1Period >= 8 && !SweepMutes(s.Pulse1Period, s.Pulse1SweepNegate, s.Pulse1SweepShift, true)) v1 = s.Pulse1Volume & 15;
            if ((mask & 2) != 0 && s.Pulse2Length > 0 && s.Pulse2Period >= 8 && !SweepMutes(s.Pulse2Period, s.Pulse2SweepNegate, s.Pulse2SweepShift, false)) v2 = s.Pulse2Volume & 15;
            if ((mask & 4) != 0 && s.TriangleLength > 0 && s.TriangleLinear > 0 && s.TrianglePeriod >= 2) vt = 10;
            if ((mask & 8) != 0 && s.NoiseLength > 0) vn = s.NoiseVolume & 15;

            int fnNoise = Fn(NesClock / (16.0 * Math.Max(1, (int)s.NoisePeriod)));
            int noiseVoice = -1;
            if (vn > 0) noiseVoice = vt == 0 ? 2 : v1 == 0 ? 0 : v2 == 0 ? 1 : -1;

            // voice 1
            if (noiseVoice == 0) Voice(0, fnNoise, 0, 0x80, vn);
            else Voice(0, v1 > 0 ? Fn(NesClock / (16.0 * (s.Pulse1Period + 1))) : (shadow[0] < 0 ? 0 : shadow[0] | (shadow[1] << 8)), DutyPw[s.Pulse1Duty & 3], 0x40, v1);
            // voice 2
            if (noiseVoice == 1) Voice(1, fnNoise, 0, 0x80, vn);
            else Voice(1, v2 > 0 ? Fn(NesClock / (16.0 * (s.Pulse2Period + 1))) : (shadow[7] < 0 ? 0 : shadow[7] | (shadow[8] << 8)), DutyPw[s.Pulse2Duty & 3], 0x40, v2);
            // voice 3
            if (noiseVoice == 2) Voice(2, fnNoise, 0, 0x80, vn);
            else Voice(2, vt > 0 ? Fn(NesClock / (32.0 * (s.TrianglePeriod + 1))) : (shadow[14] < 0 ? 0 : shadow[14] | (shadow[15] << 8)), 0, 0x10, vt);

            // filter off
            Put(0x15, 0); Put(0x16, 0); Put(0x17, 0);

            // DMC: $D418 volume-nibble trick while it plays
            int level = s.DmcOutput & 0x7F;
            if (lastDmcLevel >= 0 && level != lastDmcLevel) digiHold = 29780;
            lastDmcLevel = level;
            bool digi = (mask & 16) != 0 && (s.DmcBytesRemaining > 0 || digiHold > 0);
            Put(0x18, digi ? (level >> 3) : 15);
        }

        public new float[] GetAudioSamples(int maxSamples = 0)
        {
            int n = rCount;
            if (maxSamples > 0 && maxSamples < n) n = maxSamples;
            if (n > 4096 && maxSamples == 0) n = 4096;
            var r = new float[n];
            ReadSamples(r);
            return r;
        }

        public new int ReadSamples(Span<float> dest)
        {
            int n = Math.Min(dest.Length, rCount);
            for (int i = 0; i < n; i++) { dest[i] = ring[rHead]; rHead = (rHead + 1) % ring.Length; }
            rCount -= n;
            return n;
        }

        public new float[] GetAudioBuffer() => GetAudioSamples();
        public new int GetQueuedSampleCount() => rCount;
        public new int GetSampleRate() => ViceFastSid.SampleRate;

        public new void SetEnabledChannels(int channelMask)
        {
            mask = channelMask & 0x1F;
            base.SetEnabledChannels(channelMask);
            if (engine != null) Sync();
        }

        public new void ClearAudioBuffers()
        {
            base.ClearAudioBuffers();
            rHead = rCount = 0;
        }

        public new void Reset()
        {
            base.Reset();
            rHead = rCount = 0;
        }
    }
}
