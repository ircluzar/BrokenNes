namespace BrokenNes.Workshop.MixLab;

/// <summary>A standard 2C02 master palette (RGB), used by the bridges to move colours between families.</summary>
internal static class NesPalette
{
    public static readonly int[] Rgb =
    {
        0x666666,0x002A88,0x1412A7,0x3B00A4,0x5C007E,0x6E0040,0x6C0600,0x561D00,0x333500,0x0B4800,0x005200,0x004F08,0x00404D,0x000000,0x000000,0x000000,
        0xADADAD,0x155FD9,0x4240FF,0x7527FE,0xA01ACC,0xB71E7B,0xB53120,0x994E00,0x6B6D00,0x388700,0x0C9300,0x008F32,0x007C8D,0x000000,0x000000,0x000000,
        0xFFFEFF,0x64B0FF,0x9290FF,0xC676FF,0xF36AFF,0xFE6ECC,0xFE8170,0xEA9E22,0xBCBE00,0x88D800,0x5CE430,0x45E082,0x48CDDE,0x4F4F4F,0x000000,0x000000,
        0xFFFEFF,0xC0DFFF,0xD3D2FF,0xE8C8FF,0xFBC2FF,0xFEC4EA,0xFECCC5,0xF7D8A5,0xE4E594,0xCFEF96,0xBDF4AB,0xB3F3CC,0xB5EBF2,0xB8B8B8,0x000000,0x000000,
    };

    /// <summary>NES colour index -> SNES BGR555.</summary>
    public static ushort ToBgr555(int nesColor)
    {
        int c = Rgb[nesColor & 0x3F];
        int r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
        return (ushort)((r >> 3) | ((g >> 3) << 5) | ((b >> 3) << 10));
    }

    /// <summary>Nearest NES colour index to an RGB triple (skips the duplicate blacks $0D-$0F/$1D-...).</summary>
    public static byte Nearest(int r, int g, int b)
    {
        int best = 0x0F, bestD = int.MaxValue;
        for (int i = 0; i < 64; i++)
        {
            if ((i & 0x0F) >= 0x0E || i == 0x0D) continue;
            int c = Rgb[i];
            int dr = ((c >> 16) & 0xFF) - r, dg = ((c >> 8) & 0xFF) - g, db = (c & 0xFF) - b;
            int d = 3 * dr * dr + 4 * dg * dg + 2 * db * db;
            if (d < bestD) { bestD = d; best = i; }
        }
        if (r + g + b < 24) best = 0x0F;
        return (byte)best;
    }

    public static (int r, int g, int b) FromBgr555(ushort c) =>
        (((c & 31) * 255) / 31, (((c >> 5) & 31) * 255) / 31, (((c >> 10) & 31) * 255) / 31);
}
