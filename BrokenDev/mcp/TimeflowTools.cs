using System.Text.Json.Nodes;
using NesEmulator.Debugging.Timeflow;

namespace BrokenNes.Mcp;

/// <summary>The Timeflow tools' parts that need no session: listing the store, finding a timeline, opening a headless session on one's ROM.</summary>
internal static class TimeflowTools
{
    public static string Find(string idOrDir)
    {
        try { return TimeflowStore.Find(idOrDir) ?? throw new ToolError($"no timeline '{idOrDir}' under {TimeflowStore.Root()} (timeflow_list lists them)"); }
        catch (InvalidOperationException e) { throw new ToolError(e.Message); }
    }

    public static JsonObject List(string? project, string? timeline)
    {
        if (timeline is { Length: > 0 })
        {
            string dir = Find(timeline);
            using var rd = new TimelineReader(dir);
            var m = rd.Meta;
            return new JsonObject
            {
                ["id"] = m.Id, ["dir"] = dir, ["console"] = m.Console, ["rom"] = m.RomName, ["romSha256"] = m.RomSha256, ["cores"] = m.Cores, ["source"] = m.Source, ["note"] = m.Note,
                ["status"] = m.Status, ["stopReason"] = m.StopReason.Length > 0 ? m.StopReason : null, ["startFrame"] = m.StartFrame, ["frames"] = rd.Frames, ["endFrame"] = rd.EndFrame,
                ["parent"] = m.ParentId, ["parentFrame"] = m.ParentFrame, ["keep"] = m.Keep, ["bytes"] = TimeflowStore.DirBytes(dir),
                ["keyEvery"] = m.KeyEvery, ["pageSize"] = m.PageSize, ["codec"] = $"{m.Compression.ToString().ToLowerInvariant()}:{m.Level}",
                ["verifiedFrames"] = m.VerifiedFrames, ["verifyFailures"] = m.VerifyFailures, ["vars"] = J.Strs(m.Vars), ["domains"] = J.Strs(m.Domains.Select(d => $"{d.Id} {d.Size}")),
                ["created"] = m.CreatedUtc.ToString("o"),
            };
        }
        var games = new JsonArray();
        var dirs = project is { Length: > 0 } ? new List<string> { TimeflowStore.ProjectDir(project) } : TimeflowStore.GameDirs();
        foreach (string g in dirs)
        {
            var tl = new JsonArray();
            foreach (var (t, depth) in TimeflowPlayback.Tree(TimeflowStore.List(g)))
                tl.Add(new JsonObject
                {
                    ["id"] = t.Meta.Id, ["depth"] = depth, ["console"] = t.Meta.Console, ["rom"] = t.Meta.RomName, ["startFrame"] = t.Meta.StartFrame, ["frames"] = t.Meta.Frames,
                    ["parent"] = t.Meta.ParentId, ["parentFrame"] = t.Meta.ParentFrame, ["status"] = t.Meta.Status, ["source"] = t.Meta.Source, ["keep"] = t.Meta.Keep, ["bytes"] = t.Bytes,
                    ["created"] = t.Meta.CreatedUtc.ToString("o"),
                });
            games.Add(new JsonObject { ["gameDir"] = g, ["usedBytes"] = TimeflowStore.DirBytes(g), ["capBytes"] = TimeflowStore.CapOf(g), ["timelines"] = tl });
        }
        return new JsonObject { ["root"] = TimeflowStore.Root(), ["games"] = games };
    }

    /// <summary>
    /// T5's timeflow_patch / timeflow_resume with a patched ROM: byte patches, or a rebuild (a ROM file, or a project's build: built first when asked, the
    /// old build's symbols kept before it is rebuilt), on new headless machines (the Workshop's code: the same answers as the CLI).
    /// </summary>
    public static JsonObject Hotpatch(Args a, bool verdicts)
    {
        string dir = Find(a.Req("timeline"));
        var meta = TimelineMeta.Load(dir);
        var x = new BrokenNes.Workshop.Timeflow.HotpatchArgs
        {
            Timeline = dir, Frame = a.Long("frame") ?? throw new ToolError("'frame' is required"), To = a.Long("to"), Patches = a.List("patches"), Symbols = a.List("symbols"),
            NewSymbols = a.List("symbols_new"), Safe = a.List("safe"), AtSafe = a.Bool("at_safe", false), Branch = a.Bool("branch", true), VerdictOnOld = a.Bool("compare_old", true),
            Verdicts = verdicts ? a.List("verdicts") : new List<string>(), NewRom = a.Str("rom") ?? "",
        };
        if (x.Symbols.Count == 0) x.Symbols = BrokenNes.Workshop.Timeflow.TimeflowHotpatching.KeptSymbols(dir, meta.RomSha256);
        if (a.Str("project") is { Length: > 0 } project)
        {
            if (x.NewRom.Length > 0 || x.Patches.Count > 0) throw new ToolError("project is a rebuild: not with rom or patches");
            if (a.Bool("build", false))
            {
                // keep the old build's symbols first: the build overwrites the published ones
                try
                {
                    var old = BrokenDevBridge.Built(project);
                    if (x.Symbols.Count == 0 && File.Exists(old.Rom) && TimeflowStore.Sha256(File.ReadAllBytes(old.Rom)) == meta.RomSha256)
                        x.Symbols = BrokenNes.Workshop.Timeflow.TimeflowHotpatching.KeepSymbols(TimeflowStore.GameDirOf(dir), meta.RomSha256, old.Symbols);
                }
                catch (ToolError) { }
                var b = BrokenDevBridge.RunVerb("build", project, new List<string>(), new List<string>(), a.Int("timeout_sec", 1800));
                if (b["exitCode"]?.GetValue<int>() != 0) throw new ToolError($"the build of {project} failed: {b["output"]}");
            }
            var nb = BrokenDevBridge.Built(project);
            x.NewRom = nb.Rom; if (x.NewSymbols.Count == 0) x.NewSymbols = nb.Symbols; x.Build = $"project {project}, built {nb.BuiltAt}";
            if (x.Symbols.Count == 0) throw new ToolError($"the old build's symbols are not known: give symbols, or record the timeline with brokendev timeflow record (it keeps them), or use build: true while the project's last build is still the timeline's");
        }
        try { return BrokenNes.Workshop.Timeflow.TimeflowHotpatching.Run(x).Json; }
        catch (Exception e) when (e is IOException or FormatException or NotSupportedException or ArgumentException or InvalidDataException) { throw new ToolError(e.Message); }
    }

    /// <summary>
    /// The cross-console comparison (Sega VRUN ports, S0): a port project (its manifest's 'comparison' section, its last build) or raw files; the original a
    /// power-on timeline, or recorded now from the original project's last build (input + frames, or its verify scenario's pads). The Workshop's code.
    /// </summary>
    public static JsonObject Compare(Args a)
    {
        var x = new BrokenNes.Workshop.Timeflow.TimeflowComparing.CompareArgs
        {
            Original = a.Str("original") ?? "", Port = a.Str("port") ?? "", PortSymbols = a.List("port_symbols"), OriginalSymbols = a.List("original_symbols"),
            Map = a.Str("map") ?? "", PortConsole = a.Str("port_console") ?? "", Window = a.Int("window", 30), Open = a.Bool("open", true), Frames = a.Long("frames") ?? 0,
            Input = a.Str("input") ?? "",
        };
        if (x.Original.Length > 0) x.Original = Find(x.Original);
        if (a.Str("project") is { Length: > 0 } project)
        {
            var m = BrokenDevBridge.Manifest(project);
            string origId = m["comparison"]?["original"]?.GetValue<string>() ?? throw new ToolError($"project {project} has no comparison.original in its manifest");
            var port = BrokenDevBridge.Built(project);
            x.Port = port.Rom; if (x.PortSymbols.Count == 0) x.PortSymbols = port.Symbols; x.PortProject = project; x.PortConsole = port.Console;
            x.Manifest = Path.Combine(BrokenDevBridge.Repo, "BrokenDev", "projects", project, "project.json");
            if (x.Original.Length == 0)
            {
                var orig = BrokenDevBridge.Built(origId);
                x.OriginalRom = orig.Rom; x.OriginalProject = origId; if (x.OriginalSymbols.Count == 0) x.OriginalSymbols = orig.Symbols; x.OriginalConsole = orig.Console;
                if (x.Input.Length == 0 || x.Frames <= 0)
                {
                    // the original's verify scenario (the named one, else its first) gives the pads and the length
                    var om = BrokenDevBridge.Manifest(origId);
                    var scs = (om["verify"]?["scenarios"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                    string want = a.Str("scenario") ?? "";
                    var sc = scs.FirstOrDefault(s => want.Length == 0 || s["id"]?.GetValue<string>() == want) ?? throw new ToolError($"the original {origId} has no verify scenario '{want}'");
                    if (x.Input.Length == 0) x.Input = sc["input"]?.GetValue<string>() ?? "";
                    if (x.Frames <= 0) x.Frames = sc["frames"]?.GetValue<long>() ?? 0;
                }
            }
            else if (x.OriginalSymbols.Count == 0) x.OriginalSymbols = BrokenNes.Workshop.Timeflow.TimeflowHotpatching.KeptSymbols(x.Original, TimelineMeta.Load(x.Original).RomSha256);
        }
        else if (x.Map.Length == 0) throw new ToolError("give project (a port project with a comparison section), or map (a mapping JSON) with original, port and port_symbols");
        try { return BrokenNes.Workshop.Timeflow.TimeflowComparing.Compare(x); }
        catch (Exception e) when (e is IOException or FormatException or NotSupportedException or ArgumentException or InvalidDataException) { throw new ToolError(e.Message); }
    }

    /// <summary>The re-check after a rebuild: a port project's recorded sessions (or one timeline) replayed on the new build (the project built first when asked, or a ROM).</summary>
    public static JsonObject Recheck(Args a)
    {
        string timeline = a.Str("timeline") is { Length: > 0 } t ? Find(t) : "", rom = a.Str("rom") ?? "", map = a.Str("map") ?? "", manifest = "";
        var syms = a.List("symbols_new");
        string project = a.Str("project") ?? "";
        if (project.Length > 0)
        {
            if (a.Bool("build", false))
            {
                var b = BrokenDevBridge.RunVerb("build", project, new List<string>(), new List<string>(), a.Int("timeout_sec", 1800));
                if (b["exitCode"]?.GetValue<int>() != 0) throw new ToolError($"the build of {project} failed: {b["output"]}");
            }
            var nb = BrokenDevBridge.Built(project);
            if (rom.Length == 0) rom = nb.Rom;
            if (syms.Count == 0) syms = nb.Symbols;
            manifest = Path.Combine(BrokenDevBridge.Repo, "BrokenDev", "projects", project, "project.json");
        }
        try { return BrokenNes.Workshop.Timeflow.TimeflowComparing.Recheck(timeline, timeline.Length > 0 ? "" : project, "", rom, syms, a.List("symbols_old"), map, manifest); }
        catch (Exception e) when (e is IOException or FormatException or NotSupportedException or ArgumentException or InvalidDataException) { throw new ToolError(e.Message); }
    }

    /// <summary>A new headless session on a timeline's own ROM (the copy in its game folder) and cores.</summary>
    public static HeadlessSession OpenFor(string dir, List<string> symbols, bool callStack)
    {
        var m = TimelineMeta.Load(dir);
        byte[] rom;
        try { rom = TimeflowStore.RomOf(dir, m); } catch (Exception e) when (e is FileNotFoundException or InvalidDataException) { throw new ToolError(e.Message); }
        // the session opens a file: a scratch copy of the stored ROM under its own name (the server's own folder, never the user's)
        string tmp = Path.Combine(Path.GetTempPath(), "brokennes-mcp-timeflow-" + Environment.ProcessId, m.RomSha256[..16]);
        Directory.CreateDirectory(tmp);
        string path = Path.Combine(tmp, m.RomName.Length > 0 ? m.RomName : "rom" + m.RomExt);
        if (!File.Exists(path)) File.WriteAllBytes(path, rom);
        if (m.Ntsc) throw new ToolError($"timeline {m.Id} ran the NES at NTSC's frame length, which headless MCP sessions do not set yet: use brokendev.ps1 timeflow jump / resume (the Workshop sets it)");
        var cp = m.Cores.Split('/');
        return HeadlessSession.Open(Sessions.NewId("h"), path, m.Console, cp.Length == 3 ? cp[0] : "", cp.Length == 3 ? cp[1] : "", cp.Length == 3 ? cp[2] : "", symbols, callStack, false, new List<string>(), new List<string>());
    }
}
