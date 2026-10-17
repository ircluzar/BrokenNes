// Round 6 (presets): the preset library as a folder of `.bn2preset` files. A pure library over a directory (no UI, no plugin): list, search, add, rename, delete, export and import. The folder is shared by every project; a subfolder is a category.
// `PresetLibrary` (the glue to the Presets tab) is a thin layer over this, and the project's own copy (section PRS1) does not depend on it: a project still plays when a file here is moved or gone.
//
// Safety: an id is a path relative to the folder and is checked before use (no `..`, no drive, the right extension); a file is read only up to the format's size cap; nothing here ever overwrites a file it did not write in the same call
// (a name that is taken gets " (2)"); a damaged file is LISTED with its reason, never hidden and never half-loaded.
using System.Text;
using BrokenNes2.Instruments;

namespace BrokenNes2.Presets;

public sealed record StoreResult(bool Ok, string Message, string? Id = null);

public sealed record StoreEntry(string Id, string Name, ConsoleId? Console, string Category, string Detail, string Game, string Sha1, string[] Tags, string Notes, int Fields, int Blobs, bool HasNoteBehaviour, bool Corrupted, string? Problem, long Size, uint Crc)
{
    public string ConsoleName => Console is { } c ? PresetFile.ConsoleName(c) : "?";
}

public sealed class PresetStore
{
    public const int MaxFiles = 5000, MaxDepth = 4;
    private readonly string root;
    private readonly object gate = new();
    private readonly Dictionary<string, (long Length, long Ticks, StoreEntry Entry)> cache = new(StringComparer.OrdinalIgnoreCase);
    private List<StoreEntry> entries = new();
    private long version = 1;
    private long lastScanTick;

    public PresetStore(string folder) { root = Path.GetFullPath(folder); }

    public string Root => root;

    /// <summary>Changes whenever the list may have changed. Call <see cref="Refresh"/> first to notice changes made by other programs.</summary>
    public long Version { get { lock (gate) return version; } }

    public IReadOnlyList<StoreEntry> Entries { get { lock (gate) return entries; } }

    // ---- ids ----

    /// <summary>The full path an id names, or null when the id is not a path inside the folder to a preset file.</summary>
    public string? PathOf(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 400 || id.Contains('\0') || id.Contains(':') || id.StartsWith('/') || id.StartsWith('\\')) return null;
        if (id.Split('/', '\\').Any(part => part == ".." || part == "." || part.Length == 0)) return null;
        if (!id.EndsWith(PresetFile.Extension, StringComparison.OrdinalIgnoreCase)) return null;
        string full;
        try { full = Path.GetFullPath(Path.Combine(root, id.Replace('/', Path.DirectorySeparatorChar))); }
        catch (ArgumentException) { return null; }
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private string IdOf(string fullPath) => Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    // ---- scanning ----

    /// <summary>Looks at the folder again (at most once per <paramref name="minIntervalMs"/>, unless forced) and returns whether the list changed.</summary>
    public bool Refresh(bool force = false, int minIntervalMs = 1500)
    {
        lock (gate)
        {
            long now = Environment.TickCount64;
            if (!force && lastScanTick != 0 && now - lastScanTick < minIntervalMs) return false;
            lastScanTick = now;
            var found = new List<StoreEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(root))
            {
                int count = 0;
                foreach (var path in Walk(root, 0))
                {
                    if (++count > MaxFiles) break;
                    FileInfo fi;
                    try { fi = new FileInfo(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                    string id = IdOf(path);
                    seen.Add(id);
                    if (cache.TryGetValue(id, out var c) && c.Length == fi.Length && c.Ticks == fi.LastWriteTimeUtc.Ticks) { found.Add(c.Entry); continue; }
                    var entry = Scan(path, id, fi);
                    cache[id] = (fi.Length, fi.LastWriteTimeUtc.Ticks, entry);
                    found.Add(entry);
                }
            }
            foreach (var k in cache.Keys.Where(k => !seen.Contains(k)).ToList()) cache.Remove(k);
            found.Sort((a, b) => { int c = string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase); return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            bool changed = found.Count != entries.Count || !found.Zip(entries).All(p => ReferenceEquals(p.First, p.Second));
            if (changed) { entries = found; version++; }
            return changed;
        }
    }

    private static IEnumerable<string> Walk(string dir, int depth)
    {
        List<string> files = [];
        try { files = Directory.EnumerateFiles(dir, "*" + PresetFile.Extension, SearchOption.TopDirectoryOnly).ToList(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        foreach (var f in files) yield return f;
        if (depth >= MaxDepth) yield break;
        List<string> dirs = [];
        try { dirs = Directory.EnumerateDirectories(dir).ToList(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        foreach (var d in dirs)
        {
            try { if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in Walk(d, depth + 1)) yield return f;
        }
    }

    private StoreEntry Scan(string path, string id, FileInfo fi)
    {
        string category = CategoryOfId(id);
        if (fi.Length > PresetFile.MaxFileBytes)
            return new StoreEntry(id, Path.GetFileNameWithoutExtension(path), null, category, $"cannot be opened: too big ({fi.Length:N0} bytes)", "", "", [], "", 0, 0, false, false, $"too big ({fi.Length:N0} bytes)", fi.Length, 0);
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new StoreEntry(id, Path.GetFileNameWithoutExtension(path), null, category, "cannot be read: " + e.Message, "", "", [], "", 0, 0, false, false, e.Message, fi.Length, 0);
        }
        var r = PresetFile.ReadShallow(bytes);
        if (r.Data == null) return new StoreEntry(id, Path.GetFileNameWithoutExtension(path), null, category, "cannot be opened: " + r.Problem, "", "", [], "", 0, 0, false, false, r.Problem, fi.Length, 0);
        var d = r.Data;
        string detail = $"{(d.InstrumentName.Length > 0 ? d.InstrumentName : d.Kind.ToString())}{(d.Steps > 0 ? $", corrupted ({d.Steps})" : ", clean")}{(d.SourceGame.Length > 0 ? ", " + GameTitle(d.SourceGame) : "")}";
        return new StoreEntry(id, d.Name, d.Console, category, detail, d.SourceGame, d.SourceSha1, d.Tags, d.Notes, d.FieldCount, d.BlobCount, d.HasNoteBehaviour, d.Steps > 0, null, fi.Length, PresetFile.Checksum(bytes));
    }

    private static string CategoryOfId(string id) { int i = id.LastIndexOf('/'); return i < 0 ? "Library" : id[..i]; }

    // ---- search ----

    /// <summary>Entries whose name, tags, source game, instrument, category or console contain every word of <paramref name="text"/> (case does not matter), of one console short name (null: all). A file that cannot be opened is listed under every console.</summary>
    public IReadOnlyList<StoreEntry> Search(string text, string? console)
    {
        var words = (text ?? "").Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var list = new List<StoreEntry>();
        foreach (var e in Entries)
        {
            if (console != null && e.Console != null && e.ConsoleName != console) continue;
            bool all = true;
            foreach (var w in words)
            {
                bool hit = e.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || e.Game.Contains(w, StringComparison.OrdinalIgnoreCase) || e.Category.Contains(w, StringComparison.OrdinalIgnoreCase)
                    || e.ConsoleName.Contains(w, StringComparison.OrdinalIgnoreCase) || e.Detail.Contains(w, StringComparison.OrdinalIgnoreCase)
                    || !PresetMutants.On(PresetMutants.SearchIgnoresTags) && e.Tags.Any(t => t.Contains(w, StringComparison.OrdinalIgnoreCase));
                if (!hit) { all = false; break; }
            }
            if (all) list.Add(e);
        }
        return list;
    }

    // ---- reading one ----

    /// <summary>The preset a library entry holds, fully checked.</summary>
    public PresetRead Load(string id, out byte[]? bytes)
    {
        bytes = null;
        string? path = PathOf(id);
        if (path == null) return new PresetRead(null, "that is not a preset of this library");
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return new PresetRead(null, "the file is gone (moved or deleted outside BrokenNes2)");
            if (fi.Length > PresetFile.MaxFileBytes) return new PresetRead(null, $"too big ({fi.Length:N0} bytes)");
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new PresetRead(null, "cannot be read: " + e.Message); }
        var r = PresetFile.Read(bytes);
        if (!r.Ok) bytes = null;
        return r;
    }

    // ---- writing ----

    /// <summary>Puts a preset in the library (in the folder of its source game); a name that is taken gets " (2)". The new file is written whole, then moved into place: a crash never leaves half a preset under the final name.</summary>
    public StoreResult Add(PresetData d)
    {
        var bytes = PresetFile.TryWrite(d, out string? problem);
        if (bytes == null) return new StoreResult(false, "This instrument cannot be kept as a preset: " + problem);
        return AddBytes(bytes, d);
    }

    private StoreResult AddBytes(byte[] bytes, PresetData d)
    {
        lock (gate)
        {
            string dir = Path.Combine(root, CategoryFolder(d.SourceGame));
            string? path = WriteUnique(dir, d.Name, bytes, out string? why);
            if (path == null) return new StoreResult(false, "Could not write the preset: " + why);
            Refresh(force: true);
            string id = IdOf(path);
            return new StoreResult(true, $"Added \"{d.Name}\" to the library ({id})", id);
        }
    }

    private static string? WriteUnique(string dir, string name, byte[] bytes, out string? why)
    {
        why = null;
        try
        {
            Directory.CreateDirectory(dir);
            string stem = SafeFileName(name);
            string tmp = Path.Combine(dir, ".tmp-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(tmp, bytes);
            for (int n = 1; n < 1000; n++)
            {
                string path = Path.Combine(dir, n == 1 ? stem + PresetFile.Extension : $"{stem} ({n}){PresetFile.Extension}");
                try
                {
                    if (PresetMutants.On(PresetMutants.LibraryOverwrites) && n == 1) { File.Move(tmp, path, overwrite: true); return path; }
                    File.Move(tmp, path, overwrite: false);          // fails when the name is taken: the loop tries the next
                    return path;
                }
                catch (IOException) when (File.Exists(path)) { }
            }
            File.Delete(tmp);
            why = "too many presets with this name";
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { why = e.Message; return null; }
    }

    public StoreResult Rename(string id, string newName)
    {
        newName = (newName ?? "").Trim();
        if (newName.Length == 0) return new StoreResult(false, "A preset needs a name.");
        if (newName.Length > PresetFile.MaxName) return new StoreResult(false, $"A name is at most {PresetFile.MaxName} characters.");
        if (newName.Any(ch => ch < 32 || ch == 127)) return new StoreResult(false, "A name cannot hold control characters.");
        lock (gate)
        {
            var r = Load(id, out _);
            if (r.Data == null) return new StoreResult(false, "Cannot rename: " + r.Problem);
            string old = r.Data.Name;
            if (!PresetMutants.On(PresetMutants.RenameKeepsOldName)) r.Data.Name = newName;
            var bytes = PresetFile.TryWrite(r.Data, out string? problem);
            if (bytes == null) return new StoreResult(false, "Cannot rename: " + problem);
            string path = PathOf(id)!;
            string dir = Path.GetDirectoryName(path)!;
            string? created;
            string? why;
            if (string.Equals(Path.GetFileNameWithoutExtension(path), SafeFileName(newName), StringComparison.OrdinalIgnoreCase))
            {
                // the file name does not change (only the name inside it, or its capitals): the file is replaced in place
                created = path; why = null;
                try { string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N"); File.WriteAllBytes(tmp, bytes); File.Move(tmp, path, overwrite: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { created = null; why = e.Message; }
            }
            else
            {
                created = WriteUnique(dir, newName, bytes, out why);
                if (created != null)
                    try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new StoreResult(false, $"Renamed, but the old file could not be removed: {e.Message}"); }
            }
            if (created == null) return new StoreResult(false, "Could not write the renamed preset: " + why);
            Refresh(force: true);
            return new StoreResult(true, $"Renamed \"{old}\" to \"{newName}\"", IdOf(created));
        }
    }

    public StoreResult Delete(string id)
    {
        string? path = PathOf(id);
        if (path == null) return new StoreResult(false, "That is not a preset of this library.");
        lock (gate)
        {
            if (!File.Exists(path)) { Refresh(force: true); return new StoreResult(false, "The file is already gone."); }
            string name = cache.TryGetValue(id, out var c) ? c.Entry.Name : Path.GetFileNameWithoutExtension(path);
            try { if (!PresetMutants.On(PresetMutants.DeleteKeepsFile)) File.Delete(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new StoreResult(false, "Could not delete: " + e.Message); }
            Refresh(force: true);
            return new StoreResult(true, $"Deleted \"{name}\" from the library");
        }
    }

    /// <summary>Writes a copy of the preset's file to <paramref name="path"/> (checked first, byte for byte what the library holds). Adds the extension when there is none.</summary>
    public StoreResult Export(string id, string path)
    {
        var r = Load(id, out var bytes);
        if (r.Data == null || bytes == null) return new StoreResult(false, "Cannot export: " + r.Problem);
        return ExportBytes(bytes, path, r.Data.Name);
    }

    public static StoreResult ExportBytes(byte[] bytes, string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path)) return new StoreResult(false, "No file was chosen.");
        if (Path.GetExtension(path).Length == 0) path += PresetFile.Extension;
        try
        {
            string full = Path.GetFullPath(path);
            string tmp = full + ".tmp-" + Guid.NewGuid().ToString("N");
            var data = PresetMutants.On(PresetMutants.ExportWritesWrong) ? bytes.Take(bytes.Length / 2).ToArray() : bytes;
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, full, overwrite: true);
            return new StoreResult(true, $"Exported \"{name}\" to {full}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return new StoreResult(false, "Could not export: " + e.Message); }
    }

    /// <summary>Copies a preset file into the library after reading it completely (a file that is damaged, newer, too big or not a preset is refused with the reason and nothing is copied). The same preset twice is refused too.</summary>
    public StoreResult Import(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new StoreResult(false, "No file was chosen.");
        byte[] bytes;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return new StoreResult(false, "That file does not exist.");
            if (fi.Length > PresetFile.MaxFileBytes) return new StoreResult(false, $"That file is too big to be a preset ({fi.Length:N0} bytes; a preset is at most {PresetFile.MaxFileBytes:N0}).");
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return new StoreResult(false, "Cannot read that file: " + e.Message); }
        var r = PresetMutants.On(PresetMutants.ImportSkipsValidation) ? PresetFile.ReadShallow(bytes) : PresetFile.Read(bytes);
        if (r.Data == null) return new StoreResult(false, $"{Path.GetFileName(path)} is not a usable preset: {r.Problem}");
        lock (gate)
        {
            Refresh(force: true);
            uint crc = PresetFile.Checksum(bytes);
            var twin = entries.FirstOrDefault(e => e.Crc == crc && e.Size == bytes.Length && e.Problem == null);
            if (twin != null) return new StoreResult(false, $"\"{r.Data.Name}\" is already in the library ({twin.Id}).", twin.Id);
            return AddBytes(bytes, r.Data);
        }
    }

    // ---- names ----

    public static string SafeFileName(string name)
    {
        var sb = new StringBuilder();
        foreach (char ch in name.Trim())
        {
            if (ch < 32 || Path.GetInvalidFileNameChars().Contains(ch) || ch is '/' or '\\') sb.Append('_');
            else sb.Append(ch);
        }
        string s = sb.ToString().Trim().TrimEnd('.', ' ');
        if (s.Length > 80) s = s[..80].TrimEnd('.', ' ');
        if (s.Length == 0) s = "preset";
        string up = s.ToUpperInvariant();
        if (up is "CON" or "PRN" or "AUX" or "NUL" || up.StartsWith("COM") && up.Length == 4 && char.IsDigit(up[3]) || up.StartsWith("LPT") && up.Length == 4 && char.IsDigit(up[3])) s = "_" + s;
        return s;
    }

    /// <summary>The folder a game's presets go in: its title without the extension and the region and dump tags in brackets ("Super Mario World (U) [!].smc" becomes "Super Mario World"); "Unsorted" when the source is not known.</summary>
    public static string CategoryFolder(string sourceGame)
    {
        string t = GameTitle(sourceGame);
        return t.Length == 0 ? "Unsorted" : SafeFileName(t);
    }

    public static string GameTitle(string sourceGame)
    {
        string s = sourceGame.Trim();
        int dot = s.LastIndexOf('.');
        if (dot > 0 && s.Length - dot <= 5 && !s[dot..].Contains(' ')) s = s[..dot];
        // cut at the first region / dump tag
        int cut = s.Length;
        foreach (char open in new[] { '(', '[' }) { int i = s.IndexOf(open); if (i > 0 && i < cut) cut = i; }
        return s[..cut].Trim();
    }
}
