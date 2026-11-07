/*
 * APU_NOFR - Nofrendo's NES APU (retro-go build) on top of the stock APU_FIX.
 * Source: https://github.com/ducalex/retro-go/tree/05e97e8/retro-core/components/nofrendo/nes (apu.c + apu.h), Matthew Conte 1998-2000 + Alex Duchesne; see NofrendoApu.cs (flaws listed there).
 * License: GNU Library GPL v2 (retro-go COPYING: GPLv2). This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2a and docs/projects/apu-harvest-catalog.md
 */
using System;

namespace NesEmulator
{
    /// <summary>
    /// NES APU id "NOFR": APU_FIX keeps the game-facing side ($4015 reads, frame/DMC IRQs, DMC DMA timing); every register write also goes
    /// to the ported nofrendo APU (rendered once per frame), which is what you hear. The base's own samples are drained and discarded.
    /// </summary>
    public sealed class APU_NOFR : APU_FIX, IAPU
    {
        private readonly Bus bus;
        private NofrendoApu? engine;   // lazy: the base constructor may call Reset before this class's fields exist
        private int mask = 0x1F;
        private readonly float[] drain = new float[2048];

        public APU_NOFR(Bus bus) : base(bus) { this.bus = bus; }

        private NofrendoApu E => engine ??= new NofrendoApu(a => bus.Read(a)) { ChannelMask = mask };

        public new string CoreName => "nofrendo (retro-go)";
        public new string Description => "Nofrendo (Matthew Conte) APU from retro-go 05e97e8: the whole frame rendered at its end, no frame sequencer (per-sample counters from per-frame tables), x -= x>>7 decay, staircase triangle, DMC steps of 2, (3*cur+prev)/4 filter, 32 kHz";
        public new int Performance => 5;
        public new int Rating => 1;
        public new string Category => "Unstable";

        /// <summary>After a state load the engine's own state is not in the save: the stock APU's restored registers are replayed (re-keyed).</summary>
        public new void SetState(object state)
        {
            base.SetState(state);
            E.Reset();
            foreach (var (a, v) in SoundRegisterReplay()) E.Write(a, v);
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
        public new int GetSampleRate() => NofrendoApu.SampleRate;

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
