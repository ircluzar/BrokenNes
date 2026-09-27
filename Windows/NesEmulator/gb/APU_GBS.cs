using System;
using System.IO;

namespace NesEmulator.Gb;

/// <summary>
/// APU_GB with SNES support: a second set of Game Boy channels - 2 more pulses, a second wave channel and a second noise
/// (8 in all) - so a SNES game's voices are not squeezed into four. As with <see cref="APU_FIXS"/>, the game-facing chip is
/// APU_GB untouched and a second APU_GB sits beside it, ticked in lockstep and mixed additively. The extra bank is written
/// only through <see cref="IApuExtChannels"/> (the bridges; registers by their low byte, $10-$3F, wave RAM included) and
/// joins the mix only once something writes it - so a Game Boy game on APU_GBS sounds bit-identical to APU_GB.
/// Game Boy APU id "GBS" (GbCores).
/// </summary>
public sealed class APU_GBS : IGbApu, IApuExtChannels
{
    private readonly APU_GB main;
    private readonly GbModel model;
    private APU_GB? ext;
    private short[] scratch = Array.Empty<short>();

    public APU_GBS(GbModel model) { this.model = model; main = new APU_GB(model); }

    /// <summary>The game-facing chip (for bridges that read channel state).</summary>
    public APU_GB Main => main;

    public string CoreName => "GBS";
    public int SampleRate => main.SampleRate;
    public int ExtBanks => 1;

    private APU_GB Ext
    {
        get
        {
            if (ext != null) return ext;
            ext = new APU_GB(model);
            ext.WriteRegister(0x26, 0x80);   // power on
            ext.WriteRegister(0x24, 0x77);   // full master volume
            ext.WriteRegister(0x25, 0xFF);   // every channel to both sides
            return ext;
        }
    }

    public void WriteExtRegister(int bank, ushort address, byte value)
    {
        if (bank != 1) throw new ArgumentOutOfRangeException(nameof(bank), "APU_GBS has one extra bank (1)");
        Ext.WriteRegister(address & 0xFF, value);
    }

    public void ResetPostBoot() { main.ResetPostBoot(); }

    public void Tick(int tCycles)
    {
        main.Tick(tCycles);
        ext?.Tick(tCycles);
    }

    public void FrameSequencerStep()
    {
        main.FrameSequencerStep();
        ext?.FrameSequencerStep();
    }

    public byte ReadRegister(int reg) => main.ReadRegister(reg);
    public void WriteRegister(int reg, byte v) => main.WriteRegister(reg, v);

    public int ReadSamples(short[] buffer)
    {
        int n = main.ReadSamples(buffer);
        if (ext == null || n == 0) return n;
        // APU_GB.ReadSamples fills as much of the buffer as it has, so take exactly n. Same clock, same rate: the
        // extra bank has the same number of samples (fewer only right after it was created).
        if (scratch.Length != n) scratch = new short[n];
        int m = ext.ReadSamples(scratch);
        for (int i = 0; i < m; i++) buffer[i] = (short)Math.Clamp(buffer[i] + scratch[i], short.MinValue, short.MaxValue);
        return n;
    }

    // Save states carry the game-facing chip only: the extra bank belongs to the bridge driving it, which rebuilds it.
    public void SaveState(BinaryWriter w) => main.SaveState(w);
    public void LoadState(BinaryReader r) => main.LoadState(r);
}
