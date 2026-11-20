using System;
using System.Text;

namespace cor64.Mips.R4300I
{
    // [cpu_diff] In-process reference checker (debug aid, COR64_CPUDIFF_REF=1; interpreter steps only, so use it with
    // COR64_CPU_REC=0). Before each interpreted instruction it snapshots GPRs, HI/LO and the raw FPR file; after the
    // handler it recomputes the instruction's register results with an independent implementation written from the
    // ares (ISC) interpreter semantics and the VR4300 manual, and logs every mismatch to telemetry/cpuref.txt.
    // Loads are checked only for direct-mapped RDRAM addresses (KSEG0/KSEG1 below 8 MB). Stores, branches, COP0 and
    // anything that raised an exception are not checked. Emulation is never changed.
    public partial class Interpreter
    {
        internal static readonly bool s_Ref = Environment.GetEnvironmentVariable("COR64_CPUDIFF_REF") == "1";
        private readonly ulong[] m_RefG = new ulong[32];
        private readonly ulong[] m_RefF = new ulong[32];
        private ulong m_RefHi, m_RefLo; private uint m_RefFcr; private bool m_RefFr; private uint m_RefPc;
        private static int s_RefLogged; private static long s_RefChecked;
        private static readonly System.Collections.Generic.Dictionary<string, int> s_RefSeen = new();

        private uint m_RefExpPc; private int m_RefExpIn; private uint m_RefBrPc, m_RefBrInst;
        private unsafe void RefBefore()
        {
            if (m_RefExpIn > 0 && --m_RefExpIn == 0) {
                uint pc = (uint)PC;
                if (pc != m_RefExpPc && (pc & 0xFFFFFE00u) != 0x80000000u) { // ignore exception vectors
                    m_RefPc = m_RefBrPc; RefLog(m_RefBrInst, "nextpc", m_RefExpPc, pc);
                }
            }
            for (int i = 0; i < 32; i++) m_RefG[i] = ReadGPR64(i);
            ulong* f = (ulong*)StateR4000I.FPR.RawPointer;
            for (int i = 0; i < 32; i++) m_RefF[i] = f[i];
            m_RefHi = ReadHi(); m_RefLo = ReadLo(); m_RefFcr = State.FCR.Value; m_RefFr = StateR4000I.FPR.FullMode; m_RefPc = (uint)PC; m_RefLL = State.LLBit;
        }

        // ---- reference FPR access on the snapshot (FR=1: 32 x 64-bit; FR=0: odd single = high word of the even pair) ----
        private uint RfW(int i) => m_RefFr ? (uint)m_RefF[i] : ((i & 1) == 0 ? (uint)m_RefF[i] : (uint)(m_RefF[i & ~1] >> 32));
        private ulong RfD(int i) => m_RefFr ? m_RefF[i] : m_RefF[i & ~1];
        private float RfS(int i) => BitConverter.Int32BitsToSingle((int)RfW(i));
        private double RfDd(int i) => BitConverter.Int64BitsToDouble((long)RfD(i));

        private unsafe uint ActW(int i) { ulong* f = (ulong*)StateR4000I.FPR.RawPointer; return m_RefFr ? (uint)f[i] : ((i & 1) == 0 ? (uint)f[i] : (uint)(f[i & ~1] >> 32)); }
        private unsafe ulong ActD(int i) { ulong* f = (ulong*)StateR4000I.FPR.RawPointer; return m_RefFr ? f[i] : f[i & ~1]; }

        private unsafe bool RefRam(ulong va, int n, out ulong v)
        {
            v = 0; uint a = (uint)va;
            if (a < 0x80000000u || a >= 0xC0000000u) return false;
            uint p = a & 0x1FFFFFFF; if (p + (uint)n > 0x800000) return false;
            byte* b = (byte*)RdramWords;
            for (int k = 0; k < n; k++) { uint q = p + (uint)k; v = (v << 8) | b[(q & ~3u) + (3 - (q & 3))]; }
            return true;
        }

        private void RefLog(uint inst, string what, ulong exp, ulong act)
        {
            string key = (inst >> 26).ToString() + ":" + (inst & 63) + ":" + what.Split(' ')[0];
            lock (s_RefSeen) {
                s_RefSeen.TryGetValue(key, out int c); s_RefSeen[key] = c + 1;
                if (c >= 3 || s_RefLogged >= 2000) return;
                s_RefLogged++;
            }
            var sb = new StringBuilder();
            sb.AppendFormat("pc={0:X8} inst={1:X8} {2,-24} {3} exp={4:X16} act={5:X16} checked={6} fr={7} fcr={8:X8}",
                m_RefPc, inst, Disassembler.Disassemble(m_RefPc, Disassembler.Decode(inst)), what, exp, act, s_RefChecked, m_RefFr ? 1 : 0, m_RefFcr);
            int rs = (int)(inst >> 21) & 31, rt = (int)(inst >> 16) & 31;
            sb.AppendFormat(" rs={0:X16} rt={1:X16} hi={2:X16} lo={3:X16}", m_RefG[rs], m_RefG[rt], m_RefHi, m_RefLo);
            int fs = (int)(inst >> 11) & 31, ft = rt;
            sb.AppendFormat(" fs={0:X16} ft={1:X16}", RfD(fs), RfD(ft));
            System.IO.Directory.CreateDirectory("telemetry");
            System.IO.File.AppendAllText("telemetry/cpuref.txt", sb.AppendLine().ToString());
        }

        private static ulong Sx32(ulong v) => (ulong)(long)(int)(uint)v;

        private void CheckG(uint inst, int r, ulong exp)
        {
            if (r == 0) return;
            ulong act = ReadGPR64(r);
            if (act != exp) RefLog(inst, "gpr" + r, exp, act);
        }
        private void CheckHL(uint inst, ulong hi, ulong lo)
        {
            if (ReadHi() != hi) RefLog(inst, "hi", hi, ReadHi());
            if (ReadLo() != lo) RefLog(inst, "lo", lo, ReadLo());
        }
        private void CheckFW(uint inst, int r, uint exp) { uint a = ActW(r); if (a != exp) RefLog(inst, "fprw" + r, exp, a); }
        private void CheckFD(uint inst, int r, ulong exp) { ulong a = ActD(r); if (a != exp) RefLog(inst, "fprd" + r, exp, a); }
        private static bool SameF(ulong a, ulong b, bool dbl)
        {
            if (a == b) return true;
            // any NaN matches any NaN (the payload is not modelled)
            if (dbl) return double.IsNaN(BitConverter.Int64BitsToDouble((long)a)) && double.IsNaN(BitConverter.Int64BitsToDouble((long)b));
            return float.IsNaN(BitConverter.Int32BitsToSingle((int)(uint)a)) && float.IsNaN(BitConverter.Int32BitsToSingle((int)(uint)b));
        }
        private void CheckFS(uint inst, int r, float v) { uint e = (uint)BitConverter.SingleToInt32Bits(v); uint a = ActW(r); if (!SameF(e, a, false)) RefLog(inst, "fprs" + r, e, a); }
        private void CheckFDd(uint inst, int r, double v) { ulong e = (ulong)BitConverter.DoubleToInt64Bits(v); ulong a = ActD(r); if (!SameF(e, a, true)) RefLog(inst, "fprD" + r, e, a); }

        private static double RefRound(double v, int mode) => mode switch {
            1 => Math.Truncate(v), 2 => Math.Ceiling(v), 3 => Math.Floor(v), _ => Math.Round(v, MidpointRounding.ToEven)
        };

        private void RefAfter(uint inst)
        {
            if (Cop0.ExceptionPending || Cop0.TlbExceptionPending || Cop0.ServicingRequred) return;
            s_RefChecked++;
            var g = m_RefG;
            int op = (int)(inst >> 26), rs = (int)(inst >> 21) & 31, rt = (int)(inst >> 16) & 31, rd = (int)(inst >> 11) & 31;
            int sa = (int)(inst >> 6) & 31, fn = (int)inst & 63;
            long simm = (short)(inst & 0xFFFF); ulong zimm = inst & 0xFFFF;
            ulong S = g[rs], T = g[rt];
            RefBranch(inst, op, rs, rt, fn, S, T, simm);
            if (op >= 0x28 && op <= 0x2F || op == 0x38 || op == 0x3C || op == 0x3F) { RefStore(inst, op, S + (ulong)simm, T); return; }
            switch (op)
            {
                case 0:
                    switch (fn)
                    {
                        case 0x09: CheckG(inst, rd, Sx32(m_RefPc + 8)); break; // JALR link
                        case 0x00: CheckG(inst, rd, Sx32((uint)T << sa)); break;                       // SLL
                        case 0x02: CheckG(inst, rd, Sx32((uint)T >> sa)); break;                       // SRL
                        case 0x03: CheckG(inst, rd, Sx32((ulong)((long)T >> sa))); break;              // SRA (ares: s32(rt.s64 >> sa))
                        case 0x04: CheckG(inst, rd, Sx32((uint)T << (int)(S & 31))); break;           // SLLV
                        case 0x06: CheckG(inst, rd, Sx32((uint)T >> (int)(S & 31))); break;           // SRLV
                        case 0x07: CheckG(inst, rd, Sx32((ulong)((long)T >> (int)(S & 31)))); break;  // SRAV
                        case 0x10: CheckG(inst, rd, m_RefHi); break;
                        case 0x11: CheckHL(inst, S, m_RefLo); break;
                        case 0x12: CheckG(inst, rd, m_RefLo); break;
                        case 0x13: CheckHL(inst, m_RefHi, S); break;
                        case 0x14: CheckG(inst, rd, T << (int)(S & 63)); break;                       // DSLLV
                        case 0x16: CheckG(inst, rd, T >> (int)(S & 63)); break;                       // DSRLV
                        case 0x17: CheckG(inst, rd, (ulong)((long)T >> (int)(S & 63))); break;        // DSRAV
                        case 0x18: { ulong r = (ulong)((long)(int)(uint)S * (long)(int)(uint)T); CheckHL(inst, Sx32(r >> 32), Sx32(r)); break; } // MULT
                        case 0x19: { ulong r = (ulong)(uint)S * (uint)T; CheckHL(inst, Sx32(r >> 32), Sx32(r)); break; }  // MULTU
                        case 0x1A: {                                                                   // DIV
                            int a = (int)(uint)S, b = (int)(uint)T;
                            if (b == 0) CheckHL(inst, Sx32((uint)a), a < 0 ? 1UL : ulong.MaxValue);
                            else if (a == int.MinValue && b == -1) CheckHL(inst, 0, Sx32(0x80000000u));
                            else CheckHL(inst, Sx32((uint)(a % b)), Sx32((uint)(a / b)));
                            break; }
                        case 0x1B: {                                                                   // DIVU
                            uint a = (uint)S, b = (uint)T;
                            if (b == 0) CheckHL(inst, Sx32(a), ulong.MaxValue);
                            else CheckHL(inst, Sx32(a % b), Sx32(a / b));
                            break; }
                        case 0x1C: { var r = Math.BigMul((long)S, (long)T, out long lo); CheckHL(inst, (ulong)r, (ulong)lo); break; } // DMULT
                        case 0x1D: { ulong h = Math.BigMul(S, T, out ulong lo); CheckHL(inst, h, lo); break; }   // DMULTU
                        case 0x1E: {                                                                   // DDIV
                            long a = (long)S, b = (long)T;
                            if (b == 0) CheckHL(inst, (ulong)a, a < 0 ? 1UL : ulong.MaxValue);
                            else if (a == long.MinValue && b == -1) CheckHL(inst, 0, (ulong)a);
                            else CheckHL(inst, (ulong)(a % b), (ulong)(a / b));
                            break; }
                        case 0x1F: {                                                                   // DDIVU
                            if (T == 0) CheckHL(inst, S, ulong.MaxValue);
                            else CheckHL(inst, S % T, S / T);
                            break; }
                        case 0x20: case 0x21: CheckG(inst, rd, Sx32((uint)S + (uint)T)); break;        // ADD(U)
                        case 0x22: case 0x23: CheckG(inst, rd, Sx32((uint)S - (uint)T)); break;        // SUB(U)
                        case 0x24: CheckG(inst, rd, S & T); break;
                        case 0x25: CheckG(inst, rd, S | T); break;
                        case 0x26: CheckG(inst, rd, S ^ T); break;
                        case 0x27: CheckG(inst, rd, ~(S | T)); break;
                        case 0x2A: CheckG(inst, rd, (long)S < (long)T ? 1UL : 0); break;
                        case 0x2B: CheckG(inst, rd, S < T ? 1UL : 0); break;
                        case 0x2C: case 0x2D: CheckG(inst, rd, S + T); break;                          // DADD(U)
                        case 0x2E: case 0x2F: CheckG(inst, rd, S - T); break;                          // DSUB(U)
                        case 0x38: CheckG(inst, rd, T << sa); break;
                        case 0x3A: CheckG(inst, rd, T >> sa); break;
                        case 0x3B: CheckG(inst, rd, (ulong)((long)T >> sa)); break;
                        case 0x3C: CheckG(inst, rd, T << (sa + 32)); break;
                        case 0x3E: CheckG(inst, rd, T >> (sa + 32)); break;
                        case 0x3F: CheckG(inst, rd, (ulong)((long)T >> (sa + 32))); break;
                    }
                    break;
                case 0x08: case 0x09: CheckG(inst, rt, Sx32((uint)S + (uint)simm)); break;            // ADDI(U)
                case 0x0A: CheckG(inst, rt, (long)S < simm ? 1UL : 0); break;
                case 0x0B: CheckG(inst, rt, S < (ulong)simm ? 1UL : 0); break;
                case 0x0C: CheckG(inst, rt, S & zimm); break;
                case 0x0D: CheckG(inst, rt, S | zimm); break;
                case 0x0E: CheckG(inst, rt, S ^ zimm); break;
                case 0x0F: CheckG(inst, rt, Sx32((uint)zimm << 16)); break;
                case 0x18: case 0x19: CheckG(inst, rt, S + (ulong)simm); break;                        // DADDI(U)
                case 0x20: case 0x24: case 0x21: case 0x25: case 0x23: case 0x27: case 0x37: case 0x30: {
                    ulong va = S + (ulong)simm; int n = op switch { 0x20 or 0x24 => 1, 0x21 or 0x25 => 2, 0x37 => 8, _ => 4 };
                    if (!RefRam(va, n, out ulong v)) break;
                    ulong e = op switch {
                        0x20 => (ulong)(long)(sbyte)v, 0x24 => v, 0x21 => (ulong)(long)(short)v, 0x25 => v,
                        0x23 or 0x30 => Sx32(v), 0x27 => v, _ => v };
                    CheckG(inst, rt, e); break; }
                case 0x22: case 0x26: {                                                                // LWL / LWR (big endian)
                    ulong va = S + (ulong)simm; if (!RefRam(va & ~3UL, 4, out ulong w)) break;
                    int k = (int)(va & 3); uint data = (uint)T;
                    if (op == 0x22) {
                        uint m = k == 0 ? 0u : (0xFFFFFFFFu >> (32 - 8 * k));
                        data = (data & m) | ((uint)w << (8 * k));
                        CheckG(inst, rt, Sx32(data));
                    } else {
                        int sh = 8 * (3 - k); uint m = k == 3 ? 0u : (0xFFFFFFFFu << (8 * (k + 1)));
                        data = (data & m) | ((uint)w >> sh);
                        CheckG(inst, rt, k == 3 ? Sx32(data) : (T & 0xFFFFFFFF00000000UL) | data);
                    }
                    break; }
                case 0x1A: case 0x1B: {                                                                // LDL / LDR
                    ulong va = S + (ulong)simm; if (!RefRam(va & ~7UL, 8, out ulong w)) break;
                    int k = (int)(va & 7); ulong data = T;
                    if (op == 0x1A) { ulong m = k == 0 ? 0UL : (ulong.MaxValue >> (64 - 8 * k)); data = (data & m) | (w << (8 * k)); }
                    else { int sh = 8 * (7 - k); ulong m = k == 7 ? 0UL : (ulong.MaxValue << (8 * (k + 1))); data = (data & m) | (w >> sh); }
                    CheckG(inst, rt, data); break; }
                case 0x31: { ulong va = S + (ulong)simm; if (RefRam(va, 4, out ulong v)) CheckFW(inst, rt, (uint)v); break; }   // LWC1
                case 0x35: { ulong va = S + (ulong)simm; if (RefRam(va, 8, out ulong v)) CheckFD(inst, rt, v); break; }         // LDC1
                case 0x11: RefCop1(inst); break;
            }
        }

        private void RefBranch(uint inst, int op, int rs, int rt, int fn, ulong S, ulong T, long simm)
        {
            if (BU_ExecuteBranchDelay) { /* this instruction is itself in a delay slot */ }
            uint pc = m_RefPc; bool isBr = true, taken = false, likely = false; uint target = (uint)(pc + 4 + (simm << 2));
            switch (op) {
                case 0: if (fn == 8 || fn == 9) { taken = true; target = (uint)S; } else isBr = false; break;
                case 1: {
                    int c = rt & 0x13; likely = (rt & 2) != 0;
                    if ((rt & 0x1C) == 0x0C || (rt & 0x1C) == 0x08) { isBr = false; break; } // traps
                    taken = (rt & 1) == 0 ? (long)S < 0 : (long)S >= 0;
                    if ((rt & 0x10) != 0) CheckG(inst, 31, Sx32(pc + 8));
                    break; }
                case 2: case 3: taken = true; target = ((pc + 4) & 0xF0000000u) | ((inst & 0x03FFFFFF) << 2); if (op == 3) CheckG(inst, 31, Sx32(pc + 8)); break;
                case 4: case 0x14: taken = S == T; likely = op == 0x14; break;
                case 5: case 0x15: taken = S != T; likely = op == 0x15; break;
                case 6: case 0x16: taken = (long)S <= 0; likely = op == 0x16; break;
                case 7: case 0x17: taken = (long)S > 0; likely = op == 0x17; break;
                case 0x11: if (((inst >> 21) & 31) == 8) { bool c = (m_RefFcr & (1u << 23)) != 0; taken = ((inst >> 16) & 1) != 0 ? c : !c; likely = ((inst >> 17) & 1) != 0; } else isBr = false; break;
                default: isBr = false; break;
            }
            if (!isBr) return;
            m_RefBrPc = pc; m_RefBrInst = inst;
            if (taken) { m_RefExpPc = target; m_RefExpIn = 2; }
            else if (likely) { m_RefExpPc = pc + 8; m_RefExpIn = 1; }
            else { m_RefExpPc = pc + 8; m_RefExpIn = 2; }
        }

        private void RefStore(uint inst, int op, ulong va, ulong T)
        {
            int n; ulong e; ulong mask = 0;
            switch (op) {
                case 0x28: n = 1; e = T & 0xFF; break;
                case 0x29: n = 2; e = T & 0xFFFF; break;
                case 0x2B: n = 4; e = (uint)T; break;
                case 0x3F: n = 8; e = T; break;
                case 0x38: if (!m_RefLL) return; n = 4; e = (uint)T; break; // SC: only a successful one is checked
                case 0x2A: case 0x2E: { // SWL / SWR
                    if (!RefRam(va & ~3UL, 4, out ulong w)) return; int k = (int)(va & 3);
                    // SWL writes bytes k..3 with rt's high bytes; SWR writes bytes 0..k with rt's low bytes
                    if (op == 0x2A) { mask = 0xFFFFFFFFu >> (8 * k); e = ((uint)T >> (8 * k)); }
                    else { mask = 0xFFFFFFFFu << (8 * (3 - k)); e = ((uint)T << (8 * (3 - k))) & 0xFFFFFFFFu; }
                    RefPreStore(va & ~3UL, 4, mask, e); return; }
                case 0x2C: case 0x2D: { // SDL / SDR
                    int k = (int)(va & 7);
                    if (op == 0x2C) { mask = ulong.MaxValue >> (8 * k); e = T >> (8 * k); }
                    else { mask = ulong.MaxValue << (8 * (7 - k)); e = T << (8 * (7 - k)); }
                    RefPreStore(va & ~7UL, 8, mask, e); return; }
                default: return;
            }
            if (!RefRam(va, n, out ulong a)) return;
            if (a != e) RefLog(inst, "mem" + n + "@" + ((uint)va).ToString("X8"), e, a);
        }
        private void RefPreStore(ulong va, int n, ulong mask, ulong e)
        {
            if (!RefRam(va, n, out ulong a)) return;
            if ((a & mask) != (e & mask)) RefLog(Disassembler == null ? 0 : CurrentInst.Inst.inst, "memlr" + n + "@" + ((uint)va).ToString("X8"), e & mask, a & mask);
        }
        private bool m_RefLL;

        private void RefCop1(uint inst)
        {
            int fmt = (int)(inst >> 21) & 31, ft = (int)(inst >> 16) & 31, fs = (int)(inst >> 11) & 31, fd = (int)(inst >> 6) & 31, fn = (int)inst & 63;
            int rt = ft;
            switch (fmt)
            {
                case 0: CheckG(inst, rt, Sx32(RfW(fs))); return;          // MFC1
                case 1: CheckG(inst, rt, RfD(fs)); return;                // DMFC1
                case 4: CheckFW(inst, fs, (uint)m_RefG[rt]); return;      // MTC1
                case 5: CheckFD(inst, fs, m_RefG[rt]); return;            // DMTC1
                case 2: if (fs == 31) CheckG(inst, rt, Sx32(m_RefFcr)); return; // CFC1
                case 16: case 17: case 20: case 21: break;
                default: return;
            }
            int rm = (int)(m_RefFcr & 3);
            bool dbl = fmt == 17;
            if (fmt == 16 || fmt == 17)
            {
                double a = dbl ? RfDd(fs) : RfS(fs), b = dbl ? RfDd(ft) : RfS(ft);
                if (rm != 0 && fn < 8) return; // arithmetic in a non-nearest mode is not modelled
                switch (fn)
                {
                    case 0: if (dbl) CheckFDd(inst, fd, a + b); else CheckFS(inst, fd, RfS(fs) + RfS(ft)); return;
                    case 1: if (dbl) CheckFDd(inst, fd, a - b); else CheckFS(inst, fd, RfS(fs) - RfS(ft)); return;
                    case 2: if (dbl) CheckFDd(inst, fd, a * b); else CheckFS(inst, fd, RfS(fs) * RfS(ft)); return;
                    case 3: if (dbl) CheckFDd(inst, fd, a / b); else CheckFS(inst, fd, RfS(fs) / RfS(ft)); return;
                    case 4: if (dbl) CheckFDd(inst, fd, Math.Sqrt(a)); else CheckFS(inst, fd, MathF.Sqrt(RfS(fs))); return;
                    case 5: if (dbl) CheckFD(inst, fd, RfD(fs) & ~(1UL << 63)); else CheckFW(inst, fd, RfW(fs) & 0x7FFFFFFFu); return;
                    case 6: if (dbl) CheckFD(inst, fd, RfD(fs)); else CheckFW(inst, fd, RfW(fs)); return;   // MOV: raw copy
                    case 7: if (dbl) CheckFD(inst, fd, RfD(fs) ^ (1UL << 63)); else CheckFW(inst, fd, RfW(fs) ^ 0x80000000u); return;
                    case 8: case 9: case 10: case 11: case 12: case 13: case 14: case 15: case 36: case 37: {
                        int mode = fn >= 36 ? rm : (fn & 3);
                        double r = RefRound(a, mode);
                        bool w = fn >= 12 && fn <= 15 || fn == 36;
                        if (double.IsNaN(r) || double.IsInfinity(r)) return; // invalid: exception path, not modelled
                        if (w) { if (r >= 2147483648.0 || r < -2147483648.0) return; CheckFW(inst, fd, (uint)(int)r); }
                        else { if (r >= 9.2233720368547758e18 || r < -9.2233720368547758e18) return; CheckFD(inst, fd, (ulong)(long)r); }
                        return; }
                    case 32: if (dbl && rm == 0) CheckFS(inst, fd, (float)a); return;   // CVT.S.D
                    case 33: if (!dbl) CheckFDd(inst, fd, (double)RfS(fs)); return;     // CVT.D.S
                }
                if (fn >= 48)
                {
                    bool un = double.IsNaN(a) || double.IsNaN(b);
                    bool c = ((fn & 1) != 0 && un) || ((fn & 2) != 0 && !un && a == b) || ((fn & 4) != 0 && !un && a < b);
                    bool act = (State.FCR.Value & (1u << 23)) != 0;
                    if (act != c) RefLog(inst, "fcc", c ? 1UL : 0, act ? 1UL : 0);
                }
                return;
            }
            // W (20) / L (21) sources
            if (rm != 0) return;
            if (fmt == 20) {
                int w = (int)RfW(fs);
                if (fn == 32) CheckFS(inst, fd, (float)w); else if (fn == 33) CheckFDd(inst, fd, w);
            } else {
                long l = (long)RfD(fs);
                if (fn == 32) CheckFS(inst, fd, (float)l); else if (fn == 33) CheckFDd(inst, fd, (double)l);
            }
        }
    }
}
