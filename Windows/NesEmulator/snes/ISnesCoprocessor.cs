namespace NesEmulator.Snes;

/// <summary>
/// A chip on the cartridge that answers part of the bus (DSP-1, and later SA-1 / Super FX).
/// The board asks <see cref="Owns"/> before falling through to ROM/SRAM decoding, and passes the
/// master clock with every access so a chip can catch up lazily instead of being ticked.
/// </summary>
public interface ISnesCoprocessor
{
    string Name { get; }
    void Reset();

    /// <summary>True when this chip answers at bank:offset (read or write).</summary>
    bool Owns(uint bank, uint offset);

    byte Read(uint bank, uint offset, long masterClock);
    void Write(uint bank, uint offset, byte value, long masterClock);

    /// <summary>One line of state for probe reports.</summary>
    string Describe();
}

/// <summary>What the cartridge header says is on the board besides ROM/SRAM.</summary>
public enum SnesChip { None, Dsp, SuperFx, Sa1, Other }
