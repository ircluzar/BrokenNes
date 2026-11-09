/*
 * APU_PNES - PocketNES's sound translator (Dwedit, GBA NES emulator, src/sound.s) on top of the stock APU_FIX.
 * Source: https://github.com/Dwedit/PocketNES commit 9c07f3d (2025-07-19), src/sound.s; see PocketNesSound.cs.
 * Copyright (C) Dwedit and the PocketNES authors. License: license not stated (PocketNES, Dwedit); documented exception.
 * This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
 * Quirks kept: see PocketNesSound.cs (NES channels re-played on a Game Boy sound chip - BrokenNes2's APU_GB - with retriggers on every volume change, re-quantised pitches, on/off triangle, DirectSound DMC).
 */
using System;

namespace NesEmulator
{
    /// <summary>
    /// NES APU id "PNES": APU_FIX keeps the game-facing side ($4015 reads, frame/DMC IRQs, DMC DMA timing); every register write also goes to the
    /// ported PocketNES translator, which is what you hear. The base's own samples are drained and discarded.
    /// </summary>
    public sealed class APU_PNES : APU_FIX, IAPU, IPortedEngineState
    {
        private readonly Bus bus;
        private PocketNesSound? engine;   // lazy: the base constructor may call Reset before this class's fields exist
        private int mask = 0x1F;
        private readonly float[] drain = new float[2048];

        public APU_PNES(Bus bus) : base(bus) { this.bus = bus; }

        private PocketNesSound E => engine ??= new PocketNesSound(a => bus.Read(a)) { ChannelMask = mask };

        public new string CoreName => "PocketNES";
        public new string Description => "PocketNES (GBA) sound.s: NES register writes translated to a Game Boy sound chip (BrokenNes2's APU_GB) - pitches re-quantised to GB frequencies, every volume change retriggers the channel, triangle on/off only, DMC through a crude DirectSound stream";
        public new int Performance => 5;
        public new int Rating => 1;
        public new string Category => "Unstable";

        /// <summary>After a state load the engine's own state is not in the save: the stock APU's restored registers are replayed into the engine
        /// (the channels still sounding are re-keyed), so a held note keeps playing (docs/KNOWN_GAPS.md FA-NES-ST).</summary>
        public new void SetState(object state)
        {
            base.SetState(state);
            foreach (var (a, v) in SoundRegisterReplay()) E.Write(a, v, false);
            ApplyEngineState();   // FA-ST-1: the engine's saved state replaces the re-key (exact)
        }

        public new void WriteAPURegister(ushort address, byte value)
        {
            bool dmcActive = address == 0x4015 && base.ProbeControl().DmcRemaining > 0;
            base.WriteAPURegister(address, value);
            E.Write(address, value, dmcActive);
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
        public new int GetSampleRate() => PocketNesSound.SampleRate;

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
            engine?.ClearOutput();
        }
    }
}
