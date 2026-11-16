// The BIOS's IDE-machine part (docs/projects/x86-modular-pc.md, M3/M4): only an IDE-config machine runs any of this, so the classic
// machine's POST and memory stay byte for byte what they were. Boot order from the config (CD-ROM / hard disk / floppy), El Torito
// boot from the ATAPI CD (no-emulation, and 1.2 / 1.44 / 2.88 MB floppy emulation: what the Windows 98 CD boots with), INT 13h on the
// CD (drive E0h: the EDD calls with 2048-byte blocks, 4Bh emulation status / terminate), the fixed disk parameter tables (INT 41h /
// 46h) and the extended BIOS data area at 9FC00h that the E820 map already reserves. Written for BrokenNes from the El Torito 1.0 and
// EDD 3.0 specifications.

using System;
using System.IO;
using System.Text;

namespace NesEmulator.X86;

public sealed partial class PcMachine
{
    private const int CdDrive = 0xe0;
    private DiskImage? _cdEmu;        // the floppy image an El Torito emulation boot put on drive 00h
    private bool _cdBoot;             // booted the CD without emulation (drive E0h)
    private uint _cdImageLba;
    private byte _cdMedia;

    private void PostIde()
    {
        // the EBDA (1 KB at 9FC00h): conventional memory is 639 KB
        PhysW16(0x413, 639);
        PhysW16(0x40e, 0x9fc0);
        Array.Clear(Cpu.Mem, 0x9fc00, 0x400);
        PhysW8(0x9fc00, 1);
        // fixed disk parameter tables for C: and D: (INT 41h, 46h)
        for (int i = 0; i < 2; i++)
        {
            uint t = Rom + FixedDiskTable + (uint)i * 16;
            Array.Clear(Cpu.Mem, (int)t, 16);
            if (Hdd[i] is { } d)
            {
                PhysW16(t, (ushort)d.Cylinders); PhysW8(t + 2, (byte)d.Heads);
                PhysW16(t + 5, 0xffff); PhysW8(t + 8, (byte)(d.Heads > 8 ? 8 : 0));
                PhysW16(t + 12, (ushort)d.Cylinders); PhysW8(t + 14, (byte)d.Sectors);
            }
            PhysW16((i == 0 ? 0x41u : 0x46u) * 4, (ushort)(FixedDiskTable + i * 16)); PhysW16((i == 0 ? 0x41u : 0x46u) * 4 + 2, (ushort)RomSeg);
        }
        // CMOS: drive types 47 (user-defined) with the geometry, so setup programs and Windows' real-mode check agree
        Cmos.Ram[0x12] = (byte)((Hdd[0] != null ? 0xf0 : 0) | (Hdd[1] != null ? 0x0f : 0));
        Cmos.Ram[0x19] = (byte)(Hdd[0] != null ? 47 : 0); Cmos.Ram[0x1a] = (byte)(Hdd[1] != null ? 47 : 0);
        int ext = Math.Min(MemKb - 1024, 0xffff);
        Cmos.Ram[0x17] = (byte)ext; Cmos.Ram[0x18] = (byte)(ext >> 8);
        // above 16 MB in 64 KB units (CMOS 34h/35h, as AMI / Award BIOSes keep it)
        int above16 = Math.Max(0, (MemKb - 16 * 1024) / 64);
        Cmos.Ram[0x34] = (byte)above16; Cmos.Ram[0x35] = (byte)(above16 >> 8);
    }

    private void BootIde()
    {
        foreach (char c in Config.BootOrder)
        {
            if (c == 'C' && Cd != null && BootCd()) return;
            if (c == 'F' && TryBootDisk(0x00)) return;
            if (c == 'H' && TryBootDisk(0x80)) return;
            if (Stopped != null) return;
        }
        TeletypeString("No bootable disk.\r\n");
        Stopped = "no bootable disk";
    }

    private bool TryBootDisk(int drive)
    {
        var d = Disk(drive);
        if (d == null) return false;
        var sec = new byte[512];
        if (!d.Read(0, 1, sec) || sec[510] != 0x55 || sec[511] != 0xaa) return false;
        Array.Copy(sec, 0, Cpu.Mem, 0x7c00, 512);
        StartBoot(drive, 0, 0x7c00);
        return true;
    }

    private void StartBoot(int drive, uint cs, uint ip)
    {
        BootDrive = drive;
        C.LoadSeg(I386.SEG_SS, 0); C.R[4] = 0x7c00;
        C.LoadSeg(I386.SEG_DS, 0); C.LoadSeg(I386.SEG_ES, 0);
        C.R[2] = (uint)drive;
        C.Flags = 0x202;
        C.SetCsIp(cs, ip);
    }

    /// <summary>El Torito: the boot record volume descriptor at sector 17 names the boot catalog; its initial entry is the image to start.</summary>
    private bool BootCd()
    {
        var cd = Cd!;
        if (cd.Sectors < 18) return false;
        var brvd = cd.Data.AsSpan(17 * 2048, 2048);
        if (brvd[0] != 0 || Encoding.ASCII.GetString(brvd.Slice(1, 5)) != "CD001" || !Encoding.ASCII.GetString(brvd.Slice(7, 23)).StartsWith("EL TORITO SPECIFICATION")) return false;
        uint cat = BitConverter.ToUInt32(brvd.Slice(0x47, 4));
        if (cat >= cd.Sectors) return false;
        var c = cd.Data.AsSpan((int)(cat * 2048), 64);
        if (c[0] != 1 || c[0x1e] != 0x55 || c[0x1f] != 0xaa || c[32] != 0x88) return false;
        byte media = (byte)(c[33] & 0x0f);
        uint seg = BitConverter.ToUInt16(c.Slice(34, 2)); if (seg == 0) seg = 0x7c0;
        int count = BitConverter.ToUInt16(c.Slice(38, 2));
        uint lba = BitConverter.ToUInt32(c.Slice(40, 4));
        _cdImageLba = lba; _cdMedia = media;
        if (media == 0)
        {
            int bytes = Math.Max(1, count) * 512;
            if ((long)lba * 2048 + bytes > cd.Data.Length || (seg << 4) + bytes > 0x9fc00) return false;
            Array.Copy(cd.Data, lba * 2048, Cpu.Mem, seg << 4, bytes);
            _cdBoot = true;
            StartBoot(CdDrive, seg, 0);
            return true;
        }
        int size = media switch { 1 => 1228800, 2 => 1474560, 3 => 2949120, _ => 0 };
        if (size == 0) { Stopped = "El Torito hard disk emulation (media type 4) is not supported"; return false; }
        if ((long)lba * 2048 + size > cd.Data.Length) return false;
        _cdEmu = new DiskImage(cd.Data.AsSpan((int)(lba * 2048), size).ToArray(), true);
        if ((Phys16(0x410) & 1) == 0) PhysW16(0x410, (ushort)(Phys16(0x410) | 1));   // the emulated A: is a floppy drive (equipment word)
        return TryBootDisk(0x00);
    }

    /// <summary>INT 13h calls only an IDE machine answers: the CD (drive E0h) and 4Bh. False: the classic INT 13h code handles it.</summary>
    private bool Int13Ide(int dl, uint fn)
    {
        if (fn == 0x4b)
        {
            if (!_cdBoot && _cdEmu == null) { Fail(0x01); return true; }
            uint p = SegB(I386.SEG_DS) + SI;
            for (uint i = 0; i < 0x13; i++) PhysW8(p + i, 0);
            PhysW8(p, 0x13); PhysW8(p + 1, _cdMedia); PhysW8(p + 2, (byte)(_cdBoot ? CdDrive : 0)); PhysW8(p + 3, 0);
            PhysW32(p + 4, _cdImageLba); PhysW16(p + 8, 0);   // device spec: secondary master
            if (AL == 0x00) _cdEmu = null;                    // terminate emulation: drive 00h is the real A: again
            AH = 0; Ok(); return true;
        }
        if (dl != CdDrive) return false;
        var cd = Cd;
        if (cd == null) { DiskStatus(0x80, 0xaa); return true; }
        switch (fn)
        {
            case 0x00: case 0x01: case 0x0d: case 0x10: case 0x47: AH = 0; Ok(); return true;
            case 0x15: AH = 0x03; Ok(); return true;
            case 0x41:
                if (BX != 0x55aa) { Fail(0x01); return true; }
                BX = 0xaa55; AH = 0x21; CX = 0x0001; Ok(); return true;
            case 0x42:
            {
                uint p = SegB(I386.SEG_DS) + SI;
                int count = Phys16(p + 2);
                uint buf = (uint)(Phys16(p + 6) << 4) + Phys16(p + 4);
                long lba = Phys32(p + 8) | ((long)Phys32(p + 12) << 32);
                if (lba < 0 || lba + count > cd.Sectors) { PhysW16(p + 2, 0); AH = 0x04; SetCF(true); return true; }
                for (int i = 0; i < count * 2048; i++) PhysW8(buf + (uint)i, cd.Data[lba * 2048 + i]);
                AH = 0; Ok(); return true;
            }
            case 0x48:
            {
                uint p = SegB(I386.SEG_DS) + SI;
                if (Phys16(p) < 0x1a) { Fail(0x01); return true; }
                PhysW16(p, 0x1a); PhysW16(p + 2, 0x74);      // removable, changeline, lockable, geometry not valid
                PhysW32(p + 4, 0); PhysW32(p + 8, 0); PhysW32(p + 12, 0);
                PhysW32(p + 16, (uint)cd.Sectors); PhysW32(p + 20, 0); PhysW16(p + 24, 2048);
                AH = 0; Ok(); return true;
            }
            default: Fail(0x01); return true;
        }
    }

    private void SaveBiosExtras(BinaryWriter w)
    {
        w.Write(_cdBoot); w.Write(_cdImageLba); w.Write(_cdMedia);
        w.Write(_cdEmu != null);
        if (_cdEmu != null) { w.Write(_cdEmu.Data.Length); w.Write(_cdEmu.Data); }
    }

    private void LoadBiosExtras(BinaryReader r)
    {
        _cdBoot = r.ReadBoolean(); _cdImageLba = r.ReadUInt32(); _cdMedia = r.ReadByte();
        _cdEmu = r.ReadBoolean() ? new DiskImage(r.ReadBytes(r.ReadInt32()), true) : null;
    }
}
