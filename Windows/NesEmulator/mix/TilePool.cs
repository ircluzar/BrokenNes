using System;
using System.Collections.Generic;

namespace NesEmulator.Mix;

/// <summary>
/// Hands out the tile slots of a Sega VDP's video memory to the tiles a NES frame needs, keeping a tile in its slot from frame to frame (so an unchanged picture uploads nothing) and
/// taking back the slots that have gone longest unused when it runs short. A tile is named by a 64-bit key the caller builds (pattern-table version, tile, palette, flips ...).
/// </summary>
internal sealed class TilePool
{
    private readonly int first, count;
    private readonly int[] owner;        // per slot: the first slot of the tile that holds it, or -1 when free
    private readonly int[] spanOf;       // per first slot: how many slots the tile takes
    private readonly long[] keyOf;       // per first slot: the tile's name
    private readonly long[] lastUsed;    // per first slot: the last frame that asked for the tile
    private readonly Dictionary<long, int> map = new();
    private long frame;

    /// <param name="first">The lowest slot this pool hands out.</param>
    /// <param name="count">How many slots (first .. first + count - 1).</param>
    public TilePool(int first, int count)
    {
        this.first = first; this.count = count;
        owner = new int[count]; spanOf = new int[count]; keyOf = new long[count]; lastUsed = new long[count];
        Array.Fill(owner, -1);
    }

    public int Capacity => count;
    /// <summary>Tiles asked for this frame.</summary>
    public int InUse { get; private set; }

    /// <summary>A new frame begins: tiles not asked for from now on can lose their slots.</summary>
    public void BeginFrame() { frame++; InUse = 0; }

    /// <summary>Forget everything (the video memory was cleared).</summary>
    public void Clear() { map.Clear(); Array.Fill(owner, -1); Array.Clear(lastUsed); InUse = 0; }

    /// <summary>The slot already holding the tile named <paramref name="k"/> (marked used this frame), or -1.</summary>
    public int Find(long k)
    {
        if (!map.TryGetValue(k, out int c0)) return -1;
        if (lastUsed[c0] != frame) { lastUsed[c0] = frame; InUse++; }
        return first + c0;
    }

    /// <summary>
    /// The slot of the tile named <paramref name="k"/>, found or newly given; <paramref name="isNew"/> is true when it was just given a slot (the caller writes the tile's data). A new tile that needs
    /// <paramref name="span"/> consecutive slots (the two halves of an 8x16 sprite) gets the first of them, which is a multiple of <paramref name="align"/> (2: an even slot); the slots lie in [lo, hi).
    /// Free slots are used first, then the ones unused for longest; none of those used this frame is ever taken. Returns -1 when nothing fits.
    /// </summary>
    public int Get(long k, out bool isNew, int span = 1, int align = 1, int lo = int.MinValue, int hi = int.MaxValue)
    {
        isNew = false;
        int found = Find(k);
        if (found >= 0) return found;
        return Alloc(k, out isNew, span, align, lo, hi);
    }

    /// <summary>Give the tile a new slot in [lo, hi) (see <see cref="Get"/>); the tile must not hold one.</summary>
    public int Alloc(long k, out bool isNew, int span = 1, int align = 1, int lo = int.MinValue, int hi = int.MaxValue)
    {
        isNew = false;
        int best = -1; long bestAge = long.MaxValue; bool bestFree = false;
        for (int c = 0; c + span <= count; c++)
        {
            int abs = first + c;
            if (abs < lo || abs + span > hi || abs % align != 0) continue;
            bool free = true, usable = true; long age = long.MaxValue;
            for (int s = 0; s < span && usable; s++)
            {
                int o = owner[c + s];
                if (o < 0) continue;
                free = false;
                if (lastUsed[o] >= frame) usable = false;
                else age = Math.Min(age, lastUsed[o]);
            }
            if (!usable) continue;
            if (free) { best = c; bestFree = true; break; }
            if (!bestFree && age < bestAge) { bestAge = age; best = c; }
        }
        if (best < 0) return -1;
        for (int s = 0; s < span; s++) if (owner[best + s] >= 0) Release(owner[best + s]);
        for (int s = 0; s < span; s++) owner[best + s] = best;
        spanOf[best] = span; keyOf[best] = k; lastUsed[best] = frame; InUse++;
        map[k] = best;
        isNew = true;
        return first + best;
    }

    private void Release(int head)
    {
        map.Remove(keyOf[head]);
        for (int s = 0; s < spanOf[head]; s++) if (owner[head + s] == head) owner[head + s] = -1;
    }
}
