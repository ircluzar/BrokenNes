// Foreign APU project unit (docs/projects/foreign-apu-project.md): the glue around apus/NesticleSound.cs (NESticle 1997 prototype, NO LICENSE: leaked source); it may follow the original's
// driving code (clocking, mixing) as described in that engine file's header. A documented exception to the license policy: THIRD_PARTY_NOTICES.md section 2a.
using System;

namespace NesEmulator
{
    /// <summary>
    /// NESticle 1997's prototype sound (Bloodlust Software / Icer Addis) - the legendary bad NES sound - on top of the accurate APU_FIX.
    /// APU_FIX underneath keeps $4015 reads, frame/DMC IRQs and DMC DMA timing correct for the game; every register write is also
    /// mirrored into <see cref="NesticleSound"/>, which is clocked from Step and is the only thing heard (APU_FIX's own ring is discarded).
    /// The engine runs at its native 22050 Hz and is zero-order-held to 44100 Hz. Noise and DMC are silent, as in NESticle.
    /// Discovered by CoreRegistry as APU id "NSTC". Engine state is not in save states (quick port).
    /// </summary>
    public sealed class APU_NSTC : APU_FIX, IAPU, IPortedEngineState
    {
        private const int CpuFreqInt = 1789773;
        private const int OutRate = 44100;
        private const int RingSize = 32768;

        private NesticleSound? eng;
        private NesticleSound Eng => eng ??= new NesticleSound();
        private float[]? ring;
        private float[] Ring => ring ??= new float[RingSize];
        private int ringWrite, ringRead, ringCount;
        private int outPhase, engPhase;
        private float held;

        public APU_NSTC(Bus bus) : base(bus) { }

        public new string CoreName => "NESticle";
        public new string Description => "NESticle's 1997 prototype sound (Bloodlust Software): squares ~3 semitones sharp, triangle an octave sharp, no noise, no DMC, near-flat envelopes. Quick port for art; the game still sees an accurate APU.";
        public new string Category => "Unstable";
        public new int Rating => 1;
        public new int Performance => -5;

        /// <summary>After a state load the engine's own state is not in the save: the stock APU's restored registers are replayed into the engine
        /// (the channels still sounding are re-keyed), so a held note keeps playing (docs/KNOWN_GAPS.md FA-NES-ST).</summary>
        public new void SetState(object state)
        {
            base.SetState(state);
            foreach (var (a, v) in SoundRegisterReplay()) { if (a == 0x4015) Eng.SetEnable(v); else Eng.Write(a - 0x4000, v); }
            ApplyEngineState();   // FA-ST-1: the engine's saved state replaces the re-key (exact)
        }

        public new void WriteAPURegister(ushort address, byte value)
        {
            base.WriteAPURegister(address, value);
            if (address <= 0x4013 && address >= 0x4000) Eng.Write(address - 0x4000, value);
            else if (address == 0x4015) Eng.SetEnable(value);
        }

        public new void Step(int cpuCycles)
        {
            base.Step(cpuCycles);
            if (cpuCycles <= 0) return;
            var e = Eng; var r = Ring;
            outPhase += cpuCycles * OutRate;
            while (outPhase >= CpuFreqInt)
            {
                outPhase -= CpuFreqInt;
                engPhase += NesticleSound.SoundRate;          // one 22050 Hz engine sample per two 44100 Hz output samples
                if (engPhase >= OutRate) { engPhase -= OutRate; held = e.MixOne() / 32768f; }
                if (ringCount >= RingSize) { ringRead = (ringRead + 1) & (RingSize - 1); ringCount--; }
                r[ringWrite] = held; ringWrite = (ringWrite + 1) & (RingSize - 1); ringCount++;
            }
        }

        public new float[] GetAudioSamples(int maxSamples = 0)
        {
            base.GetAudioSamples(0);   // drop APU_FIX's own audio
            if (ringCount == 0) return Array.Empty<float>();
            int n = ringCount; if (maxSamples > 0 && maxSamples < n) n = maxSamples; if (n > 4096 && maxSamples == 0) n = 4096;
            var res = new float[n];
            ReadInto(res);
            return res;
        }

        public new int ReadSamples(Span<float> dest)
        {
            DrainBase();
            int n = Math.Min(dest.Length, ringCount);
            if (n == 0) return 0;
            ReadInto(dest.Slice(0, n));
            return n;
        }

        private float[] baseScratch = Array.Empty<float>();
        private void DrainBase()
        {
            int q = base.GetQueuedSampleCount();
            if (q == 0) return;
            if (baseScratch.Length < q) baseScratch = new float[Math.Max(q, 4096)];
            base.ReadSamples(baseScratch.AsSpan(0, q));
        }

        private void ReadInto(Span<float> dest)
        {
            var r = Ring;
            for (int i = 0; i < dest.Length; i++) { dest[i] = r[ringRead]; ringRead = (ringRead + 1) & (RingSize - 1); }
            ringCount -= dest.Length;
        }

        public new int GetQueuedSampleCount() => ringCount;
        public new int GetSampleRate() => OutRate;

        public new void SetEnabledChannels(int mask)
        {
            base.SetEnabledChannels(mask);
            Eng.ChannelMask = mask & 0x1F;
        }

        public new void ClearAudioBuffers()
        {
            base.ClearAudioBuffers();
            ringWrite = ringRead = ringCount = 0;   // only the queued samples: the resampling phase is engine state (FA-ST-1)
        }

        public new void Reset()
        {
            base.Reset();
            ringWrite = ringRead = ringCount = 0; outPhase = 0;
        }
    }
}
