using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using NesEmulator.Debugging.Movies;
using NesEmulator.Systems;

namespace NesEmulator.Debugging.Capture;

/// <summary>What the host knows of the game a capture comes from (the parts of a bundle the ring does not hold).</summary>
public sealed class BugMeta
{
    public string Note = "";
    /// <summary>The app that captured it ("BrokenNes desktop 1.2.3", "BrokenNes Lite").</summary>
    public string App = "";
    public string RomName = "";
    public byte[] Rom = Array.Empty<byte>();
    /// <summary>Where the ROM was loaded from (information: the bundle carries its own copy).</summary>
    public string RomPath = "";
    /// <summary>CPU/PPU/APU ids, as a movie names them ("FIX/FIX/FIX").</summary>
    public string Cores = "";
    public bool NtscTiming;
    public string PowerOn = "fceux";
    /// <summary>The game's build (a BrokenDev build record's note or start time), free text.</summary>
    public string Build = "";
    /// <summary>The BrokenDev project the ROM belongs to, or null.</summary>
    public BugProject? Project;
    /// <summary>The game's symbol files (name, bytes), from its BrokenDev build record.</summary>
    public readonly List<(string Name, byte[] Data)> Symbols = new();
    /// <summary>Settings worth knowing when reading the bug (name, value as text).</summary>
    public readonly List<(string Name, string Value)> Settings = new();
    public DateTime CapturedUtc = DateTime.UtcNow;
    public DateTime CapturedLocal = DateTime.Now;
}

/// <summary>A BrokenDev project a ROM belongs to (found by its sha256 in the build records).</summary>
public sealed class BugProject
{
    public string Id = "", Title = "", Workspace = "", BugsDir = "", BuildRecord = "", BuildStarted = "";
}

/// <summary>
/// A bug bundle (D6): the folder (desktop) or zip (Lite) a capture is saved as. Its files:
/// <code>
/// bug.json            what it is (format brokennes-bug 1): the note, when, the app, the ROM, the project, the cores and settings, the
///                     history (how long, and why it starts where it does), the files below
/// capture.bnm         the movie (BrokenNes's own format, D5) from a save state of the oldest second: replays exactly in Workshop / BrokenDev / MCP
/// rom/&lt;name&gt;          the ROM it ran (the bug replays on this very build even after the game is rebuilt)
/// symbols/&lt;name&gt;      the game's symbols, when its BrokenDev build record names them
/// audio.wav           the sound of those seconds (mono 16-bit, the app's rate)
/// pictures/fNNNNNN.png  one picture a second (NNNNNN: frames into the movie)
/// strip.png           the pictures at half size on one sheet
/// last.png            the picture when the key was pressed
/// </code>
/// </summary>
public static class BugBundle
{
    public const string Format = "brokennes-bug";
    public const int Version = 1;
    public const string InfoFile = "bug.json";
    public static string MovieFile => "capture" + MovieFormat.Extension;

    /// <summary>The bundle's files (relative path with '/' -> bytes), from a capture and what the host knows.</summary>
    public static SortedDictionary<string, byte[]> Files(BugCaptureData d, BugMeta meta)
    {
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        string romSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(meta.Rom)).ToLowerInvariant();
        string romFile = "rom/" + SafeName(meta.RomName.Length > 0 ? meta.RomName : "game.bin");
        if (meta.Rom.Length > 0) files[romFile] = meta.Rom;

        Movie? mv = null;
        if (d.NoMovieWhy == null)
        {
            mv = new Movie
            {
                Console = d.ConsoleKey, RomName = meta.RomName, RomSha256 = romSha, RomBytes = meta.Rom.Length, Build = meta.Build, Emulator = meta.App,
                Cores = meta.Cores, NtscTiming = meta.NtscTiming, PowerOn = meta.PowerOn, Start = d.Start, StartState = d.StartState, Frames = d.Frames, KeyEvery = 60,
            };
            mv.Comments.Add($"bug capture {meta.CapturedLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}{(meta.Note.Length > 0 ? ": " + meta.Note : "")}");
            for (long f = 0; f < d.Frames; f++) mv.SetPads(f, (PadButtons)d.Pads[f * 2], (PadButtons)d.Pads[f * 2 + 1]);
            if (mv.Inputs.Count == 0) mv.SetPads(0, PadButtons.None, PadButtons.None);
            foreach (var k in d.Keys) if (mv.Keys.Count == 0 || k.Frame > mv.Keys[^1].Frame) mv.Keys.Add(k);
            foreach (var nt in d.Notes) if (nt.Kind == "marker") mv.Marks.Add(new MovieMark(nt.Frame, nt.Text));
            files[MovieFile] = new UTF8Encoding(false).GetBytes(mv.ToText());
        }

        var pics = new List<(string File, long Frame)>();
        foreach (var p in d.Pictures)
        {
            string f = $"pictures/f{p.Frame.ToString("D6", CultureInfo.InvariantCulture)}.png";
            files[f] = BugMedia.Png(p.Pixels, p.Width, p.Height);
            pics.Add((f, p.Frame));
        }
        if (d.Pictures.Count > 0) { var (s, w, h) = BugMedia.Strip(d.Pictures); files["strip.png"] = BugMedia.Png(s, w, h); }
        if (d.Last is { Width: > 0 } last) files["last.png"] = BugMedia.Png(last.Pixels, last.Width, last.Height);
        bool sound = d.Audio.Length > 0;
        if (sound) files["audio.wav"] = BugMedia.Wav(d.Audio, d.SampleRate);
        var symFiles = new List<string>();
        foreach (var (n, data) in meta.Symbols) { string f = "symbols/" + SafeName(n); files[f] = data; symFiles.Add(f); }

        using var ms = new MemoryStream();
        using (var j = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            j.WriteStartObject();
            j.WriteString("format", Format);
            j.WriteNumber("version", Version);
            j.WriteString("note", meta.Note);
            j.WriteString("capturedUtc", meta.CapturedUtc.ToString("o", CultureInfo.InvariantCulture));
            j.WriteString("capturedLocal", meta.CapturedLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            j.WriteString("app", meta.App);
            j.WriteString("console", d.ConsoleKey);
            j.WriteStartObject("rom");
            j.WriteString("name", meta.RomName); j.WriteString("sha256", romSha); j.WriteNumber("bytes", meta.Rom.Length);
            j.WriteString("file", meta.Rom.Length > 0 ? romFile : ""); j.WriteString("loadedFrom", meta.RomPath);
            j.WriteEndObject();
            if (meta.Project is { } pr)
            {
                j.WriteStartObject("project");
                j.WriteString("id", pr.Id); j.WriteString("title", pr.Title); j.WriteString("workspace", pr.Workspace);
                j.WriteString("buildRecord", pr.BuildRecord); j.WriteString("buildStarted", pr.BuildStarted);
                j.WriteEndObject();
            }
            else j.WriteNull("project");
            j.WriteString("build", meta.Build);
            j.WriteString("cores", meta.Cores);
            j.WriteStartObject("settings");
            if (d.ConsoleKey == "nes") { j.WriteString("timing", meta.NtscTiming ? "ntsc" : "60"); j.WriteString("powerOn", meta.PowerOn); }
            foreach (var (n, v) in meta.Settings) j.WriteString(n, v);
            j.WriteEndObject();
            j.WriteStartObject("history");
            j.WriteNumber("wantedSeconds", BugCaptureRing.Seconds);
            j.WriteNumber("seconds", Math.Round(d.Seconds, 2));
            j.WriteNumber("frames", d.Frames);
            j.WriteNumber("framesPerSecond", Math.Round(d.FramesPerSecond, 4));
            j.WriteString("start", d.NoMovieWhy != null ? "none" : d.Start == MovieStart.State ? "state" : "power-on");
            j.WriteString("why", HistoryWhy(d));
            j.WriteEndObject();
            j.WriteString("movie", mv != null ? MovieFile : "");
            if (d.NoMovieWhy != null) j.WriteString("noMovie", d.NoMovieWhy);
            j.WriteNumber("keyframes", mv?.Keys.Count ?? 0);
            j.WriteStartObject("last");
            j.WriteString("file", files.ContainsKey("last.png") ? "last.png" : "");
            var lk = d.Keys.Count > 0 ? d.Keys[^1] : null;
            j.WriteString("picture", lk != null ? lk.Picture.ToString("X16") : ""); j.WriteString("ram", lk != null ? lk.Ram.ToString("X16") : "");
            j.WriteEndObject();
            j.WriteStartObject("audio");
            j.WriteString("file", sound ? "audio.wav" : "");
            j.WriteNumber("sampleRate", d.SampleRate);
            j.WriteNumber("seconds", Math.Round(d.Audio.Length / (double)Math.Max(1, d.SampleRate), 3));
            if (!sound) j.WriteString("why", "no sound was queued in those seconds (the sound is off, or the game is silent and the app queues nothing)");
            if (d.AudioMissingLead > 0) j.WriteNumber("missingLeadSamples", d.AudioMissingLead);
            j.WriteEndObject();
            j.WriteStartArray("pictures");
            foreach (var (f, fr) in pics) { j.WriteStartObject(); j.WriteString("file", f); j.WriteNumber("frame", fr); j.WriteEndObject(); }
            j.WriteEndArray();
            j.WriteString("strip", files.ContainsKey("strip.png") ? "strip.png" : "");
            j.WriteStartArray("symbols"); foreach (var s in symFiles) j.WriteStringValue(s); j.WriteEndArray();
            j.WriteStartArray("debugPort");
            foreach (var (nf, kind, text) in d.Notes) { j.WriteStartObject(); j.WriteNumber("frame", nf); j.WriteString("kind", kind); j.WriteString("text", text); j.WriteEndObject(); }
            j.WriteEndArray();
            j.WriteEndObject();
        }
        files[InfoFile] = ms.ToArray();
        return files;
    }

    private static string HistoryWhy(BugCaptureData d)
    {
        string held = $"{d.Seconds.ToString("0.0", CultureInfo.InvariantCulture)} s";
        if (d.BreakReason.Length > 0 && d.HistoryStartFrame >= 0 && d.Seconds < BugCaptureRing.Seconds - 1)
            return $"{held}: the history started again {((d.FrameAtCapture - d.HistoryStartFrame) / Math.Max(1, d.FramesPerSecond)).ToString("0.0", CultureInfo.InvariantCulture)} s before the capture, when {d.BreakReason}";
        if (d.Seconds < BugCaptureRing.Seconds - 1) return $"{held}: the game had run only that long";
        return $"{held}: the last {BugCaptureRing.Seconds} s (from the newest save state at least {BugCaptureRing.Seconds} s old)";
    }

    /// <summary>A file name without folders or characters Windows refuses.</summary>
    public static string SafeName(string name)
    {
        string n = Path.GetFileName(name.Replace('\\', '/'));
        foreach (char c in Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '|', '?', '*' })) n = n.Replace(c, '_');
        return n.Length == 0 ? "file" : n;
    }

    /// <summary>The bundle's folder name: the capture's local time (sortable; a second capture in the same second gets -2, -3, ...).</summary>
    public static string FolderName(DateTime local) => local.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);

    /// <summary>Writes the files into a new folder <c>&lt;bugsDir&gt;\&lt;date_time&gt;</c> (never into an existing one); returns its path.</summary>
    public static string WriteFolder(string bugsDir, SortedDictionary<string, byte[]> files, DateTime local)
    {
        Directory.CreateDirectory(bugsDir);
        string name = FolderName(local), dir = Path.Combine(bugsDir, name);
        for (int i = 2; Directory.Exists(dir); i++) dir = Path.Combine(bugsDir, $"{name}-{i}");
        string tmp = dir + ".partial";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        foreach (var (rel, data) in files)
        {
            string p = Path.Combine(tmp, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, data);
        }
        Directory.Move(tmp, dir);   // a bundle appears whole or not at all
        return dir;
    }

    /// <summary>The files as one zip (Lite's download).</summary>
    public static byte[] Zip(SortedDictionary<string, byte[]> files)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var (rel, data) in files)
            {
                bool packed = rel.EndsWith(".png", StringComparison.OrdinalIgnoreCase);   // already deflated
                var e = z.CreateEntry(rel, packed ? CompressionLevel.NoCompression : CompressionLevel.Fastest);
                using var s = e.Open();
                s.Write(data, 0, data.Length);
            }
        }
        return ms.ToArray();
    }

    /// <summary>The zip's download name: <c>bug_&lt;rom&gt;_&lt;date_time&gt;.zip</c>.</summary>
    public static string ZipName(string romName, DateTime local) =>
        $"bug_{SafeName(Path.GetFileNameWithoutExtension(romName)).Replace(' ', '_')}_{FolderName(local)}.zip";
}

/// <summary>
/// Where the desktop app saves a bundle (D6, the plan's "Where"): a ROM that belongs to a BrokenDev project (its sha256 is the ROM of a build
/// record <c>BROKENDEV_HOME\out\&lt;id&gt;\build.json</c>) goes to the project's game workspace, <c>&lt;workspace&gt;\bugs\</c> (outside git: BrokenDev
/// checks the workspaces ignore it); any other ROM to <c>%LOCALAPPDATA%\BrokenNes\bugs\</c> (never the app's config or saves folder).
/// <c>BROKENNES_BUGS_DIR</c> replaces that default folder (the tests' scratch folder).
/// </summary>
public static class BugPlaces
{
    public const string DefaultBrokenDevHome = @"P:\BrokenDev\env";
    public const string BugsFolder = "bugs";

    public static string BrokenDevHome() => Environment.GetEnvironmentVariable("BROKENDEV_HOME") is { Length: > 0 } h ? h : DefaultBrokenDevHome;

    public static string DefaultFolder()
    {
        if (Environment.GetEnvironmentVariable("BROKENNES_BUGS_DIR") is { Length: > 0 } d) return d;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrokenNes", BugsFolder);
    }

    /// <summary>
    /// The folder for a ROM: the project's <c>bugs</c> folder (the build record's <c>bugs</c>, else its live workspace's), or the default
    /// folder. <paramref name="why"/> says which and why (a project with no workspace in its record goes to the default folder, said).
    /// </summary>
    public static string For(string romSha256, out BugProject? project, out string why, List<(string Name, byte[] Data)>? symbols = null)
    {
        project = FindProject(romSha256, symbols, out string recordNote);
        if (project != null)
        {
            if (project.BugsDir.Length > 0 && project.Workspace.Length > 0 && Directory.Exists(project.Workspace))
            {
                why = $"the ROM is BrokenDev project {project.Id}'s build: its game workspace {project.Workspace}";
                return project.BugsDir;
            }
            why = $"the ROM is BrokenDev project {project.Id}'s build, but its build record names no game workspace ({recordNote}): the default folder";
            return DefaultFolder();
        }
        why = "the ROM belongs to no BrokenDev project: the default folder";
        return DefaultFolder();
    }

    /// <summary>The project whose newest successful build record's ROM has this sha256, or null.</summary>
    public static BugProject? FindProject(string romSha256, List<(string Name, byte[] Data)>? symbols, out string note)
    {
        note = "";
        string outRoot = Path.Combine(BrokenDevHome(), "out");
        if (!Directory.Exists(outRoot) || romSha256.Length == 0) return null;
        foreach (string dir in Directory.GetDirectories(outRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            string rec = Path.Combine(dir, "build.json");
            if (!File.Exists(rec)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(rec));
                var r = doc.RootElement;
                if (!r.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True || !r.TryGetProperty("outputs", out var outs)) continue;
                bool match = false;
                foreach (var o in outs.EnumerateArray())
                    if (Str(o, "id") == "rom" && Str(o, "sha256").Equals(romSha256, StringComparison.OrdinalIgnoreCase)) match = true;
                if (!match) continue;
                var p = new BugProject { Id = Str(r, "project"), Title = Str(r, "title"), BuildRecord = rec, BuildStarted = Str(r, "startedAtUtc") };
                if (p.Id.Length == 0) p.Id = Path.GetFileName(dir);
                string bugs = Str(r, "bugs");
                if (r.TryGetProperty("workspace", out var ws) && Str(ws, "kind") is "live" or "folder" && Str(ws, "path").Length > 0) p.Workspace = Str(ws, "path");
                if (bugs.Length > 0) { p.BugsDir = bugs; if (p.Workspace.Length == 0) p.Workspace = Path.GetDirectoryName(bugs.TrimEnd('\\', '/')) ?? ""; }
                else if (p.Workspace.Length > 0) p.BugsDir = Path.Combine(p.Workspace, BugsFolder);
                else note = r.TryGetProperty("workspace", out var w2) ? $"workspace kind '{Str(w2, "kind")}'" : "no workspace field";
                if (symbols != null)
                    foreach (var o in outs.EnumerateArray())
                        if (Str(o, "id") != "rom" && Str(o, "published") is { Length: > 0 } pub && File.Exists(pub) && IsSymbols(pub)) symbols.Add((Path.GetFileName(pub), File.ReadAllBytes(pub)));
                return p;
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { note = $"{rec}: {e.Message}"; }
        }
        return null;
    }

    private static bool IsSymbols(string path) => Path.GetExtension(path).ToLowerInvariant() is ".mlb" or ".sym" or ".noi" or ".dbg" or ".nl" or ".map";

    private static string Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
