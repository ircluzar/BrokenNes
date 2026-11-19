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
        BinCue();
        BusMaster();
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

    // X86-02: a mixed-mode BIN/CUE (MODE1/2352 data + two AUDIO tracks, two files, a PREGAP), the multi-track TOC, cooked + raw reads, CD audio play / pause /
    // sub-channel, the output, and a save state taken mid-play reloading to the same position.
    private static void BinCue()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bn_x86_bincue_" + Environment.ProcessId); Directory.CreateDirectory(dir);
        var iso = Iso(0, NoEmulBoot());
        int dataSec = iso.Length / 2048;
        var bin1 = new byte[dataSec * 2352];
        for (int i = 0; i < dataSec; i++) { int o = i * 2352; for (int k = 1; k < 11; k++) bin1[o + k] = 0xff; bin1[o + 15] = 1; Array.Copy(iso, i * 2048, bin1, o + 16, 2048); }
        const int audSec = 300;   // 4 s per audio track
        var bin2 = new byte[2 * audSec * 2352];
        for (int i = 0; i < bin2.Length / 4; i++) { short v = (short)(i / 588 < audSec ? 8000 : -8000); bin2[4 * i] = (byte)v; bin2[4 * i + 1] = (byte)(v >> 8); bin2[4 * i + 2] = (byte)v; bin2[4 * i + 3] = (byte)(v >> 8); }
        File.WriteAllBytes(Path.Combine(dir, "d.bin"), bin1); File.WriteAllBytes(Path.Combine(dir, "a.bin"), bin2);
        File.WriteAllText(Path.Combine(dir, "t.cue"), "FILE \"d.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\nFILE \"a.bin\" BINARY\n  TRACK 02 AUDIO\n    PREGAP 00:02:00\n    INDEX 01 00:00:00\n  TRACK 03 AUDIO\n    INDEX 00 00:03:00\n    INDEX 01 00:04:00\n");
        var cd = CdImage.Open(Path.Combine(dir, "t.cue"));
        Check(cd.Tracks.Count == 3 && cd.Tracks[1].Start == dataSec + 150 && cd.Tracks[2].Start == dataSec + 150 + audSec && cd.LeadOut == dataSec + 150 + 2 * audSec, $"CUE layout: {cd}");
        Check(cd.Tracks[2].Pregap == 75 && cd.Tracks[1].Pregap == 150, "CUE pregaps (PREGAP and INDEX 00)");
        var m = Ide(); m.Cd = cd; m.Floppy[0] = new DiskImage(FloppyBoot(), true); m.PowerOn();
        const int B = 0x170;
        m.Out8(B + 6, 0xa0);
        byte[]? Packet(byte[] cdb, int expect)
        {
            m.Out8(B + 6, 0xa0); m.Out8(B + 1, 0); m.Out8(B + 4, 0x00); m.Out8(B + 5, 0x80); m.Out8(B + 7, 0xa0);
            if (!WaitDrq(m, B) || m.In8(B + 2) != 1) return null;
            var p = new byte[12]; cdb.CopyTo(p, 0);
            for (int i = 0; i < 12; i += 2) m.Out16(B, (ushort)(p[i] | p[i + 1] << 8));
            var got = new System.Collections.Generic.List<byte>();
            while (WaitDrq(m, B)) { int n = m.In8(B + 4) | m.In8(B + 5) << 8; got.AddRange(ReadWords(m, B, n)); }
            if ((m.In8(B + 7) & 1) != 0) return null;
            return got.Count == expect ? got.ToArray() : null;
        }
        var toc = Packet(new byte[] { 0x43, 0, 0, 0, 0, 0, 0, 0, 0xff }, 4 + 8 * 4);
        Check(toc != null && toc[2] == 1 && toc[3] == 3 && toc[5] == 0x14 && toc[13] == 0x10 && toc[14] == 2 && toc[30] == 0xaa, "READ TOC: 1 data + 2 audio tracks + lead-out");
        var tocm = Packet(new byte[] { 0x43, 2, 0, 0, 0, 0, 0, 0, 0xff }, 36);
        Check(tocm != null && tocm[9] == 0 && tocm[10] == 2 && tocm[11] == 0, "READ TOC MSF: track 1 at 00:02:00");
        var rd = Packet(new byte[] { 0x28, 0, 0, 0, 0, 16, 0, 0, 4 }, 4 * 2048);
        Check(rd != null && rd.SequenceEqual(iso.Skip(16 * 2048).Take(4 * 2048)), "READ(10) cooked data out of MODE1/2352");
        Check(Packet(new byte[] { 0x28, 0, 0, 0, (byte)(dataSec + 160 >> 8), (byte)(dataSec + 160), 0, 0, 1 }, 0) == null, "READ(10) of an audio sector fails");
        var sense = Packet(new byte[] { 0x03, 0, 0, 0, 18 }, 18);
        Check(sense != null && sense[2] == 5 && sense[12] == 0x64, "REQUEST SENSE: illegal mode for this track");
        long a2 = cd.Tracks[1].Start;
        var raw = Packet(new byte[] { 0xbe, 0, (byte)(a2 >> 24), (byte)(a2 >> 16), (byte)(a2 >> 8), (byte)a2, 0, 0, 2, 0xf8 }, 2 * 2352);
        Check(raw != null && raw.SequenceEqual(bin2.Take(2 * 2352)), "READ CD: raw audio sectors");
        // play track 2 from its start to the end of track 3
        long s0 = a2 + 150, e0 = cd.LeadOut + 150;
        Check(Packet(new byte[] { 0x47, 0, 0, (byte)(s0 / 4500), (byte)(s0 / 75 % 60), (byte)(s0 % 75), (byte)(e0 / 4500), (byte)(e0 / 75 % 60), (byte)(e0 % 75) }, 0) != null, "PLAY AUDIO MSF accepted");
        byte[]? Sub() => Packet(new byte[] { 0x42, 0, 0x40, 1, 0, 0, 0, 0, 16 }, 16);
        var sub = Sub();
        Check(sub != null && sub[1] == 0x11 && sub[6] == 2, "READ SUB-CHANNEL: playing track 2");
        long Pos(byte[] q) => q[8] << 24 | q[9] << 16 | q[10] << 8 | q[11];
        long t0 = m.Clock; m.RunClock(m.InstructionsPerSecond); 
        sub = Sub(); long p1 = sub == null ? -1 : Pos(sub);
        Check(Math.Abs(p1 - (a2 + 75)) <= 2, $"one second of play advances 75 sectors (at {p1 - a2})");
        var st = m.SaveState();
        var buf = new short[2 * 4410]; Array.Clear(buf); m.Ata!.Secondary.MixCdAudio(buf, buf.Length);
        Check(buf.Skip(buf.Length - 100).All(v => v == 8000), "CD audio reaches the mix (track 2 tone)");
        m.RunClock(m.InstructionsPerSecond / 2); var after = Sub();
        var n = Ide(); n.Cd = cd; n.Floppy[0] = new DiskImage(FloppyBoot(), true); n.LoadState(st); n.RunClock(m.InstructionsPerSecond / 2);
        // n has no Packet helper bound: compare through the same port protocol
        var mm = m; m = n; var after2 = Sub(); m = mm;
        Check(after != null && after2 != null && after.SequenceEqual(after2), "save mid-play + reload: same play position half a second later");
        Check(Packet(new byte[] { 0x4b, 0, 0, 0, 0, 0, 0, 0, 0 }, 0) != null && Sub() is { } ps && ps[1] == 0x12, "PAUSE: sub-channel paused");
        long pp = Pos(Sub()!); m.RunClock(m.InstructionsPerSecond / 4);
        Check(Pos(Sub()!) == pp, "paused position holds");
        Check(Packet(new byte[] { 0x4b, 0, 0, 0, 0, 0, 0, 0, 1 }, 0) != null && Sub()![1] == 0x11, "RESUME");
        Check(Packet(new byte[] { 0x4e }, 0) != null && Sub()![1] == 0x15, "STOP PLAY");
        // a bare ISO through CdImage.Open and a single-file BIN/CUE with INDEX 00
        File.WriteAllBytes(Path.Combine(dir, "x.iso"), iso);
        var ci = CdImage.Open(Path.Combine(dir, "x.iso"));
        Check(ci.Tracks.Count == 1 && ci.LeadOut == dataSec && ci.ReadData(16, 1).SequenceEqual(iso.Skip(16 * 2048).Take(2048)), "Open(.iso)");
        File.WriteAllBytes(Path.Combine(dir, "one.bin"), bin1.Concat(bin2).ToArray());
        File.WriteAllText(Path.Combine(dir, "one.cue"), $"FILE \"one.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n  TRACK 02 AUDIO\n    INDEX 00 {dataSec / 75 / 60:00}:{dataSec / 75 % 60:00}:{dataSec % 75:00}\n    INDEX 01 {(dataSec + 150) / 75 / 60:00}:{(dataSec + 150) / 75 % 60:00}:{(dataSec + 150) % 75:00}\n");
        var c1 = CdImage.Open(Path.Combine(dir, "one.cue"));
        var rawb = new byte[2352]; c1.ReadRaw(c1.Tracks[1].Start, rawb, 0);
        Check(c1.Tracks[1].Start == dataSec + 150 && c1.LeadOut == dataSec + 2 * audSec && rawb.SequenceEqual(bin2.Skip(150 * 2352).Take(2352)), $"single-file CUE with INDEX 00: {c1}");
        try { Directory.Delete(dir, true); } catch { }
    }

    // X86-02: PIIX4 bus-master DMA on the PCI board: READ DMA / WRITE DMA through a two-entry PRD table, and an ATAPI READ(10) with the DMA feature bit.
    private static void BusMaster()
    {
        var m = new PcMachine(PcConfig.Win98 with { MemoryMB = 16 });
        var img = Pattern(16 * 63 * 4);
        m.Hdd[0] = new DiskImage(img, false);
        var iso = Iso(0, NoEmulBoot()); m.Cd = new CdImage(iso);
        m.PowerOn();
        uint bar = m.Pci!.ConfigRead(0, PciBus.IdeDevFn, 0x20);
        int bm = (int)(bar & 0xfffc);
        Check((bar & 1) == 1 && bm != 0 && (m.Pci.ConfigRead(0, PciBus.IdeDevFn, 4) & 4) != 0, $"POST puts the bus-master BAR4 at {bm:X4} and enables bus mastering");
        void Prd(uint at, params (uint addr, int n, bool eot)[] e) { for (int i = 0; i < e.Length; i++) { m.PhysW32(at + (uint)(8 * i), e[i].addr); m.PhysW32(at + (uint)(8 * i) + 4, (uint)(e[i].n & 0xffff) | (e[i].eot ? 0x80000000u : 0)); } }
        Prd(0x10000, (0x200000, 3 * 512, false), (0x300000, 5 * 512, true));
        const int B = 0x1f0;
        m.Out32(bm + 4, 0x10000); m.Out8(bm + 2, 0x06);
        m.Out8(B + 6, 0xe0); m.Out8(B + 2, 8); m.Out8(B + 3, 10); m.Out8(B + 4, 0); m.Out8(B + 5, 0); m.Out8(B + 7, 0xc8);
        m.Out8(bm, 0x09);   // start, to memory
        bool ok = true; for (int i = 0; i < 8 * 512; i++) if (m.Phys8((uint)(i < 1536 ? 0x200000 + i : 0x300000 + i - 1536)) != img[10 * 512 + i]) { ok = false; break; }
        Check(ok, "READ DMA: 8 sectors scattered over two PRD entries");
        Check((m.In8(bm + 2) & 7) == 4 && (m.In8(B + 7) & 0x89) == 0, "READ DMA: bus-master interrupt bit, idle status");
        m.Out8(bm, 0); m.Out8(bm + 2, 0x04);
        for (int i = 0; i < 512; i++) m.PhysW8(0x400000 + (uint)i, (byte)(255 - i));
        Prd(0x10100, (0x400000, 512, true)); m.Out32(bm + 4, 0x10100);
        m.Out8(B + 6, 0xe0); m.Out8(B + 2, 1); m.Out8(B + 3, 50); m.Out8(B + 4, 0); m.Out8(B + 5, 0); m.Out8(B + 7, 0xca);
        m.Out8(bm, 0x01);
        Check(img.Skip(50 * 512).Take(512).Select((v, i) => v == (byte)(255 - i)).All(x => x) && m.Hdd[0]!.Dirty, "WRITE DMA lands on the disk");
        m.Out8(bm, 0); m.Out8(bm + 2, 0x04);
        // ATAPI READ(10) 3 sectors with the DMA feature bit, engine started before the packet
        const int S = 0x170;
        Prd(0x10200, (0x500000, 3 * 2048, true)); m.Out32(bm + 8 + 4, 0x10200); m.Out8(bm + 8, 0x09);
        m.Out8(S + 6, 0xa0); m.Out8(S + 1, 1); m.Out8(S + 4, 0); m.Out8(S + 5, 0x80); m.Out8(S + 7, 0xa0);
        WaitDrq(m, S);
        var p = new byte[] { 0x28, 0, 0, 0, 0, 20, 0, 0, 3, 0, 0, 0 };
        for (int i = 0; i < 12; i += 2) m.Out16(S, (ushort)(p[i] | p[i + 1] << 8));
        ok = true; for (int i = 0; i < 3 * 2048; i++) if (m.Phys8(0x500000 + (uint)i) != iso[20 * 2048 + i]) { ok = false; break; }
        Check(ok && (m.In8(bm + 10) & 4) != 0 && (m.In8(S + 7) & 0x89) == 0, "ATAPI READ(10) by DMA");
        var st = m.SaveState(); var n = new PcMachine(PcConfig.Win98 with { MemoryMB = 16 }); n.Hdd[0] = m.Hdd[0]; n.Cd = m.Cd; n.LoadState(st);
        Check(n.In8(bm + 10) == m.In8(bm + 10) && n.In32(bm + 12) == 0x10200, "bus-master registers survive a state");
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
