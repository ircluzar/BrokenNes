using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator;
using NesEmulator.Mix;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--md-bridge-scenes &lt;rom&gt; --count N [--chip NES:FIXS] [--first 8] [--period 12] [--only 3,5] [--bad 12] [--max-mask N] [--noise 0.04] [--min-ssim 0.9] [--png dir]</c>: the Genesis picture bridge over a scene ROM (Track B4a's <c>scenefuzz.py</c> / <c>vsprobe.py</c>
/// make them: each scene is a complete VDP state shown until Start is pressed). The ROM runs inside a <see cref="SegaPictureSession"/> on the NES chip; Start is pressed every <c>--period</c> frames from frame <c>--first</c>
/// (as <c>--md-scenes</c> does), and just before each press the bridge's picture is compared with the VDP's own picture of the scene (the VDP's pictures are pixel-identical with BlastEm's on B4a's pinned sets): the
/// number of pixels drawn in one and not the other (a pixel is drawn when it is not black), and, on a layered chip, the number whose colour is not the NES colour nearest to the VDP's. Prints one line per scene and the totals.
/// A scene whose mask differs is "counted" when the frame's own loss counters say the bridge could not show something (more tile variants than the layer holds, past the 1023 of a layer or the sprite tiles);
/// exit code 0 when no scene differs without such a counter (a difference nothing says is a bug), 1 otherwise. Colour differences are not judged: a random scene has up to 15 colours a tile and the bridge keeps 3.
/// </summary>
internal static class MdBridgeScenesCli
{
    public static int Run(string[] args)
    {
        string? rom = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
        if (rom == null) { Console.WriteLine("usage: --md-bridge-scenes <rom> --count N [--chip NES:FIXS] [--first 8] [--period 12] [--only 3,5] [--bad 12] [--max-mask 0]"); return 2; }
        int count = int.TryParse(M68kTestCli.Arg(args, "--count"), out int c0) ? c0 : 20;
        int first = int.TryParse(M68kTestCli.Arg(args, "--first"), out int f0) ? f0 : 8;
        int period = int.TryParse(M68kTestCli.Arg(args, "--period"), out int p0) ? p0 : 12;
        int listBad = int.TryParse(M68kTestCli.Arg(args, "--bad"), out int b0) ? b0 : 12;
        int maxMask = int.TryParse(M68kTestCli.Arg(args, "--max-mask"), out int m0) ? m0 : 0;
        // --noise 0.04: a scene whose mask differs in no more than this fraction of the picture is not judged (a random scene is dense noise in 15 colours a tile: a colour the VDP shows as very dark and the bridge shows as the
        // NES's black counts as a difference, and the mask cannot see a displaced noise texture at all; the designed probe sets are judged exactly, --noise 0)
        double noise = double.TryParse(M68kTestCli.Arg(args, "--noise"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n0) ? n0 : 0;
        string chip = M68kTestCli.Arg(args, "--chip") ?? "NES:FIXS";
        var only = (M68kTestCli.Arg(args, "--only") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
        bool layered = chip == "NES:FIXS";
        string? pngDir = M68kTestCli.Arg(args, "--png");      // reference | bridge | difference for every scene that differs

        var inner = new MdSession(File.ReadAllBytes(rom), "M68K", "MD", "MD");
        var wrap = new SegaPictureSession(inner, chip);
        // --min-ssim 0.9: a scene whose structural similarity (the bridge metric, luma over 8x8 windows) with the VDP's picture is lower is judged too, whatever the mask says: a mask is blind to a plane
        // shown at the wrong scroll (every pixel of a full plane is "drawn"), the designed probe sets have a clear structure that a wrong row or column breaks
        double minSsim = double.TryParse(M68kTestCli.Arg(args, "--min-ssim"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s0) ? s0 : 0;
        var rows = new List<(int scene, int mask, int colour, string counters, double ssim)>();
        int total = first + (count - 1) * period;
        for (int f = 0; f < total; f++)
        {
            int rel = f - first;
            bool pressing = rel >= 0 && rel % period < 3 && rel / period < count - 1;
            wrap.SetPad(0, pressing ? PadButtons.Start : PadButtons.None);
            wrap.RunFrame();
            int si = (f + 1 - first) / period;
            if (f + 1 - first >= 0 && (f + 1 - first) % period == 0 && si < count && (only.Count == 0 || only.Contains(si)))
            {
                var reg = wrap.Region; int mask = 0, colour = 0;
                for (int y = reg.Y; y < reg.Y + reg.H; y++)
                    for (int x = reg.X; x < reg.X + reg.W; x++)
                    {
                        uint b = wrap.BridgeCanvas[y * 256 + x] & 0xFFFFFF, r = wrap.ReferenceCanvas[y * 256 + x] & 0xFFFFFF;
                        if ((b != 0) != (r != 0)) mask++;
                        if (layered && b != NearestNes(r)) colour++;
                    }
                double ssim = PictureMetrics.Compare(wrap.BridgeCanvas, wrap.ReferenceCanvas, 256, reg.X, reg.Y, reg.W, reg.H).Ssim;
                rows.Add((si, mask, colour, wrap.Counters.ToString(), ssim));
                if (pngDir != null && mask > 0)
                {
                    // reference | bridge | difference (a pixel drawn in one picture and not the other)
                    var both = new uint[3 * 256 * 240];
                    for (int y = 0; y < 240; y++) for (int x = 0; x < 256; x++)
                    {
                        uint b = wrap.BridgeCanvas[y * 256 + x], r = wrap.ReferenceCanvas[y * 256 + x];
                        both[y * 768 + x] = r; both[y * 768 + 256 + x] = b;
                        both[y * 768 + 512 + x] = ((b & 0xFFFFFF) != 0) != ((r & 0xFFFFFF) != 0) ? 0xFFFF0000u : 0xFF000000u;
                    }
                    Directory.CreateDirectory(pngDir);
                    BrokenNes.Workshop.MixLab.MixLabCli.SaveArgb(both, 768, 240, Path.Combine(pngDir, $"scene{si:D3}.png"));
                }
            }
        }
        // a difference is "explained" when the frame's own loss counters say the bridge could not show something (tiles past the layer's 1023, a vertical scroll or a palette that changes inside the frame)
        string[] known = { "background tiles past", "sprite tiles past" };      // a scene is static: a scroll or palette that "changes inside the frame" is not an excuse here
        bool Explained(string counters) => known.Any(k => counters.Contains(k));
        int area = wrap.Region.W * wrap.Region.H;
        bool Judged((int scene, int mask, int colour, string counters, double ssim) r) => (r.mask > maxMask && r.mask > noise * area) || r.ssim < minSsim;
        foreach (var r in rows.OrderBy(r => r.scene))
            Console.WriteLine($"scene {r.scene,3}: mask {r.mask,6}  ssim {r.ssim:F3}{(layered ? $"  colour {r.colour,6}" : "")}{(Judged(r) ? (Explained(r.counters) ? "  (counted loss)" : "  (NOT counted)") : r.mask > maxMask ? "  (noise level)" : "")}");
        int exact = rows.Count(r => r.mask <= maxMask && !Judged(r)), noisy = rows.Count(r => r.mask > maxMask && !Judged(r)), counted = rows.Count(r => Judged(r) && Explained(r.counters)), unexplained = rows.Count(r => Judged(r) && !Explained(r.counters));
        Console.WriteLine($"== {chip}: {rows.Count} scenes: {exact} exact, {noisy} at noise level (up to {noise:P0} of the picture), {counted} with a counted loss, {unexplained} differing with no loss counted, {rows.Sum(r => (long)r.mask)} mask pixels in all, mean ssim {rows.Average(r => r.ssim):F3}, lowest {rows.Min(r => r.ssim):F3}");
        foreach (var r in rows.Where(r => Judged(r) && !Explained(r.counters)).OrderByDescending(r => r.mask).Take(listBad)) Console.WriteLine($"  uncounted: scene {r.scene}: mask {r.mask}, ssim {r.ssim:F3}; {r.counters}");
        return rows.Count > 0 && unexplained == 0 ? 0 : 1;
    }

    private static uint NearestNes(uint rgb) => (uint)NesFixPalette.Rgb(NesFixPalette.Nearest((int)(rgb >> 16 & 0xFF), (int)(rgb >> 8 & 0xFF), (int)(rgb & 0xFF)));
}
