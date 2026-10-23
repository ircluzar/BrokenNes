using System;
using System.IO;

namespace NesEmulator.Gb;

/// <summary>
/// APU_GB, enhanced: more sets of Game Boy channels - 2 pulses, a wave channel and a noise each, as many as the other console's voices need - so another
/// console's voices are not squeezed into four. As with <see cref="APU_FIXS"/>, the game-facing chip is APU_GB untouched and each extra bank is another APU_GB
/// beside it, ticked in lockstep and mixed additively. The extra banks are written only through <see cref="IApuExtChannels"/> (the bridges; registers by
/// their low byte, $10-$3F, wave RAM included) and a bank joins the mix only once something writes it - so a Game Boy game on APU_GBS sounds bit-identical to
/// APU_GB. Game Boy APU id "GBS" (GbCores).
/// </summary>
public sealed class APU_GBS : IGbApu, IApuExtChannels
{
    /// <summary>The most extra banks (16 banks in all).</summary>
    public const int MaxBanks = 15;

    private readonly APU_GB main;
    private readonly GbModel model;
    /// <summary>The extra banks by number (index 0 unused), each made when first written; <see cref="live"/> in the order they were made.</summary>
    private readonly APU_GB?[] ext = new APU_GB?[MaxBanks + 1];
    private readonly APU_GB[] live = new APU_GB[MaxBanks];
    private int liveCount, extBanks = 1;
    private short[] scratch = Array.Empty<short>();

    public APU_GBS(GbModel model) { this.model = model; main = new APU_GB(model); }

    /// <summary>The game-facing chip (for bridges that read channel state).</summary>
    public APU_GB Main => main;

    public string CoreName => "GBS";
    public int SampleRate => main.SampleRate;
    public int ExtBanks => extBanks;
    public int MaxExtBanks => MaxBanks;
    public int EnsureExtBanks(int wanted) => extBanks = Math.Clamp(Math.Max(extBanks, wanted), 1, MaxBanks);
    /// <summary>The extra banks that have been written (tests).</summary>
    public int ExtBanksInUse => liveCount;

    private APU_GB Ext(int bank)
    {
        if (ext[bank] is { } e) return e;
        e = new APU_GB(model);
        e.WriteRegister(0x26, 0x80);   // power on
        e.WriteRegister(0x24, 0x77);   // full master volume
        e.WriteRegister(0x25, 0xFF);   // every channel to both sides
        ext[bank] = e; live[liveCount++] = e;
        return e;
    }

    public void WriteExtRegister(int bank, ushort address, byte value)
    {
        if (bank < 1 || bank > extBanks) throw new ArgumentOutOfRangeException(nameof(bank), $"APU_GBS has extra banks 1 to {extBanks} (EnsureExtBanks adds more, up to {MaxBanks})");
        Ext(bank).WriteRegister(address & 0xFF, value);
    }

    public void ResetPostBoot() { main.ResetPostBoot(); }

    public void Tick(int tCycles)
    {
        main.Tick(tCycles);
        for (int i = 0; i < liveCount; i++) live[i].Tick(tCycles);
    }

    public void FrameSequencerStep()
    {
        main.FrameSequencerStep();
        for (int i = 0; i < liveCount; i++) live[i].FrameSequencerStep();
    }

    public byte ReadRegister(int reg) => main.ReadRegister(reg);
    public void WriteRegister(int reg, byte v) => main.WriteRegister(reg, v);

    public int ReadSamples(short[] buffer)
    {
        int n = main.ReadSamples(buffer);
        if (liveCount == 0 || n == 0) return n;
        // APU_GB.ReadSamples fills as much of the buffer as it has, so take exactly n. Same clock, same rate: each extra bank has the same number of samples
        // (fewer only right after it was made).
        if (scratch.Length != n) scratch = new short[n];
        for (int b = 0; b < liveCount; b++)
        {
            int m = live[b].ReadSamples(scratch);
            for (int i = 0; i < m; i++) buffer[i] = (short)Math.Clamp(buffer[i] + scratch[i], short.MinValue, short.MaxValue);
        }
        return n;
    }

    // Save states carry the game-facing chip only: the extra banks belong to the bridge driving them, which rebuilds them.
    public void SaveState(BinaryWriter w) => main.SaveState(w);
    public void LoadState(BinaryReader r) => main.LoadState(r);
}
