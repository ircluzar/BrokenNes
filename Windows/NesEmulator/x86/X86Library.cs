// The asset library (C:\BrokenNes-work\library, or BROKENNES_LIBRARY): "lib:<id>" names for disks, states and CDs, read from its library.json.
// Written for BrokenNes (2026-11-27, library PROPOSAL.md section 5).
//
// - Resolve("lib:<id>") gives the file to open (a bin/cue disc: its .cue companion); any other string passes through unchanged.
// - Install() hangs a DiskImage.BaseResolver on: a state whose disks are not open finds them in the catalogue by their DSKD hash
//   (so --loadstate lib:<state> needs no --hdd). Library disks are opened like every disk (read into memory, copy-on-write overlay:
//   the file is never written); their identity hash is seeded from the catalogue only when the file's size and time are the catalogued ones.
// - IsInLibrary / GuardWrite: nothing BrokenNes writes (saves, states, shots, warm caches, p256 sidecars) may land inside the library folder.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NesEmulator.X86;

public static class X86Library
{
    public const string DefaultRoot = @"C:\BrokenNes-work\library";
    public const string Prefix = "lib:";

    /// <summary>One catalogue entry (the fields the emulator uses).</summary>
    public sealed record Item(string Id, string File, string Kind, string? Console, string? Cd, string? Identity, string? P256, string? Sha256,
        long Size, long MtimeTicks, string[] Parent, string[] ParentHashes, string? Cue)
    {
        /// <summary>The absolute path of the item's file.</summary>
        public string FullPath => Path.Combine(Root, File);
        /// <summary>The file to open for this item: a disc's .cue companion when it has one, else the file.</summary>
        public string OpenPath => Cue != null ? Path.Combine(Root, Cue) : FullPath;
    }

    /// <summary>The library folder: BROKENNES_LIBRARY, else <see cref="DefaultRoot"/>.</summary>
    public static string Root
    {
        get
        {
            string r = Environment.GetEnvironmentVariable("BROKENNES_LIBRARY") is { Length: > 0 } e ? e : DefaultRoot;
            try { r = Path.GetFullPath(r); } catch (Exception) { }
            return r.TrimEnd('\\', '/');
        }
    }

    public static bool IsLibId(string? s) => s != null && s.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    // ---------------- the catalogue (re-read when library.json changes) ----------------
    private static readonly object _lock = new();
    private static string? _catPath; private static long _catTicks = -1, _catLen = -1;
    private static List<Item> _items = new();

    /// <summary>Every catalogue item (empty when there is no library.json).</summary>
    public static IReadOnlyList<Item> Items()
    {
        string cat = Path.Combine(Root, "library.json");
        lock (_lock)
        {
            var fi = new FileInfo(cat);
            if (!fi.Exists) { _items = new(); _catPath = cat; _catTicks = -1; _catLen = -1; return _items; }
            if (_catPath == cat && _catTicks == fi.LastWriteTimeUtc.Ticks && _catLen == fi.Length) return _items;
            byte[] bytes;
            using (var f = new FileStream(cat, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { bytes = new byte[f.Length]; f.ReadExactly(bytes); }
            var list = new List<Item>();
            using (var doc = JsonDocument.Parse(bytes))
                if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                    foreach (var e in items.EnumerateArray())
                    {
                        string? S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                        long L(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long x) ? x : -1;
                        string[] A(string k)
                        {
                            if (!e.TryGetProperty(k, out var v) || v.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
                            var o = new List<string>(); foreach (var x in v.EnumerateArray()) o.Add(x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : ""); return o.ToArray();
                        }
                        string? cue = null;
                        if (e.TryGetProperty("companions", out var comp) && comp.ValueKind == JsonValueKind.Object && comp.TryGetProperty("cue", out var ce)
                            && ce.ValueKind == JsonValueKind.Object && ce.TryGetProperty("file", out var cf) && cf.ValueKind == JsonValueKind.String) cue = cf.GetString();
                        if (S("id") is not { Length: > 0 } id || S("file") is not { Length: > 0 } file) continue;
                        list.Add(new Item(id, file, S("kind") ?? "", S("console"), S("cd"), S("identity"), S("p256"), S("sha256"), L("size"), L("source_mtime_ticks"),
                            A("parent"), A("parent_hashes"), cue));
                    }
            _items = list; _catPath = cat; _catTicks = fi.LastWriteTimeUtc.Ticks; _catLen = fi.Length;
            return _items;
        }
    }

    /// <summary>The item named by an id ("lib:" optional, any case) or by a hash ("p256:HEX" / "sha256:HEX"); null when none.</summary>
    public static Item? Find(string key)
    {
        string k = IsLibId(key) ? key[Prefix.Length..] : key;
        var items = Items();
        foreach (var e in items) if (e.Id.Equals(k, StringComparison.OrdinalIgnoreCase)) return e;
        foreach (var e in items) if (HasHash(e, k)) return e;
        return null;
    }

    private static bool HasHash(Item e, string hash) =>
        (e.Identity != null && e.Identity.Equals(hash, StringComparison.OrdinalIgnoreCase))
        || (e.P256 is { Length: > 0 } p && hash.Equals("p256:" + p, StringComparison.OrdinalIgnoreCase))
        || (e.Sha256 is { Length: > 0 } s && hash.Equals("sha256:" + s, StringComparison.OrdinalIgnoreCase));

    /// <summary>A "lib:&lt;id&gt;" to the file to open (a disc: its .cue when it has one); anything else is returned unchanged.
    /// An unknown id or a missing file is an error naming it (never a guess).</summary>
    public static string Resolve(string pathOrLibId)
    {
        if (!IsLibId(pathOrLibId)) return pathOrLibId;
        Install();
        var e = Find(pathOrLibId) ?? throw new FileNotFoundException($"no library item '{pathOrLibId}' in {Path.Combine(Root, "library.json")}");
        string p = e.OpenPath;
        if (!System.IO.File.Exists(p)) throw new FileNotFoundException($"library item '{e.Id}' is catalogued but its file is missing: {p}");
        return p;
    }

    /// <summary>The CD a library state was made with (its catalogue "cd": the .cue for a bin/cue disc); null when it has none or is not a library state.</summary>
    public static string? CdFor(string stateLibId)
    {
        if (!IsLibId(stateLibId) || Find(stateLibId) is not { } st || st.Cd is not { Length: > 0 } cd) return null;
        var ce = Find(cd) ?? throw new FileNotFoundException($"the CD '{cd}' of library state '{st.Id}' is not in the library");
        if (!System.IO.File.Exists(ce.OpenPath)) throw new FileNotFoundException($"the CD '{cd}' of library state '{st.Id}' is catalogued but missing: {ce.OpenPath}");
        return ce.OpenPath;
    }

    /// <summary>The first parent disk of a library state (the C: a session opens it on), or null.</summary>
    public static string? FirstParentDisk(string stateLibId)
    {
        if (!IsLibId(stateLibId) || Find(stateLibId) is not { } st) return null;
        foreach (var p in st.Parent)
            if (!p.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase) && Find(p) is { } pe && System.IO.File.Exists(pe.FullPath)) return pe.FullPath;
        return null;
    }

    // ---------------- the write guard ----------------

    /// <summary>True when the path is the library folder or anything inside it.</summary>
    public static bool IsInLibrary(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (IsLibId(path)) return true;   // (a lib: id as a write target is the library itself)
        string full; try { full = Path.GetFullPath(path).TrimEnd('\\', '/'); } catch (Exception) { return false; }
        string root = Root;
        return full.Equals(root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Throws when a write would land inside the library (a save, a state, a screenshot...): the library is read-only to BrokenNes.</summary>
    public static void GuardWrite(string? path, string what = "write")
    {
        if (IsInLibrary(path)) throw new UnauthorizedAccessException($"refusing to {what} '{path}': it is inside the asset library ({Root}), which BrokenNes never writes; save into the run folder instead");
    }

    // ---------------- the base resolver ----------------
    private static bool _installed;
    private static Func<string, long, byte[]?>? _previous;

    /// <summary>Hooks the library into <see cref="DiskImage.BaseResolver"/> (once; a resolver set before keeps running first).</summary>
    public static void Install()
    {
        lock (_lock)
        {
            if (_installed) return;
            _installed = true;
            _previous = DiskImage.BaseResolver;
            DiskImage.BaseResolver = (hash, length) => _previous?.Invoke(hash, length) ?? FindBaseInLibrary(hash, length);
        }
    }

    /// <summary>A disk base by its identity hash from the library (or the built-in blank nano98 D: a catalogued state names); null when none.</summary>
    public static byte[]? FindBaseInLibrary(string hash, long length)
    {
        IReadOnlyList<Item> items;
        try { items = Items(); } catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { System.Console.WriteLine($"x86 library: catalogue not read: {e.Message}"); return null; }
        foreach (var e in items)
        {
            if (e.Kind != "disk" || (e.Size >= 0 && e.Size != length) || !HasHash(e, hash)) continue;
            string p = e.FullPath;
            var fi = new FileInfo(p);
            if (!fi.Exists) { System.Console.WriteLine($"x86 library: {e.Id} ({hash}) is catalogued but missing: {p}"); continue; }
            bool sparse = DiskImage.IsSparse(p);
            var img = DiskImage.ReadImageFile(p);
            if (img.LongLength != length) continue;
            // the catalogue's hashes (verified at copy time) are trusted only while the file is the very one catalogued (size + time)
            if (!sparse && fi.Length == e.Size && e.MtimeTicks > 0 && fi.LastWriteTimeUtc.Ticks == e.MtimeTicks)
            {
                if (e.P256 is { Length: > 0 }) DiskImage.SeedHash(img, "p256:" + e.P256.ToUpperInvariant());
                if (e.Sha256 is { Length: > 0 }) DiskImage.SeedLegacyHash(img, "sha256:" + e.Sha256.ToUpperInvariant());
            }
            System.Console.WriteLine($"x86 library: disk {e.Id} for {hash}");
            return img;
        }
        // a disk catalogued without the hash form asked for (no p256 yet, or a sparse N98D whose identity is its header's): same size -> hash it and compare
        foreach (var e in items)
        {
            if (e.Kind != "disk" || e.Size < 0 || !hash.StartsWith("p256:", StringComparison.OrdinalIgnoreCase)) continue;
            bool sparse = e.File.EndsWith(".n98d", StringComparison.OrdinalIgnoreCase);
            if (!sparse && (e.Size != length || e.P256 is { Length: > 0 })) continue;
            string p = e.FullPath;
            if (!System.IO.File.Exists(p)) continue;
            var img = DiskImage.ReadImageFile(p);
            if (img.LongLength == length && DiskImage.HashOf(img).Equals(hash, StringComparison.OrdinalIgnoreCase)) { System.Console.WriteLine($"x86 library: disk {e.Id} for {hash} (hashed)"); return img; }
        }
        // a state whose parent is "builtin:blank_d_..." (the nano98 seat's empty FAT D:): made, not stored
        if (length == (long)N98Seat.StandardDriveMb << 20)
            foreach (var e in items)
                for (int i = 0; i < e.Parent.Length && i < e.ParentHashes.Length; i++)
                    if (e.Parent[i].StartsWith("builtin:blank_d", StringComparison.OrdinalIgnoreCase) && e.ParentHashes[i].Equals(hash, StringComparison.OrdinalIgnoreCase))
                    {
                        System.Console.WriteLine($"x86 library: built-in blank D: for {hash}");
                        return N98Seat.MakeBlankDrive(N98Seat.StandardDriveMb);
                    }
        return null;
    }
}
