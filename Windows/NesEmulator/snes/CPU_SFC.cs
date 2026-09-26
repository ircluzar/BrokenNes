using System;

namespace NesEmulator.Snes;

/// <summary>
/// WDC 65C816 (Ricoh 5A22 core) - the SNES family's CPU, and the one chip that genuinely descends
/// from the NES: with E=1 it is a 6502-compatible machine, which is what will eventually let this
/// same core sit in a NES board as an ICPU.
///
/// Spec verifier: Windows/Resources/snes-test-roms/cputest (run with Workshop --snestest).
///
/// Timing model: every bus access and internal cycle goes through <see cref="ISnesBus"/>, which
/// charges the region's master clocks. Internal (Io) cycles follow the datasheet's cycle notes;
/// the test ROM does not check cycle counts, so treat those as a first cut to refine against Mesen.
///
/// Emulation-mode quirks implemented deliberately (each is covered by cputest-full):
///  - direct page accesses wrap within the page when E=1 and DL=0, but only for the "old" 6502
///    addressing modes; the new ones ([d], [d],Y, PEI) never wrap;
///  - (d,X) with E=1 reads the pointer's high byte with a page-wrapped +1 even when DL!=0;
///  - the 65816-only stack ops (PEA, PEI, PER, PHD, PLD, PLB, JSL, RTL, JSR (a,X)) run off page 1
///    and only snap S back into page 1 afterwards.
/// </summary>
public sealed class CPU_SFC
{
    public string CoreName => "SFC";
    public string Description => "65C816 - the SNES CPU, built as the SFC family's first member";
    public string Category => "Accuracy";

    private readonly ISnesBus bus;

    // ---- Registers (public so tracers, probes and save states can see them directly) ----
    public ushort A, X, Y, S, D, PC;
    public byte DBR, PBR, P;
    public bool E;

    public bool Stopped { get; private set; }   // STP executed - only reset recovers
    public bool Waiting { get; private set; }   // WAI executed - any interrupt line wakes it
    private bool nmiPending;
    private bool irqLine;
    public long InstructionCount { get; private set; }

    private const byte FC = 0x01, FZ = 0x02, FI = 0x04, FD = 0x08, FX = 0x10, FM = 0x20, FV = 0x40, FN = 0x80;
    private bool M8 => (P & FM) != 0;
    private bool X8 => (P & FX) != 0;
    private bool CF => (P & FC) != 0;

    // Effective address of the current operand. eaWrap = the second byte of a 16-bit access
    // wraps inside bank 0 (direct page, stack relative) instead of carrying into the next bank.
    private uint ea;
    private bool eaWrap;

    public CPU_SFC(ISnesBus bus) { this.bus = bus; }

    // =====================================================================================
    //  Control
    // =====================================================================================

    public void Reset()
    {
        E = true;
        P = FM | FX | FI;
        S = (ushort)(0x0100 | (S & 0xFF));
        D = 0; DBR = 0; PBR = 0;
        X &= 0xFF; Y &= 0xFF;
        Stopped = Waiting = nmiPending = false;
        PC = (ushort)(bus.Read(0xFFFC) | bus.Read(0xFFFD) << 8);
    }

    /// <summary>Edge-triggered: latched until serviced.</summary>
    public void RaiseNmi() => nmiPending = true;

    /// <summary>Level-triggered: the board holds it while its IRQ flag is set.</summary>
    public void SetIrq(bool asserted) => irqLine = asserted;

    /// <summary>Execute one instruction (or service one interrupt, or burn one cycle while halted).</summary>
    public void Step()
    {
        if (Stopped) { Io(); return; }
        if (nmiPending)
        {
            nmiPending = false; Waiting = false;
            Io(); Io();
            Interrupt(0xFFEA, 0xFFFA, software: false);
            return;
        }
        if (irqLine && (P & FI) == 0)
        {
            Waiting = false;
            Io(); Io();
            Interrupt(0xFFEE, 0xFFFE, software: false);
            return;
        }
        if (Waiting)
        {
            // WAI with I set still wakes on IRQ, it just doesn't take the vector.
            if (irqLine) Waiting = false;
            else { Io(); return; }
        }
        InstructionCount++;
        Execute(Fetch());
    }

    private void Interrupt(ushort nativeVector, ushort emulationVector, bool software)
    {
        if (!E) Push(PBR);
        Push((byte)(PC >> 8));
        Push((byte)PC);
        byte p = P;
        if (E) p = software ? (byte)(p | 0x10) : (byte)(p & ~0x10); // the B flag only exists on the stack
        Push(p);
        P = (byte)((P | FI) & ~FD);
        PBR = 0;
        ushort v = E ? emulationVector : nativeVector;
        PC = (ushort)(Rd(v) | Rd((uint)v + 1) << 8);
    }

    // =====================================================================================
    //  Bus helpers
    // =====================================================================================

    private byte Rd(uint a) => bus.Read(a & 0xFFFFFF);
    private void Wr(uint a, byte v) => bus.Write(a & 0xFFFFFF, v);
    private void Io() => bus.Idle();

    private byte Fetch() { byte v = Rd((uint)PBR << 16 | PC); PC++; return v; }
    private ushort Fetch16() { uint lo = Fetch(); return (ushort)(lo | (uint)Fetch() << 8); }
    private uint Fetch24() { uint lo = Fetch(); lo |= (uint)Fetch() << 8; return lo | (uint)Fetch() << 16; }

    /// <summary>Direct-page address with the 6502 page wrap (E=1 and DL=0).</summary>
    private uint Direct(uint offset) =>
        E && (D & 0xFF) == 0 ? ((uint)D & 0xFF00u) | (offset & 0xFF) : (D + offset) & 0xFFFF;

    /// <summary>Direct-page address for the 65816-only modes, which never page-wrap.</summary>
    private uint DirectN(uint offset) => (D + offset) & 0xFFFF;

    private void DirectPenalty() { if ((D & 0xFF) != 0) Io(); }

    // Stack. Push/Pull are the 6502-compatible forms (page 1 in emulation mode); PushN/PullN are the
    // 65816-only forms, which run off the page and are followed by FixStack().
    private void Push(byte v) { Wr(S, v); S = E ? (ushort)(0x0100 | (byte)(S - 1)) : (ushort)(S - 1); }
    private byte Pull() { S = E ? (ushort)(0x0100 | (byte)(S + 1)) : (ushort)(S + 1); return Rd(S); }
    private void PushN(byte v) { Wr(S, v); S--; }
    private byte PullN() { S++; return Rd(S); }
    private void FixStack() { if (E) S = (ushort)(0x0100 | (S & 0xFF)); }

    // =====================================================================================
    //  Flags
    // =====================================================================================

    private void SetFlag(byte f, bool on) { if (on) P |= f; else P = (byte)(P & ~f); }
    private void NZ8(uint v) { SetFlag(FZ, (v & 0xFF) == 0); SetFlag(FN, (v & 0x80) != 0); }
    private void NZ16(uint v) { SetFlag(FZ, (v & 0xFFFF) == 0); SetFlag(FN, (v & 0x8000) != 0); }
    private void NZ(uint v, bool wide) { if (wide) NZ16(v); else NZ8(v); }

    private void SetP(byte v)
    {
        P = v;
        if (E) P |= FM | FX;
        if (X8) { X &= 0xFF; Y &= 0xFF; }
    }

    private void SetA(uint v, bool wide) { A = wide ? (ushort)v : (ushort)(((uint)A & 0xFF00u) | (v & 0xFF)); }
    private ushort GetA(bool wide) => wide ? A : (ushort)(A & 0xFF);
    private ushort IndexVal(uint v) => X8 ? (ushort)(v & 0xFF) : (ushort)v;

    // =====================================================================================
    //  Addressing modes (each leaves the operand address in ea/eaWrap)
    // =====================================================================================

    private void AmDirect() { byte d = Fetch(); DirectPenalty(); ea = Direct(d); eaWrap = true; }
    private void AmDirectX() { byte d = Fetch(); DirectPenalty(); Io(); ea = Direct((uint)d + X); eaWrap = true; }
    private void AmDirectY() { byte d = Fetch(); DirectPenalty(); Io(); ea = Direct((uint)d + Y); eaWrap = true; }
    private void AmAbs() { ea = (uint)DBR << 16 | Fetch16(); eaWrap = false; }
    private void AmLong() { ea = Fetch24(); eaWrap = false; }
    private void AmLongX() { ea = (Fetch24() + X) & 0xFFFFFF; eaWrap = false; }

    private void AmAbsIndexed(ushort index, bool write)
    {
        uint b = (uint)DBR << 16 | Fetch16();
        ea = (b + index) & 0xFFFFFF; eaWrap = false;
        if (write || !X8 || ((b ^ ea) & 0xFF00) != 0) Io();
    }

    private uint ReadPointer16(byte d)  // (d) pointer: old-style mode, page-wraps in emulation
    {
        uint lo = Rd(Direct(d));
        return lo | (uint)Rd(Direct((uint)d + 1)) << 8;
    }

    private void AmIndirect() { byte d = Fetch(); DirectPenalty(); ea = (uint)DBR << 16 | ReadPointer16(d); eaWrap = false; }

    private void AmIndirectX()
    {
        byte d = Fetch(); DirectPenalty(); Io();
        uint p = Direct((uint)d + X);
        uint lo = Rd(p);
        uint p2 = E ? (p & 0xFF00) | ((p + 1) & 0xFF) : (p + 1) & 0xFFFF;
        ea = (uint)DBR << 16 | (uint)Rd(p2) << 8 | lo; eaWrap = false;
    }

    private void AmIndirectY(bool write)
    {
        byte d = Fetch(); DirectPenalty();
        uint b = (uint)DBR << 16 | ReadPointer16(d);
        ea = (b + Y) & 0xFFFFFF; eaWrap = false;
        if (write || !X8 || ((b ^ ea) & 0xFF00) != 0) Io();
    }

    private uint ReadPointer24(byte d)
    {
        uint lo = Rd(DirectN(d));
        lo |= (uint)Rd(DirectN((uint)d + 1)) << 8;
        return lo | (uint)Rd(DirectN((uint)d + 2)) << 16;
    }

    private void AmIndirectLong() { byte d = Fetch(); DirectPenalty(); ea = ReadPointer24(d); eaWrap = false; }
    private void AmIndirectLongY() { byte d = Fetch(); DirectPenalty(); ea = (ReadPointer24(d) + Y) & 0xFFFFFF; eaWrap = false; }
    private void AmStack() { byte o = Fetch(); Io(); ea = (uint)(S + o) & 0xFFFF; eaWrap = true; }

    private void AmStackIndirectY()
    {
        byte o = Fetch(); Io();
        uint p = (uint)(S + o) & 0xFFFF;
        uint lo = Rd(p);
        uint ptr = lo | (uint)Rd((p + 1) & 0xFFFF) << 8;
        Io();
        ea = (((uint)DBR << 16 | ptr) + Y) & 0xFFFFFF; eaWrap = false;
    }

    private uint NextEa(uint a) => eaWrap ? (a & 0xFF0000) | ((a + 1) & 0xFFFF) : (a + 1) & 0xFFFFFF;

    private ushort ReadEa(bool wide)
    {
        uint lo = Rd(ea);
        return wide ? (ushort)(lo | (uint)Rd(NextEa(ea)) << 8) : (ushort)lo;
    }

    private void WriteEa(uint v, bool wide)
    {
        Wr(ea, (byte)v);
        if (wide) Wr(NextEa(ea), (byte)(v >> 8));
    }

    // =====================================================================================
    //  ALU
    // =====================================================================================

    private void Adc(uint data, bool wide)
    {
        int c = CF ? 1 : 0, result;
        bool dec = (P & FD) != 0;
        if (!wide)
        {
            int a = A & 0xFF; data &= 0xFF;
            if (!dec) result = a + (int)data + c;
            else
            {
                result = (a & 0x0F) + ((int)data & 0x0F) + c;
                if (result > 0x09) result += 0x06;
                c = result > 0x0F ? 1 : 0;
                result = (a & 0xF0) + ((int)data & 0xF0) + (c << 4) + (result & 0x0F);
            }
            SetFlag(FV, (~(a ^ (int)data) & (a ^ result) & 0x80) != 0);
            if (dec && result > 0x9F) result += 0x60;
            SetFlag(FC, result > 0xFF);
            NZ8((uint)result);
            SetA((uint)result, false);
        }
        else
        {
            int a = A; data &= 0xFFFF;
            if (!dec) result = a + (int)data + c;
            else
            {
                result = (a & 0x000F) + ((int)data & 0x000F) + c;
                if (result > 0x0009) result += 0x0006;
                c = result > 0x000F ? 1 : 0;
                result = (a & 0x00F0) + ((int)data & 0x00F0) + (c << 4) + (result & 0x000F);
                if (result > 0x009F) result += 0x0060;
                c = result > 0x00FF ? 1 : 0;
                result = (a & 0x0F00) + ((int)data & 0x0F00) + (c << 8) + (result & 0x00FF);
                if (result > 0x09FF) result += 0x0600;
                c = result > 0x0FFF ? 1 : 0;
                result = (a & 0xF000) + ((int)data & 0xF000) + (c << 12) + (result & 0x0FFF);
            }
            SetFlag(FV, (~(a ^ (int)data) & (a ^ result) & 0x8000) != 0);
            if (dec && result > 0x9FFF) result += 0x6000;
            SetFlag(FC, result > 0xFFFF);
            NZ16((uint)result);
            A = (ushort)result;
        }
    }

    private void Sbc(uint data, bool wide)
    {
        int c = CF ? 1 : 0, result;
        bool dec = (P & FD) != 0;
        if (!wide)
        {
            int a = A & 0xFF; int d = (int)(~data & 0xFF);
            if (!dec) result = a + d + c;
            else
            {
                result = (a & 0x0F) + (d & 0x0F) + c;
                if (result <= 0x0F) result -= 0x06;
                c = result > 0x0F ? 1 : 0;
                result = (a & 0xF0) + (d & 0xF0) + (c << 4) + (result & 0x0F);
            }
            SetFlag(FV, (~(a ^ d) & (a ^ result) & 0x80) != 0);
            if (dec && result <= 0xFF) result -= 0x60;
            SetFlag(FC, result > 0xFF);
            NZ8((uint)result);
            SetA((uint)result, false);
        }
        else
        {
            int a = A; int d = (int)(~data & 0xFFFF);
            if (!dec) result = a + d + c;
            else
            {
                result = (a & 0x000F) + (d & 0x000F) + c;
                if (result <= 0x000F) result -= 0x0006;
                c = result > 0x000F ? 1 : 0;
                result = (a & 0x00F0) + (d & 0x00F0) + (c << 4) + (result & 0x000F);
                if (result <= 0x00FF) result -= 0x0060;
                c = result > 0x00FF ? 1 : 0;
                result = (a & 0x0F00) + (d & 0x0F00) + (c << 8) + (result & 0x00FF);
                if (result <= 0x0FFF) result -= 0x0600;
                c = result > 0x0FFF ? 1 : 0;
                result = (a & 0xF000) + (d & 0xF000) + (c << 12) + (result & 0x0FFF);
            }
            SetFlag(FV, (~(a ^ d) & (a ^ result) & 0x8000) != 0);
            if (dec && result <= 0xFFFF) result -= 0x6000;
            SetFlag(FC, result > 0xFFFF);
            NZ16((uint)result);
            A = (ushort)result;
        }
    }

    private void Compare(uint reg, uint data, bool wide)
    {
        uint mask = wide ? 0xFFFFu : 0xFFu;
        reg &= mask; data &= mask;
        SetFlag(FC, reg >= data);
        NZ((reg - data) & mask, wide);
    }

    private enum Rmw { Asl, Rol, Lsr, Ror, Inc, Dec, Tsb, Trb }

    private uint Modify(Rmw op, uint v, bool wide)
    {
        uint top = wide ? 0x8000u : 0x80u, mask = wide ? 0xFFFFu : 0xFFu;
        switch (op)
        {
            case Rmw.Asl: SetFlag(FC, (v & top) != 0); v = (v << 1) & mask; break;
            case Rmw.Lsr: SetFlag(FC, (v & 1) != 0); v >>= 1; break;
            case Rmw.Rol: { uint c = CF ? 1u : 0u; SetFlag(FC, (v & top) != 0); v = ((v << 1) | c) & mask; break; }
            case Rmw.Ror: { bool c = CF; SetFlag(FC, (v & 1) != 0); v = (v >> 1) | (c ? top : 0); break; }
            case Rmw.Inc: v = (v + 1) & mask; break;
            case Rmw.Dec: v = (v - 1) & mask; break;
            case Rmw.Tsb: SetFlag(FZ, (v & GetA(wide)) == 0); return (v | GetA(wide)) & mask;
            case Rmw.Trb: SetFlag(FZ, (v & GetA(wide)) == 0); return v & ~(uint)GetA(wide) & mask;
        }
        NZ(v, wide);
        return v;
    }

    /// <summary>Read-modify-write on memory at ea. 16-bit writes go high byte first, like hardware.</summary>
    private void ModifyMemory(Rmw op)
    {
        bool wide = !M8;
        uint v = ReadEa(wide);
        Io();
        v = Modify(op, v, wide);
        if (wide) Wr(NextEa(ea), (byte)(v >> 8));
        Wr(ea, (byte)v);
    }

    private void ModifyA(Rmw op) { Io(); bool wide = !M8; SetA(Modify(op, GetA(wide), wide), wide); }

    private void Branch(bool cond)
    {
        sbyte off = (sbyte)Fetch();
        if (!cond) return;
        Io();
        ushort target = (ushort)(PC + off);
        if (E && ((target ^ PC) & 0xFF00) != 0) Io();
        PC = target;
    }

    // =====================================================================================
    //  Decode
    // =====================================================================================

    /// <summary>
    /// The eight "group 1" instructions (ORA AND EOR ADC STA LDA CMP SBC) share one addressing-mode
    /// layout across 120 opcodes: xxxbbb01, xxxbbb11 (minus bbb=010/110) and xxx10010.
    /// </summary>
    private static bool IsGroup1(byte op) =>
        (op & 3) == 1 || ((op & 3) == 3 && ((op >> 2) & 7) is not (2 or 6)) || (op & 0x1F) == 0x12;

    private void Group1(byte op)
    {
        int aaa = op >> 5;
        bool store = aaa == 4, wide = !M8;
        int cc = op & 3, bbb = (op >> 2) & 7;
        uint val;
        if (cc == 1 && bbb == 2) val = wide ? Fetch16() : Fetch();
        else
        {
            if (cc == 1)
            {
                switch (bbb)
                {
                    case 0: AmIndirectX(); break;
                    case 1: AmDirect(); break;
                    case 3: AmAbs(); break;
                    case 4: AmIndirectY(store); break;
                    case 5: AmDirectX(); break;
                    case 6: AmAbsIndexed(Y, store); break;
                    default: AmAbsIndexed(X, store); break;
                }
            }
            else if (cc == 3)
            {
                switch (bbb)
                {
                    case 0: AmStack(); break;
                    case 1: AmIndirectLong(); break;
                    case 3: AmLong(); break;
                    case 4: AmStackIndirectY(); break;
                    case 5: AmIndirectLongY(); break;
                    default: AmLongX(); break;
                }
            }
            else AmIndirect();

            if (store) { WriteEa(A, wide); return; }
            val = ReadEa(wide);
        }

        switch (aaa)
        {
            case 0: SetA(GetA(wide) | val, wide); NZ(GetA(wide), wide); break;        // ORA
            case 1: SetA(GetA(wide) & val, wide); NZ(GetA(wide), wide); break;        // AND
            case 2: SetA(GetA(wide) ^ val, wide); NZ(GetA(wide), wide); break;        // EOR
            case 3: Adc(val, wide); break;                                           // ADC
            case 5: SetA(val, wide); NZ(val, wide); break;                          // LDA
            case 6: Compare(A, val, wide); break;                                    // CMP
            case 7: Sbc(val, wide); break;                                           // SBC
        }
    }

    private void LoadIndex(ref ushort reg) { bool w = !X8; uint v = ReadEa(w); reg = (ushort)v; NZ(v, w); }
    private void LoadIndexImm(ref ushort reg) { bool w = !X8; uint v = w ? Fetch16() : Fetch(); reg = (ushort)v; NZ(v, w); }
    private void CompareIndex(ushort reg, bool immediate) { bool w = !X8; uint v = immediate ? (w ? Fetch16() : Fetch()) : ReadEa(w); Compare(reg, v, w); }

    private void Bit(uint v, bool wide, bool immediate)
    {
        SetFlag(FZ, (v & GetA(wide)) == 0);
        if (immediate) return;
        uint top = wide ? 0x8000u : 0x80u;
        SetFlag(FN, (v & top) != 0);
        SetFlag(FV, (v & (top >> 1)) != 0);
    }

    private void BlockMove(int step)
    {
        byte dstBank = Fetch(), srcBank = Fetch();
        DBR = dstBank;
        byte v = Rd((uint)srcBank << 16 | X);
        Wr((uint)dstBank << 16 | Y, v);
        Io(); Io();
        if (X8) { X = (byte)(X + step); Y = (byte)(Y + step); }
        else { X = (ushort)(X + step); Y = (ushort)(Y + step); }
        if (A-- != 0) PC -= 3;  // re-execute until A wraps from 0 to $FFFF
    }

    private void Execute(byte op)
    {
        if (op == 0x89) { bool w = !M8; Bit(w ? Fetch16() : Fetch(), w, immediate: true); return; }
        if (IsGroup1(op)) { Group1(op); return; }

        switch (op)
        {
            // ---- Read-modify-write ----
            case 0x0A: ModifyA(Rmw.Asl); break;
            case 0x06: AmDirect(); ModifyMemory(Rmw.Asl); break;
            case 0x0E: AmAbs(); ModifyMemory(Rmw.Asl); break;
            case 0x16: AmDirectX(); ModifyMemory(Rmw.Asl); break;
            case 0x1E: AmAbsIndexed(X, true); ModifyMemory(Rmw.Asl); break;
            case 0x2A: ModifyA(Rmw.Rol); break;
            case 0x26: AmDirect(); ModifyMemory(Rmw.Rol); break;
            case 0x2E: AmAbs(); ModifyMemory(Rmw.Rol); break;
            case 0x36: AmDirectX(); ModifyMemory(Rmw.Rol); break;
            case 0x3E: AmAbsIndexed(X, true); ModifyMemory(Rmw.Rol); break;
            case 0x4A: ModifyA(Rmw.Lsr); break;
            case 0x46: AmDirect(); ModifyMemory(Rmw.Lsr); break;
            case 0x4E: AmAbs(); ModifyMemory(Rmw.Lsr); break;
            case 0x56: AmDirectX(); ModifyMemory(Rmw.Lsr); break;
            case 0x5E: AmAbsIndexed(X, true); ModifyMemory(Rmw.Lsr); break;
            case 0x6A: ModifyA(Rmw.Ror); break;
            case 0x66: AmDirect(); ModifyMemory(Rmw.Ror); break;
            case 0x6E: AmAbs(); ModifyMemory(Rmw.Ror); break;
            case 0x76: AmDirectX(); ModifyMemory(Rmw.Ror); break;
            case 0x7E: AmAbsIndexed(X, true); ModifyMemory(Rmw.Ror); break;
            case 0x1A: ModifyA(Rmw.Inc); break;
            case 0xE6: AmDirect(); ModifyMemory(Rmw.Inc); break;
            case 0xEE: AmAbs(); ModifyMemory(Rmw.Inc); break;
            case 0xF6: AmDirectX(); ModifyMemory(Rmw.Inc); break;
            case 0xFE: AmAbsIndexed(X, true); ModifyMemory(Rmw.Inc); break;
            case 0x3A: ModifyA(Rmw.Dec); break;
            case 0xC6: AmDirect(); ModifyMemory(Rmw.Dec); break;
            case 0xCE: AmAbs(); ModifyMemory(Rmw.Dec); break;
            case 0xD6: AmDirectX(); ModifyMemory(Rmw.Dec); break;
            case 0xDE: AmAbsIndexed(X, true); ModifyMemory(Rmw.Dec); break;
            case 0x04: AmDirect(); ModifyMemory(Rmw.Tsb); break;
            case 0x0C: AmAbs(); ModifyMemory(Rmw.Tsb); break;
            case 0x14: AmDirect(); ModifyMemory(Rmw.Trb); break;
            case 0x1C: AmAbs(); ModifyMemory(Rmw.Trb); break;

            // ---- Index loads / stores / compares ----
            case 0xA2: LoadIndexImm(ref X); break;
            case 0xA6: AmDirect(); LoadIndex(ref X); break;
            case 0xAE: AmAbs(); LoadIndex(ref X); break;
            case 0xB6: AmDirectY(); LoadIndex(ref X); break;
            case 0xBE: AmAbsIndexed(Y, false); LoadIndex(ref X); break;
            case 0xA0: LoadIndexImm(ref Y); break;
            case 0xA4: AmDirect(); LoadIndex(ref Y); break;
            case 0xAC: AmAbs(); LoadIndex(ref Y); break;
            case 0xB4: AmDirectX(); LoadIndex(ref Y); break;
            case 0xBC: AmAbsIndexed(X, false); LoadIndex(ref Y); break;
            case 0x86: AmDirect(); WriteEa(X, !X8); break;
            case 0x8E: AmAbs(); WriteEa(X, !X8); break;
            case 0x96: AmDirectY(); WriteEa(X, !X8); break;
            case 0x84: AmDirect(); WriteEa(Y, !X8); break;
            case 0x8C: AmAbs(); WriteEa(Y, !X8); break;
            case 0x94: AmDirectX(); WriteEa(Y, !X8); break;
            case 0x64: AmDirect(); WriteEa(0, !M8); break;
            case 0x74: AmDirectX(); WriteEa(0, !M8); break;
            case 0x9C: AmAbs(); WriteEa(0, !M8); break;
            case 0x9E: AmAbsIndexed(X, true); WriteEa(0, !M8); break;
            case 0xE0: CompareIndex(X, true); break;
            case 0xE4: AmDirect(); CompareIndex(X, false); break;
            case 0xEC: AmAbs(); CompareIndex(X, false); break;
            case 0xC0: CompareIndex(Y, true); break;
            case 0xC4: AmDirect(); CompareIndex(Y, false); break;
            case 0xCC: AmAbs(); CompareIndex(Y, false); break;
            case 0x24: AmDirect(); Bit(ReadEa(!M8), !M8, false); break;
            case 0x2C: AmAbs(); Bit(ReadEa(!M8), !M8, false); break;
            case 0x34: AmDirectX(); Bit(ReadEa(!M8), !M8, false); break;
            case 0x3C: AmAbsIndexed(X, false); Bit(ReadEa(!M8), !M8, false); break;

            // ---- Branches ----
            case 0x10: Branch((P & FN) == 0); break;
            case 0x30: Branch((P & FN) != 0); break;
            case 0x50: Branch((P & FV) == 0); break;
            case 0x70: Branch((P & FV) != 0); break;
            case 0x90: Branch(!CF); break;
            case 0xB0: Branch(CF); break;
            case 0xD0: Branch((P & FZ) == 0); break;
            case 0xF0: Branch((P & FZ) != 0); break;
            case 0x80: Branch(true); break;
            case 0x82: { ushort rel = Fetch16(); Io(); PC = (ushort)(PC + rel); break; }   // BRL

            // ---- Jumps / calls / returns ----
            case 0x4C: PC = Fetch16(); break;
            case 0x5C: { ushort a = Fetch16(); PBR = Fetch(); PC = a; break; }            // JML long
            case 0x6C: { ushort a = Fetch16(); PC = (ushort)(Rd(a) | Rd((uint)(ushort)(a + 1)) << 8); break; }
            case 0x7C:
            {
                ushort a = Fetch16(); Io();
                ushort p = (ushort)(a + X); uint bank = (uint)PBR << 16;
                PC = (ushort)(Rd(bank | p) | Rd(bank | (ushort)(p + 1)) << 8);
                break;
            }
            case 0xDC:
            {
                ushort a = Fetch16();
                uint lo = Rd(a), hi = Rd((ushort)(a + 1));
                PBR = Rd((ushort)(a + 2));
                PC = (ushort)(lo | hi << 8);
                break;
            }
            case 0x20:
            {
                ushort a = Fetch16(); Io();
                ushort ret = (ushort)(PC - 1);
                Push((byte)(ret >> 8)); Push((byte)ret);
                PC = a;
                break;
            }
            case 0x22:
            {
                uint lo = Fetch(), hi = Fetch();
                PushN(PBR); Io();
                byte bank = Fetch();
                ushort ret = (ushort)(PC - 1);
                PushN((byte)(ret >> 8)); PushN((byte)ret);
                PC = (ushort)(lo | hi << 8); PBR = bank;
                FixStack();
                break;
            }
            case 0xFC:
            {
                uint lo = Fetch();
                PushN((byte)(PC >> 8)); PushN((byte)PC);
                uint hi = Fetch(); Io();
                ushort p = (ushort)((lo | hi << 8) + X); uint bank = (uint)PBR << 16;
                PC = (ushort)(Rd(bank | p) | Rd(bank | (ushort)(p + 1)) << 8);
                FixStack();
                break;
            }
            case 0x60: { Io(); Io(); uint lo = Pull(); uint hi = Pull(); Io(); PC = (ushort)((lo | hi << 8) + 1); break; }
            case 0x6B:
            {
                Io(); Io();
                uint lo = PullN(), hi = PullN(); PBR = PullN();
                PC = (ushort)((lo | hi << 8) + 1);
                FixStack();
                break;
            }
            case 0x40:
            {
                Io(); Io();
                SetP(Pull());
                uint lo = Pull(), hi = Pull();
                PC = (ushort)(lo | hi << 8);
                if (!E) PBR = Pull();
                break;
            }
            case 0x00: Fetch(); Interrupt(0xFFE6, 0xFFFE, software: true); break;  // BRK
            case 0x02: Fetch(); Interrupt(0xFFE4, 0xFFF4, software: true); break;  // COP

            // ---- Flags ----
            case 0x18: Io(); SetFlag(FC, false); break;
            case 0x38: Io(); SetFlag(FC, true); break;
            case 0x58: Io(); SetFlag(FI, false); break;
            case 0x78: Io(); SetFlag(FI, true); break;
            case 0xB8: Io(); SetFlag(FV, false); break;
            case 0xD8: Io(); SetFlag(FD, false); break;
            case 0xF8: Io(); SetFlag(FD, true); break;
            case 0xC2: { byte v = Fetch(); Io(); SetP((byte)(P & ~v)); break; }
            case 0xE2: { byte v = Fetch(); Io(); SetP((byte)(P | v)); break; }
            case 0xFB:
            {
                Io();
                bool c = CF;
                SetFlag(FC, E);
                E = c;
                if (E) { P |= FM | FX; X &= 0xFF; Y &= 0xFF; S = (ushort)(0x0100 | (S & 0xFF)); }
                break;
            }

            // ---- Transfers ----
            case 0xAA: Io(); X = IndexVal(A); NZ(X, !X8); break;
            case 0xA8: Io(); Y = IndexVal(A); NZ(Y, !X8); break;
            case 0x8A: Io(); SetA(X, !M8); NZ(GetA(!M8), !M8); break;
            case 0x98: Io(); SetA(Y, !M8); NZ(GetA(!M8), !M8); break;
            case 0x9B: Io(); Y = IndexVal(X); NZ(Y, !X8); break;
            case 0xBB: Io(); X = IndexVal(Y); NZ(X, !X8); break;
            case 0x9A: Io(); S = E ? (ushort)(0x0100 | (X & 0xFF)) : X; break;
            case 0xBA: Io(); X = IndexVal(S); NZ(X, !X8); break;
            case 0x1B: Io(); S = E ? (ushort)(0x0100 | (A & 0xFF)) : A; break;        // TCS
            case 0x3B: Io(); A = S; NZ16(A); break;                                     // TSC
            case 0x5B: Io(); D = A; NZ16(D); break;                                     // TCD
            case 0x7B: Io(); A = D; NZ16(A); break;                                     // TDC
            case 0xEB: Io(); Io(); A = (ushort)(A >> 8 | A << 8); NZ8(A); break;       // XBA

            // ---- Stack ----
            case 0x48: Io(); if (!M8) Push((byte)(A >> 8)); Push((byte)A); break;
            case 0xDA: Io(); if (!X8) Push((byte)(X >> 8)); Push((byte)X); break;
            case 0x5A: Io(); if (!X8) Push((byte)(Y >> 8)); Push((byte)Y); break;
            case 0x68: { Io(); Io(); bool w = !M8; uint v = Pull(); if (w) v |= (uint)Pull() << 8; SetA(v, w); NZ(v, w); break; }
            case 0xFA: { Io(); Io(); bool w = !X8; uint v = Pull(); if (w) v |= (uint)Pull() << 8; X = (ushort)v; NZ(v, w); break; }
            case 0x7A: { Io(); Io(); bool w = !X8; uint v = Pull(); if (w) v |= (uint)Pull() << 8; Y = (ushort)v; NZ(v, w); break; }
            case 0x08: Io(); Push(P); break;
            case 0x28: Io(); Io(); SetP(Pull()); break;
            case 0x8B: Io(); Push(DBR); break;
            case 0x4B: Io(); Push(PBR); break;
            case 0xAB: Io(); Io(); DBR = PullN(); NZ8(DBR); FixStack(); break;
            case 0x0B: Io(); PushN((byte)(D >> 8)); PushN((byte)D); FixStack(); break;
            case 0x2B: { Io(); Io(); uint lo = PullN(); D = (ushort)(lo | (uint)PullN() << 8); NZ16(D); FixStack(); break; }
            case 0xF4: { ushort v = Fetch16(); PushN((byte)(v >> 8)); PushN((byte)v); FixStack(); break; }       // PEA
            case 0xD4:                                                                                            // PEI
            {
                byte d = Fetch(); DirectPenalty();
                uint lo = Rd(DirectN(d)), hi = Rd(DirectN((uint)d + 1));
                PushN((byte)hi); PushN((byte)lo); FixStack();
                break;
            }
            case 0x62: { ushort rel = Fetch16(); Io(); ushort v = (ushort)(PC + rel); PushN((byte)(v >> 8)); PushN((byte)v); FixStack(); break; } // PER

            // ---- Register increments ----
            case 0xE8: Io(); X = IndexVal((uint)X + 1); NZ(X, !X8); break;
            case 0xC8: Io(); Y = IndexVal((uint)Y + 1); NZ(Y, !X8); break;
            case 0xCA: Io(); X = IndexVal((uint)X - 1); NZ(X, !X8); break;
            case 0x88: Io(); Y = IndexVal((uint)Y - 1); NZ(Y, !X8); break;

            // ---- Misc ----
            case 0xEA: Io(); break;                          // NOP
            case 0x42: Fetch(); break;                       // WDM (reserved 2-byte NOP)
            case 0x54: BlockMove(+1); break;                 // MVN
            case 0x44: BlockMove(-1); break;                 // MVP
            case 0xDB: Io(); Io(); Stopped = true; break;    // STP
            case 0xCB: Io(); Io(); Waiting = true; break;    // WAI

            default: throw new InvalidOperationException($"CPU_SFC: unhandled opcode ${op:X2}"); // unreachable: all 256 are decoded
        }
    }
}
