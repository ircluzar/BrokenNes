// Motorola 68000 core. Derived from the m68000 component of ares (https://github.com/ares-emulator/ares, ares/component/processor/m68000), ISC License,
// Copyright (c) 2004-2025 ares team, Near et al. The licence text and what was kept, changed and added are in sega/THIRD_PARTY_NOTICES.md.
// Verified by Workshop --m68ktest (SingleStepTests/680x0 and SingleStepTests/m68000).

using System;
using System.Runtime.CompilerServices;

namespace NesEmulator.Sega;

/// <summary>
/// A Motorola 68000, executed one instruction at a time, but with every bus cycle reported to the board at the moment it happens (see <see cref="IM68kBus"/>). Generic over a struct
/// <typeparamref name="TBus"/> so the bus calls are direct and the core stays AOT/WebAssembly friendly, like <see cref="Z80{TBus}"/>.
/// </summary>
/// <remarks>
/// <para><b>Design (from ares).</b> Each instruction is written the way the chip does it: internal cycles are <c>Idle(n)</c>, every bus cycle is a four-clock <c>Wait(4)</c> plus one word-wide
/// access, and the instruction prefetch is a real bus read (<c>Prefetch</c>) that sits where the hardware puts it, which is what makes the order and timing of the accesses right and not
/// just the total. The prefetch queue is the pair IR / IRC; <see cref="Pc"/> points at the word after IRC (so it is the address of the instruction being executed plus 4).</para>
/// <para><b>Interrupts.</b> <see cref="Step"/> looks at <see cref="IM68kBus.InterruptLevel"/> once, before it starts an instruction, and takes the interrupt if the level beats
/// the mask (or is 7). The board decides when an interrupt request shows on the pins, so the one-instruction latency of the hardware is the board's to model.</para>
/// <para><b>Address errors.</b> A word or long access at an odd address raises the address-error exception (vector 3) when <see cref="AddressErrors"/> is on.</para>
/// <para><b>TAS.</b> <see cref="TasWritesBack"/> is false on the Genesis (models 1 and 2 never complete the write); the vectors assume it is true.</para>
/// </remarks>
public sealed partial class M68k<TBus> where TBus : struct, IM68kBus
{
    public const int Byte = 0, Word = 1, Long = 2;

    private TBus _bus;
    /// <summary>The bus (a struct held by the CPU; the board reads its own state through its reference inside).</summary>
    public ref TBus Bus => ref _bus;

    // ---- registers
    public readonly uint[] D = new uint[8];
    /// <summary>A0-A7; A7 is the active stack pointer (USP in user mode, SSP in supervisor mode).</summary>
    public readonly uint[] A = new uint[8];
    /// <summary>The inactive stack pointer (SSP in user mode, USP in supervisor mode).</summary>
    public uint SpOther;
    /// <summary>Address of the word after IRC: the executing instruction's address plus 4.</summary>
    public uint Pc;

    public bool C, V, Z, N, X;
    public int IntMask;
    public bool Supervisor, Trace;

    /// <summary>The prefetch queue: IRC holds the word fetched ahead, IR the word currently being decoded, IRD the word of the instruction being executed.</summary>
    public ushort Irc, Ir, Ird;
    /// <summary>The instruction word an address-error frame shows (IR as the chip's microcode sees it: the opcode, or the word a prefetch has since moved in).</summary>
    private ushort frameIr;
    public bool Stopped;

    /// <summary>The 68000 clock cycles this CPU has counted since it was created (instruction timing; bus stalls are the board's and are not in it).</summary>
    public long Cycles;
    /// <summary>Instructions and exception sequences started.</summary>
    public long Instructions;

    /// <summary>Word and long accesses at an odd address raise the address-error exception.</summary>
    public bool AddressErrors = true;
    /// <summary>TAS completes its write cycle. False on the Genesis, where the write never happens (some software depends on it).</summary>
    public bool TasWritesBack = true;

    private int pend;   // clocks counted since the last call into the bus

    // An address register the current access is about to update (post-increment, pre-decrement). The register is updated at the start of the access on the chip, so an access that
    // raises an address error leaves it updated; the code here updates it after the access, and the address-error handler applies the pending change.
    private int pendReg = -1;
    private uint pendValue;

    public M68k(TBus bus)
    {
        _bus = bus;
        BuildTable();
    }

    // ================================================================================================================
    // Power-on, reset
    // ================================================================================================================

    /// <summary>Power-on state without touching the bus: registers zero, supervisor, interrupts masked, the queue full of NOPs.</summary>
    public void Power()
    {
        Array.Clear(D); Array.Clear(A);
        SpOther = 0; Pc = 0;
        C = V = Z = N = X = false;
        IntMask = 7; Supervisor = true; Trace = false;
        Irc = Ir = Ird = 0x4E71;
        Stopped = false; Halted = false; traceDue = false; pend = 0; Cycles = 0; Instructions = 0; pendReg = -1;
    }

    /// <summary>The RESET exception as the hardware does it: supervisor mode, interrupts masked, SSP and PC read from vectors 0 and 1, and the two-word prefetch. Uses the bus, so the board's
    /// clock advances through it.</summary>
    public void Reset()
    {
        Stopped = false; Halted = false; traceDue = false;
        Supervisor = true; Trace = false; IntMask = 7;
        pend = 0;
        // two idle clocks of bus preparation are not modelled; the four reads are what matters to the board
        A[7] = ReadMem(Long, 0);
        Pc = ReadMem(Long, 4);
        Prefetch();
        Prefetch();
        Flush();
    }

    // ================================================================================================================
    // Time
    // ================================================================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Idle(int clocks) { pend += clocks; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Wait(int clocks) { pend += clocks; }

    /// <summary>Report the clocks that have passed since the last bus cycle.</summary>
    private void Flush()
    {
        if (pend != 0) { Cycles += pend; _bus.Idle(pend); pend = 0; }
    }

    // ================================================================================================================
    // Flags and status register
    // ================================================================================================================

    public ushort ReadSr() =>
        (ushort)((C ? 1 : 0) | (V ? 2 : 0) | (Z ? 4 : 0) | (N ? 8 : 0) | (X ? 0x10 : 0) | IntMask << 8 | (Supervisor ? 0x2000 : 0) | (Trace ? 0x8000 : 0));

    public byte ReadCcr() => (byte)((C ? 1 : 0) | (V ? 2 : 0) | (Z ? 4 : 0) | (N ? 8 : 0) | (X ? 0x10 : 0));

    public void WriteCcr(uint ccr)
    {
        C = (ccr & 1) != 0; V = (ccr & 2) != 0; Z = (ccr & 4) != 0; N = (ccr & 8) != 0; X = (ccr & 0x10) != 0;
    }

    public void WriteSr(uint sr)
    {
        WriteCcr(sr);
        bool s = (sr & 0x2000) != 0;
        if (Supervisor != s) { (A[7], SpOther) = (SpOther, A[7]); }
        IntMask = (int)(sr >> 8) & 7;
        Supervisor = s;
        Trace = (sr & 0x8000) != 0;
    }

    /// <summary>The user stack pointer, wherever it is held at the moment.</summary>
    public uint Usp { get => Supervisor ? SpOther : A[7]; set { if (Supervisor) SpOther = value; else A[7] = value; } }
    /// <summary>The supervisor stack pointer, wherever it is held at the moment.</summary>
    public uint Ssp { get => Supervisor ? A[7] : SpOther; set { if (Supervisor) A[7] = value; else SpOther = value; } }

    // ================================================================================================================
    // Memory: word-wide bus cycles
    // ================================================================================================================

    /// <summary>Raised from inside an instruction when a word or long access (or a program fetch) hits an odd address; <see cref="Step"/> turns it into the address-error exception.</summary>
    private sealed class AddressErrorSignal : Exception
    {
        public uint Address; public bool Write, Program;
    }

    // Callers pass a 32-bit address; the bus sees the 24-bit word address.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort BusRead(uint address, bool upper, bool lower)
    {
        int c = pend; pend = 0; Cycles += c;
        return _bus.Read(address & 0xFFFFFEu, upper, lower, c);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void BusWrite(uint address, bool upper, bool lower, ushort data)
    {
        int c = pend; pend = 0; Cycles += c;
        _bus.Write(address & 0xFFFFFEu, upper, lower, data, c);
    }

    private void CheckOdd(uint address, bool write, bool program)
    {
        throw new AddressErrorSignal { Address = address, Write = write, Program = program };
    }

    /// <summary>A data read of 1, 2 or 4 bytes (<paramref name="size"/> is <see cref="Byte"/>, <see cref="Word"/> or <see cref="Long"/>).</summary>
    private uint ReadMem(int size, uint address, bool lowWordFirst = false, bool program = false)
    {
        if (size == Byte)
        {
            Wait(4);
            return (address & 1) != 0 ? (byte)BusRead(address, false, true) : (byte)(BusRead(address, true, false) >> 8);
        }
        if (AddressErrors && (address & 1) != 0) CheckOdd(lowWordFirst && size == Long ? address + 2 : address, false, program);
        if (size == Word)
        {
            Wait(4);
            return BusRead(address, true, true);
        }
        if (lowWordFirst)
        {
            Wait(4);
            uint lo = BusRead(address + 2, true, true);
            Wait(4);
            return (uint)BusRead(address, true, true) << 16 | lo;
        }
        Wait(4);
        uint hi = (uint)BusRead(address, true, true) << 16;
        Wait(4);
        return hi | BusRead(address + 2, true, true);
    }

    /// <summary>A data write. <paramref name="reverse"/> writes a long's low word first (the stack pushes do).</summary>
    private void WriteMem(int size, uint address, uint data, bool reverse = false)
    {
        if (size == Byte)
        {
            Wait(4);
            BusWrite(address, (address & 1) == 0, (address & 1) != 0, (ushort)(data << 8 | (byte)data));
            return;
        }
        if (AddressErrors && (address & 1) != 0) CheckOdd(reverse && size == Long ? address + 2 : address, true, false);
        if (size == Word)
        {
            Wait(4);
            BusWrite(address, true, true, (ushort)data);
            return;
        }
        if (!reverse)
        {
            Wait(4); BusWrite(address, true, true, (ushort)(data >> 16));
            Wait(4); BusWrite(address + 2, true, true, (ushort)data);
        }
        else
        {
            Wait(4); BusWrite(address + 2, true, true, (ushort)data);
            Wait(4); BusWrite(address, true, true, (ushort)(data >> 16));
        }
    }

    /// <summary>Fetch an extension word: the queue moves up, the next word comes in from <see cref="Pc"/>, and the word that was in IRC is returned (as the byte, word or long the caller wants).</summary>
    private uint Extension(int size)
    {
        if (size == Long)
        {
            uint hi = Extension(Word);
            uint lo = Extension(Word);
            return hi << 16 | lo;
        }
        // an extension word goes from IRC to the effective-address logic, not into IR: IR keeps the opcode (the address-error frame shows it)
        if (AddressErrors && (Pc & 1) != 0) CheckOdd(Pc, false, true);
        Wait(4);
        ushort word = Irc;
        Irc = BusRead(Pc, true, true);
        Pc += 2;
        return size == Byte ? (byte)word : word;
    }

    /// <summary>True while the prefetch bus cycle (IRC becomes IR) is on the bus: the board can tell that cycle from the data and extension-word cycles of an instruction.</summary>
    public bool InPrefetch;
    /// <summary>The board does not take the interrupt sample at the bus cycles that follow (set by an instruction whose last cycles must not sample; cleared when the next instruction starts). See <see cref="MovemFreezesIpl"/>.</summary>
    public bool IplFrozen;
    /// <summary>Experiment: MOVEM to memory does not sample the interrupt lines at its final prefetch, so an interrupt that arrives while it runs is taken one instruction later (as BlastEm does).</summary>
    public static bool MovemFreezesIpl = false;

    /// <summary>The prefetch bus cycle: IRC becomes IR and the next word is fetched.</summary>
    private ushort Prefetch()
    {
        if (AddressErrors && (Pc & 1) != 0) CheckOdd(Pc, false, true);
        Wait(4);
        Ir = Irc;
        frameIr = Ir;
        InPrefetch = true;
        Irc = BusRead(Pc, true, true);
        InPrefetch = false;
        Pc += 2;
        return Ir;
    }

    /// <summary>Take the word in IRC without reloading it (JMP, JSR, branches, traps: the program counter is about to change, so no fetch is wasted on the old one).</summary>
    private ushort Prefetched()
    {
        ushort word = Irc;
        Irc = 0;
        Pc += 2;
        return word;
    }

    private uint Pop(int size)
    {
        uint data = ReadMem(size, A[7]);
        A[7] += size == Long ? 4u : size == Word ? 2u : 1u;
        return data;
    }

    private void Push(int size, uint data)
    {
        A[7] -= size == Long ? 4u : size == Word ? 2u : 1u;
        WriteMem(size, A[7], data);
    }

    // ================================================================================================================
    // Size helpers
    // ================================================================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Clip(int size, uint v) => size == Byte ? (byte)v : size == Word ? (ushort)v : v;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SignOf(int size, uint v) => size == Byte ? (sbyte)v : size == Word ? (short)v : (int)v;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Msb(int size) => size == Byte ? 0x80u : size == Word ? 0x8000u : 0x80000000u;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Bytes(int size) => size == Byte ? 1 : size == Word ? 2 : 4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Bits(int size) => size == Byte ? 8 : size == Word ? 16 : 32;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteD(int size, int reg, uint data)
    {
        if (size == Byte) D[reg] = D[reg] & ~0xFFu | data & 0xFF;
        else if (size == Word) D[reg] = D[reg] & ~0xFFFFu | data & 0xFFFF;
        else D[reg] = data;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadD(int size, int reg) => Clip(size, D[reg]);

    // An address register read is sign-extended and a write sign-extends too (word operations on An see the whole register).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadA(int size, int reg) => (uint)SignOf(size, A[reg]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteA(int size, int reg, uint data) => A[reg] = (uint)SignOf(size, data);

    // ================================================================================================================
    // Exceptions
    // ================================================================================================================

    public const int VecBusError = 2, VecAddressError = 3, VecIllegal = 4, VecDivideByZero = 5, VecChk = 6, VecTrapV = 7, VecPrivilege = 8, VecTrace = 9, VecLineA = 10, VecLineF = 11,
                     VecSpurious = 24, VecAutoVector = 24, VecTrap = 32;

    /// <summary>
    /// Exception processing for a trap, an illegal or privileged instruction and the like (the vectors fix this sequence): <paramref name="preIdle"/> clocks of internal work (or none, when a
    /// real prefetch cycle has just stood in for them), then the frame (PC low, SR, PC high), the vector's two reads and two prefetches with two idle clocks between them.
    /// The stacked PC is the address of the instruction that follows when the instruction consumed its prefetch word (TRAP and friends), else of the instruction itself.
    /// </summary>
    private void Exception(int vector, int preIdle = 4)
    {
        Stopped = false;
        Idle(preIdle);
        uint pc = Pc - 4;
        uint sr = ReadSr();
        if (!Supervisor) { (A[7], SpOther) = (SpOther, A[7]); }
        Supervisor = true;
        Trace = false;
        PushFrame(pc, sr);
        EnterHandler(vector);
    }

    /// <summary>An interrupt of <paramref name="level"/>: the frame is split around the acknowledge cycle (PC low is on the stack before the vector number is fetched).</summary>
    private void Interrupt(int level)
    {
        Stopped = false;
        Idle(6);
        uint pc = Pc - 4;
        uint sr = ReadSr();
        if (!Supervisor) { (A[7], SpOther) = (SpOther, A[7]); }
        Supervisor = true;
        Trace = false;

        A[7] -= 2;
        WriteMem(Word, A[7], pc & 0xFFFF);

        // the acknowledge cycle: the vector number is fetched (about 14 clocks for an autovector, which the Genesis uses) and justified (4)
        int vector = _bus.Acknowledge(level, pend);
        Cycles += pend; pend = 0;
        Idle(14 + 4);
        IntMask = level;

        A[7] -= 4;
        WriteMem(Word, A[7], sr);
        WriteMem(Word, A[7] + 2, pc >> 16);
        EnterHandler(vector);
    }

    private void PushFrame(uint pc, uint sr)
    {
        A[7] -= 2;
        WriteMem(Word, A[7], pc & 0xFFFF);
        A[7] -= 4;
        WriteMem(Word, A[7], sr);
        WriteMem(Word, A[7] + 2, pc >> 16);
    }

    private void EnterHandler(int vector)
    {
        ExceptionCounts[vector & 0xFF]++;
        Pc = ReadMem(Long, (uint)vector << 2);
        Prefetch();
        Idle(2);
        Prefetch();
    }

    private bool RequireSupervisor()
    {
        if (Supervisor) return true;
        Exception(VecPrivilege);
        return false;
    }

    // ================================================================================================================
    // The instruction loop
    // ================================================================================================================

    /// <summary>
    /// Run one instruction (or take one interrupt, or spend a stopped slice of time). Returns the 68000 clock cycles it counted. Every bus cycle in it has been reported to the bus as
    /// it happened; any trailing internal cycles are reported before this returns.
    /// </summary>
    public int Step()
    {
        long start = Cycles + pend;
        try
        {
            if (Halted)
            {
                Idle(4);
                Flush();
                return (int)(Cycles - start);
            }
            if (traceDue)
            {
                // the instruction that ran with the trace bit set is followed by the trace exception, which comes before any interrupt
                traceDue = false;
                Instructions++;
                Exception(VecTrace, 4);
                Flush();
                return (int)(Cycles - start);
            }
            int level = _bus.InterruptLevel;
            if (level > IntMask || level == 7)
            {
                Instructions++;
                if (Stopped) Pc += 4;   // the stacked return address is the instruction after STOP (two words past it)
                Interrupt(level);
                Flush();
                return (int)(Cycles - start);
            }
            if (Stopped)
            {
                Idle(4);
                Flush();
                return (int)(Cycles - start);
            }
            Instructions++;
            Ird = Ir;
            frameIr = Ir;
            bool traced = Trace;
            IplFrozen = false;
            Execute(Ird);
            traceDue = traced;
        }
        catch (AddressErrorSignal e)
        {
            try { AddressError(e); }
            catch (AddressErrorSignal) { Halted = true; pend = 0; }   // the stack itself is at an odd address: a double fault stops the processor
        }
        Flush();
        return (int)(Cycles - start);
    }

    private bool traceDue;

    /// <summary>The processor stopped by a double fault (an address error while stacking one); only a reset restarts it.</summary>
    public bool Halted;

    /// <summary>
    /// The address-error exception (vector 3), a group 0 exception: the access that hit the odd address is aborted after its four clocks, the 14-byte frame goes on the supervisor stack
    /// (PC low, SR, PC high, the instruction word, the faulting address low and high, and a status word with the access kind and function code), then the vector is read and the
    /// handler's first two words are fetched. 50 clocks in all, plus whatever the instruction had spent.
    /// </summary>
    private void AddressError(AddressErrorSignal e)
    {
        AddressErrorCount++;
        Stopped = false;
        if (pendReg >= 0) { A[pendReg] = pendValue; pendReg = -1; }
        Idle(4);
        uint sr = ReadSr();
        int fc = (Supervisor ? 4 : 0) | (e.Program ? 2 : 1);
        if (!Supervisor) { (A[7], SpOther) = (SpOther, A[7]); }
        Supervisor = true;
        Trace = false;

        uint pc = Pc - 4;
        ushort status = (ushort)((frameIr & 0xFFE0) | (e.Write ? 0 : 0x10) | fc);

        uint sp = A[7];
        A[7] = sp - 14;
        WriteMem(Word, sp - 2, pc & 0xFFFF);
        WriteMem(Word, sp - 6, sr);
        WriteMem(Word, sp - 4, pc >> 16);
        WriteMem(Word, sp - 8, frameIr);
        WriteMem(Word, sp - 10, e.Address & 0xFFFF);
        WriteMem(Word, sp - 14, status);
        WriteMem(Word, sp - 12, e.Address >> 16);
        EnterHandler(VecAddressError);
    }

    /// <summary>Address errors taken.</summary>
    public long AddressErrorCount;

    /// <summary>How many times each exception vector was taken (interrupts included: autovectors 25 to 31), for diagnostics and the whole-library sweep.</summary>
    public readonly long[] ExceptionCounts = new long[256];
}
