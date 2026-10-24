namespace NesEmulator.Snes;

// The debugger's hooks into the SFC family (the engine itself lives in Windows/NesEmulator/debug, which the plugin, Lite and the
// benches do not compile). Every hook is a field that stays null until a debugger arms it: the board then pays one null test where
// it already makes a decision (the slow bus path, a DMA byte, an instruction), and nothing at all on its fast page-table path.

/// <summary>Who made a bus access the debugger sees.</summary>
public enum SnesBusSource : byte { Cpu = 0, Dma = 1, Hdma = 2 }

/// <summary>The debugger's view of the SNES bus (<see cref="BOARD_SFC.DebugTap"/>). A-bus addresses are 24-bit; B-bus registers are
/// given as $00:21xx. Installed only while a read or write watch is set; the board then also routes every access through its decoding
/// path (<see cref="BOARD_SFC.DebugSlowPages"/>), which the reference-path goldens prove gives the same machine.</summary>
public interface ISnesBusDebugTap
{
    /// <summary>After a read, with the value read. <paramref name="channel"/> is the DMA channel (-1 for the CPU).</summary>
    void OnRead(uint address, byte value, SnesBusSource source, int channel);
    /// <summary>Before a write lands (so the picture chip's port addresses still say where it goes).</summary>
    void OnWrite(uint address, byte value, SnesBusSource source, int channel);
}

/// <summary>The debugger's per-instruction hook on the 65816 (<see cref="BOARD_SFC.DebugHook"/>). May block (a breakpoint parks the emulation thread).</summary>
public interface ISnesInstructionHook
{
    void BeforeInstruction();
}
