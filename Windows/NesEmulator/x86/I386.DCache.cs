// Intel 80386 / 80486 core: the decoded-block interpreter (our own addition, not in tiny386).
//
// Code the block compiler (I386.Jit.cs) cannot keep up with - Win98's GDI writes fresh 16-bit blit code for every call, so a compiled block
// is invalid before it pays off - runs here instead: a straight run of instructions from an entry address is decoded once (DcDecodeFast's
// DEntry forms) and kept, by physical address, with a copy of its bytes. Running it skips the per-instruction fetch, prefix loop, ModR/M
// decode and the JIT lookup; each instruction then runs the interpreter's own body (the same Translate / Load / Store / Alu / flags code), so
// the machine state after every instruction is exactly the byte interpreter's. Invalidation: the block's 64-byte code lines are marked
// (_codeMark), so a store there moves its page's generation (_pgGen); an outside write moves _memEpoch. A block whose page moved compares
// its bytes (same: it runs, else it is decoded again). Inside a block, any page move ends it after that instruction. This cache is not
// machine state: save states do not see it. OFF by default; X86_DC=1 turns it on (it did not pay: see DbOn).

using System.Runtime.CompilerServices;

namespace NesEmulator.X86;

public sealed partial class I386
{
    private struct DbIns
    {
        public DEntry E;
        public int Cost;      // Cycle: 1 + Step1's opcode cost (+ ExecTwo's 0F cost) + DecodeModrm's memory operand cost
        public bool Mem;      // a memory operand (_addr from Base / Index / Scale / Disp)
        public sbyte CurSeg;  // _curSeg as the interpreter leaves it after decoding (the resolved segment with a memory operand, else the prefix)
    }

    private sealed class DBlock
    {
        public uint Pa = 0xffffffff, Gen, Epoch, Len;
        public bool C16;
        public int N;                       // 0: the first instruction is not one of the forms (interpret it)
        public DbIns[] Ins = new DbIns[8];
        public byte[] Bytes = new byte[64];
    }

    private static readonly bool DbOn = Environment.GetEnvironmentVariable("X86_DC") != null;   // opt-in: measured neutral on GDIB/GDIF/CPUB and 20% slower on an interpreter-only boot (2026-11-19)
    private static readonly int DbMax = int.TryParse(Environment.GetEnvironmentVariable("X86_DCMAX"), out int dm) ? Math.Clamp(dm, 1, 256) : 64;
    private const int DbBits = 14, DbMask = (1 << DbBits) - 1;
    private readonly DBlock?[] _db = new DBlock?[1 << DbBits];
    public long DbRuns, DbInstrs, DbBuilds, DbRebuilds;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DbIndex(uint pa) => (int)((pa ^ (pa >> DbBits)) & DbMask);

    /// <summary>Step1's hook after the compiled code declined: run the decoded block at <paramref name="paddr"/> (OK / FAIL as Step1's own), or
    /// JIT_DECLINE (interpret this instruction).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int DbTry(uint paddr)
    {
        var b = _db[DbIndex(paddr)];
        if (b == null || b.Pa != paddr || b.C16 != _c16 || b.Epoch != _memEpoch || b.Gen != _pgGen[paddr >> 12])
            b = DbLookupSlow(paddr);
        if (b.N == 0) return JIT_DECLINE;
        return DbRun(b, paddr);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private DBlock DbLookupSlow(uint paddr)
    {
        ref DBlock? slot = ref _db[DbIndex(paddr)];
        var b = slot ??= new DBlock();
        if (b.Pa == paddr && b.C16 == _c16 && Mem.AsSpan((int)paddr, (int)b.Len).SequenceEqual(b.Bytes.AsSpan(0, (int)b.Len)))
        {   // the page moved (or memory was written from outside) but these bytes did not change
            b.Gen = _pgGen[paddr >> 12]; b.Epoch = _memEpoch;
            return b;
        }
        if (b.Pa == paddr) DbRebuilds++;
        DbBuild(b, paddr);
        return b;
    }

    /// <summary>Decode the straight run of cached forms from <paramref name="paddr"/>: up to a control transfer (included), an instruction that is
    /// not one of the forms (excluded), the end of the fetch window (the page's last 16 bytes) or DbMax instructions.</summary>
    private void DbBuild(DBlock b, uint paddr)
    {
        DbBuilds++;
        b.Pa = paddr; b.C16 = _c16; b.N = 0;
        uint pa = paddr;
        int n = 0;
        while ((pa & 0xfff) < 4096 - 16 && n < DbMax)
        {
            var e = default(DEntry);
            DcDecodeFast(ref e, pa);
            if (!DbForm(ref e, pa, out int b1, out int b2, out bool mem, out bool ends)) break;
            if (n == b.Ins.Length) Array.Resize(ref b.Ins, n * 2);
            ref DbIns d = ref b.Ins[n++];
            d.E = e; d.Mem = mem;
            d.CurSeg = mem ? e.Seg : e.SegRaw;
            int c = 1 + (mem ? _memCost : 0);
            if (_opCost != null) { c += _opCost[b1]; if (b1 == 0x0f && (b2 & 0xf0) != 0x80) c += _opCost0F![b2]; }
            d.Cost = c;
            pa += e.Len;
            if (ends) break;
        }
        b.N = n;
        // the bytes it was decoded from (an empty block keeps 16, so a change there is seen too), and their code lines marked
        uint len = n == 0 ? 16 : pa - paddr;
        b.Len = len;
        if (b.Bytes.Length < len) b.Bytes = new byte[Math.Max(len, (uint)b.Bytes.Length * 2)];
        Mem.AsSpan((int)paddr, (int)len).CopyTo(b.Bytes);
        for (uint l = paddr >> 6, le = (paddr + len - 1) >> 6; l <= le; l++) _codeMark[l] = 1;
        b.Gen = _pgGen[paddr >> 12]; b.Epoch = _memEpoch;
    }

    /// <summary>Is this decoded instruction one DbExec runs? Its first opcode byte (and the 0F one), whether it has a memory operand, whether a block ends after it.</summary>
    private bool DbForm(ref DEntry e, uint pa, out int b1, out int b2, out bool mem, out bool ends)
    {
        b1 = b2 = 0; mem = false; ends = false;
        if (e.Kind == DK_SLOW || e.Len == 0) return false;
        int p = 0; uint x;
        do x = U8(pa + (uint)p++); while (IsPrefix[x] && p < 15);
        b1 = (int)x;
        if (b1 == 0x0f)
        {
            b2 = (int)U8(pa + (uint)p);
            if (!_legacy && !ModelAllows0F((uint)b2)) return false;
        }
        bool modrm;
        switch (e.Kind)
        {
            case DK_MOVEG: case DK_MOVGE: case DK_ALUEG: case DK_ALUGE: case DK_GRP1: case DK_GRP2: case DK_TESTEG: case DK_G3TEST: case DK_GRP3:
            case DK_MOVEI: case DK_LEA: case DK_MOVX: case DK_SETCC: case DK_IMUL: case DK_MOVSR: case DK_SRMOV: case DK_XCHG:
                modrm = true; break;
            case DK_GRP5:
                modrm = true; ends = e.Reg is 2 or 4; break;
            case DK_JCC: case DK_JMP: case DK_CALL: case DK_RET:
                modrm = false; ends = true; break;
            case DK_ALUAI: case DK_MOVRI: case DK_INC: case DK_DEC: case DK_PUSH: case DK_POP: case DK_CBW: case DK_LEAVE: case DK_STOS: case DK_STR:
            case DK_REPSTR: case DK_FLAGOP: case DK_STKOP:
                modrm = false; break;
            default: return false;   // FPU, ...: the interpreter
        }
        mem = modrm && e.Mod != 3;
        return true;
    }

    /// <summary>Run block <paramref name="b"/> (valid, at the fetch position): each instruction exactly as Step1 would (Ip, Cycle, the decode state,
    /// then the interpreter's body), as long as execution falls through inside the block, the step budget lasts and its code page has not moved.</summary>
    private int DbRun(DBlock b, uint paddr)
    {
        DbRuns++;
        DbIns[] ins = b.Ins;
        int n = b.N, i = 0;
        uint pa = paddr, pg = paddr >> 12, gen = b.Gen, epoch = b.Epoch, csb = SegBase[SEG_CS];
        ulong ifl = _ifLaddr; uint ifx = _ifXaddr;
        for (;;)
        {
            ref DbIns d = ref ins[i];
            ref DEntry e = ref d.E;
            Ip = NextIp;
            uint next = Ip + e.Len;
            NextIp = next; _ifPaddr = pa + e.Len;
            Cycle += d.Cost;
            _opsz16 = e.O16; _adsz16 = e.A16; _rep = 0; _curSeg = d.CurSeg;
            _mod = e.Mod; _reg = e.Reg; _rm = e.Rm;
            if (d.Mem) _addr = DbAddr(ref e);
            if (!DbExec(ref e)) { DbInstrs += i + 1; return FAIL; }
            if (++i == n || _steps <= 1 || NextIp != next || _pgGen[pg] != gen || epoch != _memEpoch) break;
            if (ifl != _ifLaddr || ifx != _ifXaddr || csb != SegBase[SEG_CS]) break;   // (the next instruction is in this page: the fetch window holds unless it moved)
            pa += e.Len;
            _steps--;
            _c16 = Code16; _spm = SpMask;
        }
        DbInstrs += i;
        return OK;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint DbAddr(ref DEntry e)
    {
        uint a = e.Disp;
        if (e.Base != 8) a += R[e.Base];
        if (e.Index != 8) a += R[e.Index] << e.Scale;
        return e.A16 ? a & 0xffff : a;
    }

    /// <summary>One decoded instruction after the decode state is set: the interpreter's body for it (Exec1 / ExecOne / ExecTwo after their fetches).</summary>
    private bool DbExec(ref DEntry e)
    {
        int bits = e.Bits;
        switch (e.Kind)
        {
            case DK_MOVEG:
                if (!TranslateE(bits, 2)) return false;
                WriteE(bits, LReg(bits, _reg));
                return true;
            case DK_MOVGE:
                if (!TranslateE(bits, 1)) return false;
                SReg(bits, _reg, ReadE(bits));
                return true;
            case DK_ALUEG:
            {
                int op = e.Op;
                if (!TranslateE(bits, op == 7 ? 1 : 3)) return false;
                uint r = Alu(op, bits, ReadE(bits), LReg(bits, _reg));
                if (op != 7) WriteE(bits, r); else TryJcc8E();
                return true;
            }
            case DK_ALUGE:
            {
                int op = e.Op;
                if (!TranslateE(bits, 1)) return false;
                uint r = Alu(op, bits, LReg(bits, _reg), ReadE(bits));
                if (op != 7) SReg(bits, _reg, r); else TryJcc8E();
                return true;
            }
            case DK_ALUAI:
            {
                int op = e.Op;
                uint r = Alu(op, bits, LReg(bits, 0), e.Imm);
                if (op != 7) SReg(bits, 0, r); else TryJcc8();
                return true;
            }
            case DK_GRP1:
            {
                int op = e.Op;
                if (!TranslateE(bits, op == 7 ? 1 : 3)) return false;
                uint r = Alu(op, bits, ReadE(bits), e.Imm);
                if (op != 7) WriteE(bits, r); else TryJcc8E();
                return true;
            }
            case DK_GRP2:
            {
                int kind = e.Op & 3;
                uint count = kind == 0 ? e.Imm : kind == 1 ? 1u : L8(1);
                if (!TranslateE(bits, 3)) return false;
                Grp2(_reg, bits, count);
                return true;
            }
            case DK_TESTEG:
                if (!TranslateE(bits, 1)) return false;
                Alu(4, bits, ReadE(bits), LReg(bits, _reg));
                TryJcc8E();
                return true;
            case DK_G3TEST:
                if (!TranslateE(bits, 1)) return false;
                Alu(4, bits, ReadE(bits), e.Imm);
                TryJcc8E();
                return true;
            case DK_GRP3: return Grp3Body(bits);
            case DK_MOVEI:
                if (!TranslateE(bits, 2)) return false;
                WriteE(bits, e.Imm);
                return true;
            case DK_LEA: SReg(_opsz16 ? 16 : 32, _reg, _addr); return true;
            case DK_MOVRI: SReg(bits, e.Op, e.Imm); return true;
            case DK_INC: IncDecReg(_opsz16 ? 16 : 32, e.Op, false); return true;
            case DK_DEC: IncDecReg(_opsz16 ? 16 : 32, e.Op, true); return true;
            case DK_PUSH: return _opsz16 ? Push(16, R[e.Op] & 0xffff) : Push(32, R[e.Op]);
            case DK_POP: return PopReg(_opsz16 ? 16 : 32, e.Op);
            case DK_JCC:
            {
                int op = e.Op;
                if (op >= 0xe0)
                {
                    int a = _adsz16 ? 16 : 32;
                    if (op == 0xe3) { if (LReg(a, 1) == 0) JumpRel(e.Imm); return true; }
                    SReg(a, 1, LReg(a, 1) - 1);
                    if (LReg(a, 1) != 0 && (op == 0xe2 || (op == 0xe1 ? GetZF() != 0 : GetZF() == 0))) JumpRel(e.Imm);
                    return true;
                }
                if (Cond((uint)op)) JumpRel(e.Imm);
                return true;
            }
            case DK_JMP:
                if (e.Len == 1 && e.Imm == 0) return true;   // NOP (90): the interpreter does not wrap IP
                JumpRel(e.Imm);
                return true;
            case DK_CALL: return CallNear(e.Imm, true);
            case DK_RET: return Ret(0);
            case DK_MOVX:
            {
                if (!TranslateE(bits, 1)) return false;
                uint v = ReadE(bits);
                if (e.Op == 1) v = Sext(bits, v);
                SReg(_opsz16 ? 16 : 32, _reg, v);
                return true;
            }
            case DK_CBW:
                if (e.Op == 0) { if (_opsz16) S16(0, Sext8(L8(0))); else R[0] = Sext16(L16(0)); }
                else { if (_opsz16) S16(2, 0u - (Sext16(L16(0)) >> 31)); else R[2] = 0u - (R[0] >> 31); }
                return true;
            case DK_SETCC:
                if (!TranslateE(8, 2)) return false;
                WriteE(8, Cond(e.Op) ? 1u : 0u);
                return true;
            case DK_IMUL:
                if (e.Op == 0)
                {
                    int v = _opsz16 ? 16 : 32;
                    if (!TranslateE(v, 1)) return false;
                    Imul2(v, _reg, LReg(v, _reg), ReadE(v));
                    return true;
                }
                if (!TranslateE(bits, 1)) return false;
                Imul2(bits, _reg, ReadE(bits), e.Imm);
                return true;
            case DK_GRP5: return JGrp5(bits);
            case DK_LEAVE: return Leave();
            case DK_STOS:
            {   // Stos without REP: one element (no REP timing charge for one iteration)
                var md = new OptAddr();
                if (!Translate(ref md, 2, SEG_ES, LReg(A, 7), bits >> 3)) return false;
                Store(bits, ref md, R[0]);
                Adv(7, Dir(bits));
                return true;
            }
            case DK_STR:
                if (e.Op == 2)
                {   // Lods without REP
                    if (_curSeg == -1) _curSeg = SEG_DS;
                    var ms = new OptAddr();
                    if (!Translate(ref ms, 1, _curSeg, LReg(A, 6), bits >> 3)) return false;
                    SReg(bits, 0, Load(bits, ref ms));
                    Adv(6, Dir(bits));
                    return true;
                }
                return e.Op switch { 0 => Movs(bits), 1 => Cmps(bits), 2 => Lods(bits), _ => Scas(bits) };
            case DK_REPSTR:
                _rep = e.Reg;
                return e.Op switch { 0xa4 or 0xa5 => Movs(bits), 0xa6 or 0xa7 => Cmps(bits), 0xaa or 0xab => Stos(bits), 0xac or 0xad => Lods(bits), _ => Scas(bits) };
            case DK_XCHG:
            {
                if (!TranslateE(bits, 3)) return false;
                uint tmp = LReg(bits, _reg);
                SReg(bits, _reg, ReadE(bits));
                WriteE(bits, tmp);
                return true;
            }
            case DK_FLAGOP: return JFlagOp(e.Op);
            case DK_STKOP: return JStk(e.Op);
            case DK_MOVSR:
                if (_mod == 3) { SReg(_opsz16 ? 16 : 32, _rm, SegSel[_reg] & 0xffff); return true; }
                if (!TranslateE(16, 2)) return false;
                Store16(ref _meml, SegSel[_reg]);
                return true;
            case DK_SRMOV:
                if (!TranslateE(16, 1)) return false;
                return LoadSegChecked(_reg, ReadE(16));
        }
        Abort("decoded block: unknown kind " + e.Kind);
        return false;
    }
}
