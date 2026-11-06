// Intel 80386 / 80486 core: instruction decode and execution. Ported from tiny386 i386.c (cpu_exec1 and the instruction macros) and
// i386ins.def, commit 2c1205c, BSD-3-Clause, Copyright (c) 2024-2025, Chunhui He. See x86/THIRD_PARTY_NOTICES.md.

using System.Numerics;
using System.Runtime.CompilerServices;

namespace NesEmulator.X86;

public sealed partial class I386
{
    // per-instruction decode state (tiny386 cpu_exec1 locals)
    private bool _opsz16, _adsz16, _c16;
    private int _rep, _curSeg, _steps;
    private uint _spm;
    private int _mod, _reg, _rm;
    private uint _addr;
    private OptAddr _meml;

    private const int FAIL = 0, OK = 1, END = 2;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int K(bool ok) => ok ? OK : FAIL;
    private int V => _opsz16 ? 16 : 32;
    private int A => _adsz16 ? 16 : 32;

    private static readonly bool[] IsPrefix = BuildPrefixTable();
    private static bool[] BuildPrefixTable()
    {
        var t = new bool[256];
        foreach (int b in new[] { 0x26, 0x2e, 0x36, 0x3e, 0x64, 0x65, 0x66, 0x67, 0xf0, 0xf2, 0xf3 }) t[b] = true;
        return t;
    }

    private bool Exec1(int stepcount)
    {
        _steps = stepcount;
        for (; _steps > 0; _steps--)
        {
            _c16 = Code16;
            _spm = SpMask;
            Ip = NextIp;
            if (!Fetch8pf(out uint b1)) return false;
            Cycle++;
            _opsz16 = _c16; _adsz16 = _c16; _rep = 0; _curSeg = -1;
            while (IsPrefix[b1])   // a table test, not a switch: an indirect jump on every opcode costs as much as the dispatch itself
            {
                switch (b1)
                {
                    case 0x26: _curSeg = SEG_ES; break;
                    case 0x2e: _curSeg = SEG_CS; break;
                    case 0x36: _curSeg = SEG_SS; break;
                    case 0x3e: _curSeg = SEG_DS; break;
                    case 0x64: _curSeg = SEG_FS; break;
                    case 0x65: _curSeg = SEG_GS; break;
                    case 0x66: _opsz16 = !_c16; break;
                    case 0x67: _adsz16 = !_c16; break;
                    case 0xf3: _rep = 1; break;
                    case 0xf2: _rep = 2; break;
                    default: break;   // 0xf0 LOCK
                }
                if (!Fetch8(out b1)) return false;
            }
            // the everyday opcodes are dispatched here, to small handlers: going through ExecOne's big switch costs about as much as
            // the instruction itself (measured with Workshop --x86bench); everything else still goes to ExecOne
            int r;
            switch (b1)
            {
                    case 0x00: r = K(AluEG8(0)); break;
                    case 0x01: r = K(_opsz16 ? AluEG16(0) : AluEG32(0)); break;
                    case 0x02: r = K(AluGE8(0)); break;
                    case 0x03: r = K(_opsz16 ? AluGE16(0) : AluGE32(0)); break;
                    case 0x04: r = K(AluAI8(0)); break;
                    case 0x05: r = K(_opsz16 ? AluAI16(0) : AluAI32(0)); break;
                    case 0x08: r = K(AluEG8(1)); break;
                    case 0x09: r = K(_opsz16 ? AluEG16(1) : AluEG32(1)); break;
                    case 0x0a: r = K(AluGE8(1)); break;
                    case 0x0b: r = K(_opsz16 ? AluGE16(1) : AluGE32(1)); break;
                    case 0x0c: r = K(AluAI8(1)); break;
                    case 0x0d: r = K(_opsz16 ? AluAI16(1) : AluAI32(1)); break;
                    case 0x10: r = K(AluEG8(2)); break;
                    case 0x11: r = K(_opsz16 ? AluEG16(2) : AluEG32(2)); break;
                    case 0x12: r = K(AluGE8(2)); break;
                    case 0x13: r = K(_opsz16 ? AluGE16(2) : AluGE32(2)); break;
                    case 0x14: r = K(AluAI8(2)); break;
                    case 0x15: r = K(_opsz16 ? AluAI16(2) : AluAI32(2)); break;
                    case 0x18: r = K(AluEG8(3)); break;
                    case 0x19: r = K(_opsz16 ? AluEG16(3) : AluEG32(3)); break;
                    case 0x1a: r = K(AluGE8(3)); break;
                    case 0x1b: r = K(_opsz16 ? AluGE16(3) : AluGE32(3)); break;
                    case 0x1c: r = K(AluAI8(3)); break;
                    case 0x1d: r = K(_opsz16 ? AluAI16(3) : AluAI32(3)); break;
                    case 0x20: r = K(AluEG8(4)); break;
                    case 0x21: r = K(_opsz16 ? AluEG16(4) : AluEG32(4)); break;
                    case 0x22: r = K(AluGE8(4)); break;
                    case 0x23: r = K(_opsz16 ? AluGE16(4) : AluGE32(4)); break;
                    case 0x24: r = K(AluAI8(4)); break;
                    case 0x25: r = K(_opsz16 ? AluAI16(4) : AluAI32(4)); break;
                    case 0x28: r = K(AluEG8(5)); break;
                    case 0x29: r = K(_opsz16 ? AluEG16(5) : AluEG32(5)); break;
                    case 0x2a: r = K(AluGE8(5)); break;
                    case 0x2b: r = K(_opsz16 ? AluGE16(5) : AluGE32(5)); break;
                    case 0x2c: r = K(AluAI8(5)); break;
                    case 0x2d: r = K(_opsz16 ? AluAI16(5) : AluAI32(5)); break;
                    case 0x30: r = K(AluEG8(6)); break;
                    case 0x31: r = K(_opsz16 ? AluEG16(6) : AluEG32(6)); break;
                    case 0x32: r = K(AluGE8(6)); break;
                    case 0x33: r = K(_opsz16 ? AluGE16(6) : AluGE32(6)); break;
                    case 0x34: r = K(AluAI8(6)); break;
                    case 0x35: r = K(_opsz16 ? AluAI16(6) : AluAI32(6)); break;
                    case 0x38: r = K(AluEG8(7)); break;
                    case 0x39: r = K(_opsz16 ? AluEG16(7) : AluEG32(7)); break;
                    case 0x3a: r = K(AluGE8(7)); break;
                    case 0x3b: r = K(_opsz16 ? AluGE16(7) : AluGE32(7)); break;
                    case 0x3c: r = K(AluAI8(7)); break;
                    case 0x3d: r = K(_opsz16 ? AluAI16(7) : AluAI32(7)); break;
                    case 0x40: r = OpIncReg(0); break;
                    case 0x41: r = OpIncReg(1); break;
                    case 0x42: r = OpIncReg(2); break;
                    case 0x43: r = OpIncReg(3); break;
                    case 0x44: r = OpIncReg(4); break;
                    case 0x45: r = OpIncReg(5); break;
                    case 0x46: r = OpIncReg(6); break;
                    case 0x47: r = OpIncReg(7); break;
                    case 0x48: r = OpDecReg(0); break;
                    case 0x49: r = OpDecReg(1); break;
                    case 0x4a: r = OpDecReg(2); break;
                    case 0x4b: r = OpDecReg(3); break;
                    case 0x4c: r = OpDecReg(4); break;
                    case 0x4d: r = OpDecReg(5); break;
                    case 0x4e: r = OpDecReg(6); break;
                    case 0x4f: r = OpDecReg(7); break;
                    case 0x50: r = OpPushReg(0); break;
                    case 0x51: r = OpPushReg(1); break;
                    case 0x52: r = OpPushReg(2); break;
                    case 0x53: r = OpPushReg(3); break;
                    case 0x54: r = OpPushReg(4); break;
                    case 0x55: r = OpPushReg(5); break;
                    case 0x56: r = OpPushReg(6); break;
                    case 0x57: r = OpPushReg(7); break;
                    case 0x58: r = OpPopReg(0); break;
                    case 0x59: r = OpPopReg(1); break;
                    case 0x5a: r = OpPopReg(2); break;
                    case 0x5b: r = OpPopReg(3); break;
                    case 0x5c: r = OpPopReg(4); break;
                    case 0x5d: r = OpPopReg(5); break;
                    case 0x5e: r = OpPopReg(6); break;
                    case 0x5f: r = OpPopReg(7); break;
                    case 0x70: r = OpJcc8(112); break;
                    case 0x71: r = OpJcc8(113); break;
                    case 0x72: r = OpJcc8(114); break;
                    case 0x73: r = OpJcc8(115); break;
                    case 0x74: r = OpJcc8(116); break;
                    case 0x75: r = OpJcc8(117); break;
                    case 0x76: r = OpJcc8(118); break;
                    case 0x77: r = OpJcc8(119); break;
                    case 0x78: r = OpJcc8(120); break;
                    case 0x79: r = OpJcc8(121); break;
                    case 0x7a: r = OpJcc8(122); break;
                    case 0x7b: r = OpJcc8(123); break;
                    case 0x7c: r = OpJcc8(124); break;
                    case 0x7d: r = OpJcc8(125); break;
                    case 0x7e: r = OpJcc8(126); break;
                    case 0x7f: r = OpJcc8(127); break;
                    case 0x80: case 0x82: r = K(Grp1_8()); break;
                    case 0x81: r = K(_opsz16 ? Grp1_16() : Grp1_32()); break;
                    case 0x83: r = K(_opsz16 ? Grp1_16_8() : Grp1_32_8()); break;
                    case 0x88: r = K(MovEG8()); break;
                    case 0x89: r = K(_opsz16 ? MovEG16() : MovEG32()); break;
                    case 0x8a: r = K(MovGE8()); break;
                    case 0x8b: r = K(_opsz16 ? MovGE16() : MovGE32()); break;
                    case 0xc1: r = K(_opsz16 ? Grp2Op16(0) : Grp2Op32(0)); break;
                    case 0xd3: r = K(_opsz16 ? Grp2Op16(2) : Grp2Op32(2)); break;
                    case 0xe9: r = OpJmpRel(); break;
                    case 0xeb: r = OpJmp8(); break;
                    case 0xf6: r = K(Grp38()); break;
                    case 0xf7: r = K(_opsz16 ? Grp316() : Grp332()); break;
                    case 0x0f: r = OpTwo(); break;
                    case 0x84: r = OpTest8(); break;
                    case 0x85: r = _opsz16 ? OpTest16() : OpTest32(); break;
                    case 0x8d: r = OpLea(); break;
                    case 0xc3: r = OpRet(); break;
                    case 0xe8: r = OpCallRel(); break;
                    default: r = ExecOne((int)b1); break;
                }
            if (r == FAIL) return false;
            if (r == END) return true;
        }
        return true;
    }

    // ================= hot-opcode handlers (Exec1's own dispatch) =================

    [MethodImpl(MethodImplOptions.NoInlining)] private int OpIncReg(int reg) { IncDecReg(_opsz16 ? 16 : 32, reg, false); return OK; }
    [MethodImpl(MethodImplOptions.NoInlining)] private int OpDecReg(int reg) { IncDecReg(_opsz16 ? 16 : 32, reg, true); return OK; }
    [MethodImpl(MethodImplOptions.NoInlining)] private int OpPushReg(int reg) => _opsz16 ? K(Push(16, R[reg] & 0xffff)) : K(Push(32, R[reg]));
    [MethodImpl(MethodImplOptions.NoInlining)] private int OpPopReg(int reg) => K(PopReg(_opsz16 ? 16 : 32, reg));
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int OpJcc8(int b1)
    {
        if (!Fetch8(out uint imm)) return FAIL;
        if (Cond((uint)b1)) JumpRel(Sext8(imm));
        return OK;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int OpJmp8()
    {
        if (!Fetch8(out uint imm)) return FAIL;
        JumpRel(Sext8(imm));
        return OK;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int OpJmpRel()
    {
        int bits = _opsz16 ? 16 : 32;
        if (!Fetch(bits, out uint imm)) return FAIL;
        JumpRel(Sext(bits, imm));
        return OK;
    }

    /// <summary>0F: the near Jcc (0F 80-8F) here, the rest in ExecTwo.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int OpTwo()
    {
        if (_ifPaddr != 0 && (U8(_ifPaddr) & 0xf0) == 0x80)
        {
            Fetch8(out uint b2);
            int bits = _opsz16 ? 16 : 32;
            if (!Fetch(bits, out uint imm)) return FAIL;
            if (Cond(b2)) JumpRel(Sext(bits, imm));
            return OK;
        }
        return ExecTwo();
    }
    [MethodImpl(MethodImplOptions.NoInlining)] private int OpTest8() => OpTestCore(8);
    [MethodImpl(MethodImplOptions.NoInlining)] private int OpTest16() => OpTestCore(16);
    [MethodImpl(MethodImplOptions.NoInlining)] private int OpTest32() => OpTestCore(32);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int OpTestCore(int bits)
    {
        if (!DecodeE(bits, 1)) return FAIL;
        Alu(4, bits, ReadE(bits), LReg(bits, _reg));
        TryJcc8E();
        return OK;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int OpLea()
    {
        if (!DecodeModrm()) return FAIL;
        if (_mod == 3) return K(Throw0(EX_UD));
        SReg(_opsz16 ? 16 : 32, _reg, _addr);
        return OK;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] private int OpRet() => K(Ret(0));
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int OpCallRel()
    {
        int bits = _opsz16 ? 16 : 32;
        if (!Fetch(bits, out uint imm)) return FAIL;
        return K(CallNear(Sext(bits, imm), true));
    }

    // ================= operand helpers =================

    private bool DecodeModrm()
    {
        if (!Fetch8(out uint modrm)) return false;
        _mod = (int)(modrm >> 6); _reg = (int)((modrm >> 3) & 7); _rm = (int)(modrm & 7);
        if (_mod != 3) return Modsib(_adsz16, _mod, _rm, out _addr, ref _curSeg);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TranslateE(int bits, int rwm) => _mod == 3 || Translate(ref _meml, rwm, _curSeg, _addr, bits / 8);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadE(int bits) => _mod == 3 ? LReg(bits, _rm) : Load(bits, ref _meml);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteE(int bits, uint v) { if (_mod == 3) SReg(bits, _rm, v); else Store(bits, ref _meml, v); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool DecodeE(int bits, int rwm) => DecodeModrm() && TranslateE(bits, rwm);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Cond(uint c) => (c & 0xf) switch
    {
        0x0 => GetOF() != 0,
        0x1 => GetOF() == 0,
        0x2 => GetCF() != 0,
        0x3 => GetCF() == 0,
        0x4 => GetZF() != 0,
        0x5 => GetZF() == 0,
        0x6 => GetZF() != 0 || GetCF() != 0,
        0x7 => GetZF() == 0 && GetCF() == 0,
        0x8 => GetSF() != 0,
        0x9 => GetSF() == 0,
        0xa => GetPF() != 0,
        0xb => GetPF() == 0,
        0xc => GetSF() != GetOF(),
        0xd => GetSF() == GetOF(),
        0xe => GetZF() != 0 || GetSF() != GetOF(),
        _ => GetZF() == 0 && GetSF() == GetOF(),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void JumpRel(uint d) { NextIp += d; if (_c16) NextIp &= 0xffff; }

    // ================= arithmetic =================

    private const uint ALL = CF | PF | AF | ZF | SF | OF, LOGIC = CF | PF | ZF | SF | OF;

    // MOV E,G / G,E (88-8B), one copy per operand size so the size is a constant in each
    [MethodImpl(MethodImplOptions.NoInlining)] private bool MovEG8() => MovEGCore(8);
    [MethodImpl(MethodImplOptions.NoInlining)] private bool MovEG16() => MovEGCore(16);
    [MethodImpl(MethodImplOptions.NoInlining)] private bool MovEG32() => MovEGCore(32);
    [MethodImpl(MethodImplOptions.NoInlining)] private bool MovGE8() => MovGECore(8);
    [MethodImpl(MethodImplOptions.NoInlining)] private bool MovGE16() => MovGECore(16);
    [MethodImpl(MethodImplOptions.NoInlining)] private bool MovGE32() => MovGECore(32);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MovEGCore(int bits)
    {
        if (!DecodeE(bits, 2)) return false;
        WriteE(bits, LReg(bits, _reg));
        return true;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MovGECore(int bits)
    {
        if (!DecodeE(bits, 1)) return false;
        SReg(bits, _reg, ReadE(bits));
        return true;
    }

    /// <summary>Group-1 ALU op (0 ADD 1 OR 2 ADC 3 SBB 4 AND 5 SUB 6 XOR 7 CMP); returns the (sign-extended) result.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint Alu(int op, int bits, uint a, uint b)
    {
        int cf;
        switch (op)
        {
            case 0: _ccSrc1 = Sext(bits, a); _ccSrc2 = Sext(bits, b); _ccDst = Sext(bits, _ccSrc1 + _ccSrc2); _ccOp = (int)Cc.ADD; _ccMask = ALL; break;
            case 1: _ccDst = Sext(bits, a | b); _ccOp = (int)Cc.OR; _ccMask = LOGIC; break;
            case 2:
                cf = GetCF();
                _ccSrc1 = Sext(bits, a); _ccSrc2 = Sext(bits, b); _ccDst = Sext(bits, _ccSrc1 + _ccSrc2 + (uint)cf);
                _ccOp = (int)(cf != 0 ? Cc.ADC : Cc.ADD); _ccMask = ALL; break;
            case 3:
                cf = GetCF();
                _ccSrc1 = Sext(bits, a); _ccSrc2 = Sext(bits, b); _ccDst = Sext(bits, _ccSrc1 - _ccSrc2 - (uint)cf);
                _ccOp = (int)(cf != 0 ? Cc.SBB : Cc.SUB); _ccMask = ALL; break;
            case 4: _ccDst = Sext(bits, a & b); _ccOp = (int)Cc.AND; _ccMask = LOGIC; break;
            case 6: _ccDst = Sext(bits, a ^ b); _ccOp = (int)Cc.XOR; _ccMask = LOGIC; break;
            default: _ccSrc1 = Sext(bits, a); _ccSrc2 = Sext(bits, b); _ccDst = Sext(bits, _ccSrc1 - _ccSrc2); _ccOp = (int)Cc.SUB; _ccMask = ALL; break; // SUB, CMP
        }
        return _ccDst;
    }

    private void SetSub(int bits, uint a, uint b) { _ccSrc1 = Sext(bits, a); _ccSrc2 = Sext(bits, b); _ccDst = Sext(bits, _ccSrc1 - _ccSrc2); _ccOp = (int)Cc.SUB; _ccMask = ALL; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool AluEG(int op, int bits) => bits == 8 ? AluEG8(op) : bits == 16 ? AluEG16(op) : AluEG32(op);
    [MethodImpl(MethodImplOptions.NoInlining)] private bool AluEG8(int op) => AluEGCore(op, 8);
    [MethodImpl(MethodImplOptions.NoInlining)] private bool AluEG16(int op) => AluEGCore(op, 16);
    [MethodImpl(MethodImplOptions.NoInlining)] private bool AluEG32(int op) => AluEGCore(op, 32);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool AluEGCore(int op, int bits)
    {
        if (!DecodeE(bits, op == 7 ? 1 : 3)) return false;
        uint r = Alu(op, bits, ReadE(bits), LReg(bits, _reg));
        if (op != 7) WriteE(bits, r); else TryJcc8E();
        return true;
    }

    /// <summary>tiny386 try_jcc8: right after CMP / TEST, a following short JZ / JNZ that is taken is folded into this instruction
    /// (kept so the instruction stream steps exactly like the original's).</summary>
    private void TryJcc8(bool pure = true)
    {
        if (_ifPaddr == 0) return;
        uint op = P8(_ifPaddr);
        if ((op & ~1u) != 0x74) return;
        if ((_ccDst == 0) ^ ((op & 1) != 0))
        {
            NextIp += Sext8(P8(_ifPaddr + 1)) + 2;
            if (Code16) NextIp &= 0xffff;
            // A spin: CMP / TEST + JZ / JNZ back to itself, reading only registers or plain RAM. Nothing but an interrupt can change what
            // it reads, and interrupts and the devices only run between batches, so the rest of this batch would repeat this very step:
            // count those steps at once (the same instruction clock, the same state, step for step).
            if (NextIp == Ip && pure && _steps > 1) { Cycle += _steps - 1; _steps = 1; }
        }
    }

    /// <summary>TryJcc8 after a CMP / TEST with an r/m operand: a spin may be skipped only if that operand is a register or plain RAM.</summary>
    private void TryJcc8E() => TryJcc8(_mod == 3 || (IsRam(_meml.A1) && (!_meml.Two || IsRam(_meml.A2))));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool AluGE(int op, int bits) => bits == 8 ? AluGE8(op) : bits == 16 ? AluGE16(op) : AluGE32(op);
    [MethodImpl(MethodImplOptions.NoInlining)] private bool AluGE8(int op) => AluGECore(op, 8);
    [MethodImpl(MethodImplOptions.NoInlining)] private bool AluGE16(int op) => AluGECore(op, 16);
    [MethodImpl(MethodImplOptions.NoInlining)] private bool AluGE32(int op) => AluGECore(op, 32);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool AluGECore(int op, int bits)
    {
        if (!DecodeE(bits, 1)) return false;
        uint r = Alu(op, bits, LReg(bits, _reg), ReadE(bits));
        if (op != 7) SReg(bits, _reg, r); else TryJcc8E();
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool AluAI(int op, int bits) => bits == 8 ? AluAI8(op) : bits == 16 ? AluAI16(op) : AluAI32(op);
    [MethodImpl(MethodImplOptions.NoInlining)] private bool AluAI8(int op) => AluAICore(op, 8);
    [MethodImpl(MethodImplOptions.NoInlining)] private bool AluAI16(int op) => AluAICore(op, 16);
    [MethodImpl(MethodImplOptions.NoInlining)] private bool AluAI32(int op) => AluAICore(op, 32);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool AluAICore(int op, int bits)
    {
        if (!Fetch(bits, out uint imm)) return false;
        uint r = Alu(op, bits, LReg(bits, 0), imm);
        if (op != 7) SReg(bits, 0, r); else TryJcc8();
        return true;
    }

    /// <summary>Group 1 with an immediate: imm8 (sign-extended when <paramref name="immBits"/> is 8 and bits > 8) or a full immediate.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Grp1(int bits, int immBits) => bits == 8 ? Grp1_8() : immBits == 8 ? (bits == 16 ? Grp1_16_8() : Grp1_32_8()) : bits == 16 ? Grp1_16() : Grp1_32();
    [MethodImpl(MethodImplOptions.NoInlining)] private bool Grp1_8() => Grp1Core(8, 8);
    [MethodImpl(MethodImplOptions.NoInlining)] private bool Grp1_16_8() => Grp1Core(16, 8);
    [MethodImpl(MethodImplOptions.NoInlining)] private bool Grp1_32_8() => Grp1Core(32, 8);
    [MethodImpl(MethodImplOptions.NoInlining)] private bool Grp1_16() => Grp1Core(16, 16);
    [MethodImpl(MethodImplOptions.NoInlining)] private bool Grp1_32() => Grp1Core(32, 32);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Grp1Core(int bits, int immBits)
    {
        if (!DecodeModrm()) return false;
        if (!Fetch(immBits, out uint imm)) return false;
        if (immBits == 8 && bits > 8) imm = Sext8(imm);
        int op = _reg;
        if (!TranslateE(bits, op == 7 ? 1 : 3)) return false;
        uint r = Alu(op, bits, ReadE(bits), imm);
        if (op != 7) WriteE(bits, r); else TryJcc8E();
        return true;
    }

    private void IncDec(int bits, bool dec)
    {
        int cf = GetCF();
        uint x = Sext(bits, ReadE(bits));
        _ccDst = Sext(bits, dec ? x - 1 : x + 1);
        _ccOp = (int)(dec ? (bits == 8 ? Cc.DEC8 : bits == 16 ? Cc.DEC16 : Cc.DEC32) : (bits == 8 ? Cc.INC8 : bits == 16 ? Cc.INC16 : Cc.INC32));
        Flags = SetBit(Flags, cf != 0, CF);
        _ccMask = PF | AF | ZF | SF | OF;
        WriteE(bits, _ccDst);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void IncDecReg(int bits, int reg, bool dec)
    {
        int cf = GetCF();
        uint x = Sext(bits, LReg(bits, reg));
        _ccDst = Sext(bits, dec ? x - 1 : x + 1);
        _ccOp = (int)(dec ? (bits == 16 ? Cc.DEC16 : Cc.DEC32) : (bits == 16 ? Cc.INC16 : Cc.INC32));
        Flags = SetBit(Flags, cf != 0, CF);
        _ccMask = PF | AF | ZF | SF | OF;
        SReg(bits, reg, _ccDst);
    }

    /// <summary>Group 2 shifts and rotates on E (0 ROL 1 ROR 2 RCL 3 RCR 4/6 SHL 5 SHR 7 SAR) by count <paramref name="b"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Grp2(int op, int bits, uint b)
    {
        uint x = ReadE(bits);
        switch (op)
        {
            case 0:
            case 1:
            {
                uint y0 = b;   // tiny386 quirk kept: the flags test uses the unmasked count (the 386 masks it to 5 bits first)
                int y = (int)(y0 & (uint)(bits - 1));
                uint res = x;
                if (y != 0)
                {
                    res = op == 0 ? Sext(bits, (x << y) | (x >> (bits - y))) : Sext(bits, (x >> y) | (x << (bits - y)));
                    WriteE(bits, res);
                }
                if (y0 != 0)
                {
                    int cf1, of1;
                    if (op == 0) { cf1 = (int)(res & 1); of1 = (int)(res >> 31) ^ cf1; }
                    else { cf1 = (int)((res >> (bits - 1)) & 1); of1 = (int)(((res ^ (res << 1)) >> (bits - 1)) & 1); }
                    Flags = SetBit(Flags, cf1 != 0, CF);
                    Flags = SetBit(Flags, of1 != 0, OF);
                    _ccMask &= ~(CF | OF);
                }
                break;
            }
            case 2:
            {
                int y = (int)((b & 0x1f) % (uint)(bits + 1));
                if (y != 0)
                {
                    uint cf = (uint)GetCF();
                    uint res = Sext(bits, (x << y) | (cf << (y - 1)) | (y != 1 ? x >> (bits + 1 - y) : 0));
                    int cf1 = (int)((x >> (bits - y)) & 1);
                    int of1 = (int)(res >> 31) ^ cf1;
                    Flags = SetBit(Flags, cf1 != 0, CF);
                    Flags = SetBit(Flags, of1 != 0, OF);
                    _ccMask &= ~(CF | OF);
                    WriteE(bits, res);
                }
                break;
            }
            case 3:
            {
                int y = (int)((b & 0x1f) % (uint)(bits + 1));
                if (y != 0)
                {
                    uint cf = (uint)GetCF();
                    uint res = Sext(bits, (x >> y) | (cf << (bits - y)) | (y != 1 ? x << (bits + 1 - y) : 0));
                    int cf1 = (int)((Sext(bits, x << (bits - y)) >> (bits - 1)) & 1);
                    int of1 = (int)(((res ^ (res << 1)) >> (bits - 1)) & 1);
                    Flags = SetBit(Flags, cf1 != 0, CF);
                    Flags = SetBit(Flags, of1 != 0, OF);
                    _ccMask &= ~(CF | OF);
                    WriteE(bits, res);
                }
                break;
            }
            case 4:
            case 6:
            {
                int y = (int)(b & 0x1f);
                if (y != 0)
                {
                    _ccDst = Sext(bits, x << y);
                    _ccDst2 = (x >> (bits - y)) & 1;
                    _ccOp = (int)Cc.SHL; _ccMask = LOGIC;
                    WriteE(bits, _ccDst);
                }
                break;
            }
            case 5:
            {
                int y = (int)(b & 0x1f);
                if (y != 0)
                {
                    _ccSrc1 = Sext(bits, x);
                    _ccDst = Sext(bits, x >> y);
                    _ccDst2 = (x >> (y - 1)) & 1;
                    _ccOp = (int)Cc.SHR; _ccMask = LOGIC;
                    WriteE(bits, _ccDst);
                }
                break;
            }
            default:
            {
                int sx = (int)Sext(bits, x);
                int y = (int)(b & 0x1f);
                if (y != 0)
                {
                    _ccDst = (uint)(sx >> y);
                    _ccDst2 = (uint)((sx >> (y - 1)) & 1);
                    _ccOp = (int)Cc.SAR; _ccMask = LOGIC;
                    WriteE(bits, _ccDst);
                }
                break;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Grp2Op(int bits, int countKind) => bits == 8 ? Grp2Op8(countKind) : bits == 16 ? Grp2Op16(countKind) : Grp2Op32(countKind);
    [MethodImpl(MethodImplOptions.NoInlining)] private bool Grp2Op8(int countKind) => Grp2OpCore(8, countKind);
    [MethodImpl(MethodImplOptions.NoInlining)] private bool Grp2Op16(int countKind) => Grp2OpCore(16, countKind);
    [MethodImpl(MethodImplOptions.NoInlining)] private bool Grp2Op32(int countKind) => Grp2OpCore(32, countKind);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Grp2OpCore(int bits, int countKind)   // countKind: 0 = imm8, 1 = one, 2 = CL
    {
        if (!DecodeModrm()) return false;
        uint count = 1;
        if (countKind == 0) { if (!Fetch8(out count)) return false; }
        else if (countKind == 2) count = L8(1);
        if (!TranslateE(bits, 3)) return false;
        Grp2(_reg, bits, count);
        return true;
    }

    private void ShldShrd(bool left, int bits, int count)
    {
        count &= 0x1f;
        uint x = ReadE(bits), y = LReg(bits, _reg);
        if (count == 0) return;
        if (left)
        {
            _ccSrc1 = Sext(bits, x);
            if (bits < count) { (x, y) = (y, x); count -= bits; }
            _ccDst = Sext(bits, (x << count) | (y >> (bits - count)));
            _ccDst2 = count == 1 ? Sext(bits, x) : Sext(bits, (x << (count - 1)) | (y >> (bits - (count - 1))));
            _ccOp = (int)Cc.SHLD;
        }
        else
        {
            if (bits < count) { (x, y) = (y, x); count -= bits; }
            _ccSrc1 = Sext(bits, x);
            _ccDst = Sext(bits, (x >> count) | (y << (bits - count)));
            _ccDst2 = count == 1 ? Sext(bits, x) : Sext(bits, (x >> (count - 1)) | (y << (bits - (count - 1))));
            _ccOp = (int)Cc.SHRD;
        }
        _ccMask = LOGIC;
        WriteE(bits, _ccDst);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Grp3(int bits) => bits == 8 ? Grp38() : bits == 16 ? Grp316() : Grp332();
    [MethodImpl(MethodImplOptions.NoInlining)] private bool Grp38() => Grp3Core(8);
    [MethodImpl(MethodImplOptions.NoInlining)] private bool Grp316() => Grp3Core(16);
    [MethodImpl(MethodImplOptions.NoInlining)] private bool Grp332() => Grp3Core(32);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Grp3Core(int bits)
    {
        if (!DecodeModrm()) return false;
        switch (_reg)
        {
            case 0:
            case 1:
            {
                if (!Fetch(bits, out uint imm)) return false;
                if (!TranslateE(bits, 1)) return false;
                Alu(4, bits, ReadE(bits), imm);
                TryJcc8E();
                return true;
            }
            case 2:
                if (!TranslateE(bits, 3)) return false;
                WriteE(bits, ~ReadE(bits));
                return true;
            case 3:
                if (!TranslateE(bits, 3)) return false;
                _ccSrc1 = Sext(bits, ReadE(bits));
                _ccDst = Sext(bits, 0 - _ccSrc1);
                _ccOp = (int)(bits == 8 ? Cc.NEG8 : bits == 16 ? Cc.NEG16 : Cc.NEG32);
                _ccMask = ALL;
                WriteE(bits, _ccDst);
                return true;
        }
        if (!TranslateE(bits, 1)) return false;
        uint v = ReadE(bits);
        switch (_reg)
        {
            case 4: // MUL
                if (bits == 8) { _ccSrc1 = L8(0); _ccSrc2 = v; _ccDst = Sext16(_ccSrc1 * _ccSrc2); _ccOp = (int)Cc.MUL8; _ccMask = ALL; S16(0, _ccDst); }
                else if (bits == 16) { _ccSrc1 = L16(0); _ccSrc2 = v; _ccDst = _ccSrc1 * _ccSrc2; _ccOp = (int)Cc.MUL16; _ccMask = ALL; S16(0, _ccDst); S16(2, _ccDst >> 16); }
                else
                {
                    _ccSrc1 = R[0]; _ccSrc2 = v; ulong res = (ulong)_ccSrc1 * _ccSrc2;
                    _ccDst = (uint)res; _ccDst2 = (uint)(res >> 32); _ccOp = (int)Cc.MUL32; _ccMask = ALL; R[0] = _ccDst; R[2] = _ccDst2;
                }
                return true;
            case 5: // IMUL
                if (bits == 8) { _ccSrc1 = Sext8(L8(0)); _ccSrc2 = Sext8(v); _ccDst = _ccSrc1 * _ccSrc2; _ccOp = (int)Cc.IMUL8; _ccMask = ALL; S16(0, _ccDst); }
                else if (bits == 16) { _ccSrc1 = Sext16(L16(0)); _ccSrc2 = Sext16(v); _ccDst = _ccSrc1 * _ccSrc2; _ccOp = (int)Cc.IMUL16; _ccMask = ALL; S16(0, _ccDst); S16(2, _ccDst >> 16); }
                else
                {
                    _ccSrc1 = R[0]; _ccSrc2 = v; long res = (long)(int)_ccSrc1 * (int)_ccSrc2;
                    _ccDst = (uint)res; _ccDst2 = (uint)(res >> 32); _ccOp = (int)Cc.IMUL32; _ccMask = ALL; R[0] = _ccDst; R[2] = _ccDst2;
                }
                return true;
            case 6: // DIV
                if (bits == 8)
                {
                    uint s1 = L16(0), s2 = v;
                    if (s2 == 0) return Throw0(EX_DE);
                    uint res = s1 / s2;
                    if (res > 0xff) return Throw0(EX_DE);
                    if (s1 == 5 && s2 == 2) { _ccMask &= ~ZF; Flags |= ZF; } // tiny386: pass the Cyrix 5/2 test
                    S8(0, res); S8(4, s1 % s2);
                }
                else if (bits == 16)
                {
                    uint s1 = L16(0) | (L16(2) << 16), s2 = v;
                    if (s2 == 0) return Throw0(EX_DE);
                    uint res = s1 / s2;
                    if (res > 0xffff) return Throw0(EX_DE);
                    if (s1 == 0x5555 && s2 == 2) { _ccMask &= ~ZF; Flags &= ~ZF; } // tiny386: pass the NexGen test
                    S16(0, res); S16(2, s1 % s2);
                }
                else
                {
                    ulong s1 = ((ulong)R[2] << 32) | R[0], s2 = v;
                    if (s2 == 0) return Throw0(EX_DE);
                    ulong res = s1 / s2;
                    if (res > 0xffffffff) return Throw0(EX_DE);
                    R[0] = (uint)res; R[2] = (uint)(s1 % s2);
                }
                return true;
            default: // IDIV
                if (bits == 8)
                {
                    int s1 = (int)Sext16(L16(0)), s2 = (int)Sext8(v);
                    if (s2 == 0) return Throw0(EX_DE);
                    int res = s1 / s2;
                    if (res > 127 || res < -128) return Throw0(EX_DE);
                    S8(0, (uint)res); S8(4, (uint)(s1 % s2));
                }
                else if (bits == 16)
                {
                    long s1 = (int)(L16(0) | (L16(2) << 16)), s2 = (int)Sext16(v);
                    if (s2 == 0) return Throw0(EX_DE);
                    long res = s1 / s2;
                    if (res > 32767 || res < -32768) return Throw0(EX_DE);
                    S16(0, (uint)res); S16(2, (uint)(s1 % s2));
                }
                else
                {
                    long s1 = (long)(((ulong)R[2] << 32) | R[0]), s2 = (int)v;
                    if (s2 == 0) return Throw0(EX_DE);
                    if (s1 == long.MinValue && s2 == -1) return Throw0(EX_DE);
                    long res = s1 / s2;
                    if (res > int.MaxValue || res < int.MinValue) return Throw0(EX_DE);
                    R[0] = (uint)res; R[2] = (uint)(s1 % s2);
                }
                return true;
        }
    }

    private void Imul2(int bits, int reg, uint a, uint b)
    {
        _ccSrc1 = Sext(bits, a); _ccSrc2 = Sext(bits, b);
        if (bits == 16) { _ccDst = _ccSrc1 * _ccSrc2; _ccOp = (int)Cc.IMUL16; }
        else { long res = (long)(int)_ccSrc1 * (int)_ccSrc2; _ccDst = (uint)res; _ccDst2 = (uint)(res >> 32); _ccOp = (int)Cc.IMUL32; }
        _ccMask = ALL;
        SReg(bits, reg, _ccDst);
    }

    // bit tests: op 0 BT 1 BTS 2 BTR 3 BTC
    private void BitOp(int op, int bits, uint b)
    {
        int bb = (int)(b % (uint)bits);
        uint x = ReadE(bits);
        bool bit = ((x >> bb) & 1) != 0;
        if (op == 1) WriteE(bits, x | (1u << bb));
        else if (op == 2) WriteE(bits, x & ~(1u << bb));
        else if (op == 3) WriteE(bits, x ^ (1u << bb));
        _ccMask &= ~CF;
        Flags = SetBit(Flags, bit, CF);
    }

    private bool BitOpEG(int op, int bits)
    {
        if (!DecodeModrm()) return false;
        uint b = LReg(bits, _reg);
        // the register form addresses outside the operand (tiny386 quirk kept: the offset is unsigned; the 386 treats it as signed)
        if (_mod != 3) _addr += b / (uint)bits * (uint)(bits / 8);
        if (!TranslateE(bits, op == 0 ? 1 : 3)) return false;
        BitOp(op, bits, b);
        return true;
    }

    // ================= stack =================

    private bool Push(int bits, uint val)
    {
        uint sp = R[4], n = (uint)(bits / 8);
        var m = new OptAddr();
        if (!Translate(ref m, 2, SEG_SS, (sp - n) & _spm, bits / 8)) return false;
        SetSp(sp - n, _spm);
        Store(bits, ref m, val);
        return true;
    }

    private bool PushSeg(int seg)
    {
        uint sp = R[4], n = _opsz16 ? 2u : 4u;
        var m = new OptAddr();
        if (!Translate(ref m, 2, SEG_SS, (sp - n) & _spm, 2)) return false;
        SetSp(sp - n, _spm);
        Store16(ref m, SegSel[seg]);
        return true;
    }

    private bool PopSeg(int seg)
    {
        uint sp = R[4], n = _opsz16 ? 2u : 4u;
        var m = new OptAddr();
        if (!Translate(ref m, 1, SEG_SS, sp & _spm, 2)) return false;
        if (!SetSeg(seg, Load16(ref m))) return false;
        SetSp(sp + n, _spm);
        return true;
    }

    private bool PopReg(int bits, int reg)
    {
        uint sp = R[4];
        var m = new OptAddr();
        if (!Translate(ref m, 1, SEG_SS, sp & _spm, bits / 8)) return false;
        uint v = Load(bits, ref m);
        SetSp(sp + (uint)(bits / 8), _spm);
        SReg(bits, reg, v);
        return true;
    }

    private bool PopE()
    {
        int bits = V;
        var m1 = new OptAddr();
        if (!Fetch8(out uint modrm)) return false;
        _mod = (int)(modrm >> 6); _rm = (int)(modrm & 7);
        uint sp = R[4];
        if (!Translate(ref m1, 1, SEG_SS, sp & _spm, bits / 8)) return false;
        uint src = Load(bits, ref m1);
        SetSp(sp + (uint)(bits / 8), _spm);
        if (_mod == 3) SReg(bits, _rm, src);
        else
        {
            if (!Modsib(_adsz16, _mod, _rm, out _addr, ref _curSeg) || !Translate(ref _meml, 2, _curSeg, _addr, bits / 8)) { SetSp(sp, _spm); return false; }
            Store(bits, ref _meml, src);
        }
        return true;
    }

    private bool Pusha()
    {
        int bits = V; uint n = (uint)(bits / 8);
        uint sp = R[4];
        var m = new OptAddr[8];
        for (int i = 0; i < 8; i++) if (!Translate(ref m[i], 2, SEG_SS, (sp - n * (uint)(i + 1)) & _spm, bits / 8)) return false;
        for (int i = 0; i < 8; i++) Store(bits, ref m[i], i == 4 ? sp : LReg(bits, i));
        SetSp(sp - n * 8, _spm);
        return true;
    }

    private bool Popa()
    {
        int bits = V; uint n = (uint)(bits / 8);
        uint sp = R[4];
        var m = new OptAddr[8];
        for (int i = 0; i < 8; i++) { if (i == 3) continue; if (!Translate(ref m[i], 1, SEG_SS, (sp + n * (uint)i) & _spm, bits / 8)) return false; }
        for (int i = 0; i < 8; i++) { if (i == 3) continue; SReg(bits, 7 - i, Load(bits, ref m[i])); }
        SetSp(sp + n * 8, _spm);
        return true;
    }

    private bool PushF()
    {
        if ((Flags & VM) != 0 && GetIOPL() < 3) return Throw(EX_GP, 0);
        uint sp = R[4];
        if (_opsz16)
        {
            if (!Translate(ref _meml, 2, SEG_SS, (sp - 2) & _spm, 2)) return false;
            RefreshFlags(); _ccMask = 0;
            SetSp(sp - 2, _spm);
            Store16(ref _meml, Flags);
        }
        else
        {
            if (!Translate(ref _meml, 2, SEG_SS, (sp - 4) & _spm, 4)) return false;
            RefreshFlags(); _ccMask = 0;
            SetSp(sp - 4, _spm);
            Store32(ref _meml, Flags & ~(RF | VM));
        }
        return true;
    }

    private int PopF()
    {
        if ((Flags & VM) != 0 && GetIOPL() < 3) return K(Throw(EX_GP, 0));
        uint mask = VM;
        if ((Cr0 & 1) != 0)
        {
            if (Cpl > 0) mask |= IOPL;
            if (GetIOPL() < Cpl) mask |= IF;
        }
        uint sp = R[4];
        if (_opsz16)
        {
            if (!Translate(ref _meml, 1, SEG_SS, sp & _spm, 2)) return FAIL;
            SetSp(sp + 2, _spm);
            Flags = (Flags & (0xffff0000 | mask)) | (Load16(ref _meml) & ~mask);
        }
        else
        {
            if (!Translate(ref _meml, 1, SEG_SS, sp & _spm, 4)) return FAIL;
            SetSp(sp + 4, _spm);
            Flags = (Flags & mask) | (Load32(ref _meml) & ~mask);
        }
        Flags &= FlagsMask; Flags |= 2;
        _ccMask = 0;
        return Intr && (Flags & IF) != 0 ? END : OK;
    }

    private bool Enter(uint allocsz, int level)
    {
        level %= 32;
        uint sp = R[4];
        var m = new OptAddr();
        if (level == 0)
        {
            if (_opsz16)
            {
                if (!Translate(ref m, 2, SEG_SS, (sp - 2) & _spm, 2)) return false;
                SetSp(sp - 2 - allocsz, _spm);
                Store16(ref m, L16(5));
                S16(5, (sp - 2) & _spm);
            }
            else
            {
                if (!Translate(ref m, 2, SEG_SS, (sp - 4) & _spm, 4)) return false;
                SetSp(sp - 4 - allocsz, _spm);
                Store32(ref m, R[5]);
                R[5] = (sp - 4) & _spm;
            }
            return true;
        }
        // tiny386 enter_helper
        uint temp;
        if (_opsz16)
        {
            if (!Translate(ref m, 2, SEG_SS, (sp - 2) & _spm, 2)) return false;
            SetSp(sp - 2, _spm); Store16(ref m, L16(5)); temp = L16(4);
        }
        else
        {
            if (!Translate(ref m, 2, SEG_SS, (sp - 4) & _spm, 4)) return false;
            SetSp(sp - 4, _spm); Store32(ref m, R[5]); temp = R[4];
        }
        for (int i = 0; i < level - 1; i++)
        {
            uint d = _opsz16 ? 2u : 4u;
            if (_spm == 0xffff) S16(5, L16(5) - d); else R[5] -= d;
            if (_opsz16)
            {
                if (!Translate(ref m, 2, SEG_SS, (sp - 2) & _spm, 2)) return false;
                SetSp(sp - 2, _spm); Store16(ref m, L16(5));
            }
            else
            {
                if (!Translate(ref m, 2, SEG_SS, (sp - 4) & _spm, 4)) return false;
                SetSp(sp - 4, _spm); Store32(ref m, R[5]);
            }
        }
        if (_opsz16)
        {
            if (!Translate(ref m, 2, SEG_SS, (sp - 2) & _spm, 2)) return false;
            SetSp(sp - 2 - allocsz, _spm); Store16(ref m, temp); S16(5, temp);
        }
        else
        {
            if (!Translate(ref m, 2, SEG_SS, (sp - 4) & _spm, 4)) return false;
            SetSp(sp - 4 - allocsz, _spm); Store32(ref m, temp); R[5] = temp;
        }
        return true;
    }

    private bool Leave()
    {
        uint sp = R[5];
        if (_opsz16)
        {
            if (!Translate(ref _meml, 1, SEG_SS, sp & _spm, 2)) return false;
            SetSp(sp + 2, _spm); S16(5, Load16(ref _meml));
        }
        else
        {
            if (!Translate(ref _meml, 1, SEG_SS, sp & _spm, 4)) return false;
            SetSp(sp + 4, _spm); R[5] = Load32(ref _meml);
        }
        return true;
    }

    private bool Ret(uint imm)
    {
        uint sp = R[4];
        if (_opsz16)
        {
            if (!Translate(ref _meml, 1, SEG_SS, sp & _spm, 2)) return false;
            SetSp(sp + 2 + imm, _spm); NextIp = Load16(ref _meml);
        }
        else
        {
            if (!Translate(ref _meml, 1, SEG_SS, sp & _spm, 4)) return false;
            SetSp(sp + 4 + imm, _spm); NextIp = Load32(ref _meml);
        }
        return true;
    }

    private bool CallNear(uint target, bool relative)
    {
        int bits = V;
        if (!Push(bits, NextIp)) return false;
        if (relative) JumpRel(target); else NextIp = target;
        return true;
    }

    // ================= far transfers (real / V86 parts; protected mode in I386.System.cs) =================

    private bool JmpFar(uint addr, uint seg)
    {
        if ((Cr0 & 1) != 0 && (Flags & VM) == 0) return PmCall(_opsz16, addr, seg, true);
        if (!SetSeg(SEG_CS, seg)) return false;
        NextIp = addr;
        return true;
    }

    private bool CallFar(uint addr, uint seg)
    {
        if ((Cr0 & 1) != 0 && (Flags & VM) == 0) return PmCall(_opsz16, addr, seg, false);
        var m1 = new OptAddr(); var m2 = new OptAddr();
        uint sp = R[4];
        if (_opsz16)
        {
            if (!Translate(ref m1, 2, SEG_SS, (sp - 2) & _spm, 2) || !Translate(ref m2, 2, SEG_SS, (sp - 4) & _spm, 2)) return false;
            SetSp(sp - 4, _spm); Store16(ref m1, SegSel[SEG_CS]); Store16(ref m2, NextIp);
        }
        else
        {
            if (!Translate(ref m1, 2, SEG_SS, (sp - 4) & _spm, 4) || !Translate(ref m2, 2, SEG_SS, (sp - 8) & _spm, 4)) return false;
            SetSp(sp - 8, _spm); Store32(ref m1, SegSel[SEG_CS]); Store32(ref m2, NextIp);
        }
        if (!SetSeg(SEG_CS, seg)) return false;
        NextIp = addr;
        return true;
    }

    private bool RetFar(uint imm)
    {
        if ((Cr0 & 1) != 0 && (Flags & VM) == 0) return PmRet(_opsz16, (int)imm, false);
        var m1 = new OptAddr(); var m2 = new OptAddr();
        uint sp = R[4];
        int n = _opsz16 ? 2 : 4;
        if (!Translate(ref m1, 1, SEG_SS, sp & _spm, n)) return false;
        uint newip = Load(n * 8, ref m1);
        if (!Translate(ref m2, 1, SEG_SS, (sp + (uint)n) & _spm, n)) return false;
        uint newcs = Load(n * 8, ref m2);
        if (!SetSeg(SEG_CS, newcs)) return false;
        SetSp(sp + (uint)(2 * n) + imm, _spm);
        NextIp = newip;
        return true;
    }

    private int Iret()
    {
        if ((Cr0 & 1) != 0 && ((Flags & VM) == 0 || GetIOPL() < 3))
        {
            if (!PmRet(_opsz16, 0, true)) return FAIL;
        }
        else
        {
            var m1 = new OptAddr(); var m2 = new OptAddr(); var m3 = new OptAddr();
            uint sp = R[4];
            int n = _opsz16 ? 2 : 4;   // tiny386 aborts on a real-mode IRETD; done here the 386 way
            if (!Translate(ref m1, 1, SEG_SS, sp & _spm, n)) return FAIL;
            uint newip = Load(n * 8, ref m1);
            if (!Translate(ref m2, 1, SEG_SS, (sp + (uint)n) & _spm, n)) return FAIL;
            uint newcs = Load(n * 8, ref m2) & 0xffff;
            if (!Translate(ref m3, 1, SEG_SS, (sp + (uint)(2 * n)) & _spm, n)) return FAIL;
            uint oldflags = Flags;
            uint nf = Load(n * 8, ref m3);
            if (n == 2)
            {
                if ((Flags & VM) != 0) Flags = (Flags & (0xffff0000 | IOPL)) | (nf & ~IOPL);
                else Flags = (Flags & 0xffff0000) | nf;
            }
            else
            {
                if ((Flags & VM) != 0) Flags = (Flags & (0xffff0000 | IOPL)) | (nf & 0xffff & ~IOPL);
                else Flags = (Flags & (VM | 0x80000 | 0x100000)) | (nf & ~(VM | 0x80000 | 0x100000));
            }
            Flags &= FlagsMask; Flags |= 2;
            if (!SetSeg(SEG_CS, newcs)) { Flags = oldflags; return FAIL; }
            _ccMask = 0;
            SetSp(sp + (uint)(3 * n), _spm);
            NextIp = newip;
        }
        return Intr && (Flags & IF) != 0 ? END : OK;
    }

    private bool Int(uint no)
    {
        if ((Flags & VM) != 0 && GetIOPL() < 3) return Throw(EX_GP, 0);
        uint oldip = Ip;
        Ip = NextIp;
        if (!CallIsr((int)no, false, 0)) { Ip = oldip; return false; }
        return true;
    }

    private bool LoadFarPtr(int seg, int reg)
    {
        // tiny386 __LSEGw_helper / __LSEGd_helper
        uint addr = _addr;
        if (_adsz16) addr &= 0xffff;
        var m1 = new OptAddr(); var m2 = new OptAddr();
        int bits = V, n = bits / 8;
        if (!Translate(ref m1, 1, _curSeg, addr, n)) return false;
        if (!Translate(ref m2, 1, _curSeg, addr + (uint)n, 2)) return false;
        uint r = Load(bits, ref m1), s = Load16(ref m2);
        if (!SetSeg(seg, s)) return false;
        SReg(bits, reg, r);
        return true;
    }

    // ================= string instructions =================

    private int Dir(int bits) => (Flags & DF) != 0 ? -bits / 8 : bits / 8;
    private void Adv(int reg, int dir) => SReg(A, reg, (uint)((int)LReg(A, reg) + dir));
    private bool CxNonZero() => LReg(A, 1) != 0;
    private void CxDec() => SReg(A, 1, LReg(A, 1) - 1);

    private bool Movs(int bits)
    {
        if (_curSeg == -1) _curSeg = SEG_DS;
        int dir = Dir(bits), n = bits / 8;
        var ms = new OptAddr(); var md = new OptAddr();
        while (_rep == 0 || CxNonZero())
        {
            if (!Translate(ref ms, 1, _curSeg, LReg(A, 6), n)) return false;
            uint v = Load(bits, ref ms);
            if (!Translate(ref md, 2, SEG_ES, LReg(A, 7), n)) return false;
            Store(bits, ref md, v);
            Adv(6, dir); Adv(7, dir);
            if (_rep == 0) break;
            CxDec();
        }
        return true;
    }

    private bool Stos(int bits)
    {
        int dir = Dir(bits), n = bits / 8;
        uint ax = R[0];
        var md = new OptAddr();
        while (_rep == 0 || CxNonZero())
        {
            if (!Translate(ref md, 2, SEG_ES, LReg(A, 7), n)) return false;
            Store(bits, ref md, ax);
            Adv(7, dir);
            if (_rep == 0) break;
            CxDec();
        }
        return true;
    }

    private bool Lods(int bits)
    {
        if (_curSeg == -1) _curSeg = SEG_DS;
        int dir = Dir(bits), n = bits / 8;
        var ms = new OptAddr();
        while (_rep == 0 || CxNonZero())
        {
            if (!Translate(ref ms, 1, _curSeg, LReg(A, 6), n)) return false;
            SReg(bits, 0, Load(bits, ref ms));
            Adv(6, dir);
            if (_rep == 0) break;
            CxDec();
        }
        return true;
    }

    private bool Scas(int bits)
    {
        int dir = Dir(bits), n = bits / 8;
        uint ax0 = LReg(bits, 0);
        var md = new OptAddr();
        while (_rep == 0 || CxNonZero())
        {
            if (!Translate(ref md, 1, SEG_ES, LReg(A, 7), n)) return false;
            uint ax = Load(bits, ref md);
            Adv(7, dir);
            if (_rep != 0) CxDec();
            SetSub(bits, ax0, ax);
            if (_rep == 0) break;
            bool zf = GetZF() != 0;
            if ((zf && _rep == 2) || (!zf && _rep == 1)) break;
        }
        return true;
    }

    private bool Cmps(int bits)
    {
        if (_curSeg == -1) _curSeg = SEG_DS;
        int dir = Dir(bits), n = bits / 8;
        var ms = new OptAddr();
        while (_rep == 0 || CxNonZero())
        {
            if (!Translate(ref ms, 1, _curSeg, LReg(A, 6), n)) return false;
            uint ax0 = Load(bits, ref ms);
            if (!Translate(ref ms, 1, SEG_ES, LReg(A, 7), n)) return false;
            uint ax = Load(bits, ref ms);
            Adv(6, dir); Adv(7, dir);
            if (_rep != 0) CxDec();
            SetSub(bits, ax0, ax);
            if (_rep == 0) break;
            bool zf = GetZF() != 0;
            if ((zf && _rep == 2) || (!zf && _rep == 1)) break;
        }
        return true;
    }

    private bool Ins(int bits)
    {
        if (!CheckIoperm((int)L16(2), bits)) return false;
        if (_rep != 0 && _rep != 1) return Throw0(EX_UD);
        int dir = Dir(bits), n = bits / 8;
        var md = new OptAddr();
        while (_rep == 0 || CxNonZero())
        {
            if (!Translate(ref md, 2, SEG_ES, LReg(A, 7), n)) return false;
            Store(bits, ref md, PortIn(bits, (int)L16(2)));
            Adv(7, dir);
            if (_rep == 0) break;
            CxDec();
        }
        return true;
    }

    private bool Outs(int bits)
    {
        if (_curSeg == -1) _curSeg = SEG_DS;
        if (!CheckIoperm((int)L16(2), bits)) return false;
        if (_rep != 0 && _rep != 1) return Throw0(EX_UD);
        int dir = Dir(bits), n = bits / 8;
        var ms = new OptAddr();
        while (_rep == 0 || CxNonZero())
        {
            if (!Translate(ref ms, 1, _curSeg, LReg(A, 6), n)) return false;
            PortOut(bits, (int)L16(2), Load(bits, ref ms));
            Adv(6, dir);
            if (_rep == 0) break;
            CxDec();
        }
        return true;
    }

    private uint PortIn(int bits, int port) => bits == 8 ? _bus.In8(port) : bits == 16 ? _bus.In16(port) : _bus.In32(port);
    private void PortOut(int bits, int port, uint v) { if (bits == 8) _bus.Out8(port, (byte)v); else if (bits == 16) _bus.Out16(port, (ushort)v); else _bus.Out32(port, v); }

    private bool In(int bits, int port)
    {
        if (!CheckIoperm(port, bits)) return false;
        SReg(bits, 0, PortIn(bits, port));
        return true;
    }

    private bool Out(int bits, int port)
    {
        if (!CheckIoperm(port, bits)) return false;
        PortOut(bits, port, LReg(bits, 0));
        return true;
    }

    // ================= BCD =================

    private void Daa()
    {
        uint al = L8(0); int cf = GetCF();
        Flags &= ~CF;
        if ((al & 0xf) > 9 || GetAF() != 0) { S8(0, al + 6); if (cf != 0 || al > 0xff - 6) Flags |= CF; Flags |= AF; }
        else Flags &= ~AF;
        if (al > 0x99 || cf != 0) { S8(0, L8(0) + 0x60); Flags |= CF; }
        _ccDst = Sext8(L8(0)); _ccMask = ZF | SF | PF;
    }

    private void Das()
    {
        uint al = L8(0); int cf = GetCF();
        Flags &= ~CF;
        if ((al & 0xf) > 9 || GetAF() != 0) { S8(0, al - 6); if (cf != 0 || al < 6) Flags |= CF; Flags |= AF; }
        else Flags &= ~AF;
        if (al > 0x99 || cf != 0) { S8(0, L8(0) - 0x60); Flags |= CF; }
        _ccDst = Sext8(L8(0)); _ccMask = ZF | SF | PF;
    }

    private void Aaa()
    {
        if ((L8(0) & 0xf) > 9 || GetAF() != 0) { S16(0, L16(0) + 0x106); Flags |= AF | CF; }
        else Flags &= ~(AF | CF);
        _ccMask = ZF | SF | PF;
        S8(0, L8(0) & 0xf);
    }

    private void Aas()
    {
        if ((L8(0) & 0xf) > 9 || GetAF() != 0) { S16(0, L16(0) - 6); S8(4, L8(4) - 1); Flags |= AF | CF; }
        else Flags &= ~(AF | CF);
        _ccMask = ZF | SF | PF;
        S8(0, L8(0) & 0xf);
    }

    // ================= the decoder =================

    private int ExecOne(int b1)
    {
        if (b1 < 0x40 && (b1 & 7) < 6)
        {
            int op = b1 >> 3;
            switch (b1 & 7)
            {
                case 0: return K(AluEG(op, 8));
                case 1: return K(AluEG(op, V));
                case 2: return K(AluGE(op, 8));
                case 3: return K(AluGE(op, V));
                case 4: return K(AluAI(op, 8));
                default: return K(AluAI(op, V));
            }
        }
        uint imm, imm2;
        switch (b1)
        {
            case 0x06: return K(PushSeg(SEG_ES));
            case 0x07: return K(PopSeg(SEG_ES));
            case 0x0e: return K(PushSeg(SEG_CS));
            case 0x0f: return ExecTwo();
            case 0x16: return K(PushSeg(SEG_SS));
            case 0x17: if (!PopSeg(SEG_SS)) return FAIL; _steps++; return OK;
            case 0x1e: return K(PushSeg(SEG_DS));
            case 0x1f: return K(PopSeg(SEG_DS));
            case 0x27: Daa(); return OK;
            case 0x2f: Das(); return OK;
            case 0x37: Aaa(); return OK;
            case 0x3f: Aas(); return OK;

            case 0x40: case 0x41: case 0x42: case 0x43: case 0x44: case 0x45: case 0x46: case 0x47: IncDecReg(V, b1 & 7, false); return OK;
            case 0x48: case 0x49: case 0x4a: case 0x4b: case 0x4c: case 0x4d: case 0x4e: case 0x4f: IncDecReg(V, b1 & 7, true); return OK;
            case 0x50: case 0x51: case 0x52: case 0x53: case 0x54: case 0x55: case 0x56: case 0x57: return K(Push(V, LReg(V, b1 & 7)));
            case 0x58: case 0x59: case 0x5a: case 0x5b: case 0x5c: case 0x5d: case 0x5e: case 0x5f: return K(PopReg(V, b1 & 7));

            case 0x60: return K(Pusha());
            case 0x61: return K(Popa());
            case 0x62: // BOUND Gv, Ma
            {
                if (!DecodeModrm()) return FAIL;
                if (_mod == 3) return K(Throw0(EX_UD));
                int bits = V; int n = bits / 8;
                var m1 = new OptAddr(); var m2 = new OptAddr();
                int idx = (int)Sext(bits, LReg(bits, _reg));
                if (!Translate(ref m1, 3, _curSeg, _addr, n) || !Translate(ref m2, 3, _curSeg, _addr + (uint)n, n)) return FAIL;
                int lo = (int)Sext(bits, Load(bits, ref m1)), hi = (int)Sext(bits, Load(bits, ref m2));
                if (idx < lo || idx > hi) return K(Throw0(EX_BR));
                return OK;
            }
            case 0x63: // ARPL Ew, Gw
            {
                if (!DecodeModrm()) return FAIL;
                if ((Cr0 & 1) == 0 || (Flags & VM) != 0) return K(Throw0(EX_UD));
                if (!TranslateE(16, 0)) return FAIL;
                uint dst = ReadE(16), src = L16(_reg);
                if ((dst & 3) < (src & 3)) { Flags |= ZF; WriteE(16, (dst & ~3u) | (src & 3)); }
                else Flags &= ~ZF;
                _ccMask &= ~ZF;
                return OK;
            }
            case 0x68: if (!Fetch(V, out imm)) return FAIL; return K(Push(V, imm));
            case 0x69:
            case 0x6b:
            {
                int bits = V;
                if (!DecodeModrm()) return FAIL;
                if (!Fetch(b1 == 0x6b ? 8 : bits, out imm)) return FAIL;
                if (b1 == 0x6b) imm = Sext8(imm);
                if (!TranslateE(bits, 1)) return FAIL;
                Imul2(bits, _reg, ReadE(bits), imm);
                return OK;
            }
            case 0x6a: if (!Fetch8(out imm)) return FAIL; return K(Push(V, Sext8(imm)));
            case 0x6c: return K(Ins(8));
            case 0x6d: return K(Ins(V));
            case 0x6e: return K(Outs(8));
            case 0x6f: return K(Outs(V));
            case 0x70: case 0x71: case 0x72: case 0x73: case 0x74: case 0x75: case 0x76: case 0x77: case 0x78: case 0x79: case 0x7a: case 0x7b: case 0x7c: case 0x7d: case 0x7e: case 0x7f:
                if (!Fetch8(out imm)) return FAIL;
                if (Cond((uint)b1)) JumpRel(Sext8(imm));
                return OK;

            case 0x80: case 0x82: return K(Grp1(8, 8));
            case 0x81: return K(Grp1(V, V));
            case 0x83: return K(Grp1(V, 8));
            case 0x84: case 0x85:
            {
                int bits = b1 == 0x84 ? 8 : V;
                if (!DecodeE(bits, 1)) return FAIL;
                Alu(4, bits, ReadE(bits), LReg(bits, _reg));
                TryJcc8E();
                return OK;
            }
            case 0x86: case 0x87:
            {
                int bits = b1 == 0x86 ? 8 : V;
                if (!DecodeE(bits, 3)) return FAIL;
                uint tmp = LReg(bits, _reg);
                SReg(bits, _reg, ReadE(bits));
                WriteE(bits, tmp);
                return OK;
            }
            case 0x88: return K(MovEG8());
            case 0x89: return K(_opsz16 ? MovEG16() : MovEG32());
            case 0x8a: return K(MovGE8());
            case 0x8b: return K(_opsz16 ? MovGE16() : MovGE32());
            case 0x8c: // MOV Ew, Sw
            {
                if (!DecodeModrm()) return FAIL;
                if (_reg > 5) return K(Throw0(EX_UD));
                if (_mod == 3) { SReg(V, _rm, SegSel[_reg] & 0xffff); return OK; }
                if (!TranslateE(16, 2)) return FAIL;
                Store16(ref _meml, SegSel[_reg]);
                return OK;
            }
            case 0x8d: // LEA
                if (!DecodeModrm()) return FAIL;
                if (_mod == 3) return K(Throw0(EX_UD));
                SReg(V, _reg, _addr);
                return OK;
            case 0x8e: // MOV Sw, Ew
            {
                if (!DecodeE(16, 1)) return FAIL;
                if (_reg == SEG_CS || _reg > 5) return K(Throw0(EX_UD));
                if (_reg == SEG_SS) _steps++;
                return K(SetSeg(_reg, ReadE(16)));
            }
            case 0x8f: return K(PopE());
            case 0x90: return OK;
            case 0x91: case 0x92: case 0x93: case 0x94: case 0x95: case 0x96: case 0x97:
            {
                int bits = V, reg = b1 & 7;
                uint tmp = LReg(bits, reg); SReg(bits, reg, LReg(bits, 0)); SReg(bits, 0, tmp);
                return OK;
            }
            case 0x98: if (_opsz16) S16(0, Sext8(L8(0))); else R[0] = Sext16(L16(0)); return OK;
            case 0x99: if (_opsz16) S16(2, 0u - (Sext16(L16(0)) >> 31)); else R[2] = 0u - (R[0] >> 31); return OK;
            case 0x9a:
                if (!Fetch(V, out imm) || !Fetch16(out imm2)) return FAIL;
                return K(CallFar(imm, imm2));
            case 0x9b: if ((Cr0 & 0xa) == 0xa) return K(Throw0(EX_NM)); return OK;
            case 0x9c: return K(PushF());
            case 0x9d: return PopF();
            case 0x9e: // SAHF
                _ccMask &= OF;
                Flags = (Flags & 0xffffff00) | L8(4);
                Flags &= FlagsMask; Flags |= 2;
                return OK;
            case 0x9f: RefreshFlags(); _ccMask = 0; S8(4, Flags); return OK;

            case 0xa0: case 0xa1: case 0xa2: case 0xa3:
            {
                if (_adsz16) { if (!Fetch16(out _addr)) return FAIL; } else if (!Fetch32(out _addr)) return FAIL;
                if (_curSeg == -1) _curSeg = SEG_DS;
                int bits = (b1 & 1) == 0 ? 8 : V;
                if (!Translate(ref _meml, b1 < 0xa2 ? 1 : 2, _curSeg, _addr, bits / 8)) return FAIL;
                if (b1 < 0xa2) SReg(bits, 0, Load(bits, ref _meml)); else Store(bits, ref _meml, LReg(bits, 0));
                return OK;
            }
            case 0xa4: return K(Movs(8));
            case 0xa5: return K(Movs(V));
            case 0xa6: return K(Cmps(8));
            case 0xa7: return K(Cmps(V));
            case 0xa8: case 0xa9:
            {
                int bits = b1 == 0xa8 ? 8 : V;
                if (!Fetch(bits, out imm)) return FAIL;
                Alu(4, bits, LReg(bits, 0), imm);
                TryJcc8();
                return OK;
            }
            case 0xaa: return K(Stos(8));
            case 0xab: return K(Stos(V));
            case 0xac: return K(Lods(8));
            case 0xad: return K(Lods(V));
            case 0xae: return K(Scas(8));
            case 0xaf: return K(Scas(V));
            case 0xb0: case 0xb1: case 0xb2: case 0xb3: case 0xb4: case 0xb5: case 0xb6: case 0xb7: if (!Fetch8(out imm)) return FAIL; S8(b1 & 7, imm); return OK;
            case 0xb8: case 0xb9: case 0xba: case 0xbb: case 0xbc: case 0xbd: case 0xbe: case 0xbf: if (!Fetch(V, out imm)) return FAIL; SReg(V, b1 & 7, imm); return OK;

            case 0xc0: return K(Grp2Op(8, 0));
            case 0xc1: return K(Grp2Op(V, 0));
            case 0xc2: if (!Fetch16(out imm)) return FAIL; return K(Ret(imm));
            case 0xc3: return K(Ret(0));
            case 0xc4: case 0xc5:
                if (!DecodeModrm()) return FAIL;
                if (_mod == 3) return K(Throw0(EX_UD));
                return K(LoadFarPtr(b1 == 0xc4 ? SEG_ES : SEG_DS, _reg));
            case 0xc6: case 0xc7:
            {
                int bits = b1 == 0xc6 ? 8 : V;
                if (!DecodeModrm()) return FAIL;
                if (!Fetch(bits, out imm)) return FAIL;
                if (!TranslateE(bits, 2)) return FAIL;
                WriteE(bits, imm);
                return OK;
            }
            case 0xc8:
                if (!Fetch16(out imm) || !Fetch8(out imm2)) return FAIL;
                return K(Enter(imm, (int)imm2));
            case 0xc9: return K(Leave());
            case 0xca: if (!Fetch16(out imm)) return FAIL; return K(RetFar(imm));
            case 0xcb: return K(RetFar(0));
            case 0xcc: Ip = NextIp; return K(Throw0(EX_BP));
            case 0xcd: if (!Fetch8(out imm)) return FAIL; return K(Int(imm));
            case 0xce: if (GetOF() != 0) { Ip = NextIp; return K(Throw0(EX_OF)); } return OK;
            case 0xcf: return Iret();

            case 0xd0: return K(Grp2Op(8, 1));
            case 0xd1: return K(Grp2Op(V, 1));
            case 0xd2: return K(Grp2Op(8, 2));
            case 0xd3: return K(Grp2Op(V, 2));
            case 0xd4: // AAM
            {
                if (!Fetch8(out imm)) return FAIL;
                if (imm == 0) return K(Throw0(EX_DE));
                uint al = L8(0), res = al % imm;
                S8(4, al / imm); S8(0, res);
                Flags &= ~(OF | AF | CF);
                _ccDst = Sext8(res); _ccMask = ZF | SF | PF;
                return OK;
            }
            case 0xd5: // AAD
            {
                if (!Fetch8(out imm)) return FAIL;
                uint res = (L8(0) + L8(4) * imm) & 0xff;
                S8(0, res); S8(4, 0);
                Flags &= ~(OF | AF | CF);
                _ccDst = Sext8(res); _ccMask = ZF | SF | PF;
                return OK;
            }
            case 0xd6: S8(0, GetCF() != 0 ? 0xffu : 0u); return OK;
            case 0xd7: // XLAT
            {
                if (_curSeg == -1) _curSeg = SEG_DS;
                uint a = _adsz16 ? (L16(3) + L8(0)) & 0xffff : R[3] + L8(0);
                if (!Translate(ref _meml, 1, _curSeg, a, 1)) return FAIL;
                S8(0, Load8(ref _meml));
                return OK;
            }
            case 0xd8: case 0xd9: case 0xda: case 0xdb: case 0xdc: case 0xdd: case 0xde: case 0xdf: // ESC: no FPU
                if ((Cr0 & 0xc) != 0) return K(Throw0(EX_NM));
                return K(DecodeModrm());

            case 0xe0: case 0xe1: case 0xe2:
            {
                if (!Fetch8(out imm)) return FAIL;
                SReg(A, 1, LReg(A, 1) - 1);
                bool go = LReg(A, 1) != 0 && (b1 == 0xe2 || (b1 == 0xe1 ? GetZF() != 0 : GetZF() == 0));
                if (go) JumpRel(Sext8(imm));
                return OK;
            }
            case 0xe3: if (!Fetch8(out imm)) return FAIL; if (LReg(A, 1) == 0) JumpRel(Sext8(imm)); return OK;
            case 0xe4: if (!Fetch8(out imm)) return FAIL; return K(In(8, (int)imm));
            case 0xe5: if (!Fetch8(out imm)) return FAIL; return K(In(V, (int)imm));
            case 0xe6: if (!Fetch8(out imm)) return FAIL; return K(Out(8, (int)imm));
            case 0xe7: if (!Fetch8(out imm)) return FAIL; return K(Out(V, (int)imm));
            case 0xe8: if (!Fetch(V, out imm)) return FAIL; return K(CallNear(Sext(V, imm), true));
            case 0xe9: if (!Fetch(V, out imm)) return FAIL; JumpRel(Sext(V, imm)); return OK;
            case 0xea: if (!Fetch(V, out imm) || !Fetch16(out imm2)) return FAIL; return K(JmpFar(imm, imm2));
            case 0xeb: if (!Fetch8(out imm)) return FAIL; JumpRel(Sext8(imm)); return OK;
            case 0xec: return K(In(8, (int)L16(2)));
            case 0xed: return K(In(V, (int)L16(2)));
            case 0xee: return K(Out(8, (int)L16(2)));
            case 0xef: return K(Out(V, (int)L16(2)));

            case 0xf4: if (Cpl != 0) return K(Throw(EX_GP, 0)); Halt = true; return END;
            case 0xf5: { int cf = GetCF(); _ccMask &= ~CF; Flags = SetBit(Flags, cf == 0, CF); return OK; }
            case 0xf6: return K(Grp3(8));
            case 0xf7: return K(Grp3(V));
            case 0xf8: _ccMask &= ~CF; Flags &= ~CF; return OK;
            case 0xf9: _ccMask &= ~CF; Flags |= CF; return OK;
            case 0xfa: if (GetIOPL() < Cpl) return K(Throw(EX_GP, 0)); Flags &= ~IF; return OK;
            case 0xfb:
                if (GetIOPL() < Cpl) return K(Throw(EX_GP, 0));
                Flags |= IF;
                if (Intr || _steps < 2) _steps = 2;   // interrupts open after the next instruction
                return OK;
            case 0xfc: Flags &= ~DF; return OK;
            case 0xfd: Flags |= DF; return OK;
            case 0xfe:
                if (!DecodeModrm()) return FAIL;
                if (_reg > 1) return K(Throw0(EX_UD));
                if (!TranslateE(8, 3)) return FAIL;
                IncDec(8, _reg == 1);
                return OK;
            case 0xff: return Grp5();
        }
        return K(Throw0(EX_UD));
    }

    private int Grp5()
    {
        if (!DecodeModrm()) return FAIL;
        int bits = V;
        switch (_reg)
        {
            case 0: case 1:
                if (!TranslateE(bits, 3)) return FAIL;
                IncDec(bits, _reg == 1);
                return OK;
            case 2: case 4: case 6:
            {
                if (!TranslateE(bits, 1)) return FAIL;
                uint v = ReadE(bits);
                if (_reg == 2) return K(CallNear(v, false));
                if (_reg == 4) { NextIp = v; return OK; }
                return K(Push(bits, v));
            }
            case 3: case 5:
            {
                if (_mod == 3) return K(Throw0(EX_UD));
                var mo = new OptAddr(); var ms = new OptAddr();
                int n = bits / 8;
                if (!Translate(ref mo, 1, _curSeg, _addr, n) || !Translate(ref ms, 1, _curSeg, _addr + (uint)n, 2)) return FAIL;
                uint off = Load(bits, ref mo), seg = Load16(ref ms);
                return K(_reg == 3 ? CallFar(off, seg) : JmpFar(off, seg));
            }
        }
        return K(Throw0(EX_UD));
    }

    // ================= 0F xx =================

    private int ExecTwo()
    {
        if (!Fetch8(out uint b1)) return FAIL;
        uint imm;
        switch (b1)
        {
            case 0x00: return Grp6();
            case 0x01: return Grp7();
            case 0x02: case 0x03: // LAR / LSL Gv, Ew
            {
                if (!DecodeE(16, 1)) return FAIL;
                uint sel = ReadE(16);
                if (!LarLsl(sel, b1 == 0x02, out uint res, out bool zf)) return FAIL;
                if (zf) { SReg(V, _reg, res); Flags |= ZF; } else Flags &= ~ZF;
                _ccMask &= ~ZF;
                return OK;
            }
            case 0x06: Cr0 &= ~(1u << 3); return OK;   // CLTS
            case 0x09: return OK;                     // WBINVD
            case 0x20: // MOV r32, CRn
            {
                if (Cpl != 0) return K(Throw(EX_GP, 0));
                if (!Fetch8(out uint modrm)) return FAIL;
                int reg = (int)((modrm >> 3) & 7), rm = (int)(modrm & 7);
                switch (reg)
                {
                    case 0: R[rm] = Cr0; break;
                    case 2: R[rm] = Cr2; break;
                    case 3: R[rm] = Cr3; break;
                    case 4: R[rm] = 0; break;
                    default: return K(Throw0(EX_UD));
                }
                return OK;
            }
            case 0x22: // MOV CRn, r32
            {
                if (Cpl != 0) return K(Throw(EX_GP, 0));
                if (!Fetch8(out uint modrm)) return FAIL;
                int reg = (int)((modrm >> 3) & 7), rm = (int)(modrm & 7);
                switch (reg)
                {
                    case 0:
                        uint nc = R[rm];
                        if (((nc ^ Cr0) & (CR0_PG | CR0_WP | 1)) != 0) TlbClear();
                        Cr0 = nc;
                        break;
                    case 2: Cr2 = R[rm]; break;
                    case 3: Cr3 = R[rm]; TlbClear(); break;
                    case 4: break;
                    default: return K(Throw0(EX_UD));
                }
                return OK;
            }
            case 0x21: { if (!Fetch8(out uint modrm)) return FAIL; R[modrm & 7] = Dr[(modrm >> 3) & 7]; return OK; }
            case 0x23: { if (!Fetch8(out uint modrm)) return FAIL; Dr[(modrm >> 3) & 7] = R[modrm & 7]; return OK; }
            case 0x24: case 0x26: return K(Fetch8(out _));
            case 0x31: R[0] = (uint)Cycle; R[2] = (uint)((ulong)Cycle >> 32); return OK; // RDTSC: the instruction count (deterministic; tiny386 reads the host clock)
            case 0x40: case 0x41: case 0x42: case 0x43: case 0x44: case 0x45: case 0x46: case 0x47: case 0x48: case 0x49: case 0x4a: case 0x4b: case 0x4c: case 0x4d: case 0x4e: case 0x4f: // CMOVcc (P6)
            {
                if (Gen <= 5) return K(Throw0(EX_UD));
                if (!DecodeE(V, 1)) return FAIL;
                uint v = ReadE(V);
                if (Cond(b1)) SReg(V, _reg, v);
                return OK;
            }
            case 0x80: case 0x81: case 0x82: case 0x83: case 0x84: case 0x85: case 0x86: case 0x87: case 0x88: case 0x89: case 0x8a: case 0x8b: case 0x8c: case 0x8d: case 0x8e: case 0x8f:
                if (!Fetch(V, out imm)) return FAIL;
                if (Cond(b1)) JumpRel(Sext(V, imm));
                return OK;
            case 0x90: case 0x91: case 0x92: case 0x93: case 0x94: case 0x95: case 0x96: case 0x97: case 0x98: case 0x99: case 0x9a: case 0x9b: case 0x9c: case 0x9d: case 0x9e: case 0x9f:
                if (!DecodeE(8, 2)) return FAIL;
                WriteE(8, Cond(b1) ? 1u : 0u);
                return OK;
            case 0xa0: return K(PushSeg(SEG_FS));
            case 0xa1: return K(PopSeg(SEG_FS));
            case 0xa2: Cpuid(); return OK;
            case 0xa3: return K(BitOpEG(0, V));
            case 0xab: return K(BitOpEG(1, V));
            case 0xb3: return K(BitOpEG(2, V));
            case 0xbb: return K(BitOpEG(3, V));
            case 0xa4: case 0xa5: case 0xac: case 0xad:
            {
                int bits = V;
                if (!DecodeModrm()) return FAIL;
                uint count;
                if ((b1 & 1) == 0) { if (!Fetch8(out count)) return FAIL; } else count = L8(1);
                if (!TranslateE(bits, 3)) return FAIL;
                ShldShrd(b1 < 0xa8, bits, (int)count);
                return OK;
            }
            case 0xa8: return K(PushSeg(SEG_GS));
            case 0xa9: return K(PopSeg(SEG_GS));
            case 0xaf:
                if (!DecodeE(V, 1)) return FAIL;
                Imul2(V, _reg, LReg(V, _reg), ReadE(V));
                return OK;
            case 0xb0: case 0xb1: // CMPXCHG
            {
                int bits = b1 == 0xb0 ? 8 : V;
                if (!DecodeE(bits, 3)) return FAIL;
                uint e = ReadE(bits);
                SetSub(bits, e, LReg(bits, 0));
                if (_ccDst == 0) WriteE(bits, LReg(bits, _reg)); else SReg(bits, 0, _ccSrc1);
                return OK;
            }
            case 0xb2: case 0xb4: case 0xb5:
                if (!DecodeModrm()) return FAIL;
                if (_mod == 3) return K(Throw0(EX_UD));
                return K(LoadFarPtr(b1 == 0xb2 ? SEG_SS : b1 == 0xb4 ? SEG_FS : SEG_GS, _reg));
            case 0xb6: case 0xb7: case 0xbe: case 0xbf: // MOVZX / MOVSX
            {
                int sb = (b1 & 1) == 0 ? 8 : 16;
                if (!DecodeE(sb, 1)) return FAIL;
                uint v = ReadE(sb);
                if (b1 >= 0xbe) v = Sext(sb, v);
                SReg(V, _reg, v);
                return OK;
            }
            case 0xba: // group 8: BT/BTS/BTR/BTC Ev, Ib
            {
                if (!DecodeModrm()) return FAIL;
                if (_reg < 4) return K(Throw0(EX_UD));
                if (!Fetch8(out imm)) return FAIL;
                int bits = V;
                imm = Sext8(imm) & (bits == 16 ? 0xffffu : 0xffffffffu);
                if (_mod != 3) _addr += imm / (uint)bits * (uint)(bits / 8);   // tiny386 quirk kept (the 386 takes the immediate modulo the size only)
                if (!TranslateE(bits, _reg == 4 ? 1 : 3)) return FAIL;
                BitOp(_reg - 4, bits, imm);
                return OK;
            }
            case 0xbc: case 0xbd: // BSF / BSR
            {
                int bits = V;
                if (!DecodeE(bits, 1)) return FAIL;
                uint src = ReadE(bits);
                _ccMask = 0;
                if (src == 0) Flags |= ZF;
                else
                {
                    Flags &= ~ZF;
                    SReg(bits, _reg, b1 == 0xbc ? (uint)BitOperations.TrailingZeroCount(src) : (uint)(31 - BitOperations.LeadingZeroCount(src)));
                }
                return OK;
            }
            case 0xc0: case 0xc1: // XADD
            {
                int bits = b1 == 0xc0 ? 8 : V;
                if (!DecodeE(bits, 3)) return FAIL;
                uint dst = ReadE(bits);
                _ccSrc1 = Sext(bits, dst); _ccSrc2 = Sext(bits, LReg(bits, _reg));
                _ccDst = Sext(bits, _ccSrc1 + _ccSrc2); _ccOp = (int)Cc.ADD; _ccMask = ALL;
                SReg(bits, _reg, dst);
                WriteE(bits, _ccDst);
                return OK;
            }
            case 0xc7: // group 9: CMPXCHG8B
            {
                if (!DecodeModrm()) return FAIL;
                if (_reg != 1 || _mod == 3) return K(Throw0(EX_UD));
                var m1 = new OptAddr(); var m2 = new OptAddr();
                if (!Translate(ref m1, 3, _curSeg, _addr, 4) || !Translate(ref m2, 3, _curSeg, _addr + 4, 4)) return FAIL;
                uint lo = Load32(ref m1), hi = Load32(ref m2);
                if (R[0] == lo && R[2] == hi) { Flags |= ZF; Store32(ref m1, R[3]); Store32(ref m2, R[1]); }
                else { Flags &= ~ZF; R[0] = lo; R[2] = hi; }
                _ccMask &= ~ZF;
                return OK;
            }
            case 0xc8: case 0xc9: case 0xca: case 0xcb: case 0xcc: case 0xcd: case 0xce: case 0xcf: // BSWAP
            {
                if (_opsz16) return K(Throw0(EX_UD));
                R[b1 & 7] = BinaryPrimitivesReverse(R[b1 & 7]);
                return OK;
            }
        }
        return K(Throw0(EX_UD));
    }

    private static uint BinaryPrimitivesReverse(uint v) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v);

    private int Grp6()
    {
        if (!DecodeModrm()) return FAIL;
        int rwm = _reg < 2 ? 2 : 1;
        if (_reg > 5) return K(Throw0(EX_UD));
        if (!TranslateE(16, rwm)) return FAIL;
        uint v = _reg < 2 ? 0 : _mod == 3 ? LReg(V, _rm) : Load16(ref _meml);
        switch (_reg)
        {
            case 0: if (_mod == 3) SReg(V, _rm, SegSel[SEG_LDT]); else Store16(ref _meml, SegSel[SEG_LDT]); return OK;
            case 1: if (_mod == 3) SReg(V, _rm, SegSel[SEG_TR]); else Store16(ref _meml, SegSel[SEG_TR]); return OK;
            case 2: if (Cpl != 0) return K(Throw(EX_GP, 0)); return K(SetSeg(SEG_LDT, v));
            case 3: if (Cpl != 0) return K(Throw(EX_GP, 0)); return K(SetSeg(SEG_TR, v));
            default:
            {
                if (!VerRW(v, _reg == 5, out bool zf)) return FAIL;
                _ccMask &= ~ZF;
                Flags = SetBit(Flags, zf, ZF);
                return OK;
            }
        }
    }

    private int Grp7()
    {
        if (!DecodeModrm()) return FAIL;
        switch (_reg)
        {
            case 0: case 1: // SGDT / SIDT
            {
                if (_mod == 3) return K(Throw0(EX_UD));
                var m1 = new OptAddr(); var m2 = new OptAddr();
                if (!Translate(ref m1, 2, _curSeg, _addr, 2) || !Translate(ref m2, 2, _curSeg, _addr + 2, 4)) return FAIL;
                Store16(ref m1, _reg == 0 ? GdtLimit : IdtLimit);
                Store32(ref m2, _reg == 0 ? GdtBase : IdtBase);
                return OK;
            }
            case 2: case 3: // LGDT / LIDT
            {
                if (_mod == 3) return K(Throw0(EX_UD));
                if (Cpl != 0) return K(Throw(EX_GP, 0));
                var m1 = new OptAddr(); var m2 = new OptAddr();
                if (!Translate(ref m1, 1, _curSeg, _addr, 2) || !Translate(ref m2, 1, _curSeg, _addr + 2, 4)) return FAIL;
                uint limit = Load16(ref m1), bas = Load32(ref m2);
                if (_opsz16) bas &= 0xffffff;
                if (_reg == 2) { GdtBase = bas; GdtLimit = limit; } else { IdtBase = bas; IdtLimit = limit; }
                return OK;
            }
            case 4: // SMSW
                if (!TranslateE(16, 2)) return FAIL;
                if (_mod == 3) SReg(V, _rm, Cr0 & 0xffff); else Store16(ref _meml, Cr0 & 0xffff);
                return OK;
            case 6: // LMSW
            {
                if (!TranslateE(16, 1)) return FAIL;
                uint v = _mod == 3 ? LReg(V, _rm) : Load16(ref _meml);
                if (Cpl != 0) return K(Throw(EX_GP, 0));
                Cr0 = (Cr0 & (~0xfu | 1)) | (v & 0xf);
                return OK;
            }
            case 7: // INVLPG
                if (_mod == 3) return K(Throw0(EX_UD));
                TlbClear();
                return OK;
        }
        return K(Throw0(EX_UD));
    }

    private void Cpuid()
    {
        switch (R[0])
        {
            case 0: R[0] = 1; R[3] = 0x594e4954; R[2] = 0x20363833; R[1] = 0x20555043; break; // "TINY386 CPU "
            case 1:
                R[0] = (uint)(Gen << 8); R[3] = 0; R[2] = 0x110; R[1] = 0;
                if (Gen > 5) R[2] |= 0x8820;
                break;
            case 0x80000001: R[0] = 0; R[3] = 0; R[2] = 0; R[1] = 1; break;
            default: R[0] = 0; R[3] = 0; R[2] = 0; R[1] = 0; break;
        }
    }
}
