// A small FAT12 / FAT16 reader and writer for the x86 console's disk images (8.3 names): to put a program and its start-up files on a
// boot disk, and to read back what a program wrote. Written for BrokenNes.

using System;
using System.Collections.Generic;
using System.Text;

namespace NesEmulator.X86;

public sealed class FatImage
{
    private readonly byte[] _d;
    private readonly int _p;                 // byte offset of the volume (the first partition, or 0 for a floppy)
    private readonly int _bps, _spc, _res, _nf, _nroot, _fsz;
    private readonly long _totalSec;
    private readonly int _fat, _root, _data;
    private readonly bool _fat12;
    private readonly int _clusters;

    public FatImage(byte[] image)
    {
        _d = image;
        _p = 0;
        if (!(image[0] == 0xeb || image[0] == 0xe9) || image[11] != 0 || image[12] != 2)
        {
            if (image[510] != 0x55 || image[511] != 0xaa) throw new InvalidOperationException("not a FAT volume or a partitioned disk");
            _p = (int)BitConverter.ToUInt32(image, 0x1c6) * 512;
        }
        _bps = BitConverter.ToUInt16(image, _p + 11);
        _spc = image[_p + 13];
        _res = BitConverter.ToUInt16(image, _p + 14);
        _nf = image[_p + 16];
        _nroot = BitConverter.ToUInt16(image, _p + 17);
        int ts16 = BitConverter.ToUInt16(image, _p + 19);
        _fsz = BitConverter.ToUInt16(image, _p + 22);
        _totalSec = ts16 != 0 ? ts16 : BitConverter.ToUInt32(image, _p + 32);
        if (_bps != 512 || _fsz == 0) throw new InvalidOperationException("unsupported FAT volume (FAT32 or odd sector size)");
        _fat = _p + _res * _bps;
        _root = _fat + _nf * _fsz * _bps;
        _data = _root + _nroot * 32;
        _clusters = (int)((_totalSec - (_data - _p) / _bps) / _spc);
        _fat12 = _clusters < 4085;
    }

    private int ClusterBytes => _spc * _bps;
    private int ClusterOff(int c) => _data + (c - 2) * ClusterBytes;
    private int Eoc => _fat12 ? 0xff8 : 0xfff8;

    private int GetFat(int c)
    {
        if (_fat12)
        {
            int o = _fat + c * 3 / 2;
            int v = _d[o] | (_d[o + 1] << 8);
            return (c & 1) != 0 ? v >> 4 : v & 0xfff;
        }
        return BitConverter.ToUInt16(_d, _fat + c * 2);
    }

    private void SetFat(int c, int v)
    {
        for (int f = 0; f < _nf; f++)
        {
            int baseOff = _fat + f * _fsz * _bps;
            if (_fat12)
            {
                int o = baseOff + c * 3 / 2;
                if ((c & 1) != 0) { _d[o] = (byte)((_d[o] & 0x0f) | ((v << 4) & 0xf0)); _d[o + 1] = (byte)(v >> 4); }
                else { _d[o] = (byte)v; _d[o + 1] = (byte)((_d[o + 1] & 0xf0) | ((v >> 8) & 0x0f)); }
            }
            else { _d[baseOff + c * 2] = (byte)v; _d[baseOff + c * 2 + 1] = (byte)(v >> 8); }
        }
    }

    private List<int> Chain(int c)
    {
        var l = new List<int>();
        while (c >= 2 && c < Eoc && l.Count < _clusters + 2) { l.Add(c); c = GetFat(c); }
        return l;
    }

    private static string Name83(ReadOnlySpan<byte> e)
    {
        string n = Encoding.Latin1.GetString(e[..8]).TrimEnd(), x = Encoding.Latin1.GetString(e.Slice(8, 3)).TrimEnd();
        return x.Length > 0 ? n + "." + x : n;
    }

    private static byte[] To83(string name)
    {
        var b = Encoding.Latin1.GetBytes("           ");
        name = name.ToUpperInvariant();
        int dot = name.LastIndexOf('.');
        string n = dot >= 0 ? name[..dot] : name, x = dot >= 0 ? name[(dot + 1)..] : "";
        if (n.Length == 0 || n.Length > 8 || x.Length > 3) throw new ArgumentException($"not an 8.3 name: {name}");
        Encoding.Latin1.GetBytes(n).CopyTo(b, 0); Encoding.Latin1.GetBytes(x).CopyTo(b, 8);
        return b;
    }

    /// <summary>The directory entry slots of a directory (root when cluster 0): byte offsets into the image.</summary>
    private IEnumerable<int> Slots(int dirCluster)
    {
        if (dirCluster == 0) { for (int i = 0; i < _nroot; i++) yield return _root + i * 32; yield break; }
        foreach (int c in Chain(dirCluster))
            for (int i = 0; i < ClusterBytes / 32; i++) yield return ClusterOff(c) + i * 32;
    }

    private int Find(int dirCluster, string name)
    {
        foreach (int o in Slots(dirCluster))
        {
            if (_d[o] == 0) break;
            if (_d[o] == 0xe5 || _d[o + 11] == 0x0f || (_d[o + 11] & 8) != 0) continue;
            if (Name83(_d.AsSpan(o, 11)).Equals(name, StringComparison.OrdinalIgnoreCase)) return o;
        }
        return -1;
    }

    private (int dir, string leaf) Walk(string path)
    {
        var parts = path.Replace('/', '\\').Trim('\\').Split('\\');
        int dir = 0;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            int e = Find(dir, parts[i]);
            if (e < 0 || (_d[e + 11] & 0x10) == 0) throw new System.IO.DirectoryNotFoundException(path);
            dir = BitConverter.ToUInt16(_d, e + 26);
        }
        return (dir, parts[^1]);
    }

    public bool Exists(string path) { try { var (d, l) = Walk(path); return Find(d, l) >= 0; } catch (System.IO.DirectoryNotFoundException) { return false; } }

    public byte[]? ReadFile(string path)
    {
        var (dir, leaf) = Walk(path);
        int e = Find(dir, leaf);
        if (e < 0) return null;
        int size = (int)BitConverter.ToUInt32(_d, e + 28);
        var o = new byte[size];
        int pos = 0;
        foreach (int c in Chain(BitConverter.ToUInt16(_d, e + 26)))
        {
            int n = Math.Min(ClusterBytes, size - pos);
            if (n <= 0) break;
            Array.Copy(_d, ClusterOff(c), o, pos, n);
            pos += n;
        }
        return o;
    }

    public void Delete(string path)
    {
        var (dir, leaf) = Walk(path);
        int e = Find(dir, leaf);
        if (e < 0) return;
        foreach (int c in Chain(BitConverter.ToUInt16(_d, e + 26))) SetFat(c, 0);
        _d[e] = 0xe5;
    }

    /// <summary>Creates or replaces a file (the directory must exist). Timestamp: 1994-06-01 12:00 (deterministic images).</summary>
    public void WriteFile(string path, byte[] content)
    {
        Delete(path);
        var (dir, leaf) = Walk(path);
        int slot = -1;
        foreach (int o in Slots(dir)) if (_d[o] == 0 || _d[o] == 0xe5) { slot = o; break; }
        if (slot < 0) throw new InvalidOperationException("directory full");
        int need = (content.Length + ClusterBytes - 1) / ClusterBytes;
        var got = new List<int>();
        for (int c = 2; c < _clusters + 2 && got.Count < need; c++) if (GetFat(c) == 0) got.Add(c);
        if (got.Count < need) throw new InvalidOperationException("disk full");
        for (int i = 0; i < got.Count; i++)
        {
            SetFat(got[i], i + 1 < got.Count ? got[i + 1] : 0xffff & (_fat12 ? 0xfff : 0xffff));
            int n = Math.Min(ClusterBytes, content.Length - i * ClusterBytes);
            Array.Clear(_d, ClusterOff(got[i]), ClusterBytes);
            Array.Copy(content, i * ClusterBytes, _d, ClusterOff(got[i]), n);
        }
        Array.Clear(_d, slot, 32);
        To83(leaf).CopyTo(_d, slot);
        _d[slot + 11] = 0x20;
        ushort time = 12 << 11, date = (14 << 9) | (6 << 5) | 1;
        BitConverter.GetBytes(time).CopyTo(_d, slot + 22);
        BitConverter.GetBytes(date).CopyTo(_d, slot + 24);
        BitConverter.GetBytes((ushort)(got.Count > 0 ? got[0] : 0)).CopyTo(_d, slot + 26);
        BitConverter.GetBytes((uint)content.Length).CopyTo(_d, slot + 28);
    }

    /// <summary>Names in a directory ("" = root).</summary>
    public List<string> List(string dirPath)
    {
        int dir = 0;
        if (dirPath.Trim('\\', '/').Length > 0)
        {
            var (d, leaf) = Walk(dirPath);
            int e = Find(d, leaf);
            if (e < 0) throw new System.IO.DirectoryNotFoundException(dirPath);
            dir = BitConverter.ToUInt16(_d, e + 26);
        }
        var l = new List<string>();
        foreach (int o in Slots(dir))
        {
            if (_d[o] == 0) break;
            if (_d[o] == 0xe5 || _d[o + 11] == 0x0f || (_d[o + 11] & 8) != 0) continue;
            l.Add(Name83(_d.AsSpan(o, 11)) + ((_d[o + 11] & 0x10) != 0 ? "\\" : ""));
        }
        return l;
    }
}
