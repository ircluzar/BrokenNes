using System.Security.Cryptography;
using NesEmulator.X86;

namespace BrokenNes.Workshop.X86;

/// <summary>--bigdisktest [GB]: a big (chunked) disk base, writes all over it (past 2 and 4 GB), a state diff saved and loaded onto the same base,
/// every written sector compared, the base hash checked against a byte[] of the same bytes on a small size. Exit 0 = all equal.</summary>
internal static class BigDiskTest
{
    public static int Run(string[] args)
    {
        long gb = args.Length > 1 ? long.Parse(args[1]) : 8;
        long len = gb << 30; int fails = 0;
        var rnd = new Random(1234);
        var bb = new DiskBigBase(len);
        var mbr = new byte[512]; mbr[510] = 0x55; mbr[511] = 0xaa; bb.Fill(0, mbr);
        var seed = new byte[1 << 20]; rnd.NextBytes(seed); bb.Fill(len - seed.Length, seed); bb.Fill((3L << 30) + 4096, seed);
        var d = new DiskImage(bb);
        Console.WriteLine($"bigdisk: {gb} GB, sectors {d.TotalSectors}, allocated {bb.Allocated >> 20} MB, hash {bb.Hash}");
        var written = new Dictionary<long, byte[]>();
        for (int i = 0; i < 4000; i++)
        {
            long s = (long)(rnd.NextDouble() * d.TotalSectors); var sec = new byte[512]; rnd.NextBytes(sec);
            d.Write(s, 1, sec); written[s] = sec;
        }
        var ms = new MemoryStream(); using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true)) d.SaveDiff(w);
        ms.Position = 0;
        var d2 = DiskImage.LoadDiff(new BinaryReader(ms), Array.Empty<byte[]?>(), new[] { bb });
        var buf = new byte[512];
        foreach (var (s, sec) in written) { d2.Read(s, 1, buf); if (!buf.AsSpan().SequenceEqual(sec)) fails++; }
        d2.Read(d.TotalSectors - 1, 1, buf); var tail = new byte[512]; d.Read(d.TotalSectors - 1, 1, tail); if (!buf.AsSpan().SequenceEqual(tail)) fails++;
        Console.WriteLine($"bigdisk: state diff {ms.Length >> 10} KB, overlay {d.OverlaySectors} / {d2.OverlaySectors} sectors, content key {(d.ContentKey() == d2.ContentKey() ? "same" : "DIFFERENT")}, mismatches {fails}");
        if (d.ContentKey() != d2.ContentKey()) fails++;
        // hash definition: a 100 MB big base == the byte[] p256 of the same bytes
        var small = new byte[100 << 20]; rnd.NextBytes(small.AsSpan(0, 5 << 20)); rnd.NextBytes(small.AsSpan(70 << 20, 1 << 20));
        var sb = new DiskBigBase(small.Length); sb.Fill(0, small);
        bool same = sb.Hash == DiskImage.HashOf(small);
        Console.WriteLine($"bigdisk: p256 big vs byte[] {(same ? "same" : "DIFFERENT")}");
        if (!same) fails++;
        Console.WriteLine(fails == 0 ? "bigdisk: PASS" : "bigdisk: FAIL");
        return fails == 0 ? 0 : 1;
    }
}
