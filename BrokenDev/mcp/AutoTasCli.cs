using System.Text.Json.Nodes;
using BrokenNes.Workshop.Movies;
using NesEmulator.Debugging.Movies;

namespace BrokenNes.Mcp;

/// <summary>
/// AutoTAS packages from the command line (docs/projects/autotas-plan.md):
/// <code>
/// BrokenNes.Mcp.exe --autotas stitch --package DIR            seg\*.bnm in chapter order -> full.bnm, chapter frames + keyframes in autotas.json
/// BrokenNes.Mcp.exe --autotas verify --package DIR --rom FILE replays full.bnm from power-on: every keyframe, every chapter end (writes verified_at)
/// BrokenNes.Mcp.exe --autotas info   --package DIR
/// BrokenNes.Mcp.exe --autotas-test [--out DIR]                the tools' test on the movie tests' own game (no commercial ROM)
/// </code>
/// One JSON object on stdout; exit 0 when ok.
/// </summary>
internal static class AutoTasCli
{
    public static int Run(string[] args)
    {
        string? Opt(string n) { int i = Array.IndexOf(args, "--" + n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        int ai = Array.IndexOf(args, "--autotas");
        string act = ai >= 0 && ai + 1 < args.Length ? args[ai + 1] : "";
        try
        {
            var pkg = AutoTasPackage.Load(Opt("package") ?? throw new ArgumentException("--package DIR"));
            JsonObject j;
            switch (act)
            {
                case "stitch":
                {
                    var full = pkg.StitchAndSave(); pkg.Save();
                    j = new JsonObject { ["full"] = pkg.FullPath, ["frames"] = full.Frames, ["chapters"] = pkg.Manifest.Chapters.Count, ["ok"] = true };
                    break;
                }
                case "verify":
                    j = Verify(pkg, Opt("rom") ?? throw new ArgumentException("--rom FILE"), Opt("cores"));
                    pkg.Save();
                    break;
                case "info":
                    j = JsonNode.Parse(pkg.Manifest.ToJson())!.AsObject(); j["quality_avg"] = pkg.Manifest.QualityAvg(); j["ok"] = true;
                    break;
                default: throw new ArgumentException("--autotas stitch | verify | info");
            }
            Console.WriteLine(j.ToJsonString());
            return (bool?)j["ok"] == true ? 0 : 1;
        }
        catch (Exception e) when (e is ArgumentException or IOException or InvalidDataException or FormatException or ToolError)
        {
            Console.WriteLine(new JsonObject { ["ok"] = false, ["error"] = e.Message }.ToJsonString());
            return 2;
        }
    }

    /// <summary>Replays full.bnm from its start (power-on) and checks the movie's keyframes and every chapter's end hash.</summary>
    public static JsonObject Verify(AutoTasPackage pkg, string rom, string? cores)
    {
        var m = Movie.Load(pkg.FullPath);
        var ends = pkg.Manifest.Chapters.ToDictionary(c => c.EndFrame, c => c);
        var results = new JsonArray();
        int chOk = 0;
        using var mm = MovieMachine.OpenFor(m, rom, true, new List<string>(), cores);
        var r = MoviePlayer.Play(m, mm, -1, f =>
        {
            if (!ends.TryGetValue(f, out var c)) return;
            string pic = mm.Picture().ToString("X16"), ram = mm.Ram().ToString("X16");
            bool ok = pic.Equals(c.KeyPicture, StringComparison.OrdinalIgnoreCase) && ram.Equals(c.KeyRam, StringComparison.OrdinalIgnoreCase);
            if (ok) chOk++;
            results.Add(new JsonObject { ["chapter"] = c.Id, ["frame"] = f, ["ok"] = ok, ["picture"] = pic, ["ram"] = ram });
        });
        bool all = r.InSync && chOk == pkg.Manifest.Chapters.Count;
        pkg.Manifest.VerifiedAt = all ? DateTime.UtcNow.ToString("s") + "Z" : pkg.Manifest.VerifiedAt;
        pkg.Manifest.VerifyResult = all ? "ok" : $"failed: {chOk}/{pkg.Manifest.Chapters.Count} chapters, keyframes {r.KeysMatched}/{r.KeysChecked}" + (r.OffCourseFrame != null ? $", off course after {r.OffCourseFrame} ({r.OffCourseWhat})" : "");
        return new JsonObject
        {
            ["ok"] = all, ["framesRun"] = r.FramesRun, ["keyframes"] = $"{r.KeysMatched}/{r.KeysChecked}", ["chaptersOk"] = $"{chOk}/{pkg.Manifest.Chapters.Count}",
            ["offCourse"] = r.OffCourseFrame, ["offCourseWhat"] = r.OffCourseFrame != null ? r.OffCourseWhat : null, ["sameRom"] = r.SameRom, ["chapters"] = results,
            ["verified_at"] = pkg.Manifest.VerifiedAt,
        };
    }

    // ================================================================================================ the test

    /// <summary>
    /// Through the MCP tool handlers, on the movie tests' game: chapter "boot" from power-on (Start, milestone [app_state]==1, record mark with
    /// state_path), chapter "score" in a NEW session from that state file (hold A, milestone [score]>=40 from a milestones.json), the package
    /// written, stitched, verified from power-on; then a broken package (a wrong chapter hash) must fail the verify.
    /// </summary>
    public static int Test(string[] args)
    {
        int oi = Array.IndexOf(args, "--out");
        string outDir = Path.GetFullPath(oi >= 0 && oi + 1 < args.Length ? args[oi + 1] : Path.Combine(Path.GetTempPath(), "bn-autotas-test"));
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        Directory.CreateDirectory(outDir);
        var (romBytes, mlb) = MovieTestRoms.PlantedGame(new MovieTestRoms.Settings());
        string rom = Path.Combine(outDir, "autotas_test.nes"), sym = Path.Combine(outDir, "autotas_test.mlb");
        File.WriteAllBytes(rom, romBytes); File.WriteAllText(sym, mlb);
        var tools = Tools.All().ToDictionary(t => t.Name);
        ToolOut Call(string name, object a) => tools[name].Run(Args.From(a));
        var fails = new List<string>();
        void Check(bool c, string what) { if (!c) fails.Add(what); }
        var pkg = new AutoTasPackage(AutoTasPackage.VersionDir(Path.Combine(outDir, "tas"), "nes", "autotas_test", 1),
            new AutoTasManifest { GameId = "autotas_test", Title = "AutoTAS test game", Status = "building" });
        pkg.Save();
        File.WriteAllText(Path.Combine(pkg.Dir, "milestones.json"), "{\"milestones\":[{\"id\":\"score\",\"until\":\"[$13] >= 40\"}]}");
        var row = new JsonObject();
        try
        {
            // chapter 0: boot, from power-on
            var open = Call("open_rom", new { path = rom, symbols = new[] { sym } });
            string id = (string)open.Json["session"]!;
            Call("record", new { session = id, action = "start", text = "boot" });
            Call("play_step", new { session = id, frames = 30, scale = 0 });
            var s0 = Call("play_step", new { session = id, buttons = new[] { "Start" }, frames = 60, milestones = new[] { "boot=[$11]==1" }, scale = 0 });
            Check((string?)s0.Json["stoppedBy"] == "milestone" && (string?)s0.Json["milestone"]?["id"] == "boot", "boot milestone not detected: " + s0.Json.ToJsonString());
            var mk = Call("record", new { session = id, action = "mark", text = "milestone boot", state_path = pkg.RawStatePath("boot") });
            Check(File.Exists(pkg.RawStatePath("boot")), "record mark state_path wrote no file");
            Call("record", new { session = id, action = "stop", path = pkg.SegPath("boot") });
            Call("close_session", new { session = id });
            var seg0 = Movie.Load(pkg.SegPath("boot"));
            Check(seg0.Start == MovieStart.PowerOn, "segment boot is not from power-on");
            pkg.PutChapter(new AutoTasChapter { Id = "boot", Title = "Boot", Kind = "area", RawState = "state/boot.raw.state", Milestone = "boot" }, seg0);
            row["boot"] = new JsonObject { ["frames"] = seg0.Frames, ["stateFile"] = mk.Json["stateFile"]?["bytes"]?.DeepClone() };

            // chapter 1: score, in a new session from the state file
            var open2 = Call("open_rom", new { path = rom, symbols = new[] { sym } });
            string id2 = (string)open2.Json["session"]!;
            var ld = Call("load_state", new { session = id2, path = pkg.RawStatePath("boot") });
            Check(ld.Json["warnings"] == null, "load_state warnings: " + ld.Json["warnings"]?.ToJsonString());
            Call("record", new { session = id2, action = "start", text = "score" });
            var s1 = Call("play_step", new { session = id2, buttons = new[] { "A" }, frames = 200, milestone_file = Path.Combine(pkg.Dir, "milestones.json"), scale = 0, ram = new[] { "score=[$13]" } });
            Check((string?)s1.Json["milestone"]?["id"] == "score" && (long?)s1.Json["ram"]?["score"] >= 40, "score milestone not detected: " + s1.Json.ToJsonString());
            var none = Call("play_step", new { session = id2, frames = 5, milestones = new[] { "never=[$11]==7" }, scale = 0 });
            Check((string?)none.Json["stoppedBy"] == "frames" && (long?)none.Json["framesRun"] == 5, "a milestone that never hits must run the frames: " + none.Json.ToJsonString());
            Call("record", new { session = id2, action = "mark", text = "milestone score", state_path = pkg.RawStatePath("score") });
            Call("record", new { session = id2, action = "stop", path = pkg.SegPath("score") });
            Call("close_session", new { session = id2 });
            var seg1 = Movie.Load(pkg.SegPath("score"));
            Check(seg1.Start == MovieStart.State, "segment score does not start from the state");
            pkg.PutChapter(new AutoTasChapter { Id = "score", Title = "Score 40", Kind = "level", RawState = "state/score.raw.state", Milestone = "score",
                Quality = new AutoTasQuality { Score = "A", Frames = seg1.Frames } }, seg1);
            pkg.Manifest.Core = new AutoTasCore { Cores = seg0.Cores, Build = seg0.Emulator, StateFormat = AutoTasState.Load(pkg.RawStatePath("boot")).Format };
            pkg.Save();

            // stitch + verify from power-on
            var full = pkg.StitchAndSave(); pkg.Save();
            Check(full.Frames == seg0.Frames + seg1.Frames, "stitched length");
            Check(full.Marks.Any(m => m.Text.StartsWith("chapter score") && m.Frame == seg0.Frames), "chapter mark missing");
            var re = AutoTasPackage.Load(pkg.Dir);
            Check(re.Manifest.Chapters.Count == 2 && re.Manifest.QualityAvg() == 3.0, "manifest round trip");
            var v = Verify(re, rom, null); re.Save();
            row["verify"] = v;
            Check((bool?)v["ok"] == true, "verify failed");
            Check(AutoTasPackage.Load(pkg.Dir).Manifest.VerifiedAt != null, "verified_at not written");

            // a wrong chapter hash must fail
            re.Manifest.Chapters[1].KeyRam = "0000000000000000";
            var bad = Verify(re, rom, null);
            Check((bool?)bad["ok"] == false, "a wrong chapter hash passed the verify");
            // the stitcher refuses a power-on segment after the first
            try { AutoTasPackage.Stitch(new[] { seg0, seg0 }, new[] { "a", "b" }); fails.Add("stitch took a power-on segment second"); } catch (InvalidDataException) { }
            // inline milestone parsing keeps comparisons
            Check(AutoTasMilestone.ParseInline("w=[$0727]==0").Until == "[$0727]==0" && AutoTasMilestone.ParseInline("[$10]>=3").Until == "[$10]>=3", "inline milestone parse");
        }
        catch (Exception e) { fails.Add(e.GetType().Name + ": " + e.Message); }
        row["ok"] = fails.Count == 0;
        row["failures"] = J.Strs(fails);
        row["package"] = pkg.Dir;
        Console.WriteLine(row.ToJsonString());
        return fails.Count == 0 ? 0 : 1;
    }
}
