using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--md-find &lt;rom&gt; --shots a.png,b.png,... [--input "60:Start,66:"] [--from F] [--to G] [--dy 9] [--dx 0] [--scan] [--dy-search] [--best out.png] [--tol N]</c>: runs the game and looks for the stretch of consecutive frames that matches the oracle's
/// screenshots best: for every start frame F in [from, to] it adds up, over the K listed screenshots, the pixels that differ between our frame F+k and screenshot k (the oracle's screenshots are offset by <c>dy</c> rows,
/// 9 for a 224-line picture) and reports the start frame with the fewest. One screenshot finds a picture; several consecutive ones (the oracle stepped frame by frame) pin the frame and check the animation and the raster effects too.
/// Exit code 0 when the best stretch has no differing pixel, 1 otherwise.
/// </summary>
internal static class MdFindCli
{
    internal static (int w, int h, uint[] px) Load(string path)
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

    internal static long Compare(uint[] ours, int ow, int oh, uint[] theirs, int tw, int th, int dx, int dy, int tol, long stopAbove)
    {
        long bad = 0;
        for (int y = 0; y < oh; y++)
        {
            int ty = y + dy; if (ty < 0 || ty >= th) continue;
            for (int x = 0; x < ow; x++)
            {
                int tx = x + dx; if (tx < 0 || tx >= tw) continue;
                if (Diff(ours[y * ow + x], theirs[ty * tw + tx]) > tol) bad++;
            }
            if (bad > stopAbove) return bad;
        }
        return bad;
    }

    public static int Run(string[] args)
    {
        string? rom = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
        string? shotList = M68kTestCli.Arg(args, "--shots");
        if (rom == null || shotList == null) { Console.WriteLine("usage: --md-find <rom> --shots a.png,b.png [--input script] [--from F] [--to G] [--dy 9] [--dx 0] [--scan] [--best out.png] [--tol N] [--region ntsc|pal] [--force]"); return 2; }
        var shots = shotList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Load).ToArray();
        int from = int.TryParse(M68kTestCli.Arg(args, "--from"), out int vf) ? vf : 0;
        int to = int.TryParse(M68kTestCli.Arg(args, "--to"), out int vt) ? vt : 900;
        int dy = int.TryParse(M68kTestCli.Arg(args, "--dy"), out int vdy) ? vdy : 9;
        int dx = int.TryParse(M68kTestCli.Arg(args, "--dx"), out int vdx) ? vdx : 0;
        int tol = int.TryParse(M68kTestCli.Arg(args, "--tol"), out int vtol) ? vtol : 0;
        var inputs = new SortedDictionary<int, PadButtons>();
        if (M68kTestCli.Arg(args, "--input") is { } script)
            foreach (var part in script.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int colon = part.IndexOf(':');
                if (colon < 0 || !int.TryParse(part[..colon], out int frame)) continue;
                PadButtons b = PadButtons.None;
                foreach (var name in part[(colon + 1)..].Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (Enum.TryParse<PadButtons>(name, true, out var one)) b |= one;
                inputs[frame] = b;
            }
        RegionChoice? region = M68kTestCli.Arg(args, "--region")?.ToLowerInvariant() switch { "ntsc" => RegionChoice.Ntsc, "pal" => RegionChoice.Pal, _ => null };
        byte[] data = File.ReadAllBytes(rom);
        if (SegaRomFormat.Detect(data, rom) != ConsoleKind.Genesis && !M68kTestCli.Flag(args, "--force")) { Console.WriteLine($"{rom} is not a Genesis ROM; --force"); return 2; }
        var session = new MdSession(data, "M68K", "MD", "MD", region);
        int k = shots.Length;
        if (M68kTestCli.Flag(args, "--scan"))
        {
            // independent screenshots: the best frame of ours for each one (they need not be consecutive)
            var bestBadS = Enumerable.Repeat(long.MaxValue, k).ToArray(); var bestFrameS = new int[k]; var bestDyS = new int[k];
            int[] dys = M68kTestCli.Arg(args, "--dys") is { } dl ? dl.Split(',').Select(int.Parse).ToArray() : new[] { dy };
            PadButtons padS = PadButtons.None;
            for (int f = 0; f <= to; f++)
            {
                if (inputs.TryGetValue(f, out var bs)) padS = bs;
                session.SetPad(0, padS);
                session.RunFrame();
                if (f < from) continue;
                for (int i = 0; i < k; i++)
                {
                    foreach (int dyv in dys)
                    {
                        long bad = Compare(session.Frame, session.FrameWidth, session.FrameHeight, shots[i].px, shots[i].w, shots[i].h, dx, dyv, tol, bestBadS[i]);
                        if (bad < bestBadS[i]) { bestBadS[i] = bad; bestFrameS[i] = f; bestDyS[i] = dyv; }
                    }
                }
            }
            for (int i = 0; i < k; i++) Console.WriteLine($"shot {i}: best frame {bestFrameS[i]} (dy {bestDyS[i]}): {bestBadS[i]:N0} differing pixels");
            return bestBadS.All(b => b == 0) ? 0 : 1;
        }
        // our frames, kept in a ring of k: frame f's picture is copied after f is run
        var ring = new (int w, int h, uint[] px)[k];
        for (int i = 0; i < k; i++) ring[i] = (0, 0, new uint[MdVdp.PictureStride * MdVdp.PictureMaxHeight]);
        PadButtons pad = PadButtons.None;
        long bestBad = long.MaxValue; int bestStart = -1; long[] bestPer = Array.Empty<long>();
        uint[]? bestFrame = null; int bestW = 0, bestH = 0;
        int last = to + k;
        for (int f = 0; f < last; f++)
        {
            if (inputs.TryGetValue(f, out var b)) pad = b;
            session.SetPad(0, pad);
            session.RunFrame();
            var slot = ring[f % k];
            Array.Copy(session.Frame, slot.px, session.FrameWidth * session.FrameHeight);
            ring[f % k] = (session.FrameWidth, session.FrameHeight, slot.px);
            int start = f - k + 1;
            if (start < from || start > to) continue;
            long total = 0; var per = new long[k];
            for (int i = 0; i < k && total < bestBad; i++)
            {
                var o = ring[(start + i) % k];
                per[i] = Compare(o.px, o.w, o.h, shots[i].px, shots[i].w, shots[i].h, dx, dy, tol, bestBad - total);
                total += per[i];
            }
            if (total < bestBad)
            {
                bestBad = total; bestStart = start; bestPer = per;
                if (M68kTestCli.Arg(args, "--best") != null) { var o = ring[(start) % k]; bestFrame = (uint[])o.px.Clone(); bestW = o.w; bestH = o.h; }
            }
        }
        Console.WriteLine($"best start frame {bestStart}: {bestBad:N0} differing pixels over {k} screenshot(s), per frame: {string.Join(" ", bestPer.Select(v => v.ToString("N0")))}");
        if (M68kTestCli.Arg(args, "--best") is { } outPng && bestFrame != null)
        {
            using var bmp = new Bitmap(bestW, bestH, PixelFormat.Format32bppArgb);
            for (int y = 0; y < bestH; y++) for (int x = 0; x < bestW; x++) bmp.SetPixel(x, y, Color.FromArgb((int)bestFrame[y * bestW + x]));
            bmp.Save(outPng, ImageFormat.Png);
            Console.WriteLine($"wrote {outPng} (our frame {bestStart})");
        }
        return bestBad == 0 ? 0 : 1;
    }
}
