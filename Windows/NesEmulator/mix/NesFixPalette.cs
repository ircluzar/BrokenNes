namespace NesEmulator.Mix;

/// <summary>
/// The 2C02 colours as the PPU_FIX family draws them (the same 64 triples as PPU_FIX's own table). <see cref="NesPalette"/> is the textbook palette the older
/// bridges use; the NES chips of this app draw with this one, so a bridge that wants a colour to LOOK like a given RGB on those chips picks the index here, and a bridge
/// that shows a NES picture on another chip converts the NES colours with this table so both pictures start from the same colours.
/// </summary>
internal static class NesFixPalette
{
    private static readonly byte[] Bytes =
    {
        84,84,84, 0,30,116, 8,16,144, 48,0,136,
        68,0,100, 92,0,48, 84,4,0, 60,24,0,
        32,42,0, 8,58,0, 0,64,0, 0,60,0,
        0,50,60, 0,0,0, 0,0,0, 0,0,0,
        152,150,152, 8,76,196, 48,50,236, 92,30,228,
        136,20,176, 160,20,100, 152,34,32, 120,60,0,
        84,90,0, 40,114,0, 8,124,0, 0,118,40,
        0,102,120, 0,0,0, 0,0,0, 0,0,0,
        236,238,236, 76,154,236, 120,124,236, 176,98,236,
        228,84,236, 236,88,180, 236,106,100, 212,136,32,
        160,170,0, 116,196,0, 76,208,32, 56,204,108,
        56,180,204, 60,60,60, 0,0,0, 0,0,0,
        236,238,236, 168,204,236, 188,188,236, 212,178,236,
        236,174,236, 236,174,212, 236,180,176, 228,196,144,
        204,210,120, 180,222,120, 168,226,144, 152,226,180,
        160,214,228, 160,162,160, 0,0,0, 0,0,0,
    };

    /// <summary>The colour of NES palette index <paramref name="i"/> as 0xRRGGBB.</summary>
    public static int Rgb(int i) { i &= 0x3F; return Bytes[i * 3] << 16 | Bytes[i * 3 + 1] << 8 | Bytes[i * 3 + 2]; }

    /// <summary>The usable NES colour (the duplicate blacks above $0D are skipped) that looks nearest to the colour, weighing green most, as the other bridges do.</summary>
    public static byte Nearest(int r, int g, int b)
    {
        int best = 0x0F, bestD = int.MaxValue;
        for (int i = 0; i < 64; i++)
        {
            if ((i & 0x0F) >= 0x0E || i == 0x0D) continue;
            int dr = Bytes[i * 3] - r, dg = Bytes[i * 3 + 1] - g, db = Bytes[i * 3 + 2] - b;
            int d = 3 * dr * dr + 4 * dg * dg + 2 * db * db;
            if (d < bestD) { bestD = d; best = i; }
        }
        if (r + g + b < 24) best = 0x0F;
        return (byte)best;
    }

    /// <summary>The textbook-palette colour (<see cref="NesPalette"/>) of the index that looks nearest to <paramref name="rgb"/> on a PPU_FIX-family chip. Handed to a layer that converts its colours with the textbook palette
    /// (PPU_FIXS's extension does), it lands on exactly that index, so the colour on screen is the FIX-family look of the colour the source drew.</summary>
    public static int ForTextbook(int rgb) => NesPalette.Rgb[Nearest(rgb >> 16 & 0xFF, rgb >> 8 & 0xFF, rgb & 0xFF)];
}
