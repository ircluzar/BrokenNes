// PicoDraw2 - PicoDrive's "fast renderer", ported from notaz/picodrive tag v1.85 (2013-08-31), pico/draw2.c (624 lines, the C
// path; the ARM asm twin is the same algorithm): BackFillFull, DrawLayerFull, DrawTilesFromCacheF, DrawWindowFull,
// DrawAllSpritesFull, DrawSpriteFull, DrawDisplayFull, PicoFrameFull.
//
//   PicoDrive tile renderer, (C) notaz, 2006-2008. Licensed under the terms of the MAME license (COPYING at the tag:
//   non-commercial). See THIRD_PARTY_NOTICES.md (PPU harvest) and sega/THIRD_PARTY_NOTICES.md.
//
// PPU harvest climb (docs/projects/ppu-harvest-catalog.md, #7): an art instrument, ported FAITHFULLY with its flaws. It is the
// Genesis picture core "PD2": the stock VDP keeps the console's timing and its memories, and when the frame is done this
// renderer draws the whole frame at once from them, a tile at a time, as PicoFrameFull does.
//
// Characteristic jank, all of it kept on purpose:
//  * Whole frame at once from the end-of-frame VRAM/CRAM/VSRAM/registers: every mid-frame effect (palette splits,
//    H-interrupt tricks, raster scroll) is gone.
//  * Only 224 lines (28 tile rows): the bottom of a 240-line (V30) picture stays the backdrop.
//  * Horizontal scroll per 8-line tile ROW (line scroll mode takes the row's first line), vertical scroll is plane-wide
//    (VSRAM 0/1 only, no 2-cell columns).
//  * The window takes the priority of its first tile for all of it ("hack: just assume that whole window uses same
//    priority"); with a vertical window, plane A is drawn from the left edge with fewer cells.
//  * Sprites: no per-line sprite or pixel limit, only simple top/bottom X=0 masking, and a sprite tile at x <= 0 (the
//    left 8 border pixels) is skipped whole.
//  * No shadow/highlight, no interlace; colours 0 of the tiles are transparent over the backdrop (reg 7).
namespace NesEmulator.Sega;

internal sealed class PicoDraw2
{
    const int StartRow = 0, EndRow = 28, LineWidth = 328;
    private readonly byte[] fb = new byte[(8 + 320) * (8 + 240 + 8)];
    private readonly int[] hcA = new int[41 * (EndRow - StartRow + 1) + 2], hcB = new int[41 * (EndRow - StartRow + 1) + 2];
    private readonly int[] sprites = new int[80];
    private MdVdp v = null!;

    public int Width { get; private set; } = 320;
    public const int Height = 224;

    private int W16(int wordAddr) { wordAddr &= 0x7FFF; return v.Vram[wordAddr * 2] << 8 | v.Vram[wordAddr * 2 + 1]; }
    private uint W32(int wordAddr) => (uint)W16(wordAddr) | (uint)W16(wordAddr + 1) << 16;

    // The four Tile* routines: 8 rows, LINE_WIDTH apart, colour 0 transparent; true when the tile was blank.
    private bool Tile(int pd, int addr, int pal, int flip)
    {
        bool blank = true;
        bool xf = (flip & 1) != 0, yf = (flip & 2) != 0;
        if (yf) addr += 14;
        for (int i = 8; i > 0; i--, addr += yf ? -2 : 2, pd += LineWidth)
        {
            uint pack = W32(addr);
            if (pack == 0) continue;
            blank = false;
            // pixel order in pack: 12,8,4,0,28,24,20,16 (normal), the reverse when X-flipped
            for (int p = 0; p < 8; p++)
            {
                int sh = xf ? (p < 4 ? 16 + 4 * p : 4 * (p - 4)) : (p < 4 ? 12 - 4 * p : 28 - 4 * (p - 4));
                uint t = (pack >> sh) & 0xF;
                if (t != 0 && pd + p >= 0 && pd + p < fb.Length) fb[pd + p] = (byte)(t | (uint)pal);
            }
        }
        return blank;
    }

    private void DrawWindowFull(int start, int end, int prio)
    {
        int tileStart = start >> 16, tileEnd = end >> 16;
        start = (short)start; end = (short)end;
        int nametab, step;
        if ((v.Reg[12] & 1) != 0) { nametab = (v.Reg[3] & 0x3C) << 9; step = 1 << 6; }
        else { nametab = (v.Reg[3] & 0x3E) << 9; step = 1 << 5; }
        nametab += step * start;
        int code0 = W16(nametab + tileStart);
        if ((code0 >> 15) != prio) return;
        int scrpos = 8 * LineWidth + 8 + 8 * LineWidth * (start - StartRow);
        int blank = -1;
        for (int trow = start; trow < end; trow++, nametab += step)
        {
            for (int tx = tileStart; tx < tileEnd; tx++)
            {
                int code = W16(nametab + tx);
                if (code == blank) continue;
                if (Tile(scrpos + (tx << 3), (code & 0x7FF) << 4, (code >> 9) & 0x30, (code >> 11) & 3)) blank = code;
            }
            scrpos += LineWidth * 8;
        }
    }

    private void DrawLayerFull(int plane, int[] hc, int planestart, int planeend)
    {
        int[] shift = { 5, 6, 6, 7 };
        int hcI = 0;
        int cells = (planeend >> 16) - (planestart >> 16);
        planestart = (short)planestart; planeend = (short)planeend;
        int htab = v.Reg[13] << 9;
        htab += plane;
        int hscroll = 0;
        if ((v.Reg[11] & 3) == 0) { hscroll = W16(htab & 0x7FFF); htab = 0; }
        int width = v.Reg[16], height = (width >> 4) & 3; width &= 3;
        int xmask = (1 << shift[width]) - 1, ymask = (height << 5) | 0x1F;
        if (width == 1) ymask &= 0x3F; else if (width > 1) ymask = 0x1F;
        int nametab = plane == 0 ? (v.Reg[2] & 0x38) << 9 : (v.Reg[4] & 0x07) << 12;
        int scrpos = 8 * LineWidth * (planestart - StartRow);
        int vscroll = v.Vsram[plane] & 0x1FF;
        scrpos += (8 - (vscroll & 7)) * LineWidth;
        if ((vscroll & 7) != 0) planeend++;
        hc[hcI++] = 8 - (vscroll & 7);
        int blank = -1;
        for (int trow = planestart; trow < planeend; trow++)
        {
            int cellc = cells;
            int row = nametab + (((trow + (vscroll >> 3)) & ymask) << shift[width]);
            if (htab != 0)
            {
                int ha = htab + (trow << 4);
                if (trow != 0) ha -= (vscroll & 7) << 1;
                hscroll = W16(ha & 0x7FFF);
            }
            int tilex = (-hscroll) >> 3, dx = ((hscroll - 1) & 7) + 1;
            if (dx != 8) cellc++;
            for (; cellc > 0; dx += 8, tilex++, cellc--)
            {
                int code = W16(row + (tilex & xmask));
                if (code == blank) continue;
                if ((code >> 15) != 0) { if (hcI < hc.Length - 1) hc[hcI++] = code | (dx << 16) | (trow << 27); continue; }
                if (Tile(scrpos + dx, (code & 0x7FF) << 4, (code >> 9) & 0x30, (code >> 11) & 3)) blank = code;
            }
            scrpos += LineWidth * 8;
        }
        hc[hcI] = 0;
    }

    private void DrawTilesFromCacheF(int[] hc)
    {
        int i = 0;
        int scrpos = hc[i++] * LineWidth - StartRow * LineWidth * 8, pd = 0;
        uint prevy = 0xFFFFFFFF;
        short blank = -1;
        int code;
        while ((code = hc[i++]) != 0)
        {
            if ((short)code == blank) continue;
            if (((uint)code >> 27) != prevy) { prevy = (uint)code >> 27; pd = scrpos + (int)prevy * LineWidth * 8; }
            if (Tile(pd + ((code >> 16) & 0x1FF), (code & 0x7FF) << 4, (code >> 9) & 0x30, (code >> 11) & 3)) blank = (short)code;
        }
    }

    private void DrawSpriteFull(int sp)
    {
        uint s0 = W32(sp), s1 = W32(sp + 2);
        int sy = (int)s0, height = sy >> 24;
        sy = (sy & 0x1FF) - 0x78;
        int width = (height >> 2) & 3; height &= 3; width++; height++;
        int code = (int)s1;
        int sx = ((code >> 16) & 0x1FF) - 0x78;
        int tile = code & 0x7FF, tdx = height, tdy = 1;
        if ((code & 0x0800) != 0) { tdx = -tdx; tile += height * (width - 1); }
        if ((code & 0x1000) != 0) { tdy = -tdy; tile += height - 1; }
        int pal = (code >> 9) & 0x30;
        while (sy <= StartRow * 8) { sy += 8; tile += tdy; height--; }
        int scrpos = (sy - StartRow * 8) * LineWidth;
        for (; height > 0; height--, sy += 8, tile += tdy)
        {
            if (sy >= EndRow * 8 + 8) return;
            int x = sx, t = tile;
            for (int w = width; w > 0; w--, x += 8, t += tdx)
            {
                if (x <= 0) continue;
                if (x >= 328) break;
                t &= 0x7FFF;
                Tile(scrpos + x, t << 4, pal, (code >> 11) & 3);
            }
            scrpos += 8 * LineWidth;
        }
    }

    private void DrawAllSpritesFull(int prio, int maxwidth)
    {
        int table = v.Reg[5] & 0x7F;
        if ((v.Reg[12] & 1) != 0) table &= 0x7E;
        table <<= 8;
        int i = 0, link = 0, maskrange = 0, yMin = StartRow * 8, yMax = EndRow * 8;
        for (int u = 0; u < 80; u++)
        {
            int sp = (table + (link << 2)) & 0x7FFC;
            int code = (int)W32(sp);
            int sy = (code & 0x1FF) - 0x80, height = (((code >> 24) & 3) + 1) << 3;
            if (!(sy + height <= yMin || sy > yMax))
            {
                int code2 = (int)W32(sp + 2);
                int sx = (code2 >> 16) & 0x1FF;
                if (sx == 0)
                {
                    int to = sy + height;
                    if (maskrange != 0)
                    {
                        if ((maskrange >> 16) + 1 >= sy && (maskrange >> 16) <= to && (maskrange & 0xFFFF) < sy) sy = maskrange & 0xFFFF;
                        else if ((maskrange & 0xFFFF) - 1 <= to && (maskrange & 0xFFFF) >= sy && (maskrange >> 16) > to) to = maskrange >> 16;
                    }
                    if (sy <= yMin && to + 1 > yMin) yMin = to + 1;
                    else if (to >= yMax && sy - 1 < yMax) yMax = sy - 1;
                    else maskrange = (sy & 0xFFFF) | (to << 16);
                }
                else if (((code2 >> 15) & 1) == prio)
                {
                    sx -= 0x78;
                    if (!(sx <= -8 * 3 || sx >= maxwidth)) sprites[i++] = sp;
                }
            }
            link = (code >> 16) & 0x7F;
            if (link == 0) break;
        }
        for (i--; i >= 0; i--) DrawSpriteFull(sprites[i]);
    }

    private void DrawDisplayFull()
    {
        int hvwin = 0, planestart = StartRow, planeend = EndRow, winstart = StartRow, winend = EndRow, maxw, maxcolc;
        if ((v.Reg[12] & 1) != 0) { maxw = 328; maxcolc = 40; } else { maxw = 264; maxcolc = 32; }
        int win = v.Reg[0x12], edge;
        if (win != 0)
        {
            hvwin = 1; edge = win & 0x1F;
            if (win == 0x80) hvwin = 4;
            else if (win < 0x80) { if (edge <= StartRow) hvwin = 0; else if (edge >= EndRow) hvwin = 4; else planestart = winend = edge; }
            else { if (edge >= EndRow) hvwin = 0; else planeend = winstart = edge; }
        }
        if (hvwin != 4)
        {
            win = v.Reg[0x11]; edge = win & 0x1F;
            if ((win & 0x80) != 0)
            {
                if (edge == 0) hvwin = 4;
                else if (edge < (maxcolc >> 1)) { hvwin |= 2; planeend |= edge << 17; winstart |= edge << 17; winend |= maxcolc << 16; }
            }
            else
            {
                if (edge >= (maxcolc >> 1)) hvwin = 4;
                else if (edge != 0) { hvwin |= 2; winend |= edge << 17; planestart |= edge << 17; planeend |= maxcolc << 16; }
            }
        }
        if (hvwin == 1) { winend |= maxcolc << 16; planeend |= maxcolc << 16; }
        hcA[1] = hcB[1] = 0;
        DrawLayerFull(1, hcB, StartRow, (maxcolc << 16) | EndRow);
        switch (hvwin)
        {
            case 4: DrawWindowFull(StartRow, (maxcolc << 16) | EndRow, 0); break;
            case 3:
                DrawLayerFull(0, hcA, planestart, planeend);
                DrawWindowFull(winstart & ~0xFF0000, (winend & ~0xFF0000) | (maxcolc << 16), 0);
                DrawWindowFull((winstart & ~0xFF) | StartRow, (winend & ~0xFF) | EndRow, 0);
                break;
            case 2: case 1:
                DrawLayerFull(0, hcA, planestart, planeend);
                DrawWindowFull(winstart, winend, 0);
                break;
            default: DrawLayerFull(0, hcA, StartRow, (maxcolc << 16) | EndRow); break;
        }
        DrawAllSpritesFull(0, maxw);
        if (hcB[1] != 0) DrawTilesFromCacheF(hcB);
        if (hcA[1] != 0) DrawTilesFromCacheF(hcA);
        switch (hvwin)
        {
            case 4: DrawWindowFull(StartRow, (maxcolc << 16) | EndRow, 1); break;
            case 3:
                DrawWindowFull(winstart & ~0xFF0000, (winend & ~0xFF0000) | (maxcolc << 16), 1);
                DrawWindowFull((winstart & ~0xFF) | StartRow, (winend & ~0xFF) | EndRow, 1);
                break;
            case 2: case 1: DrawWindowFull(winstart, winend, 1); break;
        }
        DrawAllSpritesFull(1, maxw);
    }

    /// <summary>PicoFrameFull: the whole frame from the VDP as it stands, into <paramref name="frame"/> (0xFFRRGGBB, <see cref="Width"/> x 224).</summary>
    public void Render(MdVdp vdp, uint[] frame)
    {
        v = vdp;
        System.Array.Fill(fb, (byte)(v.Reg[7] & 0x3F));
        if ((v.Reg[1] & 0x40) != 0) DrawDisplayFull();
        Width = (v.Reg[12] & 1) != 0 ? 320 : 256;
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int c = v.Cram[fb[(y + 8) * LineWidth + x + 8] & 0x3F];
                int r = (c >> 1) & 7, g = (c >> 5) & 7, b = (c >> 9) & 7;
                frame[y * Width + x] = 0xFF000000u | (uint)((r * 255 / 7) << 16 | (g * 255 / 7) << 8 | (b * 255 / 7));
            }
    }
}
