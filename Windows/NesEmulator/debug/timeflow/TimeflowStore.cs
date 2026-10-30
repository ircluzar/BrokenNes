using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NesEmulator.Debugging.Capture;

namespace NesEmulator.Debugging.Timeflow;

/// <summary>A timeline as a listing shows it.</summary>
public sealed record TimelineInfo(string Dir, TimelineMeta Meta, long Bytes);

/// <summary>
/// Where timelines live (milestone T1): <c>BROKENDEV_HOME\timeflow\&lt;project&gt;\</c> for a ROM that is a BrokenDev project's build (its sha256 is
/// in a build record), else <c>BROKENDEV_HOME\timeflow\_roms\&lt;rom name&gt;-&lt;sha256 prefix&gt;\</c>. Never in a git repository, never in
/// <c>%APPDATA%\BrokenNes</c> (refused). In a game's folder: <c>roms\&lt;sha256&gt;&lt;ext&gt;</c> (each build's ROM, once), <c>timelines\&lt;id&gt;\</c>,
/// and <c>timeflow.json</c> (the cap: 5 GB by default, adjustable per game). When a game's folder grows past its cap, the timelines retire oldest
/// first (by last use); a timeline marked keep, and the one being recorded, never retire.
/// </summary>
public static class TimeflowStore
{
    public const long DefaultCapBytes = 5L * 1024 * 1024 * 1024;
    public const string Folder = "timeflow";

    /// <summary><c>BROKENDEV_HOME\timeflow</c> (the tests point BROKENDEV_HOME at a scratch folder).</summary>
    public static string Root()
    {
        // BROKENDEV_TIMEFLOW replaces the root (the MCP client test's scratch folder); else BROKENDEV_HOME\timeflow
        string r = Path.GetFullPath(Environment.GetEnvironmentVariable("BROKENDEV_TIMEFLOW") is { Length: > 0 } o ? o : Path.Combine(BugPlaces.BrokenDevHome(), Folder));
        string app = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrokenNes"));
        if (r.StartsWith(app, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"timelines are never kept under {app} (BROKENDEV_HOME points there)");
        return r;
    }

    public static string Sha256(byte[] rom) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(rom)).ToLowerInvariant();

    /// <summary>The folder of a game's timelines, and its project (or "" with the per-ROM folder).</summary>
    public static string GameDir(string romSha256, string romName, out string project)
    {
        var p = BugPlaces.FindProject(romSha256, null, out _);
        project = p?.Id ?? "";
        if (project.Length > 0) return Path.Combine(Root(), Safe(project));
        string stem = Safe(Path.GetFileNameWithoutExtension(romName));
        if (stem.Length == 0) stem = "rom";
        return Path.Combine(Root(), "_roms", $"{stem}-{romSha256[..Math.Min(12, romSha256.Length)]}");
    }

    /// <summary>A project's folder by its id (the CLI and the agents name projects).</summary>
    public static string ProjectDir(string project) => Path.Combine(Root(), Safe(project));

    private static string Safe(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray()).Trim('.');

    /// <summary>Keeps the ROM in the game's folder once (by sha256) and returns its path.</summary>
    public static string StoreRom(string gameDir, byte[] rom, string romName)
    {
        string dir = Path.Combine(gameDir, "roms");
        Directory.CreateDirectory(dir);
        string sha = Sha256(rom);
        string ext = Path.GetExtension(romName);
        if (ext.Length == 0 || ext.Length > 8) ext = ".rom";
        string path = Path.Combine(dir, sha + ext.ToLowerInvariant());
        if (!File.Exists(path))
        {
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, rom);
            File.Move(tmp, path, true);
        }
        return path;
    }

    /// <summary>The ROM a timeline names, from its game's folder (refused when missing or when its bytes are not the recorded sha256).</summary>
    public static byte[] RomOf(string timelineDir, TimelineMeta m)
    {
        string gameDir = GameDirOf(timelineDir);
        string path = Path.Combine(gameDir, "roms", m.RomSha256 + m.RomExt);
        if (!File.Exists(path))
        {
            var any = Directory.Exists(Path.Combine(gameDir, "roms")) ? Directory.GetFiles(Path.Combine(gameDir, "roms"), m.RomSha256 + ".*").FirstOrDefault() : null;
            path = any ?? throw new FileNotFoundException($"timeline {m.Id} names ROM {m.RomName} ({m.RomSha256}), which is not in {Path.Combine(gameDir, "roms")}");
        }
        byte[] rom = File.ReadAllBytes(path);
        if (Sha256(rom) != m.RomSha256) throw new InvalidDataException($"{path} is not the ROM timeline {m.Id} recorded (its sha256 differs)");
        return rom;
    }

    /// <summary>The game folder a timeline folder is in (<c>&lt;game&gt;\timelines\&lt;id&gt;</c>).</summary>
    public static string GameDirOf(string timelineDir) => Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(timelineDir).TrimEnd('\\', '/'))!)!;

    public static string NewId() => DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(2)).ToLowerInvariant();

    public static string TimelinesDir(string gameDir) => Path.Combine(gameDir, "timelines");

    // ------------------------------------------------------------------------------------------------ listing

    public static long DirBytes(string dir) => Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;

    /// <summary>A game folder's bytes against its cap: everything but <c>profiles\</c> (T3's profiled spans are temporary, on top of the cap, with their own limit).</summary>
    public static long GameBytes(string gameDir) => DirBytes(gameDir) - DirBytes(Path.Combine(gameDir, "profiles"));

    public static List<TimelineInfo> List(string gameDir)
    {
        var l = new List<TimelineInfo>();
        string td = TimelinesDir(gameDir);
        if (!Directory.Exists(td)) return l;
        foreach (string d in Directory.GetDirectories(td))
        {
            try { l.Add(new TimelineInfo(d, TimelineMeta.Load(d), DirBytes(d))); }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException) { }
        }
        return l.OrderBy(t => t.Meta.CreatedUtc).ToList();
    }

    /// <summary>Every game folder under the root (projects and per-ROM folders).</summary>
    public static List<string> GameDirs()
    {
        string r = Root();
        var l = new List<string>();
        if (!Directory.Exists(r)) return l;
        foreach (string d in Directory.GetDirectories(r))
        {
            if (Path.GetFileName(d) == "_roms") l.AddRange(Directory.GetDirectories(d));
            else l.Add(d);
        }
        return l;
    }

    /// <summary>A timeline by id (or a folder path), anywhere under the root.</summary>
    public static string? Find(string idOrDir)
    {
        if (Directory.Exists(idOrDir) && File.Exists(Path.Combine(idOrDir, "timeline.json"))) return Path.GetFullPath(idOrDir);
        foreach (string g in GameDirs())
        {
            string d = Path.Combine(TimelinesDir(g), idOrDir);
            if (File.Exists(Path.Combine(d, "timeline.json"))) return d;
        }
        return null;
    }

    // ------------------------------------------------------------------------------------------------ the cap

    public static long CapOf(string gameDir)
    {
        string f = Path.Combine(gameDir, "timeflow.json");
        try
        {
            if (File.Exists(f))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                if (doc.RootElement.TryGetProperty("capBytes", out var c) && c.TryGetInt64(out long v) && v > 0) return v;
            }
        }
        catch (Exception e) when (e is IOException or JsonException) { }
        return DefaultCapBytes;
    }

    public static void SetCap(string gameDir, long bytes)
    {
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(gameDir, "timeflow.json"), JsonSerializer.Serialize(new { capBytes = bytes }, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Retires timelines (oldest last use first; never one marked keep, never <paramref name="protectId"/>, never one still recording) until the
    /// game's folder is within its cap. Returns the ids retired; <paramref name="still"/> is the folder's size after.
    /// </summary>
    public static List<string> Enforce(string gameDir, string? protectId, out long still, long? capOverride = null, string? openDir = null, long openBytes = 0)
    {
        long cap = capOverride ?? CapOf(gameDir);
        var retired = new List<string>();
        // a timeline being written is counted by what its writer wrote (Windows lists an open file's size as it was when last closed)
        long Size() => GameBytes(gameDir) - (openDir != null ? DirBytes(openDir) : 0) + openBytes;
        still = Size();
        if (still <= cap) return retired;
        var candidates = List(gameDir).Where(t => !t.Meta.Keep && t.Meta.Id != protectId && (t.Meta.Status != "recording" || Stale(t.Dir)))
                                      .OrderBy(t => t.Meta.LastUsedUtc).ThenBy(t => t.Meta.CreatedUtc).ToList();
        foreach (var t in candidates)
        {
            if (still <= cap) break;
            try { Directory.Delete(t.Dir, true); retired.Add(t.Meta.Id); still -= t.Bytes; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        still = Size();
        return retired;
    }

    /// <summary>A timeline still marked recording whose description has not been written for 10 minutes: its recorder died (a crash); it can retire.</summary>
    private static bool Stale(string dir) => DateTime.UtcNow - File.GetLastWriteTimeUtc(Path.Combine(dir, "timeline.json")) > TimeSpan.FromMinutes(10);

    /// <summary>Marks a timeline keep (never retired) or not.</summary>
    public static void SetKeep(string timelineDir, bool keep)
    {
        var m = TimelineMeta.Load(timelineDir);
        m.Keep = keep; m.Save(timelineDir);
    }

    /// <summary>Notes a use (a jump, a resume): the cap retires the least recently used first.</summary>
    public static void Touch(string timelineDir)
    {
        try { var m = TimelineMeta.Load(timelineDir); if (m.Status == "recording") return; m.LastUsedUtc = DateTime.UtcNow; m.Save(timelineDir); }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException) { }
    }
}
