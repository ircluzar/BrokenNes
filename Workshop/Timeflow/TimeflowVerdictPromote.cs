using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BrokenNes.Workshop.Movies;
using NesEmulator.Debugging.Movies;
using NesEmulator.Debugging.Timeflow;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Timeflow;

/// <summary>
/// T5: a verdict promoted into a project's verify scenarios, by an explicit command only (decision 11: nothing is pinned by itself). The promoted
/// verdict is a file <c>&lt;manifests&gt;\&lt;project&gt;\verdicts\&lt;name&gt;.json</c> beside the project's manifest: the test, the frames it is about, and the
/// pads from power-on to its last frame (the timeline's, a branch's parents' before it). <c>--brokendev verify</c> plays them on the build it verifies
/// (a recording of its own in a scratch folder, removed after) and evaluates the test: a failing verdict fails verify.
/// <code>
///   --timeflow verdict-promote --timeline T --test "TEST" --project P --name ID --why TEXT [--from A] [--to B] [--manifests DIR]
/// </code>
/// </summary>
internal static class TimeflowVerdictPromote
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The pads of frames 0..<paramref name="to"/> of a timeline (its parents' before a branch's first frame) as a verify input script.</summary>
    public static string PadScript(string dir, long to, out string? input2)
    {
        var p1 = new SortedDictionary<long, PadButtons>(); var p2 = new SortedDictionary<long, PadButtons>();
        string? cur = dir; long upTo = to;
        while (cur != null)
        {
            using var rd = new TimelineReader(cur);
            for (long f = rd.StartFrame; f <= Math.Min(upTo, rd.EndFrame - 1); f++) { var row = rd.Row(f); p1.TryAdd(f, (PadButtons)row.P1); p2.TryAdd(f, (PadButtons)row.P2); }
            if (rd.StartFrame == 0)
            {
                if (!rd.Meta.PowerOnStart) throw new InvalidDataException($"timeline {rd.Meta.Id} does not start at power-on: its pads cannot be played from a fresh power-on");
                break;
            }
            if (rd.Meta.ParentId == null) throw new InvalidDataException($"timeline {rd.Meta.Id} starts at frame {rd.StartFrame} and has no parent: the frames before it are not recorded");
            upTo = rd.StartFrame - 1;
            cur = TimeflowStore.Find(rd.Meta.ParentId) ?? throw new FileNotFoundException($"timeline {rd.Meta.Id}'s parent {rd.Meta.ParentId} is gone");
        }
        for (long f = 0; f <= to; f++) if (!p1.ContainsKey(f)) throw new InvalidDataException($"frame {f}'s pads are not recorded");
        string Script(SortedDictionary<long, PadButtons> d)
        {
            var parts = new List<string>(); PadButtons? prev = null;
            foreach (var (f, b) in d) { if (prev == b) continue; parts.Add($"{f.ToString(CultureInfo.InvariantCulture)}:{Movie.ButtonsText(b, "+", "")}"); prev = b; }
            return string.Join(",", parts);
        }
        input2 = p2.Values.Any(b => b != PadButtons.None) ? Script(p2) : null;
        return Script(p1);
    }

    public static JsonObject Promote(string timeline, string test, string manifests, string project, string name, string why, long? from, long? to)
    {
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_.-]+$")) throw new ArgumentException($"--name '{name}' is not an id (letters, digits, - _ .)");
        if (string.IsNullOrWhiteSpace(why)) throw new ArgumentException("--why is needed: what the verdict tests and why it is worth keeping (a promoted test is the user's call, never automatic)");
        var t = VerdictTest.Parse(test);
        string dir = TimeflowStore.Find(timeline) ?? throw new FileNotFoundException($"no timeline '{timeline}' under {TimeflowStore.Root()}");
        string pdir = Path.Combine(manifests, project);
        if (!File.Exists(Path.Combine(pdir, "project.json"))) throw new FileNotFoundException($"no project '{project}' under {manifests}");
        using var rd = new TimelineReader(dir);
        long a = from ?? rd.StartFrame, b = Math.Min(to ?? rd.EndFrame - 1, rd.EndFrame - 1);
        if (b < a || !rd.Has(a)) throw new ArgumentOutOfRangeException(nameof(from), $"frames {a}-{b} are not in timeline {rd.Meta.Id}");
        string script = PadScript(dir, b, out string? script2);
        string file = Path.Combine(pdir, "verdicts", name + ".json");
        if (File.Exists(file)) throw new IOException($"project {project} already has a promoted verdict '{name}' ({file})");
        var j = new JsonObject
        {
            ["id"] = name, ["why"] = why, ["test"] = t.Text, ["from"] = a, ["to"] = b, ["frames"] = b + 1, ["input"] = script, ["input2"] = script2,
            ["promotedFrom"] = new JsonObject { ["timeline"] = rd.Meta.Id, ["rom"] = rd.Meta.RomSha256, ["hotpatch"] = rd.Meta.Hotpatch, ["patches"] = rd.Meta.Patches is { } ps ? new JsonArray(ps.Select(x => (JsonNode)x).ToArray()) : null },
            ["promotedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, j.ToJsonString(Indented));
        j["file"] = file;
        return j;
    }

    /// <summary>
    /// verify's part: every promoted verdict of a project played on <paramref name="romPath"/> (a recording of its own in a scratch folder that is removed
    /// after, never the project's timelines) and evaluated. Null when the project has none.
    /// </summary>
    public static JsonArray? RunPromoted(string projectDir, string console, string romPath, List<string> symbols, string only, out bool ok)
    {
        ok = true;
        string vdir = Path.Combine(projectDir, "verdicts");
        if (!Directory.Exists(vdir)) return null;
        var files = Directory.GetFiles(vdir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0) return null;
        var arr = new JsonArray();
        string? oldRoot = Environment.GetEnvironmentVariable("BROKENDEV_TIMEFLOW");
        string scratch = Path.Combine(Path.GetTempPath(), "brokennes-verdicts-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Environment.SetEnvironmentVariable("BROKENDEV_TIMEFLOW", scratch);
            foreach (var f in files)
            {
                var o = new JsonObject { ["file"] = Path.GetFileName(f) };
                try
                {
                    var j = JsonNode.Parse(File.ReadAllText(f))!.AsObject();
                    string id = j["id"]?.ToString() ?? Path.GetFileNameWithoutExtension(f);
                    o["id"] = id; o["test"] = j["test"]?.ToString(); o["why"] = j["why"]?.ToString();
                    if (only.Length > 0 && !id.Equals(only, StringComparison.OrdinalIgnoreCase)) continue;
                    var t = VerdictTest.Parse(j["test"]?.ToString() ?? "");
                    long from = j["from"]!.GetValue<long>(), to = j["to"]!.GetValue<long>();
                    string tl;
                    using (var m = MovieMachine.Open(romPath, console, "", "fceux", false, true, symbols))
                    {
                        var opt = new TimeflowOptions { Rom = m.RomFile, RomName = m.RomName, Cores = m.Cores, PowerOn = m.PowerOn, Source = "verify", PowerOnStart = true, Note = "brokendev verify: promoted verdict " + id };
                        opt.Coverage = FrameCoverage.Attach(m.Host.Dbg); opt.Accesses = FrameAccesses.Attach(m.Host.Dbg);
                        var rec = TimeflowRecorder.Start(TimeflowMachines.SourceFor(m), opt);
                        var inp = TimeflowInput.Parse(j["input"]?.ToString() ?? "", j["input2"]?.ToString() ?? "", null);
                        for (long fr = 0; fr <= to && rec.Running; fr++) { var (a, b) = inp.At(fr); rec.BeforeFrame(a, b); m.Frame(a, b); rec.AfterFrame(); }
                        tl = rec.Stop();
                    }
                    var v = TimeflowVerdict.Evaluate(t, tl, from, to, TimeflowHotpatching.Open, symbols);
                    o["pass"] = v.Pass; o["verdict"] = v.Pass ? "PASS" : v.Decided ? "FAIL" : "UNDECIDED (a fail)"; o["evidence"] = v.Evidence; o["from"] = from; o["to"] = to;
                    if (!v.Pass) ok = false;
                }
                catch (Exception e) when (e is FormatException or InvalidDataException or IOException or ArgumentException or NullReferenceException or InvalidOperationException or JsonException)
                {
                    o["pass"] = false; o["verdict"] = "FAIL"; o["evidence"] = $"the promoted verdict could not run: {e.Message}"; ok = false;
                }
                arr.Add(o);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("BROKENDEV_TIMEFLOW", oldRoot);
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return arr;
    }
}
