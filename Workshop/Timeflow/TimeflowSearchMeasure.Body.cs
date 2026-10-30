using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using BrokenNes.Workshop.Debug;
using BrokenNes.Workshop.Movies;
using NesEmulator.Debugging;
using NesEmulator.Debugging.Checks;
using NesEmulator.Debugging.Timeflow;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Timeflow;

/// <summary>
/// <c>--timeflow search-measure [--timelines K] [--frames M] [--real] [--json OUT]</c>: T4's measurements (docs/projects/brokendev-timeflow-plan.md,
/// "T4 as built"): a corpus of K timelines of M frames of the search test game (full index: coverage, access counts, checks) in a scratch
/// BROKENDEV_HOME; every condition kind searched over all of it: the coarse scan's speed, the candidates and false candidates per kind, the
/// re-runs, the time to the first hit and to the end. With <c>--real</c> the user's games too (VRUN, Super VRUN when on this machine): their
/// distinct pictures (what a picture search re-renders), the cost of the access counts while recording, and what a per-frame colour summary
/// in the index would cost and save (the plan's open question 2).
/// </summary>
internal static partial class TimeflowSearchMeasure
{
    private static int Measure(Func<string, string, string> Opt, Func<string, List<string>> All, Func<string, bool> Has, bool json)
    {
        int k = int.TryParse(Opt("timelines", ""), out int kk) ? kk : 20;
        long n = long.TryParse(Opt("frames", ""), out long nn) ? nn : 3600;
        string scratch = Path.Combine(Path.GetTempPath(), "brokennes-search-measure-" + Environment.ProcessId);
        string? oldHome = Environment.GetEnvironmentVariable("BROKENDEV_HOME");
        Environment.SetEnvironmentVariable("BROKENDEV_HOME", Path.Combine(scratch, "home"));
        var j = new JsonObject { ["timelines"] = k, ["framesEach"] = n };
        try
        {
            Directory.CreateDirectory(scratch);
            var (rom, _, mlb) = TimeflowSearchRoms.EnemyGame();
            string romPath = DebugTestRoms.Write(scratch, "enemy.nes", rom), mlbPath = DebugTestRoms.Write(scratch, "enemy.mlb", mlb);
            var sw = Stopwatch.StartNew();
            var dirs = new List<string>();
            double recFps = 0;
            for (int i = 0; i < k; i++)
            {
                var t0 = Stopwatch.StartNew();
                dirs.Add(Record(romPath, mlbPath, n, coverage: true, accesses: true, checks: true, skipFrames: i * 7));
                recFps += n / t0.Elapsed.TotalSeconds;
            }
            long corpusBytes = dirs.Sum(TimeflowStore.DirBytes);
            j["corpus"] = new JsonObject
            {
                ["frames"] = k * n, ["hoursOfPlay"] = Math.Round(k * n / 60.0988 / 3600, 2), ["bytes"] = corpusBytes, ["bytesPerFrame"] = Math.Round(corpusBytes / (double)(k * n), 1),
                ["recordFpsWithFullIndex"] = Math.Round(recFps / k), ["recordSeconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1),
            };
            Console.Error.WriteLine($"corpus: {k} x {n} frames recorded in {sw.Elapsed.TotalSeconds:0} s");
            string red = "#982220";
            var conds = new (string Kind, string Cond)[]
            {
                ("code", "exec hide_enemy"), ("code, never", "exec never_called"), ("code with if", "exec hide_enemy if [enemy_alive] == 0"),
                ("memory", "write bug_mark if value == $5A"), ("memory, same value", "write same_byte"), ("memory read", "read secret"), ("memory, never", "write never"),
                ("state", "state [enemy_alive] == 1 && [enemy_y] == $F0"), ("state, never", "state [enemy_alive] == 7"),
                ("finding", "finding ram-uninit"), ("picture changed", "picture changed"), ("colour", $"color {red}"), ("colour, never", "color #123456"),
                ("state + picture (the user's example)", $"state [enemy_alive] != 0 and not color {red}"), ("sound", "sound above 0.05"),
                ("channel", "channel \"Pulse 1\" playing"), ("breakpoint + state", "write bug_mark and state [enemy_alive] == 0"),
            };
            var rows = new JsonArray();
            foreach (var (kind, cond) in conds)
            {
                // the coarse phase over the whole corpus; the exact phase over two timelines (its rates are per frame; the whole corpus would take long)
                var co = TimeflowSearching.Run(new SearchRequest { Condition = cond, Timelines = dirs, Symbols = new() { mlbPath }, MaxHits = 1_000_000, CoarseOnly = true }).Report;
                var use = dirs.Take(kind is "sound" or "channel" ? 1 : 2).ToList();
                var r = TimeflowSearching.Run(new SearchRequest { Condition = cond, Timelines = use, Symbols = new() { mlbPath }, MaxHits = 1_000_000 }).Report;
                var first = TimeflowSearching.Run(new SearchRequest { Condition = cond, Timelines = dirs, Symbols = new() { mlbPath }, MaxHits = 1 }).Report;
                var row = new JsonObject
                {
                    ["kind"] = kind, ["condition"] = cond, ["corpusFrames"] = co.CoarseFrames, ["corpusCoarseFps"] = Math.Round(co.CoarseFps), ["corpusCoarseMs"] = Math.Round(co.CoarseMs),
                    ["corpusCandidates"] = co.Candidates, ["corpusIndexHits"] = co.TotalHits,
                    ["exactTimelines"] = use.Count, ["frames"] = r.CoarseFrames, ["coarseFps"] = Math.Round(r.CoarseFps), ["candidates"] = r.Candidates,
                    ["candidateShare"] = Math.Round(r.Candidates / (double)Math.Max(1, r.CoarseFrames), 4), ["hits"] = r.TotalHits, ["reRunFrames"] = r.ReRunFrames,
                    ["reRunFps"] = Math.Round(r.ReRunFps, 1), ["totalMs"] = Math.Round(r.TotalMs), ["firstHitMs"] = first.FirstHitMs is double fh ? Math.Round(fh) : null,
                    ["coarseMissed"] = r.Timelines.Sum(t => t.CoarseMissed), ["contradicted"] = r.Clauses.Sum(c => c.Contradicted),
                    ["clauses"] = new JsonArray(r.Clauses.Select(c => (JsonNode)new JsonObject { ["clause"] = c.Text, ["coarse"] = c.Coarse, ["maybeChecked"] = c.MaybeChecked, ["falseCandidateRate"] = Math.Round(c.FalseCandidateRate, 4) }).ToArray()),
                };
                rows.Add(row);
                Console.Error.WriteLine($"{kind,-38} corpus {co.CoarseFrames} frames coarse at {co.CoarseFps:0} fps ({co.CoarseMs:0} ms) -> {co.Candidates} candidates; exact over {use.Count}: {r.CoarseFrames,7} frames at {r.CoarseFps,9:0} fps -> {r.Candidates,6} candidates, {r.TotalHits,6} hits, {r.ReRunFrames,6} re-run, " +
                                        $"false {string.Join("/", r.Clauses.Select(c => c.FalseCandidateRate.ToString("P0")))}, first hit {first.FirstHitMs:0} ms, total {r.TotalMs:0} ms");
            }
            j["searches"] = rows;
            if (Has("real")) j["real"] = Real(scratch, int.TryParse(Opt("real-frames", ""), out int rf) ? rf : 3600);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BROKENDEV_HOME", oldHome);
            if (!Has("keep")) try { Directory.Delete(scratch, true); } catch (IOException) { }
        }
        string text = j.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (Opt("json", "") is { Length: > 0 } outFile && !outFile.StartsWith("--")) File.WriteAllText(outFile, text);
        Console.WriteLine(text);
        return 0;
    }

    private static string Record(string rom, string? mlb, long frames, bool coverage, bool accesses, bool checks, int skipFrames = 0, string? console = null, string input = "auto:1")
    {
        using var m = MovieMachine.Open(rom, console, "", "fceux", false, true, mlb != null ? new[] { mlb } : null);
        var o = new TimeflowOptions { Rom = m.RomFile, RomName = m.RomName, Cores = m.Cores, PowerOn = m.PowerOn, Source = "measure", PowerOnStart = skipFrames == 0 };
        // a timeline that starts later (another recording of the same game): the frames before it are played, not recorded
        var inp = TimeflowInput.Parse(input, "", null);
        for (long f = 0; f < skipFrames; f++) { var (a, b) = inp.At(f); m.Frame(a, b); }
        FrameCoverage? cov = null; FrameAccesses? acc = null;
        if (coverage && m.HasDebugger) { cov = FrameCoverage.Attach(m.Host.Dbg); o.Coverage = cov; }
        if (accesses && m.HasDebugger) { acc = FrameAccesses.Attach(m.Host.Dbg); o.Accesses = acc; }
        if (checks && m.HasDebugger) o.Checks = CheckRun.Attach(m.Host.Dbg, new CheckOptions { Profile = false });
        var rec = TimeflowRecorder.Start(TimeflowMachines.SourceFor(m), o);
        for (long f = skipFrames; f < skipFrames + frames && rec.Running; f++) { var (a, b) = inp.At(f); rec.BeforeFrame(a, b); m.Frame(a, b); rec.AfterFrame(); }
        string dir = rec.Stop();
        cov?.Detach(); acc?.Detach();
        return dir;
    }

    /// <summary>The user's games: the picture question (distinct pictures, a colour summary's cost and gain) and the access counts' recording cost.</summary>
    private static JsonArray Real(string scratch, int frames)
    {
        string env = @"P:\BrokenDev\env";
        var games = new[] { ("VRUN (NES)", Path.Combine(env, "out", "vrun-nes", "vrun_game.nes"), "nes"), ("Super VRUN (SNES)", Path.Combine(env, "out", "supervrun", "supervrun.sfc"), "snes") };
        var a = new JsonArray();
        foreach (var (name, path, console) in games)
        {
            if (!File.Exists(path)) { a.Add(new JsonObject { ["game"] = name, ["notRun"] = $"not on this machine ({path})" }); continue; }
            var o = new JsonObject { ["game"] = name };
            // the recording's cost: plain, with coverage, with coverage and access counts
            foreach (var (label, cov, acc) in new[] { ("plain", false, false), ("coverage", true, false), ("coverageAndAccesses", true, true) })
            {
                var sw = Stopwatch.StartNew();
                string d = Record(path, null, frames, cov, acc, false, console: console);
                o["recordFps_" + label] = Math.Round(frames / sw.Elapsed.TotalSeconds, 1);
                o["bytesPerFrame_" + label] = Math.Round(TimeflowStore.DirBytes(d) / (double)frames, 1);
                if (label != "coverageAndAccesses") Directory.Delete(d, true);
                else o["timeline"] = d;
            }
            string tdir = (string)o["timeline"]!;
            using var rd = new TimelineReader(tdir);
            var pics = Enumerable.Range(0, (int)rd.Frames).Select(f => rd.Row(f).Picture).ToList();
            int distinct = pics.Distinct().Count();
            o["frames"] = rd.Frames; o["distinctPictures"] = distinct; o["distinctShare"] = Math.Round(distinct / (double)rd.Frames, 3);
            o["picturesChangedShare"] = Math.Round(Enumerable.Range(1, pics.Count - 1).Count(i => pics[i] != pics[i - 1]) / (double)rd.Frames, 3);
            // a colour that is on screen part of the time: the colour summary's gain is the frames it would rule out
            var (colour, framesWith, summaryUs, summaryBytes) = ColourStudy(tdir, rd, console);
            var r = TimeflowSearching.Run(new SearchRequest { Condition = $"color #{colour:X6}", Timelines = new() { tdir }, MaxHits = 1_000_000 }).Report;
            o["colourSearch"] = new JsonObject
            {
                ["condition"] = $"color #{colour:X6}", ["hits"] = r.TotalHits, ["framesWithColour"] = framesWith, ["reRunFrames"] = r.ReRunFrames, ["totalMs"] = Math.Round(r.TotalMs),
                ["msPerDistinctPicture"] = Math.Round(r.ExactMs / Math.Max(1, distinct), 2),
                ["summaryWouldRuleOut"] = rd.Frames - r.TotalHits, ["summaryMicrosecondsPerFrameRecording"] = Math.Round(summaryUs, 1), ["summaryBytesPerFrame"] = summaryBytes,
            };
            var s = TimeflowSearching.Run(new SearchRequest { Condition = "sound silent", Timelines = new() { tdir }, MaxHits = 1_000_000, From = 0, To = Math.Min(rd.EndFrame - 1, 599) }).Report;
            o["soundSearch600"] = new JsonObject { ["hits"] = s.TotalHits, ["reRunFrames"] = s.ReRunFrames, ["totalMs"] = Math.Round(s.TotalMs), ["reRunFps"] = Math.Round(s.ReRunFps, 1) };
            var c = TimeflowSearching.Run(new SearchRequest { Condition = "picture changed", Timelines = new() { tdir }, MaxHits = 1_000_000 }).Report;
            o["pictureChanged"] = new JsonObject { ["hits"] = c.TotalHits, ["coarseFps"] = Math.Round(c.CoarseFps), ["totalMs"] = Math.Round(c.TotalMs) };
            a.Add(o);
            Console.Error.WriteLine($"{name}: {o.ToJsonString()}");
        }
        return a;
    }

    /// <summary>
    /// Re-renders every frame of a timeline once (a replay): picks a colour on screen in about half the frames, counts the frames with it, and
    /// times what a per-frame colour summary (a 256-bit set of the frame's colours, hashed) would cost to make while recording.
    /// </summary>
    private static (uint Colour, long FramesWith, double SummaryUs, int SummaryBytes) ColourStudy(string tdir, TimelineReader rd, string console)
    {
        using var pb = new TimeflowPlayback(tdir);
        var sm = TimeflowSearching.OpenMachine(tdir, pb.Meta, Array.Empty<string>());
        try
        {
            pb.Jump(sm.Source, rd.StartFrame);
            var perFrame = new List<HashSet<uint>>();
            var sw = new Stopwatch();
            var bits = new ulong[4];
            for (long f = rd.StartFrame; f < rd.EndFrame; f++)
            {
                var (p1, p2) = rd.Pads(f);
                sm.RunFrame(p1, p2);
                var (px, w, h) = sm.Picture();
                sw.Start();
                Array.Clear(bits);
                for (int i = 0; i < w * h; i++) { uint c = px[i] & 0xFFFFFF; uint x = (c * 2654435761u) >> 24; bits[x >> 6] |= 1UL << (int)(x & 63); }
                sw.Stop();
                var set = new HashSet<uint>(); for (int i = 0; i < w * h; i += 7) set.Add(px[i] & 0xFFFFFF);
                perFrame.Add(set);
            }
            var counts = new Dictionary<uint, int>();
            foreach (var s in perFrame) foreach (var c in s) counts[c] = counts.GetValueOrDefault(c) + 1;
            uint pick = counts.OrderBy(kv => Math.Abs(kv.Value - perFrame.Count / 2)).First().Key;
            long with = perFrame.Count(s => s.Contains(pick));
            return (pick, with, sw.Elapsed.TotalMilliseconds * 1000 / Math.Max(1, perFrame.Count), 32);
        }
        finally { sm.Dispose(); }
    }
}
