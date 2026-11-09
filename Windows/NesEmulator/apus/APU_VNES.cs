// APU_VNES - VirtuaNES 0.97's own sound (VirtuaNesApu.cs: APU_INTERNAL render side + APU.cpp mixer; VirtuaNesMmc5.cs: APU_MMC5), on top
// of the accurate APU_FIX. Source: https://github.com/ChillMagic/VirtuaNES (a modified mirror of VirtuaNES 0.97), (C) 2001-2003 Norix.
// License not stated (VirtuaNES, Norix); documented exception.
// This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
using System;

namespace NesEmulator
{
    /// <summary>
    /// VirtuaNES 0.97's APU: APU_FIX underneath keeps $4015 reads, the frame / DMC IRQs and the DMC DMA timing correct; every register write
    /// also goes to the ported VirtuaNES engine, which is clocked from Step and whose 22050 Hz 8-bit output replaces APU_FIX's. MMC5 sound writes reach the
    /// engine through <see cref="IApuExpansionAudio"/> (so the MMC5 squares and PCM are VirtuaNES's). Discovered by CoreRegistry as APU id "VNES".
    /// </summary>
    public sealed class APU_VNES : APU_FIX, IAPU, IPortedEngineState, IApuExpansionAudio
    {
        private readonly Bus host;
        private VirtuaNesApu? engine;
        private readonly float[] discard = new float[4096];

        public APU_VNES(Bus bus) : base(bus) { host = bus; }

        /// <summary>Made on first use: the base constructor may call Reset before this class's fields exist.</summary>
        private VirtuaNesApu Engine => engine ??= new VirtuaNesApu(a => host.Read(a));

        public new string CoreName => "VNES";
        public new string Description => "VirtuaNES 0.97 (Norix, 2002) sound at its 22050 Hz / 8-bit defaults: not band-limited (box-averaged squares, stepped triangle/noise), its own $4018 frame ticks, halved length ticks, the DMC-level volume effect on triangle/noise, a slew-limited DMC, a 40 Hz DC cut, and its sample-clocked MMC5 envelopes";
        public new string Category => "Unstable";
        public new int Rating => 3;
        public new int Performance => 0;

        /// <summary>After a state load the engine's own state is not in the save: the stock APU's restored registers are replayed into the engine
        /// (the channels still sounding are re-keyed), so a held note keeps playing (docs/KNOWN_GAPS.md FA-NES-ST).</summary>
        public new void SetState(object state)
        {
            base.SetState(state);
            foreach (var (a, v) in SoundRegisterReplay()) Engine.Write(a, v);
            ApplyEngineState();   // FA-ST-1: the engine's saved state replaces the re-key (exact)
        }

        public new void WriteAPURegister(ushort address, byte value)
        {
            base.WriteAPURegister(address, value);
            Engine.Write(address, value);
        }

        public void WriteExpansionAudio(ushort address, byte value) => Engine.WriteMmc5(address, value);

        public new void Step(int cpuCycles)
        {
            base.Step(cpuCycles);
            Engine.Clock(cpuCycles);
            // The base's own audio is discarded.
            if (base.GetQueuedSampleCount() > 2048) while (base.ReadSamples(discard) > 0) { }
        }

        public new float[] GetAudioSamples(int maxSamples = 0)
        {
            int n = Engine.Queued;
            if (maxSamples > 0 && maxSamples < n) n = maxSamples;
            if (n == 0) return Array.Empty<float>();
            var r = new float[n];
            Engine.Read(r);
            return r;
        }

        public new int ReadSamples(Span<float> dest)
        {
            int n = Engine.Read(dest);
            return n;
        }

        public new int GetQueuedSampleCount() => Engine.Queued;
        public new int GetSampleRate() => VirtuaNesApu.SndRate;

        /// <summary>VirtuaNES mixes with per-channel volumes, so the mask drops channels from the mix (they still run).</summary>
        public new void SetEnabledChannels(int mask) { Engine.ChannelMask = mask & 0x1F; base.SetEnabledChannels(mask); }

        public new void ClearAudioBuffers()
        {
            base.ClearAudioBuffers();
            Engine.ClearOutput();
        }

        /// <summary>Like APU_FIX's minimal Reset: drops queued audio, keeps the engine's registers (APU::Reset runs when the engine is made).</summary>
        public new void Reset()
        {
            base.Reset();
            Engine.ClearOutput();
        }
    }
}
