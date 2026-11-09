/*
 * APU_PCEG - retro-go PCE-GO's HuC6280 PSG (psg.c) driven by NES sound writes, on top of the stock APU_FIX.
 * Source: https://github.com/ducalex/retro-go/tree/05e97e8/retro-core/components/pce-go (psg.c, psg.h, pce.h); see PceGoPsg.cs
 * (ported part + flaws, and the "BrokenNes translator" mapping, which is new code). License: GNU GPL v2 (retro-go COPYING).
 * This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2a and docs/projects/apu-harvest-catalog.md
 */
using System;

namespace NesEmulator
{
    /// <summary>
    /// NES APU id "PCEG": APU_FIX keeps the game-facing side and the NES channel logic (length, envelope, sweep, linear counter);
    /// the translator re-keys that state onto the ported PCE-GO PSG, which is what you hear. The base's own samples are discarded.
    /// </summary>
    public sealed class APU_PCEG : APU_FIX, IAPU, IPortedEngineState
    {
        private NesToPceTranslator? engine;   // lazy: the base constructor may call Reset before this class's fields exist
        private int mask = 0x1F;
        private readonly float[] drain = new float[2048];

        public APU_PCEG(Bus bus) : base(bus) { }

        private NesToPceTranslator E => engine ??= new NesToPceTranslator(() => base.ProbeControl()) { ChannelMask = mask };

        public new string CoreName => "PCE-GO PSG";
        public new string Description => "retro-go PCE-GO's PC Engine PSG (psg.c): NES channels re-keyed onto HuC6280 wave channels - 32-step duty/triangle waves, 22050 Hz, flat integer pitch, 1-bit noise, DMC as untimed DDA";
        public new int Performance => 5;
        public new int Rating => 1;
        public new string Category => "Unstable";

        public new void SetState(object state)
        {
            base.SetState(state);
            E.HardReset();
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
        public new int GetSampleRate() => NesToPceTranslator.SampleRate;

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
            engine?.HardReset();
        }
    }
}
