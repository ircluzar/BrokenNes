using System.Text.Json.Nodes;
using BrokenNes.Workshop.Tas;

namespace BrokenNes.Mcp;

/// <summary>
/// TAS workline T1 (docs/projects/tas-format.md): run a TAS chapter / run file, and branch from a frame of one. Thin calls onto the Workshop
/// runner (Workshop/Tas): the same code as `Workshop --tas run|branch`, in this process, headless, deterministic, NES and N64 (others: T7).
/// </summary>
internal static partial class Tools
{
    private static void TasTools(Action<string, string, JsonObject, Func<Args, ToolOut>, bool> add)
    {
        add("tas_run",
            "Runs a TAS (docs/projects/tas-format.md): a run file (*.tasrun.json: chapters with start power-on | previous | state:FILE, script, goal, expect) or a bare script " +
            "(.tas/.txt in the N64-compat script language, or an exact per-frame .tlog). Writes checkpoints (video/audio/RAM hashes, blank check, PNGs, end states) to out " +
            "and returns the report: per chapter frames + goal, checkpoints, determinism (twice: run again in a fresh machine and compare), expect (the run file's expected checkpoints). " +
            "NES and N64. Heavy on N64 (minutes): prefer the fleet for long N64 runs.",
            Schema(("path", P("string", "the run file or script"), true),
                   ("rom", P("string", "the ROM (default: the run file's rom / BROKENNES_TAS_ROM)"), false),
                   ("console", P("string", "a bare script's console: nes, n64 (default: from the ROM)"), false),
                   ("start", P("string", "a bare script's start: power-on (default) or state:FILE"), false),
                   ("out", P("string", "the results folder (default: a new folder under BROKENNES_TAS_OUT or temp)"), false),
                   ("twice", P("boolean", "the determinism check (default true)"), false),
                   ("only", P("string", "stop after this chapter"), false),
                   ("bless", P("boolean", "write this run's checkpoints into the run file's expect blocks (default false)"), false),
                   ("pictures", P("boolean", "return the last checkpoint's picture (default true)"), false)),
            a => Wrap(() =>
            {
                var run = TasCli.LoadRun(a.Req("path"), a.Str("console") ?? "", a.Str("rom") ?? "", a.Str("start") ?? "power-on");
                var o = new TasRunOptions { Twice = a.Bool("twice", true), Only = a.Str("only"), OutDir = Path.GetFullPath(a.Str("out") ?? Path.Combine(Path.GetTempPath(), "brokennes-tas", run.Chapters[0].Id + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"))) };
                var rep = TasRunner.Run(run, o);
                if (a.Bool("bless", false)) TasRunner.Bless(run, rep);
                rep.Remove("log"); rep["out"] = o.OutDir;
                var r = new ToolOut(rep);
                if (a.Bool("pictures", true) && rep["checkpoints"]!.AsArray().LastOrDefault(c => c!["file"] != null)?["file"] is JsonNode f && File.Exists(Path.Combine(o.OutDir, (string)f!)))
                    r.Pngs.Add(File.ReadAllBytes(Path.Combine(o.OutDir, (string)f!)));
                return r;
            }), true);

        add("tas_branch",
            "Branches a TAS at a frame: runs the run file (or script) up to frame N of a chapter, saves the state there, and writes out/branch.tasrun.json, a run of one chapter " +
            "that starts from that state and plays either your new script (tail: script lines) or, without tail, the rest of the original chapter as an exact per-frame log. " +
            "Then tas_run the branch file to see where it leads. Returns the branch file, the state and the picture hash at the branch point.",
            Schema(("path", P("string", "the run file or script"), true),
                   ("chapter", P("string", "the chapter id (a bare script: its file name without extension)"), true),
                   ("frame", P("integer", "frames into the chapter"), true),
                   ("out", P("string", "the branch folder"), true),
                   ("tail", P("string[]", "script lines to play from the branch point (default: the rest of the original)"), false),
                   ("rom", P("string", "the ROM"), false), ("console", P("string", "a bare script's console"), false)),
            a => Wrap(() =>
            {
                var run = TasCli.LoadRun(a.Req("path"), a.Str("console") ?? "", a.Str("rom") ?? "", "power-on");
                var tail = a.Has("tail") ? a.List("tail").ToArray() : null;
                return new ToolOut(TasCli.Branch(run, a.Req("chapter"), (int)(a.Long("frame") ?? 0), a.Req("out"), tail, new TasRunOptions()));
            }), false);

        // ---- T2: precision (edit sessions with a savestate ring, lag frames, goals) and diffs
        add("tas_session",
            "TAS precision editing (docs/projects/tas-format.md, T2). action=open: opens a chapter of a run file (plays the earlier chapters) and returns a session id. " +
            "Then: step (run `frames` frames from the cursor; with `input` those frames' input becomes it, frames past the end are appended), seek (rewind or jump to `frame`: " +
            "nearest in-memory savestate + replay, fast), set / insert (input at `frame` for `frames` frames), delete (`frames` frames at `frame`), status (cursor, next inputs, " +
            "RAM-map variables, lag frames seen, picture), goals (play the whole chapter with its goals: first frame of each, lag frames, end hashes), save (write `out`/<id>.tlog + " +
            "`out`/edited.tasrun.json; `trim` cuts the chapter after that many frames), close. Input: buttons joined by + (NES: A B Select Start Up Down Left Right; " +
            "N64: A B Z START L R CU CD CL CR DU DD DL DR, stick as BTNS@x,y), - for none, pad 2 after |. Lag frame = the game polled no controller that frame.",
            Schema(("action", P("string", "open | step | seek | set | insert | delete | status | goals | save | close"), true),
                   ("session", P("string", "the session id (all actions but open)"), false),
                   ("path", P("string", "open: the run file or script"), false), ("chapter", P("string", "open: the chapter id"), false),
                   ("rom", P("string", "open: the ROM"), false), ("console", P("string", "open: a bare script's console"), false),
                   ("frame", P("integer", "seek/set/insert/delete: the frame (frames since the chapter start)"), false),
                   ("frames", P("integer", "step/set/insert/delete: how many frames (default 1)"), false),
                   ("input", P("string", "step/set/insert: the input"), false),
                   ("picture", P("boolean", "return the picture at the cursor (default true for step/seek/status)"), false),
                   ("out", P("string", "save: the folder"), false), ("trim", P("integer", "save: keep only this many frames"), false)),
            a => Wrap(() =>
            {
                string act = a.Req("action");
                if (act == "open")
                {
                    var run = TasCli.LoadRun(a.Req("path"), a.Str("console") ?? "", a.Str("rom") ?? "", "power-on");
                    var s0 = new TasSession(run, a.Str("chapter") ?? run.Chapters[0].Id, a.Str("rom"), new TasOpenOptions(), Path.Combine(Path.GetTempPath(), "brokennes-tas", "session_" + DateTime.Now.ToString("HHmmssfff")));
                    lock (TasSession.Open) TasSession.Open[s0.Id] = s0;
                    return Shot(s0, s0.Status(), a.Bool("picture", true));
                }
                TasSession? s;
                lock (TasSession.Open) if (!TasSession.Open.TryGetValue(a.Req("session"), out s)) throw new ArgumentException($"no TAS session {a.Str("session")} (open one)");
                int n = (int)(a.Long("frames") ?? 1), f = (int)(a.Long("frame") ?? s.Cursor);
                switch (act)
                {
                    case "step":
                    {
                        var per = s.Step(n, a.Str("input") is string inp ? s.Parse(inp) : null, perFrame: n <= 120);
                        var st = s.Status(); st["stepped"] = new JsonArray(per.Select(x => (JsonNode)x).ToArray());
                        return Shot(s, st, a.Bool("picture", true));
                    }
                    case "seek": s.Seek(f); return Shot(s, s.Status(), a.Bool("picture", true));
                    case "set": s.Set(f, n, s.Parse(a.Req("input")), append: true); return Shot(s, s.Status(), a.Bool("picture", false));
                    case "insert": s.Insert(f, n, s.Parse(a.Req("input"))); return Shot(s, s.Status(), a.Bool("picture", false));
                    case "delete": s.Delete(f, n); return Shot(s, s.Status(), a.Bool("picture", false));
                    case "status": return Shot(s, s.Status(), a.Bool("picture", true));
                    case "goals": return Shot(s, s.Evaluate(), a.Bool("picture", false));
                    case "save": return new ToolOut(new JsonObject { ["run"] = s.Save(a.Req("out"), (int)(a.Long("trim") ?? -1)) });
                    case "close": lock (TasSession.Open) TasSession.Open.Remove(s.Id); s.Dispose(); return new ToolOut(new JsonObject { ["closed"] = s.Id });
                }
                throw new ArgumentException($"action {act}: open | step | seek | set | insert | delete | status | goals | save | close");
            }), false);

        // ---- T3: input search (Workshop/Tas/TasSearch.cs)
        add("tas_search",
            "Automatic input search for the frame-exact spots of a TAS chapter (docs/projects/tas-format.md, T3). spec (a SEARCH.json path or the object itself): " +
            "{chapter, score: goal index (default the last reach/within goal), maximize: expression (tie-break), beam K, rounds, budget (candidates per parent), seed, slack, tail, " +
            "generators: [{kind: shift|hold|delete|insert|set|press|random, from, to, range, step, len: [lo,hi], buttons: [..], count, edits, ops}]}. " +
            "Each candidate = its parent's edits + one generated edit set; scored by the chapter's goals (first frame of the score goal, all goals met; lower wins), " +
            "beam: the best K distinct ones expand next round. The best is written back as a chapter (trimmed at the goal + the base's tail, played twice, blessed): out/best/edited.tasrun.json. " +
            "Runs here, in this process (small budgets; NES ~5 ms/frame). For big searches use the fleet: the result's fleetCommand (tools/tas/fleetsearch.py).",
            Schema(("path", P("string", "the run file or script"), true), ("spec", P("object", "the search spec, or a string path to SEARCH.json"), true),
                   ("chapter", P("string", "the chapter (default: spec.chapter / the first)"), false), ("rom", P("string", "the ROM"), false),
                   ("console", P("string", "a bare script's console"), false), ("out", P("string", "the results folder"), false)),
            a => Wrap(() =>
            {
                string outDir = Path.GetFullPath(a.Str("out") ?? Path.Combine(Path.GetTempPath(), "brokennes-tas", "search_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")));
                Directory.CreateDirectory(outDir);
                string specPath = a.Str("spec") is { } sp && File.Exists(sp) ? sp : Path.Combine(outDir, "spec.json");
                if (specPath == Path.Combine(outDir, "spec.json")) File.WriteAllText(specPath, a.RawJson("spec") ?? throw new ToolError("spec"));
                var argv = new List<string> { "--tas", "search", a.Req("path"), "--spec", specPath, "--out", outDir, "--local" };
                foreach (var k in new[] { "chapter", "rom", "console" }) if (a.Str(k) is { } v) { argv.Add("--" + k); argv.Add(v); }
                int rc = TasSearch.Run(argv.ToArray());
                var bj = Path.Combine(outDir, "best.json");
                var o = File.Exists(bj) ? JsonNode.Parse(File.ReadAllText(bj))!.AsObject() : new JsonObject { ["error"] = "no candidate met the goals" };
                var merged = Directory.GetFiles(outDir, "merged_r*.json").OrderBy(x => x).LastOrDefault();
                if (merged != null) o["top"] = JsonNode.Parse(File.ReadAllText(merged))!["top"]!.DeepClone();
                o["rc"] = rc; o["out"] = outDir;
                o["fleetCommand"] = $"python -I tools/tas/fleetsearch.py --run \"{a.Req("path")}\" --chapter <ID> --spec \"{specPath}\" --rom <ROM> --name <NAME> --shards 20";
                return new ToolOut(o);
            }), false);

        add("tas_diff",
            "Why did this frame differ: plays two TAS runs (run files or scripts) in lockstep and returns the first frame whose picture differs and the first whose RAM differs, " +
            "the differing RAM bytes and RAM-map variables there, the picture's differing box (a.png, b.png, diff.png in out), the first input difference and the inputs just before. " +
            "NES RAM = the 2 KB; N64 = RDRAM (slow per frame: use ram_every).",
            Schema(("a", P("string", "run A"), true), ("b", P("string", "run B"), true),
                   ("rom", P("string", "the ROM (A, and B unless romb)"), false), ("romb", P("string", "B's ROM"), false), ("console", P("string", "bare scripts' console"), false),
                   ("out", P("string", "the folder for diff.json + PNGs"), false), ("ram_every", P("integer", "compare RAM every N frames (default 1)"), false),
                   ("max", P("integer", "stop after this many frames"), false)),
            a => Wrap(() =>
            {
                var ra = TasCli.LoadRun(a.Req("a"), a.Str("console") ?? "", a.Str("rom") ?? "", "power-on");
                var rb = TasCli.LoadRun(a.Req("b"), a.Str("console") ?? "", a.Str("romb") ?? a.Str("rom") ?? "", "power-on");
                string outDir = Path.GetFullPath(a.Str("out") ?? Path.Combine(Path.GetTempPath(), "brokennes-tas", "diff_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")));
                var res = TasDiff.Run(ra, rb, a.Str("rom"), a.Str("romb") ?? a.Str("rom"), outDir, (int)(a.Long("ram_every") ?? 1), (int)(a.Long("max") ?? int.MaxValue));
                var r = new ToolOut(res);
                if (File.Exists(Path.Combine(outDir, "diff.png"))) r.Pngs.Add(File.ReadAllBytes(Path.Combine(outDir, "diff.png")));
                return r;
            }), false);
    }

    private static ToolOut Shot(TasSession s, JsonObject o, bool pic)
    {
        var r = new ToolOut(o);
        if (pic) r.Pngs.Add(TasPng.Encode(s.T.Argb, s.T.Width, s.T.Height));
        return r;
    }
}
