namespace NesEmulator.Snes;

/// <summary>
/// The 65C816's view of the SNES A-bus: a flat 24-bit address space. Unlike the NES <see cref="IBus"/>
/// every access here also COSTS time - the SNES CPU runs at 6, 8 or 12 master clocks per cycle
/// depending on which region it touches - so the board advances its clock inside Read/Write/Idle
/// rather than the CPU returning a cycle count. That keeps "how long did this take" in one place
/// (the board's memory map) instead of duplicating the region table in every CPU core.
/// </summary>
public interface ISnesBus
{
    byte Read(uint address);
    void Write(uint address, byte value);

    /// <summary>One internal CPU cycle with no bus access (always 6 master clocks).</summary>
    void Idle();
}
