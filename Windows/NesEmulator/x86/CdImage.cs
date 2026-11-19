// The x86 PC's CD-ROM disc images (X86-02, StarCraft on Win98): an ISO (2048-byte sectors), a BIN/CUE (one or several files, data tracks in
// MODE1/2048, MODE1/2352, MODE2/2336, MODE2/2352, and AUDIO tracks), or a bare .bin (2352-byte raw sectors). Written for BrokenNes from the CUE
// sheet format (CDRWIN) and the CD sector layouts in ECMA-130; no code from another emulator. Big images are read from the file on demand
// (a 700 MB BIN is not loaded into memory); the disc is never written.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NesEmulator.X86;

/// <summary>A CD-ROM image. LBAs are disc LBAs (track 1 index 1 = LBA 0 = MSF 00:02:00).</summary>
public sealed class CdImage
{
    public sealed class Track
    {
        public int Number;
        public bool Audio;
        public int SectorSize;      // bytes per sector in the file (2048, 2336, 2352)
        public int DataOffset;      // where the 2048 user bytes start in a sector (0, 8, 16, 24)
        public long Start;          // disc LBA of index 01
        public long Pregap;         // sectors of index 00 before Start (in the file or not)
        public long Length;         // sectors from Start to the next track's index 00 (or the lead-out)
        internal Source? File;
        internal long FileOffset;   // byte offset in File of index 01
        internal long GapStart;
        internal long FileSector;   // index 01 in sectors from the file start     // disc LBA where the gap not stored in the file starts (PREGAP), or Start if none
        public byte Control => (byte)(Audio ? 0x10 : 0x14);   // ADR 1, control: data track (4) or 2-channel audio (0)
    }

    internal sealed class Source
    {
        public readonly string? Path; private byte[]? _mem; private FileStream? _fs; private readonly object _lock = new();
        public readonly long Length; public readonly bool Swap;
        public Source(byte[] data) { _mem = data; Length = data.Length; }
        public Source(string path, bool swap) { Path = path; Length = new FileInfo(path).Length; Swap = swap; }
        public void Read(long off, byte[] dst, int dOff, int n)
        {
            if (off < 0 || off + n > Length) { Array.Clear(dst, dOff, n); if (off >= Length || off + n <= 0) return; long a = Math.Max(0, off), b = Math.Min(Length, off + n); Read(a, dst, dOff + (int)(a - off), (int)(b - a)); return; }
            if (_mem != null) Array.Copy(_mem, off, dst, dOff, n);
            else lock (_lock)
            {
                _fs ??= new FileStream(Path!, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                _fs.Position = off; int got = 0; while (got < n) { int k = _fs.Read(dst, dOff + got, n - got); if (k <= 0) break; got += k; }
                if (got < n) Array.Clear(dst, dOff + got, n - got);
            }
            if (Swap) for (int i = 0; i + 1 < n; i += 2) (dst[dOff + i], dst[dOff + i + 1]) = (dst[dOff + i + 1], dst[dOff + i]);
        }
    }

    public readonly List<Track> Tracks = new();
    /// <summary>The lead-out LBA (= disc length in sectors).</summary>
    public long LeadOut { get; private set; }
    public string Name { get; private set; } = "";

    /// <summary>An ISO image in memory (the classic constructor).</summary>
    public CdImage(byte[] data)
    {
        if (data.Length % 2048 != 0) throw new InvalidDataException("an ISO image is a whole number of 2048-byte sectors");
        var src = new Source(data);
        Tracks.Add(new Track { Number = 1, SectorSize = 2048, DataOffset = 0, Start = 0, GapStart = 0, File = src, Length = data.Length / 2048 });
        LeadOut = data.Length / 2048;
    }

    private CdImage() { }

    /// <summary>Opens .iso, .cue or .bin (a bare .bin: raw 2352-byte MODE1 or MODE2 sectors, or audio).</summary>
    public static CdImage Open(string path)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".cue") return FromCue(path);
        var img = new CdImage { Name = System.IO.Path.GetFileName(path) };
        long len = new FileInfo(path).Length;
        var src = new Source(path, false);
        var t = new Track { Number = 1, File = src, Start = 0, GapStart = 0 };
        if (ext == ".iso" || len % 2352 != 0) { if (len % 2048 != 0) throw new InvalidDataException($"{path}: not a whole number of 2048- or 2352-byte sectors"); t.SectorSize = 2048; }
        else
        {
            var head = new byte[16]; src.Read(0, head, 0, 16);
            bool sync = head[0] == 0 && head[11] == 0 && Array.TrueForAll(head[1..11], b => b == 0xff);
            t.SectorSize = 2352;
            if (!sync) t.Audio = true; else t.DataOffset = head[15] == 2 ? 24 : 16;
        }
        t.Length = len / t.SectorSize;
        img.Tracks.Add(t); img.LeadOut = t.Length;
        return img;
    }

    private static long Msf(string s)
    {
        var p = s.Split(':');
        if (p.Length != 3) throw new InvalidDataException($"bad CUE time '{s}'");
        return (int.Parse(p[0], CultureInfo.InvariantCulture) * 60 + int.Parse(p[1], CultureInfo.InvariantCulture)) * 75 + int.Parse(p[2], CultureInfo.InvariantCulture);
    }

    private static List<string> Tokens(string line)
    {
        var o = new List<string>(); int i = 0;
        while (i < line.Length)
        {
            if (char.IsWhiteSpace(line[i])) { i++; continue; }
            if (line[i] == '"') { int j = line.IndexOf('"', i + 1); if (j < 0) j = line.Length; o.Add(line[(i + 1)..j]); i = j + 1; }
            else { int j = i; while (j < line.Length && !char.IsWhiteSpace(line[j])) j++; o.Add(line[i..j]); i = j; }
        }
        return o;
    }

    /// <summary>Parses a CUE sheet. Tracks of one FILE follow each other in that file; each new FILE starts where the previous file ended on the disc.</summary>
    public static CdImage FromCue(string cuePath)
    {
        var img = new CdImage { Name = System.IO.Path.GetFileName(cuePath) };
        string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(cuePath)) ?? ".";
        Source? file = null; long fileBase = 0;   // disc LBA of the file's first byte
        long shift = 0;                           // PREGAP sectors (not in any file) so far
        Track? cur = null; long pendingPregap = 0;
        var idx0 = new Dictionary<Track, long>();
        foreach (var raw in File.ReadAllLines(cuePath))
        {
            var tk = Tokens(raw);
            if (tk.Count == 0) continue;
            switch (tk[0].ToUpperInvariant())
            {
                case "FILE":
                {
                    if (file != null) fileBase += FileSectors(file, img.Tracks);
                    string fn = tk[1]; string type = tk.Count > 2 ? tk[2].ToUpperInvariant() : "BINARY";
                    string full = System.IO.Path.Combine(dir, fn);
                    if (!File.Exists(full)) { var alt = System.IO.Path.Combine(dir, System.IO.Path.GetFileName(fn)); if (File.Exists(alt)) full = alt; else throw new FileNotFoundException($"the CUE sheet names {fn}, not found next to it", full); }
                    if (type is "WAVE" or "MP3" or "AIFF") throw new InvalidDataException($"CUE FILE type {type} is not supported (BINARY / MOTOROLA only)");
                    file = new Source(full, type == "MOTOROLA");
                    break;
                }
                case "TRACK":
                {
                    if (file == null) throw new InvalidDataException("CUE TRACK before FILE");
                    string mode = tk[2].ToUpperInvariant();
                    cur = new Track { Number = int.Parse(tk[1], CultureInfo.InvariantCulture), File = file, Start = -1 };
                    switch (mode)
                    {
                        case "AUDIO": cur.Audio = true; cur.SectorSize = 2352; break;
                        case "MODE1/2048": cur.SectorSize = 2048; break;
                        case "MODE1/2352": cur.SectorSize = 2352; cur.DataOffset = 16; break;
                        case "MODE2/2336": cur.SectorSize = 2336; cur.DataOffset = 8; break;
                        case "MODE2/2352": cur.SectorSize = 2352; cur.DataOffset = 24; break;
                        default: throw new InvalidDataException($"CUE track mode {mode} is not supported");
                    }
                    img.Tracks.Add(cur); pendingPregap = 0;
                    break;
                }
                case "PREGAP": if (cur != null) pendingPregap = Msf(tk[1]); break;
                case "INDEX":
                {
                    if (cur == null) throw new InvalidDataException("CUE INDEX before TRACK");
                    int n = int.Parse(tk[1], CultureInfo.InvariantCulture);
                    long t = Msf(tk[2]);   // sectors from the start of the file
                    long fileSectorOff = t;
                    if (n == 0) { idx0[cur] = fileBase + shift + fileSectorOff; }
                    else if (n == 1)
                    {
                        shift += pendingPregap;
                        cur.GapStart = fileBase + shift + fileSectorOff - pendingPregap;
                        cur.Start = fileBase + shift + fileSectorOff;
                        cur.FileOffset = ByteOffset(img.Tracks, cur, file!, t); cur.FileSector = t;
                        cur.Pregap = pendingPregap + (idx0.TryGetValue(cur, out var i0) ? cur.Start - pendingPregap - i0 : 0);
                    }
                    break;
                }
            }
        }
        if (img.Tracks.Count == 0) throw new InvalidDataException("the CUE sheet has no tracks");
        foreach (var t in img.Tracks) if (t.Start < 0) throw new InvalidDataException($"track {t.Number} has no INDEX 01");
        long end = fileBase + shift + FileSectors(file!, img.Tracks);
        img.LeadOut = end;
        for (int i = 0; i < img.Tracks.Count; i++)
        {
            long next = i + 1 < img.Tracks.Count ? img.Tracks[i + 1].Start - img.Tracks[i + 1].Pregap : end;
            img.Tracks[i].Length = Math.Max(0, next - img.Tracks[i].Start);
        }
        return img;
    }

    // tracks inside one file: the byte offset of "t sectors into the file" continues from the previous track of the same file (its sector size)
    private static long ByteOffset(List<Track> tracks, Track cur, Source f, long t)
    {
        Track? prev = null;
        foreach (var tr in tracks) { if (tr == cur) break; if (tr.File == f) prev = tr; }
        return prev == null ? t * cur.SectorSize : prev.FileOffset + (t - prev.FileSector) * prev.SectorSize;
    }

    private static long FileSectors(Source f, List<Track> tracks)
    {
        Track? last = null; foreach (var t in tracks) if (t.File == f && t.Start >= 0) last = t;
        if (last == null) return 0;
        return last.FileSector + Math.Max(0, f.Length - last.FileOffset) / last.SectorSize;
    }

    public long Sectors => LeadOut;

    /// <summary>The track holding <paramref name="lba"/> (its index 00 gap counts as part of it), or null past the lead-out.</summary>
    public Track? TrackAt(long lba)
    {
        if (lba < 0 || lba >= LeadOut) return null;
        Track? t = Tracks[0];
        foreach (var tr in Tracks) if (lba >= tr.Start - tr.Pregap) t = tr;
        return t;
    }

    /// <summary>True if every sector of the range is in a data track.</summary>
    public bool IsData(long lba, int count)
    {
        if (lba < 0 || lba + count > LeadOut) return false;
        for (long l = lba; l < lba + count;) { var t = TrackAt(l)!; if (t.Audio) return false; l = Math.Max(l + 1, t.Start + t.Length); }
        return true;
    }

    private void SectorBytes(Track t, long lba, int inOff, byte[] dst, int dOff, int n)
    {
        if (lba < t.GapStart) { Array.Clear(dst, dOff, n); return; }   // a PREGAP that is not in the file: silence / zeros
        t.File!.Read(t.FileOffset + (lba - t.Start) * t.SectorSize + inOff, dst, dOff, n);
    }

    /// <summary>Reads <paramref name="count"/> 2048-byte user-data sectors (data tracks only; audio sectors read as zeros).</summary>
    public void ReadData(long lba, int count, byte[] dst, int dOff)
    {
        for (int i = 0; i < count; i++)
        {
            var t = TrackAt(lba + i);
            if (t == null || t.Audio) { Array.Clear(dst, dOff + i * 2048, 2048); continue; }
            SectorBytes(t, lba + i, t.DataOffset, dst, dOff + i * 2048, 2048);
        }
    }

    public byte[] ReadData(long lba, int count) { var b = new byte[count * 2048]; ReadData(lba, count, b, 0); return b; }

    /// <summary>One 2352-byte raw sector: audio as stored; a cooked 2048 data sector gets a MODE1 sync + header around it (no EDC/ECC).</summary>
    public void ReadRaw(long lba, byte[] dst, int dOff)
    {
        var t = TrackAt(lba);
        if (t == null) { Array.Clear(dst, dOff, 2352); return; }
        if (t.SectorSize == 2352) { SectorBytes(t, lba, 0, dst, dOff, 2352); return; }
        Array.Clear(dst, dOff, 2352);
        for (int i = 1; i < 11; i++) dst[dOff + i] = 0xff;
        long a = lba + 150; dst[dOff + 12] = Bcd(a / 4500); dst[dOff + 13] = Bcd(a / 75 % 60); dst[dOff + 14] = Bcd(a % 75);
        if (t.SectorSize == 2048) { dst[dOff + 15] = 1; SectorBytes(t, lba, 0, dst, dOff + 16, 2048); }
        else { dst[dOff + 15] = 2; SectorBytes(t, lba, 0, dst, dOff + 16, 2336); }
    }

    private static byte Bcd(long v) => (byte)(v / 10 << 4 | v % 10);

    public override string ToString()
    {
        var sb = new StringBuilder($"{Name}: {Tracks.Count} track(s), lead-out {LeadOut}");
        foreach (var t in Tracks) sb.Append($"\n  track {t.Number,2} {(t.Audio ? "AUDIO" : $"DATA/{t.SectorSize}")} start {t.Start} pregap {t.Pregap} length {t.Length}");
        return sb.ToString();
    }
}
