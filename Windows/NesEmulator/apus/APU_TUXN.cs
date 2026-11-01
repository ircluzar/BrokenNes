/*
 * APU_TUXN - the TuxNES 0.75 sound engine (2001, sound.c by Paul Zaremba et al.) on top of the stock APU_FIX.
 * Source: https://github.com/tuxnes/tuxnes commit f537b4b ("tuxnes 0.75"), sound.c/sound.h; see TuxnesSound.cs.
 * Copyright (C) the TuxNES authors. License: GNU General Public License v2 or later.
 * This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
 * Quirks kept: see TuxnesSound.cs (unsigned 8-bit, a frame's writes squeezed into its first quarter, DMC pops, nosefart triangle delay hack).
 */
using System;

namespace NesEmulator
{
    /// <summary>
    /// NES APU id "TUXN": APU_FIX keeps the game-facing side ($4015 reads, frame/DMC IRQs, DMC DMA timing); every register write also goes to the
    /// ported TuxNES engine, which is what you hear. The base's own samples are drained and discarded.
    /// </summary>
    public sealed class APU_TUXN : APU_FIX, IAPU
    {
        private readonly Bus bus;
        private TuxnesSound? engine;   // lazy: the base constructor may call Reset before this class's fields exist
        private int mask = 0x1F;
        private readonly float[] drain = new float[2048];

        public APU_TUXN(Bus bus) : base(bus) { this.bus = bus; }

        private TuxnesSound E => engine ??= new TuxnesSound(a => bus.Read(a)) { ChannelMask = mask };

        public new string CoreName => "TuxNES";
        public new string Description => "TuxNES 0.75's sound.c (2001): unsigned 8-bit at 44.1 kHz rendered once a frame, a frame's register writes squeezed into its first quarter, DMC pops, nosefart-style triangle delay hack";
        public new int Performance => 5;
        public new int Rating => 1;
        public new string Category => "Unstable";

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
        public new int GetSampleRate() => TuxnesSound.SampleRate;

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
