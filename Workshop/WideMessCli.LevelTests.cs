using System;
using System.Collections.Generic;
using System.Linq;
using NesEmulator.Snes;

namespace BrokenNes.Workshop;

/// <summary>E2d: the level in work RAM (spec 6.1) on a synthetic game, each check beside its negative control.</summary>
internal static partial class WideMessCli
{
    private const int LvlBlocks = 0x10002, LvlWidth = 0x07A5, LvlHeight = 0x07A7, LvlTiles = 0xA000, LvlW = 64, LvlH = 32;
    private const int LvlIndex = 0x07FF, LvlFlipX = 0x0800, LvlFlipY = 0x1000;

    /// <summary>A synthetic level in work RAM: 64 x 32 blocks, block (bx, by) is block number by * 64 + bx (some flipped in x or y), each block's four entries one of 1,536
    /// (tiles 1-96 solid in colour 1 + (n - 1) mod 3, palettes 0-7, priority 0-1), so the picture has entries that appear once. <paramref name="variant"/> 1 is another level
    /// (another room in the same RAM).</summary>
    private static void FillLevel(byte[] ram, int variant)
    {
        void W16(int a, int v) { ram[a] = (byte)v; ram[a + 1] = (byte)(v >> 8); }
        W16(LvlWidth, LvlW); W16(LvlHeight, LvlH);
        for (int by = 0; by < LvlH; by++)
            for (int bx = 0; bx < LvlW; bx++)
            {
                int m = by * LvlW + bx;
                int flips = (bx + 2 * by) % 7 == 0 ? LvlFlipX : (bx + by) % 11 == 0 ? LvlFlipY : 0;
                W16(LvlBlocks + 2 * m, m | flips);
                for (int k = 0; k < 4; k++)
                {
                    int h = (m * 31 + k * 17 + variant * 101) % 1536;
                    W16(LvlTiles + m * 8 + k * 2, (1 + h % 96) | (h / 96 % 8) << 10 | (h / 768) << 13);
                }
            }
    }

    /// <summary>The level's map entry at level tile (lx, ly), as the game decodes it (the test's own decoder: the definition the console's must agree with).</summary>
    private static int LevelTile(byte[] ram, long lx, long ly)
    {
        if (lx < 0 || ly < 0 || lx >= LvlW * 2 || ly >= LvlH * 2) return -1;
        int a = LvlBlocks + 2 * (int)((ly >> 1) * LvlW + (lx >> 1));
        int wv = ram[a] | ram[a + 1] << 8;
        int qx = (int)(lx & 1), qy = (int)(ly & 1);
        bool fx = (wv & LvlFlipX) != 0, fy = (wv & LvlFlipY) != 0;
        if (fx) qx ^= 1;
        if (fy) qy ^= 1;
        int t = LvlTiles + (wv & LvlIndex) * 8 + (qy * 2 + qx) * 2;
        int e = ram[t] | ram[t + 1] << 8;
        if (fx) e ^= 0x4000;
        if (fy) e ^= 0x8000;
        return e;
    }

    /// <summary>One run: BG1 on a 32 x 32-tile map; each frame the "game" scrolls right by 4 (down by <paramref name="dy"/>) and draws the picture's tiles from the level.
    /// <paramref name="swapAt"/>: from that frame work RAM holds another level while the game keeps showing (and drawing) the first, a room left behind.
    /// The right side of frames <paramref name="judgeFrom"/> to <paramref name="judgeTo"/> is judged as the side-truth tool judges a game: each pixel against the SNES picture of the
    /// first later frame that shows its place (right: the same; empty: the side shows the backdrop where the SNES shows the level; wrong: anything else).</summary>
    private static (long right, long empty, long wrong, bool centre, string stats) LevelScene(PPU_WMS wms, PPU_SFC snes, WmsWidescreen rules, int judgeFrom, int judgeTo,
        int dy = 0, int swapAt = -1, bool trust = false)
    {
        var ram = new byte[0x20000]; var shown = new byte[0x20000];
        FillLevel(ram, 0); FillLevel(shown, 0);
        foreach (var p in new PPU_SFC[] { wms, snes })
        {
            p.Reset();
            for (int n = 1; n <= 96; n++) for (int w = 0; w < 8; w++) p.Vram[n * 16 + w] = (ushort)(((n - 1) % 3) switch { 0 => 0x00FF, 1 => 0xFF00, _ => 0xFFFF });   // tile n: solid colour 1 + (n - 1) mod 3
            for (int pal = 0; pal < 8; pal++) for (int c = 1; c < 4; c++) p.Cgram[pal * 16 + c] = (ushort)(0x7C00 | (pal * 3 + c + 4));
            p.WriteRegister(0x05, 0x01); p.WriteRegister(0x07, 0x10); p.WriteRegister(0x0B, 0x00); p.WriteRegister(0x2C, 0x01); p.WriteRegister(0x00, 0x0F);
        }
        wms.WorkRam = ram;
        wms.SetEnhancements(rules, false);
        wms.LevelTrustWithoutCheck = trust;
        int dbgLeft = 12;
        PPU_WMS.LevelDebug = Environment.GetEnvironmentVariable("WMS_DEBUG_LEVEL") == "1" ? m => { if (dbgLeft-- > 0) Console.WriteLine("    lvl " + m); } : null;
        bool centre = true;
        var history = new List<(int x, int y, uint[] snes)>();
        var claims = new List<(int x, int y, uint[] side)>();
        int x = 0, y = 8;
        for (int f = 0; f < judgeTo + 30; f++)
        {
            x += 4; y += dy;
            if (f == swapAt) FillLevel(ram, 1);   // the console's RAM moves on to another room; the game's picture (drawn from `shown`) does not
            for (long ty = (y + 1) >> 3; ty <= (y + 224) >> 3; ty++)
                for (long tx = x >> 3; tx <= (x + 255) >> 3; tx++)
                {
                    int e = LevelTile(shown, tx, ty);
                    foreach (var p in new PPU_SFC[] { wms, snes }) p.Vram[0x1000 + (int)((ty & 31) * 32 + (tx & 31))] = (ushort)(e < 0 ? 0 : e);
                }
            foreach (var p in new PPU_SFC[] { wms, snes })
            {
                p.WriteRegister(0x0D, (byte)(x & 0xFF)); p.WriteRegister(0x0D, (byte)((x >> 8) & 3));
                p.WriteRegister(0x0E, (byte)(y & 0xFF)); p.WriteRegister(0x0E, (byte)((y >> 8) & 3));
            }
            var wf = DrawFrame(wms, out _); var sf = DrawFrame(snes, out _);
            for (int r = 0; r < 224; r++) if (!wf.AsSpan(r * 400 + 72, 256).SequenceEqual(sf.AsSpan(r * 256, 256))) centre = false;
            history.Add((x, y, sf));
            if (f >= judgeFrom && f < judgeTo)
            {
                var side = new uint[224 * 72];
                for (int r = 0; r < 224; r++) wf.AsSpan(r * 400 + 328, 72).CopyTo(side.AsSpan(r * 72, 72));
                claims.Add((x, y, side));
            }
        }
        PPU_WMS.LevelDebug = null;
        long right = 0, empty = 0, wrong = 0;
        foreach (var (cx, cy, side) in claims)
            for (int r = 0; r < 224; r++)
                for (int c = 0; c < 72; c++)
                {
                    long wx = cx + 256 + c, wy = cy + r + 1;
                    // the first later frame whose SNES picture shows the place (away from its two edge tile columns, which a 32-tile map shares)
                    var g = history.FirstOrDefault(h => h.x + 8 <= wx && wx < h.x + 248 && h.y + 1 <= wy && wy <= h.y + 224);
                    if (g.snes == null) continue;
                    uint truth = g.snes[(int)(wy - g.y - 1) * 256 + (int)(wx - g.x)], v = side[r * 72 + c];
                    if (r == 100 && c % 8 == 0 && cx == claims[0].x && Environment.GetEnvironmentVariable("WMS_DEBUG_LEVEL") == "1") Console.WriteLine($"    side x {cx} c {c}: {v:X8} truth {truth:X8} (frame x {g.x}) entry {LevelTile(shown, wx >> 3, wy >> 3):X4}");
                    if (v == truth) right++;
                    else if (v == 0xFF000000u) empty++;
                    else wrong++;
                }
        string stats = wms.MemoryStats(0) ?? "";
        wms.LevelTrustWithoutCheck = false;
        return (right, empty, wrong, centre, stats);
    }

    private static void SyntheticLevel()
    {
        var wms = new PPU_WMS(); var snes = new PPU_SFC();
        WmsWidescreen Rules(int blocks)
        {
            var r = WmsWidescreen.AllExtend(); r.Layers[0] = WmsSideRule.Drawn; r.DrawnAhead[0] = 0;
            if (blocks >= 0) r.Level = new WmsLevel { Layer = 0, Blocks = blocks, Width = LvlWidth, Height = LvlHeight, Tiles = LvlTiles, Index = LvlIndex, FlipX = LvlFlipX, FlipY = LvlFlipY };
            return r;
        }
        const int all = 20 * 224 * 72;
        var a = LevelScene(wms, snes, Rules(LvlBlocks), 40, 60);
        Check(a.right == all && a.centre,
            "BG1 drawn with the level in work RAM: scrolling right, the right side ahead shows the level at every place before the game draws it (blocks flipped too), as the SNES picture shows it later; the centre the SNES chip's picture",
            $"{a.right:N0} right, {a.empty:N0} empty, {a.wrong:N0} wrong of {all:N0}; {a.stats}");
        var n = LevelScene(wms, snes, Rules(-1), 40, 60);
        Check(n.empty == all, "negative control, drawn without the level: the right side ahead is empty", $"{n.right:N0} right, {n.empty:N0} empty, {n.wrong:N0} wrong");
        var v = LevelScene(wms, snes, Rules(LvlBlocks), 40, 60, dy: 1);
        Check(v.right > all * 9 / 10 && v.empty + v.wrong == 0 && v.centre, "the level follows the vertical scroll (scrolling right and down): every judged pixel right (the top rows scroll out of view before their places are shown)",
            $"{v.right:N0} right, {v.empty:N0} empty, {v.wrong:N0} wrong; {v.stats}");
        var off = LevelScene(wms, snes, Rules(LvlBlocks + 1), 40, 60);
        Check(off.wrong == 0 && off.right == 0 && off.stats.Contains("verified on 0 of"),
            "a profile with a wrong address (the blocks 1 byte off: every block word garbled): never verified, so nothing of it is shown (empty, never wrong)", $"{off.empty:N0} empty, {off.wrong:N0} wrong; {off.stats}");
        var swap = LevelScene(wms, snes, Rules(LvlBlocks), 50, 60, swapAt: 50);
        Check(swap.wrong == 0 && swap.right == 0, "work RAM holding another room while the picture still shows the first: the check fails, so the sides show nothing of it",
            $"{swap.empty:N0} empty, {swap.wrong:N0} wrong; {swap.stats}");
        var trust = LevelScene(wms, snes, Rules(LvlBlocks), 50, 60, swapAt: 50, trust: true);
        Check(trust.wrong > 10 * 224 * 72 / 2, "negative control, the level trusted without the check: the other room's tiles fill the right side (wrong)", $"{trust.wrong:N0} wrong of {10 * 224 * 72:N0}");
        wms.SetEnhancements(null, false);
        wms.WorkRam = null;
    }
}
