using System.Text.Json.Nodes;
using NesEmulator.Debugging.Movies;

namespace BrokenNes.Mcp;

/// <summary>
/// AutoTAS (docs/projects/autotas-plan.md): save states as files (a chapter's milestone state, next to its segment), a session started from such
/// a file (its movie then starts from that state: a chapter segment), and the milestone detector play_step evaluates after every frame.
/// </summary>
internal sealed partial class HeadlessSession
{
    private string RomSha256() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(romPath))).ToLowerInvariant();

    /// <summary>Writes the machine's state to an AutoTAS state file (<see cref="AutoTasState"/>); with a name it is also a named state in memory.</summary>
    public JsonObject SaveStateFile(string? name, string path)
    {
        string? note = EnsureBetweenFrames();
        var s = Take();
        if (name is { Length: > 0 }) states[name] = s;
        var f = new AutoTasState { Console = nes != null ? "nes" : ConsoleKey, Cores = cores, Frame = s.FramesStarted, RomSha256 = RomSha256(), State = s.State };
        f.Save(path);
        var j = new JsonObject
        {
            ["state"] = name, ["file"] = Path.GetFullPath(path), ["frame"] = s.FramesStarted, ["bytes"] = new FileInfo(path).Length, ["format"] = f.Format,
            ["where"] = "a file (an AutoTAS state: load_state path loads it, a movie recorded after the load starts from it)",
        };
        if (note != null) j["note"] = note;
        return j;
    }

    /// <summary>Loads an AutoTAS state file: the session's history (and so its next movie) starts from it.</summary>
    public JsonObject LoadStateFile(string? name, string path)
    {
        if (!File.Exists(path)) throw new ToolError($"no state file {path}");
        AutoTasState f;
        try { f = AutoTasState.Load(path); } catch (InvalidDataException e) { throw new ToolError(e.Message); }
        string con = nes != null ? "nes" : ConsoleKey;
        if (!f.Console.Equals(con, StringComparison.OrdinalIgnoreCase)) throw new ToolError($"the state is for {f.Console}, this session is {con}");
        var notes = new JsonArray();
        if (!f.Cores.Equals(cores, StringComparison.OrdinalIgnoreCase)) notes.Add($"the state was saved on cores {f.Cores}, this session runs {cores}");
        if (f.RomSha256.Length > 0 && !f.RomSha256.Equals(RomSha256(), StringComparison.OrdinalIgnoreCase)) notes.Add("the state was saved on another ROM (sha256 differs)");
        string key = name is { Length: > 0 } ? name : "file:" + Path.GetFileName(path);
        states[key] = new Snapshot { Frame = f.Frame, FramesStarted = f.Frame, State = f.State };
        var j = LoadState(key);
        j["file"] = Path.GetFullPath(path); j["fileFrame"] = f.Frame; j["format"] = f.Format;
        j["movie"] = "a recording started now is a segment from this state (record start)";
        if (notes.Count > 0) j["warnings"] = notes;
        return j;
    }

    /// <summary>The milestones play_step watches: inline "id=expr" entries, then a milestones.json file's.</summary>
    internal static List<AutoTasMilestone> Milestones(List<string> inline, string? file)
    {
        var l = inline.Select(AutoTasMilestone.ParseInline).ToList();
        if (file is { Length: > 0 })
        {
            if (!File.Exists(file)) throw new ToolError($"no milestone file {file}");
            try { l.AddRange(AutoTasMilestone.ParseFile(File.ReadAllText(file))); }
            catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException or KeyNotFoundException) { throw new ToolError($"milestone file: {e.Message}"); }
        }
        return l;
    }

    /// <summary>The first milestone whose condition is true now, or null.</summary>
    private AutoTasMilestone? MilestoneHit(List<AutoTasMilestone> ms)
    {
        var d = Dbg;
        foreach (var m in ms)
        {
            long v;
            try { v = d.Evaluate(m.Until); }
            catch (Exception e) when (e is FormatException or ArgumentException) { throw new ToolError($"milestone {m.Id}: {e.Message}"); }
            if (v != 0) return m;
        }
        return null;
    }
}
