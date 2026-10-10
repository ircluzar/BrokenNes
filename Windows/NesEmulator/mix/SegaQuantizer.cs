using System;
using System.Collections.Generic;

namespace NesEmulator.Mix;

/// <summary>
/// Chooses the palettes of a 2-bit-tile picture chip for a Sega picture. Items (tiles, each with a histogram of the colours its pixels really have) are grouped into a few palettes of three colours, and every
/// item draws with its group's palette (its pixels take the nearest of the three, levels 1-3 running dark to bright, level 0 staying see-through). Unlike <see cref="SnesToNes.Quantizer"/> (means of colours, a fresh
/// k-means every frame) this one is built for pictures whose palette is small and exact:
/// <list type="bullet">
/// <item>a palette's colours are chosen <b>among the colours the pictures really use</b> (the best weighted 3-medoid of the group's pooled histogram, found exhaustively: a Sega frame has at most a few dozen
/// distinct colours), so a colour the source chip drew is reproduced as that colour, never as a blend of two;</item>
/// <item>the solution is <b>warm-started from the frame before</b>: the same tiles on the same palettes give the same palettes, so colours do not shimmer as a scroll reveals new tiles.</item>
/// </list>
/// </summary>
internal sealed class SegaQuantizer
{
    private readonly List<Dictionary<int, int>> hist = new();
    private readonly Dictionary<long, int> index = new();
    private int[] prev = Array.Empty<int>();            // last frame's palettes, 3 colours each, flat
    private int prevGroups;
    private readonly List<byte[]> levelOf = new();      // per item: level of each distinct colour (indexed through colourIndex)
    private readonly Dictionary<int, int> colourIndex = new();
    private int[] colours = Array.Empty<int>();

    public int Count => hist.Count;
    public int[] Group = Array.Empty<int>();
    /// <summary>Palette [group][level 1..3] as 0xRRGGBB (level 0 unused); levels run from dark to bright.</summary>
    public int[][] Palette = Array.Empty<int[]>();

    /// <summary>Forget the items (the palettes of the last solve are kept as the next one's starting point).</summary>
    public void BeginFrame() { hist.Clear(); index.Clear(); levelOf.Clear(); colourIndex.Clear(); Group = Array.Empty<int>(); }

    public int Add(long key, Action<Dictionary<int, int>> fill)
    {
        if (index.TryGetValue(key, out int i)) return i;
        var h = new Dictionary<int, int>(); fill(h);
        index[key] = hist.Count; hist.Add(h);
        return hist.Count - 1;
    }

    // weighted distance of the whole project (3 red, 4 green, 2 blue)
    private static int D(int a, int b)
    {
        int dr = (a >> 16 & 0xFF) - (b >> 16 & 0xFF), dg = (a >> 8 & 0xFF) - (b >> 8 & 0xFF), db = (a & 0xFF) - (b & 0xFF);
        return 3 * dr * dr + 4 * dg * dg + 2 * db * db;
    }

    private static int Lum(int c) => 3 * (c >> 16 & 0xFF) + 6 * (c >> 8 & 0xFF) + (c & 0xFF);

    /// <summary>The level (1-3) of <paramref name="rgb"/> in the item's palette (1 for a colour the item does not have).</summary>
    public byte Level(int item, int rgb)
    {
        var P = Palette[Group[item]];
        int best = 1, bd = int.MaxValue;
        for (int l = 1; l <= 3; l++) { int d = D(rgb, P[l]); if (d < bd) { bd = d; best = l; } }
        return (byte)best;
    }

    /// <summary>The error (weighted squared distance over the item's pixels) of drawing the item with palette <paramref name="g"/>.</summary>
    public long Cost(int item, int g)
    {
        var P = Palette[g]; long e = 0;
        foreach (var kv in hist[item]) { int bd = int.MaxValue; for (int l = 1; l <= 3; l++) bd = Math.Min(bd, D(kv.Key, P[l])); e += (long)bd * kv.Value; }
        return e;
    }

    private long CostOf(int item, int a, int b, int c)
    {
        long e = 0;
        foreach (var kv in hist[item]) { int bd = Math.Min(D(kv.Key, a), Math.Min(D(kv.Key, b), D(kv.Key, c))); e += (long)bd * kv.Value; }
        return e;
    }

    /// <summary>The best three colours (weighted 3-medoid) for pooled colour weights, found exhaustively over the colours present.</summary>
    private static (int a, int b, int c) BestTriple(List<(int colour, long weight)> pool)
    {
        int n = pool.Count;
        if (n == 0) return (0x000000, 0x808080, 0xFFFFFF);
        if (n == 1) return (pool[0].colour, pool[0].colour, pool[0].colour);
        var cs = new int[n]; var ws = new long[n];
        for (int i = 0; i < n; i++) { cs[i] = pool[i].colour; ws[i] = pool[i].weight; }
        // precompute distances
        var d = new int[n * n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) d[i * n + j] = D(cs[i], cs[j]);
        long best = long.MaxValue; (int, int, int) pick = (0, 0, 0);
        if (n == 2) return (cs[0], cs[1], cs[1]);
        for (int a = 0; a < n; a++)
            for (int b = a + 1; b < n; b++)
                for (int c = b + 1; c < n; c++)
                {
                    long e = 0;
                    for (int k = 0; k < n && e < best; k++) { int m = Math.Min(d[k * n + a], Math.Min(d[k * n + b], d[k * n + c])); e += ws[k] * m; }
                    if (e < best) { best = e; pick = (a, b, c); }
                }
        return (cs[pick.Item1], cs[pick.Item2], cs[pick.Item3]);
    }

    private (int a, int b, int c) BestTripleOf(IEnumerable<int> items)
    {
        var pool = new Dictionary<int, long>();
        foreach (int i in items) foreach (var kv in hist[i]) pool[kv.Key] = pool.GetValueOrDefault(kv.Key) + kv.Value;
        var list = new List<(int, long)>(pool.Count);
        foreach (var kv in pool) list.Add((kv.Key, kv.Value));
        // a very large pool (a photographic picture) is cut to its most used colours so the exhaustive search stays quick
        if (list.Count > 24) { list.Sort((x, y) => y.Item2.CompareTo(x.Item2)); list.RemoveRange(24, list.Count - 24); }
        return BestTriple(list);
    }

    private void SetPalette(int g, (int a, int b, int c) t)
    {
        var s = new[] { t.a, t.b, t.c }; Array.Sort(s, (x, y) => Lum(x).CompareTo(Lum(y)));
        Palette[g] = new[] { 0, s[0], s[1], s[2] };
    }

    /// <summary>Group the items into at most <paramref name="groups"/> palettes.</summary>
    public void Solve(int groups)
    {
        int n = hist.Count;
        Group = new int[n];
        Palette = new int[groups][];
        for (int g = 0; g < groups; g++) Palette[g] = new[] { 0, 0x505050, 0xA0A0A0, 0xF0F0F0 };
        if (n == 0) return;
        int k = Math.Min(groups, n);
        if (prevGroups == groups && prev.Length == groups * 3)
        {
            for (int g = 0; g < groups; g++) SetPalette(g, (prev[g * 3], prev[g * 3 + 1], prev[g * 3 + 2]));
        }
        else
        {
            SetPalette(0, BestTripleOf(Range(n)));
            var err = new long[n];
            for (int g = 1; g < k; g++)
            {
                // seed the next palette from the item the existing ones draw worst (weighted by its pixels)
                int worst = 0; long worstE = -1;
                for (int i = 0; i < n; i++)
                {
                    long e = long.MaxValue; for (int q = 0; q < g; q++) e = Math.Min(e, Cost(i, q));
                    err[i] = e;
                    if (e > worstE) { worstE = e; worst = i; }
                }
                if (worstE <= 0) { for (int q = g; q < groups; q++) Palette[q] = Palette[0]; k = g; break; }
                SetPalette(g, BestTripleOf(new[] { worst }));
            }
            for (int g = k; g < groups; g++) Palette[g] = Palette[0];
        }
        for (int round = 0; round < 3; round++)
        {
            // assign every item to the palette that draws it with the least error
            var members = new List<int>[groups];
            for (int g = 0; g < groups; g++) members[g] = new List<int>();
            for (int i = 0; i < n; i++)
            {
                int best = 0; long be = long.MaxValue;
                for (int g = 0; g < groups; g++) { long e = Cost(i, g); if (e < be) { be = e; best = g; } }
                Group[i] = best; members[best].Add(i);
            }
            // each palette's colours: the best triple for its members (an empty one keeps what it had)
            for (int g = 0; g < groups; g++) if (members[g].Count > 0) SetPalette(g, BestTripleOf(members[g]));
        }
        {
            for (int i = 0; i < n; i++)
            {
                int best = 0; long be = long.MaxValue;
                for (int g = 0; g < groups; g++) { long e = Cost(i, g); if (e < be) { be = e; best = g; } }
                Group[i] = best;
            }
        }
        prev = new int[groups * 3]; prevGroups = groups;
        for (int g = 0; g < groups; g++) { prev[g * 3] = Palette[g][1]; prev[g * 3 + 1] = Palette[g][2]; prev[g * 3 + 2] = Palette[g][3]; }
    }

    private static IEnumerable<int> Range(int n) { for (int i = 0; i < n; i++) yield return i; }

    /// <summary>Total error of the last solution and the number of pixels it covers (diagnosis).</summary>
    public (long error, long pixels) Quality()
    {
        long e = 0, p = 0;
        for (int i = 0; i < hist.Count; i++) { e += Cost(i, Group[i]); foreach (var v in hist[i].Values) p += v; }
        return (e, p);
    }
}
