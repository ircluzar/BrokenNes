/*
 * APU_NSDS - nesDS's ARM7 sound (Mamiya's NSF-player APU through the DS sound channels) on top of the stock APU_FIX.
 * Source: https://github.com/DS-Homebrew/NesDS tag v2.0.1 (652c8aa), arm7/source/s_apu.c + arm7main.c; see NesdsSound.cs (flaws listed there).
 * License: public domain (nesDS README; Mamiya's library "PDS"). Listed with the Foreign APU set in THIRD_PARTY_NOTICES.md section 2a.
 */
using System;

namespace NesEmulator
{
    /// <summary>
    /// NES APU id "NSDS": APU_FIX keeps the game-facing side ($4015 reads, frame/DMC IRQs, DMC DMA timing); every register write also goes
    /// to the ported nesDS ARM7 sound, which is what you hear. The base's own samples are drained and discarded.
    /// </summary>
    public sealed class APU_NSDS : APU_FIX, IAPU
    {
        private readonly Bus bus;
        private NesdsSound? engine;   // lazy: the base constructor may call Reset before this class's fields exist
        private int mask = 0x1F;
        private readonly float[] drain = new float[2048];

        public APU_NSDS(Bus bus) : base(bus) { this.bus = bus; }

        private NesdsSound E => engine ??= new NesdsSound(a => bus.Read(a)) { ChannelMask = mask };

        public new string CoreName => "nesDS";
        public new string Description => "nesDS v2.0.1 (DS) ARM7 sound by Mamiya: writes batched every 128 samples, a log volume curve per DS channel, a two-lobed 3-bit triangle that holds its level when stopped, +-1 DMC steps, 10-bit DS output at 24 kHz";
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
        public new int GetSampleRate() => NesdsSound.SampleRate;

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
