using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NesEmulator.Sega;

namespace BrokenNes.Workshop.Sega;

/// <summary>The SingleStepTests runner behind <c>--m68ktest</c>: loads a vector file, runs every vector on a fresh core state, compares, and applies the documented exclusions and tolerances.</summary>
internal static class M68kVectors
{
    private sealed class Options
    {
        public bool NoExclusions, NoTolerances;
        public int Poison;
        public HashSet<string> Ignore = new();
    }

    private sealed class FileResult
    {
        public string Set = "", Name = "";
        public int Tests, Passed, Failed;
        public readonly SortedDictionary<Exclusion, int> Excluded = new();
        public readonly SortedDictionary<Exclusion, int> ExcludedPassing = new();    // excluded vectors that the core passes anyway
        public readonly SortedDictionary<Tolerance, int> Tolerated = new();
        public long Cycles;
        public readonly SortedDictionary<string, int> Tags = new();
        public readonly List<string> Details = new();
        public double Seconds;
        public int Excl => Excluded.Values.Sum();
    }

    public static int Run(string[] args)
    {
        string set = M68kTestCli.Arg(args, "--set") ?? "both";
        string dir680 = M68kTestCli.Arg(args, "--dir680") ?? M68kTestCli.DefaultDir680;
        string dirMame = M68kTestCli.Arg(args, "--dirmame") ?? M68kTestCli.DefaultDirMame;
        string? only = M68kTestCli.Arg(args, "--only");
        int maxFailures = int.TryParse(M68kTestCli.Arg(args, "--max-failures"), out int mf) ? mf : 10;
        int jobs = int.TryParse(M68kTestCli.Arg(args, "--jobs"), out int j) ? Math.Max(1, j) : 3;
        string? json = M68kTestCli.Arg(args, "--json");
        var opt = new Options
        {
            Poison = int.TryParse(M68kTestCli.Arg(args, "--poison"), out int po) ? po : 0,
            NoExclusions = M68kTestCli.Flag(args, "--no-exclusions"),
            NoTolerances = M68kTestCli.Flag(args, "--no-tolerances"),
            Ignore = new HashSet<string>((M68kTestCli.Arg(args, "--ignore") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
        };

        var work = new List<(string set, string path)>();
        if (set is "680x0" or "both") work.AddRange(Directory.GetFiles(dir680, "*.json.gz").OrderBy(f => f, StringComparer.Ordinal).Select(f => ("680x0", f)));
        if (set is "m68000" or "both") work.AddRange(Directory.GetFiles(dirMame, "*.json.bin").OrderBy(f => f, StringComparer.Ordinal).Select(f => ("m68000", f)));
        if (only != null) work = work.Where(w => Path.GetFileName(w.path).Contains(only, StringComparison.OrdinalIgnoreCase)).ToList();
        if (work.Count == 0) { Console.WriteLine("no vector files found"); return 2; }
        Console.WriteLine($"== SingleStepTests: {work.Count} file(s) ({set}), jobs={jobs}" + (opt.Poison > 0 ? $", POISON every {opt.Poison}th test" : "") + (opt.NoExclusions ? ", NO EXCLUSIONS" : "") + (opt.NoTolerances ? ", NO TOLERANCES" : ""));

        var results = new FileResult[work.Count];
        int printed = 0;
        object gate = new();
        var sw = Stopwatch.StartNew();
        Parallel.For(0, work.Count, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i =>
        {
            var r = RunFile(work[i].set, work[i].path, opt);
            results[i] = r;
            lock (gate)
            {
                Console.WriteLine($"  {(r.Failed == 0 ? "PASS" : "FAIL")}  {r.Set,-6} {r.Name,-14} passed {r.Passed,5}  excluded {r.Excl,5}  failed {r.Failed,5}" +
                                  (r.Excl > 0 ? "  [excluded " + string.Join(", ", r.Excluded.Select(e => $"{e.Key}:{e.Value}")) + "]" : "") +
                                  (r.Tolerated.Count > 0 ? "  [tolerated " + string.Join(", ", r.Tolerated.Select(e => $"{e.Key}:{e.Value}")) + "]" : "") +
                                  (r.Failed > 0 ? "  fails: " + string.Join(" ", r.Tags.Select(t => $"{t.Key}:{t.Value}")) : ""));
                foreach (var d in r.Details)
                {
                    if (printed++ >= maxFailures) break;
                    Console.WriteLine(d);
                }
            }
        });
        sw.Stop();

        int tests = results.Sum(r => r.Tests), passed = results.Sum(r => r.Passed), failed = results.Sum(r => r.Failed), excluded = results.Sum(r => r.Excl);
        foreach (var s in new[] { "680x0", "m68000" })
        {
            var rs = results.Where(r => r.Set == s).ToList();
            if (rs.Count == 0) continue;
            Console.WriteLine($"{s,-7}: {rs.Count} files, {rs.Sum(r => r.Tests):N0} vectors: passed {rs.Sum(r => r.Passed):N0}, excluded {rs.Sum(r => r.Excl):N0}, failed {rs.Sum(r => r.Failed):N0}; files with failures: {rs.Count(r => r.Failed > 0)}");
        }
        var excl = new SortedDictionary<Exclusion, int>();
        foreach (var r in results) foreach (var e in r.Excluded) excl[e.Key] = excl.GetValueOrDefault(e.Key) + e.Value;
        var exclPass = new SortedDictionary<Exclusion, int>();
        foreach (var r in results) foreach (var e in r.ExcludedPassing) exclPass[e.Key] = exclPass.GetValueOrDefault(e.Key) + e.Value;
        foreach (var e in excl) Console.WriteLine($"excluded {e.Value,7:N0}  {e.Key} ({exclPass.GetValueOrDefault(e.Key):N0} of them pass anyway): {Describe(e.Key)}");
        var tol = new SortedDictionary<Tolerance, int>();
        foreach (var r in results) foreach (var e in r.Tolerated) tol[e.Key] = tol.GetValueOrDefault(e.Key) + e.Value;
        foreach (var e in tol) Console.WriteLine($"tolerated {e.Value,6:N0}  {e.Key}: {Describe(e.Key)}");
        var tags = new SortedDictionary<string, int>();
        foreach (var r in results) foreach (var t in r.Tags) tags[t.Key] = tags.GetValueOrDefault(t.Key) + t.Value;
        if (tags.Count > 0) Console.WriteLine("failures by kind (a vector can fail several): " + string.Join(", ", tags.Select(t => $"{t.Key}={t.Value}")));
        Console.WriteLine($"TOTAL: {tests:N0} vectors: passed {passed:N0}, excluded {excluded:N0}, failed {failed:N0}   ({results.Sum(r => r.Cycles):N0} 68000 clocks executed, {sw.Elapsed.TotalSeconds:F1} s wall)");

        if (json != null)
        {
            using var fs = File.Create(json);
            using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
            w.WriteStartObject();
            w.WriteNumber("vectors", tests); w.WriteNumber("passed", passed); w.WriteNumber("excluded", excluded); w.WriteNumber("failed", failed);
            w.WriteStartArray("files");
            foreach (var r in results)
            {
                w.WriteStartObject();
                w.WriteString("set", r.Set); w.WriteString("file", r.Name); w.WriteNumber("vectors", r.Tests); w.WriteNumber("passed", r.Passed); w.WriteNumber("excluded", r.Excl); w.WriteNumber("failed", r.Failed);
                w.WriteStartObject("excludedBy"); foreach (var e in r.Excluded) w.WriteNumber(e.Key.ToString(), e.Value); w.WriteEndObject();
                w.WriteStartObject("toleratedBy"); foreach (var e in r.Tolerated) w.WriteNumber(e.Key.ToString(), e.Value); w.WriteEndObject();
                w.WriteStartObject("kinds"); foreach (var t in r.Tags) w.WriteNumber(t.Key, t.Value); w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return failed == 0 ? 0 : 1;
    }

    public static string Describe(Exclusion e) => e switch
    {
        Exclusion.RteRtrPopOrder => "RTE and RTR read the PC before the status word in the 680x0 vectors, status first in the MAME vectors (the core follows MAME)",
        Exclusion.LinkA7 => "LINK A7 pushes the decremented stack pointer in the 680x0 vectors, the original in the MAME vectors (the core follows MAME)",
        Exclusion.ChkSequence => "CHK: the sets disagree on the fetch before the exception, the branch conditions and the flags (the core follows MAME)",
        Exclusion.DivOverflow => "DIVS/DIVU overflow: the sets disagree on the early exit and on N and Z (the core follows MAME)",
        Exclusion.DivZero => "the one DIVU by zero stacks the instruction's own address (contradicts the manual and the rest of the set)",
        Exclusion.QuickLongToAn => "ADDQ.L/SUBQ.L to An: 6 clocks in the 680x0 vectors, 8 in the MAME vectors and the manual",
        Exclusion.AsrCountPastWidth => "ASR by a count above the width on a negative value: C and X cleared in the 680x0 vectors, set in the MAME vectors and the manual",
        Exclusion.CorruptVector => "ASL.B vectors with a final register no ASL.B can produce",
        _ => "",
    };

    public static string Describe(Tolerance t) => t switch
    {
        Tolerance.AddressErrorStackedPc => "MAME address errors: the PC word of the frame",
        Tolerance.AddressErrorAddressRegisters => "address errors: the address registers' values after the aborted access",
        Tolerance.AddressErrorInstructionWordAndSpace => "680x0 address errors: the frame's instruction word and the function code of PC-relative operands",
        Tolerance.AddressErrorMoveLFlags => "MAME MOVE.L address errors: the flag bits of the stacked SR",
        Tolerance.DbccCounter => "680x0 DBcc address errors: the counter register",
        Tolerance.AddressErrorEightClockGap => "MAME address errors: eight idle clocks after the aborted access (removed from the expected log)",
        Tolerance.AddressErrorStatusInBit => "680x0 address errors: the I/N bit of the status word (cleared in the expected frame)",
        _ => "",
    };

    private static FileResult RunFile(string set, string path, Options opt)
    {
        string name = Path.GetFileName(path);
        name = name.Substring(0, name.IndexOf(".json", StringComparison.Ordinal));
        var res = new FileResult { Set = set, Name = name };
        var sw = Stopwatch.StartNew();
        var tests = set == "680x0" ? VecLoaders.Load680x0(path) : VecLoaders.LoadMame(path);
        var m = new VecMachine();
        var cpu = new M68k<VecBus>(new VecBus { M = m });
        int index = 0;
        foreach (var t in tests)
        {
            index++;
            res.Tests++;
            var excl = opt.NoExclusions || set != "680x0" ? Exclusion.None : Classify(name, t);
            if (excl != Exclusion.None)
            {
                // an excluded vector is still run, so the report says how many of the excluded class the core passes anyway
                res.Excluded[excl] = res.Excluded.GetValueOrDefault(excl) + 1;
                var scratch = new FileResult();
                if (RunTest(set, name, cpu, m, t, scratch, opt, false) == null) res.ExcludedPassing[excl] = res.ExcludedPassing.GetValueOrDefault(excl) + 1;
                continue;
            }
            string? err = RunTest(set, name, cpu, m, t, res, opt, opt.Poison > 0 && index % opt.Poison == 0);
            if (err == null) res.Passed++;
            else
            {
                res.Failed++;
                if (res.Details.Count < 4) res.Details.Add(err);
            }
        }
        sw.Stop();
        res.Seconds = sw.Elapsed.TotalSeconds;
        return res;
    }

    private static readonly Regex QuickToAn = new(@"^[0-9a-f]{4} \[(ADD|SUB)\.l Q, A[0-7]\]", RegexOptions.Compiled);
    private static readonly Regex AsrRegisterCount = new(@"^([0-9a-f]{4}) \[ASR\.([bwl]) D([0-7]), D([0-7])\]", RegexOptions.Compiled);

    /// <summary>The 680x0 vectors the core is not held to (each class is contradicted by the MAME set, which the core passes in full).</summary>
    private static Exclusion Classify(string file, VecTest t)
    {
        if (file is "RTE" or "RTR") return Exclusion.RteRtrPopOrder;
        if (t.Name.Contains("[LINK A7,", StringComparison.Ordinal)) return Exclusion.LinkA7;
        if (file == "CHK" && !t.AddressError) return Exclusion.ChkSequence;
        if (file is "DIVS" or "DIVU" && !t.AddressError)
        {
            if (t.Tx.Any(x => x.Kind == 'r' && !x.Byte && x.Addr == 0x14)) return Exclusion.DivZero;
            if ((t.Final.Sr & 2) != 0) return Exclusion.DivOverflow;
        }
        if (file is "ADD.l" or "SUB.l" && QuickToAn.IsMatch(t.Name)) return Exclusion.QuickLongToAn;
        if (file is "ASL.b" && (t.Name == "e502 [ASL.b Q, D2] 1583" || t.Name == "e502 [ASL.b Q, D2] 1761")) return Exclusion.CorruptVector;
        if (file.StartsWith("ASR.", StringComparison.Ordinal) && AsrRegisterCount.Match(t.Name) is { Success: true } m)
        {
            int bits = m.Groups[2].Value switch { "b" => 8, "w" => 16, _ => 32 };
            uint count = t.Init.D[m.Groups[3].Value[0] - '0'] & 63;
            uint value = t.Init.D[m.Groups[4].Value[0] - '0'];
            bool negative = (value >> (bits - 1) & 1) != 0;
            if (count > bits && negative) return Exclusion.AsrCountPastWidth;
        }
        return Exclusion.None;
    }

    private static string? RunTest(string set, string file, M68k<VecBus> cpu, VecMachine m, VecTest t, FileResult res, Options opt, bool poison)
    {
        m.Clear();
        m.IrqLevel = 0;
        var ini = t.Init;

        // ---- set up
        cpu.Power();
        for (int i = 0; i < 8; i++) cpu.D[i] = ini.D[i];
        for (int i = 0; i < 7; i++) cpu.A[i] = ini.A[i];
        bool s = (ini.Sr & 0x2000) != 0;
        cpu.A[7] = s ? ini.Ssp : ini.Usp;
        cpu.SpOther = s ? ini.Usp : ini.Ssp;
        cpu.Supervisor = s;
        cpu.WriteCcr(ini.Sr);
        cpu.IntMask = (int)(ini.Sr >> 8) & 7;
        cpu.Trace = (ini.Sr & 0x8000) != 0;
        cpu.Pc = set == "680x0" ? ini.Pc + 4 : ini.Pc;
        cpu.Ir = (ushort)ini.Pf0; cpu.Irc = (ushort)ini.Pf1;
        foreach (var (a, v) in ini.Ram) { m.Ram[a] = v; m.Touched.Add(a); }

        // ---- run one instruction
        int cycles = cpu.Step();
        res.Cycles += cycles;

        // ---- compare
        var fin = t.Final;
        var diffs = new List<string>();
        var kinds = new HashSet<string>();
        void Tag(string tag, string detail) { if (!opt.Ignore.Contains(tag)) { kinds.Add(tag); diffs.Add(detail); } }
        var tolerated = new HashSet<Tolerance>();
        bool tol = !opt.NoTolerances;
        bool ae = t.AddressError;
        void Tolerate(Tolerance k) => tolerated.Add(k);

        for (int i = 0; i < 8; i++)
        {
            uint want = fin.D[i] ^ (poison && i == 0 ? 1u : 0u);
            if (want == cpu.D[i]) continue;
            if (tol && ae && set == "680x0" && file == "DBcc") { Tolerate(Tolerance.DbccCounter); continue; }
            Tag("regs", $"d{i}: want {want:X8} got {cpu.D[i]:X8}");
        }
        for (int i = 0; i < 7; i++)
        {
            if (fin.A[i] == cpu.A[i]) continue;
            if (tol && ae) { Tolerate(Tolerance.AddressErrorAddressRegisters); continue; }
            Tag("regs", $"a{i}: want {fin.A[i]:X8} got {cpu.A[i]:X8}");
        }
        if (fin.Usp != cpu.Usp) Tag("regs", $"usp: want {fin.Usp:X8} got {cpu.Usp:X8}");
        if (fin.Ssp != cpu.Ssp) Tag("regs", $"ssp: want {fin.Ssp:X8} got {cpu.Ssp:X8}");
        ushort sr = cpu.ReadSr();
        uint srMask = 0xFFFFu;
        if (tol && ae && set == "m68000" && file == "MOVE.l") srMask = 0xFFF0u;
        if ((fin.Sr & srMask) != (sr & srMask)) Tag("sr", $"sr: want {fin.Sr:X4} got {sr:X4} (xor {fin.Sr ^ sr:X4}; {Flags(fin.Sr)} vs {Flags(sr)})");
        else if (fin.Sr != sr) Tolerate(Tolerance.AddressErrorMoveLFlags);
        uint pc = set == "680x0" ? cpu.Pc - 4 : cpu.Pc;
        if (fin.Pc != pc) Tag("pc", $"pc: want {fin.Pc:X6} got {pc:X6}");
        if (fin.Pf0 != cpu.Ir || fin.Pf1 != cpu.Irc) Tag("prefetch", $"prefetch: want {fin.Pf0:X4} {fin.Pf1:X4} got {cpu.Ir:X4} {cpu.Irc:X4}");
        if (cycles != t.Length) Tag("cycles", $"clocks: want {t.Length} got {cycles}");

        var got = Normalise(m.Log, expectTas: t.Tx.Any(x => x.Kind == 't'));
        var want2 = Normalise(t.Tx, false);

        // address-error frames: apply the field tolerances to the expected side, and leave the frame's bytes to the bus log (which holds the same writes)
        var frame = ae ? FrameRange(fin) : (lo: 0, hi: 0);
        if (tol && ae) ApplyFrameTolerances(set, file, t, want2, got, tolerated);
        foreach (var (a, v) in fin.Ram)
        {
            if (ae && a >= frame.lo && a < frame.hi) continue;
            if (m.Ram[a] != v) Tag("ram", $"ram[{a:X6}]: want {v:X2} got {m.Ram[a]:X2}");
        }
        string? txDiff = CompareTx(want2, got, set);
        if (txDiff != null) Tag("bus", txDiff);

        if (t.EightClockGap && tol) Tolerate(Tolerance.AddressErrorEightClockGap);
        if (t.StatusInBitCleared && tol) Tolerate(Tolerance.AddressErrorStatusInBit);

        if (diffs.Count == 0)
        {
            foreach (var k in tolerated) res.Tolerated[k] = res.Tolerated.GetValueOrDefault(k) + 1;
            return null;
        }
        foreach (var k in kinds) res.Tags[k] = res.Tags.GetValueOrDefault(k) + 1;
        var sb = new StringBuilder();
        sb.Append($"    {t.Name}: ");
        sb.Append(string.Join("; ", diffs.Take(6)));
        if (diffs.Count > 6) sb.Append($"; (+{diffs.Count - 6} more)");
        sb.Append($"\n        expected bus: {string.Join(" ", want2.Select(x => x.ToString()))}");
        sb.Append($"\n        actual bus:   {string.Join(" ", got.Select(x => x.ToString()))}");
        return sb.ToString();
    }

    /// <summary>The bytes of the 14-byte frame an address error leaves at the end of the supervisor stack.</summary>
    private static (int lo, int hi) FrameRange(VecState fin) => ((int)fin.Ssp, (int)fin.Ssp + 14);

    /// <summary>
    /// The fields of an address-error frame the vector sets and the chip do not agree on, made equal on the expected side before comparing (each use is counted). The frame is the seven
    /// writes just before the read of vector 3: PC low, SR, PC high, the instruction word, the faulting address low and high, and the status word at its lowest address.
    /// </summary>
    private static void ApplyFrameTolerances(string set, string file, VecTest t, List<VecTx> want, List<VecTx> got, HashSet<Tolerance> used)
    {
        int k = want.FindIndex(x => x.Kind == 'r' && x.Addr == 0x0C);
        int g = got.FindIndex(x => x.Kind == 'r' && x.Addr == 0x0C);
        if (k < 7 || g < 7) return;
        // order of the writes in the log: PC low, SR, PC high, instruction word, address low, status, address high
        const int PcLow = 7, Sr = 6, PcHigh = 5, Ir = 4, Status = 2;
        bool Pair(int off, out VecTx w, out VecTx a)
        {
            w = want[k - off]; a = got[g - off];
            return w.Kind == 'w' && a.Kind == 'w' && w.Addr == a.Addr;
        }
        void Set(int off, VecTx w) => want[k - off] = w;

        if (set == "m68000")
        {
            foreach (int off in new[] { PcLow, PcHigh })
                if (Pair(off, out var w, out var a) && w.Data != a.Data) { w.Data = a.Data; Set(off, w); used.Add(Tolerance.AddressErrorStackedPc); }
            if (file == "MOVE.l" && Pair(Sr, out var ws, out var asr) && (ws.Data & 0x0F) != (asr.Data & 0x0F)) { ws.Data = ws.Data & ~0x0F | asr.Data & 0x0F; Set(Sr, ws); used.Add(Tolerance.AddressErrorMoveLFlags); }
        }
        else
        {
            if (Pair(Ir, out var wi, out var ai) && wi.Data != ai.Data) { wi.Data = ai.Data; Set(Ir, wi); used.Add(Tolerance.AddressErrorInstructionWordAndSpace); }
            if (Pair(Status, out var wst, out var ast))
            {
                int newData = wst.Data;
                if ((wst.Data & 0xFFE0) != (ast.Data & 0xFFE0)) { newData = newData & 0x1F | ast.Data & 0xFFE0; used.Add(Tolerance.AddressErrorInstructionWordAndSpace); }
                // a PC-relative operand is read in data space in these vectors and in program space on the chip (and in the core)
                bool pcRelative = t.Name.Contains(", PC", StringComparison.Ordinal) || t.Name.Contains("(d16, PC)", StringComparison.Ordinal);
                if (pcRelative && (newData & 0x1F) != (ast.Data & 0x1F) && ((newData & 0x10) == (ast.Data & 0x10)) && ((newData & 7) is 1 or 5) && ((ast.Data & 7) == (newData & 7) + 1))
                {
                    newData = newData & ~7 | ast.Data & 7;
                    used.Add(Tolerance.AddressErrorInstructionWordAndSpace);
                }
                if (newData != wst.Data) { wst.Data = newData; Set(Status, wst); }
            }
        }
    }

    private static string Flags(uint sr) => $"{((sr & 0x10) != 0 ? 'X' : '-')}{((sr & 8) != 0 ? 'N' : '-')}{((sr & 4) != 0 ? 'Z' : '-')}{((sr & 2) != 0 ? 'V' : '-')}{((sr & 1) != 0 ? 'C' : '-')}";

    /// <summary>Merge runs of idle cycles; if the expected side uses TAS's single locked cycle, fold read + 2 idle + write into it.</summary>
    private static List<VecTx> Normalise(List<VecTx> log, bool expectTas)
    {
        var o = new List<VecTx>();
        foreach (var x in log)
        {
            if (x.Kind == 'n' && o.Count > 0 && o[^1].Kind == 'n') { var l = o[^1]; l.Cycles += x.Cycles; o[^1] = l; }
            else o.Add(x);
        }
        if (expectTas)
        {
            for (int i = 0; i + 2 < o.Count; i++)
                if (o[i].Kind == 'r' && o[i].Byte && o[i + 1].Kind == 'n' && o[i + 1].Cycles == 2 && o[i + 2].Kind == 'w' && o[i + 2].Byte && o[i].Addr == o[i + 2].Addr)
                {
                    var t = o[i + 2]; t.Kind = 't'; t.Cycles = 10;
                    o[i] = t; o.RemoveRange(i + 1, 2);
                }
        }
        return o;
    }

    private static string? CompareTx(List<VecTx> want, List<VecTx> got, string set)
    {
        int n = Math.Min(want.Count, got.Count);
        for (int i = 0; i < n; i++)
        {
            var w = want[i]; var g = got[i];
            if (w.Kind != g.Kind) return $"bus[{i}]: want {w} got {g}";
            if (w.Cycles != g.Cycles) return $"bus[{i}]: want {w} got {g}";
            if (w.Kind == 'n') continue;
            if (set == "m68000")
            {
                // the MAME set records the bus as the chip drives it: the word address (A0 is not a pin), the two data strobes, and the lane's byte in place on the 16-bit data bus
                if ((w.Addr & ~1u) != (g.Addr & ~1u) || w.Upper != g.Upper || w.Lower != g.Lower) return $"bus[{i}]: want {w} (uds={w.Upper} lds={w.Lower}) got {g} (uds={g.Upper} lds={g.Lower})";
                int wd = w.Byte ? (w.Upper ? w.Data >> 8 & 0xFF : w.Data & 0xFF) : w.Data;
                if (wd != g.Data) return $"bus[{i}]: want {w} got {g}";
                continue;
            }
            // SingleStepTests/680x0: a byte cycle carries the byte address
            if (w.Addr != g.Addr) return $"bus[{i}]: want {w} got {g}";
            if (w.Byte != g.Byte) return $"bus[{i}]: want {w} got {g}";
            if (w.Data != g.Data) return $"bus[{i}]: want {w} got {g}";
        }
        if (want.Count != got.Count) return $"bus: want {want.Count} transactions, got {got.Count}";
        return null;
    }
}
