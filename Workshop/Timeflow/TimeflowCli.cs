using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using BrokenNes.Workshop.Movies;
using NesEmulator.Debugging;
using NesEmulator.Debugging.Capture;
using NesEmulator.Debugging.Checks;
using NesEmulator.Debugging.Timeflow;
using NesEmulator.Systems;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace BrokenNes.Workshop.Timeflow;

/// <summary>
/// <c>--timeflow</c>: BrokenDev Timeflow's timelines (milestones T1 and T2 of docs/projects/brokendev-timeflow-plan.md). Headless recordings are
/// Workshop's own machines (never the user's play: the desktop app records only when the user switches it on).
/// <code>
///   --timeflow record  --rom R [--console c] [--cores C/P/A] --frames N [--input "120:Start,132:" | --input auto[:seed] | --movie M.bnm] [--input2 S]
///                      [--var name=expr]... [--sym F]... [--coverage] [--checks] [--key-every 60] [--page 256] [--codec brotli:1|deflate:0|none]
///                      [--game-dir DIR] [--cap-gb 5] [--note TEXT] [--json]
///   --timeflow list    [--project P | --game-dir DIR] [--json]                 every game's timelines (a branch under its parent), with the space used
///   --timeflow info    --timeline ID|DIR [--json]
///   --timeflow verify  --timeline ID|DIR [--every K] [--machine] [--json]     restore equals record (every K-th frame; --machine: loaded into a machine too)
///   --timeflow jump    --timeline ID|DIR --frame F [--png OUT] [--state OUT] [--json]
///   --timeflow resume  --timeline ID|DIR --frame F (--replay [--frames N] | --input S [--frames N] [--record]) [--json]
///   --timeflow prune   [--project P | --game-dir DIR] [--cap-gb G] [--json]   retire the oldest until within the cap (keep-marked never)
///   --timeflow keep    --timeline ID|DIR [--off]
///   --timeflow status  [--json]                                                 the root, every game folder's use against its cap
///   --timeflow measure --rom R [--console c] [--cores C/P/A] [--warm N] [--frames N] [--input ...|--movie M] [--json OUT]
/// </code>
/// Exit 0 done, 1 a check failed (verify off, a replay out of sync), 2 usage, 3 cannot run.
/// </summary>
internal static class TimeflowCli
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        string Opt(string n, string d = "") { int i = Array.FindIndex(args, 2, a => a.Equals("--" + n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        List<string> All(string n) { var l = new List<string>(); for (int i = 2; i < args.Length - 1; i++) if (args[i].Equals("--" + n, StringComparison.OrdinalIgnoreCase)) l.Add(args[i + 1]); return l; }
        bool Has(string n) => args.Skip(2).Any(a => a.Equals("--" + n, StringComparison.OrdinalIgnoreCase));
        if (args.Length < 2) { Console.Error.WriteLine("usage: --timeflow record|list|info|verify|jump|resume|prune|keep|status|measure ... (see Workshop/Timeflow/TimeflowCli.cs)"); return 2; }
        if (Opt("home") is { Length: > 0 } home) Environment.SetEnvironmentVariable("BROKENDEV_HOME", home);
        bool json = Has("json");
        try
        {
            switch (args[1].ToLowerInvariant())
            {
                case "record": return Record(Opt, All, Has, json);
                case "list": return List(Opt("project"), Opt("game-dir"), json);
                case "info":
                {
                    string dir = NeedTimeline(Opt("timeline"));
                    using var rd = new TimelineReader(dir);
                    var j = InfoJson(dir, rd);
                    Console.WriteLine(json ? j.ToJsonString(Indented) : Summary(j));
                    return 0;
                }
                case "verify": return Verify(NeedTimeline(Opt("timeline")), int.TryParse(Opt("every", "1"), out int ev) ? Math.Max(1, ev) : 1, Has("machine"), json);
                case "jump": return Jump(NeedTimeline(Opt("timeline")), long.Parse(Need(Opt("frame"), "--frame"), CultureInfo.InvariantCulture), Opt("png"), Opt("state"), json);
                case "resume": return Resume(NeedTimeline(Opt("timeline")), long.Parse(Need(Opt("frame"), "--frame"), CultureInfo.InvariantCulture), Has("replay"), Opt("input"), Opt("input2"),
                                             long.TryParse(Opt("frames"), out long rf) ? rf : -1, Has("record"), json);
                case "prune": return Prune(Opt("project"), Opt("game-dir"), double.TryParse(Opt("cap-gb"), NumberStyles.Float, CultureInfo.InvariantCulture, out double g) ? (long)(g * 1024 * 1024 * 1024) : null, json);
                case "keep":
                {
                    string dir = NeedTimeline(Opt("timeline"));
                    TimeflowStore.SetKeep(dir, !Has("off"));
                    Console.WriteLine($"{Path.GetFileName(dir)}: keep {(Has("off") ? "off (it may retire when the cap is reached)" : "on (never retired)")}");
                    return 0;
                }
                case "status": return Status(json);
                case "measure": return TimeflowMeasure.Run(Opt, All, Has);
                case "profile" or "profile-query" or "profiles" or "profile-keep" or "profile-drop" or "profile-sweep" or "profile-measure" or "profile-rom":
                    return TimeflowProfiling.Cli(args[1].ToLowerInvariant(), (n, d) => Opt(n, d), All, Has, json);
                default: Console.Error.WriteLine($"unknown verb '{args[1]}'"); return 2;
            }
        }
        catch (Exception e) when (e is ArgumentException or FormatException or FileNotFoundException or InvalidDataException or NotSupportedException or IOException or InvalidOperationException)
        {
            Console.Error.WriteLine(e.Message);
            return e is FileNotFoundException or NotSupportedException ? 3 : 2;
        }
    }

    private static string Need(string v, string what) => v.Length > 0 ? v : throw new ArgumentException($"{what} is needed");

    private static string NeedTimeline(string id) => TimeflowStore.Find(Need(id, "--timeline")) ?? throw new FileNotFoundException($"no timeline '{id}' under {TimeflowStore.Root()}");

    public static TimeflowOptions ParseCodec(TimeflowOptions o, string codec)
    {
        if (codec.Length == 0) return o;
        var p = codec.Split(':');
        o.Compression = p[0].ToLowerInvariant() switch { "brotli" => TimelineCompression.Brotli, "deflate" => TimelineCompression.Deflate, "none" => TimelineCompression.None, _ => throw new ArgumentException($"codec '{codec}': brotli[:q], deflate[:0-2] or none") };
        o.Level = p.Length > 1 ? int.Parse(p[1], CultureInfo.InvariantCulture) : o.Compression == TimelineCompression.Brotli ? TimeflowOptions.DefaultLevel : 0;
        return o;
    }

    // ------------------------------------------------------------------------------------------------ record

    private static int Record(Func<string, string, string> Opt, Func<string, List<string>> All, Func<string, bool> Has, bool json)
    {
        string rom = Need(Opt("rom", ""), "--rom");
        var input = TimeflowInput.Parse(Opt("input", ""), Opt("input2", ""), Opt("movie", "") is { Length: > 0 } mp ? mp : null);
        long frames = long.TryParse(Opt("frames", ""), out long fr) && fr > 0 ? fr : input.MovieFrames ?? throw new ArgumentException("--frames N is needed");
        var vars = All("var").Select(v => { int e = v.IndexOf('='); return e > 0 ? (Name: v[..e].Trim(), Expr: v[(e + 1)..].Trim()) : throw new ArgumentException($"--var '{v}': name=expression"); }).ToList();
        bool coverage = Has("coverage"), checks = Has("checks");
        using var m = MovieMachine.Open(rom, Opt("console", "") is { Length: > 0 } c ? c : null, Opt("cores", ""), Opt("power-on", "fceux"), Has("ntsc"), true, All("sym"));
        if ((vars.Count > 0 || coverage || checks) && !m.HasDebugger) throw new NotSupportedException($"watched variables, coverage and checks need the debugger, which does not cover the {m.ConsoleKey} yet (T0)");
        var src = TimeflowMachines.SourceFor(m);
        var o = ParseCodec(new TimeflowOptions(), Opt("codec", ""));
        o.Rom = m.RomFile; o.RomName = m.RomName; o.Cores = m.Cores; o.PowerOn = m.PowerOn; o.Ntsc = m.PowerOnNtsc; o.Source = "workshop"; o.Note = Opt("note", "");
        o.PowerOnStart = true;
        if (Opt("game-dir", "") is { Length: > 0 } gd) o.GameDir = Path.GetFullPath(gd);
        if (int.TryParse(Opt("key-every", ""), out int ke)) o.KeyEvery = ke;
        if (int.TryParse(Opt("page", ""), out int pg)) o.PageSize = pg;
        if (double.TryParse(Opt("cap-gb", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double cap)) o.CapBytes = (long)(cap * 1024 * 1024 * 1024);
        if (int.TryParse(Opt("full-check-every", ""), out int fce)) o.FullCheckEvery = fce;
        if (vars.Count > 0) { var dbg = m.Host.Dbg; o.VarNames = vars.Select(v => $"{v.Name}={v.Expr}").ToList(); o.Vars = () => vars.Select(v => dbg.Evaluate(v.Expr)).ToArray(); }
        FrameCoverage? cov = coverage ? FrameCoverage.Attach(m.Host.Dbg) : null;
        o.Coverage = cov;
        if (checks) o.Checks = CheckRun.Attach(m.Host.Dbg, new CheckOptions { Profile = false });
        var sw = Stopwatch.StartNew();
        var rec = TimeflowRecorder.Start(src, o);
        long f = 0;
        for (; f < frames && rec.Running; f++)
        {
            var (p1, p2) = input.At(f);
            rec.BeforeFrame(p1, p2);
            m.Frame(p1, p2);
            rec.AfterFrame();
            if (m.Crashed) { Console.Error.WriteLine($"the game crashed at frame {f + 1}: {m.CrashInfo}"); f++; break; }
        }
        double emuSec = sw.Elapsed.TotalSeconds;
        string dir = rec.Stop();
        double total = sw.Elapsed.TotalSeconds;
        cov?.Detach();
        using var rd = new TimelineReader(dir);
        var j = InfoJson(dir, rd);
        j["fps"] = Math.Round(f / Math.Max(1e-9, emuSec), 1);
        j["secondsWithDrain"] = Math.Round(total, 2);
        j["captureMsAverage"] = Math.Round(rec.CaptureMsAverage, 3);
        j["captureMsMax"] = Math.Round(rec.CaptureMsMax, 3);
        j["compressMs"] = Math.Round(rec.CompressMs, 1);
        j["verifyMs"] = Math.Round(rec.VerifyMs, 1);
        if (rec.Retired.Count > 0) j["retired"] = new JsonArray(rec.Retired.Select(r => (JsonNode)r).ToArray());
        if (rec.StopReason != null) j["stopReason"] = rec.StopReason;
        Console.WriteLine(json ? j.ToJsonString(Indented) : Summary(j));
        return rec.StopReason == null ? 0 : 1;
    }

    // ------------------------------------------------------------------------------------------------ info, list, status, prune

    public static JsonObject InfoJson(string dir, TimelineReader rd)
    {
        var m = rd.Meta;
        long bytes = TimeflowStore.DirBytes(dir);
        return new JsonObject
        {
            ["id"] = m.Id, ["dir"] = dir, ["console"] = m.Console, ["rom"] = m.RomName, ["romSha256"] = m.RomSha256, ["cores"] = m.Cores,
            ["project"] = m.Project, ["source"] = m.Source, ["status"] = m.Status, ["stopReason"] = m.StopReason.Length > 0 ? m.StopReason : null,
            ["startFrame"] = m.StartFrame, ["frames"] = rd.Frames, ["endFrame"] = rd.EndFrame,
            ["parent"] = m.ParentId != null ? new JsonObject { ["id"] = m.ParentId, ["frame"] = m.ParentFrame } : null,
            ["keyEvery"] = m.KeyEvery, ["pageSize"] = m.PageSize, ["codec"] = $"{m.Compression.ToString().ToLowerInvariant()}:{m.Level}",
            ["storedBytes"] = m.StoredBytes, ["rawBytes"] = m.RawBytes, ["folderBytes"] = bytes,
            ["bytesPerFrame"] = rd.Frames > 0 ? Math.Round((double)bytes / rd.Frames, 1) : 0,
            ["storedBytesPerFrame"] = rd.Frames > 0 ? Math.Round((double)m.StoredBytes / rd.Frames, 1) : 0,
            ["verifiedFrames"] = m.VerifiedFrames, ["verifyFailures"] = m.VerifyFailures, ["keep"] = m.Keep,
            ["vars"] = new JsonArray(m.Vars.Select(v => (JsonNode)v).ToArray()), ["coverage"] = m.Coverage, ["checks"] = m.Checks,
            ["domains"] = new JsonArray(m.Domains.Select(d => (JsonNode)$"{d.Id} {d.Size}").ToArray()),
            ["created"] = m.CreatedUtc.ToString("o"), ["lastUsed"] = m.LastUsedUtc.ToString("o"),
        };
    }

    private static string Summary(JsonObject j) =>
        $"timeline {j["id"]}: {j["console"]} {j["rom"]} ({j["cores"]}), frames {j["startFrame"]}-{(long)j["endFrame"]! - 1} ({j["frames"]}), {j["status"]}" +
        $"{(j["stopReason"] is { } r ? " (" + r + ")" : "")}, {j["folderBytes"]} bytes on disk ({j["bytesPerFrame"]} a frame; blocks {j["storedBytesPerFrame"]} a frame), " +
        $"N {j["keyEvery"]}, page {j["pageSize"]}, {j["codec"]}, restore = record checked on {j["verifiedFrames"]} frames ({j["verifyFailures"]} failed)" +
        (j["fps"] is { } fps ? $", {fps} fps recording (capture {j["captureMsAverage"]} ms a frame, worst {j["captureMsMax"]})" : "") + $"\n  {j["dir"]}";

    private static List<string> GameDirsFor(string project, string gameDir)
    {
        if (gameDir.Length > 0) return new() { Path.GetFullPath(gameDir) };
        if (project.Length > 0) return new() { TimeflowStore.ProjectDir(project) };
        return TimeflowStore.GameDirs();
    }

    private static int List(string project, string gameDir, bool json)
    {
        var arr = new JsonArray();
        foreach (string g in GameDirsFor(project, gameDir))
        {
            var all = TimeflowStore.List(g);
            var tl = new JsonArray();
            foreach (var (t, depth) in TimeflowPlayback.Tree(all))
            {
                tl.Add(new JsonObject
                {
                    ["id"] = t.Meta.Id, ["depth"] = depth, ["console"] = t.Meta.Console, ["rom"] = t.Meta.RomName, ["frames"] = t.Meta.Frames, ["startFrame"] = t.Meta.StartFrame,
                    ["parent"] = t.Meta.ParentId, ["parentFrame"] = t.Meta.ParentFrame, ["status"] = t.Meta.Status, ["bytes"] = t.Bytes, ["keep"] = t.Meta.Keep,
                    ["source"] = t.Meta.Source, ["created"] = t.Meta.CreatedUtc.ToString("o"),
                });
                if (!json) Console.WriteLine($"  {new string(' ', depth * 2)}{t.Meta.Id}  {t.Meta.Console} {t.Meta.RomName}  frames {t.Meta.StartFrame}-{t.Meta.StartFrame + t.Meta.Frames - 1}  {t.Meta.Status}{(t.Meta.Keep ? " KEEP" : "")}  {t.Bytes / 1024} KB  {t.Meta.Source}" +
                                               (t.Meta.ParentId != null ? $"  (branch of {t.Meta.ParentId} at {t.Meta.ParentFrame})" : ""));
            }
            long used = TimeflowStore.DirBytes(g), cap = TimeflowStore.CapOf(g);
            arr.Add(new JsonObject { ["gameDir"] = g, ["usedBytes"] = used, ["capBytes"] = cap, ["timelines"] = tl });
            if (!json) Console.WriteLine($"{g}: {tl.Count} timeline(s), {used / (1024.0 * 1024):0.0} MB of {cap / (1024.0 * 1024 * 1024):0.##} GB");
        }
        if (json) Console.WriteLine(new JsonObject { ["root"] = TimeflowStore.Root(), ["games"] = arr }.ToJsonString(Indented));
        return 0;
    }

    private static int Status(bool json)
    {
        string root = TimeflowStore.Root();
        var games = new JsonArray();
        foreach (string g in TimeflowStore.GameDirs())
            games.Add(new JsonObject { ["gameDir"] = g, ["timelines"] = TimeflowStore.List(g).Count, ["usedBytes"] = TimeflowStore.DirBytes(g), ["capBytes"] = TimeflowStore.CapOf(g) });
        var j = new JsonObject { ["root"] = root, ["exists"] = Directory.Exists(root), ["games"] = games, ["defaultCapBytes"] = TimeflowStore.DefaultCapBytes };
        if (json) Console.WriteLine(j.ToJsonString(Indented));
        else
        {
            Console.WriteLine($"Timeflow root {root}{(Directory.Exists(root) ? "" : " (nothing recorded yet)")}");
            foreach (var g in games) Console.WriteLine($"  {g!["gameDir"]}: {g["timelines"]} timeline(s), {(long)g["usedBytes"]! / (1024.0 * 1024):0.0} MB of {(long)g["capBytes"]! / (1024.0 * 1024 * 1024):0.##} GB");
        }
        return 0;
    }

    private static int Prune(string project, string gameDir, long? cap, bool json)
    {
        var arr = new JsonArray();
        foreach (string g in GameDirsFor(project, gameDir))
        {
            var retired = TimeflowStore.Enforce(g, null, out long still, cap);
            arr.Add(new JsonObject { ["gameDir"] = g, ["retired"] = new JsonArray(retired.Select(r => (JsonNode)r).ToArray()), ["usedBytes"] = still, ["capBytes"] = cap ?? TimeflowStore.CapOf(g) });
            if (!json) Console.WriteLine($"{g}: retired {retired.Count} ({string.Join(", ", retired)}), {still / (1024.0 * 1024):0.0} MB now");
        }
        if (json) Console.WriteLine(new JsonObject { ["games"] = arr }.ToJsonString(Indented));
        return 0;
    }

    // ------------------------------------------------------------------------------------------------ verify

    private static int Verify(string dir, int every, bool machine, bool json)
    {
        using var rd = new TimelineReader(dir);
        var sw = Stopwatch.StartNew();
        long ok = 0, bad = 0, machineOk = 0, machineBad = 0;
        var problems = new List<string>();
        MovieMachine? mm = machine ? TimeflowMachines.OpenFor(dir, rd.Meta, false) : null;
        var src = mm != null ? TimeflowMachines.SourceFor(mm) : null;
        try
        {
            for (long f = rd.StartFrame; f < rd.EndFrame; f++)
            {
                bool sample = (f - rd.StartFrame) % every == 0 || f == rd.EndFrame - 1;
                if (!sample) continue;
                try
                {
                    var st = rd.State(f);   // throws when the restored state's hash is not the recorded one
                    ok++;
                    if (src != null)
                    {
                        src.ForgetHeader();
                        src.Restore(st);
                        if (src.Capture().Hash() == rd.Row(f).State) machineOk++;
                        else { machineBad++; if (problems.Count < 10) problems.Add($"frame {f}: the machine's state after loading differs"); }
                    }
                }
                catch (InvalidDataException e) { bad++; if (problems.Count < 10) problems.Add(e.Message); }
            }
        }
        finally { mm?.Dispose(); }
        var j = new JsonObject
        {
            ["id"] = rd.Meta.Id, ["frames"] = rd.Frames, ["checked"] = ok + bad, ["restoreEqualsRecord"] = ok, ["failed"] = bad,
            ["machineChecked"] = machine ? machineOk + machineBad : null, ["machineFailed"] = machine ? machineBad : null,
            ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 2), ["problems"] = new JsonArray(problems.Select(p => (JsonNode)p).ToArray()),
        };
        Console.WriteLine(json ? j.ToJsonString(Indented) : $"timeline {rd.Meta.Id}: restore equals record on {ok} of {ok + bad} frames checked" +
                                 (machine ? $"; loaded into a machine: {machineOk} of {machineOk + machineBad} hash the same" : "") + $" ({sw.Elapsed.TotalSeconds:0.0} s)" +
                                 string.Concat(problems.Select(p => "\n  " + p)));
        return bad == 0 && machineBad == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------------------------------------ jump and resume (T2)

    private static int Jump(string dir, long frame, string png, string stateOut, bool json)
    {
        using var pb = new TimeflowPlayback(dir);
        using var m = TimeflowMachines.OpenFor(dir, pb.Meta, false);
        var src = TimeflowMachines.SourceFor(m);
        pb.CheckMachine(src, m.RomSha256);
        var sw = Stopwatch.StartNew();
        pb.Jump(src, frame);
        double ms = sw.Elapsed.TotalMilliseconds;
        var row = pb.Reader.Row(frame);
        var ex = pb.Reader.Extras(frame);
        if (png.Length > 0)
        {
            // the picture of the frame before (the restored machine shows what the frame before drew): run this frame with its input to draw it
            var (p1, p2) = pb.Reader.Pads(frame);
            m.Frame(p1, p2);
            File.WriteAllBytes(png, Picture(m));
        }
        if (stateOut.Length > 0) File.WriteAllBytes(stateOut, m.Host.SaveState());
        var j = new JsonObject
        {
            ["id"] = pb.Meta.Id, ["frame"] = frame, ["restoreMs"] = Math.Round(ms, 2), ["state"] = $"{row.State:x16}", ["keyframe"] = row.IsKey,
            ["pads"] = $"{(PadButtons)row.P1} / {(PadButtons)row.P2}", ["picture"] = $"{row.Picture:x16}", ["ram"] = $"{row.Ram:x16}",
            ["vars"] = new JsonObject(pb.Meta.Vars.Select((v, i) => KeyValuePair.Create(v.Split('=')[0], (JsonNode?)(i < ex.Vars.Length ? ex.Vars[i] : null)))),
            ["changed"] = new JsonObject(pb.Meta.Domains.Select((d, i) => KeyValuePair.Create(d.Id, (JsonNode?)(i < ex.Domains.Count ? ex.Domains[i].ChangedBytes : 0)))),
            ["findings"] = new JsonArray(ex.Findings.Select(x => (JsonNode)x).ToArray()), ["coverageRanges"] = ex.Coverage.Count,
            ["png"] = png.Length > 0 ? png : null, ["stateFile"] = stateOut.Length > 0 ? stateOut : null,
        };
        Console.WriteLine(json ? j.ToJsonString(Indented) : $"timeline {pb.Meta.Id} frame {frame}: restored and loaded in {ms:0.0} ms, the machine's state hashes {row.State:x16} as recorded");
        return 0;
    }

    public static byte[] Picture(MovieMachine m)
    {
        var p = new BugPicture();
        if (m.Host.Nes != null) p.FromRgba(m.Host.Nes.GetFrameBuffer(), 256, 240);
        else p.FromArgb(m.Host.Session!.Frame, m.Host.Session.FrameWidth, m.Host.Session.FrameHeight);
        return BugMedia.Png(p.Pixels, p.Width, p.Height);
    }

    private static int Resume(string dir, long frame, bool replay, string script1, string script2, long frames, bool record, bool json)
    {
        using var pb = new TimeflowPlayback(dir);
        using var m = TimeflowMachines.OpenFor(dir, pb.Meta, false);
        var src = TimeflowMachines.SourceFor(m);
        pb.CheckMachine(src, m.RomSha256);
        pb.Jump(src, frame);
        JsonObject j;
        int code = 0;
        if (replay)
        {
            long to = frames > 0 ? frame + frames : pb.Reader.EndFrame;
            var r = pb.Replay(src, frame, to, (a, b) => m.Frame(a, b));
            j = new JsonObject
            {
                ["id"] = pb.Meta.Id, ["mode"] = "replay", ["from"] = frame, ["to"] = r.To, ["framesRun"] = r.FramesRun, ["checks"] = r.Checked,
                ["inSync"] = r.InSync, ["offFrame"] = r.OffFrame, ["offWhat"] = r.OffFrame != null ? r.OffWhat : null,
            };
            code = r.InSync ? 0 : 1;
            if (!json) Console.WriteLine(r.InSync ? $"replayed frames {frame}-{r.To - 1} of {pb.Meta.Id}: in sync with the recording ({r.Checked} checks)"
                                                  : $"replayed from {frame}: off at frame {r.OffFrame} ({r.OffWhat})");
        }
        else
        {
            if (frames <= 0) throw new ArgumentException("a resume with live input needs --frames N");
            var input = TimeflowInput.Parse(script1, script2, null);
            TimeflowRecorder? rec = null;
            if (record)
            {
                var o = pb.BranchOptions(frame, TimeflowStore.RomOf(dir, pb.Meta));
                o.Source = "workshop"; o.Note = $"branch of {pb.Meta.Id} at {frame}";
                rec = TimeflowRecorder.Start(src, o);
            }
            long ran = 0;
            for (; ran < frames && (rec == null || rec.Running); ran++)
            {
                var (p1, p2) = input.At(ran);
                rec?.BeforeFrame(p1, p2);
                m.Frame(p1, p2);
                rec?.AfterFrame();
            }
            string? branch = rec?.Stop();
            j = new JsonObject
            {
                ["id"] = pb.Meta.Id, ["mode"] = "live", ["from"] = frame, ["framesRun"] = ran, ["picture"] = $"{src.PictureHash():x16}", ["ram"] = $"{src.RamHash():x16}",
                ["branch"] = rec != null ? rec.Id : null, ["branchDir"] = branch, ["stopReason"] = rec?.StopReason,
            };
            if (rec?.StopReason != null) code = 1;
            if (!json) Console.WriteLine($"resumed {pb.Meta.Id} at frame {frame} with new input for {ran} frames" + (rec != null ? $": recorded as branch {rec.Id}" : " (not recorded)"));
        }
        if (json) Console.WriteLine(j.ToJsonString(Indented));
        return code;
    }
}
