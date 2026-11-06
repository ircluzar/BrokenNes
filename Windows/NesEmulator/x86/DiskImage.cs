// A raw disk image (floppy or hard disk) for the x86 console's BIOS (INT 13h). Written for BrokenNes.

using System;

namespace NesEmulator.X86;

/// <summary>A raw sector image held in memory, with a CHS geometry. Writes land in <see cref="Data"/>; <see cref="Dirty"/> says so.</summary>
public sealed class DiskImage
{
    public readonly byte[] Data;
    public readonly int Cylinders, Heads, Sectors;
    public readonly bool IsFloppy;
    public bool Dirty;
    public long TotalSectors => Data.Length / 512;

    public DiskImage(byte[] data, bool floppy)
    {
        Data = data; IsFloppy = floppy;
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

    public bool Read(long lba, int count, Span<byte> dst)
    {
        if (lba < 0 || lba + count > TotalSectors) return false;
        Data.AsSpan((int)(lba * 512), count * 512).CopyTo(dst);
        return true;
    }

    public bool Write(long lba, int count, ReadOnlySpan<byte> src)
    {
        if (lba < 0 || lba + count > TotalSectors) return false;
        src[..(count * 512)].CopyTo(Data.AsSpan((int)(lba * 512)));
        Dirty = true;
        return true;
    }
}
