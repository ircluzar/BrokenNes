using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using BrokenNes.Workshop.Movies;
using NesEmulator.Debugging;
using NesEmulator.Debugging.Timeflow;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace BrokenNes.Workshop.Timeflow;

/// <summary>A profile request: a span of a timeline, the queries over it, and whether (and how) it is kept.</summary>
internal sealed class ProfileRequest
{
    public string Timeline = "";
    public long From, To;
    public List<string> Queries = new();
    public List<string> Symbols = new();
    public List<string>? Vars;
    public bool Store, Keep;
    public int TtlMinutes = ProfileStore.DefaultTtlMinutes;
    public long LimitBytes = ProfileStore.DefaultLimitBytes;
    public long? CapBytes;
    public int Quality = ProfileStore.DefaultQuality;
    public bool StopWhenOff = true;
    public Action<MovieMachine, long>? BeforeFrame;
    public string Note = "";
}

/// <summary>
/// T3's profiled spans on Workshop machines (docs/projects/brokendev-timeflow-plan.md, "T3 as built"): the CLI verbs and the calls the MCP
/// server's <c>timeflow_profile</c> tools make (the same code, so the answers are the same everywhere). A span is re-run on a NEW headless
/// machine of the timeline's own ROM and cores; nothing of the caller's sessions is touched.
/// <code>
///   --timeflow profile        --timeline ID|DIR --from A --to B [--q "QUERY"]... [--sym F]... [--var NAME]... [--store [--keep] [--ttl-min 60] [--limit-mb 512]] [--json] [--hold-ms N]
///   --timeflow profile-query  --profile ID|DIR --q "QUERY"... [--sym F]... [--json]       a kept profile (the queries that need the machine running: a re-run)
///   --timeflow profiles       [--json]                                                     the kept profiles (after a sweep)
///   --timeflow profile-keep   --profile ID|DIR [--off] [--ttl-min N]
///   --timeflow profile-drop   --profile ID|DIR | --all
///   --timeflow profile-sweep  [--json]                                                     deletes the expired, orphaned (crash) and over-cap ones
///   --timeflow profile-measure --rom R [--console c] [--frames N] [--warm N] [--input S] [--json]   cost, bytes a frame, query speed
/// </code>
/// Queries: see <see cref="ProfileQuery"/> (writes, reads, accesses, last-write, stack, between, became, when, histogram, events, trace, vars, summary).
/// </summary>
internal static class TimeflowProfiling
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Why a console cannot be profiled (null: it can). Since T0 every console can: the debugger covers the Sega consoles' Z80 and 68000 too.</summary>
    public static string? NoProfileWhy(string console) => null;

    /// <summary>Re-runs the span with the queries (and the store when asked); the answer as JSON.</summary>
    public static JsonObject Run(ProfileRequest q)
    {
        string dir = TimeflowStore.Find(q.Timeline) ?? throw new FileNotFoundException($"no timeline '{q.Timeline}' under {TimeflowStore.Root()}");
        using var pb = new TimeflowPlayback(dir);
        if (NoProfileWhy(pb.Meta.Console) is { } why) throw new NotSupportedException(why);
        var queries = q.Queries.Select(ProfileQuery.Parse).ToList();
        ProfileStore.Sweep(TimeflowStore.GameDirOf(dir));
        using var m = TimeflowMachines.OpenFor(dir, pb.Meta, true, q.Symbols);
        if (!m.HasDebugger) throw new NotSupportedException($"profiling the {pb.Meta.Console} needs the debugger, which does not cover it: no profile is made");
        var src = TimeflowMachines.SourceFor(m);
        pb.CheckMachine(src, m.RomSha256);
        var target = new ProfileTarget { Machine = m.Host.Machine, Debugger = m.Host.Dbg, Source = src, RunFrame = (a, b) => m.Frame(a, b), Port = m.Port, PoweredOn = true };
        var sinks = new List<ProfileSink>(queries);
        ProfileWriter? w = null;
        if (q.Store)
        {
            w = ProfileStore.Create(pb.Meta, dir, q.From, q.To, new ProfileStoreOptions { Keep = q.Keep, TtlMinutes = q.TtlMinutes, LimitBytes = q.LimitBytes, Quality = q.Quality, Symbols = q.Symbols, Note = q.Note, CapBytes = q.CapBytes });
            sinks.Add(w);
        }
        var span = new ProfileSpan { From = q.From, To = q.To, Vars = q.Vars, StopWhenOff = q.StopWhenOff };
        if (q.BeforeFrame != null) { var bf = q.BeforeFrame; span.BeforeFrame = f => bf(m, f); }
        ProfileReport r;
        try { r = TimeflowProfiler.Run(pb, target, span, sinks); }
        catch { if (w != null) { w.Dispose(); ProfileStore.Delete(w.Dir); } throw; }
        w?.Close(r);
        var j = ReportJson(r);
        var qa = new JsonArray();
        foreach (var x in queries) qa.Add(x.Result());
        j["queries"] = qa;
        if (w != null) j["profile"] = ProfileJson(w.Dir, w.Meta);
        else j["profile"] = null;
        j["stored"] = w != null ? "kept in the profiles folder (see profile)" : "nothing stored: the queries streamed over the re-run and only their answers are kept";
        return j;
    }

    public static JsonObject ReportJson(ProfileReport r)
    {
        var j = new JsonObject
        {
            ["timeline"] = r.TimelineId, ["console"] = r.Console, ["from"] = r.From, ["to"] = r.To, ["keyframe"] = r.KeyFrame,
            ["inSync"] = r.InSync, ["offFrame"] = r.OffFrame, ["offWhat"] = r.OffFrame != null ? r.OffWhat : null,
            ["framesProfiled"] = r.FramesProfiled, ["preRollFrames"] = r.PreRollFrames, ["stoppedEarly"] = r.StoppedEarly ? true : null, ["cancelled"] = r.Cancelled ? true : null,
            ["cpus"] = new JsonArray(r.Cpus.Select(c => (JsonNode)c).ToArray()),
            ["instructions"] = new JsonObject(r.InstructionsByCpu.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["interrupts"] = r.Interrupts, ["accesses"] = r.Accesses, ["calls"] = r.Calls, ["variableChanges"] = r.VarChanges, ["markers"] = r.Markers,
            ["varsFollowed"] = r.VarsFollowed,
            ["cost"] = new JsonObject
            {
                ["profiledFps"] = Math.Round(r.Fps, 1), ["timesSlowerThanRealTime"] = Math.Round(r.TimesSlower, 2), ["profileSeconds"] = Math.Round(r.ProfileMs / 1000, 2),
                ["jumpMs"] = Math.Round(r.JumpMs, 1), ["preRollSeconds"] = Math.Round(r.PreRollMs / 1000, 2),
            },
        };
        if (r.NotProfiled.Count > 0) j["limitations"] = new JsonArray(r.NotProfiled.Select(n => (JsonNode)n).ToArray());
        if (r.Notes.Count > 0) j["notes"] = new JsonArray(r.Notes.Select(n => (JsonNode)n).ToArray());
        return j;
    }

    public static JsonObject ProfileJson(string dir, ProfileMeta m) => new()
    {
        ["id"] = m.Id, ["dir"] = dir, ["timeline"] = m.Timeline, ["console"] = m.Console, ["from"] = m.From, ["to"] = m.To, ["status"] = m.Status,
        ["reason"] = m.Reason.Length > 0 ? m.Reason : null, ["framesStored"] = m.FramesStored, ["lastFrameStored"] = m.LastFrameStored,
        ["bytes"] = m.Bytes, ["rawBytes"] = m.RawBytes, ["bytesPerFrame"] = m.FramesStored > 0 ? Math.Round((double)m.Bytes / m.FramesStored, 0) : 0,
        ["limitBytes"] = m.LimitBytes, ["keep"] = m.Keep, ["ttlMinutes"] = m.TtlMinutes, ["expires"] = m.ExpiresUtc.ToString("o"),
        ["owner"] = $"{m.OwnerName} {m.OwnerPid}", ["offFrame"] = m.OffFrame,
        ["deletedWhen"] = m.Keep ? $"its time to live passes ({m.TtlMinutes} min after its last use), or profile-drop" : "this session ends (or, after a crash, the next sweep), or its time to live passes",
    };

    /// <summary>Queries over a kept profile: its records read back into the same query code.</summary>
    public static JsonObject Query(string profile, List<string> queries, List<string> extraSymbols)
    {
        string dir = ProfileStore.Find(profile) ?? throw new FileNotFoundException($"no kept profile '{profile}' (timeflow profiles lists them; a profile is deleted when its session ends unless kept)");
        var rd = new ProfileReader(dir);
        var meta = rd.Meta;
        if (!Directory.Exists(meta.TimelineDir)) throw new FileNotFoundException($"profile {meta.Id}'s timeline {meta.Timeline} is gone ({meta.TimelineDir}): its machine cannot be opened to name symbols");
        var tl = TimelineMeta.Load(meta.TimelineDir);
        var syms = meta.Symbols.Where(File.Exists).Concat(extraSymbols).Distinct().ToList();
        using var m = TimeflowMachines.OpenFor(meta.TimelineDir, tl, true, syms);
        var parsed = queries.Select(ProfileQuery.Parse).ToList();
        var ctx = new ProfileContext
        {
            Machine = m.Host.Machine, Debugger = m.Host.Dbg, Live = false, Console = meta.Console, TimelineId = meta.Timeline, From = meta.From, To = Math.Max(meta.From, meta.LastFrameStored),
            StackKnownFrom = meta.StackKnownFrom, Vars = meta.Vars,
        };
        if (meta.OffFrame is long off) { ctx.OffFrame = off; ctx.OffWhat = meta.OffWhat; }
        var sw = Stopwatch.StartNew();
        rd.Feed(parsed.Cast<ProfileSink>().ToList(), ctx);
        double ms = sw.Elapsed.TotalMilliseconds;
        var qa = new JsonArray();
        foreach (var x in parsed) qa.Add(x.Result());
        return new JsonObject
        {
            ["profile"] = ProfileJson(dir, ProfileMeta.Load(dir)), ["queryMs"] = Math.Round(ms, 1),
            ["recordsPerSecond"] = ms > 0 ? Math.Round((meta.Instructions + meta.Accesses) / (ms / 1000.0)) : null,
            ["queries"] = qa,
        };
    }

    public static JsonObject List()
    {
        var swept = ProfileStore.Sweep();
        var a = new JsonArray();
        foreach (var p in ProfileStore.List()) a.Add(ProfileJson(p.Dir, p.Meta));
        return new JsonObject { ["root"] = SafeRoot(), ["profiles"] = a, ["swept"] = SweptJson(swept) };
    }

    private static string SafeRoot() { try { return TimeflowStore.Root(); } catch (InvalidOperationException e) { return e.Message; } }

    public static JsonArray SweptJson(List<ProfileStore.Swept> s) => new(s.Select(x => (JsonNode)new JsonObject { ["id"] = x.Id, ["why"] = x.Why, ["bytes"] = x.Bytes }).ToArray());

    // ------------------------------------------------------------------------------------------------ the CLI

    public static int Cli(string verb, Func<string, string, string> Opt, Func<string, List<string>> All, Func<string, bool> Has, bool json)
    {
        JsonObject j;
        switch (verb)
        {
            case "profile":
            {
                var q = new ProfileRequest
                {
                    Timeline = Opt("timeline", ""), From = long.Parse(Need(Opt("from", ""), "--from"), CultureInfo.InvariantCulture),
                    To = long.Parse(Need(Opt("to", ""), "--to"), CultureInfo.InvariantCulture), Queries = All("q"), Symbols = All("sym"),
                    Vars = All("var") is { Count: > 0 } v ? v : null, Store = Has("store") || Has("keep"), Keep = Has("keep"), Note = Opt("note", "workshop"),
                };
                if (int.TryParse(Opt("ttl-min", ""), out int ttl)) q.TtlMinutes = ttl;
                if (double.TryParse(Opt("limit-mb", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double mb)) q.LimitBytes = (long)(mb * 1024 * 1024);
                if (double.TryParse(Opt("cap-mb", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double cmb)) q.CapBytes = (long)(cmb * 1024 * 1024);
                if (int.TryParse(Opt("quality", ""), out int ql)) q.Quality = ql;
                if (q.Timeline.Length == 0) throw new ArgumentException("--timeline is needed");
                j = Run(q);
                Print(j, json, Human);
                if (int.TryParse(Opt("hold-ms", ""), out int hold) && hold > 0)
                {
                    // the cleanup test: the profile exists while this process lives; the test kills it here (a crash) and the next sweep must remove it
                    Console.WriteLine($"HOLDING {Environment.ProcessId} {j["profile"]?["dir"]}");
                    Console.Out.Flush();
                    System.Threading.Thread.Sleep(hold);
                }
                int ended = ProfileStore.EndSession();
                if (!json && ended > 0) Console.WriteLine($"session end: {ended} unkept profile(s) deleted");
                return j["inSync"]?.GetValue<bool>() == true ? 0 : 1;
            }
            case "profile-query":
                j = Query(Need(Opt("profile", ""), "--profile"), All("q"), All("sym"));
                Print(j, json, x => string.Join("\n", ((JsonArray)x["queries"]!).Select(a => a!.ToJsonString(Indented))));
                return 0;
            case "profiles":
                j = List();
                Print(j, json, x => string.Join("\n", ((JsonArray)x["profiles"]!).Select(p => $"{p!["id"]}  {p["console"]} timeline {p["timeline"]} frames {p["from"]}-{p["to"]}  {p["status"]}  {(long)p["bytes"]! / 1024} KB  {(p["keep"]!.GetValue<bool>() ? "KEEP " : "")}expires {p["expires"]}")) +
                                       $"\n{((JsonArray)x["profiles"]!).Count} kept profile(s); swept {((JsonArray)x["swept"]!).Count}");
                return 0;
            case "profile-keep":
            {
                string dir = ProfileStore.Find(Need(Opt("profile", ""), "--profile")) ?? throw new FileNotFoundException($"no kept profile '{Opt("profile", "")}'");
                var m = ProfileStore.SetKeep(dir, !Has("off"), int.TryParse(Opt("ttl-min", ""), out int t) ? t : null);
                j = ProfileJson(dir, m);
                Print(j, json, x => $"{x["id"]}: keep {x["keep"]}, expires {x["expires"]}");
                return 0;
            }
            case "profile-drop":
            {
                if (Has("all"))
                {
                    var s = ProfileStore.Sweep(null, all: true);
                    j = new JsonObject { ["dropped"] = SweptJson(s) };
                    Print(j, json, x => $"dropped {s.Count} profile(s)");
                    return 0;
                }
                string dir = ProfileStore.Find(Need(Opt("profile", ""), "--profile")) ?? throw new FileNotFoundException($"no kept profile '{Opt("profile", "")}'");
                bool ok = ProfileStore.Delete(dir);
                j = new JsonObject { ["dropped"] = Path.GetFileName(dir), ["ok"] = ok };
                Print(j, json, x => ok ? $"dropped {x["dropped"]}" : $"could not delete {dir}");
                return ok ? 0 : 1;
            }
            case "profile-sweep":
            {
                var s = ProfileStore.Sweep();
                j = new JsonObject { ["swept"] = SweptJson(s), ["left"] = ProfileStore.List().Count };
                Print(j, json, x => $"swept {s.Count} profile(s){string.Concat(s.Select(y => $"\n  {y.Id}: {y.Why}"))}; {x["left"]} left");
                return 0;
            }
            case "profile-measure": return Measure(Opt, All, Has, json);
            case "profile-rom":
            {
                // T3's planted corruption (and with --fixed its fixed build) as a game repo would ship it: the ROM and its .mlb (BrokenDev's run-tests build it)
                string outRom = Need(Opt("out", ""), "--out");
                var (rom, _, mlb) = TimeflowProfileRoms.Corrupting(Has("fixed"));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outRom))!);
                File.WriteAllBytes(outRom, rom);
                File.WriteAllText(Path.ChangeExtension(outRom, ".mlb"), mlb);
                Console.WriteLine($"wrote {outRom} and {Path.ChangeExtension(outRom, ".mlb")}");
                return 0;
            }
        }
        Console.Error.WriteLine($"unknown verb '{verb}'");
        return 2;
    }

    private static string Need(string v, string what) => v.Length > 0 ? v : throw new ArgumentException($"{what} is needed");

    private static void Print(JsonObject j, bool json, Func<JsonObject, string> human) => Console.WriteLine(json ? j.ToJsonString(Indented) : human(j));

    private static string Human(JsonObject j)
    {
        var c = j["cost"]!;
        string s = $"profiled frames {j["from"]}-{j["to"]} of timeline {j["timeline"]} ({j["console"]}): {(j["inSync"]!.GetValue<bool>() ? "in sync with the recording" : $"OUT OF SYNC at frame {j["offFrame"]} ({j["offWhat"]})")}, " +
                   $"{j["framesProfiled"]} frames, {j["instructions"]!.ToJsonString()} instructions, {j["accesses"]} accesses; {c["profiledFps"]} fps = {c["timesSlowerThanRealTime"]}x slower than real time";
        if (j["profile"] is JsonObject p) s += $"\n  kept as {p["id"]} ({p["status"]}, {p["bytes"]} bytes, {p["bytesPerFrame"]} a frame): {p["dir"]}";
        foreach (var q in (JsonArray)j["queries"]!) s += "\n" + q!.ToJsonString(Indented);
        return s;
    }

    // ------------------------------------------------------------------------------------------------ measure

    /// <summary>Records warm + N frames, then profiles the last N: the cost, the bytes a stored frame, and the stored queries' speed.</summary>
    private static int Measure(Func<string, string, string> Opt, Func<string, List<string>> All, Func<string, bool> Has, bool json)
    {
        string rom = Need(Opt("rom", ""), "--rom");
        int frames = int.TryParse(Opt("frames", ""), out int fr) ? fr : 120, warm = int.TryParse(Opt("warm", ""), out int w) ? w : 300;
        var j = MeasureOne(rom, Opt("console", "") is { Length: > 0 } c ? c : null, frames, warm, Opt("input", "auto:1"), All("sym"));
        Console.WriteLine(json ? j.ToJsonString(Indented) : j.ToJsonString(Indented));
        return 0;
    }

    public static JsonObject MeasureOne(string rom, string? console, int frames, int warm, string input, List<string> symbols)
    {
        string dir;
        double realFps;
        // 1. a timeline of warm + frames, every frame recorded (the plain machine, no debugger hook: the recording's own speed)
        var swPlain = Stopwatch.StartNew();
        using (var m = MovieMachine.Open(rom, console, "", "fceux", false, true, symbols))
        {
            var o = new TimeflowOptions { Rom = m.RomFile, RomName = m.RomName, Cores = m.Cores, PowerOn = m.PowerOn, Source = "measure", PowerOnStart = true };
            var rec = TimeflowRecorder.Start(TimeflowMachines.SourceFor(m), o);
            var inp = TimeflowInput.Parse(input, "", null);
            for (long f = 0; f < warm + frames && rec.Running; f++) { var (a, b) = inp.At(f); rec.BeforeFrame(a, b); m.Frame(a, b); rec.AfterFrame(); }
            dir = rec.Stop();
            realFps = TimeflowProfiler.RealFps(m.ConsoleKey);
        }
        double recordFps = (warm + frames) / swPlain.Elapsed.TotalSeconds;
        // 2. the plain re-run speed of the span (a replay, no profiling): the baseline the profile is slower than
        double plainFps;
        using (var pb = new TimeflowPlayback(dir))
        using (var m = TimeflowMachines.OpenFor(dir, pb.Meta, false))
        {
            var src = TimeflowMachines.SourceFor(m);
            pb.Jump(src, warm);
            var sw = Stopwatch.StartNew();
            for (long f = warm; f < warm + frames; f++) { var (a, b) = pb.Reader.Pads(f); m.Frame(a, b); }
            plainFps = frames / sw.Elapsed.TotalSeconds;
        }
        // 3. streaming only (nothing stored), one summary query
        var streaming = Run(new ProfileRequest { Timeline = dir, From = warm, To = warm + frames - 1, Queries = new() { "summary" }, Symbols = symbols });
        // 4. stored
        var stored = Run(new ProfileRequest { Timeline = dir, From = warm, To = warm + frames - 1, Queries = new(), Symbols = symbols, Store = true, LimitBytes = 4L << 30, CapBytes = 8L << 30 });
        string pid = stored["profile"]!["id"]!.GetValue<string>();
        // 5. the stored queries' speed (records read back per second)
        var q1 = Query(pid, new() { "histogram", "writes $0000-$00FF limit 5", "summary" }, new());
        ProfileStore.Delete((string)stored["profile"]!["dir"]!);
        var pj = (JsonObject)stored["profile"]!;
        try { Directory.Delete(dir, true); } catch (IOException) { }   // the measurement's own timeline
        return new JsonObject
        {
            ["rom"] = Path.GetFileName(rom), ["console"] = stored["console"]?.GetValue<string>(), ["frames"] = frames, ["warm"] = warm,
            ["realFps"] = realFps, ["plainReplayFps"] = Math.Round(plainFps, 1), ["recordFps"] = Math.Round(recordFps, 1),
            ["streamingFps"] = streaming["cost"]!["profiledFps"]!.DeepClone(), ["streamingTimesSlower"] = streaming["cost"]!["timesSlowerThanRealTime"]!.DeepClone(),
            ["storedFps"] = stored["cost"]!["profiledFps"]!.DeepClone(), ["storedTimesSlower"] = stored["cost"]!["timesSlowerThanRealTime"]!.DeepClone(),
            ["streamingTimesSlowerThanPlainReplay"] = Math.Round(plainFps / Math.Max(1e-9, streaming["cost"]!["profiledFps"]!.GetValue<double>()), 1),
            ["storedTimesSlowerThanPlainReplay"] = Math.Round(plainFps / Math.Max(1e-9, stored["cost"]!["profiledFps"]!.GetValue<double>()), 1),
            ["instructionsPerFrame"] = Math.Round(((JsonObject)stored["instructions"]!).Sum(kv => kv.Value!.GetValue<long>()) / (double)frames),
            ["accessesPerFrame"] = Math.Round(stored["accesses"]!.GetValue<long>() / (double)frames),
            ["storedBytesPerFrame"] = pj["bytesPerFrame"]!.DeepClone(), ["rawBytesPerFrame"] = Math.Round(pj["rawBytes"]!.GetValue<long>() / (double)Math.Max(1, frames)),
            ["storedQueryMs"] = q1["queryMs"]!.DeepClone(), ["storedRecordsPerSecond"] = q1["recordsPerSecond"]?.DeepClone(),
            ["inSync"] = stored["inSync"]!.DeepClone(), ["timeline"] = dir,
        };
    }
}
