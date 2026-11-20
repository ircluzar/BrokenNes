// [dbg_disasm] Console-neutral debugger interface (BrokenNes-style: named memory domains, register files,
// disassembly, symbols). Only called between frames by a host / tool; never on the emulation hot path.
using System;
using System.Collections.Generic;

namespace cor64.Debug
{
    public sealed class MemoryDomain
    {
        public string Name { get; }
        /// <summary>Size in bytes; addresses passed to Read/Write are offsets 0..Size-1 into the domain.</summary>
        public ulong Size { get; }
        public bool Writable { get; }
        /// <summary>The CPU that disassembles this domain by default ("cpu" or "rsp"), or null for data.</summary>
        public string CodeCpu { get; }
        /// <summary>Address shown to the user for offset 0 (e.g. 0x80000000 for RDRAM, 0x04001000 for IMEM).</summary>
        public ulong DisplayBase { get; }
        public MemoryDomain(string name, ulong size, bool writable, string codeCpu = null, ulong displayBase = 0)
        { Name = name; Size = size; Writable = writable; CodeCpu = codeCpu; DisplayBase = displayBase; }
        public override string ToString() => $"{Name} ({Size:X} bytes{(Writable ? ", rw" : "")})";
    }

    public readonly struct RegisterInfo
    {
        public readonly string Name; public readonly int Bits; public readonly ulong Value;
        public RegisterInfo(string name, int bits, ulong value) { Name = name; Bits = bits; Value = value; }
        public override string ToString() => Bits <= 32 ? $"{Name}={Value:X8}" : $"{Name}={Value:X16}";
    }

    public readonly struct DisasmLine
    {
        public readonly ulong Address; public readonly uint Word; public readonly string Text; public readonly string Label;
        public DisasmLine(ulong address, uint word, string text, string label) { Address = address; Word = word; Text = text; Label = label; }
        public override string ToString() => $"{(uint)Address:X8}: {Word:X8}  {Text}";
    }

    public interface IDebugTarget
    {
        string SystemName { get; }
        /// <summary>Processors with register files and disassemblers, e.g. "cpu", "rsp".</summary>
        IReadOnlyList<string> Cpus { get; }
        IReadOnlyList<MemoryDomain> Domains { get; }

        /// <summary>Reads count bytes from domain at offset; returns bytes read (0 if the domain is unknown).</summary>
        int ReadMemory(string domain, ulong offset, Span<byte> dest);
        /// <summary>Writes bytes; returns bytes written (0 if the domain is unknown or read-only).</summary>
        int WriteMemory(string domain, ulong offset, ReadOnlySpan<byte> src);

        /// <summary>All registers of a cpu ("cpu" = GPRs, PC, HI, LO, COP0, FPRs, FCR31; "rsp" = GPRs, PC).</summary>
        IReadOnlyList<RegisterInfo> GetRegisters(string cpu);
        bool TryGetRegister(string cpu, string name, out ulong value);

        ulong GetPC(string cpu);
        /// <summary>Disassembles count instructions from a virtual address (cpu: KSEG0/1 VA; rsp: IMEM offset).</summary>
        IReadOnlyList<DisasmLine> Disassemble(string cpu, ulong address, int count);

        SymbolTable Symbols { get; }
    }

    /// <summary>[dbg_mem] Memory side of the debugger (BrokenNes read/write/search_memory, set_register, watch).
    /// Like IDebugTarget, call between frames only; watch hits are recorded during RunFrame.</summary>
    public interface IDebugMemoryEx : IDebugTarget
    {
        /// <summary>Writes a register by the name GetRegisters reports (cpu: GPRs, pc, hi, lo, COP0, FPRs, fcr31;
        /// rsp: GPRs, pc). False if the cpu or register is unknown or read-only.</summary>
        bool SetRegister(string cpu, string name, ulong value);

        /// <summary>CPU virtual address to physical with no side effects: KSEG0/KSEG1 directly, else a TLB walk
        /// (current ASID) that raises no exception, writes no COP0 register and fills no translation cache.</summary>
        bool TryTranslate(ulong va, out uint phys);

        /// <summary>Offsets in domain where (mem &amp; mask) == (pattern &amp; mask); an empty mask means all bits.
        /// Scans [start, start+length) and stops after maxResults hits.</summary>
        IReadOnlyList<ulong> Search(string domain, ReadOnlySpan<byte> pattern, ReadOnlySpan<byte> mask, ulong start = 0, ulong length = ulong.MaxValue, int maxResults = 1024);

        /// <summary>Adds a watch. KSEG0/KSEG1 addresses and TLB-mapped VAs are translated to physical (so every
        /// mirror fires); addresses below 0x20000000 are physical. Returns the watch id, or -1 if a VA does not translate.</summary>
        int AddWatch(ulong address, int length, WatchKind kind, WatchCondition condition = WatchCondition.Any, ulong value = 0, ulong mask = ulong.MaxValue);
        bool RemoveWatch(int id);
        void ClearWatches();
        IReadOnlyList<MemoryWatch> Watches { get; }
        /// <summary>Hits recorded since the last call (oldest first), then clears them.</summary>
        IReadOnlyList<WatchHit> DrainWatchHits();
    }
}
