/*
 * APU_NSTRDC - NesterDC 7.1.0's NES APU (Dreamcast; nofrendo nes_apu.c + YANO takashi's APU_YANO sound code) on top of the stock APU_FIX.
 * Source: https://github.com/pqrs-org/NesterDC/archive/refs/tags/v7.1.0.zip (nester/nes/apu/nes_apu.c), see NesterApu.cs (flaws listed there).
 * License: GNU Library GPL v2 (nofrendo legend kept in NesterApu.cs). Art instrument: THIRD_PARTY_NOTICES.md section 2a, docs/projects/apu-harvest-catalog.md
 */
using System;

namespace NesEmulator
{
    /// <summary>
    /// NES APU id "NSTRDC": APU_FIX keeps the game-facing side ($4015 reads, frame/DMC IRQs, DMC DMA timing); every register write also goes
    /// to the ported nester APU (NesterDC 7.1.0 (Dreamcast)), which is what you hear. The base's own samples are drained and discarded.
    /// </summary>
    public sealed class APU_NSTRDC : APU_FIX, IAPU, IPortedEngineState
    {
        private readonly Bus bus;
        private NesterApu? engine;   // lazy: the base constructor may call Reset before this class's fields exist
        private int mask = 0x1F;
        private readonly float[] drain = new float[2048];

        public APU_NSTRDC(Bus bus) : base(bus) { this.bus = bus; }

        private NesterApu E => engine ??= new NesterApu(a => bus.Read(a), true) { ChannelMask = mask };

        public new string CoreName => "NesterDC 7.1.0 (Dreamcast)";
        public new string Description => "NesterDC 7.1.0 (Dreamcast nester, APU_YANO build): box-filtered channels in doubles, folded triangle, DC-offset cancel, writes queued and rendered 2048 samples at a time (~46 ms late), no frame sequencer, 44.1 kHz";
        public new int Performance => 5;
        public new int Rating => 1;
        public new string Category => "Unstable";

        /// <summary>After a state load the engine's own state is not in the save: the stock APU's restored registers are replayed (re-keyed).</summary>
        public new void SetState(object state)
        {
            base.SetState(state);
            E.Reset();
            foreach (var (a, v) in SoundRegisterReplay()) E.Write(a, v);
            ApplyEngineState();   // FA-ST-1: the engine's saved state replaces the re-key (exact)
        }

        public new void WriteAPURegister(ushort address, byte value)
        {
            base.WriteAPURegister(address, value);
            E.Write(address, value);
        }

        public new void Step(int cpuCycles)
        {
            base.Step(cpuCycles);
            if (base.GetQueuedSampleCount() > 4096) while (base.ReadSamples(drain) > 0) { }
            E.Clock(cpuCycles);
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
        public new int GetSampleRate() => NesterApu.SampleRate;

        public new void SetEnabledChannels(int channelMask)
        {
            mask = channelMask & 0x1F;
            base.SetEnabledChannels(channelMask);
            E.ChannelMask = mask;
        }

        public new void ClearAudioBuffers()
        {
            base.ClearAudioBuffers();
            engine?.ClearOutput();
        }

        public new void Reset()
        {
            base.Reset();
            engine?.Reset();
            engine?.ClearOutput();
        }
    }
}
