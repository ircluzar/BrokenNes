using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using BrokenNes.Workshop.Movies;
using NesEmulator.Debugging.Timeflow;

namespace BrokenNes.Workshop.Timeflow;

/// <summary>A hotpatch as the CLI, BrokenDev and the MCP server ask for it (the same code everywhere).</summary>
internal sealed class HotpatchArgs
{
    public string Timeline = "";
    public long Frame;
    public long? To;
    public List<string> Patches = new();
    public string NewRom = "";
    public List<string> Symbols = new(), NewSymbols = new();
    public List<string> Safe = new();
    public int Scan = 600;
    public bool AtSafe;
    public bool Branch = true, Index = true, Divergence = true, VerdictOnOld = true;
    public List<string> Verdicts = new();
    public string? Build;
}

/// <summary>
/// T5's hotpatch and resume on Workshop machines (docs/projects/brokendev-timeflow-plan.md, "T5 as built"): the CLI verbs and the calls the
/// MCP server's <c>timeflow_patch</c>, <c>timeflow_resume</c> (with a patched ROM) and <c>timeflow_verdict</c> make. Every run is on NEW headless
/// machines of the timeline's console and cores (the old build and the patched one); nothing of the caller's sessions is touched.
/// <code>
///   --timeflow patch          --timeline T --frame F --patch "AT=OLD>NEW"... [--to B] [--sym F]... [--verdict "TEST"]... [--no-branch] [--no-old] [--json]
///   --timeflow rebuild-resume --timeline T --frame F --rom NEW [--sym OLD.sym]... --sym-new NEW.sym... [--safe f1,f2] [--scan N] [--at-safe]
///                             [--to B] [--verdict "TEST"]... [--build TEXT] [--no-branch] [--json]
///   --timeflow diverge        --timeline T --frame F (--patch ... | --rom NEW --sym-new F) [--to B] [--sym F]... [--json]     the divergence only (no branch)
///   --timeflow diverge        --timeline BRANCH [--sym F]... [--sym-new F]... [--json]          a recorded hotpatch branch against its parent
///   --timeflow verdict        --timeline T --test "TEST"... [--from A] [--to B] [--sym F]... [--json]   a written test over a span of any timeline
/// </code>
/// Patches: <see cref="RomPatch.Help"/>. Tests: <see cref="VerdictTest.Help"/>. Exit 0 done (verdicts: all pass), 1 refused or a verdict failed, 2 usage, 3 cannot run.
/// </summary>
internal static class TimeflowHotpatching
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The Workshop's opener: the movie machine of a timeline's console, cores, power-on and timing on a ROM (the timeline's own, or a patched / rebuilt one), with the debugger and these symbols.</summary>
    public static HotpatchMachine Open(string dir, TimelineMeta meta, byte[]? romFile, IReadOnlyList<string> symbols)
    {
        MovieMachine m;
        if (romFile == null) m = TimeflowMachines.OpenFor(dir, meta, true, symbols);
        else
        {
            string sha = TimeflowStore.Sha256(romFile);
            string tmp = Path.Combine(Path.GetTempPath(), "brokennes-timeflow-" + Environment.ProcessId, sha[..16], meta.RomName.Length > 0 ? meta.RomName : "rom" + meta.RomExt);
            Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
            if (!File.Exists(tmp)) File.WriteAllBytes(tmp, romFile);
            m = MovieMachine.Open(tmp, meta.Console, meta.Cores, meta.Console == "nes" ? meta.PowerOn : "fceux", meta.Ntsc, true, symbols);
        }
        if (!m.HasDebugger) { m.Dispose(); throw new NotSupportedException($"a hotpatch needs the debugger on the {meta.Console}'s CPU"); }
        return new HotpatchMachine
        {
            Machine = m.Host.Machine, Debugger = m.Host.Dbg, Source = TimeflowMachines.SourceFor(m), RunFrame = (a, b) => m.Frame(a, b), Port = m.Port,
            Game = (object?)m.Host.Nes ?? m.Host.Session!, RomFile = m.RomFile, RomSha256 = m.RomSha256, OnDispose = m.Dispose,
        };
    }

    public static HotpatchRequest Request(HotpatchArgs x)
    {
        string dir = TimeflowStore.Find(x.Timeline) ?? throw new FileNotFoundException($"no timeline '{x.Timeline}' under {TimeflowStore.Root()}");
        var q = new HotpatchRequest
        {
            TimelineDir = dir, Frame = x.Frame, To = x.To, Patches = x.Patches.Select(RomPatch.Parse).ToList(), Symbols = x.Symbols.ToList(),
            NewSymbols = x.NewSymbols.Count > 0 ? x.NewSymbols.ToList() : x.Symbols.ToList(), SafeFunctions = x.Safe.ToList(), SafeScanFrames = x.Scan, ResumeAtSafePoint = x.AtSafe,
            Branch = x.Branch, Index = x.Index, Divergence = x.Divergence, VerdictOnOld = x.VerdictOnOld, Verdicts = x.Verdicts.Select(VerdictTest.Parse).ToList(),
            Build = x.Build, Open = Open,
        };
        if (x.NewRom.Length > 0)
        {
            if (!File.Exists(x.NewRom)) throw new FileNotFoundException($"no ROM at {x.NewRom}");
            q.NewRom = File.ReadAllBytes(x.NewRom); q.NewRomName = Path.GetFileName(x.NewRom);
            if (x.NewSymbols.Count == 0) throw new ArgumentException("a rebuild-resume needs the new build's symbols (--sym-new / symbols_new)");
        }
        return q;
    }

    public static (HotpatchReport Report, JsonObject Json) Run(HotpatchArgs x)
    {
        var q = Request(x);
        var rep = TimeflowHotpatch.Run(q);
        // the branch's ROM keeps its symbols beside it (a byte-patched ROM has the old build's, a rebuild the new one's): later jumps, profiles and verdicts on the branch name things
        if (rep.BranchDir != null && rep.PatchedRomSha256.Length > 0)
        {
            var syms = q.NewRom != null ? q.NewSymbols : q.Symbols;
            if (syms.Count > 0) KeepSymbols(TimeflowStore.GameDirOf(rep.BranchDir), rep.PatchedRomSha256, syms);
        }
        return (rep, rep.ToJson());
    }

    /// <summary>The symbol files kept for a build in a game folder (<c>symbols\&lt;sha256&gt;\</c>), or none.</summary>
    public static List<string> KeptSymbols(string timelineDir, string romSha256)
    {
        string d = Path.Combine(TimeflowStore.GameDirOf(timelineDir), "symbols", romSha256);
        return Directory.Exists(d) ? Directory.GetFiles(d).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
    }

    /// <summary>Keeps a build's symbol files in its game folder (<c>symbols\&lt;sha256&gt;\</c>, the files' own names): the old build's are needed after a rebuild overwrote the published ones.</summary>
    public static List<string> KeepSymbols(string gameDir, string romSha256, IEnumerable<string> files)
    {
        string d = Path.Combine(gameDir, "symbols", romSha256);
        Directory.CreateDirectory(d);
        var kept = new List<string>();
        foreach (var f in files.Where(File.Exists))
        {
            string to = Path.Combine(d, Path.GetFileName(f));
            if (!string.Equals(Path.GetFullPath(f), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase)) File.Copy(f, to, true);
            kept.Add(to);
        }
        return kept;
    }

    /// <summary>A written test over a span of a timeline (any: a hotpatch branch, its parent, a recording).</summary>
    public static (List<VerdictResult> Results, JsonObject Json) Verdict(string timeline, List<string> tests, long? from, long? to, List<string> symbols)
    {
        string dir = TimeflowStore.Find(timeline) ?? throw new FileNotFoundException($"no timeline '{timeline}' under {TimeflowStore.Root()}");
        if (tests.Count == 0) throw new ArgumentException("a verdict needs a test (" + VerdictTest.Help + ")");
        var parsed = tests.Select(VerdictTest.Parse).ToList();
        using var rd = new TimelineReader(dir);
        long a = from ?? rd.StartFrame, b = Math.Min(to ?? rd.EndFrame - 1, rd.EndFrame - 1);
        if (!rd.Has(a) || b < a) throw new ArgumentOutOfRangeException(nameof(from), $"frames {a}-{b} are not in timeline {rd.Meta.Id} (it holds frames {rd.StartFrame}-{rd.EndFrame - 1})");
        var res = parsed.Select(t => TimeflowVerdict.Evaluate(t, dir, a, b, Open, symbols)).ToList();
        var j = new JsonObject
        {
            ["timeline"] = rd.Meta.Id, ["from"] = a, ["to"] = b, ["pass"] = res.All(r => r.Pass),
            ["summary"] = $"{res.Count(r => r.Pass)} of {res.Count} verdict(s) pass over frames {a}-{b}",
            ["verdicts"] = new JsonArray(res.Select(r => (JsonNode)r.ToJson()).ToArray()),
        };
        return (res, j);
    }

    /// <summary>A recorded hotpatch branch against its parent: the recordings' hashes, then (both machines in lockstep) the first instruction.</summary>
    public static JsonObject DivergeRecorded(string branch, List<string> symbols, List<string> newSymbols)
    {
        string dir = TimeflowStore.Find(branch) ?? throw new FileNotFoundException($"no timeline '{branch}' under {TimeflowStore.Root()}");
        var j = TimeflowHotpatch.CompareRecorded(dir);
        j["lockstep"] = TimeflowHotpatch.DivergeRecorded(dir, Open, symbols, newSymbols.Count > 0 ? newSymbols : symbols);
        return j;
    }

    public static int Cli(string verb, Func<string, string, string> Opt, Func<string, List<string>> All, Func<string, bool> Has, bool json)
    {
        long L(string n) => long.Parse(Opt(n, "") is { Length: > 0 } v ? v : throw new ArgumentException($"--{n} is needed"), CultureInfo.InvariantCulture);
        long? LO(string n) => long.TryParse(Opt(n, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : null;
        if (verb == "verdict")
        {
            var (res, j) = Verdict(Opt("timeline", ""), All("test"), LO("from"), LO("to"), All("sym"));
            Console.WriteLine(json ? j.ToJsonString(Indented) : HumanVerdicts(j));
            return res.All(r => r.Pass) ? 0 : 1;
        }
        if (verb == "diverge" && Opt("frame", "").Length == 0)
        {
            var j = DivergeRecorded(Opt("timeline", ""), All("sym"), All("sym-new"));
            Console.WriteLine(json ? j.ToJsonString(Indented) : j["summary"] + "\n  lockstep: " + j["lockstep"]?["summary"]);
            return 0;
        }
        var x = new HotpatchArgs
        {
            Timeline = Opt("timeline", ""), Frame = L("frame"), To = LO("to"), Patches = All("patch"), NewRom = Opt("rom", ""), Symbols = All("sym"), NewSymbols = All("sym-new"),
            Safe = Opt("safe", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(), AtSafe = Has("at-safe"),
            Branch = !Has("no-branch") && verb != "diverge", VerdictOnOld = !Has("no-old"), Verdicts = verb == "diverge" ? new() : All("verdict"), Build = Opt("build", "") is { Length: > 0 } bd ? bd : null,
        };
        if (int.TryParse(Opt("scan", ""), out int sc)) x.Scan = sc;
        if (verb == "patch" && x.Patches.Count == 0) throw new ArgumentException("--patch \"AT=OLD>NEW\" is needed (" + RomPatch.Help + ")");
        if (verb == "rebuild-resume" && x.NewRom.Length == 0) throw new ArgumentException("--rom NEW (the new build) is needed");
        var (rep, json2) = Run(x);
        Console.WriteLine(json ? json2.ToJsonString(Indented) : Human(rep));
        if (rep.Refused != null) return 1;
        return rep.Verdicts.Count == 0 || rep.VerdictsPass ? 0 : 1;
    }

    public static string Human(HotpatchReport r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"hotpatch ({r.Mode}) of timeline {r.TimelineId} at frame {r.Frame}, replayed to {r.To}");
        foreach (var a in r.Applied) sb.AppendLine($"  patch {a.Text}: {a.Resolved}, file offset {a.FileOffset}: {a.Old} -> {a.New}");
        if (r.Check != null) sb.AppendLine($"  symbol-map check at frame {r.Check.Frame}: {(r.Check.Safe ? "safe" : "NOT safe: " + r.Check.Why)}");
        if (r.SafePoint != null) sb.AppendLine($"  nearest safe resume point: frame {r.SafePoint.Frame} (PC in {r.SafePoint.Function ?? "?"})");
        if (r.Refused != null) { sb.AppendLine("  REFUSED: " + r.Refused); return sb.ToString().TrimEnd(); }
        sb.AppendLine($"  resumed at frame {r.ResumedAt}; {r.FramesReplayed} frame(s) replayed with the recorded pads{(r.Branch != null ? $"; recorded as branch {r.Branch}" : "")}");
        if (r.Divergence is { } d) sb.AppendLine("  divergence: " + d["summary"]);
        foreach (var v in r.Verdicts)
            sb.AppendLine($"  verdict '{v.Test}': {(v.Pass ? "PASS" : v.Decided ? "FAIL" : "UNDECIDED")} ({v.Evidence}){(v.Old != null ? $"; on the old future: {(v.Old.Pass ? "PASS" : v.Old.Decided ? "FAIL" : "UNDECIDED")} ({v.Old.Evidence})" : "")}");
        if (r.Verdicts.Count > 0) sb.AppendLine("  " + r.VerdictSummary);
        foreach (var n in r.Notes) sb.AppendLine("  note: " + n);
        return sb.ToString().TrimEnd();
    }

    private static string HumanVerdicts(JsonObject j)
    {
        var sb = new System.Text.StringBuilder($"verdicts over timeline {j["timeline"]} frames {j["from"]}-{j["to"]}: {j["summary"]}\n");
        foreach (var v in j["verdicts"]!.AsArray()) sb.AppendLine($"  {v!["verdict"]}  {v["test"]}: {v["evidence"]}");
        return sb.ToString().TrimEnd();
    }
}
