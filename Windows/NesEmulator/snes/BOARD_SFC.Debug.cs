using System;

namespace NesEmulator.Snes;

/// <summary>The debugger's side of the board (Windows/NesEmulator/debug drives it). Nothing here runs unless a debugger set
/// <see cref="DebugHook"/> or <see cref="DebugTap"/>. The CPU's own accesses are watched without touching the board at all: the debugger
/// interposes a watching bus in front of it (<see cref="CPU_SFC.DebugInterpose"/>), so Read and Write stay exactly as they are.</summary>
public sealed partial class BOARD_SFC
{
    /// <summary>Called before every 65816 step (instruction, interrupt entry or a halted cycle); null = no debugger armed.</summary>
    public ISnesInstructionHook? DebugHook;

    /// <summary>Every DMA and HDMA byte (both buses) and every HDMA table fetch; null = no watch set.</summary>
    public ISnesBusDebugTap? DebugTap;

    /// <summary>The cartridge's decoding of a CPU address: the ROM or SRAM index, or -1 (the board's own areas, open bus).</summary>
    public int DebugCartIndex(uint bank, uint offset, out bool sram) => Cart.DebugDecode(bank, offset, out sram);

    /// <summary>Whether the WideMess's 896 KB work RAM answers in this bank ($70-$7D on a WideMess-map game).</summary>
    public bool DebugIsWramHighBank(uint bank) => wramHigh != null && IsWramHighBank(bank);

    /// <summary>The work RAM address the $2180 port reads or writes next.</summary>
    public uint DebugWramPortAddress => wramPortAddress & wramPortMask;
    public uint DebugWramPortMask => wramPortMask;

    /// <summary>The CPU I/O and DMA registers ($4200-$421F as the game last wrote or reads them without side effects, $4300-$437F).</summary>
    public byte DebugDmaRegister(int index) => dmaRegs[index & 0x7F];
    public byte DebugHdmaEnable => hdmaen;
    public bool DebugNmiFlag => nmiFlag;
    public bool DebugIrqFlag => irqFlag;
    public byte DebugOpenBus => mdr;

    /// <summary>One DMA or HDMA byte with the debugger watching: the same reads and writes in the same order as the untapped lines in RunDma / RunHdmaLine.</summary>
    private void DebugDmaByte(bool toA, uint aBank, uint aAddr, byte bReg, int channel, SnesBusSource source)
    {
        var tap = DebugTap!;
        uint a = aBank << 16 | aAddr, b = 0x2100u | bReg;
        if (toA)
        {
            byte v = ReadBBus(bReg);
            tap.OnRead(b, v, source, channel);
            tap.OnWrite(a, v, source, channel);
            WriteNoTick(aBank, aAddr, v);
        }
        else
        {
            byte v = ReadNoTick(aBank, aAddr);
            tap.OnRead(a, v, source, channel);
            tap.OnWrite(b, v, source, channel);
            DmaWriteBBus(bReg, v);
        }
    }
}

public sealed partial class SnesCartridge
{
    /// <summary>The cartridge's own address decoding (ROM or SRAM index, or -1), for the debugger's mirror folding.</summary>
    public int DebugDecode(uint bank, uint offset, out bool sram) => Decode(bank, offset, out sram);
}

public sealed partial class CPU_SFC
{
    /// <summary>For the debugger: the next <see cref="Step"/> takes an interrupt (NMI, or an unmasked IRQ) instead of running the instruction at PC.</summary>
    public bool DebugInterruptPending => !Stopped && (nmiPending || (irqLine && (P & FI) == 0));
    /// <summary>"NMI" or "IRQ", the interrupt <see cref="DebugInterruptPending"/> means.</summary>
    public string DebugPendingInterruptKind => nmiPending ? "NMI" : "IRQ";
    /// <summary>For the debugger: the next <see cref="Step"/> runs the instruction at PBR:PC (not stopped, no interrupt, not waiting).</summary>
    public bool DebugWillExecute => !Stopped && !DebugInterruptPending && (!Waiting || irqLine);

    private ISnesBus? debugRealBus;
    private BOARD_SFC? debugRealBoard;

    /// <summary>The bus the CPU really runs on (the board, or a bridge's wrapper), whatever is interposed.</summary>
    public ISnesBus DebugRealBus => debugRealBus ?? bus;

    /// <summary>True while a debugger's watching bus sits between the CPU and its board.</summary>
    public bool DebugInterposed => debugRealBus != null;

    /// <summary>The debugger's watches: every CPU access goes through <paramref name="watching"/> (which forwards to <see cref="DebugRealBus"/>),
    /// the CPU taking its interface path as it does on a bridge's bus; null puts the CPU back on its own bus. The board is not touched.</summary>
    public void DebugInterpose(ISnesBus? watching)
    {
        if (watching != null)
        {
            if (debugRealBus == null) { debugRealBus = bus; debugRealBoard = board; }
            bus = watching;
            board = null;
        }
        else if (debugRealBus != null)
        {
            bus = debugRealBus; board = debugRealBoard;
            debugRealBus = null; debugRealBoard = null;
        }
    }
}

public partial class PPU_SFC
{
    /// <summary>For the debugger: the VRAM word the $2118/$2119 port writes next (after the address remapping), and whether a write there now lands.</summary>
    public int DebugVramWordAddress => VramAddress();
    public bool DebugVramWritable => VramWritable;
    /// <summary>The CGRAM byte the $2122 port writes next (the low byte of the colour on the first write of a pair, the high byte on the second).</summary>
    public int DebugCgramByteAddress => cgadd * 2 + (cgHigh ? 1 : 0);
    /// <summary>The OAM byte the $2104 port writes next (0-543).</summary>
    public int DebugOamByteAddress => oamInternal < 0x200 ? oamInternal : 0x200 + (oamInternal & 0x1F);
}
