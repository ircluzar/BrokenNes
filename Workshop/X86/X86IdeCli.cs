using System;
using System.IO;
using System.Linq;
using System.Text;
using NesEmulator.X86;

namespace BrokenNes.Workshop.X86;

/// <summary>
/// <c>--x86ide</c>: self-tests for the modular machine's IDE pieces (docs/projects/x86-modular-pc.md, M1/M3): the config system and its
/// save-state block, the ATA disk (IDENTIFY, CHS / LBA reads, READ / WRITE MULTIPLE, interrupts), the ATAPI CD-ROM (signature, PACKET
/// READ(10), READ CAPACITY, REQUEST SENSE), El Torito boot (no-emulation with INT 13h E0h reads, and 1.44 MB floppy emulation) and the
/// classic machine staying byte for byte what it was. Exit code 0 when every check passes.
/// </summary>
internal static class X86IdeCli
{
    private static int _fail, _pass;
    private static void Check(bool ok, string what) { if (ok) _pass++; else { _fail++; Console.WriteLine("FAIL " + what); } }

    public static int Run(string[] args)
    {
        Classic();
        AtaDisk();
        Atapi();
        CdBootNoEmulation();
        CdBootFloppyEmulation();
        StateBlock();
        Console.WriteLine($"x86ide: {_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    private static void Classic()
    {
        var a = new PcMachine();
        Check(a.Ata == null, "classic machine has no IDE controller");
        Check(a.Config == PcConfig.Default, "classic machine config is the default");
        a.Floppy[0] = new DiskImage(FloppyBoot(), true);
        a.PowerOn(); a.RunClock(200_000);
        var s = a.SaveState();
        Check(!Contains(s, 0x47464350), "classic state has no PCFG block");
        Check(a.In8(0x1f7) == 0xff, "classic machine: nothing answers 1F7h");
    }

    private static PcMachine Ide(int mb = 16) => new(PcConfig.Dos486 with { MemoryMB = mb });

    private static byte[] Pattern(int sectors) { var d = new byte[sectors * 512]; for (int i = 0; i < d.Length; i++) d[i] = (byte)(i / 512 * 7 + i); return d; }

    private static bool WaitDrq(PcMachine m, int b) { for (int i = 0; i < 1000; i++) { int s = m.In8(b + 7); if ((s & 0x80) == 0) return (s & 8) != 0; } return false; }

    private static byte[] ReadWords(PcMachine m, int b, int bytes) { var r = new byte[bytes]; for (int i = 0; i < bytes; i += 2) { ushort w = m.In16(b); r[i] = (byte)w; r[i + 1] = (byte)(w >> 8); } return r; }

    private static void AtaDisk()
    {
        var m = Ide();
        var img = Pattern(16 * 63 * 8);   // 8 cylinders
        m.Hdd[0] = new DiskImage(img, false);
        m.PowerOn();
        const int B = 0x1f0;
        Check(m.In8(B + 4) == 0 && m.In8(B + 5) == 0, "ATA disk signature");
        m.Out8(B + 6, 0xa0); m.Out8(B + 7, 0xec);
        Check(WaitDrq(m, B), "IDENTIFY gives DRQ");
        var id = ReadWords(m, B, 512);
        int lba = id[120] | id[121] << 8 | id[122] << 16 | id[123] << 24;
        Check(lba == img.Length / 512, $"IDENTIFY LBA capacity {lba}");
        string model = new string(Enumerable.Range(0, 40).Select(i => (char)id[54 + (i ^ 1)]).ToArray()).Trim();
        Check(model == "BrokenNes ATA disk", $"IDENTIFY model '{model}'");
        Check((m.In8(B + 7) & 8) == 0, "DRQ clears after 512 bytes");
        // LBA read of 2 sectors at 5
        m.Out8(B + 2, 2); m.Out8(B + 3, 5); m.Out8(B + 4, 0); m.Out8(B + 5, 0); m.Out8(B + 6, 0xe0); m.Out8(B + 7, 0x20);
        Check(WaitDrq(m, B), "READ SECTORS DRQ");
        var r1 = ReadWords(m, B, 512); Check(WaitDrq(m, B), "second sector DRQ"); var r2 = ReadWords(m, B, 512);
        Check(r1.SequenceEqual(img.Skip(5 * 512).Take(512)) && r2.SequenceEqual(img.Skip(6 * 512).Take(512)), "LBA read data");
        Check((m.In8(B + 7) & 0x89) == 0, "status idle after read");
        // CHS read: cyl 1, head 2, sector 3 -> lba (1*16+2)*63+2
        m.Out8(B + 2, 1); m.Out8(B + 3, 3); m.Out8(B + 4, 1); m.Out8(B + 5, 0); m.Out8(B + 6, 0xa2); m.Out8(B + 7, 0x20);
        WaitDrq(m, B);
        Check(ReadWords(m, B, 512).SequenceEqual(img.Skip(((1 * 16 + 2) * 63 + 2) * 512).Take(512)), "CHS read data");
        // WRITE MULTIPLE 8 sectors at LBA 100 with a block of 4, using 32-bit data writes
        m.Out8(B + 2, 4); m.Out8(B + 6, 0xe0); m.Out8(B + 7, 0xc6);
        Check((m.In8(B + 7) & 1) == 0, "SET MULTIPLE 4");
        m.Out8(B + 2, 8); m.Out8(B + 3, 100); m.Out8(B + 4, 0); m.Out8(B + 5, 0); m.Out8(B + 6, 0xe0); m.Out8(B + 7, 0xc5);
        for (int blk = 0; blk < 2; blk++)
        {
            Check(WaitDrq(m, B), $"WRITE MULTIPLE block {blk} DRQ");
            for (int i = 0; i < 4 * 512; i += 4) m.Out32(B, 0xA5000000u | (uint)(blk * 4096 + i));
        }
        Check((m.In8(B + 7) & 0x89) == 0, "status idle after write");
        Check(BitConverter.ToUInt32(img, 100 * 512 + 4 * 512 + 8) == (0xA5000000u | 4096 + 8), "write landed in the image");
        Check(m.Hdd[0]!.Dirty, "disk marked dirty");
        // out of range -> IDNF error
        m.Out8(B + 2, 1); m.Out8(B + 3, 0); m.Out8(B + 4, 0); m.Out8(B + 5, 0xff); m.Out8(B + 6, 0xef); m.Out8(B + 7, 0x20);
        Check((m.In8(B + 7) & 1) != 0 && m.In8(B + 1) == 0x10, "out-of-range read gives IDNF");
        // absent slave
        m.Out8(B + 6, 0xb0);
        Check(m.In8(B + 7) == 0, "absent slave status 0");
        // BIOS INT 13h still sees the disk (BDA count)
        Check(m.Phys8(0x475) == 1, "BIOS counts one hard disk");
        Check(m.Phys16(0x413) == 639 && m.Phys16(0x40e) == 0x9fc0, "EBDA at 9FC00h");
    }

    /// <summary>An ISO 9660-shaped image (only what El Torito and our tests read): a boot record at 17, a catalog at 19, payload at 20.</summary>
    private static byte[] Iso(byte media, byte[] bootImage, int sectors = 64)
    {
        var d = new byte[Math.Max(sectors, 21 + (bootImage.Length + 2047) / 2048) * 2048];
        int brvd = 17 * 2048;
        d[brvd] = 0; Encoding.ASCII.GetBytes("CD001").CopyTo(d, brvd + 1); d[brvd + 6] = 1;
        Encoding.ASCII.GetBytes("EL TORITO SPECIFICATION").CopyTo(d, brvd + 7);
        BitConverter.GetBytes(19u).CopyTo(d, brvd + 0x47);
        int cat = 19 * 2048;
        d[cat] = 1; d[cat + 0x1e] = 0x55; d[cat + 0x1f] = 0xaa;
        d[cat + 32] = 0x88; d[cat + 33] = media;
        BitConverter.GetBytes((ushort)(media == 0 ? (bootImage.Length + 511) / 512 : 1)).CopyTo(d, cat + 38);
        BitConverter.GetBytes(21u).CopyTo(d, cat + 40);
        d[20 * 2048] = (byte)'Z';
        bootImage.CopyTo(d, 21 * 2048);
        return d;
    }

    private static byte[] NoEmulBoot()
    {
        var b = new byte[512];
        byte[] code = { 0x0e, 0x1f, 0xb4, 0x42, 0xb2, 0xe0, 0xbe, 0x40, 0x00, 0xcd, 0x13, 0x6a, 0x00, 0x07, 0x26, 0xa0, 0x00, 0x80, 0xe6, 0xe9, 0xb0, 0x4b, 0xe6, 0xe9, 0xf4, 0xeb, 0xfd };
        code.CopyTo(b, 0);
        byte[] dap = { 0x10, 0, 1, 0, 0x00, 0x80, 0, 0, 20, 0, 0, 0, 0, 0, 0, 0 };
        dap.CopyTo(b, 0x40);
        return b;
    }

    private static byte[] FloppyBoot(int size = 1474560)
    {
        var f = new byte[size];
        byte[] code = { 0x31, 0xc0, 0x8e, 0xc0, 0xbb, 0x00, 0x80, 0xb8, 0x01, 0x02, 0xb9, 0x02, 0x00, 0xba, 0x00, 0x00, 0xcd, 0x13, 0x26, 0xa0, 0x00, 0x80, 0xe6, 0xe9, 0xb0, 0x46, 0xe6, 0xe9, 0xf4, 0xeb, 0xfd };
        code.CopyTo(f, 0); f[510] = 0x55; f[511] = 0xaa; f[512] = (byte)'Y';
        return f;
    }

    private static void Atapi()
    {
        var m = Ide();
        var iso = Iso(0, NoEmulBoot());
        m.Cd = new CdImage(iso);
        m.PowerOn();
        const int B = 0x170;
        m.Out8(B + 6, 0xa0);
        Check(m.In8(B + 4) == 0x14 && m.In8(B + 5) == 0xeb, "ATAPI signature");
        m.Out8(B + 7, 0xec);
        Check((m.In8(B + 7) & 1) != 0, "IDENTIFY DEVICE aborts on ATAPI");
        m.Out8(B + 7, 0xa1);
        Check(WaitDrq(m, B), "IDENTIFY PACKET DEVICE DRQ");
        var id = ReadWords(m, B, 512);
        Check((id[1] & 0x1f) == 0x05 && (id[1] & 0xc0) == 0x80, "IDENTIFY PACKET: ATAPI CD-ROM");
        byte[]? Packet(byte[] cdb, int expect)
        {
            m.Out8(B + 1, 0); m.Out8(B + 4, 0x00); m.Out8(B + 5, 0x80); m.Out8(B + 7, 0xa0);
            if (!WaitDrq(m, B) || m.In8(B + 2) != 1) return null;
            var p = new byte[12]; cdb.CopyTo(p, 0);
            for (int i = 0; i < 12; i += 2) m.Out16(B, (ushort)(p[i] | p[i + 1] << 8));
            var got = new System.Collections.Generic.List<byte>();
            while (WaitDrq(m, B))
            {
                int n = m.In8(B + 4) | m.In8(B + 5) << 8;
                got.AddRange(ReadWords(m, B, n));
            }
            if ((m.In8(B + 7) & 1) != 0) return null;
            return got.Count == expect ? got.ToArray() : null;
        }
        var cap = Packet(new byte[] { 0x25 }, 8);
        Check(cap != null && (cap[0] << 24 | cap[1] << 16 | cap[2] << 8 | cap[3]) == iso.Length / 2048 - 1 && cap[6] == 8, "READ CAPACITY");
        var rd = Packet(new byte[] { 0x28, 0, 0, 0, 0, 20, 0, 0, 20 }, 20 * 2048);   // 40 KB: more than one 32 KB DRQ chunk
        Check(rd != null && rd.SequenceEqual(iso.Skip(20 * 2048).Take(20 * 2048)), "READ(10) 20 sectors across chunks");
        Check(Packet(new byte[] { 0x28, 0, 0, 0, 0x10, 0, 0, 0, 1 }, 0) == null, "READ beyond the disc fails");
        var sense = Packet(new byte[] { 0x03, 0, 0, 0, 18 }, 18);
        Check(sense != null && sense[2] == 5 && sense[12] == 0x21, "REQUEST SENSE: ILLEGAL REQUEST / LBA out of range");
        var inq = Packet(new byte[] { 0x12, 0, 0, 0, 36 }, 36);
        Check(inq != null && inq[0] == 5, "INQUIRY: CD-ROM device");
        var toc = Packet(new byte[] { 0x43, 0, 0, 0, 0, 0, 0, 0, 20 }, 20);
        Check(toc != null && toc[14] == 0xaa, "READ TOC lead-out");
        var noDisc = Ide(); noDisc.PowerOn();
        noDisc.Out8(B + 6, 0xa0);
        Check(noDisc.In8(B + 4) == 0x14, "empty CD drive still answers ATAPI");
    }

    private static void CdBootNoEmulation()
    {
        var m = new PcMachine(PcConfig.Win98 with { CpuGen = 4 });
        m.Cd = new CdImage(Iso(0, NoEmulBoot()));
        m.PowerOn();
        m.RunClock(500_000);
        Check(m.DebugPort.ToString() == "ZK", $"El Torito no-emulation boot + INT 13h E0h read: '{m.DebugPort}' {m.Stopped}");
        Check(m.BootDrive == 0xe0, "boot drive E0h");
    }

    private static void CdBootFloppyEmulation()
    {
        var m = new PcMachine(PcConfig.Win98 with { CpuGen = 4 });
        m.Cd = new CdImage(Iso(2, FloppyBoot()));
        m.PowerOn();
        m.RunClock(500_000);
        Check(m.DebugPort.ToString() == "YF", $"El Torito 1.44 MB emulation boot: '{m.DebugPort}' {m.Stopped}");
        Check(m.BootDrive == 0, "emulated floppy is drive 00h");
    }

    private static void StateBlock()
    {
        var m = Ide(32);
        m.Hdd[0] = new DiskImage(Pattern(16 * 63 * 2), false);
        m.Cd = new CdImage(Iso(0, NoEmulBoot()));
        m.PowerOn(); m.RunClock(100_000);
        // leave a read half done
        m.Out8(0x1f2, 3); m.Out8(0x1f3, 0); m.Out8(0x1f4, 0); m.Out8(0x1f5, 0); m.Out8(0x1f6, 0xe0); m.Out8(0x1f7, 0x20);
        ReadWords(m, 0x1f0, 600);
        var s = m.SaveState();
        Check(Contains(s, 0x47464350), "IDE state has a PCFG block");
        var n = Ide(32); n.Cd = m.Cd; n.LoadState(s);
        var rest = ReadWords(n, 0x1f0, 3 * 512 - 600);
        var whole = Pattern(16 * 63 * 2);
        Check(rest.SequenceEqual(whole.Skip(600).Take(3 * 512 - 600)), "half-done read resumes after a state load");
        string? err = null;
        try { new PcMachine().LoadState(s); } catch (InvalidDataException e) { err = e.Message; }
        Check(err != null && err.Contains("this machine"), $"IDE state into a classic machine is a visible error ({err})");
        err = null;
        var classic = new PcMachine(32 * 1024 * 1024); classic.PowerOn();
        try { Ide(32).LoadState(classic.SaveState()); } catch (InvalidDataException e) { err = e.Message; }
        Check(err != null, "classic state into an IDE machine is a visible error");
    }

    private static bool Contains(byte[] s, int magic)
    {
        var k = BitConverter.GetBytes(magic);
        for (int i = s.Length - 4; i >= Math.Max(0, s.Length - 200_000); i--) if (s[i] == k[0] && s[i + 1] == k[1] && s[i + 2] == k[2] && s[i + 3] == k[3]) return true;
        return false;
    }
}
