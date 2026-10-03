using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--smstest [--only suite[,suite]] [--tools dir] [--verbose] [--tune name=value,...] [--poison what]</c>: the Master System / Game Gear verifier.
/// See <see cref="SmsSuites"/> for the suites and how each one reads its result.
/// </summary>
internal static class SmsTestCli
{
    public static string ToolsRoot(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++) if (args[i].Equals("--tools", StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return Environment.GetEnvironmentVariable("BROKENNES_SEGA_TOOLS") is { Length: > 0 } e ? e : @"C:\BrokenNes-tools\sega";
    }

    public static string? Opt(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => a.Equals("--" + name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static int Run(string[] args)
    {
        var ctx = new SmsSuiteContext
        {
            Tools = ToolsRoot(args),
            Verbose = args.Any(a => a.Equals("--verbose", StringComparison.OrdinalIgnoreCase)),
            Tune = (Opt(args, "tune") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=')).ToDictionary(x => x[0], x => int.Parse(x[1])),
            Poison = Opt(args, "poison") ?? "",
        };
        var only = (Opt(args, "only") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Opt(args, "scan") is { } scan) return Vdptest.Scan(ctx, scan);
        int pass = 0, fail = 0, skipped = 0;
        foreach (var suite in SmsSuites.All)
        {
            if (only.Count > 0 && !only.Contains(suite.Id)) continue;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            List<SmsCheck> checks;
            try { checks = suite.Run(ctx).ToList(); }
            catch (Exception e) { checks = new() { new SmsCheck(suite.Id + " (exception)", false, e.GetType().Name + ": " + e.Message) }; }
            int p = checks.Count(c => c.Status == SmsStatus.Pass), f = checks.Count(c => c.Status == SmsStatus.Fail), s = checks.Count(c => c.Status == SmsStatus.Skip);
            pass += p; fail += f; skipped += s;
            Console.WriteLine($"{suite.Id,-16} {p,3} pass {f,3} fail {s,3} skip  ({sw.Elapsed.TotalSeconds:F1}s)  {suite.Title}");
            foreach (var c in checks)
                if (ctx.Verbose || c.Status != SmsStatus.Pass) Console.WriteLine($"    {c.Status.ToString().ToUpperInvariant(),-4} {c.Name}{(c.Detail.Length > 0 ? "  - " + c.Detail : "")}");
        }
        Console.WriteLine($"smstest: {pass} pass, {fail} fail, {skipped} skip");
        return fail == 0 ? 0 : 1;
    }
}

internal enum SmsStatus { Pass, Fail, Skip }

internal readonly record struct SmsCheck(string Name, SmsStatus Status, string Detail)
{
    public SmsCheck(string name, bool pass, string detail = "") : this(name, pass ? SmsStatus.Pass : SmsStatus.Fail, detail) { }
    public static SmsCheck Skipped(string name, string why) => new(name, SmsStatus.Skip, why);
}

/// <summary>What a suite is told: where the test assets are, whether to be chatty, and any deliberate poisoning (a proof that the suite can fail) or timing override (tuning).</summary>
internal sealed class SmsSuiteContext
{
    public string Tools = "";
    public bool Verbose;
    public Dictionary<string, int> Tune = new();
    public string Poison = "";

    public string TestRom(params string[] rel) => Path.Combine(new[] { Tools, "test-sets" }.Concat(rel).ToArray());

    /// <summary>A session on a ROM, with any timing override applied to the board and the VDP (by field name).</summary>
    public SmsSession Session(string romPath, string? name = null, ConsoleKind console = ConsoleKind.MasterSystem, RegionChoice? region = null)
    {
        byte[] rom = File.ReadAllBytes(romPath);
        name ??= Path.GetFileName(romPath);
        var s = console == ConsoleKind.GameGear ? new GgSession(rom, "Z80", "GG", "GG", name, region) : new SmsSession(rom, console, "Z80", "SMS", "SMS", name, null, region);
        foreach (var kv in Tune) ApplyField(s, kv.Key, kv.Value);
        return s;
    }

    private static void ApplyField(SmsSession s, string field, int value)
    {
        foreach (object target in new object[] { s.Board, s.Board.Vdp })
        {
            var f = target.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance);
            if (f != null && f.FieldType == typeof(int)) { f.SetValue(target, value); return; }
        }
        throw new ArgumentException($"no tunable field '{field}'");
    }
}

internal sealed record SmsSuite(string Id, string Title, Func<SmsSuiteContext, IEnumerable<SmsCheck>> Run);

/// <summary>The suites of <c>--smstest</c>. Each says how its result is read.</summary>
internal static class SmsSuites
{
    public static readonly SmsSuite[] All =
    {
        new("vdptest", "FluBBa's SMS VDP Test: 55 checks (data port, counters, interrupts, sprite flags), read from the test's result bytes in work RAM", Vdptest.Run),
    };
}

/// <summary>
/// FluBBa's SMS VDP Test v1.31. The program is menu driven: item 1 runs three pages of tests (data port, counters and interrupts, sprite flags), each ending with "push any button"; every
/// test leaves one result byte at $C900 + n - 1 (0 = ok, $FF = error; test 21, the V counter table, leaves 2 for an NTSC counter and 1 for a PAL one). The suite presses Button 1 at the menu
/// and on every page, runs long enough for all three, then reads the bytes straight from the board's work RAM, so the verdict is the test program's own.
/// </summary>
internal static class Vdptest
{
    public static readonly (int Index, string Name)[] Tests =
    {
        (1, "Normal read/write"), (2, "Read after code1 wr"), (3, "Read after code2 wr"), (4, "Read after code3 wr"), (5, "Write after code0 wr"), (6, "Write after code2 wr"),
        (7, "Write after code3 wr"), (8, "Mixed read/write"), (9, "VRAM wr set VDPbuffer"), (10, "CRAM wr set VDPbuffer"), (11, "1byte wr sets rd adr"), (12, "1byte wr sets wr adr"),
        (13, "1byte wr keeps wr mode"), (14, "Address wraps at $3FFF"), (15, "VRAM & CRAM share adr"), (16, "Rd VDPCtrl reset latch"), (17, "Rd VDPData reset latch"),
        (18, "Wr VDPData reset latch"), (19, "Unused regs, no effect"), (21, "VCounter values"), (22, "HCounter keeps value"), (23, "HC change on TH 0->1"), (24, "HCounter correct"),
        (25, "VCounter chg time"), (26, "VDP Register mirrors"), (27, "VDP data mirrors"), (28, "VDP ctrl mirrors"), (29, "VCounter mirrors"), (30, "HCounter mirrors"),
        (31, "Frame IRQ VCount"), (32, "Frame IRQ HCount"), (33, "Line IRQ VCount"), (34, "Line IRQ HCount"), (35, "VINT flag HCount"), (36, "VINT flag keept"),
        (41, "No sprite collision"), (42, "No disp, no spr col"), (43, "9th sprite no col"), (44, "Transp pixl, no col"), (45, "Offscreen X, no col"), (46, "Offscreen Y, col"),
        (47, "Sprite collision"), (48, "Colflag keept in vbl"), (49, "Spr col behind tile"), (50, "Spr col correct line"), (51, "Spr col many lines"), (52, "Spr col correct HC"),
        (53, "No sprite overflow"), (54, "No disp, spr ovr"), (55, "Offscreen Y, no ovr"), (56, "Sprite overflow"), (57, "Ovrflag keept in vbl"), (58, "Spr ovr correct line"),
        (59, "Spr ovr many lines"), (60, "Spr ovr correct HC"),
    };

    /// <summary>The tests the oracle (Mesen 2.1.1) is known to fail; a failure here is reported but marked as the oracle's too.</summary>
    private static readonly HashSet<int> OracleFails = new() { 46 };

    public static IEnumerable<SmsCheck> Run(SmsSuiteContext ctx)
    {
        string rom = ctx.TestRom("sms-roms", "smsvdptest-1.31", "VDPTEST.sms");
        if (!File.Exists(rom)) { yield return SmsCheck.Skipped("vdptest", "VDPTEST.sms not found under " + ctx.Tools); yield break; }
        var results = RunResults(ctx, rom, out string pal);
        foreach (var (index, name) in Tests)
        {
            byte b = results[index - 1];
            bool ok = index == 21 ? b == 2 || b == 1 : b == 0;
            string detail = ok ? "" : $"result byte ${b:X2}" + (OracleFails.Contains(index) ? " (Mesen 2.1.1 fails this one too)" : "");
            yield return new SmsCheck($"#{index} {name}", ok, detail);
        }
    }

    /// <summary>
    /// Tuning aid: <c>--scan "IoPhaseMc=0..4,VintFlagMc=4..12:2"</c> runs the VDP test for every combination of the listed timing fields (<c>a..b</c> or <c>a..b:step</c>) and prints which tests fail.
    /// </summary>
    public static int Scan(SmsSuiteContext ctx, string spec)
    {
        string rom = ctx.TestRom("sms-roms", "smsvdptest-1.31", "VDPTEST.sms");
        var axes = new List<(string Name, int[] Values)>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=');
            var vals = new List<int>();
            foreach (var piece in kv[1].Split('|'))
            {
                var stepSplit = piece.Split(':'); int step = stepSplit.Length > 1 ? int.Parse(stepSplit[1]) : 1;
                var range = stepSplit[0].Split("..");
                int a = int.Parse(range[0]), b = range.Length > 1 ? int.Parse(range[1]) : a;
                for (int v = a; v <= b; v += step) vals.Add(v);
            }
            axes.Add((kv[0], vals.ToArray()));
        }
        var saved = new Dictionary<string, int>(ctx.Tune);
        int[] idx = new int[axes.Count];
        while (true)
        {
            foreach (var kv in saved) ctx.Tune[kv.Key] = kv.Value;
            for (int i = 0; i < axes.Count; i++) ctx.Tune[axes[i].Name] = axes[i].Values[idx[i]];
            var r = RunResults(ctx, rom, out _);
            var failing = Tests.Where(t => !(t.Index == 21 ? r[20] is 1 or 2 : r[t.Index - 1] == 0) && !OracleFails.Contains(t.Index)).Select(t => t.Index).ToList();
            Console.WriteLine($"{string.Join(" ", axes.Select((a, i) => $"{a.Name}={a.Values[idx[i]]}"))}  fail={failing.Count}: {string.Join(",", failing)}");
            int k = axes.Count - 1;
            while (k >= 0 && ++idx[k] >= axes[k].Values.Length) { idx[k] = 0; k--; }
            if (k < 0) break;
        }
        return 0;
    }

    /// <summary>Runs the program and returns the 60 result bytes.</summary>
    public static byte[] RunResults(SmsSuiteContext ctx, string rom, out string info, int frames = 1600)
    {
        var s = ctx.Session(rom);
        PadButtons held = 0;
        for (int f = 0; f < frames; f++)
        {
            // Button 1 for five frames at 300 (the menu is up from about frame 190: it runs page 1), then at 700 and 1100 for pages 2 and 3; the third page is done by 1600
            held = f is >= 300 and < 305 or >= 700 and < 705 or >= 1100 and < 1105 ? PadButtons.A : 0;
            s.SetPad(0, held);
            s.RunFrame();
        }
        info = s.Description;
        var ram = s.Board.Ram;
        var r = new byte[60];
        Array.Copy(ram, 0x900, r, 0, 60);
        return r;
    }
}
