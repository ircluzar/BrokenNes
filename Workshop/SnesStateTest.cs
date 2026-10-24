using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using NesEmulator.Mix;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes.Workshop;

/// <summary>
/// <c>--snes-state-test [--games smw,smk,...|all] [--only group,group] [--quick] [--poison NAME]</c>: the SNES session's save states, through the session contract (the same members the apps call). The groups:
/// <list type="number">
/// <item><b>round trip</b>: on every roster game (plain boards, DSP-1, SA-1, Super FX) a state is taken at save points of a scripted play-through; the session carries on and a fresh session (that has already run a frame) loads the state and runs
/// the same window of 120 frames: the picture of every frame, every audio sample, and the state each of them writes at the end (the whole machine, byte for byte) are those of the session that carried on, and the picture right
/// after the load is the one that was saved.</item>
/// <item><b>every field</b>: the restored machine is compared with the saved one field by field by reflection (the count is printed), and then <b>each field in turn</b> is changed in the saving machine and must come back out of a load.</item>
/// <item><b>mid-frame</b>: states taken at lines of the display, of the blanking and of the vertical interrupt.</item>
/// <item><b>refusals</b>: another game, another console, another sound unit, another chip, a damaged / cut / foreign file; the machine is left as it was.</item>
/// <item><b>sound units and picture chips</b>: a state saved after live swaps (the stand-in unit with the real SPC700 parked behind it, a NES chip listening, a NES picture chip).</item>
/// <item><b>the app's own factory and the quick-save file</b>.</item>
/// </list>
/// <c>--poison NAME</c> breaks the load after the fact (one component's field left as it was); the groups above must then fail: wram vram cgram oam ppureg cpu timing dma hdma aram spc dsp timers ports sram chip parked.
/// </summary>
internal static class SnesStateTest
{
    private static int pass, fail, skipped;
    private static string poison = "";

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) pass++; else fail++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail == "" ? "" : "  (" + detail + ")")}");
    }

    private static void Skip(string name, string why) { skipped++; Console.WriteLine($"  SKIP  {name}  ({why})"); }
    private static void Info(string text) => Console.WriteLine($"        {text}");
    private static void Group(string title) => Console.WriteLine($"-- {title}");

    // ---------------------------------------------------------------------------------------------------------------------
    //  The roster: the games of the Classic mini and the other cartridges the chips need
    // ---------------------------------------------------------------------------------------------------------------------

    private static string RomDir => Environment.GetEnvironmentVariable("BROKENNES_SNES_ROMS") is { Length: > 0 } e ? e : @"X:\EMULATION\Super Nintendo\Super Nintendo (U)";

    private sealed record Game(string Key, string Short, string File, string Chip, string Script, int[] Points)
    {
        public string Path => System.IO.Path.Combine(RomDir, File);
        public bool Exists => System.IO.File.Exists(Path);
        public byte[] Load() => System.IO.File.ReadAllBytes(Path);
    }

    /// <summary>Taps that move a menu on: a button for 6 frames every <paramref name="period"/> frames from <paramref name="from"/> to <paramref name="to"/>, cycling through <paramref name="buttons"/>.</summary>
    private static string Mash(int from, int to, int period, params string[] buttons)
    {
        var steps = new List<string>(); int n = 0;
        for (int f = from; f < to; f += period) { steps.Add($"{f}:{buttons[n++ % buttons.Length]}"); steps.Add($"{f + 6}:"); }
        return string.Join(",", steps);
    }

    private static string Smw()
    {
        var steps = new List<string> { "500:Start", "508:", "700:A", "708:", "900:A", "908:", "1100:A", "1108:", "1300:A", "1308:" };
        for (int f = 1600; f <= 2600; f += 100) { steps.Add($"{f}:B"); steps.Add($"{f + 10}:"); steps.Add($"{f + 50}:A"); steps.Add($"{f + 60}:"); }
        steps.AddRange(new[] { "2800:Right", "3200:Right+B", "3240:Right", "3900:" });
        return string.Join(",", steps);
    }

    private static readonly Game[] Roster =
    {
        new("smw", "Super Mario World", "Super Mario World (U) [!].smc", "none", Smw(), new[] { 900, 3300 }),
        new("alttp", "Zelda: A Link to the Past", "Legend of Zelda, The - A Link to the Past (U) [!].smc", "none", "1300:Start,1308:," + Mash(1500, 2400, 60, "Start", "A", "Down", "A"), new[] { 1100, 2300 }),
        new("dkc", "Donkey Kong Country", "Donkey Kong Country (U) (V1.2) [!].smc", "none", Mash(200, 2600, 50, "Start", "A", "B", "Right", "A", "Down", "Right+B"), new[] { 800, 2000 }),
        new("sm", "Super Metroid", "Super Metroid (JU) [!].smc", "none", Mash(200, 2600, 50, "Start", "A", "Start", "A", "Right", "B"), new[] { 800, 2000 }),
        new("sf2t", "Street Fighter II Turbo", "Street Fighter II Turbo - Hyper Fighting (U) [!].smc", "none", Mash(150, 2600, 40, "Start", "A", "B", "Right+A", "Down", "X", "Y"), new[] { 800, 2000 }),
        new("contra3", "Contra III", "Contra III - The Alien Wars (U) [!].smc", "none", Mash(150, 2600, 40, "Start", "A", "B", "Right", "Right+B", "Y"), new[] { 800, 2000 }),
        new("smk", "Super Mario Kart", "Super Mario Kart (U) [!].smc", "DSP-1", "250:Up,260:," + Mash(300, 2600, 45, "B", "B", "Right", "B", "A", "B", "Start"), new[] { 900, 2200 }),
        new("kss", "Kirby Super Star", "Kirby Super Star (U) [!].smc", "SA-1", Mash(150, 2600, 45, "Start", "A", "A", "Start", "Right", "B"), new[] { 800, 2000 }),
        new("smrpg", "Super Mario RPG", "Super Mario RPG - Legend of the Seven Stars (U) [!].smc", "SA-1", Mash(150, 2600, 45, "Start", "A", "A", "B", "Start"), new[] { 800, 2000 }),
        new("starfox", "Star Fox", "Star Fox (U) (V1.2) [!].smc", "Super FX", "200:Start,208:,400:Start,408:," + Mash(600, 2600, 50, "A", "Start", "Right", "Left", "B"), new[] { 700, 1800 }),
        new("yoshi", "Yoshi's Island", "Super Mario World 2 - Yoshi's Island (U) (M3) (V1.1).smc", "Super FX", Mash(150, 2600, 45, "Start", "A", "A", "B", "Right", "Start"), new[] { 800, 2000 }),
    };

    private static Dictionary<int, PadButtons> ParseInput(string spec)
    {
        var d = new Dictionary<int, PadButtons>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':'); PadButtons b = 0;
            if (kv.Length > 1) foreach (var name in kv[1].Split('+', StringSplitOptions.RemoveEmptyEntries)) b |= Enum.Parse<PadButtons>(name, true);
            d[int.Parse(kv[0])] = b;
        }
        return d;
    }

    private static readonly Dictionary<string, Dictionary<int, PadButtons>> Scripts = new();
    private static Dictionary<int, PadButtons> ScriptOf(Game g) => Scripts.TryGetValue(g.Key, out var s) ? s : Scripts[g.Key] = ParseInput(g.Script);

    /// <summary>Nintendo's DSP-1 firmware, when the user's BizHawk folder has it (never copied anywhere): lets the test tell the homemade chip from the firmware-driven one.</summary>
    private static Func<string[], (string name, byte[] data)?>? FirmwareLoader()
    {
        string dir = Environment.GetEnvironmentVariable("BROKENNES_SNES_FIRMWARE") is { Length: > 0 } e ? e : @"C:\Users\philt\OneDrive\Documents\PROJECTS\!!! DUMP\BizHawkLibretro\output\Firmware";
        if (!Directory.Exists(dir)) return null;
        return names =>
        {
            foreach (var n in names) { string p = Path.Combine(dir, n); if (File.Exists(p)) return (n, File.ReadAllBytes(p)); }
            return null;
        };
    }

    private static SnesSession Make(Game g, string apu = "SFC", string ppu = "SFC", Func<string[], (string name, byte[] data)?>? firmware = null) =>
        (SnesSession)ConsoleSessions.Create(ConsoleKind.Snes, g.Load(), "SFC", ppu, apu, firmware, g.File);

    // ---------------------------------------------------------------------------------------------------------------------
    //  Playing with a script and keeping a trace
    // ---------------------------------------------------------------------------------------------------------------------

    private static string PictureHash(IConsoleSession s) =>
        Convert.ToHexString(SHA1.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(s.Frame.AsSpan(0, s.FrameWidth * s.FrameHeight))))[..16];

    private static string Fingerprint(IConsoleSession s) => Convert.ToHexString(SHA1.HashData(s.SaveState()))[..16];

    private sealed record Trace(List<string> Pictures, string Audio, long Samples);

    private static void Frame(IConsoleSession s, Dictionary<int, PadButtons> script, ref PadButtons held, int f, List<string>? pictures, IncrementalHash? audio, short[] buf, ref long samples)
    {
        if (script.TryGetValue(f, out var h)) held = h;
        s.SetPad(0, held);
        s.RunFrame();
        pictures?.Add(PictureHash(s));
        int n; while ((n = s.ReadSamples(buf)) > 0) { samples += n; audio?.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(buf.AsSpan(0, n))); }
    }

    private static PadButtons HeldAt(Dictionary<int, PadButtons> script, int from)
    {
        PadButtons held = 0;
        foreach (var kv in script.Where(k => k.Key <= from).OrderBy(k => k.Key)) held = kv.Value;
        return held;
    }

    private static void Advance(IConsoleSession s, Dictionary<int, PadButtons> script, int from, int to)
    {
        var buf = new short[32768]; long n = 0; var held = HeldAt(script, from);
        for (int f = from; f < to; f++) Frame(s, script, ref held, f, null, null, buf, ref n);
    }

    private static Trace Play(IConsoleSession s, Dictionary<int, PadButtons> script, int from, int frames)
    {
        var pictures = new List<string>(); var audio = IncrementalHash.CreateHash(HashAlgorithmName.SHA1); var buf = new short[32768]; long n = 0;
        var held = HeldAt(script, from);
        for (int f = from; f < from + frames; f++) Frame(s, script, ref held, f, pictures, audio, buf, ref n);
        return new Trace(pictures, Convert.ToHexString(audio.GetHashAndReset())[..16], n);
    }

    // ---------------------------------------------------------------------------------------------------------------------
    //  The poison: something a loader "forgot", applied to the session after it has loaded, so the checks must notice
    // ---------------------------------------------------------------------------------------------------------------------

    private static readonly BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;

    private static void SetField(object o, string name, object value) => o.GetType().GetField(name, NonPublic)!.SetValue(o, value);
    private static T GetField<T>(object o, string name) => (T)o.GetType().GetField(name, NonPublic)!.GetValue(o)!;

    private static void ApplyPoison(SnesSession s)
    {
        var b = s.Board;
        switch (poison)
        {
            case "wram": b.Wram[0x1234] ^= 0x5A; break;
            case "vram": b.Ppu.Vram[0x123] ^= 0x5A5A; break;
            case "cgram": b.Ppu.Cgram[7] ^= 0x1F; break;
            case "oam": b.Ppu.Oam[9] ^= 0x33; break;
            case "ppureg": SetField(b.Ppu, "m7a", (short)(GetField<short>(b.Ppu, "m7a") ^ 0x55)); break;
            case "cpu": b.Cpu.A ^= 1; break;
            case "timing": SetField(b, "lineClock", GetField<int>(b, "lineClock") + 4); break;
            case "dma": GetField<byte[]>(b, "dmaRegs")[0x12] ^= 1; break;
            case "hdma": GetField<bool[]>(b, "hdmaDone")[2] ^= true; break;
            case "aram": ((APU_SFC)ActiveSpc(s)).Aram[0x2000] ^= 1; break;
            case "spc": ((APU_SFC)ActiveSpc(s)).Smp.PC ^= 1; break;
            case "dsp": ((APU_SFC)ActiveSpc(s)).Dsp.Regs[0x0C] ^= 0x10; break;
            case "timers": GetField<byte[]>(ActiveSpc(s), "timerStage")[0] ^= 1; break;
            case "ports": GetField<byte[]>(ActiveSpc(s), "cpuToApu")[2] ^= 0x20; break;
            case "sram": if (b.Cart.Sram.Length > 0) b.Cart.Sram[0] ^= 0xFF; break;
            case "chip":
                switch (b.Coprocessor)
                {
                    case GSU_SFC gsu: GetField<ushort[]>(gsu, "r")[3] ^= 1; break;
                    case SA1_SFC sa1: GetField<byte[]>(sa1, "mmc")[1] ^= 1; break;
                    case DSP1_SFC d1: SetField(d1, "inputCount", GetField<int>(d1, "inputCount") ^ 1); break;
                    case NECDSP_SFC nec: SetField(nec, "a", (ushort)(GetField<ushort>(nec, "a") ^ 1)); break;
                }
                break;
            case "parked": if (s.ParkedSpc is { } p) p.Dsp.Regs[0x4D] ^= 1; break;
        }
    }

    /// <summary>The real SPC700 + S-DSP wherever it is: the active unit, or inside the NES listener, or parked behind the stand-in.</summary>
    private static object ActiveSpc(SnesSession s) => s.Board.Apu as APU_SFC ?? (s.Board.Apu as NesApuOnSnes)?.Front as APU_SFC ?? s.ParkedSpc ?? throw new InvalidOperationException("no SPC700 in this session");

    // ---------------------------------------------------------------------------------------------------------------------
    //  Field exclusions: what is not state of the machine, and why
    // ---------------------------------------------------------------------------------------------------------------------

    private static readonly SnesFieldWalker.Exclusion[] Exclusions =
    {
        new(@"\.(romBytes|Rom|rom|program|dataRom)$", "the game's ROM / the chip's firmware image: the game's identity (the header names the game and the firmware hash), not state"),
        new(@"\.Cart\.(HiRom|MapMode|HasBattery|ChipsetByte|Chip|Title|WideMessMap)$", "the cartridge's header facts, decoded from the ROM: the game's identity"),
        new(@"\.board\.(fastCpu|idleClocks|dmaByteClocks|wramPortMask)$", "the WideMess modes (BOARD_SFC.WideMess.cs): derived from the WideMess registers, which are state, and re-applied when they load; a SNES keeps their SNES values"),
        new(@"\.(map|dspPerMaster|ramMask|bwMask|version|ownsRam|ownsBwram)$", "chip wiring fixed at construction from the cartridge (mapping, clock ratio, address masks): identity, not state"),
        new(@"^SnesSession\.(frame|width|height|mid|down|snesPpu|apuId|cpuId|ppuId|chipLabel|wideBridge)$", "the apps' copy of the picture and the picture bridge: views of the S-PPU, rebuilt after a load"),
        new(@"^SnesSession\.WideMess(Picture|Voice)FramesNotCarried$", "the status line's counts of what another console's chips did not carry of a WideMess game (milestone 7b): the session's counters, not the machine's"),
        new(@"\.Ppu\.(bgColor|bgPrio|objColor|objPrio|objMath|rankMain|rankSub|colorMain|colorSub|layerMain|layerSub)(\[\d+\])?$", "S-PPU per-line scratch: rewritten whole for every line before it is read"),
        new(@"\.Ppu\.(window|windowSignature|rankTable|rankTableOrder|brightnessLut|spriteY|spriteH|spriteXVisible|oamVersion|spriteCacheVersion|bridgeOam|bridgeOamVersion)(\[\d+\])?$", "S-PPU caches of registers / OAM: rebuilt on use (InvalidateCaches runs at the end of a load)"),
        new(@"\.Ppu\.HiResBuffer$", "S-PPU: only the rows drawn at 512 width are state (rowHiRes marks them, and the state test compares those rows); the others are rebuilt from the 256-wide frame on request"),
        new(@"\.Ppu\.(BridgeLines|DebugLineRegisters|DebugLayerMask|DropVramWritesDuringDisplay)$", "S-PPU settings and hooks of tools"),
        new(@"\.board\.pages(\[\d+\])?$", "the board's page table: derived from $420D and the chip's bank registers, rebuilt at the end of a load"),
        new(@"\.board\.(InstructionHook|WriteWatch)$", "hooks of tools"),
        new(@"(^|\.)(ring|ringRead)$", "audio output ring: only the unread samples (ringCount, compared with the samples themselves) are state, not where in the ring they sit"),
        new(@"(^|\.)SmpHook$", "hook of tools"),
        new(@"\.(Trace|tracePos|WatchPcs|WatchFrom|WatchRamFrom|WatchRamLength|WatchArmPc|WatchArmCount|WatchMax|watchArmHits|watchArmed|WatchLog)$", "Super FX tracing tools"),
        new(@"\.(PortLog)$", "DSP-1 tracing tool"),
    };

    // ---------------------------------------------------------------------------------------------------------------------

    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        pass = fail = skipped = 0;
        poison = Arg(args, "--poison") ?? "";
        bool quick = args.Contains("--quick");
        string? gamesArg = Arg(args, "--games");
        var wanted = gamesArg == null || gamesArg == "all" ? null : gamesArg.Split(',').Select(x => x.Trim().ToLowerInvariant()).ToHashSet();
        var games = Roster.Where(g => wanted == null || wanted.Contains(g.Key)).ToList();
        var only = Arg(args, "--only")?.Split(',').Select(x => x.Trim().ToLowerInvariant()).ToHashSet();
        var missing = games.Where(g => !g.Exists).ToList();
        games = games.Where(g => g.Exists).ToList();
        Console.WriteLine($"SNES save states ({games.Count} roster games from {RomDir}{(poison != "" ? ", POISON " + poison : "")}{(quick ? ", quick" : "")})");
        foreach (var g in missing) Skip($"roster game {g.Short}", $"{g.File} is not under {RomDir} (set BROKENNES_SNES_ROMS)");
        foreach (var (name, key, group) in new (string, string, Action)[]
        {
            ("round trip on the roster", "roundtrip", () => RoundTrips(games, quick)),
            ("every field", "fields", () => Fields(games, quick)),
            ("a state saved in the middle of a frame", "midframe", () => MidFrame(games)),
            ("refusals", "refusals", () => Refusals(games)),
            ("sound units and picture chips", "units", () => Units(games)),
            ("the app's own factory and the quick-save file", "factory", () => Factory(games)),
            ("a program that counts frames while Right is held", "program", ProgramGroup),
            ("size and time", "size", () => SizeAndTime(games)),
            ("allocation", "alloc", () => Allocation(games)),
        })
        {
            if (only != null && !only.Contains(key)) continue;
            try { group(); }
            catch (Exception e) { Check($"group '{name}' ran to its end", false, e.ToString().Replace('\n', ' ')); }
        }
        Console.WriteLine($"== {pass} passed, {fail} failed, {skipped} skipped");
        return fail == 0 ? 0 : 1;
    }

    private static string? Arg(string[] args, string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

    // ---------------------------------------------------------------------------------------------------------------------
    //  1. The round trip
    // ---------------------------------------------------------------------------------------------------------------------

    /// <summary>A frame of the script far from <paramref name="at"/>: the point the loading session is running at when the state is loaded.</summary>
    private static int ElsewhereOf(Game g, int at) => g.Points.Length > 1 && g.Points.Any(p => p != at) ? g.Points.First(p => p != at) : at + 331;

    /// <summary>
    /// The board's page table is derived (from $420D and the chip's bank registers) and rebuilt at the end of a load. True when the table the session holds is the table a rebuild gives. Rebuilds it, so it is the last thing asked
    /// of a session that is being tested.
    /// </summary>
    private static bool PageTableMatchesRebuild(SnesSession s)
    {
        var board = s.Board;
        var pages = (Array)typeof(BOARD_SFC).GetField("pages", NonPublic)!.GetValue(board)!;
        var before = new object?[pages.Length];
        for (int i = 0; i < pages.Length; i++) before[i] = pages.GetValue(i);
        typeof(BOARD_SFC).GetMethod("BuildPageTable", NonPublic)!.Invoke(board, null);
        var fields = pages.GetType().GetElementType()!.GetFields();
        for (int i = 0; i < pages.Length; i++)
        {
            var now = pages.GetValue(i)!;
            foreach (var f in fields)
            {
                object? x = f.GetValue(before[i]!), y = f.GetValue(now);
                if (!(f.FieldType.IsArray ? ReferenceEquals(x, y) : Equals(x, y))) return false;
            }
        }
        return true;
    }

    private static string ChipOf(SnesSession s) => s.Board.Coprocessor is { } c ? c.Describe() : "no chip";

    private static void RoundTrips(List<Game> games, bool quick)
    {
        Group("a state loaded into a fresh session carries on exactly like the session that saved it");
        if (games.Count == 0) { Skip("round trip", $"no roster ROM under {RomDir}"); return; }
        const int window = 120;
        foreach (var g in games)
        {
            var script = ScriptOf(g);
            int[] points = quick ? g.Points[..1] : g.Points;
            int badImmediate = 0, badIdempotent = 0, badPicture = 0, badAudio = 0, badEnd = 0, badTable = 0, firstBad = -1;
            long size = 0; string chip = "";
            foreach (int at in points)
            {
                var a = Make(g);
                Advance(a, script, 0, at);
                byte[] state = a.SaveState(); size = Math.Max(size, state.Length);
                string pic = PictureHash(a);
                chip = ChipOf(a);
                var carried = Play(a, script, at, window);
                var b = Make(g);
                Advance(b, script, 0, ElsewhereOf(g, at));                     // the loading session is the same game running somewhere else (as when F5 is pressed in a running game): a loader that replaces too little, or leaves a cache of the old picture, shows it
                b.LoadState(state);
                ApplyPoison(b);
                if (PictureHash(b) != pic) badImmediate++;
                if (!b.SaveState().AsSpan().SequenceEqual(state)) badIdempotent++;     // saving what was just loaded gives the same bytes back
                var restored = Play(b, script, at, window);
                int first = Enumerable.Range(0, window).FirstOrDefault(i => carried.Pictures[i] != restored.Pictures[i], -1);
                if (first >= 0) { badPicture++; if (firstBad < 0) firstBad = at; }
                if (carried.Audio != restored.Audio || carried.Samples != restored.Samples) badAudio++;
                if (!a.SaveState().AsSpan().SequenceEqual(b.SaveState())) badEnd++;
                if (!PageTableMatchesRebuild(b)) badTable++;
            }
            // a state from power-on (before the game sets FastROM, the chip's banks...) loaded into the game running at a save point: what the running game derived from its registers must follow the loaded ones
            {
                var early = Make(g);                                   // power-on: no frame run yet
                byte[] earlyState = early.SaveState();
                var run = Make(g); Advance(run, script, 0, points[0]);
                run.LoadState(earlyState);
                ApplyPoison(run);
                bool same = run.SaveState().AsSpan().SequenceEqual(earlyState);
                var ce = Play(early, script, 0, 90); var re = Play(run, script, 0, 90);
                bool tableOk = PageTableMatchesRebuild(run);
                Check($"{g.Short}: a power-on state loaded into the game running at frame {points[0]} (what the running game derived - page table, caches - follows the loaded registers): 90 frames of the same pictures and sound, the same state at the end, the page table is the rebuilt one",
                    same && ce.Pictures.SequenceEqual(re.Pictures) && ce.Audio == re.Audio && early.SaveState().AsSpan().SequenceEqual(run.SaveState()) && tableOk, $"{(same ? "" : "bytes differ after load; ")}{(ce.Pictures.SequenceEqual(re.Pictures) ? "" : "pictures differ; ")}{(tableOk ? "" : "page table stale")}");
            }
            int n = points.Length;
            string tag = $"{g.Short}";
            Check($"{tag}: the picture right after loading is the saved one; saving again gives the same bytes ({n} save points {string.Join("/", points)}, state {size / 1024} KB)", badImmediate == 0 && badIdempotent == 0, badImmediate + badIdempotent == 0 ? "" : $"picture differs {badImmediate}, bytes differ {badIdempotent} of {n}");
            Check($"{tag}: {window} frames after the load are the picture of the session that carried on", badPicture == 0, badPicture == 0 ? "" : $"{badPicture} of {n} differ, first save point {firstBad}");
            Check($"{tag}: the audio is the same sample for sample", badAudio == 0, badAudio == 0 ? "" : $"{badAudio} of {n} differ");
            Check($"{tag}: the state each writes at the end of the window (the whole machine, byte for byte) is identical, and the page table the loaded session holds is the rebuilt one", badEnd == 0 && badTable == 0, badEnd + badTable == 0 ? chip : $"{badEnd} of {n} differ, page table stale {badTable}");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------
    //  2. Nothing left out
    // ---------------------------------------------------------------------------------------------------------------------

    private static SnesFieldWalker Walk(SnesSession s) => new(s, "SnesSession", Exclusions);

    private static void Fields(List<Game> games, bool quick)
    {
        Group("a restored session is the saved one, field for field; and every field in turn is restored");
        var pick = games.Where(g => g.Key is "smw" or "smk" or "kss" or "starfox").ToList();
        if (pick.Count == 0) pick = games.Take(2).ToList();
        if (pick.Count == 0) { Skip("every field", "no roster ROM"); return; }
        foreach (var g in pick)
        {
            var script = ScriptOf(g);
            var a = Make(g);
            Advance(a, script, 0, g.Points[0]);
            byte[] state = a.SaveState();
            var b = Make(g);
            Advance(b, script, 0, ElsewhereOf(g, g.Points[0]));
            b.LoadState(state);
            ApplyPoison(b);
            var fresh = Make(g); Advance(fresh, script, 0, 1);
            var wa = Walk(a); var wb = Walk(b); var wf = Walk(fresh);
            var (diffs, compared, values) = SnesFieldWalker.Compare(wa, wb);
            // the audio queue: the samples a unit holds unread are compared as samples
            var qa = Unread(a); var qb = Unread(b);
            if (!qa.AsSpan().SequenceEqual(qb)) diffs.Add($"unread audio samples ({qa.Length} vs {qb.Length})");
            // the hi-res rows
            diffs.AddRange(HiResRowDiffs(a, b));
            Check($"{g.Short}: every field of the session, the board, the 65816, the S-PPU, the audio unit, the SPC700, the S-DSP and the chip equals the saved session's ({compared:N0} fields, {values:N0} values compared by reflection)", diffs.Count == 0, string.Join("; ", diffs.Take(12)));
            var (vsFresh, _, _) = SnesFieldWalker.Compare(wa, wf);
            Check($"{g.Short}: the comparison can see the state: a freshly started machine differs from the saved one in {vsFresh.Count} of those fields (its own proof of failure)", vsFresh.Count > 100, $"e.g. {string.Join(", ", vsFresh.Take(3))}");
            Info($"excluded as not machine state: {wa.Excluded.Sum(x => x.Value)} paths in {wa.Excluded.Count} patterns");
            Info("fields by component: " + string.Join(", ", wa.Slots.GroupBy(sl => ComponentOf(sl.Path)).OrderByDescending(grp => grp.Count()).Select(grp => $"{grp.Key} {grp.Count()}")));

            // each field in turn
            MutationSweep(g, script, quick);
        }
        // the exclusions, once, with the reason
        Info("exclusions (not state of the machine):");
        foreach (var ex in Exclusions) Info($"  {ex.Pattern}   -- {ex.Reason}");
    }

    /// <summary>The samples every SPC700 + S-DSP of the session holds unread (the output ring's logical content, read without consuming it): the active unit's, the one behind a NES chip and the one parked behind the stand-in.</summary>
    /// <summary>The component a field belongs to, from its path (SnesSession.board.Ppu.m7a is the S-PPU's).</summary>
    private static string ComponentOf(string path)
    {
        var p = path.Split('.');                         // SnesSession.board.Apu.Smp.PC
        if (p.Length < 3 || p[1] != "board") return p.Length >= 2 && p[1] == "parkedSpc" ? "parked unit" : "session";
        if (p.Length == 3) return "board";
        string c = p[2];
        if (c == "Apu" && p.Length > 4 && (p[3] is "Smp" or "Dsp")) return "Apu." + p[3];
        if (c == "Coprocessor" && p.Length > 4 && p[3] == "Cpu") return "Coprocessor.Cpu";
        return c is "Cpu" or "Ppu" or "Cart" or "Apu" or "Coprocessor" ? c : "board";
    }

    private static short[] Unread(SnesSession s)
    {
        var all = new List<short>();
        foreach (var apu in new[] { s.Board.Apu as APU_SFC, (s.Board.Apu as NesApuOnSnes)?.Front as APU_SFC, s.ParkedSpc })
        {
            if (apu == null) continue;
            var ring = GetField<short[]>(apu, "ring"); int count = GetField<int>(apu, "ringCount"), read = GetField<int>(apu, "ringRead");
            for (int i = 0; i < count; i++) all.Add(ring[(read + i) & 0xFFFF]);
            all.Add(short.MinValue);   // a separator between units
        }
        return all.ToArray();
    }

    private static IEnumerable<string> HiResRowDiffs(SnesSession a, SnesSession b)
    {
        var rows = GetField<bool[]>(a.Board.Ppu, "rowHiRes");
        var ha = a.Board.Ppu.HiResBuffer; var hb = b.Board.Ppu.HiResBuffer;
        for (int row = 0; row < rows.Length; row++)
            if (rows[row] && !ha.AsSpan(row * PPU_SFC.HiResWidth, PPU_SFC.HiResWidth).SequenceEqual(hb.AsSpan(row * PPU_SFC.HiResWidth, PPU_SFC.HiResWidth))) yield return $"hi-res row {row}";
    }

    /// <summary>
    /// Each slot of the saving machine is changed in turn (an array: its first, middle and last element), the machine saves, a loading machine of the same shape loads, and the slot must hold the changed value. A slot that is not
    /// restored is named. Done on the board's own payload (no compression, no checksum): the point is what is written, not how it is packed.
    /// </summary>
    private static void MutationSweep(Game g, Dictionary<int, PadButtons> script, bool quick)
    {
        var a = Make(g); Advance(a, script, 0, g.Points[0]);
        var b = Make(g); Advance(b, script, 0, 3);
        var sa = Walk(a); var sb = Walk(b);
        var bySlot = sb.Slots.ToDictionary(s => s.Path);
        int verified = 0, rejected = 0, unsafeMut = 0, elements = 0; var notRestored = new List<string>(); var unsafeList = new List<string>();
        var watch = Stopwatch.StartNew();
        foreach (var slot in sa.Slots)
        {
            if (!bySlot.TryGetValue(slot.Path, out var other)) { notRestored.Add(slot.Path + " (no such slot in the loading machine)"); continue; }
            var trials = new List<Func<int, (Action Apply, Action Revert, Func<bool> Restored)?>>();
            if (slot.IsArray)
            {
                var arr = (Array)slot.Get()!;
                if (arr.Length == 0) continue;
                int size = Math.Max(1, Buffer.ByteLength(arr) / arr.Length);
                bool isBool = arr.GetType().GetElementType() == typeof(bool);
                foreach (int idx in new[] { 0, arr.Length / 2, arr.Length - 1 }.Distinct())
                {
                    int offset = idx * size;
                    trials.Add(attempt =>
                    {
                        if (isBool && attempt > 0) return null;
                        byte mask = (byte)(1 << attempt);
                        var span = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(arr), Buffer.ByteLength(arr));
                        byte before = span[offset];
                        var otherArr = (Array)other.Get()!;
                        return (() => { var s2 = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(arr), Buffer.ByteLength(arr)); s2[offset] ^= mask; },
                                () => { var s2 = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(arr), Buffer.ByteLength(arr)); s2[offset] = before; },
                                () => { var o2 = System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference((Array)other.Get()!), Buffer.ByteLength(otherArr)); return o2[offset] == (byte)(before ^ mask); });
                    });
                }
            }
            else
            {
                trials.Add(attempt =>
                {
                    object orig = slot.Get()!;
                    object mut = SnesFieldWalker.Mutate(orig, attempt);
                    return (() => slot.Set(mut), () => slot.Set(orig), () => Equals(other.Get(), mut));
                });
            }
            foreach (var trial in trials)
            {
                bool done = false, anyApplied = false;
                for (int attempt = 0; attempt < SnesFieldWalker.Attempts && !done; attempt++)
                {
                    var t = trial(attempt);
                    if (t == null) break;
                    var (apply, revert, restored) = t.Value;
                    apply(); anyApplied = true;
                    byte[] payload;
                    try { payload = a.PayloadForTest(); }
                    catch (Exception e) { revert(); unsafeMut++; unsafeList.Add($"{slot.Path}: {e.GetType().Name}"); done = true; continue; }
                    revert();
                    try { b.LoadPayloadForTest(payload); }
                    catch (Exception) { rejected++; continue; }     // the loader's sanity check refused this value (out of range): try the next flip
                    elements++;
                    if (restored()) verified++; else notRestored.Add(slot.Path);
                    done = true;
                }
                if (!done && anyApplied && !unsafeList.Any(u => u.StartsWith(slot.Path))) { unsafeMut++; unsafeList.Add($"{slot.Path}: every flip was refused by the loader's range check"); }
            }
        }
        Check($"{g.Short}: every field comes back out of a load when it is changed in the saving machine ({sa.Slots.Count:N0} fields, {verified:N0} changes tried: array ends and middles, scalars; {rejected} flips refused by range checks and retried; {watch.Elapsed.TotalSeconds:F1} s)",
            notRestored.Count == 0 && unsafeMut == 0, notRestored.Count + unsafeMut == 0 ? "" : $"not restored: {string.Join(", ", notRestored.Distinct().Take(15))}{(unsafeList.Count > 0 ? "; not testable: " + string.Join(", ", unsafeList.Take(8)) : "")}");
    }

    // ---------------------------------------------------------------------------------------------------------------------
    //  3. A state saved in the middle of a frame
    // ---------------------------------------------------------------------------------------------------------------------

    private static void MidFrame(List<Game> games)
    {
        Group("a state saved between two instructions in the middle of a frame (display, the lines after it, the vertical interrupt) carries on exactly");
        var pick = games.Where(g => g.Key is "smw" or "alttp" or "smk" or "kss" or "starfox").ToList();
        if (pick.Count == 0) pick = games.Take(2).ToList();
        if (pick.Count == 0) { Skip("mid-frame", "no roster ROM"); return; }
        foreach (var g in pick)
        {
            var script = ScriptOf(g);
            int at = g.Points[0];
            int bad = 0, badAudio = 0, badEnd = 0, fieldBad = 0, badIdem = 0, n = 0; string firstBad = "", firstFields = "";
            foreach (int line in new[] { 30, 112, 190, 226, 250 })
            {
                n++;
                var a = Make(g);
                Advance(a, script, 0, at);
                a.RunToLine(line);
                byte[] state = a.SaveState();
                var b = Make(g);
                Advance(b, script, 0, ElsewhereOf(g, at));
                b.LoadState(state);
                ApplyPoison(b);
                var (fd, _, _) = SnesFieldWalker.Compare(Walk(a), Walk(b));
                fd.AddRange(HiResRowDiffs(a, b));
                if (fd.Count > 0) { fieldBad++; if (firstFields == "") firstFields = $"line {line}: " + string.Join("; ", fd.Take(4)); }
                if (!b.SaveState().AsSpan().SequenceEqual(state)) badIdem++;
                const int window = 41;
                var carried = Play(a, script, at, window);
                var restored = Play(b, script, at, window);
                int first = Enumerable.Range(0, window).FirstOrDefault(i => carried.Pictures[i] != restored.Pictures[i], -1);
                if (first >= 0) { bad++; if (firstBad == "") firstBad = $"line {line}, frame {at + first}"; }
                if (carried.Audio != restored.Audio) badAudio++;
                if (!a.SaveState().AsSpan().SequenceEqual(b.SaveState())) badEnd++;
            }
            string tag = g.Short;
            Check($"{tag}: right after the load every field equals the saved session's, and saving again gives the same bytes ({n} mid-frame save points: lines 30, 112, 190, 226, 250)", fieldBad == 0 && badIdem == 0, fieldBad + badIdem == 0 ? "" : $"{fieldBad} of {n} differ ({badIdem} by bytes): {firstFields}");
            Check($"{tag}: the pictures of the next 41 frames are the saved session's", bad == 0, bad == 0 ? "" : $"{bad} of {n} differ, first at {firstBad}");
            Check($"{tag}: the audio agrees, and the state each writes 41 frames later is identical, byte for byte", badAudio == 0 && badEnd == 0, $"audio {badAudio}, machine {badEnd} of {n} differ");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------
    //  4. Refusals
    // ---------------------------------------------------------------------------------------------------------------------

    private static void Refusals(List<Game> games)
    {
        Group("a state that does not belong is refused with its reason and the machine is left as it was");
        var smw = games.FirstOrDefault(g => g.Key == "smw"); var alttp = games.FirstOrDefault(g => g.Key == "alttp"); var smk = games.FirstOrDefault(g => g.Key == "smk");
        if (smw == null || alttp == null) { Skip("refusals", "need Super Mario World and A Link to the Past"); return; }
        var a = Make(smw); Advance(a, ScriptOf(smw), 0, 300);
        byte[] state = a.SaveState();
        var hle = Make(smw, "HLE"); Advance(hle, ScriptOf(smw), 0, 20);
        var nes = Make(smw, "NES:FIX"); Advance(nes, ScriptOf(smw), 0, 20);
        var other = Make(alttp); Advance(other, ScriptOf(alttp), 0, 20);

        void Refused(string name, IConsoleSession target, byte[] bytes, params string[] mustSay)
        {
            string before = Convert.ToHexString(SHA1.HashData(target.SaveState()));
            string message;
            try { target.LoadState(bytes); message = ""; }
            catch (InvalidDataException e) { message = e.Message; }
            string after = Convert.ToHexString(SHA1.HashData(target.SaveState()));
            bool says = message.Length > 0 && mustSay.All(w => message.Contains(w, StringComparison.OrdinalIgnoreCase));
            Check(name, says && before == after, message.Length == 0 ? "NOT REFUSED" : (before != after ? "refused, but the machine changed: " : "") + message);
        }

        Refused("a stock-sound-unit state is refused by a session on the stand-in unit (HLE), naming the units", hle, state, "sound unit", "HLE", "SFC");
        Refused("a stock-sound-unit state is refused by a session on a NES chip as the sound unit, naming the units", nes, state, "sound unit", "NES:FIX", "SFC");
        byte[] hleState = hle.SaveState();
        Refused("a state of the stand-in unit is refused by a stock-unit session", a, hleState, "sound unit", "HLE");
        Refused("one game's state is refused by another game", other, state, "belongs to");
        // another console's state
        var gb = TryOtherConsoleState(ConsoleKind.GameBoy);
        if (gb != null) Refused("a Game Boy state is refused by the SNES", a, gb, "Game Boy", "SNES");
        else Skip("a Game Boy state is refused by the SNES", "no Game Boy ROM found for the other console's state");
        byte[] flipped = (byte[])state.Clone(); flipped[flipped.Length / 2] ^= 0x40;
        var healthy = Make(smw); Advance(healthy, ScriptOf(smw), 0, 5);
        Refused("a state with a flipped byte is refused as damaged", healthy, flipped, "damaged");
        Refused("a cut-short state is refused", healthy, state.AsSpan(0, state.Length / 2).ToArray(), "cut short");
        Refused("a file that is not a state is refused", healthy, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, "not a BrokenNes console state");
        // a wrong-chip state
        if (smk != null)
        {
            var fw = FirmwareLoader();
            var homemade = Make(smk); Advance(homemade, ScriptOf(smk), 0, 120);
            byte[] hmState = homemade.SaveState();
            Refused("a game's state is refused by another game of the same console (Super Mario Kart's by A Link to the Past)", other, hmState, "belongs to");
            var withFw = fw == null ? null : Make(smk, firmware: fw);
            if (withFw != null && withFw.Board.Coprocessor is NECDSP_SFC)
            {
                Advance(withFw, ScriptOf(smk), 0, 120);
                Refused("a state saved with the homemade DSP-1 is refused by a session running Nintendo's DSP-1 firmware, naming both chips", withFw, hmState, "cartridge chip", "homemade", "firmware");
                byte[] fwState = withFw.SaveState();
                Refused("a state saved with the DSP-1 firmware is refused by a session on the homemade chip", homemade, fwState, "cartridge chip", "firmware");
                // and the firmware chip's state round-trips on its own
                var c = Make(smk, firmware: fw); Advance(c, ScriptOf(smk), 0, 1); c.LoadState(fwState);
                var carried = Play(withFw, ScriptOf(smk), 120, 60); var restored = Play(c, ScriptOf(smk), 120, 60);
                Check("Super Mario Kart on Nintendo's DSP-1 firmware: 60 frames after a load are the picture and the sound of the session that carried on, and the state at the end is identical", carried.Pictures.SequenceEqual(restored.Pictures) && carried.Audio == restored.Audio && withFw.SaveState().AsSpan().SequenceEqual(c.SaveState()), withFw.Board.Coprocessor.StateId);
            }
            else Skip("the homemade DSP-1 and the firmware DSP-1 refuse each other's states", "no DSP-1 firmware found (BROKENNES_SNES_FIRMWARE)");
        }
        // a payload that passes the checksum but is the wrong shape: undone
        var forged = ConsoleStateFile.Wrap(ConsoleKind.Snes, healthy.GameId, healthy.Title, new Dictionary<string, string> { ["apu"] = "SFC", ["chip"] = "none", ["format"] = SnesSession.StateFormat }, new byte[1000]);
        Refused("a payload of the right header but the wrong size is refused and the game is left as it was", healthy, forged, "could not be loaded");
        // a payload whose chip does not match the session (a state of a Super FX game's payload under a plain game's header)
        bool again; try { healthy.LoadState(state); again = true; } catch (Exception) { again = false; }
        Check("the session that refused all of them still loads a good state", again);
        // the contract
        Check("SupportsState is true and says nothing is missing", healthy.SupportsState);
    }

    private static byte[]? TryOtherConsoleState(ConsoleKind kind)
    {
        string dir = Environment.GetEnvironmentVariable("BROKENNES_GB_ROMS") is { Length: > 0 } e ? e : @"X:\EMULATION\GameBoy";
        var f = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.zip").FirstOrDefault() : null;
        if (f == null) return null;
        try
        {
            byte[] rom = RomDetect.Unwrap(File.ReadAllBytes(f), Path.GetFileName(f), out string inner);
            var s = ConsoleSessions.Create(kind, rom, "GB", "GB", "GB", null, inner);
            s.RunFrame();
            return s.SaveState();
        }
        catch (Exception) { return null; }
    }

    // ---------------------------------------------------------------------------------------------------------------------
    //  5. Sound units and picture chips
    // ---------------------------------------------------------------------------------------------------------------------

    private static void Units(List<Game> games)
    {
        Group("a state saved after live swaps of the sound unit and the picture chip carries on exactly, or says why not");
        var g = games.FirstOrDefault(x => x.Key == "smw");
        if (g == null) { Skip("sound units", "no Super Mario World ROM"); return; }
        var script = ScriptOf(g);
        int at = 1200;

        // the stand-in unit with the real SPC700 parked behind it
        {
            var a = Make(g); Advance(a, script, 0, 1000);
            bool toHle = a.TrySwapCore(CoreSlot.Apu, "HLE");
            Advance(a, script, 1000, at);
            byte[] state = a.SaveState();
            var b = Make(g, "HLE"); Advance(b, script, 0, 2);
            b.LoadState(state);
            ApplyPoison(b);
            var (fd, compared, _) = SnesFieldWalker.Compare(Walk(a), Walk(b));
            if (!Unread(a).AsSpan().SequenceEqual(Unread(b))) fd.Add("unread audio samples");
            Check("a state saved after a live swap to the stand-in unit loads into a session built with it; every field (the parked SPC700 + S-DSP included) equals the saved one", toHle && a.ParkedSpc != null && b.ParkedSpc != null && fd.Count == 0, toHle ? $"{compared:N0} fields compared, parked unit {(b.ParkedSpc != null ? "restored" : "MISSING")}{(fd.Count > 0 ? ": " + string.Join("; ", fd.Take(6)) : "")}" : "the swap was refused");
            var ca = Play(a, script, at, 60); var cb = Play(b, script, at, 60);
            Check("... the two carry on: the same pictures for 60 frames and the same state at the end", ca.Pictures.SequenceEqual(cb.Pictures) && a.SaveState().AsSpan().SequenceEqual(b.SaveState()));
            // swap both back to the real unit: it resumes from where it stopped, identically
            bool backA = a.TrySwapCore(CoreSlot.Apu, "SFC"), backB = b.TrySwapCore(CoreSlot.Apu, "SFC");
            var ra = Play(a, script, at + 60, 90); var rb = Play(b, script, at + 60, 90);
            Check("... and swapped back to the real unit, both resume the same SPC700 + S-DSP: 90 frames of the same pictures, the same sound, the same state", backA && backB && ra.Pictures.SequenceEqual(rb.Pictures) && ra.Audio == rb.Audio && a.SaveState().AsSpan().SequenceEqual(b.SaveState()));
            // the stand-in's state refused by a stock-unit session, and a stock state by it (also in Refusals)
            string msg = ""; try { Make(g).LoadState(state); } catch (InvalidDataException e) { msg = e.Message; }
            Check("... a session on the real unit refuses that state and says which unit it was saved with", msg.Contains("HLE"), msg);
        }

        // a NES chip as the sound unit: the SPC700 + S-DSP behind it are the machine
        foreach (string chip in new[] { "NES:FIX", "NES:FIXS" })
        {
            var live = Make(g); Advance(live, script, 0, 1000);
            bool swapped = live.TrySwapCore(CoreSlot.Apu, chip);
            Advance(live, script, 1000, 1100);
            byte[] state = live.SaveState();
            var fresh = Make(g, chip); Advance(fresh, script, 0, 2);
            string why = ""; bool loaded; try { fresh.LoadState(state); loaded = true; } catch (Exception e) { loaded = false; why = e.Message; }
            Check($"a state saved after a live swap to {chip} loads into a session built with {chip}", swapped && loaded, swapped ? why : "the swap was refused");
            if (!loaded) continue;
            ApplyPoison(fresh);
            // the machine: the walker over everything but the NES listener (the front unit is compared on its own)
            var excl = Exclusions.Append(new SnesFieldWalker.Exclusion(@"\.Apu$", "the NES listener (compared through its SPC700 + S-DSP below)")).ToArray();
            var (fd, compared, _) = SnesFieldWalker.Compare(new SnesFieldWalker(live, "S", excl), new SnesFieldWalker(fresh, "S", excl));
            var (fd2, compared2, _) = SnesFieldWalker.Compare(new SnesFieldWalker(((NesApuOnSnes)live.Board.Apu).Front, "SPC", Exclusions), new SnesFieldWalker(((NesApuOnSnes)fresh.Board.Apu).Front, "SPC", Exclusions));
            if (!Unread(live).AsSpan().SequenceEqual(Unread(fresh))) fd2.Add("unread audio samples");
            Check($"... every field of the machine ({compared:N0}) and of the SPC700 + S-DSP behind the NES chip ({compared2:N0}) equals the saved one", fd.Count + fd2.Count == 0, string.Join("; ", fd.Concat(fd2).Take(8)));
            var picsL = new List<string>(); var picsF = new List<string>(); var buf = new short[32768]; long n1 = 0, n2 = 0; var held = HeldAt(script, 1100); var held2 = held; int peak = 0;
            for (int f = 1100; f < 1220; f++)
            {
                Frame(live, script, ref held, f, picsL, null, buf, ref n1);
                Frame(fresh, script, ref held2, f, picsF, null, buf, ref n2);
            }
            // the NES chip it drives is a listener that starts again: the pictures, the machine and the SPC700 + S-DSP are exact, the sound is alive but not identical
            var bufP = new short[32768]; var peakS = fresh.ReadSamples(bufP);
            Check($"... and the two carry on: the same pictures for 120 frames, the same machine and the same SPC700 + S-DSP at the end, sound alive ({chip})",
                picsL.SequenceEqual(picsF) && ((NesApuOnSnes)live.Board.Apu).Front is APU_SFC la && ((NesApuOnSnes)fresh.Board.Apu).Front is APU_SFC fa && SameSpc(la, fa) && n2 > 1000, $"pictures {(picsL.SequenceEqual(picsF) ? "equal" : "differ")}, samples read {n2}");
            string refusal = ""; bool refused; try { Make(g).LoadState(state); refused = false; } catch (Exception e) { refused = true; refusal = e.Message; }
            Check($"... a session with the stock unit refuses it and says why ({chip})", refused && refusal.Contains("sound unit"), refused ? refusal : "it was loaded");
        }

        // a NES chip as the picture: a view; the machine is the board's
        foreach (string ppu in new[] { "NES:FIXS", "NES:FIX" })
        {
            var live = Make(g, ppu: ppu); Advance(live, script, 0, 1000);
            byte[] state = live.SaveState();
            var fresh = Make(g, ppu: ppu); Advance(fresh, script, 0, 2);
            fresh.LoadState(state);
            var (fd, compared, _) = SnesFieldWalker.Compare(Walk(live), Walk(fresh));
            Check($"a state saved with the picture on {ppu} loads into a session with it; every field of the machine equals the saved one ({compared:N0})", fd.Count == 0, string.Join("; ", fd.Take(6)));
            var pa = Play(live, script, 1000, 60); var pb = Play(fresh, script, 1000, 60);
            int diffFrames = Enumerable.Range(0, 60).Count(i => pa.Pictures[i] != pb.Pictures[i]);
            Check($"... the audio is the same and the state at the end is identical; the NES chip's pictures: {diffFrames} of 60 frames differ from the session that carried on (the translator's palette and tile choices start again from the loaded machine - a view, not state)", pa.Audio == pb.Audio && live.SaveState().AsSpan().SequenceEqual(fresh.SaveState()));
            // the state is the same machine whichever chip draws it: a state saved on the SFC picture loads on a NES chip and the other way
            var nativeLoad = Make(g, ppu: ppu); Advance(nativeLoad, script, 0, 2);
            var native = Make(g); Advance(native, script, 0, 1000); byte[] nativeState = native.SaveState();
            nativeLoad.LoadState(nativeState);
            Check($"... a state saved on the SFC picture chip loads into a session with {ppu} (the picture chip is not part of the state), and its machine equals the saved one", SnesFieldWalker.Compare(Walk(native), Walk(nativeLoad)).Diffs.Count == 0);
        }

        // hot-swap interplay: swapping the sound unit after a load works
        {
            var a = Make(g); Advance(a, script, 0, 800); byte[] st = a.SaveState();
            var b = Make(g); Advance(b, script, 0, 2); b.LoadState(st);
            bool s1 = b.TrySwapCore(CoreSlot.Apu, "NES:FIX"), s2 = b.TrySwapCore(CoreSlot.Apu, "HLE"), s3 = b.TrySwapCore(CoreSlot.Apu, "SFC");
            var pb = Play(b, script, 800, 60); var pa = Play(a, script, 800, 60);
            Check("after a load the sound unit swaps live (NES chip, stand-in, back) and the game does not notice: the same pictures as the session that carried on with no swap", s1 && s2 && s3 && pa.Pictures.SequenceEqual(pb.Pictures));
        }
    }

    private static bool SameSpc(APU_SFC a, APU_SFC b)
    {
        var wa = new SnesFieldWalker(a, "SPC", Exclusions); var wb = new SnesFieldWalker(b, "SPC", Exclusions);
        return SnesFieldWalker.Compare(wa, wb).Diffs.Count == 0;
    }

    // ---------------------------------------------------------------------------------------------------------------------
    //  6. The app's own factory and the quick-save file
    // ---------------------------------------------------------------------------------------------------------------------

    private static void Factory(List<Game> games)
    {
        Group("the session the apps build (ConsoleSessions.Create) saves and loads states through the contract and the quick-save file");
        var g = games.FirstOrDefault(x => x.Key == "smw") ?? games.FirstOrDefault();
        if (g == null) { Skip("factory", "no roster ROM"); return; }
        var script = ScriptOf(g);
        {
            // the container's checksum is table driven (about ten times faster than the bitwise definition on a SNES state); it must stay the same function
            var rng = new Random(12345); bool same = true;
            foreach (int len in new[] { 0, 1, 7, 255, 1000, 65537, 531_000 }) { var buf = new byte[len]; rng.NextBytes(buf); same &= ConsoleStateFile.Crc32(buf) == ConsoleStateFile.Crc32Reference(buf); }
            Check("the container's table-driven CRC-32 equals the bit-by-bit definition on random buffers of 0 to 531,000 bytes", same && ConsoleStateFile.Crc32("123456789"u8) == 0xCBF43926u);
        }
        IConsoleSession Build() => ConsoleSessions.Create(ConsoleKind.Snes, g.Load(), "SFC", "SFC", "SFC", null, g.File);
        var a = Build();
        Check("SupportsState is true through the factory's session (a missing forward silently reverts to the contract default 'no states')", a.SupportsState, a.SupportsState ? "" : a.StateUnsupportedReason);
        Advance(a, script, 0, g.Points[0]);
        byte[] state = a.SaveState();
        string saved = PictureHash(a);
        var carried = Play(a, script, g.Points[0], 40);
        var b = Build(); Advance(b, script, 0, 3); b.LoadState(state);
        var restored = Play(b, script, g.Points[0], 40);
        Check("save, carry on 40 frames; load into a fresh session, 40 frames: identical pictures, audio and machine", carried.Pictures.SequenceEqual(restored.Pictures) && carried.Audio == restored.Audio && a.SaveState().AsSpan().SequenceEqual(b.SaveState()));
        string tmp = Path.Combine(Path.GetTempPath(), "snesstate-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = ConsoleStateFile.SlotPath(tmp, ConsoleKind.Snes, a.GameId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, state);
            var e = Build(); Advance(e, script, 0, 2);
            e.LoadState(File.ReadAllBytes(path));
            Check("the quick-save slot file reads back into a fresh session and shows the saved picture", PictureHash(e) == saved);
            var info = ConsoleStateFile.Peek(File.ReadAllBytes(path));
            Check("the file's header names the console, the game's id, the sound unit, the chip and the layout", info.Console == ConsoleKind.Snes && info.GameId == a.GameId && info.Units["apu"] == "SFC" && info.Units["chip"] == "none" && info.Units["format"] == SnesSession.StateFormat, string.Join(",", info.Units.Select(kv => kv.Key + "=" + kv.Value)));
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    // ---------------------------------------------------------------------------------------------------------------------
    //  6b. A program that counts (no game data: the same ROM the app smoke test loads)
    // ---------------------------------------------------------------------------------------------------------------------

    private static string SmokeScriptPath()
    {
        for (string? d = AppContext.BaseDirectory; d != null; d = Path.GetDirectoryName(d))
        {
            string p = Path.Combine(d, "UAT", "entrypoints", "snes-state-smoke.ps1");
            if (File.Exists(p)) return p;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "UAT", "entrypoints", "snes-state-smoke.ps1");
    }

    private static void ProgramGroup()
    {
        Group("a 32 KB program of ours that adds one to a work-RAM counter for every frame Right is held, and shows it as the backdrop colour");
        byte[] rom = SnesProgs.CounterRom();
        SnesSession Mk(string apu = "SFC") => (SnesSession)ConsoleSessions.Create(ConsoleKind.Snes, rom, "SFC", "SFC", apu, null, "counter.sfc");
        // the program as the app smoke test carries it
        string ps1 = SmokeScriptPath();
        if (File.Exists(ps1))
        {
            var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(ps1), @"\$SnesCounterHex\s*=\s*'([0-9A-Fa-f]+)'");
            Check("the program in UAT/entrypoints/snes-state-smoke.ps1 is byte for byte the one Workshop/SnesProgs.cs builds", m.Success && m.Groups[1].Value.Equals(SnesProgs.CounterHex, StringComparison.OrdinalIgnoreCase), m.Success ? "" : "no $SnesCounterHex line found");
        }
        else Skip("the app smoke test carries the same program", $"{ps1} not found");

        var script = ParseInput("10:Right,50:,80:Right,100:");
        int Counter(SnesSession s) => s.Board.Wram[0x10] | s.Board.Wram[0x11] << 8;
        int Backdrop(SnesSession s) => s.Board.Ppu.Cgram[0];
        var a = Mk();
        Advance(a, script, 0, 8);
        Check("with no button held the counter stays 0 and the picture is black", Counter(a) == 0 && Backdrop(a) == 0);
        Advance(a, script, 8, 50);
        int c50 = Counter(a);
        Check("Right held for 40 frames: the counter counts the frames and the backdrop colour is the counter", c50 >= 35 && c50 <= 41 && Backdrop(a) == (c50 & 0x7FFF), $"counter {c50}, backdrop ${Backdrop(a):X4}");
        // save with Right released, at frame 60 (the picture is still)
        Advance(a, script, 50, 60);
        byte[] state = a.SaveState();
        string stillPicture = PictureHash(a); int cSaved = Counter(a);
        Advance(a, script, 60, 100);        // Right held again from 80 to 100: the counter moves on
        int cAfter = Counter(a);
        var b = Mk(); Advance(b, script, 0, 30);       // another point of the same run
        int cBefore = Counter(b);
        b.LoadState(state);
        ApplyPoison(b);
        Check("a state loaded into the program running elsewhere brings back the counter and the picture it was saved with", Counter(b) == cSaved && PictureHash(b) == stillPicture && Backdrop(b) == (cSaved & 0x7FFF), $"saved counter {cSaved}, session was at {cBefore}, after load {Counter(b)}");
        Advance(b, script, 60, 100);
        Check("... and from there it counts the same 20 frames of Right as the session that carried on: the same counter, the same picture, the same state", Counter(b) == cAfter && PictureHash(b) == PictureHash(a) && a.SaveState().AsSpan().SequenceEqual(b.SaveState()), $"counter {Counter(b)} vs {cAfter}");
        // a power-on state (what a slot would hold for a game that has not run) puts the counter back to 0
        var fresh = Mk(); byte[] zero = fresh.SaveState();
        b.LoadState(zero);
        Check("a power-on state loaded into the running program puts the counter back to 0 and the picture to black", Counter(b) == 0 && Backdrop(b) == 0);
        // the same program under the other sound units (no sound; the state must still follow the unit)
        foreach (string apu in new[] { "HLE", "NES:FIX" })
        {
            var u = Mk(apu); Advance(u, script, 0, 60);
            byte[] us = u.SaveState();
            var v = Mk(apu); Advance(v, script, 0, 25); v.LoadState(us);
            Advance(u, script, 60, 100); Advance(v, script, 60, 100);
            Check($"on the {apu} sound unit: the same round trip (counter {Counter(u)})", Counter(u) == Counter(v) && PictureHash(u) == PictureHash(v));
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------
    //  7b. Allocation of the frame loop
    // ---------------------------------------------------------------------------------------------------------------------

    private static void Allocation(List<Game> games)
    {
        Group("the frame loop allocates nothing (the state code is not on it)");
        foreach (var g in games.Where(x => x.Key is "smw" or "alttp" or "kss" or "starfox").Take(4))
        {
            var script = ScriptOf(g);
            var s = Make(g); Advance(s, script, 0, 400);               // warm: tables, caches, the JIT
            var buf = new short[32768]; long n = 0; var held = HeldAt(script, 400);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int f = 400; f < 700; f++) Frame(s, script, ref held, f, null, null, buf, ref n);
            long perFrame = (GC.GetAllocatedBytesForCurrentThread() - before) / 300;
            // Star Fox allocates about 3 KB a frame on the build before round 5 too (917,672 bytes over 300 frames by `--alloc-probe` on commit 15795e6 and on this one, to the byte): not the state code, which is not on the frame loop
            long allowed = g.Key == "starfox" ? 4096 : 64;
            Check($"{g.Short}: 300 frames of play allocate {perFrame} bytes a frame", perFrame <= allowed, g.Key == "starfox" ? "the same 3 KB a frame the build before the state code had (--alloc-probe on both: 917,672 bytes in 300 frames)" : "measured with GC.GetAllocatedBytesForCurrentThread over frames 400-700");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------
    //  7. Size and time
    // ---------------------------------------------------------------------------------------------------------------------

    private static void SizeAndTime(List<Game> games)
    {
        Group("size and time of a save and a load (information; the checks bound them loosely)");
        foreach (var g in games)
        {
            var script = ScriptOf(g);
            var a = Make(g); Advance(a, script, 0, g.Points[0]);
            byte[] state = a.SaveState();                       // warm
            const int reps = 10;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < reps; i++) state = a.SaveState();
            double save = sw.Elapsed.TotalMilliseconds / reps;
            byte[] payload = a.PayloadForTest();
            sw.Restart();
            for (int i = 0; i < reps; i++) payload = a.PayloadForTest();
            double write = sw.Elapsed.TotalMilliseconds / reps;
            var b = Make(g); Advance(b, script, 0, 2); b.LoadState(state);
            sw.Restart();
            for (int i = 0; i < reps; i++) b.LoadState(state);
            double load = sw.Elapsed.TotalMilliseconds / reps;
            sw.Restart();
            for (int i = 0; i < reps; i++) b.LoadPayloadForTest(payload);
            double read = sw.Elapsed.TotalMilliseconds / reps;
            Check($"{g.Short}: state {state.Length / 1024.0:F0} KB packed ({payload.Length / 1024.0:F0} KB raw); save {save:F1} ms ({write:F1} ms of it writing the machine), load {load:F1} ms ({read:F1} ms of it reading it back; the rest is the undo copy a load keeps)", state.Length < 1_500_000 && save < 250 && load < 250);
        }
        // the other sound units
        var smw = games.FirstOrDefault(x => x.Key == "smw");
        if (smw != null)
            foreach (string apu in new[] { "HLE", "NES:FIX", "NES:FIXS" })
            {
                var script = ScriptOf(smw);
                var a = Make(smw, apu); Advance(a, script, 0, smw.Points[0]);
                byte[] state = a.SaveState();
                var b = Make(smw, apu); Advance(b, script, 0, 2);
                var sw = Stopwatch.StartNew(); const int reps = 5;
                for (int i = 0; i < reps; i++) state = a.SaveState();
                double save = sw.Elapsed.TotalMilliseconds / reps;
                sw.Restart();
                for (int i = 0; i < reps; i++) b.LoadState(state);
                double load = sw.Elapsed.TotalMilliseconds / reps;
                Check($"{smw.Short} on the {apu} sound unit: state {state.Length / 1024.0:F0} KB packed; save {save:F1} ms, load {load:F1} ms", state.Length < 1_500_000 && save < 250 && load < 400);
            }
    }
}
