using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using NesEmulator.Systems;

namespace NesEmulator.Debugging.Movies;

/// <summary>
/// An AutoTAS package (docs/projects/autotas-plan.md, decision 1): one game's TAS cut into chapters, on T: (never in git):
/// <code>
/// T:\BrokenDev\Catalog\tas\&lt;console&gt;\&lt;game_id&gt;\v&lt;N&gt;\
///   autotas.json            the manifest (<see cref="AutoTasManifest"/>)
///   full.bnm                the stitched movie from power-on, a mark "chapter &lt;id&gt;" at each chapter's start
///   seg\&lt;ch&gt;.bnm            each chapter's segment (chapter 0 from power-on, the others from the previous chapter's raw state)
///   state\&lt;ch&gt;.raw.state    the state at the chapter's milestone (its end); .clean.state the cleaned one
///   thumb\&lt;ch&gt;.png  notes\&lt;ch&gt;.md  cleanup.json  milestones.json
/// </code>
/// The state files are <see cref="AutoTasState"/> files (a small header, then the emulator's own state, gzip).
/// </summary>
public sealed class AutoTasPackage
{
    public const string ManifestName = "autotas.json";
    public const string FullMovie = "full.bnm";
    public string Dir { get; }
    public AutoTasManifest Manifest { get; set; }

    public AutoTasPackage(string dir, AutoTasManifest manifest) { Dir = Path.GetFullPath(dir); Manifest = manifest; }

    public static string VersionDir(string tasRoot, string console, string gameId, int version) => Path.Combine(tasRoot, console, gameId, "v" + version.ToString(CultureInfo.InvariantCulture));

    public string SegPath(string ch) => Path.Combine(Dir, "seg", ch + MovieFormat.Extension);
    public string RawStatePath(string ch) => Path.Combine(Dir, "state", ch + ".raw.state");
    public string CleanStatePath(string ch) => Path.Combine(Dir, "state", ch + ".clean.state");
    public string ThumbPath(string ch) => Path.Combine(Dir, "thumb", ch + ".png");
    public string NotesPath(string ch) => Path.Combine(Dir, "notes", ch + ".md");
    public string FullPath => Path.Combine(Dir, FullMovie);
    public string Resolve(string? rel) => rel is { Length: > 0 } ? Path.GetFullPath(Path.Combine(Dir, rel)) : "";

    public static AutoTasPackage Load(string dir)
    {
        string f = Path.Combine(dir, ManifestName);
        if (!File.Exists(f)) throw new FileNotFoundException($"no {ManifestName} in {dir}");
        return new AutoTasPackage(dir, AutoTasManifest.Parse(File.ReadAllText(f)));
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        foreach (var d in new[] { "seg", "state", "thumb", "notes" }) Directory.CreateDirectory(Path.Combine(Dir, d));
        Manifest.Validate();
        string f = Path.Combine(Dir, ManifestName), tmp = f + ".tmp";
        File.WriteAllText(tmp, Manifest.ToJson());
        File.Move(tmp, f, true);
    }

    /// <summary>Adds or replaces a chapter from its recorded segment: start / end frames come from the chapters before it, the keyframe from the segment's last one.</summary>
    public AutoTasChapter PutChapter(AutoTasChapter ch, Movie segment)
    {
        int i = Manifest.Chapters.FindIndex(c => c.Id == ch.Id);
        int before = i >= 0 ? i : Manifest.Chapters.Count;
        long start = Manifest.Chapters.Take(before).Sum(c => c.EndFrame - c.StartFrame);
        ch.StartFrame = start; ch.EndFrame = start + segment.Frames;
        var k = segment.KeyAt(segment.Frames) ?? throw new InvalidDataException($"the segment for {ch.Id} has no keyframe at its last frame");
        ch.KeyPicture = k.Picture.ToString("X16"); ch.KeyRam = k.Ram.ToString("X16");
        ch.Segment ??= "seg/" + ch.Id + MovieFormat.Extension;
        if (i >= 0) Manifest.Chapters[i] = ch; else Manifest.Chapters.Add(ch);
        return ch;
    }

    /// <summary>
    /// Stitches the chapters' segments into one movie from power-on (<see cref="Stitch"/>), writes full.bnm and fixes every chapter's frames
    /// and keyframe from the stitched movie.
    /// </summary>
    public Movie StitchAndSave()
    {
        var segs = Manifest.Chapters.Select(c => Movie.Load(Resolve(c.Segment))).ToList();
        var full = Stitch(segs, Manifest.Chapters.Select(c => c.Id).ToList(), Manifest.Chapters.Select(c => c.Title).ToList());
        long at = 0;
        for (int i = 0; i < segs.Count; i++)
        {
            var c = Manifest.Chapters[i];
            c.StartFrame = at; at += segs[i].Frames; c.EndFrame = at;
            var k = full.KeyAt(at)!;
            c.KeyPicture = k.Picture.ToString("X16"); c.KeyRam = k.Ram.ToString("X16");
        }
        full.Save(FullPath);
        Manifest.Full = FullMovie;
        return full;
    }

    /// <summary>
    /// One movie from segments in order: the first from power-on (or any start), each next one from the state the previous ended on.
    /// Inputs and keyframes are shifted by the frames before them; each segment's own marks are kept (shifted) and a mark
    /// "chapter &lt;id&gt;: &lt;title&gt;" is put at each segment's start. Every segment's last keyframe stays in (so each chapter end can be checked).
    /// </summary>
    public static Movie Stitch(IReadOnlyList<Movie> segs, IReadOnlyList<string> ids, IReadOnlyList<string>? titles = null)
    {
        if (segs.Count == 0) throw new ArgumentException("no segments");
        if (ids.Count != segs.Count) throw new ArgumentException("one chapter id per segment");
        var a = segs[0];
        for (int i = 1; i < segs.Count; i++)
        {
            var s = segs[i];
            if (s.Start != MovieStart.State) throw new InvalidDataException($"segment {ids[i]} starts at power-on: the segments after the first start from the previous chapter's state");
            if (!s.Console.Equals(a.Console, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"segment {ids[i]} is for {s.Console}, the first for {a.Console}");
            if (!s.RomSha256.Equals(a.RomSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"segment {ids[i]} was recorded on another ROM ({s.RomSha256[..Math.Min(12, s.RomSha256.Length)]})");
            if (s.Vars.Count != a.Vars.Count || s.Vars.Where((v, j) => v != a.Vars[j]).Any()) throw new InvalidDataException($"segment {ids[i]} has other course variables than the first");
        }
        var m = new Movie
        {
            Console = a.Console, RomName = a.RomName, RomSha256 = a.RomSha256, RomBytes = a.RomBytes, Build = a.Build, Emulator = a.Emulator, Cores = a.Cores,
            NtscTiming = a.NtscTiming, PowerOn = a.PowerOn, Start = a.Start, StartState = a.StartState, KeyEvery = a.KeyEvery,
        };
        m.Vars.AddRange(a.Vars);
        long off = 0;
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            m.Marks.Add(new MovieMark(off, $"chapter {ids[i]}" + (titles != null && titles[i] is { Length: > 0 } t ? ": " + t : "")));
            // the segment's pads at its frame 0 (it starts with nothing held unless it says so)
            if (s.Inputs.Count == 0 || s.Inputs[0].Frame > 0) m.SetPads(off, PadButtons.None, PadButtons.None);
            foreach (var inp in s.Inputs) m.SetPads(off + inp.Frame, inp.P1, inp.P2);
            foreach (var k in s.Keys) m.Keys.Add(new MovieKey { Frame = off + k.Frame, Picture = k.Picture, Ram = k.Ram, Vars = k.Vars });
            foreach (var mk in s.Marks) if (mk.Frame < s.Frames) m.Marks.Add(new MovieMark(off + mk.Frame, mk.Text));
            off += s.Frames;
        }
        m.Frames = off;
        m.Comments.Add($"AutoTAS: stitched from {segs.Count} segment(s): {string.Join(", ", ids)}");
        return m;
    }
}

public sealed class AutoTasManifest
{
    public const int CurrentSchema = 1;
    [JsonPropertyName("schema")] public int Schema { get; set; } = CurrentSchema;
    [JsonPropertyName("console")] public string Console { get; set; } = "nes";
    [JsonPropertyName("game_id")] public string GameId { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("rom_sha1")] public string RomSha1 { get; set; } = "";
    [JsonPropertyName("rom_sha256")] public string RomSha256 { get; set; } = "";
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    /// <summary>none, building, pilot, complete.</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "building";
    /// <summary>The core the movie and states were made on: CPU/PPU/APU ids, the build, the state format (<see cref="AutoTasState.Format"/>).</summary>
    [JsonPropertyName("core")] public AutoTasCore Core { get; set; } = new();
    [JsonPropertyName("full")] public string? Full { get; set; }
    [JsonPropertyName("milestones")] public string? Milestones { get; set; } = "milestones.json";
    [JsonPropertyName("cleanup")] public string? Cleanup { get; set; } = "cleanup.json";
    [JsonPropertyName("verified_at")] public string? VerifiedAt { get; set; }
    [JsonPropertyName("verify")] public string? VerifyResult { get; set; }
    [JsonPropertyName("chapters")] public List<AutoTasChapter> Chapters { get; set; } = new();

    public static readonly string[] Statuses = { "none", "building", "pilot", "complete" };
    public static readonly string[] Kinds = { "level", "boss", "area", "map" };

    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string ToJson() => JsonSerializer.Serialize(this, Opt) + "\n";
    public static AutoTasManifest Parse(string json)
    {
        var m = JsonSerializer.Deserialize<AutoTasManifest>(json, Opt) ?? throw new InvalidDataException("empty manifest");
        m.Validate();
        return m;
    }

    public double? QualityAvg()
    {
        var q = Chapters.Where(c => c.Quality?.Score is { Length: > 0 }).Select(c => c.Quality!.Score switch { "A" => 3.0, "B" => 2.0, _ => 1.0 }).ToList();
        return q.Count == 0 ? null : Math.Round(q.Average(), 2);
    }

    public void Validate()
    {
        if (Schema != CurrentSchema) throw new InvalidDataException($"manifest schema {Schema} (this build reads {CurrentSchema})");
        if (GameId.Length == 0) throw new InvalidDataException("game_id is empty");
        if (!Statuses.Contains(Status)) throw new InvalidDataException($"status '{Status}' is not one of {string.Join(", ", Statuses)}");
        var seen = new HashSet<string>();
        long at = 0;
        foreach (var c in Chapters)
        {
            if (c.Id.Length == 0 || c.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || c.Id.Contains(' ')) throw new InvalidDataException($"chapter id '{c.Id}' is not a file name");
            if (!seen.Add(c.Id)) throw new InvalidDataException($"chapter id '{c.Id}' twice");
            if (!Kinds.Contains(c.Kind)) throw new InvalidDataException($"chapter {c.Id}: kind '{c.Kind}' is not one of {string.Join(", ", Kinds)}");
            if (c.StartFrame != at || c.EndFrame < c.StartFrame) throw new InvalidDataException($"chapter {c.Id}: frames {c.StartFrame}-{c.EndFrame} do not follow the chapter before (ends at {at})");
            at = c.EndFrame;
        }
    }
}

public sealed class AutoTasCore
{
    [JsonPropertyName("cores")] public string Cores { get; set; } = "";
    [JsonPropertyName("build")] public string Build { get; set; } = "";
    [JsonPropertyName("state_format")] public string StateFormat { get; set; } = "";
}

public sealed class AutoTasChapter
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "level";
    [JsonPropertyName("start_frame")] public long StartFrame { get; set; }
    [JsonPropertyName("end_frame")] public long EndFrame { get; set; }
    [JsonPropertyName("segment")] public string? Segment { get; set; }
    [JsonPropertyName("raw_state")] public string? RawState { get; set; }
    [JsonPropertyName("clean_state")] public string? CleanState { get; set; }
    [JsonPropertyName("thumb")] public string? Thumb { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    /// <summary>The picture and RAM hashes (16 hex digits each) after the chapter's last frame: the stitched movie must hit them.</summary>
    [JsonPropertyName("key_picture")] public string KeyPicture { get; set; } = "";
    [JsonPropertyName("key_ram")] public string KeyRam { get; set; } = "";
    [JsonPropertyName("milestone")] public string? Milestone { get; set; }
    [JsonPropertyName("quality")] public AutoTasQuality? Quality { get; set; }
    [JsonPropertyName("accepted_by")] public string? AcceptedBy { get; set; }
    [JsonPropertyName("retries")] public int Retries { get; set; }
    /// <summary>The segment was re-recorded from the clean state (an intentional clean-up change desynced the raw one).</summary>
    [JsonPropertyName("clean_seg")] public bool CleanSeg { get; set; }
    [JsonPropertyName("needs_help")] public bool NeedsHelp { get; set; }
}

public sealed class AutoTasQuality
{
    [JsonPropertyName("deaths")] public int Deaths { get; set; }
    [JsonPropertyName("frames")] public long Frames { get; set; }
    [JsonPropertyName("par_frames")] public long? ParFrames { get; set; }
    /// <summary>A, B or C (the judge checklist).</summary>
    [JsonPropertyName("score")] public string Score { get; set; } = "";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
}

/// <summary>
/// A milestone: a named debugger expression that is non-zero when a chapter ends (milestones.json: <c>{"milestones":[{"id":"1-1","until":"[$0727]==0 &amp;&amp; ..."}]}</c>).
/// </summary>
public sealed record AutoTasMilestone(string Id, string Until)
{
    public static List<AutoTasMilestone> ParseFile(string json)
    {
        using var d = JsonDocument.Parse(json);
        var arr = d.RootElement.ValueKind == JsonValueKind.Array ? d.RootElement : d.RootElement.GetProperty("milestones");
        var l = new List<AutoTasMilestone>();
        foreach (var e in arr.EnumerateArray())
        {
            string id = e.GetProperty("id").GetString() ?? "";
            string until = e.TryGetProperty("until", out var u) ? u.GetString() ?? "" : e.GetProperty("condition").GetString() ?? "";
            if (id.Length == 0 || until.Length == 0) throw new InvalidDataException("a milestone needs an id and an until");
            l.Add(new AutoTasMilestone(id, until));
        }
        return l;
    }

    /// <summary>"id=expression" (the first '=' not part of ==, &lt;=, &gt;=, !=) or a bare expression (its id is the expression).</summary>
    public static AutoTasMilestone ParseInline(string s)
    {
        for (int i = 1; i < s.Length - 1; i++)
            if (s[i] == '=' && s[i + 1] != '=' && s[i - 1] is not ('=' or '<' or '>' or '!'))
                return new AutoTasMilestone(s[..i].Trim(), s[(i + 1)..].Trim());
        return new AutoTasMilestone(s.Trim(), s.Trim());
    }
}

/// <summary>
/// An AutoTAS state file: "bnstate 1\n", header lines (console, cores, frame, rom_sha256, format), a blank line, then the emulator's own
/// state bytes, gzip. <see cref="Format"/> names what the app must match to load it (otherwise it fast-replays full.bnm).
/// </summary>
public sealed class AutoTasState
{
    public const string Magic = "bnstate 1";
    public string Console = "", Cores = "", RomSha256 = "";
    public long Frame;
    public byte[] State = Array.Empty<byte>();
    public string Format => $"{Console}:{Cores}:gzip1";

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var f = File.Create(path);
        var head = System.Text.Encoding.UTF8.GetBytes($"{Magic}\nconsole {Console}\ncores {Cores}\nframe {Frame.ToString(CultureInfo.InvariantCulture)}\nrom_sha256 {RomSha256}\nformat {Format}\n\n");
        f.Write(head);
        using var gz = new GZipStream(f, CompressionLevel.Optimal);
        gz.Write(State);
    }

    public static AutoTasState Load(string path)
    {
        var all = File.ReadAllBytes(path);
        int end = -1;
        for (int i = 0; i + 1 < all.Length && i < 4096; i++) if (all[i] == '\n' && all[i + 1] == '\n') { end = i; break; }
        if (end < 0) throw new InvalidDataException($"{path} is not a {Magic} file");
        var lines = System.Text.Encoding.UTF8.GetString(all, 0, end).Split('\n');
        if (lines[0] != Magic) throw new InvalidDataException($"{path} is not a {Magic} file");
        var s = new AutoTasState();
        foreach (var l in lines.Skip(1))
        {
            int sp = l.IndexOf(' ');
            string k = sp < 0 ? l : l[..sp], v = sp < 0 ? "" : l[(sp + 1)..];
            switch (k)
            {
                case "console": s.Console = v; break;
                case "cores": s.Cores = v; break;
                case "frame": s.Frame = long.Parse(v, CultureInfo.InvariantCulture); break;
                case "rom_sha256": s.RomSha256 = v; break;
            }
        }
        using var ms = new MemoryStream(all, end + 2, all.Length - end - 2);
        using var gz = new GZipStream(ms, CompressionMode.Decompress);
        using var o = new MemoryStream();
        gz.CopyTo(o);
        s.State = o.ToArray();
        return s;
    }
}
