using System;
using System.Collections.Generic;
using System.Linq;

namespace NesEmulator.Mix;

/// <summary>
/// Chooses the background tiles of a flat NES picture (a NES chip holds 255 usable tiles of 8 x 8 two-bit pixels): the tile variants a Sega picture needs are ranked by how many cells use them, the most used get a tile of
/// their own, and every other variant is shown as the closest of those (closest by the brightness levels of the 16 2 x 2 blocks of the tile). Better than showing a wrong tile by number, and counted either way.
/// </summary>
internal static class FlatTilePacker
{
    /// <param name="uses">Cells using each variant (key -> count).</param>
    /// <param name="levels">The 64 levels (0 clear, 1-3) of a variant.</param>
    /// <param name="limit">Tiles the chip holds (255).</param>
    /// <param name="kept">The levels of NES tile 1 .. limit (null where unused).</param>
    /// <param name="approximated">Variants shown as another tile.</param>
    /// <returns>The NES tile (1 .. limit) of every variant.</returns>
    public static Dictionary<int, int> Pack(Dictionary<int, int> uses, Func<int, byte[]> levels, int limit, out byte[]?[] kept, out int approximated)
    {
        var keys = uses.Keys.OrderByDescending(k => uses[k]).ThenBy(k => k).ToList();
        var map = new Dictionary<int, int>(keys.Count);
        kept = new byte[]?[limit + 1];
        int n = Math.Min(limit, keys.Count);
        var sig = new int[limit + 1][];
        for (int i = 0; i < n; i++) { int t = i + 1; kept[t] = levels(keys[i]); sig[t] = Signature(kept[t]!); map[keys[i]] = t; }
        approximated = keys.Count - n;
        for (int i = n; i < keys.Count; i++)
        {
            var s = Signature(levels(keys[i]));
            int best = 1; long bd = long.MaxValue;
            for (int t = 1; t <= n; t++)
            {
                long d = 0; var o = sig[t];
                for (int k = 0; k < 16 && d < bd; k++) d += Math.Abs(o[k] - s[k]);
                if (d < bd) { bd = d; best = t; }
            }
            map[keys[i]] = best;
        }
        return map;
    }

    private static int[] Signature(byte[] lv)
    {
        var s = new int[16];
        for (int by = 0; by < 4; by++) for (int bx = 0; bx < 4; bx++)
            s[by * 4 + bx] = lv[(by * 2) * 8 + bx * 2] + lv[(by * 2) * 8 + bx * 2 + 1] + lv[(by * 2 + 1) * 8 + bx * 2] + lv[(by * 2 + 1) * 8 + bx * 2 + 1];
        return s;
    }

    /// <summary>The two bitplane bytes of row <paramref name="y"/> of a tile of levels.</summary>
    public static (byte p0, byte p1) Row(byte[] lv, int y)
    {
        byte p0 = 0, p1 = 0;
        for (int x = 0; x < 8; x++) { int v = lv[y * 8 + x]; p0 |= (byte)((v & 1) << (7 - x)); p1 |= (byte)((v >> 1 & 1) << (7 - x)); }
        return (p0, p1);
    }
}
