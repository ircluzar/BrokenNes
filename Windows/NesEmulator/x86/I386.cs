// Intel 80386 / 80486 core. Ported from tiny386 (https://github.com/hchunhui/tiny386, i386.c + i386ins.def, commit 2c1205c),
// BSD-3-Clause, Copyright (c) 2024-2025, Chunhui He. The licence text and what was kept, changed and left out are in
// x86/THIRD_PARTY_NOTICES.md. Verified by Workshop --x86test.
//
// The x86 console is ISOLATED (docs/projects/next-pool-plans.md, P3): this core is never a slot of another console, and no other
// console's chip is a slot of it.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NesEmulator.X86;

/// <summary>What the CPU sees outside itself: I/O ports, the memory-mapped device windows (0xA0000-0xBFFFF and 0xE0000000 up) and the PIC.</summary>
public interface IX86Bus
{
    byte In8(int port); void Out8(int port, byte v);
    ushort In16(int port); void Out16(int port, ushort v);
    uint In32(int port); void Out32(int port, uint v);
    /// <summary>True when the board answers the device windows itself (tiny386: the iomem callbacks are set); false = plain RAM there.</summary>
    bool MapsIoMem { get; }
    byte MemRead8(uint a); void MemWrite8(uint a, byte v);
    ushort MemRead16(uint a); void MemWrite16(uint a, ushort v);
    uint MemRead32(uint a); void MemWrite32(uint a, uint v);
    /// <summary>The vector of the pending interrupt (tiny386 pic_read_irq), called when the CPU takes it.</summary>
    int AckIrq();
}

/// <summary>A CPU fault tiny386 handles with abort() (TRY1 / cpu_abort): a state the core cannot continue from.</summary>
public sealed class I386AbortException : Exception
{
    public I386AbortException(string m) : base(m) { }
}

/// <summary>
/// The tiny386 i386 core in C#: real mode, 16/32-bit protected mode, paging, virtual-8086 mode, the 486 additions. No FPU (an ESC instruction
/// decodes its operand and does nothing, unless CR0.EM/TS ask for #NM), no MMX/SSE, no long mode, no fast path.
/// One <see cref="Step"/> runs up to <c>n</c> instructions (fewer when HLT, an interrupt window or a fault ends the batch).
/// </summary>
public sealed partial class I386
{
    // ---- architectural state ----
    public readonly uint[] R = new uint[8];          // EAX ECX EDX EBX ESP EBP ESI EDI
    public uint Ip, NextIp;
    public uint Flags;                               // EFLAGS, but the arithmetic flags may be lazy: read them with GetFlags()
    public uint FlagsMask;
    public int Cpl;
    public bool Code16;
    public uint SpMask;
    public bool Halt;
    public readonly uint[] SegSel = new uint[8], SegBase = new uint[8], SegLimit = new uint[8], SegFlags = new uint[8];
    /// <summary>Bumped on every write to SegSel / SegBase / SegLimit / SegFlags (compiled blocks skip their segment checks while it holds).</summary>
    public uint SegGen;
    public uint IdtBase, IdtLimit, GdtBase, GdtLimit;
    public uint Cr0, Cr2, Cr3;
    public readonly uint[] Dr = new uint[8];
    public long Cycle;                               // instructions executed (tiny386's cycle counter)
    public int Excno;
    /// <summary>Debug tools: called with (vector, error code) before each CPU exception is delivered (CS:EIP still the faulting instruction).</summary>
    public Action<int, uint>? ExceptionLog;
    /// <summary>Debug tools: called with (old, new) on MOV CR0 (CS:EIP still the MOV).</summary>
    public Action<uint, uint>? Cr0Log;
    public uint Excerr;
    public bool Intr;
    public readonly int Gen;

    // ---- lazy flags ----
    private int _ccOp;
    private uint _ccDst, _ccDst2, _ccSrc1, _ccSrc2, _ccMask;

    // ---- memory ----
    public readonly byte[] Mem;                      // physical RAM (16 spare bytes at the end so a word access at the last byte stays in the array)
    public readonly uint MemSize;
    private IX86Bus _bus;
    private readonly bool _ioMem;

    // instruction fetch cache
    private ulong _ifLaddr;
    private uint _ifXaddr, _ifPaddr;

    // TLB
    /// <summary>X86_TLBBITS (default 9 = 512 entries): the TLB size as a power of two (direct-mapped; the JIT reads it at emit time).</summary>
    private static readonly int TlbSize = 1 << (int.TryParse(Environment.GetEnvironmentVariable("X86_TLBBITS"), out var tb) && tb is >= 6 and <= 14 ? tb : 9);
    /// <summary>The TLB slot of a linear page: X86_TLBHASH folds bits 9+ in (pages 2 MB apart no longer share a slot: Win98's VMM / Win16
    /// thunk data against user stacks); default the low bits as before.</summary>
    private static readonly bool TlbHash = Environment.GetEnvironmentVariable("X86_TLBHASH") != null;
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static int TlbIdx(uint lp) => (int)((TlbHash ? lp ^ (lp >> 9) ^ (lp >> 18) : lp) & (TlbSize - 1));
    private readonly uint[] _tlbLpgno = new uint[TlbSize], _tlbXaddr = new uint[TlbSize];
    private readonly int[] _tlbLookup = new int[TlbSize];    // index into PteLookup: wp*4 + ((pte >> 1) & 3)
    private readonly int[] _tlbPpte = new int[TlbSize];      // physical address of the PTE's low byte
    /// <summary>The compiled code's TLB hit test (I386.Jit.cs Xlat): [(user * 2 + write) * TlbSize + entry] = the entry's page number once a translation
    /// at that privilege (and a write: its dirty bit set) passed, else FFFFFFFF; cleared with the entry.</summary>
    private readonly uint[] _tlbTag = new uint[4 * TlbSize];
    private void TagClear(int e) { _tlbTag[e] = _tlbTag[e + TlbSize] = _tlbTag[e + 2 * TlbSize] = _tlbTag[e + 3 * TlbSize] = 0xffffffff; }

    public const int SEG_ES = 0, SEG_CS = 1, SEG_SS = 2, SEG_DS = 3, SEG_FS = 4, SEG_GS = 5, SEG_LDT = 6, SEG_TR = 7;
    public const int EX_DE = 0, EX_DB = 1, EX_NMI = 2, EX_BP = 3, EX_OF = 4, EX_BR = 5, EX_UD = 6, EX_NM = 7, EX_DF = 8, EX_INT9 = 9,
        EX_TS = 10, EX_NP = 11, EX_SS = 12, EX_GP = 13, EX_PF = 14;
    public const uint CF = 0x1, PF = 0x4, AF = 0x10, ZF = 0x40, SF = 0x80, TF = 0x100, IF = 0x200, DF = 0x400, OF = 0x800,
        IOPL = 0x3000, NT = 0x4000, RF = 0x10000, VM = 0x20000;
    private const uint SEG_D_BIT = 1 << 14, SEG_B_BIT = 1 << 14;
    private const uint CR0_PG = 1u << 31, CR0_WP = 0x10000;
    private const uint EFLAGS_MASK_386 = 0x37fd7, EFLAGS_MASK_486 = 0x77fd7, EFLAGS_MASK_586 = 0x277fd7;

    private enum Cc
    {
        ADC, ADD, SBB, SUB, NEG8, NEG16, NEG32, DEC8, DEC16, DEC32, INC8, INC16, INC32,
        IMUL8, IMUL16, IMUL32, MUL8, MUL16, MUL32, SAR, SHL, SHR, SHLD, SHRD, BSF, BSR, AND, OR, XOR,
    }

    /// <param name="gen">3 = 386, 4 = 486 (5/6 accepted for the flags mask only).</param>
    public I386(int gen, int memSize, IX86Bus bus) : this(CpuModel.LegacyModel(gen), memSize, bus) { }

    /// <summary>A core of the given CPU model (see I386.Model.cs).</summary>
    public I386(CpuModel model, int memSize, IX86Bus bus)
    {
        int gen = model.Gen;
        FlagsMask = model.Legacy ? gen switch { 3 => EFLAGS_MASK_386, 4 => EFLAGS_MASK_486, 5 or 6 => EFLAGS_MASK_586, _ => throw new ArgumentException("gen") } : model.FlagsMask;
        Gen = gen;
        Model = model;
        _legacy = model.Legacy;
        _hasFpu = !_legacy && model.Fpu; _has486 = !_legacy && model.Ops486; _hasCpuid = !_legacy && model.Cpuid;
        _has586 = !_legacy && model.Ops586; _hasMmx = !_legacy && model.Mmx; _has686 = !_legacy && model.Ops686;
        _repCost16 = RepCosts(model);
        _memCost = MemCost(model);
        BuildOpCosts(model, out _opCost, out _opCost0F, out _mulCost, out _divCost);
        // X86_P5COST (measurement only, not a default): "nomem" = memory operands cost nothing extra, "nofpu" = x87 opcodes nothing extra,
        // "flat" = every instruction one clock unit (no class extras, no memory, no REP iterations): the timing-model sensitivity for the bench ladder
        if (Environment.GetEnvironmentVariable("X86_P5COST") is string pc && !model.Legacy)
        {
            if (pc.Contains("nomem") || pc.Contains("flat")) _memCost = 0;
            if ((pc.Contains("nofpu") || pc.Contains("flat")) && _opCost != null) for (int i = 0xd8; i <= 0xdf; i++) _opCost[i] = 0;
            if (pc.Contains("flat")) { if (_opCost != null) Array.Clear(_opCost); if (_opCost0F != null) Array.Clear(_opCost0F); if (_mulCost != null) Array.Clear(_mulCost); if (_divCost != null) Array.Clear(_divCost); _repCost16 = null; }
        }
        Mem = new byte[memSize + 16];
        MemSize = (uint)memSize;
        _codeMark = new byte[(memSize >> 6) + 2];
        _pgGen = new uint[(memSize >> 12) + 2];
        _bus = bus;
        _ioMem = bus.MapsIoMem;
        Reset();
    }

    // ================= small helpers =================

    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static uint Sext8(uint a) => (uint)(int)(sbyte)a;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static uint Sext16(uint a) => (uint)(int)(short)a;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Sext(int bits, uint a) => bits == 8 ? Sext8(a) : bits == 16 ? Sext16(a) : a;
    private static uint SetBit(uint w, bool f, uint m) => f ? w | m : w & ~m;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint LReg(int bits, int i) => bits == 8 ? (i > 3 ? (R[i - 4] >> 8) & 0xff : R[i] & 0xff) : bits == 16 ? R[i] & 0xffff : R[i];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SReg(int bits, int i, uint v)
    {
        if (bits == 8) { if (i > 3) R[i - 4] = (R[i - 4] & 0xffff00ff) | ((v & 0xff) << 8); else R[i] = (R[i] & 0xffffff00) | (v & 0xff); }
        else if (bits == 16) R[i] = (R[i] & 0xffff0000) | (v & 0xffff);
        else R[i] = v;
    }
    private uint L8(int i) => LReg(8, i);
    private uint L16(int i) => R[i] & 0xffff;
    private void S8(int i, uint v) => SReg(8, i, v);
    private void S16(int i, uint v) => R[i] = (R[i] & 0xffff0000) | (v & 0xffff);
    private void SetSp(uint v, uint mask) => R[4] = (v & mask) | (R[4] & ~mask);

    [MethodImpl(MethodImplOptions.AggressiveInlining)] private byte P8(uint a) => Mem[a];
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private uint P16(uint a) => Unsafe.ReadUnaligned<ushort>(ref Mem[a]);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private uint P32(uint a) => Unsafe.ReadUnaligned<uint>(ref Mem[a]);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private void PS8(uint a, uint v) { Mem[a] = (byte)v; CodeWr1(a); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private void PS16(uint a, uint v) { Unsafe.WriteUnaligned(ref Mem[a], (ushort)v); CodeWr(a, 2); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private void PS32(uint a, uint v) { Unsafe.WriteUnaligned(ref Mem[a], v); CodeWr(a, 4); }

    // ---- the compiled blocks' code-change test (I386.Jit.cs JitSame): a store to a 64-byte line some compiled block was made from bumps the
    // generation of its 4 KB page; a block runs without comparing its bytes while its pages' generations (and _memEpoch) are the ones it saw.
    // Writes to memory from outside the CPU (the BIOS, DMA, disk loads, save states, tools) bump _memEpoch: every block compares once.
    private byte[] _codeMark;   // per 64-byte line of RAM: 1 = a compiled block's bytes are there
    private readonly uint[] _pgGen;      // per 4 KB page: the code write generation
    private uint _memEpoch;
    /// <summary>Memory was written by something other than the CPU's own stores (the BIOS, DMA, a disk load, a tool): compiled blocks check their bytes.</summary>
    public void MemTouched() => _memEpoch++;
    /// <summary>RAM [a, a+n) was written from outside the CPU (bus-master DMA): only the pages holding compiled code lines in that range move to a new generation
    /// (cheaper than <see cref="MemTouched"/>, which makes every compiled block compare its bytes once).</summary>
    public void RamWritten(uint a, int n)
    {
        if (n <= 0) return;
        for (uint l = a >> 6, e = (uint)((a + n - 1) >> 6); l <= e; l++) if (_codeMark[l] != 0) { CodeWrite(l << 6, 64); }
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CodeWr1(uint a) { if (Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_codeMark), (nint)(a >> 6)) != 0) CodeWrite(a, 1); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CodeWr(uint a, uint n)
    {
        ref byte m = ref MemoryMarshal.GetArrayDataReference(_codeMark);
        if ((Unsafe.Add(ref m, (nint)(a >> 6)) | Unsafe.Add(ref m, (nint)((a + n - 1) >> 6))) != 0) CodeWrite(a, n);
    }
    /// <summary>A store of <paramref name="n"/> bytes at RAM address <paramref name="a"/> touched a code line: its page(s) move to a new generation.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CodeWrite(uint a, uint n)
    {
        if (a >= MemSize) return;
        if (_wsRealMark != null && !WsLog(a, n)) return;   // X86_WAITSKIP proof: every store comes here (I386.WaitSkip.cs)
        if (n == 4 && JitLiveImmOn) SmcNote(a);
        _pgGen[a >> 12]++;
        uint e = a + n - 1;
        if ((e >> 12) != (a >> 12) && e < MemSize) _pgGen[e >> 12]++;
    }
    /// <summary>A store of a whole range (REP fast paths): its code lines, if any, move their pages on.</summary>
    private void CodeWrRange(uint a, uint bytes)
    {
        if (bytes == 0) return;
        for (uint l = a >> 6, le = (a + bytes - 1) >> 6; l <= le; l++)
            if (_codeMark[l] != 0) { CodeWrite(l << 6, 64); }
    }

    private bool Throw(int ex, uint err) { Excno = ex; Excerr = err; return false; }
    private bool Throw0(int ex) { Excno = ex; return false; }
    private void Abort(string why) => throw new I386AbortException($"i386 abort: {why} at {SegSel[SEG_CS]:x4}:{Ip:x8} cycle {Cycle}");

    // ================= lazy flags =================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetCF()
    {
        if ((_ccMask & CF) == 0) return (Flags & CF) != 0 ? 1 : 0;
        switch ((Cc)_ccOp)
        {
            case Cc.ADC: return _ccDst <= _ccSrc2 ? 1 : 0;
            case Cc.ADD: return _ccDst < _ccSrc2 ? 1 : 0;
            case Cc.SBB: return _ccSrc1 <= _ccSrc2 ? 1 : 0;
            case Cc.SUB: return _ccSrc1 < _ccSrc2 ? 1 : 0;
            case Cc.NEG8: case Cc.NEG16: case Cc.NEG32: return _ccDst != 0 ? 1 : 0;
            case Cc.IMUL8: return Sext8(_ccDst) != _ccDst ? 1 : 0;
            case Cc.IMUL16: return Sext16(_ccDst) != _ccDst ? 1 : 0;
            case Cc.IMUL32: return (uint)((int)_ccDst >> 31) != _ccDst2 ? 1 : 0;
            case Cc.MUL8: return (_ccDst >> 8) != 0 ? 1 : 0;
            case Cc.MUL16: return (_ccDst >> 16) != 0 ? 1 : 0;
            case Cc.MUL32: return _ccDst2 != 0 ? 1 : 0;
            case Cc.SHL: case Cc.SHR: case Cc.SAR: return (int)(_ccDst2 & 1);
            case Cc.SHLD: return (int)(_ccDst2 >> 31);
            case Cc.SHRD: return (int)(_ccDst2 & 1);
            default: return 0;   // BSF BSR AND OR XOR (INC/DEC never have CF in the mask)
        }
    }

    private static readonly byte[] ParityTab = BuildParity();
    private static byte[] BuildParity()
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++) t[i] = (byte)((System.Numerics.BitOperations.PopCount((uint)i) & 1) == 0 ? 1 : 0);
        return t;
    }

    private int GetPF() => (_ccMask & PF) != 0 ? ParityTab[_ccDst & 0xff] : ((Flags & PF) != 0 ? 1 : 0);

    private int GetAF()
    {
        if ((_ccMask & AF) == 0) return (Flags & AF) != 0 ? 1 : 0;
        switch ((Cc)_ccOp)
        {
            case Cc.ADC: case Cc.ADD: case Cc.SBB: case Cc.SUB: return (int)(((_ccSrc1 ^ _ccSrc2 ^ _ccDst) >> 4) & 1);
            case Cc.NEG8: case Cc.NEG16: case Cc.NEG32: return (_ccDst & 0xf) != 0 ? 1 : 0;
            case Cc.DEC8: case Cc.DEC16: case Cc.DEC32: return (_ccDst & 0xf) == 0xf ? 1 : 0;
            case Cc.INC8: case Cc.INC16: case Cc.INC32: return (_ccDst & 0xf) == 0 ? 1 : 0;
            default: return 0;
        }
    }

    private int GetZF() => (_ccMask & ZF) != 0 ? (_ccDst == 0 ? 1 : 0) : ((Flags & ZF) != 0 ? 1 : 0);
    private int GetSF() => (_ccMask & SF) != 0 ? (int)(_ccDst >> 31) : ((Flags & SF) != 0 ? 1 : 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetOF()
    {
        if ((_ccMask & OF) == 0) return (Flags & OF) != 0 ? 1 : 0;
        switch ((Cc)_ccOp)
        {
            case Cc.ADC: case Cc.ADD: return (int)((~(_ccSrc1 ^ _ccSrc2) & (_ccDst ^ _ccSrc2)) >> 31);
            case Cc.SBB: case Cc.SUB: return (int)(((_ccSrc1 ^ _ccSrc2) & (_ccDst ^ _ccSrc1)) >> 31);
            case Cc.DEC8: return _ccDst == 0x7f ? 1 : 0;
            case Cc.DEC16: return _ccDst == 0x7fff ? 1 : 0;
            case Cc.DEC32: return _ccDst == 0x7fffffff ? 1 : 0;
            case Cc.INC8: case Cc.NEG8: return _ccDst == 0xffffff80 ? 1 : 0;
            case Cc.INC16: case Cc.NEG16: return _ccDst == 0xffff8000 ? 1 : 0;
            case Cc.INC32: case Cc.NEG32: return _ccDst == 0x80000000 ? 1 : 0;
            case Cc.IMUL8: case Cc.IMUL16: case Cc.IMUL32: case Cc.MUL8: case Cc.MUL16: case Cc.MUL32: return GetCF();
            case Cc.SHL: return (int)((_ccDst >> 31) ^ (_ccDst2 & 1));
            case Cc.SHR: return (int)(_ccSrc1 >> 31);
            case Cc.SHLD: case Cc.SHRD: return (int)((_ccSrc1 ^ _ccDst) >> 31);
            default: return 0;
        }
    }

    private void RefreshFlags()
    {
        uint f = Flags;
        f = SetBit(f, GetCF() != 0, CF);
        f = SetBit(f, GetPF() != 0, PF);
        f = SetBit(f, GetAF() != 0, AF);
        f = SetBit(f, GetZF() != 0, ZF);
        f = SetBit(f, GetSF() != 0, SF);
        f = SetBit(f, GetOF() != 0, OF);
        Flags = f;
    }

    private int GetIOPL() => (int)((Flags & IOPL) >> 12);

    // ================= MMU =================

    private struct OptAddr { public bool Two; public uint A1, A2; }

    // [wp][(pte >> 1) & 3][cpl == 3][rwm > 1] -> 1 = fault
    private static readonly byte[] PteLookup =
    {
        0,0, 1,1,   0,0, 1,1,   0,0, 0,1,   0,0, 0,0,   // wp == 0: s,r  s,w  u,r  u,w
        0,1, 1,1,   0,0, 1,1,   0,1, 0,1,   0,0, 0,0,   // wp == 1
    };

    private static readonly bool InvlpgAll = Environment.GetEnvironmentVariable("X86_INVLPGALL") != null;
    private void TlbClear()
    {
        for (int i = 0; i < TlbSize; i++) _tlbLpgno[i] = 0xffffffff;
        Array.Fill(_tlbTag, 0xffffffff);
        _ifLaddr = ulong.MaxValue;
        _ifPaddr = 0;
    }

    private uint PRead32Safe(uint a) => a <= MemSize - 4 ? P32(a) : 0;

    private uint _refillPde = 0xffffffff, _refillPte;
    private void RefillDone(bool ok)
    {
        if (_refillPde == 0xffffffff) return;
        if (ok) { Mem[_refillPde] |= 1 << 5; Mem[_refillPte] |= 1 << 5; CodeWr1(_refillPde); CodeWr1(_refillPte); }
        _refillPde = 0xffffffff;
    }

    private bool TlbRefill(int e, uint lpgno)
    {
        uint baseAddr = Cr3 & ~0xfffu;
        uint i = lpgno >> 10, j = lpgno & 1023;
        uint pdeAddr = baseAddr + i * 4;
        uint pde = PRead32Safe(pdeAddr);
        if ((pde & 1) == 0) return false;
        uint base2 = pde & ~0xfffu;
        uint pteAddr = base2 + j * 4;
        uint pte = PRead32Safe(pteAddr);
        if ((pte & 1) == 0) return false;
        _refillPde = pdeAddr; _refillPte = pteAddr;   // the accessed bits are set once the access passes the protection check (a 386 sets no A bit on a faulting walk; test386 POST 11h)
        _tlbLpgno[e] = lpgno; TagClear(e);
        _tlbXaddr[e] = (pte & ~0xfffu) ^ (lpgno << 12);
        pte &= (pde & 7) | 0xfffffff8;
        _tlbLookup[e] = ((Cr0 & CR0_WP) != 0 ? 4 : 0) + (int)((pte >> 1) & 3);
        _tlbPpte[e] = (int)pteAddr;
        return true;
    }

    private bool TranslateLpgno(int rwm, uint lpgno, uint laddr, int cpl, out uint paddr)
    {
        paddr = 0;
        int e = TlbIdx(lpgno);
        if (_tlbLpgno[e] != lpgno)
        {
            if (!TlbRefill(e, lpgno))
            {
                Cr2 = laddr; Excno = EX_PF; Excerr = 0;
                if ((rwm & 2) != 0) Excerr |= 2;
                if (cpl == 3) Excerr |= 4;
                return false;
            }
        }
        if (PteLookup[_tlbLookup[e] * 4 + (cpl == 3 ? 2 : 0) + (rwm > 1 ? 1 : 0)] != 0)
        {
            Cr2 = laddr; Excno = EX_PF; Excerr = 1;
            if ((rwm & 2) != 0) Excerr |= 2;
            if (cpl == 3) Excerr |= 4;
            _tlbLpgno[e] = 0xffffffff; TagClear(e);
            RefillDone(false);
            return false;
        }
        RefillDone(true);
        paddr = _tlbXaddr[e] ^ laddr;
        int tg = (cpl == 3 ? 2 * TlbSize : 0) + e;
        _tlbTag[tg] = lpgno;
        if ((rwm & 2) != 0) { Mem[_tlbPpte[e]] |= 1 << 6; CodeWr1((uint)_tlbPpte[e]); _tlbTag[tg + TlbSize] = lpgno; } // dirty
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TranslateLaddr(ref OptAddr res, int rwm, uint laddr, int size, int cpl)
    {
        if ((Cr0 & CR0_PG) != 0)
        {
            uint lpgno = laddr >> 12;
            if (!TranslateLpgno(rwm, lpgno, laddr, cpl, out uint paddr)) return false;
            res.Two = false; res.A1 = paddr;
            if ((laddr & 0xfff) > 0x1000 - size)
            {
                lpgno++;
                if (!TranslateLpgno(rwm, lpgno, lpgno << 12, cpl, out paddr)) return false;
                res.Two = true; res.A2 = paddr;
            }
        }
        else { res.Two = false; res.A1 = laddr; }
        return true;
    }

    /// <summary>The segment limit check (every mode, real mode too: offset FFFFh + a word = #GP, the 386's "segment wrap" fault):
    /// expand-up = every byte at or below the limit; expand-down data = above the limit, up to FFFFh or (B bit) FFFFFFFFh.
    /// #SS(0) for SS, #GP(0) otherwise. tiny386 had no limit checks (SingleStepTests 80386: ~100k tests faulted there).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool LimitOk(int seg, uint addr, int size)
    {
        uint lim = SegLimit[seg], last = addr + (uint)(size - 1);
        if ((SegFlags[seg] & 0xc) != 0x4)
        {
            if (addr <= lim && last <= lim && last >= addr) return true;
        }
        else
        {
            uint top = (SegFlags[seg] & SEG_B_BIT) != 0 ? 0xffffffff : 0xffff;
            if (addr > lim && last <= top && last >= addr) return true;
        }
        return LimitFault(seg);
    }
    /// <summary>A protected-mode descriptor's type: writes need a writable data segment, reads a data or readable code segment;
    /// #GP(0) (#SS(0) for SS) otherwise (test386 POST 12h, mov [0],eax with DS read-only). Real-mode loads leave SegFlags 0 and skip this.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TypeOk(int seg, int rwm)
    {
        uint f = SegFlags[seg];
        bool code = (f & 8) != 0, rw = (f & 2) != 0;
        if ((rwm & 2) != 0 ? (code || !rw) : (code && !rw)) return LimitFault(seg);
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool LimitFault(int seg) => Throw(seg == SEG_SS ? EX_SS : EX_GP, 0);

    private bool SegCheck(int seg)
    {
        if ((Cr0 & 1) != 0 && SegLimit[seg] == 0 && (SegSel[seg] & ~3u) == 0) return Throw(EX_GP, 0); // null selector
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Translate(ref OptAddr res, int rwm, int seg, uint addr, int size, int cpl)
    {
        uint laddr = SegBase[seg] + addr;
        if (!SegCheck(seg)) return false;
        if (!LimitOk(seg, addr, size)) return false;
        if ((SegFlags[seg] & 0x10) != 0 && !TypeOk(seg, rwm)) return false;
        return TranslateLaddr(ref res, rwm, laddr, size, cpl);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Translate(ref OptAddr res, int rwm, int seg, uint addr, int size) => Translate(ref res, rwm, seg, addr, size, Cpl);

    private bool Translate8r(ref OptAddr res, int seg, uint addr)
    {
        uint laddr = SegBase[seg] + addr;
        if (!SegCheck(seg)) return false;
        if (!LimitOk(seg, addr, 1)) return false;
        if ((Cr0 & CR0_PG) != 0)
        {
            uint lpgno = laddr >> 12;
            int e = TlbIdx(lpgno);
            if (_tlbLpgno[e] != lpgno && !TlbRefill(e, lpgno))
            {
                Cr2 = laddr; Excno = EX_PF; Excerr = Cpl == 3 ? 4u : 0u;
                return false;
            }
            if (PteLookup[_tlbLookup[e] * 4 + (Cpl == 3 ? 2 : 0)] != 0)
            {
                Cr2 = laddr; Excno = EX_PF; Excerr = Cpl == 3 ? 5u : 1u;
                _tlbLpgno[e] = 0xffffffff; TagClear(e);
                RefillDone(false);
                return false;
            }
            RefillDone(true);
            res.Two = false; res.A1 = _tlbXaddr[e] ^ laddr;
        }
        else { res.Two = false; res.A1 = laddr; }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool InIoMem(uint a) => (a >= 0xa0000 && a < 0xc0000) || a >= 0xe0000000;

    // unchecked RAM access: only for an address already checked to be below MemSize (Mem has 16 spare bytes past MemSize)
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private uint U8(uint a) => Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Mem), (nint)a);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private uint U16(uint a) => Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Mem), (nint)a));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private uint U32(uint a) => Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Mem), (nint)a));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private void US8(uint a, uint v) { Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Mem), (nint)a) = (byte)v; CodeWr1(a); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private void US16(uint a, uint v) { Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Mem), (nint)a), (ushort)v); CodeWr(a, 2); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private void US32(uint a, uint v) { Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Mem), (nint)a), v); CodeWr(a, 4); }

    /// <summary>Plain RAM (below MemSize and, with memory-mapped I/O on, outside A0000-BFFFF; the E0000000+ window is above MemSize).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsRam(uint a) => a < MemSize && (!_ioMem || a - 0xa0000u >= 0x20000u);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint Load8(ref OptAddr r) { if (RwOn) RwHit(r.A1, 1); return IsRam(r.A1) ? U8(r.A1) : Load8Slow(ref r); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private uint Load8Slow(ref OptAddr r)
    {
        uint a = r.A1;
        if (IsRam(a)) return U8(a);
        if (_ioMem && InIoMem(a)) return _bus.MemRead8(a);
        if (a >= MemSize) return 0;
        return P8(a);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint Load16(ref OptAddr r) { if (RwOn) RwHit(r.A1, 2); return !r.Two && IsRam(r.A1) ? U16(r.A1) : Load16Slow(ref r); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private uint Load16Slow(ref OptAddr r)
    {
        if (!r.Two && IsRam(r.A1)) return U16(r.A1);
        if (_ioMem && InIoMem(r.A1)) return _bus.MemRead16(r.A1);
        if (r.A1 >= MemSize) return 0;
        if (!r.Two) return P16(r.A1);
        return P8(r.A1) | ((uint)P8(r.A2) << 8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint Load32(ref OptAddr r) { if (RwOn) RwHit(r.A1, 4); return !r.Two && IsRam(r.A1) ? U32(r.A1) : Load32Slow(ref r); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private uint Load32Slow(ref OptAddr r)
    {
        if (!r.Two && IsRam(r.A1)) return U32(r.A1);
        if (_ioMem && InIoMem(r.A1)) return _bus.MemRead32(r.A1);
        if (r.A1 >= MemSize) return 0;
        if (!r.Two) return P32(r.A1);
        switch (r.A1 & 0xf)
        {
            case 0xf: return P8(r.A1) | (P16(r.A2) << 8) | ((uint)P8(r.A2 + 2) << 24);
            case 0xe: return P16(r.A1) | (P16(r.A2) << 16);
            default: return P8(r.A1) | (P16(r.A1 + 1) << 8) | ((uint)P8(r.A2) << 24); // 0xd
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Store8(ref OptAddr r, uint v) { if (IsRam(r.A1)) US8(r.A1, v); else Store8Slow(ref r, v); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Store8Slow(ref OptAddr r, uint v)
    {
        uint a = r.A1;
        if (IsRam(a)) { US8(a, v); return; }
        if (_ioMem && InIoMem(a)) { _bus.MemWrite8(a, (byte)v); return; }
        if (a >= MemSize) return;
        PS8(a, v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Store16(ref OptAddr r, uint v) { if (!r.Two && IsRam(r.A1)) US16(r.A1, v); else Store16Slow(ref r, v); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Store16Slow(ref OptAddr r, uint v)
    {
        if (!r.Two && IsRam(r.A1)) { US16(r.A1, v); return; }
        if (_ioMem && InIoMem(r.A1)) { _bus.MemWrite16(r.A1, (ushort)v); return; }
        if (r.A1 >= MemSize) return;
        if (!r.Two) PS16(r.A1, v);
        else { PS8(r.A1, v); PS8(r.A2, v >> 8); }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Store32(ref OptAddr r, uint v) { if (!r.Two && IsRam(r.A1)) US32(r.A1, v); else Store32Slow(ref r, v); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Store32Slow(ref OptAddr r, uint v)
    {
        if (!r.Two && IsRam(r.A1)) { US32(r.A1, v); return; }
        if (_ioMem && InIoMem(r.A1)) { _bus.MemWrite32(r.A1, v); return; }
        if (r.A1 >= MemSize) return;
        if (!r.Two) { PS32(r.A1, v); return; }
        switch (r.A1 & 0xf)
        {
            case 0xf: PS8(r.A1, v); PS16(r.A2, v >> 8); PS8(r.A2 + 2, v >> 24); break;
            case 0xe: PS16(r.A1, v); PS16(r.A2, v >> 16); break;
            default: PS8(r.A1, v); PS16(r.A1 + 1, v >> 8); PS8(r.A2, v >> 24); break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint Load(int bits, ref OptAddr r) => bits == 8 ? Load8(ref r) : bits == 16 ? Load16(ref r) : Load32(ref r);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Store(int bits, ref OptAddr r, uint v) { if (bits == 8) Store8(ref r, v); else if (bits == 16) Store16(ref r, v); else Store32(ref r, v); }

    // public linear-segment accessors (tiny386 cpu_load8/cpu_store8...): for the BIOS and the tests
    public bool CpuLoad(int bits, int seg, uint addr, out uint v)
    {
        var o = new OptAddr(); v = 0;
        if (!Translate(ref o, 1, seg, addr, bits / 8)) return false;
        v = Load(bits, ref o); return true;
    }
    public bool CpuStore(int bits, int seg, uint addr, uint v)
    {
        var o = new OptAddr();
        if (!Translate(ref o, 2, seg, addr, bits / 8)) return false;
        Store(bits, ref o, v); return true;
    }

    // ================= instruction fetch =================

    private bool Peek8(out uint val)
    {
        uint laddr = SegBase[SEG_CS] + NextIp;
        if (((ulong)laddr ^ _ifLaddr) < 4096) { val = U8(_ifXaddr ^ laddr); return true; }
        val = 0;
        var res = new OptAddr();
        if (!Translate8r(ref res, SEG_CS, NextIp)) return false;
        uint paddr = res.A1;
        if (_ioMem && InIoMem(paddr)) { val = _bus.MemRead8(paddr); return true; }
        if (paddr >= MemSize) { val = 0; return true; }
        val = P8(paddr);
        if (MemSize > 4096 && paddr < MemSize - 4096 && !InIoMem(paddr + 16))
        {
            _ifLaddr = laddr & ~4095ul;
            _ifXaddr = paddr ^ laddr;
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Fetch8(out uint val)
    {
        if (_ifPaddr != 0) { val = U8(_ifPaddr); _ifPaddr++; NextIp++; return true; }
        return Fetch8Slow(out val);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool Fetch8Slow(out uint val)
    {
        if (!Peek8(out val)) return false;
        NextIp++;
        return true;
    }

    private bool Fetch8pf(out uint val)
    {
        uint laddr = SegBase[SEG_CS] + NextIp;
        if (((ulong)laddr ^ _ifLaddr) < 4096 - 16)
        {
            uint paddr = _ifXaddr ^ laddr;
            val = U8(paddr);
            _ifPaddr = paddr + 1;
            NextIp++;
            return true;
        }
        _ifPaddr = 0;
        if (!Peek8(out val)) return false;
        NextIp++;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Fetch16(out uint val)
    {
        if (_ifPaddr != 0) { val = U16(_ifPaddr); _ifPaddr += 2; NextIp += 2; return true; }
        return Fetch16Slow(out val);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool Fetch16Slow(out uint val)
    {
        uint laddr = SegBase[SEG_CS] + NextIp;
        if (((ulong)laddr ^ _ifLaddr) < 4095) val = U16(_ifXaddr ^ laddr);
        else
        {
            var res = new OptAddr(); val = 0;
            if (!Translate(ref res, 1, SEG_CS, NextIp, 2)) return false;
            val = Load16(ref res);
        }
        NextIp += 2;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Fetch32(out uint val)
    {
        if (_ifPaddr != 0) { val = U32(_ifPaddr); _ifPaddr += 4; NextIp += 4; return true; }
        return Fetch32Slow(out val);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool Fetch32Slow(out uint val)
    {
        uint laddr = SegBase[SEG_CS] + NextIp;
        if (((ulong)laddr ^ _ifLaddr) < 4093) val = U32(_ifXaddr ^ laddr);
        else
        {
            var res = new OptAddr(); val = 0;
            if (!Translate(ref res, 1, SEG_CS, NextIp, 4)) return false;
            val = Load32(ref res);
        }
        NextIp += 4;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Fetch(int bits, out uint val) => bits == 8 ? Fetch8(out val) : bits == 16 ? Fetch16(out val) : Fetch32(out val);

    // ================= ModR/M =================

    private bool Modsib32(int mod, int rm, out uint addr, ref int seg)
    {
        addr = 0;
        uint v;
        if (rm == 4)
        {
            if (!Fetch8(out uint sib)) return false;
            int b = (int)(sib & 7);
            if (b == 5 && mod == 0) { if (!Fetch32(out addr)) return false; }
            else
            {
                addr = R[b];
                if ((b == 4 || b == 5) && seg == -1) seg = SEG_SS;
            }
            int i = (int)((sib >> 3) & 7);
            if (i != 4) addr += R[i] << (int)(sib >> 6);
        }
        else if (rm == 5 && mod == 0) { if (!Fetch32(out addr)) return false; }
        else
        {
            addr = R[rm];
            if (rm == 5 && seg == -1) seg = SEG_SS;
        }
        if (mod == 1) { if (!Fetch8(out v)) return false; addr += Sext8(v); }
        else if (mod == 2) { if (!Fetch32(out v)) return false; addr += v; }
        if (seg == -1) seg = SEG_DS;
        return true;
    }

    private bool Modsib16(int mod, int rm, out uint addr, ref int seg)
    {
        addr = 0;
        uint v;
        if (rm == 6 && mod == 0) { if (!Fetch16(out addr)) return false; }
        else
        {
            switch (rm)
            {
                case 0: addr = R[3] + R[6]; break;
                case 1: addr = R[3] + R[7]; break;
                case 2: addr = R[5] + R[6]; break;
                case 3: addr = R[5] + R[7]; break;
                case 4: addr = R[6]; break;
                case 5: addr = R[7]; break;
                case 6: addr = R[5]; break;
                default: addr = R[3]; break;
            }
            if (mod == 1) { if (!Fetch8(out v)) return false; addr += Sext8(v); }
            else if (mod == 2) { if (!Fetch16(out v)) return false; addr += v; }
            addr &= 0xffff;
        }
        if (seg == -1)
        {
            if (rm == 2 || rm == 3) seg = SEG_SS;
            else if (mod != 0 && rm == 6) seg = SEG_SS;
            else seg = SEG_DS;
        }
        return true;
    }

    private bool Modsib(bool adsz16, int mod, int rm, out uint addr, ref int seg) =>
        adsz16 ? Modsib16(mod, rm, out addr, ref seg) : Modsib32(mod, rm, out addr, ref seg);

    // ================= descriptors and segments =================

    private bool ReadDesc(uint sel, out uint w1, out uint w2, bool wantW1 = true)
    {
        w1 = w2 = 0;
        sel &= 0xffff;
        uint off = sel & ~7u;
        uint bas, limit;
        if ((sel & 4) != 0) { bas = SegBase[SEG_LDT]; limit = SegLimit[SEG_LDT]; }
        else { bas = GdtBase; limit = GdtLimit; }
        if (off + 7 > limit) return Throw(EX_GP, sel & ~3u);
        var m = new OptAddr();
        if (wantW1)
        {
            if (!TranslateLaddr(ref m, 1, bas + off, 4, 0)) return false;
            w1 = Load32(ref m);
        }
        if (!TranslateLaddr(ref m, 1, bas + off + 4, 4, 0)) return false;
        w2 = Load32(ref m);
        return true;
    }

    /// <summary>MOV Sreg / POP Sreg / LDS..LSS in protected mode: the privilege and type checks of a data or stack segment load (386 manual,
    /// MOV): SS needs a writable data segment with RPL = DPL = CPL; DS/ES/FS/GS a data or readable code segment, and unless conforming code
    /// DPL >= max(CPL, RPL). test386 POST 0Bh (mov ds, DPL-0 selector from ring 3).</summary>
    private bool LoadSegChecked(int seg, uint sel)
    {
        sel &= 0xffff;
        if ((Cr0 & 1) == 0 || (Flags & VM) != 0) return SetSeg(seg, sel);
        if ((sel & ~3u) == 0)
        {
            if (seg == SEG_SS) return Throw(EX_GP, 0);
            return SetSeg(seg, sel);
        }
        if (!ReadDesc(sel, out uint w1, out uint w2)) return false;   // both words once: SetSegDesc uses them (no second descriptor read)
        bool s = ((w2 >> 12) & 1) != 0, code = ((w2 >> 11) & 1) != 0, rw = ((w2 >> 9) & 1) != 0, conf = ((w2 >> 10) & 1) != 0;
        int dpl = (int)((w2 >> 13) & 3), rpl = (int)(sel & 3);
        if (seg == SEG_SS)
        {
            if (rpl != Cpl || !s || code || !rw || dpl != Cpl) return Throw(EX_GP, sel & ~3u);
        }
        else if (s)
        {
            if (code && !rw) return Throw(EX_GP, sel & ~3u);
            if ((!code || !conf) && dpl < Math.Max(Cpl, rpl)) return Throw(EX_GP, sel & ~3u);
        }
        return SetSegDesc(seg, sel, w1, w2);
    }

    private bool SetSeg(int seg, uint sel)
    {
        sel &= 0xffff;
        if ((Cr0 & 1) == 0 || (Flags & VM) != 0)
        {
            uint s0 = SegSel[seg], b0 = SegBase[seg], f0 = SegFlags[seg], l0 = SegLimit[seg];
            SegSel[seg] = sel; SegBase[seg] = sel << 4; SegFlags[seg] = 0; if ((Flags & VM) != 0) SegLimit[seg] = 0xffff;
            if (s0 != SegSel[seg] || b0 != SegBase[seg] || f0 != SegFlags[seg] || l0 != SegLimit[seg]) SegGen++;   // (only a real change: Win98 reloads the same segments on every ring transition)   // a real-mode load keeps the cached limit, as a 386 does ("unreal mode"); V86 = 64 KB
            if (seg == SEG_CS) { Cpl = (Flags & VM) != 0 ? 3 : 0; Code16 = true; }
            if (seg == SEG_SS) SpMask = 0xffff;
            return true;
        }
        if (!ReadDesc(sel, out uint w1, out uint w2)) return false;
        return SetSegDesc(seg, sel, w1, w2);
    }

    /// <summary>The protected-mode part of <see cref="SetSeg"/> with the descriptor already read.</summary>
    private bool SetSegDesc(int seg, uint sel, uint w1, uint w2)
    {
        bool s = ((w2 >> 12) & 1) != 0;
        bool p = ((w2 >> 15) & 1) != 0;
        if ((sel & ~3u) != 0)
        {
            if ((seg == SEG_DS || seg == SEG_ES || seg == SEG_FS || seg == SEG_GS) && !s) return Throw(EX_GP, sel & ~3u);
            if (!p) return Throw(seg == SEG_SS ? EX_SS : EX_NP, sel & ~3u);
        }
        uint s0 = SegSel[seg], b0 = SegBase[seg], f0 = SegFlags[seg], l0 = SegLimit[seg];
        SegSel[seg] = sel;
        SegBase[seg] = (w1 >> 16) | ((w2 & 0xff) << 16) | (w2 & 0xff000000);
        SegLimit[seg] = (w2 & 0xf0000) | (w1 & 0xffff);
        if ((w2 & 0x00800000) != 0) SegLimit[seg] = (SegLimit[seg] << 12) | 0xfff;
        SegFlags[seg] = (w2 >> 8) & 0xffff;
        if (s0 != sel || b0 != SegBase[seg] || f0 != SegFlags[seg] || l0 != SegLimit[seg]) SegGen++;
        if (seg == SEG_CS) { Cpl = (int)(sel & 3); Code16 = (SegFlags[SEG_CS] & SEG_D_BIT) == 0; }
        if (seg == SEG_SS) SpMask = (SegFlags[SEG_SS] & SEG_B_BIT) != 0 ? 0xffffffff : 0xffff;
        return true;
    }

    private void ClearSegs()
    {
        ReadOnlySpan<int> segs = stackalloc int[] { SEG_DS, SEG_ES, SEG_FS, SEG_GS };
        foreach (int s in segs)
        {
            uint w2 = SegFlags[s] << 8;
            bool isData = ((w2 >> 11) & 1) == 0;
            int dpl = (int)((w2 >> 13) & 3);
            bool conforming = ((w2 >> 8) & 4) != 0;
            if ((isData || !conforming) && dpl < Cpl) { SegGen++; SegSel[s] = 0; SegBase[s] = 0; SegLimit[s] = 0; SegFlags[s] = 0; }
        }
    }

    private uint SsMask() => (SegFlags[SEG_SS] & SEG_B_BIT) != 0 ? 0xffffffff : 0xffff;

    // ================= public control =================

    public void Reset()
    {
        WsReset();
        _memEpoch++;
        Array.Clear(R);
        Flags = 0x2; Cpl = 0; Code16 = true; SpMask = 0xffff; Halt = false;
        for (int i = 0; i < 8; i++) { SegSel[i] = 0; SegBase[i] = 0; SegLimit[i] = i < 6 ? 0xffffu : 0; SegFlags[i] = 0; }
        SegFlags[2] = 1 << 22; SegFlags[1] = 1 << 22;
        Ip = 0xfff0; NextIp = Ip;
        SegSel[SEG_CS] = 0xf000; SegBase[SEG_CS] = 0xf0000;
        IdtBase = 0; IdtLimit = 0x3ff; GdtBase = 0; GdtLimit = 0;
        Cr0 = 0; Cr2 = 0; Cr3 = 0;
        if (!_legacy) { Cr4 = 0; if (_hasFpu) Cr0 = 0x10; FpuInit(); }   // ET: a 387-class FPU is there
        if (_has686) { _sysCs = _sysEsp = _sysEip = 0; Array.Clear(_mtrr); _pmcEvt0 = _pmcEvt1 = _pmc0 = _pmc1 = _biosSign = 0; }
        Array.Clear(Dr);
        _ccMask = 0; SegGen++;
        TlbClear();
        Excno = 0; Excerr = 0;
    }

    /// <summary>tiny386 cpui386_reset_pm: flat 32-bit protected mode at <paramref name="startAddr"/> (CS 0x08, SS/DS/ES 0x10).</summary>
    public void ResetPm(uint startAddr)
    {
        Reset();
        Cr0 = 1;
        SegSel[SEG_CS] = 0x8; SegBase[SEG_CS] = 0; SegLimit[SEG_CS] = 0xffffffff; SegFlags[SEG_CS] = SEG_D_BIT;
        NextIp = startAddr; Cpl = 0; Code16 = false; SpMask = 0xffffffff;
        SegSel[SEG_SS] = 0x10; SegBase[SEG_SS] = 0; SegLimit[SEG_SS] = 0xffffffff; SegFlags[SEG_SS] = SEG_B_BIT;
        foreach (int s in new[] { SEG_DS, SEG_ES })
        { SegSel[s] = SegSel[SEG_SS]; SegBase[s] = SegBase[SEG_SS]; SegLimit[s] = SegLimit[SEG_SS]; SegFlags[s] = SegFlags[SEG_SS]; }
        SegGen++;
    }

    /// <summary>Set the CS:IP the next instruction is fetched from (real mode / V86 meaning of CS); for the tests and the BIOS.</summary>
    public void SetCsIp(uint cs, uint ip) { SetSeg(SEG_CS, cs); NextIp = ip; Ip = ip; }
    public bool LoadSeg(int seg, uint sel) => SetSeg(seg, sel);

    public void RaiseIrq() => Intr = true;

    /// <summary>EFLAGS with the lazy flags folded in (tiny386 cpu_getflags).</summary>
    public uint GetFlags()
    {
        if (_ccMask != 0) { RefreshFlags(); _ccMask = 0; }
        return Flags;
    }

    /// <summary>EFLAGS with the lazy flags folded in, without touching any state (for tracers and tests).</summary>
    public uint PeekFlags()
    {
        uint save = Flags;
        RefreshFlags();
        uint f = Flags;
        Flags = save;
        return f;
    }

    /// <summary>tiny386 cpu_setflags.</summary>
    public void SetFlags(uint setMask, uint clearMask)
    {
        if ((_ccMask & (setMask | clearMask)) != 0) { RefreshFlags(); _ccMask = 0; }
        Flags |= setMask; Flags &= ~clearMask; Flags &= FlagsMask;
    }

    /// <summary>tiny386 cpui386_step: take a pending interrupt, then run up to <paramref name="stepcount"/> instructions; a fault is delivered through the IDT/IVT.</summary>
    public long V86Enters;                           // IRETs into V86 mode (nano98 profiling; not saved)
    public long TicksExec1;
    public void Step(int stepcount)
    {
        if ((Flags & IF) != 0 && Intr)
        {
            if (WaitSkipOn) WsOnIrq();
            Intr = false; Halt = false;
            int no = _bus.AckIrq();
            Ip = NextIp;
            if (!CallIsr(no, false, 1)) Abort("interrupt delivery failed");
        }
        if (Halt) return;
        if (!_jitDone.IsEmpty) JitDrain();
        long tq0 = PcMachine.TimeProbe ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        bool ret = WaitSkipOn && WsBegin() ? WsExec(stepcount) : Exec1(stepcount);
        if (PcMachine.TimeProbe) TicksExec1 += System.Diagnostics.Stopwatch.GetTimestamp() - tq0;
        _ifPaddr = 0;
        if (!ret)
        {
            bool pusherr = Excno is EX_DF or EX_TS or EX_NP or EX_SS or EX_GP or EX_PF;
            NextIp = Ip;
            ExceptionLog?.Invoke(Excno, Excerr);
            if (!CallIsr(Excno, pusherr, 1)) Abort($"exception {Excno} delivery failed");
        }
        if (_wsRealMark != null) WsLogOff();
    }
}
