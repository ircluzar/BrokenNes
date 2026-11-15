using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrokenNes.Windows.AutoTas
{
    /// <summary>
    /// An AutoTAS package (docs/projects/autotas-plan.md section 1): a folder tas\&lt;console&gt;\&lt;game_id&gt;\v&lt;N&gt;\ holding autotas.json, full.bnm,
    /// seg\&lt;ch&gt;.bnm, state\&lt;ch&gt;.raw.state / .clean.state, thumb\&lt;ch&gt;.png, notes\&lt;ch&gt;.md. Packages live on T: (never in git): their
    /// states and thumbnails come from commercial games. A state file holds the bytes the app's own save state makes (the NES: its JSON as UTF-8;
    /// the other consoles: their session's bytes), the same bytes BrokenDev's headless sessions save.
    /// </summary>
    public sealed class AutoTasManifest
    {
        [JsonPropertyName("game_id")] public string GameId { get; set; } = "";
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("console")] public string? Console { get; set; }
        [JsonPropertyName("rom_sha1")] public string? RomSha1 { get; set; }
        [JsonPropertyName("rom_sha256")] public string? RomSha256 { get; set; }
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("core")] public AutoTasCore? Core { get; set; }
        [JsonPropertyName("chapters")] public List<AutoTasChapter> Chapters { get; set; } = new();
    }

    public sealed class AutoTasCore
    {
        [JsonPropertyName("cpu")] public string? Cpu { get; set; }
        [JsonPropertyName("ppu")] public string? Ppu { get; set; }
        [JsonPropertyName("apu")] public string? Apu { get; set; }
        [JsonPropertyName("build")] public string? Build { get; set; }
        [JsonPropertyName("state_format")] public JsonElement? StateFormat { get; set; }
    }

    public sealed class AutoTasChapter
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("kind")] public string Kind { get; set; } = "level";
        [JsonPropertyName("start_frame")] public long StartFrame { get; set; }
        [JsonPropertyName("end_frame")] public long EndFrame { get; set; }
        [JsonPropertyName("seg")] public string? Seg { get; set; }
        /// <summary>The segment recorded from the clean state (when the clean-up changes the run); null means <see cref="Seg"/> plays after either jump.</summary>
        [JsonPropertyName("clean_seg")] public string? CleanSeg { get; set; }
        [JsonPropertyName("raw")] public string? Raw { get; set; }
        [JsonPropertyName("clean")] public string? Clean { get; set; }
        [JsonPropertyName("thumb")] public string? Thumb { get; set; }
        [JsonPropertyName("keyframe_hash")] public string? KeyframeHash { get; set; }
        [JsonPropertyName("quality")] public AutoTasQuality? Quality { get; set; }
        [JsonPropertyName("accepted_by")] public string? AcceptedBy { get; set; }
        [JsonPropertyName("retries")] public int Retries { get; set; }
        [JsonIgnore] public long Frames => Math.Max(0, EndFrame - StartFrame);
    }

    public sealed class AutoTasQuality
    {
        [JsonPropertyName("deaths")] public int Deaths { get; set; }
        [JsonPropertyName("frames")] public long Frames { get; set; }
        [JsonPropertyName("score")] public string? Score { get; set; }
        [JsonPropertyName("notes")] public string? Notes { get; set; }
    }

    /// <summary>A package found on disk: its manifest and folder.</summary>
    public sealed class AutoTasPackage
    {
        public AutoTasManifest Manifest { get; }
        public string Dir { get; }
        public AutoTasPackage(AutoTasManifest m, string dir) { Manifest = m; Dir = dir; }

        /// <summary>A package path (relative to the package folder) as a full path, or null; never leaves the package folder.</summary>
        public string? PathOf(string? rel)
        {
            if (string.IsNullOrWhiteSpace(rel)) return null;
            string full = Path.GetFullPath(Path.Combine(Dir, rel.Replace('/', Path.DirectorySeparatorChar)));
            string root = Path.GetFullPath(Dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        public string? ExistingPath(string? rel) { var p = PathOf(rel); return p != null && File.Exists(p) ? p : null; }
        public string? ThumbOf(AutoTasChapter c) => ExistingPath(c.Thumb ?? $"thumb/{c.Id}.png");
        public string? NotesOf(AutoTasChapter c) => ExistingPath(c.Quality?.Notes ?? $"notes/{c.Id}.md");
        public string? StateOf(AutoTasChapter c, bool clean) => clean ? ExistingPath(c.Clean ?? $"state/{c.Id}.clean.state") : ExistingPath(c.Raw ?? $"state/{c.Id}.raw.state");
        public string? SegOf(AutoTasChapter c, bool clean) => (clean ? ExistingPath(c.CleanSeg) : null) ?? ExistingPath(c.Seg ?? $"seg/{c.Id}.bnm");

        public static AutoTasPackage Load(string manifestPath)
        {
            var m = JsonSerializer.Deserialize<AutoTasManifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                    ?? throw new InvalidDataException("empty manifest");
            if (m.Chapters.Count == 0) throw new InvalidDataException("the manifest has no chapters");
            return new AutoTasPackage(m, Path.GetDirectoryName(Path.GetFullPath(manifestPath))!);
        }
    }

    /// <summary>
    /// Finds the AutoTAS package of a ROM: every autotas.json under the root (tas\&lt;console&gt;\&lt;game_id&gt;\v&lt;N&gt;\) whose rom_sha1 or rom_sha256
    /// is the ROM's; the highest version wins. The root is AUTOTAS_ROOT, else BROKENDEV_HOME\Catalog\tas, else T:\BrokenDev\Catalog\tas.
    /// A missing root means no package (the activity is not offered).
    /// </summary>
    public static class AutoTasIndex
    {
        public static string Root()
        {
            string? env = Environment.GetEnvironmentVariable("AUTOTAS_ROOT");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            string? home = Environment.GetEnvironmentVariable("BROKENDEV_HOME");
            if (!string.IsNullOrWhiteSpace(home)) return Path.Combine(home, "Catalog", "tas");
            return @"T:\BrokenDev\Catalog\tas";
        }

        public static (string Sha1, string Sha256) Hashes(byte[] rom) =>
            (Convert.ToHexString(SHA1.HashData(rom)).ToLowerInvariant(), Convert.ToHexString(SHA256.HashData(rom)).ToLowerInvariant());

        /// <summary>The package for these hashes, or null (<paramref name="note"/> says why when it helps).</summary>
        public static AutoTasPackage? Find(string sha1, string sha256, out string note)
        {
            note = "";
            string root = Root();
            if (!Directory.Exists(root)) { note = $"no AutoTAS root ({root})"; return null; }
            AutoTasPackage? best = null;
            List<string> files;
            try { files = Directory.EnumerateFiles(root, "autotas.json", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true }).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { note = e.Message; return null; }
            foreach (string f in files)
            {
                try
                {
                    var p = AutoTasPackage.Load(f);
                    bool match = string.Equals(p.Manifest.RomSha1, sha1, StringComparison.OrdinalIgnoreCase) || string.Equals(p.Manifest.RomSha256, sha256, StringComparison.OrdinalIgnoreCase);
                    if (match && (best == null || p.Manifest.Version > best.Manifest.Version)) best = p;
                }
                catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException) { note = $"{f}: {e.Message}"; }
            }
            if (best == null && note.Length == 0) note = "no package for this ROM";
            return best;
        }
    }
}
