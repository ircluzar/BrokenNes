using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NesEmulator.Corruption;

namespace NesEmulator.Debugging;

/// <summary>One traced instruction (or interrupt entry): the CPU state before it ran. Fixed size, no references: the ring never allocates.</summary>
public struct TraceRecord
{
    public long Cycle;
    public long Frame;
    public short Scanline, Dot;
    public uint Pc;
    /// <summary>Up to 4 instruction bytes, the first in the low byte.</summary>
    public uint Bytes;
    public byte Length;
    /// <summary>0 an instruction; 1 NMI, 2 IRQ, 3 another interrupt (its entry, before the handler's first instruction).</summary>
    public byte Kind;
    public ushort R0, R1, R2, R3, R4;
    public byte P, R5, R6;
    /// <summary>The address the instruction reads or writes (-1: none) and the value there before it ran.</summary>
    public int Ea;
    public ushort EaValue;
    public byte Byte(int i) => (byte)(Bytes >> (8 * i));
}

/// <summary>
/// One debuggable CPU of a machine, as the debugger sees it: registers, side-effect-free memory, the canonical keys of its addresses
/// (mirror folding), its disassembler and its stack. The console adapters (NES, SNES's 65816 and SPC700, Game Boy) implement it.
/// Space 0 is always "cpu": the CPU's own addresses with mirrors folded.
/// </summary>
public abstract class DebugCpu : IExprEnvironment
{
    protected DebugCpu(DebugMachine machine) { Machine = machine; }
    public DebugMachine Machine { get; }
    internal Debugger? Debugger => Machine.Debugger;
    internal int Index;
    internal readonly CpuDebugState State = new();

    /// <summary>"main" or "spc".</summary>
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract IDebugDisassembler Disassembler { get; }
    public abstract IReadOnlyList<AddressSpace> Spaces { get; }
    public abstract uint PC { get; }
    public abstract uint SP { get; }
    /// <summary>Hex digits of a CPU address in text (4, or 6 for the 65816).</summary>
    public virtual int AddressDigits => 4;
    public virtual DecodeContext Context => DecodeContext.Default;
    /// <summary>True when the next step runs the instruction at PC; false for an interrupt entry (<paramref name="interrupt"/> names it) or a halted CPU.</summary>
    public abstract bool WillExecute(out string? interrupt);
    public abstract IReadOnlyList<string> RegisterNames { get; }
    public abstract bool TryGetRegister(string name, out long value);
    public abstract bool TrySetRegister(string name, long value);
    /// <summary>A CPU address without side effects (no register reads, no mapper writes): what the debugger shows and conditions read.</summary>
    public abstract byte Peek(uint address);
    /// <summary>Writes memory the way a debugger does (RAM and the like directly; returns false where that is not defined).</summary>
    public abstract bool Poke(uint address, byte value);
    public virtual byte PeekSpace(int space, uint offset) => space == 0 ? Peek(offset) : (byte)0;
    public virtual bool PokeSpace(int space, uint offset, byte value) => space == 0 && Poke(offset, value);
    /// <summary>The canonical keys of an access at a CPU address: the folded CPU address and, where it maps to a memory, its place there (and
    /// for the SNES picture ports, the VRAM / CGRAM / OAM byte the write reaches). Returns how many.</summary>
    public abstract int AccessKeys(uint address, bool write, Span<SpaceKey> keys);
    /// <summary>How a CPU address typed in a breakpoint matches: on the NES and Game Boy the folded CPU address (any bank); on the SNES the memory it maps to.</summary>
    public abstract SpaceKey CpuSpecKey(uint address);
    /// <summary>A symbol's canonical location on this CPU, or false when the symbol is not this CPU's.</summary>
    public abstract bool BindSymbol(DebugSymbol sym, out SpaceKey key);
    /// <summary>A banked spec ("01:4000" on the Game Boy, "7E:0010" on the SNES): its canonical key.</summary>
    public virtual bool TryBankedSpec(uint bank, uint address, out SpaceKey key) { key = default; return false; }
    /// <summary>The stack pointer after pushing <paramref name="bytes"/> bytes.</summary>
    public abstract uint StackAfterPush(uint sp, int bytes);
    /// <summary>True when <paramref name="sp"/> is above <paramref name="reference"/>: the stack has popped past it (stacks grow down).</summary>
    public virtual bool StackAbove(uint sp, uint reference) => sp > reference;
    /// <summary>Fills a trace record's registers (and the instruction bytes, effective address and value).</summary>
    public abstract void FillTrace(ref TraceRecord r);
    /// <summary>The register columns of a trace line, as Mesen's trace logger writes them ("A:00 X:00 Y:00 S:FD P:nvUbdIzc").</summary>
    public abstract string FormatRegisters(in TraceRecord r);
    /// <summary>The 65816's M / X / E at the time of a trace record (others: the default).</summary>
    public virtual DecodeContext ContextOf(in TraceRecord r) => DecodeContext.Default;
    /// <summary>Heuristic interrupt detection for a CPU that cannot say an interrupt is coming (NES cores other than CPU_FIX): an entry seen afterwards.</summary>
    public virtual bool DetectInterruptEntry(uint prevPc, uint prevSp, uint pc, uint sp, out string kind) { kind = ""; return false; }

    public AddressSpace? FindSpace(string name) => Spaces.FirstOrDefault(s => s.Is(name));

    // ---------------------------------------------------------------- IExprEnvironment
    bool IExprEnvironment.TryRegister(string name, out Func<long> read)
    {
        if (RegisterNames.Any(r => r.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            string n = name;
            read = () => TryGetRegister(n, out long v) ? v : 0;
            return true;
        }
        read = null!;
        return false;
    }

    bool IExprEnvironment.TryMachineValue(string name, out Func<long> read)
    {
        var m = Machine;
        switch (name)
        {
            case "frame": read = () => m.Frame; return true;
            case "scanline": read = () => m.Scanline; return true;
            case "dot": read = () => m.Dot; return true;
            case "cycle": read = () => m.Cycle; return true;
        }
        read = null!;
        return false;
    }

    bool IExprEnvironment.TrySymbol(string name, out long address, out int space, out string? error)
    {
        address = 0; space = -1; error = null;
        var dbg = Debugger;
        if (dbg == null) return false;
        if (!dbg.Symbols.TryFind(name, out var sym, out error)) return false;
        if (!BindSymbol(sym, out var key)) { error = $"symbol '{name}' ({sym}) is not on the {Name}"; return false; }
        address = key.Offset;
        space = key.Space == 0 ? -1 : key.Space;
        return true;
    }

    bool IExprEnvironment.TrySpace(string name, out int space)
    {
        var s = FindSpace(name);
        space = s?.Id ?? -1;
        return s != null;
    }

    byte IExprEnvironment.ReadCpu(long address) => Peek((uint)address);
    byte IExprEnvironment.ReadSpace(int space, long offset) => PeekSpace(space, (uint)offset);

    /// <summary>The instruction at PC: its bytes (up to 4) read without side effects.</summary>
    public uint FetchBytes(uint pc)
    {
        uint b = 0;
        for (int i = 0; i < 4; i++) b |= (uint)Peek(NextAddress(pc, i)) << (8 * i);
        return b;
    }

    /// <summary>pc + i within the CPU's program space (16-bit wrap; the 65816 stays in its bank).</summary>
    public virtual uint NextAddress(uint pc, int i) => (uint)((pc + i) & 0xFFFF);

    public InstrClass Classify(uint bytes) => Disassembler.Classify((byte)bytes, (byte)(bytes >> 8), Context);

    public string FormatAddress(uint a) => "$" + a.ToString(AddressDigits == 6 ? "X6" : "X4");
}

/// <summary>A console as the debugger sees it: its CPUs, where the picture is, its memory domains, and the hooks it can arm.</summary>
public abstract class DebugMachine
{
    internal Debugger? Debugger;
    /// <summary>"nes", "snes", "widemess", "gb", "gbc".</summary>
    public abstract string Console { get; }
    public abstract IReadOnlyList<DebugCpu> Cpus { get; }
    /// <summary>The frame the emulation is in (for the NES the RunFrame count, for the SNES and Game Boy the vblanks counted so far).</summary>
    public abstract long Frame { get; }
    public abstract int Scanline { get; }
    public abstract int Dot { get; }
    /// <summary>The main CPU's clock: NES CPU cycles, SNES master clocks, Game Boy M-cycles.</summary>
    public abstract long Cycle { get; }
    /// <summary>Installs or removes the per-instruction hook and the memory tap of each CPU (by index). All false: the machine runs exactly as without a debugger.</summary>
    public abstract void ApplyHooks(bool[] exec, bool[] memory);
    /// <summary>The machine's memories by name (WRAM, VRAM, ...), for the HTTP API and tools.</summary>
    public abstract IReadOnlyList<IMemoryDomain> Domains();
    /// <summary>What this machine's debugging cannot see, said up front (e.g. a NES CPU core that reads RAM without the bus).</summary>
    public virtual IEnumerable<string> Limitations => Array.Empty<string>();
    public DebugCpu? Cpu(string id) => Cpus.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    public DebugCpu Main => Cpus[0];
}

/// <summary>Per-CPU debugger state (call stack, stepping, the pending break of a watch, the trace ring).</summary>
internal sealed class CpuDebugState
{
    public readonly List<CallFrame> Stack = new();
    public bool CallPending; public uint CallPc, CallTargetSpExpected, CallReturnPc, CallSpBefore; public string CallKind = "";
    public string? IrqPending; public uint IrqPc, IrqSp;
    public bool LastWasReturn;
    public bool HaveLast; public uint LastPc, LastSp;
    public StepMode Step; public int StepDepth;
    public BreakInfo? PendingBreak;
    public readonly ExprContext Ctx = new();
    public long Executed;
    public TraceRecord[]? Ring; public int RingPos; public long RingCount;
    public int TracedFunctionFrames;
    // the SPC700's predicted writes, checked after its instruction ran
    public (ushort addr, byte kind)[] SpcAccess = new (ushort, byte)[4]; public int SpcPendingWrites; public uint SpcAccessPc;
}

public enum StepMode { None, Into, Over, Out, OutAny }
