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
            RomDir = GoldenSuite.RomDir(args),
            GoldenFile = GoldenSuite.FindGolden(args),
        };
        var only = (Opt(args, "only") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Opt(args, "scan") is { } scan) return Vdptest.Scan(ctx, scan);
        if (Opt(args, "golden-write") is { } gw) return ctx.RomDir == null ? Fail("--golden-write needs --roms <dir> (folders sms and gg inside)") : GoldenSuite.Write(ctx, ctx.RomDir, gw);
        if (args.Any(a => a.Equals("--print-smoke-hex", StringComparison.OrdinalIgnoreCase))) { SmokeSuite.Print(); return 0; }
        if (Opt(args, "write-fm-probes") is { } probeDir) return FmProbes.Write(probeDir);
        if (args.Any(a => a.Equals("--pal-vdptest", StringComparison.OrdinalIgnoreCase))) return PalSuite.Print(ctx);
        if (Opt(args, "write-irq-probes") is { } irqDir) return IrqProbes.Write(irqDir);
        if (Opt(args, "write-vdp-probes") is { } vdpDir) return VdpSlotProbes.Write(vdpDir);
        if (Opt(args, "write-sg-programs") is { } sgDir) return Sg1000Progs.Write(sgDir);
        if (Opt(args, "write-swap-programs") is { } swapDir) { Directory.CreateDirectory(swapDir); File.WriteAllBytes(Path.Combine(swapDir, "tone-scroller.sms"), SwapSuite.ToneScroller(false)); File.WriteAllBytes(Path.Combine(swapDir, "tone-scroller.gg"), SwapSuite.ToneScroller(true)); Console.WriteLine("wrote tone-scroller.sms / .gg to " + swapDir); return 0; }
        if (Opt(args, "vram-sweep") is { } sweepDir) return VdpSlotProbes.Sweep(ctx, sweepDir);
        if (Opt(args, "vram-double") is { } doubleDir) return VdpSlotProbes.DoubleReport(ctx, doubleDir);
        if (Opt(args, "sg-hash") is { } sgHashDir) { Directory.CreateDirectory(sgHashDir); return Sg1000Suite.Print(ctx, sgHashDir); }
        if (Opt(args, "png-hash") is { } pngPath) { Console.WriteLine(Sg1000Suite.PngHash(pngPath)); return 0; }
        if (args.Any(a => a.Equals("--prove", StringComparison.OrdinalIgnoreCase))) return Prove(ctx, only);
        int pass = 0, fail = 0, skipped = 0;
        foreach (var suite in SmsSuites.All)
        {
            if (only.Count > 0 && !only.Contains(suite.Id)) continue;
            var (p, f, s) = RunSuite(suite, ctx, print: true);
            pass += p; fail += f; skipped += s;
        }
        Console.WriteLine($"smstest: {pass} pass, {fail} fail, {skipped} skip");
        return fail == 0 ? 0 : 1;
    }

    private static int Fail(string message) { Console.Error.WriteLine(message); return 2; }

    private static (int Pass, int Fail, int Skip) RunSuite(SmsSuite suite, SmsSuiteContext ctx, bool print)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        List<SmsCheck> checks;
        try { checks = suite.Run(ctx).ToList(); }
        catch (Exception e) { checks = new() { new SmsCheck(suite.Id + " (exception)", false, e.GetType().Name + ": " + e.Message) }; }
        int p = checks.Count(c => c.Status == SmsStatus.Pass), f = checks.Count(c => c.Status == SmsStatus.Fail), s = checks.Count(c => c.Status == SmsStatus.Skip);
        if (print)
        {
            Console.WriteLine($"{suite.Id,-16} {p,3} pass {f,3} fail {s,3} skip  ({sw.Elapsed.TotalSeconds:F1}s)  {suite.Title}");
            foreach (var c in checks)
                if (ctx.Verbose || c.Status != SmsStatus.Pass) Console.WriteLine($"    {c.Status.ToString().ToUpperInvariant(),-4} {c.Name}{(c.Detail.Length > 0 ? "  - " + c.Detail : "")}");
        }
        return (p, f, s);
    }

    /// <summary>
    /// <c>--smstest --prove</c>: the proof that every suite can fail. Each suite runs clean (it must pass), then again with its poison (a deliberate fault in the emulator or the input: a timing constant moved, a
    /// sound unit with the wrong clock, a corrupted save, the wrong button) and must report at least one failure. A suite whose poison goes unnoticed, or that fails clean, makes this exit 1.
    /// </summary>
    private static int Prove(SmsSuiteContext ctx, HashSet<string> only)
    {
        int bad = 0;
        foreach (var suite in SmsSuites.All)
        {
            if (only.Count > 0 && !only.Contains(suite.Id)) continue;
            if (suite.Poison == null) { Console.WriteLine($"{suite.Id,-12} no poison defined: NOT PROVEN"); bad++; continue; }
            var clean = RunSuite(suite, new SmsSuiteContext { Tools = ctx.Tools, RomDir = ctx.RomDir, GoldenFile = ctx.GoldenFile }, print: false);
            if (clean.Pass == 0 && clean.Fail == 0) { Console.WriteLine($"{suite.Id,-12} skipped clean ({clean.Skip} skip): NOT PROVEN (the suite could not run here)"); bad++; continue; }
            var poisoned = new SmsSuiteContext { Tools = ctx.Tools, RomDir = ctx.RomDir, GoldenFile = ctx.GoldenFile, Poison = suite.Id };
            suite.Poison(poisoned);
            var hit = RunSuite(suite, poisoned, print: false);
            bool ok = clean.Fail == 0 && hit.Fail > 0;
            Console.WriteLine($"{suite.Id,-12} clean {clean.Pass} pass {clean.Fail} fail; poisoned ({suite.PoisonText}) {hit.Pass} pass {hit.Fail} fail: {(ok ? "CAN FAIL" : "NOT PROVEN")}");
            if (!ok) bad++;
        }
        Console.WriteLine(bad == 0 ? "prove: every suite failed when poisoned" : $"prove: {bad} suite(s) not proven");
        return bad == 0 ? 0 : 1;
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
    public string? RomDir, GoldenFile;

    public string TestRom(params string[] rel) => Path.Combine(new[] { Tools, "test-sets" }.Concat(rel).ToArray());

    /// <summary>A session on a ROM file, with any timing override applied to the board and the VDP (by field name).</summary>
    public SmsSession Session(string romPath, string? name = null, ConsoleKind console = ConsoleKind.MasterSystem, RegionChoice? region = null)
        => Session(File.ReadAllBytes(romPath), name ?? Path.GetFileName(romPath), console, region);

    /// <summary>A session on ROM bytes (a program built in the suite, or a file already read).</summary>
    public SmsSession Session(byte[] rom, string name, ConsoleKind console = ConsoleKind.MasterSystem, RegionChoice? region = null, string apu = "SMS")
    {
        var s = console == ConsoleKind.GameGear ? new GgSession(rom, "Z80", "GG", apu == "SMS" ? "GG" : apu, name, region) : new SmsSession(rom, console, "Z80", "SMS", apu, name, null, region);
        foreach (var kv in Tune) ApplyField(s, kv.Key, kv.Value);
        return s;
    }

    public SmsSession GgSession(byte[] rom, string name, RegionChoice? region = null) => Session(rom, name, ConsoleKind.GameGear, region);

    /// <summary>Sets a timing constant of the board or the VDP by name (a public int field or property); a property that sets the power-on state (StartCycle, StartLine) takes effect through the reset that follows.</summary>
    private static void ApplyField(SmsSession s, string field, int value)
    {
        foreach (object target in new object[] { s.Board, s.Board.Vdp })
        {
            var f = target.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance);
            if (f != null && f.FieldType == typeof(int)) { f.SetValue(target, value); s.Board.Reset(); return; }
            var p = target.GetType().GetProperty(field, BindingFlags.Public | BindingFlags.Instance);
            if (p is { CanWrite: true } && p.PropertyType == typeof(int)) { p.SetValue(target, value); s.Board.Reset(); return; }
        }
        throw new ArgumentException($"no tunable field '{field}'");
    }
}

/// <param name="Poison">Sets up the context for the proof-of-failure run (<c>--prove</c>): a deliberate fault the suite must notice. <paramref name="PoisonText"/> says what it is.</param>
internal sealed record SmsSuite(string Id, string Title, Func<SmsSuiteContext, IEnumerable<SmsCheck>> Run, Action<SmsSuiteContext>? Poison = null, string PoisonText = "");

/// <summary>The suites of <c>--smstest</c>. Each says how its result is read.</summary>
internal static class SmsSuites
{
    public static readonly SmsSuite[] All =
    {
        new("vdptest", "FluBBa's SMS VDP Test: 55 checks (data port, counters, interrupts, sprite flags), read from the test program's own result bytes in work RAM", Vdptest.Run,
            c => { c.Tune["VintFlagMc"] = 60; c.Tune["HintMc"] = 60; }, "frame and line interrupt flags 55 master clocks late"),
        new("irqphase", "interrupt and flag timing against the oracle's recorded Z80 cycle counts (frame interrupt entry after a NOP sled; a status-polling loop)", IrqPhase.Run,
            c => c.Tune["StartCycle"] = 25, "the Z80 / VDP phase moved by 4 cycles"),
        new("psg", "the PSG on the delivered audio: tone frequency, 2 dB volume steps, noise, silence at power-on, Game Gear stereo port (measured, not compared with a recording)", PsgSuite.Run,
            c => { }, "a sound unit 6% fast with no stereo port"),
        new("fm", "the YM2413 on the board (SMS-FM): $F2 detection and the ports against the oracle's answers, registers, pitch/volume/mix on the delivered audio, rhythm, hub voices, APU swap, state round trip, speed, and the chip against Nuked-OPLL", FmSuite.Run,
            c => { }, "the FM data port ($F1) dropped and the $F2 read-back wrong in bit 0"),
        new("state", "the board's save state: a session restored from a state runs on frame for frame and sample for sample as the one that carried on (programs built here, and roster games with --roms)", StateSuite.Run,
            c => { }, "a work RAM byte left unrestored after the load"),
        new("sessionstate", "save states through the shared session contract (IConsoleSession.SaveState / LoadState): round trip at many save points, every field compared, refusals of a state that does not belong (sound unit, game, console, region, damage) with the machine left as it was, a session without states says so", SessionStateSuite.Run,
            c => { }, "a VRAM byte left unrestored after the load"),
        new("swap", "hot swapping of every core slot of the Master System and the Game Gear mid-game through the session the apps build: every id of the CPU, picture (own chip and each NES:<chip>) and sound (own unit, SMS-FM, each NES:<chip>) menus on a tone-and-scroll program and, with --roms, on real games; the game, its picture and its tone carry on, a bad id is refused, chains and a swap per frame", SwapSuite.Run,
            c => { }, "a session whose TrySwapCore says yes to every id and silences the PSG on a sound swap"),
        new("eeprom", "the 24C01 serial EEPROM of the Game Gear baseball cartridges (the SegaEeprom mapper) driven by a data-sheet bit-banging master: acknowledge, byte write, random and sequential read, page wrap, stop, save file, state in mid-transfer", EepromSuite.Run,
            c => { }, "a read started with the write bit"),
        new("bios", "BIOS images (names with BIOS as a word) run as the console's BIOS from a cold power-on with an empty slot: BIOS mapped, VDP cold, $3E switches it off, the name rule, state", EepromSuite.RunBios,
            c => { }, "the BIOS name left off the file, so the image runs as a cartridge"),
        new("known", "games that need the original VDP (SmsCartridge.KnownModels): a listed game takes the SMS 1 board unless the user chose one, an unlisted game and the Game Gear do not", EepromSuite.RunKnown,
            c => { }, "the game listed with the wrong board"),
        new("refresh","the Z80 refresh register R: steps of prefixed instructions, block repeats, interrupt acknowledge and HALT against the Zilog manual and the oracle's measurements; the oracle's R does not count while halted (tool-only setting)", RefreshSuite.Run,
            c => c.Tune["OracleHaltNoRefresh"] = 1, "R not counting while halted in the default configuration"),
        new("pal", "the 50 Hz machine: 313 lines of 228 cycles, the PAL Z80 clock in the PSG's and the YM2413's pitch, FluBBa's VDP test run as a PAL console against the oracle's PAL result bytes", PalSuite.Run,
            c => { }, "a PAL console whose frame is the NTSC length"),
        new("sg1000", "the SG-1000 / TMS9918A: eight hand-assembled programs (four modes, two sprite sizes) as .sg cartridges, picture and status flags against the oracle's", Sg1000Suite.Run,
            c => { }, "a picture hash the oracle did not produce"),
        new("save", "battery saves: export, import into a program that reads it, no save offered without RAM mapping", SaveSuite.Run, c => { }, "a corrupted save"),
        new("mapper", "cartridge mappers: Sega banks and RAM, Codemasters, plain, detection, copier header", MapperSuite.Run, c => { }, "the plain cartridge where the Sega mapper belongs"),
        new("session", "console layer: pad bits on $DC/$DD, Pause as an NMI, region and model from the name, frame rates, picture sizes", SessionSuite.Run, c => { }, "player 1 presses B where the check expects A"),
        new("smoke", "the programs of the desktop smoke test: a three-colour picture that scrolls with Right held, a battery program that notices its save; the smoke script carries the same bytes", SmokeSuite.Run,
            c => { }, "Left held where the check expects Right"),
        new("golden", "regression guard: frame hashes of the 18 roster games' scripted play-through (needs --roms)", GoldenSuite.Run, c => c.Tune["VintFlagMc"] = 60, "frame interrupt 55 master clocks late"),
        new("speed", "throughput on one core (target 8x real time)", SpeedSuite.Run, c => { }, "a target no machine reaches"),
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
