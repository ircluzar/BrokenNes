using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using NesEmulator.X86;

namespace BrokenNes.Workshop.X86;

/// <summary>
/// <c>--x86t386 ROM.bin [--gen 3|4] [--ee test386-EE-reference.txt] [--out ee.txt] [--max N] [--nojit]</c>: runs test386.asm
/// (github.com/barotto/test386.asm, assembled with POST_PORT 190h and OUT_PORT E9h; see C:\BrokenNes-tools\x86\tests\SOURCES.md) as the
/// BIOS of a bare i386 (ROM at F0000h, no devices), logs every POST code, and stops at its HLT. POST FFh = every checked test passed; any
/// other code at the HLT names the test that failed. The 0xEE arithmetic dump is compared line by line with the reference.
/// Exit code 0 = POST FFh reached and the EE dump matches.
/// </summary>
internal static class X86T386Cli
{
    private sealed class Bus : IX86Bus
    {
        public readonly List<(byte code, long at)> Posts = new();
        public readonly StringBuilder Out = new();
        public I386? Cpu;
        public byte In8(int port) => 0xff;
        public ushort In16(int port) => 0xffff;
        public uint In32(int port) => 0xffffffff;
        public void Out8(int port, byte v)
        {
            if (port == 0x190) Posts.Add((v, Cpu?.Cycle ?? 0));
            else if (port == 0xe9) Out.Append((char)v);
        }
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

    public static int Run(string[] args)
    {
        if (args.Contains("--nojit", StringComparer.OrdinalIgnoreCase)) Environment.SetEnvironmentVariable("X86_NOJIT", "1");
        string rom = args[1];
        int gen = int.Parse(Arg(args, "--gen") ?? "3");
        long max = long.Parse(Arg(args, "--max") ?? "500000000");
        var bus = new Bus();
        var cpu = new I386(gen, 4 * 1024 * 1024, bus);
        bus.Cpu = cpu;
        var img = File.ReadAllBytes(rom);
        Array.Copy(img, 0, cpu.Mem, 0x100000 - img.Length, img.Length);
        cpu.Reset();
        var sw = Stopwatch.StartNew();
        string? stop = null;
        try
        {
            int ring = int.Parse(Arg(args, "--last") ?? "0");
            var hist = new Queue<string>();
            byte lastPost = 0xff;
            while (cpu.Cycle < max && !cpu.Halt)
            {
                if (ring == 0) { cpu.Step(1000); continue; }
                hist.Enqueue($"{cpu.SegSel[1]:X4}:{cpu.NextIp:X8} cpl{cpu.Cpl} eax {cpu.R[0]:X8} edx {cpu.R[2]:X8} esp {cpu.R[4]:X8}");
                if (hist.Count > ring) hist.Dequeue();
                cpu.Step(1);
                if (bus.Posts.Count > 0 && bus.Posts[^1].code != lastPost) lastPost = bus.Posts[^1].code;
                if (cpu.NextIp is >= 0xfe7f and <= 0xfe8a && cpu.SegBase[1] + cpu.NextIp >= 0xffe7f) break;
            }
            foreach (var h in hist) Console.WriteLine("  trace " + h);
        }
        catch (I386AbortException e) { stop = e.Message; }
        byte last = bus.Posts.Count > 0 ? bus.Posts[^1].code : (byte)0;
        Console.WriteLine($"x86t386 gen{gen}: {cpu.Cycle} instructions, {sw.Elapsed.TotalSeconds:F1}s; {(cpu.Halt ? "HLT" : stop ?? "instruction cap")} at {cpu.SegSel[1]:X4}:{cpu.Ip:X8}");
        Console.WriteLine("  POST codes: " + string.Join(" ", bus.Posts.Select(p => p.code.ToString("X2")).Distinct()));
        Console.WriteLine($"  last POST {last:X2}: {(last == 0xff ? "PASS (testing completed)" : "FAIL in test " + last.ToString("X2"))}");
        bool eeOk = true;
        string ee = bus.Out.ToString().Replace("\r", "");
        if (Arg(args, "--out") is string op) File.WriteAllText(op, ee);
        if (Arg(args, "--ee") is string refPath)
        {
            var exp = File.ReadAllText(refPath).Replace("\r", "").Split('\n').Where(l => l.Length > 0).ToArray();
            var got = ee.Split('\n').Where(l => l.Length > 0).ToArray();
            int diff = 0;
            var byOp = new Dictionary<string, int>();
            for (int i = 0; i < Math.Max(exp.Length, got.Length); i++)
            {
                string e = i < exp.Length ? exp[i] : "(missing)", g = i < got.Length ? got[i] : "(missing)";
                if (e == g) continue;
                diff++;
                string key = string.Join(" ", e.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(3));
                byOp[key] = byOp.GetValueOrDefault(key) + 1;
                if (diff <= 8) Console.WriteLine($"    EE line {i + 1}: got      {g}\n                 expected {e}");
            }
            eeOk = diff == 0;
            Console.WriteLine($"  EE dump: {exp.Length - diff}/{exp.Length} lines match" + (diff > 0 ? "; differing ops: " + string.Join(", ", byOp.OrderByDescending(k => k.Value).Take(30).Select(k => $"{k.Key} x{k.Value}")) : ""));
        }
        return last == 0xff && eeOk ? 0 : 1;
    }

    private static string? Arg(string[] a, string k) { int i = Array.FindIndex(a, x => x.Equals(k, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
}
