// Copied (not referenced) from third_party/cor64/src/cor64/Mips/X64/X64Asm.cs for the x86 native backend (lane D 2026-11-20, X86_NATIVE=1); keep in sync by hand if needed.
// [climb4 x64_emit] A small x86-64 assembler: just the forms the native block emitters need.
// Registers are numbered as in the ModRM encoding (rax=0 ... r15=15). Code is position independent: branches are
// rel32, the only absolute data are jump tables, patched by Link() when the final address is known.
using System;
using System.Collections.Generic;

namespace NesEmulator.X86.Native
{
    public enum R { Rax = 0, Rcx, Rdx, Rbx, Rsp, Rbp, Rsi, Rdi, R8, R9, R10, R11, R12, R13, R14, R15 }

    public enum Cc { O = 0, NO, B, AE, E, NE, BE, A, S, NS, P, NP, L, GE, LE, G }

    /// <summary>A memory operand [base + index*scale + disp].</summary>
    public readonly struct M
    {
        public readonly R Base; public readonly int Index; public readonly int Scale; public readonly int Disp;
        public M(R b, int disp) { Base = b; Index = -1; Scale = 1; Disp = disp; }
        public M(R b, R index, int scale, int disp) { Base = b; Index = (int)index; Scale = scale; Disp = disp; }
    }

    public sealed class XLabel { internal int Pos = -1; }

    public sealed class NAsm
    {
        private byte[] m_Buf = new byte[4096];
        private int m_Len;
        private readonly List<(int at, XLabel l)> m_Rel32 = new();
        private readonly List<(int at, XLabel l)> m_Abs64 = new();

        public int Length => m_Len;

        private void B(int v) { if (m_Len == m_Buf.Length) Array.Resize(ref m_Buf, m_Buf.Length * 2); m_Buf[m_Len++] = (byte)v; }
        private void D(int v) { B(v); B(v >> 8); B(v >> 16); B(v >> 24); }
        private void Q(long v) { D((int)v); D((int)(v >> 32)); }

        // REX prefix; force = emit 0x40 even when empty (byte registers spl/bpl/sil/dil)
        private void Rex(bool w, int reg, int index, int rm, bool force = false)
        {
            int r = 0x40 | (w ? 8 : 0) | ((reg >> 3) & 1) << 2 | ((index >> 3) & 1) << 1 | ((rm >> 3) & 1);
            if (r != 0x40 || force) B(r);
        }

        private void ModRmReg(int reg, int rm) => B(0xC0 | (reg & 7) << 3 | (rm & 7));

        private void ModRmMem(int reg, in M m)
        {
            int b = (int)m.Base;
            int mod = m.Disp == 0 && (b & 7) != 5 ? 0 : (m.Disp >= -128 && m.Disp <= 127) ? 1 : 2;
            if (m.Index < 0 && (b & 7) != 4) {
                B(mod << 6 | (reg & 7) << 3 | (b & 7));
            }
            else {
                int ss = m.Scale == 1 ? 0 : m.Scale == 2 ? 1 : m.Scale == 4 ? 2 : 3;
                int idx = m.Index < 0 ? 4 : m.Index;
                if (idx == 4 && m.Index >= 0) throw new ArgumentException("rsp as index");
                B(mod << 6 | (reg & 7) << 3 | 4);
                B(ss << 6 | (idx & 7) << 3 | (b & 7));
            }
            if (mod == 1) B(m.Disp); else if (mod == 2) D(m.Disp);
        }

        private void OpRM(bool w, int[] op, int reg, in M m, bool forceRex = false, int prefix = -1)
        {
            if (prefix >= 0) B(prefix);
            Rex(w, reg, m.Index < 0 ? 0 : m.Index, (int)m.Base, forceRex);
            foreach (var o in op) B(o);
            ModRmMem(reg, m);
        }

        private void OpRR(bool w, int[] op, int reg, int rm, bool forceRex = false)
        {
            Rex(w, reg, 0, rm, forceRex);
            foreach (var o in op) B(o);
            ModRmReg(reg, rm);
        }

        private static readonly int[] s_Mov = { 0x8B }, s_MovSt = { 0x89 }, s_MovSt8 = { 0x88 }, s_Movsxd = { 0x63 };
        private static readonly int[] s_Movsx8 = { 0x0F, 0xBE }, s_Movzx8 = { 0x0F, 0xB6 }, s_Movsx16 = { 0x0F, 0xBF }, s_Movzx16 = { 0x0F, 0xB7 };
        private static readonly int[] s_Imul = { 0x0F, 0xAF }, s_Lea = { 0x8D };

        // ---- moves ----
        public void MovRM(R d, M m, bool w = true) => OpRM(w, s_Mov, (int)d, m);           // mov d, [m]
        public void MovMR(M m, R s, bool w = true) => OpRM(w, s_MovSt, (int)s, m);         // mov [m], s
        public void MovMR8(M m, R s) => OpRM(false, s_MovSt8, (int)s, m, (int)s >= 4);     // mov byte [m], s8
        public void MovMR16(M m, R s) => OpRM(false, s_MovSt, (int)s, m, false, 0x66);     // mov word [m], s16
        public void MovRR(R d, R s, bool w = true) => OpRR(w, s_MovSt, (int)s, (int)d);
        public void MovsxdRM(R d, M m) => OpRM(true, s_Movsxd, (int)d, m);
        public void MovsxdRR(R d, R s) => OpRR(true, s_Movsxd, (int)d, (int)s);
        public void Movsx8RM(R d, M m) => OpRM(true, s_Movsx8, (int)d, m);
        public void Movzx8RM(R d, M m) => OpRM(false, s_Movzx8, (int)d, m);
        public void Movsx16RM(R d, M m) => OpRM(true, s_Movsx16, (int)d, m);
        public void Movzx16RM(R d, M m) => OpRM(false, s_Movzx16, (int)d, m);
        public void Movzx8RR(R d, R s) => OpRR(false, s_Movzx8, (int)d, (int)s, (int)s >= 4);
        public void Lea(R d, M m) => OpRM(true, s_Lea, (int)d, m);
        /// <summary>lea d32, [m] (x86 lane D: a 32-bit effective address, wrapped)</summary>
        public void Lea32(R d, M m, bool noBase = false)
        {
            if (!noBase) { OpRM(false, s_Lea, (int)d, m); return; }
            // [index*scale + disp32], no base: mod 00, rm 100, SIB base 101
            int ss = m.Scale == 1 ? 0 : m.Scale == 2 ? 1 : m.Scale == 4 ? 2 : 3;
            Rex(false, (int)d, m.Index, 0);
            B(0x8D); B(((int)d & 7) << 3 | 4); B(ss << 6 | (m.Index & 7) << 3 | 5); D(m.Disp);
        }

        public void MovRI(R d, long v)
        {
            if (v == 0) { Alu(Alu8.Xor, d, d, false); return; }
            if ((ulong)v <= 0xFFFFFFFFUL) { Rex(false, 0, 0, (int)d); B(0xB8 + ((int)d & 7)); D((int)v); return; }
            if (v >= int.MinValue && v <= int.MaxValue) { Rex(true, 0, 0, (int)d); B(0xC7); ModRmReg(0, (int)d); D((int)v); return; }
            Rex(true, 0, 0, (int)d); B(0xB8 + ((int)d & 7)); Q(v);
        }
        public void MovMI32(M m, int v, bool w) { OpRM(w, new[] { 0xC7 }, 0, m); D(v); }           // mov dword/qword [m], simm32
        public void MovMI8(M m, int v) { OpRM(false, new[] { 0xC6 }, 0, m); B(v); }
        public void CmpMI8(M m, int v) { OpRM(false, new[] { 0x80 }, 7, m); B(v); }                 // cmp byte [m], imm8

        // ---- ALU ----
        public enum Alu8 { Add = 0, Or = 1, Adc = 2, Sbb = 3, And = 4, Sub = 5, Xor = 6, Cmp = 7 }
        public void Alu(Alu8 op, R d, R s, bool w = true) => OpRR(w, new[] { (int)op * 8 + 1 }, (int)s, (int)d);   // op d, s
        public void AluRM(Alu8 op, R d, M m, bool w = true) => OpRM(w, new[] { (int)op * 8 + 3 }, (int)d, m);     // op d, [m]
        public void AluMR(Alu8 op, M m, R s, bool w = true) => OpRM(w, new[] { (int)op * 8 + 1 }, (int)s, m);     // op [m], s
        public void AluRI(Alu8 op, R d, int imm, bool w = true)
        {
            if (imm >= -128 && imm <= 127) { OpRR(w, new[] { 0x83 }, (int)op, (int)d); B(imm); }
            else { OpRR(w, new[] { 0x81 }, (int)op, (int)d); D(imm); }
        }
        public void AluMI(Alu8 op, M m, int imm, bool w = true)
        {
            if (imm >= -128 && imm <= 127) { OpRM(w, new[] { 0x83 }, (int)op, m); B(imm); }
            else { OpRM(w, new[] { 0x81 }, (int)op, m); D(imm); }
        }
        public void TestRI(R d, int imm, bool w = false) { OpRR(w, new[] { 0xF7 }, 0, (int)d); D(imm); }
        public void Not(R d, bool w = true) => OpRR(w, new[] { 0xF7 }, 2, (int)d);
        public void Inc(R d, bool w = false) => OpRR(w, new[] { 0xFF }, 0, (int)d);
        public void IncM(M m, bool w = false) => OpRM(w, new[] { 0xFF }, 0, m);
        public void ImulRR(R d, R s) => OpRR(true, s_Imul, (int)d, (int)s);
        public void DivR(R s, bool signed) => OpRR(true, new[] { 0xF7 }, signed ? 7 : 6, (int)s); // [speed_cat12] (i)div rdx:rax by s
        public void MulR(R s) => OpRR(true, new[] { 0xF7 }, 4, (int)s); // [speed_cat12] rdx:rax = rax * s (unsigned)
        public void Cqo() { B(0x48); B(0x99); }

        public enum Sh { Shl = 4, Shr = 5, Sar = 7 }
        public void ShiftI(Sh op, R d, int n, bool w = true) { if (n == 0) return; OpRR(w, new[] { 0xC1 }, (int)op, (int)d); B(n); }
        public void ShiftCl(Sh op, R d, bool w = true) => OpRR(w, new[] { 0xD3 }, (int)op, (int)d);

        public void SetCc(Cc c, R d) => OpRR(false, new[] { 0x0F, 0x90 + (int)c }, 0, (int)d, (int)d >= 4);

        // ---- SSE scalar (xmm0-xmm7 only) ----
        public enum Sse { Mov = 0x10, MovSt = 0x11, Add = 0x58, Mul = 0x59, Sub = 0x5C, Div = 0x5E }
        /// <summary>op{ss,sd} x, [m] (store form for MovSt).</summary>
        public void SseRM(bool dbl, Sse op, int x, M m) => OpRM(false, new[] { 0x0F, (int)op }, x, m, false, dbl ? 0xF2 : 0xF3);
        /// <summary>[fpu_native] prefix (-1 none) 0F op reg, [m]: cvt*/ucomis* forms (reg = xmm or GPR as the opcode needs).</summary>
        public void Sse2RR(int prefix, int op, int reg, int rm) { if (prefix >= 0) B(prefix); OpRR(false, new[] { 0x0F, op }, reg, rm); }
        // [climb4 speed_cat11] roundsd x, s, imm (SSE4.1: 66 0F 3A 0B /r ib); cvttsd2si r64, xmm (F2 REX.W 0F 2C)
        public void RoundsdRR(int x, int s, int imm) { B(0x66); OpRR(false, new[] { 0x0F, 0x3A, 0x0B }, x, s); B(imm); }
        public void Cvttsd2siRR64(int r, int x) { B(0xF2); OpRR(true, new[] { 0x0F, 0x2C }, r, x); }
        public void Sse2RM(int prefix, int op, int reg, M m, bool w = false) => OpRM(w, new[] { 0x0F, op }, reg, m, false, prefix);

        // ---- SSE integer (legacy encoding, xmm0-xmm7) ----
        public void MovdquRM(int x, M m) => OpRM(false, new[] { 0x0F, 0x6F }, x, m, false, 0xF3);
        public void MovdquMR(M m, int x) => OpRM(false, new[] { 0x0F, 0x7F }, x, m, false, 0xF3);
        public void PshufbRM(int x, M m) => OpRM(false, new[] { 0x0F, 0x38, 0x00 }, x, m, false, 0x66);

        // ---- VEX (3-byte form); map 1 = 0F, 2 = 0F38, 3 = 0F3A; pp 0 none, 1 = 66, 2 = F3, 3 = F2 ----
        private void VexHead(int L, int pp, int map, int w, int reg, int vvvv, int index, int rmBase)
        {
            B(0xC4);
            B((((reg >> 3) & 1) ^ 1) << 7 | (((index >> 3) & 1) ^ 1) << 6 | (((rmBase >> 3) & 1) ^ 1) << 5 | map);
            B(w << 7 | ((~vvvv) & 15) << 3 | L << 2 | pp);
        }
        public void VexRM(int L, int pp, int map, int w, int op, int reg, int vvvv, M m, int imm = -1)
        { VexHead(L, pp, map, w, reg, vvvv, m.Index < 0 ? 0 : m.Index, (int)m.Base); B(op); ModRmMem(reg, m); if (imm >= 0) B(imm); }
        public void VexRR(int L, int pp, int map, int w, int op, int reg, int vvvv, int rm, int imm = -1)
        { VexHead(L, pp, map, w, reg, vvvv, 0, rm); B(op); ModRmReg(reg, rm); if (imm >= 0) B(imm); }
        public void Vzeroupper() { B(0xC5); B(0xF8); B(0x77); }

        // ---- control flow ----
        public XLabel NewLabel() => new XLabel();
        public void Bind(XLabel l) { if (l.Pos >= 0) throw new InvalidOperationException("label bound twice"); l.Pos = m_Len; }
        public void Jmp(XLabel l) { B(0xE9); m_Rel32.Add((m_Len, l)); D(0); }
        public void Jcc(Cc c, XLabel l) { B(0x0F); B(0x80 + (int)c); m_Rel32.Add((m_Len, l)); D(0); }
        public void JmpM(M m) => OpRM(false, new[] { 0xFF }, 4, m);
        public void JmpR(R r) => OpRR(false, new[] { 0xFF }, 4, (int)r); // [block_link] jmp reg
        public void CallR(R r) => OpRR(false, new[] { 0xFF }, 2, (int)r);
        /// <summary>lea d, [rip + label]</summary>
        public void LeaRip(R d, XLabel l) { Rex(true, (int)d, 0, 0); B(0x8D); B(0x05 | ((int)d & 7) << 3); m_Rel32.Add((m_Len, l)); D(0); }
        public void Push(R r) { if ((int)r >= 8) B(0x41); B(0x50 + ((int)r & 7)); }
        public void Pop(R r) { if ((int)r >= 8) B(0x41); B(0x58 + ((int)r & 7)); }
        public void Ret() => B(0xC3);
        public void Int3() => B(0xCC);
        public void Align(int a) { while ((m_Len & (a - 1)) != 0) B(0xCC); }

        /// <summary>A table of absolute label addresses (8 bytes each), patched at Link().</summary>
        public void AbsTable(XLabel[] ls) { foreach (var l in ls) { m_Abs64.Add((m_Len, l)); Q(0); } }

        /// <summary>Resolves the branches and returns the final bytes for code placed at address <paramref name="at"/>.</summary>
        public byte[] Link(long at)
        {
            var r = new byte[m_Len];
            Array.Copy(m_Buf, r, m_Len);
            foreach (var (pos, l) in m_Rel32) {
                if (l.Pos < 0) throw new InvalidOperationException("unbound label");
                BitConverter.TryWriteBytes(new Span<byte>(r, pos, 4), l.Pos - (pos + 4));
            }
            foreach (var (pos, l) in m_Abs64) {
                if (l.Pos < 0) throw new InvalidOperationException("unbound label");
                BitConverter.TryWriteBytes(new Span<byte>(r, pos, 8), at + l.Pos);
            }
            return r;
        }
    }
}
