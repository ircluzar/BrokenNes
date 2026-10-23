using System;

namespace NesEmulator
{
    /// <summary>
    /// The enhanced sound contract: a sound chip with extra channel banks that only the cross-console bridges drive (the way an enhanced picture chip's extra
    /// layers are only fed by a translator). Bank 0 is the chip's own registers; banks 1.. are more channels of the chip's own kind, written through
    /// <see cref="WriteExtRegister"/>, never through the game's address space. A bridge asks for as many banks as its guest's voices need
    /// (<see cref="EnsureExtBanks"/>: the Genesis's eleven voices take five banks, the Master System's FM eighteen take seven, a SNES game grows to what it
    /// plays at once), so no voice of the guest is dropped; a stock chip has bank 0 only and the bridge drops (and counts) what does not fit.
    /// Implemented by APU_FIXS (NES channels), APU_GBS (Game Boy channels) and APU_DMGS (NES channels played on APU_GBS's banks).
    /// </summary>
    public interface IApuExtChannels
    {
        /// <summary>Extra banks beyond bank 0 on offer now: 1 when the chip is made (APU_FIXS: 4 pulses, 2 triangles, 2 noises + the DMC in all), more once a bridge asks.</summary>
        int ExtBanks { get; }
        /// <summary>The most extra banks the chip can add.</summary>
        int MaxExtBanks { get; }
        /// <summary>Grows to at least <paramref name="wanted"/> extra banks (at most <see cref="MaxExtBanks"/>; never fewer than now) and returns <see cref="ExtBanks"/>. A bank costs nothing until it is written.</summary>
        int EnsureExtBanks(int wanted);
        /// <summary>Write a register of an extra bank (1 to <see cref="ExtBanks"/>), addressed as the chip's own ($4000-$4017 on the NES).</summary>
        void WriteExtRegister(int bank, ushort address, byte value);
    }

    /// <summary>
    /// APU_FIX, enhanced: more sets of NES channels - 2 pulses, a triangle and a noise each, as many as the guest's voices need - so another console's voices are
    /// not squeezed into three tones and a noise. Built the way NES expansion audio is (MMC5, VRC6): the game-facing chip is APU_FIX untouched (registers, frame IRQ,
    /// DMC, DMA timing), and each extra bank is another APU_FIX on its own isolated host board beside it, stepped in lockstep and mixed additively. The extra banks are
    /// reachable only through <see cref="IApuExtChannels"/> (the bridges), never from the game, and a bank joins the mix only once something writes it - so a NES game
    /// on APU_FIXS sounds bit-identical to APU_FIX. The extra banks have no DMC. Discovered by CoreRegistry as APU id "FIXS".
    /// </summary>
    public sealed class APU_FIXS : APU_FIX, IAPU, IApuExtChannels
    {
        /// <summary>The most extra banks (16 banks in all: 32 pulses, 16 triangles, 16 noises).</summary>
        public const int MaxBanks = 15;

        /// <summary>The extra banks by number (index 0 unused), each made when first written.</summary>
        private readonly IAPU?[] ext = new IAPU?[MaxBanks + 1];
        /// <summary>The banks made so far, in the order they were made (the ones stepped and mixed).</summary>
        private readonly IAPU[] live = new IAPU[MaxBanks];
        private int liveCount, extBanks = 1;
        private int channelMask = 0x1F;

        public APU_FIXS(Bus bus) : base(bus) { }

        /// <summary>
        /// Extra bank <paramref name="bank"/>, created on first use. Its host is an idle NES of its own: APU_FIX raises IRQs on its bus's CPU, and on the game's bus
        /// that would clear the game's frame / DMC IRQ line whenever the extra bank updated it.
        /// </summary>
        private IAPU Ext(int bank)
        {
            if (ext[bank] is { } e) return e;
            e = new NesEmulator.Mix.NesApuHost("FIX").Apu;
            e.WriteAPURegister(0x4017, 0x40);   // frame IRQ inhibited: nothing to raise, even on its own host
            e.WriteAPURegister(0x4015, 0x0F);   // pulses, triangle, noise on; no DMC
            e.SetEnabledChannels(channelMask & 0x0F);
            ext[bank] = e; live[liveCount++] = e;
            return e;
        }

        public new string CoreName => "FIXS";
        public new string Description => "APU_FIX plus extra banks of NES channels (2 pulses, a triangle and a noise each, as many as the other console's voices need) for the cross-console bridges";
        public new string Category => "Experimental";

        public int ExtBanks => extBanks;
        public int MaxExtBanks => MaxBanks;
        public int EnsureExtBanks(int wanted) => extBanks = Math.Clamp(Math.Max(extBanks, wanted), 1, MaxBanks);

        /// <summary>The extra banks that have been written (tests: how many the bridge really uses).</summary>
        public int ExtBanksInUse => liveCount;

        public void WriteExtRegister(int bank, ushort address, byte value)
        {
            if (bank < 1 || bank > extBanks) throw new ArgumentOutOfRangeException(nameof(bank), $"APU_FIXS has extra banks 1 to {extBanks} (EnsureExtBanks adds more, up to {MaxBanks})");
            if (address is >= 0x4010 and <= 0x4013) return;   // no DMC on the extra banks
            if (address == 0x4015) value &= 0x0F;
            Ext(bank).WriteAPURegister(address, value);
        }

        public new void Step(int cpuCycles)
        {
            base.Step(cpuCycles);
            for (int i = 0; i < liveCount; i++) live[i].Step(cpuCycles);
        }

        public new float[] GetAudioSamples(int maxSamples = 0)
        {
            var samples = base.GetAudioSamples(maxSamples);
            if (liveCount == 0 || samples.Length == 0) return samples;
            // Same clock, same sample rate: each extra bank has produced the same number of samples (fewer only right after it was made).
            for (int b = 0; b < liveCount; b++)
            {
                var more = live[b].GetAudioSamples(samples.Length);
                int n = Math.Min(samples.Length, more.Length);
                for (int i = 0; i < n; i++) samples[i] += more[i];
            }
            return samples;
        }

        private float[] extScratch = Array.Empty<float>();

        /// <summary>IAPU.ReadSamples with the extra banks mixed in, as GetAudioSamples does. Declared here because this class re-implements IAPU: without it the
        /// interface would map to APU_FIX's version and skip the extra banks. Allocates only when a block is larger than any before it.</summary>
        public new int ReadSamples(Span<float> dest)
        {
            int n = base.ReadSamples(dest);
            if (liveCount == 0 || n == 0) return n;
            if (extScratch.Length < n) extScratch = new float[n];
            for (int b = 0; b < liveCount; b++)
            {
                int m = live[b].ReadSamples(extScratch.AsSpan(0, n));
                for (int i = 0; i < m; i++) dest[i] += extScratch[i];
            }
            return n;
        }

        public new void SetEnabledChannels(int mask)
        {
            channelMask = mask;
            base.SetEnabledChannels(mask);
            for (int i = 0; i < liveCount; i++) live[i].SetEnabledChannels(mask & 0x0F);
        }

        public new void ClearAudioBuffers()
        {
            base.ClearAudioBuffers();
            for (int i = 0; i < liveCount; i++) live[i].ClearAudioBuffers();
        }

        public new void Reset()
        {
            base.Reset();
            for (int i = 0; i < liveCount; i++) live[i].Reset();
        }
    }
}
