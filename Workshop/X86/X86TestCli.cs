using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using NesEmulator.X86;

namespace BrokenNes.Workshop.X86;

/// <summary>
/// <c>--x86test</c>: verifies the i386 core (<c>NesEmulator.X86.I386</c>, ported from tiny386).
/// <list type="bullet">
/// <item>Hand-written programs (real mode ALU / flags, multiply and divide, shifts, string instructions, the stack, loops and jumps, software
/// interrupts, a divide fault, a hardware interrupt out of HLT, the 486 additions, the protected-mode switch with an IDT and a #GP, paging
/// with a page fault, ring 3 through a TSS), each checked against values worked out by hand.</item>
/// <item>A fuzz set: random real-mode instruction streams (16-bit, with 0x66 / 0x67 prefixes for the 32-bit paths) from a random state.</item>
/// <item><c>--ref &lt;tiny386ref.exe&gt;</c>: the differential trace. The same cases are run by the original tiny386 core (built from the
/// pinned source with a minimal harness) and the state after every instruction step, plus a memory hash, must match line for line.</item>
/// </list>
/// <c>--x86test [--ref exe] [--fuzz N] [--seed S] [--keep dir] [--only name]</c>. Exit code 0 = everything run passed.
/// </summary>
internal static class X86TestCli
{
    private const int MemSize = 4 * 1024 * 1024;

    public static int Run(string[] args)
    {
        string? refExe = Arg(args, "--ref"), keep = Arg(args, "--keep"), only = Arg(args, "--only");
        int fuzz = int.Parse(Arg(args, "--fuzz") ?? "2000");
        int seed = int.Parse(Arg(args, "--seed") ?? "386");

        var cases = new List<X86Case>();
        cases.AddRange(Programs.All());
        cases.AddRange(Fuzz.Generate(fuzz, seed, 0));
        if (only != null) cases = cases.Where(c => only.Split(',').Any(o => c.Name.Equals(o, StringComparison.OrdinalIgnoreCase) || (!o.StartsWith("fuzz") && c.Name.Contains(o, StringComparison.OrdinalIgnoreCase)))).ToList();
        if (args.Contains("--hashstep", StringComparer.OrdinalIgnoreCase)) foreach (var c in cases) c.HashStep = true;
        int nProg = cases.Count(c => c.Check != null);
        for (int i = 0; i < cases.Count; i++) cases[i].Id = i;

        var sw = Stopwatch.StartNew();
        var bus = new TestBus();
        var cpu = new I386(4, MemSize, bus);
        var trace = new StringBuilder();
        var perCase = new List<string>();
        int checks = 0, failed = 0, crashed = 0;
        foreach (var c in cases)
        {
            var one = new StringBuilder();
            try { RunCase(cpu, bus, c, one); }
            catch (Exception e) { crashed++; one.AppendLine($"CRASH {e.GetType().Name}: {e.Message}"); Console.WriteLine($"  CRASH case {c.Id} ({c.Name}): {e.Message}"); }
            perCase.Add(one.ToString());
            trace.Append(one);
            if (c.Check != null)
            {
                var chk = new Checker(c.Name, cpu);
                try { c.Check(chk); } catch (Exception e) { chk.Fail($"check threw {e.Message}"); }
                checks += chk.Total; failed += chk.Failed;
                Console.WriteLine($"  {(chk.Failed == 0 ? "PASS" : "FAIL")} {c.Name,-12} {chk.Total - chk.Failed}/{chk.Total}");
            }
        }
        Console.WriteLine($"x86test: programs {nProg}, checks {checks - failed}/{checks} pass; fuzz cases {cases.Count - nProg} run, {crashed} crashed ({sw.Elapsed.TotalSeconds:F1}s)");

        int rc = failed == 0 && crashed == 0 ? 0 : 1;
        if (refExe != null)
        {
            string dir = keep ?? Path.Combine(Path.GetTempPath(), "x86test_" + Environment.ProcessId);
            Directory.CreateDirectory(dir);
            string casePath = Path.Combine(dir, "cases.txt"), refOut = Path.Combine(dir, "ref.txt"), ourOut = Path.Combine(dir, "ours.txt");
            using (var w = new StreamWriter(casePath)) foreach (var c in cases) c.Write(w);
            File.WriteAllText(ourOut, trace.ToString());
            sw.Restart();
            var psi = new ProcessStartInfo(refExe, $"\"{casePath}\" \"{refOut}\"") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            using (var p = Process.Start(psi)!)
            {
                p.StandardError.ReadToEndAsync();
                p.StandardOutput.ReadToEndAsync();
                p.WaitForExit();
                if (p.ExitCode != 0) Console.WriteLine($"  reference exited with code {p.ExitCode}");
            }
            var refCases = SplitCases(File.ReadAllText(refOut).Replace("\r", ""));
            int match = 0, lines = 0, shown = 0;
            for (int i = 0; i < cases.Count; i++)
            {
                string ours = perCase[i];
                string theirs = refCases.TryGetValue(i, out var t) ? t : "(missing)\n";
                lines += ours.Count(ch => ch == '\n');
                if (ours == theirs) { match++; continue; }
                if (shown++ < 5) ShowDiff(cases[i], ours, theirs);
            }
            Console.WriteLine($"x86test --ref: {match}/{cases.Count} cases match tiny386 step for step ({lines} trace lines, reference ran {sw.Elapsed.TotalSeconds:F1}s){(match != cases.Count || keep != null ? $"; files in {dir}" : "")}");
            if (match != cases.Count) rc = 1;
            else if (keep == null) Directory.Delete(dir, true);
        }
        return rc;
    }

    private static Dictionary<int, string> SplitCases(string text)
    {
        var d = new Dictionary<int, string>();
        int cur = -1; var sb = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0) continue;
            if (line.StartsWith("CASE ")) { if (cur >= 0) d[cur] = sb.ToString(); cur = int.Parse(line[5..]); sb.Clear(); }
            sb.Append(line).Append('\n');
        }
        if (cur >= 0) d[cur] = sb.ToString();
        return d;
    }

    private static void ShowDiff(X86Case c, string ours, string theirs)
    {
        var a = ours.Split('\n'); var b = theirs.Split('\n');
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            string x = i < a.Length ? a[i] : "(end)", y = i < b.Length ? b[i] : "(end)";
            if (x == y) continue;
            Console.WriteLine($"  MISMATCH case {c.Id} ({c.Name}) line {i}:");
            if (i > 0) Console.WriteLine($"    before: {a[i - 1]}");
            Console.WriteLine($"    ours:   {x}");
            Console.WriteLine($"    tiny386:{y}");
            if (c.Code != null) Console.WriteLine($"    code: {Convert.ToHexString(c.Code)}");
            return;
        }
    }

    // ---- running a case exactly like the reference harness ----

    private static void RunCase(I386 cpu, TestBus bus, X86Case c, StringBuilder o)
    {
        o.Append("CASE ").Append(c.Id).Append('\n');
        var mem = cpu.Mem;
        foreach (var d in c.Mem)
        {
            switch (d.Kind)
            {
                case 'C': Array.Clear(mem, (int)d.Addr, (int)d.Len); break;
                case 'F': { uint xs = d.Seed == 0 ? 1 : d.Seed; for (uint i = 0; i < d.Len; i++) { xs ^= xs << 13; xs ^= xs >> 17; xs ^= xs << 5; mem[d.Addr + i] = (byte)xs; } break; }
                default: Array.Copy(d.Bytes!, 0, mem, d.Addr, d.Bytes!.Length); break;
            }
        }
        bus.IrqVec = c.IrqVec;
        cpu.Reset();
        cpu.Intr = false;
        for (int i = 0; i < 8; i++) cpu.R[i] = c.Regs[i];
        cpu.Flags = c.Regs[8];
        int[] order = { I386.SEG_CS, I386.SEG_DS, I386.SEG_ES, I386.SEG_SS, I386.SEG_FS, I386.SEG_GS };
        for (int i = 0; i < 6; i++) cpu.LoadSeg(order[i], c.Segs[i]);
        cpu.NextIp = c.Ip; cpu.Ip = c.Ip;
        for (int k = 0; k < c.Steps; k++)
        {
            if (k == c.IrqAt) cpu.RaiseIrq();
            if (cpu.Halt && !(cpu.Intr && (cpu.Flags & I386.IF) != 0)) break;
            cpu.Step(1);
            uint fl = cpu.PeekFlags();
            o.Append($"{k} ip={cpu.NextIp:x8} a={cpu.R[0]:x8} c={cpu.R[1]:x8} d={cpu.R[2]:x8} b={cpu.R[3]:x8} sp={cpu.R[4]:x8} bp={cpu.R[5]:x8} si={cpu.R[6]:x8} di={cpu.R[7]:x8} fl={fl:x8} " +
                     $"cs={cpu.SegSel[I386.SEG_CS]:x4} ds={cpu.SegSel[I386.SEG_DS]:x4} es={cpu.SegSel[I386.SEG_ES]:x4} ss={cpu.SegSel[I386.SEG_SS]:x4} fs={cpu.SegSel[I386.SEG_FS]:x4} gs={cpu.SegSel[I386.SEG_GS]:x4} " +
                     $"cr0={cpu.Cr0:x8} cr2={cpu.Cr2:x8} cr3={cpu.Cr3:x8} cpl={cpu.Cpl} h={(cpu.Halt ? 1 : 0)}\n");
            if (c.HashStep) { uint h = 2166136261u; for (uint i = 0; i < c.HashLen; i++) { h ^= mem[c.HashAddr + i]; h *= 16777619u; } o.Append($"mhash {h:x8}\n"); }
        }
        if (c.HashLen > 0)
        {
            uint h = 2166136261u;
            for (uint i = 0; i < c.HashLen; i++) { h ^= mem[c.HashAddr + i]; h *= 16777619u; }
            o.Append($"hash {h:x8}\n");
        }
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    internal sealed class TestBus : IX86Bus
    {
        public int IrqVec = 8;
        public byte In8(int port) => 0xff;
        public ushort In16(int port) => 0xffff;
        public uint In32(int port) => 0xffffffff;
        public void Out8(int port, byte v) { }
        public void Out16(int port, ushort v) { }
        public void Out32(int port, uint v) { }
        public bool MapsIoMem => false;
        public byte MemRead8(uint a) => 0;
        public void MemWrite8(uint a, byte v) { }
        public ushort MemRead16(uint a) => 0;
        public void MemWrite16(uint a, ushort v) { }
        public uint MemRead32(uint a) => 0;
        public void MemWrite32(uint a, uint v) { }
        public int AckIrq() => IrqVec;
    }

    internal sealed class Checker
    {
        private readonly string _name; private readonly I386 _cpu;
        public int Total, Failed;
        public Checker(string name, I386 cpu) { _name = name; _cpu = cpu; }
        public I386 Cpu => _cpu;
        public uint M8(uint a) => _cpu.Mem[a];
        public uint M16(uint a) => (uint)(_cpu.Mem[a] | _cpu.Mem[a + 1] << 8);
        public uint M32(uint a) => M16(a) | M16(a + 2) << 16;
        public void Eq(string what, uint actual, uint expected)
        {
            Total++;
            if (actual != expected) { Failed++; Console.WriteLine($"    {_name}: {what} = {actual:x} (expected {expected:x})"); }
        }
        public void Fail(string msg) { Total++; Failed++; Console.WriteLine($"    {_name}: {msg}"); }
    }
}

internal sealed class X86Case
{
    public int Id;
    public string Name = "";
    public uint[] Regs = new uint[9];   // eax ecx edx ebx esp ebp esi edi eflags
    public uint[] Segs = new uint[6];   // cs ds es ss fs gs
    public uint Ip;
    public int Steps;
    public int IrqAt = -1, IrqVec = 8;
    public uint HashAddr, HashLen;
    public bool HashStep;
    public byte[]? Code;
    public readonly List<MemOp> Mem = new();
    public Action<X86TestCli.Checker>? Check;

    public sealed class MemOp { public char Kind; public uint Addr, Len, Seed; public byte[]? Bytes; }
    public X86Case Clear(uint a, uint l) { Mem.Add(new MemOp { Kind = 'C', Addr = a, Len = l }); return this; }
    public X86Case Fill(uint a, uint l, uint seed) { Mem.Add(new MemOp { Kind = 'F', Addr = a, Len = l, Seed = seed }); return this; }
    public X86Case Bytes(uint a, byte[] b) { Mem.Add(new MemOp { Kind = 'B', Addr = a, Bytes = b }); return this; }

    public void Write(TextWriter w)
    {
        w.WriteLine($"CASE {Id}");
        foreach (var m in Mem)
        {
            if (m.Kind == 'C') w.WriteLine($"CLEAR {m.Addr:x} {m.Len:x}");
            else if (m.Kind == 'F') w.WriteLine($"FILL {m.Addr:x} {m.Len:x} {m.Seed:x}");
            else w.WriteLine($"BYTES {m.Addr:x} {Convert.ToHexString(m.Bytes!)}");
        }
        w.WriteLine("REGS " + string.Join(' ', Regs.Select(r => r.ToString("x"))));
        w.WriteLine("SEGS " + string.Join(' ', Segs.Select(r => r.ToString("x"))));
        w.WriteLine($"IP {Ip:x}");
        if (IrqAt >= 0) w.WriteLine($"IRQ {IrqAt} {IrqVec}");
        if (HashStep) w.WriteLine($"HASHSTEP {HashAddr:x} {HashLen:x}");
        w.WriteLine($"RUN {Steps}");
        if (HashLen > 0) w.WriteLine($"HASH {HashAddr:x} {HashLen:x}");
    }
}

/// <summary>A very small assembler: raw bytes plus labels with 8/16/32-bit relative and absolute fixups.</summary>
internal sealed class Asm
{
    private readonly List<byte> _b = new();
    private readonly uint _org;
    private readonly Dictionary<string, uint> _labels = new();
    private readonly List<(int pos, string label, int kind)> _fix = new();   // kind: 1 rel8, 2 rel16, 4 rel32, 12 abs16, 14 abs32
    public Asm(uint org) { _org = org; }
    public uint Here => _org + (uint)_b.Count;
    public uint this[string label] => _labels[label];
    public Asm L(string name) { _labels[name] = Here; return this; }
    public Asm B(params int[] x) { foreach (int v in x) _b.Add((byte)v); return this; }
    public Asm W(uint v) => B((int)(v & 0xff), (int)((v >> 8) & 0xff));
    public Asm D(uint v) => W(v & 0xffff).W(v >> 16);
    public Asm Rel8(string l) { _fix.Add((_b.Count, l, 1)); return B(0); }
    public Asm Rel16(string l) { _fix.Add((_b.Count, l, 2)); return W(0); }
    public Asm Rel32(string l) { _fix.Add((_b.Count, l, 4)); return D(0); }
    public Asm Abs16(string l) { _fix.Add((_b.Count, l, 12)); return W(0); }
    public Asm Abs32(string l) { _fix.Add((_b.Count, l, 14)); return D(0); }
    public Asm Align(int n) { while (Here % n != 0) B(0x90); return this; }
    public byte[] Build()
    {
        var b = _b.ToArray();
        foreach (var (pos, label, kind) in _fix)
        {
            uint t = _labels[label];
            switch (kind)
            {
                case 1: { int d = (int)(t - (_org + (uint)pos + 1)); if (d < -128 || d > 127) throw new Exception($"rel8 to {label} out of range"); b[pos] = (byte)d; break; }
                case 2: { uint d = t - (_org + (uint)pos + 2); b[pos] = (byte)d; b[pos + 1] = (byte)(d >> 8); break; }
                case 4: { uint d = t - (_org + (uint)pos + 4); for (int i = 0; i < 4; i++) b[pos + i] = (byte)(d >> (8 * i)); break; }
                case 12: b[pos] = (byte)t; b[pos + 1] = (byte)(t >> 8); break;
                default: for (int i = 0; i < 4; i++) b[pos + i] = (byte)(t >> (8 * i)); break;
            }
        }
        return b;
    }
}
