using System;
using System.Collections.Generic;

namespace NesEmulator.Mix;

/// <summary>Whoever knows the pixels of a tile variant: fills its 64 levels (0 clear, 1-3).</summary>
internal interface IVariantLevels
{
    /// <summary>Write the 64 levels of the variant <paramref name="variantKey"/> into <paramref name="levels"/> (row by row).</summary>
    void FillLevels(int variantKey, byte[] levels);
}

/// <summary>
/// Chooses the background tiles of a flat NES picture (a NES chip holds 255 usable tiles of 8 x 8 two-bit pixels): the tile variants a Sega picture needs are ranked by how many cells use them, the most used get a tile of
/// their own, and every other variant is shown as the closest of those (closest by the brightness levels of the 16 2 x 2 blocks of the tile). Better than showing a wrong tile by number, and counted either way.
/// One packer is kept per bridge: its tables are made once and cleared for each frame, so packing allocates nothing once warm.
/// </summary>
internal sealed class FlatTilePacker
{
    private readonly Dictionary<int, int> map = new();
    private long[] order = new long[256];
    private byte[]?[] kept = new byte[]?[256];
    private int[]?[] sig = new int[]?[256];
    private readonly byte[] scratch = new byte[64];
    private readonly int[] scratchSig = new int[16];
    private int keptCount;

    /// <summary>The levels of NES tile <paramref name="t"/> (1 .. limit) of the last <see cref="Pack"/>; null where the tile is unused.</summary>
    public byte[]? Kept(int t) => t >= 1 && t <= keptCount ? kept[t] : null;

    /// <param name="uses">Cells using each variant (key -> count). Keys are non-negative.</param>
    /// <param name="source">Knows the 64 levels (0 clear, 1-3) of a variant.</param>
    /// <param name="limit">Tiles the chip holds (255).</param>
    /// <param name="approximated">Variants shown as another tile.</param>
    /// <returns>The NES tile (1 .. limit) of every variant (valid until the next call).</returns>
    public Dictionary<int, int> Pack(Dictionary<int, int> uses, IVariantLevels source, int limit, out int approximated)
    {
        if (kept.Length < limit + 1) { Array.Resize(ref kept, limit + 1); Array.Resize(ref sig, limit + 1); }
        // most used first, then by key: one number per variant sorts as that pair (count high to low in the upper half, key in the lower)
        if (order.Length < uses.Count) order = new long[Math.Max(uses.Count, order.Length * 2)];
        int total = 0;
        foreach (var kv in uses) order[total++] = ((long)(int.MaxValue - kv.Value) << 32) | (uint)kv.Key;
        Array.Sort(order, 0, total);
        map.Clear();
        int n = Math.Min(limit, total);
        keptCount = n;
        for (int i = 0; i < n; i++)
        {
            int t = i + 1, key = (int)(order[i] & 0xFFFFFFFF);
            var lv = kept[t] ??= new byte[64];
            source.FillLevels(key, lv);
            Signature(lv, sig[t] ??= new int[16]);
            map[key] = t;
        }
        approximated = total - n;
        for (int i = n; i < total; i++)
        {
            int key = (int)(order[i] & 0xFFFFFFFF);
            source.FillLevels(key, scratch);
            Signature(scratch, scratchSig);
            var s = scratchSig;
            int best = 1; long bd = long.MaxValue;
            for (int t = 1; t <= n; t++)
            {
                long d = 0; var o = sig[t]!;
                for (int k = 0; k < 16 && d < bd; k++) d += Math.Abs(o[k] - s[k]);
                if (d < bd) { bd = d; best = t; }
            }
            map[key] = best;
        }
        return map;
    }

    private static void Signature(byte[] lv, int[] s)
    {
        for (int by = 0; by < 4; by++) for (int bx = 0; bx < 4; bx++)
            s[by * 4 + bx] = lv[(by * 2) * 8 + bx * 2] + lv[(by * 2) * 8 + bx * 2 + 1] + lv[(by * 2 + 1) * 8 + bx * 2] + lv[(by * 2 + 1) * 8 + bx * 2 + 1];
    }

    /// <summary>The two bitplane bytes of row <paramref name="y"/> of a tile of levels.</summary>
    public static (byte p0, byte p1) Row(byte[] lv, int y)
    {
        byte p0 = 0, p1 = 0;
        for (int x = 0; x < 8; x++) { int v = lv[y * 8 + x]; p0 |= (byte)((v & 1) << (7 - x)); p1 |= (byte)((v >> 1 & 1) << (7 - x)); }
        return (p0, p1);
    }
}
