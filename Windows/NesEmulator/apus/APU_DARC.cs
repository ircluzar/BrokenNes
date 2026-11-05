using System;

namespace NesEmulator
{
    /// <summary>
    /// Foreign APU <c>DARC</c>: DarcNES 1.50's NES sound (<see cref="DarcNesPsg"/>; Alastair Bridgewater, credit required, non-commercial -
    /// a documented exception, see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md).
    /// The stock APU_FIX keeps running underneath ($4015 reads, frame / DMC IRQs, DMC DMA timing); what is heard is DarcNES:
    /// 182-sample blocks every 7457 CPU cycles, no DMC, no noise (its renderer is #if 0'd), no triangle linear counter, unipolar 8-bit channels.
    /// The engine's blocks (44.1 kHz nominal, ~43.68 kHz in emulated time) are placed by CPU cycle and held (zero-order) at the base's 44.1 kHz.
    /// Quick port: the engine's own state is not saved (a state load keeps whatever the engine had).
    /// </summary>
    public sealed class APU_DARC : APU_FIX, IAPU
    {
        private const int CpuHz = 1789773;
        private DarcNesPsg? eng;
        private DarcNesPsg Eng => eng ??= new DarcNesPsg();

        private const int RingSize = 32768;
        private readonly float[] ring = new float[RingSize];
        private int rw, rr, rc;
        private int syncCycles = DarcNesPsg.CyclesPerSync;   // first Step renders a block at once
        private long outPhase;
        private int channelMask = 0x1F;
        private readonly float[] discard = new float[1024];

        public APU_DARC(Bus bus) : base(bus) { }

        public new string CoreName => "DarcNES";
        public new string Description => "DarcNES 1.50 (Alastair Bridgewater, 2000) NES sound: 240 Hz block rendering, no DMC, no noise, no triangle linear counter, 8-bit unipolar channels. Stock APU underneath for IRQs and $4015.";
        public new int Performance => 0;
        public new int Rating => 1;
        public new string Category => "Unstable";

        /// <summary>After a state load the engine's own state is not in the save: the stock APU's restored registers are replayed into the engine
        /// (the channels still sounding are re-keyed), so a held note keeps playing (docs/KNOWN_GAPS.md FA-NES-ST).</summary>
        public new void SetState(object state)
        {
            base.SetState(state);
            foreach (var (a, v) in SoundRegisterReplay()) Eng.Write(a, v);
        }

        public new void WriteAPURegister(ushort address, byte value)
        {
            base.WriteAPURegister(address, value);
            Eng.Write(address, value);
        }

        public new void Step(int cpuCycles)
        {
            base.Step(cpuCycles);
            var e = Eng;
            int rate = base.GetSampleRate();
            for (int c = 0; c < cpuCycles; c++)
            {
                if (syncCycles >= DarcNesPsg.CyclesPerSync) { syncCycles = 0; e.ChannelMask = channelMask; e.Frame(); }
                outPhase += rate;
                if (outPhase >= CpuHz)
                {
                    outPhase -= CpuHz;
                    int idx = syncCycles * DarcNesPsg.SamplesPerSync / DarcNesPsg.CyclesPerSync;
                    float s = e.Mix[idx] / 32768f;
                    ring[rw] = s; rw = (rw + 1) & (RingSize - 1);
                    if (rc < RingSize) rc++; else rr = (rr + 1) & (RingSize - 1);
                }
                syncCycles++;
            }
            while (base.GetQueuedSampleCount() > 0 && base.ReadSamples(discard) > 0) { }   // the stock APU's audio is not heard
        }

        public new float[] GetAudioSamples(int maxSamples = 0)
        {
            if (rc == 0) return Array.Empty<float>();
            int n = rc; if (maxSamples > 0 && maxSamples < n) n = maxSamples; if (n > 4096 && maxSamples == 0) n = 4096;
            var r = new float[n];
            ReadSamples(r);
            return r;
        }

        public new int ReadSamples(Span<float> dest)
        {
            int n = Math.Min(dest.Length, rc);
            for (int i = 0; i < n; i++) { dest[i] = ring[rr]; rr = (rr + 1) & (RingSize - 1); }
            rc -= n;
            return n;
        }

        public new int GetQueuedSampleCount() => rc;
        public new int GetSampleRate() => base.GetSampleRate();

        public new void SetEnabledChannels(int mask) { channelMask = mask & 0x1F; base.SetEnabledChannels(mask); }

        public new void ClearAudioBuffers()
        {
            base.ClearAudioBuffers();
            rw = rr = rc = 0; outPhase = 0;
        }

        public new void Reset()
        {
            base.Reset();
            rw = rr = rc = 0; outPhase = 0;
        }
    }
}
