using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NesEmulator.X86;

namespace BrokenNes.Workshop.X86;

/// <summary>
/// <c>--x86cpu [--vec cpuvec.bin] [--only 386DX,P55C]</c>: checks the x86 CPU models (I386.Model.cs) on a bare i386 bus.
/// <list type="bullet">
/// <item>Opcode gating: which 486 / Pentium / MMX / P6 instructions are #UD on each model.</item>
/// <item>CPUID: vendor, signature and feature bits; whether EFLAGS.AC / EFLAGS.ID stick (the classic 386 / 486 / CPUID detection).</item>
/// <item>RDTSC: counts the model's core clock (clock / instruction rate), WRMSR 10h moves it.</item>
/// <item>The x87 + MMX vectors of tools/x86-cpu/cpuvec.asm (expected values worked out by hand): every line must be "ok".</item>
/// <item>A save state of a model with FPU / MMX state reloads and saves the same bytes, and refuses another model.</item>
/// </list>
/// Exit code 0 = everything passed.
/// </summary>
internal static class X86CpuCli
{
    private sealed class Bus : IX86Bus
    {
        public readonly StringBuilder Out = new();
        public int LastVec = -1;
        public byte In8(int port) => 0xff;
        public ushort In16(int port) => 0xffff;
        public uint In32(int port) => 0xffffffff;
        public int LastPost = -1;
        public void Out8(int port, byte v) { if (port == 0xe9) Out.Append((char)v); else if (port == 0x80) LastVec = v; else if (port == 0x190) LastPost = v; }
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

    private const int Mem = 2 * 1024 * 1024;
    private static int _fails;

    private static (I386 cpu, Bus bus) Make(CpuModel m)
    {
        var bus = new Bus();
        var cpu = new I386(m, Mem, bus);
        // IVT: vector n -> 0000:0600 + n * 8: MOV AL, n; OUT 80h, AL; HLT
        for (int n = 0; n < 32; n++)
        {
            uint stub = 0x600 + (uint)n * 8;
            cpu.Mem[n * 4] = (byte)stub; cpu.Mem[n * 4 + 1] = (byte)(stub >> 8); cpu.Mem[n * 4 + 2] = 0; cpu.Mem[n * 4 + 3] = 0;
            cpu.Mem[stub] = 0xb0; cpu.Mem[stub + 1] = (byte)n; cpu.Mem[stub + 2] = 0xe6; cpu.Mem[stub + 3] = 0x80; cpu.Mem[stub + 4] = 0xf4;
        }
        cpu.Reset();
        cpu.SetCsIp(0x1000, 0);
        foreach (int s in new[] { I386.SEG_DS, I386.SEG_ES, I386.SEG_SS }) cpu.LoadSeg(s, 0x1000);
        cpu.R[4] = 0xfff0;
        return (cpu, bus);
    }

    private static void Run(I386 cpu, int max)
    {
        for (int i = 0; i < max && !cpu.Halt; i++) cpu.Step(1);
    }

    private static void Check(bool ok, string what)
    {
        if (!ok) { _fails++; Console.WriteLine("  FAIL " + what); }
    }

    public static int Run(string[] args)
    {
        string? vec = Arg(args, "--vec");
        var models = CpuModel.Ids.Select(id => CpuModel.Parse(id)!).ToList();
        string? only = Arg(args, "--only");
        if (only != null) models = models.Where(m => only.Split(',').Contains(m.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        _fails = 0;
        string? dos = Arg(args, "--dos");
        if (dos != null)
        {   // a DOS program on the FreeDOS disk, once per model: its port E9h output (CPUDET.COM prints its findings there)
            var prog = File.ReadAllBytes(dos);
            int frames = int.Parse(Arg(args, "--frames") ?? "3000");
            foreach (var m in models)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var ses = new PcSession(prog, Path.GetFileName(dos), model: m);
                int f = 0;
                for (; f < frames && !ses.Machine.DebugPort.ToString().Contains("END"); f++) ses.RunFrame();
                Console.WriteLine($"== {m.Id} ({m.Name}): {f} frames, {sw.Elapsed.TotalSeconds:F1}s{(ses.Machine.Stopped != null ? ", STOPPED " + ses.Machine.Stopped : "")}");
                if (Environment.GetEnvironmentVariable("X86CPU_DBG") == "1") { var c = ses.Machine.Cpu; Console.WriteLine($"   cpu {c.SegSel[1]:x4}:{c.Ip:x8} halt {c.Halt} cycle {c.Cycle} tick {BitConverter.ToUInt32(c.Mem, 0x46c)} eflags {c.PeekFlags():x8}"); }
                foreach (var l in ses.Machine.DebugPort.ToString().Split(new[] { (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries)) Console.WriteLine("   " + l);
            }
            return 0;
        }
        string? t386 = Arg(args, "--t386");
        if (t386 != null)
        {   // test386.asm as the BIOS of each model: the last POST code at its HLT (FFh = all passed)
            var img = File.ReadAllBytes(t386);
            foreach (var m in models)
            {
                var bus = new Bus();
                var cpu = new I386(m, 4 * 1024 * 1024, bus);
                Array.Copy(img, 0, cpu.Mem, 0x100000 - img.Length, img.Length);
                cpu.Reset();
                string? stop = null;
                long n = 0;
                try { while (!cpu.Halt && n < 400_000_000) { cpu.Step(10000); n += 10000; } } catch (I386AbortException e) { stop = e.Message; }
                Console.WriteLine($"test386 {m.Id,-10} POST {bus.LastPost:X2} after {cpu.Cycle:N0} instructions{(stop != null ? " (" + stop + ")" : "")}");
            }
            return 0;
        }

        // ---- gating ----
        // (name, code, which models run it: a predicate on the model)
        var ops = new List<(string name, byte[] code, Func<CpuModel, bool> exists)>
        {
            ("CPUID", new byte[] { 0x0f, 0xa2 }, m => m.Legacy || m.Cpuid),
            ("BSWAP", new byte[] { 0x66, 0x0f, 0xc8 }, m => m.Legacy || m.Ops486),
            ("CMPXCHG", new byte[] { 0x0f, 0xb0, 0xc1 }, m => m.Legacy || m.Ops486),
            ("XADD", new byte[] { 0x0f, 0xc1, 0xc1 }, m => m.Legacy || m.Ops486),
            ("INVLPG", new byte[] { 0x0f, 0x01, 0x3e, 0x00, 0x00 }, m => m.Legacy || m.Ops486),
            ("WBINVD", new byte[] { 0x0f, 0x09 }, m => m.Legacy || m.Ops486),
            ("RDTSC", new byte[] { 0x0f, 0x31 }, m => m.Legacy || m.Ops586),
            ("CMPXCHG8B", new byte[] { 0x0f, 0xc7, 0x0e, 0x00, 0x01 }, m => m.Legacy || m.Ops586),
            ("RDMSR", new byte[] { 0x66, 0xb9, 0x10, 0, 0, 0, 0x0f, 0x32 }, m => m.Ops586),
            ("MOV EAX,CR4", new byte[] { 0x0f, 0x20, 0xe0 }, m => m.Legacy || m.Ops586),
            ("MOVQ mm", new byte[] { 0x0f, 0x6f, 0xc1 }, m => m.Mmx),
            ("EMMS", new byte[] { 0x0f, 0x77 }, m => m.Mmx),
            ("PADDB mm", new byte[] { 0x0f, 0xfc, 0xc1 }, m => m.Mmx),
            ("PSHUFW", new byte[] { 0x0f, 0x70, 0xc1, 0x00 }, m => m.Sse),   // the P3's MMX extensions (MMX rules, no OSFXSR)
            ("ADDPS", new byte[] { 0x0f, 0x58, 0xc1 }, m => false),           // SSE with CR4.OSFXSR clear: #UD on every chip
            ("CMOVZ", new byte[] { 0x0f, 0x44, 0xc1 }, m => m.Ops686),
            ("RDPMC", new byte[] { 0x0f, 0x33 }, m => m.Ops686),
            ("NOP 0F1F", new byte[] { 0x0f, 0x1f, 0xc0 }, m => m.Ops686),
            ("UD2", new byte[] { 0x0f, 0x0b }, m => false),
        };
        Console.WriteLine("opcode gating (#UD = not on this chip):");
        Console.WriteLine("  " + "".PadRight(12) + string.Join("", models.Select(m => m.Id.PadRight(11))));
        foreach (var (name, code, exists) in ops)
        {
            var row = new StringBuilder("  " + name.PadRight(12));
            foreach (var m in models)
            {
                var (cpu, bus) = Make(m);
                int steps = code[0] == 0x66 && code[1] == 0xb9 ? 2 : 1;
                Array.Copy(code, 0, cpu.Mem, 0x10000, code.Length);
                cpu.Mem[0x10000 + code.Length] = 0xf4;
                Run(cpu, steps + 4);
                bool ud = bus.LastVec == 6;
                row.Append((ud ? "#UD" : "ok").PadRight(11));
                Check(ud == !exists(m), $"{name} on {m.Id}: {(ud ? "#UD" : "ran")} (vector {bus.LastVec})");
            }
            Console.WriteLine(row);
        }

        // ---- CPUID / EFLAGS detection ----
        Console.WriteLine("CPUID / EFLAGS:");
        foreach (var m in models)
        {
            var (cpu, bus) = Make(m);
            // PUSHFD; POP EAX; MOV ECX,EAX; XOR EAX,240000h; PUSH EAX; POPFD; PUSHFD; POP EAX; XOR EAX,ECX; HLT
            byte[] code = { 0x66, 0x9c, 0x66, 0x58, 0x66, 0x89, 0xc1, 0x66, 0x35, 0x00, 0x00, 0x24, 0x00, 0x66, 0x50, 0x66, 0x9d, 0x66, 0x9c, 0x66, 0x58, 0x66, 0x31, 0xc8, 0xf4 };
            Array.Copy(code, 0, cpu.Mem, 0x10000, code.Length);
            Run(cpu, 50);
            bool ac = (cpu.R[0] & 0x40000) != 0, id = (cpu.R[0] & 0x200000) != 0;
            string cpuid = "-";
            if (m.Legacy || m.Cpuid)
            {
                var (c2, _) = Make(m);
                // CPUID(0) -> save EBX EDX ECX at 0:9000; CPUID(1) -> EAX EDX at 0:900C
                byte[] q = { 0x66, 0x31, 0xc0, 0x0f, 0xa2, 0x66, 0x89, 0x1e, 0x00, 0x90, 0x66, 0x89, 0x16, 0x04, 0x90, 0x66, 0x89, 0x0e, 0x08, 0x90,
                             0x66, 0xb8, 1, 0, 0, 0, 0x0f, 0xa2, 0x66, 0xa3, 0x0c, 0x90, 0x66, 0x89, 0x16, 0x10, 0x90, 0xf4 };
                Array.Copy(q, 0, c2.Mem, 0x10000, q.Length);
                Run(c2, 50);
                string vendor = Encoding.ASCII.GetString(c2.Mem, 0x19000, 12);
                uint sig = BitConverter.ToUInt32(c2.Mem, 0x1900c), edx = BitConverter.ToUInt32(c2.Mem, 0x19010);
                cpuid = $"\"{vendor}\" sig {sig:x4} edx {edx:x8}";
                if (!m.Legacy) { Check(vendor == "GenuineIntel", $"{m.Id} vendor {vendor}"); Check(sig == m.Signature && edx == m.FeatureEdx, $"{m.Id} cpuid 1"); }
            }
            Console.WriteLine($"  {m.Id,-10} AC {(ac ? "sticks" : "fixed")}  ID {(id ? "sticks" : "fixed")}  CPUID {cpuid}  {m.Ips / 1_000_000.0:F1} M instr/s at {m.ClockHz / 1_000_000} MHz");
            if (!m.Legacy) { Check(ac == m.Ops486, $"{m.Id} AC"); Check(id == m.Cpuid, $"{m.Id} ID"); }
        }

        // ---- RDTSC ----
        {
            var m = CpuModel.PentiumMmx;
            var (cpu, _) = Make(m);
            // 1000 x NOP then RDTSC; HLT
            for (int i = 0; i < 1000; i++) cpu.Mem[0x10000 + i] = 0x90;
            cpu.Mem[0x10000 + 1000] = 0x0f; cpu.Mem[0x10000 + 1001] = 0x31; cpu.Mem[0x10000 + 1002] = 0xf4;
            Run(cpu, 1010);
            ulong tsc = ((ulong)cpu.R[2] << 32) | cpu.R[0];
            long expect = (long)(1000 * (double)m.ClockHz / m.Ips);
            Console.WriteLine($"RDTSC after 1000 instructions on P55C: {tsc} (clock / IPS = {(double)m.ClockHz / m.Ips:F3} per instruction)");
            Check(Math.Abs((long)tsc - expect) <= 2, $"rdtsc {tsc} vs {expect}");
            // WRMSR 10h := 5000000000, then RDTSC: just above it
            var (c2, _) = Make(m);
            byte[] q = { 0x66, 0xb9, 0x10, 0, 0, 0, 0x66, 0xb8, 0x00, 0xf2, 0x05, 0x2a, 0x66, 0xba, 1, 0, 0, 0, 0x0f, 0x30, 0x0f, 0x31, 0xf4 };
            Array.Copy(q, 0, c2.Mem, 0x10000, q.Length);
            Run(c2, 20);
            ulong t2 = ((ulong)c2.R[2] << 32) | c2.R[0];
            Console.WriteLine($"RDTSC after WRMSR 10h := 5000000000: {t2}");
            Check(t2 >= 5_000_000_000UL && t2 < 5_000_000_100UL, "wrmsr tsc");
        }

        // ---- the x87 / MMX vectors ----
        if (vec != null)
        {
            var bin = File.ReadAllBytes(vec);
            foreach (var m in models)
            {
                var (cpu, bus) = Make(m);
                Array.Copy(bin, 0, cpu.Mem, 0x10000, bin.Length);
                Run(cpu, 2_000_000);
                var lines = bus.Out.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                int ok = lines.Count(l => l.StartsWith("ok ")), bad = lines.Count(l => l.StartsWith("FAIL"));
                bool fpu = lines.Contains("fpu present"), mmx = lines.Contains("mmx present"), done = lines.Contains("DONE");
                Console.WriteLine($"cpuvec {m.Id,-10} {(fpu ? "fpu" : "no fpu")}, {(mmx ? "mmx" : "no mmx")}: {ok} ok, {bad} FAIL{(done ? "" : $", NOT DONE (vector {bus.LastVec})")}");
                foreach (var l in lines.Where(l => l.StartsWith("FAIL"))) Console.WriteLine("    " + l);
                Check(done && bad == 0, $"cpuvec on {m.Id}");
                if (!m.Legacy) { Check(fpu == m.Fpu, $"{m.Id} fpu detect"); Check(mmx == m.Mmx, $"{m.Id} mmx detect"); }
            }
        }

        // ---- save state round trip ----
        {
            var m = CpuModel.PentiumMmx;
            var (cpu, bus) = Make(m);
            // FLDPI; MOVQ is not needed: FLD1; and an MMX write would reset TOP. FLDPI FLD1 HLT
            byte[] q = { 0xd9, 0xeb, 0xd9, 0xe8, 0xf4 };
            Array.Copy(q, 0, cpu.Mem, 0x10000, q.Length);
            Run(cpu, 10);
            byte[] a = Save(cpu);
            var (c2, _) = Make(m);
            using (var r = new BinaryReader(new MemoryStream(a))) c2.LoadState(r);
            byte[] b = Save(c2);
            Check(a.AsSpan().SequenceEqual(b), "P55C state round trip");
            var (c3, _) = Make(CpuModel.I486DX2);
            bool refused = false;
            try { using var r = new BinaryReader(new MemoryStream(a)); c3.LoadState(r); } catch (InvalidDataException) { refused = true; }
            Check(refused, "a P55C state must not load into a 486DX2");
            Console.WriteLine($"save state: P55C round trip {(a.AsSpan().SequenceEqual(b) ? "identical" : "DIFFERENT")}, into a 486DX2 {(refused ? "refused" : "ACCEPTED")}");
        }

        Console.WriteLine(_fails == 0 ? "x86cpu: all passed" : $"x86cpu: {_fails} FAILED");
        return _fails == 0 ? 0 : 1;
    }

    private static byte[] Save(I386 cpu)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true)) cpu.SaveState(w);
        return ms.ToArray();
    }

    private static string? Arg(string[] a, string n) { int i = Array.FindIndex(a, x => x.Equals(n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
}
