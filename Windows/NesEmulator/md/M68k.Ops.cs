// The instructions. Derived from ares' m68000 instructions.cpp (ISC; see M68k.cs): the bus cycles and internal cycles are in the order ares puts them.

using System.Numerics;

namespace NesEmulator.Sega;

public sealed partial class M68k<TBus> where TBus : struct, IM68kBus
{
    private static int SizeField(int op) => (op >> 6) & 3;
    private static M68kEa EaField(int op) => new((op >> 3) & 7, op & 7);

    private static bool IsRegOrImm(int mode) => mode == mDn || mode == mAn || mode == mImm;

    private void Execute(int op)
    {
        switch (Table[op])
        {
            case M68kH.Abcd: Abcd(op); break;
            case M68kH.Sbcd: Sbcd(op); break;
            case M68kH.AddEaDn: ArithEaDn(op, 0); break;
            case M68kH.SubEaDn: ArithEaDn(op, 1); break;
            case M68kH.AndEaDn: ArithEaDn(op, 2); break;
            case M68kH.OrEaDn: ArithEaDn(op, 3); break;
            case M68kH.AddDnEa: ArithDnEa(op, 0); break;
            case M68kH.SubDnEa: ArithDnEa(op, 1); break;
            case M68kH.AndDnEa: ArithDnEa(op, 2); break;
            case M68kH.OrDnEa: ArithDnEa(op, 3); break;
            case M68kH.Eor: Eor(op); break;
            case M68kH.Adda: Adda(op); break;
            case M68kH.Suba: Suba(op); break;
            case M68kH.Addi: ArithImm(op, 0); break;
            case M68kH.Subi: ArithImm(op, 1); break;
            case M68kH.Andi: ArithImm(op, 2); break;
            case M68kH.Ori: ArithImm(op, 3); break;
            case M68kH.Eori: ArithImm(op, 4); break;
            case M68kH.AddqEa: Quick(op, false); break;
            case M68kH.SubqEa: Quick(op, true); break;
            case M68kH.AddqAn: QuickAn(op, false); break;
            case M68kH.SubqAn: QuickAn(op, true); break;
            case M68kH.Addx: Addx(op, false); break;
            case M68kH.Subx: Addx(op, true); break;
            case M68kH.AndiCcr: LogicToCcr(op, 0); break;
            case M68kH.OriCcr: LogicToCcr(op, 1); break;
            case M68kH.EoriCcr: LogicToCcr(op, 2); break;
            case M68kH.AndiSr: LogicToSr(op, 0); break;
            case M68kH.OriSr: LogicToSr(op, 1); break;
            case M68kH.EoriSr: LogicToSr(op, 2); break;
            case M68kH.ShiftReg: ShiftRegister(op); break;
            case M68kH.ShiftEa: ShiftMemory(op); break;
            case M68kH.Bcc: Bcc(op); break;
            case M68kH.Bra: Bra(op); break;
            case M68kH.Bsr: Bsr(op); break;
            case M68kH.BitReg: BitOp(op, true); break;
            case M68kH.BitImm: BitOp(op, false); break;
            case M68kH.Chk: Chk(op); break;
            case M68kH.Clr: Clr(op); break;
            case M68kH.Cmp: Cmp(op); break;
            case M68kH.Cmpa: Cmpa(op); break;
            case M68kH.Cmpi: Cmpi(op); break;
            case M68kH.Cmpm: Cmpm(op); break;
            case M68kH.Dbcc: Dbcc(op); break;
            case M68kH.Divs: Divs(op); break;
            case M68kH.Divu: Divu(op); break;
            case M68kH.ExgDd: ExgDd(op); break;
            case M68kH.ExgAa: ExgAa(op); break;
            case M68kH.ExgDa: ExgDa(op); break;
            case M68kH.Ext: Ext(op); break;
            case M68kH.Jmp: Jmp(op); break;
            case M68kH.Jsr: Jsr(op); break;
            case M68kH.Lea: Lea(op); break;
            case M68kH.Link: Link(op); break;
            case M68kH.Move: Move(op); break;
            case M68kH.Movea: Movea(op); break;
            case M68kH.MovemToMem: MovemToMem(op); break;
            case M68kH.MovemToReg: MovemToReg(op); break;
            case M68kH.MovepToMem: MovepToMem(op); break;
            case M68kH.MovepToReg: MovepToReg(op); break;
            case M68kH.Moveq: Moveq(op); break;
            case M68kH.MoveFromSr: MoveFromSr(op); break;
            case M68kH.MoveToCcr: MoveToCcr(op); break;
            case M68kH.MoveToSr: MoveToSr(op); break;
            case M68kH.MoveFromUsp: MoveFromUsp(op); break;
            case M68kH.MoveToUsp: MoveToUsp(op); break;
            case M68kH.Muls: Muls(op); break;
            case M68kH.Mulu: Mulu(op); break;
            case M68kH.Nbcd: Nbcd(op); break;
            case M68kH.Neg: NegOp(op, false); break;
            case M68kH.Negx: NegOp(op, true); break;
            case M68kH.Nop: Prefetch(); break;
            case M68kH.Not: NotOp(op); break;
            case M68kH.Pea: Pea(op); break;
            case M68kH.Reset: ResetInstruction(); break;
            case M68kH.Rte: Rte(); break;
            case M68kH.Rtr: Rtr(); break;
            case M68kH.Rts: Rts(); break;
            case M68kH.Scc: Scc(op); break;
            case M68kH.Stop: StopInstruction(); break;
            case M68kH.Swap: SwapOp(op); break;
            case M68kH.Tas: Tas(op); break;
            case M68kH.Trap: Prefetched(); Exception(VecTrap + (op & 15), 4); break;
            case M68kH.Trapv: Prefetch(); if (V) Exception(VecTrapV, 0); break;
            case M68kH.Tst: Tst(op); break;
            case M68kH.Unlk: Unlk(op); break;
            default: IllegalInstruction(op); break;
        }
    }

    private void IllegalInstruction(int op)
    {
        int line = op >> 12;
        Exception(line == 0xA ? VecLineA : line == 0xF ? VecLineF : VecIllegal);
    }

    // ---- BCD

    private void Abcd(int op)
    {
        int mode = (op & 8) != 0 ? mPre : mDn;
        var from = new M68kEa(mode, op & 7);
        var with = new M68kEa(mode, (op >> 9) & 7);
        uint source = ReadEa(Byte, ref from);
        uint target = ReadEa(Byte, ref with, hold: true, fast: true);
        uint result = source + target + (X ? 1u : 0u);
        bool c = false, v = false;

        if (((target ^ source ^ result) & 0x10) != 0 || (result & 0x0F) >= 0x0A)
        {
            uint previous = result;
            result += 0x06;
            v |= ((~previous & 0x80) & (result & 0x80)) != 0;
        }

        if (result >= 0xA0)
        {
            uint previous = result;
            result += 0x60;
            c = true;
            v |= ((~previous & 0x80) & (result & 0x80)) != 0;
        }

        Prefetch();
        WriteEa(Byte, ref with, result);
        if (with.Mode == mDn) Idle(2);

        C = c; V = v;
        Z = (byte)result != 0 ? false : Z;
        N = (sbyte)result < 0;
        X = C;
    }

    private void Sbcd(int op)
    {
        int mode = (op & 8) != 0 ? mPre : mDn;
        var from = new M68kEa(mode, op & 7);
        var with = new M68kEa(mode, (op >> 9) & 7);
        uint source = ReadEa(Byte, ref from);
        uint target = ReadEa(Byte, ref with, hold: true, fast: true);
        uint result = target - source - (X ? 1u : 0u);
        bool c = false, v = false;

        bool adjustLo = ((target ^ source ^ result) & 0x10) != 0;
        bool adjustHi = (result & 0x100) != 0;

        if (adjustLo)
        {
            uint previous = result;
            result -= 0x06;
            c = ((~previous & 0x80) & (result & 0x80)) != 0;
            v |= ((previous & 0x80) & (~result & 0x80)) != 0;
        }

        if (adjustHi)
        {
            uint previous = result;
            result -= 0x60;
            c = true;
            v |= ((previous & 0x80) & (~result & 0x80)) != 0;
        }

        Prefetch();
        WriteEa(Byte, ref with, result);
        if (with.Mode == mDn) Idle(2);

        C = c; V = v;
        Z = (byte)result != 0 ? false : Z;
        N = (sbyte)result < 0;
        X = C;
    }

    private void Nbcd(int op)
    {
        var with = EaField(op);
        uint source = ReadEa(Byte, ref with, hold: true);
        uint target = 0;
        uint result = target - source - (X ? 1u : 0u);
        bool c = false, v = false;

        bool adjustLo = ((target ^ source ^ result) & 0x10) != 0;
        bool adjustHi = (result & 0x100) != 0;

        if (adjustLo)
        {
            uint previous = result;
            result -= 0x06;
            c = ((~previous & 0x80) & (result & 0x80)) != 0;
            v |= ((previous & 0x80) & (~result & 0x80)) != 0;
        }

        if (adjustHi)
        {
            uint previous = result;
            result -= 0x60;
            c = true;
            v |= ((previous & 0x80) & (~result & 0x80)) != 0;
        }

        Prefetch();
        WriteEa(Byte, ref with, result);
        if (with.Mode == mDn || with.Mode == mAn) Idle(2);

        C = c; V = v;
        Z = (byte)result != 0 ? false : Z;
        N = (sbyte)result < 0;
        X = C;
    }

    // ---- ADD, SUB, AND, OR, EOR, CMP

    private uint Arith(int kind, int size, uint source, uint target) => kind switch
    {
        0 => AddOp(size, source, target),
        1 => SubOp(size, source, target),
        2 => AndOp(size, source, target),
        3 => OrOp(size, source, target),
        _ => EorOp(size, source, target),
    };

    /// <summary>ADD, SUB, AND, OR: <paramref name="kind"/> 0 to 3. Source from the effective address, destination a data register.</summary>
    private void ArithEaDn(int op, int kind)
    {
        int size = SizeField(op);
        var from = EaField(op);
        int dn = (op >> 9) & 7;
        uint source = ReadEa(size, ref from);
        uint target = ReadD(size, dn);
        uint result = Arith(kind, size, source, target);
        Prefetch();
        WriteD(size, dn, result);
        if (size == Long)
        {
            bool shortForm = kind < 2 ? IsRegOrImm(from.Mode) : (from.Mode == mDn || from.Mode == mImm);
            Idle(shortForm ? 4 : 2);
        }
    }

    private void ArithDnEa(int op, int kind)
    {
        int size = SizeField(op);
        var with = EaField(op);
        uint source = ReadD(size, (op >> 9) & 7);
        uint target = ReadEa(size, ref with, hold: true);
        uint result = Arith(kind, size, source, target);
        Prefetch();
        WriteEa(size, ref with, result, rmw: true);
    }

    private void Eor(int op)
    {
        int size = SizeField(op);
        var with = EaField(op);
        uint source = ReadD(size, (op >> 9) & 7);
        uint target = ReadEa(size, ref with, hold: true);
        uint result = EorOp(size, source, target);
        Prefetch();
        WriteEa(size, ref with, result, rmw: true);
        if (size == Long && with.Mode == mDn) Idle(4);
    }

    private void Adda(int op)
    {
        int size = (op & 0x100) != 0 ? Long : Word;
        var from = EaField(op);
        int an = (op >> 9) & 7;
        uint source = (uint)SignOf(size, ReadEa(size, ref from));
        uint target = A[an];
        Prefetch();
        A[an] = source + target;
        Idle(size != Long || IsRegOrImm(from.Mode) ? 4 : 2);
    }

    private void Suba(int op)
    {
        int size = (op & 0x100) != 0 ? Long : Word;
        var from = EaField(op);
        int an = (op >> 9) & 7;
        uint source = (uint)SignOf(size, ReadEa(size, ref from));
        uint target = A[an];
        Prefetch();
        A[an] = target - source;
        Idle(size != Long || IsRegOrImm(from.Mode) ? 4 : 2);
    }

    /// <summary>ADDI, SUBI, ANDI, ORI, EORI: <paramref name="kind"/> 0 to 4 (the order of <see cref="Arith"/>, EOR last).</summary>
    private void ArithImm(int op, int kind)
    {
        int size = SizeField(op);
        var with = EaField(op);
        uint source = Extension(size);
        uint target = ReadEa(size, ref with, hold: true);
        uint result = Arith(kind, size, source, target);
        Prefetch();
        WriteEa(size, ref with, result, rmw: true);
        if (size == Long && with.Mode == mDn) Idle(4);
    }

    private void Quick(int op, bool sub)
    {
        int size = SizeField(op);
        var with = EaField(op);
        uint source = (uint)((op >> 9) & 7);
        if (source == 0) source = 8;
        uint target = ReadEa(size, ref with, hold: true);
        uint result = sub ? SubOp(size, source, target) : AddOp(size, source, target);
        Prefetch();
        WriteEa(size, ref with, result, rmw: true);
        if (size == Long && with.Mode == mDn) Idle(4);
    }

    private void QuickAn(int op, bool sub)
    {
        int an = op & 7;
        uint imm = (uint)((op >> 9) & 7);
        if (imm == 0) imm = 8;
        uint result = sub ? A[an] - imm : A[an] + imm;
        Prefetch();
        A[an] = result;
        Idle(4);
    }

    private void Addx(int op, bool sub)
    {
        int size = SizeField(op);
        int mode = (op & 8) != 0 ? mPre : mDn;
        var from = new M68kEa(mode, op & 7);
        var with = new M68kEa(mode, (op >> 9) & 7);
        uint source = ReadEa(size, ref from, lowWordFirst: size == Long);
        uint target = ReadEa(size, ref with, hold: true, fast: true, lowWordFirst: size == Long);
        uint result = sub ? SubOp(size, source, target, extend: true) : AddOp(size, source, target, extend: true);
        if (size == Long)
        {
            if (with.Mode == mPre)
            {
                WriteEa(Word, ref with, result);
                Prefetch();
                WriteEa(Word, ref with, result >> 16);
            }
            else
            {
                Prefetch();
                WriteEa(Long, ref with, result);
                Idle(4);
            }
        }
        else
        {
            Prefetch();
            WriteEa(size, ref with, result);
        }
    }

    private void Cmp(int op)
    {
        int size = SizeField(op);
        var from = EaField(op);
        uint source = ReadEa(size, ref from);
        uint target = ReadD(size, (op >> 9) & 7);
        CmpOp(size, source, target);
        Prefetch();
        if (size == Long) Idle(2);
    }

    private void Cmpa(int op)
    {
        int size = (op & 0x100) != 0 ? Long : Word;
        var from = EaField(op);
        uint source = (uint)SignOf(size, ReadEa(size, ref from));
        uint target = A[(op >> 9) & 7];
        CmpOp(Long, source, target);
        Prefetch();
        Idle(2);
    }

    private void Cmpi(int op)
    {
        int size = SizeField(op);
        var with = EaField(op);
        uint source = Extension(size);
        uint target = ReadEa(size, ref with);
        CmpOp(size, source, target);
        Prefetch();
        if (size == Long && with.Mode == mDn) Idle(2);
    }

    private void Cmpm(int op)
    {
        int size = SizeField(op);
        var from = new M68kEa(mPost, op & 7);
        var with = new M68kEa(mPost, (op >> 9) & 7);
        uint source = ReadEa(size, ref from);
        uint target = ReadEa(size, ref with);
        CmpOp(size, source, target);
        Prefetch();
    }

    // ---- CCR / SR logic

    private void LogicToCcr(int op, int kind)
    {
        uint data = Extension(Word);
        uint ccr = ReadCcr();
        WriteCcr(kind == 0 ? ccr & data : kind == 1 ? ccr | data : ccr ^ data);
        Idle(8);
        ReadMem(Word, Pc - 2);
        Prefetch();
    }

    private void LogicToSr(int op, int kind)
    {
        if (!RequireSupervisor()) return;
        uint data = Extension(Word);
        uint sr = ReadSr();
        WriteSr(kind == 0 ? sr & data : kind == 1 ? sr | data : sr ^ data);
        Idle(8);
        ReadMem(Word, Pc - 2);
        Prefetch();
    }

    // ---- shifts and rotates

    private void ShiftRegister(int op)
    {
        int size = SizeField(op);
        int kind = (op >> 3) & 3;
        bool left = (op & 0x100) != 0;
        int dn = op & 7;
        int count = (op & 0x20) != 0 ? (int)(D[(op >> 9) & 7] & 63) : (((op >> 9) & 7) == 0 ? 8 : (op >> 9) & 7);
        Prefetch();
        Idle((size != Long ? 2 : 4) + count * 2);
        uint result = ShiftOp(kind, left, size, ReadD(size, dn), count);
        WriteD(size, dn, result);
    }

    private void ShiftMemory(int op)
    {
        int kind = (op >> 9) & 3;
        bool left = (op & 0x100) != 0;
        var with = EaField(op);
        uint result = ShiftOp(kind, left, Word, ReadEa(Word, ref with, hold: true), 1);
        Prefetch();
        WriteEa(Word, ref with, result);
    }

    // ---- branches

    private void Bcc(int op)
    {
        int displacement = op & 0xFF;
        if (!Condition((op >> 8) & 15))
        {
            Idle(4);
            if (displacement == 0) Prefetch();
        }
        else
        {
            if ((BranchIplMask & 1) != 0) IplFromStart = true;
            Idle(2);
            uint offset = displacement != 0 ? (uint)(sbyte)displacement : (uint)(short)Prefetched() - 2;
            Pc -= 2;
            Pc += offset;
            Prefetch();
        }
        Prefetch();
    }

    private void Bra(int op)
    {
        int displacement = op & 0xFF;
        if ((BranchIplMask & 1) != 0) IplFromStart = true;
        Idle(2);
        uint offset = displacement != 0 ? (uint)(sbyte)displacement : (uint)(short)Prefetched() - 2;
        Pc -= 2;
        Pc += offset;
        Prefetch();
        Prefetch();
    }

    private void Bsr(int op)
    {
        int displacement = op & 0xFF;
        if ((BranchIplMask & 1) != 0) IplFromStart = true;
        Idle(2);
        uint offset = displacement != 0 ? (uint)(sbyte)displacement : (uint)(short)Prefetched() - 2;
        Pc -= 2;
        Push(Long, Pc);
        Pc += offset;
        Prefetch();
        Prefetch();
    }

    // ---- bit operations

    private void BitOp(int op, bool register)
    {
        int type = (op >> 6) & 3;   // 0 BTST, 1 BCHG, 2 BCLR, 3 BSET
        var with = EaField(op);
        int size = with.Mode == mDn ? Long : Byte;
        uint index;
        if (register) index = ReadD(size, (op >> 9) & 7) & (uint)(Bits(size) - 1);
        else index = Extension(Word) & (uint)(Bits(size) - 1);
        uint test = ReadEa(size, ref with, hold: type != 0);
        Z = (test >> (int)index & 1) == 0;
        if (type == 0)
        {
            Prefetch();
            if (size == Long && with.Mode == mDn) Idle(2);
            if (register && size == Byte && with.Mode == mImm) Idle(2);
            return;
        }
        uint mask = 1u << (int)index;
        test = type == 1 ? test ^ mask : type == 2 ? test & ~mask : test | mask;
        Prefetch();
        WriteEa(size, ref with, test);
        if (size == Long && with.Mode == mDn)
            Idle(type == 2 ? (index < 16 ? 4 : 6) : (index < 16 ? 2 : 4));
    }

    private void Chk(int op)
    {
        var maximum = EaField(op);
        uint source = ReadD(Word, (op >> 9) & 7);
        uint target = ReadEa(Word, ref maximum);
        bool negative = (short)source < 0;
        // The microcode branches on the N and V flags of bound - Dn (so on the subtraction's sign bit and its overflow), then on the sign of Dn.
        // Z, V and C come out clear and N is Dn's sign.
        uint difference = target - source;
        bool aboveBound = ((difference & 0x8000) != 0) || (((target ^ source) & (target ^ difference) & 0x8000) != 0);
        Z = false; V = false; C = false; N = negative;
        if (aboveBound)
        {
            Prefetched();
            Exception(VecChk, 8);
            return;
        }
        if (negative)
        {
            Prefetched();
            Exception(VecChk, 10);
            return;
        }
        Idle(6);
        Prefetch();
    }

    private void Clr(int op)
    {
        int size = SizeField(op);
        var with = EaField(op);
        ReadEa(size, ref with, hold: true);
        Prefetch();
        WriteEa(size, ref with, 0, rmw: true);
        if (size == Long && (with.Mode == mDn || with.Mode == mAn)) Idle(2);
        C = false; V = false; Z = true; N = false;
    }
}
