using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace NesEmulator.Debugging.Capture;

/// <summary>
/// The bug capture's own picture and sound files (D6): a PNG writer (8-bit RGB, zlib through the framework) and a 16-bit PCM WAV writer,
/// both plain managed code so the desktop app, Lite (WASM: no System.Drawing) and the Workshop make byte-identical files.
/// </summary>
public static class BugMedia
{
    /// <summary>A PNG of 0xAARRGGBB pixels (alpha dropped).</summary>
    public static byte[] Png(uint[] argb, int w, int h)
    {
        if (w <= 0 || h <= 0 || argb.Length < w * h) throw new ArgumentException("no picture");
        var raw = new byte[h * (1 + w * 3)];
        for (int y = 0, o = 0; y < h; y++)
        {
            raw[o++] = 0;   // filter: none
            for (int x = 0; x < w; x++)
            {
                uint c = argb[y * w + x];
                raw[o++] = (byte)(c >> 16); raw[o++] = (byte)(c >> 8); raw[o++] = (byte)c;
            }
        }
        byte[] z;
        using (var ms = new MemoryStream())
        {
            using (var zs = new ZLibStream(ms, CompressionLevel.Fastest, true)) zs.Write(raw, 0, raw.Length);
            z = ms.ToArray();
        }
        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BigEndian(ihdr, 0, (uint)w); BigEndian(ihdr, 4, (uint)h);
        ihdr[8] = 8; ihdr[9] = 2; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;   // 8 bits, RGB, deflate, no filter method, no interlace
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", z);
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    /// <summary>Reads back a PNG this class wrote (8-bit RGB, no interlace): the tests' round trip.</summary>
    public static (uint[] argb, int w, int h) ReadPng(byte[] png)
    {
        if (png.Length < 8 || png[0] != 0x89 || png[1] != 0x50) throw new InvalidDataException("not a PNG");
        int p = 8, w = 0, h = 0;
        using var idat = new MemoryStream();
        while (p + 8 <= png.Length)
        {
            int len = (int)ReadBig(png, p); string type = Encoding.ASCII.GetString(png, p + 4, 4);
            if (type == "IHDR") { w = (int)ReadBig(png, p + 8); h = (int)ReadBig(png, p + 12); if (png[p + 16] != 8 || png[p + 17] != 2) throw new InvalidDataException("only 8-bit RGB PNGs"); }
            else if (type == "IDAT") idat.Write(png, p + 8, len);
            uint crc = ReadBig(png, p + 8 + len);
            if (crc != Crc(png, p + 4, len + 4)) throw new InvalidDataException($"PNG chunk {type}: bad CRC");
            p += 12 + len;
            if (type == "IEND") break;
        }
        idat.Position = 0;
        using var zs = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zs.CopyTo(raw);
        var r = raw.ToArray();
        if (r.Length != h * (1 + w * 3)) throw new InvalidDataException("PNG data size");
        var px = new uint[w * h];
        for (int y = 0, o = 0; y < h; y++)
        {
            if (r[o++] != 0) throw new InvalidDataException("only unfiltered PNGs");
            for (int x = 0; x < w; x++, o += 3) px[y * w + x] = 0xFF000000u | ((uint)r[o] << 16) | ((uint)r[o + 1] << 8) | r[o + 2];
        }
        return (px, w, h);
    }

    /// <summary>A contact sheet: the pictures at half size (each at least 1 pixel), <paramref name="cols"/> to a row.</summary>
    public static (uint[] argb, int w, int h) Strip(System.Collections.Generic.IReadOnlyList<BugPicture> pics, int cols = 6)
    {
        if (pics.Count == 0) throw new ArgumentException("no pictures");
        int cw = 0, ch = 0;
        foreach (var p in pics) { cw = Math.Max(cw, Math.Max(1, p.Width / 2)); ch = Math.Max(ch, Math.Max(1, p.Height / 2)); }
        cols = Math.Min(cols, pics.Count);
        int rows = (pics.Count + cols - 1) / cols, gap = 2;
        int w = cols * cw + (cols - 1) * gap, h = rows * ch + (rows - 1) * gap;
        var o = new uint[w * h];
        Array.Fill(o, 0xFF202020u);
        for (int i = 0; i < pics.Count; i++)
        {
            var p = pics[i];
            int ox = (i % cols) * (cw + gap), oy = (i / cols) * (ch + gap);
            int pw = Math.Max(1, p.Width / 2), ph = Math.Max(1, p.Height / 2);
            for (int y = 0; y < ph; y++)
                for (int x = 0; x < pw; x++)
                    o[(oy + y) * w + ox + x] = p.Pixels[Math.Min(p.Height - 1, y * 2) * p.Width + Math.Min(p.Width - 1, x * 2)];
        }
        return (o, w, h);
    }

    /// <summary>A mono 16-bit PCM WAV of float samples (clipped to -1..1).</summary>
    public static byte[] Wav(float[] samples, int sampleRate)
    {
        int n = samples.Length;
        var b = new byte[44 + n * 2];
        void S(int at, string t) => Encoding.ASCII.GetBytes(t, 0, 4, b, at);
        void U32(int at, uint v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24); }
        void U16(int at, ushort v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); }
        S(0, "RIFF"); U32(4, (uint)(36 + n * 2)); S(8, "WAVE");
        S(12, "fmt "); U32(16, 16); U16(20, 1); U16(22, 1); U32(24, (uint)sampleRate); U32(28, (uint)(sampleRate * 2)); U16(32, 2); U16(34, 16);
        S(36, "data"); U32(40, (uint)(n * 2));
        for (int i = 0; i < n; i++)
        {
            float f = samples[i];
            if (float.IsNaN(f)) f = 0;
            int v = (int)Math.Round(Math.Clamp(f, -1f, 1f) * 32767f);
            U16(44 + i * 2, (ushort)(short)v);
        }
        return b;
    }

    private static void BigEndian(byte[] b, int at, uint v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }
    private static uint ReadBig(byte[] b, int at) => (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var head = new byte[8];
        BigEndian(head, 0, (uint)data.Length);
        Encoding.ASCII.GetBytes(type, 0, 4, head, 4);
        s.Write(head, 0, 8);
        s.Write(data, 0, data.Length);
        var crcIn = new byte[4 + data.Length];
        Array.Copy(head, 4, crcIn, 0, 4); Array.Copy(data, 0, crcIn, 4, data.Length);
        var c = new byte[4]; BigEndian(c, 0, Crc(crcIn, 0, crcIn.Length));
        s.Write(c, 0, 4);
    }

    private static uint[]? crcTable;
    private static uint Crc(byte[] b, int at, int len)
    {
        var t = crcTable;
        if (t == null)
        {
            t = new uint[256];
            for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[n] = c; }
            crcTable = t;
        }
        uint crc = 0xFFFFFFFFu;
        for (int i = at; i < at + len; i++) crc = t[(crc ^ b[i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
