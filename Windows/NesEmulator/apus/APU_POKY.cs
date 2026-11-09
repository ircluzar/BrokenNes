/*
 * APU_POKY - the NES sound re-keyed onto an Atari POKEY (atari800 0.9.8a's PokeySound V2.4, see AtariPokeySnd.cs) on top of the stock APU_FIX.
 * Source of the engine: https://sources.debian.org/src/atari800/0.9.8a-2/pokeysnd.c (Ron Fries 1996-98 + atari800 team), LGPL-2 (Library GPL v2).
 * This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2a and docs/projects/apu-harvest-catalog.md
 * The translator below is NEW code (not ported): no NES emulator ever drove a POKEY. It reads the stock APU's lag-immune state
 * (ProbeApuState: periods, volume in force, length/linear counters, DMC level) after every Step / register write and writes the
 * POKEY registers that changed, so envelopes, length counters and sweeps arrive as POKEY volume/frequency rewrites.
 * Mapping (choices documented here):
 *  - Pulse 1 -> POKEY ch1, pulse 2 -> ch2, pure tone (AUDC 0xA0 | volume, NES volume 0-15 = AUDC volume 0-15). Duty cannot be expressed:
 *    POKEY pure tone is always a 50% square. Muted on length 0, period < 8 or sweep target > $7FF.
 *  - Triangle -> ch3, pure tone at fixed volume 10 (POKEY has no triangle: a square an octave-true, on while length and linear counters run).
 *  - Pitch, following pokeysnd's divide maths: a pure channel toggles every Div_n_max ticks, so f = 1789790 / (2 * N). NES pulse needs
 *    N = 8 (p+1), triangle N = 16 (p+1). Default clock is 64 kHz (AUDCTL bit 0 clear): N = 28 (AUDF+1). Ch1 and ch3 switch to 1.79 MHz
 *    (AUDCTL CH1_179 / CH3_179: N = AUDF + 4) when the period fits 8 bits (pulse p <= 31, triangle p <= 15). 16-bit linked mode is NOT used
 *    (it would cost a channel); notes too low for 8 bits at 64 kHz (below ~125 Hz pulse, ~62 Hz triangle) are folded up by octaves.
 *  - Noise -> ch4: long mode AUDC 0x80 | vol (17-bit poly, no poly5), short mode 0xC0 | vol (4-bit poly: the closest "metallic" loop).
 *    AUDF = round(T / 28) - 1 for the NES timer period T, clamped 1..255 (AUDF 0 would be under the playback rate and parked silent).
 *  - DMC / $4011 -> ch4 in volume-only mode (AUDC 0x10 | level >> 3) whenever the noise is idle (volume in force 0, length 0 or masked);
 *    the noise wins when both sound. Polled per Step, so the DMC is a 4-bit DAC at the CPU-instruction rate.
 *  - One POKEY, mono, 44100 Hz, unsigned 8-bit (centred at 128) turned into float.
 */
using System;

namespace NesEmulator
{
    public sealed class APU_POKY : APU_FIX, IAPU, IPortedEngineState
    {
        private const long NesClock = 1789773;
        private AtariPokeySnd? engine;   // lazy: the base constructor may call Reset before this class's fields exist
        private int mask = 0x1F;
        private readonly float[] drain = new float[2048];
        private readonly float[] ring = new float[16384];
        private int rHead, rCount;
        private long sampAcc;
        private byte[] tmp = new byte[1024];
        private readonly int[] shadow = new int[9];   // last written AUDF1,AUDC1..AUDF4,AUDC4,AUDCTL (-1 = never)

        public APU_POKY(Bus bus) : base(bus) { ResetShadow(); }

        private AtariPokeySnd E => engine ??= new AtariPokeySnd();

        public new string CoreName => "atari800 POKEY";
        public new string Description => "NES sound re-keyed onto an Atari POKEY (Ron Fries' PokeySound 2.4 from atari800 0.9.8a): every note a 50% square from a divide-by-N counter, octave-folded at 64 kHz, triangle as a square, noise from the 17/4-bit polys, DMC as volume-only writes; 8-bit, 44.1 kHz, aliased";
        public new int Performance => 5;
        public new int Rating => 1;
        public new string Category => "Unstable";

        private void ResetShadow() { for (int i = 0; i < shadow.Length; i++) shadow[i] = -1; }

        private void Put(int reg, int v)
        {
            if (shadow[reg] == v) return;
            shadow[reg] = v;
            E.Update(reg, (byte)v);
        }

        /// <summary>After a state load the engine's own state is not in the save: the engine is reset and re-keyed from the restored stock APU.</summary>
        public new void SetState(object state)
        {
            base.SetState(state);
            E.Init(AtariPokeySnd.Freq17Exact, AtariPokeySnd.PlaybackFreq);
            ResetShadow();
            Sync();
            ApplyEngineState();   // FA-ST-1: the engine's saved state replaces the re-key (exact)
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
            Render(cpuCycles);
            Sync();
        }

        private void Render(int cpuCycles)
        {
            sampAcc += (long)cpuCycles * AtariPokeySnd.PlaybackFreq;
            int n = (int)(sampAcc / NesClock);
            if (n <= 0) return;
            sampAcc -= n * NesClock;
            if (tmp.Length < n) tmp = new byte[n];
            var span = tmp.AsSpan(0, n);
            E.Process(span);
            for (int i = 0; i < n; i++)
            {
                float s = (span[i] - 128) / 128f;
                if (rCount == ring.Length) { rHead = (rHead + 1) % ring.Length; rCount--; }
                ring[(rHead + rCount) % ring.Length] = s; rCount++;
            }
        }

        private static int Fold64(double n)
        {
            int a = (int)Math.Round(n / 28.0) - 1;
            while (a > 255) { n /= 2; a = (int)Math.Round(n / 28.0) - 1; }
            return Math.Max(1, a);
        }

        private static bool SweepMutes(int p, bool neg, int shift, bool ch1)
        {
            int ch = p >> shift;
            return (neg ? p - ch - (ch1 ? 1 : 0) : p + ch) > 0x7FF;
        }

        /// <summary>Re-keys the POKEY from the stock APU's state (only changed registers are written).</summary>
        private void Sync()
        {
            var s = ProbeApuState();
            int audctl = 0;

            // pulse 1 -> ch1 (may use 1.79 MHz)
            int f1 = shadow[0] < 0 ? 0 : shadow[0], v1 = 0;
            if ((mask & 1) != 0 && s.Pulse1Length > 0 && s.Pulse1Period >= 8 && !SweepMutes(s.Pulse1Period, s.Pulse1SweepNegate, s.Pulse1SweepShift, true)) v1 = s.Pulse1Volume & 15;
            if (v1 > 0)
            {
                int p = s.Pulse1Period;
                if (p <= 31) { audctl |= 0x40; f1 = 8 * (p + 1) - 4; }
                else f1 = Fold64(8.0 * (p + 1));
            }
            else if ((shadow[8] & 0x40) != 0 && shadow[8] >= 0) audctl |= 0x40; // keep the clock while silent

            int f2 = shadow[2] < 0 ? 0 : shadow[2], v2 = 0;
            if ((mask & 2) != 0 && s.Pulse2Length > 0 && s.Pulse2Period >= 8 && !SweepMutes(s.Pulse2Period, s.Pulse2SweepNegate, s.Pulse2SweepShift, false)) v2 = s.Pulse2Volume & 15;
            if (v2 > 0) f2 = Fold64(8.0 * (s.Pulse2Period + 1));

            int f3 = shadow[4] < 0 ? 0 : shadow[4], v3 = 0;
            if ((mask & 4) != 0 && s.TriangleLength > 0 && s.TriangleLinear > 0 && s.TrianglePeriod >= 2) v3 = 10;
            if (v3 > 0)
            {
                int p = s.TrianglePeriod;
                if (p <= 15) { audctl |= 0x20; f3 = 16 * (p + 1) - 4; }
                else f3 = Fold64(16.0 * (p + 1));
            }
            else if (shadow[8] >= 0 && (shadow[8] & 0x20) != 0) audctl |= 0x20;

            int f4 = shadow[6] < 0 ? 0 : shadow[6], c4 = 0;
            int nv = ((mask & 8) != 0 && s.NoiseLength > 0) ? s.NoiseVolume & 15 : 0;
            if (nv > 0)
            {
                f4 = Math.Clamp((int)Math.Round(s.NoisePeriod / 28.0) - 1, 1, 255);
                c4 = (s.NoiseMode ? 0xC0 : 0x80) | nv;
            }
            else if ((mask & 16) != 0) c4 = 0x10 | ((s.DmcOutput & 0x7F) >> 3);

            Put(8, audctl);
            Put(0, f1); Put(1, 0xA0 | v1);
            Put(2, f2); Put(3, 0xA0 | v2);
            Put(4, f3); Put(5, 0xA0 | v3);
            Put(6, f4); Put(7, c4);
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
        public new int GetSampleRate() => AtariPokeySnd.PlaybackFreq;

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
