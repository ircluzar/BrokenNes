using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using NesEmulator.Sega;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--z80test</c>: verifies the shared Z80 core (<c>NesEmulator.Sega.Z80</c>).
/// <list type="bullet">
/// <item><c>--z80test --vectors &lt;dir&gt; [--only substr] [--max-failures N] [--json out.json] [--jobs N] [--ignore tags]</c>: the SingleStepTests/z80 <c>v1/*.json</c> vectors. Each test runs one
/// instruction from a given register/RAM state; the final registers (including WZ, Q and P), RAM, the number of T-states, every memory and I/O access (kind, address, data and the
/// T-state it happens at) and the port transactions are compared.</item>
/// <item><c>--z80test --zex file.com [--timeout s]</c>: the CP/M exercisers zexdoc and zexall (loaded at $0100; BDOS by <c>CALL 5</c>: C=2 prints E, C=9 prints a '$' string; a jump to $0000 ends).</item>
/// <item><c>--z80test --tap file.tap [--timeout s]</c>: Patrik Rak's z80test programs (z80full, z80doc, z80flags, z80docflags, z80ccf, z80memptr) on a minimal ZX Spectrum 48K host.</item>
/// <item><c>--z80test --bench [--seconds s] [--com file.com]</c>: throughput of the core on a synthetic instruction mix (and the start of an exerciser).</item>
/// <item><c>--z80test --selftest</c>: hand-written checks of what the vectors cannot reach (interrupts, NMI, HALT, wait states, state save).</item>
/// </list>
/// Exit code 0 = everything run passed.
/// </summary>
internal static class Z80TestCli
{
    public static int Run(string[] args)
    {
        string? vectors = Arg(args, "--vectors"), zex = Arg(args, "--zex"), tap = Arg(args, "--tap");
        bool bench = args.Contains("--bench", StringComparer.OrdinalIgnoreCase);
        bool selftest = args.Contains("--selftest", StringComparer.OrdinalIgnoreCase);
        try
        {
            if (vectors != null) return Z80Vectors.Run(args, vectors);
            if (zex != null) return Z80Zex.Run(args, zex);
            if (tap != null) return Z80Tap.Run(args, tap);
            if (bench) return Z80Bench.Run(args);
            if (selftest) return Z80SelfTest.Run(args);
        }
        catch (Exception e)
        {
            Console.WriteLine("z80test: " + e);
            return 2;
        }
        Console.WriteLine("usage: --z80test --vectors <dir> [--only name] [--max-failures N] [--json out.json] [--jobs N] [--ignore tags]");
        Console.WriteLine("       --z80test --zex <file.com> [--timeout seconds]");
        Console.WriteLine("       --z80test --tap <file.tap> [--timeout seconds]");
        Console.WriteLine("       --z80test --bench [--seconds s] [--com file.com]     --z80test --selftest");
        return 2;
    }

    internal static string? Arg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    internal static bool Flag(string[] args, string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);
}

/// <summary>A bus over a flat 64 KB RAM, for the exercisers and benchmarks. Port reads and writes go to <see cref="Z80Host.PortIn"/> and <see cref="Z80Host.PortOut"/>.</summary>
internal struct FlatBus : IZ80Bus
{
    public Z80Host Host;
    public byte FetchOpcode(ushort address) => Host.Mem[address];
    public byte Read(ushort address) => Host.Mem[address];
    public void Write(ushort address, byte value) => Host.Mem[address] = value;
    public byte In(ushort port) => Host.PortIn(port);
    public void Out(ushort port, byte value) => Host.PortOut(port, value);
    public byte InterruptVector() => 0xFF;
}

internal sealed class Z80Host
{
    public readonly byte[] Mem = new byte[65536];
    public Func<ushort, byte> PortIn = _ => 0xFF;
    public Action<ushort, byte> PortOut = (_, _) => { };
}

// ==================================================================================================================================================
// SingleStepTests/z80
// ==================================================================================================================================================

internal static class Z80Vectors
{
    private sealed class Access
    {
        public int Tick; public char Kind; public ushort Addr; public int Data;   // Kind: R, W (memory), I, O (I/O); Data -1 = unknown
        public override string ToString() => $"t{Tick} {Kind} {Addr:X4}={(Data < 0 ? "??" : Data.ToString("X2"))}";
    }

    /// <summary>The bus the vectors run on: 64 KB of RAM, a log of every access with the T-state it happened at, and scripted port reads.</summary>
    private sealed class VecMachine
    {
        public readonly byte[] Ram = new byte[65536];
        public readonly List<Access> Log = new();
        public int Tick;
        public readonly List<(ushort addr, byte val)> PortReads = new();
        public int PortReadIndex;
        public int UnlistedReads;
        public readonly bool[] Listed = new bool[65536];
    }

    private struct VecBus : IZ80Bus
    {
        public VecMachine M;
        public byte FetchOpcode(ushort a) => ReadAt(a);
        public byte Read(ushort a) => ReadAt(a);
        private byte ReadAt(ushort a)
        {
            if (!M.Listed[a]) M.UnlistedReads++;
            byte v = M.Ram[a];
            M.Log.Add(new Access { Tick = M.Tick, Kind = 'R', Addr = a, Data = v });
            return v;
        }
        public void Write(ushort a, byte v)
        {
            M.Ram[a] = v;
            M.Log.Add(new Access { Tick = M.Tick, Kind = 'W', Addr = a, Data = v });
        }
        public byte In(ushort p)
        {
            byte v = M.PortReadIndex < M.PortReads.Count ? M.PortReads[M.PortReadIndex++].val : (byte)0xFF;
            M.Log.Add(new Access { Tick = M.Tick, Kind = 'I', Addr = p, Data = v });
            return v;
        }
        public void Out(ushort p, byte v) => M.Log.Add(new Access { Tick = M.Tick, Kind = 'O', Addr = p, Data = v });
        public byte InterruptVector() => 0xFF;
    }

    private sealed class FileResult
    {
        public string Name = "";
        public int Tests, Failed;
        public long Ticks, Unlisted;
        public readonly SortedDictionary<string, int> Tags = new();
        public readonly List<string> Details = new();
        public double Seconds;
    }

    private static readonly string[] AllTags = { "regs", "flags", "wz", "q", "p", "ei", "iff", "im", "ram", "cycles", "events", "ports" };

    public static int Run(string[] args, string dir)
    {
        string? only = Z80TestCli.Arg(args, "--only");
        int maxFailures = int.TryParse(Z80TestCli.Arg(args, "--max-failures"), out int mf) ? mf : 12;
        string? json = Z80TestCli.Arg(args, "--json");
        int jobs = int.TryParse(Z80TestCli.Arg(args, "--jobs"), out int j) ? Math.Max(1, j) : 2;
        var ignore = new HashSet<string>((Z80TestCli.Arg(args, "--ignore") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var files = Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (only != null) files = files.Where(f => Path.GetFileName(f).Contains(only, StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count == 0) { Console.WriteLine($"no vector files in {dir}"); return 2; }
        Console.WriteLine($"== SingleStepTests/z80: {files.Count} file(s), jobs={jobs}" + (ignore.Count > 0 ? ", ignoring: " + string.Join(",", ignore) : ""));

        var results = new FileResult[files.Count];
        int printed = 0;
        object gate = new();
        var sw = Stopwatch.StartNew();
        Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i =>
        {
            var r = RunFile(files[i], ignore);
            results[i] = r;
            lock (gate)
            {
                Console.WriteLine($"  {(r.Failed == 0 ? "PASS" : "FAIL")}  {r.Name,-14} {r.Tests - r.Failed}/{r.Tests}" + (r.Failed > 0 ? "  [" + string.Join(" ", r.Tags.Select(t => $"{t.Key}:{t.Value}")) + "]" : ""));
                foreach (var d in r.Details)
                {
                    if (printed++ >= maxFailures) break;
                    Console.WriteLine(d);
                }
            }
        });
        sw.Stop();

        int tests = results.Sum(r => r.Tests), failed = results.Sum(r => r.Failed);
        long ticks = results.Sum(r => r.Ticks);
        int filesFailed = results.Count(r => r.Failed > 0);
        var tags = new SortedDictionary<string, int>();
        foreach (var r in results) foreach (var t in r.Tags) tags[t.Key] = tags.GetValueOrDefault(t.Key) + t.Value;
        Console.WriteLine();
        Console.WriteLine($"{files.Count - filesFailed}/{files.Count} files clean; {tests - failed}/{tests} tests passed; {failed} failed");
        if (tags.Count > 0) Console.WriteLine("failures by kind (a test can fail several): " + string.Join(", ", tags.Select(t => $"{t.Key}={t.Value}")));
        Console.WriteLine($"reads of memory the vector did not list (served as 0): {results.Sum(r => r.Unlisted)}");
        double cpu = results.Sum(r => r.Seconds);
        Console.WriteLine($"{ticks:N0} T-states executed in {sw.Elapsed.TotalSeconds:F1} s wall ({cpu:F1} s of worker time incl. parsing)");

        if (json != null)
        {
            using var fs = File.Create(json);
            using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
            w.WriteStartObject();
            w.WriteNumber("files", files.Count); w.WriteNumber("tests", tests); w.WriteNumber("failed", failed); w.WriteNumber("tstates", ticks); w.WriteNumber("unlistedReads", results.Sum(r => r.Unlisted));
            w.WriteStartArray("results");
            foreach (var r in results)
            {
                w.WriteStartObject();
                w.WriteString("file", r.Name); w.WriteNumber("tests", r.Tests); w.WriteNumber("failed", r.Failed);
                w.WriteStartObject("kinds"); foreach (var t in r.Tags) w.WriteNumber(t.Key, t.Value); w.WriteEndObject();
                if (r.Details.Count > 0) { w.WriteStartArray("examples"); foreach (var d in r.Details.Take(3)) w.WriteStringValue(d); w.WriteEndArray(); }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return failed == 0 ? 0 : 1;
    }

    private static FileResult RunFile(string path, HashSet<string> ignore)
    {
        var res = new FileResult { Name = Path.GetFileNameWithoutExtension(path) };
        var sw = Stopwatch.StartNew();
        var m = new VecMachine();
        var cpu = new Z80<VecBus>(new VecBus { M = m });
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var t in doc.RootElement.EnumerateArray())
        {
            res.Tests++;
            string? err = RunTest(cpu, m, t, ignore, res);
            if (err != null)
            {
                res.Failed++;
                if (res.Details.Count < 4) res.Details.Add(err);
            }
        }
        sw.Stop();
        res.Seconds = sw.Elapsed.TotalSeconds;
        return res;
    }

    private static int Int(JsonElement e, string name) => e.GetProperty(name).GetInt32();

    private static string? RunTest(Z80<VecBus> cpu, VecMachine m, JsonElement t, HashSet<string> ignore, FileResult res)
    {
        var ini = t.GetProperty("initial");
        var fin = t.GetProperty("final");

        // ---- set up
        cpu.Reset();
        cpu.PC = (ushort)Int(ini, "pc"); cpu.SP = (ushort)Int(ini, "sp");
        cpu.A = (byte)Int(ini, "a"); cpu.F = (byte)Int(ini, "f");
        cpu.B = (byte)Int(ini, "b"); cpu.C = (byte)Int(ini, "c");
        cpu.D = (byte)Int(ini, "d"); cpu.E = (byte)Int(ini, "e");
        cpu.H = (byte)Int(ini, "h"); cpu.L = (byte)Int(ini, "l");
        cpu.I = (byte)Int(ini, "i"); cpu.R = (byte)Int(ini, "r");
        cpu.IX = (ushort)Int(ini, "ix"); cpu.IY = (ushort)Int(ini, "iy");
        cpu.AF2 = (ushort)Int(ini, "af_"); cpu.BC2 = (ushort)Int(ini, "bc_"); cpu.DE2 = (ushort)Int(ini, "de_"); cpu.HL2 = (ushort)Int(ini, "hl_");
        cpu.WZ = (ushort)Int(ini, "wz");
        cpu.InterruptMode = Int(ini, "im");
        cpu.IFF1 = Int(ini, "iff1") != 0; cpu.IFF2 = Int(ini, "iff2") != 0;
        cpu.EiPending = Int(ini, "ei") != 0;
        cpu.LdAirJustRan = Int(ini, "p") != 0;
        cpu.Q = (byte)Int(ini, "q");
        cpu.Irq = false;

        var touched = new List<int>();
        foreach (var r in ini.GetProperty("ram").EnumerateArray())
        {
            int a = r[0].GetInt32();
            m.Ram[a] = (byte)r[1].GetInt32();
            m.Listed[a] = true;
            touched.Add(a);
        }
        m.Log.Clear(); m.PortReads.Clear(); m.PortReadIndex = 0; m.Tick = 0; m.UnlistedReads = 0;
        bool hasPorts = t.TryGetProperty("ports", out var portsWanted);
        if (hasPorts)
            foreach (var p in portsWanted.EnumerateArray())
                if (p[2].GetString() == "r") m.PortReads.Add(((ushort)p[0].GetInt32(), (byte)p[1].GetInt32()));

        var cycles = t.GetProperty("cycles");
        int expectedCycles = cycles.GetArrayLength();

        // ---- run one instruction
        int ticks = 0;
        do
        {
            m.Tick = ticks;
            cpu.Tick();
            ticks++;
        } while (!cpu.AtInstructionBoundary && ticks < 64);
        res.Ticks += ticks;

        // ---- compare
        var diffs = new List<string>();
        var kinds = new HashSet<string>();
        void Tag(string tag, string detail) { if (!ignore.Contains(tag)) { kinds.Add(tag); diffs.Add(detail); } }

        void Reg8(string key, int got, string tag = "regs")
        {
            int want = Int(fin, key);
            if (want != got) Tag(tag, $"{key}: want {want:X2} got {got:X2}");
        }
        void Reg16(string key, int got, string tag = "regs")
        {
            int want = Int(fin, key);
            if (want != got) Tag(tag, $"{key}: want {want:X4} got {got:X4}");
        }
        int wantF = Int(fin, "f");
        if (wantF != cpu.F) Tag("flags", $"f: want {wantF:X2} ({FlagString(wantF)}) got {cpu.F:X2} ({FlagString(cpu.F)}) xor {(wantF ^ cpu.F):X2}");
        Reg8("a", cpu.A); Reg8("b", cpu.B); Reg8("c", cpu.C); Reg8("d", cpu.D); Reg8("e", cpu.E); Reg8("h", cpu.H); Reg8("l", cpu.L);
        Reg8("i", cpu.I); Reg8("r", cpu.R);
        Reg16("pc", cpu.PC); Reg16("sp", cpu.SP); Reg16("ix", cpu.IX); Reg16("iy", cpu.IY);
        Reg16("af_", cpu.AF2); Reg16("bc_", cpu.BC2); Reg16("de_", cpu.DE2); Reg16("hl_", cpu.HL2);
        Reg16("wz", cpu.WZ, "wz");
        Reg8("q", cpu.Q, "q");
        if (Int(fin, "p") != (cpu.LdAirJustRan ? 1 : 0)) Tag("p", $"p: want {Int(fin, "p")} got {(cpu.LdAirJustRan ? 1 : 0)}");
        if (Int(fin, "ei") != (cpu.EiPending ? 1 : 0)) Tag("ei", $"ei: want {Int(fin, "ei")} got {(cpu.EiPending ? 1 : 0)}");
        if (Int(fin, "iff1") != (cpu.IFF1 ? 1 : 0)) Tag("iff", $"iff1: want {Int(fin, "iff1")} got {(cpu.IFF1 ? 1 : 0)}");
        if (Int(fin, "iff2") != (cpu.IFF2 ? 1 : 0)) Tag("iff", $"iff2: want {Int(fin, "iff2")} got {(cpu.IFF2 ? 1 : 0)}");
        if (Int(fin, "im") != cpu.InterruptMode) Tag("im", $"im: want {Int(fin, "im")} got {cpu.InterruptMode}");

        foreach (var r in fin.GetProperty("ram").EnumerateArray())
        {
            int a = r[0].GetInt32(), want = r[1].GetInt32();
            if (m.Ram[a] != want) Tag("ram", $"ram[{a:X4}]: want {want:X2} got {m.Ram[a]:X2}");
        }

        if (ticks != expectedCycles) Tag("cycles", $"T-states: want {expectedCycles} got {ticks}");

        // bus accesses: the expected list comes from the pin columns (a read pulses at T2, its data is visible in T3; a write has its data at T2; the core does
        // both at the third T-state of the machine cycle, which is the cycle after the pin sample).
        var expected = new List<Access>();
        for (int k = 0; k < expectedCycles; k++)
        {
            var c = cycles[k];
            string pins = c[2].GetString()!;
            bool mem = pins[2] == 'm', io = pins[3] == 'i';
            if (!mem && !io) continue;
            if (pins[0] == 'r')
            {
                var next = k + 1 < expectedCycles ? cycles[k + 1][1] : default;
                int data = next.ValueKind == JsonValueKind.Number ? next.GetInt32() : -1;
                expected.Add(new Access { Tick = k + 1, Kind = mem ? 'R' : 'I', Addr = (ushort)c[0].GetInt32(), Data = data });
            }
            else if (pins[1] == 'w')
            {
                int data = c[1].ValueKind == JsonValueKind.Number ? c[1].GetInt32() : -1;
                expected.Add(new Access { Tick = k + 1, Kind = mem ? 'W' : 'O', Addr = (ushort)c[0].GetInt32(), Data = data });
            }
        }
        var got = m.Log;
        string? evDiff = null;
        if (expected.Count != got.Count) evDiff = $"{expected.Count} accesses expected, {got.Count} made";
        else
            for (int k = 0; k < expected.Count; k++)
                if (expected[k].Tick != got[k].Tick || expected[k].Kind != got[k].Kind || expected[k].Addr != got[k].Addr || (expected[k].Data >= 0 && expected[k].Data != got[k].Data))
                { evDiff = $"access {k}: want [{expected[k]}] got [{got[k]}]"; break; }
        if (evDiff != null) Tag("events", evDiff + "  all want: " + string.Join("; ", expected) + "  all got: " + string.Join("; ", got));

        // port transactions
        var ioGot = got.Where(g => g.Kind is 'I' or 'O').ToList();
        int np = hasPorts ? portsWanted.GetArrayLength() : 0;
        if (np != ioGot.Count) Tag("ports", $"{np} port transactions expected, {ioGot.Count} made");
        else
            for (int k = 0; k < np; k++)
            {
                var p = portsWanted[k];
                char kind = p[2].GetString() == "r" ? 'I' : 'O';
                if (p[0].GetInt32() != ioGot[k].Addr || p[1].GetInt32() != ioGot[k].Data || kind != ioGot[k].Kind)
                { Tag("ports", $"port {k}: want {p[0].GetInt32():X4} {p[1].GetInt32():X2} {p[2].GetString()} got {ioGot[k].Addr:X4} {ioGot[k].Data:X2} {ioGot[k].Kind}"); break; }
            }

        res.Unlisted += m.UnlistedReads;

        // restore RAM
        foreach (var a in touched) { m.Ram[a] = 0; m.Listed[a] = false; }
        foreach (var g in got) if (g.Kind == 'W') m.Ram[g.Addr] = 0;

        if (diffs.Count == 0) return null;
        foreach (var k in kinds) res.Tags[k] = res.Tags.GetValueOrDefault(k) + 1;
        var sb = new StringBuilder();
        sb.Append("    ").Append(t.GetProperty("name").GetString()).Append(": ");
        var bytes = new List<string>();
        foreach (var r in ini.GetProperty("ram").EnumerateArray()) bytes.Add($"{r[0].GetInt32():X4}={r[1].GetInt32():X2}");
        sb.Append("pc=").Append(Int(ini, "pc").ToString("X4")).Append(" ram[").Append(string.Join(" ", bytes.Take(6))).Append("] ");
        sb.Append("a=").Append(Int(ini, "a").ToString("X2")).Append(" f=").Append(Int(ini, "f").ToString("X2")).Append(" bc=").Append(((Int(ini, "b") << 8) | Int(ini, "c")).ToString("X4"))
          .Append(" hl=").Append(((Int(ini, "h") << 8) | Int(ini, "l")).ToString("X4")).Append(" wz=").Append(Int(ini, "wz").ToString("X4")).Append(" q=").Append(Int(ini, "q").ToString("X2")).AppendLine();
        foreach (var d in diffs.Take(6)) sb.Append("        ").AppendLine(d.Length > 600 ? d[..600] + "..." : d);
        return sb.ToString().TrimEnd();
    }

    private static string FlagString(int f)
    {
        var sb = new StringBuilder();
        sb.Append((f & 0x80) != 0 ? 'S' : 's').Append((f & 0x40) != 0 ? 'Z' : 'z').Append((f & 0x20) != 0 ? 'Y' : 'y').Append((f & 0x10) != 0 ? 'H' : 'h')
          .Append((f & 0x08) != 0 ? 'X' : 'x').Append((f & 0x04) != 0 ? 'P' : 'p').Append((f & 0x02) != 0 ? 'N' : 'n').Append((f & 0x01) != 0 ? 'C' : 'c');
        return sb.ToString();
    }
}

// ==================================================================================================================================================
// zexdoc / zexall
// ==================================================================================================================================================

internal static class Z80Zex
{
    public static int Run(string[] args, string file)
    {
        double timeout = double.TryParse(Z80TestCli.Arg(args, "--timeout"), out double t) ? t : 3600;
        var host = new Z80Host();
        var com = File.ReadAllBytes(file);
        Array.Copy(com, 0, host.Mem, 0x100, com.Length);
        host.Mem[0] = 0xC3; host.Mem[1] = 0x00; host.Mem[2] = 0x00;        // warm boot: JP 0 (the run ends when the CPU gets here)
        host.Mem[5] = 0xC9;                                                // BDOS entry: RET, after the host has serviced the call
        var cpu = new Z80<FlatBus>(new FlatBus { Host = host });
        cpu.PC = 0x100;
        cpu.SP = 0xFFF0;

        Console.WriteLine($"== {Path.GetFileName(file)} ({com.Length} bytes), timeout {timeout:F0} s");
        var sw = Stopwatch.StartNew();
        var output = new StringBuilder();
        var line = new StringBuilder();
        long lastReport = 0;
        bool finished = false, timedOut = false;
        while (true)
        {
            ushort pc = cpu.PC;
            if (pc == 0) { finished = true; break; }
            if (pc == 5)
            {
                byte fn = cpu.C;
                if (fn == 2) Emit((char)cpu.E);
                else if (fn == 9)
                {
                    for (int a = cpu.DE; host.Mem[a] != (byte)'$'; a++) Emit((char)host.Mem[a]);
                }
            }
            cpu.ExecuteInstruction();
            if ((cpu.TotalCycles & ~0xFFFFFL) != lastReport)
            {
                lastReport = cpu.TotalCycles & ~0xFFFFFL;
                if (sw.Elapsed.TotalSeconds > timeout) { timedOut = true; break; }
            }
        }
        sw.Stop();
        if (line.Length > 0) Console.WriteLine(line);
        double secs = sw.Elapsed.TotalSeconds;
        string text = output.ToString();
        int ok = CountOccurrences(text, "OK"), err = CountOccurrences(text, "ERROR");
        Console.WriteLine();
        Console.WriteLine($"{(finished ? "finished" : timedOut ? "TIMED OUT" : "stopped")}: {cpu.TotalCycles:N0} T-states in {secs:F1} s = {cpu.TotalCycles / secs / 1e6:F1} M T-states/s " +
                          $"(a real 3.58 MHz Z80 would need {cpu.TotalCycles / 3.579545e6 / 3600:F2} h); {ok} test(s) reported OK, {err} ERROR");
        return finished && err == 0 && ok > 0 ? 0 : 1;

        void Emit(char c)
        {
            output.Append(c);
            if (c == '\n') { Console.WriteLine(line.ToString().TrimEnd('\r')); line.Clear(); }
            else if (c != '\r') line.Append(c);
            Console.Out.Flush();
        }
    }

    private static int CountOccurrences(string s, string needle)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}

// ==================================================================================================================================================
// z80test (.tap) on a minimal ZX Spectrum 48K host
// ==================================================================================================================================================

/// <summary>
/// Runs Patrik Rak's z80test programs (MIT; each compares an exhaustive computation over a family of instructions against CRCs recorded from a real Zilog Z80 in a 48K Spectrum).
/// The host is as small as it can be: 48K of RAM, the keyboard port returns "no key pressed" (the test insists on the value $BF), and the three ROM routines the program
/// calls are intercepted (print a character at $0010, open the channel at $1601); no ROM is needed and nothing is contended.
/// </summary>
internal static class Z80Tap
{
    private struct TapBus : IZ80Bus
    {
        public byte[] Mem;
        public byte FetchOpcode(ushort a) => Mem[a];
        public byte Read(ushort a) => Mem[a];
        public void Write(ushort a, byte v) { if (a >= 0x4000) Mem[a] = v; }
        public byte In(ushort port) => (port & 1) == 0 ? (byte)0xBF : (byte)0xFF;     // the ULA: no keys, EAR low
        public void Out(ushort port, byte v) { }
        public byte InterruptVector() => 0xFF;
    }

    public static int Run(string[] args, string file)
    {
        double timeout = double.TryParse(Z80TestCli.Arg(args, "--timeout"), out double t) ? t : 3600;
        var tap = File.ReadAllBytes(file);
        var mem = new byte[65536];

        // the TAP file: [length lo, hi][flag][data...][checksum] blocks; the CODE block (flag $FF) after the BASIC loader is the program, loaded at 32768
        int pos = 0, codeLength = 0; bool loaded = false; ushort start = 0x8000;
        var blocks = new List<(byte flag, byte[] data)>();
        while (pos + 2 <= tap.Length)
        {
            int len = tap[pos] | tap[pos + 1] << 8; pos += 2;
            if (len < 2 || pos + len > tap.Length) break;
            blocks.Add((tap[pos], tap[(pos + 1)..(pos + len - 1)]));
            pos += len;
        }
        for (int i = 0; i + 1 < blocks.Count; i++)
        {
            var h = blocks[i];
            if (h.flag == 0x00 && h.data.Length >= 17 && h.data[0] == 3 && blocks[i + 1].flag == 0xFF)
            {
                start = (ushort)(h.data[13] | h.data[14] << 8);
                var code = blocks[i + 1].data;
                Array.Copy(code, 0, mem, start, code.Length);
                codeLength = code.Length;
                loaded = true;
            }
        }
        if (!loaded) { Console.WriteLine("no CODE block found in " + file); return 2; }

        var cpu = new Z80<TapBus>(new TapBus { Mem = mem });
        const ushort Sentinel = 0x1234;                       // where the program's final RET goes (BASIC, on a real machine)
        cpu.SP = 0xFF00; mem[0xFEFE] = (byte)(Sentinel & 0xFF); mem[0xFEFF] = (byte)(Sentinel >> 8); cpu.SP = 0xFEFE;
        cpu.PC = start;
        cpu.IY = 0x5C3A;

        Console.WriteLine($"== {Path.GetFileName(file)}: CODE at {start} ({codeLength} bytes), timeout {timeout:F0} s");
        var sw = Stopwatch.StartNew();
        var line = new StringBuilder();
        var all = new StringBuilder();
        int skip = 0;
        bool finished = false, timedOut = false;
        long lastCheck = 0;
        while (true)
        {
            ushort pc = cpu.PC;
            if (pc == Sentinel) { finished = true; break; }
            if (pc == 0x0010)
            {
                Emit(cpu.A);
                Ret();
            }
            else if (pc == 0x1601) Ret();
            else
            {
                cpu.ExecuteInstruction();
                if (cpu.TotalCycles - lastCheck > 0xFFFFF)
                {
                    lastCheck = cpu.TotalCycles;
                    if (sw.Elapsed.TotalSeconds > timeout) { timedOut = true; break; }
                }
            }
        }
        if (line.Length > 0) Console.WriteLine(line);
        sw.Stop();
        string text = all.ToString();
        int failed = CountOccurrences(text, "FAILED");
        bool passed = finished && text.Contains("all tests passed");
        Console.WriteLine();
        Console.WriteLine($"{(finished ? "finished" : timedOut ? "TIMED OUT" : "stopped")}: {cpu.TotalCycles:N0} T-states in {sw.Elapsed.TotalSeconds:F1} s = {cpu.TotalCycles / sw.Elapsed.TotalSeconds / 1e6:F1} M T-states/s; " +
                          $"{(passed ? "all tests passed" : failed + " FAILED line(s)")}");
        return passed ? 0 : 1;

        void Ret()
        {
            ushort sp = cpu.SP;
            cpu.PC = (ushort)(mem[sp] | mem[(ushort)(sp + 1)] << 8);
            cpu.SP = (ushort)(sp + 2);
        }

        void Emit(byte c)
        {
            if (skip > 0) { skip--; if (skip == 0) { line.Append(' '); } return; }
            if (c == 23) { skip = 2; return; }                // TAB (column, ...): two parameter bytes follow
            if (c == 13) { Console.WriteLine(line.ToString()); all.Append(line).Append((char)10); line.Clear(); return; }
            if (c == 127) { line.Append("(c)"); return; }
            if (c >= 32 && c < 127) line.Append((char)c);
        }
    }

    private static int CountOccurrences(string s, string needle)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}

/// <summary>
/// <c>--bench [--seconds s] [--com file.com]</c>: how many T-states per second the core executes through <see cref="Z80{TBus}.ExecuteInstruction"/> on a flat 64 KB bus, on a synthetic
/// mix (block copy, a byte loop, indexed instructions, CALL/RET) and, with <c>--com</c>, on the first part of an exerciser. A real Z80 does 3.58 M T-states per second.
/// </summary>
internal static class Z80Bench
{
    private static readonly byte[] Mix =
    {
        0x21, 0x00, 0x40, 0x11, 0x00, 0x50, 0x01, 0x00, 0x01, 0xED, 0xB0,                       // 0100: LD HL,4000 / LD DE,5000 / LD BC,0100 / LDIR
        0x06, 0x00, 0x21, 0x00, 0x40, 0x11, 0x00, 0x50,                                         // 010B: LD B,0 / LD HL,4000 / LD DE,5000
        0x7E, 0x80, 0xA9, 0x12, 0x23, 0x13, 0x10, 0xF8,                                         // 0113: LD A,(HL) / ADD A,B / XOR C / LD (DE),A / INC HL / INC DE / DJNZ
        0xC5, 0xCD, 0x30, 0x01, 0xC1, 0xC3, 0x00, 0x01,                                         // 011B: PUSH BC / CALL 0130 / POP BC / JP 0100
    };
    private static readonly byte[] Sub = { 0xDD, 0x21, 0x00, 0x60, 0xDD, 0x7E, 0x05, 0xDD, 0x34, 0x06, 0xDD, 0xCB, 0x07, 0x5E, 0x09, 0xC9 };   // 0130

    public static int Run(string[] args)
    {
        double seconds = double.TryParse(Z80TestCli.Arg(args, "--seconds"), out double sc) ? sc : 3;
        string? com = Z80TestCli.Arg(args, "--com");
        Console.WriteLine($"== Z80 throughput ({Environment.ProcessorCount} logical CPUs, {(IntPtr.Size * 8)}-bit, .NET {Environment.Version}); a real Z80 is 3.58 M T-states/s");
        var host = new Z80Host();
        Array.Copy(Mix, 0, host.Mem, 0x100, Mix.Length);
        Array.Copy(Sub, 0, host.Mem, 0x130, Sub.Length);
        Measure("synthetic mix", host, 0x100, seconds, args);
        if (com != null)
        {
            var h2 = new Z80Host();
            var bytes = File.ReadAllBytes(com);
            Array.Copy(bytes, 0, h2.Mem, 0x100, bytes.Length);
            h2.Mem[5] = 0xC9;
            Measure(Path.GetFileName(com), h2, 0x100, seconds, args);
        }
        return 0;
    }

    private static void Measure(string name, Z80Host host, ushort entry, double seconds, string[] args)
    {
        var cpu = new Z80<FlatBus>(new FlatBus { Host = host });
        cpu.PC = entry; cpu.SP = 0xFFF0;
        // warm up (tiered JIT), then three timed rounds
        RunFor(cpu, 1.0);
        var rates = new List<double>();
        for (int r = 0; r < 3; r++)
        {
            long c0 = cpu.TotalCycles;
            var sw = Stopwatch.StartNew();
            RunFor(cpu, seconds);
            rates.Add((cpu.TotalCycles - c0) / sw.Elapsed.TotalSeconds / 1e6);
        }
        rates.Sort();
        Console.WriteLine($"  {name,-16} {rates[1],7:F1} M T-states/s (median of 3; best {rates[2]:F1}, worst {rates[0]:F1})  = {rates[1] / 3.579545:F0}x real time");
    }

    private static void RunFor(Z80<FlatBus> cpu, double seconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
            for (int i = 0; i < 200000; i++)
            {
                if (cpu.PC == 5 || cpu.PC == 0) cpu.PC = 0x100;       // keep an exerciser running if it asks for BDOS or reboots
                cpu.ExecuteInstruction();
            }
    }
}

/// <summary>
/// Hand-written checks of what the instruction vectors cannot reach: interrupt acceptance and timing (IM 0, 1, 2, NMI, the EI delay, HALT, the LD A,I quirk), the WAIT pin,
/// the I/O address bus, save states, and the documented T-state counts of a few instructions through <see cref="Z80{TBus}.ExecuteInstruction"/>.
/// The expected numbers come from the Z80 CPU User Manual (UM0080) and Sean Young's "The Undocumented Z80 Documented", not from the core.
/// </summary>
internal static class Z80SelfTest
{
    private sealed class Machine
    {
        public readonly byte[] Mem = new byte[65536];
        public byte Vector = 0xFF;
        public readonly List<string> Io = new();
        public byte PortValue = 0x5A;
        public readonly List<ushort> Fetches = new();
    }

    private struct SBus : IZ80Bus
    {
        public Machine M;
        public byte FetchOpcode(ushort a) { M.Fetches.Add(a); return M.Mem[a]; }
        public byte Read(ushort a) => M.Mem[a];
        public void Write(ushort a, byte v) => M.Mem[a] = v;
        public byte In(ushort p) { M.Io.Add($"IN {p:X4}"); return M.PortValue; }
        public void Out(ushort p, byte v) => M.Io.Add($"OUT {p:X4} {v:X2}");
        public byte InterruptVector() => M.Vector;
    }

    private static int passes, failures;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) passes++; else failures++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(!ok && detail.Length > 0 ? "  [" + detail + "]" : "")}");
    }

    private static (Z80<SBus> cpu, Machine m) New(ushort at, params byte[] code)
    {
        var m = new Machine();
        var cpu = new Z80<SBus>(new SBus { M = m });
        Array.Copy(code, 0, m.Mem, at, code.Length);
        cpu.PC = at; cpu.SP = 0x2000;
        return (cpu, m);
    }

    private static int Time(params byte[] code)
    {
        var (cpu, _) = New(0x100, code);
        cpu.BC = 0x0005; cpu.HL = 0x4000; cpu.IX = 0x4000; cpu.A = 0; cpu.F = 0;
        return cpu.ExecuteInstruction();
    }

    public static int Run(string[] args)
    {
        Console.WriteLine("== Z80 self-test");
        passes = failures = 0;

        // ---------------------------------------------------------------- reset
        {
            var (cpu, _) = New(0);
            cpu.Reset();
            Check("reset: PC, I, R, IFF, IM clear; AF and SP $FFFF", cpu.PC == 0 && cpu.I == 0 && cpu.R == 0 && !cpu.IFF1 && !cpu.IFF2 && cpu.InterruptMode == 0 && cpu.AF == 0xFFFF && cpu.SP == 0xFFFF);
        }

        {
            var (cpu, _) = New(0x100, 0x76);
            cpu.BC = 0x1234; cpu.IX = 0x4321; cpu.AF2 = 0x5566; cpu.IFF1 = cpu.IFF2 = true; cpu.InterruptMode = 2; cpu.I = 0x40;
            cpu.ExecuteInstruction();
            cpu.ResetPin();
            Check("RESET pin: PC, I, R, IFF, IM cleared and HALT left; the other registers keep their values",
                  cpu.PC == 0 && cpu.I == 0 && cpu.R == 0 && !cpu.IFF1 && !cpu.IFF2 && cpu.InterruptMode == 0 && !cpu.Halted && cpu.BC == 0x1234 && cpu.IX == 0x4321 && cpu.AF2 == 0x5566 && cpu.SP == 0x2000);
        }

        // ---------------------------------------------------------------- documented instruction timing (UM0080 table of M-cycles and T-states)
        Check("NOP 4 T", Time(0x00) == 4);
        Check("LD A,n 7 T", Time(0x3E, 1) == 7);
        Check("LD HL,nn 10 T", Time(0x21, 1, 2) == 10);
        Check("LD (HL),n 10 T", Time(0x36, 1) == 10);
        Check("INC (HL) 11 T", Time(0x34) == 11);
        Check("ADD HL,BC 11 T", Time(0x09) == 11);
        Check("PUSH BC 11 T", Time(0xC5) == 11);
        Check("POP BC 10 T", Time(0xC1) == 10);
        Check("CALL nn 17 T", Time(0xCD, 0, 2) == 17);
        Check("RET 10 T", Time(0xC9) == 10);
        Check("RST 38h 11 T", Time(0xFF) == 11);
        Check("JR e 12 T", Time(0x18, 0) == 12);
        Check("DJNZ taken (B=5) 13 T", Time(0x10, 0) == 13);
        Check("EX (SP),HL 19 T", Time(0xE3) == 19);
        Check("LD A,(nn) 13 T", Time(0x3A, 0, 0x40) == 13);
        Check("LD (nn),HL 16 T", Time(0x22, 0, 0x40) == 16);
        Check("LD (IX+d),n 19 T", Time(0xDD, 0x36, 1, 2) == 19);
        Check("INC (IX+d) 23 T", Time(0xDD, 0x34, 1) == 23);
        Check("BIT 7,(IX+d) 20 T", Time(0xDD, 0xCB, 1, 0x7E) == 20);
        Check("RLC (IX+d) 23 T", Time(0xDD, 0xCB, 1, 0x06) == 23);
        Check("SET 0,(HL) 15 T", Time(0xCB, 0xC6) == 15);
        Check("IN A,(n) 11 T", Time(0xDB, 0xFE) == 11);
        Check("OUT (n),A 11 T", Time(0xD3, 0xFE) == 11);
        Check("IN B,(C) 12 T", Time(0xED, 0x40) == 12);
        Check("LDIR with BC=5 repeats: 21 T", Time(0xED, 0xB0) == 21);
        Check("LDI 16 T", Time(0xED, 0xA0) == 16);
        Check("NEG 8 T", Time(0xED, 0x44) == 8);
        Check("LD A,I 9 T", Time(0xED, 0x57) == 9);
        Check("RLD 18 T", Time(0xED, 0x6F) == 18);
        Check("undefined ED opcode is an 8 T NOP", Time(0xED, 0x00) == 8);
        {
            var (cpu, _) = New(0x100, 0x76);
            int t = cpu.ExecuteInstruction();
            Check("HALT 4 T, PC on the next byte, Halted", t == 4 && cpu.Halted && cpu.PC == 0x101);
        }

        {
            var (cpu, m) = New(0x100);
            Array.Fill(m.Mem, (byte)0xDD);
            cpu.IFF1 = cpu.IFF2 = true; cpu.InterruptMode = 1; cpu.Irq = true;
            int t = cpu.ExecuteInstruction(), t2 = cpu.ExecuteInstruction();
            Check("memory full of $DD: each prefix is 4 T and holds interrupts off, but ExecuteInstruction still returns (after 32 prefixes) instead of trapping the board",
                  t == 131 && t2 == 128 && cpu.PC == 0x100 + 65 && !cpu.AtInstructionBoundary && cpu.SP == 0x2000, $"{t} {t2} pc={cpu.PC:X4}");
        }

        {
            // which reads are opcode fetches (M1) and which are plain reads, and what that does to the refresh counter
            var (cpu, m) = New(0x100, 0xDD, 0xCB, 0x05, 0x06, 0x3E, 0x12, 0xED, 0xB0);
            cpu.R = 0x7E; cpu.IX = 0x4000; cpu.BC = 1; cpu.HL = 0x5000; cpu.DE = 0x5100;
            cpu.ExecuteInstruction();                                             // RLC (IX+5): fetches DD, CB only; the displacement and the operation byte are plain reads
            string f1 = string.Join(",", m.Fetches.Select(a => a.ToString("X3"))); byte r1 = cpu.R; m.Fetches.Clear();
            cpu.ExecuteInstruction();                                             // LD A,n: one fetch
            string f2 = string.Join(",", m.Fetches.Select(a => a.ToString("X3"))); m.Fetches.Clear();
            cpu.ExecuteInstruction();                                             // LDI: ED, A0 ... (opcode B0 with BC=1: no repeat)
            string f3 = string.Join(",", m.Fetches.Select(a => a.ToString("X3"))); byte r3 = cpu.R;
            Check("RLC (IX+d) fetches only DD and CB as opcodes (d and the operation byte are plain reads) and R counts 2 (7-bit wrap from $7E to $00)", f1 == "100,101" && r1 == 0x00, $"{f1} r={r1:X2}");
            Check("LD A,n fetches once and LDIR (BC=1) twice; R counts every fetch", f2 == "104" && f3 == "106,107" && r3 == 0x03, $"{f2} {f3} r={r3:X2}");
        }

        // ---------------------------------------------------------------- I/O address bus
        {
            var (cpu, m) = New(0x100, 0x3E, 0x12, 0xD3, 0x34, 0xDB, 0x56, 0x06, 0x78, 0x0E, 0x9A, 0xED, 0x41, 0xED, 0x78);
            for (int i = 0; i < 7; i++) cpu.ExecuteInstruction();
            Check("OUT (n),A puts A on A15-A8; IN A,(n) the same; OUT (C),B and IN A,(C) put B there",
                  string.Join(",", m.Io) == "OUT 1234 12,IN 1256,OUT 789A 78,IN 789A", string.Join(",", m.Io));
        }

        // ---------------------------------------------------------------- interrupts
        {
            var (cpu, m) = New(0x100, 0x00, 0x00);
            cpu.IFF1 = cpu.IFF2 = true; cpu.InterruptMode = 1; cpu.Irq = true;
            int a = cpu.ExecuteInstruction(), b = cpu.ExecuteInstruction();
            Check("IM 1: NOP, then the interrupt takes 13 T, PC=$0038, return address pushed, IFF cleared, R counts the acknowledge",
                  a == 4 && b == 13 && cpu.PC == 0x38 && cpu.SP == 0x1FFE && m.Mem[0x1FFE] == 0x01 && m.Mem[0x1FFF] == 0x01 && !cpu.IFF1 && !cpu.IFF2 && cpu.R == 2,
                  $"{a} {b} pc={cpu.PC:X4} sp={cpu.SP:X4} r={cpu.R}");
            m.Mem[0x38] = 0x00;
            int c = cpu.ExecuteInstruction();
            Check("IM 1: the line is still asserted but IFF1 is clear, so the handler runs", c == 4 && cpu.PC == 0x39);
        }
        {
            var (cpu, _) = New(0x100, 0x00, 0x00);
            cpu.IFF1 = cpu.IFF2 = false; cpu.InterruptMode = 1; cpu.Irq = true;
            cpu.ExecuteInstruction(); int b = cpu.ExecuteInstruction();
            Check("IM 1: no interrupt while IFF1 is clear", b == 4 && cpu.PC == 0x102);
        }
        {
            var (cpu, m) = New(0x100, 0x00, 0x00);
            cpu.IFF1 = cpu.IFF2 = true; cpu.InterruptMode = 2; cpu.I = 0x40; m.Vector = 0x20; cpu.Irq = true;
            m.Mem[0x4020] = 0x34; m.Mem[0x4021] = 0x12;
            cpu.ExecuteInstruction(); int b = cpu.ExecuteInstruction();
            Check("IM 2: 19 T, PC from the table at I:vector, return address pushed", b == 19 && cpu.PC == 0x1234 && cpu.SP == 0x1FFE && m.Mem[0x1FFE] == 0x01 && m.Mem[0x1FFF] == 0x01, $"{b} pc={cpu.PC:X4}");
        }
        {
            var (cpu, m) = New(0x100, 0x00, 0x00);
            cpu.IFF1 = cpu.IFF2 = true; cpu.InterruptMode = 0; m.Vector = 0xFF; cpu.Irq = true;
            cpu.ExecuteInstruction(); int b = cpu.ExecuteInstruction();
            Check("IM 0 with $FF (RST 38h on the bus, as the Master System): 13 T, PC=$0038", b == 13 && cpu.PC == 0x38, $"{b} pc={cpu.PC:X4}");
        }
        {
            var (cpu, m) = New(0x100, 0x00, 0x00);
            cpu.IFF1 = cpu.IFF2 = true; cpu.InterruptMode = 0; m.Vector = 0xD7; cpu.Irq = true;
            cpu.ExecuteInstruction(); int b = cpu.ExecuteInstruction();
            Check("IM 0 with RST 10h on the bus: PC=$0010", b == 13 && cpu.PC == 0x10, $"{b} pc={cpu.PC:X4}");
        }
        {
            var (cpu, m) = New(0x100, 0x00, 0x00);
            cpu.IFF1 = false; cpu.IFF2 = true; cpu.RaiseNmi();
            cpu.ExecuteInstruction(); int b = cpu.ExecuteInstruction();
            Check("NMI: 11 T, PC=$0066, return address pushed, IFF2 keeps the old IFF1 (here 0), IFF1 cleared, even with interrupts disabled",
                  b == 11 && cpu.PC == 0x66 && cpu.SP == 0x1FFE && m.Mem[0x1FFE] == 0x01 && !cpu.IFF1 && !cpu.IFF2, $"{b} pc={cpu.PC:X4} iff={cpu.IFF1}/{cpu.IFF2}");
        }
        {
            var (cpu, m) = New(0x100, 0x00, 0x00);
            cpu.IFF1 = cpu.IFF2 = true; cpu.RaiseNmi();
            m.Mem[0x66] = 0xED; m.Mem[0x67] = 0x45;    // RETN
            cpu.ExecuteInstruction(); cpu.ExecuteInstruction();
            Check("NMI saves IFF1 in IFF2 and clears IFF1", !cpu.IFF1 && cpu.IFF2);
            int t = cpu.ExecuteInstruction();
            Check("RETN: 14 T, returns, IFF1 = IFF2", t == 14 && cpu.PC == 0x101 && cpu.IFF1, $"{t} pc={cpu.PC:X4}");
            int n = cpu.ExecuteInstruction();
            Check("NMI is edge-triggered: not taken twice", cpu.PC == 0x102 && n == 4);
        }
        {
            var (cpu, m) = New(0x100, 0xFB, 0x00, 0x00);          // EI, NOP, NOP
            cpu.InterruptMode = 1; cpu.Irq = true;
            int a = cpu.ExecuteInstruction(); bool iffAfterEi = cpu.IFF1 && cpu.IFF2;
            int b = cpu.ExecuteInstruction(); int c = cpu.ExecuteInstruction();
            Check("EI: IFF set at once, but the interrupt waits for the instruction after EI (EI 4, NOP 4, then 13)", a == 4 && iffAfterEi && b == 4 && c == 13 && cpu.PC == 0x38 && m.Mem[0x1FFE] == 0x02, $"{a} {b} {c} pc={cpu.PC:X4}");
        }
        {
            var (cpu, _) = New(0x100, 0xFB, 0xF3, 0x00);          // EI, DI, NOP
            cpu.InterruptMode = 1; cpu.Irq = true;
            cpu.ExecuteInstruction(); cpu.ExecuteInstruction(); int c = cpu.ExecuteInstruction();
            Check("EI; DI: the interrupt is never taken", c == 4 && cpu.PC == 0x103);
        }
        {
            var (cpu, m) = New(0x100, 0x76);
            cpu.IFF1 = cpu.IFF2 = true; cpu.InterruptMode = 1;
            int a = cpu.ExecuteInstruction();
            byte r0 = cpu.R;
            int b = cpu.ExecuteInstruction(), c = cpu.ExecuteInstruction();
            Check("HALT: each halted NOP cycle is 4 T and counts one refresh; PC stays after the HALT", a == 4 && b == 4 && c == 4 && cpu.PC == 0x101 && cpu.R == (byte)(r0 + 2) && cpu.Halted, $"{a} {b} {c} pc={cpu.PC:X4} r={cpu.R}");
            cpu.Irq = true;
            int d = cpu.ExecuteInstruction(), e = cpu.ExecuteInstruction();
            Check("HALT: the interrupt wakes the CPU, the pushed address is the byte after HALT", e == 13 && cpu.PC == 0x38 && !cpu.Halted && m.Mem[0x1FFE] == 0x01 && m.Mem[0x1FFF] == 0x01, $"{d} {e} pc={cpu.PC:X4}");
        }
        {
            var (cpu, m) = New(0x100, 0x76);
            cpu.IFF1 = cpu.IFF2 = false;
            cpu.ExecuteInstruction(); cpu.ExecuteInstruction();
            cpu.RaiseNmi();
            cpu.ExecuteInstruction(); int e = cpu.ExecuteInstruction();
            Check("HALT: NMI wakes the CPU even with interrupts disabled", e == 11 && cpu.PC == 0x66 && !cpu.Halted && m.Mem[0x1FFE] == 0x01);
        }
        foreach (bool bug in new[] { true, false })
        {
            var (cpu, _) = New(0x100, 0xED, 0x57, 0x00);       // LD A,I
            cpu.IFF1 = cpu.IFF2 = true; cpu.InterruptMode = 1; cpu.NmosInterruptBug = bug; cpu.Irq = true;
            cpu.ExecuteInstruction();                              // LD A,I (P/V = IFF2 = 1), and the interrupt is accepted at once, at its end
            bool pv = (cpu.F & 4) != 0;
            int t = cpu.ExecuteInstruction();
            Check($"LD A,I immediately followed by an interrupt: P/V {(bug ? "is cleared (NMOS bug)" : "stays IFF2 (CMOS)")}", t == 13 && cpu.PC == 0x38 && pv == !bug, $"pv={pv} t={t} pc={cpu.PC:X4}");
        }

        // ---------------------------------------------------------------- the WAIT pin
        {
            var (cpu, m) = New(0x100, 0x3A, 0x00, 0x40);           // LD A,(4000)
            m.Mem[0x4000] = 0x77;
            long t0 = cpu.TotalCycles; int ticks = 0, waits = 0;
            do
            {
                // hold WAIT during the first 3 ticks of every 8 (some of them land on T2 of a machine cycle, where the CPU samples it)
                cpu.Wait = ticks % 8 is 1 or 2 or 3;
                if (cpu.Wait) waits++;
                cpu.Tick(); ticks++;
            } while (!cpu.AtInstructionBoundary && ticks < 200);
            Check("WAIT held at T2 stretches the cycle and the result is unchanged", cpu.A == 0x77 && cpu.TotalCycles - t0 == ticks && ticks > 13, $"ticks={ticks} waits={waits}");
        }
        {
            var (cpu, _) = New(0x100, 0x00);
            int ticks = 1;
            cpu.Tick(); cpu.Wait = true;                            // T1 done; the CPU is about to sample WAIT in T2
            for (int i = 0; i < 5; i++) { cpu.Tick(); ticks++; }
            cpu.Wait = false;
            do { cpu.Tick(); ticks++; } while (!cpu.AtInstructionBoundary);
            Check("NOP with WAIT held for 5 T-states in T2 takes 4 + 5 T", ticks == 9, $"{ticks}");
        }

        // ---------------------------------------------------------------- state
        {
            // a small program that exercises most of the machinery, run in lockstep with a copy restored from a save taken mid-instruction
            var code = new byte[] { 0x21, 0x00, 0x40, 0x06, 0x20, 0x3E, 0x55, 0x77, 0x23, 0x86, 0x10, 0xFA, 0xDD, 0x21, 0x00, 0x41, 0xDD, 0xCB, 0x05, 0x06, 0xED, 0xB0, 0xCD, 0x30, 0x01, 0x76 };
            var (a, ma) = New(0x100, code);
            ma.Mem[0x130] = 0xC9;
            a.BC = 0x0010; a.DE = 0x4800;
            for (int i = 0; i < 237; i++) a.Tick();
            var ms = new MemoryStream(); a.SaveState(new BinaryWriter(ms));
            var (b, mb) = New(0x100, code);
            Array.Copy(ma.Mem, mb.Mem, ma.Mem.Length);
            b.LoadState(new BinaryReader(new MemoryStream(ms.ToArray())));
            bool same = true;
            for (int i = 0; i < 4000 && same; i++)
            {
                a.Tick(); b.Tick();
                same = a.PC == b.PC && a.AF == b.AF && a.BC == b.BC && a.DE == b.DE && a.HL == b.HL && a.SP == b.SP && a.IX == b.IX && a.WZ == b.WZ && a.TotalCycles == b.TotalCycles && a.R == b.R;
            }
            Check("save state taken mid-instruction, restored into a fresh CPU: both run identically for 4000 T-states", same && ma.Mem.AsSpan().SequenceEqual(mb.Mem));
        }

        Console.WriteLine($"\n{passes} passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }
}
