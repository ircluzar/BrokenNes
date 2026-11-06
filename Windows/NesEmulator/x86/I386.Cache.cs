// Intel 80386 / 80486 core: the instruction decoder of the block compiler (our own addition, not in tiny386; see I386.Jit.cs).
//
// DcDecode reads one instruction of the everyday forms (MOV, the ALU group, TEST, shifts, INC / DEC / PUSH / POP of a register, LEA,
// near jumps / calls / returns, group 3) into a DEntry: the prefixes, the ModR/M operand as base + index * scale + displacement and its
// segment (Modsib16 / Modsib32's rules), the immediate. Anything else (and any LOCK / REP prefix) is DK_SLOW: the interpreter runs it.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NesEmulator.X86;

public sealed partial class I386
{
    private struct DEntry
    {
        public byte Kind, Len, Bits, Op, Mod, Reg, Rm, Base, Index, Scale;
        public bool O16, A16;
        public sbyte Seg, SegRaw; // the segment used for a memory operand / the prefix one (-1 none)
        public uint Disp, Imm;
    }

    // kinds (0 = not cached: the ordinary path)
    private const byte DK_SLOW = 0, DK_MOVEG = 1, DK_MOVGE = 2, DK_ALUEG = 3, DK_ALUGE = 4, DK_ALUAI = 5, DK_GRP1 = 6, DK_GRP2 = 7,
        DK_TESTEG = 8, DK_G3TEST = 9, DK_GRP3 = 10, DK_MOVEI = 11, DK_LEA = 12, DK_MOVRI = 13, DK_INC = 14, DK_DEC = 15, DK_PUSH = 16,
        DK_POP = 17, DK_JCC = 18, DK_JMP = 19, DK_CALL = 20, DK_RET = 21;

    /// <summary>Decode the instruction at <paramref name="paddr"/> (in a fast-fetch code page) into <paramref name="e"/>; Kind 0 if it is not
    /// one of the cached forms.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DcDecode(ref DEntry e, uint paddr)
    {
        e.Kind = DK_SLOW;
        bool c16 = _c16, o16 = c16, a16 = c16;
        int seg = -1, p = 0;
        uint b;
        for (;;)
        {
            b = U8(paddr + (uint)p++);
            if (!IsPrefix[b]) break;
            switch (b)
            {
                case 0x26: seg = SEG_ES; break;
                case 0x2e: seg = SEG_CS; break;
                case 0x36: seg = SEG_SS; break;
                case 0x3e: seg = SEG_DS; break;
                case 0x64: seg = SEG_FS; break;
                case 0x65: seg = SEG_GS; break;
                case 0x66: o16 = !c16; break;
                case 0x67: a16 = !c16; break;
                default: return;   // LOCK / REP: the ordinary path
            }
            if (p > 4) return;
        }
        int v = o16 ? 16 : 32;
        byte kind = DK_SLOW;
        int bits = v, op = 0, immBits = 0;
        bool modrm = false, immSext8 = false, noReg = false;
        uint rel = 0;
        switch (b)
        {
            case < 0x40 when (b & 7) < 6:
                op = (int)(b >> 3);
                switch (b & 7)
                {
                    case 0: kind = DK_ALUEG; bits = 8; modrm = true; break;
                    case 1: kind = DK_ALUEG; modrm = true; break;
                    case 2: kind = DK_ALUGE; bits = 8; modrm = true; break;
                    case 3: kind = DK_ALUGE; modrm = true; break;
                    case 4: kind = DK_ALUAI; bits = 8; immBits = 8; break;
                    default: kind = DK_ALUAI; immBits = v; break;
                }
                break;
            case >= 0x40 and <= 0x5f:
                kind = (b >> 3) switch { 8 => DK_INC, 9 => DK_DEC, 10 => DK_PUSH, _ => DK_POP };
                op = (int)(b & 7);
                break;
            case >= 0x70 and <= 0x7f: kind = DK_JCC; op = (int)b; immBits = 8; break;
            case 0x80: case 0x82: kind = DK_GRP1; bits = 8; modrm = true; immBits = 8; break;
            case 0x81: kind = DK_GRP1; modrm = true; immBits = v; break;
            case 0x83: kind = DK_GRP1; modrm = true; immBits = 8; immSext8 = true; break;
            case 0x84: kind = DK_TESTEG; bits = 8; modrm = true; break;
            case 0x85: kind = DK_TESTEG; modrm = true; break;
            case 0x88: kind = DK_MOVEG; bits = 8; modrm = true; break;
            case 0x89: kind = DK_MOVEG; modrm = true; break;
            case 0x8a: kind = DK_MOVGE; bits = 8; modrm = true; break;
            case 0x8b: kind = DK_MOVGE; modrm = true; break;
            case 0x8d: kind = DK_LEA; modrm = true; noReg = true; break;
            case >= 0xb0 and <= 0xb7: kind = DK_MOVRI; bits = 8; op = (int)(b & 7); immBits = 8; break;
            case >= 0xb8 and <= 0xbf: kind = DK_MOVRI; op = (int)(b & 7); immBits = v; break;
            case 0xc0: kind = DK_GRP2; bits = 8; modrm = true; immBits = 8; op = 0; break;
            case 0xc1: kind = DK_GRP2; modrm = true; immBits = 8; op = 0; break;
            case 0xd0: kind = DK_GRP2; bits = 8; modrm = true; op = 1; break;
            case 0xd1: kind = DK_GRP2; modrm = true; op = 1; break;
            case 0xd2: kind = DK_GRP2; bits = 8; modrm = true; op = 2; break;
            case 0xd3: kind = DK_GRP2; modrm = true; op = 2; break;
            case 0xc3: kind = DK_RET; break;
            case 0xc6: kind = DK_MOVEI; bits = 8; modrm = true; immBits = 8; break;
            case 0xc7: kind = DK_MOVEI; modrm = true; immBits = v; break;
            case 0xe8: kind = DK_CALL; immBits = v; break;
            case 0xe9: kind = DK_JMP; immBits = v; break;
            case 0xeb: kind = DK_JMP; immBits = 8; break;
            case 0xf6: kind = DK_GRP3; bits = 8; modrm = true; break;
            case 0xf7: kind = DK_GRP3; modrm = true; break;
            case 0x0f:
                b = U8(paddr + (uint)p++);
                if ((b & 0xf0) != 0x80) return;
                kind = DK_JCC; op = (int)b; immBits = v;
                break;
            default: return;
        }
        if (modrm)
        {
            uint m = U8(paddr + (uint)p++);
            int mod = (int)(m >> 6), reg = (int)((m >> 3) & 7), rm = (int)(m & 7);
            e.Mod = (byte)mod; e.Reg = (byte)reg; e.Rm = (byte)rm;
            e.Base = 8; e.Index = 8; e.Scale = 0;
            uint disp = 0;
            int s = seg;
            if (mod != 3)
            {
                if (a16)
                {
                    if (rm == 6 && mod == 0) { disp = U16(paddr + (uint)p); p += 2; }
                    else
                    {
                        switch (rm)
                        {
                            case 0: e.Base = 3; e.Index = 6; break;
                            case 1: e.Base = 3; e.Index = 7; break;
                            case 2: e.Base = 5; e.Index = 6; break;
                            case 3: e.Base = 5; e.Index = 7; break;
                            case 4: e.Base = 6; break;
                            case 5: e.Base = 7; break;
                            case 6: e.Base = 5; break;
                            default: e.Base = 3; break;
                        }
                        if (mod == 1) { disp = Sext8(U8(paddr + (uint)p)); p++; }
                        else if (mod == 2) { disp = U16(paddr + (uint)p); p += 2; }
                    }
                    if (s == -1) s = rm == 2 || rm == 3 || (mod != 0 && rm == 6) ? SEG_SS : SEG_DS;
                }
                else
                {
                    if (rm == 4)
                    {
                        uint sib = U8(paddr + (uint)p++);
                        int bb = (int)(sib & 7);
                        if (bb == 5 && mod == 0) { disp = U32(paddr + (uint)p); p += 4; }
                        else
                        {
                            e.Base = (byte)bb;
                            if ((bb == 4 || bb == 5) && s == -1) s = SEG_SS;
                        }
                        int ii = (int)((sib >> 3) & 7);
                        if (ii != 4) { e.Index = (byte)ii; e.Scale = (byte)(sib >> 6); }
                    }
                    else if (rm == 5 && mod == 0) { disp = U32(paddr + (uint)p); p += 4; }
                    else
                    {
                        e.Base = (byte)rm;
                        if (rm == 5 && s == -1) s = SEG_SS;
                    }
                    if (mod == 1) { disp += Sext8(U8(paddr + (uint)p)); p++; }
                    else if (mod == 2) { disp += U32(paddr + (uint)p); p += 4; }
                    if (s == -1) s = SEG_DS;
                }
            }
            else if (noReg) return;   // LEA with a register operand: #UD on the ordinary path
            e.Disp = disp; e.Seg = (sbyte)s;
            if (kind == DK_GRP1) op = reg;
            else if (kind == DK_GRP2) op = op | (reg << 2);
            else if (kind == DK_GRP3)
            {
                if (reg < 2) { kind = DK_G3TEST; immBits = bits; }
            }
        }
        uint imm = 0;
        if (immBits == 8) { imm = U8(paddr + (uint)p); p++; }
        else if (immBits == 16) { imm = U16(paddr + (uint)p); p += 2; }
        else if (immBits == 32) { imm = U32(paddr + (uint)p); p += 4; }
        if (immSext8) imm = Sext8(imm);
        if (kind is DK_JCC or DK_JMP or DK_CALL) rel = Sext(immBits, imm);
        if (p > 15) return;
        e.Imm = kind is DK_JCC or DK_JMP or DK_CALL ? rel : imm;
        e.Bits = (byte)bits; e.Op = (byte)op; e.O16 = o16; e.A16 = a16; e.SegRaw = (sbyte)seg;
        e.Len = (byte)p;
        e.Kind = kind;
    }

    // group 2 per size, for the compiled code's out-of-line rotates
    [MethodImpl(MethodImplOptions.NoInlining)] private void DGrp2_8(int op, uint count) => Grp2(op, 8, count);
    [MethodImpl(MethodImplOptions.NoInlining)] private void DGrp2_16(int op, uint count) => Grp2(op, 16, count);
    [MethodImpl(MethodImplOptions.NoInlining)] private void DGrp2_32(int op, uint count) => Grp2(op, 32, count);
}
