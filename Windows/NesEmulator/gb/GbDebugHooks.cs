namespace NesEmulator.Gb;

// The debugger's hooks into the Game Boy (the engine itself lives in Windows/NesEmulator/debug, which the plugin and Lite do not
// compile). Each is a field that stays null until a debugger arms it; the board then pays one null test per access or instruction.

/// <summary>Who made a bus access the debugger sees.</summary>
public enum GbBusSource : byte { Cpu = 0, OamDma = 1, VramDma = 2 }

/// <summary>The debugger's view of the Game Boy bus (<see cref="BOARD_GB.DebugTap"/>). Addresses are CPU addresses; a DMA's destination
/// is given as the address it lands at ($FE00+ for OAM, $8000+ in the VRAM bank selected for VRAM).</summary>
public interface IGbBusDebugTap
{
    /// <summary>After a read, with the value read.</summary>
    void OnRead(ushort address, byte value, GbBusSource source);
    /// <summary>Before a write lands.</summary>
    void OnWrite(ushort address, byte value, GbBusSource source);
}

/// <summary>The debugger's per-instruction hook on the SM83 (<see cref="BOARD_GB.DebugHook"/>). May block (a breakpoint parks the emulation thread).</summary>
public interface IGbInstructionHook
{
    void BeforeInstruction();
}
