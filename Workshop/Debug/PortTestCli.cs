using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator.Debugging;
using NesEmulator.Debugging.Port;
using NesEmulator.Systems;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace BrokenNes.Workshop.Debug;

/// <summary>
/// <c>--port-test [--only nes,snes,wms,gb,tools,games] [--keep &lt;dir&gt;]</c>: the debug port's suite (BrokenDev milestone D7). Each console's
/// test ROM (PortTestRoms) sends every kind of message; the checks read them back exactly (text, values, place, frame, timers to the cycle),
/// and each has a negative control: the same ROM writing next to the port (nothing), the port detached (nothing more), the game frame-identical
/// with the port on, off and without a debugger at all. Exit 0 when every check passes.
/// </summary>
internal static class PortTestCli
{
    private static int pass, fail, notRun;
    private static readonly List<string> failures = new();

    internal static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
        if (ok) pass++; else { fail++; failures.Add(name + ": " + detail); }
    }

    internal static void NotRun(string name, string why) { Console.WriteLine($"  NOT RUN  {name}: {why}"); notRun++; }

    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        string Opt(string n, string d) { int i = Array.IndexOf(args, "--" + n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        var only = Opt("only", "nes,snes,wms,gb,tools,games").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string dir = Opt("keep", Path.Combine(Path.GetTempPath(), "brokennes-port-test-" + Environment.ProcessId));
        Directory.CreateDirectory(dir);
        if (Opt("write-roms", "") is { Length: > 0 } romDir)
        {
            Directory.CreateDirectory(romDir);
            var (nr, _, nm) = PortTestRoms.Nes(); DebugTestRoms.Write(romDir, "porttest.nes", nr); DebugTestRoms.Write(romDir, "porttest.mlb", nm);
            var (sr, _, ss) = PortTestRoms.Snes(); DebugTestRoms.Write(romDir, "porttest.sfc", sr); DebugTestRoms.Write(romDir, "porttest.sym", ss);
            var (gr, _, gs) = PortTestRoms.GameBoy(); DebugTestRoms.Write(romDir, "porttest.gb", gr); DebugTestRoms.Write(romDir, "porttest_gb.sym", gs);
            Console.WriteLine($"wrote the debug port test ROMs to {romDir}");
            return 0;
        }
        var sw = Stopwatch.StartNew();
        void Group(string key, string title, Action body)
        {
            if (!only.Contains(key)) return;
            Console.WriteLine($"== {title}");
            try { body(); }
            catch (Exception e) { Check(title + " (ran to the end)", false, e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace); }
        }
        Group("nes", "NES ($401C-$401F)", () => Registers(dir, "nes"));
        Group("snes", "SNES ($4230-$423F)", () => Registers(dir, "snes"));
        Group("wms", "WideMess ($4230-$423F)", () => Registers(dir, "wms"));
        Group("gb", "Game Boy (ld d,d messages, ld b,b)", () => GameBoy(dir));
        Group("tools", "Where the events go (checks, profile, movies, bug captures, the debugger)", () => PortToolsTest.Run(dir));
        Group("games", "The games' dev builds", () => PortToolsTest.Games(dir));
        Console.WriteLine();
        Console.WriteLine($"port-test: {pass} pass, {fail} fail, {notRun} not run ({sw.Elapsed.TotalSeconds:F1} s); files in {dir}");
        foreach (var f in failures) Console.WriteLine("  FAILED " + f);
        return fail == 0 ? 0 : 1;
    }

    internal static (byte[] rom, string name, ConsoleKind kind, string sym, string family) Rom(string dir, string console, bool real = true)
    {
        switch (console)
        {
            case "nes": { var (r, _, m) = PortTestRoms.Nes(real ? (ushort)0x401C : (ushort)0x4018); return (r, "porttest.nes", ConsoleKind.Nes, DebugTestRoms.Write(dir, real ? "porttest.mlb" : "porttest_neg.mlb", m), "nes"); }
            case "snes": case "wms": { var (r, _, s) = PortTestRoms.Snes(real ? (ushort)0x4230 : (ushort)0x4240); return (r, "porttest.sfc", console == "wms" ? ConsoleKind.WideMess : ConsoleKind.Snes, DebugTestRoms.Write(dir, real ? "porttest.sym" : "porttest_neg.sym", s), "snes"); }
            default: { var (r, _, s) = PortTestRoms.GameBoy(real); return (r, "porttest.gb", ConsoleKind.GameBoy, DebugTestRoms.Write(dir, real ? "porttest_gb.sym" : "porttest_gb_neg.sym", s), "gb"); }
        }
    }

    internal static (DebugHost h, DebugPort port) Open(string dir, string console, bool real = true, bool attach = true)
    {
        if (console == "wms") Consoles.WideMessEnabled = true;
        var (rom, name, kind, sym, family) = Rom(dir, console, real);
        var h = DebugHost.Open(rom, name, kind);
        h.Dbg.Symbols.Load(sym, family);
        var port = new DebugPort(h.Machine, h.Dbg);
        if (attach) port.Attach();
        return (h, port);
    }

    private static string Texts(IEnumerable<PortEvent> e) => string.Join(" | ", e.Select(x => x.Text));

    // ================================================================================================ NES, SNES, WideMess

    private static void Registers(string dir, string console)
    {
        string C = console.ToUpperInvariant();
        var (h, port) = Open(dir, console);
        using (h)
        {
            h.RunPlain(8);
            var ev = port.Events;
            var prints = ev.Where(e => e.Kind == PortEventKind.Print).ToList();
            Check($"{C}: a text print", prints.Count > 0 && prints[0].Text == PortTestRoms.Hello && prints[0].PcSymbol?.StartsWith(console == "nes" ? "say_hello" : "long_cmd") == true,
                  prints.Count > 0 ? $"\"{prints[0].Text}\" at {prints[0].Place}, frame {prints[0].Frame}" : "no print");
            Check($"{C}: a print with values (%hu %u %02hx %hd)", prints.Any(p => p.Text == PortTestRoms.RoomExpected), Texts(prints));
            Check($"{C}: a print by pointer", prints.Any(p => p.Text == PortTestRoms.PointerText), Texts(prints));
            var marks = port.Markers;
            Check($"{C}: markers at start-up and on the fifth frame", marks.Count == 2 && marks[0].Text == PortTestRoms.StartMark && marks[1].Text == PortTestRoms.FiveMark && marks[1].PcSymbol?.StartsWith("do_mark") == true,
                  string.Join(" | ", marks.Select(m => $"{m.Text} (frame {m.Frame}, {m.Place})")));
            var asserts = port.Asserts;
            Check($"{C}: one assert, with its value and place", asserts.Count == 1 && asserts[0].Text == PortTestRoms.AssertExpected && asserts[0].PcSymbol?.StartsWith("do_assert") == true,
                  string.Join(" | ", asserts.Select(a => $"\"{a.Text}\" at {a.Place}, frame {a.Frame}")));
            var errors = ev.Where(e => e.Kind == PortEventKind.Error).ToList();
            int wantErrors = console == "nes" ? 1 : 2;   // the unknown command; the SNES also writes a reserved register
            Check($"{C}: wrong use is an error event, never silence", errors.Count == wantErrors && errors.Any(e => e.Text.Contains("$55")) && (console == "nes" || errors.Any(e => e.Text.Contains("$423C"))),
                  Texts(errors));
            var work = port.Timer(PortTestRoms.WorkTimer);
            var draw = port.Timer(PortTestRoms.DrawTimer);
            Check($"{C}: the numbered timer (named by command 7) times the loop the same every frame", work != null && work.Calls >= 6 && work.Best == work.Worst && work.Unmatched == 0
                  && (console != "nes" || work.Worst == PortTestRoms.NesWorkCycles),
                  work == null ? "no timer 'work'" : $"{work.Calls} calls, best {work.Best}, worst {work.Worst} {port.CycleUnit}{(console == "nes" ? $" (expected {PortTestRoms.NesWorkCycles})" : "")}, per frame max {work.FrameWorst}");
            Check($"{C}: the timer by name", draw != null && draw.Calls == work?.Calls && draw.Worst > 0 && draw.Unmatched == 0, draw == null ? "no timer 'draw'" : $"{draw.Calls} calls, worst {draw.Worst}");
            long seq = port.EventCount;
            port.Detach();
            h.RunPlain(4);
            Check($"{C}: detached, the port hears nothing more", port.EventCount == seq && !port.Attached, $"{port.EventCount - seq} events after detaching");
        }

        // two ports on one machine (a session's own and the checks'): both hear everything; each detach removes only itself
        var (h2, p1) = Open(dir, console);
        using (h2)
        {
            var p2 = new DebugPort(h2.Machine, h2.Dbg);
            p2.Attach();
            h2.RunPlain(3);
            long a1 = p1.EventCount, a2 = p2.EventCount;
            p1.Detach();
            h2.RunPlain(3);
            bool p2Hears = p2.EventCount > a2 && p1.EventCount == a1;
            p2.Detach();
            bool clean = h2.Nes != null ? h2.Nes.DebugBus!.DebugPortWrite == null : h2.Session is SnesSession sn ? sn.Board.DebugPortWrite == null : true;
            Check($"{C}: two ports on one machine both hear it, each detach removes only itself, and the listener is null again", a1 == a2 && a1 > 0 && p2Hears && clean, $"{a1} and {a2} events; after one detach the other heard {p2.EventCount - a2} more; listener null: {clean}");
        }

        // negative control: the same ROM writing beside the port
        var (hn, pn) = Open(dir, console, real: false);
        using (hn)
        {
            hn.RunPlain(8);
            Check($"{C}: the negative control (the same code writing beside the port) gives nothing", pn.EventCount == 0 && pn.Writes == 0 && pn.Timers.Count == 0, $"{pn.EventCount} events, {pn.Writes} writes");
        }

        // the game runs frame-identically with the port on, off, and with no debugger at all
        var (rom, name, kind, _, _) = Rom(dir, console);
        var (hon, _) = Open(dir, console);
        using var hoff = DebugHost.Open(rom, name, kind);
        using var bare = DebugHost.Open(rom, name, kind, debugger: false);
        using (hon)
        {
            int differ = -1;
            for (int f = 0; f < 30 && differ < 0; f++)
            {
                hon.RunPlain(1); hoff.RunPlain(1); bare.RunPlain(1);
                if (hon.PictureHash() != hoff.PictureHash() || hon.RamHash() != hoff.RamHash() || hoff.RamHash() != bare.RamHash() || hoff.PictureHash() != bare.PictureHash()) differ = f;
            }
            Check($"{C}: frame-identical with the port on, off and without a debugger (30 frames)", differ < 0, differ < 0 ? $"RAM {hon.RamHash():X16}, picture {hon.PictureHash():X16}" : $"frame {differ} differs");
        }
    }

    // ================================================================================================ Game Boy

    private static void GameBoy(string dir)
    {
        var (h, port) = Open(dir, "gb");
        using (h)
        {
            h.RunPlain(10);
            var ev = port.Events;
            var prints = ev.Where(e => e.Kind == PortEventKind.Print).ToList();
            Check("GB: an inline message (ld d,d; jr; dw $6464, $0000)", prints.Count > 0 && prints[0].Text == PortTestRoms.Hello && prints[0].PcSymbol == "say_hello", prints.Count > 0 ? $"\"{prints[0].Text}\" at {prints[0].Place}" : "none");
            Check("GB: bgb's % fields (registers in hex, sums)", prints.Any(p => p.Text == PortTestRoms.GbRegsExpected), Texts(prints));
            Check("GB: GBDK's EMU_printf message ($0200: the format at HL, the values at DE)", prints.Any(p => p.Text == PortTestRoms.GbPrintfExpected), Texts(prints));
            Check("GB: a message jumped over never shows", !ev.Any(e => e.Text.Contains("never")), Texts(ev));
            var marks = port.Markers;
            Check("GB: @mark markers", marks.Count == 2 && marks[0].Text == PortTestRoms.StartMark && marks[1].Text == PortTestRoms.FiveMark, string.Join(" | ", marks.Select(m => $"{m.Text} (frame {m.Frame})")));
            var asserts = port.Asserts;
            Check("GB: one @assert with the library's file and line", asserts.Count == 1 && asserts[0].Text == "main.c:42 frame counter < 3" && asserts[0].PcSymbol == "do_assert", string.Join(" | ", asserts.Select(a => $"{a.Text} at {a.Place}")));
            var breaks = ev.Where(e => e.Kind == PortEventKind.Break).ToList();
            Check("GB: a bare ld b,b is a break; the assert's own ld b,b is part of the assert", breaks.Count == 1 && breaks[0].PcSymbol == "bare_break", string.Join(" | ", breaks.Select(b => b.Place)));
            Check("GB: a plain ld d,d (no $6464) is nothing; no error events", ev.All(e => e.PcSymbol != "plain_ldd") && ev.All(e => e.Kind != PortEventKind.Error), Texts(ev.Where(e => e.Kind == PortEventKind.Error)));
            var work = port.Timer(PortTestRoms.WorkTimer);
            Check("GB: @timer+ / @timer- time the loop the same every frame", work != null && work.Calls >= 4 && work.Best == work.Worst && work.Worst > 0 && work.Unmatched == 0,
                  work == null ? "no timer" : $"{work.Calls} calls, {work.Worst} M-cycles");
        }
        var (hn, pn) = Open(dir, "gb", real: false);
        using (hn)
        {
            hn.RunPlain(10);
            Check("GB: the negative control ($6565 messages, ld c,c) gives nothing", pn.EventCount == 0 && pn.Timers.Count == 0, $"{pn.EventCount} events: {Texts(pn.Events)}");
        }
        var (rom, name, kind, _, _) = Rom(dir, "gb");
        var (hon, _) = Open(dir, "gb");
        using var bare = DebugHost.Open(rom, name, kind, debugger: false);
        using (hon)
        {
            int differ = -1;
            for (int f = 0; f < 30 && differ < 0; f++)
            {
                hon.RunPlain(1); bare.RunPlain(1);
                if (hon.PictureHash() != bare.PictureHash() || hon.RamHash() != bare.RamHash()) differ = f;
            }
            Check("GB: frame-identical with the port on and without a debugger (30 frames)", differ < 0, differ < 0 ? $"RAM {hon.RamHash():X16}" : $"frame {differ} differs");
        }
        // the chained instruction hook: a host's own hook still runs, and is put back on detach
        var (hc, pc) = Open(dir, "gb", attach: false);
        using (hc)
        {
            var board = ((NesEmulator.Systems.GbSession)hc.Session!).Board;
            long seen = 0;
            Action<ushort> mine = _ => seen++;
            board.InstructionHook = mine;
            pc.Attach();
            hc.RunPlain(2);
            pc.Detach();
            Check("GB: the port chains a host's instruction hook and gives it back", seen > 1000 && pc.EventCount > 0 && board.InstructionHook == mine, $"{seen} host calls, {pc.EventCount} events");
            board.InstructionHook = null;
            pc.Attach(); pc.Detach();
            Check("GB: attached and detached alone, the instruction hook is null again", board.InstructionHook == null, "");
        }
    }
}
