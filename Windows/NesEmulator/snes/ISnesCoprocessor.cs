using System;

namespace NesEmulator.Snes;

/// <summary>
/// A chip on the cartridge that answers part of the bus (DSP-1, SA-1, later Super FX).
/// The board asks <see cref="Owns"/> before falling through to ROM/SRAM decoding, and passes the
/// master clock with every access so a chip can catch up lazily instead of being ticked.
/// </summary>
public interface ISnesCoprocessor
{
    string Name { get; }
    void Reset();

    /// <summary>
    /// Called once by the board: <paramref name="setIrq"/> drives the chip's line into the SNES
    /// CPU's IRQ input, <paramref name="remapPages"/> asks the board to rebuild its fast page table
    /// (e.g. after an SA-1 ROM bank switch).
    /// </summary>
    void Attach(Action<bool> setIrq, Action remapPages) { }

    /// <summary>True when this chip answers at bank:offset (read or write).</summary>
    bool Owns(uint bank, uint offset);

    /// <summary>
    /// For an owned 4KB page: true when it is plain read-only memory the board may serve straight
    /// from <paramref name="data"/> starting at <paramref name="index"/> (no chip sync needed).
    /// </summary>
    bool TryMapPage(uint bank, uint offset, out byte[]? data, out int index) { data = null; index = 0; return false; }

    byte Read(uint bank, uint offset, long masterClock);
    void Write(uint bank, uint offset, byte value, long masterClock);

    /// <summary>Let a chip that runs on its own (SA-1) catch up; the board calls this every scanline.</summary>
    void RunTo(long masterClock) { }

    /// <summary>One line of state for probe reports.</summary>
    string Describe();
}

/// <summary>What the cartridge header says is on the board besides ROM/SRAM.</summary>
public enum SnesChip { None, Dsp, SuperFx, Sa1, Other }
