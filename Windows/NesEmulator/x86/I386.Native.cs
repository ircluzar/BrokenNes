// x86 native backend, milestone 1 (lane D 2026-11-20; X86_NATIVE=1, opt-in, x64 Windows only).
// A compiled block (JitCode, its IL version stays the full fallback) also gets raw x64 code for the instructions of a small tier-1
// set (32-bit MOV / LEA / ADD OR AND SUB XOR CMP / TEST / Jcc / JMP, register or flat-segment memory operands through the TLB), run
// only when the block is entered at its first instruction. The guest's eight registers live in host callee-saved registers for the
// whole run. Anything the native code does not do (another instruction, a TLB miss, memory that is not plain RAM, a store into a code
// line, a lazy-flag state its Jcc cannot read, a page cross) leaves BEFORE that instruction with the state exactly as the IL code
// would have it there (Ip = NextIp = that instruction, as the IL code's own "leave before" exit, JExitOk(pa, pa)); the interpreter
// loop then enters the IL code (or the interpreter) at that instruction. Budget exits leave as the IL code's unlinked JExitLink.
// The instruction clock (cost per instruction), the step budget and the lazy-flag fields are kept exactly as the IL code keeps them.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NesEmulator.X86.Native;
using NR = NesEmulator.X86.Native.R;
using NC = NesEmulator.X86.Native.Cc;

namespace NesEmulator.X86;

public sealed partial class I386
{
    private static readonly bool NativeOn = Environment.GetEnvironmentVariable("X86_NATIVE") == "1" && NExecMem.Supported && !TlbHash;
    /// <summary>X86_NATIVESTAT=1: native code is compiled (even without X86_NATIVE) and the runs entered at the first instruction of a block
    /// that has native code are timed, native or IL (the A/B of the same block set).</summary>
    private static readonly bool NativeStat = Environment.GetEnvironmentVariable("X86_NATIVESTAT") == "1" && NExecMem.Supported && !TlbHash;
    public static long NatCompiled, NatInstrs, NatRuns, NatBefore, NatNoProgress, NatAfter, NatRetired, NatSkipped;
    public static long StatRuns, StatTicks, StatRetired, NatDemoted, StatCallTicks;
    private static readonly int NativeMinRun = int.TryParse(Environment.GetEnvironmentVariable("X86_NATIVEMINRUN"), out int nmr) ? nmr : 8;
    private static readonly ulong NatKinds = ulong.TryParse(Environment.GetEnvironmentVariable("X86_NATIVEKINDS"), System.Globalization.NumberStyles.HexNumber, null, out ulong nk) ? nk : ulong.MaxValue;
    public static readonly long[] NatWhy = new long[128];

    private sealed class NativeInfo
    {
        public IntPtr Fn;
        public int SegMask;
        public int[] SpecSegs = Array.Empty<int>();
        public uint[] SBase = Array.Empty<uint>(), SSpan = Array.Empty<uint>(), SLo = Array.Empty<uint>();
        public uint Spm;
        public int Covered;
        public long Runs, Retired;   // (a block whose native runs do little before leaving goes back to IL only: NativeDemote)
    }

    // the context the native code reads and writes (on the CPU thread's stack during a run)
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NCtx
    {
        public fixed uint R[8];          // 0
        public int Op;                   // 32
        public uint Dst, Src1, Src2, Mask, Flags;   // 36 40 44 48 52
        public long Cyc;                 // 56
        public int St;                   // 64
        public int ExitK;                // 68
        public int ExitKind;             // 72 (0 = before instruction ExitK, 1 = after it, NextIp = ExitNext)
        public uint ExitNext;            // 76 (a physical address in the block's page)
        public long Mem, CMark, TlbR, TlbW, TlbX;   // 80 88 96 104 112
        public uint Ip0, CsLim;          // 120 124
        public long RPtr;                // 128 (the pinned R[])
    }
    private const int CxOp = 32, CxDst = 36, CxSrc1 = 40, CxSrc2 = 44, CxMask = 48, CxFlags = 52, CxCyc = 56, CxSt = 64, CxK = 68, CxKind = 72, CxNext = 76,
        CxMem = 80, CxCMark = 88, CxTlbR = 96, CxTlbW = 104, CxTlbX = 112, CxIp0 = 120, CxCsLim = 124, CxRPtr = 128;
    private static readonly NR[] NG = { NR.Rbx, NR.Rbp, NR.Rsi, NR.Rdi, NR.R12, NR.R13, NR.R14, NR.R15 };   // guest EAX..EDI
    private static readonly NR[] NSaved = { NR.Rbx, NR.Rbp, NR.Rsi, NR.Rdi, NR.R12, NR.R13, NR.R14, NR.R15 };

    private bool NatSupported(in DEntry e, uint pa)
    {
        if (e.O16 || e.A16) return false;
        if (e.Bits != 32 && !(e.Kind == DK_MOVX && e.Bits is 8 or 16)) return false;
        bool mem = DcHasModrm(e.Kind) && e.Mod != 3;
        if ((NatKinds >> e.Kind & 1) == 0 || (mem && e.Kind != DK_LEA && (NatKinds >> 63 & 1) == 0)) return false;   // (X86_NATIVEKINDS: a bisect mask)
        switch (e.Kind)
        {
            case DK_MOVEG: case DK_MOVGE: case DK_MOVEI: case DK_LEA: break;
            case DK_MOVRI: if (e.Live) return false; break;
            case DK_ALUEG: case DK_ALUGE: if (e.Op is 2 or 3) return false; break;
            case DK_ALUAI: if (e.Op is 2 or 3) return false; break;
            case DK_GRP1: if (e.Reg is 2 or 3) return false; break;
            case DK_TESTEG: case DK_G3TEST: break;
            case DK_JCC:
                if (!(e.Op is >= 0x70 and <= 0x7f or >= 0x80 and <= 0x8f)) return false;
                if ((e.Op & 0xf) is 0xa or 0xb) return false;   // (parity: through JCond in the IL code)
                break;
            case DK_JMP: break;
            case DK_PUSH: case DK_POP: case DK_CALL: break;
            case DK_INC: case DK_DEC: case DK_MOVX: break;
            case DK_FLAGOP: if (e.Op is not (0xf8 or 0xf9 or 0xfc or 0xfd)) return false; break;
            case DK_GRP3: if (e.Reg is not (2 or 3)) return false; break;   // NOT / NEG (MUL / DIV: the IL code's helper)
            case DK_GRP5: if (e.Reg is not (2 or 4 or 6)) return false; break;   // CALL / JMP / PUSH Ev
            case DK_RET: if (e.Imm > 0xfff0) return false; break;
            default: return false;
        }
        if (e.Folds && pa + e.Len + 2 + e.FoldRel == pa) return false;   // (the spin skip: the IL code's)
        if (mem && e.Kind != DK_LEA && (e.Seg < 0 || e.Seg > 5)) return false;
        return true;
    }

    private static bool NatStackOk(in DEntry e, SegSpec spec) => !(e.Kind is DK_PUSH or DK_POP or DK_CALL or DK_RET || (e.Kind == DK_GRP5 && e.Reg is 2 or 6)) || spec.Spm == 0xffffffff;

    /// <summary>Compile the native part of a block (the CPU thread, at install). Null when it is not eligible.</summary>
    private unsafe NativeInfo? NativeCompile(JitCode code)
    {
        var ins = code.Ins; var pas = code.Addrs; var spec = code.Spec;
        if (ins == null || !code.Pg || code.C16 || spec == null || UsePend || !_ioMem || MemSize <= 0xc0000 || NoRamRange) return null;
        int n = ins.Length;
        int segMask = 0;
        foreach (var e in ins) segMask |= InsSegs(e);
        if (segMask == 0 || NoSegLoad || NoSegSpec) return null;
        if ((segMask & ~spec.Mask) != 0) return null;   // every segment the block uses specialized (no lazy segment tests)
        if (!NatSupported(ins[0], pas[0]) || !NatStackOk(ins[0], spec)) return null;
        var sup = new bool[n]; int covered = 0;
        for (int k = 0; k < n; k++) if (sup[k] = NatSupported(ins[k], pas[k]) && NatStackOk(ins[k], spec)) covered++;
        var index = new Dictionary<uint, int>();
        for (int k = 0; k < n; k++) index[pas[k]] = k;
        uint start = code.Start;

        var a = new NAsm();
        var L = new XLabel[n]; for (int k = 0; k < n; k++) L[k] = a.NewLabel();
        var before = new XLabel[n];
        var afterStubs = new List<(XLabel l, int k, uint next)>();
        var exitCommon = a.NewLabel();
        XLabel Before(int k) => before[k] ??= a.NewLabel();
        XLabel After(int k, uint next) { var l = a.NewLabel(); afterStubs.Add((l, k, next)); return l; }
        M C(int off) => new M(NR.Rcx, off);

        // prologue: save the host's callee-saved registers, load the guest's
        foreach (var r in NSaved) a.Push(r);
        a.MovRM(NR.Rax, C(CxRPtr));
        for (int i = 0; i < 8; i++) a.MovRM(NG[i], new M(NR.Rax, 4 * i), false);
        a.MovRM(NR.R10, C(CxCyc));
        a.MovRM(NR.R11, C(CxSt), false);
        a.Jmp(L[0]);

        // after instruction k (st not yet counted down): st -= 1; st <= 0: leave (Ip = k, NextIp = next); else go on at `target`
        void Continue(int k, uint target)
        {
            a.AluRI(NAsm.Alu8.Sub, NR.R11, 1, false);
            a.Jcc(NC.LE, After(k, target));
            if ((target >> 12) == (pas[k] >> 12) && index.TryGetValue(target, out int j)) a.Jmp(sup[j] ? L[j] : Before(j));
            else a.Jmp(After(k, target));
        }
        void AddCost(int k)
        {
            var e = ins[k];
            int cost = 1 + JitExtraCost(pas[k], e.Kind, e.Reg, e.Bits, DcHasModrm(e.Kind) && e.Mod != 3);
            a.AluRI(NAsm.Alu8.Add, NR.R10, cost, true);
        }
        // the effective address (32-bit wrap) into eax
        void Ea(in DEntry e, NR d)
        {
            bool hb = e.Base < 8, hx = e.Index < 8;
            if (!hb && !hx) { a.MovRI(d, e.Disp); return; }
            int sc = 1 << e.Scale;
            if (hb && hx) a.Lea32(d, new M(NG[e.Base], NG[e.Index], sc, unchecked((int)e.Disp)));
            else if (hb) a.Lea32(d, new M(NG[e.Base], unchecked((int)e.Disp)));
            else a.Lea32(d, new M(NR.Rbp /*unused*/, NG[e.Index], sc, unchecked((int)e.Disp)), noBase: true);
        }
        // a memory operand: physical address into edx (r9 = Mem base), or leave before k
        void MemAddr(int k, bool write, int size = 4) { Ea(ins[k], NR.Rax); MemLa(k, ins[k].Seg, write, size); }
        // eax = the offset in segment s: translate (limit, base, TLB, RAM, code line) into edx, r9 = Mem base, or leave before k
        void MemLa(int k, int s, bool write, int size = 4)
        {
            // the segment limit (LimitCheck, the specialized constants)
            uint lo = spec.Lo[s], span = spec.Span[s];
            if (lo != 0 || span != 0xffffffff)
            {
                a.MovRR(NR.Rdx, NR.Rax, false);
                if (lo != 0) a.AluRI(NAsm.Alu8.Sub, NR.Rdx, unchecked((int)lo), false);
                a.AluRI(NAsm.Alu8.Cmp, NR.Rdx, unchecked((int)(span - (uint)(size - 1))), false);
                a.Jcc(NC.A, Before(k));
            }
            else { a.AluRI(NAsm.Alu8.Cmp, NR.Rax, unchecked((int)(0xffffffffu - (uint)(size - 1))), false); a.Jcc(NC.A, Before(k)); }
            if (spec.Base[s] != 0) a.AluRI(NAsm.Alu8.Add, NR.Rax, unchecked((int)spec.Base[s]), false);
            // the TLB hit for this CPL and access (Xlat)
            a.MovRR(NR.Rdx, NR.Rax, false); a.ShiftI(NAsm.Sh.Shr, NR.Rdx, 12, false);
            a.MovRR(NR.R8, NR.Rdx, false); a.AluRI(NAsm.Alu8.And, NR.R8, TlbSize - 1, false);
            a.MovRM(NR.R9, C(write ? CxTlbW : CxTlbR));
            a.AluRM(NAsm.Alu8.Cmp, NR.Rdx, new M(NR.R9, NR.R8, 4, 0), false);
            a.Jcc(NC.NE, Before(k));
            a.MovRR(NR.R9, NR.Rax, false); a.AluRI(NAsm.Alu8.And, NR.R9, 0xfff, false); if (size > 1) { a.AluRI(NAsm.Alu8.Cmp, NR.R9, 4096 - size, false); a.Jcc(NC.A, Before(k)); }
            a.MovRM(NR.R9, C(CxTlbX));
            a.MovRM(NR.Rdx, new M(NR.R9, NR.R8, 4, 0), false);
            a.Alu(NAsm.Alu8.Xor, NR.Rdx, NR.Rax, false);   // pax
            // plain RAM: [C0000, MemSize) in one compare, else below A0000
            var ram = a.NewLabel();
            a.MovRR(NR.R8, NR.Rdx, false); a.AluRI(NAsm.Alu8.Sub, NR.R8, 0xc0000, false); a.AluRI(NAsm.Alu8.Cmp, NR.R8, unchecked((int)(MemSize - 0xc0000)), false); a.Jcc(NC.B, ram);
            a.AluRI(NAsm.Alu8.Cmp, NR.Rdx, 0xa0000, false); a.Jcc(NC.AE, Before(k));
            a.Bind(ram);
            if (write)
            {   // a store into a code line: the IL code (CodeWrite, its own exits)
                a.MovRM(NR.R9, C(CxCMark));
                a.MovRR(NR.R8, NR.Rdx, false); a.ShiftI(NAsm.Sh.Shr, NR.R8, 6, false); a.Movzx8RM(NR.Rax, new M(NR.R9, NR.R8, 1, 0));
                a.Lea32(NR.R8, new M(NR.Rdx, 3)); a.ShiftI(NAsm.Sh.Shr, NR.R8, 6, false); a.Movzx8RM(NR.R8, new M(NR.R9, NR.R8, 1, 0));
                a.Alu(NAsm.Alu8.Or, NR.Rax, NR.R8, false); a.Jcc(NC.NE, Before(k));
            }
            a.MovRM(NR.R9, C(CxMem));
        }
        M Mem() => new M(NR.R9, NR.Rdx, 1, 0);

        for (int k = 0; k < n; k++)
        {
            if (!sup[k]) continue;
            var e = ins[k]; uint pa = pas[k], pe = pa + e.Len;
            bool mem = DcHasModrm(e.Kind) && e.Mod != 3;
            a.Bind(L[k]);
            switch (e.Kind)
            {
                case DK_MOVGE:
                    if (mem) { MemAddr(k, false); AddCost(k); a.MovRM(NG[e.Reg], Mem(), false); }
                    else { AddCost(k); a.MovRR(NG[e.Reg], NG[e.Rm], false); }
                    Continue(k, pe); break;
                case DK_MOVEG:
                    if (mem) { MemAddr(k, true); AddCost(k); a.MovMR(Mem(), NG[e.Reg], false); }
                    else { AddCost(k); a.MovRR(NG[e.Rm], NG[e.Reg], false); }
                    Continue(k, pe); break;
                case DK_MOVEI:
                    if (mem) { MemAddr(k, true); AddCost(k); a.MovMI32(Mem(), unchecked((int)e.Imm), false); }
                    else { AddCost(k); a.MovRI(NG[e.Rm], e.Imm); }
                    Continue(k, pe); break;
                case DK_MOVRI:
                    AddCost(k); a.MovRI(NG[e.Op], e.Imm); Continue(k, pe); break;
                case DK_LEA:
                    AddCost(k); Ea(e, NR.Rax); a.MovRR(NG[e.Reg], NR.Rax, false); Continue(k, pe); break;
                case DK_ALUEG: case DK_GRP1: case DK_ALUGE: case DK_ALUAI: case DK_TESTEG: case DK_G3TEST:
                {
                    int aop = e.Kind switch { DK_GRP1 => e.Reg, DK_TESTEG or DK_G3TEST => 4, _ => e.Op };
                    bool test = e.Kind is DK_TESTEG or DK_G3TEST;
                    bool eIsDest = e.Kind is DK_ALUEG or DK_GRP1;
                    bool writes = !test && aop != 7;
                    if (e.Kind != DK_ALUAI && mem) MemAddr(k, eIsDest && writes);
                    AddCost(k);
                    // A -> eax, B -> r8d
                    switch (e.Kind)
                    {
                        case DK_ALUEG: case DK_GRP1: case DK_TESTEG: case DK_G3TEST:
                            if (mem) a.MovRM(NR.Rax, Mem(), false); else a.MovRR(NR.Rax, NG[e.Rm], false);
                            if (e.Kind is DK_GRP1 or DK_G3TEST) a.MovRI(NR.R8, e.Imm); else a.MovRR(NR.R8, NG[e.Reg], false);
                            break;
                        case DK_ALUGE:
                            a.MovRR(NR.Rax, NG[e.Reg], false);
                            if (mem) a.MovRM(NR.R8, Mem(), false); else a.MovRR(NR.R8, NG[e.Rm], false);
                            break;
                        default:   // ALUAI
                            a.MovRR(NR.Rax, NG[0], false); a.MovRI(NR.R8, e.Imm); break;
                    }
                    if (aop is 0 or 5 or 7)
                    {
                        a.MovMR(C(CxSrc1), NR.Rax, false); a.MovMR(C(CxSrc2), NR.R8, false);
                        a.Alu(aop == 0 ? NAsm.Alu8.Add : NAsm.Alu8.Sub, NR.Rax, NR.R8, false);
                        a.MovMR(C(CxDst), NR.Rax, false); a.MovMI32(C(CxOp), aop == 0 ? (int)Cc.ADD : (int)Cc.SUB, false); a.MovMI32(C(CxMask), unchecked((int)ALL), false);
                    }
                    else
                    {
                        a.Alu(aop == 1 ? NAsm.Alu8.Or : aop == 4 ? NAsm.Alu8.And : NAsm.Alu8.Xor, NR.Rax, NR.R8, false);
                        a.MovMR(C(CxDst), NR.Rax, false); a.MovMI32(C(CxOp), aop == 1 ? (int)Cc.OR : aop == 4 ? (int)Cc.AND : (int)Cc.XOR, false); a.MovMI32(C(CxMask), unchecked((int)LOGIC), false);
                    }
                    if (writes)
                    {
                        if (e.Kind == DK_ALUAI) a.MovRR(NG[0], NR.Rax, false);
                        else if (e.Kind == DK_ALUGE) a.MovRR(NG[e.Reg], NR.Rax, false);
                        else if (mem) a.MovMR(Mem(), NR.Rax, false);
                        else a.MovRR(NG[e.Rm], NR.Rax, false);
                    }
                    else if (e.Folds)
                    {   // CMP / TEST + JZ / JNZ rel8 (Fold): taken on Dst (eax)
                        var notTaken = a.NewLabel();
                        a.Alu(NAsm.Alu8.Or, NR.Rax, NR.Rax, false);
                        a.Jcc((e.FoldNb & 1) != 0 ? NC.E : NC.NE, notTaken);
                        Continue(k, pe + 2 + e.FoldRel);
                        a.Bind(notTaken);
                    }
                    Continue(k, pe);
                    break;
                }
                case DK_JCC:
                {
                    uint cc = (uint)e.Op & 0xf, bc = cc & 0xe;
                    var done = a.NewLabel();
                    if (cc is 4 or 5 or 8 or 9)
                    {
                        uint flag = cc < 8 ? ZF : SF;
                        var conc = a.NewLabel();
                        a.MovRM(NR.Rax, C(CxMask), false); a.AluRI(NAsm.Alu8.And, NR.Rax, (int)flag, false); a.Jcc(NC.E, conc);
                        if (cc < 8) { a.AluMI(NAsm.Alu8.Cmp, C(CxDst), 0, false); a.SetCc(NC.E, NR.Rax); }
                        else { a.MovRM(NR.Rax, C(CxDst), false); a.ShiftI(NAsm.Sh.Shr, NR.Rax, 31, false); }
                        a.Jmp(done);
                        a.Bind(conc);
                        a.MovRM(NR.Rax, C(CxFlags), false); a.AluRI(NAsm.Alu8.And, NR.Rax, (int)flag, false); a.SetCc(NC.NE, NR.Rax);
                    }
                    else
                    {
                        uint need = cc switch { 0 or 1 => OF, 2 or 3 => CF, 6 or 7 => CF | ZF, 0xc or 0xd => SF | OF, _ => ZF | SF | OF };
                        var notSub = a.NewLabel();
                        a.MovRM(NR.Rax, C(CxMask), false); a.AluRI(NAsm.Alu8.And, NR.Rax, (int)need, false); a.AluRI(NAsm.Alu8.Cmp, NR.Rax, (int)need, false); a.Jcc(NC.NE, Before(k));
                        a.AluMI(NAsm.Alu8.Cmp, C(CxOp), (int)Cc.SUB, false); a.Jcc(NC.NE, notSub);
                        a.MovRM(NR.Rdx, C(CxSrc1), false); a.MovRM(NR.R8, C(CxSrc2), false);
                        if (bc == 0)
                        {
                            a.MovRR(NR.Rax, NR.Rdx, false); a.Alu(NAsm.Alu8.Xor, NR.Rax, NR.R8, false);
                            a.MovRM(NR.R9, C(CxDst), false); a.Alu(NAsm.Alu8.Xor, NR.R9, NR.Rdx, false);
                            a.Alu(NAsm.Alu8.And, NR.Rax, NR.R9, false); a.ShiftI(NAsm.Sh.Shr, NR.Rax, 31, false);
                        }
                        else
                        {
                            a.Alu(NAsm.Alu8.Cmp, NR.Rdx, NR.R8, false);
                            a.SetCc(bc == 2 ? NC.B : bc == 6 ? NC.BE : bc == 0xc ? NC.L : NC.LE, NR.Rax);
                        }
                        a.Jmp(done);
                        a.Bind(notSub);
                        a.MovRM(NR.Rax, C(CxOp), false); a.AluRI(NAsm.Alu8.Sub, NR.Rax, (int)Cc.AND, false); a.AluRI(NAsm.Alu8.Cmp, NR.Rax, 2, false); a.Jcc(NC.A, Before(k));
                        if (bc is 0 or 2) a.MovRI(NR.Rax, 0);
                        else if (bc == 6) { a.AluMI(NAsm.Alu8.Cmp, C(CxDst), 0, false); a.SetCc(NC.E, NR.Rax); }
                        else if (bc == 0xc) { a.MovRM(NR.Rax, C(CxDst), false); a.ShiftI(NAsm.Sh.Shr, NR.Rax, 31, false); }
                        else { a.AluMI(NAsm.Alu8.Cmp, C(CxDst), 1, false); a.SetCc(NC.L, NR.Rax); }
                    }
                    a.Bind(done);
                    a.Movzx8RR(NR.Rax, NR.Rax);
                    if ((cc & 1) != 0) a.AluRI(NAsm.Alu8.Xor, NR.Rax, 1, false);
                    AddCost(k);
                    var nt = a.NewLabel();
                    a.Alu(NAsm.Alu8.Or, NR.Rax, NR.Rax, false); a.Jcc(NC.E, nt);
                    Continue(k, pe + e.Imm);
                    a.Bind(nt);
                    Continue(k, pe);
                    break;
                }
                case DK_JMP:
                    AddCost(k); Continue(k, pe + e.Imm); break;
                case DK_PUSH: case DK_CALL:
                {   // Push(32, v): offset = ESP - 4 (SpMask FFFFFFFF), limit / TLB / RAM / code line, ESP = offset, store
                    a.Lea32(NR.Rax, new M(NG[4], -4));
                    MemLa(k, SEG_SS, true);
                    AddCost(k);
                    a.Lea32(NG[4], new M(NG[4], -4));
                    if (e.Kind == DK_CALL) { a.MovRM(NR.Rax, C(CxIp0), false); a.AluRI(NAsm.Alu8.Add, NR.Rax, unchecked((int)(pe - start)), false); a.MovMR(Mem(), NR.Rax, false); }
                    else if (e.Op == 8) a.MovMI32(Mem(), unchecked((int)e.Imm), false);
                    else if (e.Op == 4) { a.Lea32(NR.Rax, new M(NG[4], 4)); a.MovMR(Mem(), NR.Rax, false); }   // (PUSH ESP: the value before)
                    else a.MovMR(Mem(), NG[e.Op], false);
                    if (e.Kind == DK_CALL) { a.AluRI(NAsm.Alu8.Sub, NR.R11, 1, false); a.Jmp(After(k, pe + e.Imm)); }   // (CALL: always leaves, NextIp = the target)
                    else Continue(k, pe);
                    break;
                }
                case DK_INC: case DK_DEC:
                {   // IncDecReg inline: CF made concrete in Flags when lazy (SUB / ADD / logic), else the IL code
                    var skip = a.NewLabel(); var notSub = a.NewLabel(); var notAdd = a.NewLabel(); var have = a.NewLabel();
                    a.MovRM(NR.Rax, C(CxMask), false); a.AluRI(NAsm.Alu8.And, NR.Rax, (int)CF, false); a.Jcc(NC.E, skip);
                    a.AluMI(NAsm.Alu8.Cmp, C(CxOp), (int)Cc.SUB, false); a.Jcc(NC.NE, notSub);
                    a.MovRM(NR.Rax, C(CxSrc1), false); a.AluRM(NAsm.Alu8.Cmp, NR.Rax, C(CxSrc2), false); a.SetCc(NC.B, NR.Rax); a.Jmp(have);
                    a.Bind(notSub);
                    a.AluMI(NAsm.Alu8.Cmp, C(CxOp), (int)Cc.ADD, false); a.Jcc(NC.NE, notAdd);
                    a.MovRM(NR.Rax, C(CxDst), false); a.AluRM(NAsm.Alu8.Cmp, NR.Rax, C(CxSrc2), false); a.SetCc(NC.B, NR.Rax); a.Jmp(have);
                    a.Bind(notAdd);
                    a.MovRM(NR.Rax, C(CxOp), false); a.AluRI(NAsm.Alu8.Sub, NR.Rax, (int)Cc.AND, false); a.AluRI(NAsm.Alu8.Cmp, NR.Rax, 2, false); a.Jcc(NC.A, Before(k));
                    a.MovRI(NR.Rax, 0);
                    a.Bind(have);
                    a.Movzx8RR(NR.Rax, NR.Rax);
                    a.MovRM(NR.Rdx, C(CxFlags), false); a.AluRI(NAsm.Alu8.And, NR.Rdx, unchecked((int)~CF), false); a.Alu(NAsm.Alu8.Or, NR.Rdx, NR.Rax, false); a.MovMR(C(CxFlags), NR.Rdx, false);
                    a.Bind(skip);
                    AddCost(k);
                    a.MovRR(NR.Rax, NG[e.Op], false); a.AluRI(e.Kind == DK_DEC ? NAsm.Alu8.Sub : NAsm.Alu8.Add, NR.Rax, 1, false);
                    a.MovMR(C(CxDst), NR.Rax, false); a.MovMI32(C(CxOp), (int)(e.Kind == DK_DEC ? Cc.DEC32 : Cc.INC32), false); a.MovMI32(C(CxMask), (int)(PF | AF | ZF | SF | OF), false);
                    a.MovRR(NG[e.Op], NR.Rax, false);
                    Continue(k, pe);
                    break;
                }
                case DK_MOVX:
                {   // MOVZX / MOVSX r32, Eb / Ew
                    int sb = e.Bits;
                    if (mem) { MemAddr(k, false, sb / 8); AddCost(k); if (sb == 8) a.Movzx8RM(NR.Rax, Mem()); else a.Movzx16RM(NR.Rax, Mem()); }
                    else
                    {
                        AddCost(k);
                        if (sb == 16) a.Movzx16RR(NR.Rax, NG[e.Rm]);
                        else if (e.Rm < 4) a.Movzx8RR(NR.Rax, NG[e.Rm]);
                        else { a.MovRR(NR.Rax, NG[e.Rm - 4], false); a.ShiftI(NAsm.Sh.Shr, NR.Rax, 8, false); a.Movzx8RR(NR.Rax, NR.Rax); }
                    }
                    if (e.Op == 1) { a.ShiftI(NAsm.Sh.Shl, NR.Rax, 32 - sb, false); a.ShiftI(NAsm.Sh.Sar, NR.Rax, 32 - sb, false); }
                    a.MovRR(NG[e.Reg], NR.Rax, false);
                    Continue(k, pe);
                    break;
                }
                case DK_FLAGOP:
                    AddCost(k);
                    if (e.Op is 0xf8 or 0xf9) { a.AluMI(NAsm.Alu8.And, C(CxMask), unchecked((int)~CF), false); a.AluMI(e.Op == 0xf8 ? NAsm.Alu8.And : NAsm.Alu8.Or, C(CxFlags), e.Op == 0xf8 ? unchecked((int)~CF) : (int)CF, false); }
                    else a.AluMI(e.Op == 0xfc ? NAsm.Alu8.And : NAsm.Alu8.Or, C(CxFlags), e.Op == 0xfc ? unchecked((int)~DF) : (int)DF, false);
                    Continue(k, pe);
                    break;
                case DK_GRP3:
                {   // NOT / NEG Ev (Grp3Body): NEG: Src1 = E, Dst = -E, NEG32, ALL
                    if (mem) MemAddr(k, true);
                    AddCost(k);
                    if (mem) a.MovRM(NR.Rax, Mem(), false); else a.MovRR(NR.Rax, NG[e.Rm], false);
                    if (e.Reg == 2) a.Not(NR.Rax, false);
                    else
                    {
                        a.MovMR(C(CxSrc1), NR.Rax, false); a.Neg32(NR.Rax);
                        a.MovMR(C(CxDst), NR.Rax, false); a.MovMI32(C(CxOp), (int)Cc.NEG32, false); a.MovMI32(C(CxMask), unchecked((int)ALL), false);
                    }
                    if (mem) a.MovMR(Mem(), NR.Rax, false); else a.MovRR(NG[e.Rm], NR.Rax, false);
                    Continue(k, pe);
                    break;
                }
                case DK_GRP5:
                {   // CALL / JMP / PUSH Ev (JGrp5): v = E (read first), then the push; CALL / JMP leave with NextIp = v
                    if (mem) { MemAddr(k, false); a.MovRM(NR.Rax, Mem(), false); } else a.MovRR(NR.Rax, NG[e.Rm], false);
                    a.MovMR(C(CxNext), NR.Rax, false);
                    if (e.Reg is 2 or 6)
                    {
                        a.Lea32(NR.Rax, new M(NG[4], -4));
                        MemLa(k, SEG_SS, true);
                        AddCost(k);
                        a.Lea32(NG[4], new M(NG[4], -4));
                        if (e.Reg == 2) { a.MovRM(NR.Rax, C(CxIp0), false); a.AluRI(NAsm.Alu8.Add, NR.Rax, unchecked((int)(pe - start)), false); }
                        else a.MovRM(NR.Rax, C(CxNext), false);
                        a.MovMR(Mem(), NR.Rax, false);
                    }
                    else AddCost(k);
                    if (e.Reg == 6) { Continue(k, pe); break; }
                    a.AluRI(NAsm.Alu8.Sub, NR.R11, 1, false);
                    a.MovMI32(C(CxK), k, false); a.MovMI32(C(CxKind), 2, false); a.Jmp(exitCommon);
                    break;
                }
                case DK_POP:
                    a.MovRR(NR.Rax, NG[4], false);
                    MemLa(k, SEG_SS, false);
                    AddCost(k);
                    a.MovRM(NR.Rax, Mem(), false);
                    a.Lea32(NG[4], new M(NG[4], 4));
                    a.MovRR(NG[e.Op], NR.Rax, false);
                    Continue(k, pe);
                    break;
                case DK_RET:
                {   // Ret(imm) inline: NextIp = [ESP], past the CS limit: the IL code (its #GP); ESP += 4 + imm; always leaves
                    a.MovRR(NR.Rax, NG[4], false);
                    MemLa(k, SEG_SS, false);
                    a.MovRM(NR.Rax, Mem(), false);
                    a.AluRM(NAsm.Alu8.Cmp, NR.Rax, C(CxCsLim), false); a.Jcc(NC.A, Before(k));
                    AddCost(k);
                    a.Lea32(NG[4], new M(NG[4], 4 + (int)e.Imm));
                    a.AluRI(NAsm.Alu8.Sub, NR.R11, 1, false);
                    a.MovMI32(C(CxK), k, false); a.MovMI32(C(CxKind), 2, false); a.MovMR(C(CxNext), NR.Rax, false); a.Jmp(exitCommon);
                    break;
                }
            }
        }
        // exits
        for (int k = 0; k < n; k++)
        {
            if (before[k] == null) continue;
            a.Bind(before[k]);
            a.MovMI32(C(CxK), k, false); a.MovMI32(C(CxKind), 0, false); a.Jmp(exitCommon);
        }
        foreach (var (l, k, next) in afterStubs)
        {
            a.Bind(l);
            a.MovMI32(C(CxK), k, false); a.MovMI32(C(CxKind), 1, false); a.MovMI32(C(CxNext), unchecked((int)next), false); a.Jmp(exitCommon);
        }
        a.Bind(exitCommon);
        a.MovRM(NR.Rax, C(CxRPtr));
        for (int i = 0; i < 8; i++) a.MovMR(new M(NR.Rax, 4 * i), NG[i], false);
        a.MovMR(C(CxCyc), NR.R10);
        a.MovMR(C(CxSt), NR.R11, false);
        for (int i = NSaved.Length - 1; i >= 0; i--) a.Pop(NSaved[i]);
        a.MovRI(NR.Rax, 0);
        a.Ret();

        byte* fn = NExecMem.Publish(a);
        if (fn == null) return null;
        var ss = new List<int>();
        for (int s = 0; s < 6; s++) if ((segMask >> s & 1) != 0) ss.Add(s);
        NatCompiled++; NatInstrs += covered;
        return new NativeInfo
        {
            Fn = (IntPtr)fn, SegMask = segMask, SpecSegs = ss.ToArray(),
            SBase = (uint[])spec.Base.Clone(), SSpan = (uint[])spec.Span.Clone(), SLo = (uint[])spec.Lo.Clone(), Spm = spec.Spm, Covered = covered,
        };
    }

    /// <summary>Run a block's native code from its first instruction (Exec1's JitTry, entry 0): OK / FAIL / JIT_END as the IL code.</summary>
    private unsafe int NativeRun(JitCode code)
    {
        var ni = code.NI!;
        // the IL prologue's checks (JSegLoad, the specialized segments, SpMask): anything else = the IL code decides
        JSegLoad(ni.SegMask);
        foreach (int s in ni.SpecSegs) if (!JSSame(s, ni.SBase[s], ni.SSpan[s], ni.SLo[s])) { NatSkipped++; return code.Fn(this, 0); }
        if (_spm != ni.Spm) { NatSkipped++; return code.Fn(this, 0); }
        NCtx* cp = _nctx;
        if (cp == null) cp = NatCtxInit();
        ref NCtx c = ref *cp;
        c.Op = _ccOp; c.Dst = _ccDst; c.Src1 = _ccSrc1; c.Src2 = _ccSrc2; c.Mask = _ccMask; c.Flags = Flags;
        c.Cyc = Cycle; c.St = _steps; c.CsLim = SegLimit[SEG_CS];
        long tag = (long)_nTag + (Cpl == 3 ? 2 * TlbSize * 4 : 0);
        c.TlbR = tag; c.TlbW = tag + TlbSize * 4;
        int loaded = ni.SegMask;   // (the segments JSegLoad has checked: the native code never changes a segment)
        bool first = true;
        while (true)
        {
            int st0 = c.St;
            c.ExitKind = 0; c.Ip0 = Ip - (code.Addrs[0] - code.Start);
            uint[] rSave = NativeVerify ? (uint[])R.Clone() : null!;
            if (NativeStat) { long q0 = System.Diagnostics.Stopwatch.GetTimestamp(); ((delegate* unmanaged[SuppressGCTransition]<NCtx*, int>)ni.Fn)(cp); StatCallTicks += System.Diagnostics.Stopwatch.GetTimestamp() - q0; }
            else ((delegate* unmanaged[SuppressGCTransition]<NCtx*, int>)ni.Fn)(cp);
            NatRuns++;
            if (c.ExitKind == 0 && c.St == st0)
            {   // left before its first instruction, nothing done: the IL code runs it (from the state as it is now)
                NatNoProgress++;
                _ccOp = c.Op; _ccDst = c.Dst; _ccSrc1 = c.Src1; _ccSrc2 = c.Src2; _ccMask = c.Mask; Flags = c.Flags;
                Cycle = c.Cyc; _steps = c.St;
                if (!first) _jitCur = code;
                return code.Fn(this, 0);
            }
            if (NativeVerify) { _ccOp = c.Op; NatVerify(code, ref c, st0, rSave); }
            NatRetired += st0 - c.St;
            ni.Retired += st0 - c.St;
            if (++ni.Runs == 256 && ni.Retired < 256 * NativeMinRun) { code.NI = null; NatDemoted++; }   // (exact either way: the IL code from now on)
            uint ip0 = c.Ip0;
            uint nextIp;
            if (c.ExitKind == 0) { NatBefore++; if (NativeStat) { var xe = code.Ins![c.ExitK]; int key = xe.Kind * 2 + (NatSupported(xe, code.Addrs[c.ExitK]) ? 1 : 0); NatWhy[key]++; } uint ipk = ip0 + (code.Addrs[c.ExitK] - code.Start); Ip = ipk; NextIp = ipk; break; }
            NatAfter++;
            Ip = ip0 + (code.Addrs[c.ExitK] - code.Start); nextIp = c.ExitKind == 2 ? c.ExitNext : ip0 + (c.ExitNext - code.Start);
            NextIp = nextIp;
            // chaining (X86_NATIVENOCHAIN: off): budget left and the next address starts a block with native code (entry 0) on the cached
            // fetch page: run it from here, as Exec1 + JitTry would (the same checks), without leaving
            if (c.St <= 0 || NoNatChain || NativeVerify) break;
            uint laddr = SegBase[SEG_CS] + nextIp;
            if (((ulong)laddr ^ _ifLaddr) >= 4096 - 16) break;
            uint paddr = _ifXaddr ^ laddr;
            ref var en = ref _je[JitIndex(paddr)];
            if (!en.Used || en.Start != paddr || en.C16 != Code16 || en.Pg != ((Cr0 & CR0_PG) != 0) || en.K != 0 || en.Code is not JitCode nc || nc.NI is not NativeInfo nn || nc.SpecFail || !JitSame(nc)) break;
            if ((nn.SegMask & ~loaded) != 0) { JSegLoad(nn.SegMask); loaded |= nn.SegMask; }
            bool ok = SpMask == nn.Spm;
            foreach (int s in nn.SpecSegs) if (!JSSame(s, nn.SBase[s], nn.SSpan[s], nn.SLo[s])) { ok = false; break; }
            if (!ok) break;
            // Exec1's step: _steps = st + 1, then its loop counts one down: st; Step1: Ip = NextIp
            Ip = nextIp;
            code = nc; ni = nn; _jitCur = nc; first = false; NatChained++;
        }
        _ccOp = c.Op; _ccDst = c.Dst; _ccSrc1 = c.Src1; _ccSrc2 = c.Src2; _ccMask = c.Mask; Flags = c.Flags;
        Cycle = c.Cyc; _steps = c.St + 1;
        return OK;
    }
    private static readonly bool NoNatChain = Environment.GetEnvironmentVariable("X86_NATIVENOCHAIN") != null;
    public static long NatChained;

    /// <summary>X86_NATIVESTAT: time one entry-0 run of a block that has native code (native with X86_NATIVE, else its IL code).</summary>
    private int NativeStatRun(JitCode code)
    {
        int st0 = _steps;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int rc = NativeOn ? NativeRun(code) : code.Fn(this, 0);
        StatTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        StatRuns++;
        if (rc == OK) StatRetired += st0 - (_steps - 1);
        return rc;
    }

    private unsafe NCtx* _nctx; private unsafe uint* _nTag;
    private unsafe NCtx* NatCtxInit()
    {   // one context per CPU, in unmanaged memory; the arrays it points to are pinned (I386.cs)
        _nctx = (NCtx*)NativeMemory.AllocZeroed((nuint)sizeof(NCtx));
        _nctx->RPtr = (long)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(R));
        _nctx->Mem = (long)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(Mem));
        _nctx->CMark = (long)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_codeMark));
        _nctx->TlbX = (long)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_tlbXaddr));
        _nTag = (uint*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_tlbTag));
        return _nctx;
    }
    private static readonly bool NativeVerify = Environment.GetEnvironmentVariable("X86_NATIVEVERIFY") == "1";
    private int _nvBad;
    /// <summary>X86_NATIVEVERIFY (debug; blocks without stores only): the interpreter runs the same steps from the same state; compare.</summary>
    private unsafe void NatVerify(JitCode code, ref NCtx c, int st0, uint[] rPre)
    {
        uint ipE = Ip, ip0 = Ip - (code.Addrs[0] - code.Start); long cyc0 = Cycle; uint nip0 = NextIp;
        int steps = st0 - c.St;
        uint nIp, nNext;
        if (c.ExitKind == 0) { nIp = nNext = ip0 + (code.Addrs[c.ExitK] - code.Start); }
        else { nIp = ip0 + (code.Addrs[c.ExitK] - code.Start); nNext = c.ExitKind == 2 ? c.ExitNext : ip0 + (c.ExitNext - code.Start); }
        var rNat = (uint[])R.Clone(); for (int i = 0; i < 8; i++) R[i] = rPre[i];
        var r0 = rPre; int op0 = _ccOp; uint d0 = _ccDst, s10 = _ccSrc1, s20 = _ccSrc2, m0 = _ccMask, f0 = Flags;
        NextIp = ipE; _steps = st0;
        for (int i = 0; i < steps; i++) { if (Step1(false) != OK) break; }
        string bad = "";
        for (int i = 0; i < 8; i++) if (R[i] != rNat[i]) bad += $" r{i} {R[i]:X}/{rNat[i]:X}";
        if (Cycle != c.Cyc) bad += $" cyc {Cycle - cyc0}/{c.Cyc - cyc0}";
        if (NextIp != nNext) bad += $" next {NextIp:X}/{nNext:X}";
        if (_ccMask != c.Mask || _ccDst != c.Dst) bad += $" cc {_ccOp},{_ccDst:X},{_ccMask:X}/{c.Op},{c.Dst:X},{c.Mask:X}";
        if (bad != "" && ++_nvBad <= 20) Console.Error.WriteLine($"nverify {code.Start:X8} a0 {code.Addrs[0]:X8} steps {steps} exit k{c.ExitK} kind {c.ExitKind}:{bad} | " + Convert.ToHexString(Mem, (int)code.Addrs[0], 24) + " kinds " + string.Join(",", code.Ins!.Take(6).Select(x => x.Kind)));
        for (int i = 0; i < 8; i++) R[i] = rNat[i];
        _ccOp = op0; _ccDst = d0; _ccSrc1 = s10; _ccSrc2 = s20; _ccMask = m0; Flags = f0; Cycle = cyc0; Ip = ipE; NextIp = nip0; _steps = st0;
    }

    public static string NativeReport() =>
        $"native: compiled {NatCompiled} (covered instrs {NatInstrs}), runs {NatRuns} retired {NatRetired}, exits before {NatBefore} after {NatAfter}, no-progress {NatNoProgress}, skipped {NatSkipped}, demoted {NatDemoted}, chained {NatChained}, code {NExecMem.BytesUsed / 1024} KB" +
        (NativeStat ? $" | stat ({(NativeOn ? "native" : "IL")}): runs {StatRuns} retired {StatRetired} ticks {StatTicks} = {(StatRetired == 0 ? 0 : StatTicks * 1e9 / System.Diagnostics.Stopwatch.Frequency / StatRetired):F2} ns/retired, {(StatRuns == 0 ? 0 : StatTicks * 1e9 / System.Diagnostics.Stopwatch.Frequency / StatRuns):F1} ns/run, inside the native call {StatCallTicks * 1e9 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, StatRuns):F1} ns/run" : "") +
        (NativeStat ? "\nnative exits before, by kind (k=kind, s=supported: a runtime leave): " + string.Join(" ", Enumerable.Range(0, 128).Where(i => NatWhy[i] > 0).OrderByDescending(i => NatWhy[i]).Take(16).Select(i => $"k{i / 2}{(i % 2 == 1 ? "s" : "")}={NatWhy[i]}")) : "");
}
