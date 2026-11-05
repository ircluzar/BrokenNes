/*
 * APU_INES - the InfoNES pAPU (Jay's Factory, ~2000, "based on DarcNES and NesterJ") on top of the stock APU_FIX.
 * Source: https://github.com/jay-kumogata/InfoNES (master, fetched 2026-11-01), src/InfoNES_pAPU.cpp/.h; see InfoNesApu.cs.
 * Copyright (C) 1999-2004 Jay's Factory. License: Apache License, Version 2.0.
 * This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
 * Quirks kept: see InfoNesApu.cs (unsigned 8-bit, 5 channels averaged, frozen envelopes/sweeps, per-frame rendering, slow tiny DMC).
 */
using System;

namespace NesEmulator
{
    /// <summary>
    /// NES APU id "INES": APU_FIX keeps the game-facing side ($4015 reads, frame/DMC IRQs, DMC DMA timing); every register write also goes to the
    /// ported InfoNES engine, which is what you hear. The base's own samples are drained and discarded.
    /// </summary>
    public sealed class APU_INES : APU_FIX, IAPU
    {
        private readonly Bus bus;
        private InfoNesApu? engine;   // lazy: the base constructor may call Reset before this class's fields exist
        private int mask = 0x1F;
        private readonly float[] drain = new float[2048];

        public APU_INES(Bus bus) : base(bus) { this.bus = bus; }

        private InfoNesApu E => engine ??= new InfoNesApu(a => bus.Read(a)) { ChannelMask = mask };

        public new string CoreName => "InfoNES";
        public new string Description => "InfoNES's pAPU (2000, Jay's Factory): unsigned 8-bit, the 5 channels averaged, rendered once a frame; envelopes and sweeps frozen, tiny slow DMC";
        public new int Performance => 5;
        public new int Rating => 1;
        public new string Category => "Unstable";

        /// <summary>After a state load the engine's own state is not in the save: the stock APU's restored registers are replayed into the engine
        /// (the channels still sounding are re-keyed), so a held note keeps playing (docs/KNOWN_GAPS.md FA-NES-ST).</summary>
        public new void SetState(object state)
        {
            base.SetState(state);
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
        public new int GetSampleRate() => InfoNesApu.SampleRate;

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
