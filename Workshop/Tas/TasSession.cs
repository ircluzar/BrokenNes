// TAS workline T2 (docs/projects/tas-format.md "Precision"): frame-level tools over the runner's machines.
//   TasPlayer  - plays a whole run frame by frame (chapter starts, ops), the engine of --tas diff and of opening an edit session.
//   TasSession - one chapter under the cursor: step N frames (with input edits), rewind/seek to any frame through an in-memory
//                savestate ring, set/insert/delete input at any frame, lag frames (no controller poll), goals, save as a new run.
//   TasDiff    - "why did this frame differ": two runs in lockstep, the first frame whose picture or RAM differs, which bytes / named
//                variables / picture region, and the input difference that came before it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using BrokenNes.Workshop.Tas.Script;

namespace BrokenNes.Workshop.Tas;

/// <summary>Plays a run (all chapters, or up to one) one frame at a time, applying the ops the runner applies (poke, reset, loadstate, console ops).</summary>
public sealed class TasPlayer : IDisposable
{
    public readonly TasRun Run; public readonly string Rom; readonly TasOpenOptions open;
    public ITasTarget? T { get; private set; }
    public int ChapterIndex = -1; public TasChapter? Chapter; public List<TasStep> Steps = new(); public int Pos; public int Frame; public int Global;
    public TasPad[] LastPads = Array.Empty<TasPad>(); public int LastPolls;
    readonly Dictionary<string, byte[]> named = new();
    readonly string scratch;

    public TasPlayer(TasRun run, string? rom, TasOpenOptions o, string scratchDir)
    {
        Run = run; Rom = TasRunner.FindRom(run, rom); open = o; scratch = scratchDir;
        if (run.Cores.Length > 0 && o.Cores.Length == 0) o.Cores = run.Cores;
        o.PowerOn = run.PowerOn; o.Ntsc = run.Ntsc;
        Directory.CreateDirectory(scratch);
    }

    /// <summary>Moves to the start of the next chapter (opening / loading the machine). False at the end of the run.</summary>
    public bool NextChapter()
    {
        if (ChapterIndex + 1 >= Run.Chapters.Count) return false;
        var ch = Run.Chapters[++ChapterIndex]; Chapter = ch;
        if (ch.Start == "power-on" || T == null)
        {
            T?.Dispose();
            var oo = new TasOpenOptions { Cores = open.Cores, PowerOn = open.PowerOn, Ntsc = open.Ntsc, N64Renderer = open.N64Renderer, PakDir = open.PakDir ?? Path.Combine(scratch, "pak") };
            Directory.CreateDirectory(oo.PakDir);
            T = TasTargets.Open(Run.Console, Rom, oo);
        }
        if (ch.Start.StartsWith("state:")) T.LoadState(File.ReadAllBytes(Run.Resolve(ch.Start[6..])));
        Drain();
        Steps = TasRunner.Expand(Run, ch, T.Fps).Steps; Pos = 0; Frame = 0;
        return true;
    }

    readonly short[] buf = new short[1 << 16];
    public void Drain() { if (T == null) return; while (T.ReadSamples(buf) > 0) { } T.InputPolls(); }

    /// <summary>Runs the ops before the next frame, then the frame. False when the chapter has no more frames (its trailing ops are run).</summary>
    public bool StepFrame()
    {
        while (Pos < Steps.Count && !Steps[Pos].IsFrame) Op(Steps[Pos++]);
        if (Pos >= Steps.Count) return false;
        var s = Steps[Pos++];
        T!.SetPads(s.Pads); T.RunFrame(); LastPads = s.Pads;
        LastPolls = T.InputPolls(); while (T.ReadSamples(buf) > 0) { }
        Frame++; Global++;
        return true;
    }

    /// <summary>The next frame of the whole run (crossing chapters). False at the end of the run.</summary>
    public bool Step()
    {
        if (ChapterIndex < 0 && !NextChapter()) return false;
        while (!StepFrame()) if (!NextChapter()) return false;
        return true;
    }

    void Op(TasStep s)
    {
        var t = T!;
        switch (s.Op)
        {
            case "poke": t.Poke(s.Args[0], Convert.FromHexString(s.Args[1].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s.Args[1][2..] : s.Args[1])); break;
            case "reset": t.Reset(); break;
            case "savestate": named[s.Args.Length > 0 ? s.Args[0] : "state"] = t.SaveState(); break;
            case "loadstate":
            {
                var a = s.Args[0];
                if (named.TryGetValue(a, out var st)) { t.LoadState(st); t.InputPolls(); break; }
                var cand = new[] { a.EndsWith(".state") ? Run.Resolve(a) : "", Run.Resolve(a + ".state") };
                var f = cand.FirstOrDefault(x => x.Length > 0 && File.Exists(x)) ?? throw new FileNotFoundException($"loadstate {a}: no state");
                t.LoadState(File.ReadAllBytes(f)); t.InputPolls(); break;
            }
            case "checkpoint": case "screenshot": case "mark": case "peek": case "savefile": break;
            default: if (!t.ConsoleOp(s.Op!, s.Args, scratch)) throw new FormatException($"line {s.Line}: '{s.Op}' is not a {t.Console} command"); break;
        }
    }

    /// <summary>Runs the ops at the current position (before the frame at Pos) without running a frame.</summary>
    public void OpsHere() { while (Pos < Steps.Count && !Steps[Pos].IsFrame) Op(Steps[Pos++]); }

    public void Dispose() => T?.Dispose();

    /// <summary>RAM bytes for diffs (NES: the 2 KB of CPU RAM; N64: RDRAM, after the RDP drain; others: none).</summary>
    public static byte[]? RamBytes(ITasTarget t) => t.Console switch
    {
        "nes" => t.Peek("0", 0x800),
        "n64" => RamN64(t),
        _ => null,
    };
    static byte[] RamN64(ITasTarget t) { t.RamHash(); return t.Peek("0", 0x800000); }
}

/// <summary>An edit session on one chapter: a cursor, an input list you can change at any frame, rewind through a savestate ring.</summary>
public sealed class TasSession : IDisposable
{
    public readonly TasRun Run; public readonly TasChapter Chapter; public readonly TasPlayer P; public readonly TasRamMap? Map;
    public readonly int Every; readonly int maxStates;
    public List<TasStep> Steps;              // the chapter's program (frames + ops), edited in place
    readonly List<int> frameAt = new();       // frame index -> step index
    readonly SortedDictionary<int, byte[]> ring = new();   // frame -> state before the ops + frame at that position
    public int Cursor;                        // frames run since the chapter start
    readonly Dictionary<int, bool> lagAt = new();
    public int Frames => frameAt.Count;
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];

    public TasSession(TasRun run, string chapterId, string? rom, TasOpenOptions o, string scratch, int every = 0)
    {
        Run = run; Chapter = run.Chapters.FirstOrDefault(c => c.Id == chapterId) ?? throw new ArgumentException($"no chapter '{chapterId}' (have {string.Join(", ", run.Chapters.Select(c => c.Id))})");
        P = new TasPlayer(run, rom, o, scratch);
        while (P.NextChapter() && P.Chapter != Chapter) while (P.StepFrame()) { }
        if (P.Chapter != Chapter) throw new ArgumentException($"chapter {chapterId} not reached");
        Every = every > 0 ? every : run.Console == "n64" ? 120 : 10;
        maxStates = run.Console == "n64" ? 200 : 20000;
        Steps = P.Steps; Index();
        Map = TasRamMap.For(run);
        ring[0] = P.T!.SaveState();
    }

    void Index() { frameAt.Clear(); for (int i = 0; i < Steps.Count; i++) if (Steps[i].IsFrame) frameAt.Add(i); }
    int PosOf(int frame) => frame < frameAt.Count ? frameAt[frame] : Steps.Count;
    // the position right after frame f-1 (ops before frame f belong to f)
    int StartOf(int frame) => frame == 0 ? 0 : frameAt[frame - 1] + 1;

    public ITasTarget T => P.T!;

    /// <summary>Runs n frames from the cursor. With pads, those frames' input becomes pads (frames past the end are appended).</summary>
    public List<JsonObject> Step(int n, TasPad[]? pads = null, bool perFrame = false)
    {
        var outp = new List<JsonObject>();
        if (pads != null) Set(Cursor, n, pads, append: true);
        for (int k = 0; k < n && Cursor < Frames; k++)
        {
            if (Cursor % Every == 0 && !ring.ContainsKey(Cursor)) { ring[Cursor] = T.SaveState(); Trim(); }
            P.Pos = StartOf(Cursor); P.Frame = Cursor;
            if (!P.StepFrame()) break;
            Cursor++;
            bool lag = P.LastPolls == 0; if (P.LastPolls >= 0) lagAt[Cursor - 1] = lag;
            if (perFrame) outp.Add(new JsonObject { ["frame"] = Cursor, ["input"] = PadText(P.LastPads), ["lag"] = P.LastPolls < 0 ? null : lag });
        }
        return outp;
    }

    void Trim() { while (ring.Count > maxStates) { var k = ring.Keys.Where(x => x != 0).ElementAt(ring.Count % 2 == 0 ? 1 : 0); ring.Remove(k); } }

    /// <summary>Puts the machine at frame f (frames run = f): the nearest ring state at or before f, then replays.</summary>
    public void Seek(int f)
    {
        f = Math.Clamp(f, 0, Frames);
        if (f >= Cursor && f - Cursor < Every * 2) { Step(f - Cursor); return; }
        var k = ring.Keys.Where(x => x <= f).Max();
        T.LoadState(ring[k]); P.Drain(); Cursor = k;
        Step(f - Cursor);
    }

    void Invalidate(int fromFrame)
    {
        foreach (var k in ring.Keys.Where(x => x > fromFrame).ToList()) ring.Remove(k);
        foreach (var k in lagAt.Keys.Where(x => x >= fromFrame).ToList()) lagAt.Remove(k);
    }

    /// <summary>Sets the input of frames [f, f+n). append: frames past the end are added.</summary>
    public void Set(int f, int n, TasPad[] pads, bool append = false)
    {
        if (f > Frames) throw new ArgumentException($"frame {f} is past the chapter's end ({Frames})");
        for (int i = f; i < f + n; i++)
        {
            var p = Fit(pads);
            if (i < Frames) Steps[frameAt[i]] = new TasStep { Pads = p, Line = Steps[frameAt[i]].Line };
            else if (append) { Steps.Add(new TasStep { Pads = p }); frameAt.Add(Steps.Count - 1); }
            else break;
        }
        Changed(f);
    }

    public void Insert(int f, int n, TasPad[] pads)
    {
        if (f > Frames) throw new ArgumentException($"frame {f} is past the chapter's end ({Frames})");
        int at = PosOf(f);
        Steps.InsertRange(at, Enumerable.Range(0, n).Select(_ => new TasStep { Pads = Fit(pads) }));
        Index(); Changed(f);
    }

    public void Delete(int f, int n)
    {
        for (int k = 0; k < n && f < Frames; k++) { Steps.RemoveAt(frameAt[f]); Index(); }
        Changed(f);
    }

    /// <summary>A copy of the program (to try variants and come back).</summary>
    public List<TasStep> Snapshot() => new(Steps);
    public void Restore(List<TasStep> steps) { Steps.Clear(); Steps.AddRange(steps); Index(); Changed(0); }

    TasPad[] Fit(TasPad[] p) { var r = new TasPad[Math.Max(T.Map.Pads, p.Length)]; Array.Copy(p, r, p.Length); return r; }

    void Changed(int f)
    {
        Invalidate(f);
        if (Cursor > f) { int c = Cursor; var k = ring.Keys.Where(x => x <= f).Max(); T.LoadState(ring[k]); P.Drain(); Cursor = k; Step(c - k); }
    }

    public string PadText(TasPad[] p) => string.Join(" ", p.Take(Math.Max(1, PadsUsed)).Select(x => TasScript.PadText(T.Map, x)));
    int PadsUsed => Math.Max(1, Steps.Where(s => s.IsFrame).Select(s => Array.FindLastIndex(s.Pads, x => x.Buttons != 0 || x.X != 0 || x.Y != 0) + 1).DefaultIfEmpty(1).Max());

    public TasPad[] Parse(string input)
    {
        // "A+B+Right" (pad 1), "A|Start" (pad 1 | pad 2), stick "A@-20,80"
        var parts = input.Split('|'); var r = new TasPad[Math.Max(T.Map.Pads, parts.Length)];
        for (int i = 0; i < parts.Length; i++)
        {
            var s = parts[i].Trim(); int at = s.IndexOf('@'); sbyte x = 0, y = 0;
            if (at >= 0) { var xy = s[(at + 1)..].Split(','); x = sbyte.Parse(xy[0]); y = sbyte.Parse(xy[1]); s = s[..at]; }
            r[i] = new TasPad(T.Map.Parse(s.Length == 0 ? "-" : s), x, y);
        }
        return r;
    }

    /// <summary>Where the cursor is: frame, the next frames' input, vars, lag so far, picture hash.</summary>
    public JsonObject Status(int show = 8)
    {
        var next = new JsonArray();
        for (int i = Cursor; i < Math.Min(Frames, Cursor + show); i++) next.Add($"{i}: {PadText(Steps[frameAt[i]].Pads)}");
        var lagList = lagAt.Where(kv => kv.Value).Select(kv => kv.Key).OrderBy(x => x).ToList();
        return new JsonObject
        {
            ["session"] = Id, ["chapter"] = Chapter.Id, ["frame"] = Cursor, ["frames"] = Frames, ["picture"] = T.PictureHash().ToString("X16"),
            ["vars"] = Map?.Snapshot(T), ["nextInput"] = next, ["lagSeen"] = lagList.Count, ["lagFrames"] = new JsonArray(lagList.TakeLast(40).Select(x => (JsonNode)x).ToArray()),
            ["ringStates"] = ring.Count,
        };
    }

    /// <summary>Plays the whole chapter from its start with its goals (the cursor ends at the end). Returns frames, goals, lag.</summary>
    public JsonObject Evaluate()
    {
        var goals = Chapter.Goal.Select(TasGoal.Parse).ToList(); var per = goals.Where(g => g.PerFrame).ToList();
        Seek(0); int lag = 0; var lags = new List<int>();
        while (Cursor < Frames)
        {
            Step(1);
            if (P.LastPolls == 0) { lag++; lags.Add(Cursor - 1); }
            if (per.Count > 0) { var c = new TasEvalContext(T, Map, Run.Dir, Cursor, lag); foreach (var g in per) g.OnFrame(c); }
        }
        P.Pos = Steps.Count; // trailing ops are not run here (savefile/screenshot are the runner's)
        foreach (var g in goals) g.Finish(new TasEvalContext(T, Map, Run.Dir, Cursor, lag));
        return new JsonObject
        {
            ["chapter"] = Chapter.Id, ["frames"] = Frames, ["goal"] = goals.Count == 0 ? null : goals.All(g => g.Met == true),
            ["goals"] = new JsonArray(goals.Select(g => (JsonNode)g.ToJson()).ToArray()), ["lag"] = lag, ["lagFrames"] = new JsonArray(lags.Take(200).Select(x => (JsonNode)x).ToArray()),
            ["end"] = new JsonObject { ["picture"] = T.PictureHash().ToString("X16"), ["ram"] = T.RamHash().ToString("X16") }, ["vars"] = Map?.Snapshot(T),
        };
    }

    /// <summary>Writes the edited chapter (DIR/&lt;id&gt;.tlog) and DIR/edited.tasrun.json: the original run with this chapter's script replaced
    /// (paths absolute, the chapter's expect dropped). trimTo: cut the chapter after that many frames (e.g. the goal's first frame).</summary>
    public string Save(string dir, int trimTo = -1)
    {
        dir = Path.GetFullPath(dir); Directory.CreateDirectory(dir);
        var prog = new TasProgram { Console = Run.Console, Map = T.Map, Fps = T.Fps, PadsUsed = PadsUsed };
        int f = 0; foreach (var s in Steps) { if (trimTo >= 0 && f >= trimTo && s.IsFrame) break; prog.Steps.Add(s); if (s.IsFrame) f++; }
        var log = Path.Combine(dir, Chapter.Id + ".tlog");
        using (var w = new StreamWriter(log)) TasScript.WriteLog(prog, w);
        var j = Run.Raw.Count > 0 ? (JsonObject)Run.Raw.DeepClone() : new JsonObject { ["schema"] = 1, ["console"] = Run.Console, ["chapters"] = new JsonArray(new JsonObject { ["id"] = Chapter.Id, ["start"] = Chapter.Start }) };
        if (Run.Rom.Length > 0) j["rom"] = Run.Resolve(Run.Rom); else j["rom"] = P.Rom;
        if (j["ramdb"] is JsonNode rd) j["ramdb"] = Run.Resolve((string)rd!);
        else if (Map != null) j["ramdb"] = Map.Path;
        j["edited_from"] = new JsonObject { ["run"] = Run.Path, ["chapter"] = Chapter.Id };
        foreach (var c in j["chapters"]!.AsArray())
        {
            var o = c!.AsObject();
            if ((string?)o["start"] is string st && st.StartsWith("state:")) o["start"] = "state:" + Run.Resolve(st[6..]);
            if ((string?)o["id"] == Chapter.Id) { o["script"] = log; o.Remove("expect"); }
            else o["script"] = Run.Resolve((string)o["script"]!);
        }
        var path = Path.Combine(dir, "edited.tasrun.json");
        File.WriteAllText(path, j.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    public void Dispose() => P.Dispose();

    // in-process sessions (MCP)
    public static readonly Dictionary<string, TasSession> Open = new();
}

/// <summary>--tas diff A B: two runs in lockstep; the first diverging frame and what differs there.</summary>
public static class TasDiff
{
    public static JsonObject Run(TasRun a, TasRun b, string? romA, string? romB, string outDir, int ramEvery = 1, int maxFrames = int.MaxValue)
    {
        outDir = Path.GetFullPath(outDir); Directory.CreateDirectory(outDir);
        using var pa = new TasPlayer(a, romA, new TasOpenOptions(), Path.Combine(outDir, "a"));
        using var pb = new TasPlayer(b, romB, new TasOpenOptions(), Path.Combine(outDir, "b"));
        var map = TasRamMap.For(a) ?? TasRamMap.For(b);
        var res = new JsonObject { ["a"] = a.Path, ["b"] = b.Path };
        int firstInput = -1, firstPic = -1, firstRam = -1; JsonObject? inputDiff = null;
        var histA = new Queue<string>(); var histB = new Queue<string>();
        string Where(TasPlayer p) => $"{p.Chapter?.Id}@{p.Frame}";
        while (true)
        {
            bool sa = pa.Step(), sb = pb.Step();
            if (!sa || !sb)
            {
                if (sa != sb) res["lengths"] = $"{(sa ? "B" : "A")} ended at frame {(sa ? pb.Global : pa.Global)}, the other runs on";
                break;
            }
            int g = pa.Global;
            string ia = pa.T!.Map.Text(pa.LastPads[0].Buttons), ib = pb.T!.Map.Text(pb.LastPads[0].Buttons);
            if (pa.LastPads.Length > 1) { ia += "|" + TasScript.PadText(pa.T.Map, pa.LastPads[1]); ib += "|" + TasScript.PadText(pb.T.Map, pb.LastPads[1]); }
            histA.Enqueue($"{g - 1} {Where(pa)} {ia}{(pa.LastPolls == 0 ? " (lag)" : "")}"); histB.Enqueue($"{g - 1} {Where(pb)} {ib}{(pb.LastPolls == 0 ? " (lag)" : "")}");
            if (histA.Count > 12) { histA.Dequeue(); histB.Dequeue(); }
            if (firstInput < 0 && !pa.LastPads.Take(Math.Min(pa.LastPads.Length, pb.LastPads.Length)).SequenceEqual(pb.LastPads.Take(Math.Min(pa.LastPads.Length, pb.LastPads.Length))))
            {
                firstInput = g - 1;
                inputDiff = new JsonObject { ["frame"] = g - 1, ["a"] = $"{Where(pa)} {ia}", ["b"] = $"{Where(pb)} {ib}", ["aLag"] = pa.LastPolls == 0, ["bLag"] = pb.LastPolls == 0,
                    ["note"] = pa.LastPolls == 0 && pb.LastPolls == 0 ? "both lag frames: the game did not read this input" : null };
            }
            if (firstPic < 0 && pa.T.PictureHash() != pb.T.PictureHash())
            {
                firstPic = g - 1;
                res["picture"] = PictureDiff(pa.T, pb.T, outDir, g - 1);
                res["picture"]!["where"] = $"A {Where(pa)}, B {Where(pb)}";
            }
            if (firstRam < 0 && (g - 1) % ramEvery == 0)
            {
                var ra = TasPlayer.RamBytes(pa.T); var rb = TasPlayer.RamBytes(pb.T);
                bool differ = ra != null && rb != null ? !ra.AsSpan().SequenceEqual(rb) : pa.T.RamHash() != pb.T.RamHash();
                if (differ)
                {
                    firstRam = g - 1;
                    var r = new JsonObject { ["frame"] = g - 1, ["where"] = $"A {Where(pa)}, B {Where(pb)}" };
                    if (ra != null && rb != null)
                    {
                        var bytes = new JsonArray(); int count = 0;
                        for (int i = 0; i < Math.Min(ra.Length, rb.Length); i++) if (ra[i] != rb[i]) { if (count < 64) bytes.Add($"{i:X4}: {ra[i]:X2} -> {rb[i]:X2}"); count++; }
                        r["bytesDiffering"] = count; r["bytes"] = bytes;
                    }
                    if (map != null)
                    {
                        var va = map.Snapshot(pa.T); var vb = map.Snapshot(pb.T); var vd = new JsonObject();
                        foreach (var kv in va) if (kv.Value?.ToJsonString() != vb[kv.Key]?.ToJsonString()) vd[kv.Key] = $"{kv.Value?.ToJsonString()} -> {vb[kv.Key]?.ToJsonString()}";
                        r["vars"] = vd;
                    }
                    r["recentInputA"] = new JsonArray(histA.Select(x => (JsonNode)x!).ToArray()); r["recentInputB"] = new JsonArray(histB.Select(x => (JsonNode)x!).ToArray());
                    res["ram"] = r;
                }
            }
            if ((firstPic >= 0 && firstRam >= 0) || g >= maxFrames) break;
        }
        res["frames"] = pa.Global;
        res["firstInputDiff"] = inputDiff; res["firstPictureDiff"] = firstPic >= 0 ? firstPic : null; res["firstRamDiff"] = firstRam >= 0 ? firstRam : null;
        int first = new[] { firstPic, firstRam }.Where(x => x >= 0).DefaultIfEmpty(-1).Min();
        res["identical"] = first < 0 && res["lengths"] == null;
        res["why"] = first < 0 ? (res["lengths"] == null ? "no picture or RAM difference" : "same until one run ended") :
            firstInput < 0 ? $"frame {first} differs with identical input before it: not an input change (start state, ROM, cores or a nondeterminism)" :
            firstInput <= first ? $"input first differs at frame {firstInput}; the machine diverges {first - firstInput} frame(s) later at frame {first}" :
            $"frame {first} differs before any input difference (input differs later at {firstInput}): start state / ops / cores";
        File.WriteAllText(Path.Combine(outDir, "diff.json"), res.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return res;
    }

    static JsonObject PictureDiff(ITasTarget a, ITasTarget b, string dir, int frame)
    {
        var o = new JsonObject { ["frame"] = frame };
        if (a.Width != b.Width || a.Height != b.Height) { o["size"] = $"{a.Width}x{a.Height} vs {b.Width}x{b.Height}"; return o; }
        int w = a.Width, h = a.Height; var pa = (uint[])a.Argb.Clone(); var pb = b.Argb; var d = new uint[w * h];
        int x0 = w, y0 = h, x1 = -1, y1 = -1, n = 0;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            int i = y * w + x;
            if ((pa[i] & 0xFFFFFF) != (pb[i] & 0xFFFFFF)) { n++; d[i] = 0xFFFF00FF; x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
            else d[i] = 0xFF000000 | ((pa[i] >> 2) & 0x3F3F3F);
        }
        TasPng.Write(Path.Combine(dir, "a.png"), pa, w, h); TasPng.Write(Path.Combine(dir, "b.png"), pb, w, h); TasPng.Write(Path.Combine(dir, "diff.png"), d, w, h);
        o["pixels"] = n; o["box"] = n > 0 ? $"x {x0}-{x1}, y {y0}-{y1}" : null; o["pngs"] = dir;
        return o;
    }
}
