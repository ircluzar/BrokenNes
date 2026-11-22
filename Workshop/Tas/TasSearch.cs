// TAS workline T3: input search (docs/projects/tas-format.md "Search").
//   --tas search RUN --spec SEARCH.json --out DIR [--chapter ID] [--local]              every round in this process, then the best is written back
//   --tas search RUN --spec SEARCH.json --out DIR --round R [--parents P.json] --shard I/N   one fleet shard: evaluates candidates k with k % N == I
//   --tas search-merge RUN --spec SEARCH.json --round R --in DIR [--in DIR].. --out DIR [--final]   top K -> DIR/parents_r{R+1}.json; --final writes the best chapter
//   --tas search-pack RUN --chapter ID --out PACK [--frame F] [--rom R]                  a portable pack: state at the chapter frame F + one-chapter run + ramdb/refs/ROM
// A candidate = its parent's edit list + one generated edit set ("set F:BTNS[:N]; insert F:BTNS[:N]; delete F[:N]", the --variants syntax).
// Score (lower is better): first frame of the score goal (all goals met), then -maximize (an expression at the stop frame), then lag frames.
// Generation is deterministic (spec seed + round + parent index), so every shard builds the same candidate list without talking to the others.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using BrokenNes.Workshop.Tas.Script;

namespace BrokenNes.Workshop.Tas;

internal static class TasSearch
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    internal sealed class Spec
    {
        public string Chapter = ""; public int Score = -1; public int Tail = -1; public string? Maximize;
        public int Beam = 4, Rounds = 1, Budget = 200, Seed = 1, Slack = 0;
        public JsonArray Gens = new();
        public static Spec Load(string path, string chapterOpt)
        {
            var j = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var s = new Spec
            {
                Chapter = chapterOpt.Length > 0 ? chapterOpt : (string?)j["chapter"] ?? "",
                Score = (int?)j["score"] ?? -1, Tail = (int?)j["tail"] ?? -1, Maximize = (string?)j["maximize"],
                Beam = (int?)j["beam"] ?? 4, Rounds = (int?)j["rounds"] ?? 1, Budget = (int?)j["budget"] ?? 200, Seed = (int?)j["seed"] ?? 1, Slack = (int?)j["slack"] ?? 0,
                Gens = j["generators"]?.AsArray() ?? throw new InvalidDataException($"{path}: no \"generators\""),
            };
            return s;
        }
    }

    sealed record Cand(int Parent, string Edits, int Frame);

    public static int Run(string[] args)
    {
        string Opt(string n, string d = "") { int i = Array.FindIndex(args, 2, a => a.Equals("--" + n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        bool Has(string n) => args.Skip(2).Any(a => a.Equals("--" + n, StringComparison.OrdinalIgnoreCase));
        List<string> All(string n) { var r = new List<string>(); for (int i = 2; i + 1 < args.Length; i++) if (args[i].Equals("--" + n, StringComparison.OrdinalIgnoreCase)) r.Add(args[i + 1]); return r; }
        string verb = args[1].ToLowerInvariant(), target = args[2];
        var run = TasCli.LoadRun(target, Opt("console"), Opt("rom"), Opt("start", "power-on"));
        string rom = Opt("rom");
        var open = new TasOpenOptions { Cores = Opt("cores", ""), N64Renderer = Opt("renderer", "") is { Length: > 0 } r ? r : null };
        string outDir = Path.GetFullPath(Opt("out", "tas_search")); Directory.CreateDirectory(outDir);
        if (verb == "search-pack") return Pack(run, Opt("chapter", run.Chapters[0].Id), int.Parse(Opt("frame", "0")), outDir, rom, open);
        var spec = Spec.Load(Opt("spec"), Opt("chapter"));
        if (spec.Chapter.Length == 0) spec.Chapter = run.Chapters[0].Id;
        if (verb == "search-merge")
        {
            int round = int.Parse(Opt("round", "0"));
            var rows = All("in").SelectMany(d => Directory.GetFiles(d, "results.json", SearchOption.AllDirectories)).SelectMany(f => JsonNode.Parse(File.ReadAllText(f))!["results"]!.AsArray().Select(x => x!.AsObject())).ToList();
            var parents = Opt("parents") is { Length: > 0 } pp ? LoadParents(pp) : new List<string> { "" };
            return Merge(run, spec, rows, parents, round, outDir, rom, open, Has("final"));
        }
        // search
        using var s = new TasSession(run, spec.Chapter, rom.Length > 0 ? rom : null, open, Path.Combine(outDir, "scratch"));
        if (Opt("shard") is { Length: > 0 } sh)
        {
            var p = sh.Split('/'); int si = int.Parse(p[0]), sn = int.Parse(p[1]);
            int round = int.Parse(Opt("round", "0"));
            var parents = Opt("parents") is { Length: > 0 } pp ? LoadParents(pp) : new List<string> { "" };
            var rows = EvalRound(s, spec, parents, round, si, sn, int.Parse(Opt("cutoff", int.MaxValue.ToString())));
            File.WriteAllText(Path.Combine(outDir, "results.json"), new JsonObject { ["round"] = round, ["shard"] = sh, ["results"] = new JsonArray(rows.Select(x => (JsonNode)x).ToArray()) }.ToJsonString(Indented));
            Console.WriteLine($"TAS search shard {sh} round {round}: {rows.Count} candidates, best {Best(rows)} -> {Path.Combine(outDir, "results.json")}");
            return 0;
        }
        // local: every round here
        var par = new List<string> { "" }; var allRows = new List<JsonObject>();
        for (int round = 0; round < spec.Rounds; round++)
        {
            var rows = EvalRound(s, spec, par, round, 0, 1, int.MaxValue);
            allRows.AddRange(rows);
            par = TopK(allRows, spec.Beam).Select(x => (string)x["edits"]!).ToList();
            Console.WriteLine($"TAS search round {round}: {rows.Count} candidates, best {Best(allRows)}");
        }
        s.Dispose();
        return Merge(run, spec, allRows, new List<string> { "" }, spec.Rounds - 1, outDir, rom, open, true);
    }

    static List<string> LoadParents(string p) => JsonNode.Parse(File.ReadAllText(p))!["parents"]!.AsArray().Select(x => (string)x!).ToList();

    static string Best(List<JsonObject> rows) { var b = TopK(rows, 1).FirstOrDefault(); return b == null ? "none ok" : $"score {b["score"]} lag {b["lag"]} ({b["edits"]})"; }

    static long? N(JsonNode? n) => n == null ? null : long.Parse(n.ToJsonString());
    static (long, long, long) Key(JsonObject r) => (N(r["score"]) ?? long.MaxValue, -(N(r["value"]) ?? 0), N(r["lag"]) ?? 0);

    /// <summary>The best K ok rows, one per distinct end state (two edit lists that end in the same machine are the same candidate).</summary>
    static List<JsonObject> TopK(IEnumerable<JsonObject> rows, int k)
    {
        var seen = new HashSet<string>(); var r = new List<JsonObject>();
        foreach (var x in rows.Where(x => (bool)x["ok"]!).OrderBy(Key).ThenBy(x => ((string)x["edits"]!).Length).ThenBy(x => (string)x["edits"]!, StringComparer.Ordinal))
            if (seen.Add((string?)x["endRam"] + "/" + (string?)x["endPicture"] + "/" + x["score"])) { r.Add(x); if (r.Count >= k) break; }
        return r;
    }

    static int ScoreGoal(TasSession s, Spec spec)
    {
        if (spec.Score >= 0) return spec.Score;
        var gs = s.Chapter.Goal.Select(TasGoal.Parse).ToList();
        return Enumerable.Range(0, gs.Count).Where(i => gs[i].Kind is "reach" or "within").DefaultIfEmpty(-1).Last();
    }

    /// <summary>The frame by which every reach/within goal was reached (null: not all).</summary>
    static int? LastReach(JsonArray gs)
    {
        var r = gs.Where(g => (string?)g!["kind"] is "reach" or "within").Select(g => (int?)g!["firstFrame"]).ToList();
        return r.Count == 0 || r.Any(x => x == null) ? null : r.Max();
    }

    static void ApplyAll(TasSession s, string edits) { foreach (var e in edits.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) TasCli.Apply(s, e); }

    /// <summary>Plays the current program with the early stop and scores it.</summary>
    static JsonObject Score(TasSession s, Spec spec, int gi, int tail, int cutoff, string edits, int parent)
    {
        var ev = s.Evaluate(gi, tail, cutoff);
        var gs = ev["goals"]!.AsArray();
        bool ok = (bool?)ev["goal"] ?? true;
        int? score = ok && gi >= 0 && gi < gs.Count ? (int?)gs[gi]!["firstFrame"] : null;
        if (gi >= 0 && score == null) ok = false;
        long? value = null;
        if (spec.Maximize is { Length: > 0 } mx) value = TasExpr.Parse(mx).Eval(new TasEvalContext(s.T, s.Map, s.Run.Dir, s.Cursor, (int)ev["lag"]!));
        if (gi < 0 && ok) score = s.Cursor;
        return new JsonObject
        {
            ["parent"] = parent, ["edits"] = edits, ["ok"] = ok, ["score"] = score, ["value"] = value, ["lag"] = (int)ev["lag"]!, ["stop"] = s.Cursor, ["last"] = LastReach(gs),
            ["firsts"] = new JsonArray(gs.Select(g => (JsonNode?)g!["firstFrame"]?.DeepClone()).ToArray()),
            ["endRam"] = s.T.RamHash().ToString("X16"), ["endPicture"] = s.T.PictureHash().ToString("X16"),
        };
    }

    static List<JsonObject> EvalRound(TasSession s, Spec spec, List<string> parents, int round, int si, int sn, int cutoffOpt)
    {
        var baseSteps = s.Snapshot(); int gi = ScoreGoal(s, spec);
        // the base's own score sets the tail (frames kept after the goal) and the cutoff
        var b = Score(s, spec, gi, int.MaxValue / 4, int.MaxValue, "", -1);
        int tail = spec.Tail >= 0 ? spec.Tail : (int?)b["last"] is int bl ? s.Frames - bl : 0;
        int cutoff = cutoffOpt != int.MaxValue ? cutoffOpt : (int?)b["score"] is int bs2 ? bs2 + Math.Max(spec.Slack, 0) : int.MaxValue;
        var rows = new List<JsonObject>();
        for (int pi = 0; pi < parents.Count; pi++)
        {
            s.Restore(baseSteps); ApplyAll(s, parents[pi]);
            var cands = Generate(s, spec, round, pi, parents[pi]);
            var mine = cands.Where((c, k) => k % sn == si).OrderByDescending(c => c.Frame).ToList();   // late edits first: each replays only from its edit
            var parentSteps = s.Snapshot();
            if (round == 0 && pi == 0 && si == 0) { b["edits"] = "(base)"; b["parent"] = -1; rows.Add(b); }
            foreach (var c in mine)
            {
                try
                {
                    s.Restore(parentSteps); ApplyAll(s, c.Edits);
                    var r = Score(s, spec, gi, tail, cutoff, string.Join("; ", new[] { parents[pi], c.Edits }.Where(x => x.Length > 0)), pi);
                    r["round"] = round; rows.Add(r);
                }
                catch (Exception e) when (e is ArgumentException or FormatException or ArgumentOutOfRangeException) { rows.Add(new JsonObject { ["parent"] = pi, ["edits"] = c.Edits, ["ok"] = false, ["error"] = e.Message, ["round"] = round }); }
            }
        }
        s.Restore(baseSteps);
        foreach (var r in rows.Where(r => r["edits"] is JsonValue v && (string)v! == "(base)")) r["edits"] = "";
        return rows;
    }

    // ---- candidate generation ------------------------------------------------------------------------------------------

    static string Pad(TasSession s, TasPad[] p) => string.Join("|", p.Take(Math.Max(1, Array.FindLastIndex(p, x => x.Buttons != 0 || x.X != 0 || x.Y != 0) + 1)).Select(x => TasScript.PadText(s.T.Map, x)));

    static List<Cand> Generate(TasSession s, Spec spec, int round, int parent, string parentEdits)
    {
        var rng = new Random(HashCode.Combine(spec.Seed, round, parent) & 0x7fffffff ^ (spec.Seed * 7919 + round * 104729 + parent * 31));
        var list = new List<Cand>(); int n = s.Frames;
        foreach (var gn in spec.Gens)
        {
            var g = gn!.AsObject(); string kind = (string?)g["kind"] ?? "";
            int from = Math.Clamp((int?)g["from"] ?? 0, 0, n), to = Math.Clamp((int?)g["to"] ?? n, 0, n);
            int R = (int?)g["range"] ?? 4, step = Math.Max(1, (int?)g["step"] ?? 1);
            int[] len = g["len"] is JsonArray la ? new[] { (int)la[0]!, (int)la[1]! } : new[] { 1, (int?)g["len"] ?? 1 };
            var buttons = g["buttons"] is JsonArray ba ? ba.Select(x => (string)x!).ToList() : new List<string> { "-" };
            var edges = Enumerable.Range(Math.Max(1, from), Math.Max(0, Math.Min(to, n) - Math.Max(1, from))).Where(e => !s.PadsAt(e).SequenceEqual(s.PadsAt(e - 1))).ToList();
            switch (kind)
            {
                case "shift":   // move an input change point by d frames, the length stays
                    foreach (var e in edges)
                        for (int d = -R; d <= R; d++)
                        {
                            if (d == 0) continue;
                            if (d > 0 && e + d <= n) list.Add(new(parent, $"set {e}:{Pad(s, s.PadsAt(e - 1))}:{d}", e));
                            if (d < 0 && e + d >= 0) list.Add(new(parent, $"set {e + d}:{Pad(s, s.PadsAt(e))}:{-d}", e + d));
                        }
                    break;
                case "hold":    // hold the input before a change point d frames longer (insert) or shorter (delete): later input moves
                    foreach (var e in edges)
                        for (int d = 1; d <= R; d++)
                        {
                            list.Add(new(parent, $"insert {e}:{Pad(s, s.PadsAt(e - 1))}:{d}", e));
                            if (e - d >= 0) list.Add(new(parent, $"delete {e - d}:{d}", e - d));
                        }
                    break;
                case "delete":
                    for (int f = from; f < to; f += step) for (int k = len[0]; k <= len[1]; k++) if (f + k <= n) list.Add(new(parent, $"delete {f}:{k}", f));
                    break;
                case "insert":
                    for (int f = from; f <= to; f += step) foreach (var bt in buttons) for (int k = len[0]; k <= len[1]; k++) list.Add(new(parent, $"insert {f}:{bt}:{k}", f));
                    break;
                case "set":     // replace frames [f, f+k) with a button set
                    for (int f = from; f < to; f += step) foreach (var bt in buttons) for (int k = len[0]; k <= len[1]; k++) if (f + k <= n) list.Add(new(parent, $"set {f}:{bt}:{k}", f));
                    break;
                case "press":   // add buttons to what the frames already hold (OR), frames [f, f+k)
                    for (int f = from; f < to; f += step)
                        foreach (var bt in buttons)
                            for (int k = len[0]; k <= len[1]; k++)
                            {
                                if (f + k > n) continue;
                                var add = s.Parse(bt);
                                var parts = Enumerable.Range(f, k).Select(i => { var p = s.PadsAt(i).ToArray(); for (int q = 0; q < Math.Min(p.Length, add.Length); q++) p[q] = new TasPad(p[q].Buttons | add[q].Buttons, p[q].X, p[q].Y); return $"set {i}:{Pad(s, p)}:1"; });
                                list.Add(new(parent, string.Join("; ", parts), f));
                            }
                    break;
                case "random":  // count candidates, each 1..edits random set/insert/delete ops in the window
                {
                    int count = (int?)g["count"] ?? 50; int[] ne = g["edits"] is JsonArray ea ? new[] { (int)ea[0]!, (int)ea[1]! } : new[] { 1, (int?)g["edits"] ?? 2 };
                    var ops = g["ops"] is JsonArray oa ? oa.Select(x => (string)x!).ToList() : new List<string> { "set" };
                    for (int c = 0; c < count; c++)
                    {
                        int m = rng.Next(ne[0], ne[1] + 1); var es = new List<string>(); int lo = int.MaxValue;
                        for (int q = 0; q < m; q++)
                        {
                            int f = rng.Next(from, Math.Max(from + 1, to)); int k = rng.Next(len[0], len[1] + 1); string op = ops[rng.Next(ops.Count)]; string bt = buttons[rng.Next(buttons.Count)];
                            es.Add(op switch { "insert" => $"insert {f}:{bt}:{k}", "delete" => $"delete {f}:{k}", _ => $"set {f}:{bt}:{k}" }); lo = Math.Min(lo, f);
                        }
                        list.Add(new(parent, string.Join("; ", es), lo));
                    }
                    break;
                }
                default: throw new InvalidDataException($"search generator kind '{kind}': shift hold delete insert set press random");
            }
        }
        list = list.DistinctBy(c => c.Edits).ToList();
        if (list.Count > spec.Budget) list = list.OrderBy(_ => rng.Next()).Take(spec.Budget).ToList();
        return list;
    }

    // ---- merge + write back ------------------------------------------------------------------------------------------

    static int Merge(TasRun run, Spec spec, List<JsonObject> rows, List<string> parentsIn, int round, string outDir, string rom, TasOpenOptions open, bool final)
    {
        var top = TopK(rows, spec.Beam);
        var bas = rows.FirstOrDefault(x => (string?)x["edits"] == "" && (int?)x["parent"] == -1);
        File.WriteAllText(Path.Combine(outDir, $"merged_r{round}.json"), new JsonObject { ["round"] = round, ["candidates"] = rows.Count, ["ok"] = rows.Count(x => (bool)x["ok"]!), ["base"] = bas?.DeepClone(), ["top"] = new JsonArray(top.Select(x => x.DeepClone()).ToArray()) }.ToJsonString(Indented));
        File.WriteAllText(Path.Combine(outDir, $"parents_r{round + 1}.json"), new JsonObject { ["parents"] = new JsonArray(top.Select(x => (JsonNode)(string)x["edits"]!).ToArray()) }.ToJsonString(Indented));
        Console.WriteLine($"TAS search merge round {round}: {rows.Count} candidates ({rows.Count(x => (bool)x["ok"]!)} ok), base score {bas?["score"]}, best {Best(rows)}");
        if (!final) return 0;
        var best = top.FirstOrDefault();
        if (best == null) { Console.WriteLine("TAS search: no candidate met the goals"); return 1; }
        // write back: replay the edits, trim at the score goal + tail, bless it (the runner plays it twice: deterministic)
        using (var s = new TasSession(run, spec.Chapter, rom.Length > 0 ? rom : null, open, Path.Combine(outDir, "scratch_final")))
        {
            int gi = ScoreGoal(s, spec);
            var b = Score(s, spec, gi, int.MaxValue / 4, int.MaxValue, "", -1);
            int tail = spec.Tail >= 0 ? spec.Tail : (int?)b["last"] is int bl ? s.Frames - bl : 0;
            ApplyAll(s, (string)best["edits"]!);
            var ev = Score(s, spec, gi, tail, int.MaxValue, (string)best["edits"]!, 0);
            if ((int?)ev["score"] != (int?)best["score"]) { Console.WriteLine($"TAS search: replay of the best scored {ev["score"]}, the shard said {best["score"]} (not deterministic?)"); return 1; }
            int trim = (int?)ev["last"] is int el ? el + tail : -1;
            string runPath = s.Save(Path.Combine(outDir, "best"), trim);
            var r2 = TasRun.Load(runPath);
            var ch = r2.Raw["chapters"]!.AsArray().First(c => (string?)c!["id"] == spec.Chapter)!.AsObject();
            ch["notes"] = $"T3 search: {best["edits"]} -> score goal first at {best["score"]} (base {b["score"]}), lag {best["lag"]} (base {b["lag"]}); {rows.Count} candidates, {spec.Rounds} round(s)";
            File.WriteAllText(runPath, r2.Raw.ToJsonString(Indented));
            r2 = TasRun.Load(runPath);
            var o = new TasRunOptions { OutDir = Path.Combine(outDir, "best_check"), Twice = true, Only = spec.Chapter, Open = open };
            var rep = TasRunner.Run(r2, o);
            bool det = (bool?)rep["determinism"]!["identical"] ?? false;
            if (rep["error"] == null && det) TasRunner.Bless(r2, rep);
            var summary = new JsonObject
            {
                ["run"] = runPath, ["edits"] = (string)best["edits"]!, ["score"] = best["score"]?.DeepClone(), ["baseScore"] = b["score"]?.DeepClone(), ["gain"] = (int?)b["score"] - (int?)best["score"],
                ["lag"] = best["lag"]?.DeepClone(), ["baseLag"] = b["lag"]?.DeepClone(), ["frames"] = trim, ["deterministic"] = det, ["blessed"] = rep["error"] == null && det, ["error"] = rep["error"]?.DeepClone(),
            };
            File.WriteAllText(Path.Combine(outDir, "best.json"), summary.ToJsonString(Indented));
            Console.WriteLine("TAS search best: " + summary.ToJsonString(Indented));
            return det && rep["error"] == null ? 0 : 1;
        }
    }

    // ---- pack: a portable start for the fleet --------------------------------------------------------------------------

    static int Pack(TasRun run, string chapter, int frame, string outDir, string rom, TasOpenOptions open)
    {
        var o = new TasRunOptions { Open = open };
        var res = TasCli.Branch(run, chapter, frame, outDir, null, o);
        if (res["error"] != null) { Console.Error.WriteLine("TAS search-pack: " + res["error"]); return 1; }
        string path = (string)res["branch"]!;
        var j = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var ch = j["chapters"]![0]!.AsObject(); ch["id"] = chapter;   // keep the chapter id (specs name it)
        if (TasRamMap.For(run) is { } map) { File.Copy(map.Path, Path.Combine(outDir, "ramdb.json"), true); j["ramdb"] = "ramdb.json"; }
        foreach (var png in Directory.GetFiles(run.Dir, "*.png")) File.Copy(png, Path.Combine(outDir, Path.GetFileName(png)), true);
        string romSrc = rom.Length > 0 ? rom : (string?)j["rom"] ?? "";
        if (File.Exists(romSrc)) { var rn = "rom" + Path.GetExtension(romSrc); File.Copy(romSrc, Path.Combine(outDir, rn), true); j["rom"] = rn; }
        File.Delete(path); Directory.Delete(Path.Combine(outDir, "to_branch"), true);
        path = Path.Combine(outDir, "pack.tasrun.json");
        File.WriteAllText(path, j.ToJsonString(Indented));
        Console.WriteLine($"TAS search-pack: {chapter} from frame {frame} -> {path}");
        return 0;
    }
}
