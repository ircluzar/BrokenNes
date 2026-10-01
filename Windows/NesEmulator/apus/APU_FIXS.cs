using System;

namespace NesEmulator
{
    /// <summary>
    /// A sound chip with extra channel banks that only the cross-console bridges drive (the way a SNES-support picture
    /// chip's extra layers are only fed by the bridge). Bank 0 is the chip's own registers; bank 1.. are more channels
    /// written through <see cref="WriteExtRegister"/>, never through the game's address space.
    /// </summary>
    public interface IApuExtChannels
    {
        /// <summary>Extra banks beyond bank 0 (APU_FIXS: 1, so 4 pulses, 2 triangles, 2 noises + the DMC in all).</summary>
        int ExtBanks { get; }
        /// <summary>Write a register of an extra bank (bank &gt;= 1), addressed as the chip's own ($4000-$4017 on the NES).</summary>
        void WriteExtRegister(int bank, ushort address, byte value);
    }

    /// <summary>
    /// APU_FIX with SNES support: a second set of NES channels - 2 more pulses, a second triangle and a second noise - so
    /// a SNES game's eight voices are not squeezed into three tones and a noise. Built the way NES expansion audio is
    /// (MMC5, VRC6): the game-facing chip is APU_FIX untouched (registers, frame IRQ, DMC, DMA timing), and a second
    /// APU_FIX on its own isolated host board sits beside it, stepped in lockstep and mixed additively. The extra bank is
    /// reachable only through <see cref="IApuExtChannels"/> (the bridges), never from the game, and joins the mix only
    /// once something writes it - so a NES game on APU_FIXS sounds bit-identical to APU_FIX. The extra bank has no DMC.
    /// Discovered by CoreRegistry as APU id "FIXS".
    /// </summary>
    public sealed class APU_FIXS : APU_FIX, IAPU, IApuExtChannels
    {
        private IAPU? ext;
        private bool extUsed;
        private int channelMask = 0x1F;

        public APU_FIXS(Bus bus) : base(bus) { }

        /// <summary>
        /// The extra bank, created on first use. Its host is an idle NES of its own: APU_FIX raises IRQs on its bus's CPU,
        /// and on the game's bus that would clear the game's frame / DMC IRQ line whenever the extra bank updated it.
        /// </summary>
        private IAPU Ext
        {
            get
            {
                if (ext != null) return ext;
                ext = new NesEmulator.Mix.NesApuHost("FIX").Apu;
                ext.WriteAPURegister(0x4017, 0x40);   // frame IRQ inhibited: nothing to raise, even on its own host
                ext.WriteAPURegister(0x4015, 0x0F);   // pulses, triangle, noise on; no DMC
                ext.SetEnabledChannels(channelMask & 0x0F);
                return ext;
            }
        }

        public new string CoreName => "FIXS";
        public new string Description => "APU_FIX plus a second bank of NES channels (4 pulses, 2 triangles, 2 noises) for the SNES bridges";
        public new string Category => "Experimental";

        public int ExtBanks => 1;

        public void WriteExtRegister(int bank, ushort address, byte value)
        {
            if (bank != 1) throw new ArgumentOutOfRangeException(nameof(bank), "APU_FIXS has one extra bank (1)");
            if (address is >= 0x4010 and <= 0x4013) return;   // no DMC on the extra bank
            if (address == 0x4015) value &= 0x0F;
            Ext.WriteAPURegister(address, value);
            extUsed = true;
        }

        public new void Step(int cpuCycles)
        {
            base.Step(cpuCycles);
            if (extUsed) ext!.Step(cpuCycles);
        }

        public new float[] GetAudioSamples(int maxSamples = 0)
        {
            var samples = base.GetAudioSamples(maxSamples);
            if (!extUsed || samples.Length == 0) return samples;
            // Same clock, same sample rate: the extra bank has produced the same number of samples.
            var more = ext!.GetAudioSamples(samples.Length);
            int n = Math.Min(samples.Length, more.Length);
            for (int i = 0; i < n; i++) samples[i] += more[i];
            return samples;
        }

        private float[] extScratch = Array.Empty<float>();

        /// <summary>IAPU.ReadSamples with the extra bank mixed in, as GetAudioSamples does. Declared here because
        /// this class re-implements IAPU: without it the interface would map to APU_FIX's version and skip the
        /// extra bank. Allocates only when a block is larger than any before it.</summary>
        public new int ReadSamples(Span<float> dest)
        {
            int n = base.ReadSamples(dest);
            if (!extUsed || n == 0) return n;
            if (extScratch.Length < n) extScratch = new float[n];
            int m = ext!.ReadSamples(extScratch.AsSpan(0, n));
            for (int i = 0; i < m; i++) dest[i] += extScratch[i];
            return n;
        }

        public new void SetEnabledChannels(int mask)
        {
            channelMask = mask;
            base.SetEnabledChannels(mask);
            ext?.SetEnabledChannels(mask & 0x0F);
        }

        public new void ClearAudioBuffers()
        {
            base.ClearAudioBuffers();
            ext?.ClearAudioBuffers();
        }

        public new void Reset()
        {
            base.Reset();
            ext?.Reset();
        }
    }
}
