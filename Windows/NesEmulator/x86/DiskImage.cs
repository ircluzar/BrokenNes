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
    public readonly int Cylinders, Heads, Sectors;
    public readonly bool IsFloppy;
    /// <summary>Set by every write (the desktop's battery export clears it).</summary>
    public bool Dirty;
    private readonly Dictionary<long, byte[]> _overlay = new();
    public long Length => Base.Length;
    public long TotalSectors => Base.Length / SectorSize;
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

    /// <summary>A disk on <paramref name="baseImage"/> whose content is <paramref name="full"/> (same length): the overlay is their difference.</summary>
    public static DiskImage FromFull(byte[]? baseImage, byte[] full, bool floppy)
    {
        if (baseImage == null || ReferenceEquals(baseImage, full) || baseImage.Length != full.Length) return new DiskImage(full, floppy);
        var d = new DiskImage(baseImage, floppy);
        d.SetContent(full);
        d.Dirty = false;
        return d;
    }

    public bool Read(long lba, int count, Span<byte> dst)
    {
        if (lba < 0 || count < 0 || lba + count > TotalSectors) return false;
        if (_overlay.Count == 0) { Base.AsSpan(checked((int)(lba * SectorSize)), count * SectorSize).CopyTo(dst); return true; }
        for (int i = 0; i < count; i++)
        {
            long s = lba + i;
            var to = dst.Slice(i * SectorSize, SectorSize);
            if (_overlay.TryGetValue(s, out var sec)) sec.CopyTo(to);
            else Base.AsSpan((int)(s * SectorSize), SectorSize).CopyTo(to);
        }
        return true;
    }

    public bool Write(long lba, int count, ReadOnlySpan<byte> src)
    {
        if (lba < 0 || count < 0 || lba + count > TotalSectors) return false;
        for (int i = 0; i < count; i++) PutSector(lba + i, src.Slice(i * SectorSize, SectorSize));
        Dirty = true;
        return true;
    }

    private void PutSector(long s, ReadOnlySpan<byte> data)
    {
        if (data.SequenceEqual(Base.AsSpan((int)(s * SectorSize), SectorSize))) { _overlay.Remove(s); return; }
        if (!_overlay.TryGetValue(s, out var sec)) _overlay[s] = sec = new byte[SectorSize];
        data.CopyTo(sec);
    }

    /// <summary>The whole disk as the machine sees it (a new array).</summary>
    public byte[] ToArray()
    {
        var a = (byte[])Base.Clone();
        foreach (var (s, sec) in _overlay) sec.CopyTo(a, s * SectorSize);
        return a;
    }

    /// <summary>Makes the disk read as <paramref name="full"/> (same length as the base): the overlay becomes their difference.</summary>
    public void SetContent(byte[] full)
    {
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

    /// <summary>The base's identity: "sha256:HEX". Hashed once per base array (big ones in the background from the moment the disk is made).</summary>
    public string BaseHash => HashOf(Base);

    private static Task<string> HashTask(byte[] image)
    {
        lock (_lock) return _hashes.GetValue(image, a => Task.Run(() => "sha256:" + Convert.ToHexString(SHA256.HashData(a))));
    }

    public static string HashOf(byte[] image) => HashTask(image).GetAwaiter().GetResult();

    private static void Register(byte[] image)
    {
        lock (_lock)
        {
            _known.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var w in _known) if (w.TryGetTarget(out var a) && ReferenceEquals(a, image)) return;
            _known.Add(new WeakReference<byte[]>(image));
        }
        if (image.Length >= 4 << 20) HashTask(image);
    }

    /// <summary>A base with this hash and length: the candidates first (the disks in the machine now), then every base still alive, then <see cref="BaseResolver"/>.</summary>
    public static byte[]? FindBase(string hash, long length, IEnumerable<byte[]?> candidates)
    {
        foreach (var c in candidates) if (c != null && c.Length == length && HashOf(c) == hash) return c;
        List<byte[]> alive = new();
        lock (_lock) foreach (var w in _known) if (w.TryGetTarget(out var a) && a.Length == length) alive.Add(a);
        foreach (var a in alive) if (HashOf(a) == hash) return a;
        var r = BaseResolver?.Invoke(hash, length);
        return r != null && r.Length == length && HashOf(r) == hash ? r : null;
    }

    // ================= the diff in a savestate =================

    /// <summary>Writes floppy flag, base length + hash, then the overlay as runs of consecutive sectors in LBA order (canonical: the same disk writes the same bytes).</summary>
    public void SaveDiff(BinaryWriter w)
    {
        w.Write(IsFloppy); w.Write(Base.LongLength); w.Write(BaseHash);
        var keys = new List<long>(_overlay.Keys);
        keys.Sort();
        int runs = 0;
        for (int i = 0; i < keys.Count; i++) if (i == 0 || keys[i] != keys[i - 1] + 1) runs++;
        w.Write(runs);
        for (int i = 0; i < keys.Count;)
        {
            int j = i + 1;
            while (j < keys.Count && keys[j] == keys[j - 1] + 1) j++;
            w.Write(keys[i]); w.Write(j - i);
            for (int k = i; k < j; k++) w.Write(_overlay[keys[k]]);
            i = j;
        }
    }

    /// <summary>Reads a <see cref="SaveDiff"/>; the base is looked up by hash (<see cref="FindBase"/>). A missing base is an error naming it, never a guess.</summary>
    public static DiskImage LoadDiff(BinaryReader r, IEnumerable<byte[]?> candidates)
    {
        bool floppy = r.ReadBoolean(); long len = r.ReadInt64(); string hash = r.ReadString();
        var b = FindBase(hash, len, candidates)
            ?? throw new InvalidDataException($"this x86 state needs the {(floppy ? "floppy" : "hard disk")} image it was saved on ({len:N0} bytes, {hash}); open the machine with that image first");
        var d = new DiskImage(b, floppy);
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
