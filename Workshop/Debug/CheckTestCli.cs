using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator.Debugging;
using NesEmulator.Debugging.Checks;
using NesEmulator.Systems;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace BrokenNes.Workshop.Debug;

/// <summary>
/// <c>--check-test [--only nes,snes,wms,gb,games] [--keep &lt;dir&gt;]</c>: the hardware checks' conformance suite (BrokenDev milestone D4). Every
/// rule of the catalogue has a test ROM that must trigger it (with the finding naming the rule, the first frame, the PC and its symbol) and
/// a clean one that must not; the clean templates break no rule at all; a game checked runs frame-identical to the same game unchecked.
/// The wms group also runs the WideMess devkit's sample game and its -D WMS_NOCLEAR control from <c>--wmsroms &lt;dir&gt;</c> (default
/// WideMess/testroms/bin): the devkit's start-up clears work RAM.
/// Exit 0 when every check passes.
/// </summary>
internal static class CheckTestCli
{
    private static int pass, fail, notRun;
    private static readonly List<string> failures = new();
    private static readonly HashSet<string> triggered = new(), cleared = new();

    private static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
        if (ok) pass++; else { fail++; failures.Add(name + ": " + detail); }
    }

    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        string Opt(string n, string d) { int i = Array.IndexOf(args, "--" + n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        var only = Opt("only", "nes,snes,wms,gb,games").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        dir = Opt("keep", Path.Combine(Path.GetTempPath(), "brokennes-check-test-" + Environment.ProcessId));
        wmsRoms = Opt("wmsroms", wmsRoms);
        Directory.CreateDirectory(dir);
        var sw = Stopwatch.StartNew();
        void Group(string key, string title, Action body)
        {
            if (!only.Contains(key)) return;
            Console.WriteLine($"== {title}");
            try { body(); }
            catch (Exception e) { Check(title + " (ran to the end)", false, e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace); }
        }
        Group("nes", "NES rules (CPU_FIX / PPU_FIX / APU_FIX)", Nes);
        Group("snes", "SNES rules", () => Snes(ConsoleKind.Snes));
        Group("wms", "WideMess (the SNES rules on the WideMess console, the debug port)", WideMess);
        Group("gb", "Game Boy rules", GameBoy);
        Group("games", "Real games", () => Games(Opt("vrun", @"P:\BrokenDev\env\out\vrun-nes\vrun_game.nes"), Opt("supervrun", @"P:\BrokenDev\env\out\supervrun\supervrun.sfc")));
        if (only.Contains("nes") && only.Contains("snes") && only.Contains("gb"))
        {
            var missing = CheckCatalogue.All.Where(r => !triggered.Contains(r.Id) || !cleared.Contains(r.Id)).Select(r => r.Id).ToList();
            Check("every rule of the catalogue has a trigger and a negative control", missing.Count == 0, missing.Count == 0 ? $"{CheckCatalogue.All.Length} rules" : "missing: " + string.Join(", ", missing));
        }
        Console.WriteLine();
        Console.WriteLine($"check-test: {pass} pass, {fail} fail, {notRun} not run ({sw.Elapsed.TotalSeconds:F1} s); files in {dir}");
        foreach (var f in failures) Console.WriteLine("  FAILED " + f);
        return fail == 0 ? 0 : 1;
    }

    private static string dir = "";

    /// <summary>Runs a ROM with the checks on, its symbols loaded; returns the findings and the checks.</summary>
    private static (List<Finding> findings, ConsoleChecks checks) RunRom(byte[] rom, string name, ConsoleKind console, string sym, int frames, CheckOptions? opt = null, Action<CheckedGame>? before = null)
    {
        if (console == ConsoleKind.WideMess) Consoles.WideMessEnabled = true;
        string romPath = Path.Combine(dir, name), symPath = Path.ChangeExtension(romPath, console == ConsoleKind.Nes ? ".mlb" : ".sym");
        File.WriteAllBytes(romPath, rom); File.WriteAllText(symPath, sym);
        using var g = CheckedGame.Open(rom, name, console);
        g.Dbg.Symbols.Load(symPath, CheckCatalogue.FamilyOf(g.Host.Machine.Console));
        before?.Invoke(g);
        var c = g.Attach(opt ?? new CheckOptions());
        g.Run(frames, new List<CheckedGame.Step>());
        c.Finish();
        return (c.Findings.ToList(), c);
    }

    /// <summary>The pair every rule gets: the trigger must report the rule (at <paramref name="place"/> when given, with the PC named by a
    /// symbol of the test ROM), the negative control must not report it.</summary>
    private static void Pair(string rule, Func<(List<Finding>, ConsoleChecks)> trigger, Func<(List<Finding>, ConsoleChecks)> clean, string? place = null, long fromFrame = 0, Func<Finding, bool>? also = null, string what = "")
    {
        var (tf, _) = trigger();
        var hit = tf.Where(f => f.Rule == rule).ToList();
        var first = hit.FirstOrDefault(f => place == null || f.Place == place) ?? hit.FirstOrDefault();
        bool okPlace = first != null && (place == null || first.Place == place) && (also == null || also(first));
        Check($"{rule}: the trigger{(what.Length > 0 ? " (" + what + ")" : "")} reports it", okPlace,
            first == null ? "no finding (" + string.Join("; ", tf.Select(f => f.Rule + "@" + f.Place)) + ")" : first.ToString());
        if (okPlace) triggered.Add(rule);
        var (cf, _) = clean();
        var bad = cf.Where(f => f.Rule == rule && f.FirstFrame >= fromFrame).ToList();
        Check($"{rule}: the negative control stays silent", bad.Count == 0, bad.Count == 0 ? "no finding" : string.Join("; ", bad));
        if (bad.Count == 0) cleared.Add(rule);
    }

    private static byte[] B(params byte[] b) => b;
    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    // ================================================================================================================================ NES
    private static readonly byte[] NesRender = B(0xA9, 0x18, 0x8D, 0x01, 0x20);                       // rendering on
    private static readonly byte[] NesDelay = B(0xA0, 0x02, 0xA2, 0x00, 0xCA, 0xD0, 0xFD, 0x88, 0xD0, 0xF8);   // ~2600 cycles: out of vblank

    private static (List<Finding>, ConsoleChecks) NesRun(CheckTestRoms.Slots s, int frames = 30, string name = "nes", Action<CheckedGame>? before = null)
    {
        var (rom, _, mlb) = CheckTestRoms.Nes(s);
        return RunRom(rom, name + ".nes", ConsoleKind.Nes, mlb, frames, before: before);
    }

    private static void Nes()
    {
        var (clean, _) = NesRun(new CheckTestRoms.Slots(), 60, "nes_clean");
        Check("NES: the clean template breaks no rule", clean.Count == 0, clean.Count == 0 ? "no finding in 60 frames" : string.Join("; ", clean));
        var (clean30, _) = NesRun(new CheckTestRoms.Slots { Mapper30 = true }, 60, "nes_clean30");
        Check("NES: the clean template on the self-flashing mapper-30 board breaks no rule", clean30.Count == 0, clean30.Count == 0 ? "no finding" : string.Join("; ", clean30));

        Pair("nes.ram-uninit", () => NesRun(new() { Pre = B(0xAD, 0x45, 0x03) }), () => NesRun(new() { Pre = B(0x8D, 0x45, 0x03, 0xAD, 0x45, 0x03) }),
            place: "ram:$0345", also: f => f.PcSymbol == "pre" && f.FirstFrame <= 1);
        // the 6502's dummy reads are not reads: LDA $02F8,X and STA $02F8,X with X = $10 read $0208 (never written) on their way to $0308
        {
            Breakpoint? watch = null;
            var (f, _) = NesRun(new() { Pre = B(0xA9, 0x00, 0x8D, 0x08, 0x03, 0xA2, 0x10, 0xBD, 0xF8, 0x02, 0x9D, 0xF8, 0x02) }, 5, "nes_dummy",
                g => { g.Dbg.Log = null; watch = g.Dbg.Add("$0208", AccessKind.Read, tracepoint: true); });
            bool silent = !f.Any(x => x.Rule == "nes.ram-uninit");
            Check("nes.ram-uninit negative: the 6502's dummy reads (page-crossing LDA abs,X / STA abs,X) are not reads", silent && watch!.Hits >= 2,
                $"{(silent ? "no finding" : string.Join("; ", f))}; the bus saw {watch!.Hits} dummy reads of $0208");
        }
        Pair("nes.ppu-render-write", () => NesRun(new() { Init = NesRender, Main = Cat(NesDelay, B(0xA9, 0x00, 0x8D, 0x07, 0x20)) }),
            () => NesRun(new() { Init = NesRender, Nmi = B(0xA9, 0x00, 0x8D, 0x07, 0x20) }), place: "main");
        Pair("nes.oam-dma-render", () => NesRun(new() { Init = NesRender, Main = Cat(NesDelay, B(0xA9, 0x02, 0x8D, 0x14, 0x40)) }),
            () => NesRun(new() { Init = NesRender, Nmi = B(0xA9, 0x02, 0x8D, 0x14, 0x40) }), place: "main");
        Pair("nes.frame-irq", () => NesRun(new() { Init = B(0xA9, 0x00, 0x8D, 0x17, 0x40, 0x58), Irq = B(0x40) }),
            () => NesRun(new() { Init = B(0xA9, 0x00, 0x8D, 0x17, 0x40, 0x58), Irq = B(0x2C, 0x15, 0x40, 0x40) }), place: "irq");
        var pad = B(0xA9, 0x01, 0x8D, 0x16, 0x40, 0xA9, 0x00, 0x8D, 0x16, 0x40, 0xAD, 0x16, 0x40);
        Pair("nes.dmc-conflict", () => NesRun(new() { Init = B(0xA9, 0x4F, 0x8D, 0x10, 0x40, 0xA9, 0x00, 0x8D, 0x12, 0x40, 0xA9, 0xFF, 0x8D, 0x13, 0x40, 0xA9, 0x10, 0x8D, 0x15, 0x40), Main = pad }, 60),
            () => NesRun(new() { Main = pad }, 60), place: "main");
        Pair("nes.unofficial-opcode", () => NesRun(new() { Main = B(0xA7, 0x20) }), () => NesRun(new() { Main = B(0xA5, 0x20, 0xAA) }), place: "$A7 LAX zp",
            also: f => f.Detail.Contains("LAX"));
        Pair("nes.open-bus", () => NesRun(new() { Main = B(0xAD, 0x01, 0x20) }), () => NesRun(new() { Main = B(0xAD, 0x02, 0x20) }), place: "$2001",
            also: f => f.PcSymbol == "main");
        Pair("nes.stack-wrap", () => NesRun(new() { Init = B(0xA2, 0x01, 0x9A, 0x48, 0x48, 0x48, 0xA2, 0xFF, 0x9A) }),
            () => NesRun(new() { Init = B(0xA2, 0x10, 0x9A, 0x48, 0x48, 0x48, 0xA2, 0xFF, 0x9A) }), place: "init");
        Pair("nes.stack-overflow", () => NesRun(new() { Init = B(0xAD, 0x40, 0x01, 0xA2, 0x40, 0x9A, 0x48, 0xA2, 0xFF, 0x9A) }),
            () => NesRun(new() { Init = B(0xAD, 0x40, 0x01, 0xA2, 0x50, 0x9A, 0x48, 0xA2, 0xFF, 0x9A) }), place: "ram:$0140");
        Pair("nes.lag-frame", () => NesRun(new() { Main = B(0xA0, 0x30, 0xA2, 0x00, 0x8D, 0x00, 0x03, 0xCA, 0xD0, 0xFA, 0x88, 0xD0, 0xF5) }, 60),
            () => NesRun(new() { Main = B(0xA2, 0x10, 0x8D, 0x00, 0x03, 0xCA, 0xD0, 0xFA) }, 60), place: "main", fromFrame: 5);
        Pair("nes.flash-write", () => NesRun(new() { Mapper30 = true, Main = B(0xA9, 0x00, 0x8D, 0x00, 0x80) }, 10),
            () => NesRun(new() { Mapper30 = true, Main = B(0xA9, 0x01, 0x8D, 0x00, 0xC0) }, 10), place: "main");

        // the profiler on the lag ROM: lag frames counted, the main loop busy for whole frames, the NMI's OAM DMA counted
        {
            var (_, c) = NesRun(new() { Main = B(0xA0, 0x30, 0xA2, 0x00, 0x8D, 0x00, 0x03, 0xCA, 0xD0, 0xFA, 0x88, 0xD0, 0xF5) }, 60, "nes_prof");
            var p = CheckRun.Summarize(c, 10);
            var (_, c2) = NesRun(new() { Nmi = B(0xA9, 0x02, 0x8D, 0x14, 0x40) }, 60, "nes_prof2");
            var p2 = CheckRun.Summarize(c2, 10);
            // the lag ROM's main loop works 3.7 frames, then waits for the next NMI: ~88% busy, 3 frames in 4 lagged
            Check("NES profiler: a 3.7-frame loop is over 80% busy with lag frames; the idle template is under 5% with none", p.BusyAverage > 80 && p.LagFrames >= 30 && p2.BusyAverage < 5 && p2.LagFrames == 0,
                $"busy {p.BusyAverage:F1}% / {p.LagFrames} lag frames; idle {p2.BusyAverage:F1}% / {p2.LagFrames}");
            Check("NES profiler: OAM DMA bytes per frame and vblank use", p2.DmaAverage >= 255 && p2.DmaWorst == 256 && p2.UploadWorst > 20 && p2.UploadWorst < 60,
                $"DMA {p2.DmaAverage:F0} bytes/frame (worst {p2.DmaWorst}), vblank upload worst {p2.UploadWorst:F1}%");
            var fn = p.Functions.FirstOrDefault(x => x.Name == "nmi");
            Check("NES profiler: functions by symbol (the NMI handler, inclusive and exclusive)", fn != null && fn.Calls >= 50 && fn.Inclusive >= fn.Exclusive && fn.Exclusive > 0,
                fn == null ? string.Join(", ", p.Functions.Select(x => x.Name)) : $"nmi: {fn.Calls} calls, inclusive {fn.Inclusive}, exclusive {fn.Exclusive}");
        }

        // checks on, frame-identical to checks off (the negative control of the whole engine)
        {
            var s = new CheckTestRoms.Slots { Init = NesRender, Nmi = B(0xA9, 0x02, 0x8D, 0x14, 0x40) };
            var (rom, _, _) = CheckTestRoms.Nes(s);
            using var plain = DebugHost.Open(rom, "nes_ident.nes", debugger: false);
            using var g = CheckedGame.Open(rom, "nes_ident.nes");
            var c = g.Attach(new CheckOptions());
            plain.RunPlain(120); g.Run(120, new());
            Check("NES: checks on, frame-identical to checks off", g.Host.PictureHash() == plain.PictureHash() && g.Host.RamHash() == plain.RamHash(), $"picture {g.Host.PictureHash():X16} vs {plain.PictureHash():X16}");
            g.Dbg.Detach();
            Check("NES: checks removed, no hook left", g.Host.Nes!.DebugHook == null && g.Host.Nes.DebugBus!.DebugTap == null, "NES.DebugHook and Bus.DebugTap are null");
        }
    }

    // ================================================================================================================================ SNES
    private static readonly byte[] SnesDelay = B(0xA0, 0x08, 0xA2, 0x00, 0xCA, 0xD0, 0xFD, 0x88, 0xD0, 0xF8);   // out of vblank into the picture
    private static (List<Finding>, ConsoleChecks) SnesRun(CheckTestRoms.Slots s, ConsoleKind console, int frames = 20, string name = "snes", byte[]? spc = null)
    {
        var (rom, _, sym) = CheckTestRoms.Snes(s, spc);
        return RunRom(rom, name + ".sfc", console, sym, frames);
    }

    /// <summary>HDMA channel 1 to COLDATA ($2132), repeat mode every line, from a table at $F000; switched on in the NMI (vblank) when asked.</summary>
    private static CheckTestRoms.Slots WithHdma(CheckTestRoms.Slots s, bool enableInNmi)
    {
        var table = new List<byte> { 0xF0 };
        table.AddRange(Enumerable.Repeat((byte)0xE0, 112)); table.Add(0xF0); table.AddRange(Enumerable.Repeat((byte)0xE0, 112)); table.Add(0x00);
        s.Fixed = new[] { (0xF000u, table.ToArray()) };
        s.Init = Cat(B(0x9C, 0x10, 0x43, 0xA9, 0x32, 0x8D, 0x11, 0x43, 0x9C, 0x12, 0x43, 0xA9, 0xF0, 0x8D, 0x13, 0x43, 0x9C, 0x14, 0x43), s.Init);
        if (enableInNmi) s.Nmi = Cat(s.Nmi, B(0xA9, 0x02, 0x8D, 0x0C, 0x42));
        return s;
    }

    // a 1 KB DMA from the zero byte to work RAM through $2180 (channel 0)
    private static readonly byte[] SnesDma1k = B(0x9C, 0x81, 0x21, 0x9C, 0x82, 0x21, 0x9C, 0x83, 0x21, 0xA9, 0x08, 0x8D, 0x00, 0x43, 0xA9, 0x80, 0x8D, 0x01, 0x43,
                                                0x9C, 0x05, 0x43, 0xA9, 0x04, 0x8D, 0x06, 0x43, 0xA9, 0x01, 0x8D, 0x0B, 0x42);
    private static byte[] SnesVramDma(byte pages) => B(0xA9, 0x80, 0x8D, 0x15, 0x21, 0x9C, 0x16, 0x21, 0x9C, 0x17, 0x21, 0xA9, 0x09, 0x8D, 0x00, 0x43, 0xA9, 0x18, 0x8D, 0x01, 0x43,
                                                        0x9C, 0x05, 0x43, 0xA9, pages, 0x8D, 0x06, 0x43, 0xA9, 0x01, 0x8D, 0x0B, 0x42);

    private static void Snes(ConsoleKind console)
    {
        string k = console == ConsoleKind.WideMess ? "wms" : "snes";
        var (clean, _) = SnesRun(new CheckTestRoms.Slots(), console, 60, k + "_clean");
        var cleanNow = clean.Where(f => !(f.Rule == "snes.lag-frame" && f.FirstFrame <= 5)).ToList();
        Check($"{k}: the clean template breaks no rule (the boot's 64 KB clears lag its first frames)", cleanNow.Count == 0, cleanNow.Count == 0 ? $"no finding in 60 frames ({clean.Count} boot lag)" : string.Join("; ", cleanNow));
        if (console == ConsoleKind.WideMess) return;

        Pair("snes.wram-uninit", () => SnesRun(new() { Pre = B(0xAF, 0x34, 0x12, 0x7E) }, console), () => SnesRun(new() { Pre = B(0x8F, 0x34, 0x12, 0x7E, 0xAF, 0x34, 0x12, 0x7E) }, console),
            place: "wram:$01234", also: f => f.PcSymbol == "pre");
        var vramRead = B(0x9C, 0x16, 0x21, 0xA9, 0x40, 0x8D, 0x17, 0x21, 0xAD, 0x39, 0x21, 0xAD, 0x39, 0x21);
        Pair("snes.vram-uninit", () => SnesRun(new() { Pre = vramRead }, console), () => SnesRun(new() { Init = vramRead }, console), place: "vram:$8000", what: "a $2139 read");
        Pair("snes.vram-uninit", () => SnesRun(new() { ClearVram = false, Tm = 0x01 }, console), () => SnesRun(new() { Tm = 0x01 }, console), place: "BG1 tile map", what: "the picture: BG1's map never written");
        Pair("snes.cgram-uninit", () => SnesRun(new() { ClearCgram = false }, console), () => SnesRun(new(), console), place: "the backdrop colour (cgram:$000)", what: "the picture: the backdrop");
        Pair("snes.oam-uninit", () => SnesRun(new() { ClearOam = false, Tm = 0x10 }, console), () => SnesRun(new() { Tm = 0x10 }, console), place: "OAM (the sprite scan)", what: "the picture: sprites on");
        Pair("snes.dma-hdma", () => SnesRun(WithHdma(new() { Main = Cat(SnesDelay, SnesDma1k) }, true), console), () => SnesRun(WithHdma(new() { Nmi = SnesDma1k }, true), console), place: "main");
        Pair("snes.hdma-midframe", () => SnesRun(WithHdma(new() { Main = Cat(SnesDelay, B(0xA9, 0x02, 0x8D, 0x0C, 0x42)) }, false), console),
            () => SnesRun(WithHdma(new(), true), console), place: "main");
        Pair("snes.ppu-write-active", () => SnesRun(new() { Main = Cat(SnesDelay, B(0x8D, 0x18, 0x21)) }, console), () => SnesRun(new() { Nmi = B(0x8D, 0x18, 0x21) }, console), place: "main");
        Pair("snes.dma-overrun", () => SnesRun(new() { Nmi = SnesVramDma(0x40) }, console), () => SnesRun(new() { Nmi = SnesVramDma(0x04) }, console), place: "nmi_slot");
        Pair("snes.muldiv-early", () => SnesRun(new() { Main = B(0xA9, 0x05, 0x8D, 0x02, 0x42, 0xA9, 0x07, 0x8D, 0x03, 0x42, 0xAD, 0x16, 0x42) }, console),
            () => SnesRun(new() { Main = B(0xA9, 0x05, 0x8D, 0x02, 0x42, 0xA9, 0x07, 0x8D, 0x03, 0x42, 0xEA, 0xEA, 0xEA, 0xEA, 0xAD, 0x16, 0x42) }, console), place: "main", what: "a multiply");
        {
            var (f, _) = SnesRun(new() { Main = B(0xA9, 0x00, 0x8D, 0x04, 0x42, 0xA9, 0x01, 0x8D, 0x05, 0x42, 0xA9, 0x03, 0x8D, 0x06, 0x42, 0xEA, 0xEA, 0xAD, 0x14, 0x42) }, console, 10, "snes_div");
            var (g, _) = SnesRun(new() { Main = B(0xA9, 0x00, 0x8D, 0x04, 0x42, 0xA9, 0x01, 0x8D, 0x05, 0x42, 0xA9, 0x03, 0x8D, 0x06, 0x42, 0xEA, 0xEA, 0xEA, 0xEA, 0xEA, 0xEA, 0xEA, 0xEA, 0xAD, 0x14, 0x42) }, console, 10, "snes_div_ok");
            Check("snes.muldiv-early: a divide read 8 cycles on is reported, 20 cycles on is not", f.Any(x => x.Rule == "snes.muldiv-early") && !g.Any(x => x.Rule == "snes.muldiv-early"),
                $"{f.FirstOrDefault(x => x.Rule == "snes.muldiv-early")}; clean: {g.Count(x => x.Rule == "snes.muldiv-early")} findings");
        }
        Pair("snes.spc-handshake", () => SnesRun(new() { Init = B(0xAD, 0x40, 0x21, 0xC9, 0x55, 0xD0, 0xF9) }, console, 120),
            () => SnesRun(new() { Init = B(0xAD, 0x40, 0x21, 0xC9, 0xAA, 0xD0, 0xF9) }, console, 120), place: "init");
        Pair("snes.echo-overrun", () => SnesRun(new(), console, 30, "snes_echo", CheckTestRoms.SpcEcho(0x02)), () => SnesRun(new(), console, 30, "snes_echo_ok", CheckTestRoms.SpcEcho(0x80)),
            also: f => f.Place.StartsWith("SPC700 code in the echo buffer $0200"));
        Pair("snes.stack-overflow", () => SnesRun(new() { Init = B(0xAD, 0x40, 0x1F, 0xC2, 0x10, 0xA2, 0x40, 0x1F, 0x9A, 0xE2, 0x10, 0x48, 0xC2, 0x10, 0xA2, 0xFF, 0x1F, 0x9A, 0xE2, 0x10) }, console),
            () => SnesRun(new() { Init = B(0xAD, 0x40, 0x1F, 0xC2, 0x10, 0xA2, 0x80, 0x1F, 0x9A, 0xE2, 0x10, 0x48, 0xC2, 0x10, 0xA2, 0xFF, 0x1F, 0x9A, 0xE2, 0x10) }, console), place: "wram:$01F40");
        Pair("snes.open-bus", () => SnesRun(new() { Main = B(0xAD, 0x00, 0x21) }, console), () => SnesRun(new() { Main = B(0xAD, 0x12, 0x42, 0xAD, 0x21, 0x42) }, console), place: "$2100",
            also: f => f.PcSymbol == "main");
        Pair("snes.lag-frame", () => SnesRun(new() { Main = B(0xA0, 0x30, 0xA2, 0x00, 0x8D, 0x00, 0x03, 0xCA, 0xD0, 0xFA, 0x88, 0xD0, 0xF5) }, console, 40),
            () => SnesRun(new() { Main = B(0xA2, 0x10, 0x8D, 0x00, 0x03, 0xCA, 0xD0, 0xFA) }, console, 40), place: "main", fromFrame: 8);

        {
            var (_, c) = SnesRun(new() { Nmi = SnesVramDma(0x04) }, console, 60, "snes_prof");
            var p = CheckRun.Summarize(c, 10);
            Check("SNES profiler: WAI is waiting (busy under 10%, no lag), the NMI's 1 KB VRAM DMA counted and timed", p.BusyAverage < 10 && p.LagFrames == 0 && p.DmaAverage >= 1023 && p.UploadWorst > 5 && p.UploadWorst < 60,
                $"busy {p.BusyAverage:F1}%, {p.LagFrames} lag, DMA {p.DmaAverage:F0} bytes/frame, vblank upload worst {p.UploadWorst:F1}%");
        }
        {
            var (rom, _, _) = CheckTestRoms.Snes(WithHdma(new() { Tm = 0x11, Nmi = SnesVramDma(0x04) }, true), CheckTestRoms.SpcEcho(0x80));
            using var plain = DebugHost.Open(rom, "snes_ident.sfc", ConsoleKind.Snes, debugger: false);
            using var g = CheckedGame.Open(rom, "snes_ident.sfc", ConsoleKind.Snes);
            g.Attach(new CheckOptions());
            plain.RunPlain(120); g.Run(120, new());
            Check("SNES: checks on (watching bus, DMA taps, SPC700 hook), frame-identical to checks off", g.Host.PictureHash() == plain.PictureHash() && g.Host.RamHash() == plain.RamHash(), $"picture {g.Host.PictureHash():X16} vs {plain.PictureHash():X16}");
            var board = ((SnesSession)g.Host.Session!).Board;
            g.Dbg.Detach();
            Check("SNES: checks removed, no hook left", board.DebugHook == null && board.DebugTap == null && !board.Cpu.DebugInterposed && ((SnesSession)g.Host.Session!).Spc!.SmpHook == null, "board hooks null, the CPU on its own bus");
        }
    }

    private static void WideMess()
    {
        Snes(ConsoleKind.WideMess);
        var c = ConsoleKind.WideMess;
        Pair("snes.wram-uninit", () => SnesRun(new() { Pre = B(0xAF, 0x34, 0x12, 0x7E) }, c, 10, "wms_wram"), () => SnesRun(new() { Pre = B(0x8F, 0x34, 0x12, 0x7E, 0xAF, 0x34, 0x12, 0x7E) }, c, 10, "wms_wram_ok"),
            place: "wram:$01234", what: "on the WideMess console");
        Pair("snes.ppu-write-active", () => SnesRun(new() { Main = Cat(SnesDelay, B(0x8D, 0x18, 0x21)) }, c, 10, "wms_vram"), () => SnesRun(new() { Nmi = B(0x8D, 0x18, 0x21) }, c, 10, "wms_vram_ok"),
            place: "main", what: "on the WideMess console");
        // the debug port: unlock, read WMID ($4221), write $4230: a register, not open bus, and counted by the reserved hook
        var (f, ch) = SnesRun(new() { Main = B(0xA9, 0x57, 0x8D, 0x20, 0x42, 0xA9, 0x4D, 0x8D, 0x20, 0x42, 0xAD, 0x21, 0x42, 0x8D, 0x30, 0x42) }, c, 10, "wms_port");
        long port = ((SnesChecks)ch).DebugPortWrites;
        Check("WideMess: $4220-$423F unlocked are registers (no open bus) and debug-port writes reach the reserved hook", !f.Any(x => x.Rule == "snes.open-bus") && port >= 5,
            $"{f.Count(x => x.Rule == "snes.open-bus")} open-bus findings, {port} debug-port writes");
        var (g, _) = SnesRun(new() { Main = B(0xAD, 0x21, 0x42) }, ConsoleKind.Snes, 10, "snes_detect");
        Check("SNES: the WideMess detection read of $4221 is the spec's protocol, not an open-bus finding", !g.Any(x => x.Rule == "snes.open-bus"), $"{g.Count(x => x.Rule == "snes.open-bus")} open-bus findings");
        DevkitStartupClear();
    }

    /// <summary>The WideMess devkit's start-up clear of work RAM (BrokenDev/widemess/lib/wmsboot.asm): the sample game, Wide Blaster, reads no
    /// work RAM before writing it; the same game built with -D WMS_NOCLEAR (the start-up before the clear, byte for byte) does. The ROMs are
    /// WideMess/testroms/devkit/build.ps1's, in WideMess/testroms/bin (or --wmsroms).</summary>
    private static void DevkitStartupClear()
    {
        string bin = wmsRoms;
        string smp = Path.Combine(bin, "devkit_sample.wms"), noclr = Path.Combine(bin, "devkit_sample_noclear.wms");
        if (!File.Exists(smp) || !File.Exists(noclr) || !File.Exists(Path.ChangeExtension(smp, ".sym")) || !File.Exists(Path.ChangeExtension(noclr, ".sym")))
        {
            Console.WriteLine($"  NOT RUN  the devkit's start-up clear: {smp}, {noclr} or their symbols are missing (WideMess/testroms/devkit/build.ps1; --wmsroms <dir>)");
            notRun++;
            return;
        }
        List<Finding> Uninit(string path)
        {
            Consoles.WideMessEnabled = true;
            var rom = File.ReadAllBytes(path);
            using var g = CheckedGame.Open(rom, Path.GetFileName(path), ConsoleKind.WideMess);
            g.Dbg.Symbols.Load(Path.ChangeExtension(path, ".sym"), g.Family);
            var c = g.Attach(new CheckOptions());
            g.Run(120, CheckedGame.ParseInput("30:Right+B"));
            c.Finish();
            return c.Findings.Where(f => f.Rule == "snes.wram-uninit").ToList();
        }
        var clean = Uninit(smp);
        Check("WideMess devkit: the sample game reads no work RAM before writing it (the start-up clears it)", clean.Count == 0,
            clean.Count == 0 ? "no snes.wram-uninit in 120 frames" : string.Join("; ", clean));
        var old = Uninit(noclr);
        Check("WideMess devkit: built with -D WMS_NOCLEAR (no clear) it does: the rule still sees the bug", old.Any(f => f.Place == "game_frame"),
            $"{old.Count} variables: " + string.Join(", ", old.Select(f => f.Place)));
    }

    private static string wmsRoms = Path.Combine("WideMess", "testroms", "bin");

    // ================================================================================================================================ Game Boy
    private static (List<Finding>, ConsoleChecks) GbRun(CheckTestRoms.Slots s, int frames = 30, string name = "gb")
    {
        var (rom, _, sym) = CheckTestRoms.GameBoy(s);
        return RunRom(rom, name + ".gb", s.Cgb ? ConsoleKind.GameBoyColor : ConsoleKind.GameBoy, sym, frames);
    }

    private static readonly byte[] GbWaitMode3 = B(0xF0, 0x41, 0xE6, 0x03, 0xFE, 0x03, 0x20, 0xF8);
    private static readonly byte[] GbWaitMode2 = B(0xF0, 0x41, 0xE6, 0x03, 0xFE, 0x02, 0x20, 0xF8);

    private static void GameBoy()
    {
        foreach (bool cgb in new[] { false, true })
        {
            var (clean, _) = GbRun(new CheckTestRoms.Slots { Cgb = cgb }, 60, cgb ? "gbc_clean" : "gb_clean");
            var now = clean.Where(f => !(f.Rule == "gb.lag-frame" && f.FirstFrame <= 8)).ToList();
            Check($"{(cgb ? "Game Boy Color" : "Game Boy")}: the clean template breaks no rule (the boot's clear loops lag its first frames)", now.Count == 0, now.Count == 0 ? $"no finding in 60 frames ({clean.Count} boot lag)" : string.Join("; ", now));
        }
        Pair("gb.ram-uninit", () => GbRun(new() { Pre = B(0xFA, 0x45, 0xC3) }), () => GbRun(new() { Pre = B(0xEA, 0x45, 0xC3, 0xFA, 0x45, 0xC3) }), place: "wram:$0345", also: f => f.PcSymbol == "pre");
        Pair("gb.ram-uninit", () => GbRun(new() { ClearOam = false }), () => GbRun(new()), place: "OAM (the sprite scan)", what: "sprites on, OAM never cleared");
        Pair("gb.vram-mode3", () => GbRun(new() { Main = Cat(GbWaitMode3, B(0xEA, 0x00, 0x98)) }), () => GbRun(new() { Nmi = B(0xEA, 0x00, 0x98) }), place: "main");
        Pair("gb.oam-mode23", () => GbRun(new() { Main = Cat(GbWaitMode2, B(0xEA, 0x00, 0xFE)) }), () => GbRun(new() { Nmi = B(0xEA, 0x00, 0xFE) }), place: "main");
        Pair("gb.oam-dma-hram", () => GbRun(new() { Main = B(0x3E, 0xC1, 0xE0, 0x46, 0x3E, 0x28, 0x3D, 0x20, 0xFD) }), () => GbRun(new() { Main = B(0x3E, 0xC1, 0xCD, 0x80, 0xFF) }), place: "main");
        Pair("gb.lcd-off", () => GbRun(new() { Main = B(0xF0, 0x44, 0xFE, 0x32, 0x20, 0xFA, 0xAF, 0xE0, 0x40) }), () => GbRun(new() { Nmi = B(0xAF, 0xE0, 0x40, 0x3E, 0x93, 0xE0, 0x40) }), place: "main");
        Pair("gb.unusable", () => GbRun(new() { Main = B(0xEA, 0xA0, 0xFE) }), () => GbRun(new() { Nmi = B(0xEA, 0x9F, 0xFE) }), place: "$FEA0-$FEFF", also: f => f.PcSymbol == "main");
        var pal = B(0x3E, 0x80, 0xE0, 0x68, 0xE0, 0x69);
        Pair("gb.cgb-palette-mode3", () => GbRun(new() { Cgb = true, Main = Cat(GbWaitMode3, pal) }), () => GbRun(new() { Cgb = true, Nmi = pal }), place: "main");
        Pair("gb.speed-switch", () => GbRun(new() { Cgb = true, Init = B(0x3E, 0x01, 0xE0, 0x4D) }, 10),
            () => GbRun(new() { Cgb = true, Init = B(0x3E, 0x01, 0xE0, 0x4D, 0xAF, 0xE0, 0xFF, 0x3E, 0x30, 0xE0, 0x00, 0x10, 0x00) }, 10), place: "init", what: "KEY1 armed, no STOP");
        {
            var (f, _) = GbRun(new() { Cgb = true, Init = B(0x3E, 0x01, 0xE0, 0x4D, 0x3E, 0x01, 0xE0, 0xFF, 0x10, 0x00) }, 10, "gbc_stop_ie");
            Check("gb.speed-switch: STOP with IE not $00 is reported", f.Any(x => x.Rule == "gb.speed-switch" && x.Detail.Contains("IE = $01")), string.Join("; ", f.Where(x => x.Rule == "gb.speed-switch")));
        }
        Pair("gb.cart-ram-disabled", () => GbRun(new() { Main = B(0x3E, 0x12, 0xEA, 0x00, 0xA0) }),
            () => GbRun(new() { Main = B(0x3E, 0x0A, 0xEA, 0x00, 0x00, 0x3E, 0x12, 0xEA, 0x00, 0xA0, 0xAF, 0xEA, 0x00, 0x00) }), place: "main");
        Pair("gb.halt-bug", () => GbRun(new() { Init = B(0xF3, 0x3E, 0x01, 0xE0, 0xFF, 0xE0, 0x0F, 0x76, 0x00, 0x00) }),
            () => GbRun(new() { Init = B(0xF3, 0x3E, 0x01, 0xE0, 0xFF, 0xAF, 0xE0, 0x0F, 0x76, 0x00, 0x00) }), place: "init");
        Pair("gb.stack-overflow", () => GbRun(new() { Init = B(0xFA, 0x00, 0xD1, 0x31, 0x02, 0xD1, 0xF5, 0x31, 0x00, 0xE0) }),
            () => GbRun(new() { Init = B(0xFA, 0x00, 0xD1, 0x31, 0x02, 0xD2, 0xF5, 0x31, 0x00, 0xE0) }), place: "wram:$1100");
        // The SM83's SP points at the last byte pushed (the live stack is SP..top), not at the next free one as on the 6502 and the 65816.
        // Trigger: data read at SP-1 (the byte just below the stack), then pushed onto. Negative control: GBDK-2020's (SDCC's) frame,
        // ADD SP,-2, a local written and read at SP+0 through LD HL,SP+0, ADD SP,+2, then a push onto the freed frame (a call, as in
        // _voc_room_get then _set_bkg_palette in the VOC room viewer): the local was stack, not data below the stack.
        Pair("gb.stack-overflow", () => GbRun(new() { Init = B(0x31, 0x02, 0xD1, 0xFA, 0x01, 0xD1, 0xF5, 0x31, 0x00, 0xE0) }, name: "gb_stack_sp1"),
            () => GbRun(new() { Init = B(0x31, 0x02, 0xD1, 0xE8, 0xFE, 0x3E, 0x01, 0xF8, 0x00, 0x77, 0x7E, 0xE8, 0x02, 0xF5, 0x31, 0x00, 0xE0) }, name: "gb_stack_gbdk"),
            place: "wram:$1101", what: "a read just below SP, then a push onto it; control: GBDK's SP+0 local");
        Pair("gb.lag-frame", () => GbRun(new() { Main = B(0x06, 0x40, 0x0E, 0x00, 0xEA, 0x00, 0xC1, 0x0D, 0x20, 0xFA, 0x05, 0x20, 0xF5) }, 60),
            () => GbRun(new() { Main = B(0x0E, 0x10, 0xEA, 0x00, 0xC1, 0x0D, 0x20, 0xFA) }, 60), place: "main", fromFrame: 10);

        {
            var (_, c) = GbRun(new() { Nmi = B(0x3E, 0xC1, 0xCD, 0x80, 0xFF) }, 60, "gb_prof");
            var p = CheckRun.Summarize(c, 12);
            Check("Game Boy profiler: HALT is waiting (busy under 15%, no lag), the vblank handler's OAM DMA counted", p.BusyAverage < 15 && p.LagFrames == 0 && p.DmaAverage >= 159,
                $"busy {p.BusyAverage:F1}%, {p.LagFrames} lag, DMA {p.DmaAverage:F0} bytes/frame, vblank upload worst {p.UploadWorst:F1}%");
        }
        {
            var (rom, _, _) = CheckTestRoms.GameBoy(new() { Nmi = B(0x3E, 0xC1, 0xCD, 0x80, 0xFF) });
            using var plain = DebugHost.Open(rom, "gb_ident.gb", ConsoleKind.GameBoy, debugger: false);
            using var g = CheckedGame.Open(rom, "gb_ident.gb", ConsoleKind.GameBoy);
            g.Attach(new CheckOptions());
            plain.RunPlain(120); g.Run(120, new());
            Check("Game Boy: checks on, frame-identical to checks off", g.Host.PictureHash() == plain.PictureHash() && g.Host.RamHash() == plain.RamHash(), $"picture {g.Host.PictureHash():X16} vs {plain.PictureHash():X16}");
            var board = ((GbSession)g.Host.Session!).Board;
            g.Dbg.Detach();
            Check("Game Boy: checks removed, no hook left", board.DebugHook == null && board.DebugTap == null && !((NesEmulator.Gb.CPU_GB)board.Core).DebugInterposed, "board hooks null");
        }
    }

    // ================================================================================================================================ real games
    private static void Games(string vrunPath, string superPath)
    {
        foreach (var (path, console, frames, input) in new[]
        {
            (vrunPath, ConsoleKind.Nes, 1200, "120:Start,132:,400:Right,600:Right+B,700:Right,900:Up,1000:Right+B"),
            (superPath, ConsoleKind.Snes, 900, "300:Start,306:,520:Start,526:,800:Right"),
        })
        {
            string name = Path.GetFileName(path);
            string sym = Path.ChangeExtension(path, console == ConsoleKind.Nes ? ".mlb" : ".sym");
            if (!File.Exists(path) || !File.Exists(sym)) { Console.WriteLine($"  NOT RUN  {name}: {path} or its symbols are not on this machine"); notRun++; continue; }
            var rom = File.ReadAllBytes(path);
            var steps = CheckedGame.ParseInput(input);
            using var plain = CheckedGame.Open(rom, name, console);
            var sw1 = Stopwatch.StartNew(); plain.Run(frames, steps); sw1.Stop();
            using var g = CheckedGame.Open(rom, name, console);
            g.Dbg.Symbols.Load(sym, g.Family);
            var c = g.Attach(new CheckOptions());
            var sw2 = Stopwatch.StartNew(); g.Run(frames, steps); sw2.Stop();
            c.Finish();
            Check($"{name}: checks on, frame-identical to checks off ({frames} frames of scripted play)", g.Host.PictureHash() == plain.Host.PictureHash() && g.Host.RamHash() == plain.Host.RamHash(),
                $"picture {g.Host.PictureHash():X16} vs {plain.Host.PictureHash():X16}; {frames / sw1.Elapsed.TotalSeconds:F0} fps unchecked, {frames / sw2.Elapsed.TotalSeconds:F0} fps checked (cost x{sw2.Elapsed.TotalSeconds / sw1.Elapsed.TotalSeconds:F1})");
            var p = CheckRun.Summarize(c, 300);
            Check($"{name}: the profile names the game's functions and its frame wait", p.Functions.Count >= 10 && p.Functions.Any(f => f.Name is "runtime_wait_nmi" or "WaitForVBlank"),
                $"{c.Findings.Count} findings; busy {p.BusyAverage:F1}% (worst {p.BusyWorst:F1}%), {p.LagFrames} lag frames from frame 300; top: {string.Join(", ", p.Functions.Take(4).Select(f => f.Name))}");
        }
    }
}
