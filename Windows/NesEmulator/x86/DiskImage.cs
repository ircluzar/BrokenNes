using System.Linq;
// A raw disk image (floppy or hard disk) for the x86 console's BIOS (INT 13h), IDE and floppy controller. Written for BrokenNes.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace NesEmulator.X86;

/// <summary>
/// A sector image with a CHS geometry, copy-on-write: the BASE image is never written (it is shared, read-only, and named by its
/// SHA-256), every sector the machine writes lands in the OVERLAY. A savestate carries only the overlay plus the base's hash
/// (<see cref="SaveDiff"/>), so a state of a 1 GB Windows disk is the few MB that changed, and it reloads onto exactly the disk it saw.
/// A sector written back to its base content leaves the overlay (the diff stays minimal and canonical).
/// </summary>
public sealed class DiskImage
{
    public const int SectorSize = 512;
    /// <summary>The read-only base image (never modified by the machine).</summary>
    public readonly byte[] Base;
    /// <summary>The base of a disk larger than one array (nano98 large game drives); then <see cref="Base"/> is empty.</summary>
    public readonly DiskBigBase? Big;
    public readonly int Cylinders, Heads, Sectors;
    public readonly bool IsFloppy;
    /// <summary>Set by every write (the desktop's battery export clears it).</summary>
    public bool Dirty;
    private readonly Dictionary<long, byte[]> _overlay = new();
    public long Length => Big?.Length ?? Base.LongLength;
    public long TotalSectors => Length / SectorSize;
    private ReadOnlySpan<byte> BaseSector(long s) => Big != null ? Big.Sector(s) : Base.AsSpan((int)(s * SectorSize), SectorSize);
    /// <summary>The number of sectors that differ from the base.</summary>
    public int OverlaySectors => _overlay.Count;

    /// <param name="data">The base image: kept by reference and never written (callers that change their array afterwards must pass a copy).</param>
    public DiskImage(byte[] data, bool floppy)
    {
        Base = data; IsFloppy = floppy;
        Register(data);
        if (floppy)
        {
            (Cylinders, Heads, Sectors) = data.Length switch
            {
                368640 => (40, 2, 9), 737280 => (80, 2, 9), 1228800 => (80, 2, 15), 2949120 => (80, 2, 36), _ => (80, 2, 18),
            };
            return;
        }
        // a hard disk: the geometry its first partition's end CHS says (what the partitioning tool used), else 16 x 63
        int heads = 16, spt = 63;
        if (data.Length >= 512 && data[510] == 0x55 && data[511] == 0xaa && data[0x1c2] != 0)
        {
            int eh = data[0x1c3], es = data[0x1c4] & 0x3f;
            if (es > 0 && eh > 0) { heads = eh + 1; spt = es; }
        }
        Heads = heads; Sectors = spt;
        Cylinders = (int)Math.Min(1024, Math.Max(1, data.Length / 512 / (heads * spt)));
    }

    /// <summary>A hard disk on a big (chunked) base: the same copy-on-write disk, for sizes past one array.</summary>
    public DiskImage(DiskBigBase big)
    {
        Base = Array.Empty<byte>(); Big = big; IsFloppy = false;
        DiskBigBase.Register(big);
        var mbr = big.Sector(0);
        int heads = 16, spt = 63;
        if (mbr[510] == 0x55 && mbr[511] == 0xaa && mbr[0x1c2] != 0)
        {
            int eh = mbr[0x1c3], es = mbr[0x1c4] & 0x3f;
            if (es > 0 && eh > 0) { heads = eh + 1; spt = es; }
        }
        Heads = heads; Sectors = spt;
        Cylinders = (int)Math.Min(1024, Math.Max(1, big.Length / 512 / (heads * spt)));
    }

    /// <summary>A disk on <paramref name="baseImage"/> whose content is <paramref name="full"/> (same length): the overlay is their difference.</summary>
    public static DiskImage FromFull(byte[]? baseImage, byte[] full, bool floppy)
    {
        if (baseImage == null || ReferenceEquals(baseImage, full) || baseImage.Length != full.Length) return new DiskImage(full, floppy);
        var d = new DiskImage(baseImage, floppy);
        d.SetContent(full);
        d.Dirty = false;
        return d;
    }

    /// <summary>Called on every read (nano98's warm-boot check: which D: sectors Windows read before the game drive went in).</summary>
    public Action<long, int>? ReadHook;

    public bool Read(long lba, int count, Span<byte> dst)
    {
        if (lba < 0 || count < 0 || lba + count > TotalSectors) return false;
        ReadHook?.Invoke(lba, count);
        if (_overlay.Count == 0 && Big == null) { Base.AsSpan(checked((int)(lba * SectorSize)), count * SectorSize).CopyTo(dst); return true; }
        for (int i = 0; i < count; i++)
        {
            long s = lba + i;
            var to = dst.Slice(i * SectorSize, SectorSize);
            if (_overlay.TryGetValue(s, out var sec)) sec.CopyTo(to);
            else BaseSector(s).CopyTo(to);
        }
        return true;
    }

    /// <summary>The whole disk as the machine sees it into a raw image file, without one big array (any size; zero areas of a big base are not written).</summary>
    public void WriteFile(string path)
    {
        using var f = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        f.SetLength(Length);
        if (Big != null)
            for (int i = 0; i < Big.ChunkCount; i++) { if (Big.ChunkAt(i) is { } c) { f.Position = (long)i * DiskBigBase.ChunkBytes; f.Write(c); } }
        else f.Write(Base);
        foreach (var (s, sec) in _overlay.OrderBy(k => k.Key)) { f.Position = s * SectorSize; f.Write(sec); }
    }

    /// <summary>One sector of the base (what the disk held before any write).</summary>
    public void ReadBase(long s, Span<byte> dst) => BaseSector(s).CopyTo(dst);

    public bool Write(long lba, int count, ReadOnlySpan<byte> src)
    {
        if (lba < 0 || count < 0 || lba + count > TotalSectors) return false;
        for (int i = 0; i < count; i++) PutSector(lba + i, src.Slice(i * SectorSize, SectorSize));
        Dirty = true;
        return true;
    }

    private void PutSector(long s, ReadOnlySpan<byte> data)
    {
        if (data.SequenceEqual(BaseSector(s))) { _overlay.Remove(s); return; }
        if (!_overlay.TryGetValue(s, out var sec)) _overlay[s] = sec = new byte[SectorSize];
        data.CopyTo(sec);
    }

    /// <summary>The whole disk as the machine sees it (a new array).</summary>
    /// <summary>A key for the disk's content relative to its base: the base length + a hash of the changed sectors (nano98 resume snapshots).</summary>
    public string ContentKey()
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(BitConverter.GetBytes(Length));
        foreach (var s in _overlay.Keys.OrderBy(k => k)) { h.AppendData(BitConverter.GetBytes(s)); h.AppendData(_overlay[s]); }
        return Convert.ToHexString(h.GetHashAndReset());
    }

    /// <summary>The changed sectors as (sector, 512 bytes) records, sorted (nano98's saves layer).</summary>
    public void WriteOverlay(BinaryWriter w)
    {
        w.Write(_overlay.Count);
        foreach (var s in _overlay.Keys.OrderBy(k => k)) { w.Write(s); w.Write(_overlay[s]); }
    }
    /// <summary>Replaces the changed sectors with the ones <see cref="WriteOverlay"/> wrote.</summary>
    public void ReadOverlay(BinaryReader r)
    {
        _overlay.Clear();
        for (int n = r.ReadInt32(); n > 0; n--) { long s = r.ReadInt64(); var d = r.ReadBytes(SectorSize); if (s >= 0 && s < TotalSectors) _overlay[s] = d; }
        Dirty = false;
    }

    public byte[] ToArray()
    {
        if (Big != null) throw new NotSupportedException("a disk larger than 2 GB has no single-array form: read it by sectors");
        var a = (byte[])Base.Clone();
        foreach (var (s, sec) in _overlay) sec.CopyTo(a, s * SectorSize);
        return a;
    }

    /// <summary>The disk as it is now, built later (on another thread): only the written sectors are copied here (cheap), the whole image is built when the function runs.</summary>
    public Func<byte[]> Snapshot()
    {
        if (Big != null) throw new NotSupportedException("a disk larger than 2 GB has no single-array form: read it by sectors");
        var secs = new List<(long s, byte[] d)>(_overlay.Count);
        foreach (var (s, sec) in _overlay) secs.Add((s, (byte[])sec.Clone()));
        var b = Base;
        return () => { var a = (byte[])b.Clone(); foreach (var (s, sec) in secs) sec.CopyTo(a, s * SectorSize); return a; };
    }

    /// <summary>Makes the disk read as <paramref name="full"/> (same length as the base): the overlay becomes their difference.</summary>
    public void SetContent(byte[] full)
    {
        if (Big != null) throw new NotSupportedException("a disk larger than 2 GB cannot be set from one array");
        if (full.Length != Base.Length) throw new ArgumentException($"a disk content of {full.Length} bytes on a {Base.Length}-byte base");
        _overlay.Clear();
        for (long s = 0; s < TotalSectors; s++)
        {
            var f = full.AsSpan((int)(s * SectorSize), SectorSize);
            if (!f.SequenceEqual(Base.AsSpan((int)(s * SectorSize), SectorSize))) _overlay[s] = f.ToArray();
        }
        Dirty = true;
    }

    // ================= base identity =================

    private static readonly object _lock = new();
    private static readonly List<WeakReference<byte[]>> _known = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], Task<string>> _hashes = new();

    /// <summary>Finds a base image by hash when no disk in the machine has it (the desktop / tools may point it at a folder of images).</summary>
    public static Func<string, long, byte[]?>? BaseResolver;

    /// <summary>The base's identity: "p256:HEX" = SHA-256 over the SHA-256s of its 16 MB pieces, hashed in parallel (a 1 GB image: ~0.5 s on 12
    /// threads; the plain SHA-256 of it took ~4.5 s on this i7-8700 without SHA instructions, and a state load waited for both disks, ~10 s).
    /// Hashed once per base array, when a state first needs it (the old whole-image hash from the moment the disk is made, as before). States saved before carry "sha256:HEX"
    /// (the whole image): <see cref="FindBase"/> still matches those, computing that hash only when one is asked for.</summary>
    public string BaseHash => Big?.Hash ?? HashOf(Base);

    private const int HashPiece = 16 << 20;
    private static Task<string> HashTask(byte[] image)
    {
        lock (_lock) return _hashes.GetValue(image, a => Task.Run(() => { string h = PieceHash(a); WriteSidecar(a, h); return h; }));
    }

    // ---- cached base identity (nano98, 2026-11-21): IMAGE.p256 next to a disk file, so a cold state load skips hashing GBs ----
    // The sidecar holds the file's size, last-write time (UTC ticks), a sampled check (SHA-256 over 64 spread 4 KB pieces + the first and last
    // 64 KB of the bytes actually read) and the p256. It is trusted only when all three match the image just read; any mismatch -> the normal
    // full p256 (when a state first needs it), which then rewrites the sidecar. Unwritable folders just mean no cache.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], string> _sidecarPath = new();
    private const string SidecarMagic = "brokennes-p256-cache v1";

    /// <summary>Reads a disk image file; a valid IMAGE.p256 sidecar seeds its base hash (no hashing at state load), else the sidecar is written once the hash is made.</summary>
    public static byte[] ReadImageFile(string path)
    {
        if (IsSparse(path)) return ReadSparse(path);
        var fi = new FileInfo(path);
        long size = fi.Length, ticks = fi.LastWriteTimeUtc.Ticks;
        byte[] image = File.ReadAllBytes(path);
        if (image.Length < 4 << 20 || Environment.GetEnvironmentVariable("X86_NOHASHCACHE") == "1") return image;
        string side = path + ".p256";
        lock (_lock) _sidecarPath.AddOrUpdate(image, side);
        try
        {
            if (File.Exists(side))
            {
                var l = File.ReadAllLines(side);
                if (l.Length >= 5 && l[0] == SidecarMagic && l[1] == size.ToString() && l[2] == ticks.ToString() && image.LongLength == size
                    && l[3] == SampleHash(image) && l[4].StartsWith("p256:", StringComparison.Ordinal)
                    && new FileInfo(path) is var fi2 && fi2.Length == size && fi2.LastWriteTimeUtc.Ticks == ticks)
                {
                    lock (_lock) { _hashes.AddOrUpdate(image, Task.FromResult(l[4])); _sidecarPath.Remove(image); }
                    if (Environment.GetEnvironmentVariable("X86_HASHCACHE_LOG") == "1") Console.WriteLine($"diskimage: base hash from {side}");
                }
            }
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        return image;
    }

    // ---- N98D sparse container (tools/nano98/pkg/sparse.py): only the non-zero sectors, deflated; the guest sees the whole disk ----
    /// <summary>True when the file is an N98D container (magic "N98D").</summary>
    public static bool IsSparse(string path)
    {
        try { using var f = File.OpenRead(path); Span<byte> m = stackalloc byte[4]; return f.Read(m) == 4 && m.SequenceEqual("N98D"u8); }
        catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>The disk an N98D holds, expanded (zero-filled between its chunks). Its identity hash comes from the header (written by sparse.py
    /// from the raw image) unless X86_N98D_VERIFY=1, which re-hashes and refuses a mismatch.</summary>
    public static byte[] ReadSparse(string path) { using var f = File.OpenRead(path); return ReadSparse(f, path); }
    public static byte[] ReadSparse(Stream s, string name)
    {
        using var r = new BinaryReader(s, System.Text.Encoding.ASCII, true);
        if (r.ReadUInt32() != 0x4438394E) throw new InvalidDataException($"{name}: not an N98D disk");
        if (r.ReadInt32() != 1) throw new InvalidDataException($"{name}: N98D version this build does not know");
        long len = r.ReadInt64();
        if (len <= 0 || len % SectorSize != 0 || len > int.MaxValue) throw new InvalidDataException($"{name}: bad N98D length {len}");
        string hash = System.Text.Encoding.ASCII.GetString(r.ReadBytes(r.ReadByte()));
        var img = new byte[len];
        for (int n = r.ReadInt32(); n > 0; n--)
        {
            long first = r.ReadInt64(); int count = r.ReadInt32(), clen = r.ReadInt32();
            if (first < 0 || count <= 0 || (first + count) * SectorSize > len || clen < 0) throw new InvalidDataException($"{name}: N98D chunk outside its disk");
            using var z = new System.IO.Compression.ZLibStream(new MemoryStream(r.ReadBytes(clen)), System.IO.Compression.CompressionMode.Decompress);
            var dst = img.AsSpan(checked((int)(first * SectorSize)), count * SectorSize);
            while (dst.Length > 0) { int got = z.Read(dst); if (got <= 0) throw new InvalidDataException($"{name}: N98D chunk truncated"); dst = dst[got..]; }
        }
        if (Environment.GetEnvironmentVariable("X86_N98D_VERIFY") == "1" && PieceHash(img) != hash) throw new InvalidDataException($"{name}: N98D content does not match its hash {hash}");
        if (hash.StartsWith("p256:", StringComparison.Ordinal)) lock (_lock) _hashes.AddOrUpdate(img, Task.FromResult(hash));
        return img;
    }

    /// <summary>An N98D disk of any size into a chunked base (nano98 large game drives); zero runs stay unallocated.</summary>
    public static DiskBigBase ReadSparseBig(Stream s, string name)
    {
        using var r = new BinaryReader(s, System.Text.Encoding.ASCII, true);
        if (r.ReadUInt32() != 0x4438394E) throw new InvalidDataException($"{name}: not an N98D disk");
        if (r.ReadInt32() != 1) throw new InvalidDataException($"{name}: N98D version this build does not know");
        long len = r.ReadInt64();
        if (len <= 0 || len % SectorSize != 0) throw new InvalidDataException($"{name}: bad N98D length {len}");
        string hash = System.Text.Encoding.ASCII.GetString(r.ReadBytes(r.ReadByte()));
        var big = new DiskBigBase(len); var buf = new byte[1 << 20];
        for (int n = r.ReadInt32(); n > 0; n--)
        {
            long first = r.ReadInt64(); int count = r.ReadInt32(), clen = r.ReadInt32();
            if (first < 0 || count <= 0 || (first + count) * SectorSize > len || clen < 0) throw new InvalidDataException($"{name}: N98D chunk outside its disk");
            using var z = new System.IO.Compression.ZLibStream(new MemoryStream(r.ReadBytes(clen)), System.IO.Compression.CompressionMode.Decompress);
            long off = first * SectorSize, left = (long)count * SectorSize;
            while (left > 0)
            {
                int want = (int)Math.Min(buf.Length, left), got = 0;
                while (got < want) { int g = z.Read(buf, got, want - got); if (g <= 0) throw new InvalidDataException($"{name}: N98D chunk truncated"); got += g; }
                big.Fill(off, buf.AsSpan(0, want)); off += want; left -= want;
            }
        }
        if (hash.StartsWith("p256:", StringComparison.Ordinal)) big.SeedHash(hash);
        return big;
    }

    /// <summary>The disk size an N98D header names (without reading the disk).</summary>
    public static long SparseLength(Stream s)
    {
        var h = new byte[16]; int got = 0; while (got < 16) { int g = s.Read(h, got, 16 - got); if (g <= 0) break; got += g; }
        return got == 16 && BitConverter.ToUInt32(h, 0) == 0x4438394E ? BitConverter.ToInt64(h, 8) : -1;
    }

    private static string SampleHash(byte[] a)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int edge = (int)Math.Min(65536, a.LongLength);
        h.AppendData(a, 0, edge); h.AppendData(a, a.Length - edge, edge);
        for (int i = 0; i < 64; i++) { long off = (a.LongLength - 4096) * i / 63; h.AppendData(a, (int)off, 4096); }
        return Convert.ToHexString(h.GetHashAndReset());
    }

    private static void WriteSidecar(byte[] image, string hash)
    {
        string? side; lock (_lock) if (!_sidecarPath.TryGetValue(image, out side)) return;
        try
        {
            string path = side[..^5]; var fi = new FileInfo(path);
            if (X86Library.IsInLibrary(path)) return;   // (the asset library is read-only: its sidecars are written by its own copy tool)
            if (!fi.Exists || fi.Length != image.LongLength) return;
            string tmp = side + ".tmp" + Environment.ProcessId;
            File.WriteAllLines(tmp, new[] { SidecarMagic, fi.Length.ToString(), fi.LastWriteTimeUtc.Ticks.ToString(), SampleHash(image), hash });
            File.Move(tmp, side, true);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string PieceHash(byte[] a)
    {
        int n = (int)((a.LongLength + HashPiece - 1) / HashPiece);
        var parts = new byte[Math.Max(1, n) * 32];
        System.Threading.Tasks.Parallel.For(0, n, i =>
        {
            long off = (long)i * HashPiece; int len = (int)Math.Min(HashPiece, a.LongLength - off);
            SHA256.HashData(a.AsSpan((int)off, len), parts.AsSpan(i * 32, 32));
        });
        return "p256:" + Convert.ToHexString(SHA256.HashData(parts));
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], Task<string>> _legacy = new();
    private static Task<string> LegacyTask(byte[] image)
    {
        lock (_lock) return _legacy.GetValue(image, a => Task.Run(() => "sha256:" + Convert.ToHexString(SHA256.HashData(a))));
    }
    private static string LegacyHashOf(byte[] image) => LegacyTask(image).GetAwaiter().GetResult();

    public static string HashOf(byte[] image) => HashTask(image).GetAwaiter().GetResult();
    /// <summary>Gives an array its identity hash from a trusted cache (nano98's composed seat drives: base + package layer, hash cached beside the layer).</summary>
    public static void SeedHash(byte[] image, string hash) { lock (_lock) _hashes.AddOrUpdate(image, Task.FromResult(hash)); }
    /// <summary>Gives an array its whole-image "sha256:" identity (older states name disks by it) from a trusted catalogue (the asset library).</summary>
    public static void SeedLegacyHash(byte[] image, string hash) { lock (_lock) _legacy.AddOrUpdate(image, Task.FromResult(hash)); }

    /// <summary>The image's hash in the form <paramref name="like"/> is written in ("p256:" now, "sha256:" in older states).</summary>
    private static string HashLike(byte[] image, string like) => like.StartsWith("sha256:", StringComparison.Ordinal) ? LegacyHashOf(image) : HashOf(image);

    private static void Register(byte[] image)
    {
        lock (_lock)
        {
            _known.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var w in _known) if (w.TryGetTarget(out var a) && ReferenceEquals(a, image)) return;
            _known.Add(new WeakReference<byte[]>(image));
        }
        // the whole-image sha256 starts now, one thread per image, as it always did (a state saved before p256 names the disk by it, and
        // its load must be no slower than before); the parallel p256 is made when a state first needs it (~1 s)
        bool seeded; lock (_lock) seeded = _hashes.TryGetValue(image, out _);
        if (image.Length >= 4 << 20 && !seeded) LegacyTask(image);   // (an image whose p256 came from a trusted header / cache is never named by the old hash)
    }

    /// <summary>A base with this hash and length: the candidates first (the disks in the machine now), then every base still alive, then <see cref="BaseResolver"/>.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], object> _ids = new();
    private static long _nextId;
    /// <summary>"obj:N": this very array, for a state that never leaves the process (LoadState's undo copy).</summary>
    private static string ObjectKey(byte[] a) { lock (_lock) return "obj:" + ((long)_ids.GetValue(a, _ => Interlocked.Increment(ref _nextId))); }

    public static byte[]? FindBase(string hash, long length, IEnumerable<byte[]?> candidates)
    {
        if (hash.StartsWith("obj:", StringComparison.Ordinal))
        {
            foreach (var c in candidates) if (c != null && c.Length == length && ObjectKey(c) == hash) return c;
            lock (_lock) foreach (var w in _known) if (w.TryGetTarget(out var a) && a.Length == length && _ids.TryGetValue(a, out var id) && "obj:" + (long)id == hash) return a;
            return null;
        }
        foreach (var c in candidates) if (c != null && c.Length == length && HashLike(c, hash) == hash) return c;
        List<byte[]> alive = new();
        lock (_lock) foreach (var w in _known) if (w.TryGetTarget(out var a) && a.Length == length) alive.Add(a);
        foreach (var a in alive) if (HashLike(a, hash) == hash) return a;
        var r = BaseResolver?.Invoke(hash, length);
        return r != null && r.Length == length && HashLike(r, hash) == hash ? r : null;
    }

    // ================= the diff in a savestate =================

    /// <summary>Writes floppy flag, base length + hash, then the overlay as runs of consecutive sectors in LBA order (canonical: the same disk writes the same bytes).</summary>
    private long[] _keys = Array.Empty<long>();
    public void SaveDiff(BinaryWriter w, bool byObject = false)
    {
        w.Write(IsFloppy); w.Write(Length); w.Write(Big != null ? Big.Hash : byObject ? ObjectKey(Base) : BaseHash);
        // the sorted sector list in a buffer kept with the disk (a Win98 disk has ~80k changed sectors: a new 640 KB list per state was
        // large-object garbage a second under the bug capture, never collected between rare gen-2 GCs: the soak's creep)
        int count = _overlay.Count;
        if (_keys.Length < count) _keys = new long[count + count / 4 + 1024];
        _overlay.Keys.CopyTo(_keys, 0);
        var keys = _keys.AsSpan(0, count);
        keys.Sort();
        int runs = 0;
        for (int i = 0; i < count; i++) if (i == 0 || keys[i] != keys[i - 1] + 1) runs++;
        w.Write(runs);
        for (int i = 0; i < count;)
        {
            int j = i + 1;
            while (j < count && keys[j] == keys[j - 1] + 1) j++;
            w.Write(keys[i]); w.Write(j - i);
            for (int k = i; k < j; k++) w.Write(_overlay[keys[k]]);
            i = j;
        }
    }

    /// <summary>Reads a <see cref="SaveDiff"/>; the base is looked up by hash (<see cref="FindBase"/>). A missing base is an error naming it, never a guess.</summary>
    public static DiskImage LoadDiff(BinaryReader r, IEnumerable<byte[]?> candidates, IEnumerable<DiskBigBase?>? bigCandidates = null)
    {
        bool floppy = r.ReadBoolean(); long len = r.ReadInt64(); string hash = r.ReadString();
        DiskImage d;
        if (len > Array.MaxLength || (!floppy && bigCandidates != null && bigCandidates.Any(c => c != null && c.Length == len)))
        {
            var bb = DiskBigBase.Find(hash, len, bigCandidates ?? Array.Empty<DiskBigBase?>())
                ?? throw new InvalidDataException($"this x86 state needs the hard disk image it was saved on ({len:N0} bytes, {hash}); open the machine with that image first");
            d = new DiskImage(bb);
        }
        else
        {
            var b = FindBase(hash, len, candidates)
                ?? throw new InvalidDataException($"this x86 state needs the {(floppy ? "floppy" : "hard disk")} image it was saved on ({len:N0} bytes, {hash}); open the machine with that image first");
            d = new DiskImage(b, floppy);
        }
        int runs = r.ReadInt32();
        for (int i = 0; i < runs; i++)
        {
            long start = r.ReadInt64(); int n = r.ReadInt32();
            if (start < 0 || n < 0 || start + n > d.TotalSectors) throw new InvalidDataException("a disk diff run outside its disk");
            for (int k = 0; k < n; k++) d._overlay[start + k] = r.ReadBytes(SectorSize);
        }
        return d;
    }
}
