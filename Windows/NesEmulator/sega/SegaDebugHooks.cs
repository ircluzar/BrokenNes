namespace NesEmulator.Sega;

/// <summary>
/// The debugger's per-instruction hook on a Sega CPU (BrokenDev Timeflow T0: the Z80 of the Master System / Game Gear and of the Genesis's sound unit, the Genesis's
/// 68000). Called at every instruction boundary (an instruction, an interrupt response, a halted or stopped slice) before it starts. Null = no debugger: the board runs
/// the loop it always ran (the Master System and the Genesis sound unit pick their plain loop when the hook is null; the 68000's loop tests it once per instruction).
/// </summary>
public interface ISegaInstructionHook
{
    void BeforeInstruction();
}

/// <summary>
/// The debugger's memory tap on a Sega bus. Data reads are reported after they happen, writes before they land (the place a write reaches is decided by the mapping
/// before it; <see cref="OnWriteDone"/> follows when it has landed). Opcode fetches are not data reads (execute breakpoints see the instructions). Null = no debugger:
/// one field test per data access.
/// </summary>
public interface ISegaBusDebugTap
{
    /// <summary>A data read of the byte at <paramref name="address"/> (the Z80's 16-bit address; the 68000's 24-bit address, one call per byte of a word access).</summary>
    void OnRead(uint address, byte value);
    /// <summary>A data write, before it lands.</summary>
    void OnWrite(uint address, byte value);
    /// <summary>The write has landed.</summary>
    void OnWriteDone(uint address, byte value);
    /// <summary>A Z80 I/O port access (the whole 16-bit port address).</summary>
    void OnPort(ushort port, byte value, bool write);
    /// <summary>A byte the VDP's DMA read from the 68000's space (68000 to VRAM / CRAM / VSRAM), one call per byte.</summary>
    void OnDmaRead(uint address, byte value);
}
