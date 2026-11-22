// TAS workline T1 (docs/projects/tas-format.md): runs expanded TAS programs on a target, chapter by chapter, with checkpoints
// (video/audio/RAM hashes, blank check, PNGs, states), goals, the expected-checkpoint check and the run-twice determinism check.
// ITasFrameSink is the console-neutral offline-replay hook (every frame's picture + the core's audio): the MP4 renderer plugs in there.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BrokenNes.Workshop.Tas.Script;

namespace BrokenNes.Workshop.Tas;

/// <summary>Receives every emulated frame and its sound during a replay (render to video, dump frames...).</summary>
public interface ITasFrameSink
{
    void Begin(int width, int height, double fps, int sampleRate);
    /// <summary>The picture after a frame (0xAARRGGBB; the size may change mid-run on some consoles).</summary>
    void Frame(uint[] argb, int width, int height);
    /// <summary>Stereo interleaved samples the core produced during that frame.</summary>
    void Audio(ReadOnlySpan<short> stereo);
    /// <summary>The core's current sample rate (N64: the game sets the AI rate, it can change mid-run); called before each frame's Audio.</summary>
    void Rate(int sampleRate) { }
    void End();
}

public sealed class TasCheckpoint
{
    public string Name = "", Kind = "", Video = "", Picture = "", Audio = "", Ram = "";
    public string? File, Chapter;
    public int Frame; public long Samples; public double Rms, Peak, DominantFraction; public bool Solid;
    public JsonObject ToJson() => new()
    {
        ["name"] = Name, ["kind"] = Kind, ["chapter"] = Chapter, ["frame"] = Frame, ["video"] = Video, ["picture"] = Picture, ["audio"] = Audio, ["ram"] = Ram,
        ["samples"] = Samples, ["rms"] = Rms, ["peak"] = Peak, ["solid"] = Solid, ["dominantFraction"] = DominantFraction, ["file"] = File,
    };
}

public sealed class TasChapter
{
    public string Id = "", Title = "", Start = "power-on", Script = "";
    public List<string> Goal = new();
    public JsonObject? Expect;
    public string? Notes;
}

/// <summary>A run: chapters on one ROM, each starting from power-on, a state file, or where the previous one ended.</summary>
public sealed class TasRun
{
    public string Path = "", Dir = "", Console = "", Game = "", RomSha1 = "", Rom = "", Cores = "", PowerOn = "fceux";
    public bool Ntsc;
    public List<TasChapter> Chapters = new();
    public JsonObject Raw = new();

    public static TasRun Load(string path)
    {
        var j = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var r = new TasRun { Path = System.IO.Path.GetFullPath(path), Raw = j };
        r.Dir = System.IO.Path.GetDirectoryName(r.Path)!;
        r.Console = (string?)j["console"] ?? throw new FormatException($"{path}: no console");
        r.Game = (string?)j["game"] ?? ""; r.RomSha1 = (string?)j["rom_sha1"] ?? ""; r.Rom = (string?)j["rom"] ?? "";
        r.Cores = (string?)j["cores"] ?? ""; r.PowerOn = (string?)j["power_on"] ?? "fceux"; r.Ntsc = (bool?)j["ntsc"] ?? false;
        foreach (var c in j["chapters"]!.AsArray())
            r.Chapters.Add(new TasChapter
            {
                Id = (string)c!["id"]!, Title = (string?)c["title"] ?? "", Start = (string?)c["start"] ?? "previous", Script = (string)c["script"]!,
                Goal = c["goal"] is JsonArray g ? g.Select(x => (string)x!).ToList() : new(), Expect = c["expect"] as JsonObject, Notes = (string?)c["notes"],
            });
        if (r.Chapters.Count == 0) throw new FormatException($"{path}: no chapters");
        return r;
    }

    /// <summary>A one-chapter run around a bare script or log.</summary>
    public static TasRun ForScript(string script, string console, string rom, string start)
    {
        var full = System.IO.Path.GetFullPath(script);
        return new TasRun { Path = full, Dir = System.IO.Path.GetDirectoryName(full)!, Console = console, Rom = rom, Chapters = { new TasChapter { Id = System.IO.Path.GetFileNameWithoutExtension(full), Start = start, Script = full } } };
    }

    public string Resolve(string p) => System.IO.Path.IsPathRooted(p) ? p : System.IO.Path.GetFullPath(System.IO.Path.Combine(Dir, p));

    public void Save()
    {
        var arr = Raw["chapters"]!.AsArray();
        for (int i = 0; i < Chapters.Count; i++) arr[i]!["expect"] = Chapters[i].Expect?.DeepClone();
        File.WriteAllText(Path, Raw.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed class TasPass
{
    public readonly List<TasCheckpoint> Checkpoints = new();
    public readonly List<string> Log = new();
    public readonly Dictionary<string, TasChapterResult> Chapters = new();
    public int Frames; public string? Error; public double WallMs;
}

public sealed class TasChapterResult
{
    public int Frames; public bool? Goal; public List<string> GoalDetail = new(); public List<TasGoal> Goals = new();
    public int Lag = -1; public List<int> LagFrames = new();
    public JsonObject? Vars;
    public JsonObject ToJson(string id) => new()
    {
        ["id"] = id, ["frames"] = Frames, ["goal"] = Goal, ["goalDetail"] = new JsonArray(GoalDetail.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
        ["goals"] = new JsonArray(Goals.Select(g => (JsonNode)g.ToJson()).ToArray()),
        ["lag"] = Lag >= 0 ? Lag : null, ["lagFrames"] = Lag >= 0 ? new JsonArray(LagFrames.Take(500).Select(x => (JsonNode)x).ToArray()) : null, ["vars"] = Vars,
    };
}

public sealed class TasRunOptions
{
    public string OutDir = "";
    public bool Twice = true;
    public bool Screenshots = true;       // PNGs for screenshot checkpoints (pass 1 only)
    public string? Only;                  // run up to and including this chapter id
    public int StopAtFrame = -1;          // with Only: stop after this many frames of that chapter (branch)
    public ITasFrameSink? Sink;           // pass 1 only
    public TasOpenOptions Open = new();
    public string? RomOverride;
}

public static class TasRunner
{
    public static TasProgram Expand(TasRun run, TasChapter ch, double fps)
    {
        string p = run.Resolve(ch.Script);
        var ops = TasTargets.ConsoleOps(run.Console);
        return p.EndsWith(".tlog", StringComparison.OrdinalIgnoreCase) ? TasScript.ReadLog(p, ops) : TasScript.ExpandFile(p, TasButtonMap.For(run.Console), fps, ops);
    }

    public static string FindRom(TasRun run, string? overrideRom)
    {
        foreach (var c in new[] { overrideRom, run.Rom.Length > 0 ? run.Resolve(run.Rom) : null, Environment.GetEnvironmentVariable("BROKENNES_TAS_ROM") })
            if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c!;
        // a ROM folder index (BROKENNES_TAS_ROMDIRS=dir;dir) by SHA-1
        if (run.RomSha1.Length > 0)
            foreach (var d in (Environment.GetEnvironmentVariable("BROKENNES_TAS_ROMDIRS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                if (Directory.Exists(d))
                    foreach (var f in Directory.EnumerateFiles(d))
                        if (new FileInfo(f).Length < 128 << 20 && TasTargets.Sha1(File.ReadAllBytes(f)) == run.RomSha1) return f;
        throw new FileNotFoundException($"no ROM for {run.Path} (give --rom, or set rom in the run file, BROKENNES_TAS_ROM or BROKENNES_TAS_ROMDIRS)");
    }

    /// <summary>Runs every chapter (once, or twice with the determinism check) and writes outDir/report.json.</summary>
    public static JsonObject Run(TasRun run, TasRunOptions o)
    {
        string rom = FindRom(run, o.RomOverride);
        if (run.Cores.Length > 0 && o.Open.Cores.Length == 0) o.Open.Cores = run.Cores;
        o.Open.PowerOn = run.PowerOn; o.Open.Ntsc = run.Ntsc;
        Directory.CreateDirectory(o.OutDir);
        var a = Pass(run, rom, o, true, out var info);
        TasPass? b = null;
        if (o.Twice) { var keep = o.Sink; o.Sink = null; b = Pass(run, rom, o, false, out _); o.Sink = keep; }
        var rep = new JsonObject
        {
            ["run"] = run.Path, ["console"] = run.Console, ["rom"] = rom, ["romSha1"] = info.sha, ["cores"] = info.cores, ["title"] = info.title,
            ["frames"] = a.Frames, ["error"] = a.Error, ["wallMs"] = Math.Round(a.WallMs),
            ["chapters"] = new JsonArray(a.Chapters.Select(kv => (JsonNode)kv.Value.ToJson(kv.Key)).ToArray()),
            ["checkpoints"] = new JsonArray(a.Checkpoints.Select(c => (JsonNode)c.ToJson()).ToArray()),
            ["log"] = J(a.Log),
        };
        if (info.sha.Length > 0 && run.RomSha1.Length > 0 && info.sha != run.RomSha1) rep["romWarning"] = $"ROM SHA-1 {info.sha} is not the run's {run.RomSha1}";
        if (b != null)
        {
            var diffs = new List<string>();
            for (int i = 0; i < Math.Max(a.Checkpoints.Count, b.Checkpoints.Count); i++)
            {
                if (i >= a.Checkpoints.Count || i >= b.Checkpoints.Count) { diffs.Add($"checkpoint count {a.Checkpoints.Count} vs {b.Checkpoints.Count}"); break; }
                var x = a.Checkpoints[i]; var y = b.Checkpoints[i];
                var f = new List<string>();
                if (x.Frame != y.Frame) f.Add("frame"); if (x.Video != y.Video) f.Add("video"); if (x.Audio != y.Audio) f.Add("audio"); if (x.Ram != y.Ram) f.Add("ram");
                if (f.Count > 0) diffs.Add($"{x.Chapter}/{x.Name} ({string.Join(",", f)})");
            }
            rep["determinism"] = new JsonObject { ["checked"] = true, ["identical"] = diffs.Count == 0 && a.Error == b.Error, ["differing"] = J(diffs), ["secondError"] = b.Error,
                ["second"] = diffs.Count == 0 ? null : new JsonArray(b.Checkpoints.Select(c => (JsonNode)c.ToJson()).ToArray()) };
        }
        else rep["determinism"] = new JsonObject { ["checked"] = false };
        rep["expect"] = Check(run, a);
        File.WriteAllText(Path.Combine(o.OutDir, "report.json"), rep.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return rep;
    }

    private static JsonArray J(IEnumerable<string> s) => new(s.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());

    /// <summary>Compares a pass with each chapter's expect block (frames, and video/picture/ram of every named checkpoint).</summary>
    public static JsonObject Check(TasRun run, TasPass a)
    {
        var res = new JsonObject(); int bad = 0, checkedN = 0;
        foreach (var ch in run.Chapters)
        {
            if (ch.Expect == null || !a.Chapters.ContainsKey(ch.Id)) continue;
            var miss = new List<string>();
            if (ch.Expect["frames"] is JsonNode f && (int)f != a.Chapters[ch.Id].Frames) miss.Add($"frames {a.Chapters[ch.Id].Frames} != {(int)f}");
            if (ch.Expect["checkpoints"] is JsonObject cps)
                foreach (var kv in cps)
                {
                    var got = a.Checkpoints.LastOrDefault(c => c.Chapter == ch.Id && c.Name == kv.Key);   // the last of a repeated name (a script "checkpoint end" + the automatic end): what --bless kept
                    if (got == null) { miss.Add($"{kv.Key}: not reached"); continue; }
                    foreach (var k in new[] { "video", "picture", "ram" })
                        if (kv.Value?[k] is JsonNode want && (string)want! != (k == "video" ? got.Video : k == "picture" ? got.Picture : got.Ram)) miss.Add($"{kv.Key}.{k}");
                }
            checkedN++; if (miss.Count > 0) bad++;
            res[ch.Id] = miss.Count == 0 ? "ok" : string.Join("; ", miss);
        }
        res["ok"] = bad == 0; res["checked"] = checkedN;
        return res;
    }

    /// <summary>Writes this pass's checkpoints into the run's expect blocks (--bless).</summary>
    public static void Bless(TasRun run, JsonObject report)
    {
        foreach (var ch in run.Chapters)
        {
            var mine = report["checkpoints"]!.AsArray().Where(c => (string?)c!["chapter"] == ch.Id).ToList();
            if (mine.Count == 0) continue;
            var cps = new JsonObject();
            foreach (var c in mine) cps[(string)c!["name"]!] = new JsonObject { ["frame"] = (int)c["frame"]!, ["video"] = (string)c["video"]!, ["picture"] = (string)c["picture"]!, ["ram"] = (string)c["ram"]! };
            var f = report["chapters"]!.AsArray().First(x => (string?)x!["id"] == ch.Id)!["frames"]!.GetValue<int>();
            ch.Expect = new JsonObject { ["frames"] = f, ["checkpoints"] = cps };
        }
        run.Save();
    }

    private static TasPass Pass(TasRun run, string rom, TasRunOptions o, bool primary, out (string sha, string cores, string title) info)
    {
        var p = new TasPass(); var sw = Stopwatch.StartNew();
        ITasTarget? t = null; info = ("", "", "");
        var buf = new short[1 << 16];
        var map = TasRamMap.For(run);
        try
        {
            foreach (var ch in run.Chapters)
            {
                if (ch.Start == "power-on" || t == null)
                {
                    t?.Dispose();
                    // each pass is a fresh machine: its own (empty) Controller Pak folder, so pass 1's pak writes never reach pass 2
                    var oo = new TasOpenOptions { Cores = o.Open.Cores, PowerOn = o.Open.PowerOn, Ntsc = o.Open.Ntsc, N64Renderer = o.Open.N64Renderer,
                        PakDir = o.Open.PakDir ?? Path.Combine(o.OutDir, primary ? "pak" : Path.Combine("pass2", "pak")) };
                    Directory.CreateDirectory(oo.PakDir);
                    t = TasTargets.Open(run.Console, rom, oo);
                    info = (t.RomSha1, t.Cores, t.Title);
                    if (primary) o.Sink?.Begin(t.Width, t.Height, t.Fps, t.SampleRate);
                    if (ch.Start != "power-on" && ch.Start != "previous" && !ch.Start.StartsWith("state:")) throw new FormatException($"chapter {ch.Id}: start '{ch.Start}' (power-on | previous | state:FILE)");
                }
                if (ch.Start.StartsWith("state:")) { t.LoadState(File.ReadAllBytes(run.Resolve(ch.Start[6..]))); while (t.ReadSamples(buf) > 0) { } }
                string dir = primary ? Path.Combine(o.OutDir, ch.Id) : Path.Combine(o.OutDir, "pass2", ch.Id); Directory.CreateDirectory(dir);
                var prog = Expand(run, ch, t.Fps);
                int stop = o.Only == ch.Id ? o.StopAtFrame : -1;
                var res = new TasChapterResult { Goals = ch.Goal.Select(TasGoal.Parse).ToList() };
                t.InputPolls();   // polls before the chapter's first frame are not its own
                int frames = RunChapter(t, prog, ch, run, dir, primary && o.Screenshots, primary ? o.Sink : null, p, buf, stop, res, map);
                res.Frames = frames;
                foreach (var g in res.Goals) { var c2 = new TasEvalContext(t, map, run.Dir, frames, Math.Max(0, res.Lag)); g.Finish(c2); res.GoalDetail.Add($"{g.Text}: {g.Detail}"); }
                res.Goal = res.Goals.Count == 0 ? null : res.Goals.All(g => g.Met == true);
                if (map != null) res.Vars = map.Snapshot(t);
                if (res.Lag > 0) p.Log.Add($"LAG {ch.Id} {res.Lag} frames: {string.Join(" ", res.LagFrames.Take(40))}{(res.LagFrames.Count > 40 ? " ..." : "")}");
                p.Chapters[ch.Id] = res;
                if (primary) File.WriteAllBytes(Path.Combine(dir, "end.state"), t.SaveState());
                if (o.Only == ch.Id) break;
            }
        }
        catch (Exception e) { p.Error = $"{e.GetType().Name}: {e.Message} (frame {p.Frames})"; }
        finally { if (primary) o.Sink?.End(); t?.Dispose(); }
        p.WallMs = sw.Elapsed.TotalMilliseconds;
        return p;
    }

    private static int RunChapter(ITasTarget t, TasProgram prog, TasChapter ch, TasRun run, string dir, bool shots, ITasFrameSink? sink, TasPass p, short[] buf, int stopAt, TasChapterResult res, TasRamMap? map)
    {
        var perFrame = res.Goals.Where(g => g.PerFrame).ToList(); int lag = 0; bool lagKnown = true;
        ulong hv = 1469598103934665603UL, ha = hv; double sumSq = 0, peak = 0; long win = 0; int frames = 0;
        TasCheckpoint Mark(string name, string kind)
        {
            var px = t.Argb; int n = t.Width * t.Height;
            bool solid = true; uint c0 = n > 0 ? px[0] & 0xFFFFFF : 0; for (int i = 1; i < n; i++) if ((px[i] & 0xFFFFFF) != c0) { solid = false; break; }
            var counts = new Dictionary<uint, int>(); int sampled = 0;
            for (int i = 0; i < n; i += 3) { uint c = px[i] & 0xFFFFFF; counts[c] = counts.TryGetValue(c, out var v) ? v + 1 : 1; sampled++; }
            int dom = counts.Count > 0 ? counts.Values.Max() : 0;
            var cp = new TasCheckpoint
            {
                Name = name, Kind = kind, Chapter = ch.Id, Frame = frames, Video = hv.ToString("X16"), Picture = t.PictureHash().ToString("X16"), Audio = ha.ToString("X16"),
                Ram = t.RamHash().ToString("X16"), Samples = win, Rms = Math.Round(win > 0 ? Math.Sqrt(sumSq / win) : 0, 5), Peak = Math.Round(peak, 5),
                Solid = solid, DominantFraction = Math.Round(sampled > 0 ? (double)dom / sampled : 1, 4),
            };
            win = 0; sumSq = 0; peak = 0;
            p.Checkpoints.Add(cp);
            return cp;
        }
        foreach (var s in prog.Steps)
        {
            if (stopAt >= 0 && frames >= stopAt) break;
            if (s.IsFrame)
            {
                t.SetPads(s.Pads);
                t.RunFrame();
                frames++; p.Frames++;
                int polls = t.InputPolls();
                if (polls < 0) lagKnown = false; else if (polls == 0) { lag++; res.LagFrames.Add(frames - 1); }
                if (perFrame.Count > 0) { var gc = new TasEvalContext(t, map, run.Dir, frames, lag); foreach (var g in perFrame) g.OnFrame(gc); }
                hv = TasTargets.Fnv(t.Argb, t.Width * t.Height, hv);
                sink?.Frame(t.Argb, t.Width, t.Height);
                sink?.Rate(t.SampleRate);
                int got;
                while ((got = t.ReadSamples(buf)) > 0)
                {
                    win += got;
                    for (int k = 0; k < got; k++) { ha ^= (ushort)buf[k]; ha *= 1099511628211UL; double v = buf[k] / 32768.0; sumSq += v * v; if (Math.Abs(v) > peak) peak = Math.Abs(v); }
                    sink?.Audio(buf.AsSpan(0, got));
                }
                continue;
            }
            switch (s.Op)
            {
                case "checkpoint": Mark(s.Args.Length > 0 ? s.Args[0] : "cp" + p.Checkpoints.Count, "checkpoint"); break;
                case "mark": p.Log.Add($"{ch.Id} frame {frames}: {string.Join(" ", s.Args)}"); break;
                case "screenshot":
                {
                    var name = s.Args.Length > 0 ? s.Args[0] : "shot" + p.Checkpoints.Count;
                    var cp = Mark(name, "screenshot");
                    if (shots) { cp.File = Path.Combine(ch.Id, name + ".png"); TasPng.Write(Path.Combine(dir, name + ".png"), t.Argb, t.Width, t.Height); }
                    break;
                }
                case "savestate":
                {
                    var name = s.Args.Length > 0 ? s.Args[0] : "state" + p.Checkpoints.Count;
                    var st = t.SaveState(); var cp = Mark(name, "savestate");
                    if (shots) { cp.File = Path.Combine(ch.Id, name + ".state"); File.WriteAllBytes(Path.Combine(dir, name + ".state"), st); }
                    break;
                }
                case "loadstate":
                {
                    var a = s.Args[0];
                    var cand = new[] { a.EndsWith(".state") ? run.Resolve(a) : "", Path.Combine(dir, a + ".state"), run.Resolve(a + ".state") };
                    var f = cand.FirstOrDefault(x => x.Length > 0 && File.Exists(x)) ?? throw new FileNotFoundException($"loadstate {a}: no state (looked in the chapter's folder and next to the run)");
                    t.LoadState(File.ReadAllBytes(f)); t.InputPolls(); break;
                }
                case "reset": t.Reset(); break;
                case "poke": t.Poke(s.Args[0], Convert.FromHexString(s.Args[1].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s.Args[1][2..] : s.Args[1])); break;
                case "peek": p.Log.Add($"PEEK {ch.Id} {s.Args[0]} frame={frames} {Convert.ToHexString(t.Peek(s.Args[0], s.Args.Length > 1 ? int.Parse(s.Args[1]) : 4))}"); break;
                case "savefile":
                {
                    var sv = t.ExportSave(); var nm = s.Args.Length > 0 ? s.Args[0] : "battery";
                    if (shots) File.WriteAllBytes(Path.Combine(dir, nm + ".sav"), sv);
                    p.Log.Add($"SAVE {ch.Id} {nm} frame={frames} size={sv.Length} sha1={TasTargets.Sha1(sv)[..12]}"); break;
                }
                default:
                    if (!t.ConsoleOp(s.Op!, s.Args, dir)) throw new FormatException($"line {s.Line}: '{s.Op}' is not a {t.Console} command");
                    break;
            }
        }
        Mark("end", "end");
        res.Lag = lagKnown ? lag : -1;
        return frames;
    }
}

/// <summary>A small RGB PNG writer (no System.Drawing), as SessionProbe's.</summary>
public static class TasPng
{
    public static void Write(string path, uint[] px, int w, int h)
    {
        using var raw = new MemoryStream();
        for (int y = 0; y < h; y++) { raw.WriteByte(0); for (int x = 0; x < w; x++) { uint c = px[y * w + x]; raw.WriteByte((byte)(c >> 16)); raw.WriteByte((byte)(c >> 8)); raw.WriteByte((byte)c); } }
        using var z = new MemoryStream();
        using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Fastest, true)) { raw.Position = 0; raw.CopyTo(zs); }
        using var f = File.Create(path);
        f.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13]; BE(ihdr, 0, (uint)w); BE(ihdr, 4, (uint)h); ihdr[8] = 8; ihdr[9] = 2;
        Chunk(f, "IHDR", ihdr); Chunk(f, "IDAT", z.ToArray()); Chunk(f, "IEND", Array.Empty<byte>());
    }
    public static byte[] Encode(uint[] px, int w, int h) { var tmp = Path.GetTempFileName(); try { Write(tmp, px, w, h); return File.ReadAllBytes(tmp); } finally { File.Delete(tmp); } }
    static void BE(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    static void Chunk(Stream f, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, (uint)data.Length); f.Write(len);
        var td = new byte[4 + data.Length]; Encoding.ASCII.GetBytes(type, 0, 4, td, 0); Buffer.BlockCopy(data, 0, td, 4, data.Length);
        f.Write(td); var crc = new byte[4]; BE(crc, 0, Crc(td)); f.Write(crc);
    }
    static uint[]? s_Crc;
    static uint Crc(byte[] d)
    {
        if (s_Crc == null) { s_Crc = new uint[256]; for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; s_Crc[n] = c; } }
        uint r = 0xFFFFFFFF; foreach (var b in d) r = s_Crc[(r ^ b) & 0xFF] ^ (r >> 8); return r ^ 0xFFFFFFFF;
    }
}
