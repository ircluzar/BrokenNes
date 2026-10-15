// The instructions, second half. Derived from ares' m68000 instructions.cpp (ISC; see M68k.cs).

using System.Numerics;

namespace NesEmulator.Sega;

public sealed partial class M68k<TBus> where TBus : struct, IM68kBus
{
    private void Dbcc(int op)
    {
        int dn = op & 7;
        Idle(2);
        Pc -= 2;
        if (!Condition((op >> 8) & 15))
        {
            uint disp = (uint)(short)Irc;
            Pc += disp;
            Prefetch();
            ushort result = (ushort)ReadD(Word, dn);
            WriteD(Word, dn, (uint)(result - 1));
            if (result != 0)
            {
                Prefetch();
                return;
            }
            Pc -= disp;
        }
        else
        {
            Idle(2);
            Pc += 2;
        }
        Prefetch();
        Prefetch();
    }

    private void Divs(int op)
    {
        var from = EaField(op);
        int dn = (op >> 9) & 7;
        uint dividend = D[dn], odividend = dividend;
        uint divisor = ReadEa(Word, ref from) << 16, odivisor = divisor;

        if (divisor == 0)
        {
            Idle(4);
            Prefetched();
            Exception(VecDivideByZero, 4);
            return;
        }

        if ((divisor >> 31) != 0) divisor = (uint)-(int)divisor;

        if ((dividend >> 31) != 0)
        {
            dividend = (uint)-(int)dividend;
            Idle(2);
        }

        C = false;
        V = dividend >= divisor;
        if (V)
        {
            Z = false; N = true;
            Idle(12);
            Prefetch();
            return;
        }

        ushort quotient = 0;
        bool carry = false;
        int ticks = 12 + 8;
        for (int index = 0; index < 15; index++)
        {
            dividend <<= 1;
            quotient = (ushort)(quotient << 1 | (carry ? 1 : 0));
            carry = dividend >= divisor;
            if (carry) dividend -= divisor;
            ticks += !carry ? 8 : 6;
        }
        quotient = (ushort)(quotient << 1 | (carry ? 1 : 0));
        dividend <<= 1;
        carry = dividend >= divisor;
        if (carry) dividend -= divisor;
        quotient = (ushort)(quotient << 1 | (carry ? 1 : 0));
        ticks += 4;

        if ((odivisor >> 31) != 0)
        {
            ticks += 4;
            if ((odividend >> 31) != 0)
            {
                if ((quotient >> 15) != 0) V = true;
                dividend = (uint)-(int)dividend;
            }
            else
            {
                quotient = (ushort)-quotient;
                if (quotient != 0 && (quotient >> 15) == 0) V = true;
            }
        }
        else if ((odividend >> 31) != 0)
        {
            ticks += 6;
            quotient = (ushort)-quotient;
            if (quotient != 0 && (quotient >> 15) == 0) V = true;
            dividend = (uint)-(int)dividend;
        }
        else
        {
            ticks += 2;
            if ((quotient >> 15) != 0) V = true;
        }

        if (V)
        {
            Z = false; N = true;
            Idle(ticks);
            Prefetch();
            return;
        }

        Z = quotient == 0;
        N = (short)quotient < 0;

        Idle(ticks);
        D[dn] = dividend | quotient;
        Prefetch();
    }

    private void Divu(int op)
    {
        var from = EaField(op);
        int dn = (op >> 9) & 7;
        uint dividend = D[dn];
        uint divisor = ReadEa(Word, ref from) << 16;

        if (divisor == 0)
        {
            Idle(4);
            Prefetched();
            Exception(VecDivideByZero, 4);
            return;
        }

        C = false;
        V = dividend >= divisor;
        if (V)
        {
            Z = false; N = true;
            Idle(6);
            Prefetch();
            return;
        }

        int ticks = 6;
        ushort quotient = 0;
        bool carry;
        bool force = (dividend >> 31) != 0;
        dividend <<= 1;
        for (int index = 0; index < 15; index++)
        {
            carry = force || dividend >= divisor;
            if (carry) dividend -= divisor;
            ticks += !carry ? 8 : !force ? 6 : 4;
            force = (dividend >> 31) != 0;
            dividend <<= 1;
            quotient = (ushort)(quotient << 1 | (carry ? 1 : 0));
        }

        carry = force || dividend >= divisor;
        if (carry) dividend -= divisor;
        quotient = (ushort)(quotient << 1 | (carry ? 1 : 0));
        Z = quotient == 0;
        N = (short)quotient < 0;

        Idle(ticks + 6);
        D[dn] = dividend | quotient;
        Prefetch();
    }

    private void ExgDd(int op)
    {
        int x = (op >> 9) & 7, y = op & 7;
        uint z = D[x];
        D[x] = D[y];
        D[y] = z;
        Prefetch();
        Idle(2);
    }

    private void ExgAa(int op)
    {
        int x = (op >> 9) & 7, y = op & 7;
        uint z = A[x];
        Prefetch();
        A[x] = A[y];
        A[y] = z;
        Idle(2);
    }

    private void ExgDa(int op)
    {
        int x = (op >> 9) & 7, y = op & 7;
        uint z = D[x];
        Prefetch();
        D[x] = A[y];
        A[y] = z;
        Idle(2);
    }

    private void Ext(int op)
    {
        int dn = op & 7;
        if ((op & 0x40) == 0)
        {
            uint result = (uint)(sbyte)D[dn];
            Prefetch();
            WriteD(Word, dn, result);
            C = false; V = false;
            Z = (ushort)result == 0;
            N = (short)result < 0;
        }
        else
        {
            uint result = (uint)(short)D[dn];
            Prefetch();
            D[dn] = result;
            C = false; V = false;
            Z = result == 0;
            N = (int)result < 0;
        }
    }

    private void Jmp(int op)
    {
        var from = EaField(op);
        Pc = PrefetchedAddress(ref from);
        Prefetch();
        Prefetch();
    }

    private void Jsr(int op)
    {
        var from = EaField(op);
        uint target = PrefetchedAddress(ref from);
        uint pc = Pc;
        Pc = target;
        Prefetch();
        Push(Long, pc - 2);
        Prefetch();
    }

    private void Lea(int op)
    {
        var from = EaField(op);
        A[(op >> 9) & 7] = FetchEa(Long, ref from);
        if (from.Mode == mIdx || from.Mode == mPcIdx) Idle(2);
        Prefetch();
    }

    private void Link(int op)
    {
        int an = op & 7;
        int displacement = (short)Extension(Word);
        Push(Long, A[an]);
        A[an] = A[7];
        A[7] = A[7] + (uint)displacement;
        Prefetch();
    }

    private void Unlk(int op)
    {
        int an = op & 7;
        uint address = A[an];
        uint value = ReadMem(Long, address);     // nothing changes if this read raises an address error
        A[7] = address + 4;
        A[an] = value;
        Prefetch();
    }

    // ---- MOVE family

    private static int MoveSize(int op) => ((op >> 12) & 3) switch { 1 => Byte, 3 => Word, _ => Long };

    private void SetLogicFlags(int size, uint data)
    {
        C = false; V = false;
        Z = Clip(size, data) == 0;
        N = SignOf(size, data) < 0;
    }

    private void Move(int op)
    {
        int size = MoveSize(op);
        var from = EaField(op);
        var to = new M68kEa((op >> 6) & 7, (op >> 9) & 7);
        uint data = ReadEa(size, ref from);
        // the flags are set before the write goes out (so they are already set when the write raises an address error; the chip's long MOVEs to (An) and (An)+ differ slightly, see the ledger)
        const bool flagsFirst = true;
        SetLogicFlags(size, data);

        if (to.Mode == mAbsL)
        {
            // An absolute-long destination takes its two address words from the program stream around the write. After a memory read the write goes out as soon as the high word has
            // been fetched and the low word is still in IRC (two fetches follow it); after a register or immediate source both words are fetched first (one follows).
            uint hi = Extension(Word);
            bool memorySource = from.Mode != mDn && from.Mode != mAn && from.Mode != mImm;
            if (memorySource)
            {
                WriteMem(size, hi << 16 | Irc, data);
                if (!flagsFirst) SetLogicFlags(size, data);
                Prefetch();
                Prefetch();
            }
            else
            {
                uint lo = Extension(Word);
                WriteMem(size, hi << 16 | lo, data);
                if (!flagsFirst) SetLogicFlags(size, data);
                Prefetch();
            }
            return;
        }

        if (to.Mode == mPre)
        {
            Prefetch();
            if (size == Long) frameIr = Ird;     // (a long move's write error still shows the opcode in the frame)
            WriteEa(size, ref to, data);
            if (!flagsFirst) SetLogicFlags(size, data);
        }
        else
        {
            WriteEa(size, ref to, data);
            if (!flagsFirst) SetLogicFlags(size, data);
            Prefetch();
        }
    }

    private void Movea(int op)
    {
        int size = MoveSize(op);
        var from = EaField(op);
        uint data = (uint)SignOf(size, ReadEa(size, ref from));
        A[(op >> 9) & 7] = data;
        Prefetch();
    }

    private void Moveq(int op)
    {
        uint imm = (uint)(op & 0xFF);
        D[(op >> 9) & 7] = (uint)(sbyte)imm;
        C = false; V = false;
        Z = imm == 0;
        N = (sbyte)imm < 0;
        Prefetch();
    }

    private void MovemToMem(int op)
    {
        int size = (op & 0x40) != 0 ? Long : Word;
        var to = EaField(op);
        uint list = Extension(Word);
        uint addr = FetchEa(Long, ref to);
        bool pre = to.Mode == mPre;
        IplFrozen = MovemFreezesIpl;       // the oracle takes an interrupt that arrives during the register writes one instruction later (inthist.py): the lines are not sampled from here to the end of the instruction

        for (int n = 0; n < 16; n++)
        {
            if ((list >> n & 1) == 0) continue;
            int index = pre ? 15 - n : n;

            if (pre) addr -= (uint)Bytes(size);
            uint data = index < 8 ? D[index] : A[index - 8];
            if (pre) WriteMem(size, addr, data, reverse: true);
            else WriteMem(size, addr, data);
            if (!pre) addr += (uint)Bytes(size);
        }

        if (pre) A[to.Reg] = addr;
        Prefetch();
    }

    private void MovemToReg(int op)
    {
        int size = (op & 0x40) != 0 ? Long : Word;
        var from = EaField(op);
        uint list = Extension(Word);
        uint addr = FetchEa(Long, ref from);
        bool pcRelative = from.Mode == mPcDisp || from.Mode == mPcIdx;

        for (int n = 0; n < 16; n++)
        {
            if ((list >> n & 1) == 0) continue;
            uint data = (uint)SignOf(size, ReadMem(size, addr, program: pcRelative));
            if (n < 8) D[n] = data; else A[n - 8] = data;
            addr += (uint)Bytes(size);
        }

        // the spurious extra word read that only MOVEM memory to register makes
        ReadMem(Word, addr, program: pcRelative);

        if (from.Mode == mPost) A[from.Reg] = addr;
        Prefetch();
    }

    private void MovepToMem(int op)
    {
        int size = (op & 0x40) != 0 ? Long : Word;
        var to = new M68kEa(mDisp, op & 7);
        uint address = FetchEa(size, ref to);
        uint data = D[(op >> 9) & 7];
        int shift = Bits(size);
        for (int i = 0; i < Bytes(size); i++)
        {
            shift -= 8;
            WriteMem(Byte, address, data >> shift);
            address += 2;
        }
        Prefetch();
    }

    private void MovepToReg(int op)
    {
        int size = (op & 0x40) != 0 ? Long : Word;
        var from = new M68kEa(mDisp, op & 7);
        uint address = FetchEa(size, ref from);
        int dn = (op >> 9) & 7;
        uint data = D[dn];
        int shift = Bits(size);
        for (int i = 0; i < Bytes(size); i++)
        {
            shift -= 8;
            data &= ~(0xFFu << shift);
            data |= ReadMem(Byte, address) << shift;
            address += 2;
        }
        D[dn] = data;
        Prefetch();
    }

    private void MoveFromSr(int op)
    {
        var to = EaField(op);
        uint data = ReadSr();
        ReadEa(Word, ref to, hold: true);
        Prefetch();
        WriteEa(Word, ref to, data);
        if (to.Mode == mDn) Idle(2);
    }

    private void MoveToCcr(int op)
    {
        var from = EaField(op);
        uint data = ReadEa(Word, ref from);
        Idle(4);
        WriteCcr(data);
        ReadMem(Word, Pc - 2);
        Prefetch();
    }

    private void MoveToSr(int op)
    {
        if (!RequireSupervisor()) return;
        var from = EaField(op);
        uint data = ReadEa(Word, ref from);
        Idle(4);
        WriteSr(data);
        ReadMem(Word, Pc - 2);
        Prefetch();
    }

    private void MoveFromUsp(int op)
    {
        if (!RequireSupervisor()) return;
        A[op & 7] = SpOther;
        Prefetch();
    }

    private void MoveToUsp(int op)
    {
        if (!RequireSupervisor()) return;
        SpOther = A[op & 7];
        Prefetch();
    }

    private void Muls(int op)
    {
        var from = EaField(op);
        int dn = (op >> 9) & 7;
        uint source = ReadEa(Word, ref from);
        uint target = ReadD(Word, dn);
        int result = (short)source * (short)target;
        Prefetch();
        // +2 cycles per 0<>1 bit transition
        int cycles = BitOperations.PopCount((uint)((ushort)(source << 1) ^ source));
        Idle(34 + cycles * 2);
        D[dn] = (uint)result;

        C = false; V = false;
        Z = result == 0;
        N = result < 0;
    }

    private void Mulu(int op)
    {
        var from = EaField(op);
        int dn = (op >> 9) & 7;
        uint source = ReadEa(Word, ref from);
        uint target = ReadD(Word, dn);
        uint result = source * target;
        Prefetch();
        // +2 cycles per bit set
        int cycles = BitOperations.PopCount(source);
        Idle(34 + cycles * 2);
        D[dn] = result;

        C = false; V = false;
        Z = result == 0;
        N = (int)result < 0;
    }

    private void NegOp(int op, bool extend)
    {
        int size = SizeField(op);
        var with = EaField(op);
        uint value = ReadEa(size, ref with, hold: true);
        uint result = SubOp(size, value, 0, extend);
        Prefetch();
        WriteEa(size, ref with, result, rmw: true);
        if (size == Long && (with.Mode == mDn || with.Mode == mAn)) Idle(2);
    }

    private void NotOp(int op)
    {
        int size = SizeField(op);
        var with = EaField(op);
        uint result = ~ReadEa(size, ref with, hold: true);
        Prefetch();
        WriteEa(size, ref with, result, rmw: true);
        if (size == Long && (with.Mode == mDn || with.Mode == mAn)) Idle(2);

        C = false; V = false;
        Z = Clip(size, result) == 0;
        N = SignOf(size, result) < 0;
    }

    private void Pea(int op)
    {
        var from = EaField(op);
        uint data = FetchEa(Long, ref from);
        if (from.Mode == mIdx || from.Mode == mPcIdx) Idle(2);
        if (from.Mode == mAbsW || from.Mode == mAbsL)
        {
            Push(Long, data);
            Prefetch();
        }
        else
        {
            Prefetch();
            Push(Long, data);
        }
    }

    private void ResetInstruction()
    {
        if (!RequireSupervisor()) return;
        int c = pend; pend = 0; Cycles += c;
        _bus.ResetLine(c);
        Idle(128);
        Prefetch();
    }

    private void Rte()
    {
        if (!RequireSupervisor()) return;
        uint sr = Pop(Word);
        Pc = Pop(Long);
        WriteSr(sr);
        Prefetch();
        Prefetch();
    }

    private void Rtr()
    {
        WriteCcr(Pop(Word));
        Pc = Pop(Long);
        Prefetch();
        Prefetch();
    }

    private void Rts()
    {
        Pc = Pop(Long);
        Prefetch();
        Prefetch();
    }

    private void Scc(int op)
    {
        var to = EaField(op);
        ReadEa(Byte, ref to, hold: true);
        Prefetch();
        if (!Condition((op >> 8) & 15))
        {
            WriteEa(Byte, ref to, 0);
        }
        else
        {
            WriteEa(Byte, ref to, 0xFF);
            if (to.Mode == mDn) Idle(2);
        }
    }

    private void StopInstruction()
    {
        if (!RequireSupervisor()) return;
        // the immediate is the word already in IRC: nothing is fetched until an interrupt ends the stop (and then the PC is moved past the immediate)
        WriteSr(Irc);
        Idle(4);
        Stopped = true;
    }

    private void SwapOp(int op)
    {
        int dn = op & 7;
        uint result = D[dn];
        result = result >> 16 | result << 16;
        D[dn] = result;

        C = false; V = false;
        Z = result == 0;
        N = (int)result < 0;
        Prefetch();
    }

    private void Tas(int op)
    {
        var with = EaField(op);
        uint data;

        if (TasWritesBack || with.Mode == mDn)
        {
            data = ReadEa(Byte, ref with, hold: true);
            if (with.Mode != mDn) Idle(2);
            WriteEa(Byte, ref with, data | 0x80);
            Prefetch();
        }
        else
        {
            // Mega Drive models 1 and 2 never complete the write cycle; software relies on it
            data = ReadEa(Byte, ref with);
            Idle(2 + 4);
            Prefetch();
        }

        C = false; V = false;
        Z = (byte)data == 0;
        N = (sbyte)data < 0;
    }

    private void Tst(int op)
    {
        int size = SizeField(op);
        var from = EaField(op);
        uint data = ReadEa(size, ref from);
        SetLogicFlags(size, data);
        Prefetch();
    }
}
