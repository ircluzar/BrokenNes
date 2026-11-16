using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using NesEmulator.X86;

namespace BrokenNes.Workshop.X86;

/// <summary>
/// <c>--x86sst DIR [--cpu 386|8088] [--only 00,0FA3,...] [--max N] [--show N] [--gen 3|4] [--out report.txt]</c>: runs the SingleStepTests
/// per-opcode suites (MOO files, github.com/SingleStepTests/80386 v1_ex_real_mode and /8088 v2_binary; downloaded to
/// C:\BrokenNes-tools\x86\tests, see SOURCES.md there) on the i386 core and compares the final registers and RAM with the hardware's.
/// <list type="bullet">
/// <item>386: each test is the instruction then a HLT (or a HLT the generator injected at the jump target / exception handler), and the final
/// EIP is past that HLT: one <see cref="I386.Step"/> runs the instruction (and delivers a fault), then EIP+1 stands for the HLT.
/// Undefined flags are masked with 80386.csv's f_umask; the flags word an exception pushed is masked the same way.</item>
/// <item>8088: one step, no HLT; flags compared on bits 0-11 with metadata.json's flags-mask. The core is a 386, so 8088-only behaviour
/// (PUSH SP, unmasked shift counts, the undefined-opcode aliases, 1 MB address wrap, #DE return address, ...) shows up as differences:
/// read that suite for the opcodes that are the same on both.</item>
/// </list>
/// Exit code 0 = every test run passed.
/// </summary>
internal static class X86SstCli
{
    private static readonly string[] R16 = { "ax", "bx", "cx", "dx", "cs", "ss", "ds", "es", "sp", "bp", "si", "di", "ip", "flags" };
    private static readonly string[] R32 = { "cr0", "cr3", "eax", "ebx", "ecx", "edx", "esi", "edi", "ebp", "esp", "cs", "ds", "es", "fs", "gs", "ss", "eip", "eflags", "dr6", "dr7" };

    private sealed class State
    {
        public readonly Dictionary<string, uint> Regs = new();
        public readonly Dictionary<string, uint> Masks = new();
        public readonly List<(uint a, byte v)> Ram = new();
    }

    private sealed class Test
    {
        public string Name = "";
        public byte[] Bytes = Array.Empty<byte>();
        public State Init = new(), Fina = new();
        public int Excp = -1;
        public uint ExcpFlagAddr;
        public string Hash = "";
    }

    public static int Run(string[] args)
    {
        Environment.SetEnvironmentVariable("X86_NOJIT", "1");   // one instruction per test: the block compiler would only add noise
        string dir = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : throw new ArgumentException("--x86sst DIR");
        bool is8088 = (Arg(args, "--cpu") ?? "386") == "8088";
        int gen = int.Parse(Arg(args, "--gen") ?? "3");
        int max = int.Parse(Arg(args, "--max") ?? "0"), show = int.Parse(Arg(args, "--show") ?? "3");
        var only = Arg(args, "--only")?.Split(',').Select(s => s.ToUpperInvariant()).ToHashSet();
        string? outPath = Arg(args, "--out");

        string testDir = Directory.Exists(Path.Combine(dir, "v1_ex_real_mode")) ? Path.Combine(dir, "v1_ex_real_mode")
            : Directory.Exists(Path.Combine(dir, "v2_binary")) ? Path.Combine(dir, "v2_binary") : dir;
        var revoked = File.Exists(Path.Combine(dir, "revocation_list.txt"))
            ? File.ReadAllLines(Path.Combine(dir, "revocation_list.txt")).Where(l => l.Length == 40 && !l.StartsWith('#')).Select(l => l.ToLowerInvariant()).ToHashSet()
            : new HashSet<string>();
        var umask386 = is8088 ? null : LoadCsvMasks(Path.Combine(dir, "80386.csv"));
        var meta8088 = is8088 ? LoadMeta8088(Path.Combine(dir, "v2", "metadata.json")) : null;

        var files = Directory.GetFiles(testDir, "*.MOO.gz").OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (only != null) files = files.Where(f => only.Contains(Path.GetFileName(f)[..^7].ToUpperInvariant())).ToList();

        var bus = new X86TestCli.TestBus();
        var cpu = new I386(gen, 16 * 1024 * 1024, bus);
        var report = new StringBuilder();
        long total = 0, pass = 0, skipped = 0;
        int filesFail = 0;
        var sw = Stopwatch.StartNew();
        var failsByKind = new Dictionary<string, int>();
        foreach (var f in files)
        {
            string id = Path.GetFileName(f)[..^7];
            var (tests, fileMask) = Parse(f);
            uint flagKeep = is8088 ? Flags8088(meta8088!, id) : FlagsCsv(umask386!, id);
            int n = 0, ok = 0, shown = 0;
            var kinds = new Dictionary<string, int>();
            foreach (var t in tests)
            {
                if (max > 0 && n >= max) break;
                if (revoked.Contains(t.Hash)) { skipped++; continue; }
                n++;
                string? why;
                try { why = RunOne(cpu, t, fileMask, flagKeep, is8088); }
                catch (Exception e) { why = "crash " + e.GetType().Name + ": " + e.Message; }
                if (why == null) { ok++; continue; }
                string kind = why.Split(' ')[0];
                kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                failsByKind[kind] = failsByKind.GetValueOrDefault(kind) + 1;
                if (shown++ < show) report.AppendLine($"    {id} #{n - 1} [{t.Name}] bytes {Convert.ToHexString(t.Bytes)}{(t.Excp >= 0 ? $" exc {t.Excp}" : "")}: {why}");
            }
            total += n; pass += ok;
            if (ok != n) filesFail++;
            string line = $"  {(ok == n ? "PASS" : "FAIL")} {id,-10} {ok}/{n}{(ok == n ? "" : "  " + string.Join(" ", kinds.OrderByDescending(k => k.Value).Select(k => $"{k.Key}={k.Value}")))}";
            report.Insert(0, ""); Console.WriteLine(line);
            report.AppendLine(line);
        }
        string sum = $"x86sst {(is8088 ? "8088" : "80386")} gen{gen}: files {files.Count - filesFail}/{files.Count} clean, tests {pass}/{total} pass ({100.0 * pass / Math.Max(1, total):F2}%), revoked skipped {skipped}, {sw.Elapsed.TotalSeconds:F1}s; failures by first difference: "
            + string.Join(" ", failsByKind.OrderByDescending(k => k.Value).Select(k => $"{k.Key}={k.Value}"));
        Console.WriteLine(sum);
        if (outPath != null) File.WriteAllText(outPath, report + sum + "\n");
        else if (show > 0) Console.Write(string.Join("\n", report.ToString().Split('\n').Where(l => l.StartsWith("    "))) + "\n");
        return pass == total ? 0 : 1;
    }

    // ---------------- running one test ----------------

    private static readonly int[] Seg32 = { I386.SEG_CS, I386.SEG_DS, I386.SEG_ES, I386.SEG_FS, I386.SEG_GS, I386.SEG_SS };
    private static readonly string[] Seg32Names = { "cs", "ds", "es", "fs", "gs", "ss" };
    private static readonly string[] Gpr32 = { "eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi" };   // I386.R order
    private static readonly string[] Gpr16 = { "ax", "cx", "dx", "bx", "sp", "bp", "si", "di" };

    private static string? RunOne(I386 cpu, Test t, Dictionary<string, uint> fileMask, uint flagKeep, bool is8088)
    {
        cpu.Reset();
        var ir = t.Init.Regs;
        var gpr = is8088 ? Gpr16 : Gpr32;
        for (int i = 0; i < 8; i++) cpu.R[i] = ir[gpr[i]];
        if (is8088)
        {
            foreach (var (s, nm) in new[] { (I386.SEG_ES, "es"), (I386.SEG_SS, "ss"), (I386.SEG_DS, "ds") }) cpu.LoadSeg(s, ir[nm]);
            cpu.LoadSeg(I386.SEG_FS, 0); cpu.LoadSeg(I386.SEG_GS, 0);
            cpu.SetCsIp(ir["cs"], ir["ip"]);
            cpu.Flags = (ir["flags"] & 0x0fd5) | 2;
        }
        else
        {
            for (int k = 0; k < 6; k++) if (Seg32[k] != I386.SEG_CS) cpu.LoadSeg(Seg32[k], ir[Seg32Names[k]]);
            cpu.SetCsIp(ir["cs"], ir["eip"]);
            cpu.Flags = (ir["eflags"] & cpu.FlagsMask) | 2;
        }
        foreach (var (a, v) in t.Init.Ram) cpu.Mem[a] = v;

        long c0 = cpu.Cycle;
        cpu.Step(1);
        string? why = null;
        uint ip = cpu.NextIp;
        if (!is8088 && !cpu.Halt) ip += 1;               // the HLT that ends every 386 test (already run when it sat in a MOV/POP SS shadow)

        // registers
        var fr = t.Fina.Regs;
        uint Exp(string n) => fr.TryGetValue(n, out var v) ? v : ir[n];
        uint Msk(string n) => t.Fina.Masks.TryGetValue(n, out var m) ? m : fileMask.TryGetValue(n, out var m2) ? m2 : 0xffffffff;
        for (int i = 0; i < 8 && why == null; i++)
        {
            uint ours = is8088 ? cpu.R[i] & 0xffff : cpu.R[i], exp = Exp(gpr[i]), m = Msk(gpr[i]);
            if ((ours & m) != (exp & m)) why = $"{gpr[i]} {ours:X} expected {exp:X}";
        }
        var segs = is8088 ? new[] { (I386.SEG_CS, "cs"), (I386.SEG_SS, "ss"), (I386.SEG_DS, "ds"), (I386.SEG_ES, "es") } : Seg32.Zip(Seg32Names).ToArray();
        foreach (var (s, nm) in segs)
            if (why == null && (cpu.SegSel[s] & 0xffff) != (Exp(nm) & 0xffff)) why = $"seg-{nm} {cpu.SegSel[s]:X4} expected {Exp(nm) & 0xffff:X4}";
        string ipn = is8088 ? "ip" : "eip";
        if (why == null && (is8088 ? ip & 0xffff : ip) != Exp(ipn)) why = $"ip {ip:X} expected {Exp(ipn):X}";
        string fn = is8088 ? "flags" : "eflags";
        uint fm = flagKeep & Msk(fn) & (is8088 ? 0x0fd5u : 0x37fd5u);
        uint fl = cpu.GetFlags();
        if (why == null && (fl & fm) != (Exp(fn) & fm)) why = $"flags {fl & fm:X} expected {Exp(fn) & fm:X} (diff {(fl ^ Exp(fn)) & fm:X})";

        // memory
        if (why == null)
            foreach (var (a, v) in t.Fina.Ram)
            {
                byte ours = cpu.Mem[a];
                byte m = 0xff;
                if (t.Excp >= 0 && (a == t.ExcpFlagAddr || a == t.ExcpFlagAddr + 1))
                    m = (byte)((a == t.ExcpFlagAddr ? fm : fm >> 8) & 0xff);
                if ((ours & m) != (v & m)) { why = $"mem [{a:X}] {ours:X2} expected {v:X2}" + (t.Excp >= 0 && (a == t.ExcpFlagAddr || a == t.ExcpFlagAddr + 1) ? " (pushed flags)" : ""); break; }
            }
        if (why == null && cpu.Cycle - c0 != (cpu.Halt ? 2 : 1)) why = $"steps ran {cpu.Cycle - c0} instructions";

        // clear what the test touched (the next test writes its own bytes; RAM is otherwise left as is to stay fast)
        foreach (var (a, _) in t.Init.Ram) cpu.Mem[a] = 0;
        foreach (var (a, _) in t.Fina.Ram) cpu.Mem[a] = 0;
        return why;
    }

    // ---------------- masks ----------------

    private static Dictionary<string, uint> LoadCsvMasks(string path)
    {
        var d = new Dictionary<string, uint>();
        var lines = File.ReadAllLines(path);
        var hdr = lines[0].Split(',');
        int iop = Array.IndexOf(hdr, "op"), iex = Array.IndexOf(hdr, "ex"), ium = Array.IndexOf(hdr, "f_umask");
        foreach (var l in lines.Skip(1))
        {
            var c = l.Split(',');
            if (c.Length <= ium || c[ium].Length == 0) continue;
            string key = c[iop].ToUpperInvariant() + (c[iex].Length > 0 ? "." + c[iex] : "");
            d[key] = Convert.ToUInt32(c[ium], 16);
        }
        return d;
    }

    private static string StripPrefixes(string id)
    {
        while (id.Length > 2 && (id.StartsWith("66") || id.StartsWith("67")) && id.Split('.')[0].Length > 2) id = id[2..];
        return id;
    }

    private static uint FlagsCsv(Dictionary<string, uint> m, string id)
    {
        string op = StripPrefixes(id.ToUpperInvariant());
        if (m.TryGetValue(op, out var v)) return v | 0xffff0000;
        string baseOp = op.Split('.')[0];
        if (m.TryGetValue(baseOp, out v)) return v | 0xffff0000;
        return 0xffffffff;
    }

    private static Dictionary<string, uint> LoadMeta8088(string path)
    {
        var d = new Dictionary<string, uint>();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var op in doc.RootElement.GetProperty("opcodes").EnumerateObject())
        {
            if (op.Value.TryGetProperty("flags-mask", out var fm)) d[op.Name.ToUpperInvariant()] = fm.GetUInt32();
            if (op.Value.TryGetProperty("reg", out var reg))
                foreach (var r in reg.EnumerateObject())
                    if (r.Value.TryGetProperty("flags-mask", out var fm2)) d[op.Name.ToUpperInvariant() + "." + r.Name] = fm2.GetUInt32();
        }
        return d;
    }

    private static uint Flags8088(Dictionary<string, uint> m, string id)
        => m.TryGetValue(id.ToUpperInvariant(), out var v) ? v : m.TryGetValue(id.ToUpperInvariant().Split('.')[0], out v) ? v : 0xffff;

    // ---------------- MOO parsing ----------------

    private static (List<Test>, Dictionary<string, uint>) Parse(string path)
    {
        byte[] d;
        using (var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
        using (var ms = new MemoryStream()) { gz.CopyTo(ms); d = ms.ToArray(); }
        var tests = new List<Test>();
        var fileMask = new Dictionary<string, uint>();
        foreach (var (t, s, e) in Chunks(d, 0, d.Length))
        {
            if (t == "RMSK") ReadRegs(d, s, 2, R16, fileMask);
            else if (t == "RM32") ReadRegs(d, s, 4, R32, fileMask);
            if (t != "TEST") continue;
            var test = new Test();
            foreach (var (t2, s2, e2) in Chunks(d, s + 4, e))
            {
                switch (t2)
                {
                    case "NAME": test.Name = Encoding.ASCII.GetString(d, s2 + 4, (int)BitConverter.ToUInt32(d, s2)); break;
                    case "BYTS": test.Bytes = d[(s2 + 4)..(s2 + 4 + (int)BitConverter.ToUInt32(d, s2))]; break;
                    case "INIT": case "FINA":
                        var st = t2 == "INIT" ? test.Init : test.Fina;
                        foreach (var (t3, s3, e3) in Chunks(d, s2, e2))
                        {
                            if (t3 == "REGS") ReadRegs(d, s3, 2, R16, st.Regs);
                            else if (t3 == "RG32") ReadRegs(d, s3, 4, R32, st.Regs);
                            else if (t3 == "RMSK") ReadRegs(d, s3, 2, R16, st.Masks);
                            else if (t3 == "RM32") ReadRegs(d, s3, 4, R32, st.Masks);
                            else if (t3 == "RAM ")
                            {
                                uint c = BitConverter.ToUInt32(d, s3);
                                for (int i = 0; i < c; i++) st.Ram.Add((BitConverter.ToUInt32(d, s3 + 4 + 5 * i), d[s3 + 8 + 5 * i]));
                            }
                        }
                        break;
                    case "EXCP": test.Excp = d[s2]; test.ExcpFlagAddr = BitConverter.ToUInt32(d, s2 + 1); break;
                    case "HASH": test.Hash = Convert.ToHexString(d, s2, 20).ToLowerInvariant(); break;
                }
            }
            tests.Add(test);
        }
        return (tests, fileMask);
    }

    private static void ReadRegs(byte[] d, int s, int w, string[] names, Dictionary<string, uint> into)
    {
        uint m = w == 4 ? BitConverter.ToUInt32(d, s) : BitConverter.ToUInt16(d, s);
        int o = s + w;
        for (int i = 0; i < names.Length; i++)
            if ((m >> i & 1) != 0) { into[names[i]] = w == 4 ? BitConverter.ToUInt32(d, o) : BitConverter.ToUInt16(d, o); o += w; }
    }

    private static IEnumerable<(string t, int s, int e)> Chunks(byte[] d, int o, int end)
    {
        while (o + 8 <= end)
        {
            string t = Encoding.ASCII.GetString(d, o, 4);
            int l = (int)BitConverter.ToUInt32(d, o + 4);
            yield return (t, o + 8, Math.Min(end, o + 8 + l));
            o += 8 + l;
        }
    }

    private static string? Arg(string[] a, string k) { int i = Array.FindIndex(a, x => x.Equals(k, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
}
