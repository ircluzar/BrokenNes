using System;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// The fidelity metric of the picture bridges: how close a bridged picture is to the picture the source chip drew itself, over a region of the canvas. Pictures are 0xAARRGGBB.
/// <list type="bullet">
/// <item><b>colour error</b>: the mean per-pixel colour distance, sqrt((dr^2 + dg^2 + db^2) / 3) as a percentage of full scale (0 identical, about 44 for a black picture against a white one is 100; random pictures sit near 30-40).</item>
/// <item><b>block error</b>: the same distance between the average colours of 16 x 16 blocks - blind to dithering and to the pixel-level losses of the 3-colour tiles, it asks only whether each part of the picture has about the right colour.</item>
/// <item><b>structure</b> (SSIM): the structural similarity of the luma channel over 8 x 8 windows every 4 pixels (1 identical; below about 0.5 the picture is not recognisably the same).</item>
/// <item><b>match</b>: the share of pixels within 6 % of each other.</item>
/// </list>
/// </summary>
internal static class PictureMetrics
{
    public readonly record struct Result(double ColourError, double BlockError, double Ssim, double Match)
    {
        public override string ToString() => $"colour {ColourError:F1}% block {BlockError:F1}% ssim {Ssim:F3} match {Match * 100:F0}%";
        /// <summary>The bridge is "recognisably the same picture": structure above 0.55 and block error under 12 %.</summary>
        public bool Recognisable => Ssim >= 0.55 && BlockError <= 12;
    }

    private static double Luma(uint c) => 0.299 * (c >> 16 & 0xFF) + 0.587 * (c >> 8 & 0xFF) + 0.114 * (c & 0xFF);

    private static double Dist(uint a, uint b)
    {
        double dr = (int)(a >> 16 & 0xFF) - (int)(b >> 16 & 0xFF), dg = (int)(a >> 8 & 0xFF) - (int)(b >> 8 & 0xFF), db = (int)(a & 0xFF) - (int)(b & 0xFF);
        return Math.Sqrt((dr * dr + dg * dg + db * db) / 3.0) / 255.0 * 100.0;
    }

    /// <summary>Compare two pictures of <paramref name="stride"/> pixels a row over the rectangle (x, y, w, h).</summary>
    public static Result Compare(uint[] a, uint[] b, int stride, int x0, int y0, int w, int h)
    {
        double sum = 0; long match = 0, n = 0;
        for (int y = y0; y < y0 + h; y++)
            for (int x = x0; x < x0 + w; x++)
            {
                double d = Dist(a[y * stride + x], b[y * stride + x]);
                sum += d; n++; if (d <= 6.0) match++;
            }
        // 16x16 blocks
        double bsum = 0; int bn = 0;
        for (int by = y0; by + 16 <= y0 + h; by += 16)
            for (int bx = x0; bx + 16 <= x0 + w; bx += 16)
            {
                double ar = 0, ag = 0, ab = 0, br = 0, bg = 0, bb = 0;
                for (int y = by; y < by + 16; y++)
                    for (int x = bx; x < bx + 16; x++)
                    {
                        uint p = a[y * stride + x], q = b[y * stride + x];
                        ar += p >> 16 & 0xFF; ag += p >> 8 & 0xFF; ab += p & 0xFF; br += q >> 16 & 0xFF; bg += q >> 8 & 0xFF; bb += q & 0xFF;
                    }
                double dr = (ar - br) / 256, dg = (ag - bg) / 256, db = (ab - bb) / 256;
                bsum += Math.Sqrt((dr * dr + dg * dg + db * db) / 3.0) / 255.0 * 100.0; bn++;
            }
        // SSIM on luma, 8x8 windows every 4 pixels
        const double C1 = (0.01 * 255) * (0.01 * 255), C2 = (0.03 * 255) * (0.03 * 255);
        double ssum = 0; int sn = 0;
        for (int wy = y0; wy + 8 <= y0 + h; wy += 4)
            for (int wx = x0; wx + 8 <= x0 + w; wx += 4)
            {
                double mx = 0, my = 0, sxx = 0, syy = 0, sxy = 0;
                for (int y = wy; y < wy + 8; y++)
                    for (int x = wx; x < wx + 8; x++)
                    {
                        double p = Luma(a[y * stride + x]), q = Luma(b[y * stride + x]);
                        mx += p; my += q; sxx += p * p; syy += q * q; sxy += p * q;
                    }
                mx /= 64; my /= 64;
                double vx = sxx / 64 - mx * mx, vy = syy / 64 - my * my, cxy = sxy / 64 - mx * my;
                ssum += (2 * mx * my + C1) * (2 * cxy + C2) / ((mx * mx + my * my + C1) * (vx + vy + C2)); sn++;
            }
        return new Result(n == 0 ? 0 : sum / n, bn == 0 ? 0 : bsum / bn, sn == 0 ? 1 : ssum / sn, n == 0 ? 1 : (double)match / n);
    }
}
