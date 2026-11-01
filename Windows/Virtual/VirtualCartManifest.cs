using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BrokenNes.Windows.Virtual
{
    /// <summary>
    /// A WideMess virtual cartridge (docs/projects/next-pool-plans.md, P4): a small JSON file (<c>.wmv</c>) that names a web game living in its own
    /// folder. Nothing of the game is copied into BrokenNes; the manifest only says where it is and how to start its server.
    /// <code>
    /// {
    ///   "format": "widemess-virtual", "version": 1,
    ///   "title": "VRUN Corrupt The World Remastered",
    ///   "folder": "C:\\...\\VRUN_CTW_Remastered",          (absolute, or relative to the manifest)
    ///   "command": "node",                                 (looked up on PATH, or a path)
    ///   "args": ["tools/replacement-engine/Launch-VRUN.mjs", "--port", "{port}"],
    ///   "ready": "server ready",                           (a line of the server's output that says it listens; optional)
    ///   "page": "/",                                        (the page, on http://127.0.0.1:{port})
    ///   "width": 398, "height": 224,                        (the game's picture; it sits in the middle of the WideMess's 400 x 224)
    ///   "layers": "#screen canvas, #screen video"           (the page's stacked layers, back to front in document order)
    /// }
    /// </code>
    /// <c>{port}</c> (a free port BrokenNes picks) and <c>{folder}</c> are replaced in the arguments.
    /// </summary>
    public sealed class VirtualCartManifest
    {
        public const string Extension = ".wmv";
        public const int ConsoleWidth = 400, ConsoleHeight = 224;

        public string ManifestPath { get; private init; } = "";
        public string Title { get; private init; } = "";
        public string Folder { get; private init; } = "";
        public string Command { get; private init; } = "";
        public string[] Args { get; private init; } = Array.Empty<string>();
        public string ReadyText { get; private init; } = "";
        public string Page { get; private init; } = "/";
        public int Width { get; private init; }
        public int Height { get; private init; }
        public string Layers { get; private init; } = "";

        public static bool IsManifestPath(string? path) => path != null && path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

        /// <summary>Reads and checks a manifest. Throws <see cref="InvalidDataException"/> with a message for the user when it is not usable.</summary>
        public static VirtualCartManifest Load(string path)
        {
            string full = Path.GetFullPath(path);
            if (!File.Exists(full)) throw new InvalidDataException($"The virtual cartridge {full} does not exist.");
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(full), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
            catch (JsonException ex) { throw new InvalidDataException($"The virtual cartridge {Path.GetFileName(full)} is not valid JSON: {ex.Message}"); }
            using (doc)
            {
                var r = doc.RootElement;
                string Str(string name, string? fallback = null)
                {
                    if (r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s) return s;
                    if (fallback != null) return fallback;
                    throw new InvalidDataException($"The virtual cartridge {Path.GetFileName(full)} has no \"{name}\".");
                }
                int Int(string name, int fallback) => r.TryGetProperty(name, out var v) && v.TryGetInt32(out int i) ? i : fallback;

                if (r.ValueKind != JsonValueKind.Object || Str("format", "") != "widemess-virtual")
                    throw new InvalidDataException($"{Path.GetFileName(full)} is not a WideMess virtual cartridge (it needs \"format\": \"widemess-virtual\").");
                if (Int("version", 1) != 1) throw new InvalidDataException($"The virtual cartridge {Path.GetFileName(full)} is version {Int("version", 1)}; this BrokenNes reads version 1.");

                string folder = Str("folder");
                if (!Path.IsPathRooted(folder)) folder = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(full)!, folder));
                if (!Directory.Exists(folder)) throw new InvalidDataException($"The game folder of {Path.GetFileName(full)} does not exist: {folder}");

                var args = new List<string>();
                if (r.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array)
                    foreach (var x in a.EnumerateArray()) args.Add(x.GetString() ?? "");
                int w = Int("width", 398), h = Int("height", 224);
                if (w < 1 || w > ConsoleWidth || h < 1 || h > ConsoleHeight)
                    throw new InvalidDataException($"The picture of {Path.GetFileName(full)} is {w} x {h}; a WideMess virtual cartridge draws at most {ConsoleWidth} x {ConsoleHeight}.");
                string page = Str("page", "/");
                if (!page.StartsWith('/')) page = "/" + page;

                return new VirtualCartManifest
                {
                    ManifestPath = full,
                    Title = Str("title", Path.GetFileNameWithoutExtension(full)),
                    Folder = folder,
                    Command = Str("command"),
                    Args = args.ToArray(),
                    ReadyText = Str("ready", ""),
                    Page = page,
                    Width = w,
                    Height = h,
                    Layers = Str("layers", "#screen canvas, #screen video"),
                };
            }
        }

        /// <summary>The arguments with <c>{port}</c> and <c>{folder}</c> filled in.</summary>
        public IEnumerable<string> ArgsFor(int port) => Args.Select(x => x.Replace("{port}", port.ToString()).Replace("{folder}", Folder));
    }
}
