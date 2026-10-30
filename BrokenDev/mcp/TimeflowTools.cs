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
