using System.IO.Compression;

namespace BrokenNes.Headless;

/// Dependency-free PNG and WAV writers so the harness runs anywhere .NET does.
public static class Media
{
    public static void WritePng(string path, byte[] rgba, int w = 256, int h = 240)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BE(ihdr, 0, (uint)w); BE(ihdr, 4, (uint)h);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        Chunk(fs, "IHDR", ihdr);
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (int y = 0; y < h; y++)
            {
                z.WriteByte(0); // filter: none
                // Force alpha opaque: some cores leave A=0 which renders as invisible.
                var row = new byte[w * 4];
                Buffer.BlockCopy(rgba, y * w * 4, row, 0, Math.Min(row.Length, Math.Max(0, rgba.Length - y * w * 4)));
                for (int i = 3; i < row.Length; i += 4) row[i] = 255;
                z.Write(row);
            }
        }
        Chunk(fs, "IDAT", raw.ToArray());
        Chunk(fs, "IEND", Array.Empty<byte>());
    }

    public static void WriteWav(string path, IReadOnlyList<float> samples, int rate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var bw = new BinaryWriter(File.Create(path));
        int dataLen = samples.Count * 2;
        bw.Write("RIFF"u8); bw.Write(36 + dataLen); bw.Write("WAVE"u8);
        bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)1);
        bw.Write(rate); bw.Write(rate * 2); bw.Write((short)2); bw.Write((short)16);
        bw.Write("data"u8); bw.Write(dataLen);
        foreach (var s in samples) bw.Write((short)Math.Clamp(s * 32767f, -32768f, 32767f));
    }

    static void BE(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, (uint)data.Length); s.Write(len);
        var td = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(td, 0);
        data.CopyTo(td, 4);
        s.Write(td);
        var crc = new byte[4]; BE(crc, 0, Crc32(td)); s.Write(crc);
    }

    static uint[]? _crcTable;
    static uint Crc32(byte[] d)
    {
        _crcTable ??= Enumerable.Range(0, 256).Select(n =>
        {
            uint c = (uint)n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();
        uint crc = 0xFFFFFFFFu;
        foreach (var b in d) crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
