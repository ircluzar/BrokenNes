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
        Console.WriteLine("E2b. The drawn side rule on a synthetic game that streams its map as it scrolls");
        SyntheticDrawn();
        Console.WriteLine("E2c. The map memory on a synthetic game whose map holds only the screen's width");
        SyntheticMemory();
        Console.WriteLine("E2d. The level in work RAM on a synthetic game that keeps its level as blocks");
        SyntheticLevel();
        Console.WriteLine("E2f. The level in work RAM in the byte format, the room's columns and a level per layer (Chrono Trigger's layout) on a synthetic game");
        SyntheticLevelBytes();
        Console.WriteLine("E2e. Side truth compares what a layer draws (palette entries): a synthetic game whose palette fades while it scrolls");
        SyntheticSideTruth();

        Console.WriteLine("E3. Better sound: cubic interpolation on the S-DSP (a synthetic voice)");
        SyntheticSound();

        Console.WriteLine("E4. Fast CPU from the console: the speed ROM, never unlocked (its SNES image on the WideMess console)");
        FastCpuSpeedRom(dir);
        Console.WriteLine("E4b. Fast CPU's sound-port guard: a synthetic handshake that needs SNES time between the SPC700's echo and the next write");
        SoundGuardHandshake();

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

        Console.WriteLine("E8. Side truth: the drawn rule's side columns against what the SNES picture shows when the place scrolls into it (every layer extended: the negative control)");
        var truthGames = games.Where(x => x.Key is "dkc" or "sm").ToArray();
        var truth = new ((long right, long empty, long wrong) drawn, (long right, long empty, long wrong) ext)[truthGames.Length];
        Parallel.For(0, truthGames.Length, i =>
        {
            var common = new[] { "--game", truthGames[i].Key, "--layer", "bg1", "--snes-dir", snesDir }.Concat(profiles != "" ? new[] { "--profiles", profiles } : Array.Empty<string>()).ToArray();

            truth[i].drawn = SideTruthCounts(common);
            truth[i].ext = SideTruthCounts(common.Append("--all-extend").ToArray());
        });
        for (int i = 0; i < truthGames.Length; i++)
        {
            var (d, x) = truth[i];
            long dj = d.right + d.empty + d.wrong, xj = x.right + x.empty + x.wrong;
            Check(dj >= 100 && d.wrong * 100 <= dj, $"{truthGames[i].Key}: BG1 on its profile's drawn rule: at most 1% of the side columns the SNES picture later shows are wrong",
                $"{d.wrong:N0} wrong of {dj:N0} ({d.right:N0} right, {d.empty:N0} left to the layers behind)");
            Check(xj >= 100 && x.wrong * 5 >= xj, $"{truthGames[i].Key}: negative control, BG1 extended: at least 20% wrong (the old tiles the drawn rule leaves out)", $"{x.wrong:N0} wrong of {xj:N0}");
        }
        // the level in work RAM (milestone 6 climb): Super Metroid's profile names where its rooms live; without it the sides ahead are empty
        var smg = games.FirstOrDefault(x => x.Key == "sm");
        if (smg != null && WmsProfiles.All.FirstOrDefault(p => p.Title == smg.Title)?.Widescreen?.Level != null)
        {
            var common = new[] { "--game", "sm", "--layer", "bg1", "--snes-dir", snesDir }.Concat(profiles != "" ? new[] { "--profiles", profiles } : Array.Empty<string>()).ToArray();
            (long right, long empty, long wrong) lv = default, nl = default;
            Parallel.Invoke(() => lv = SideTruthCounts(common), () => nl = SideTruthCounts(common.Append("--no-level").ToArray()));
            long lj = lv.right + lv.empty + lv.wrong, nj = nl.right + nl.empty + nl.wrong;
            Check(lj >= 1000 && lv.wrong * 100 <= lj && lv.empty * 20 <= lj, "sm: BG1 with its level in work RAM: at most 1% of the side columns wrong and at most 5% empty (the side ahead drawn from the room before the game draws it)",
                $"{lv.right:N0} right, {lv.empty:N0} empty, {lv.wrong:N0} wrong of {lj:N0}");
            Check(nj >= 1000 && nl.empty * 2 >= nj, "sm: negative control, the same profile without its level: at least half the side columns empty", $"{nl.empty:N0} empty of {nj:N0}");
        }
        // the byte-format level (milestone 6 climb, part 2): Chrono Trigger's field scenes, its BG1 from work RAM; without the levels the sides are empty, every layer extended they are wrong
        var ctg = games.FirstOrDefault(x => x.Key == "ct");
        if (ctg != null && WmsProfiles.All.FirstOrDefault(p => p.Title == ctg.Title)?.Widescreen?.Levels.Length > 0)
        {
            var common = new[] { "--game", "ct", "--layer", "bg1", "--snes-dir", snesDir, "--frames", "8000", "--judge", CtFieldScenes }
                .Concat(profiles != "" ? new[] { "--profiles", profiles } : Array.Empty<string>()).ToArray();
            (long right, long empty, long wrong) lv = default, nl = default, ex = default;
            Parallel.Invoke(() => lv = SideTruthCounts(common), () => nl = SideTruthCounts(common.Append("--no-level").ToArray()), () => ex = SideTruthCounts(common.Append("--all-extend").ToArray()));
            long lj = lv.right + lv.empty + lv.wrong, nj = nl.right + nl.empty + nl.wrong, xj = ex.right + ex.empty + ex.wrong;
            Check(lj >= 300 && lv.wrong * 100 <= lj && lv.empty * 20 <= lj, "ct: BG1 with its byte-format level, on the field scenes: at most 1% of the side columns wrong and at most 5% empty",
                $"{lv.right:N0} right, {lv.empty:N0} empty, {lv.wrong:N0} wrong of {lj:N0}");
            Check(nj >= 300 && nl.empty * 2 >= nj, "ct: negative control, the same profile without its levels: at least half the side columns empty", $"{nl.empty:N0} empty of {nj:N0}");
            Check(xj >= 300 && ex.wrong * 5 >= xj, "ct: negative control, every layer extended: at least 20% wrong (the unrelated tiles the game keeps beside its rooms)", $"{ex.wrong:N0} wrong of {xj:N0}");
        }
        // the palette fix (spec 6.3): Super Metroid's BG2 (extended) fades a room's spores' palette between a side showing a place and the SNES picture showing it
        if (smg != null)
        {
            var common = new[] { "--game", "sm", "--layer", "bg2", "--snes-dir", snesDir }.Concat(profiles != "" ? new[] { "--profiles", profiles } : Array.Empty<string>()).ToArray();
            (long right, long empty, long wrong) ix = default, co = default;
            Parallel.Invoke(() => ix = SideTruthCounts(common), () => co = SideTruthCounts(common.Append("--compare").Append("colour").ToArray()));
            long ij = ix.right + ix.empty + ix.wrong, cj = co.right + co.empty + co.wrong;
            Check(ij >= 1000 && ix.wrong == 0, "sm: BG2 extended, palette entries compared: 0 wrong (its palette animation is the same drawing)", $"{ix.right:N0} right, {ix.empty:N0} empty, {ix.wrong:N0} wrong of {ij:N0}");
            Check(cj == ij && co.wrong > 0, "sm: negative control, the same run compared by colour (--compare colour): the fading spores count as wrong", $"{co.wrong:N0} wrong of {cj:N0}");
        }

        Console.WriteLine("E7. Speed: the enhanced picture (widescreen, every line through the wide compositor and the profile's rules)");
        foreach (var key in new[] { "smw", "fzero", "smk", "sm" })   // sm: the level in work RAM (milestone 6 climb)
        {
            var g = games.FirstOrDefault(x => x.Key == key);
            if (g != null) EnhancedSpeed(g, Path.Combine(snesDir, g.File));
        }
    }

    // ------------------------------------------------------------------ E1

    /// <summary>Chrono Trigger's field scenes in its attract sequence (the frames E8 judges its side truth on: the rooms its levels in work RAM cover).</summary>
    internal const string CtFieldScenes = "2950-3300,3650-3900,4400-4500,4750-4900,5700-6450,6500-6650,7400-8000";
    private const string ByteLevelJson = "{ \"layer\": \"bg1\", \"format\": \"bytes\", \"blocks\": \"$7E4000\", \"stride\": 256, \"high\": \"$7E40C0\", \"highBit\": 0, \"width\": \"$7E0BCB\", \"height\": \"$7E0BCD\", \"tiles\": \"$7EB000\", \"columns\": [\"$7E1D1A\", \"$7E1D1B\"] }";
    private const string LevelJson = "\"level\": { \"layer\": \"bg1\", \"blocks\": \"$7F0002\", \"width\": \"$7E07A5\", \"height\": \"$7E07A7\", \"tiles\": \"$7EA000\", \"index\": \"$03FF\", \"flipX\": \"$0400\", \"flipY\": \"$0800\" }";

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
            var d = WmsProfile.Parse(good.Replace("\"bg1\": \"extend\"", "\"bg1\": \"drawn\"").Replace("\"masks\": [", "\"drawnAhead\": { \"bg1\": 8 }, \"masks\": ["), "drawn.json");
            Check(d.Widescreen!.Layers[0] == WmsSideRule.Drawn && d.Widescreen.DrawnAhead[0] == 8 && d.Widescreen.DrawnAhead[1] == 16, "a profile with a drawn layer reads (its ahead, the others' default 16)");
            var mm = WmsProfile.Parse(good.Replace("\"bg1\": \"extend\"", "\"bg1\": \"drawn\"").Replace("\"masks\": [", "\"memory\": [\"bg1\"], \"masks\": ["), "memory.json");
            Check(mm.Widescreen!.Memory[0] && !mm.Widescreen.Memory[1] && !d.Widescreen.Memory[0], "a profile with the map memory on its drawn layer reads (and without the key, no memory)");
            var lv = WmsProfile.Parse(good.Replace("\"bg1\": \"extend\"", "\"bg1\": \"drawn\"").Replace("\"masks\": [", LevelJson + ", \"masks\": ["), "level.json");
            Check(lv.Widescreen!.Level is { Layer: 0, Blocks: 0x10002, Width: 0x07A5, Height: 0x07A7, Tiles: 0xA000, Index: 0x3FF, FlipX: 0x400, FlipY: 0x800 } && d.Widescreen.Level == null,
                "a profile with the level in work RAM reads (its addresses as offsets into work RAM, its bits; without the key, no level)");
            var by = WmsProfile.Parse(good.Replace("\"bg1\": \"extend\"", "\"bg1\": \"drawn\"").Replace("\"bg2\": \"repeat\"", "\"bg2\": \"drawn\"")
                .Replace("\"masks\": [", "\"level\": [" + ByteLevelJson + ", " + ByteLevelJson.Replace("\"bg1\"", "\"bg2\"").Replace("$7E4000", "$7E4040").Replace("\"highBit\": 0", "\"highBit\": 1") + "], \"masks\": ["), "bytes.json");
            Check(by.Widescreen!.Levels.Length == 2 && by.Widescreen.Levels[0] is { Layer: 0, Bytes: true, Blocks: 0x4000, Stride: 256, High: 0x40C0, HighBit: 0, Width: 0x0BCB, Tiles: 0xB000, Left: 0x1D1A, Right: 0x1D1B }
                && by.Widescreen.Levels[1] is { Layer: 1, Blocks: 0x4040, HighBit: 1 } && lv.Widescreen.Level!.Left == -1 && !lv.Widescreen.Level.Bytes,
                "a profile with a list of byte levels and the room's columns reads (Chrono Trigger's layout; the word format without columns as before)");
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
            ("sprites on the drawn rule", "\"sprites\": \"fill\"", "\"sprites\": \"drawn\"", "extend or fill"),
            ("a drawn ahead for a layer not on the drawn rule", "\"masks\": [", "\"drawnAhead\": { \"bg2\": 8 }, \"masks\": [", "not drawn"),
            ("a drawn ahead past the side", "\"bg1\": \"extend\"", "\"bg1\": \"drawn\"", "0-72 pixels"),
            ("the map memory for a layer not on the drawn rule", "\"masks\": [", "\"memory\": [\"bg2\"], \"masks\": [", "not drawn"),
            ("the map memory for the sprites", "\"masks\": [", "\"memory\": [\"sprites\"], \"masks\": [", "not a layer"),
            ("the map memory not a list", "\"masks\": [", "\"memory\": \"bg1\", \"masks\": [", "a list of layers"),
            ("the level for a layer not on the drawn rule", "\"masks\": [", LevelJson.Replace("\"bg1\"", "\"bg2\"") + ", \"masks\": [", "not drawn"),
            ("the level outside work RAM", "\"masks\": [", LevelJson.Replace("$7F0002", "$800002") + ", \"masks\": [", "work RAM is"),
            ("the level's bits overlapping", "\"masks\": [", LevelJson.Replace("\"flipX\": \"$0400\"", "\"flipX\": \"$0200\"") + ", \"masks\": [", "overlap"),
            ("the level in bytes with flips", "\"masks\": [", "\"level\": " + ByteLevelJson.Replace("\"stride\": 256", "\"stride\": 256, \"flipX\": \"$0400\"") + ", \"masks\": [", "unknown key 'flipX'"),
            ("the level in bytes without its stride", "\"masks\": [", "\"level\": " + ByteLevelJson.Replace("\"stride\": 256, ", "") + ", \"masks\": [", "no 'stride'"),
            ("the level's bit 8 without its byte", "\"masks\": [", "\"level\": " + ByteLevelJson.Replace("\"high\": \"$7E40C0\", ", "") + ", \"masks\": [", "go together"),
            ("the level's bit 8 past bit 7", "\"masks\": [", "\"level\": " + ByteLevelJson.Replace("\"highBit\": 0", "\"highBit\": 8") + ", \"masks\": [", "0-7"),
            ("the level in an unknown format", "\"masks\": [", "\"level\": " + ByteLevelJson.Replace("\"bytes\"", "\"nibbles\"") + ", \"masks\": [", "words or bytes"),
            ("the level's columns not two addresses", "\"masks\": [", "\"level\": " + ByteLevelJson.Replace("[\"$7E1D1A\", \"$7E1D1B\"]", "[\"$7E1D1A\"]") + ", \"masks\": [", "two bytes"),
            ("the level listing a layer twice", "\"masks\": [", "\"level\": [" + ByteLevelJson + ", " + ByteLevelJson + "], \"masks\": [", "twice"),
            ("the level without its block table", "\"masks\": [", LevelJson.Replace(", \"tiles\": \"$7EA000\"", "") + ", \"masks\": [", "no 'tiles'"),
        };
        foreach (var (change, from, to, mustSay) in bad)
        {
            string json = good.Replace(from, to);
            if (change.StartsWith("recommending")) json = json.Replace("\"recommended\": [\"widescreen\"]", "\"recommended\": [\"fastCpu\"]");
            if (change.StartsWith("a drawn ahead past")) json = json.Replace("\"masks\": [", "\"drawnAhead\": { \"bg1\": 80 }, \"masks\": [");
            if (change.StartsWith("the level")) json = json.Replace("\"bg1\": \"extend\"", "\"bg1\": \"drawn\"");
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

    // ------------------------------------------------------------------ E2b

    /// <summary>
    /// The drawn rule on a synthetic streaming game, written straight into a picture chip: BG1 on a 64-tile map (512 pixels) full of red tiles (what an old level left in video RAM).
    /// The level is stripes 96 pixels wide, white and green (tile column t is white when (t / 12) is even), so a map column the game drew 512 pixels earlier holds other stripes than
    /// the place it is shown for. Each frame the "game" scrolls BG1 by <paramref name="step"/> pixels and, like Donkey Kong Country, draws the place the SNES picture shows plus 8 pixels
    /// ahead of the edge it scrolls towards, wrapping in the 512-pixel map. Returns the last frame and the scroll it showed.
    /// </summary>
    private static (uint[] wide, uint[] snes, int x) DrawnScene(PPU_WMS wms, PPU_SFC snes, WmsWidescreen? rules, int frames, int step, int jumpAt = -1)
    {
        foreach (var p in new PPU_SFC[] { wms, snes })
        {
            p.Reset();
            for (int w = 16; w < 24; w++) p.Vram[w] = 0x00FF;   // tile 1: colour 1 (white)
            for (int w = 32; w < 40; w++) p.Vram[w] = 0xFF00;   // tile 2: colour 2 (red)
            for (int w = 48; w < 56; w++) p.Vram[w] = 0xFFFF;   // tile 3: colour 3 (green)
            p.Cgram[0] = 0; p.Cgram[1] = 0x7FFF; p.Cgram[2] = 0x001F; p.Cgram[3] = 0x03E0;
            for (int a = 0x1000; a < 0x1800; a++) p.Vram[a] = 2;
            p.WriteRegister(0x05, 0x01);   // BGMODE 1
            p.WriteRegister(0x07, 0x11);   // BG1 map at word $1000, 64 tiles wide
            p.WriteRegister(0x0B, 0x00);
            p.WriteRegister(0x2C, 0x01);   // TM: BG1
            p.WriteRegister(0x00, 0x0F);
        }
        wms.SetEnhancements(rules, false);
        int x = 1024;
        void Draw(int from, int to)
        {
            for (int px = from & ~7; px < to; px += 8)
            {
                int t = px >> 3, col = t & 63;
                ushort tile = (ushort)(t / 12 % 2 == 0 ? 1 : 3);
                for (int row = 0; row < 32; row++) foreach (var p in new PPU_SFC[] { wms, snes }) p.Vram[0x1000 + (col & 31) + ((col & 32) != 0 ? 0x400 : 0) + row * 32] = tile;
            }
        }
        Draw(x, x + 256);
        uint[] wf = Array.Empty<uint>(), sf = Array.Empty<uint>();
        for (int f = 0; f < frames; f++)
        {
            x += f == jumpAt ? 200 : step;
            if (f == jumpAt) Draw(x, x + 256);                      // a new scene: the game draws the picture's columns
            else if (step > 0) Draw(x + 256 - step, x + 256 + 8);   // the columns coming in on the right, 8 pixels ahead
            else Draw(x - 8, x - step);                             // on the left
            foreach (var p in new PPU_SFC[] { wms, snes }) { p.WriteRegister(0x0D, (byte)(x & 0xFF)); p.WriteRegister(0x0D, (byte)((x >> 8) & 3)); }
            wf = DrawFrame(wms, out _); sf = DrawFrame(snes, out _);
        }
        return (wf, sf, x);
    }

    private static void SyntheticDrawn()
    {
        var wms = new PPU_WMS(); var snes = new PPU_SFC();
        bool Centre(uint[] w, uint[] s) => Enumerable.Range(0, 224).All(r => w.AsSpan(r * 400 + 72, 256).SequenceEqual(s.AsSpan(r * 256, 256)));
        // the colours as the chips draw them, from the SNES picture: white and green stripes (the level), black the backdrop
        (int wrong, int level, int backdrop) Judge(uint[] f, uint[] s, int x, int from, int to)
        {
            uint white = 0, green = 0;
            for (int c = 0; c < 256; c++) { int t = (x + c) >> 3; if (t / 12 % 2 == 0) white = s[c]; else green = s[c]; }
            int wrong = 0, level = 0, back = 0;
            for (int r = 0; r < 224; r++)
                for (int c = from; c < to; c++)
                {
                    int t = (x + c - 72) >> 3;
                    uint v = f[r * 400 + c], want = t / 12 % 2 == 0 ? white : green;
                    if (v == want) level++; else if (v == 0xFF000000u) back++; else wrong++;
                }
            return (wrong, level, back);
        }
        var (ext, es, ex) = DrawnScene(wms, snes, WmsWidescreen.AllExtend(), 150, 4);
        var er = Judge(ext, es, ex, 336, 400);
        Check(er.wrong > 0 && Centre(ext, es), "negative control, BG1 extended: scrolling right, the right side beyond the 8 pixels the game draws ahead shows old tiles (not the stripes of the place)",
            $"{er.wrong:N0} wrong pixels in the right side");
        var drawn = WmsWidescreen.AllExtend(); drawn.Layers[0] = WmsSideRule.Drawn; drawn.DrawnAhead[0] = 8;
        var (dr, ds, dx) = DrawnScene(wms, snes, drawn, 150, 4);
        var dl = Judge(dr, ds, dx, 0, 72); var da = Judge(dr, ds, dx, 328, 336); var db = Judge(dr, ds, dx, 336, 400);
        Check(dl.wrong + da.wrong + db.wrong == 0 && Centre(dr, ds), "BG1 drawn (ahead 8): no wrong pixel in either side, the centre the SNES chip's picture", $"wrong {dl.wrong + da.wrong + db.wrong}");
        Check(da.level == 224 * 8 && db.backdrop == 224 * 64 && dl.level == 224 * 72,
            "drawn: the 8 pixels ahead and the whole left side (drawn as the picture passed) show the level; beyond the 8, the backdrop",
            $"ahead {da.level:N0} level, beyond {db.backdrop:N0} backdrop, left {dl.level:N0} level");
        var (lf, ls, lx) = DrawnScene(wms, snes, drawn, 150, -4);
        var ll = Judge(lf, ls, lx, 0, 64); var la = Judge(lf, ls, lx, 64, 72); var lr = Judge(lf, ls, lx, 328, 400);
        Check(ll.wrong + la.wrong + lr.wrong == 0 && la.level == 224 * 8 && ll.backdrop == 224 * 64 && lr.level == 224 * 72 && Centre(lf, ls),
            "drawn, scrolling left: the same on the other side", $"left ahead {la.level:N0} level, beyond {ll.backdrop:N0} backdrop, right {lr.level:N0} level, wrong {ll.wrong + la.wrong + lr.wrong}");
        var (jf, js, jx) = DrawnScene(wms, snes, drawn, 150, 4, jumpAt: 149);
        var jl = Judge(jf, js, jx, 0, 72); var jr = Judge(jf, js, jx, 328, 400);
        var (xf, xs, xx) = DrawnScene(wms, snes, WmsWidescreen.AllExtend(), 150, 4, jumpAt: 149);
        var xr = Judge(xf, xs, xx, 328, 400);
        Check(jl.wrong + jr.wrong == 0 && jl.backdrop == 224 * 72 && jr.backdrop == 224 * 72 && xr.wrong > 0 && Centre(jf, js),
            "a cut (a jump of 200 pixels: a new scene): the drawn span starts again at the picture's own columns, both sides the backdrop until the game draws there; extended, the right side shows old tiles",
            $"drawn: left {jl.backdrop:N0} / right {jr.backdrop:N0} backdrop; extended: {xr.wrong:N0} wrong on the right");
        wms.SetEnhancements(null, false);
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

    private static long speedRomGuard = -1;

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
            if (fast) speedRomGuard = s.Board.SoundGuardMasterClocks;
            return new[] { Wd(0x02), Wd(0x06), w[0x101] | (s.WideMess!.Unlocked ? 0x100 : 0) };
        }
        var off = Measure(false); var on = Measure(true);
        double pa = PredictedCount(LoopA, Nmi), paf = PredictedCount(LoopAFast, NmiFast), pb = PredictedCount(LoopB, Nmi), pbf = PredictedCount(LoopBFast, NmiFast);
        Check(Near(off[0], pa, 0.005) && Near(off[1], pb, 0.005), "off: the locked game runs at SNES speed (loops A and B as spec 2 predicts)", $"A {off[0]} (predicted {pa:F0}), B {off[1]} (predicted {pb:F0})");
        Check(Near(on[0], paf, 0.005) && Near(on[1], pbf, 0.005), "on: the same locked game runs at fast mode's speed (2 clocks a cycle, the I/O register at 6)", $"A {on[0]} (predicted {paf:F0}), B {on[1]} (predicted {pbf:F0})");
        Check(on[2] == 0x42 && off[2] == 0x42, "the game never sees it: WMSPEED reads open bus ($42), the extension stays locked", $"WMSPEED read {on[2] & 0xFF:X2}{((on[2] & 0x100) != 0 ? ", UNLOCKED" : "")}");
        Check(speedRomGuard == 0, "the speed ROM never touches the sound ports, so the sound-port guard never runs: the loops above are fast mode's own", $"{speedRomGuard:N0} clocks in the guard");
    }

    // ------------------------------------------------------------------ E4b

    /// <summary>
    /// A LoROM test ROM (assembled here) with the handshake that hangs Contra III's loader at fast speed: the 65816 uploads a small SPC700 program through
    /// the IPL; the program says it runs ($5A on port 1), waits for $55 on port 0, echoes it and clears its input ports ($F1 bit 4) 7 SPC700 cycles later
    /// (about 145 master clocks, as Contra III's driver), then waits for $CC and echoes it. The 65816 writes $55, waits for the echo, writes ports 2 and 3, then $CC: about
    /// 150-230 master clocks after seeing the echo at SNES speed (after the clear: it arrives), 50-70 at fast speed (before it: wiped).
    /// Work RAM $0010: $01 the 65816 saw $CC come back, $EE it gave up.
    /// </summary>
    internal static byte[] HandshakeRom()
    {
        var s = new BrokenNes.Workshop.Debug.Asm(0x0200);
        s.B(0xE8, 0x5A, 0xC4, 0xF5)                                  // MOV A,#$5A; MOV $F5,A: running
         .L("l1").B(0xE4, 0xF4, 0x68, 0x55, 0xD0).R("l1")           // MOV A,$F4; CMP A,#$55; BNE l1
         .B(0xC4, 0xF4)                                              // MOV $F4,A: the echo
         .B(0x00)                                                    // NOP (the driver's set-up)
         .B(0x8F, 0x10, 0xF1)                                        // MOV $F1,#$10: clear the input ports 0 and 1
         .L("l2").B(0xE4, 0xF4, 0x68, 0xCC, 0xD0).R("l2")           // MOV A,$F4; CMP A,#$CC; BNE l2
         .B(0xC4, 0xF4)                                              // MOV $F4,A: $CC back
         .L("l3").B(0x2F).R("l3");                                   // BRA l3
        var spc = s.Build();
        var a = new BrokenNes.Workshop.Debug.Asm(0x8000);
        a.L("reset").B(0x78, 0x18, 0xFB, 0xE2, 0x30, 0xA9, 0x80, 0x8D, 0x00, 0x21)      // SEI CLC XCE SEP #$30; forced blank
         .B(0x9C, 0x10, 0x00)                                                           // STZ $0010
         .L("w1").B(0xAD, 0x40, 0x21, 0xC9, 0xAA, 0xD0).R("w1")
         .B(0xA9, 0x00, 0x8D, 0x42, 0x21, 0xA9, 0x02, 0x8D, 0x43, 0x21)                 // to $0200
         .B(0xA9, 0x01, 0x8D, 0x41, 0x21, 0xA9, 0xCC, 0x8D, 0x40, 0x21)
         .L("w2").B(0xAD, 0x40, 0x21, 0xC9, 0xCC, 0xD0).R("w2")
         .B(0xA2, 0x00)
         .L("send").B(0xBD).A("spc").B(0x8D, 0x41, 0x21, 0x8A, 0x8D, 0x40, 0x21)
         .L("w3").B(0xCD, 0x40, 0x21, 0xD0).R("w3")
         .B(0xE8, 0xE0, (byte)spc.Length, 0xD0).R("send")
         .B(0xA9, 0x00, 0x8D, 0x42, 0x21, 0xA9, 0x02, 0x8D, 0x43, 0x21)                 // entry $0200
         .B(0x9C, 0x41, 0x21, 0x8A, 0x1A, 0x8D, 0x40, 0x21)                             // run it
         .L("w4").B(0xAD, 0x41, 0x21, 0xC9, 0x5A, 0xD0).R("w4")                         // until it says it runs
         .B(0xA9, 0x55, 0x8D, 0x40, 0x21)                                               // LDA #$55 STA $2140
         .L("w5").B(0xCD, 0x40, 0x21, 0xD0).R("w5")                                     // CMP $2140 BNE w5: the echo
         .B(0xA9, 0x00, 0x8D, 0x42, 0x21, 0xA9, 0x00, 0x8D, 0x43, 0x21)                 // ports 2 and 3
         .B(0xA9, 0xCC, 0x8D, 0x40, 0x21)                                               // then $CC
         .B(0xC2, 0x10, 0xA2, 0x00, 0x00)                                               // REP #$10 LDX #0
         .L("w6").B(0xAD, 0x40, 0x21, 0xC9, 0xCC, 0xF0).R("ok")                         // LDA $2140 CMP #$CC BEQ ok
         .B(0xCA, 0xD0).R("w6")                                                         // DEX BNE w6
         .B(0xA9, 0xEE, 0x8D, 0x10, 0x00, 0x80).R("done")                               // gave up
         .L("ok").B(0xA9, 0x01, 0x8D, 0x10, 0x00)
         .L("done").B(0x80).R("done")
         .L("nmi").B(0x40)
         .L("spc").B(spc);
        var code = a.Build();
        var rom = new byte[0x8000];
        code.CopyTo(rom, 0);
        void Vec(int at, string l) { rom[at] = (byte)a[l]; rom[at + 1] = (byte)(a[l] >> 8); }
        Vec(0x7FEA, "nmi"); Vec(0x7FEE, "nmi"); Vec(0x7FFC, "reset"); Vec(0x7FFA, "nmi"); Vec(0x7FFE, "nmi");
        System.Text.Encoding.ASCII.GetBytes("WIDEMESS HANDSHAKE   ").CopyTo(rom, 0x7FC0);
        rom[0x7FD5] = 0x20; rom[0x7FD7] = 0x05; rom[0x7FD9] = 0x01;
        int sum = 0; foreach (byte b in rom) sum += b;
        rom[0x7FDE] = (byte)sum; rom[0x7FDF] = (byte)(sum >> 8); rom[0x7FDC] = (byte)~sum; rom[0x7FDD] = (byte)(~sum >> 8);
        return rom;
    }

    private static void SoundGuardHandshake()
    {
        byte[] rom = HandshakeRom();
        (byte result, long guard) Play(ConsoleKind console, bool fast, bool guardOff)
        {
            using var s = Session(console, rom);
            if (fast && s.SetEnhancements(fastCpu: true) is { } why) { Check(false, "the console's fast CPU switches on for the handshake ROM", why); return (0, 0); }
            s.Board.SoundGuardOffForTest = guardOff;
            for (int f = 0; f < 90 && s.Board.Wram[0x10] == 0; f++) Run(s, 1);
            return (s.Board.Wram[0x10], s.Board.SoundGuardMasterClocks + (s.Board.InSoundGuard ? 1 : 0));
        }
        string R(byte r) => r switch { 0x01 => "$CC came back", 0xEE => "the $CC was wiped: the 65816 gave up", _ => $"no answer (${r:X2})" };
        var snes = Play(ConsoleKind.Snes, false, false);
        Check(snes.result == 0x01, "the SNES console: the handshake completes (the 65816's $CC lands after the driver's port clear)", R(snes.result));
        var off = Play(ConsoleKind.WideMess, false, false);
        Check(off.result == 0x01 && off.guard == 0, "the WideMess console, fast CPU off: the same, and no guard", $"{R(off.result)}, guard {off.guard:N0} clocks");
        var on = Play(ConsoleKind.WideMess, true, false);
        Check(on.result == 0x01 && on.guard > 0, "fast CPU on: the sound-port guard keeps SNES time after each port access, so the handshake completes", $"{R(on.result)}, {on.guard:N0} master clocks at SNES time");
        var neg = Play(ConsoleKind.WideMess, true, true);
        Check(neg.result == 0xEE, "negative control: fast CPU without the guard, the $CC lands before the clear and is lost (Contra III's hang)", R(neg.result));
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
        int FastFirstDiff, bool FastAlive, int LateFirstDiff, bool LateAlive, double LateGuardShare, double Seconds);

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
        if (why != null) return new GameResult(g, why, 0, look.Describe(), offered, 0, -1, 0, 0, false, 0, 0, 0, 0, 0, 0, false, 0, false, 0, false, -1, false, -1, false, 0, 0);
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
        (int first, bool alive, double guard) FastRun(int from)
        {
            using var fast = Session(ConsoleKind.WideMess, rom);
            if (from == 0) fast.SetEnhancements(fastCpu: true);
            long onAt = 0;
            PadButtons hold = 0;
            int maxColours = 0, firstDiff = -1; var lastHashes = new HashSet<ulong>();
            for (int f = 1; f <= frames; f++)
            {
                if (f == from + 1 && from > 0) { fast.SetEnhancements(fastCpu: true); onAt = fast.Board.MasterClock; }
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
            double share = fast.Board.SoundGuardMasterClocks / (double)Math.Max(1, fast.Board.MasterClock - onAt);   // of the time fast CPU was on, the share at SNES time in sound-port guards
            return (firstDiff, maxColours >= 8 && lastHashes.Count >= 3, share);   // over its last 900 frames the game drew pictures of 8+ colours, and they changed
        }
        var (fastFirst, alive, _) = FastRun(0);
        var (lateFirst, lateAlive, lateGuard) = FastRun(g.Moments[0]);
        return new GameResult(g, null, frames, look.Describe(), offered, centreFrames, centreFirst, wp.EnhCompareDiffer, wideFlags, hw == hs, offFrames, offDiffer,
            np.EnhCompareDiffer, np.EnhCompareDiffer - np.EnhCompareDifferOverflow, nfOverflowRows, nfFlags, hn == hs,
            bsPic, hb != hs, bsFlags, silent, fastFirst, alive, lateFirst, lateAlive, lateGuard, sw.Elapsed.TotalSeconds);
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
        Console.WriteLine($"     fast CPU (risky by nature, reported, not gated): from power-on {Fast(r.FastFirstDiff, r.FastAlive)}; switched on at frame {r.Game.Moments[0]:N0} {Fast(r.LateFirstDiff, r.LateAlive)}, {r.LateGuardShare:P1} of its time at SNES speed in sound-port guards");
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
