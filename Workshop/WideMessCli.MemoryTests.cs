using System;
using System.Collections.Generic;
using System.Linq;
using NesEmulator.Snes;

namespace BrokenNes.Workshop;

/// <summary>E2c: the map memory (spec 6.1) on a synthetic game, each check beside its negative control.</summary>
internal static partial class WideMessCli
{
    /// <summary>One run of the synthetic memory game: BG1 on a 32 x 32-tile map (256 pixels: the map holds only the screen's width, as Super Castlevania IV's and Contra III's do)
    /// over a level of white and green blocks (tile column t, tile row r: white when (t / 12 + r / 6) is even). Each frame the "game" moves by the next of <paramref name="moves"/>
    /// and draws exactly the tiles the SNES picture shows at their places (old tiles stay in the rest of the map). <paramref name="newLevelAt"/>: from that frame the level is red
    /// tiles everywhere, at the same scroll (a scene change the scroll does not show); <paramref name="blankAt"/>: that frame is forced blank. The judge of each frame's sides
    /// counts, per side pixel, the level's colour where the place has been shown since the last scene change (right), the backdrop where it has not (right), the backdrop where
    /// it has (empty) and anything else (wrong).</summary>
    private static (long right, long empty, long wrong, long remembered, bool centre) MemoryScene(PPU_WMS wms, PPU_SFC snes, WmsWidescreen rules, IList<(int dx, int dy)> moves,
        int newLevelAt = -1, int blankAt = -1, bool disagreeCut = true, int judgeFrom = 0, int judgeEvery = 1)
    {
        foreach (var p in new PPU_SFC[] { wms, snes })
        {
            p.Reset();
            for (int w = 16; w < 24; w++) p.Vram[w] = 0x00FF;   // tile 1: colour 1 (white)
            for (int w = 32; w < 40; w++) p.Vram[w] = 0xFF00;   // tile 2: colour 2 (red)
            for (int w = 48; w < 56; w++) p.Vram[w] = 0xFFFF;   // tile 3: colour 3 (green)
            p.Cgram[0] = 0; p.Cgram[1] = 0x7FFF; p.Cgram[2] = 0x001F; p.Cgram[3] = 0x03E0;
            for (int a = 0x1000; a < 0x1400; a++) p.Vram[a] = 2;   // what an old level left: red
            p.WriteRegister(0x05, 0x01);   // BGMODE 1
            p.WriteRegister(0x07, 0x10);   // BG1 map at word $1000, 32 x 32 tiles
            p.WriteRegister(0x0B, 0x00);
            p.WriteRegister(0x2C, 0x01);   // TM: BG1
            p.WriteRegister(0x00, 0x0F);
        }
        wms.SetEnhancements(rules, false);
        wms.SetMemoryDisagreeCut(disagreeCut);
        bool newLevel = false;
        int Tile(long tx, long ty) => newLevel ? 2 : ((tx / 12 + ty / 6) & 1) == 0 ? 1 : 3;
        var shown = new HashSet<(long, long)>();
        int x = 0, y = 0;
        long right = 0, empty = 0, wrong = 0, remembered = 0; bool centre = true;
        var colour = new Dictionary<int, uint>();
        // a 32-tile map not on a tile boundary: the picture's two end tile columns share one map column, and the SNES shows one of them for both (the place is not what it shows)
        bool Aliased(long tx) => ((x + 255) >> 3) - (x >> 3) + 1 > 32 && (tx == x >> 3 || tx == (x + 255) >> 3);
        for (int f = 0; f < moves.Count; f++)
        {
            x += moves[f].dx; y += moves[f].dy;
            if (f == newLevelAt) { newLevel = true; shown.Clear(); }
            // the game draws the picture's tiles (lines 1-224 show map rows y + 1 .. y + 224)
            for (long ty = (y + 1) >> 3; ty <= (y + 224) >> 3; ty++)
                for (long tx = x >> 3; tx <= (x + 255) >> 3; tx++)
                    foreach (var p in new PPU_SFC[] { wms, snes }) p.Vram[0x1000 + (int)((ty & 31) * 32 + (tx & 31))] = (ushort)Tile(tx, ty);
            foreach (var p in new PPU_SFC[] { wms, snes })
            {
                p.WriteRegister(0x00, (byte)(f == blankAt ? 0x8F : 0x0F));
                p.WriteRegister(0x0D, (byte)(x & 0xFF)); p.WriteRegister(0x0D, (byte)((x >> 8) & 3));
                p.WriteRegister(0x0E, (byte)(y & 0xFF)); p.WriteRegister(0x0E, (byte)((y >> 8) & 3));
            }
            var wf = DrawFrame(wms, out _); var sf = DrawFrame(snes, out _);
            if (f == blankAt) { shown.Clear(); continue; }
            for (int r = 0; r < 224; r++)
            {
                if (!wf.AsSpan(r * 400 + 72, 256).SequenceEqual(sf.AsSpan(r * 256, 256))) centre = false;
                for (int c = 0; c < 256; c++) if (!Aliased((x + c) >> 3)) colour[Tile((x + c) >> 3, (y + r + 1) >> 3)] = sf[r * 256 + c];
            }
            if (f >= judgeFrom && (f - judgeFrom) % judgeEvery == 0)
                for (int r = 0; r < 224; r++)
                    for (int c = 0; c < 400; c++)
                    {
                        if (c == 72) c = 328;
                        long wx = x + c - 72, wy = y + r + 1;
                        var place = (wx >> 3, wy >> 3);
                        uint v = wf[r * 400 + c];
                        bool was = shown.Contains(place);
                        bool level = colour.TryGetValue(Tile(place.Item1, place.Item2), out var want) && v == want;
                        if (was && level) { right++; remembered++; }
                        else if (!was && v == 0xFF000000u) right++;
                        else if (was && v == 0xFF000000u) empty++;
                        else wrong++;
                    }
            for (long ty = (y + 1) >> 3; ty <= (y + 224) >> 3; ty++)
                for (long tx = x >> 3; tx <= (x + 255) >> 3; tx++) if (!Aliased(tx)) shown.Add((tx, ty));
        }
        return (right, empty, wrong, remembered, centre);
    }

    private static void SyntheticMemory()
    {
        var wms = new PPU_WMS(); var snes = new PPU_SFC();
        var drawn = WmsWidescreen.AllExtend(); drawn.Layers[0] = WmsSideRule.Drawn; drawn.DrawnAhead[0] = 0;
        var mem = WmsWidescreen.AllExtend(); mem.Layers[0] = WmsSideRule.Drawn; mem.DrawnAhead[0] = 0; mem.Memory[0] = true;
        static List<(int, int)> Moves(params (int n, int dx, int dy)[] runs) { var l = new List<(int, int)>(); foreach (var (n, dx, dy) in runs) for (int i = 0; i < n; i++) l.Add((dx, dy)); return l; }
        // right 240 pixels, then back left 72: the right side shows places the picture showed on the way
        var back = Moves((60, 4, 0), (18, -4, 0));
        var m = MemoryScene(wms, snes, mem, back, judgeFrom: back.Count - 1);
        var d = MemoryScene(wms, snes, drawn, back, judgeFrom: back.Count - 1);
        Check(m.wrong == 0 && m.empty == 0 && m.remembered == 2 * 224 * 72 && m.centre,
            "BG1 drawn with the map memory (a 32-tile map): after a run right and 72 pixels back, both sides show the level as the picture showed it, every pixel; the centre the SNES chip's picture",
            $"{m.remembered:N0} remembered pixels, {m.empty:N0} empty, {m.wrong:N0} wrong");
        Check(d.remembered == 0 && d.empty == 2 * 224 * 72 && d.wrong == 0,
            "negative control, drawn without the memory: both sides empty (the map holds only the screen's width)", $"{d.empty:N0} empty, {d.wrong:N0} wrong");
        // diagonal: right and down, then back: the world rows follow the vertical scroll
        var diag = Moves((60, 4, 2), (24, -4, -3));
        var g = MemoryScene(wms, snes, mem, diag, judgeFrom: 60);
        Check(g.wrong == 0 && g.empty == 0 && g.remembered > 100_000 && g.centre,
            "the memory follows the vertical scroll: scrolling diagonally and back, every remembered side pixel is the level's at its place (judged on 24 frames)",
            $"{g.remembered:N0} remembered, {g.empty:N0} empty, {g.wrong:N0} wrong");
        // a scene change at the same scroll (the level reloaded behind no cut of the scroll): the map disagrees with the memory, which is emptied before the frame is drawn
        var cut = Moves((60, 4, 0), (12, 0, 0));
        var c = MemoryScene(wms, snes, mem, cut, newLevelAt: 66, judgeFrom: 66);
        var cn = MemoryScene(wms, snes, mem, cut, newLevelAt: 66, disagreeCut: false, judgeFrom: 66);
        Check(c.wrong == 0 && c.remembered == 0 && c.centre, "a new level at the same scroll: the game's map disagrees with the memory, so the memory is emptied before the frame is drawn: no side pixel of the old level",
            $"{c.wrong:N0} wrong, {c.remembered:N0} remembered");
        Check(cn.wrong >= 224 * 72 * 6, "negative control, the disagreement cut off: the old level's blocks stay in the left side (stale)", $"{cn.wrong:N0} wrong pixels over 6 frames");
        // a forced-blank frame (games change scenes behind black): forgotten
        var b = MemoryScene(wms, snes, mem, cut, blankAt: 64, judgeFrom: 65, judgeEvery: 100);
        Check(b.wrong == 0 && b.remembered == 0, "a frame without the layer (forced blank): the memory is forgotten, the frame after shows no remembered place", $"{b.remembered:N0} remembered, {b.wrong:N0} wrong");
        var stats = wms.MemoryStats(0) ?? "";
        Check(stats.Contains("by a frame without the layer"), "the memory's counters are reported (tools)", stats);

        wms.SetEnhancements(null, false);
    }
}
