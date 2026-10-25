using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using BrokenNes.Workshop.Movies;
using NesEmulator.Debugging.Capture;
using NesEmulator.Debugging.Checks;
using NesEmulator.Debugging.Movies;

namespace BrokenNes.Workshop.Bugs;

/// <summary>A bug bundle opened for reading (D6): a folder, or a zip (Lite's download) unpacked into a scratch folder of its own.</summary>
internal sealed class BugBundleReader
{
    public string Path { get; private init; } = "";
    /// <summary>The folder holding bug.json (the bundle itself, or where its zip was unpacked).</summary>
    public string Dir { get; private init; } = "";
    public JsonObject Info { get; private init; } = new();
    public string Id => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));

    public static BugBundleReader Open(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        string dir;
        if (Directory.Exists(path)) dir = path;
        else if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))[..16].ToLowerInvariant();
            dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "brokennes-bugs", sha);
            if (!File.Exists(System.IO.Path.Combine(dir, BugBundle.InfoFile)))
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                ZipFile.ExtractToDirectory(path, dir);
            }
        }
        else if (File.Exists(path) && System.IO.Path.GetFileName(path).Equals(BugBundle.InfoFile, StringComparison.OrdinalIgnoreCase)) { dir = System.IO.Path.GetDirectoryName(path)!; path = dir; }
        else throw new FileNotFoundException($"no bug bundle at {path} (a folder with {BugBundle.InfoFile}, or Lite's .zip)");
        string info = System.IO.Path.Combine(dir, BugBundle.InfoFile);
        if (!File.Exists(info)) throw new InvalidDataException($"{path} is not a bug bundle: it has no {BugBundle.InfoFile}");
        var j = JsonNode.Parse(File.ReadAllText(info)) as JsonObject ?? throw new InvalidDataException($"{info}: not a JSON object");
        if ((string?)j["format"] != BugBundle.Format) throw new InvalidDataException($"{info}: format '{j["format"]}' is not {BugBundle.Format}");
        int v = (int?)j["version"] ?? 0;
        if (v < 1 || v > BugBundle.Version) throw new InvalidDataException($"{info}: version {v} is not one this build reads (1 to {BugBundle.Version})");
        return new BugBundleReader { Path = path, Dir = dir, Info = j };
    }

    public string File_(string rel) => System.IO.Path.Combine(Dir, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
    public string S(params string[] keys) { JsonNode? n = Info; foreach (var k in keys) n = n?[k]; return n is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : ""; }
    public bool HasMovie => S("movie").Length > 0 && File.Exists(File_(S("movie")));
    public Movie LoadMovie() => HasMovie ? Movie.Load(File_(S("movie"))) : throw new InvalidOperationException($"the bundle has no movie: {S("noMovie")}");
    public List<string> Symbols() => (Info["symbols"] as JsonArray ?? new JsonArray()).Select(s => File_((string)s!)).Where(File.Exists).ToList();

    /// <summary>The ROM to replay on: <paramref name="given"/>, else the bundle's own copy, else the place it was loaded from if it is still the same ROM.</summary>
    public string Rom(string? given, out string note)
    {
        string sha = S("rom", "sha256");
        if (!string.IsNullOrEmpty(given)) { note = Sha(given) == sha ? "the ROM given (the captured one)" : $"the ROM given, ANOTHER build than the captured one (sha256 {Sha(given)[..12]}, captured {sha[..Math.Min(12, sha.Length)]})"; return given; }
        string own = S("rom", "file");
        if (own.Length > 0 && File.Exists(File_(own))) { note = "the bundle's own copy of the ROM"; return File_(own); }
        string from = S("rom", "loadedFrom");
        if (from.Length > 0 && File.Exists(from) && Sha(from) == sha) { note = $"the ROM at {from} (the bundle has no copy; same sha256)"; return from; }
        throw new FileNotFoundException($"the bundle has no copy of its ROM and {(from.Length > 0 ? from + " is gone or another build" : "names no place")}: give --rom (sha256 {sha})");
    }

    private static string Sha(string p) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
}

/// <summary>
/// <c>--bug</c>: the bug bundles of D6 (docs/projects/brokendev-debugging-plan.md, "D6 as built").
/// <code>
///   --bug info --bug B [--json]                       what the bundle holds
///   --bug play --bug B [--rom R] [--sym F]... [--checks] [--out DIR] [--json]
///                                                     replays the capture: every keyframe checked, the last picture compared, the
///                                                     hardware checks if asked (from a state: the read-before-written rules are off);
///                                                     exit 0 in sync | 1 off course or crashed | 3 no movie to replay | 2 usage
///   --bug list --dir D [--json]                       the bundles in a bugs folder, newest first
/// </code>
/// B: a bundle folder, its bug.json, or Lite's .zip.
/// </summary>
internal static class BugCli
{
    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        string Opt(string n, string d = "") { int i = Array.FindIndex(args, 2, a => a.Equals("--" + n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        List<string> All(string n) { var l = new List<string>(); for (int i = 2; i < args.Length - 1; i++) if (args[i].Equals("--" + n, StringComparison.OrdinalIgnoreCase)) l.Add(args[i + 1]); return l; }
        bool Has(string n) => args.Skip(2).Any(a => a.Equals("--" + n, StringComparison.OrdinalIgnoreCase));
        if (args.Length < 2) { Console.Error.WriteLine("usage: --bug info|play|list ... (see Workshop/Bugs/BugCli.cs)"); return 2; }
        string verb = args[1].ToLowerInvariant();
        var opts = new JsonSerializerOptions { WriteIndented = true };
        try
        {
            switch (verb)
            {
                case "info":
                {
                    var b = BugBundleReader.Open(Need(Opt("bug"), "--bug"));
                    var j = Summary(b);
                    Console.WriteLine(Has("json") ? j.ToJsonString(opts) : Text(j));
                    return 0;
                }
                case "list":
                {
                    string dir = Need(Opt("dir"), "--dir");
                    var arr = List(dir);
                    if (Has("json")) Console.WriteLine(new JsonObject { ["dir"] = dir, ["bugs"] = arr }.ToJsonString(opts));
                    else if (arr.Count == 0) Console.WriteLine($"no bug bundles in {dir}");
                    else foreach (var j in arr) Console.WriteLine(Text((JsonObject)j!));
                    return 0;
                }
                case "play":
                {
                    var b = BugBundleReader.Open(Need(Opt("bug"), "--bug"));
                    var (code, j) = Play(b, Opt("rom") is { Length: > 0 } r ? r : null, All("sym"), Has("checks"), Opt("out") is { Length: > 0 } o ? o : null);
                    Console.WriteLine(Has("json") ? j.ToJsonString(opts) : (string)j["summary"]!);
                    return code;
                }
                default:
                    Console.Error.WriteLine($"unknown --bug verb '{verb}' (info, play, list)");
                    return 2;
            }
        }
        catch (Exception e) when (e is FormatException or ArgumentException or FileNotFoundException or InvalidDataException or NotSupportedException or InvalidOperationException or JsonException or IOException)
        {
            Console.Error.WriteLine($"--bug {verb}: {e.Message}");
            return 2;
        }
    }

    private static string Need(string v, string what) => v.Length > 0 ? v : throw new ArgumentException($"{what} is needed");

    /// <summary>The bundles of a bugs folder (folders with bug.json and .zip files), newest first.</summary>
    public static JsonArray List(string dir)
    {
        var arr = new JsonArray();
        if (!Directory.Exists(dir)) return arr;
        var items = Directory.GetDirectories(dir).Where(d => File.Exists(System.IO.Path.Combine(d, BugBundle.InfoFile)))
            .Concat(Directory.GetFiles(dir, "*.zip")).OrderByDescending(p => System.IO.Path.GetFileName(p), StringComparer.Ordinal);
        foreach (var p in items)
        {
            try { arr.Add(Summary(BugBundleReader.Open(p))); }
            catch (Exception e) when (e is InvalidDataException or JsonException or IOException) { arr.Add(new JsonObject { ["id"] = System.IO.Path.GetFileName(p), ["path"] = p, ["error"] = e.Message }); }
        }
        return arr;
    }

    public static JsonObject Summary(BugBundleReader b) => new()
    {
        ["id"] = b.Id, ["path"] = b.Path, ["note"] = b.S("note"), ["captured"] = b.S("capturedLocal"), ["app"] = b.S("app"), ["console"] = b.S("console"),
        ["rom"] = b.S("rom", "name"), ["romSha256"] = b.S("rom", "sha256"), ["project"] = b.S("project", "id") is { Length: > 0 } p ? p : null,
        ["cores"] = b.S("cores"), ["seconds"] = b.Info["history"]?["seconds"]?.DeepClone(), ["frames"] = b.Info["history"]?["frames"]?.DeepClone(),
        ["start"] = b.S("history", "start"), ["history"] = b.S("history", "why"), ["movie"] = b.HasMovie ? b.File_(b.S("movie")) : null,
        ["noMovie"] = b.S("noMovie") is { Length: > 0 } nm ? nm : null, ["audio"] = b.S("audio", "file") is { Length: > 0 } a ? b.File_(a) : null,
        ["last"] = b.S("last", "file") is { Length: > 0 } l ? b.File_(l) : null, ["strip"] = b.S("strip") is { Length: > 0 } s ? b.File_(s) : null,
        ["pictures"] = (b.Info["pictures"] as JsonArray)?.Count ?? 0,
    };

    private static string Text(JsonObject j) =>
        j["error"] is { } e ? $"{j["id"]}: unreadable ({e})" :
        $"{j["id"]}  {j["captured"]}  {j["console"]} {j["rom"]}{(j["project"] is { } p ? " (" + p + ")" : "")}  {j["seconds"]} s  {(j["movie"] != null ? "movie from " + j["start"] : "NO MOVIE: " + j["noMovie"])}{((string?)j["note"] is { Length: > 0 } n ? "  note: " + n : "")}";

    /// <summary>
    /// Replays a bundle's movie: the keyframes (one a second and the moment of the capture) checked, the last picture written to
    /// <paramref name="outDir"/>, the hardware checks when asked. Exit code: 0 in sync, 1 off course or crashed, 3 no movie.
    /// </summary>
    public static (int code, JsonObject report) Play(BugBundleReader b, string? rom, List<string> sym, bool checks, string? outDir)
    {
        var j = Summary(b);
        if (!b.HasMovie) { j["verdict"] = "no-movie"; j["summary"] = $"{b.Id}: nothing to replay: {b.S("noMovie")}"; return (3, j); }
        var mv = b.LoadMovie();
        string romPath = b.Rom(rom, out string romNote);
        var symbols = sym.Count > 0 ? sym : b.Symbols();
        using var m = MovieMachine.OpenFor(mv, romPath, true, symbols);
        ConsoleChecks? c = null;
        string? checksNote = null;
        if (checks)
        {
            if (m.Host.Dbg == null) checksNote = $"the hardware checks do not cover the {mv.Console}";
            else
            {
                var o = new CheckOptions();
                if (mv.Start == MovieStart.State)
                {
                    // from a state, what was written before it is unknown: every read would look like a read before a write
                    foreach (var rule in new[] { "nes.ram-uninit", "snes.wram-uninit", "snes.vram-uninit", "snes.cgram-uninit", "snes.oam-uninit", "gb.ram-uninit" }) o.Off.Add(rule);
                    checksNote = "the replay starts from a save state: the read-before-written rules are off (what was written before the state is unknown)";
                }
                c = CheckRun.Attach(m.Host.Dbg, o);
            }
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = MoviePlayer.Play(mv, m);
        c?.Finish();
        var rep = MovieCli.ReportJson(mv, m, r);
        j["romUsed"] = romPath; j["romNote"] = romNote; j["replay"] = rep; j["inSync"] = r.InSync;
        j["fps"] = Math.Round(r.FramesRun / Math.Max(1e-9, sw.Elapsed.TotalSeconds));
        string lastRec = b.S("last", "picture");
        ulong lastNow = m.Picture();
        j["lastPicture"] = lastNow.ToString("X16"); j["lastPictureCaptured"] = lastRec; j["lastPictureSame"] = lastRec.Equals(lastNow.ToString("X16"), StringComparison.OrdinalIgnoreCase);
        if (outDir != null)
        {
            Directory.CreateDirectory(outDir);
            string png = System.IO.Path.Combine(outDir, b.Id + "_replay_last.png");
            var pic = LastPicture(m);
            File.WriteAllBytes(png, BugMedia.Png(pic.Pixels, pic.Width, pic.Height));
            j["replayLast"] = png;
        }
        if (c != null)
        {
            j["checks"] = new JsonArray(c.Findings.OrderBy(f => f.Rule).ThenBy(f => f.FirstFrame).Select(CheckRun.FindingJson).ToArray());
            if (checksNote != null) j["checksNote"] = checksNote;
        }
        else if (checksNote != null) j["checksNote"] = checksNote;
        bool ok = r.InSync && (bool)j["lastPictureSame"]!;
        j["verdict"] = ok ? "in-sync" : r.Crashed ? "crashed" : "off-course";
        j["summary"] = $"{b.Id}: {(string)rep["summary"]!}; the last picture {((bool)j["lastPictureSame"]! ? "is the captured one" : $"DIFFERS from the captured one ({lastNow:X16}, captured {lastRec})")}" +
                       (c != null ? $"; {c.Findings.Count} finding(s)" : "") + $" ({romNote})";
        return (ok ? 0 : 1, j);
    }

    /// <summary>The machine's picture now, as the capture keeps one.</summary>
    public static BugPicture LastPicture(MovieMachine m)
    {
        var p = new BugPicture();
        if (m.Host.Nes != null) p.FromRgba(m.Host.Nes.GetFrameBuffer(), 256, 240);
        else { var s = m.Host.Session!; p.FromArgb(s.Frame, s.FrameWidth, s.FrameHeight); }
        return p;
    }
}
