using NesEmulator.Systems;
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

    /// <summary>
    /// AutoTAS search in one call: from the machine as it is now, tries each plan ("Right+B:10 Right+B+A:{..}" already filled in),
    /// then holds <paramref name="then"/> until <paramref name="want"/> (at most <paramref name="within"/> frames), optionally checks the want
    /// still holds <paramref name="settle"/> frames later and that the player lives <paramref name="survive"/> more frames. A frame where
    /// <paramref name="dead"/> is true ends a try. The machine is put back exactly as it was afterwards. One call instead of ~5 per try.
    /// </summary>
    public JsonObject TryPlans(List<string> plans, string[] then, int within, string want, string? dead, int settle, int survive, List<string> ram, string[]? surviveWith = null)
    {
        if (plans.Count == 0) throw new ToolError("give at least one plan");
        if (plans.Count > 2000) throw new ToolError("at most 2000 plans a call");
        var d = Dbg;
        EnsureBetweenFrames();
        var baseState = SaveRaw();
        long f0 = Interlocked.Read(ref framesStarted);
        var thenPad = ParseButtons(then);
        var survivePad = surviveWith == null ? thenPad : ParseButtons(surviveWith);
        bool Eval(string e) { try { return d.Evaluate(e) != 0; } catch (Exception x) when (x is FormatException or ArgumentException) { throw new ToolError($"{e}: {x.Message}"); } }
        bool hasDead = dead is { Length: > 0 };
        // one frame with these pads; false = died
        bool Frame(PadButtons p) { lock (padLock) held[0] = p; RunFrames(1); return !(hasDead && Eval(dead!)); }
        var results = new JsonArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            for (int k = 0; k < plans.Count; k++)
            {
                LoadRaw(baseState); Interlocked.Exchange(ref framesStarted, f0);
                int t = 0; string verdict = "ok";
                foreach (var tok in plans[k].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    int c = tok.LastIndexOf(':');
                    string bt = c < 0 ? tok : tok[..c]; int n = c < 0 ? 1 : int.Parse(tok[(c + 1)..]);
                    var pad = ParseButtons(bt is "none" or "-" or "" ? Array.Empty<string>() : bt.Split('+'));
                    for (int i = 0; i < n && verdict == "ok"; i++) { if (!Frame(pad)) verdict = "died"; t++; }
                    if (verdict != "ok") break;
                }
                int reach = -1;
                if (verdict == "ok")
                {
                    for (int i = 0; i < within; i++)
                    {
                        if (!Frame(thenPad)) { verdict = "died"; break; }
                        t++;
                        if (Eval(want)) { reach = t; break; }
                    }
                    if (verdict == "ok" && reach < 0) verdict = "missed";
                }
                JsonObject? vals = null;
                if (verdict == "ok")
                {
                    vals = new JsonObject();
                    foreach (var w in ram) { int eq = w.IndexOf('='); string nm = eq > 0 ? w[..eq] : w, ex = eq > 0 ? w[(eq + 1)..] : w; try { vals[nm] = d.Evaluate(ex); } catch (Exception) { vals[nm] = null; } }
                    if (settle > 0)
                    {
                        for (int i = 0; i < settle; i++) if (!Frame(thenPad)) { verdict = "died"; break; }
                        if (verdict == "ok" && !Eval(want)) verdict = "unsettled";
                    }
                    if (verdict == "ok" && survive > 0)
                        for (int i = 0; i < survive; i++) if (!Frame(survivePad)) { verdict = "doomed"; break; }
                }
                var o = new JsonObject { ["i"] = k, ["verdict"] = verdict };
                if (verdict == "ok") { o["frames"] = reach; o["ram"] = vals; }
                results.Add(o);
            }
        }
        finally
        {
            lock (padLock) { held[0] = PadButtons.None; held[1] = PadButtons.None; }
            LoadRaw(baseState); Interlocked.Exchange(ref framesStarted, f0);
            FreshDebugState();
        }
        return new JsonObject { ["results"] = results, ["plans"] = plans.Count, ["ms"] = sw.ElapsedMilliseconds, ["how"] = "verdict ok (frames = plan + then until want), died, missed (never met want), unsettled, doomed (dies within survive); the machine is put back" };
    }
}
