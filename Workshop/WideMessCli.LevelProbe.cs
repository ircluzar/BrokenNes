using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes.Workshop;

internal static partial class WideMessCli
{
    /// <summary>
    /// <c>--wms-level-probe --game KEY --at F1,F2 [--blocks 7F0002] [--width 7E07A5] [--tiles 7EA000]</c>: research for a level source (is the level the game shows kept in work RAM as
    /// 16 x 16 blocks of 2 x 2 map entries?). At each moment: BG1's visible map entries are searched for in the blocks decoded through the tile table (block word: bits 0-9 the
    /// block's tile-table index, bit 10 flip x, bit 11 flip y; tile table 8 bytes a block: top-left, top-right, bottom-left, bottom-right). Prints the alignment that matches and
    /// how much of the visible map it explains.
    /// </summary>
    public static int LevelProbe(string[] args)
    {
        RomTestCli.EnsureConsole();
        var g = GameArgs(args);
        if (g == null) return 2;
        var (rom, script, moments, name) = g.Value;
        int blocksAt = Convert.ToInt32(Opt(args, "blocks", "7F0002"), 16) - 0x7E0000, widthAt = Convert.ToInt32(Opt(args, "width", "7E07A5"), 16) - 0x7E0000;
        int tilesAt = Convert.ToInt32(Opt(args, "tiles", "7EA000"), 16) - 0x7E0000;
        using var s = EnhancedSession(rom, args, out _);
        if (s == null) return 1;
        var ppu = s.WideMessPpu!;
        var pad = ParsePad(script);
        int at = 0; PadButtons held = 0;
        var wram = s.Board.Wram;
        foreach (int m in moments)
        {
            PlayTo(s, pad, ref at, m, ref held);
            var r = ppu.DebugLineRegisters![1];
            int width = wram[widthAt] | wram[widthAt + 1] << 8;
            int sc = r.Bg1sc, mapBase = (sc & 0xFC) << 8; bool wide = (sc & 1) != 0, tall = (sc & 2) != 0;
            int hofs = r.Bg1hofs & 0x3FF, vofs = r.Bg1vofs & 0x3FF;
            ushort Map(int col, int row)
            {
                int a = mapBase + ((row & 31) << 5) + (col & 31);
                if (wide && (col & 32) != 0) a += 0x400;
                if (tall && (row & 32) != 0) a += wide ? 0x800 : 0x400;
                return ppu.Vram[a & 0x7FFF];
            }
            ushort Decode(int bx, int by, int qx, int qy)
            {
                int i = blocksAt + 2 * (by * width + bx);
                if (i < 0 || i + 1 >= wram.Length) return 0xFFFF;
                int w = wram[i] | wram[i + 1] << 8;
                bool fx = (w & 0x400) != 0, fy = (w & 0x800) != 0;
                int k = (fy ? 1 - qy : qy) * 2 + (fx ? 1 - qx : qx);
                int t = tilesAt + (w & 0x3FF) * 8 + k * 2;
                int e = wram[t] | wram[t + 1] << 8;
                if (fx) e ^= 0x4000;
                if (fy) e ^= 0x8000;
                return (ushort)e;
            }
            // the visible tiles: map tile (c0 + i, r0 + j), 33 x 29
            int c0 = hofs >> 3, r0 = (vofs + 1) >> 3;
            int height = width == 0 ? 0 : Math.Min(256, (0x6400 - 2) / 2 / width);
            int best = -1, bestTx = 0, bestTy = 0, total = 0;
            for (int ty = 0; ty < height * 2; ty++)
                for (int tx = 0; tx < width * 2; tx++)
                {
                    // world tile (tx, ty) is shown at map tile (c0, r0)?
                    if (Decode(tx >> 1, ty >> 1, tx & 1, ty & 1) != Map(c0, r0)) continue;
                    int n = 0, all = 0;
                    for (int j = 0; j < 28; j += 3)
                        for (int i = 0; i < 32; i += 3)
                        {
                            int wx = tx + i, wy = ty + j; all++;
                            if (wx >> 1 < width && Decode(wx >> 1, wy >> 1, wx & 1, wy & 1) == Map(c0 + i, r0 + j)) n++;
                        }
                    total = all;
                    if (n > best) { best = n; bestTx = tx; bestTy = ty; }
                }
            Console.WriteLine($"  frame {m}: BG1 HOFS {hofs} VOFS {vofs} SC {sc:X2}, room width {width} blocks; best: world tile ({bestTx}, {bestTy}) at map tile ({c0}, {r0}), {best} of {total} sampled tiles match");
            if (!args.Contains("--find", StringComparer.OrdinalIgnoreCase)) continue;
            // the generic search: is there a block table in work RAM (8 bytes: the four entries of a 2 x 2 group of the picture), at either block alignment?
            foreach (int ax in new[] { 0, 1 })
                foreach (int ay in new[] { 0, 1 })
                {
                    int groups = 0, found = 0; var residues = new int[8]; var regions = new SortedDictionary<int, int>();
                    for (int j = ay; j + 1 < 28; j += 2)
                        for (int i = ax; i + 1 < 32; i += 2)
                        {
                            ushort tl = Map(c0 + i, r0 + j), tr = Map(c0 + i + 1, r0 + j), bl = Map(c0 + i, r0 + j + 1), br = Map(c0 + i + 1, r0 + j + 1);
                            if (tl == tr && tr == bl && bl == br) continue;   // a plain group says nothing
                            groups++;
                            int hit = -1;
                            for (int a = 0; a + 8 <= wram.Length; a += 2)
                                if ((wram[a] | wram[a + 1] << 8) == tl && (wram[a + 2] | wram[a + 3] << 8) == tr && (wram[a + 4] | wram[a + 5] << 8) == bl && (wram[a + 6] | wram[a + 7] << 8) == br) { hit = a; break; }
                            if (hit < 0) continue;
                            found++; residues[hit & 7]++;
                            int region = hit >> 12; regions[region] = regions.TryGetValue(region, out int k) ? k + 1 : 1;
                        }
                    Console.WriteLine($"    blocks aligned ({ax}, {ay}): {found} of {groups} distinct 2x2 groups found as 8 bytes in work RAM; by address mod 8 [{string.Join(",", residues)}]; 4 KB regions {string.Join(" ", regions.Select(kv => $"${0x7E0000 + (kv.Key << 12):X6}:{kv.Value}"))}");
                    if (found * 10 < groups * 9) continue;
                    // the block map: for each visible block row, the table addresses of its groups; a row of the map holds (address - table) / 8, as bytes or words
                    var rowsA = new List<long[]>();
                    for (int j = ay; j + 1 < 28; j += 2)
                    {
                        var list = new List<long>();
                        for (int i = ax; i + 1 < 32; i += 2)
                        {
                            ushort tl = Map(c0 + i, r0 + j), tr = Map(c0 + i + 1, r0 + j), bl = Map(c0 + i, r0 + j + 1), br = Map(c0 + i + 1, r0 + j + 1);
                            long hit = -1;
                            for (int a = 0; a + 8 <= wram.Length; a += 2)
                                if ((wram[a] | wram[a + 1] << 8) == tl && (wram[a + 2] | wram[a + 3] << 8) == tr && (wram[a + 4] | wram[a + 5] << 8) == bl && (wram[a + 6] | wram[a + 7] << 8) == br) { hit = a; break; }
                            list.Add(hit);
                        }
                        rowsA.Add(list.ToArray());
                    }
                    foreach (int size in new[] { 1, 2 })
                    {
                        var hits = new List<(int p, long table, int row)>();
                        for (int ri = 0; ri < rowsA.Count; ri++)
                        {
                            var A = rowsA[ri];
                            if (A.Any(a => a < 0)) continue;
                            for (int p = 0; p + A.Length * size <= wram.Length; p++)
                            {
                                long v0 = size == 1 ? wram[p] : (wram[p] | wram[p + 1] << 8) & 0x3FF;
                                long table = A[0] - v0 * 8;
                                bool ok = true;
                                for (int i = 1; i < A.Length && ok; i++)
                                {
                                    long v = size == 1 ? wram[p + i * size] : (wram[p + i * size] | wram[p + i * size + 1] << 8) & 0x3FF;
                                    ok = table + v * 8 == A[i];
                                }
                                if (ok) hits.Add((p, table, ri));
                            }
                        }
                        foreach (var grp in hits.GroupBy(h => h.table).OrderByDescending(gr => gr.Count()).Take(3))
                            Console.WriteLine($"      block map as {(size == 1 ? "bytes" : "words")}: table ${0x7E0000 + grp.Key:X6}: {grp.Select(h => h.row).Distinct().Count()} of {rowsA.Count} rows found, at {string.Join(" ", grp.Take(8).Select(h => $"r{h.row}:${0x7E0000 + h.p:X6}"))}");
                    }
                }
        }
        return 0;
    }
}
