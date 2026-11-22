using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using NesEmulator.X86;

namespace BrokenNes.Workshop.X86;

/// <summary>
/// <c>--x86p6 [--seeds N] [--parity]</c>: the Pentium II (P6) instructions of the x86 core.
/// 1) Hand-checked semantics: CMOVcc, FCMOVcc, FCOMI / FUCOMI / FCOMIP / FUCOMIP, RDPMC, CPUID leaves 0-2, the MTRR / P6 MSRs, the hint NOPs,
///    SYSENTER / SYSEXIT through ring 3 and back, #UD of every P6 form on the P55C, the P2 save state round trip.
/// 2) Random hot loops of the P6 forms (CMOV reg / mem, 16 / 32-bit, FCMOV, FCOMI family, mixed with flag setters) run long enough to be
///    compiled; a hash of the whole state (registers, flags, x87, the clock, memory) per seed.
/// --parity runs (2) in two child processes, X86_NOJIT (the interpreter) and X86_JITSYNC + X86_JITHOT=2 (the IL JIT, compiled at once),
/// and requires identical hashes, and the JIT run to have run compiled code.
/// </summary>
public static class X86P6Cli
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

    private static int _fails;
    private static void Check(bool ok, string what) { if (!ok) { _fails++; Console.WriteLine("  FAIL " + what); } else Console.WriteLine("  ok   " + what); }

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

    private static void RunReal(I386 cpu, byte[] code, int max = 10_000)
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

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    private static byte[] MovR32(int r, uint v) => new byte[] { 0x66, (byte)(0xb8 + r), (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) };
    private static byte[] FldM64(ushort addr) => new byte[] { 0xdd, 0x06, (byte)addr, (byte)(addr >> 8) };
    private static void PutD(I386 cpu, uint a, double d) => BitConverter.GetBytes(d).CopyTo(cpu.Mem, 0x10000 + a);
    private const uint Zf = 0x40, Cf = 0x1, Pf = 0x4;

    public static int Run(string[] args)
    {
        if (args.Contains("--child")) return Child(args);
        _fails = 0;
        var p2 = CpuModel.PentiumII;
        Console.WriteLine($"x86p6: {p2.Name}, {p2.Ips / 1e6:F0} M instr/s, CPUID sig {p2.Signature:x4} edx {p2.FeatureEdx:x8}");

        // ---- CMOVcc ----
        {
            // MOV EAX,1; MOV EBX,2; MOV ECX,3; CMP EAX,EAX; CMOVZ EAX,EBX (taken); CMOVNZ ECX,EBX (not); CMOVB DX,[0x100] with CF=0 (not); STC; CMOVB SI,[0x100] (taken)
            var (c, _) = Make(p2);
            c.Mem[0x10100] = 0x34; c.Mem[0x10101] = 0x12;
            RunReal(c, Cat(MovR32(0, 1), MovR32(3, 2), MovR32(1, 3), MovR32(2, 0x55555555), MovR32(6, 0x77777777),
                new byte[] { 0x66, 0x39, 0xc0, 0x66, 0x0f, 0x44, 0xc3, 0x66, 0x0f, 0x45, 0xcb, 0x0f, 0x42, 0x16, 0x00, 0x01, 0xf9, 0x0f, 0x42, 0x36, 0x00, 0x01 }));
            Check(c.R[0] == 2 && c.R[1] == 3, $"CMOVZ taken / CMOVNZ not taken (eax {c.R[0]:x} ecx {c.R[1]:x})");
            Check(c.R[2] == 0x55555555 && c.R[6] == 0x77771234, $"CMOVB r16,[mem] not taken / taken (edx {c.R[2]:x8} esi {c.R[6]:x8})");
        }
        // ---- FCOMI / FUCOMI / FCOMIP / FCMOV ----
        {
            var (c, _) = Make(p2);
            // FNINIT; FLD1; FLDZ (ST0 0, ST1 1); FCOMI ST1 -> CF; PUSHF; POP [0x200]; FUCOMI ST0 -> ZF; PUSHF; POP [0x202];
            // FCMOVE ST0, ST1 (ZF set: ST0 = 1); FCOMIP ST1 (1 vs 1: ZF, pops); FNSTSW [0x204]; PUSHF; POP [0x206]
            RunReal(c, new byte[] { 0xdb, 0xe3, 0xd9, 0xe8, 0xd9, 0xee, 0xdb, 0xf1, 0x9c, 0x8f, 0x06, 0x00, 0x02, 0xdb, 0xe8, 0x9c, 0x8f, 0x06, 0x02, 0x02,
                0xda, 0xc9, 0xdf, 0xf1, 0xdd, 0x3e, 0x04, 0x02, 0x9c, 0x8f, 0x06, 0x06, 0x02, 0xdd, 0x1e, 0x10, 0x02 });
            uint f1 = BitConverter.ToUInt16(c.Mem, 0x10200), f2 = BitConverter.ToUInt16(c.Mem, 0x10202), sw = BitConverter.ToUInt16(c.Mem, 0x10204), f3 = BitConverter.ToUInt16(c.Mem, 0x10206);
            double st = BitConverter.ToDouble(c.Mem, 0x10210);
            Check((f1 & (Zf | Pf | Cf)) == Cf, $"FCOMI 0 < 1: CF only (flags {f1:x4})");
            Check((f2 & (Zf | Pf | Cf)) == Zf, $"FUCOMI ST0 = ST0: ZF only (flags {f2:x4})");
            Check((f3 & (Zf | Pf | Cf)) == Zf && ((sw >> 11) & 7) == 7, $"FCMOVE copied ST1, FCOMIP equal + popped (flags {f3:x4}, TOP {(sw >> 11) & 7})");
            Check(st == 1.0, $"FCMOVE: ST0 = 1 ({st})");
            // a NaN: FUCOMI = unordered (ZF PF CF) without #IA masked
            var (d, _) = Make(p2);
            PutD(d, 0x300, double.NaN);
            RunReal(d, new byte[] { 0xdb, 0xe3, 0xd9, 0xe8, 0xdd, 0x06, 0x00, 0x03, 0xdb, 0xe9, 0x9c, 0x8f, 0x06, 0x00, 0x02 });
            Check((BitConverter.ToUInt16(d.Mem, 0x10200) & (Zf | Pf | Cf)) == (Zf | Pf | Cf), "FUCOMI NaN: unordered");
        }
        // ---- RDPMC, CPUID, MSRs, hint NOP ----
        {
            var (c, b) = Make(p2);
            // WRMSR C1h := 0x12_3456789A; RDPMC ECX=0 -> [0x200]; CPUID 0 -> EAX [0x208]; CPUID 2 -> EAX [0x20C] EDX [0x210]; RDMSR FEh -> [0x214]; NOP [EAX]
            RunReal(c, Cat(MovR32(1, 0xc1), MovR32(0, 0x3456789a), MovR32(2, 0x12), new byte[] { 0x0f, 0x30 }, MovR32(1, 0), new byte[] { 0x0f, 0x33, 0x66, 0xa3, 0x00, 0x02, 0x66, 0x89, 0x16, 0x04, 0x02 },
                MovR32(0, 0), new byte[] { 0x0f, 0xa2, 0x66, 0xa3, 0x08, 0x02 }, MovR32(0, 2), new byte[] { 0x0f, 0xa2, 0x66, 0xa3, 0x0c, 0x02, 0x66, 0x89, 0x16, 0x10, 0x02 },
                MovR32(1, 0xfe), new byte[] { 0x0f, 0x32, 0x66, 0xa3, 0x14, 0x02, 0x0f, 0x1f, 0x00, 0x0f, 0x18, 0x07 },
                MovR32(1, 0x200), MovR32(0, 0x06), MovR32(2, 0xf), new byte[] { 0x0f, 0x30, 0x0f, 0x32, 0x66, 0xa3, 0x18, 0x02, 0x66, 0x89, 0x16, 0x1c, 0x02 }));
            uint U(int a) => BitConverter.ToUInt32(c.Mem, 0x10000 + a);
            Check(U(0x200) == 0x3456789a && U(0x204) == 0x12, $"RDPMC 0 = the WRMSR C1h value ({U(0x204):x2}{U(0x200):x8})");
            Check(U(0x208) == 2, $"CPUID 0: max leaf 2 ({U(0x208)})");
            Check(U(0x20c) == 0x03020101 && U(0x210) == 0x0c040843, $"CPUID 2: Deschutes 512 KB L2 descriptors ({U(0x20c):x8} {U(0x210):x8})");
            Check(U(0x214) == 0x508, $"MTRRcap ({U(0x214):x})");
            Check(U(0x218) == 6 && U(0x21c) == 0xf, $"MTRRphysBase0 write / read ({U(0x21c):x}{U(0x218):x8})");
            Check(b.LastVec == -1 && c.Halt, $"hint NOPs ran, no exception (vector {b.LastVec})");
            // a state round trip carries the P6 MSRs
            byte[] a = Save(c);
            var (c2, _) = Make(p2);
            using (var r = new BinaryReader(new MemoryStream(a))) c2.LoadState(r);
            Check(a.AsSpan().SequenceEqual(Save(c2)), "P2 save state round trip identical");
            var (c3, _) = Make(CpuModel.PentiumMmx);
            bool refused = false;
            try { using var r = new BinaryReader(new MemoryStream(a)); c3.LoadState(r); } catch (InvalidDataException) { refused = true; }
            Check(refused, "a P2 state is refused by a P55C machine");
            var (c4, b4) = Make(p2);
            RunReal(c4, Cat(MovR32(1, 0x1b), new byte[] { 0x0f, 0x32 }));
            Check(b4.LastVec == -1 && c4.R[0] == 0xFEE00100u, $"RDMSR APIC_BASE: FEE00100 (BSP, APIC off; Win98 VMM reads it on family 6) (vector {b4.LastVec}, eax {c4.R[0]:X8})");
        }
        // ---- P6 forms on the P55C: #UD ----
        foreach (var (name, code) in new (string, byte[])[] { ("CMOVZ", new byte[] { 0x0f, 0x44, 0xc1 }), ("RDPMC", new byte[] { 0x0f, 0x33 }), ("NOP 0F1F", new byte[] { 0x0f, 0x1f, 0xc0 }),
            ("SYSENTER", new byte[] { 0x0f, 0x34 }), ("FCMOVB", new byte[] { 0xdb, 0xe3, 0xda, 0xc1 }), ("FCOMI", new byte[] { 0xdb, 0xe3, 0xdb, 0xf1 }), ("FCOMIP", new byte[] { 0xdb, 0xe3, 0xdf, 0xf1 }) })
        {
            var (c, b) = Make(CpuModel.PentiumMmx);
            RunReal(c, code);
            Check(b.LastVec == 6, $"{name} on P55C: #UD (vector {b.LastVec})");
        }
        // ---- SYSENTER / SYSEXIT: ring 0 -> SYSEXIT -> ring 3 -> SYSENTER -> ring 0 ----
        {
            var (c, b) = Make(p2);
            byte[] r0 = { 0xb9, 0x74, 1, 0, 0, 0xb8, 8, 0, 0, 0, 0x31, 0xd2, 0x0f, 0x30, 0xb9, 0x75, 1, 0, 0, 0xb8, 0, 0x90, 0, 0, 0x0f, 0x30,
                          0xb9, 0x76, 1, 0, 0, 0xb8, 0, 0x20, 0, 0, 0x0f, 0x30, 0xb9, 0, 0x80, 0, 0, 0xba, 0, 0x18, 0, 0, 0x0f, 0x35 };
            byte[] r3 = { 0x8c, 0xc8, 0xa3, 0, 0x30, 0, 0, 0x89, 0xe0, 0xa3, 4, 0x30, 0, 0, 0x0f, 0x34 };
            c.ResetPm(0x1000);
            Array.Copy(r0, 0, c.Mem, 0x1000, r0.Length); Array.Copy(r3, 0, c.Mem, 0x1800, r3.Length); c.Mem[0x2000] = 0xf4;
            for (int i = 0; i < 100 && !c.Halt; i++) c.Step(1);
            uint cs3 = BitConverter.ToUInt16(c.Mem, 0x3000), sp3 = BitConverter.ToUInt32(c.Mem, 0x3004);
            Check(cs3 == 0x1b && sp3 == 0x8000, $"SYSEXIT: ring 3 CS {cs3:x2}, ESP {sp3:x}");
            Check(c.Halt && c.SegSel[I386.SEG_CS] == 8 && c.SegSel[I386.SEG_SS] == 0x10 && c.Cpl == 0 && c.R[4] == 0x9000 && b.LastVec == -1,
                $"SYSENTER: back in ring 0 (CS {c.SegSel[I386.SEG_CS]:x2} SS {c.SegSel[I386.SEG_SS]:x2} CPL {c.Cpl} ESP {c.R[4]:x}, vector {b.LastVec})");
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
        else foreach (var (h, _) in Enumerable.Range(0, seeds).Select(RandomCase)) Console.WriteLine($"p6hash {h}");

        Console.WriteLine(_fails == 0 ? "x86p6: all passed" : $"x86p6: {_fails} FAILED");
        return _fails == 0 ? 0 : 1;
    }

    private static int Child(string[] args)
    {
        int seeds = int.Parse(Arg(args, "--seeds") ?? "40");
        long jit = 0;
        for (int i = 0; i < seeds; i++) { var (h, j) = RandomCase(i); jit += j; Console.WriteLine($"p6hash {h}"); }
        Console.WriteLine($"p6jit {jit}");
        return 0;
    }

    private static (List<string> Hashes, long JitSteps) RunChild(int seeds, params (string k, string v)[] env)
    {
        string exe = Environment.ProcessPath!;
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) psi.ArgumentList.Add(typeof(X86P6Cli).Assembly.Location);
        foreach (var a in new[] { "--x86p6", "--child", "--seeds", seeds.ToString() }) psi.ArgumentList.Add(a);
        psi.Environment.Remove("X86_NOJIT"); psi.Environment.Remove("X86_JITSYNC");
        foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var lines = p.StandardOutput.ReadToEnd().Split('\n').Select(l => l.Trim()).ToList();
        p.WaitForExit();
        return (lines.Where(l => l.StartsWith("p6hash ")).Select(l => l[7..]).ToList(),
                lines.Where(l => l.StartsWith("p6jit ")).Select(l => long.Parse(l[6..])).FirstOrDefault());
    }

    /// <summary>One random case: 32 random P6 / flag-setting instructions in a loop run 600 times; the FNV hash of the save state + memory.</summary>
    private static (string hash, long jitSteps) RandomCase(int seed)
    {
        var rng = new Random(1000 + seed);
        var (c, _) = Make(CpuModel.PentiumII);
        var code = new List<byte>();
        int[] regs = { 0, 1, 2, 3, 5, 6, 7 };   // not ESP
        foreach (int r in regs) code.AddRange(MovR32(r, rng.Next(4) == 0 ? (uint)rng.Next(4) : (uint)rng.NextInt64(0x100000000)));
        code.AddRange(new byte[] { 0xdb, 0xe3 });   // FNINIT
        for (int k = 0; k < 6; k++)
        {
            double v = rng.Next(6) switch { 0 => 1.0, 1 => -2.5, 2 => 0.0, 3 => double.NaN, _ => Math.Round(rng.NextDouble() * 10 - 5, 1) };
            PutD(c, 0x2000 + (uint)k * 8, v);
            code.AddRange(FldM64((ushort)(0x2000 + k * 8)));
        }
        for (int k = 0; k < 0x400; k += 4) BitConverter.GetBytes((uint)rng.NextInt64(0x100000000)).CopyTo(c.Mem, 0x14000 + k);
        code.AddRange(new byte[] { 0xc7, 0x06, 0x00, 0x30, 0x58, 0x02 });   // MOV word [0x3000], 600
        int loop = code.Count;
        byte Rg() => (byte)regs[rng.Next(regs.Length)];
        byte ModRR(int reg, int rm) => (byte)(0xc0 | reg << 3 | rm);
        for (int k = 0; k < 32; k++)
        {
            ushort ma = (ushort)(0x4000 + rng.Next(0x100) * 4);
            switch (rng.Next(12))
            {
                case 0: code.AddRange(new byte[] { 0x66, 0x39, ModRR(Rg(), Rg()) }); break;                                     // CMP r32, r32
                case 1: code.AddRange(new byte[] { 0x66, 0x83, (byte)(0xc0 | rng.Next(8) << 3 | Rg()), (byte)rng.Next(256) }); break;   // ALU r32, imm8
                case 2: case 3: code.AddRange(new byte[] { 0x66, 0x0f, (byte)(0x40 + rng.Next(16)), ModRR(Rg(), Rg()) }); break;  // CMOVcc r32, r32
                case 4: code.AddRange(new byte[] { 0x0f, (byte)(0x40 + rng.Next(16)), ModRR(Rg(), Rg()) }); break;               // CMOVcc r16, r16
                case 5: code.AddRange(new byte[] { 0x66, 0x0f, (byte)(0x40 + rng.Next(16)), (byte)(Rg() << 3 | 6), (byte)ma, (byte)(ma >> 8) }); break;   // CMOVcc r32, [m]
                case 6: code.AddRange(new byte[] { 0x0f, (byte)(0x40 + rng.Next(16)), (byte)(Rg() << 3 | 6), (byte)ma, (byte)(ma >> 8) }); break;         // CMOVcc r16, [m]
                case 7: code.AddRange(new byte[] { 0x66, 0x89, (byte)(Rg() << 3 | 6), (byte)ma, (byte)(ma >> 8) }); break;      // MOV [m], r32
                case 8: code.AddRange(new byte[] { 0x66, 0xc1, (byte)(0xc0 | Rg()), (byte)rng.Next(1, 31) }); break;            // ROL r32, imm
                case 9: code.AddRange(new byte[] { (byte)(0xda + rng.Next(2)), (byte)(0xc0 | rng.Next(4) << 3 | rng.Next(1, 6)) }); break;   // FCMOVcc ST0, STi
                case 10: code.AddRange(new byte[] { 0xdb, (byte)((rng.Next(2) == 0 ? 0xe8 : 0xf0) + rng.Next(6)) }); break;     // FUCOMI / FCOMI ST0, STi
                default:
                    if (rng.Next(2) == 0) code.AddRange(new byte[] { 0xdf, (byte)((rng.Next(2) == 0 ? 0xe8 : 0xf0) + rng.Next(1, 6)), 0xd9, (byte)(0xc0 + rng.Next(5)) });   // FCOMIP / FUCOMIP + FLD ST(k)
                    else code.AddRange(new byte[] { 0xd9, (byte)(0xc8 + rng.Next(1, 6)), 0xd8, (byte)(0xc0 + rng.Next(6)) });   // FXCH STi; FADD ST0, STi
                    break;
            }
        }
        code.AddRange(new byte[] { 0xff, 0x0e, 0x00, 0x30 });   // DEC word [0x3000]
        int rel = loop - (code.Count + 4);
        code.AddRange(new byte[] { 0x0f, 0x85, (byte)rel, (byte)(rel >> 8) });
        RunRealLong(c, code.ToArray());
        ulong h = 14695981039346656037UL;
        void Mix(ReadOnlySpan<byte> s) { foreach (byte x in s) { h ^= x; h *= 1099511628211UL; } }
        Mix(Save(c)); Mix(c.Mem.AsSpan(0, 0x60000));
        Mix(BitConverter.GetBytes(c.Cycle));
        return ($"{h:x16}", c.JitCompiled * 1_000_000L + c.JitRuns);
    }

    private static void RunRealLong(I386 cpu, byte[] code)
    {
        Array.Copy(code, 0, cpu.Mem, 0x10000, code.Length);
        cpu.Mem[0x10000 + code.Length] = 0xf4;
        for (int i = 0; i < 200_000 && !cpu.Halt; i++) cpu.Step(64);
    }

    private static string? Arg(string[] a, string n) { int i = Array.FindIndex(a, x => x.Equals(n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
}
