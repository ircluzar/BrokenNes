using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--img-diff &lt;ours.png&gt; &lt;theirs.png&gt; [--dy N] [--dx N] [--search] [--out diff.png] [--tol N] [--region x,y,w,h]</c>: compares two pictures pixel by pixel (the second one is shifted by dx, dy first: the oracle's screenshots carry
/// a border of backdrop lines around the 224-line picture) and reports how many pixels differ by more than the tolerance (per channel, default 0), where (bounding box and the busiest 16x16 blocks), and with <c>--out</c> writes
/// a montage of ours, theirs and the difference. <c>--search</c> finds the shift (dx in -8..8, dy in -4..20) that matches best first. Exit code 0 when no pixel differs, 1 otherwise, 2 on usage errors.
/// </summary>
internal static class ImgDiffCli
{
    private static (int w, int h, uint[] px) Load(string path)
    {
        using var bmp = new Bitmap(path);
        int w = bmp.Width, h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new uint[w * h];
        try
        {
            var row = new int[w];
            for (int y = 0; y < h; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, w);
                for (int x = 0; x < w; x++) px[y * w + x] = (uint)row[x] | 0xFF000000u;
            }
        }
        finally { bmp.UnlockBits(data); }
        return (w, h, px);
    }

    private static int Diff(uint a, uint b)
    {
        int dr = Math.Abs((int)(a >> 16 & 255) - (int)(b >> 16 & 255)), dg = Math.Abs((int)(a >> 8 & 255) - (int)(b >> 8 & 255)), db = Math.Abs((int)(a & 255) - (int)(b & 255));
        return Math.Max(dr, Math.Max(dg, db));
    }

    private static long Mismatches(uint[] a, int aw, int ah, uint[] b, int bw, int bh, int dx, int dy, int tol, Rectangle? region, out int compared)
    {
        long bad = 0; compared = 0;
        int x0 = 0, y0 = 0, x1 = aw, y1 = ah;
        if (region is { } r) { x0 = r.X; y0 = r.Y; x1 = Math.Min(aw, r.X + r.Width); y1 = Math.Min(ah, r.Y + r.Height); }
        for (int y = y0; y < y1; y++)
        {
            int by = y + dy; if (by < 0 || by >= bh) continue;
            for (int x = x0; x < x1; x++)
            {
                int bx = x + dx; if (bx < 0 || bx >= bw) continue;
                compared++;
                if (Diff(a[y * aw + x], b[by * bw + bx]) > tol) bad++;
            }
        }
        return bad;
    }

    public static int Run(string[] args)
    {
        if (args.Length < 3 || args[1].StartsWith("--") || args[2].StartsWith("--")) { Console.WriteLine("usage: --img-diff <ours.png> <theirs.png> [--dx N] [--dy N] [--search] [--out diff.png] [--tol N] [--region x,y,w,h]"); return 2; }
        (int aw, int ah, uint[] a) = Load(args[1]);
        (int bw, int bh, uint[] b) = Load(args[2]);
        int dx = int.TryParse(M68kTestCli.Arg(args, "--dx"), out int vx) ? vx : 0;
        int dy = int.TryParse(M68kTestCli.Arg(args, "--dy"), out int vy) ? vy : 0;
        int tol = int.TryParse(M68kTestCli.Arg(args, "--tol"), out int vt) ? vt : 0;
        Rectangle? region = null;
        if (M68kTestCli.Arg(args, "--region") is { } rs) { var p = rs.Split(',').Select(int.Parse).ToArray(); region = new Rectangle(p[0], p[1], p[2], p[3]); }
        if (M68kTestCli.Flag(args, "--search"))
        {
            long best = long.MaxValue; int bdx = 0, bdy = 0;
            for (int sy = -4; sy <= 20; sy++)
                for (int sx = -8; sx <= 8; sx++)
                {
                    long m = Mismatches(a, aw, ah, b, bw, bh, sx, sy, tol, region, out int c);
                    if (c > aw * ah / 2 && m < best) { best = m; bdx = sx; bdy = sy; }
                }
            dx = bdx; dy = bdy;
            Console.WriteLine($"best shift of theirs: dx {dx} dy {dy}");
        }
        long bad = Mismatches(a, aw, ah, b, bw, bh, dx, dy, tol, region, out int compared);
        Console.WriteLine($"ours {aw}x{ah}, theirs {bw}x{bh}, shift ({dx},{dy}), compared {compared:N0} pixels, {bad:N0} differ by more than {tol} ({(compared > 0 ? 100.0 * bad / compared : 0):F3}%)");
        if (bad > 0)
        {
            int minx = int.MaxValue, miny = int.MaxValue, maxx = -1, maxy = -1;
            var blocks = new Dictionary<(int, int), int>();
            for (int y = 0; y < ah; y++)
            {
                int by = y + dy; if (by < 0 || by >= bh) continue;
                for (int x = 0; x < aw; x++)
                {
                    int bx = x + dx; if (bx < 0 || bx >= bw) continue;
                    if (region is { } r && !r.Contains(x, y)) continue;
                    if (Diff(a[y * aw + x], b[by * bw + bx]) <= tol) continue;
                    minx = Math.Min(minx, x); maxx = Math.Max(maxx, x); miny = Math.Min(miny, y); maxy = Math.Max(maxy, y);
                    blocks[(x >> 4, y >> 4)] = blocks.GetValueOrDefault((x >> 4, y >> 4)) + 1;
                }
            }
            Console.WriteLine($"differences in x {minx}..{maxx}, y {miny}..{maxy}; busiest 16x16 blocks (x,y: pixels): " + string.Join("  ", blocks.OrderByDescending(k => k.Value).Take(10).Select(k => $"({k.Key.Item1 * 16},{k.Key.Item2 * 16}): {k.Value}")));
            // a few example pixels
            int shown = 0;
            for (int y = 0; y < ah && shown < 6; y++)
                for (int x = 0; x < aw && shown < 6; x++)
                {
                    int bx = x + dx, by = y + dy; if (bx < 0 || bx >= bw || by < 0 || by >= bh) continue;
                    if (region is { } r && !r.Contains(x, y)) continue;
                    if (Diff(a[y * aw + x], b[by * bw + bx]) > tol) { Console.WriteLine($"  ({x},{y}) ours {a[y * aw + x] & 0xFFFFFF:X6} theirs {b[by * bw + bx] & 0xFFFFFF:X6}"); shown += 1; x += 40; }
                }
        }
        if (M68kTestCli.Arg(args, "--out") is { } outPath)
        {
            int w = aw, h = ah;
            using var m = new Bitmap(w * 3 + 8, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(m)) g.Clear(Color.Gray);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    m.SetPixel(x, y, Color.FromArgb((int)a[y * aw + x]));
                    int bx = x + dx, by = y + dy;
                    uint tb = bx >= 0 && bx < bw && by >= 0 && by < bh ? b[by * bw + bx] : 0xFF404040u;
                    m.SetPixel(w + 4 + x, y, Color.FromArgb((int)tb));
                    bool d = bx >= 0 && bx < bw && by >= 0 && by < bh && Diff(a[y * aw + x], tb) > tol;
                    m.SetPixel(2 * w + 8 + x, y, d ? Color.Red : Color.FromArgb(unchecked((int)0xFF000000) | (int)(a[y * aw + x] >> 2 & 0x3F3F3F)));
                }
            m.Save(outPath, ImageFormat.Png);
            Console.WriteLine($"wrote {outPath}");
        }
        return bad == 0 ? 0 : 1;
    }
}
