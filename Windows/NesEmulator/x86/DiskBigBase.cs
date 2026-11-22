// A disk base larger than one .NET array (nano98 large game drives, user 2026-11-22: "large FAT32 game drive, e.g. 8 GB"). Written for BrokenNes.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace NesEmulator.X86;

/// <summary>
/// The read-only base of a <see cref="DiskImage"/> that may exceed 2 GB: 64 MB chunks, a null chunk is all zeros (a fresh FAT32 game drive
/// costs only the clusters in use). Named by the same p256 hash a byte[] base of the same bytes gets (sha256 over the sha256 of each 16 MB
/// piece), so a state's disk diff names it the usual way. Disks of 2 GB or less keep the byte[] base (no change for any existing disk).
/// </summary>
public sealed class DiskBigBase
{
    public const int ChunkBytes = 64 << 20;
    private const int Piece = 16 << 20;
    public readonly long Length;
    private readonly byte[]?[] _chunks;
    private string? _hash;
    private static readonly byte[] Zero = new byte[DiskImage.SectorSize];

    public DiskBigBase(long length)
    {
        if (length <= 0 || length % DiskImage.SectorSize != 0) throw new ArgumentException("a disk is a whole number of 512-byte sectors");
        Length = length;
        _chunks = new byte[(length + ChunkBytes - 1) / ChunkBytes][];
    }

    /// <summary>Base content set while building (before the disk is used): writes into the chunks, allocating them on the first non-zero byte.</summary>
    public void Fill(long offset, ReadOnlySpan<byte> data)
    {
        if (_hash != null) throw new InvalidOperationException("a disk base is read-only once named");
        while (data.Length > 0)
        {
            int ci = (int)(offset / ChunkBytes), co = (int)(offset % ChunkBytes), n = Math.Min(data.Length, ChunkBytes - co);
            var part = data[..n];
            if (_chunks[ci] != null || part.IndexOfAnyExcept((byte)0) >= 0)
            {
                var c = _chunks[ci] ??= new byte[(int)Math.Min(ChunkBytes, Length - (long)ci * ChunkBytes)];
                part.CopyTo(c.AsSpan(co));
            }
            offset += n; data = data[n..];
        }
    }

    /// <summary>One sector of the base (a view into its chunk, or zeros).</summary>
    public ReadOnlySpan<byte> Sector(long s)
    {
        long off = s * DiskImage.SectorSize;
        var c = _chunks[off / ChunkBytes];
        return c == null ? Zero : c.AsSpan((int)(off % ChunkBytes), DiskImage.SectorSize);
    }

    public int ChunkCount => _chunks.Length;
    /// <summary>Chunk i's bytes, or null when it is all zeros.</summary>
    public byte[]? ChunkAt(int i) => _chunks[i];

    /// <summary>Bytes in use (allocated chunks): what the base really costs in memory.</summary>
    public long Allocated { get { long n = 0; foreach (var c in _chunks) if (c != null) n += c.Length; return n; } }

    /// <summary>p256 of the whole content (the same value <see cref="DiskImage.HashOf"/> gives a byte[] of these bytes).</summary>
    public string Hash => _hash ??= ComputeHash();

    /// <summary>A trusted hash from the image's own header (N98D): the base is not hashed again (X86_N98D_VERIFY=1 checks it).</summary>
    public void SeedHash(string hash)
    {
        if (Environment.GetEnvironmentVariable("X86_N98D_VERIFY") == "1" && ComputeHash() != hash) throw new InvalidDataException($"N98D content does not match its hash {hash}");
        _hash = hash;
    }

    private string ComputeHash()
    {
        int n = (int)((Length + Piece - 1) / Piece);
        var parts = new byte[Math.Max(1, n) * 32];
        System.Threading.Tasks.Parallel.For(0, n, i =>
        {
            long off = (long)i * Piece; int len = (int)Math.Min(Piece, Length - off);
            var c = _chunks[off / ChunkBytes];
            if (c == null) SHA256.HashData(new byte[len], parts.AsSpan(i * 32, 32));
            else SHA256.HashData(c.AsSpan((int)(off % ChunkBytes), len), parts.AsSpan(i * 32, 32));
        });
        return "p256:" + Convert.ToHexString(SHA256.HashData(parts));
    }

    /// <summary>Reads a raw image file of any size (zero chunks stay unallocated).</summary>
    public static DiskBigBase FromFile(string path)
    {
        using var f = File.OpenRead(path);
        return FromStream(f, f.Length);
    }

    public static DiskBigBase FromStream(Stream s, long length)
    {
        var b = new DiskBigBase(length);
        var buf = new byte[4 << 20]; long off = 0;
        while (off < length)
        {
            int want = (int)Math.Min(buf.Length, length - off), got = 0;
            while (got < want) { int r = s.Read(buf, got, want - got); if (r <= 0) throw new EndOfStreamException(); got += r; }
            b.Fill(off, buf.AsSpan(0, want)); off += want;
        }
        return b;
    }

    // ---- identity: big bases alive in this process, found by hash when a state names one ----
    private static readonly object _lock = new();
    private static readonly List<WeakReference<DiskBigBase>> _known = new();
    internal static void Register(DiskBigBase b) { lock (_lock) { _known.RemoveAll(w => !w.TryGetTarget(out _)); _known.Add(new WeakReference<DiskBigBase>(b)); } }
    /// <summary>Finds a big base for a state's disk diff: the candidates first (the disks in the machine), then every big base alive, then the resolver.</summary>
    public static Func<string, long, DiskBigBase?>? Resolver;
    internal static DiskBigBase? Find(string hash, long length, IEnumerable<DiskBigBase?> candidates)
    {
        foreach (var c in candidates) if (c != null && c.Length == length && c.Hash == hash) return c;
        List<DiskBigBase> alive = new();
        lock (_lock) foreach (var w in _known) if (w.TryGetTarget(out var a) && a.Length == length) alive.Add(a);
        foreach (var a in alive) if (a.Hash == hash) return a;
        var r = Resolver?.Invoke(hash, length);
        return r != null && r.Length == length && r.Hash == hash ? r : null;
    }
}
