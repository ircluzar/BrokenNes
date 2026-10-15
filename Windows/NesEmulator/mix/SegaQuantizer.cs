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
/// Everything it needs for a frame is kept and cleared, not made again: once warm it allocates nothing (the picture bridges are measured for that by <c>--picture-bridge-test --only alloc</c>).
/// </summary>
internal sealed class SegaQuantizer
{
    private readonly List<Dictionary<int, int>> histPool = new();     // the histograms of the items in use are the first <c>count</c>; the rest wait to be used again
    private int count;
    private readonly Dictionary<long, int> index = new();
    private int[] prev = Array.Empty<int>();            // last frame's palettes, 3 colours each, flat
    private int prevGroups;

    public int Count => count;
    /// <summary>The palette group of each item, valid after <see cref="Solve"/> for items 0 .. <see cref="Count"/> - 1 (the array may be longer).</summary>
    public int[] Group = Array.Empty<int>();
    /// <summary>Palette [group][level 1..3] as 0xRRGGBB (level 0 unused); levels run from dark to bright.</summary>
    public int[][] Palette = Array.Empty<int[]>();

    /// <summary>Forget the items (the palettes of the last solve are kept as the next one's starting point).</summary>
    public void BeginFrame() { count = 0; index.Clear(); }

    /// <summary>The item for <paramref name="key"/>. When the key is new, <paramref name="fresh"/> is the item's empty histogram (colour -> pixels) for the caller to fill; otherwise it is null.</summary>
    public int Add(long key, out Dictionary<int, int>? fresh)
    {
        if (index.TryGetValue(key, out int i)) { fresh = null; return i; }
        Dictionary<int, int> h;
        if (count < histPool.Count) { h = histPool[count]; h.Clear(); } else { h = new Dictionary<int, int>(); histPool.Add(h); }
        index[key] = count;
        fresh = h;
        return count++;
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
        foreach (var kv in histPool[item]) { int bd = int.MaxValue; for (int l = 1; l <= 3; l++) bd = Math.Min(bd, D(kv.Key, P[l])); e += (long)bd * kv.Value; }
        return e;
    }

    // ---- the best three colours for a set of items (a weighted 3-medoid), made without allocating
    private readonly Dictionary<int, long> poolDict = new();
    private readonly List<(int colour, long weight)> poolList = new();
    private readonly int[] csBuf = new int[24], dBuf = new int[24 * 24];
    private readonly long[] wsBuf = new long[24];
    private static readonly Comparison<(int colour, long weight)> HeaviestFirst = static (x, y) => y.weight.CompareTo(x.weight);
    private static readonly Comparison<int> DarkestFirst = static (x, y) => Lum(x).CompareTo(Lum(y));
    private readonly int[] sortBuf = new int[3];

    private void Pool(int item) { foreach (var kv in histPool[item]) poolDict[kv.Key] = poolDict.GetValueOrDefault(kv.Key) + kv.Value; }

    /// <summary>The best three colours (weighted 3-medoid) of what <c>Pool</c> gathered, found exhaustively over the colours present.</summary>
    private (int a, int b, int c) PooledTriple()
    {
        poolList.Clear();
        foreach (var kv in poolDict) poolList.Add((kv.Key, kv.Value));
        poolDict.Clear();
        // a very large pool (a photographic picture) is cut to its most used colours so the exhaustive search stays quick
        if (poolList.Count > 24) { poolList.Sort(HeaviestFirst); poolList.RemoveRange(24, poolList.Count - 24); }
        int n = poolList.Count;
        if (n == 0) return (0x000000, 0x808080, 0xFFFFFF);
        if (n == 1) return (poolList[0].colour, poolList[0].colour, poolList[0].colour);
        var cs = csBuf; var ws = wsBuf;
        for (int i = 0; i < n; i++) { cs[i] = poolList[i].colour; ws[i] = poolList[i].weight; }
        // precompute distances
        var d = dBuf;
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

    private (int a, int b, int c) TripleOfAll(int n) { for (int i = 0; i < n; i++) Pool(i); return PooledTriple(); }
    private (int a, int b, int c) TripleOfOne(int item) { Pool(item); return PooledTriple(); }
    private (int a, int b, int c) TripleOf(List<int> items) { foreach (int i in items) Pool(i); return PooledTriple(); }

    private void SetPalette(int g, (int a, int b, int c) t)
    {
        var s = sortBuf; s[0] = t.a; s[1] = t.b; s[2] = t.c; Array.Sort(s, DarkestFirst);
        var p = Palette[g]; p[0] = 0; p[1] = s[0]; p[2] = s[1]; p[3] = s[2];
    }

    private void CopyPalette(int to, int from) { Array.Copy(Palette[from], Palette[to], 4); }

    private List<int>[] members = new List<int>[16];

    /// <summary>Group the items into at most <paramref name="groups"/> palettes.</summary>
    public void Solve(int groups)
    {
        int n = count;
        if (members.Length < groups) Array.Resize(ref members, groups);
        if (Group.Length < n) Group = new int[Math.Max(n, Group.Length * 2)];
        Array.Clear(Group, 0, n);
        if (Palette.Length != groups)
        {
            Palette = new int[groups][];
            for (int g = 0; g < groups; g++) Palette[g] = new int[4];
        }
        for (int g = 0; g < groups; g++) { var p = Palette[g]; p[0] = 0; p[1] = 0x505050; p[2] = 0xA0A0A0; p[3] = 0xF0F0F0; }
        if (n == 0) return;
        int k = Math.Min(groups, n);
        if (prevGroups == groups && prev.Length == groups * 3)
        {
            for (int g = 0; g < groups; g++) SetPalette(g, (prev[g * 3], prev[g * 3 + 1], prev[g * 3 + 2]));
        }
        else
        {
            SetPalette(0, TripleOfAll(n));
            for (int g = 1; g < k; g++)
            {
                // seed the next palette from the item the existing ones draw worst (weighted by its pixels)
                int worst = 0; long worstE = -1;
                for (int i = 0; i < n; i++)
                {
                    long e = long.MaxValue; for (int q = 0; q < g; q++) e = Math.Min(e, Cost(i, q));
                    if (e > worstE) { worstE = e; worst = i; }
                }
                if (worstE <= 0) { for (int q = g; q < groups; q++) CopyPalette(q, 0); k = g; break; }
                SetPalette(g, TripleOfOne(worst));
            }
            for (int g = k; g < groups; g++) CopyPalette(g, 0);
        }
        for (int round = 0; round < 3; round++)
        {
            // assign every item to the palette that draws it with the least error
            for (int g = 0; g < groups; g++) { members[g] ??= new List<int>(); members[g].Clear(); }
            for (int i = 0; i < n; i++)
            {
                int best = 0; long be = long.MaxValue;
                for (int g = 0; g < groups; g++) { long e = Cost(i, g); if (e < be) { be = e; best = g; } }
                Group[i] = best; members[best].Add(i);
            }
            // each palette's colours: the best triple for its members (an empty one keeps what it had)
            for (int g = 0; g < groups; g++) if (members[g].Count > 0) SetPalette(g, TripleOf(members[g]));
        }
        {
            for (int i = 0; i < n; i++)
            {
                int best = 0; long be = long.MaxValue;
                for (int g = 0; g < groups; g++) { long e = Cost(i, g); if (e < be) { be = e; best = g; } }
                Group[i] = best;
            }
        }
        if (prev.Length != groups * 3) prev = new int[groups * 3];
        prevGroups = groups;
        for (int g = 0; g < groups; g++) { prev[g * 3] = Palette[g][1]; prev[g * 3 + 1] = Palette[g][2]; prev[g * 3 + 2] = Palette[g][3]; }
    }

    /// <summary>Total error of the last solution and the number of pixels it covers (diagnosis).</summary>
    public (long error, long pixels) Quality()
    {
        long e = 0, p = 0;
        for (int i = 0; i < count; i++) { e += Cost(i, Group[i]); foreach (var v in histPool[i].Values) p += v; }
        return (e, p);
    }
}
