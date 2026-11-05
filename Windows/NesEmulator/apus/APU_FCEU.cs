// APU_FCEU - FCE Ultra 0.96's sound (FceuApu.cs: sound.c low-quality path + filter.c SexyFilter + mbshare/mmc5.c sound), on top of the
// accurate APU_FIX. Source: https://sourceforge.net/projects/fceultra/files/Source%20Code/0.96/fceu096src.tar.gz
// (C) 2002 Ben Parnell, Xodnizel et al. GPL-2.0-or-later.
// This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
using System;

namespace NesEmulator
{
    /// <summary>
    /// FCE Ultra 0.96's APU: APU_FIX underneath keeps $4015 reads, the frame / DMC IRQs and the DMC DMA timing correct; every register write
    /// also goes to the ported FCEU engine, which is clocked from Step and whose 44100 Hz output replaces APU_FIX's. MMC5 sound writes reach the
    /// engine through <see cref="IApuExpansionAudio"/> (so the MMC5 squares and PCM are FCEU 0.96's). Discovered by CoreRegistry as APU id "FCEU".
    /// </summary>
    public sealed class APU_FCEU : APU_FIX, IAPU, IApuExpansionAudio
    {
        private readonly Bus host;
        private FceuApu? engine;
        private int channelMask = 0x1F;
        private readonly float[] discard = new float[4096];

        public APU_FCEU(Bus bus) : base(bus) { host = bus; }

        /// <summary>Made on first use: the base constructor may call Reset before this class's fields exist.</summary>
        private FceuApu Engine => engine ??= new FceuApu(a => host.Read(a));

        public new string CoreName => "FCEU";
        public new string Description => "FCE Ultra 0.96 (2003) sound, low-quality default: FCEU's own frame sequencer and envelopes, 16x oversampled squares, a flat triangle above 14 kHz, an inverted two-pole filter, and its old MMC5 sound (no envelopes, a too-loud PCM)";
        public new string Category => "Degraded";
        public new int Rating => 2;
        public new int Performance => 0;

        /// <summary>After a state load the engine's own state is not in the save: the stock APU's restored registers are replayed into the engine
        /// (the channels still sounding are re-keyed), so a held note keeps playing (docs/KNOWN_GAPS.md FA-NES-ST).</summary>
        public new void SetState(object state)
        {
            base.SetState(state);
            foreach (var (a, v) in SoundRegisterReplay()) Engine.Write(a, v);
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
            if (channelMask == 0) Array.Clear(r);
            return r;
        }

        public new int ReadSamples(Span<float> dest)
        {
            int n = Engine.Read(dest);
            if (channelMask == 0) dest.Slice(0, n).Clear();
            return n;
        }

        public new int GetQueuedSampleCount() => Engine.Queued;
        public new int GetSampleRate() => FceuApu.SndRate;

        /// <summary>FCEU mixes its channels into one buffer as it goes, so only "all off" is honoured (the mask is still passed to the base).</summary>
        public new void SetEnabledChannels(int mask) { channelMask = mask & 0x1F; base.SetEnabledChannels(mask); }

        public new void ClearAudioBuffers()
        {
            base.ClearAudioBuffers();
            Engine.ClearOutput();
        }

        /// <summary>Like APU_FIX's minimal Reset: drops queued audio, keeps the engine's registers (FCEU's own power-on state is set when the engine is made).</summary>
        public new void Reset()
        {
            base.Reset();
            Engine.ClearOutput();
        }
    }
}
