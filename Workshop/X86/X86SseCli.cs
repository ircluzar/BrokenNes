using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using NesEmulator.X86;

namespace BrokenNes.Workshop.X86;

/// <summary>
/// <c>--x86sse [--seeds N] [--parity] [--vectors N]</c>: the Pentium III (SSE) instructions of the x86 core.
/// 1) Hand-checked semantics: CPUID, CR4.OSFXSR / OSXMMEXCPT, the moves, shuffles, logic, CMPPS / COMISS / UCOMISS flags, the conversions
///    with every rounding mode, MIN / MAX NaN rules, RCP / RSQRT bounds, the MMX extensions, LDMXCSR / STMXCSR, FXSAVE / FXRSTOR, #XM / #UD /
///    #NM / #GP(alignment), #UD of SSE on the P2, the P3 save state round trip.
/// 2) Known results: ADD / SUB / MUL / DIV / SQRT PS + SS on random and edge values against .NET's IEEE single arithmetic (correctly rounded,
///    round to nearest), and the directed rounding modes against an exact rational reference (BigInteger), with the MXCSR flags.
/// 3) Random hot loops of SSE + MMX-extension forms (hash per seed); --parity = X86_NOJIT vs the JIT (X86_JITSYNC, X86_JITHOT=2), identical.
/// </summary>
public static class X86SseCli
{
    private sealed class Bus : IX86Bus
    {
        public int LastVec = -1;
        public byte In8(int port) => 0xff;
        public ushort In16(int port) => 0xffff;
        public uint In32(int port) => 0xffffffff;
        public void Out8(int port, byte v) { if (port == 0x80) LastVec = v; }
        public void Out16(int port, ushort v) => Out8(port, (byte)v);
        public void Out32(int port, uint v) => Out8(port, (byte)v);
        public bool MapsIoMem => false;
        public byte MemRead8(uint a) => 0;
        public void MemWrite8(uint a, byte v) { }
        public ushort MemRead16(uint a) => 0;
        public void MemWrite16(uint a, ushort v) { }
        public uint MemRead32(uint a) => 0;
        public void MemWrite32(uint a, uint v) { }
        public int AckIrq() => 8;
    }

    private static int _fails, _oks;
    private static bool _quiet;
    private static void Check(bool ok, string what)
    {
        if (!ok) { _fails++; Console.WriteLine("  FAIL " + what); }
        else { _oks++; if (!_quiet) Console.WriteLine("  ok   " + what); }
    }

    private static (I386 cpu, Bus bus) Make(CpuModel m)
    {
        var bus = new Bus();
        var cpu = new I386(m, 2 * 1024 * 1024, bus);
        for (int n = 0; n < 32; n++)
        {   // vector n -> 0000:0600 + n * 8: MOV AL, n; OUT 80h, AL; HLT
            uint stub = 0x600 + (uint)n * 8;
            cpu.Mem[n * 4] = (byte)stub; cpu.Mem[n * 4 + 1] = (byte)(stub >> 8);
            cpu.Mem[stub] = 0xb0; cpu.Mem[stub + 1] = (byte)n; cpu.Mem[stub + 2] = 0xe6; cpu.Mem[stub + 3] = 0x80; cpu.Mem[stub + 4] = 0xf4;
        }
        cpu.Reset();
        cpu.SetCsIp(0x1000, 0);
        foreach (int s in new[] { I386.SEG_DS, I386.SEG_ES, I386.SEG_SS }) cpu.LoadSeg(s, 0x1000);
        cpu.R[4] = 0xfff0;
        return (cpu, bus);
    }

    private static void RunReal(I386 cpu, byte[] code, int max = 20_000)
    {
        Array.Copy(code, 0, cpu.Mem, 0x10000, code.Length);
        cpu.Mem[0x10000 + code.Length] = 0xf4;
        for (int i = 0; i < max && !cpu.Halt; i++) cpu.Step(1);
    }

    private static byte[] Save(I386 cpu)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true)) cpu.SaveState(w);
        return ms.ToArray();
    }

    // ---- a tiny assembler (real mode, [disp16] operands) ----
    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    private static byte[] MovR32(int r, uint v) => new byte[] { 0x66, (byte)(0xb8 + r), (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) };
    /// <summary>MOV EAX, imm; MOV CR4, EAX.</summary>
    private static byte[] Cr4(uint v) => Cat(MovR32(0, v), new byte[] { 0x0f, 0x22, 0xe0 });
    private static readonly byte[] SseOn = Cr4(0x600);
    /// <summary>[prefix] 0F op modrm(reg, [disp16]) [imm].</summary>
    private static byte[] M(int op, int reg, ushort a, int prefix = 0, int imm = -1)
    {
        var l = new List<byte>();
        if (prefix != 0) l.Add((byte)prefix);
        l.AddRange(new byte[] { 0x0f, (byte)op, (byte)(reg << 3 | 6), (byte)a, (byte)(a >> 8) });
        if (imm >= 0) l.Add((byte)imm);
        return l.ToArray();
    }
    /// <summary>[prefix] 0F op modrm(reg, rm register) [imm].</summary>
    private static byte[] Rr(int op, int reg, int rm, int prefix = 0, int imm = -1)
    {
        var l = new List<byte>();
        if (prefix != 0) l.Add((byte)prefix);
        l.AddRange(new byte[] { 0x0f, (byte)op, (byte)(0xc0 | reg << 3 | rm) });
        if (imm >= 0) l.Add((byte)imm);
        return l.ToArray();
    }
    private static byte[] Ldmx(ushort a) => M(0xae, 2, a);
    private static byte[] Stmx(ushort a) => M(0xae, 3, a);

    private static void PutF(I386 c, uint a, params float[] f) { for (int i = 0; i < f.Length; i++) BitConverter.GetBytes(f[i]).CopyTo(c.Mem, 0x10000 + a + i * 4); }
    private static void PutU(I386 c, uint a, params uint[] f) { for (int i = 0; i < f.Length; i++) BitConverter.GetBytes(f[i]).CopyTo(c.Mem, 0x10000 + a + i * 4); }
    private static void PutQ(I386 c, uint a, ulong v) => BitConverter.GetBytes(v).CopyTo(c.Mem, 0x10000 + a);
    private static uint GetU(I386 c, uint a) => BitConverter.ToUInt32(c.Mem, (int)(0x10000 + a));
    private static ulong GetQ(I386 c, uint a) => BitConverter.ToUInt64(c.Mem, (int)(0x10000 + a));
    private static float GetF(I386 c, uint a) => BitConverter.ToSingle(c.Mem, (int)(0x10000 + a));
    private static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);
    private const uint Zf = 0x40, Cf = 0x1, Pf = 0x4;

    public static int Run(string[] args)
    {
        if (args.Contains("--child")) return Child(args);
        _fails = 0; _oks = 0;
        var p3 = CpuModel.PentiumIII;
        Console.WriteLine($"x86sse: {p3.Name}, {p3.Ips / 1e6:F0} M instr/s, CPUID sig {p3.Signature:x4} edx {p3.FeatureEdx:x8}");

        // ---- CPUID ----
        {
            var (c, b) = Make(p3);
            RunReal(c, Cat(MovR32(0, 1), new byte[] { 0x0f, 0xa2, 0x66, 0xa3, 0x00, 0x02, 0x66, 0x89, 0x16, 0x04, 0x02 },
                MovR32(0, 2), new byte[] { 0x0f, 0xa2, 0x66, 0x89, 0x16, 0x08, 0x02 }, MovR32(0, 0), new byte[] { 0x0f, 0xa2, 0x66, 0xa3, 0x0c, 0x02 }));
            Check(GetU(c, 0x200) == 0x686, $"CPUID 1: family 6 model 8 stepping 6 ({GetU(c, 0x200):x})");
            Check((GetU(c, 0x204) & 0x3000000) == 0x3000000 && (GetU(c, 0x204) & 0x800000) != 0 && (GetU(c, 0x204) & 0x40000) == 0, $"CPUID 1 EDX: FXSR + SSE + MMX, no PSN ({GetU(c, 0x204):x8})");
            Check(GetU(c, 0x208) == 0x0c040882, $"CPUID 2: Coppermine 256 KB L2 ({GetU(c, 0x208):x8})");
            Check(GetU(c, 0x20c) == 2 && b.LastVec == -1, $"CPUID 0: max leaf 2 ({GetU(c, 0x20c)})");
        }

        // ---- packed arithmetic, moves, shuffles ----
        {
            var (c, b) = Make(p3);
            PutF(c, 0x100, 1.5f, -2f, 1e30f, 3f); PutF(c, 0x110, 2.25f, 2f, 1e30f, 0.1f);
            RunReal(c, Cat(SseOn, M(0x28, 0, 0x100), M(0x28, 1, 0x110), Rr(0x58, 0, 1), M(0x29, 0, 0x200),        // ADDPS
                M(0x28, 2, 0x100), Rr(0x59, 2, 1), M(0x29, 2, 0x210),                                                // MULPS
                M(0x28, 3, 0x100), Rr(0x5e, 3, 1), M(0x29, 3, 0x220),                                                // DIVPS
                M(0x28, 4, 0x100), M(0x5c, 4, 0x110), M(0x11, 4, 0x230),                                             // SUBPS m128, MOVUPS store
                M(0x28, 5, 0x100), Rr(0x59, 5, 1, 0xf3), M(0x29, 5, 0x240),                                          // MULSS: upper lanes kept
                M(0x28, 6, 0x110), Rr(0x51, 6, 6), M(0x29, 6, 0x250)));                                              // SQRTPS
            bool Eq(uint a, params float[] f) => Enumerable.Range(0, 4).All(i => GetU(c, a + (uint)i * 4) == Bits(f[i]));
            Check(b.LastVec == -1, $"no exception (vector {b.LastVec})");
            Check(Eq(0x200, 1.5f + 2.25f, 0f, 2e30f, 3f + 0.1f), "ADDPS");
            Check(Eq(0x210, 1.5f * 2.25f, -4f, 1e30f * 1e30f, 3f * 0.1f), "MULPS (1e30 * 1e30 = inf, masked overflow)");
            Check(Eq(0x220, 1.5f / 2.25f, -1f, 1f, 3f / 0.1f), "DIVPS");
            Check(Eq(0x230, 1.5f - 2.25f, -4f, 0f, 3f - 0.1f), "SUBPS m128 + MOVUPS");
            Check(Eq(0x240, 1.5f * 2.25f, -2f, 1e30f, 3f), "MULSS keeps lanes 1-3");
            Check(Eq(0x250, MathF.Sqrt(2.25f), MathF.Sqrt(2f), MathF.Sqrt(1e30f), MathF.Sqrt(0.1f)), "SQRTPS");
            Check((c.Mxcsr & 0x3f) == 0x28, $"MXCSR flags OE + PE ({c.Mxcsr:x4})");
        }
        {
            var (c, b) = Make(p3);
            PutU(c, 0x100, 0x11111111, 0x22222222, 0x33333333, 0x44444444); PutU(c, 0x110, 0xaaaaaaaa, 0xbbbbbbbb, 0xcccccccc, 0x80000000);
            RunReal(c, Cat(SseOn, M(0x28, 0, 0x100), M(0x28, 1, 0x110),
                Rr(0x10, 2, 0), Rr(0xc6, 2, 1, 0, 0x1b), M(0x29, 2, 0x200),                 // SHUFPS imm 00 01 10 11 -> a3 a2 b1 b0
                Rr(0x10, 3, 0), Rr(0x14, 3, 1), M(0x29, 3, 0x210),                          // UNPCKLPS
                Rr(0x10, 4, 0), Rr(0x15, 4, 1), M(0x29, 4, 0x220),                          // UNPCKHPS
                Rr(0x10, 5, 0), Rr(0x12, 5, 1), M(0x29, 5, 0x230),                          // MOVHLPS
                Rr(0x10, 6, 0), Rr(0x16, 6, 1), M(0x29, 6, 0x240),                          // MOVLHPS
                Rr(0x10, 7, 0), M(0x16, 7, 0x118), M(0x12, 7, 0x110), M(0x17, 7, 0x250), M(0x13, 7, 0x258),   // MOVHPS / MOVLPS load + store
                Rr(0x50, 0, 1), new byte[] { 0x66, 0xa3, 0x60, 0x02 },                       // MOVMSKPS EAX, xmm1
                Rr(0x10, 2, 0), Rr(0x57, 2, 1), M(0x29, 2, 0x270),                          // XORPS
                Rr(0x10, 3, 0), Rr(0x55, 3, 1), M(0x29, 3, 0x280),                          // ANDNPS
                M(0x10, 4, 0x110, 0xf3), M(0x29, 4, 0x290),                                 // MOVSS m32: upper lanes zeroed
                Rr(0x10, 5, 0), Rr(0x10, 5, 1, 0xf3), M(0x29, 5, 0x2a0), M(0x11, 1, 0x2b0, 0xf3)));   // MOVSS reg: low lane only, MOVSS store
            bool Eq(uint a, params uint[] f) => Enumerable.Range(0, f.Length).All(i => GetU(c, a + (uint)i * 4) == f[i]);
            Check(b.LastVec == -1, $"no exception (vector {b.LastVec})");
            Check(Eq(0x200, 0x44444444, 0x33333333, 0xbbbbbbbb, 0xaaaaaaaa), "SHUFPS");
            Check(Eq(0x210, 0x11111111, 0xaaaaaaaa, 0x22222222, 0xbbbbbbbb), "UNPCKLPS");
            Check(Eq(0x220, 0x33333333, 0xcccccccc, 0x44444444, 0x80000000), "UNPCKHPS");
            Check(Eq(0x230, 0xcccccccc, 0x80000000, 0x33333333, 0x44444444), "MOVHLPS");
            Check(Eq(0x240, 0x11111111, 0x22222222, 0xaaaaaaaa, 0xbbbbbbbb), "MOVLHPS");
            Check(Eq(0x250, 0xcccccccc, 0x80000000, 0xaaaaaaaa, 0xbbbbbbbb), "MOVHPS / MOVLPS load + store");
            Check(GetU(c, 0x260) == 0xf, $"MOVMSKPS ({GetU(c, 0x260):x})");
            Check(Eq(0x270, 0xbbbbbbbb, 0x99999999, 0xffffffff, 0xc4444444), "XORPS");
            Check(Eq(0x280, 0xaaaaaaaa, 0x99999999, 0xcccccccc, 0x80000000), "ANDNPS");
            Check(Eq(0x290, 0xaaaaaaaa, 0, 0, 0), "MOVSS m32 zeroes lanes 1-3");
            Check(Eq(0x2a0, 0xaaaaaaaa, 0x22222222, 0x33333333, 0x44444444) && GetU(c, 0x2b0) == 0xaaaaaaaa, "MOVSS reg keeps lanes 1-3, MOVSS store");
        }

        // ---- compares ----
        {
            var (c, b) = Make(p3);
            PutF(c, 0x100, 1f, 2f, float.NaN, -0f); PutF(c, 0x110, 2f, 2f, 1f, 0f);
            var code = new List<byte>(Cat(SseOn, M(0x28, 1, 0x110)));
            for (int p = 0; p < 8; p++) code.AddRange(Cat(M(0x28, 0, 0x100), Rr(0xc2, 0, 1, 0, p), M(0x29, 0, (ushort)(0x200 + p * 16))));
            code.AddRange(Cat(Stmx(0x300), Ldmx(0x304)));
            // COMISS 1 vs 2 (CF), 2 vs 2 (ZF), NaN (ZF PF CF, IE), UCOMISS QNaN (no IE)
            PutU(c, 0x304, 0x1f80);
            code.AddRange(Cat(M(0x10, 0, 0x100, 0xf3), M(0x2f, 0, 0x110), new byte[] { 0x9c, 0x8f, 0x06, 0x10, 0x03 }));
            code.AddRange(Cat(M(0x10, 0, 0x104, 0xf3), M(0x2f, 0, 0x114), new byte[] { 0x9c, 0x8f, 0x06, 0x12, 0x03 }));
            code.AddRange(Cat(M(0x10, 0, 0x108, 0xf3), M(0x2e, 0, 0x118), new byte[] { 0x9c, 0x8f, 0x06, 0x14, 0x03 }, Stmx(0x318)));
            code.AddRange(Cat(M(0x10, 0, 0x108, 0xf3), M(0x2f, 0, 0x118), new byte[] { 0x9c, 0x8f, 0x06, 0x16, 0x03 }, Stmx(0x31c)));
            RunReal(c, code.ToArray());
            uint T = 0xffffffff;
            uint[][] want =
            {
                new[] { 0u, T, 0u, T },   // EQ   (-0 == 0)
                new[] { T, 0u, 0u, 0u },  // LT
                new[] { T, T, 0u, T },    // LE
                new[] { 0u, 0u, T, 0u },  // UNORD
                new[] { T, 0u, T, 0u },   // NEQ
                new[] { 0u, T, T, T },    // NLT
                new[] { 0u, 0u, T, 0u },  // NLE
                new[] { T, T, 0u, T },    // ORD
            };
            string[] names = { "EQ", "LT", "LE", "UNORD", "NEQ", "NLT", "NLE", "ORD" };
            for (int p = 0; p < 8; p++)
                Check(Enumerable.Range(0, 4).All(i => GetU(c, (uint)(0x200 + p * 16 + i * 4)) == want[p][i]), $"CMPPS {names[p]}");
            Check((GetU(c, 0x300) & 1) != 0, $"CMPPS LT on a QNaN raises IE ({GetU(c, 0x300):x4})");
            uint f1 = BitConverter.ToUInt16(c.Mem, 0x10310), f2 = BitConverter.ToUInt16(c.Mem, 0x10312), f3 = BitConverter.ToUInt16(c.Mem, 0x10314), f4 = BitConverter.ToUInt16(c.Mem, 0x10316);
            Check((f1 & (Zf | Pf | Cf)) == Cf && (f2 & (Zf | Pf | Cf)) == Zf, $"COMISS less: CF, equal: ZF ({f1:x4} {f2:x4})");
            Check((f3 & (Zf | Pf | Cf)) == (Zf | Pf | Cf) && (GetU(c, 0x318) & 1) == 0, $"UCOMISS QNaN: unordered, no IE ({f3:x4}, mxcsr {GetU(c, 0x318):x4})");
            Check((f4 & (Zf | Pf | Cf)) == (Zf | Pf | Cf) && (GetU(c, 0x31c) & 1) == 1, $"COMISS QNaN: unordered + IE ({f4:x4}, mxcsr {GetU(c, 0x31c):x4})");
            Check(b.LastVec == -1, $"no exception (vector {b.LastVec})");
        }

        // ---- MIN / MAX, NaN propagation, RCP / RSQRT ----
        {
            var (c, b) = Make(p3);
            PutF(c, 0x100, 1f, float.NaN, 0f, -5f); PutF(c, 0x110, 2f, 3f, -0f, float.NaN);
            uint snan = 0x7f800001;
            PutU(c, 0x120, snan, 0x7fc00005, 1, 0x3f800000); PutU(c, 0x130, 0x7fc00007, snan, 0x40000000, 0x7f800000);
            PutF(c, 0x140, 4f, 0.25f, -0f, float.PositiveInfinity); PutF(c, 0x150, 3f, 100f, 1e-3f, 7f);
            RunReal(c, Cat(SseOn, M(0x28, 0, 0x100), M(0x5d, 0, 0x110), M(0x29, 0, 0x200),   // MINPS
                M(0x28, 0, 0x100), M(0x5f, 0, 0x110), M(0x29, 0, 0x210),                       // MAXPS
                M(0x28, 0, 0x120), M(0x58, 0, 0x130), M(0x29, 0, 0x220),                       // ADDPS NaN rules (+ a denormal)
                M(0x52, 1, 0x140), M(0x29, 1, 0x230), M(0x53, 2, 0x150), M(0x29, 2, 0x240), Stmx(0x250)));
            Check(GetU(c, 0x200) == Bits(1f) && GetU(c, 0x204) == Bits(3f) && GetU(c, 0x208) == 0x80000000 && float.IsNaN(GetF(c, 0x20c)), "MINPS: a NaN or two zeros -> the source");
            Check(GetU(c, 0x210) == Bits(2f) && GetU(c, 0x214) == Bits(3f) && GetU(c, 0x218) == 0x80000000 && float.IsNaN(GetF(c, 0x21c)), "MAXPS: a NaN or two zeros -> the source");
            Check(GetU(c, 0x220) == 0x7fc00001 && GetU(c, 0x224) == 0x7fc00005 && GetU(c, 0x228) == Bits(2f + 1.4e-45f) && GetU(c, 0x22c) == 0x7f800000,
                $"ADDPS NaN: the first operand's, quieted ({GetU(c, 0x220):x8} {GetU(c, 0x224):x8} {GetU(c, 0x228):x8})");
            bool Close(float got, double want) => Math.Abs(got - want) <= Math.Abs(want) * 1.5 / 4096;
            Check(Close(GetF(c, 0x230), 0.5) && Close(GetF(c, 0x234), 2) && GetU(c, 0x238) == 0xff800000 && GetU(c, 0x23c) == 0, "RSQRTPS (12-bit, -0 -> -inf, inf -> 0)");
            Check(Close(GetF(c, 0x240), 1 / 3.0) && Close(GetF(c, 0x244), 0.01) && Close(GetF(c, 0x248), 1000) && Close(GetF(c, 0x24c), 1 / 7.0), "RCPPS within 1.5 x 2^-12");
            Check((GetU(c, 0x250) & 3) == 3 && b.LastVec == -1, $"MXCSR: IE (any NaN in MIN/MAX, SNaN) + DE (denormal) ({GetU(c, 0x250):x4})");
        }

        // ---- conversions and the rounding modes ----
        {
            var (c, b) = Make(p3);
            PutF(c, 0x100, 2.5f, -2.7f, 3.5f, 3e9f); PutU(c, 0x110, 16777217, 0xffffffff);
            var code = new List<byte>(SseOn);
            int o = 0x200;
            foreach (uint rc in new uint[] { 0, 1, 2, 3 })
            {
                PutU(c, (uint)(0x180 + rc * 4), 0x1f80 | rc << 13);
                code.AddRange(Ldmx((ushort)(0x180 + rc * 4)));
                foreach (ushort src in new ushort[] { 0x100, 0x104, 0x108 })
                {
                    code.AddRange(Cat(M(0x2d, 0, src, 0xf3), new byte[] { 0x66, 0xa3, (byte)o, (byte)(o >> 8) })); o += 4;   // CVTSS2SI EAX, m32
                }
            }
            code.AddRange(Cat(Ldmx(0x180), M(0x2c, 0, 0x104, 0xf3), new byte[] { 0x66, 0xa3, 0x40, 0x02 }));   // CVTTSS2SI -2.7
            code.AddRange(Cat(M(0x2d, 0, 0x10c, 0xf3), new byte[] { 0x66, 0xa3, 0x44, 0x02 }, Stmx(0x248)));   // 3e9: IE, indefinite
            code.AddRange(Cat(Ldmx(0x180), M(0x2a, 1, 0x110, 0xf3), M(0x11, 1, 0x250, 0xf3), Stmx(0x254)));   // CVTSI2SS 16777217 -> 16777216, PE
            code.AddRange(Cat(M(0x6f, 0, 0x110) /* MOVQ mm0 */, Rr(0x2a, 2, 0), M(0x29, 2, 0x260)));            // CVTPI2PS xmm2, mm0
            code.AddRange(Cat(M(0x28, 3, 0x100), Rr(0x2d, 1, 3), M(0x7f, 1, 0x270), Rr(0x2c, 2, 3), M(0x7f, 2, 0x278)));   // CVTPS2PI / CVTTPS2PI
            code.AddRange(new byte[] { 0x0f, 0x77 });
            RunReal(c, code.ToArray());
            int[] got = Enumerable.Range(0, 12).Select(i => (int)GetU(c, (uint)(0x200 + i * 4))).ToArray();
            Check(got.SequenceEqual(new[] { 2, -3, 4, 2, -3, 3, 3, -2, 4, 2, -2, 3 }), $"CVTSS2SI nearest/down/up/trunc ({string.Join(",", got)})");
            Check((int)GetU(c, 0x240) == -2, "CVTTSS2SI truncates");
            Check(GetU(c, 0x244) == 0x80000000 && (GetU(c, 0x248) & 1) == 1, "CVTSS2SI out of range: 80000000h + IE");
            Check(GetF(c, 0x250) == 16777216f && (GetU(c, 0x254) & 0x20) != 0, "CVTSI2SS 16777217 -> 16777216, PE");
            Check(GetF(c, 0x260) == 16777216f && GetF(c, 0x264) == -1f, "CVTPI2PS");
            Check(GetQ(c, 0x270) == ((ulong)unchecked((uint)-3) << 32 | 2) && GetQ(c, 0x278) == ((ulong)unchecked((uint)-2) << 32 | 2), $"CVTPS2PI / CVTTPS2PI ({GetQ(c, 0x270):x16} {GetQ(c, 0x278):x16})");
            Check(b.LastVec == -1, $"no exception (vector {b.LastVec})");
        }

        // ---- known results: random ADD / SUB / MUL / DIV / SQRT against .NET's IEEE single (nearest) ----
        int nvec = int.TryParse(Arg(args, "--vectors"), out int nv) ? nv : 4000;
        {
            var rng = new Random(77);
            int bad = 0, n = 0;
            string firstBad = "";
            float R()
            {
                return rng.Next(10) switch
                {
                    0 => BitConverter.UInt32BitsToSingle((uint)rng.Next(1, 0x800000) | (rng.Next(2) == 0 ? 0 : 0x80000000u)),   // denormals
                    1 => rng.Next(2) == 0 ? 0f : -0f,
                    2 => rng.Next(2) == 0 ? float.PositiveInfinity : float.NegativeInfinity,
                    3 => BitConverter.UInt32BitsToSingle((uint)rng.NextInt64(0x100000000)),
                    4 => (float)(rng.NextDouble() * 1e38) * (rng.Next(2) == 0 ? 1 : -1),
                    5 => (float)(rng.NextDouble() * 1e-37),
                    _ => (float)((rng.NextDouble() - 0.5) * Math.Pow(2, rng.Next(-30, 30))),
                };
            }
            for (int blk = 0; blk < nvec / 4; blk++)
            {
                var (c, b) = Make(p3);
                float[] a = new float[4], d = new float[4];
                for (int i = 0; i < 4; i++) { a[i] = R(); d[i] = R(); }
                PutF(c, 0x100, a); PutF(c, 0x110, d);
                var code = new List<byte>(SseOn);
                int[] ops = { 0x58, 0x5c, 0x59, 0x5e, 0x51 };
                for (int k = 0; k < ops.Length; k++) code.AddRange(Cat(M(0x28, 0, 0x100), M(ops[k], 0, 0x110), M(0x29, 0, (ushort)(0x200 + k * 16))));
                RunReal(c, code.ToArray());
                for (int k = 0; k < ops.Length; k++)
                    for (int i = 0; i < 4; i++)
                    {
                        float want = ops[k] switch { 0x58 => a[i] + d[i], 0x5c => a[i] - d[i], 0x59 => a[i] * d[i], 0x5e => a[i] / d[i], _ => MathF.Sqrt(d[i]) };
                        uint g = GetU(c, (uint)(0x200 + k * 16 + i * 4)), w = Bits(want);
                        bool ok = g == w || (float.IsNaN(want) && float.IsNaN(BitConverter.UInt32BitsToSingle(g)));   // (NaN payloads: checked by hand above)
                        n++;
                        if (!ok) { if (bad++ == 0) firstBad = $"op {ops[k]:x2} {a[i]:R} {d[i]:R}: got {g:x8} want {w:x8}"; }
                    }
                if (b.LastVec != -1) { bad++; firstBad = $"vector {b.LastVec}"; }
            }
            Check(bad == 0, $"{n} packed results identical to IEEE single (round to nearest) {(bad > 0 ? $"({bad} bad; first {firstBad})" : "")}");
        }
        // ---- the directed modes against an exact reference ----
        {
            var rng = new Random(91);
            int bad = 0, n = 0; string firstBad = "";
            for (int blk = 0; blk < nvec / 16; blk++)
            {
                uint rc = (uint)rng.Next(4);
                var (c, b) = Make(p3);
                float[] a = new float[4], d = new float[4];
                for (int i = 0; i < 4; i++)
                {
                    a[i] = (float)((rng.NextDouble() - 0.5) * Math.Pow(2, rng.Next(-20, 20)));
                    d[i] = rng.Next(5) == 0 ? BitConverter.UInt32BitsToSingle((uint)rng.Next(1, 0x800000)) : (float)((rng.NextDouble() - 0.5) * Math.Pow(2, rng.Next(-20, 20)));
                }
                PutF(c, 0x100, a); PutF(c, 0x110, d); PutU(c, 0x180, 0x1f80 | rc << 13);
                var code = new List<byte>(Cat(SseOn, Ldmx(0x180)));
                int[] ops = { 0x58, 0x5c, 0x59, 0x5e, 0x51 };
                for (int k = 0; k < ops.Length; k++) code.AddRange(Cat(M(0x28, 0, 0x100), M(ops[k], 0, 0x110), M(0x29, 0, (ushort)(0x200 + k * 16))));
                RunReal(c, code.ToArray());
                for (int k = 0; k < ops.Length; k++)
                    for (int i = 0; i < 4; i++)
                    {
                        if (ops[k] == 0x51 && d[i] < 0) continue;
                        float want = ExactRef.Round(ops[k], a[i], d[i], (int)rc);
                        uint g = GetU(c, (uint)(0x200 + k * 16 + i * 4));
                        n++;
                        if (g != Bits(want) && bad++ == 0) firstBad = $"rc {rc} op {ops[k]:x2} {a[i]:R} {d[i]:R}: got {g:x8} want {Bits(want):x8}";
                    }
            }
            Check(bad == 0, $"{n} results in the directed rounding modes identical to the exact reference {(bad > 0 ? $"({bad} bad; first {firstBad})" : "")}");
        }

        // ---- the MMX extensions ----
        {
            var (c, b) = Make(p3);
            PutQ(c, 0x100, 0x8070_6050_4030_2010UL); PutQ(c, 0x108, 0x0180_ff60_0590_2201UL);
            PutQ(c, 0x110, 0xfffe_0003_8000_7fffUL); PutQ(c, 0x118, 0x0001_fffd_7fff_8000UL);
            PutQ(c, 0x140, 0x1111_2222_3333_4444UL); PutQ(c, 0x148, 0x00ff_0000_ff00_80ffUL);
            byte[] Mm(int op, int reg, ushort a, int imm = -1) => M(op, reg, a, 0, imm);
            RunReal(c, Cat(
                Mm(0x6f, 0, 0x100), Mm(0xda, 0, 0x108), Mm(0x7f, 0, 0x200),     // PMINUB
                Mm(0x6f, 0, 0x100), Mm(0xde, 0, 0x108), Mm(0x7f, 0, 0x208),     // PMAXUB
                Mm(0x6f, 0, 0x100), Mm(0xe0, 0, 0x108), Mm(0x7f, 0, 0x210),     // PAVGB
                Mm(0x6f, 0, 0x100), Mm(0xf6, 0, 0x108), Mm(0x7f, 0, 0x218),     // PSADBW
                Mm(0x6f, 0, 0x110), Mm(0xea, 0, 0x118), Mm(0x7f, 0, 0x220),     // PMINSW
                Mm(0x6f, 0, 0x110), Mm(0xee, 0, 0x118), Mm(0x7f, 0, 0x228),     // PMAXSW
                Mm(0x6f, 0, 0x110), Mm(0xe3, 0, 0x118), Mm(0x7f, 0, 0x230),     // PAVGW
                Mm(0x6f, 0, 0x110), Mm(0xe4, 0, 0x118), Mm(0x7f, 0, 0x238),     // PMULHUW
                Mm(0x70, 1, 0x140, 0x1b), Mm(0x7f, 1, 0x240),                   // PSHUFW reverse
                Mm(0x6f, 2, 0x140), MovR32(0, 0xbeef), Rr(0xc4, 2, 0, 0, 2), Mm(0x7f, 2, 0x248),   // PINSRW word 2
                Rr(0xc5, 3, 2, 0, 3), new byte[] { 0x66, 0x89, 0x1e, 0x50, 0x02 },                // PEXTRW EBX, mm2, 3
                Mm(0x6f, 4, 0x100), Rr(0xd7, 1, 4), new byte[] { 0x66, 0x89, 0x0e, 0x54, 0x02 },   // PMOVMSKB ECX, mm4
                MovR32(7, 0x300), Mm(0x6f, 5, 0x140), Mm(0x6f, 6, 0x148), Rr(0xf7, 5, 6),          // MASKMOVQ mm5, mm6 -> DS:[EDI]
                Mm(0xe7, 5, 0x310),                                                                 // MOVNTQ
                new byte[] { 0x0f, 0x77 }));
            Check(b.LastVec == -1, $"no exception (vector {b.LastVec})");
            Check(GetQ(c, 0x200) == 0x0170_6050_0530_2001UL, $"PMINUB ({GetQ(c, 0x200):x16})");
            Check(GetQ(c, 0x208) == 0x8080_ff60_4090_2210UL, $"PMAXUB ({GetQ(c, 0x208):x16})");
            Check(GetQ(c, 0x210) == 0x4178_b058_2360_2109UL, $"PAVGB ({GetQ(c, 0x210):x16})");
            Check(GetQ(c, 0x218) == (ulong)(0x7f + 0x10 + 0x9f + 0x10 + 0x3b + 0x60 + 0x02 + 0x0f), $"PSADBW ({GetQ(c, 0x218):x})");
            Check(GetQ(c, 0x220) == 0xfffe_fffd_8000_8000UL, $"PMINSW ({GetQ(c, 0x220):x16})");
            Check(GetQ(c, 0x228) == 0x0001_0003_7fff_7fffUL, $"PMAXSW ({GetQ(c, 0x228):x16})");
            Check(GetQ(c, 0x230) == 0x8000_8000_8000_8000UL, $"PAVGW ({GetQ(c, 0x230):x16})");
            Check(GetQ(c, 0x238) == 0x0000_0002_3fff_3fffUL, $"PMULHUW ({GetQ(c, 0x238):x16})");
            Check(GetQ(c, 0x240) == 0x4444_3333_2222_1111UL, $"PSHUFW ({GetQ(c, 0x240):x16})");
            Check(GetQ(c, 0x248) == 0x1111_beef_3333_4444UL, $"PINSRW ({GetQ(c, 0x248):x16})");
            Check(GetU(c, 0x250) == 0x1111, $"PEXTRW ({GetU(c, 0x250):x})");
            Check(GetU(c, 0x254) == 0x80, $"PMOVMSKB ({GetU(c, 0x254):x})");
            Check(GetQ(c, 0x300) == 0x0011_0000_3300_4444UL, $"MASKMOVQ: only the masked bytes ({GetQ(c, 0x300):x16})");
            Check(GetQ(c, 0x310) == 0x1111_2222_3333_4444UL, "MOVNTQ");
        }

        // ---- LDMXCSR / STMXCSR / exceptions ----
        {
            var (c, b) = Make(p3);
            PutU(c, 0x100, 0x7f80 | 0x6000);
            RunReal(c, Cat(SseOn, Ldmx(0x100), Stmx(0x104)));
            Check(GetU(c, 0x104) == 0x7f80 && b.LastVec == -1, $"LDMXCSR / STMXCSR round trip ({GetU(c, 0x104):x4})");
            var (c2, b2) = Make(p3); PutU(c2, 0x100, 0x1fc0);   // DAZ: reserved on the P3
            RunReal(c2, Cat(SseOn, Ldmx(0x100)));
            Check(b2.LastVec == 13, $"LDMXCSR with DAZ (reserved on the P3): #GP (vector {b2.LastVec})");
            var (c3, b3) = Make(p3); PutF(c3, 0x100, 1, 1, 1, 1); PutF(c3, 0x110, 1, 0, 1, 1); PutU(c3, 0x120, 0x1f80 & ~(4u << 7)); PutF(c3, 0x130, 9, 9, 9, 9);
            RunReal(c3, Cat(SseOn, Ldmx(0x120), M(0x28, 0, 0x130), M(0x28, 1, 0x100), M(0x5e, 1, 0x110), M(0x29, 1, 0x200)));
            Check(b3.LastVec == 19 && (c3.Mxcsr & 4) != 0 && c3.Xmm(1, 0) == Bits(1f), $"DIVPS by 0 with ZE unmasked: #XM, ZE set, destination kept (vector {b3.LastVec}, mxcsr {c3.Mxcsr:x4})");
            var (c4, b4) = Make(p3); PutF(c4, 0x100, 1, 1, 1, 1); PutF(c4, 0x110, 1, 0, 1, 1); PutU(c4, 0x120, 0x1f80 & ~(4u << 7));
            RunReal(c4, Cat(Cr4(0x200), Ldmx(0x120), M(0x28, 1, 0x100), M(0x5e, 1, 0x110)));
            Check(b4.LastVec == 6, $"... with CR4.OSXMMEXCPT clear: #UD (vector {b4.LastVec})");
            var (c5, b5) = Make(p3); RunReal(c5, Rr(0x58, 0, 1));
            Check(b5.LastVec == 6, $"ADDPS with CR4.OSFXSR clear: #UD (vector {b5.LastVec})");
            var (c5m, b5m) = Make(p3); RunReal(c5m, Cat(Rr(0xda, 0, 1), new byte[] { 0x0f, 0x77 }));
            Check(b5m.LastVec == -1, $"PMINUB needs no OSFXSR (MMX rules) (vector {b5m.LastVec})");
            var (c6, b6) = Make(p3); RunReal(c6, Cat(SseOn, new byte[] { 0x0f, 0x20, 0xc0, 0x0c, 0x08, 0x0f, 0x22, 0xc0 }, Rr(0x58, 0, 1)));   // CR0.TS
            Check(b6.LastVec == 7, $"ADDPS with CR0.TS: #NM (vector {b6.LastVec})");
            var (c7, b7) = Make(p3); RunReal(c7, Cat(SseOn, M(0x28, 0, 0x108)));
            Check(b7.LastVec == 13, $"MOVAPS misaligned: #GP (vector {b7.LastVec})");
            var (c8, b8) = Make(p3); RunReal(c8, Cat(SseOn, M(0x10, 0, 0x108), M(0x58, 0, 0x104, 0xf3)));
            Check(b8.LastVec == -1, $"MOVUPS / ADDSS misaligned: fine (vector {b8.LastVec})");
            var (c9, b9) = Make(p3); RunReal(c9, Cat(SseOn, Rr(0x58, 0, 1, 0x66)));
            Check(b9.LastVec == 6, $"66 0F 58 (SSE2 ADDPD): #UD on the P3 (vector {b9.LastVec})");
            var (ca, ba) = Make(p3); RunReal(ca, Cat(SseOn, new byte[] { 0x0f, 0xae, 0xf8, 0x0f, 0x18, 0x06, 0x00, 0x01, 0x0f, 0x18, 0x0e, 0x00, 0x01 }));
            Check(ba.LastVec == -1 && ca.Halt, $"SFENCE, PREFETCHNTA / PREFETCHT0 (vector {ba.LastVec})");
        }

        // ---- FXSAVE / FXRSTOR ----
        {
            var (c, b) = Make(p3);
            PutU(c, 0x100, 0x11111111, 0x22222222, 0x33333333, 0x44444444); PutU(c, 0x110, 0x2f80);
            RunReal(c, Cat(SseOn, M(0x28, 7, 0x100), Ldmx(0x110), new byte[] { 0xdb, 0xe3, 0xd9, 0xe8, 0xd9, 0xeb }, M(0xae, 0, 0x400),   // FNINIT FLD1 FLDPI FXSAVE
                Rr(0x57, 7, 7), new byte[] { 0xdb, 0xe3 }, M(0xae, 1, 0x400), M(0x29, 7, 0x200), Stmx(0x210),
                new byte[] { 0xdd, 0x1e, 0x20, 0x02, 0xdd, 0x1e, 0x28, 0x02, 0xdf, 0xe0, 0xa3, 0x30, 0x02 }));   // FSTP x2, FNSTSW AX
            Check(b.LastVec == -1, $"no exception (vector {b.LastVec})");
            Check(BitConverter.ToUInt16(c.Mem, 0x10400) == 0x37f && c.Mem[0x10404] == 0xc0 && GetU(c, 0x418) == 0x2f80, $"FXSAVE image: FCW, abridged tag C0h (2 registers), MXCSR ({c.Mem[0x10404]:x2} {GetU(c, 0x418):x4})");
            Check(GetU(c, 0x400 + 160 + 7 * 16) == 0x11111111 && GetU(c, 0x400 + 160 + 7 * 16 + 12) == 0x44444444, "FXSAVE image: XMM7");
            Check(GetU(c, 0x200) == 0x11111111 && GetU(c, 0x20c) == 0x44444444 && GetU(c, 0x210) == 0x2f80, "FXRSTOR: XMM7 + MXCSR back");
            Check(BitConverter.ToDouble(c.Mem, 0x10220) == Math.PI && BitConverter.ToDouble(c.Mem, 0x10228) == 1.0 && ((BitConverter.ToUInt16(c.Mem, 0x10230) >> 11) & 7) == 0,
                "FXRSTOR: the x87 stack back (pi, 1), TOP");
            var (c2, b2) = Make(p3); RunReal(c2, Cat(SseOn, M(0xae, 0, 0x408)));
            Check(b2.LastVec == 13, $"FXSAVE misaligned: #GP (vector {b2.LastVec})");
            // a P3 state round trip carries the SSE state
            byte[] a = Save(c);
            var (c3, _) = Make(p3);
            using (var r = new BinaryReader(new MemoryStream(a))) c3.LoadState(r);
            Check(a.AsSpan().SequenceEqual(Save(c3)) && c3.Xmm(7, 3) == 0x44444444 && c3.Mxcsr == 0x2f80, "P3 save state round trip identical (XMM, MXCSR)");
            var (c4, _) = Make(CpuModel.PentiumII);
            bool refused = false;
            try { using var r = new BinaryReader(new MemoryStream(a)); c4.LoadState(r); } catch (InvalidDataException) { refused = true; }
            Check(refused, "a P3 state is refused by a P2 machine");
            var (c5, _) = Make(CpuModel.PentiumII);
            byte[] p2s = Save(c5);
            var (c6, _) = Make(CpuModel.PentiumII);
            using (var r = new BinaryReader(new MemoryStream(p2s))) c6.LoadState(r);
            Check(p2s.AsSpan().SequenceEqual(Save(c6)), "P2 states keep their layout (no SSE block)");
        }

        // ---- SSE on the P2: #UD ----
        foreach (var (name, code) in new (string, byte[])[] { ("ADDPS", Rr(0x58, 0, 1)), ("MOVAPS", M(0x28, 0, 0x100)), ("FXSAVE", M(0xae, 0, 0x400)),
            ("LDMXCSR", Ldmx(0x100)), ("PMINUB", Rr(0xda, 0, 1)), ("PSHUFW", Rr(0x70, 0, 1, 0, 0)), ("SHUFPS", Rr(0xc6, 0, 1, 0, 0)) })
        {
            var (c, b) = Make(CpuModel.PentiumII);
            RunReal(c, code);
            Check(b.LastVec == 6, $"{name} on the P2: #UD (vector {b.LastVec})");
        }
        {
            var (c, b) = Make(CpuModel.PentiumII);
            RunReal(c, Cr4(0x200));
            Check(b.LastVec == 13, $"CR4.OSFXSR on the P2: #GP (vector {b.LastVec})");
        }

        // ---- random hot loops: hashes (and the JIT / interpreter parity) ----
        int seeds = int.TryParse(Arg(args, "--seeds"), out int sn) ? sn : 40;
        if (args.Contains("--parity"))
        {
            var interp = RunChild(seeds, ("X86_NOJIT", "1"));
            var jit = RunChild(seeds, ("X86_JITSYNC", "1"), ("X86_JITHOT", "2"), ("X86_JITSTAT", "1"));
            int same = 0;
            for (int i = 0; i < seeds; i++)
            {
                string a = interp.Hashes.ElementAtOrDefault(i) ?? "?", j = jit.Hashes.ElementAtOrDefault(i) ?? "?";
                if (a == j && a != "?") same++; else Console.WriteLine($"  FAIL seed {i}: interpreter {a} JIT {j}");
            }
            Check(same == seeds, $"JIT / interpreter parity: {same} / {seeds} seeds identical (state + clock + memory)");
            Check(jit.JitSteps > 0 && interp.JitSteps == 0, $"the JIT run ran compiled code ({jit.JitSteps:N0} compiled block runs; interpreter run {interp.JitSteps:N0})");
        }
        else foreach (var (h, _) in Enumerable.Range(0, seeds).Select(RandomCase)) Console.WriteLine($"ssehash {h}");

        Console.WriteLine(_fails == 0 ? $"x86sse: all {_oks} passed" : $"x86sse: {_fails} FAILED ({_oks} ok)");
        return _fails == 0 ? 0 : 1;
    }

    private static int Child(string[] args)
    {
        int seeds = int.Parse(Arg(args, "--seeds") ?? "40");
        long jit = 0;
        for (int i = 0; i < seeds; i++) { var (h, j) = RandomCase(i); jit += j; Console.WriteLine($"ssehash {h}"); }
        Console.WriteLine($"ssejit {jit}");
        return 0;
    }

    private static (List<string> Hashes, long JitSteps) RunChild(int seeds, params (string k, string v)[] env)
    {
        string exe = Environment.ProcessPath!;
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) psi.ArgumentList.Add(typeof(X86SseCli).Assembly.Location);
        foreach (var a in new[] { "--x86sse", "--child", "--seeds", seeds.ToString() }) psi.ArgumentList.Add(a);
        psi.Environment.Remove("X86_NOJIT"); psi.Environment.Remove("X86_JITSYNC");
        foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var lines = p.StandardOutput.ReadToEnd().Split('\n').Select(l => l.Trim()).ToList();
        p.WaitForExit();
        return (lines.Where(l => l.StartsWith("ssehash ")).Select(l => l[8..]).ToList(),
                lines.Where(l => l.StartsWith("ssejit ")).Select(l => long.Parse(l[7..])).FirstOrDefault());
    }

    /// <summary>One random case: 32 random SSE / MMX-extension / integer instructions in a loop run 600 times; the FNV hash of the save state + memory.</summary>
    private static (string hash, long jitSteps) RandomCase(int seed)
    {
        var rng = new Random(2000 + seed);
        var (c, _) = Make(CpuModel.PentiumIII);
        var code = new List<byte>(SseOn);
        int[] regs = { 0, 1, 2, 3, 5, 6 };   // not ESP, not EDI (MASKMOVQ's pointer)
        foreach (int r in regs) code.AddRange(MovR32(r, rng.Next(4) == 0 ? (uint)rng.Next(4) : (uint)rng.NextInt64(0x100000000)));
        code.AddRange(MovR32(7, 0x5000));
        PutU(c, 0x1f0, (uint)(0x1f80 | rng.Next(4) << 13 | (rng.Next(3) == 0 ? 0x8000 : 0)));
        code.AddRange(Ldmx(0x1f0));
        for (int k = 0; k < 0x400; k += 4)
        {
            uint v = rng.Next(4) switch { 0 => (uint)rng.NextInt64(0x100000000), 1 => Bits((float)(rng.NextDouble() * 200 - 100)), 2 => Bits((float)rng.Next(-5, 6)), _ => Bits((float)(rng.NextDouble() * 1e-38)) };
            BitConverter.GetBytes(v).CopyTo(c.Mem, 0x14000 + k);
        }
        for (int x = 0; x < 8; x++) code.AddRange(M(0x28, x, (ushort)(0x4000 + x * 16)));
        code.AddRange(new byte[] { 0xc7, 0x06, 0x00, 0x30, 0x58, 0x02 });   // MOV word [0x3000], 600
        int loop = code.Count;
        int X() => rng.Next(8);
        byte Rg() => (byte)regs[rng.Next(regs.Length)];
        for (int k = 0; k < 32; k++)
        {
            ushort ma = (ushort)(0x4000 + rng.Next(0x40) * 16);
            int[] arith = { 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5c, 0x5d, 0x5e, 0x5f };
            switch (rng.Next(14))
            {
                case 0: case 1: case 2: code.AddRange(Rr(arith[rng.Next(arith.Length)], X(), X(), rng.Next(3) == 0 ? 0xf3 : 0)); break;
                case 3: code.AddRange(M(arith[rng.Next(arith.Length)], X(), ma)); break;
                case 4: code.AddRange(Rr(0xc2, X(), X(), rng.Next(2) == 0 ? 0xf3 : 0, rng.Next(8))); break;
                case 5: code.AddRange(Rr(0xc6, X(), X(), 0, rng.Next(256))); break;
                case 6: code.AddRange(Rr(0x14 + rng.Next(2), X(), X())); break;
                case 7: code.AddRange(M(0x29, X(), ma)); break;
                case 8: code.AddRange(Rr(0x2e + rng.Next(2), X(), X())); code.AddRange(new byte[] { 0x66, 0x0f, (byte)(0x40 + rng.Next(16)), (byte)(0xc0 | Rg() << 3 | Rg()) }); break;   // (U)COMISS + CMOVcc
                case 9: code.AddRange(Rr(0x2d - rng.Next(2), Rg(), X(), 0xf3)); break;   // CVT(T)SS2SI r32
                case 10: code.AddRange(Rr(0x2a, X(), Rg(), 0xf3)); break;                // CVTSI2SS
                case 11: code.AddRange(Rr(new[] { 0xda, 0xde, 0xe0, 0xe3, 0xe4, 0xea, 0xee, 0xf6, 0xfe, 0xef }[rng.Next(10)], rng.Next(8), rng.Next(8))); break;   // MMX ext + MMX
                case 12: code.AddRange(Rr(0x70, rng.Next(8), rng.Next(8), 0, rng.Next(256))); code.AddRange(Rr(0xf7, rng.Next(8), rng.Next(8))); break;   // PSHUFW, MASKMOVQ
                default: code.AddRange(new byte[] { 0x66, 0x83, (byte)(0xc0 | rng.Next(8) << 3 | Rg()), (byte)rng.Next(256) }); code.AddRange(Rr(0x50, Rg(), X())); break;   // ALU + MOVMSKPS
            }
        }
        code.AddRange(Stmx(0x1f4));
        code.AddRange(new byte[] { 0xff, 0x0e, 0x00, 0x30 });   // DEC word [0x3000]
        int rel = loop - (code.Count + 4);
        code.AddRange(new byte[] { 0x0f, 0x85, (byte)rel, (byte)(rel >> 8) });
        code.AddRange(M(0xae, 0, 0x6000));   // FXSAVE: everything into memory as well
        Array.Copy(code.ToArray(), 0, c.Mem, 0x10000, code.Count);
        c.Mem[0x10000 + code.Count] = 0xf4;
        for (int i = 0; i < 200_000 && !c.Halt; i++) c.Step(64);
        ulong h = 14695981039346656037UL;
        void Mix(ReadOnlySpan<byte> s) { foreach (byte x in s) { h ^= x; h *= 1099511628211UL; } }
        Mix(Save(c)); Mix(c.Mem.AsSpan(0, 0x60000));
        Mix(BitConverter.GetBytes(c.Cycle));
        return ($"{h:x16}", c.JitCompiled * 1_000_000L + c.JitRuns);
    }

    private static string? Arg(string[] a, string n) { int i = Array.FindIndex(a, x => x.Equals(n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
}

/// <summary>An independent exact reference for the directed rounding modes: the operation in exact rationals (BigInteger), rounded to float.</summary>
internal static class ExactRef
{
    private static (System.Numerics.BigInteger m, int e) Split(float f)
    {
        uint b = BitConverter.SingleToUInt32Bits(f);
        int ex = (int)((b >> 23) & 0xff); long man = b & 0x7fffff;
        if (ex == 0) ex = 1; else man |= 0x800000;
        var m = new System.Numerics.BigInteger(man);
        if ((b >> 31) != 0) m = -m;
        return (m, ex - 150);   // value = m * 2^e
    }

    /// <summary>op 0x58 ADD, 0x5C SUB, 0x59 MUL, 0x5E DIV, 0x51 SQRT(b); rc 0 nearest even, 1 down, 2 up, 3 toward zero. Finite operands only.</summary>
    public static float Round(int op, float a, float b, int rc)
    {
        if (op == 0x5e && b == 0) return a / b;
        // the exact value as num / den * 2^e (den > 0)
        System.Numerics.BigInteger num, den = 1; int e;
        var (ma, ea) = Split(a); var (mb, eb) = Split(b);
        switch (op)
        {
            case 0x58: case 0x5c:
            {
                if (op == 0x5c) mb = -mb;
                int lo = Math.Min(ea, eb);
                num = (ma << (ea - lo)) + (mb << (eb - lo)); e = lo;
                if (num.IsZero) return a == 0 && b == 0 ? (op == 0x58 ? a + b : a - b) : rc == 1 ? -0f : 0f;   // x - x: +0, -0 rounding down
                break;
            }
            case 0x59: num = ma * mb; e = ea + eb; if (num.IsZero) return a * b; break;
            case 0x5e: num = ma << 200; den = mb; e = ea - eb - 200; if (den.Sign < 0) { den = -den; num = -num; } if (num.IsZero) return a / b; break;
            default:
            {   // sqrt(mb * 2^eb): scale to an even exponent with plenty of bits, integer sqrt, the remainder's sign = the sticky bit
                if (mb.IsZero) return b;
                int sh = 200 + ((eb & 1) != 0 ? 1 : 0);
                var v = mb << sh; int ve = eb - sh;
                var r = ISqrt(v);
                bool exact = r * r == v;
                num = r * 2 + (exact ? 0 : 1); den = 2; e = ve / 2;   // r < sqrt < r + 1: r + 1/2 stands for the open interval (sticky)
                break;
            }
        }
        return RoundRational(num, den, e, rc);
    }

    private static System.Numerics.BigInteger ISqrt(System.Numerics.BigInteger n)
    {
        var x = (System.Numerics.BigInteger)Math.Sqrt((double)n);
        for (int i = 0; i < 8; i++) x = (x + n / x) >> 1;   // Newton from the double guess
        while (x * x > n) x--;
        while ((x + 1) * (x + 1) <= n) x++;
        return x;
    }

    /// <summary>Rounds the exact (num / den) * 2^e to float in mode rc.</summary>
    private static float RoundRational(System.Numerics.BigInteger num, System.Numerics.BigInteger den, int e, int rc)
    {
        bool neg = num.Sign < 0; if (neg) num = -num;
        // find k with 2^23 <= num / den * 2^k < 2^24 -> the value = q * 2^(e - k), q = 24-bit integer part
        long nb = (long)num.GetBitLength(), db = (long)den.GetBitLength();
        int k = (int)(23 - (nb - db));
        for (; ; )
        {
            var n2 = k >= 0 ? num << k : num; var d2 = k >= 0 ? den : den << -k;
            var q = System.Numerics.BigInteger.DivRem(n2, d2, out var rem);
            if (q >= (1 << 24)) { k--; continue; }
            if (q < (1 << 23)) { k++; continue; }
            int exp = e - k;   // value = (q + rem / d2) * 2^exp
            // subnormal: the float grid is 2^-149 at most
            if (exp < -149)
            {
                int s = -149 - exp;
                var d3 = d2 << s;
                q = System.Numerics.BigInteger.DivRem(n2, d3, out rem); d2 = d3; exp = -149;
            }
            int cmp = (rem * 2).CompareTo(d2);
            bool inexact = !rem.IsZero;
            bool up = rc switch
            {
                0 => cmp > 0 || (cmp == 0 && !q.IsEven),
                1 => inexact && neg,
                2 => inexact && !neg,
                _ => false,
            };
            if (up) q += 1;
            double v = (double)q * Math.Pow(2, exp);
            if (v > float.MaxValue) return neg ? (rc is 2 or 3 ? -float.MaxValue : float.NegativeInfinity) : (rc is 1 or 3 ? float.MaxValue : float.PositiveInfinity);
            float f = (float)v;
            return neg ? -f : f;
        }
    }
}
