using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace NesEmulator.Debugging.Timeflow;

/// <summary>How a timeline's blocks are compressed: .NET's own Brotli or Deflate (no package), chosen from T1's measurements.</summary>
public enum TimelineCompression : byte { None = 0, Brotli = 1, Deflate = 2 }

/// <summary>
/// The frame records of a block, before compression (milestone T1). A block is <c>keyEvery</c> frames and starts with a keyframe (the whole
/// state), so a block decodes on its own. A delta frame holds the skeleton whole and, for each section, only the pages that changed since the
/// frame before, as the XOR of the new page with the old one (unchanged bytes are zeros, which the compressor all but removes). A frame whose
/// layout differs from the one before (a section grew, appeared or went) is written as a keyframe.
/// <code>
///   frame record:  kind (0 key, 1 delta)  varint skeleton length, skeleton
///   key:           varint sections; per section: name (varint length, UTF-8), varint length, bytes
///   delta:         varint sections; per section: varint changed pages; per page: varint page index (delta from the last), XOR bytes (page size, the last page shorter)
/// </code>
/// </summary>
public static class TimelineCodec
{
    public const byte KindKey = 0, KindDelta = 1;

    public static void WriteVar(Stream s, ulong v)
    {
        while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
        s.WriteByte((byte)v);
    }

    public static ulong ReadVar(ReadOnlySpan<byte> b, ref int at)
    {
        ulong v = 0; int shift = 0;
        while (true)
        {
            if (at >= b.Length) throw new InvalidDataException("a timeline record is cut short");
            byte x = b[at++];
            v |= (ulong)(x & 0x7F) << shift;
            if ((x & 0x80) == 0) return v;
            shift += 7;
            if (shift > 63) throw new InvalidDataException("a timeline record holds a broken number");
        }
    }

    public static void WriteZig(Stream s, long v) => WriteVar(s, (ulong)((v << 1) ^ (v >> 63)));
    public static long ReadZig(ReadOnlySpan<byte> b, ref int at) { ulong u = ReadVar(b, ref at); return (long)(u >> 1) ^ -(long)(u & 1); }

    public static void WriteString(Stream s, string t) { var b = Encoding.UTF8.GetBytes(t); WriteVar(s, (ulong)b.Length); s.Write(b); }
    public static string ReadString(ReadOnlySpan<byte> b, ref int at) { int n = (int)ReadVar(b, ref at); Need(b, at, n); var t = Encoding.UTF8.GetString(b.Slice(at, n)); at += n; return t; }

    private static void Need(ReadOnlySpan<byte> b, int at, long n) { if (n < 0 || at + n > b.Length) throw new InvalidDataException("a timeline record is cut short"); }

    /// <summary>A keyframe record of <paramref name="st"/>.</summary>
    public static void WriteKey(Stream s, TimeflowState st)
    {
        s.WriteByte(KindKey);
        WriteVar(s, (ulong)st.Skeleton.Length); s.Write(st.Skeleton);
        WriteVar(s, (ulong)st.Sections.Length);
        for (int i = 0; i < st.Sections.Length; i++)
        {
            WriteString(s, st.Names[i]);
            WriteVar(s, (ulong)st.Sections[i].Length); s.Write(st.Sections[i]);
        }
    }

    /// <summary>A delta record of <paramref name="st"/> against <paramref name="prev"/> (same layout: the caller checks). Returns the pages written.</summary>
    public static int WriteDelta(Stream s, TimeflowState st, TimeflowState prev, int pageSize, byte[] scratch)
    {
        s.WriteByte(KindDelta);
        WriteVar(s, (ulong)st.Skeleton.Length); s.Write(st.Skeleton);
        WriteVar(s, (ulong)st.Sections.Length);
        int pagesTotal = 0;
        var pages = new List<int>();
        for (int i = 0; i < st.Sections.Length; i++)
        {
            var cur = st.Sections[i].AsSpan(); var old = prev.Sections[i].AsSpan();
            pages.Clear();
            int n = (cur.Length + pageSize - 1) / pageSize;
            for (int p = 0; p < n; p++)
            {
                int o = p * pageSize, len = Math.Min(pageSize, cur.Length - o);
                if (!cur.Slice(o, len).SequenceEqual(old.Slice(o, len))) pages.Add(p);
            }
            WriteVar(s, (ulong)pages.Count);
            int last = 0;
            foreach (int p in pages)
            {
                WriteVar(s, (ulong)(p - last)); last = p;
                int o = p * pageSize, len = Math.Min(pageSize, cur.Length - o);
                for (int k = 0; k < len; k++) scratch[k] = (byte)(cur[o + k] ^ old[o + k]);
                s.Write(scratch, 0, len);
            }
            pagesTotal += pages.Count;
        }
        return pagesTotal;
    }

    /// <summary>
    /// Reads one record at <paramref name="at"/> into <paramref name="into"/> (a delta applies on top of what it holds: the frame before).
    /// Returns the record's kind.
    /// </summary>
    public static byte Read(ReadOnlySpan<byte> b, ref int at, TimeflowState into, int pageSize)
    {
        Need(b, at, 1);
        byte kind = b[at++];
        int sk = (int)ReadVar(b, ref at); Need(b, at, sk);
        into.Skeleton = b.Slice(at, sk).ToArray(); at += sk;
        int count = (int)ReadVar(b, ref at);
        if (kind == KindKey)
        {
            var secs = new byte[count][]; var names = new string[count];
            for (int i = 0; i < count; i++)
            {
                names[i] = ReadString(b, ref at);
                int n = (int)ReadVar(b, ref at); Need(b, at, n);
                secs[i] = b.Slice(at, n).ToArray(); at += n;
            }
            into.Sections = secs; into.Names = names;
            return kind;
        }
        if (kind != KindDelta) throw new InvalidDataException($"a timeline record of an unknown kind ({kind})");
        if (count != into.Sections.Length) throw new InvalidDataException($"a delta frame has {count} sections where the frame before had {into.Sections.Length}");
        for (int i = 0; i < count; i++)
        {
            var sec = into.Sections[i];
            int pages = (int)ReadVar(b, ref at), page = 0;
            for (int k = 0; k < pages; k++)
            {
                page += (int)ReadVar(b, ref at);
                int o = page * pageSize;
                if (o >= sec.Length) throw new InvalidDataException($"a delta frame names page {page} of a {sec.Length}-byte section");
                int len = Math.Min(pageSize, sec.Length - o);
                Need(b, at, len);
                var x = b.Slice(at, len);
                for (int j = 0; j < len; j++) sec[o + j] ^= x[j];
                at += len;
            }
        }
        return kind;
    }

    // ------------------------------------------------------------------------------------------------------ compression

    /// <summary>Compresses a block. <paramref name="level"/>: Brotli's quality (0-11), or Deflate's: 0 fastest, 1 optimal, 2 smallest.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> raw, TimelineCompression how, int level)
    {
        switch (how)
        {
            case TimelineCompression.None: return raw.ToArray();
            case TimelineCompression.Brotli:
            {
                byte[] buf = ArrayPool<byte>.Shared.Rent(BrotliEncoder.GetMaxCompressedLength(raw.Length));
                try
                {
                    if (!BrotliEncoder.TryCompress(raw, buf, out int written, Math.Clamp(level, 0, 11), 22)) throw new InvalidOperationException("Brotli could not compress a timeline block");
                    return buf.AsSpan(0, written).ToArray();
                }
                finally { ArrayPool<byte>.Shared.Return(buf); }
            }
            case TimelineCompression.Deflate:
            {
                using var ms = new MemoryStream(raw.Length / 4 + 64);
                var lvl = level <= 0 ? CompressionLevel.Fastest : level == 1 ? CompressionLevel.Optimal : CompressionLevel.SmallestSize;
                using (var d = new DeflateStream(ms, lvl, leaveOpen: true)) d.Write(raw);
                return ms.ToArray();
            }
            default: throw new ArgumentException($"unknown compression {how}");
        }
    }

    public static byte[] Decompress(ReadOnlySpan<byte> packed, int rawLength, TimelineCompression how)
    {
        var raw = new byte[rawLength];
        switch (how)
        {
            case TimelineCompression.None:
                if (packed.Length != rawLength) throw new InvalidDataException("a timeline block has the wrong length");
                packed.CopyTo(raw); return raw;
            case TimelineCompression.Brotli:
                if (!BrotliDecoder.TryDecompress(packed, raw, out int got) || got != rawLength) throw new InvalidDataException("a timeline block does not decompress to its recorded length (damaged)");
                return raw;
            case TimelineCompression.Deflate:
            {
                try
                {
                    using var z = new DeflateStream(new MemoryStream(packed.ToArray()), CompressionMode.Decompress);
                    int at = 0;
                    while (at < rawLength) { int k = z.Read(raw, at, rawLength - at); if (k <= 0) break; at += k; }
                    if (at != rawLength) throw new InvalidDataException("a timeline block does not decompress to its recorded length (damaged)");
                }
                catch (InvalidDataException) { throw; }
                catch (Exception e) { throw new InvalidDataException("a timeline block does not decompress (damaged): " + e.Message); }
                return raw;
            }
            default: throw new InvalidDataException($"unknown compression {how}");
        }
    }

    /// <summary>CRC-32 (the zip polynomial) of a block as written: a damaged block is found before it is decoded.</summary>
    public static uint Crc32(ReadOnlySpan<byte> data) => NesEmulator.Systems.ConsoleStateFile.Crc32Public(data);
}
