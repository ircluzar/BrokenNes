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

    /// <summary>Called once by the board: the SNES CPU's current bus address, for bus-conflict timing (SA-1).</summary>
    void AttachBusProbe(Func<uint> snesBusAddress) { }

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

    // ---- save states (the board writes the chip inside its own state; see BOARD_SFC.State.cs) ----

    /// <summary>
    /// What a state saved with this chip depends on: its name, and for a chip that runs a firmware image the firmware's identity too. A state only loads into a session whose chip has the same id (a state saved with
    /// the homemade DSP-1 does not load into one running Nintendo's firmware, and the other way round).
    /// </summary>
    string StateId => Name;

    /// <summary>Writes everything a running game can change in the chip: registers, internal memories, clocks, interrupt lines. Memory shared with the cartridge (the battery RAM) is the cartridge's to write.</summary>
    void SaveState(System.IO.BinaryWriter w);

    /// <summary>Puts a state written by <see cref="SaveState"/> back, without touching the lines to the board (the board's own state carries those) and without asking it to remap pages (the board rebuilds its table after the load).</summary>
    void LoadState(System.IO.BinaryReader r);
}

/// <summary>What the cartridge header says is on the board besides ROM/SRAM.</summary>
public enum SnesChip { None, Dsp, SuperFx, Sa1, Other }
