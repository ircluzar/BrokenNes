namespace NesEmulator.Gb;

/// <summary>
/// How another console's colours become Game Boy colours, in one place for every path that does it (the Game Boy picture chips on the NES board, the
/// enhanced chip's cross-console layers, the Game Boy look's inversions on the NES and SNES pictures of Game Boy games):
/// <list type="bullet">
/// <item><see cref="ShadeOf"/>: a colour's DMG shade by its own brightness - bright 0 (the lightest shade) .. dark 3. A picture is "upright" when its bright
/// colours are light on the Game Boy and its dark colours dark, which is what this gives on every path.</item>
/// <item><see cref="FlipLightness"/>: the Game Boy look's inversion on a colour (Game Boy Color pictures): the lightness is mirrored (light becomes dark and back)
/// while the hue and the saturation are kept, so a dark blue sky becomes a light blue one, not the orange of a colour negative. On DMG shades the same
/// inversion is <c>3 - shade</c>.</item>
/// </list>
/// </summary>
public static class GbColour
{
    /// <summary>The luma of a 0xRRGGBB colour (0-255), weighted as the bridges weigh colours (3 red, 6 green, 1 blue).</summary>
    public static int Luma(uint rgb) => ((int)(rgb >> 16 & 0xFF) * 3 + (int)(rgb >> 8 & 0xFF) * 6 + (int)(rgb & 0xFF)) / 10;

    /// <summary>A 0xRRGGBB colour's DMG shade by its brightness: luma 192 and up is shade 0 (lightest), 128 up 1, 64 up 2, below 3 (darkest).</summary>
    public static byte ShadeOf(uint rgb)
    {
        int y = Luma(rgb);
        return (byte)(y >= 192 ? 0 : y >= 128 ? 1 : y >= 64 ? 2 : 3);
    }

    /// <summary>
    /// A 0xRRGGBB colour with its HSL lightness mirrored (L -> 1 - L) and its hue and saturation kept: each channel x becomes x + 255 - (max + min). White and
    /// black swap, a mid-lightness colour (max + min = 255) stays as it is, dark blue becomes light blue. The top byte is kept.
    /// </summary>
    public static uint FlipLightness(uint rgb)
    {
        int r = (int)(rgb >> 16 & 0xFF), g = (int)(rgb >> 8 & 0xFF), b = (int)(rgb & 0xFF);
        int max = r > g ? (r > b ? r : b) : (g > b ? g : b), min = r < g ? (r < b ? r : b) : (g < b ? g : b);
        int d = 255 - (max + min);
        return (rgb & 0xFF000000u) | (uint)((r + d) << 16 | (g + d) << 8 | (b + d));
    }

    /// <summary><see cref="FlipLightness"/> on a BGR555 colour (a CGB / SNES palette entry): each 5-bit channel x becomes x + 31 - (max + min). Bit 15 is cleared.</summary>
    public static ushort FlipLightness555(ushort bgr)
    {
        int r = bgr & 31, g = bgr >> 5 & 31, b = bgr >> 10 & 31;
        int max = r > g ? (r > b ? r : b) : (g > b ? g : b), min = r < g ? (r < b ? r : b) : (g < b ? g : b);
        int d = 31 - (max + min);
        return (ushort)((r + d) | (g + d) << 5 | (b + d) << 10);
    }
}
