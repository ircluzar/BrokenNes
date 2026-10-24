using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes.Workshop;

/// <summary>
/// <c>--wms-test</c>'s milestone-6 checks (docs/projects/widemess-spec.md section 6), each enhancement against a negative control:
/// <list type="bullet">
/// <item>E1 the profiles folder: every file reads; the format's checks refuse broken files and say why.</item>
/// <item>E2 synthetic scenes on the picture chip itself: no flicker draws more sprites only on a line over the SNES's limits and STAT77 keeps the SNES's count; widescreen draws the side parts of
/// sprites, fill and masks take them away, the centre stays the SNES chip's.</item>
/// <item>E3 the S-DSP: cubic interpolation keeps a high tone the Gaussian dulls, leaves a low one as it is, and is bit-identical to the stock DSP when off.</item>
/// <item>E4 fast CPU: the speed ROM, locked, runs at fast mode's speed with the console's fast CPU on and at the SNES's with it off.</item>
/// <item>E5 who gets enhancements: not a .wms game, not the SNES console, not a game without a profile (widescreen), and a game that unlocks WideMess loses them.</item>
/// <item>E6 the proven games (<c>--enhance-games KEY;KEY</c> or every roster game found in <c>--snes-dir</c>), each in lockstep with the SNES console: widescreen keeps the centre the SNES's
/// picture every frame, STAT77 and the sound identical, and switched off the picture is the SNES's frame for frame; no flicker changes only lines over the SNES's limits; better sound changes
/// only the sound; fast CPU (reported, with when it first departs from the SNES).</item>
/// <item>E7 speed: the enhanced picture's frames per second, and no allocation per frame.</item>
/// </list>
/// </summary>
internal static partial class WideMessCli
{
    /// <summary><c>--wms-enhance-test [--roms DIR] [--snes-dir DIR] [--enhance-games KEY;KEY] [--profiles DIR] [--reference-paths]</c>: milestone 6's checks alone (<c>--wms-test</c> runs them after
    /// the other milestones'). Exit 0 when every check passes.</summary>
    public static int EnhanceTest(string[] args)
    {
        RomTestCli.EnsureConsole();
        Consoles.WideMessEnabled = true;
        string dir = Opt(args, "roms", Path.Combine(AppContext.BaseDirectory, "WideMess", "testroms", "bin"));
        fails = passes = 0;
        if (args.Any(a => a.Equals("--reference-paths", StringComparison.OrdinalIgnoreCase)))
        {
            PPU_SFC.FastPaths = DSP_SFC.FastPaths = BOARD_SFC.FastPaths = false;
            Console.WriteLine("(reference paths: the SNES chips' fast paths are off)");
        }
        EnhanceTests(args, dir);
        Console.WriteLine($"{passes} passed, {fails} failed");
        return fails == 0 ? 0 : 1;
    }

    private static void EnhanceTests(string[] args, string dir)
    {
        if (args.Any(a => a.Equals("--no-enhance", StringComparison.OrdinalIgnoreCase))) return;
        string profiles = Opt(args, "profiles", "");
        if (profiles != "") WmsProfiles.Directory = profiles;

        Console.WriteLine($"E1. The profiles folder ({WmsProfiles.Directory})");
        WmsProfiles.Reload();
        var all = WmsProfiles.All;
        Check(all.Count >= EnhanceRoster.Length && WmsProfiles.Errors.Count == 0, $"every profile file reads ({all.Count} profiles)", WmsProfiles.Errors.Count == 0 ? string.Join(", ", all.Select(p => p.Source)) : string.Join(" | ", WmsProfiles.Errors));
        ProfileRefusals();

        Console.WriteLine("E2. Synthetic sprite scenes on the WideMess picture chip");
        SyntheticSprites();

        Console.WriteLine("E3. Better sound: cubic interpolation on the S-DSP (a synthetic voice)");
        SyntheticSound();

        Console.WriteLine("E4. Fast CPU from the console: the speed ROM, never unlocked (its SNES image on the WideMess console)");
        FastCpuSpeedRom(dir);

        Console.WriteLine("E5. Who gets enhancements, and the unlock taking them away");
        WhoGetsThem(dir);

        string snesDir = Opt(args, "snes-dir", DefaultSnesDir);
        string list = Opt(args, "enhance-games", "");
        var games = list != "" ? list.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(k => EnhanceRoster.FirstOrDefault(g => g.Key == k) ?? throw new ArgumentException($"unknown game '{k}'")).ToArray()
                               : EnhanceRoster.Where(g => File.Exists(Path.Combine(snesDir, g.File))).ToArray();
        if (games.Length == 0) { Console.WriteLine($"E6. (no proven games found in {snesDir}: skipped)"); return; }
        Console.WriteLine($"E6. The proven games, each in lockstep with the SNES console up to its contact sheet's last moment ({games.Length} games)");
        var results = new GameResult[games.Length];
        Parallel.For(0, games.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 4)) },
            i => results[i] = ProveGame(games[i], Path.Combine(snesDir, games[i].File)));
        foreach (var r in results) ReportGame(r);

        Console.WriteLine("E7. Speed: the enhanced picture (widescreen, every line through the wide compositor and the profile's rules)");
        foreach (var key in new[] { "smw", "fzero", "smk" })
        {
            var g = games.FirstOrDefault(x => x.Key == key);
            if (g != null) EnhancedSpeed(g, Path.Combine(snesDir, g.File));
        }
    }

    // ------------------------------------------------------------------ E1

    private static void ProfileRefusals()
    {
        const string good = """
        { "format": "widemess-profile-1", "title": "T", "sha1": ["0123456789abcdef0123456789abcdef01234567"],
          "widescreen": { "layers": { "bg1": "extend", "bg2": "repeat", "bg3": "fill", "bg4": "extend", "sprites": "fill" },
                          "masks": [ { "why": "w", "layers": ["bg1"], "rows": [0, 31], "rule": "fill", "when": { "bgmode": "$09", "bg1sc": "$7C/$FC" } } ] },
          "enhancements": { "widescreen": "good", "noFlicker": "good", "betterSound": "good", "fastCpu": "risky" },
          "recommended": ["widescreen"], "issues": [], "verified": { "date": "d", "how": "h" } }
        """;
        try
        {
            var p = WmsProfile.Parse(good, "good.json");
            Check(p.Widescreen!.Layers[1] == WmsSideRule.Repeat && p.Widescreen.Masks[0].When.Length == 2 && p.Widescreen.Masks[0].When[1].Mask == 0xFC, "a well-formed profile reads (rules, masks, scene conditions)");
        }
        catch (Exception e) { Check(false, "a well-formed profile reads", e.Message); }
        (string change, string from, string to, string mustSay)[] bad =
        {
            ("an unknown key", "\"issues\": []", "\"issues\": [], \"colour\": 1", "unknown key 'colour'"),
            ("an unknown side rule", "\"bg2\": \"repeat\"", "\"bg2\": \"stretch\"", "'stretch'"),
            ("sprites that repeat", "\"sprites\": \"fill\"", "\"sprites\": \"repeat\"", "extend or fill"),
            ("a black mask over one layer", "\"rule\": \"fill\"", "\"rule\": \"black\"", "every layer"),
            ("a mask reaching into the centre", "\"rule\": \"fill\",", "\"rule\": \"fill\", \"to\": 80,", "0-72"),
            ("a scene condition that can never match", "\"$7C/$FC\"", "\"$7D/$FC\"", "never matches"),
            ("a condition on an unknown register", "\"bgmode\"", "\"vram\"", "'vram'"),
            ("widescreen offered without rules", "\"widescreen\": { \"layers\"", "\"unused\": { \"layers\"", "no widescreen rules"),
            ("recommending what it calls unavailable", "\"fastCpu\": \"risky\"", "\"fastCpu\": \"unavailable\" ", "fastCpu"),
            ("a bad SHA-1", "01234567\"]", "0123456\"]", "not a SHA-1"),
            ("another format", "widemess-profile-1", "widemess-profile-9", "format"),
        };
        foreach (var (change, from, to, mustSay) in bad)
        {
            string json = good.Replace(from, to);
            if (change.StartsWith("recommending")) json = json.Replace("\"recommended\": [\"widescreen\"]", "\"recommended\": [\"fastCpu\"]");
            if (change.StartsWith("widescreen offered")) json = System.Text.RegularExpressions.Regex.Replace(json, "\"unused\": \\{.*?\\] \\},", "", System.Text.RegularExpressions.RegexOptions.Singleline);
            try { WmsProfile.Parse(json, "bad.json"); Check(false, $"refuses a profile with {change}", "it was read"); }
            catch (InvalidDataException e) { Check(e.Message.Contains(mustSay, StringComparison.OrdinalIgnoreCase), $"refuses a profile with {change}, and says why", e.Message); }
        }
        // the folder: a broken file is reported for its game, never skipped; two files claiming one game are refused
        string tmp = Path.Combine(Path.GetTempPath(), "wms_m6_profiles_" + Environment.ProcessId);
        string saved = WmsProfiles.Directory;
        try
        {
            Directory.CreateDirectory(tmp);
            foreach (var f in Directory.GetFiles(tmp)) File.Delete(f);
            File.WriteAllText(Path.Combine(tmp, "a.json"), good);
            File.WriteAllText(Path.Combine(tmp, "b.json"), good.Replace("\"title\": \"T\"", "\"title\": \"U\""));
            File.WriteAllText(Path.Combine(tmp, "c.json"), good.Replace("0123456789abcdef0123456789abcdef01234567", "1111111111111111111111111111111111111111").Replace("\"good\"", "\"great\""));
            WmsProfiles.Directory = tmp;
            var dup = WmsProfiles.Find("0123456789abcdef0123456789abcdef01234567");
            var broken = WmsProfiles.Find("1111111111111111111111111111111111111111");
            Check(dup.Profile?.Title == "T" && WmsProfiles.Errors.Any(e => e.Contains("already")), "two files claiming one game: the second is refused, and says so", string.Join(" | ", WmsProfiles.Errors));
            Check(broken.Profile == null && broken.Error != null && broken.Describe().Contains("broken"), "a broken profile is reported for its game ('the profile ... is broken: why'), not taken for 'no profile'", broken.Describe());
            Check(WmsProfiles.Find("2222222222222222222222222222222222222222").Describe() == "no profile for this game", "a game no file names: 'no profile for this game'");
        }
        finally { WmsProfiles.Directory = saved; try { Directory.Delete(tmp, true); } catch (IOException) { } }
    }

    // ------------------------------------------------------------------ E2

    /// <summary>A sprite scene written straight into a picture chip: 40 small sprites on rows 100-107 (X = 0, 6, ... 234), 10 on rows 50-57, two at the SNES edges (X = -4 and X = 300 on rows
    /// 150-157), a solid white tile, a black backdrop.</summary>
    private static void SpriteScene(PPU_SFC p)
    {
        p.Reset();
        for (int w = 16; w < 24; w++) p.Vram[w] = 0x00FF;              // tile 1, 4 bpp: colour 1 everywhere
        p.Cgram[0] = 0; p.Cgram[129] = 0x7FFF;
        Array.Clear(p.Oam);
        for (int n = 0; n < 128; n++) p.Oam[n * 4 + 1] = 240;          // off the picture
        void Put(int n, int x, int y) { p.Oam[n * 4] = (byte)x; p.Oam[n * 4 + 1] = (byte)y; p.Oam[n * 4 + 2] = 1; p.Oam[n * 4 + 3] = 0x30; if ((x & 0x100) != 0) p.Oam[0x200 + (n >> 2)] |= (byte)(1 << ((n & 3) * 2)); }
        for (int i = 0; i < 40; i++) Put(i, i * 6, 100);
        for (int i = 0; i < 10; i++) Put(40 + i, i * 20, 50);
        Put(50, 0x1FC, 150);                                             // X = -4: 4 columns in the SNES picture, 4 in the left side
        Put(51, 300, 150);                                               // X = 300: off a SNES, at picture column 372 on a wide one
        Put(52, 260, 150);                                               // X = 260: off a SNES, at picture column 332 (4-11 out from the SNES edge)
        Put(53, 0x100, 150);                                             // X = 256: the SNES's -256, where games hide sprites (columns 328-335 if it were drawn)
        p.WriteRegister(0x01, 0x00);                                     // OBSEL (also refreshes the chip's sprite cache)
        p.WriteRegister(0x2C, 0x10);                                     // TM: sprites
        p.WriteRegister(0x00, 0x0F);                                     // full brightness
    }

    private static uint[] DrawFrame(PPU_SFC p, out (bool range, bool time) flags)
    {
        p.BeginFrame();
        for (int line = 1; line <= 224; line++) p.RenderLine(line);
        flags = p is PPU_WMS w ? w.SpriteOverflowFlags : p.SpriteOverflowFlags;
        if (p is PPU_WMS { FrameIsWide: true } wide) return wide.GetWideFrame().AsSpan(0, 400 * 224).ToArray();
        return p.FrameBuffer.AsSpan(0, 256 * 224).ToArray();
    }

    private static int White(uint[] f, int width, int row, int from = 0, int to = -1)
    {
        if (to < 0) to = width;
        int n = 0;
        for (int x = from; x < to; x++) if (f[row * width + x] == 0xFFFFFFFFu) n++;
        return n;
    }

    private static void SyntheticSprites()
    {
        var snes = new PPU_SFC(); SpriteScene(snes);
        var snesFrame = DrawFrame(snes, out var snesFlags);
        var wms = new PPU_WMS(); SpriteScene(wms);
        var plain = DrawFrame(wms, out var plainFlags);
        Check(plain.SequenceEqual(snesFrame) && plainFlags == snesFlags, "no enhancement: the WideMess chip draws the scene exactly as the SNES chip", $"STAT77 range {snesFlags.range}, time {snesFlags.time}");

        wms.SetEnhancements(null, noFlicker: true);
        var nf = DrawFrame(wms, out var nfFlags);
        int s100 = White(snesFrame, 256, 100), n100 = White(nf, 256, 100);
        bool sameElsewhere = Enumerable.Range(0, 224).Where(r => r is < 100 or > 107).All(r => nf.AsSpan(r * 256, 256).SequenceEqual(snesFrame.AsSpan(r * 256, 256)));
        Check(s100 == 194 && n100 == 242, "no flicker: the line with 40 sprites shows all 40 (242 lit columns); the SNES drops the last 8 at its 32-sprite limit (194)", $"SNES {s100}, no flicker {n100}");
        Check(sameElsewhere, "no flicker: every line within the SNES's limits (the 10-sprite line, the empty ones) is the SNES's, pixel for pixel");
        Check(nfFlags == snesFlags && nfFlags.range, "no flicker: STAT77 still says what a SNES says (more than 32 sprites on a line)", $"range {nfFlags.range}, time {nfFlags.time}");
        Check(wms.EnhancedOverflowRow[100] && !wms.EnhancedOverflowRow[50], "the enhanced pass marks exactly the lines over the SNES's limits");

        var extend = WmsWidescreen.AllExtend();
        wms.SetEnhancements(extend, noFlicker: false);
        var wf = DrawFrame(wms, out var wFlags);
        bool centre = Enumerable.Range(0, 224).All(r => wf.AsSpan(r * 400 + 72, 256).SequenceEqual(snesFrame.AsSpan(r * 256, 256)));
        int left = White(wf, 400, 150, 64, 72), right = White(wf, 400, 150, 372, 380), centreEdge = White(wf, 400, 150, 72, 76);
        Check(centre && wFlags == snesFlags, "widescreen: the centre 256 columns are the SNES chip's picture on every line, STAT77 the same");
        Check(left == 4 && centreEdge == 4 && right == 8, "widescreen, sprites extended: X = -4 shows 4 columns in the centre and its other 4 in the left side; X = 300 (off a SNES) shows at picture column 372", $"left side {left}, centre {centreEdge}, right side {right}");
        Check(White(wf, 400, 100, 0, 72) == 0 && White(wf, 400, 100, 328, 400) == 0, "widescreen alone keeps the SNES's limit: the 8 sprites past 32 on row 100 are not drawn in the sides either (they are inside the SNES picture)");
        Check(White(wf, 400, 150, 328, 332) == 0 && White(wf, 400, 150, 332, 340) == 8, "a sprite at X = 256 (the SNES's -256, where games hide sprites) is not drawn in the sides; one at X = 260 is", $"328-331: {White(wf, 400, 150, 328, 332)}, 332-339: {White(wf, 400, 150, 332, 340)}");

        var fill = WmsWidescreen.AllExtend(); fill.Layers[WmsWidescreen.Sprites] = WmsSideRule.Fill;
        wms.SetEnhancements(fill, false);
        var ff = DrawFrame(wms, out _);
        Check(White(ff, 400, 150, 0, 72) == 0 && White(ff, 400, 150, 328, 400) == 0 && White(ff, 400, 150, 72, 76) == 4, "negative control, sprites filled: nothing in the sides, the centre unchanged");

        var masked = WmsWidescreen.AllExtend();
        masked.Masks = new[] { new WmsMask { Layers = 0x10, Top = 150, Bottom = 151, Rule = WmsSideRule.Fill, Right = false } };
        wms.SetEnhancements(masked, false);
        var mf = DrawFrame(wms, out _);
        Check(White(mf, 400, 150, 64, 72) == 0 && White(mf, 400, 152, 64, 72) == 4 && White(mf, 400, 150, 372, 380) == 8, "a mask (sprites, left side, rows 150-151): fills exactly its rectangle (row 152 and the right side keep their sprites)");

        var black = WmsWidescreen.AllExtend();
        black.Masks = new[] { new WmsMask { Layers = 0x1F, Top = 0, Bottom = 223, Rule = WmsSideRule.Black, From = 40, Left = false, When = new[] { new WmsCondition(WmsRegister.Tm, 0xFF, 0x10) } } };
        wms.SetEnhancements(black, false);
        var bf = DrawFrame(wms, out _);
        wms.WriteRegister(0x2C, 0x11);   // the scene condition (TM = $10) no longer holds
        var bf2 = DrawFrame(wms, out _);
        wms.WriteRegister(0x2C, 0x10);
        Check(White(bf, 400, 150, 372, 380) == 0 && White(bf2, 400, 150, 372, 380) == 8 && White(bf, 400, 150, 332, 340) == 8 && White(bf, 400, 150, 64, 72) == 4,
            "a black mask with a scene condition (TM = $10, right side, 40-72 out): black only while the condition holds, only in its band");
        wms.SetEnhancements(null, false);
        var off = DrawFrame(wms, out _);
        Check(off.SequenceEqual(snesFrame), "every enhancement off again: the SNES chip's own picture");
    }

    // ------------------------------------------------------------------ E3

    /// <summary>One voice of a BRR loop (16 samples a block) at pitch 1.0, at full gain and volume: the left output's peak over 4,000 samples.</summary>
    private static (int peak, ulong hash) PlayTone(int[] nibbles, bool cubic)
    {
        var aram = new byte[0x10000];
        var dsp = new DSP_SFC(aram) { CubicInterpolation = cubic };
        aram[0x200] = 0x00; aram[0x201] = 0x03; aram[0x202] = 0x00; aram[0x203] = 0x03;   // directory entry 0: start and loop $0300
        int blocks = nibbles.Length / 16;
        for (int b = 0; b < blocks; b++)
        {
            int a = 0x300 + b * 9;
            aram[a] = (byte)(0xC0 | (b == blocks - 1 ? 0x03 : 0x00));                   // shift 12, filter 0; the last block ends and loops
            for (int i = 0; i < 8; i++) aram[a + 1 + i] = (byte)((nibbles[b * 16 + 2 * i] & 0xF) << 4 | (nibbles[b * 16 + 2 * i + 1] & 0xF));
        }
        foreach (var (r, v) in new (int, byte)[] { (0x6C, 0x20), (0x5D, 0x02), (0x0C, 0x7F), (0x1C, 0x7F), (0x00, 0x7F), (0x01, 0x7F), (0x02, 0x00), (0x03, 0x10), (0x04, 0x00), (0x05, 0x00), (0x07, 0x7F), (0x4C, 0x01) })
            dsp.Write(r, v);
        int peak = 0, n = 0; ulong h = 14695981039346656037UL;
        dsp.Output = (l, r) => { if (++n > 400) peak = Math.Max(peak, Math.Abs((int)l)); h = (h ^ (ushort)l) * 1099511628211UL; };
        for (int i = 0; i < 4000; i++) dsp.RunSample();
        return (peak, h);
    }

    private static void SyntheticSound()
    {
        var high = Enumerable.Range(0, 16).Select(i => (i & 1) == 0 ? 0 : (i & 2) != 0 ? -7 : 7).ToArray();          // 0, 7, 0, -7: a tone at a quarter of the rate
        var low = Enumerable.Range(0, 32).Select(i => (int)Math.Round(7 * Math.Sin(2 * Math.PI * i / 32))).ToArray(); // one cycle in 32 samples
        var (gh, gHash) = PlayTone(high, false); var (ch, _) = PlayTone(high, true);
        var (gl, _) = PlayTone(low, false); var (cl, _) = PlayTone(low, true);
        Check(ch >= gh * 1.4, "a high tone (a quarter of the 32 kHz rate) at pitch 1.0: the cubic gives the samples as they are, the Gaussian dulls it", $"peak {gh} Gaussian, {ch} cubic ({ch / (double)gh:F2}x)");
        Check(Math.Abs(cl - gl) <= gl * 0.06, "a low tone (1 kHz): the two agree within 6% (better sound changes the highs, not the level)", $"peak {gl} Gaussian, {cl} cubic");
        var stock = new DSP_SFC(new byte[0x10000]);
        Check(!stock.CubicInterpolation && PlayTone(high, false).hash == gHash, "off (the default, and always on a SNES): the stock Gaussian, the same samples run after run");
    }

    // ------------------------------------------------------------------ E4

    private static void FastCpuSpeedRom(string dir)
    {
        string speedNo = Path.Combine(dir, "speed_nounlock.wms");
        if (!File.Exists(speedNo)) { Check(false, "the speed ROM's no-unlock twin", $"missing {speedNo}"); return; }
        byte[] image = WmsFile.Payload(File.ReadAllBytes(speedNo), out _);   // its SNES image: a SNES game that never unlocks
        int[] Measure(bool fast)
        {
            using var s = Session(ConsoleKind.WideMess, image);
            if (fast && s.SetEnhancements(fastCpu: true) is { } why) { Check(false, "the console's fast CPU switches on for a locked SNES game", why); return new int[3]; }
            var w = s.Board.Wram;
            for (int f = 0; f < 120 && w[0x1FF] != 0xA5; f++) Run(s, 1);
            int Wd(int o) => w[0x100 + o] | w[0x101 + o] << 8;
            return new[] { Wd(0x02), Wd(0x06), w[0x101] | (s.WideMess!.Unlocked ? 0x100 : 0) };
        }
        var off = Measure(false); var on = Measure(true);
        double pa = PredictedCount(LoopA, Nmi), paf = PredictedCount(LoopAFast, NmiFast), pb = PredictedCount(LoopB, Nmi), pbf = PredictedCount(LoopBFast, NmiFast);
        Check(Near(off[0], pa, 0.005) && Near(off[1], pb, 0.005), "off: the locked game runs at SNES speed (loops A and B as spec 2 predicts)", $"A {off[0]} (predicted {pa:F0}), B {off[1]} (predicted {pb:F0})");
        Check(Near(on[0], paf, 0.005) && Near(on[1], pbf, 0.005), "on: the same locked game runs at fast mode's speed (2 clocks a cycle, the I/O register at 6)", $"A {on[0]} (predicted {paf:F0}), B {on[1]} (predicted {pbf:F0})");
        Check(on[2] == 0x42 && off[2] == 0x42, "the game never sees it: WMSPEED reads open bus ($42), the extension stays locked", $"WMSPEED read {on[2] & 0xFF:X2}{((on[2] & 0x100) != 0 ? ", UNLOCKED" : "")}");
    }

    // ------------------------------------------------------------------ E5

    private static void WhoGetsThem(string dir)
    {
        byte[] layersSfc = File.ReadAllBytes(Path.Combine(dir, "layers.sfc")), layersWms = File.ReadAllBytes(Path.Combine(dir, "layers.wms"));
        using (var s = Session(ConsoleKind.WideMess, layersWms))
        {
            string? why = s.SetEnhancements(noFlicker: true);
            Check(why != null && why.Contains("WideMess game") && !s.Enhancements.Any, "a .wms game gets no enhancements, and is told why", why ?? "accepted");
        }
        using (var s = Session(ConsoleKind.Snes, layersSfc))
        {
            string? why = s.SetEnhancements(noFlicker: true);
            Check(why != null && why.Contains("WideMess console") && !s.Enhancements.Any, "on the SNES console: none (they are the WideMess console's)", why ?? "accepted");
        }
        using (var s = Session(ConsoleKind.WideMess, layersSfc))
        {
            var look = s.EnhanceProfile;
            string? why = s.SetEnhancements(widescreen: true);
            Check(look.Profile == null && why != null && why.Contains("no profile for this game"), "a SNES game without a profile: widescreen is refused with 'no profile for this game'", why ?? "accepted");
            Check(s.SetEnhancements(noFlicker: true, betterSound: true) == null && s.Enhancements is { NoFlicker: true, BetterSound: true }, "without a profile, no flicker and better sound (generic) switch on");
            s.SetScratchProfile(AllExtendProfile(s.ImageSha1));
            Check(s.SetEnhancements(widescreen: true) == null && s.Enhancements.Widescreen, "with a scratch profile (the HTTP API's), widescreen switches on");
            Run(s, 60);
            Check(s.WideMess is { Unlocked: true } && !s.Enhancements.Any && s.EnhanceNote.Contains("unlocked") && !s.WideMessPpu!.Enhanced,
                "the game unlocks WideMess (it knows it): its enhancements go off at the unlock, and the session says why", s.EnhanceNote);
            Run(s, 1);
            using var plain = Session(ConsoleKind.WideMess, layersSfc);
            Run(plain, 61);
            Check(Hash(s.Frame.AsSpan(0, s.FrameWidth * s.FrameHeight)) == Hash(plain.Frame.AsSpan(0, plain.FrameWidth * plain.FrameHeight)) && s.FrameWidth == 400,
                "after the unlock the picture is the game's own (frame 61 equals a run without enhancements)");
        }
    }

    // ------------------------------------------------------------------ E6

    private sealed record GameResult(EnhanceGame Game, string? Refused, int Frames, string ProfileNote, bool WideOffered,
        int WideCentreFrames, int WideCentreFirst, int WideLineDiffer, int WideFlagFrames, bool WideSound, int OffFrames, int OffDiffer,
        long NfLines, long NfDifferOutsideOverflow, long NfOverflowRows, int NfFlagFrames, bool NfSound,
        int BsPictureDiffer, bool BsSoundDiffers, int BsFlagFrames, bool SnesSilent,
        int FastFirstDiff, bool FastAlive, int LateFirstDiff, bool LateAlive, double Seconds);

    /// <summary>One proven game: the SNES console and three WideMess consoles in lockstep (widescreen with its profile, no flicker, better sound), then fast CPU on its own.</summary>
    private static GameResult ProveGame(EnhanceGame g, string path)
    {
        var sw = Stopwatch.StartNew();
        byte[] rom = RomDetect.Unwrap(File.ReadAllBytes(path), path, out _);
        int frames = g.Moments.Max();
        var pad = ParsePad(g.Script);
        using var snes = Session(ConsoleKind.Snes, rom);
        using var wide = Session(ConsoleKind.WideMess, rom);
        using var nf = Session(ConsoleKind.WideMess, rom);
        using var bs = Session(ConsoleKind.WideMess, rom);
        var look = wide.EnhanceProfile;
        bool offered = look.Profile?.Verdict(WmsEnhancement.Widescreen) is WmsVerdict.Good or WmsVerdict.Risky or WmsVerdict.Broken;
        if (!offered) wide.SetScratchProfile(AllExtendProfile(wide.ImageSha1));   // widescreen unavailable for the game: the centre is still proven, on every layer extended
        string? why = wide.SetEnhancements(widescreen: true) ?? nf.SetEnhancements(noFlicker: true) ?? bs.SetEnhancements(betterSound: true);
        if (why != null) return new GameResult(g, why, 0, look.Describe(), offered, 0, -1, 0, 0, false, 0, 0, 0, 0, 0, 0, false, 0, false, 0, false, -1, false, -1, false, 0);
        wide.WideMessPpu!.CompareEnhanced = true;
        nf.WideMessPpu!.CompareEnhanced = true;
        var snesHashes = new ulong[frames + 1];
        ulong hs = 14695981039346656037UL, hw = hs, hn = hs, hb = hs;
        var buf = new short[65536];
        int centreFrames = 0, centreFirst = -1, wideFlags = 0, nfFlags = 0, bsFlags = 0, bsPic = 0, offFrames = 0, offDiffer = 0;
        long nfOverflowRows = 0;
        bool silent = true;
        int offAt = Math.Max(1, frames - 300);
        PadButtons held = 0;
        ulong Audio(SnesSession s, ulong h, bool track)
        {
            int n;
            while ((n = s.ReadSamples(buf)) > 0) for (int i = 0; i < n; i++) { h = (h ^ (ushort)buf[i]) * 1099511628211UL; if (track && buf[i] != 0) silent = false; }
            return h;
        }
        for (int f = 1; f <= frames; f++)
        {
            if (pad.TryGetValue(f, out var b)) held = b;
            foreach (var s in new[] { snes, wide, nf, bs }) { s.SetPad(0, held); s.RunFrame(); }
            hs = Audio(snes, hs, true); hw = Audio(wide, hw, false); hn = Audio(nf, hn, false); hb = Audio(bs, hb, false);
            int h = snes.Ppu.VisibleHeight;
            var sf = snes.Ppu.FrameBuffer;
            snesHashes[f] = Hash(snes.Frame.AsSpan(0, snes.FrameWidth * snes.FrameHeight));
            if (f == offAt) wide.SetEnhancements(widescreen: false);
            if (f <= offAt)
            {
                bool same = wide.FrameWidth == 400;
                for (int y = 0; same && y < h; y++) same = wide.Frame.AsSpan(y * 400 + 72, 256).SequenceEqual(sf.AsSpan(y * 256, 256));
                if (!same) { centreFrames++; if (centreFirst < 0) centreFirst = f; }
            }
            else
            {
                offFrames++;
                if (wide.FrameWidth != snes.FrameWidth || Hash(wide.Frame.AsSpan(0, wide.FrameWidth * wide.FrameHeight)) != snesHashes[f]) offDiffer++;
            }
            var flags = snes.Ppu.SpriteOverflowFlags;
            if (wide.Ppu.SpriteOverflowFlags != flags) wideFlags++;
            if (nf.Ppu.SpriteOverflowFlags != flags) nfFlags++;
            if (bs.Ppu.SpriteOverflowFlags != flags) bsFlags++;
            for (int y = 0; y < h; y++) if (nf.WideMessPpu.EnhancedOverflowRow[y]) nfOverflowRows++;
            if (bs.FrameWidth != snes.FrameWidth || Hash(bs.Frame.AsSpan(0, bs.FrameWidth * bs.FrameHeight)) != snesHashes[f]) bsPic++;
        }
        var wp = wide.WideMessPpu; var np = nf.WideMessPpu;
        // fast CPU on its own: when does it first depart from the SNES picture, and is the game alive at the end
        // twice: from power-on, and switched on at the contact sheet's first moment (after the game has loaded its sound driver)
        (int first, bool alive) FastRun(int from)
        {
            using var fast = Session(ConsoleKind.WideMess, rom);
            if (from == 0) fast.SetEnhancements(fastCpu: true);
            PadButtons hold = 0;
            int maxColours = 0, firstDiff = -1; var lastHashes = new HashSet<ulong>();
            for (int f = 1; f <= frames; f++)
            {
                if (f == from + 1 && from > 0) fast.SetEnhancements(fastCpu: true);
                if (pad.TryGetValue(f, out var b)) hold = b;
                fast.SetPad(0, hold); fast.RunFrame(); Audio(fast, 0, false);
                ulong fh = Hash(fast.Frame.AsSpan(0, fast.FrameWidth * fast.FrameHeight));
                if (firstDiff < 0 && fh != snesHashes[f]) firstDiff = f;
                if (f > frames - 900 && f % 30 == 0)
                {
                    lastHashes.Add(fh);
                    maxColours = Math.Max(maxColours, fast.Frame.AsSpan(0, fast.FrameWidth * fast.FrameHeight).ToArray().Distinct().Take(8).Count());
                }
            }
            return (firstDiff, maxColours >= 8 && lastHashes.Count >= 3);   // over its last 900 frames the game drew pictures of 8+ colours, and they changed
        }
        var (fastFirst, alive) = FastRun(0);
        var (lateFirst, lateAlive) = FastRun(g.Moments[0]);
        return new GameResult(g, null, frames, look.Describe(), offered, centreFrames, centreFirst, wp.EnhCompareDiffer, wideFlags, hw == hs, offFrames, offDiffer,
            np.EnhCompareDiffer, np.EnhCompareDiffer - np.EnhCompareDifferOverflow, nfOverflowRows, nfFlags, hn == hs,
            bsPic, hb != hs, bsFlags, silent, fastFirst, alive, lateFirst, lateAlive, sw.Elapsed.TotalSeconds);
    }

    private static void ReportGame(GameResult r)
    {
        string name = $"{r.Game.Title}";
        if (r.Refused != null) { Check(false, name, r.Refused); return; }
        Console.WriteLine($"   {name}: {r.Frames:N0} frames, {r.ProfileNote}{(r.WideOffered ? "" : " (widescreen unavailable: checked on every layer extended)")}, {r.Seconds:F0} s");
        Check(r.WideCentreFrames == 0 && r.WideLineDiffer == 0 && r.WideFlagFrames == 0 && r.WideSound,
            $"{r.Game.Key}: widescreen: every frame's centre 256 columns are the SNES console's picture, STAT77 and the sound identical",
            r.WideCentreFrames == 0 ? $"{r.Frames - r.OffFrames:N0} frames, 0 lines differ" : $"{r.WideCentreFrames} frames differ from frame {r.WideCentreFirst}; {r.WideLineDiffer} lines; flags {r.WideFlagFrames}; sound {(r.WideSound ? "same" : "DIFFERS")}");
        Check(r.OffFrames > 0 && r.OffDiffer == 0, $"{r.Game.Key}: widescreen switched off mid-game: frame for frame the SNES console's picture", $"{r.OffFrames} frames, {r.OffDiffer} differ");
        Check(r.NfDifferOutsideOverflow == 0 && r.NfFlagFrames == 0 && r.NfSound,
            $"{r.Game.Key}: no flicker: only lines over the SNES's sprite limits change; STAT77 and the sound identical",
            $"{r.NfLines:N0} lines show more sprites, of {r.NfOverflowRows:N0} over the limits; {r.NfDifferOutsideOverflow} elsewhere");
        Check(r.BsPictureDiffer == 0 && r.BsFlagFrames == 0 && (r.BsSoundDiffers || r.SnesSilent),
            $"{r.Game.Key}: better sound: the picture identical every frame, the sound {(r.SnesSilent ? "(the game was silent)" : "different")}",
            $"{r.BsPictureDiffer} frames differ");
        string Fast(int first, bool alive) => $"{(first < 0 ? "never departs from the SNES's picture" : $"departs from the SNES's picture at frame {first:N0}")}, {(alive ? "alive at the end (pictures of 8+ colours, changing)" : "NOT drawing a changing picture at the end")}";
        Console.WriteLine($"     fast CPU (risky by nature, reported, not gated): from power-on {Fast(r.FastFirstDiff, r.FastAlive)}; switched on at frame {r.Game.Moments[0]:N0} {Fast(r.LateFirstDiff, r.LateAlive)}");
    }

    // ------------------------------------------------------------------ E7

    private static void EnhancedSpeed(EnhanceGame g, string path)
    {
        byte[] rom = RomDetect.Unwrap(File.ReadAllBytes(path), path, out _);
        using var s = Session(ConsoleKind.WideMess, rom);
        if (s.SetEnhancements(widescreen: true, noFlicker: true) is { } why) { Check(false, $"{g.Key}: speed", why); return; }
        var pad = ParsePad(g.Script);
        int at = 0; PadButtons held = 0;
        int start = g.Moments[Math.Min(2, g.Moments.Length - 1)];
        PlayTo(s, pad, ref at, start, ref held);
        double fps = 0;
        var sw = new Stopwatch();
        for (int k = 0; k < 2; k++) { sw.Restart(); PlayTo(s, pad, ref at, at + 300, ref held); fps = Math.Max(fps, 300 / sw.Elapsed.TotalSeconds); }
        var ppu = s.WideMessPpu!;
        for (int line = 1; line <= 224; line++) ppu.RenderLine(line);
        long before = GC.GetAllocatedBytesForCurrentThread();
        double picture = 0;
        for (int k = 0; k < 2; k++)
        {
            sw.Restart();
            for (int f = 0; f < 150; f++) for (int line = 1; line <= 224; line++) ppu.RenderLine(line);
            picture = Math.Max(picture, 150 / sw.Elapsed.TotalSeconds);
        }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(fps >= 120 && bytes == 0, $"{g.Key}: widescreen + no flicker runs at {fps:0} frames a second in Workshop (the picture alone {picture:0}), drawing allocates nothing", $"{bytes} bytes over 300 frames of lines; gate 120 fps");
    }
}
