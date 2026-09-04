using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NesEmulator;

namespace BrokenNes.Workshop;

/// <summary>
/// A BrokenNes port of the VRUN project's own corruption oracle,
/// <c>sys0/simulations/VRUN/tools/corruption_detector.lua</c> - the test the game's author actually
/// trusts, because every bug it looks for is one a human had to spot on screen and report.
///
/// The Lua runs under Mesen and uses three checks. Two of them could not run here at all until this
/// change: BrokenNes exposed no PPU-register-write hook carrying the current scanline, and no
/// nametable read access. Bus.PpuRegisterWriteObserver and the "Nametable" memory domain
/// (NES.PeekNametable / IPpuProbe) exist to close exactly those two gaps.
///
/// THE THREE CHECKS, and what each is really asking
///
///  1. LATE PPU WRITE - the root cause, and the reason this file exists.
///     The game's NMI ends with ppu_reset_scroll() -> $2005. PPU writes are sequential, so if THAT
///     write is still inside vblank then every earlier write was too. If it has slipped onto a
///     visible scanline the frame has overrun, and a $2007 write landing there does not write
///     linearly - it collides with the PPU's internal render address and scatters into CHR-RAM.
///     That is the mechanism behind the author's "stray pixels in black tiles" and "image jumps up
///     for one frame" reports. Safe lines are 240 (post-render) and 241-260 (vblank); 261
///     (pre-render, where dots 280-304 reload vertical scroll from t) and 0-239 (visible) are not.
///     This is precisely the defect class a 4%-fast or one-scanline-off emulator hides, which is
///     why it belongs in BrokenNes' own test set and not only in Mesen's.
///
///  2. CHR-RAM SCATTER - the damage. Only the deliberately animated metatiles' own tiles may change
///     during gameplay; any other tile changing means a write landed somewhere it was never
///     addressed to. Checksums all 16 bytes of every tile, deliberately: the Lua's first version
///     sampled bytes 0/7/15 for speed and its own chr injection - a byte planted at offset 3 -
///     sailed straight past it.
///
///  3. NAMETABLE FLOOR WRITES - the ambient tile churn is gated on the cell being CM_SOLID. A tile
///     write landing on a cell whose collision_map says non-solid, painting something that is not
///     floor art, means either that gate regressed or a write went astray.
///
/// WHERE THIS DELIBERATELY DIFFERS FROM THE LUA, and why. Stated up front because a port that
/// quietly weakens a check is worse than no port:
///
///   * SYMBOLS. The Lua refuses to run without the build's .mlb, on the grounds that NESFab
///     reallocates RAM freely and a hardcoded address is a time bomb - a trap that has bitten the
///     VRUN project twice. There is no .mlb for the ROM under test here (the Desktop build predates
///     the current source tree, and its symbols after $0384 sit 2 bytes lower than docs/23-ram-map.md
///     says). So the addresses are FLAGS with documented defaults, and the run prints the app_state
///     transitions it observed, so a wrong address shows up as "never reached gameplay" rather than
///     as a silently green run. If a .mlb ever exists for the build under test, pass --mlb and it is
///     read the same way the Lua reads it.
///
///   * ALLOWLISTS. The Lua derives the animated-tile allowlist and the floor-art tile set from
///     map_data.fab (a generated file that is not in the source tree here) and refuses to guess.
///     This port derives the floor-art set BY OBSERVATION - the tiles the game itself has painted
///     on non-solid cells at the moment observation starts - and prints it. That is a real
///     derivation from the ROM under test rather than a guess, and it is overridable with
///     --floor-tiles. The CHR allowlist works the same way via --allowed-tiles, with a calibration
///     mode (--learn) that prints what changed so the set can be pinned deliberately instead of
///     absorbed silently.
///
///   * HEADROOM TELEMETRY. The Lua also reports CPU cycles from NMI entry to the final $2005 write,
///     as advisory margin. There is no NMI-entry hook here, and the Lua's own comment says the
///     scanline is ground truth and the cycle figure is advisory - so this reports margin in DOTS
///     instead: how far the latest vblank write sat from the end of scanline 260. Same question,
///     measured in the unit the check actually gates on.
///
///   * SCANLINE PRECISION. NES.RunFrame catches the PPU up between CPU instructions, not between
///     CPU cycles, so the scanline this reports lags the true dot by up to one instruction (~18
///     dots with --ntsc-frame-timing on, which is the default here). See PPU_FIX's IPpuProbe block.
///     A write within ~18 dots of a scanline boundary can be attributed to the previous line;
///     across the 6820-dot vblank window this check gates on, that is noise.
///
/// PROVING THE DETECTOR - a checker nobody has seen fail proves nothing. --inject reproduces the
/// Lua's VRUN_DET_INJECT and adds the one it documents but never implemented:
///   --inject chr     pokes one byte into CHR tile 16 (the blank-floor metatile). Check 2 must fail.
///   --inject nt      paints a wall tile onto the first non-solid cell. Check 3 must fail.
///   --inject scroll  issues a real $2005 write pair through Bus's own PPU register path at the
///                    instant RunFrame returns, when the PPU counter sits at the top of the frame
///                    rather than in vblank. Check 1 must fail. This goes through the SAME
///                    Bus.WriteSlow -> observer -> PPU_FIX.ProbeScanline path a game write does, so
///                    it exercises the plumbing and not just the verdict arithmetic.
///
/// Usage:
///   --corrupt --rom &lt;path.nes&gt; [--frames N] [--input idle|combat] [--inject none|chr|nt|scroll]
///             [--cpu ID] [--ppu ID] [--apu ID] [--out report.txt] [--learn]
///             [--app-state ADDR] [--collision-map ADDR] [--room-loading ADDR] [--current-room ADDR]
///             [--allowed-tiles CSV] [--floor-tiles CSV] [--chr-bytes N]
///             [--ntsc-frame-timing on|off]
///
/// Exit codes match the other Workshop CLIs:
///   0 clean | 1 corruption detected (or never reached gameplay, or emulator crash)
///   2 usage/IO error | 3 a requested core did not apply | 4 unsupported mapper | 5 unexpected exception
/// </summary>
internal static class VrunCorruptCli
{
    private const string Usage =
        "Usage: --corrupt --rom <path.nes> [options]\n" +
        "  --frames N          frames to OBSERVE after gameplay starts (default 1800).\n" +
        "  --input MODE        drive pattern during observation. Default combat.\n" +
        "                        combat  cycle a direction every 90 frames with B held, which churns\n" +
        "                                blocks, score and the HUD - the combination that produced\n" +
        "                                the reported corruption. This is the Lua's own drive.\n" +
        "                        idle    hold nothing.\n" +
        "                        roam    combat, plus force a room transition every few seconds by\n" +
        "                                re-seating the player on a passable edge cell and pushing.\n" +
        "                                This POKES player_x/player_y - it is the same trick the\n" +
        "                                project's own UAT/vrun-shophunt.ps1 uses, because organic\n" +
        "                                navigation out of the spawn room does not happen under any\n" +
        "                                fixed button pattern. It buys coverage of step_room_load,\n" +
        "                                which is where the Lua's author found the one real bug this\n" +
        "                                detector has ever caught, at the cost of a perturbed run -\n" +
        "                                so a failure seen ONLY in roam mode must be reproduced with\n" +
        "                                a script: drive before it is believed.\n" +
        "  --player-x/--player-y ADDR   roam only (defaults 0x327/0x328).\n" +
        "                        script:<frame:Buttons,...>  an explicit --input-style script, frames\n" +
        "                                counted from the START OF OBSERVATION. Use this to drive a\n" +
        "                                known route through several rooms; combat and idle both tend\n" +
        "                                to stay in the spawn room, which leaves room-transition\n" +
        "                                frames - the ones the Lua's own author found a real bug on -\n" +
        "                                completely unobserved. The report prints how many rooms were\n" +
        "                                actually entered so that coverage is never implicit.\n" +
        "  --inject none|chr|nt|scroll   deliberately introduce a defect at observation frame 30 so\n" +
        "                      the corresponding check can be SEEN to fail. Default none.\n" +
        "  --learn             report every CHR tile that changed and every tile value written onto a\n" +
        "                      non-solid cell, and do not fail on either. Use this to derive the two\n" +
        "                      allowlists deliberately; it is not a pass/fail mode.\n" +
        "  --out FILE          also write the report here (it always goes to stdout).\n" +
        "  --app-state ADDR    work-RAM address of app_state (default 0x3AD, the Desktop build).\n" +
        "                      0=Title 1=Game 2=Death 3=Shop 4=Ending 5=Options.\n" +
        "  --room-loading ADDR optional: gate 'in gameplay' on this reading 0 as well as app_state==1.\n" +
        "  --current-room ADDR optional (default 0x358): reported per room transition, not gated on.\n" +
        "  --collision-map ADDR base of the 16x14 collision map (default 0x500).\n" +
        "  --mlb FILE          read app_state/room_loading/collision_map/current_room from a NESFab\n" +
        "                      .mlb instead, the way the Lua does. Overrides the four flags above.\n" +
        "  --allowed-tiles CSV CHR tiles that may legitimately change (the animated metatiles). With\n" +
        "                      none given, check 2 reports the changing set but cannot fail on it -\n" +
        "                      see --learn.\n" +
        "  --floor-tiles CSV   tile values that count as floor/blank art. Default: derived from the\n" +
        "                      ROM under test - every tile the game has painted on a non-solid cell at\n" +
        "                      the instant observation starts.\n" +
        "  --chr-bytes N       bytes of CHR-RAM to scan for check 2 (default 4096, i.e. tiles 0-255,\n" +
        "                      matching the Lua's own range).\n" +
        "  --boot-input SCRIPT frame:buttons steps for the boot phase (default \"60:Start,66:\").\n" +
        "  --max-boot-frames N give up if gameplay is not reached within this many frames (default 900).\n" +
        "  --ntsc-frame-timing on|off  default on - 89342/89341 alternating dots per frame, which is\n" +
        "                      what makes the scanline this check reads mean the same thing as Mesen's.";

    private const int WorkRamSize = 2048;
    private const int RoomW = 16, RoomH = 14;      // main.fab: ct U ROOM_W / ROOM_H
    private const int NtRowTiles = 32;
    private const int NtCells = 960;               // 30 rows x 32 - the Lua's own scan range
    private const byte CmSolid = 1;                // main.fab: ct U CM_SOLID = 1

    private enum Inject { None, Chr, Nt, Scroll }

    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();

        string? romPath = null, cpu = null, ppu = null, apu = null, outPath = null, mlbPath = null;
        string inputMode = "combat", bootInput = "60:Start,66:";
        int frames = 1800, maxBootFrames = 900, chrBytes = 4096;
        bool learn = false, ntscFrameTiming = true;
        var inject = Inject.None;
        int addrAppState = 0x3AD, addrCollisionMap = 0x500, addrCurrentRoom = 0x358;
        int addrRoomLoading = -1, addrPlayerX = 0x327, addrPlayerY = 0x328;
        HashSet<int>? allowedTiles = null, floorTilesOverride = null;

        try
        {
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--rom": romPath = args[++i]; break;
                    case "--cpu": cpu = args[++i]; break;
                    case "--ppu": ppu = args[++i]; break;
                    case "--apu": apu = args[++i]; break;
                    case "--frames": frames = int.Parse(args[++i]); break;
                    case "--input": inputMode = args[++i].ToLowerInvariant(); break;
                    case "--inject": inject = ParseInject(args[++i]); break;
                    case "--learn": learn = true; break;
                    case "--out": outPath = args[++i]; break;
                    case "--mlb": mlbPath = args[++i]; break;
                    case "--app-state": addrAppState = ParseAddr(args[++i]); break;
                    case "--room-loading": addrRoomLoading = ParseAddr(args[++i]); break;
                    case "--current-room": addrCurrentRoom = ParseAddr(args[++i]); break;
                    case "--collision-map": addrCollisionMap = ParseAddr(args[++i]); break;
                    case "--player-x": addrPlayerX = ParseAddr(args[++i]); break;
                    case "--player-y": addrPlayerY = ParseAddr(args[++i]); break;
                    case "--allowed-tiles": allowedTiles = ParseCsv(args[++i]); break;
                    case "--floor-tiles": floorTilesOverride = ParseCsv(args[++i]); break;
                    case "--chr-bytes": chrBytes = int.Parse(args[++i]); break;
                    case "--boot-input": bootInput = args[++i]; break;
                    case "--max-boot-frames": maxBootFrames = int.Parse(args[++i]); break;
                    case "--ntsc-frame-timing": ntscFrameTiming = args[++i].Equals("on", StringComparison.OrdinalIgnoreCase); break;
                    default:
                        Console.Error.WriteLine($"Unknown argument: {args[i]}\n{Usage}");
                        return 2;
                }
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or FormatException)
        {
            Console.Error.WriteLine($"Bad arguments: {ex.Message}\n{Usage}");
            return 2;
        }

        if (romPath == null) { Console.Error.WriteLine(Usage); return 2; }
        if (frames < 1) { Console.Error.WriteLine("--frames must be >= 1"); return 2; }

        List<RomTestCli.InputStep>? driveScript = null;
        if (inputMode.StartsWith("script:", StringComparison.Ordinal))
        {
            try { driveScript = RomTestCli.ParseInputScript(inputMode["script:".Length..]); }
            catch (FormatException fe) { Console.Error.WriteLine($"Bad --input script: {fe.Message}"); return 2; }
        }
        else if (inputMode != "combat" && inputMode != "idle" && inputMode != "roam")
        {
            Console.Error.WriteLine("--input must be idle, combat, roam, or script:<frame:Buttons,...>");
            return 2;
        }

        var report = new Report(outPath);

        // Symbols from a .mlb if one was given. Same reader idiom as the Lua's loadSymbols(): the
        // R: records are work-RAM labels, "R:<hex>:<name>@...".
        if (mlbPath != null)
        {
            try
            {
                var sym = ReadMlb(mlbPath);
                string[] required = { "app_state", "collision_map" };
                foreach (var r in required)
                    if (!sym.ContainsKey(r)) { Console.Error.WriteLine($"--mlb: symbol '{r}' not found in {mlbPath}. Refusing to run with hardcoded addresses."); return 2; }
                addrAppState = sym["app_state"];
                addrCollisionMap = sym["collision_map"];
                if (sym.TryGetValue("room_loading", out int rl)) addrRoomLoading = rl;
                if (sym.TryGetValue("current_room", out int cr)) addrCurrentRoom = cr;
                report.Line($"symbols: {sym.Count} from {mlbPath}");
            }
            catch (Exception ex) { Console.Error.WriteLine($"--mlb: {ex.Message}"); return 2; }
        }

        List<RomTestCli.InputStep> bootScript;
        try { bootScript = RomTestCli.ParseInputScript(bootInput); }
        catch (FormatException fe) { Console.Error.WriteLine($"Bad --boot-input: {fe.Message}"); return 2; }

        byte[] romBytes;
        string romFullPath;
        try
        {
            romFullPath = Path.GetFullPath(romPath);
            romBytes = File.ReadAllBytes(romFullPath);
        }
        catch (Exception ex) { Console.Error.WriteLine($"Failed to read ROM: {ex.Message}"); return 2; }

        try
        {
            var nes = new NES { RomName = Path.GetFileName(romFullPath), RomPath = romFullPath };
            nes.LoadROM(romBytes);

            var cpuApply = RomTestCli.ApplyCore("CPU", cpu, nes.SetCpuCore, nes.GetCpuCoreId);
            var ppuApply = RomTestCli.ApplyCore("PPU", ppu, nes.SetPpuCore, nes.GetPpuCoreId);
            var apuApply = RomTestCli.ApplyCore("APU", apu, nes.SetApuCore, nes.GetApuCoreId);
            if (!cpuApply.Applied || !ppuApply.Applied || !apuApply.Applied)
            {
                foreach (var c in new[] { cpuApply, ppuApply, apuApply })
                    Console.Error.WriteLine($"  {c.Kind}: requested={c.Requested ?? "(default)"} effective={c.Effective} applied={c.Applied}");
                return 3;
            }

            var speed = nes.GetSpeedConfig();
            if (speed != null) speed.NtscAccurateFrameRate = ntscFrameTiming;
            nes.EnableEventScheduler = false;
            nes.SetCrashBehavior(NES.CrashBehavior.RedScreen);

            // A PPU core without IPpuProbe cannot answer either of the two questions this tool was
            // built for. Refuse loudly rather than report "0 late writes" from a scanline of -1.
            var (probeSl, _, _) = nes.GetPpuTiming();
            bool probeAvailable = probeSl >= 0 && nes.GetNametableSize() > 0;
            if (!probeAvailable)
            {
                Console.Error.WriteLine(
                    $"PPU core '{ppuApply.Effective}' does not implement IPpuProbe, so it can report neither the\n" +
                    "scanline a PPU register write landed on (check 1) nor the nametable contents (check 3).\n" +
                    "Re-run with --ppu FIX. Refusing to emit a green report from unavailable data.");
                return 3;
            }

            int chrSize = nes.GetChrSize();
            if (chrBytes > chrSize) chrBytes = chrSize;
            int chrTiles = chrBytes / 16;

            var f = new Findings();
            int frameNo = 0;            // absolute frames run, 1-based at the point checks see it
            int observeFrame = -1;      // frames since observation started, -1 while booting
            bool observing = false;

            // ---- CHECK 1 wiring: the PPU register write observer -----------------------------
            // Attached before the first frame runs, exactly as the Lua registers its memory
            // callback at script load, so title-screen and transition writes are counted too. The
            // `rendering` gate is what keeps those from being false positives: menu and transition
            // code legitimately writes VRAM with rendering off.
            nes.SetPpuRegisterWriteObserver((reg, value, scanline, dot, mask) =>
            {
                if (reg != 0x2005) return;              // the Lua hooks $2005 and only $2005
                f.ScanlineHist.TryGetValue(scanline, out int n);
                f.ScanlineHist[scanline] = n + 1;
                bool rendering = (mask & 0x18) != 0;    // background enabled | sprites enabled
                if (!rendering) { f.NotRendering++; return; }
                if (!ScanlineUnsafe(scanline))
                {
                    // Margin telemetry: how close to the cliff the latest safe write sat. vblank
                    // ends at the end of scanline 260, so distance-to-cliff in dots is what is left
                    // of the vblank window after this write.
                    int dotsLeft = (260 - scanline) * 341 + (340 - dot);
                    if (f.MinDotsLeft < 0 || dotsLeft < f.MinDotsLeft)
                    {
                        f.MinDotsLeft = dotsLeft;
                        f.MinDotsLeftAt = $"frame {frameNo}, scanline {scanline} dot {dot}";
                    }
                    return;
                }
                f.LateWrite++;
                if (observing) f.LateWriteObserving++; else f.LateWriteBoot++;
                if (scanline >= 0 && scanline <= 239 && scanline > f.WorstVisibleScanline) f.WorstVisibleScanline = scanline;
                Note(f.LateWriteExamples, string.Format(CultureInfo.InvariantCulture,
                    "frame {0}: $2005 write at scanline {1} dot {2} ({3}), mask={4:x2}",
                    frameNo, scanline, dot, scanline == 261 ? "pre-render" : "VISIBLE", mask));
            });

            // ---- boot: press Start, then wait for gameplay -----------------------------------
            var held = new bool[8];
            int nextStep = 0, stableGameplay = 0;
            int lastAppState = -1;
            var stateLog = new List<string>();

            while (!observing)
            {
                while (nextStep < bootScript.Count && bootScript[nextStep].Frame <= frameNo)
                {
                    held = (bool[])bootScript[nextStep].Held.Clone();
                    nextStep++;
                }
                nes.SetInputs(held, null);
                nes.RunFrame();
                frameNo++;
                if (nes.IsCrashed())
                {
                    report.Line($"FAIL: emulator crashed during boot at frame {frameNo}: {nes.GetCrashInfo()}");
                    report.Close();
                    return 1;
                }

                int app = nes.PeekSystemRam(addrAppState);
                if (app != lastAppState)
                {
                    stateLog.Add($"frame {frameNo}: app_state {(lastAppState < 0 ? "-" : lastAppState.ToString())} -> {app}");
                    lastAppState = app;
                }
                bool inGame = app == 1 && (addrRoomLoading < 0 || nes.PeekSystemRam(addrRoomLoading) == 0);
                stableGameplay = inGame ? stableGameplay + 1 : 0;
                if (stableGameplay > 30) { observing = true; observeFrame = 0; break; }

                if (frameNo > maxBootFrames)
                {
                    report.Line($"FAIL: never reached gameplay within {maxBootFrames} frames " +
                                $"(app_state at ${addrAppState:X4} reads {app}).");
                    foreach (var s in stateLog) report.Line("  " + s);
                    report.Line("  If app_state never left 0, the address is probably wrong for this build -");
                    report.Line("  pass --app-state or --mlb. See this file's header on the symbol trap.");
                    report.Close();
                    return 1;
                }
            }

            report.Line($"gameplay reached at frame {frameNo} (app_state stable for 30 frames)");
            foreach (var s in stateLog) report.Line("  " + s);

            // ---- snapshots that the two content checks diff against --------------------------
            var chrBase = new int[chrTiles];
            for (int t = 0; t < chrTiles; t++) chrBase[t] = TileSum(nes, t);

            var ntBase = new byte[NtCells];
            for (int i = 0; i < NtCells; i++) ntBase[i] = nes.PeekNametable(i);

            // FLOOR_TILES, derived from the ROM under test rather than guessed: every tile the game
            // itself is currently drawing on a cell its own collision_map calls non-solid. Those are
            // by construction the tiles a legitimate repaint of an empty cell can produce. The Lua
            // gets the same set out of map_data.fab's metatile_tile_tl/tr/bl/br arrays; that file is
            // generated and absent from the source tree here, so this reads the answer off the
            // running game instead. Printed below so a human can sanity-check it.
            var floorTiles = floorTilesOverride ?? DeriveFloorTiles(nes, ntBase, addrCollisionMap);
            string floorSrc = floorTilesOverride != null ? "--floor-tiles" : "derived from non-solid cells at observation start";

            report.Line($"floor-art tiles ({floorSrc}): {string.Join(",", floorTiles.OrderBy(x => x))}");
            report.Line($"CHR scan: {chrTiles} tiles ({chrBytes} bytes of {chrSize})");
            report.Line($"CHR allowlist: {(allowedTiles == null ? "(none given - check 2 reports but cannot fail; see --learn)" : string.Join(",", allowedTiles.OrderBy(x => x)))}");

            int lastRoom = nes.PeekSystemRam(addrCurrentRoom);
            int roomChanges = 0;
            var roomLog = new List<string>();
            var driveHeld = new bool[8];
            int driveStep = 0;
            var roamer = inputMode == "roam"
                ? new Roamer(addrAppState, addrCollisionMap, addrCurrentRoom, addrPlayerX, addrPlayerY)
                : null;

            // ---- observation ------------------------------------------------------------------
            for (observeFrame = 0; observeFrame < frames; observeFrame++)
            {
                if (driveScript != null)
                {
                    while (driveStep < driveScript.Count && driveScript[driveStep].Frame <= observeFrame)
                    {
                        driveHeld = (bool[])driveScript[driveStep].Held.Clone();
                        driveStep++;
                    }
                }
                else if (inputMode == "roam") driveHeld = roamer!.Next(nes, observeFrame);
                else driveHeld = DriveInput(inputMode, observeFrame);
                nes.SetInputs(driveHeld, null);
                nes.RunFrame();
                frameNo++;
                if (nes.IsCrashed())
                {
                    report.Line($"FAIL: emulator crashed at frame {frameNo} (observation frame {observeFrame}): {nes.GetCrashInfo()}");
                    report.Close();
                    return 1;
                }

                if (observeFrame == 30 && inject != Inject.None)
                    DoInject(nes, inject, ntBase, addrCollisionMap, floorTiles, report, f);

                // CHECK 2 - CHR scatter
                for (int t = 0; t < chrTiles; t++)
                {
                    int s = TileSum(nes, t);
                    if (s == chrBase[t]) continue;
                    chrBase[t] = s;
                    f.ChrChanged.TryGetValue(t, out int c);
                    f.ChrChanged[t] = c + 1;
                    if (allowedTiles != null && allowedTiles.Contains(t)) continue;
                    if (allowedTiles == null && !learn) continue; // reported, not failed - see --learn
                    if (learn) continue;
                    f.ChrScatter++;
                    f.ChrScatterTiles.TryGetValue(t, out int sc);
                    f.ChrScatterTiles[t] = sc + 1;
                    Note(f.ChrExamples, $"frame {frameNo}: CHR tile {t} changed (not in the allowlist)");
                }

                // CHECK 3 - nametable writes onto empty floor
                for (int i = 0; i < NtCells; i++)
                {
                    byte v = nes.PeekNametable(i);
                    if (v == ntBase[i]) continue;
                    ntBase[i] = v;
                    int tx = i % NtRowTiles, ty = i / NtRowTiles;
                    int cx = tx / 2, cy = ty / 2;
                    if (cy >= RoomH) continue;              // rows 28-29 are the HUD, not the room
                    byte flags = nes.PeekSystemRam(addrCollisionMap + cy * RoomW + cx);
                    if ((flags & CmSolid) != 0) continue;   // solid cell: churn here is the normal case
                    f.NonSolidWrites++;
                    f.NonSolidTileValues.TryGetValue(v, out int nv);
                    f.NonSolidTileValues[v] = nv + 1;
                    // A non-solid cell being repainted is NORMAL when what lands is floor art:
                    // destroying a breakable and opening a door both turn a cell non-solid and then
                    // legitimately repaint it as floor. The defect is the opposite - wall art on a
                    // cell that is not solid.
                    if (floorTiles.Contains(v)) continue;
                    if (learn) continue;
                    f.NtFloor++;
                    Note(f.NtExamples, $"frame {frameNo}: tile ${v:x2} written on non-solid cell ({cx},{cy}) flags={flags}");
                }

                int room = nes.PeekSystemRam(addrCurrentRoom);
                if (room != lastRoom)
                {
                    roomChanges++;
                    if (roomLog.Count < 12) roomLog.Add($"frame {frameNo}: room {lastRoom} -> {room}");
                    lastRoom = room;
                }
            }

            nes.SetPpuRegisterWriteObserver(null);

            // ---- verdict ----------------------------------------------------------------------
            int bad = 0;
            report.Line("");
            report.Line("================ VRUN corruption detector (BrokenNes) ================");
            report.Line($"rom={Path.GetFileName(romFullPath)} cores={cpuApply.Effective}/{ppuApply.Effective}/{apuApply.Effective}");
            report.Line($"mode={inputMode} observed-frames={frames} (absolute frames run={frameNo}) inject={inject.ToString().ToLowerInvariant()}" +
                        $"{(learn ? " LEARN (nothing can fail)" : "")}");
            // Coverage, stated rather than assumed. A run that never left the spawn room has not
            // observed a single room transition - and a room transition is exactly where the Lua's
            // own author found the one real bug this detector has caught to date (step_room_load's
            // 32-byte nametable row write stacking with a HUD digit write). A green report from a
            // 0-room run is a narrower claim than a green report from a 6-room one, so the number
            // is printed on its own line every time.
            report.Line($"rooms entered during observation: {roomChanges}" +
                        (roomChanges == 0 ? "  <-- COVERAGE: no room transition was observed in this run" : ""));
            if (roamer != null)
                report.Line($"  roam drive: {roamer.Reseats} player re-seats, {roamer.DeadEnds} edges with no passable cell. " +
                            "This run POKED player_x/player_y - see --input roam.");
            foreach (var r in roomLog) report.Line("  " + r);
            report.Line("");

            if (f.LateWrite > 0)
            {
                bad++;
                report.Line($"FAIL  late PPU write : {f.LateWrite} $2005 writes landed outside vblank while rendering " +
                            $"({f.LateWriteBoot} during boot/title, {f.LateWriteObserving} during observation)");
                if (f.WorstVisibleScanline >= 0) report.Line($"        worst visible scanline reached: {f.WorstVisibleScanline}");
                foreach (var e in f.LateWriteExamples) report.Line("        " + e);
            }
            else
            {
                report.Line("ok    late PPU write : none - every $2005 write with rendering on landed inside vblank");
            }
            report.Line("        scanline distribution of $2005 writes -> " +
                        string.Join("  ", f.ScanlineHist.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}")));
            report.Line($"        ({f.NotRendering} more with rendering disabled - menus/transitions, legitimately unchecked)");

            if (f.ChrScatter > 0)
            {
                bad++;
                report.Line($"FAIL  CHR scatter    : {f.ChrScatter} unexpected tile changes -> " +
                            string.Join(" ", f.ChrScatterTiles.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}(x{kv.Value})")));
                foreach (var e in f.ChrExamples) report.Line("        " + e);
            }
            else if (allowedTiles == null && !learn)
            {
                report.Line($"INFO  CHR scatter    : {f.ChrChanged.Count} distinct tiles changed, {f.ChrChanged.Values.Sum()} changes total.");
                report.Line("        No --allowed-tiles given, so this cannot pass or fail - it is an observation.");
            }
            else
            {
                report.Line("ok    CHR scatter    : none - only allowlisted tiles changed");
            }
            if (f.ChrChanged.Count > 0)
                report.Line("        tiles that changed -> " +
                            string.Join(" ", f.ChrChanged.OrderBy(kv => kv.Key).Take(40).Select(kv => $"{kv.Key}(x{kv.Value})")) +
                            (f.ChrChanged.Count > 40 ? $" ... {f.ChrChanged.Count - 40} more" : ""));

            if (f.NtFloor > 0)
            {
                bad++;
                report.Line($"FAIL  floor writes   : {f.NtFloor} non-floor tile writes onto non-solid cells");
                foreach (var e in f.NtExamples) report.Line("        " + e);
            }
            else
            {
                report.Line("ok    floor writes   : none - every write onto a non-solid cell was floor art");
            }
            report.Line($"        {f.NonSolidWrites} writes onto non-solid cells in total; tile values seen -> " +
                        string.Join(" ", f.NonSolidTileValues.OrderBy(kv => kv.Key).Take(20).Select(kv => $"${kv.Key:x2}(x{kv.Value})")) +
                        (f.NonSolidTileValues.Count > 20 ? $" ... {f.NonSolidTileValues.Count - 20} more" : ""));

            int latestSafe = f.ScanlineHist.Keys.Where(k => k >= 241 && k <= 260).DefaultIfEmpty(-999).Max();
            string tag = latestSafe >= 260 ? "WARN " : "ok   ";
            report.Line($"{tag} margin        : latest safe $2005 write on scanline {latestSafe} of 260 (vblank ends there); " +
                        $"tightest margin {(f.MinDotsLeft < 0 ? "n/a" : f.MinDotsLeft + " dots")}" +
                        (f.MinDotsLeftAt == null ? "" : $" at {f.MinDotsLeftAt}"));

            report.Line("");
            if (learn) report.Line("RESULT: LEARN RUN - no verdict. Pin --allowed-tiles / --floor-tiles from the sets above.");
            else if (bad == 0) report.Line("RESULT: CLEAN");
            else report.Line($"RESULT: CORRUPTION DETECTED ({bad} of 3 checks failed)");
            report.Line("======================================================================");
            report.Close();
            return (learn || bad == 0) ? 0 : 1;
        }
        catch (Cartridge.UnsupportedMapperException ume)
        {
            Console.Error.WriteLine($"Unsupported mapper {ume.MapperId} ({ume.MapperName}).");
            return 4;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 5;
        }
    }

    /// <summary>
    /// Scanline safety, measured on this game rather than assumed (the Lua's own comment block):
    ///   241..260  vblank            - safe, and where healthy frames land
    ///   240       post-render idle  - safe
    ///   261       PRE-RENDER        - NOT safe: the PPU is fetching again, and dots 280-304 of this
    ///                                 line reload the vertical scroll from t, so a $2005 write
    ///                                 landing here can be clobbered or mis-set.
    ///   0..239    visible render    - NOT safe: worst case a whole-frame split, and $2007 writes
    ///                                 scatter into CHR-RAM.
    /// The Lua tests both -1 and 261 because Mesen numbers pre-render -1; PPU_FIX numbers it 261.
    /// </summary>
    private static bool ScanlineUnsafe(int sl) => sl == 261 || sl == -1 || (sl >= 0 && sl <= 239);

    /// <summary>Checksum all 16 bytes of a CHR tile. Deliberately all 16: the Lua's first version
    /// sampled bytes 0/7/15 and its own chr-injection proof - a byte planted at offset 3 - went
    /// completely undetected. A sampling detector silently misses 13/16 of what it exists to find.</summary>
    private static int TileSum(NES nes, int tile)
    {
        int b = tile * 16, s = 0;
        for (int i = 0; i < 16; i++) s += nes.PeekChr(b + i) * (i + 1);
        return s;
    }

    private static HashSet<int> DeriveFloorTiles(NES nes, byte[] ntBase, int collisionMap)
    {
        var set = new HashSet<int>();
        for (int i = 0; i < NtCells; i++)
        {
            int tx = i % NtRowTiles, ty = i / NtRowTiles;
            int cx = tx / 2, cy = ty / 2;
            if (cy >= RoomH) continue;
            byte flags = nes.PeekSystemRam(collisionMap + cy * RoomW + cx);
            if ((flags & CmSolid) == 0) set.Add(ntBase[i]);
        }
        return set;
    }

    /// <summary>
    /// Fault injection, so the detector can be PROVEN rather than trusted. Each of these is the
    /// smallest thing that must make exactly one check fail.
    /// </summary>
    private static void DoInject(NES nes, Inject inject, byte[] ntBase, int collisionMap,
                                 HashSet<int> floorTiles, Report report, Findings f)
    {
        switch (inject)
        {
            case Inject.Chr:
                // Scribble on the blank-floor metatile's CHR, exactly what a scattered $2007 write
                // does. Offset 3 on purpose - that is the byte that caught the Lua's own
                // three-sample first version out.
                nes.PokeChr(16 * 16 + 3, 0xA5);
                report.Line("[INJECT] corrupted CHR tile 16 byte 3 (the blank-floor metatile)");
                break;

            case Inject.Nt:
            {
                // Paint a non-floor tile onto an empty floor cell, through the Nametable domain
                // this change added. The value is chosen so it cannot accidentally be floor art.
                byte wall = 0x42;
                while (floorTiles.Contains(wall)) wall++;
                for (int cy = 0; cy < RoomH; cy++)
                {
                    for (int cx = 0; cx < RoomW; cx++)
                    {
                        if ((nes.PeekSystemRam(collisionMap + cy * RoomW + cx) & CmSolid) != 0) continue;
                        int idx = (cy * 2) * NtRowTiles + (cx * 2);
                        nes.PokeNametable(idx, wall);
                        report.Line($"[INJECT] wrote tile ${wall:x2} onto empty floor cell ({cx},{cy}) at nametable +{idx}");
                        return;
                    }
                }
                report.Line("[INJECT] nt: no non-solid cell found to write to - injection did NOT happen");
                break;
            }

            case Inject.Scroll:
            {
                // The one the Lua documents as VRUN_DET_INJECT=scroll but never implemented.
                //
                // Issue a real $2005 write pair down Bus's own PPU register path at the instant
                // RunFrame returns. The PPU's dot budget for a frame is exactly 262*341, so at that
                // instant its counter has wrapped back to the top of the frame - scanline 0 or the
                // tail of 261 - which is precisely the "outside vblank" condition check 1 exists to
                // catch. Two writes, not one, so the shared $2005 latch is left balanced and the
                // only thing being injected is the timing.
                //
                // What this proves: the observer fires, Bus routes it, PPU_FIX reports the live
                // scanline, and the checker classifies that scanline as unsafe - the whole chain.
                // What it does not prove: that a mis-timed write by the GAME would be caught at some
                // other point in the frame, which is a claim about the game, not the detector.
                var (sl, dot, mask) = nes.GetPpuTiming();
                nes.PokeCpu(0x2005, 0x00);
                nes.PokeCpu(0x2005, 0x00);
                report.Line($"[INJECT] issued a $2005 write pair with the PPU at scanline {sl} dot {dot} (mask=${mask:x2}, " +
                            $"rendering={((mask & 0x18) != 0 ? "on" : "OFF - check 1 correctly ignores it")})");
                break;
            }
        }
    }

    private static bool[] DriveInput(string mode, int pf)
    {
        var b = new bool[8];
        if (mode != "combat") return b;
        // Move and fire, alternating direction, to churn blocks, score, HUD digits and the redraw
        // queues - the combination that produced the reported HUD corruption. B fires (and needs a
        // direction held to do anything); A only jumps, which is why it is absent.
        int d = (pf / 90) % 4;
        switch (d)
        {
            case 0: b[7] = true; break; // Right
            case 1: b[6] = true; break; // Left
            case 2: b[4] = true; break; // Up
            case 3: b[5] = true; break; // Down
        }
        b[1] = true; // B
        return b;
    }

    /// <summary>
    /// The "roam" drive. Organic navigation out of the spawn room does not happen: measured on
    /// game.nes, every fixed pattern tried - hold a direction, hold direction+B, pulse B, jump then
    /// walk, in all four directions, over 1500 frames - left current_room at 0, with the player
    /// walking left and stalling against a blocking cell at x=176. The project's OWN harness
    /// (UAT/vrun-shophunt.ps1, Cross-Door) reached the same conclusion and solves it the same way:
    /// re-seat the player on a passable edge cell by poking player_x/player_y, then push.
    ///
    /// This is a deliberate perturbation of the run, and the report says so. It exists because a
    /// detector that never sees a room transition never sees step_room_load, and step_room_load is
    /// where the single real bug the Lua detector has ever caught actually lived.
    /// </summary>
    private sealed class Roamer
    {
        private readonly int _appState, _collisionMap, _currentRoom, _playerX, _playerY;
        private int _dirIdx, _timer, _lastRoom = -1;
        private bool _seated;
        public int Reseats, Crossings, DeadEnds;

        public Roamer(int appState, int collisionMap, int currentRoom, int playerX, int playerY)
        { _appState = appState; _collisionMap = collisionMap; _currentRoom = currentRoom; _playerX = playerX; _playerY = playerY; }

        // Order matches nbr_id / nbr_flags in the game: Left, Right, Up, Down.
        private static readonly int[] DirButton = { 6, 7, 4, 5 };

        public bool[] Next(NES nes, int frame)
        {
            var b = new bool[8];
            int app = nes.PeekSystemRam(_appState);
            if (app != 1)
            {
                // A shop (3), death (2) or ending (4) screen. Tap B every half second to back out
                // rather than sitting in it for the rest of the run.
                if (frame % 30 == 0) b[1] = true;
                _seated = false; _timer = 0;
                return b;
            }

            int room = nes.PeekSystemRam(_currentRoom);
            if (_lastRoom < 0) _lastRoom = room;
            if (room != _lastRoom) { _lastRoom = room; Crossings++; _seated = false; _timer = 0; _dirIdx = (_dirIdx + 1) & 3; }

            if (!_seated)
            {
                // Prefer an actual door cell (CM_DOOR, bit 3); fall back to any non-solid cell,
                // because a door already shot open has had CM_DOOR and CM_SOLID cleared and is now
                // just a hole. Exactly the fallback vrun-shophunt.ps1's Cross-Door uses.
                // NB: _dirIdx must NOT advance on success - the push below has to be in the same
                // direction as the edge we just seated against. Advancing it here (a for-loop
                // increment that still runs on the exit iteration) had the player seated on the
                // left edge and then pushed right, for 1800 frames and 15 re-seats with zero
                // crossings, which looked exactly like "the poke does not take".
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    if (TrySeat(nes, _dirIdx)) { _seated = true; Reseats++; break; }
                    _dirIdx = (_dirIdx + 1) & 3;
                }
                if (!_seated) { DeadEnds++; _timer = 0; return b; }
                _timer = 0;
            }

            b[DirButton[_dirIdx]] = true;
            b[1] = (_timer % 16) < 6;                 // pulse B - fire is edge-triggered, not held
            if (_timer % 24 == 23) TrySeat(nes, _dirIdx); // gravity drags the player off the door row
            if (++_timer > 120) { _seated = false; _dirIdx = (_dirIdx + 1) & 3; }
            return b;
        }

        private bool TrySeat(NES nes, int dir)
        {
            var cm = new byte[RoomW * RoomH];
            for (int i = 0; i < cm.Length; i++) cm[i] = nes.PeekSystemRam(_collisionMap + i);

            var cells = new List<(int cx, int cy)>();
            for (int pass = 0; pass < 2 && cells.Count == 0; pass++)
            {
                // pass 0: real door cells. pass 1: anything not solid.
                bool Ok(byte f) => pass == 0 ? (f & 8) != 0 : (f & CmSolid) == 0;
                switch (dir)
                {
                    case 0: for (int cy = 0; cy < RoomH; cy++) if (Ok(cm[cy * RoomW + 0])) cells.Add((0, cy)); break;
                    case 1: for (int cy = 0; cy < RoomH; cy++) if (Ok(cm[cy * RoomW + 15])) cells.Add((15, cy)); break;
                    case 2: for (int cx = 0; cx < RoomW; cx++) if (Ok(cm[0 * RoomW + cx])) cells.Add((cx, 0)); break;
                    default: for (int cx = 0; cx < RoomW; cx++) if (Ok(cm[13 * RoomW + cx])) cells.Add((cx, 13)); break;
                }
            }
            if (cells.Count == 0) return false;

            var (mx, my) = cells[cells.Count / 2];
            switch (dir)
            {
                case 0: nes.PokeSystemRam(_playerY, (byte)(my * 16)); nes.PokeSystemRam(_playerX, 24); break;
                case 1: nes.PokeSystemRam(_playerY, (byte)(my * 16)); nes.PokeSystemRam(_playerX, 224); break;
                case 2: nes.PokeSystemRam(_playerX, (byte)(mx * 16)); nes.PokeSystemRam(_playerY, 24); break;
                default: nes.PokeSystemRam(_playerX, (byte)(mx * 16)); nes.PokeSystemRam(_playerY, 200); break;
            }
            return true;
        }
    }

    private static void Note(List<string> list, string s) { if (list.Count < 6) list.Add(s); }

    private static Inject ParseInject(string s) => s.ToLowerInvariant() switch
    {
        "none" => Inject.None,
        "chr" => Inject.Chr,
        "nt" => Inject.Nt,
        "scroll" => Inject.Scroll,
        _ => throw new FormatException($"--inject must be none|chr|nt|scroll, got '{s}'"),
    };

    private static int ParseAddr(string s)
    {
        s = s.Trim();
        if (s.StartsWith("$", StringComparison.Ordinal)) return int.Parse(s[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return int.Parse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return int.Parse(s, CultureInfo.InvariantCulture);
    }

    private static HashSet<int> ParseCsv(string s)
    {
        var set = new HashSet<int>();
        foreach (var t in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            set.Add(ParseAddr(t));
        return set;
    }

    private static Dictionary<string, int> ReadMlb(string path)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            // R:<hex>:<name>@... - work-RAM label records, the same ones the Lua reads.
            if (!line.StartsWith("R:", StringComparison.Ordinal)) continue;
            int c2 = line.IndexOf(':', 2);
            if (c2 < 0) continue;
            int at = line.IndexOf('@', c2 + 1);
            string name = at < 0 ? line[(c2 + 1)..] : line[(c2 + 1)..at];
            if (int.TryParse(line[2..c2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int addr))
                map[name] = addr;
        }
        if (map.Count == 0) throw new InvalidDataException($"no R: records in {path}");
        return map;
    }

    private sealed class Findings
    {
        public int LateWrite, LateWriteBoot, LateWriteObserving, NotRendering;
        public int WorstVisibleScanline = -1;
        public readonly List<string> LateWriteExamples = new();
        public readonly Dictionary<int, int> ScanlineHist = new();
        public int MinDotsLeft = -1;
        public string? MinDotsLeftAt;

        public int ChrScatter;
        public readonly Dictionary<int, int> ChrScatterTiles = new();
        public readonly Dictionary<int, int> ChrChanged = new();
        public readonly List<string> ChrExamples = new();

        public int NtFloor, NonSolidWrites;
        public readonly Dictionary<int, int> NonSolidTileValues = new();
        public readonly List<string> NtExamples = new();
    }

    private sealed class Report
    {
        private readonly StreamWriter? _file;
        public Report(string? path)
        {
            if (path != null) _file = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" };
        }
        public void Line(string s) { Console.WriteLine(s); _file?.WriteLine(s); }
        public void Close() { Console.Out.Flush(); _file?.Flush(); _file?.Dispose(); }
    }
}
