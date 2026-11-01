// Effective-address handling. Derived from ares' m68000 effective-address.cpp (ISC; see M68k.cs).

namespace NesEmulator.Sega;

/// <summary>An effective address as the instruction encodes it (mode, register), plus the address once it has been worked out.
/// Modes 0 to 6 are the encoded ones; mode 7 with register 0 to 4 is folded into 7 to 11.</summary>
internal struct M68kEa
{
    public const int DataRegisterDirect = 0, AddressRegisterDirect = 1, AddressRegisterIndirect = 2, PostIncrement = 3, PreDecrement = 4, Displacement = 5, Index = 6,
                     AbsoluteShort = 7, AbsoluteLong = 8, PcDisplacement = 9, PcIndex = 10, Immediate = 11;

    public int Mode;
    public int Reg;
    public bool Valid;
    public uint Address;

    public M68kEa(int mode, int reg)
    {
        Mode = mode == 7 ? 7 + reg : mode;
        Reg = reg;
        Valid = false;
        Address = 0;
    }
}

public sealed partial class M68k<TBus> where TBus : struct, IM68kBus
{
    private const int mDn = M68kEa.DataRegisterDirect, mAn = M68kEa.AddressRegisterDirect, mInd = M68kEa.AddressRegisterIndirect, mPost = M68kEa.PostIncrement,
                      mPre = M68kEa.PreDecrement, mDisp = M68kEa.Displacement, mIdx = M68kEa.Index, mAbsW = M68kEa.AbsoluteShort, mAbsL = M68kEa.AbsoluteLong,
                      mPcDisp = M68kEa.PcDisplacement, mPcIdx = M68kEa.PcIndex, mImm = M68kEa.Immediate;

    /// <summary>Used by JMP and JSR (and the like): the program counter is going to change, so no unnecessary prefetch is made.</summary>
    private uint PrefetchedAddress(ref M68kEa ea)
    {
        if (ea.Valid) return ea.Address;
        ea.Valid = true;

        switch (ea.Mode)
        {
            case mInd:
                return ea.Address = A[ea.Reg];

            case mDisp:
                Idle(2);
                return ea.Address = A[ea.Reg] + (uint)(short)Prefetched();

            case mIdx:
            {
                Idle(6);
                uint extension = Prefetched();
                uint index = (extension & 0x8000) != 0 ? A[(int)(extension >> 12) & 7] : D[(int)(extension >> 12) & 7];
                if ((extension & 0x800) == 0) index = (uint)(short)index;
                return ea.Address = A[ea.Reg] + index + (uint)(sbyte)extension;
            }

            case mAbsW:
                Idle(2);
                return ea.Address = (uint)(short)Prefetched();

            case mAbsL:
            {
                uint hi = Extension(Word);
                uint lo = Prefetched();
                return ea.Address = hi << 16 | lo;
            }

            case mPcDisp:
            {
                Idle(2);
                uint b = Pc - 2;
                return ea.Address = b + (uint)(short)Prefetched();
            }

            case mPcIdx:
            {
                Idle(6);
                uint b = Pc - 2;
                uint extension = Prefetched();
                uint index = (extension & 0x8000) != 0 ? A[(int)(extension >> 12) & 7] : D[(int)(extension >> 12) & 7];
                if ((extension & 0x800) == 0) index = (uint)(short)index;
                return ea.Address = b + index + (uint)(sbyte)extension;
            }
        }
        return ea.Address = 0;
    }

    /// <summary>Work out the effective address (fetching extension words and charging the internal cycles the mode costs); a register mode "address" is the register's value.</summary>
    private uint FetchEa(int size, ref M68kEa ea)
    {
        if (ea.Valid) return ea.Address;
        ea.Valid = true;

        switch (ea.Mode)
        {
            case mDn:
                return ea.Address = D[ea.Reg];
            case mAn:
            case mInd:
            case mPost:
            case mPre:
                return ea.Address = A[ea.Reg];

            case mDisp:
                return ea.Address = A[ea.Reg] + (uint)(short)Extension(Word);

            case mIdx:
            {
                Idle(2);
                uint data = Extension(Word);
                uint index = (data & 0x8000) != 0 ? A[(int)(data >> 12) & 7] : D[(int)(data >> 12) & 7];
                if ((data & 0x800) == 0) index = (uint)(short)index;
                return ea.Address = A[ea.Reg] + index + (uint)(sbyte)data;
            }

            case mAbsW:
                return ea.Address = (uint)(short)Extension(Word);

            case mAbsL:
                return ea.Address = Extension(Long);

            case mPcDisp:
            {
                uint b = Pc - 2;
                return ea.Address = b + (uint)(short)Extension(Word);
            }

            case mPcIdx:
            {
                Idle(2);
                uint b = Pc - 2;
                uint data = Extension(Word);
                uint index = (data & 0x8000) != 0 ? A[(int)(data >> 12) & 7] : D[(int)(data >> 12) & 7];
                if ((data & 0x800) == 0) index = (uint)(short)index;
                return ea.Address = b + index + (uint)(sbyte)data;
            }

            case mImm:
                return ea.Address = Extension(size);
        }
        return ea.Address = 0;
    }

    /// <summary>Read the operand. <paramref name="hold"/>: leave the register of a post-increment or pre-decrement mode alone (the write that follows will update it). <paramref name="fast"/>: skip the pre-decrement's two internal cycles.</summary>
    private uint ReadEa(int size, ref M68kEa ea, bool hold = false, bool fast = false, bool lowWordFirst = false)
    {
        FetchEa(size, ref ea);

        switch (ea.Mode)
        {
            case mDn:
                return Clip(size, ea.Address);
            case mAn:
                return (uint)SignOf(size, ea.Address);
            case mInd:
                return ReadMem(size, ea.Address);

            case mPost:
            {
                uint address = ea.Address + (uint)(ea.Reg == 7 && size == Byte ? 2 : Bytes(size));
                // A word read has already moved the register on if it raises an address error; a long read has not
                if (size != Long) { pendReg = ea.Reg; pendValue = address; }
                uint data = ReadMem(size, ea.Address);
                pendReg = -1;
                if (!hold) A[ea.Reg] = ea.Address = address;
                return data;
            }

            case mPre:
            {
                if (!fast) Idle(2);
                uint address = ea.Address - (uint)(ea.Reg == 7 && size == Byte ? 2 : Bytes(size));
                pendReg = ea.Reg; pendValue = lowWordFirst && size == Long ? ea.Address - 2 : address;   // a long read low word first has taken the register down one word so far
                uint data = ReadMem(size, address, lowWordFirst);
                pendReg = -1;
                if (!hold) A[ea.Reg] = ea.Address = address;
                return data;
            }

            case mImm:
                return Clip(size, ea.Address);

            default:    // displacement, index, absolute and PC-relative: the address is worked out (PC-relative operands are read in program space)
                return ReadMem(size, ea.Address, program: ea.Mode == mPcDisp || ea.Mode == mPcIdx);
        }
    }

    /// <summary>Write the operand. <paramref name="rmw"/>: the write of a read-modify-write instruction, which puts a long's low word on the bus first.</summary>
    private void WriteEa(int size, ref M68kEa ea, uint data, bool hold = false, bool rmw = false)
    {
        FetchEa(size, ref ea);

        switch (ea.Mode)
        {
            case mDn:
                WriteD(size, ea.Reg, data);
                return;
            case mAn:
                WriteA(size, ea.Reg, data);
                return;
            case mInd:
                WriteMem(size, ea.Address, data, rmw);
                return;

            case mPost:
            {
                uint address = ea.Address + (uint)(ea.Reg == 7 && size == Byte ? 2 : Bytes(size));
                WriteMem(size, ea.Address, data, rmw);
                if (!hold) A[ea.Reg] = ea.Address = address;     // (a write that raises an address error leaves the register alone)
                return;
            }

            case mPre:
            {
                uint address = ea.Address - (uint)(ea.Reg == 7 && size == Byte ? 2 : Bytes(size));
                // a word write that raises an address error has already taken the register down; a long write has not
                if (size != Long) { pendReg = ea.Reg; pendValue = address; }
                WriteMem(size, address, data, reverse: true);
                pendReg = -1;
                if (!hold) A[ea.Reg] = ea.Address = address;
                return;
            }

            case mImm:
                return;

            default:
                WriteMem(size, ea.Address, data, rmw);
                return;
        }
    }
}
