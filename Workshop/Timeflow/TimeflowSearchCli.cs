using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using NesEmulator.Debugging.Timeflow;

namespace BrokenNes.Workshop.Timeflow;

/// <summary>A search request: the condition, which timelines (one, several, a project, a game, or the whole corpus), the symbols, the limits.</summary>
internal sealed class SearchRequest
{
    public string Condition = "";
    public List<string> Timelines = new();
    public string Project = "", GameDir = "", Rom = "";
    public List<string> Symbols = new();
    public int MaxHits = 100;
    public bool CoarseOnly;
    public long? From, To;
    public Action<SearchHit>? OnHit;
}

/// <summary>
/// T4's search on Workshop machines (docs/projects/brokendev-timeflow-plan.md, "T4 as built"): the CLI verb and the call the MCP server's
/// <c>timeflow_search</c> makes (the same code, the same answers). The re-runs happen on NEW headless machines of each timeline's own ROM and
/// cores (one per game); nothing of the caller's sessions is touched, nothing is written but a session's temporary ROM copy.
/// <code>
///   --timeflow search --q "CONDITION" [--timeline ID|DIR]... [--project P | --game-dir DIR | --rom R] [--sym F]... [--max-hits 100] [--coarse]
///                     [--from A] [--to B] [--json]
/// </code>
/// Without a timeline / project / game / ROM the whole corpus (every game folder under BROKENDEV_HOME\timeflow) is searched. The condition
/// language: <see cref="SearchCondition"/> (exec, read, write, access, state, finding, picture, color, sound, channel; "and", "not").
/// Exit 0 with hits, 1 none, 2 usage, 3 cannot run.
/// </summary>
internal static class TimeflowSearching
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The Workshop's machine for a timeline (the movie machine: its ROM from the store, its cores; the debugger where the console has one).</summary>
    public static SearchMachine OpenMachine(string dir, TimelineMeta meta, IReadOnlyList<string> symbols)
    {
        bool dbg = meta.Console is not ("sms" or "gg" or "md");
        var m = TimeflowMachines.OpenFor(dir, meta, dbg, dbg ? symbols : null);
        return new SearchMachine
        {
            Machine = m.HasDebugger ? m.Host.Machine : null, Debugger = m.HasDebugger ? m.Host.Dbg : null, Source = TimeflowMachines.SourceFor(m),
            RunFrame = (a, b) => m.Frame(a, b), Port = m.Port, Game = (object?)m.Host.Nes ?? m.Host.Session!, RomSha256 = m.RomSha256, OnDispose = m.Dispose,
        };
    }

    /// <summary>The timelines a request names, in order (a game's timelines oldest first).</summary>
    public static List<string> Resolve(SearchRequest q)
    {
        var dirs = new List<string>();
        foreach (var t in q.Timelines) dirs.Add(TimeflowStore.Find(t) ?? throw new FileNotFoundException($"no timeline '{t}' under {TimeflowStore.Root()}"));
        var games = new List<string>();
        if (q.Project.Length > 0) games.Add(TimeflowStore.ProjectDir(q.Project));
        if (q.GameDir.Length > 0) games.Add(Path.GetFullPath(q.GameDir));
        if (q.Rom.Length > 0)
        {
            byte[] rom = File.ReadAllBytes(q.Rom);
            games.Add(TimeflowStore.GameDir(TimeflowStore.Sha256(rom), Path.GetFileName(q.Rom), out _));
        }
        if (dirs.Count == 0 && games.Count == 0) games.AddRange(TimeflowStore.GameDirs());
        foreach (var g in games)
        {
            if (!Directory.Exists(g)) throw new FileNotFoundException($"no Timeflow game folder {g}");
            dirs.AddRange(TimeflowStore.List(g).OrderBy(t => t.Meta.CreatedUtc).Select(t => t.Dir));
        }
        return dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static (SearchReport Report, JsonObject Json) Run(SearchRequest q)
    {
        var cond = SearchCondition.Parse(q.Condition);
        var dirs = Resolve(q);
        if (dirs.Count == 0) throw new FileNotFoundException($"no timelines to search under {TimeflowStore.Root()} (record some first: timeflow record)");
        var rep = TimeflowSearch.Run(new SearchRequestCore
        {
            Condition = cond, TimelineDirs = dirs, Open = OpenMachine, Symbols = q.Symbols, MaxHits = Math.Max(1, q.MaxHits), CoarseOnly = q.CoarseOnly,
            From = q.From, To = q.To, OnHit = q.OnHit,
        });
        var j = rep.ToJson();
        j["searched"] = dirs.Count;
        return (rep, j);
    }

    public static int Cli(Func<string, string, string> Opt, Func<string, List<string>> All, Func<string, bool> Has, bool json)
    {
        var q = new SearchRequest
        {
            Condition = string.Join(" and ", All("q")), Timelines = All("timeline"), Project = Opt("project", ""), GameDir = Opt("game-dir", ""), Rom = Opt("rom", ""),
            Symbols = All("sym"), CoarseOnly = Has("coarse"),
        };
        if (q.Condition.Length == 0) throw new ArgumentException("--q \"CONDITION\" is needed (" + SearchCondition.Help + ")");
        if (int.TryParse(Opt("max-hits", ""), out int mh)) q.MaxHits = mh;
        if (long.TryParse(Opt("from", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out long a)) q.From = a;
        if (long.TryParse(Opt("to", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out long b)) q.To = b;
        var (rep, j) = Run(q);
        Console.WriteLine(json ? j.ToJsonString(Indented) : Human(rep));
        return rep.TotalHits > 0 ? 0 : 1;
    }

    public static string Human(SearchReport r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"search: {r.Condition}");
        sb.AppendLine($"  {r.TotalHits} hit(s){(r.HitLimitReached ? " (the limit: more candidates not checked)" : "")} in {r.Timelines.Count} timeline(s); coarse {r.CoarseFrames} frames at {r.CoarseFps:0} fps -> {r.Candidates} candidate(s); " +
                      $"exact {r.ExactFrames} frame(s) decided, {r.ReRunFrames} re-run at {r.ReRunFps:0.0} fps; first hit after {(r.FirstHitMs is double fh ? $"{fh:0} ms" : "-")}, total {r.TotalMs:0} ms");
        foreach (var c in r.Clauses)
            sb.AppendLine($"  clause '{c.Text}' ({c.Coarse}): coarse no {c.No} / maybe {c.Maybe} / yes {c.Yes}; of the maybes checked {c.MaybeChecked}, held {c.MaybeHeld} (false candidates {c.FalseCandidateRate:P1}){(c.Contradicted > 0 ? $"; CONTRADICTED {c.Contradicted}" : "")}");
        foreach (var h in r.Hits)
            sb.AppendLine($"  HIT {h.Timeline} frame {h.Frame} ({h.Via}){(h.Cycle != null ? $" cycle {h.Cycle}" : "")}{(h.Pc != null ? $" pc {h.Pc}" : "")}{(h.Scanline != null ? $" line {h.Scanline} dot {h.Dot}" : "")}" +
                          $"{(h.Kind != null ? $" {h.Kind}" : "")}{(h.Address != null ? $" {h.Address} = {h.Value}" : "")}{(h.Instruction != null ? $" by {h.Instruction}" : "")}{(h.In != null ? $" in {h.In}" : "")}" +
                          (h.Context.Count > 0 ? "  [" + string.Join("; ", h.Context) + "]" : ""));
        foreach (var t in r.Timelines.Where(t => t.Notes.Count > 0 || t.Skipped != null || t.OffFrame != null))
        {
            if (t.Skipped != null) sb.AppendLine($"  timeline {t.Id}: skipped: {t.Skipped}");
            foreach (var n in t.Notes.Take(6)) sb.AppendLine($"  timeline {t.Id}: {n}");
        }
        foreach (var n in r.Notes.Take(10)) sb.AppendLine("  note: " + n);
        if (r.Hits.Count > 0) sb.AppendLine($"  jump: --timeflow jump --timeline {r.Hits[0].Timeline} --frame {r.Hits[0].Frame}; profile: --timeflow profile --timeline {r.Hits[0].Timeline} --from {r.Hits[0].Frame} --to {r.Hits[0].Frame} --q summary");
        return sb.ToString().TrimEnd();
    }
}
