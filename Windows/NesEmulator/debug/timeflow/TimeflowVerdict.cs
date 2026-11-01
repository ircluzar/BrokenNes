using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NesEmulator.Debugging.Timeflow;

// =====================================================================================================================================
//  T5: the verdict (decision 10 of docs/projects/brokendev-timeflow-plan.md): a test the agent or the user writes, evaluated over a span of a
//  timeline (after a hotpatch: over the recorded branch, the patched future). Forms:
//    never <T4 condition>          no frame of the span matches        ("never write hp by fill_table")
//    sometime <T4 condition>       at least one frame matches          (also "must match", "ever")
//    always <T4 condition>         every frame matches (a frame-level condition: no breakpoint anchor)
//    query <T3 query> expect <field> <op> <value>     a profiled re-run's answer ("query writes hp expect total == 0")
//  The answer is pass or fail with the evidence (the hits, the first frame that failed, the query's field); a span that could not be decided
//  (the re-run went out of sync, the timeline was skipped) is a fail that says so, never a pass. No verdict is ever kept as a test by itself:
//  promoting one into a project's verify scenarios is an explicit command (no automatic regression pinning, decision 11).
// =====================================================================================================================================

public enum VerdictKind { Never, Sometime, Always, Query }

/// <summary>A written test.</summary>
public sealed class VerdictTest
{
    public string Text = "";
    public VerdictKind Kind;
    public string Condition = "";
    public string Query = "", Field = "", Op = "==", Value = "";

    public static string Help => "never <condition> | sometime <condition> (must match) | always <condition> | query <T3 query> expect <field> <op> <value> (op: == != < <= > >=, value a number or null)";

    public static VerdictTest Parse(string text)
    {
        text = (text ?? "").Trim();
        var t = new VerdictTest { Text = text };
        var m = Regex.Match(text, @"^(must[\s-]+never|never)\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (m.Success) { t.Kind = VerdictKind.Never; t.Condition = m.Groups[2].Value.Trim(); SearchCondition.Parse(t.Condition); return t; }
        m = Regex.Match(text, @"^(must[\s-]+match|sometime|sometimes|ever)\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (m.Success) { t.Kind = VerdictKind.Sometime; t.Condition = m.Groups[2].Value.Trim(); SearchCondition.Parse(t.Condition); return t; }
        m = Regex.Match(text, @"^(must[\s-]+always|always)\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (m.Success)
        {
            t.Kind = VerdictKind.Always; t.Condition = m.Groups[2].Value.Trim();
            if (SearchCondition.Parse(t.Condition).Anchor != null) throw new FormatException($"'{text}': always needs a frame-level condition (state, picture, sound, a breakpoint under not): a breakpoint's firings are not frames");
            return t;
        }
        m = Regex.Match(text, @"^query\s+(.+?)\s+expect\s+(\S+)\s*(==|!=|<=|>=|<|>)\s*(\S+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (m.Success)
        {
            t.Kind = VerdictKind.Query; t.Query = m.Groups[1].Value.Trim(); t.Field = m.Groups[2].Value; t.Op = m.Groups[3].Value; t.Value = m.Groups[4].Value;
            ProfileQuery.Parse(t.Query);
            return t;
        }
        throw new FormatException($"'{text}' is not a verdict ({Help})");
    }

    public override string ToString() => Text;
}

/// <summary>A verdict's answer over one span.</summary>
public sealed class VerdictResult
{
    public string Test = "", Timeline = "";
    public long From, To;
    public bool Pass;
    public bool Decided = true;
    public string Evidence = "";
    public JsonNode? Detail;
    /// <summary>The same test over the old future (the timeline the patch left), when asked: the comparison the fix is judged by.</summary>
    public VerdictResult? Old;

    public JsonObject ToJson() => new()
    {
        ["test"] = Test, ["timeline"] = Timeline, ["from"] = From, ["to"] = To, ["pass"] = Pass, ["verdict"] = Pass ? "PASS" : Decided ? "FAIL" : "UNDECIDED (a fail)",
        ["evidence"] = Evidence, ["detail"] = Detail?.DeepClone(), ["old"] = Old?.ToJson(),
    };
}

public static class TimeflowVerdict
{
    /// <summary>Evaluates a test over frames <paramref name="from"/>-<paramref name="to"/> of a timeline (the machines opened with <paramref name="open"/>, these symbols).</summary>
    public static VerdictResult Evaluate(VerdictTest t, string timelineDir, long from, long to, HotpatchOpener open, IReadOnlyList<string> symbols)
    {
        var meta = TimelineMeta.Load(timelineDir);
        var v = new VerdictResult { Test = t.Text, Timeline = meta.Id, From = from, To = to };
        if (t.Kind == VerdictKind.Query) return Query(t, v, timelineDir, from, to, open, symbols);
        var cond = SearchCondition.Parse(t.Condition);
        int max = t.Kind switch { VerdictKind.Never => 5, VerdictKind.Sometime => 1, _ => 1_000_000 };
        var rep = TimeflowSearch.Run(new SearchRequestCore
        {
            Condition = cond, TimelineDirs = new() { timelineDir }, Symbols = symbols.ToList(), MaxHits = max, From = from, To = to,
            Open = (d, m, s) => open(d, m, null, s).ForSearch(),
        });
        var tr = rep.Timelines.FirstOrDefault();
        var hits = rep.Hits;
        var detail = new JsonObject
        {
            ["condition"] = t.Condition, ["hits"] = rep.TotalHits, ["candidates"] = rep.Candidates, ["reRunFrames"] = rep.ReRunFrames,
            ["first"] = new JsonArray(hits.Take(5).Select(h => (JsonNode)HitText(h)).ToArray()),
        };
        v.Detail = detail;
        if (tr?.Skipped != null) { v.Decided = false; v.Pass = false; v.Evidence = $"undecided: the timeline was skipped: {tr.Skipped}"; return v; }
        if (tr?.OffFrame is long off && (t.Kind != VerdictKind.Sometime || hits.Count == 0))
        {
            v.Decided = false; v.Pass = false;
            v.Evidence = $"undecided: the re-run went out of sync at frame {off} ({tr.OffWhat}): the frames after it are not the recorded game";
            return v;
        }
        long frames = to - from + 1;
        switch (t.Kind)
        {
            case VerdictKind.Never:
                v.Pass = rep.TotalHits == 0;
                v.Evidence = v.Pass ? $"0 hits in frames {from}-{to} ({frames} frames; {rep.Candidates} candidate(s) re-run as needed: {rep.ReRunFrames} frames)" :
                             $"{(rep.HitLimitReached ? "at least " : "")}{rep.TotalHits} hit(s); the first: {HitText(hits[0])}";
                break;
            case VerdictKind.Sometime:
                v.Pass = rep.TotalHits > 0;
                v.Evidence = v.Pass ? $"matched: {HitText(hits[0])}" : $"0 hits in frames {from}-{to} ({frames} frames)";
                break;
            case VerdictKind.Always:
            {
                var hitFrames = hits.Select(h => h.Frame).ToHashSet();
                long firstMiss = -1;
                for (long f = from; f <= to; f++) if (!hitFrames.Contains(f)) { firstMiss = f; break; }
                v.Pass = firstMiss < 0;
                v.Evidence = v.Pass ? $"every one of the {frames} frames {from}-{to} matches" : $"frame {firstMiss} does not match ({frames - hitFrames.Count(f => f >= from && f <= to)} of {frames} frames do not)";
                break;
            }
        }
        return v;
    }

    private static string HitText(SearchHit h) =>
        $"frame {h.Frame}{(h.Pc != null ? $" pc {h.Pc}" : "")}{(h.Instruction != null ? $" {h.Instruction}" : "")}{(h.In != null ? $" in {h.In}" : "")}{(h.Address != null ? $" {h.Kind} {h.Address} = {h.Value}" : "")}" +
        (h.Stack.Count > 0 ? $" [{string.Join(" | ", h.Stack.Take(2))}]" : "") + (h.Context.Count > 0 ? $" ({string.Join("; ", h.Context.Take(2))})" : "");

    private static double? Number(string s)
    {
        if (s.StartsWith("$") && long.TryParse(s[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long h)) return h;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long h2)) return h2;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null;
    }

    private static VerdictResult Query(VerdictTest t, VerdictResult v, string dir, long from, long to, HotpatchOpener open, IReadOnlyList<string> symbols)
    {
        using var pb = new TimeflowPlayback(dir);
        using var m = open(dir, pb.Meta, null, symbols);
        var q = ProfileQuery.Parse(t.Query);
        var r = TimeflowProfiler.Run(pb, m.Target(poweredOn: true), new ProfileSpan { From = from, To = to, Vars = null, StopWhenOff = true }, new ProfileSink[] { q });
        var res = q.Result();
        if (r.OffFrame is long off) { v.Decided = false; v.Pass = false; v.Evidence = $"undecided: the profiled re-run went out of sync at frame {off} ({r.OffWhat})"; v.Detail = res; return v; }
        if (res["error"] is { } err) { v.Decided = false; v.Pass = false; v.Evidence = $"undecided: the query failed: {err}"; v.Detail = res; return v; }
        JsonNode? node = res;
        foreach (var part in t.Field.Split('.'))
        {
            if (node is JsonObject o) node = o[part];
            else if (node is JsonArray a && int.TryParse(part, out int i)) node = i >= 0 && i < a.Count ? a[i] : null;
            else node = null;
            if (node == null) break;
        }
        bool ok;
        string got = node?.ToJsonString() ?? "null";
        if (t.Value.Equals("null", StringComparison.OrdinalIgnoreCase)) ok = t.Op == "==" ? node == null : t.Op == "!=" && node != null;
        else if (node is JsonValue jv && double.TryParse(jv.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double x) && Number(t.Value) is double y)
            ok = t.Op switch { "==" => x == y, "!=" => x != y, "<" => x < y, "<=" => x <= y, ">" => x > y, ">=" => x >= y, _ => false };
        else if (node == null) ok = t.Op == "!=";
        else ok = t.Op == "==" ? node.ToString() == t.Value : t.Op == "!=" && node.ToString() != t.Value;
        v.Pass = ok;
        v.Evidence = $"{t.Field} = {got} ({(ok ? "holds" : "does not hold")}: {t.Op} {t.Value}) over frames {from}-{to}";
        v.Detail = res;
        return v;
    }
}
