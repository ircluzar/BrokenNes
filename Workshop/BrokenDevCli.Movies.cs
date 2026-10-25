using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using NesEmulator.Debugging.Movies;

namespace BrokenNes.Workshop;

/// <summary>
/// BrokenDev <c>verify</c>'s promoted movies (milestone D5 of docs/projects/brokendev-debugging-plan.md): a movie the project promoted
/// (<c>brokendev.ps1 movie promote</c>) is listed in the manifest's <c>movies</c> section and kept beside it; verify replays each one as a
/// scenario (its input, its power-on, its frame timing) and checks it like any scenario (<c>expect</c>: the picture, chosen variables by symbol,
/// sound...), and the checks section's findings and budgets cover it too. Its keyframes are checked as well:
/// <code>
///   "movies": [ { "id": "first-room", "file": "movies/first-room.bnm", "why": "...", "sync": "same-rom", "expect": { "memory": [ ... ] } } ]
/// </code>
/// <c>sync</c>: <c>same-rom</c> (the default: on the build the movie was recorded with, every keyframe's picture and RAM must match; on
/// another build the keyframes are information, the game may have changed), <c>required</c> (on any build), <c>off</c>.
/// </summary>
internal static partial class BrokenDevCli
{
    /// <summary>This verify's promoted movies, as scenarios (empty without a movies section).</summary>
    private static List<JsonElement> promotedScenarios = new();
    private static readonly Dictionary<string, Movie> moviesByFile = new(StringComparer.OrdinalIgnoreCase);

    private static List<JsonElement> PromotedMovies(JsonElement root, string projectDir, string console, byte[] romBytes, List<string> problems)
    {
        var list = new List<JsonElement>();
        if (!root.TryGetProperty("movies", out var ms)) return list;
        if (ms.ValueKind != JsonValueKind.Array) { problems.Add("movies: must be a list"); return list; }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in ms.EnumerateArray())
        {
            string id = e.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
            string file = e.TryGetProperty("file", out var f) ? f.GetString() ?? "" : "";
            string sync = e.TryGetProperty("sync", out var s) ? s.GetString() ?? "same-rom" : "same-rom";
            if (id.Length == 0 || !System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9_.-]+$")) { problems.Add($"movies: '{id}' is not an id (letters, digits, - _ .)"); continue; }
            if (!seen.Add(id)) { problems.Add($"movies: '{id}' is listed twice"); continue; }
            if (!e.TryGetProperty("why", out var w) || string.IsNullOrWhiteSpace(w.GetString())) problems.Add($"movies: '{id}' has no why (what the movie plays and why it is a test)");
            if (sync is not ("same-rom" or "required" or "off")) { problems.Add($"movies: '{id}': sync '{sync}' (same-rom, required or off)"); continue; }
            string path = Path.GetFullPath(Path.Combine(projectDir, file));
            if (file.Length == 0 || !File.Exists(path)) { problems.Add($"movies: '{id}': no movie at {path}"); continue; }
            Movie mv;
            try { mv = Movie.Load(path); }
            catch (FormatException ex) { problems.Add($"movies: '{id}': {ex.Message}"); continue; }
            if (mv.Start != MovieStart.PowerOn) { problems.Add($"movies: '{id}' starts from a save state; a verify test starts at power-on"); continue; }
            bool sameFamily = mv.Console == console || IsGameBoyKey(mv.Console) && IsGameBoyKey(console);
            if (!sameFamily) { problems.Add($"movies: '{id}' is a {mv.Console} movie; the project runs on the {console}"); continue; }
            if (IsGameBoyKey(mv.Console) && mv.UsesPad2) { problems.Add($"movies: '{id}' presses pad 2, which the Game Boy does not have"); continue; }
            moviesByFile[path] = mv;
            var sc = new JsonObject
            {
                ["id"] = "movie-" + id, ["frames"] = mv.Frames, ["input"] = mv.ToScript(1),
                ["checkpoints"] = new JsonArray(mv.Keys.Select(k => (JsonNode)k.Frame).ToArray()),
                ["movieFile"] = path, ["movieSync"] = sync,
            };
            if (mv.UsesPad2) sc["input2"] = mv.ToScript(2);
            if (mv.Console == "nes") { sc["powerOnRam"] = mv.PowerOn; sc["ntscFrameTiming"] = mv.NtscTiming; }
            if (mv.Console != console) sc["console"] = mv.Console;
            if (e.TryGetProperty("expect", out var ex2)) sc["expect"] = JsonNode.Parse(ex2.GetRawText());
            if (e.TryGetProperty("info", out var inf) && inf.ValueKind == JsonValueKind.True) sc["info"] = true;
            list.Add(JsonDocument.Parse(sc.ToJsonString()).RootElement.Clone());
        }
        return list;
    }

    private static Movie? MovieOf(JsonElement sc) =>
        sc.TryGetProperty("movieFile", out var f) && f.GetString() is { } p && moviesByFile.TryGetValue(p, out var m) ? m : null;

    /// <summary>A movie plays on the cores it was recorded on: verify's cores must be the same, or the scenario is refused (never replayed quietly on others).</summary>
    private static void RefuseMovieCores(Movie mv, string runnerCores)
    {
        string first = runnerCores.Split(' ')[0];
        var p = first.Split('/');
        static string Strip(string id, string prefix) => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? id[prefix.Length..] : id;
        string have = p.Length == 3 ? $"{Strip(p[0], "CPU_")}/{Strip(p[1], "PPU_")}/{Strip(p[2], "APU_")}" : first;
        if (!have.Equals(mv.Cores, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"the movie was recorded on the cores {mv.Cores}; this verify runs {have} (record the movie again on these cores, or give verify the movie's)");
    }

    private static void EvaluateMovie(ScenarioResult res, Movie mv, byte[] romBytes, string mode)
    {
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(romBytes)).ToLowerInvariant();
        bool sameRom = sha.Equals(mv.RomSha256, StringComparison.OrdinalIgnoreCase);
        res.MovieSameRom = sameRom;
        res.MovieSync = mode;
        if (mode == "off") { res.Checks.Add(new CheckResult { Id = "movie-in-sync", Ok = true, Detail = "not checked (sync off)" }); return; }
        if (res.Result != "ok") return;
        int matched = 0; long? off = null; string what = "";
        foreach (var k in mv.Keys)
        {
            var cp = res.Checkpoints.FirstOrDefault(c => c.Frame == k.Frame);
            if (cp == null) continue;
            bool pic = cp.Hash == k.Picture.ToString("X16"), ram = cp.Ram == k.Ram.ToString("X16");
            if (pic && ram) { matched++; continue; }
            if (off == null) { off = k.Frame; what = (pic ? "" : "the picture") + (!pic && !ram ? " and " : "") + (ram ? "" : "the work RAM"); }
        }
        res.MovieOffCourse = off;
        string detail = off == null ? $"every one of the {matched} keyframes matches ({(sameRom ? "the recorded build" : "another build than the recording's")})"
                                    : $"off course after {off} frames ({what}); {matched} of {mv.Keys.Count} keyframes match";
        if (off != null && mode == "same-rom" && !sameRom)
            res.Checks.Add(new CheckResult { Id = "movie-in-sync", Ok = true, Detail = $"information only (another build than the recording's; the game may have changed): {detail}" });
        else res.Checks.Add(new CheckResult { Id = "movie-in-sync", Ok = off == null, Detail = detail });
    }

    private sealed partial class ScenarioResult
    {
        public string MovieSync = "";
        public bool? MovieSameRom;
        public long? MovieOffCourse;
    }
}
