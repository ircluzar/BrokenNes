using System;
using System.Collections.Generic;

namespace NesEmulator.Mix;

/// <summary>
/// The colour reduction rule of the bridges that put a picture on a Sega chip: a colour (0xRRGGBB) becomes the nearest colour the chip can show, by the weighted distance every bridge of this project
/// uses (3 for red, 4 for green, 2 for blue: <see cref="NesPalette.Nearest"/>). The chips' colours are the ones the boards draw: the Master System's levels 0, 82, 173, 255 a channel (64 colours), the
/// Genesis DAC's even steps 0, 49, 87, 119, 146, 174, 206, 255 (512 colours). (A perceptual space such as L*a*b* was tried and chose paler, yellower colours for the NES's tans: worse to look at and by the
/// measure, so the simple rule stays.)
/// </summary>
internal static class SegaColour
{
    private static readonly int[] SmsLevels = { 0x00, 0x52, 0xAD, 0xFF };
    private static readonly int[] MdLevels = { 0, 49, 87, 119, 146, 174, 206, 255 };
    private static readonly Dictionary<int, int> smsCache = new(), mdCache = new();

    private static int Nearest(int[] levels, int rgb, out int r, out int g, out int b)
    {
        int rr = rgb >> 16 & 0xFF, gg = rgb >> 8 & 0xFF, bb = rgb & 0xFF;
        r = NearestLevel(levels, rr); g = NearestLevel(levels, gg); b = NearestLevel(levels, bb);
        return 0;
    }

    // weighted distance is separable, so the nearest colour is the nearest level of each channel
    private static int NearestLevel(int[] levels, int x)
    {
        int best = 0;
        for (int v = 1; v < levels.Length; v++) if (Math.Abs(levels[v] - x) < Math.Abs(levels[best] - x)) best = v;
        return best;
    }

    /// <summary>The Master System colour (--BBGGRR, 6 bits) nearest to <paramref name="rgb"/>.</summary>
    public static int Sms6(int rgb)
    {
        lock (smsCache)
        {
            if (smsCache.TryGetValue(rgb, out int v)) return v;
            Nearest(SmsLevels, rgb, out int r, out int g, out int b);
            return smsCache[rgb] = b << 4 | g << 2 | r;
        }
    }

    /// <summary>The Genesis colour (0000BBB0GGG0RRR0, 9 bits) nearest to <paramref name="rgb"/>.</summary>
    public static int Md9(int rgb)
    {
        lock (mdCache)
        {
            if (mdCache.TryGetValue(rgb, out int v)) return v;
            Nearest(MdLevels, rgb, out int r, out int g, out int b);
            return mdCache[rgb] = b << 9 | g << 5 | r << 1;
        }
    }
}
