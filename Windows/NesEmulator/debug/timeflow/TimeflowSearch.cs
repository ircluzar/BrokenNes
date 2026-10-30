using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using NesEmulator.Systems;

namespace NesEmulator.Debugging.Timeflow;

/// <summary>A search hit: a frame of a timeline (and, for a breakpoint, the firing in it), a jump target (T2) and a span to profile (T3).</summary>
public sealed class SearchHit
{
    public string Timeline = "", TimelineDir = "";
    public long Frame;
    /// <summary>"index" (decided from the per-frame index alone, no re-run) or "re-run" (the exact phase saw it).</summary>
    public string Via = "";
    public long? Cycle;
    public string? Cpu, Pc, Kind, Address, Value, In, Instruction, Registers;
    public int? Scanline, Dot;
    /// <summary>The firing's position in its profiled re-run (T3's #N), for <c>timeflow profile ... --q "stack N"</c>.</summary>
    public long? At;
    public List<string> Stack = new();
    /// <summary>What each clause saw in the frame ("[enemy_alive] != 0 = 1", "0 pixels of #F83800", "peak 0.000").</summary>
    public List<string> Context = new();

    public JsonObject ToJson() => new()
    {
        ["timeline"] = Timeline, ["frame"] = Frame, ["via"] = Via, ["cycle"] = Cycle, ["cpu"] = Cpu, ["pc"] = Pc, ["scanline"] = Scanline, ["dot"] = Dot,
        ["kind"] = Kind, ["address"] = Address, ["value"] = Value, ["in"] = In, ["instruction"] = Instruction, ["registers"] = Registers, ["at"] = At,
        ["stack"] = Stack.Count > 0 ? new JsonArray(Stack.Select(s => (JsonNode)s).ToArray()) : null,
        ["context"] = new JsonArray(Context.Select(s => (JsonNode)s).ToArray()),
        ["jump"] = new JsonObject { ["timeline"] = Timeline, ["frame"] = Frame, ["note"] = "timeflow jump puts a machine at the frame's start; the hit is in the frame (run it once to its end)" },
        ["profile"] = new JsonObject { ["timeline"] = Timeline, ["from"] = Frame, ["to"] = Frame },
    };
}

/// <summary>A clause's numbers over a search: what the coarse phase said, and what the exact phase found of its candidates.</summary>
public sealed class SearchClauseStats
{
    public string Text = "", Kind = "", Family = "";
    /// <summary>How the coarse phase decides this clause ("the frame's coverage", "exact from the recorded states", "re-run only").</summary>
    public string Coarse = "";
    public long No, Maybe, Yes;
    /// <summary>Of the frames the coarse phase was unsure of (Maybe) that the exact phase checked: how many held.</summary>
    public long MaybeChecked, MaybeHeld;
    /// <summary>Frames the coarse phase was sure of that the exact phase saw otherwise (must be 0: a coarse-phase error).</summary>
    public long Contradicted;
    public long Undecided;
    public double FalseCandidateRate => MaybeChecked > 0 ? (MaybeChecked - MaybeHeld) / (double)MaybeChecked : 0;

    public JsonObject ToJson() => new()
    {
        ["clause"] = Text, ["kind"] = Kind, ["family"] = Family, ["coarse"] = Coarse, ["no"] = No, ["maybe"] = Maybe, ["yes"] = Yes,
        ["maybeChecked"] = MaybeChecked, ["maybeHeld"] = MaybeHeld, ["falseCandidateRate"] = Math.Round(FalseCandidateRate, 4),
        ["contradicted"] = Contradicted, ["undecided"] = Undecided > 0 ? Undecided : null,
    };
}

/// <summary>What the search did with one timeline.</summary>
public sealed class SearchTimelineResult
{
    public string Id = "", Dir = "", Console = "";
    public long Frames, Scanned, Candidates, ExactFrames, ReRunFrames, Hits;
    /// <summary>Frames the coarse phase ruled out that a re-run (between candidates) found to hold: must be 0.</summary>
    public long CoarseMissed;
    public long ScanFrom, ScanTo;
    public string? Skipped;
    public long? OffFrame;
    public string OffWhat = "";
    public List<string> Notes = new();

    public JsonObject ToJson() => new()
    {
        ["id"] = Id, ["console"] = Console, ["frames"] = Frames, ["scanned"] = Scanned, ["candidates"] = Candidates, ["exactFrames"] = ExactFrames,
        ["reRunFrames"] = ReRunFrames, ["hits"] = Hits, ["coarseMissed"] = CoarseMissed, ["skipped"] = Skipped, ["offFrame"] = OffFrame, ["offWhat"] = OffFrame != null ? OffWhat : null,
        ["notes"] = Notes.Count > 0 ? new JsonArray(Notes.Select(n => (JsonNode)n).ToArray()) : null,
    };
}

/// <summary>A search's answer: the hits, per timeline what happened, per clause the numbers, what it cost.</summary>
public sealed class SearchReport
{
    public string Condition = "";
    public List<SearchHit> Hits = new();
    public long TotalHits;
    public bool HitLimitReached;
    public List<SearchTimelineResult> Timelines = new();
    public List<SearchClauseStats> Clauses = new();
    public long CoarseFrames, Candidates, ExactFrames, ReRunFrames, CandidatesNotChecked;
    public double CoarseMs, ExactMs, TotalMs, MachineMs;
    public double? FirstHitMs;
    public bool CoarseOnly, Cancelled;
    public List<string> Notes = new();

    public double CoarseFps => CoarseMs > 0 ? CoarseFrames / (CoarseMs / 1000.0) : 0;
    public double ReRunFps => ExactMs > 0 ? ReRunFrames / (ExactMs / 1000.0) : 0;

    public JsonObject ToJson() => new()
    {
        ["condition"] = Condition, ["totalHits"] = TotalHits, ["hitsShown"] = Hits.Count, ["hitLimitReached"] = HitLimitReached ? true : null,
        ["coarseOnly"] = CoarseOnly ? true : null, ["cancelled"] = Cancelled ? true : null,
        ["hits"] = new JsonArray(Hits.Select(h => (JsonNode)h.ToJson()).ToArray()),
        ["timelines"] = new JsonArray(Timelines.Select(t => (JsonNode)t.ToJson()).ToArray()),
        ["clauses"] = new JsonArray(Clauses.Select(c => (JsonNode)c.ToJson()).ToArray()),
        ["cost"] = new JsonObject
        {
            ["coarseFrames"] = CoarseFrames, ["coarseMs"] = Math.Round(CoarseMs, 1), ["coarseFps"] = Math.Round(CoarseFps), ["candidates"] = Candidates,
            ["exactFrames"] = ExactFrames, ["reRunFrames"] = ReRunFrames, ["exactMs"] = Math.Round(ExactMs, 1), ["reRunFps"] = Math.Round(ReRunFps, 1),
            ["candidatesNotChecked"] = CandidatesNotChecked > 0 ? CandidatesNotChecked : null,
            ["machineMs"] = Math.Round(MachineMs, 1), ["firstHitMs"] = FirstHitMs is double fh ? Math.Round(fh, 1) : null, ["totalMs"] = Math.Round(TotalMs, 1),
        },
        ["notes"] = Notes.Count > 0 ? new JsonArray(Notes.Select(n => (JsonNode)n).ToArray()) : null,
    };
}

/// <summary>A search to run: the condition, the timelines, how to open their machines, the symbols, the limits.</summary>
public sealed class SearchRequestCore
{
    public SearchCondition Condition = null!;
    public List<string> TimelineDirs = new();
    public SearchMachineOpener Open = null!;
    public List<string> Symbols = new();
    /// <summary>Hits kept (the search stops checking candidates once it has this many; the rest are counted, said).</summary>
    public int MaxHits = 100;
    /// <summary>Only the coarse phase: the candidates and the hits the index alone decides; nothing re-run.</summary>
    public bool CoarseOnly;
    /// <summary>Frames to search within each timeline (inclusive), null: all.</summary>
    public long? From, To;
    public Func<bool>? Cancel;
    /// <summary>Told as each timeline is done (timelines done, of how many).</summary>
    public Action<int, int>? Progress;
    /// <summary>Called with each hit as it is found (the desktop puts them on the scrubber while the search runs).</summary>
    public Action<SearchHit>? OnHit;
}

/// <summary>
/// T4's two-phase search over recorded timelines (docs/projects/brokendev-timeflow-plan.md, "Search over the corpus", "T4 as built"). Phase 1
/// (coarse) reads only each timeline's per-frame index and its recorded states: a clause is certainly false, certainly true, or unsure for each
/// frame. Phase 2 (exact) re-runs the unsure frames (and, with a breakpoint, the true ones, to find each firing) from their recorded states and
/// decides on the running machine: a breakpoint by every instruction and access (a T3 profiled span), a state on the machine's memory, a
/// picture on its picture, a sound on its samples. A re-run is checked against the recording frame by frame (a desync is reported, never
/// hidden). Hits are jump targets: T2's jump to the frame, T3's profile of it.
/// </summary>
public static class TimeflowSearch
{
    internal sealed class Bound
    {
        public SearchClause C = null!;
        public int Index;
        public SearchClauseStats Stats = null!;
        // breakpoints
        public int Cpu;
        public List<SpaceRange> Ranges = new();
        public string Resolved = "";
        public DebugExpr? IfExpr;
        public int RomSpace = -1;
        public bool CoverageUsable;
        // state
        public SearchStateEnv? CoarseEnv, LiveEnv;
        public DebugExpr? CoarseExpr, LiveExpr;
        public string? CoarseOff;
        // colour
        public readonly Dictionary<ulong, (bool Held, int Count)> ColorCache = new();
        public int Cost => C.Kind switch
        {
            SearchClauseKind.Finding or SearchClauseKind.PictureChanged or SearchClauseKind.PictureStill => 0,
            SearchClauseKind.Exec => 1,
            SearchClauseKind.Read or SearchClauseKind.Write or SearchClauseKind.Access => 2,
            SearchClauseKind.State => 3,
            _ => 4,
        };
    }

    /// <summary>A game's machine and its bound clauses (one per game folder, console and cores).</summary>
    internal sealed class Group : IDisposable
    {
        public string Key = "";
        public SearchMachine? M;
        public List<Bound> B = new();
        public string? BindError;
        public readonly Dictionary<string, SearchStateMap> Maps = new();
        public void Dispose() => M?.Dispose();
    }

    public static SearchReport Run(SearchRequestCore q)
    {
        var all = Stopwatch.StartNew();
        var rep = new SearchReport { Condition = q.Condition.Text, CoarseOnly = q.CoarseOnly };
        rep.Clauses = q.Condition.Clauses.Select(c => new SearchClauseStats { Text = c.Text, Kind = c.Kind.ToString().ToLowerInvariant(), Family = c.Family }).ToList();
        var groups = new Dictionary<string, Group>();
        int done = 0;
        try
        {
            foreach (string dir in q.TimelineDirs)
            {
                if (q.Cancel?.Invoke() == true) { rep.Cancelled = true; break; }
                var tr = new SearchTimelineResult { Dir = dir };
                rep.Timelines.Add(tr);
                try { SearchOne(q, rep, tr, dir, groups, all); }
                catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException or InvalidOperationException or ArgumentException or FormatException)
                {
                    tr.Skipped ??= e.Message;
                    if (e is FormatException) throw;   // a condition that does not read on this machine: said once, as the search's error
                }
                q.Progress?.Invoke(++done, q.TimelineDirs.Count);
            }
        }
        finally { foreach (var g in groups.Values) g.Dispose(); }
        rep.TotalMs = all.Elapsed.TotalMilliseconds;
        foreach (var t in rep.Timelines.Where(t => t.Skipped != null)) rep.Notes.Add($"timeline {t.Id}: not searched: {t.Skipped}");
        return rep;
    }

    // ------------------------------------------------------------------------------------------------ binding

    private static bool NeedsMachine(SearchCondition c, bool coarseOnly) => c.Clauses.Any(x => x.IsBreakpoint || x.Kind is SearchClauseKind.State or SearchClauseKind.Channel) ||
                                                                             (!coarseOnly && c.Clauses.Any(x => x.Kind is SearchClauseKind.Color or SearchClauseKind.Sound));

    private static Group GroupFor(SearchRequestCore q, SearchReport rep, string dir, TimelineMeta meta, Dictionary<string, Group> groups)
    {
        string key = TimeflowStore.GameDirOf(dir) + "|" + meta.Console + "|" + meta.Cores + "|" + meta.RomSha256 + "|" + meta.PowerOn + "|" + meta.Ntsc;
        if (groups.TryGetValue(key, out var g)) return g;
        g = new Group { Key = key };
        groups[key] = g;
        var sw = Stopwatch.StartNew();
        try
        {
            if (NeedsMachine(q.Condition, q.CoarseOnly)) g.M = q.Open(dir, meta, q.Symbols);
            Bind(q, g, meta, rep);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or InvalidOperationException or IOException or InvalidDataException)
        {
            g.BindError = e.Message;
        }
        rep.MachineMs += sw.Elapsed.TotalMilliseconds;
        return g;
    }

    private static void Bind(SearchRequestCore q, Group g, TimelineMeta meta, SearchReport rep)
    {
        var m = g.M;
        for (int i = 0; i < q.Condition.Clauses.Count; i++)
        {
            var c = q.Condition.Clauses[i];
            var b = new Bound { C = c, Index = i, Stats = rep.Clauses[i] };
            if (c.NeedsDebugger && (m == null || !m.HasDebugger))
                throw new NotSupportedException($"'{c.Text}' needs the debugger on the {meta.Console}'s CPU" + (meta.Console is "sms" or "gg" or "md" ? ", which is milestone T0 (the Z80 / 68000 debugger, built in parallel): this console's searches take state, picture and sound clauses until then" : ", which this machine does not have"));
            if (c.IsBreakpoint)
            {
                var mm = m!.Machine!;
                b.Cpu = -1;
                for (int k = 0; k < mm.Cpus.Count; k++) if (mm.Cpus[k].Id.Equals(c.CpuName, StringComparison.OrdinalIgnoreCase)) b.Cpu = k;
                if (b.Cpu < 0) throw new FormatException($"'{c.Text}': no CPU '{c.CpuName}' on the {mm.Console} (CPUs: {string.Join(", ", mm.Cpus.Select(x => x.Id))})");
                var cpu = mm.Cpus[b.Cpu];
                b.Ranges = Debugger.ResolveSpec(cpu, m.Debugger!.Symbols, c.Spec, c.Access, out b.Resolved);
                if (c.If != null) b.IfExpr = DebugExpr.Compile(c.If, cpu);
                var rom = cpu.Spaces.FirstOrDefault(s => s.Is("rom"));
                b.RomSpace = rom?.Id ?? -1;
                b.CoverageUsable = c.Kind == SearchClauseKind.Exec && b.Cpu == 0 && b.RomSpace >= 0 && b.Ranges.Count > 0 && b.Ranges.All(r => r.Space == b.RomSpace);
            }
            else if (c.Kind == SearchClauseKind.State)
            {
                var cpu = m!.Machine?.Main;
                b.CoarseEnv = new SearchStateEnv(cpu, m.Source.Domains, meta.Vars);
                b.LiveEnv = new SearchStateEnv(cpu, m.Source.Domains, meta.Vars);
                b.CoarseExpr = DebugExpr.Compile(c.Expr, b.CoarseEnv);
                b.LiveExpr = DebugExpr.Compile(c.Expr, b.LiveEnv);
            }
            else if (c.Kind == SearchClauseKind.Channel)
            {
                var names = DebugSound.Channels(m!.Machine!, m.Game, out string what).Select(x => x.Name).ToList();
                if (names.Count == 0) throw new NotSupportedException($"'{c.Text}': {what}");
                if (!names.Any(n => n.Equals(c.Channel, StringComparison.OrdinalIgnoreCase)))
                    throw new FormatException($"'{c.Text}': no channel '{c.Channel}' on the {meta.Console} (its channels: {string.Join(", ", names)})");
            }
            g.B.Add(b);
        }
    }

    // ------------------------------------------------------------------------------------------------ one timeline

    private static void SearchOne(SearchRequestCore q, SearchReport rep, SearchTimelineResult tr, string dir, Dictionary<string, Group> groups, Stopwatch all)
    {
        using var pb = new TimeflowPlayback(dir);
        var rd = pb.Reader;
        var meta = pb.Meta;
        tr.Id = meta.Id; tr.Console = meta.Console; tr.Frames = rd.Frames;
        if (rd.Frames == 0) { tr.Skipped = "it holds no frames"; return; }
        var g = GroupFor(q, rep, dir, meta, groups);
        if (g.BindError != null) { tr.Skipped = g.BindError; return; }
        if (g.M != null) pb.CheckMachine(g.M.Source, g.M.RomSha256);
        if (q.Condition.Clauses.Any(c => c.Kind == SearchClauseKind.Finding) && !meta.Checks)
        {
            tr.Skipped = "it was recorded without the hardware checks, so its index holds no findings (record with the checks: --checks / -Checks / checks: true)";
            return;
        }
        long from = Math.Max(rd.StartFrame, q.From ?? rd.StartFrame), to = Math.Min(rd.EndFrame - 1, q.To ?? long.MaxValue);
        if (from > to) { tr.Skipped = $"no frame of it is in {q.From}-{q.To}"; return; }
        tr.ScanFrom = from; tr.ScanTo = to;

        // ---------------------------------------------------------------- phase 1: coarse
        var sw = Stopwatch.StartNew();
        var order = g.B.OrderBy(b => b.Cost).ToList();
        bool needExtras = g.B.Any(b => b.C.Kind is SearchClauseKind.Finding || b.C.Kind == SearchClauseKind.Exec && b.CoverageUsable && meta.Coverage ||
                                       b.C.IsBreakpoint && b.C.Kind != SearchClauseKind.Exec && meta.Accesses || b.C.Kind == SearchClauseKind.State && meta.Vars.Count > 0);
        foreach (var b in g.B) Explain(b, meta, tr);
        var state = g.B.FirstOrDefault(b => b.C.Kind == SearchClauseKind.State);
        if (state != null && g.M != null)
        {
            // the machine holds a state of this timeline (its layout) for the state map's measurements
            pb.Jump(g.M.Source, from);
            foreach (var b in g.B.Where(b => b.C.Kind == SearchClauseKind.State)) b.CoarseOff = null;
        }
        var candidates = new List<(long F, SearchTri Combined, byte[] Each)>();
        var direct = new List<(long F, byte[] Each, List<string> Context)>();
        TimelineExtras? ex = null; long exFrame = -1;
        TimelineExtras Ex(long f) { if (exFrame != f) { ex = needExtras ? rd.ExtrasScan(f) : new TimelineExtras(); exFrame = f; } return ex!; }
        var each = new byte[g.B.Count];
        for (long f = from; f <= to; f++)
        {
            if ((f & 1023) == 0 && q.Cancel?.Invoke() == true) { rep.Cancelled = true; break; }
            var combined = SearchTri.Yes;
            Array.Fill(each, (byte)SearchTri.Maybe);
            List<string>? ctx = null;
            foreach (var b in order)
            {
                var t = Coarse(b, f, rd, meta, Ex, g, pb, tr, ref ctx);
                var st = b.Stats;
                if (t == SearchTri.No) st.No++; else if (t == SearchTri.Yes) st.Yes++; else st.Maybe++;
                each[b.Index] = (byte)t;
                if (b.C.Not) t = t == SearchTri.Yes ? SearchTri.No : t == SearchTri.No ? SearchTri.Yes : SearchTri.Maybe;
                if (t == SearchTri.No) { combined = SearchTri.No; break; }
                if (t == SearchTri.Maybe) combined = SearchTri.Maybe;
            }
            tr.Scanned++;
            if (combined == SearchTri.No) continue;
            tr.Candidates++;
            if (combined == SearchTri.Yes && q.Condition.Anchor == null) direct.Add((f, (byte[])each.Clone(), ctx ?? new List<string>()));
            else candidates.Add((f, combined, (byte[])each.Clone()));
        }
        rep.CoarseFrames += tr.Scanned;
        rep.Candidates += tr.Candidates;
        rep.CoarseMs += sw.Elapsed.TotalMilliseconds;

        // the frames the index alone decided are hits (no re-run)
        foreach (var (f, _, context) in direct)
        {
            if (!AddHit(q, rep, tr, new SearchHit { Timeline = meta.Id, TimelineDir = dir, Frame = f, Via = "index", Context = context }, all)) break;
        }
        if (q.CoarseOnly || candidates.Count == 0 || rep.HitLimitReached || rep.Cancelled)
        {
            if (q.CoarseOnly && candidates.Count > 0) tr.Notes.Add($"{candidates.Count} candidate frame(s) not checked (coarse only): {Frames(candidates.Select(c => c.F))}");
            if (!q.CoarseOnly && rep.HitLimitReached) rep.CandidatesNotChecked += candidates.Count;
            return;
        }

        // ---------------------------------------------------------------- phase 2: exact
        var sw2 = Stopwatch.StartNew();
        try { Exact(q, rep, tr, dir, pb, g, candidates, direct.Select(d => d.F).ToHashSet(), all); }
        finally { rep.ExactMs += sw2.Elapsed.TotalMilliseconds; }
    }

    private static string Frames(IEnumerable<long> fs)
    {
        var l = fs.Take(12).ToList();
        return string.Join(", ", l) + (fs.Skip(12).Any() ? ", ..." : "");
    }

    /// <summary>How the coarse phase decides a clause on this timeline (once, into the clause's numbers and the timeline's notes).</summary>
    private static void Explain(Bound b, TimelineMeta meta, SearchTimelineResult tr)
    {
        string how;
        switch (b.C.Kind)
        {
            case SearchClauseKind.Exec:
                if (!b.CoverageUsable) { how = "re-run only (the address is not ROM code of the main CPU: give a code symbol or a prg / rom offset to use the coverage)"; tr.Notes.Add($"'{b.C.Text}': {how}: every frame is a candidate"); }
                else if (!meta.Coverage) { how = "re-run only (the timeline has no coverage in its index)"; tr.Notes.Add($"'{b.C.Text}': timeline {meta.Id} was recorded without coverage: every frame is a candidate (record with coverage)"); }
                else how = "the frame's coverage (the ROM code the main CPU ran)";
                break;
            case SearchClauseKind.Read or SearchClauseKind.Write or SearchClauseKind.Access:
                if (b.Cpu != 0) { how = "re-run only (the index counts the main CPU's accesses)"; tr.Notes.Add($"'{b.C.Text}': {how}: every frame is a candidate"); }
                else if (!meta.Accesses) { how = "re-run only (the timeline has no access counts in its index)"; tr.Notes.Add($"'{b.C.Text}': timeline {meta.Id} was recorded without access counts: every frame is a candidate (record with accesses)"); }
                else how = "the frame's access counts per 256-byte page";
                break;
            case SearchClauseKind.State: how = "exact from the recorded states (the end of the frame: the next frame's state) and watched variables"; break;
            case SearchClauseKind.Finding: how = "exact from the index (the findings first seen in the frame)"; break;
            case SearchClauseKind.PictureChanged or SearchClauseKind.PictureStill: how = "exact from the index (the picture hashes)"; break;
            case SearchClauseKind.Color: how = "re-render (the picture of each distinct picture hash among the candidates)"; break;
            default: how = "re-run (the frame's sound)"; break;
        }
        if (b.Stats.Coarse.Length == 0 || b.Stats.Coarse == how) b.Stats.Coarse = how;
        else if (!b.Stats.Coarse.Contains(how)) b.Stats.Coarse += "; " + how;
    }

    private static SearchTri Coarse(Bound b, long f, TimelineReader rd, TimelineMeta meta, Func<long, TimelineExtras> ex, Group g, TimeflowPlayback pb, SearchTimelineResult tr, ref List<string>? ctx)
    {
        switch (b.C.Kind)
        {
            case SearchClauseKind.Exec:
            {
                if (!b.CoverageUsable || !meta.Coverage) return SearchTri.Maybe;
                foreach (var (s, l) in ex(f).Coverage)
                    foreach (var r in b.Ranges)
                        if (r.Start <= s + l - 1 && r.End >= s) return SearchTri.Maybe;
                return SearchTri.No;
            }
            case SearchClauseKind.Read or SearchClauseKind.Write or SearchClauseKind.Access:
            {
                if (b.Cpu != 0 || !meta.Accesses) return SearchTri.Maybe;
                var acc = ex(f).Accesses;
                if (acc == null) return SearchTri.Maybe;
                foreach (var a in acc)
                {
                    bool kind = b.C.Kind switch { SearchClauseKind.Read => a.Reads > 0, SearchClauseKind.Write => a.Writes > 0, _ => a.Reads + a.Writes > 0 };
                    if (!kind) continue;
                    foreach (var r in b.Ranges)
                        if (r.Space == a.Space && (r.Start >> 8) <= a.Page && (r.End >> 8) >= a.Page) return SearchTri.Maybe;
                }
                return SearchTri.No;
            }
            case SearchClauseKind.State:
            {
                if (b.CoarseOff != null || f + 1 >= rd.EndFrame) return SearchTri.Maybe;
                var env = b.CoarseEnv!;
                env.NextFrame(f, meta.Vars.Count > 0 ? ex(f).Vars : Array.Empty<long>());
                env.State = () => rd.StateView(f + 1);
                // a state's layout can change (a keyframe says so): each layout has its own map, measured on the machine holding a state of it
                env.MapFor = (st, lay) =>
                {
                    if (!g.Maps.TryGetValue(lay, out var map)) { pb.Jump(g.M!.Source, f + 1); g.Maps[lay] = map = new SearchStateMap(g.M, lay); }
                    return map;
                };
                try
                {
                    long v = b.CoarseExpr!.Evaluate(new ExprContext());
                    if (v != 0) { ctx ??= new(); ctx.Add($"{b.C.Expr} = {v}"); }
                    return v != 0 ? SearchTri.Yes : SearchTri.No;
                }
                catch (SearchUnmappableException e)
                {
                    // nothing can read a CPU address on a console without the debugger, the re-run neither: the timeline is skipped, saying so
                    if (e.Message == SearchStateMap.NoCpu) throw new NotSupportedException($"'{b.C.Text}': {e.Message}");
                    b.CoarseOff = e.Message;
                    tr.Notes.Add($"'{b.C.Text}': not decided from the recorded states ({e.Message}): its frames are checked by a re-run");
                    return SearchTri.Maybe;
                }
            }
            case SearchClauseKind.Finding:
            {
                var fs = ex(f).Findings.Where(x => b.C.Contains.Length == 0 || x.Contains(b.C.Contains, StringComparison.OrdinalIgnoreCase)).ToList();
                if (fs.Count > 0) { ctx ??= new(); ctx.AddRange(fs.Select(x => "finding: " + x)); }
                return fs.Count > 0 ? SearchTri.Yes : SearchTri.No;
            }
            case SearchClauseKind.PictureChanged or SearchClauseKind.PictureStill:
            {
                if (f == rd.StartFrame) return SearchTri.No;   // no frame before it in the timeline (said in the clause's notes)
                bool changed = rd.Row(f).Picture != rd.Row(f - 1).Picture;
                bool holds = b.C.Kind == SearchClauseKind.PictureChanged ? changed : !changed;
                if (holds) { ctx ??= new(); ctx.Add(changed ? "the picture changed" : "the picture is the frame before's"); }
                return holds ? SearchTri.Yes : SearchTri.No;
            }
            default: return SearchTri.Maybe;
        }
    }

    private static bool AddHit(SearchRequestCore q, SearchReport rep, SearchTimelineResult tr, SearchHit h, Stopwatch all)
    {
        rep.TotalHits++; tr.Hits++;
        rep.FirstHitMs ??= all.Elapsed.TotalMilliseconds;
        if (rep.Hits.Count < q.MaxHits) { rep.Hits.Add(h); q.OnHit?.Invoke(h); }
        if (rep.Hits.Count >= q.MaxHits) { rep.HitLimitReached = true; return false; }
        return true;
    }

    // ------------------------------------------------------------------------------------------------ phase 2

    /// <summary>What a re-run frame gave each clause (null: undecided, with the reason in the notes).</summary>
    internal sealed class FrameTruth
    {
        public bool?[] Holds = Array.Empty<bool?>();
        public List<string> Context = new();
    }

    private static void Exact(SearchRequestCore q, SearchReport rep, SearchTimelineResult tr, string dir, TimeflowPlayback pb, Group g, List<(long F, SearchTri Combined, byte[] Each)> cands, HashSet<long> direct, Stopwatch all)
    {
        var m = g.M ?? throw new InvalidOperationException("the exact phase needs the game's machine");
        var rd = pb.Reader;
        var meta = pb.Meta;
        var byFrame = cands.ToDictionary(c => c.F, c => c.Each);
        // a candidate whose only unsure clauses are colours and whose picture was already rendered: decided from the cache, not re-run
        bool hasAnchor = q.Condition.Anchor != null;
        var colourOnly = new HashSet<long>();
        if (!hasAnchor)
        {
            var seen = new HashSet<ulong>();
            foreach (var c in cands)
            {
                bool onlyColour = g.B.All(b => c.Each[b.Index] != (byte)SearchTri.Maybe || b.C.Kind == SearchClauseKind.Color);
                if (!onlyColour) continue;
                ulong pic = rd.Row(c.F).Picture;
                if (!seen.Add(pic)) colourOnly.Add(c.F);
            }
        }
        var toRun = cands.Where(c => !colourOnly.Contains(c.F)).Select(c => c.F).ToList();
        bool profiled = g.B.Any(b => b.C.IsBreakpoint);
        int gap = profiled ? Math.Max(1, meta.KeyEvery) : 8;
        var runs = new List<(long A, long B)>();
        foreach (long f in toRun)
        {
            if (runs.Count > 0 && f - runs[^1].B <= gap) runs[^1] = (runs[^1].A, f);
            else runs.Add((f, f));
        }
        bool stop = false;
        long decided = 0;
        var decidedFrames = new HashSet<long>();
        int undecidedNotes = 0, missedNotes = 0;
        void Decide(long f, FrameTruth t, List<SearchHit>? firings)
        {
            if (stop) return;
            byFrame.TryGetValue(f, out var each);
            bool candidate = each != null;
            // a frame is decided once (a run's frame before may be a candidate the picture cache also holds)
            if (!decidedFrames.Add(f)) return;
            if (candidate) { tr.ExactFrames++; rep.ExactFrames++; decided++; }
            bool hit = true, undecided = false;
            for (int i = 0; i < g.B.Count; i++)
            {
                var b = g.B[i];
                bool? h = t.Holds[i];
                if (candidate)
                {
                    var co = (SearchTri)each![i];
                    if (h == null) b.Stats.Undecided++;
                    else if (co == SearchTri.Maybe) { b.Stats.MaybeChecked++; if (h == true) b.Stats.MaybeHeld++; }
                    else if (co == SearchTri.Yes && h == false || co == SearchTri.No && h == true) b.Stats.Contradicted++;
                }
                if (h == null) { undecided = true; hit = false; continue; }
                if (h.Value == b.C.Not) hit = false;
            }
            if (!hit)
            {
                if (undecided && candidate && undecidedNotes++ < 5) tr.Notes.Add($"frame {f}: undecided ({string.Join("; ", t.Context.Where(x => x.StartsWith("undecided")))})");
                return;
            }
            if (!candidate && (f < tr.ScanFrom || f > tr.ScanTo || direct.Contains(f))) return;   // outside the frames asked for (the frame before a run), or a hit the index already gave
            if (!candidate)
            {
                // the coarse phase said no for this frame, and the re-run (a frame between candidates, or the one before) says yes: a coarse error
                tr.CoarseMissed++;
                if (missedNotes++ < 5) rep.Notes.Add($"timeline {meta.Id} frame {f}: the coarse phase missed this hit (a frame re-run between candidates holds): a bug in the index or the search");
                return;
            }
            if (firings != null && firings.Count > 0)
            {
                foreach (var h in firings) { h.Context.AddRange(t.Context); if (!AddHit(q, rep, tr, h, all)) { stop = true; break; } }
            }
            else if (!hasAnchor)
            {
                if (!AddHit(q, rep, tr, new SearchHit { Timeline = meta.Id, TimelineDir = dir, Frame = f, Via = "re-run", Context = t.Context, Cycle = m.Machine?.Cycle, Pc = m.Machine != null ? m.Machine.Main.FormatAddress(m.Machine.Main.PC) : null }, all)) stop = true;
            }
        }
        // the colour-only candidates decided from the cache after the runs
        foreach (var (a, b) in runs)
        {
            if (stop || q.Cancel?.Invoke() == true) { if (!stop) rep.Cancelled = true; break; }
            long fromF = a > rd.StartFrame ? a - 1 : a;
            if (profiled) RunProfiled(q, rep, tr, pb, g, fromF, b, Decide, meta, dir);
            else RunPlain(q, rep, tr, pb, g, fromF, b, Decide, meta);
            if (tr.OffFrame != null) { tr.Notes.Add($"the re-run went out of sync at frame {tr.OffFrame} ({tr.OffWhat}): the search of this timeline stops there"); break; }
        }
        foreach (long f in colourOnly)
        {
            if (stop || tr.OffFrame != null) break;
            var t = new FrameTruth { Holds = new bool?[g.B.Count] };
            ulong pic = rd.Row(f).Picture;
            for (int i = 0; i < g.B.Count; i++)
            {
                var b = g.B[i];
                var co = (SearchTri)byFrame[f][i];
                if (b.C.Kind == SearchClauseKind.Color && b.ColorCache.TryGetValue(pic, out var cc)) { t.Holds[i] = cc.Held; t.Context.Add($"{cc.Count} pixel(s) of #{b.C.Rgb:X6} (the same picture as a frame re-rendered)"); }
                else if (b.C.Kind == SearchClauseKind.Color) t.Holds[i] = null;
                else t.Holds[i] = co == SearchTri.Yes ? true : co == SearchTri.No ? false : null;
            }
            Decide(f, t, null);
        }
        if (decided < cands.Count) rep.CandidatesNotChecked += cands.Count - decided;
    }

    /// <summary>Evaluates the frame-level clauses on the machine at the end of frame <paramref name="f"/> (the breakpoints come from the re-run's records).</summary>
    private static FrameTruth EndOfFrame(Group g, TimelineReader rd, TimelineMeta meta, long f, float[] samples, bool[]? fired)
    {
        var m = g.M!;
        var t = new FrameTruth { Holds = new bool?[g.B.Count] };
        var row = rd.Row(f);
        bool pictureOk = m.Source.PictureHash() == row.Picture;
        TimelineExtras? ex = null;
        for (int i = 0; i < g.B.Count; i++)
        {
            var b = g.B[i];
            var c = b.C;
            switch (c.Kind)
            {
                case SearchClauseKind.Exec or SearchClauseKind.Read or SearchClauseKind.Write or SearchClauseKind.Access:
                    t.Holds[i] = fired?[i] ?? false;
                    break;
                case SearchClauseKind.State:
                {
                    var env = b.LiveEnv!;
                    try
                    {
                        ex ??= meta.Vars.Count > 0 ? rd.ExtrasScan(f) : new TimelineExtras();
                        env.NextFrame(f, ex.Vars);
                        long v = b.LiveExpr!.Evaluate(new ExprContext());
                        t.Holds[i] = v != 0;
                        t.Context.Add($"{c.Expr} = {v}");
                    }
                    catch (SearchUnmappableException e) { t.Holds[i] = null; t.Context.Add($"undecided: {c.Text}: {e.Message}"); }
                    break;
                }
                case SearchClauseKind.Finding:
                {
                    ex ??= rd.ExtrasScan(f);
                    var fs = ex.Findings.Where(x => c.Contains.Length == 0 || x.Contains(c.Contains, StringComparison.OrdinalIgnoreCase)).ToList();
                    t.Holds[i] = fs.Count > 0;
                    t.Context.AddRange(fs.Select(x => "finding: " + x));
                    break;
                }
                case SearchClauseKind.PictureChanged or SearchClauseKind.PictureStill:
                {
                    if (f == rd.StartFrame) { t.Holds[i] = false; break; }
                    bool changed = row.Picture != rd.Row(f - 1).Picture;
                    t.Holds[i] = c.Kind == SearchClauseKind.PictureChanged ? changed : !changed;
                    break;
                }
                case SearchClauseKind.Color:
                {
                    if (b.ColorCache.TryGetValue(row.Picture, out var cc)) { t.Holds[i] = cc.Held; t.Context.Add($"{cc.Count} pixel(s) of #{c.Rgb:X6}"); break; }
                    if (!pictureOk) { t.Holds[i] = null; t.Context.Add($"undecided: {c.Text}: the re-run's picture is not the recorded one (a loaded NES shows the picture before the load for one frame)"); break; }
                    var (px, w, h) = m.Picture();
                    int n = CountColour(px, w, h, c);
                    bool held = n >= c.Min;
                    b.ColorCache[row.Picture] = (held, n);
                    t.Holds[i] = held;
                    t.Context.Add($"{n} pixel(s) of #{c.Rgb:X6}{(c.W > 0 ? $" in {c.X},{c.Y} {c.W}x{c.H}" : "")}");
                    break;
                }
                case SearchClauseKind.Sound:
                {
                    if (samples.Length == 0) { t.Holds[i] = null; t.Context.Add($"undecided: {c.Text}: the machine gave no sound samples for the frame"); break; }
                    float peak = 0; foreach (float s in samples) { float a = Math.Abs(s); if (a > peak) peak = a; }
                    t.Holds[i] = c.Above ? peak > c.Level : peak < c.Level;
                    t.Context.Add($"sound peak {peak:0.0000} ({samples.Length} samples)");
                    break;
                }
                case SearchClauseKind.Channel:
                {
                    var ch = DebugSound.Channels(m.Machine!, m.Game, out _).FirstOrDefault(x => x.Name.Equals(c.Channel, StringComparison.OrdinalIgnoreCase));
                    if (ch == null) { t.Holds[i] = null; t.Context.Add($"undecided: no channel {c.Channel}"); break; }
                    t.Holds[i] = c.Playing ? ch.On : !ch.On;
                    t.Context.Add($"{ch.Name} {(ch.On ? "playing" : "silent")} ({ch.Text.Trim()})");
                    break;
                }
            }
        }
        return t;
    }

    public static int CountColour(uint[] px, int w, int h, SearchClause c)
    {
        int x0 = 0, y0 = 0, x1 = w, y1 = h;
        if (c.W > 0) { x0 = Math.Clamp(c.X, 0, w); y0 = Math.Clamp(c.Y, 0, h); x1 = Math.Clamp(c.X + c.W, 0, w); y1 = Math.Clamp(c.Y + c.H, 0, h); }
        int r = (int)(c.Rgb >> 16 & 0xFF), gg = (int)(c.Rgb >> 8 & 0xFF), bb = (int)(c.Rgb & 0xFF), tol = c.Tol, n = 0;
        uint want = c.Rgb & 0xFFFFFF;
        for (int y = y0; y < y1; y++)
        {
            int row = y * w;
            for (int x = x0; x < x1; x++)
            {
                uint p = px[row + x] & 0xFFFFFF;
                if (tol == 0) { if (p == want) n++; continue; }
                if (Math.Abs((int)(p >> 16 & 0xFF) - r) <= tol && Math.Abs((int)(p >> 8 & 0xFF) - gg) <= tol && Math.Abs((int)(p & 0xFF) - bb) <= tol) n++;
            }
        }
        return n;
    }

    /// <summary>A re-run with no breakpoint: jump, the recorded pads, every frame checked, the frame-level clauses at each frame's end.</summary>
    private static void RunPlain(SearchRequestCore q, SearchReport rep, SearchTimelineResult tr, TimeflowPlayback pb, Group g, long from, long to, Action<long, FrameTruth, List<SearchHit>?> decide, TimelineMeta meta)
    {
        var m = g.M!;
        var rd = pb.Reader;
        pb.Jump(m.Source, from);
        m.DrainSound();
        for (long f = from; f <= to; f++)
        {
            var row = rd.Row(f);
            m.RunFrame((PadButtons)row.P1, (PadButtons)row.P2);
            tr.ReRunFrames++; rep.ReRunFrames++;
            var samples = m.DrainSound();
            ulong pic = m.Source.PictureHash(), ram = m.Source.RamHash();
            bool held = f == from && meta.Console == "nes" && pic != row.Picture && ram == row.Ram;
            if (!held && (pic != row.Picture || ram != row.Ram))
            {
                tr.OffFrame = f;
                tr.OffWhat = (pic != row.Picture ? "the picture" : "") + (pic != row.Picture && ram != row.Ram ? " and " : "") + (ram != row.Ram ? "the work RAM" : "") + " after the frame";
                return;
            }
            decide(f, EndOfFrame(g, rd, meta, f, samples, null), null);
        }
    }

    /// <summary>A re-run with breakpoints: a T3 profiled span whose records feed a <see cref="SearchSink"/> (every instruction and access).</summary>
    private static void RunProfiled(SearchRequestCore q, SearchReport rep, SearchTimelineResult tr, TimeflowPlayback pb, Group g, long from, long to, Action<long, FrameTruth, List<SearchHit>?> decide, TimelineMeta meta, string dir)
    {
        var m = g.M!;
        var sink = new SearchSink(g, pb.Reader, meta, dir, decide);
        var span = new ProfileSpan { From = from, To = to, Vars = new List<string>(), StopWhenOff = true, Cancel = q.Cancel };
        var r = TimeflowProfiler.Run(pb, m.Profile(), span, new ProfileSink[] { sink });
        long ran = r.PreRollFrames + r.FramesProfiled;
        tr.ReRunFrames += ran; rep.ReRunFrames += ran;
        if (r.OffFrame != null) { tr.OffFrame = r.OffFrame; tr.OffWhat = r.OffWhat; }
        if (sink.Error is { } err) throw new FormatException(err);
    }

    /// <summary>The exact phase's sink over a profiled re-run: the breakpoints' firings from the records, the frame-level clauses at each frame's end.</summary>
    internal sealed class SearchSink : ProfileQuery
    {
        private readonly Group g;
        private readonly TimelineReader rd;
        private readonly TimelineMeta meta;
        private readonly string dir;
        private readonly Action<long, FrameTruth, List<SearchHit>?> decide;
        private long cur = -1;
        private bool[] fired = Array.Empty<bool>();
        private readonly List<SearchHit> firings = new();
        private long firingsMore;
        private Stacks stacks = null!;
        private LastInstr last = null!;
        private readonly ExprContext ec = new();
        private int anchor = -1;
        public new string? Error => base.Error;

        public SearchSink(Group g, TimelineReader rd, TimelineMeta meta, string dir, Action<long, FrameTruth, List<SearchHit>?> decide)
        {
            this.g = g; this.rd = rd; this.meta = meta; this.dir = dir; this.decide = decide;
        }

        protected override void Setup()
        {
            fired = new bool[g.B.Count];
            stacks = new Stacks(Ctx.Machine.Cpus.Count);
            last = new LastInstr(Ctx.Machine.Cpus.Count);
            var a = g.B.FirstOrDefault(b => b.C.IsBreakpoint && !b.C.Not);
            anchor = a?.Index ?? -1;
        }

        public override void OnStack(int c, IReadOnlyList<ProfCall> frames) => stacks.Reset(c, frames);
        public override void OnCall(in ProfCall c) => stacks.On(in c);

        public override void OnFrame(in ProfFrame f)
        {
            if (cur >= 0) EndFrame();
            cur = f.Frame;
            Array.Clear(fired);
            firings.Clear(); firingsMore = 0;
            g.M!.DrainSound();
        }

        public override void End() { if (cur >= 0) EndFrame(); cur = -1; }

        private void EndFrame()
        {
            if (Ctx.OffFrame is long off && cur >= off) return;   // not the recorded game from there (the report says so)
            var samples = g.M!.DrainSound();
            var t = EndOfFrame(g, rd, meta, cur, samples, fired);
            if (firingsMore > 0) t.Context.Add($"{firingsMore} more firing(s) in this frame (64 shown)");
            decide(cur, t, anchor >= 0 ? new List<SearchHit>(firings) : null);
        }

        private void Fire(Bound b, in ProfInstr ins, long cycle, int scanline, int dot, string? address, string? value, string kind)
        {
            fired[b.Index] = true;
            if (b.Index != anchor) return;
            if (firings.Count >= 64) { firingsMore++; return; }
            var h = new SearchHit
            {
                Timeline = meta.Id, TimelineDir = dir, Frame = cur, Via = "re-run", Cycle = cycle, Cpu = Ctx.CpuId(ins.Cpu), Pc = Ctx.Address(ins.Cpu, ins.R.Pc),
                Scanline = scanline, Dot = dot, Kind = kind, Address = address, Value = value, In = Ctx.SymbolOf(in ins), Instruction = ins.R.Kind == 0 ? Ctx.Disasm(in ins) : null,
                Registers = Ctx.Cpu(ins.Cpu).FormatRegisters(in ins.R), At = ins.Seq,
            };
            foreach (var s in StackJson(stacks.Of(ins.Cpu)).Take(4)) h.Stack.Add(s!.GetValue<string>());
            firings.Add(h);
        }

        public override void OnInstr(in ProfInstr i)
        {
            last.Of[i.Cpu] = i;
            if (cur < 0) return;
            var keys = i.Keys(stackalloc SpaceKey[4]);
            foreach (var b in g.B)
            {
                if (b.C.Kind != SearchClauseKind.Exec || b.Cpu != i.Cpu || i.R.Kind != 0) continue;
                if (fired[b.Index] && b.Index != anchor) continue;
                if (!Matches(b, keys)) continue;
                if (b.IfExpr != null) { ec.Address = i.R.Pc; ec.Value = 0; ec.Source = 0; if (!b.IfExpr.IsTrue(ec)) continue; }
                Fire(b, in i, i.R.Cycle, i.R.Scanline, i.R.Dot, null, null, "exec");
            }
        }

        public override void OnAccess(in ProfAccess a)
        {
            if (cur < 0) return;
            var keys = a.Keys(stackalloc SpaceKey[4]);
            foreach (var b in g.B)
            {
                if (!b.C.IsBreakpoint || b.C.Kind == SearchClauseKind.Exec || b.Cpu != a.Cpu) continue;
                if (b.C.Kind == SearchClauseKind.Read && a.Write || b.C.Kind == SearchClauseKind.Write && !a.Write) continue;
                if (fired[b.Index] && b.Index != anchor) continue;
                if (!Matches(b, keys)) continue;
                if (b.IfExpr != null) { ec.Address = a.Address; ec.Value = a.Value; ec.Source = (long)a.Source; if (!b.IfExpr.IsTrue(ec)) continue; }
                ref var ins = ref last.Of[a.Cpu];
                Fire(b, in ins, a.Cycle, a.Scanline, a.Dot, Ctx.Address(a.Cpu, a.Address) + (Ctx.SymbolOfTarget(in a) is { } sym ? $" ({sym})" : ""), Hex(a.Value),
                     (a.Write ? "write" : "read") + (a.Source != AccessSource.Cpu ? " by " + a.Source + (a.Channel >= 0 ? " ch" + a.Channel : "") : ""));
            }
        }

        private static bool Matches(Bound b, ReadOnlySpan<SpaceKey> keys)
        {
            foreach (var k in keys) foreach (var r in b.Ranges) if (r.Contains(k)) return true;
            return false;
        }

        protected override void Answer(JsonObject j) { }
    }
}
