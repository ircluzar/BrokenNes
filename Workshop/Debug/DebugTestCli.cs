using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator.Debugging;
using Debugger = NesEmulator.Debugging.Debugger;
using Stopwatch = System.Diagnostics.Stopwatch;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Debug;

/// <summary>
/// <c>--debug-test [--only nes,snes,gb,wms,games] [--keep &lt;dir&gt;]</c>: the debugger's conformance suite (BrokenDev milestone D1). Small
/// test ROMs with known behaviour per console (assembled in DebugTestRoms), each check with a negative control, then the real games when
/// they are on this machine (VRUN NES with its .mlb, Super VRUN with its .sym; a missing one is NOT RUN, never a pass). Exit 0 when every
/// check passes.
/// </summary>
internal static class DebugTestCli
{
    private static int pass, fail, notRun;
    private static readonly List<string> failures = new();

    private static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
        if (ok) pass++; else { fail++; failures.Add(name + ": " + detail); }
    }

    private static void NotRun(string name, string why) { Console.WriteLine($"  NOT RUN  {name}: {why}"); notRun++; }

    public static int Run(string[] args)
    {
        string Opt(string n, string d) { int i = Array.IndexOf(args, "--" + n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        if (Opt("write-roms", "") is { Length: > 0 } romDir)
        {
            // the test ROMs and their symbol files, for the desktop app's debugger smoke (UAT\entrypoints\debugger-desktop-smoke.ps1)
            Directory.CreateDirectory(romDir);
            var (nr, na, nm) = DebugTestRoms.Nes();
            var (sr, sa, ss, sy) = DebugTestRoms.Snes();
            var (gr, ga, gs, gn) = DebugTestRoms.GameBoy();
            DebugTestRoms.Write(romDir, "debugtest.nes", nr); DebugTestRoms.Write(romDir, "debugtest.mlb", nm);
            DebugTestRoms.Write(romDir, "debugtest.sfc", sr); DebugTestRoms.Write(romDir, "debugtest.sym", sy);
            DebugTestRoms.Write(romDir, "debugtest.gb", gr); DebugTestRoms.Write(romDir, "debugtest_gb.sym", gs); DebugTestRoms.Write(romDir, "debugtest_gb.noi", gn);
            var labels = new Dictionary<string, object>
            {
                ["nes"] = na.Labels.ToDictionary(k => k.Key, k => k.Value), ["snes"] = sa.Labels.ToDictionary(k => k.Key, k => k.Value),
                ["spc"] = ss.Labels.ToDictionary(k => k.Key, k => k.Value), ["gb"] = ga.Labels.ToDictionary(k => k.Key, k => k.Value),
            };
            File.WriteAllText(Path.Combine(romDir, "labels.json"), System.Text.Json.JsonSerializer.Serialize(labels));
            Console.WriteLine($"wrote the debugger test ROMs to {romDir}");
            return 0;
        }
        var only = Opt("only", "nes,snes,gb,wms,games").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string dir = Opt("keep", Path.Combine(Path.GetTempPath(), "brokennes-debug-test-" + Environment.ProcessId));
        Directory.CreateDirectory(dir);
        var sw = Stopwatch.StartNew();
        void Group(string key, string title, Action body)
        {
            if (!only.Contains(key)) return;
            Console.WriteLine($"== {title}");
            try { body(); }
            catch (Exception e) { Check(title + " (ran to the end)", false, e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace); }
        }
        Group("nes", "NES (6502, CPU_FIX)", () => Nes(dir));
        Group("snes", "SNES (65816 + SPC700)", () => Snes(dir));
        Group("gb", "Game Boy (SM83, MBC1)", () => GameBoy(dir));
        Group("wms", "WideMess", () => WideMess());
        Group("games", "Real games", () => Games(Opt("vrun", @"C:\Users\philt\OneDrive\Documents\PROJECTS\VRUN_Nes_Dev\builds\2026-10-23_poweron\vrun_game.nes"),
                                                  Opt("supervrun", @"P:\BrokenDev\env\out\supervrun\supervrun.sfc")));
        Console.WriteLine();
        Console.WriteLine($"debug-test: {pass} pass, {fail} fail, {notRun} not run ({sw.Elapsed.TotalSeconds:F1} s); files in {dir}");
        foreach (var f in failures) Console.WriteLine("  FAILED " + f);
        return fail == 0 ? 0 : 1;
    }

    private static string Hex(uint v) => "$" + v.ToString("X4");

    // ================================================================================================================================ NES
    private static void Nes(string dir)
    {
        var (rom, a, mlb) = DebugTestRoms.Nes();
        DebugTestRoms.Write(dir, "debugtest.nes", rom);
        string mlbPath = DebugTestRoms.Write(dir, "debugtest.mlb", mlb);
        DebugHost Open() { var h = DebugHost.Open(rom, "debugtest.nes"); h.Dbg.Symbols.Load(mlbPath, "nes"); return h; }

        using (var h = Open())
        {
            var bp = h.Dbg.Add(Hex(a["sub2"]), AccessKind.Exec);
            var o = h.Runner.Run(10);
            var b = h.Dbg.LastBreak;
            Check("exec breakpoint stops at its PC", o == RunOutcome.Break && b?.Pc == a["sub2"] && bp.Hits == 1, $"outcome {o}, pc {b?.Pc:X4}, hits {bp.Hits} ({b})");
            var st = h.Dbg.CallStack();
            Check("call stack from JSR", st.Count == 2 && st[0].Kind == "JSR" && st[0].Target == a["sub1"] && st[0].CallerPc == a["loop"] && st[0].ReturnPc == a["loop"] + 3
                                         && st[1].Target == a["sub2"] && st[1].CallerPc == a["sub1"],
                  string.Join(" | ", st.Select(f => f.ToString())));
            Check("call stack names symbols", st.Count == 2 && st[1].TargetSymbol == "sub2" && st[0].CallerSymbol == "loop", $"{st.LastOrDefault()?.TargetSymbol}, {st.FirstOrDefault()?.CallerSymbol}");
            var tail = h.Dbg.TraceTail(3);
            Check("trace ring holds the last instructions", tail.Length == 3 && tail[0].Pc == a["loop"] && tail[1].Pc == a["sub1"] && tail[2].Pc == a["sub2"],
                  string.Join(", ", tail.Select(t => t.Pc.ToString("X4"))));
            string line = h.Dbg.FormatTrace(tail[1]);
            Check("trace line: Mesen columns with symbols", line.StartsWith("C021  JSR sub2") && line.Contains(" A:") && line.Contains(" S:") && line.Contains(" P:") && line.Contains(" V:") && line.Contains(" H:") && line.Contains("Fr:0"), line);
            h.Dbg.TrySetRegisterForTest("A", 0x42);
            Check("registers are read and written", h.Dbg.Machine.Main.TryGetRegister("A", out long av) && av == 0x42, $"A = {av:X2}");
            h.Dbg.Remove(bp.Id);
            o = h.Runner.Run(3);
            Check("removed breakpoint no longer stops", o == RunOutcome.Frames, $"outcome {o}");
        }

        using (var h = Open())
        {
            var bp = h.Dbg.Add(Hex(a["sub2"]), AccessKind.Exec, hits: "==5");
            var o = h.Runner.Run(30);
            byte counter = h.Dbg.Machine.Main.Peek(0x10);
            Check("hit count ==5 breaks on the 5th hit", o == RunOutcome.Break && bp.Hits == 5 && counter == 4, $"outcome {o}, hits {bp.Hits}, [$10] = {counter} (4 loops done)");
            o = h.Runner.Run(5);
            Check("hit count ==5 does not break again", o == RunOutcome.Frames && bp.Hits > 5, $"outcome {o}, hits {bp.Hits}");
        }

        using (var h = Open())
        {
            var bp = h.Dbg.Add(Hex(a["inc10"]), AccessKind.Exec, condition: "[$10] == 7 && X == 3");
            var o = h.Runner.Run(30);
            Check("condition on memory and registers", o == RunOutcome.Break && h.Dbg.Machine.Main.Peek(0x10) == 7 && h.Dbg.LastBreak?.Pc == a["inc10"], $"outcome {o}, [$10] = {h.Dbg.Machine.Main.Peek(0x10)}");
            var never = h.Dbg.Add(Hex(a["inc10"]), AccessKind.Exec, condition: "[$10] == 7 && X == 4");
            h.Dbg.Remove(bp.Id);
            o = h.Runner.Run(20);
            Check("a false condition never breaks", o == RunOutcome.Frames && never.Hits == 0, $"outcome {o}, hits {never.Hits}");
        }

        using (var h = Open())
        {
            var w = h.Dbg.Add("$0012", AccessKind.Write);
            var o = h.Runner.Run(10);
            var b = h.Dbg.LastBreak;
            byte c = h.Dbg.Machine.Main.Peek(0x10);
            Check("write watch sees RAM through a mirror ($0812 -> $0012)", o == RunOutcome.Break && b?.Address == 0x0812 && b.Value == c && b.AccessPc == a["sta_mirror"] && b.Pc == a["sta_mirror"] + 3,
                  $"{b}");
            h.Dbg.Remove(w.Id);
            var w2 = h.Dbg.Add("$1812", AccessKind.Write);   // another mirror of the same byte, as written in the spec
            o = h.Runner.Run(10);
            Check("a mirror in the spec folds to the same byte", o == RunOutcome.Break && h.Dbg.LastBreak?.Address == 0x0812 && w2.Resolved == "cpu:$12", $"{h.Dbg.LastBreak} ({w2.Resolved})");
            h.Dbg.Remove(w2.Id);
            var neg = h.Dbg.Add("$0014", AccessKind.ReadWrite);
            o = h.Runner.Run(20);
            Check("negative: a watch on a byte nothing touches stays silent", o == RunOutcome.Frames && neg.Hits == 0, $"outcome {o}, hits {neg.Hits}");
        }

        using (var h = Open())
        {
            var w = h.Dbg.Add("$0205", AccessKind.Read);
            var o = h.Runner.Run(10);
            var b = h.Dbg.LastBreak;
            Check("read watch sees the OAM DMA read RAM", o == RunOutcome.Break && b?.Source == AccessSource.OamDma && b.Address == 0x0205 && b.AccessPc == a["nmi"] + 4, $"{b}");
            h.Dbg.Remove(w.Id);
            var ow = h.Dbg.Add("oam:$05", AccessKind.Write);
            o = h.Runner.Run(10);
            b = h.Dbg.LastBreak;
            Check("write watch on OAM sees the DMA", o == RunOutcome.Break && b?.Source == AccessSource.OamDma && ow.Hits >= 1, $"{b}");
        }

        using (var h = Open())
        {
            var bp = h.Dbg.Add("never", AccessKind.Exec);
            var o = h.Runner.Run(20);
            Check("negative: a breakpoint never reached never hits", o == RunOutcome.Frames && bp.Hits == 0, $"outcome {o}, hits {bp.Hits} ({bp.Resolved})");
        }

        using (var h = Open())
        {
            var bp = h.Dbg.Add("loop", AccessKind.Exec);
            h.Runner.Run(5);
            h.Dbg.Remove(bp.Id);
            var o = h.Runner.Step(() => h.Dbg.StepInto());
            Check("step into enters the call", o == RunOutcome.Break && h.Dbg.LastBreak?.Pc == a["sub1"] && h.Dbg.CallStack().Count == 1, $"{h.Dbg.LastBreak}, depth {h.Dbg.CallStack().Count}");
            o = h.Runner.Step(() => h.Dbg.StepInto());
            o = h.Runner.Step(() => h.Dbg.StepOut());
            Check("step out returns to the caller", o == RunOutcome.Break && h.Dbg.LastBreak?.Pc == a["sub1_rts"] && h.Dbg.CallStack().Count == 1, $"{h.Dbg.LastBreak}, depth {h.Dbg.CallStack().Count}");
            o = h.Runner.Step(() => h.Dbg.StepOut());
            Check("step out again lands after the first call", o == RunOutcome.Break && h.Dbg.LastBreak?.Pc == a["inc10"] && h.Dbg.CallStack().Count == 0, $"{h.Dbg.LastBreak}");
            var bp2 = h.Dbg.Add("loop", AccessKind.Exec);
            h.Runner.Run(5);
            h.Dbg.Remove(bp2.Id);
            o = h.Runner.Step(() => h.Dbg.StepOver());
            Check("step over runs the whole call", o == RunOutcome.Break && h.Dbg.LastBreak?.Pc == a["inc10"] && h.Dbg.CallStack().Count == 0, $"{h.Dbg.LastBreak}");
            o = h.Runner.Step(() => h.Dbg.StepOver());
            Check("step over a plain instruction moves one", o == RunOutcome.Break && h.Dbg.LastBreak?.Pc == a["inc10"] + 2, $"{h.Dbg.LastBreak}");
        }

        using (var h = Open())
        {
            var bp = h.Dbg.Add("nmi_handler", AccessKind.Exec);   // a NESFab base name
            var o = h.Runner.Run(5);
            var st = h.Dbg.CallStack();
            Check("interrupt entries are call frames (NMI)", o == RunOutcome.Break && st.Count >= 1 && st[^1].Kind == "NMI" && st[^1].Target == a["nmi"], string.Join(" | ", st));
            h.Dbg.Remove(bp.Id);
            int depth = st.Count;
            o = h.Runner.Step(() => h.Dbg.StepOut());
            Check("step out of the NMI returns where it interrupted", o == RunOutcome.Break && h.Dbg.CallStack().Count == depth - 1 && h.Dbg.LastBreak!.Pc == st[^1].ReturnPc, $"{h.Dbg.LastBreak}; interrupted at {st[^1].ReturnPc:X4}");
        }

        using (var h = Open())
        {
            var sym = h.Dbg.Symbols;
            bool okVar = sym.TryFind("oam", out var oam, out _);
            bool split = sym.TryFind("split", out _, out string? splitErr);
            Check("symbols: NESFab byte variables group, scattered ones are refused", okVar && oam.Size == 3 && !split && splitErr!.Contains("not side by side"), $"oam {oam?.Size} bytes; split: {splitErr}");
            Check("symbols: the PC's label", h.Dbg.SymbolAt(h.Dbg.Machine.Main, a["sub2"]) == "sub2" && h.Dbg.SymbolAt(h.Dbg.Machine.Main, a["sub2"] + 2) == "sub2+$2", $"{h.Dbg.SymbolAt(h.Dbg.Machine.Main, a["sub2"])}, {h.Dbg.SymbolAt(h.Dbg.Machine.Main, a["sub2"] + 2)}");
            var w = h.Dbg.Add("mirror_target", AccessKind.Write);
            var o = h.Runner.Run(10);
            Check("a watch by symbol (NESFab R: label) fires through the mirror", o == RunOutcome.Break && h.Dbg.LastBreak?.Address == 0x0812, $"{h.Dbg.LastBreak}");
            var v = h.Dbg.ReadVariable("counter");
            Check("variables are read by name", v.Length == 1 && v[0] == h.Dbg.Machine.Main.Peek(0x10), $"counter = {v[0]}");
            h.Dbg.WriteVariable("counter", 0x33);
            Check("variables are written by name", h.Dbg.Machine.Main.Peek(0x10) == 0x33 && h.Dbg.Evaluate("[counter] + 1") == 0x34, $"[$10] = {h.Dbg.Machine.Main.Peek(0x10):X2}");
        }

        using (var h = Open())
        {
            h.Dbg.Log = null;
            var tp = h.Dbg.Add("sub2", AccessKind.Exec, tracepoint: true, log: "x={X} c={[counter]}");
            var o = h.Runner.Run(5);
            var hits = h.Dbg.Hits;
            Check("tracepoint logs and keeps running", o == RunOutcome.Frames && tp.Hits > 10 && hits.Count > 10 && hits[0].Text.Contains("x=$") && hits[0].Logged, $"outcome {o}, {tp.Hits} hits, first: {hits.FirstOrDefault()}");
        }

        using (var h = Open())
        {
            var o = h.Runner.Step(() => h.Dbg.RunToFrame(10));
            Check("run to frame", o == RunOutcome.Break && h.Dbg.LastBreak?.Frame == 10, $"{h.Dbg.LastBreak}");
            o = h.Runner.Step(() => h.Dbg.StepFrame());
            Check("step frame", o == RunOutcome.Break && h.Dbg.LastBreak?.Frame == 11, $"{h.Dbg.LastBreak}");
            int line = h.Dbg.LastBreak!.Scanline;
            o = h.Runner.Step(() => h.Dbg.StepScanline());
            int line2 = h.Dbg.LastBreak!.Scanline;
            Check("step scanline", o == RunOutcome.Break && line2 != line && (line2 == line + 1 || (line == 261 && line2 == 0)), $"{line} -> {line2}");
            o = h.Runner.Step(() => h.Dbg.RunToScanline(100));
            Check("run to a line", o == RunOutcome.Break && h.Dbg.LastBreak?.Scanline == 100, $"{h.Dbg.LastBreak}");
            var rt = h.Runner.Step(() => h.Dbg.RunTo("sub2"));
            Check("run to an address (symbol)", rt == RunOutcome.Break && h.Dbg.LastBreak?.Pc == a["sub2"] && h.Dbg.Breakpoints.Count == 0, $"{h.Dbg.LastBreak}, breakpoints left {h.Dbg.Breakpoints.Count}");
        }

        using (var h = Open())
        {
            string path = Path.Combine(dir, "nes_sub1.trace");
            h.Dbg.StartTraceFile(path, new Debugger.TraceFileOptions { Function = "sub1", FromFrame = 0, ToFrame = 2 });
            h.Runner.Run(4);
            long n = h.Dbg.StopTraceFile();
            var lines = File.ReadAllLines(path).Where(l => !l.StartsWith("#")).ToArray();
            var allowed = new[] { a["sub1"], a["sub1_rts"], a["sub2"], a["sub2"] + 2, a["sub2"] + 5 }.Select(x => x.ToString("X4")).ToHashSet();
            bool onlyFn = lines.All(l => allowed.Contains(l[..4]) || l[6..].StartsWith("-- NMI") || IsNmiLine(l, a));
            Check("filtered trace file: only the function (and what it calls)", n > 20 && lines.Length == n && onlyFn && lines.All(l => int.Parse(l[(l.IndexOf("Fr:") + 3)..].Split(' ')[0]) <= 2),
                  $"{n} lines, first: {lines.FirstOrDefault()}");
            Check("negative: the trace file has none of the main loop", lines.All(l => !l.StartsWith(a["inc10"].ToString("X4"))), "no INC $10 lines");
        }

        // the negative control of the whole debugger: with it attached and busy (watches, tracepoints, breaks continued) the game
        // runs exactly as without it; detached, every hook is null again
        using (var plain = DebugHost.Open(rom, "debugtest.nes", debugger: false))
        using (var h = Open())
        {
            plain.RunPlain(120);
            h.Dbg.Log = null;
            h.Dbg.Add("$0000-$07FF", AccessKind.ReadWrite, tracepoint: true);
            h.Dbg.Add("prg:$0000-$3FFF", AccessKind.Exec, tracepoint: true);
            var stop = h.Dbg.Add("sub2", AccessKind.Exec, hits: "%50");
            int breaks = 0;
            while (h.Runner.FramesDone < 120) { if (h.Runner.Run(120 - h.Runner.FramesDone) == RunOutcome.Break) breaks++; }
            Check("debugger attached and breaking: frame-identical to no debugger", h.PictureHash() == plain.PictureHash() && h.RamHash() == plain.RamHash() && breaks > 5,
                  $"picture {h.PictureHash():X16} vs {plain.PictureHash():X16}, RAM {h.RamHash():X16} vs {plain.RamHash():X16}, {breaks} breaks");
            h.Dbg.Detach();
            Check("detached: no hook left", h.Nes!.DebugHook == null && h.Nes.DebugBus!.DebugTap == null && !h.Dbg.IsArmed, "NES.DebugHook and Bus.DebugTap are null");
            h.Runner.Run(30); plain.RunPlain(30);
            Check("detached: still frame-identical", h.PictureHash() == plain.PictureHash() && h.RamHash() == plain.RamHash(), $"{h.PictureHash():X16}");
        }

        // a CPU core other than CPU_FIX: interrupts are found afterwards (the documented limitation), breakpoints still work
        using (var h = DebugHost.Open(rom, "debugtest.nes", cpu: "FMC"))
        {
            h.Dbg.Symbols.Load(mlbPath, "nes");
            var bp = h.Dbg.Add("nmi_handler", AccessKind.Exec);
            var o = h.Runner.Run(5);
            var st = h.Dbg.CallStack();
            Check("CPU_FMC: NMI frame found by its vector", o == RunOutcome.Break && st.Count >= 1 && st[^1].Kind == "NMI", string.Join(" | ", st));
            Check("CPU_FMC: the limitation is said", h.Machine.Limitations.Any(l => l.Contains("interrupt")), string.Join(" / ", h.Machine.Limitations));
        }
    }

    private static bool IsNmiLine(string l, Asm a)
    {
        uint pc = uint.Parse(l[..4], System.Globalization.NumberStyles.HexNumber);
        return pc >= a["nmi"] && pc < a["irq"];
    }

    // ================================================================================================================================ SNES
    private static void Snes(string dir)
    {
        var (rom, a, spc, sym) = DebugTestRoms.Snes();
        DebugTestRoms.Write(dir, "debugtest.sfc", rom);
        string symPath = DebugTestRoms.Write(dir, "debugtest.sym", sym);
        DebugHost Open(ConsoleKind console = ConsoleKind.Snes) { var h = DebugHost.Open(rom, "debugtest.sfc", console); h.Dbg.Symbols.Load(symPath, "snes"); return h; }

        using (var h = Open())
        {
            var bp = h.Dbg.Add("ssub", AccessKind.Exec);
            var o = h.Runner.Run(30);
            var st = h.Dbg.CallStack();
            Check("65816: exec breakpoint by WLA symbol", o == RunOutcome.Break && h.Dbg.LastBreak?.Pc == a["ssub"], $"{h.Dbg.LastBreak}");
            Check("65816: call stack from JSL and JSR", st.Count == 2 && st[0].Kind == "JSL" && st[0].Target == a["lsub"] && st[0].ReturnPc == a["loop"] + 4 && st[1].Kind == "JSR" && st[1].Target == a["ssub"],
                  string.Join(" | ", st));
            string line = h.Dbg.FormatTrace(h.Dbg.TraceTail(2)[0]);
            Check("65816: trace line", line.StartsWith($"00{a["lsub"]:X4}  JSR ssub") && line.Contains(" D:") && line.Contains(" DB:") && line.Contains(" P:"), line);
            h.Dbg.Remove(bp.Id);
            o = h.Runner.Step(() => h.Dbg.StepOut());
            Check("65816: step out of JSR", o == RunOutcome.Break && h.Dbg.LastBreak?.Pc == a["lsub_rtl"], $"{h.Dbg.LastBreak}");
            o = h.Runner.Step(() => h.Dbg.StepOut());
            Check("65816: step out of JSL", o == RunOutcome.Break && h.Dbg.LastBreak?.Pc == a["inc10"] && h.Dbg.CallStack().Count == 0, $"{h.Dbg.LastBreak}");
            var lp = h.Dbg.Add("loop", AccessKind.Exec);
            h.Runner.Run(2); h.Dbg.Remove(lp.Id);
            o = h.Runner.Step(() => h.Dbg.StepOver());
            Check("65816: step over JSL", o == RunOutcome.Break && h.Dbg.LastBreak?.Pc == a["inc10"], $"{h.Dbg.LastBreak}");
        }

        using (var h = Open())
        {
            var w = h.Dbg.Add("$7E0013", AccessKind.Write);
            var o = h.Runner.Run(30);
            var b = h.Dbg.LastBreak;
            Check("65816: watch on $7E:0013 sees the $80:0013 write", o == RunOutcome.Break && b?.Address == 0x800013 && b.AccessPc == a["sta_mirror"], $"{b}");
            h.Dbg.Remove(w.Id);
            var w2 = h.Dbg.Add("$0012", AccessKind.Write);   // the bank-0 mirror in the spec
            o = h.Runner.Run(5);
            Check("65816: watch on $00:0012 sees the $7E:0012 write", o == RunOutcome.Break && h.Dbg.LastBreak?.Address == 0x7E0012 && w2.Resolved == "wram:$12", $"{h.Dbg.LastBreak} ({w2.Resolved})");
            h.Dbg.Remove(w2.Id);
            var neg = h.Dbg.Add("$7E0015", AccessKind.ReadWrite);
            o = h.Runner.Run(20);
            Check("65816 negative: a watch on an untouched byte stays silent", o == RunOutcome.Frames && neg.Hits == 0, $"{o}, {neg.Hits} hits");
        }

        using (var h = Open())
        {
            var w = h.Dbg.Add("vram:$2000-$2001", AccessKind.Write);
            var rd = h.Dbg.Add($"{a["table"] + 15:X4}", AccessKind.Read, condition: "source == 1", tracepoint: true);
            var negv = h.Dbg.Add("vram:$2020", AccessKind.Write);
            h.Dbg.Log = null;
            var o = h.Runner.Run(5);
            var b = h.Dbg.LastBreak;
            // a watch breaks after the instruction that made the access: the whole DMA ran inside STA $420B
            Check("65816: a watch on VRAM sees the DMA upload", o == RunOutcome.Break && b?.Source == AccessSource.Dma && b.Channel == 0 && b.Value == 0x11 && b.Address == 0x2118 && b.AccessPc == a["dma_go"] + 2 && b.Pc == a["dma_go"] + 5, $"{b}");
            var wh = h.Dbg.Hits.Where(x => x.BreakpointId == w.Id).ToList();
            Check("65816: ... both bytes of the word, counted in one break", w.Hits == 2 && wh.Count == 2 && wh[1].Value == 0x22 && wh[1].Address == 0x2119, string.Join(" / ", wh));
            Check("65816: a read watch on ROM sees the DMA read its source (source == 1)", rd.Hits == 1 && h.Dbg.Hits.Last(x => x.BreakpointId == rd.Id).Source == AccessSource.Dma, $"{rd.Hits} hits");
            h.Dbg.Remove(w.Id);
            o = h.Runner.Run(5);
            Check("65816 negative: VRAM past the upload is never written", negv.Hits == 0, $"{negv.Hits} hits");
        }

        using (var h = Open())
        {
            var w = h.Dbg.Add("$80420B", AccessKind.Write);   // a register mirror in the spec (the game writes $00:420B)
            var o = h.Runner.Run(5);
            Check("65816: register mirrors fold ($80:420B is $00:420B)", o == RunOutcome.Break && h.Dbg.LastBreak?.Address == 0x00420B && w.Resolved == "io:$420B", $"{h.Dbg.LastBreak} ({w.Resolved})");
            h.Dbg.Remove(w.Id);
            var c = h.Dbg.Add("inc10", AccessKind.Exec, condition: "[counter] == 5 && A == 3");
            o = h.Runner.Run(30);
            Check("65816: condition on a symbol and A", o == RunOutcome.Break && h.Dbg.ReadVariable("counter")[0] == 5, $"{h.Dbg.LastBreak}, counter {h.Dbg.ReadVariable("counter")[0]}");
            h.Dbg.Remove(c.Id);
            var nmi = h.Dbg.Add("nmi", AccessKind.Exec);
            o = h.Runner.Run(5);
            var st = h.Dbg.CallStack();
            Check("65816: NMI frame", o == RunOutcome.Break && st.Count > 0 && st[^1].Kind == "NMI", string.Join(" | ", st));
            h.Dbg.Remove(nmi.Id);
            var never = h.Dbg.Add("never", AccessKind.Exec);
            o = h.Runner.Run(10);
            Check("65816 negative: never reached", o == RunOutcome.Frames && never.Hits == 0, $"{o}");
            Check("WLA: [definitions] are not places", !h.Dbg.Symbols.TryFind("COUNTER_CONST", out _, out _) && h.Dbg.Symbols.TryFind("after_frames", out var af, out _) && af.Address == 0x7E0021, "COUNTER_CONST absent, after_frames at $7E0021");
        }

        // the SPC700 (the program the 65816 uploaded through the IPL)
        using (var h = Open())
        {
            var ipl = h.Dbg.Add("spc:$F4", AccessKind.Write, cpu: "spc");
            var o = h.Runner.Run(5);
            Check("SPC700: watch on the IPL's port write", o == RunOutcome.Break && h.Dbg.LastBreak?.CpuId == "spc" && h.Dbg.LastBreak.Value == 0xAA, $"{h.Dbg.LastBreak}");
            h.Dbg.Remove(ipl.Id);
            var bp = h.Dbg.Add($"{spc["spc_sub"]:X4}", AccessKind.Exec, cpu: "spc");
            o = h.Runner.Run(60);
            var st = h.Dbg.CallStack("spc");
            Check("SPC700: exec breakpoint in uploaded code", o == RunOutcome.Break && h.Dbg.LastBreak?.CpuId == "spc" && h.Dbg.LastBreak.Pc == spc["spc_sub"], $"{h.Dbg.LastBreak}");
            Check("SPC700: call stack from CALL", st.Count == 1 && st[0].Kind == "CALL" && st[0].CallerPc == spc["spc_loop"] && st[0].ReturnPc == spc["spc_inc"], string.Join(" | ", st));
            h.Dbg.Remove(bp.Id);
            o = h.Runner.Step(() => h.Dbg.StepOut("spc"));
            Check("SPC700: step out of CALL", o == RunOutcome.Break && h.Dbg.LastBreak?.Pc == spc["spc_inc"] && h.Dbg.CallStack("spc").Count == 0, $"{h.Dbg.LastBreak}");
            o = h.Runner.Step(() => h.Dbg.StepInto("spc"));
            Check("SPC700: step into", o == RunOutcome.Break && h.Dbg.LastBreak?.Pc == spc["spc_inc"] + 2, $"{h.Dbg.LastBreak}");
            byte before = h.Dbg.Machine.Cpu("spc")!.Peek(0x40);
            var w = h.Dbg.Add("spc:$0040", AccessKind.Write, cpu: "spc");
            var neg = h.Dbg.Add("spc:$0042", AccessKind.ReadWrite, cpu: "spc");
            o = h.Runner.Run(5);
            Check("SPC700: write watch on its RAM", o == RunOutcome.Break && h.Dbg.LastBreak?.Address == 0x40 && h.Dbg.LastBreak.Value == (byte)(before + 1) && h.Dbg.LastBreak.AccessPc == spc["spc_inc"], $"{h.Dbg.LastBreak}");
            h.Dbg.Remove(w.Id);
            o = h.Runner.Run(5);
            Check("SPC700 negative: an untouched byte", neg.Hits == 0, $"{neg.Hits} hits");
            var tail = h.Dbg.TraceTail(4, "spc");
            Check("SPC700: trace", tail.Length == 4 && h.Dbg.FormatTrace(tail[^1], "spc").Contains(" PSW") == false && h.Dbg.FormatTrace(tail[^1], "spc").Contains(" P:"), h.Dbg.FormatTrace(tail[^1], "spc"));
        }

        using (var plain = DebugHost.Open(rom, "debugtest.sfc", debugger: false))
        using (var h = Open())
        {
            plain.RunPlain(120);
            h.Dbg.Log = null;
            h.Dbg.Add("wram:$0000-$1FFFF", AccessKind.ReadWrite, tracepoint: true);
            h.Dbg.Add("vram:$0000-$FFFF", AccessKind.Write, tracepoint: true);
            h.Dbg.Add("spc:$0000-$FFFF", AccessKind.ReadWrite, tracepoint: true, cpu: "spc");
            h.Dbg.Add("ssub", AccessKind.Exec, hits: "%40");
            int breaks = 0;
            while (h.Runner.FramesDone < 120) { if (h.Runner.Run(120 - h.Runner.FramesDone) == RunOutcome.Break) breaks++; }
            Check("SNES: attached and breaking, frame-identical to no debugger (slow pages, DMA taps, SPC hook)", h.PictureHash() == plain.PictureHash() && h.RamHash() == plain.RamHash() && breaks > 3,
                  $"picture {h.PictureHash():X16} vs {plain.PictureHash():X16}, WRAM {h.RamHash():X16} vs {plain.RamHash():X16}, {breaks} breaks");
            var board = ((SnesSession)h.Session!).Board;
            h.Dbg.Detach();
            Check("SNES detached: no hook left", board.DebugHook == null && board.DebugTap == null && !board.DebugSlowPages && ((SnesSession)h.Session!).Spc!.SmpHook == null, "board hooks null, page table back, SmpHook restored");
        }
    }

    // ================================================================================================================================ Game Boy
    private static void GameBoy(string dir)
    {
        var (rom, a, sym, noi) = DebugTestRoms.GameBoy();
        DebugTestRoms.Write(dir, "debugtest.gb", rom);
        string symPath = DebugTestRoms.Write(dir, "debugtest_gb.sym", sym);
        string noiPath = DebugTestRoms.Write(dir, "debugtest_gb.noi", noi);
        DebugHost Open() { var h = DebugHost.Open(rom, "debugtest.gb", ConsoleKind.GameBoy); h.Dbg.Symbols.Load(symPath, "gb"); return h; }

        using (var h = Open())
        {
            var bp = h.Dbg.Add("sub2", AccessKind.Exec);
            var o = h.Runner.Run(10);
            var st = h.Dbg.CallStack();
            Check("SM83: exec breakpoint by symbol", o == RunOutcome.Break && h.Dbg.LastBreak?.Pc == a["sub2"], $"{h.Dbg.LastBreak}");
            Check("SM83: call stack from CALL", st.Count == 2 && st[0].Kind == "CALL" && st[0].CallerPc == a["loop"] && st[0].ReturnPc == a["after_call"] && st[1].Target == a["sub2"], string.Join(" | ", st));
            h.Dbg.Remove(bp.Id);
            o = h.Runner.Step(() => h.Dbg.StepOut());
            Check("SM83: step out", o == RunOutcome.Break && h.Dbg.LastBreak?.Pc == a["sub1_ret"], $"{h.Dbg.LastBreak}");
            var lp = h.Dbg.Add("loop", AccessKind.Exec);
            h.Runner.Run(2); h.Dbg.Remove(lp.Id);
            o = h.Runner.Step(() => h.Dbg.StepOver());
            Check("SM83: step over CALL", o == RunOutcome.Break && h.Dbg.LastBreak?.Pc == a["after_call"], $"{h.Dbg.LastBreak}");
            string line = h.Dbg.FormatTrace(h.Dbg.TraceTail(1)[0]);
            Check("SM83: trace line", line.Contains("CALL sub1") || line.Contains(" A:"), line);
        }

        using (var h = Open())
        {
            var b2 = h.Dbg.Add("bank2_func", AccessKind.Exec);
            var b1 = h.Dbg.Add("bank1_func", AccessKind.Exec);
            var any = h.Dbg.Add("$4000", AccessKind.Exec, tracepoint: true);
            var o = h.Runner.Run(10);
            Check("SM83: a banked symbol breaks in its bank", o == RunOutcome.Break && h.Dbg.LastBreak?.BreakpointId == b2.Id && b2.Resolved == "rom:$8000", $"{h.Dbg.LastBreak} ({b2.Resolved})");
            o = h.Runner.Run(10);
            Check("SM83 negative: the same address in another bank never breaks", b1.Hits == 0 && any.Hits >= 2 && b1.Resolved == "rom:$4000", $"bank 1: {b1.Hits} hits, any bank: {any.Hits}");
        }

        using (var h = Open())
        {
            var w = h.Dbg.Add("echo_target", AccessKind.Write);
            var o = h.Runner.Run(10);
            Check("SM83: watch on $C011 sees the echo write at $E011", o == RunOutcome.Break && h.Dbg.LastBreak?.Address == 0xE011 && h.Dbg.LastBreak.AccessPc == a["echo_write"], $"{h.Dbg.LastBreak}");
            h.Dbg.Remove(w.Id);
            var w2 = h.Dbg.Add("$F011", AccessKind.Write);   // the echo of the echo... no: $F011 is the echo of $D011
            var w3 = h.Dbg.Add("$E011", AccessKind.Write);
            var neg = h.Dbg.Add("$C014", AccessKind.ReadWrite);
            o = h.Runner.Run(10);
            Check("SM83: an echo address in the spec folds", o == RunOutcome.Break && h.Dbg.LastBreak?.BreakpointId == w3.Id && w3.Resolved == "cpu:$C011", $"{h.Dbg.LastBreak} ({w3.Resolved})");
            h.Dbg.Remove(w3.Id);
            o = h.Runner.Run(10);
            Check("SM83 negative: other bytes stay silent", w2.Hits == 0 && neg.Hits == 0, $"$F011 {w2.Hits}, $C014 {neg.Hits}");
        }

        using (var h = Open())
        {
            var w = h.Dbg.Add("$C105", AccessKind.Read);
            var o = h.Runner.Run(10);
            Check("SM83: read watch sees the OAM DMA read", o == RunOutcome.Break && h.Dbg.LastBreak?.Source == AccessSource.OamDma, $"{h.Dbg.LastBreak}");
            h.Dbg.Remove(w.Id);
            var ow = h.Dbg.Add("oam:$05", AccessKind.Write);
            o = h.Runner.Run(10);
            Check("SM83: write watch on OAM sees the DMA", o == RunOutcome.Break && h.Dbg.LastBreak?.Source == AccessSource.OamDma && h.Dbg.LastBreak.Address == 0xFE05, $"{h.Dbg.LastBreak}");
            h.Dbg.Remove(ow.Id);
            var vb = h.Dbg.Add("$0040", AccessKind.Exec);
            o = h.Runner.Run(5);
            var st = h.Dbg.CallStack();
            Check("SM83: interrupt entries are frames (VBLANK)", o == RunOutcome.Break && st.Count > 0 && st[^1].Kind == "VBLANK" && st[^1].Target == 0x40, string.Join(" | ", st));
            h.Dbg.Remove(vb.Id);
            var c = h.Dbg.Add("after_call", AccessKind.Exec, condition: "[counter] == 3 && SP == $FFFE");
            o = h.Runner.Run(10);
            Check("SM83: condition", o == RunOutcome.Break && h.Dbg.ReadVariable("counter")[0] == 3, $"{h.Dbg.LastBreak}");
        }

        using (var h = DebugHost.Open(rom, "debugtest.gb", ConsoleKind.GameBoy))
        {
            int n = h.Dbg.Symbols.Load(noiPath, "gb");
            bool ok = h.Dbg.Symbols.TryFind("_bank2_func", out var bf, out _) && h.Dbg.Symbols.TryFind("_counter", out var cn, out _) && !h.Dbg.Symbols.TryFind("l__DATA", out _, out _);
            var bp = h.Dbg.Add("_bank2_func", AccessKind.Exec);
            var o = h.Runner.Run(10);
            Check("GBDK .noi: banked symbols, constants skipped", n == 4 && ok && bf!.Bank == 2 && bf.Address == 0x4000 && bp.Resolved == "rom:$8000" && o == RunOutcome.Break, $"{n} symbols, {bf}, {bp.Resolved}, {o}");
        }

        using (var plain = DebugHost.Open(rom, "debugtest.gb", ConsoleKind.GameBoy, debugger: false))
        using (var h = Open())
        {
            plain.RunPlain(120);
            h.Dbg.Log = null;
            h.Dbg.Add("$0000-$FFFF", AccessKind.ReadWrite, tracepoint: true);
            h.Dbg.Add("sub2", AccessKind.Exec, hits: "%40");
            int breaks = 0;
            while (h.Runner.FramesDone < 120) { if (h.Runner.Run(120 - h.Runner.FramesDone) == RunOutcome.Break) breaks++; }
            Check("Game Boy: attached and breaking, frame-identical to no debugger", h.PictureHash() == plain.PictureHash() && h.RamHash() == plain.RamHash() && breaks > 3,
                  $"picture {h.PictureHash():X16} vs {plain.PictureHash():X16}, RAM {h.RamHash():X16}, {breaks} breaks");
            var board = ((GbSession)h.Session!).Board;
            h.Dbg.Detach();
            Check("Game Boy detached: no hook left", board.DebugHook == null && board.DebugTap == null, "board hooks null");
        }
    }

    // ================================================================================================================================ WideMess
    private static void WideMess()
    {
        string root = FindRepoFile(Path.Combine("WideMess", "testroms", "bin", "layers.wms"));
        if (root == "") { NotRun("WideMess", "WideMess/testroms/bin/layers.wms not found"); return; }
        var rom = File.ReadAllBytes(root);
        using var plain = DebugHost.Open(rom, "layers.wms", ConsoleKind.WideMess, debugger: false);
        using var h = DebugHost.Open(rom, "layers.wms", ConsoleKind.WideMess);
        Check("WideMess: the debugger opens the WideMess board", h.Machine.Console == "widemess" && h.Machine.Cpus.Count == 2, h.Machine.Console);
        var o = h.Runner.Step(() => h.Dbg.Pause());
        uint pc0 = h.Dbg.LastBreak?.Pc ?? 0;
        o = h.Runner.Step(() => h.Dbg.StepInto());
        o = h.Runner.Step(() => h.Dbg.StepInto());
        Check("WideMess: pause and step", o == RunOutcome.Break && h.Dbg.Machine.Main.TryGetRegister("PC", out _) && h.Dbg.TraceTail(3).Length == 3, $"started at {pc0:X6}, now {h.Dbg.LastBreak}");
        h.Dbg.Log = null;
        var w = h.Dbg.Add("wram:$0000-$1FFFF", AccessKind.Write, tracepoint: true);
        h.Runner.Run(60);
        plain.RunPlain(60);
        Check("WideMess: watches see work RAM writes", w.Hits > 0, $"{w.Hits} writes");
        Check("WideMess: attached, frame-identical to no debugger", h.PictureHash() == plain.PictureHash() && h.RamHash() == plain.RamHash(), $"{h.PictureHash():X16} vs {plain.PictureHash():X16}");
    }

    private static string FindRepoFile(string rel)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string p = Path.Combine(d.FullName, rel);
            if (File.Exists(p)) return p;
        }
        string here = Path.Combine(Directory.GetCurrentDirectory(), rel);
        return File.Exists(here) ? here : "";
    }

    // ================================================================================================================================ real games
    private static void Games(string vrunPath, string superPath)
    {
        string mlb = Path.ChangeExtension(vrunPath, ".mlb");
        if (!File.Exists(vrunPath) || !File.Exists(mlb)) NotRun("VRUN (NES) with its .mlb", $"{vrunPath} or its .mlb is not on this machine");
        else
        {
            var rom = File.ReadAllBytes(vrunPath);
            using var plain = DebugHost.Open(rom, Path.GetFileName(vrunPath), debugger: false);
            using var h = DebugHost.Open(rom, Path.GetFileName(vrunPath));
            int n = h.Dbg.Symbols.Load(mlb, "nes");
            var nmi = h.Dbg.Add("runtime_nmi", AccessKind.Exec);
            var o = h.Runner.Run(120);
            var st = h.Dbg.CallStack();
            Check("VRUN: exec breakpoint on a NESFab label (runtime_nmi)", o == RunOutcome.Break && h.Dbg.LastBreak?.PcSymbol == "runtime_nmi@31_1" && st.Count > 0 && st[^1].Kind == "NMI",
                  $"{n} symbols; {h.Dbg.LastBreak}; stack {string.Join(" | ", st)}");
            string tl = h.Dbg.FormatTrace(h.Dbg.TraceTail(1)[0]);
            Check("VRUN: trace lines carry NESFab symbols", tl.EndsWith("; runtime_nmi@31_1"), tl);
            h.Dbg.Remove(nmi.Id);
            var pads = h.Dbg.Add("raw_pads", AccessKind.Write);
            o = h.Runner.Run(30);
            Check("VRUN: watch on a variable by name (raw_pads)", o == RunOutcome.Break && h.Dbg.LastBreak?.Address == 0x0010, $"{h.Dbg.LastBreak}");
            h.Dbg.Remove(pads.Id);
            // frame-identity on the real game: 600 frames with watches on all of RAM and tracepoints on all code
            h.Dbg.Log = null;
            h.Dbg.Add("$0000-$07FF", AccessKind.ReadWrite, tracepoint: true);
            h.Dbg.Add("prg:$0-$7FFFF", AccessKind.Exec, tracepoint: true);
            long done = h.Runner.FramesDone;
            h.Runner.Run(600 - done);
            plain.RunPlain((int)h.Runner.FramesDone);
            Check("VRUN: debugger attached, frame-identical to no debugger", h.PictureHash() == plain.PictureHash() && h.RamHash() == plain.RamHash(), $"{h.Runner.FramesDone} frames, picture {h.PictureHash():X16} vs {plain.PictureHash():X16}");
        }

        string sym = Path.ChangeExtension(superPath, ".sym");
        if (!File.Exists(superPath) || !File.Exists(sym)) NotRun("Super VRUN with its .sym", $"{superPath} or its .sym is not on this machine");
        else
        {
            var rom = File.ReadAllBytes(superPath);
            using var plain = DebugHost.Open(rom, Path.GetFileName(superPath), ConsoleKind.Snes, debugger: false);
            using var h = DebugHost.Open(rom, Path.GetFileName(superPath), ConsoleKind.Snes);
            int n = h.Dbg.Symbols.Load(sym, "snes");
            var main = h.Dbg.Add("main", AccessKind.Exec);
            var o = h.Runner.Run(60);
            Check("Super VRUN: exec breakpoint by WLA symbol (main)", o == RunOutcome.Break && h.Dbg.LastBreak?.PcSymbol == "main", $"{n} symbols; {h.Dbg.LastBreak}");
            h.Dbg.Remove(main.Id);
            // the case Mesen's write callbacks missed: work RAM written by DMA through the $2180 port (the boot's WRAM clear)
            var amc = h.Dbg.Add("audio_music_calls", AccessKind.Write);
            o = h.Runner.Run(60);
            var b = h.Dbg.LastBreak;
            Check("Super VRUN: a watch on a variable sees the DMA that writes it through $2180", o == RunOutcome.Break && b?.Source == AccessSource.Dma && b.Address == 0x002180 && (b.PcSymbol ?? "").StartsWith("wram_boot_clear"),
                  $"{b} (resolved {amc.Resolved})");
            Check("Super VRUN: call stack with WLA symbols", h.Dbg.CallStack().Select(f => f.TargetSymbol).SequenceEqual(new[] { "main", "wram_boot_clear" }), string.Join(" | ", h.Dbg.CallStack()));
            h.Dbg.Remove(amc.Id);
            var pk = h.Dbg.Add("pad_keys", AccessKind.Write);
            o = h.Runner.Run(300);
            Check("Super VRUN: watch on a variable by name (pad_keys, written by the NMI's pad scan)", o == RunOutcome.Break && (h.Dbg.LastBreak?.PcSymbol ?? "").Contains("ScanPads"), $"{h.Dbg.LastBreak}");
            h.Dbg.Remove(pk.Id);
            h.Dbg.Log = null;
            var cpuW = h.Dbg.Add("vram:$0000-$FFFF", AccessKind.Write, tracepoint: true, condition: "source == 0");
            var dmaW = h.Dbg.Add("vram:$0000-$FFFF", AccessKind.Write, tracepoint: true, condition: "source != 0");
            h.Runner.Run(300 - h.Runner.FramesDone);
            plain.RunPlain((int)h.Runner.FramesDone);
            Check("Super VRUN: a VRAM watch sees the DMA uploads", dmaW.Hits > 1000, $"{dmaW.Hits} VRAM bytes by DMA, {cpuW.Hits} by the CPU in {h.Runner.FramesDone} frames");
            Check("Super VRUN: debugger attached (every access on the slow path), frame-identical to no debugger", h.PictureHash() == plain.PictureHash() && h.RamHash() == plain.RamHash(),
                  $"{h.Runner.FramesDone} frames, picture {h.PictureHash():X16} vs {plain.PictureHash():X16}");
        }
    }
}

internal static class DebuggerTestExtensions
{
    public static void TrySetRegisterForTest(this Debugger d, string name, long v) => d.Machine.Main.TrySetRegister(name, v);
}
