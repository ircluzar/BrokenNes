using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BrokenNes.Workshop.Sega;

/// <summary>One register-level event of a VGM file, at a time in 44.1 kHz samples.</summary>
internal readonly record struct VgmEvent(long Sample, VgmKind Kind, int A, int B);

internal enum VgmKind
{
    /// <summary>A YM2612 register write: A = register (0-255), B = value; the part is in <see cref="VgmEvent.Part"/>.</summary>
    Ym0, Ym1,
    /// <summary>An SN76489 write: A = the byte.</summary>
    Psg,
}

internal static class VgmEventExtensions
{
    public static int Part(this VgmEvent e) => e.Kind == VgmKind.Ym1 ? 1 : 0;
}

/// <summary>
/// A VGM file read down to what the Genesis sound unit takes: YM2612 register writes (both parts, and the PCM-bank DAC commands <c>0x80-0x8F</c> turned into writes to register $2A), SN76489 writes, the waits between
/// them, the loop point and the clocks. VGM is a public, documented format (vgmrips.net's specification); this reader follows the version 1.50 layout (data offset at $34) and skips commands for chips it does not drive.
/// </summary>
internal sealed class VgmFile
{
    public int Version;
    public int Sn76489Clock, Ym2612Clock, Ym2413Clock;
    public long TotalSamples, LoopSamples;
    public int LoopEvent = -1;                       // index of the first event of the loop, or -1
    public long LoopStartSample;
    public readonly List<VgmEvent> Events = new();
    public long EndSample;
    public string Gd3Title = "";
    public int Ym2413Writes, UnsupportedCommands;

    public static VgmFile Load(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length > 2 && data[0] == 0x1F && data[1] == 0x8B)
        {
            using var gz = new System.IO.Compression.GZipStream(new MemoryStream(data), System.IO.Compression.CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);
            data = ms.ToArray();
        }
        return Parse(data);
    }

    private static int U32(byte[] d, int at) => at + 4 <= d.Length ? d[at] | d[at + 1] << 8 | d[at + 2] << 16 | d[at + 3] << 24 : 0;

    public static VgmFile Parse(byte[] d)
    {
        if (d.Length < 0x40 || d[0] != 'V' || d[1] != 'g' || d[2] != 'm' || d[3] != ' ') throw new InvalidDataException("not a VGM file (no 'Vgm ' signature)");
        var v = new VgmFile { Version = U32(d, 8) };
        v.Sn76489Clock = U32(d, 0x0C) & 0x3FFFFFFF;
        v.Ym2413Clock = U32(d, 0x10) & 0x3FFFFFFF;
        v.TotalSamples = U32(d, 0x18);
        int loopOffset = U32(d, 0x1C);
        v.LoopSamples = U32(d, 0x20);
        if (v.Version >= 0x110) v.Ym2612Clock = U32(d, 0x2C) & 0x3FFFFFFF;
        int dataStart = 0x40;
        if (v.Version >= 0x150 && U32(d, 0x34) != 0) dataStart = 0x34 + U32(d, 0x34);
        int gd3 = U32(d, 0x14);
        if (gd3 != 0 && 0x14 + gd3 + 12 < d.Length)
        {
            int at = 0x14 + gd3 + 12;
            var sb = new StringBuilder();
            while (at + 1 < d.Length && (d[at] | d[at + 1] << 8) != 0) { sb.Append((char)(d[at] | d[at + 1] << 8)); at += 2; }
            v.Gd3Title = sb.ToString();
        }
        int loopAt = loopOffset != 0 ? 0x1C + loopOffset : -1;

        var bank = new List<byte>();
        int pcm = 0;
        long t = 0;
        int p = dataStart;
        bool ended = false;
        while (p < d.Length && !ended)
        {
            if (p == loopAt) { v.LoopEvent = v.Events.Count; v.LoopStartSample = t; }
            byte c = d[p++];
            switch (c)
            {
                case 0x50: v.Events.Add(new VgmEvent(t, VgmKind.Psg, d[p++], 0)); break;
                case 0x4F: p++; break;                                   // Game Gear stereo
                case 0x52: v.Events.Add(new VgmEvent(t, VgmKind.Ym0, d[p], d[p + 1])); p += 2; break;
                case 0x53: v.Events.Add(new VgmEvent(t, VgmKind.Ym1, d[p], d[p + 1])); p += 2; break;
                case 0x61: t += d[p] | d[p + 1] << 8; p += 2; break;
                case 0x62: t += 735; break;
                case 0x63: t += 882; break;
                case 0x66: ended = true; break;
                case 0x67:
                {
                    int type = d[p + 1], size = U32(d, p + 2);
                    if (type == 0x00) for (int i = 0; i < size; i++) bank.Add(d[p + 6 + i]);
                    p += 6 + size;
                    break;
                }
                case 0xE0: pcm = U32(d, p); p += 4; break;
                case 0x51: case 0x54: case 0x55: case 0x56: case 0x57: case 0x58: case 0x59: case 0x5A: case 0x5B: case 0x5C: case 0x5D: case 0x5E: case 0x5F:
                    if (c == 0x51) v.Ym2413Writes++; else v.UnsupportedCommands++;
                    p += 2; break;
                default:
                    if (c >= 0x70 && c <= 0x7F) t += (c & 0xF) + 1;
                    else if (c >= 0x80 && c <= 0x8F)
                    {
                        // YM2612 PCM bank write: the next byte of the bank goes to register $2A, then a wait of n samples
                        int value = pcm < bank.Count ? bank[pcm] : 0x80;
                        pcm++;
                        v.Events.Add(new VgmEvent(t, VgmKind.Ym0, 0x2A, value));
                        t += c & 0xF;
                    }
                    else if (c >= 0x30 && c <= 0x3F) p += 1;
                    else if (c >= 0x40 && c <= 0x4E) p += 2;
                    else if (c >= 0xA0 && c <= 0xBF) p += 2;
                    else if (c >= 0xC0 && c <= 0xDF) p += 3;
                    else if (c >= 0xE1) p += 4;
                    else { v.UnsupportedCommands++; }
                    break;
            }
        }
        v.EndSample = t;
        if (v.TotalSamples == 0) v.TotalSamples = t;
        return v;
    }
}

/// <summary>Writes a VGM file (version 1.61 layout, 256-byte header) from register writes with times in master clocks: YM2612 and SN76489 only, which is what the Genesis sound unit makes.</summary>
internal sealed class VgmWriter
{
    private readonly MemoryStream body = new();
    private readonly double masterHz;
    private long lastSample;
    private readonly int ymClock, psgClock;
    public long YmWrites, PsgWrites;

    public VgmWriter(double masterHz)
    {
        this.masterHz = masterHz;
        ymClock = (int)Math.Round(masterHz / 7);
        psgClock = (int)Math.Round(masterHz / 15);
    }

    private void Wait(long master)
    {
        long sample = (long)(master * 44100.0 / masterHz);
        long dt = sample - lastSample;
        lastSample = Math.Max(lastSample, sample);
        while (dt > 0)
        {
            int n = (int)Math.Min(dt, 65535);
            body.WriteByte(0x61); body.WriteByte((byte)n); body.WriteByte((byte)(n >> 8));
            dt -= n;
        }
    }

    /// <summary>A write to one of the YM2612's four ports; an address write remembers the register, the data write that follows emits the VGM command.</summary>
    private int addr0, addr1;
    public void YmPort(long master, int port, byte value)
    {
        switch (port & 3)
        {
            case 0: addr0 = value; break;
            case 2: addr1 = value; break;
            case 1: Wait(master); body.WriteByte(0x52); body.WriteByte((byte)addr0); body.WriteByte(value); YmWrites++; break;
            case 3: Wait(master); body.WriteByte(0x53); body.WriteByte((byte)addr1); body.WriteByte(value); YmWrites++; break;
        }
    }

    public void Psg(long master, byte value) { Wait(master); body.WriteByte(0x50); body.WriteByte(value); PsgWrites++; }

    public void Save(string path, long endMaster, string title)
    {
        Wait(endMaster);
        body.WriteByte(0x66);
        var gd3 = new MemoryStream();
        void W16(string s) { foreach (char ch in s) { gd3.WriteByte((byte)ch); gd3.WriteByte((byte)(ch >> 8)); } gd3.WriteByte(0); gd3.WriteByte(0); }
        W16(title); W16(title); W16("Genesis"); W16("Genesis"); W16("Mega Drive"); W16("Mega Drive"); W16("BrokenNes"); W16("2026/10"); W16("recorded by BrokenNes --md-run --vgm"); W16("");
        var h = new byte[0x100];
        void P32(int at, int v) { h[at] = (byte)v; h[at + 1] = (byte)(v >> 8); h[at + 2] = (byte)(v >> 16); h[at + 3] = (byte)(v >> 24); }
        Encoding.ASCII.GetBytes("Vgm ").CopyTo(h, 0);
        P32(8, 0x161);
        P32(0x0C, psgClock);
        P32(0x18, (int)lastSample);
        P32(0x24, 60);
        h[0x28] = 0x09; h[0x29] = 0x00; h[0x2A] = 16;
        P32(0x2C, ymClock);
        P32(0x34, 0x100 - 0x34);
        int total = 0x100 + (int)body.Length + 12 + (int)gd3.Length;
        P32(0x14, 0x100 + (int)body.Length - 0x14);
        P32(4, total - 4);
        using var fs = File.Create(path);
        fs.Write(h);
        body.Position = 0; body.CopyTo(fs);
        fs.Write(Encoding.ASCII.GetBytes("Gd3 "));
        fs.Write(BitConverter.GetBytes(0x100));
        fs.Write(BitConverter.GetBytes((int)gd3.Length));
        gd3.Position = 0; gd3.CopyTo(fs);
    }
}

/// <summary>A 16-bit stereo PCM WAV file.</summary>
internal static class WavFile
{
    public static void Write(string path, IReadOnlyList<short> interleaved, int sampleRate)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        int bytes = interleaved.Count * 2;
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(sampleRate); w.Write(sampleRate * 4); w.Write((short)4); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes);
        for (int i = 0; i < interleaved.Count; i++) w.Write(interleaved[i]);
    }

    public static short[] Read(string path, out int sampleRate)
    {
        var d = File.ReadAllBytes(path);
        sampleRate = BitConverter.ToInt32(d, 24);
        int channels = BitConverter.ToInt16(d, 22);
        int at = 12; int dataAt = -1, dataLen = 0;
        while (at + 8 <= d.Length)
        {
            string id = Encoding.ASCII.GetString(d, at, 4); int len = BitConverter.ToInt32(d, at + 4);
            if (id == "data") { dataAt = at + 8; dataLen = len; break; }
            at += 8 + len;
        }
        var s = new short[dataLen / 2];
        Buffer.BlockCopy(d, dataAt, s, 0, s.Length * 2);
        _ = channels;
        return s;
    }
}
