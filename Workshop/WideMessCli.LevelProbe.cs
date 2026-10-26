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
        // --format bytes --stride N: the block map as one byte a block (the block table's index), rows N bytes apart (Chrono Trigger)
        bool bytes = Opt(args, "format", "words") == "bytes"; int stride = int.Parse(Opt(args, "stride", "256"));
        string layerName = Opt(args, "layer", "bg1"); int layer = layerName[2] - '1';
        using var s = EnhancedSession(rom, args, out _);
        if (s == null) return 1;
        var ppu = s.WideMessPpu!;
        var pad = ParsePad(script);
        int at = 0; PadButtons held = 0;
        var wram = s.Board.Wram;
        int scan = int.Parse(Opt(args, "scan", "0"));
        if (scan > 0) return ByteMapScan(s, pad, moments.Max(), scan, args);
        foreach (int m in moments)
        {
            PlayTo(s, pad, ref at, m, ref held);
            var r = ppu.DebugLineRegisters![1];
            int width = bytes ? stride : wram[widthAt] | wram[widthAt + 1] << 8;
            int sc = layer switch { 1 => r.Bg2sc, 2 => r.Bg3sc, 3 => r.Bg4sc, _ => r.Bg1sc }, mapBase = (sc & 0xFC) << 8; bool wide = (sc & 1) != 0, tall = (sc & 2) != 0;
            int hofs = (layer switch { 1 => r.Bg2hofs, 2 => r.Bg3hofs, 3 => r.Bg4hofs, _ => r.Bg1hofs }) & 0x3FF, vofs = (layer switch { 1 => r.Bg2vofs, 2 => r.Bg3vofs, 3 => r.Bg4vofs, _ => r.Bg1vofs }) & 0x3FF;
            ushort Map(int col, int row)
            {
                int a = mapBase + ((row & 31) << 5) + (col & 31);
                if (wide && (col & 32) != 0) a += 0x400;
                if (tall && (row & 32) != 0) a += wide ? 0x800 : 0x400;
                return ppu.Vram[a & 0x7FFF];
            }
            ushort Decode(int bx, int by, int qx, int qy)
            {
                int i = bytes ? blocksAt + by * stride + bx : blocksAt + 2 * (by * width + bx);
                if (i < 0 || i + 1 >= wram.Length) return 0xFFFF;
                int w = bytes ? wram[i] : wram[i] | wram[i + 1] << 8;
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
            int height = width == 0 ? 0 : bytes ? int.Parse(Opt(args, "rows", "64")) : Math.Min(256, (0x6400 - 2) / 2 / width);
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
            Console.WriteLine($"  frame {m}: {layerName} HOFS {hofs} VOFS {vofs} SC {sc:X2}, room width {width} blocks; best: world tile ({bestTx}, {bestTy}) at map tile ({c0}, {r0}), {best} of {total} sampled tiles match");
            string saveTo = Opt(args, "save-ram", "");
            if (saveTo != "")
            {
                // research: work RAM and video RAM at this moment, for offline analysis
                File.WriteAllBytes($"{saveTo}_{m}.wram", wram);
                var vb = new byte[0x10000];
                for (int k = 0; k < 0x8000; k++) { vb[2 * k] = (byte)ppu.Vram[k]; vb[2 * k + 1] = (byte)(ppu.Vram[k] >> 8); }
                File.WriteAllBytes($"{saveTo}_{m}.vram", vb);
                File.WriteAllText($"{saveTo}_{m}.regs", r.ToString());
            }
            if (args.Contains("--dump", StringComparer.OrdinalIgnoreCase))
            {
                // each visible 2 x 2 group (at the --ax/--ay alignment): its index in the block table at --tiles (8 bytes a block; up to two shown), "----" when not in work RAM, "~~~~" elsewhere
                int dax = int.Parse(Opt(args, "ax", "0")), day = int.Parse(Opt(args, "ay", "0"));
                for (int j = day; j + 1 < 28; j += 2)
                {
                    var line = new System.Text.StringBuilder($"    r{j,2}:");
                    for (int i = dax; i + 1 < 34; i += 2)
                    {
                        ushort tl = Map(c0 + i, r0 + j), tr = Map(c0 + i + 1, r0 + j), bl = Map(c0 + i, r0 + j + 1), br = Map(c0 + i + 1, r0 + j + 1);
                        var hitsT = new List<int>();
                        for (int a = 0; a + 8 <= wram.Length; a += 2)
                            if ((wram[a] | wram[a + 1] << 8) == tl && (wram[a + 2] | wram[a + 3] << 8) == tr && (wram[a + 4] | wram[a + 5] << 8) == bl && (wram[a + 6] | wram[a + 7] << 8) == br) hitsT.Add(a);
                        var idx = hitsT.Where(a => a >= tilesAt && (a - tilesAt) % 8 == 0 && (a - tilesAt) / 8 < 1024).Select(a => (a - tilesAt) / 8).ToList();
                        line.Append(idx.Count == 0 ? (hitsT.Count == 0 ? " ----" : " ~~~~") : " " + string.Join("/", idx.Take(2).Select(v => v.ToString("X3"))));
                    }
                    Console.WriteLine(line.ToString());
                }
                if (bytes)
                {
                    // the block map's bytes around --blocks: rows of the given stride, the first 17 bytes of each
                    for (int row = int.Parse(Opt(args, "from-row", "0")), n = 0; n < 20; row++, n++)
                    {
                        int a = blocksAt + row * stride;
                        Console.WriteLine($"    map row {row,3} ${0x7E0000 + a:X6}: " + string.Join(" ", Enumerable.Range(0, 17).Select(k => wram[a + k].ToString("X2"))));
                    }
                }
            }
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

    /// <summary>Research (<c>--scan N</c>): every N frames, for BG1-BG3, the best fit of the byte-map layout (rows of 256 bytes at --blocks, a plane per layer at +$00/+$40/+$80, the
    /// block index's bit 8 in a bit of the byte at +$C0, a block table of 8 bytes a block) to the picture's map, over the tables $7EA000-$7EC400.</summary>
    private static int ByteMapScan(SnesSession s, SortedDictionary<int, PadButtons> pad, int frames, int every, string[] args)
    {
        var ppu = s.WideMessPpu!; var wram = s.Board.Wram;
        int blocksAt = Convert.ToInt32(Opt(args, "blocks", "7E4000"), 16) - 0x7E0000;
        int at = 0; PadButtons held = 0;
        var pic = new ushort[28, 32];
        for (int f = every; f <= frames; f += every)
        {
            PlayTo(s, pad, ref at, f, ref held);
            var r = ppu.DebugLineRegisters![1];
            var line = new System.Text.StringBuilder($"  frame {f,5} TM {r.Tm:X2} BGMODE {r.Bgmode:X2}:");
            for (int layer = 0; layer < 3; layer++)
            {
                int sc = layer switch { 1 => r.Bg2sc, 2 => r.Bg3sc, _ => r.Bg1sc }, mapBase = (sc & 0xFC) << 8; bool wide = (sc & 1) != 0, tall = (sc & 2) != 0;
                int hofs = (layer switch { 1 => r.Bg2hofs, 2 => r.Bg3hofs, _ => r.Bg1hofs }) & 0x3FF, vofs = (layer switch { 1 => r.Bg2vofs, 2 => r.Bg3vofs, _ => r.Bg1vofs }) & 0x3FF;
                int c0 = hofs >> 3, r0 = (vofs + 1) >> 3;
                for (int j = 0; j < 28; j++)
                    for (int i = 0; i < 32; i++)
                    {
                        int col = c0 + i, row = r0 + j;
                        int a = mapBase + ((row & 31) << 5) + (col & 31);
                        if (wide && (col & 32) != 0) a += 0x400;
                        if (tall && (row & 32) != 0) a += wide ? 0x800 : 0x400;
                        pic[j, i] = ppu.Vram[a & 0x7FFF];
                    }
                int best = -1, bp = 0, bt = 0, bb = 0, bx0 = 0, by0 = 0, distinct = 0;
                var set = new HashSet<ushort>(); foreach (var e in pic) set.Add(e); distinct = set.Count;
                foreach (int plane in new[] { 0, 0x40, 0x80 })
                    for (int T = 0xA000; T <= 0xC400; T += 0x400)
                        foreach (int bit in new[] { -1, 0, 1, 2 })
                        {
                            int Entry(int lx, int ly)
                            {
                                int bx = lx >> 1, by = ly >> 1;
                                if (bx < 0 || by < 0 || bx >= 64 || by >= 64) return -1;
                                int a = blocksAt + by * 256 + bx;
                                int k = wram[a + plane] | (bit >= 0 ? ((wram[a + 0xC0] >> bit) & 1) << 8 : 0);
                                int t = T + 8 * k + ((ly & 1) * 2 + (lx & 1)) * 2;
                                return wram[t] | wram[t + 1] << 8;
                            }
                            for (int oy = -1; oy < 128 - 27; oy++)
                                for (int ox = -1; ox < 128 - 31; ox++)
                                {
                                    if (Entry(ox + 15, oy + 13) != pic[13, 15]) continue;
                                    int n = 0;
                                    for (int j = 0; j < 28; j += 3) for (int i = 0; i < 32; i += 3) if (Entry(ox + i, oy + j) == pic[j, i]) n++;
                                    if (n > best) { best = n; bp = plane; bt = T; bb = bit; bx0 = ox; by0 = oy; }
                                }
                        }
                line.Append($" | BG{layer + 1} ({hofs},{vofs}) {distinct,3} entries: {best,3}/110 +{bp:X2} ${0x7E0000 + bt:X6} b{bb} @({bx0},{by0})");
            }
            Console.WriteLine(line.ToString());
        }
        return 0;
    }
}
