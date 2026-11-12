// Snes9xTylGfx - Snes9xTYL's "approx." software renderer (PSP), ported from 173210/snes9xTYL @0f82df6
// (https://github.com/173210/snes9xTYL/tree/0f82df610ef4e4c422bebd3ffb020f4c4887cd4f): gfx.cpp S9xUpdateScreen's own software path
// (the one TYL falls back to in its default "PSP accel + approx. soft" engine for Mode 7 and short line ranges, and uses alone in the
// "approx." engine), DrawBackgroundNew / DrawBackgroundOffsetNew / DrawBackgroundMode5New, DrawOBJSNew, the Mode 7 "New" drawers and the
// FIXCLIP / ON_MAIN / ON_SUB macros. Tile decoding, BG addressing, the Mode 7 maths and the window regions follow the Snes9x code this
// project already ports (Snes9xGfx.cs), which TYL inherits from Snes9x 1.39-1.43.
//
//   Snes9x (c) 1996-2002 Gary Henderson, Jerremy Koot and the Snes9x team (Snes9x licence: non-commercial use, notice kept);
//   Snes9xTYL: Yoyofr (TYL), the PSP port contributors, 173210 (repository). See THIRD_PARTY_NOTICES.md (PPU harvest).
//
// SNES picture core "TYL", an art instrument (PPU harvest climb, round 2): the stock S-PPU keeps the timing and memories; at each line
// this renderer draws the line the way the approx. path would, from the live registers.
//
// What the approx. path does, kept on purpose:
//  * NO colour math. With os9x_ShowSub = 0 (TYL's default, "both, MAIN has priority") the sub screen is not blended: every layer that is
//    on the sub screen (and $2130 lets the sub screen in) but not on the main screen is simply painted UNDER the main-screen layers,
//    opaque. Translucent water, fog, shadows and HUD fades become solid layers or vanish.
//  * Painter's algorithm, back to front, in TYL's fixed order (mode 0/1 with or without the BG3-priority bit, modes 2-6, mode 7), the
//    whole sub-only sequence first, then the whole main sequence. Within one sprite priority pass the lower OAM index wins.
//  * "mario hack": when $2131 adds (not subtracts) and the fixed colour is not black, the backdrop IS the fixed colour (not backdrop+fixed).
//  * Mosaic is ignored (its call is commented out). Mode 2 offset-per-tile and mode 4 go through the offset drawer; modes 5/6 are drawn
//    256 wide from the even pixels of each 16-pixel pair (DrawBackgroundMode5New's halving).
//  * Sprites: one list for all lines, no 32-sprite / 34-tile limits, square sprites only (the width is used as the height, so 16x32 /
//    32x64 sizes lose their bottom half), tile numbers advanced with a plain add (no 4-bit row/column wrap).
//  * Windows: a layer whose window masks the whole line has no clip regions left, and TYL then draws it on the WHOLE line
//    ("if (!clipcount) clipcount = 1" with Left 0 / Right 256). The colour window and its black clipping are ignored.
//  * Mode 7 EXTBG with sprites on draws only BG2 (two priority passes around the sprites), never BG1.
// Not ported: the PSP GU (hardware polygon) renderer that TYL's default engine prefers for most full frames, interlace, hires output.
using System;

namespace NesEmulator.Snes;

internal sealed class Snes9xTylGfx : ISnesHarvestPicture
{
    const int PPL = 256, MaxH = 239;
    private readonly PPU_SFC ppu;
    private PPU_SFC.DecodedRegs R;
    public readonly ushort[] Screen = new ushort[PPL * MaxH];
    private readonly byte[] sprZ = new byte[PPL];
    private readonly ushort[] screenColors = new ushort[256];
    private readonly (ushort hofs, ushort vofs)[,] lineData = new (ushort, ushort)[MaxH + 1, 4];
    private readonly (short a, short b, short c, short d, short cx, short cy, short h, short v)[] lineMatrix = new (short, short, short, short, short, short, short, short)[MaxH + 1];

    private ushort[] S = null!;
    private int sprIndex = -1;    // -1 = BG pixel (painter), else the OBJList index (lower wins)
    private int prioPass;         // the priority this DrawBackgroundNew / DrawOBJSNew pass draws
    private int tileAddress, nameSelect, tileShift, paletteShift, paletteMask, startPalette, depth;
    private int conv;             // 0 normal, 2 even pixels of a 16-pixel pair (modes 5/6)
    private bool directColour;
    private int tileSizeH, tileSizeV, offsetSizeH, offsetSizeV;
    private int clipSet;          // 0 = Clip[0] (main), 1 = Clip[1] (sub)

    private readonly int[,] clipCount = new int[2, 6];
    private readonly int[,,] clipLeft = new int[2, 6, 6], clipRight = new int[2, 6, 6];

    public Snes9xTylGfx(PPU_SFC p) { ppu = p; p.LineObserver += Line; p.FrameObserver += StartScreenRefresh; }
    public void Detach() { ppu.LineObserver -= Line; ppu.FrameObserver -= StartScreenRefresh; }
    public int Height => ppu.VisibleHeight;
    public int Width => 256;

    private void StartScreenRefresh() { R = ppu.Decoded; }

    private void Line(int row)
    {
        if (row < 0 || row >= MaxH) return;
        R = ppu.Decoded;
        lineData[row, 0] = (R.Hofs0, (ushort)(R.Vofs0 + 1)); lineData[row, 1] = (R.Hofs1, (ushort)(R.Vofs1 + 1));
        lineData[row, 2] = (R.Hofs2, (ushort)(R.Vofs2 + 1)); lineData[row, 3] = (R.Hofs3, (ushort)(R.Vofs3 + 1));
        lineMatrix[row] = (R.M7a, R.M7b, R.M7c, R.M7d, R.M7x, R.M7y, R.M7hofs, R.M7vofs);
        int o = row * PPL;
        S = Screen;
        if ((R.Inidisp & 0x80) != 0) { Array.Fill(Screen, (ushort)0, o, PPL); return; }   // ForcedBlanking: back = BLACK
        ComputeClipWindows();
        int bright = R.Inidisp & 0x0F;
        int XB(int c) => c * bright / 15;
        for (int i = 0; i < 256; i++) { int c = ppu.Cgram[i]; screenColors[i] = Build(XB(c & 0x1F), XB((c >> 5) & 0x1F), XB((c >> 10) & 0x1F)); }
        // "mario hack : fixed colour addition on backdrop colour"
        int fr = R.Coldata & 0x1F, fg = (R.Coldata >> 5) & 0x1F, fb = (R.Coldata >> 10) & 0x1F;
        ushort back = (R.Cgadsub & 0x80) == 0 && (R.Cgadsub & 0x20) != 0 && (fr | fg | fb) != 0 ? Build(XB(fr), XB(fg), XB(fb)) : screenColors[0];
        Array.Fill(Screen, back, o, PPL);
        RenderLine(row);
    }

    private static ushort Build(int r, int g, int b) => (ushort)(r << 10 | g << 5 | b);

    // ----------------------------------------------------------------- the approx. path's layer order (ShowSub = 0)
    private bool OnMain(int n) => (R.Tm & (1 << n)) != 0;
    private bool OnSub(int n) => (R.Cgwsel & 0x30) != 0x30 && (R.Cgwsel & 2) != 0 && (R.Ts & (1 << n)) != 0;

    private void RenderLine(int y)
    {
        bool anyMath = (R.Cgadsub & 0x3F) != 0;
        bool[] main = new bool[5], sub = new bool[5];
        for (int n = 0; n < 5; n++) { main[n] = OnMain(n); sub[n] = anyMath && OnSub(n) && !main[n]; }   // case 0: MAIN has priority
        int mode = R.Bgmode & 7;
        for (int pass = 0; pass < 2; pass++)
        {
            var on = pass == 0 ? sub : main;
            clipSet = pass == 0 ? 1 : 0;
            void Bg(int n, int p) { if (on[n]) DrawBackgroundNew(y, mode, n, p); }
            void Ob(int p) { if (on[4]) DrawOBJSNew(y, p); }
            if (mode <= 1)
            {
                if ((R.Bgmode & 8) != 0)
                {
                    if (mode == 0) Bg(3, 0); Bg(2, 0); Ob(0); if (mode == 0) Bg(3, 1); Ob(1); Bg(1, 0); Bg(0, 0); Ob(2); Bg(1, 1); Bg(0, 1); Ob(3); Bg(2, 1);
                }
                else
                {
                    if (mode == 0) Bg(3, 0); Bg(2, 0); Ob(0); if (mode == 0) Bg(3, 1); Bg(2, 1); Ob(1); Bg(1, 0); Bg(0, 0); Ob(2); Bg(1, 1); Bg(0, 1); Ob(3);
                }
            }
            else if (mode != 7)
            {
                if (mode != 6) Bg(1, 0); Ob(0); Bg(0, 0); Ob(1); if (mode != 6) Bg(1, 1); Ob(2); Bg(0, 1); Ob(3);
            }
            else
            {
                // Mod 7: FIXCLIP picks the clip set from TM alone, whichever pass this is
                bool ob = on[4];
                if (ob) { clipSet = (R.Tm & 0x10) != 0 ? 0 : 1; DrawOBJSNew(y, 0); }
                if (on[0] || ((R.Setini & 0x40) != 0 && on[1]))
                {
                    clipSet = (R.Tm & 1) != 0 ? 0 : 1;
                    if (ob && (R.Setini & 0x40) != 0)
                    {
                        DrawMode7(y, 1, 0);
                        clipSet = (R.Tm & 0x10) != 0 ? 0 : 1; DrawOBJSNew(y, 1);
                        clipSet = (R.Tm & 1) != 0 ? 0 : 1; DrawMode7(y, 1, 1);
                        clipSet = (R.Tm & 0x10) != 0 ? 0 : 1; DrawOBJSNew(y, 2); DrawOBJSNew(y, 3);
                    }
                    else
                    {
                        DrawMode7(y, 0, -1);
                        if (ob) { clipSet = (R.Tm & 0x10) != 0 ? 0 : 1; DrawOBJSNew(y, 1); DrawOBJSNew(y, 2); DrawOBJSNew(y, 3); }
                    }
                }
                else if (ob) { clipSet = (R.Tm & 0x10) != 0 ? 0 : 1; DrawOBJSNew(y, 1); DrawOBJSNew(y, 2); DrawOBJSNew(y, 3); }
            }
        }
    }

    /// <summary>The regions a layer draws in; none left means the whole line (TYL's "if (!clipcount) clipcount = 1").</summary>
    private int Regions(int layer, Span<(int l, int r)> dst)
    {
        int n = clipCount[clipSet, layer];
        if (n == 0) { dst[0] = (0, 256); return 1; }
        for (int i = 0; i < n; i++) dst[i] = (clipLeft[clipSet, layer, i], clipRight[clipSet, layer, i]);
        return n;
    }

    private void DrawPixel(int offset, int n, ushort color)
    {
        int i = offset + n;
        if ((uint)i >= (uint)S.Length) return;
        if (sprIndex >= 0)
        {
            int x = i % PPL;
            if (sprZ[x] <= sprIndex) return;
            sprZ[x] = (byte)sprIndex;
        }
        S[i] = color;
    }

    // ----------------------------------------------------------------- tiles
    private byte VB(int a) { int w = ppu.Vram[(a >> 1) & 0x7FFF]; return (byte)((a & 1) != 0 ? w >> 8 : w); }

    private int Px(int addr, int row, int x)
    {
        int bit = 7 - x, v = 0;
        int b = addr + row * 2;
        v |= ((VB(b) >> bit) & 1) | (((VB(b + 1) >> bit) & 1) << 1);
        if (depth >= 4) v |= (((VB(b + 16) >> bit) & 1) << 2) | (((VB(b + 17) >> bit) & 1) << 3);
        if (depth == 8) v |= (((VB(b + 32) >> bit) & 1) << 4) | (((VB(b + 33) >> bit) & 1) << 5) | (((VB(b + 48) >> bit) & 1) << 6) | (((VB(b + 49) >> bit) & 1) << 7);
        return v;
    }

    private int CachePixel(int tile, int row, int x)
    {
        int tileAddr = tileAddress + ((tile & 0x3FF) << tileShift);
        if ((tile & 0x100) != 0) tileAddr += nameSelect;
        tileAddr &= 0xFFFF;
        if (conv == 0) return Px(tileAddr, row, x);
        int next = (tile & 0x3FF) == 0x3FF ? tileAddr - (0x3FF << tileShift) : tileAddr + (1 << tileShift);
        int srcTile = x < 4 ? tileAddr : next;
        return Px(srcTile & 0xFFFF, row, (x & 3) * 2);
    }

    private readonly ushort[] directTmp = new ushort[256];
    private (ushort[] pal, int bas) SelectPalette(int tile)
    {
        if (directColour)
        {
            int p = (tile >> 10) & 7, bright = R.Inidisp & 0x0F;
            int XB(int c) => c * bright / 15;
            for (int c = 0; c < 256; c++) directTmp[c] = Build(XB(((c & 7) << 2) | ((p & 1) << 1)), XB(((c & 0x38) >> 1) | (p & 2)), XB(((c & 0xC0) >> 3) | (p & 4)));
            return (directTmp, 0);
        }
        return (screenColors, ((tile >> paletteShift) & paletteMask) + startPalette);
    }

    private void DrawClippedTile(int tile, int offset, int startPixel, int width, int virtAlign)
    {
        var (pal, bas) = SelectPalette(tile);
        int row = virtAlign >> 3;
        if ((tile & 0x8000) != 0) row = 7 - row;
        bool hf = (tile & 0x4000) != 0;
        for (int n = startPixel, w = width; w > 0 && n < 8; n++, w--)
        {
            int pix = CachePixel(tile, row, hf ? 7 - n : n);
            if (pix == 0) continue;
            DrawPixel(offset, n, pal[(bas + pix) & 0xFF]);
        }
    }

    private static int TilePlus(int t, int v) => (t & 0xFC00) | ((t + v) & 0x3FF);

    private void SelectConverter(int d, bool hires)
    {
        depth = d;
        conv = hires ? 2 : 0;
        switch (d)
        {
            case 8: tileShift = 6; paletteShift = 0; paletteMask = 0; break;
            case 4: tileShift = 5; paletteShift = 10 - 4; paletteMask = 7 << 4; break;
            default: tileShift = 4; paletteShift = 10 - 2; paletteMask = 7 << 2; break;
        }
    }

    // ----------------------------------------------------------------- DrawBackgroundNew
    private void DrawBackgroundNew(int y, int mode, int bg, int prio)
    {
        prioPass = prio; sprIndex = -1;
        int d = mode switch { 0 => 2, 1 => bg == 2 ? 2 : 4, 2 => 4, 3 => bg == 0 ? 8 : 4, 4 => bg == 0 ? 8 : 2, 5 => bg == 0 ? 4 : 2, _ => 4 };
        startPalette = mode == 0 ? bg * 32 : 0;
        bool hires = mode is 5 or 6;
        SelectConverter(d, hires);
        directColour = (mode == 3 || mode == 4) && bg == 0 && (R.Cgwsel & 1) != 0;
        bool size = ((R.Bgmode >> (4 + bg)) & 1) != 0;
        tileSizeH = !hires && size ? 16 : 8; tileSizeV = size ? 16 : 8;
        nameSelect = 0;
        // mosaic: "//DrawBackgroundMosaic (BGMode, bg, Z1, Z2); //return;" - ignored
        if (mode == 4 || mode == 2 || mode == 6)
        {
            bool s3 = ((R.Bgmode >> 6) & 1) != 0;
            offsetSizeH = !hires && s3 ? 16 : 8; offsetSizeV = s3 ? 16 : 8;
            DrawBackgroundOffset(y, bg, mode == 4 ? 0 : 8);
        }
        else DrawBackground(y, bg);
    }

    private (int sc0, int sc1, int sc2, int sc3) Screens(int bg)
    {
        byte sc = bg switch { 0 => R.Bgsc0, 1 => R.Bgsc1, 2 => R.Bgsc2, _ => R.Bgsc3 };
        int baseW = (sc & 0x7C) << 8, sz = sc & 3;
        int sc0 = baseW, sc1 = (sz & 1) != 0 ? sc0 + 1024 : sc0; sc1 &= 0x7FFF;
        int sc2 = (sz & 2) != 0 ? sc1 + 1024 : sc0; sc2 &= 0x7FFF;
        int sc3 = (sz & 1) != 0 ? sc2 + 1024 : sc2; sc3 &= 0x7FFF;
        return (sc0, sc1, sc2, sc3);
    }
    private int NameBase(int bg) => bg switch { 0 => (R.Bg12nba & 0xF) << 12, 1 => (R.Bg12nba >> 4) << 12, 2 => (R.Bg34nba & 0xF) << 12, _ => (R.Bg34nba >> 4) << 12 };
    private int W(int wordAddr) => ppu.Vram[wordAddr & 0x7FFF];

    private void TileAt(int tile, int t1, int t2, int hTile, int offset, int startPixel, int w, int virtAlign)
    {
        if (((tile & 0x2000) != 0 ? 1 : 0) != prioPass) return;
        if (tileSizeV == 16) tile = TilePlus(tile, (tile & 0x8000) != 0 ? t2 : t1);
        if (tileSizeH == 8) DrawClippedTile(tile, offset, startPixel, w, virtAlign);
        else DrawClippedTile(TilePlus(tile, (tile & 0x4000) == 0 ? (hTile & 1) : 1 - (hTile & 1)), offset, startPixel, w, virtAlign);
    }

    private void DrawBackground(int Y, int bg)
    {
        tileAddress = NameBase(bg) << 1;
        var (sc0, sc1, sc2, sc3) = Screens(bg);
        int offsetMask = tileSizeH == 16 ? 0x3FF : 0x1FF, offsetShift = tileSizeV == 16 ? 4 : 3;
        Span<(int l, int r)> regs = stackalloc (int, int)[6];
        int nr = Regions(bg, regs);
        for (int clip = 0; clip < nr; clip++)
        {
            int vOffset = lineData[Y, bg].vofs, hOffset = lineData[Y, bg].hofs;
            int virtAlign = ((Y + vOffset) & 7) << 3;
            int tilemapRow = (vOffset + Y) >> offsetShift;
            int t1, t2; if (((vOffset + Y) & 8) != 0) { t1 = 16; t2 = 0; } else { t1 = 0; t2 = 16; }
            int b1, b2; if ((tilemapRow & 0x20) != 0) { b1 = sc2; b2 = sc3; } else { b1 = sc0; b2 = sc1; }
            b1 += (tilemapRow & 0x1F) << 5; b2 += (tilemapRow & 0x1F) << 5;
            int left = regs[clip].l, right = regs[clip].r;
            // modes 5/6: an 8-pixel output tile is one 16-pixel hires pair, halved to its even pixels
            int offset = left + Y * PPL;
            int hPos = (hOffset + left) & offsetMask, hTile = hPos >> 3;
            int t = tileSizeH == 8 ? (hTile > 31 ? b2 + (hTile & 0x1F) : b1 + hTile) : (hTile > 63 ? b2 + ((hTile >> 1) & 0x1F) : b1 + (hTile >> 1));
            int width = right - left;
            void Advance()
            {
                if (tileSizeH == 8) { t++; if (hTile == 31) t = b2; else if (hTile == 63) t = b1; }
                else { t += hTile & 1; if (hTile == 63) t = b2; else if (hTile == 127) t = b1; }
                hTile++;
            }
            if ((hPos & 7) != 0)
            {
                int l = hPos & 7, w = Math.Min(8 - l, width);
                offset -= l;
                TileAt(W(t), t1, t2, hTile, offset, l, w, virtAlign); Advance();
                offset += 8; width -= w;
            }
            while (width >= 8) { TileAt(W(t), t1, t2, hTile, offset, 0, 8, virtAlign); Advance(); offset += 8; width -= 8; }
            if (width > 0) TileAt(W(t), t1, t2, hTile, offset, 0, width, virtAlign);
        }
    }

    private void DrawBackgroundOffset(int Y, int bg, int voffoff)
    {
        tileAddress = NameBase(bg) << 1;
        var (bps0, bps1, bps2, bps3) = Screens(2);
        var (sc0, sc1, sc2, sc3) = Screens(bg);
        int offsetMask = tileSizeH == 16 ? 0x3FF : 0x1FF, offsetShift = tileSizeV == 16 ? 4 : 3;
        int offset2Mask = offsetSizeH == 16 ? 0x3FF : 0x1FF, offset2Shift = offsetSizeV == 16 ? 4 : 3;
        int enableMask = 0x2000 << bg;
        Span<(int l, int r)> regs = stackalloc (int, int)[6];
        int nr = Regions(bg, regs);
        for (int clip = 0; clip < nr; clip++)
        {
            int vOff = lineData[Y, 2].vofs - 1, hOff = lineData[Y, 2].hofs;
            int hOffsetRow = vOff >> offset2Shift, vOffsetRow = (vOff + voffoff) >> offset2Shift;
            int s1, s2; if ((hOffsetRow & 0x20) != 0) { s1 = bps2; s2 = bps3; } else { s1 = bps0; s2 = bps1; }
            s1 += (hOffsetRow & 0x1F) << 5; s2 += (hOffsetRow & 0x1F) << 5;
            int sv = ((vOffsetRow & 0x20) != 0 ? bps2 : bps0) + ((vOffsetRow & 0x1F) << 5);
            int vOffsetOffset = sv - s1;
            int left = regs[clip].l, right = regs[clip].r;
            int offset = left + Y * PPL;
            int lineHOffset = lineData[Y, bg].hofs;
            bool leftEdge = left < 8 - (lineHOffset & 7);
            int width = right - left;
            while (left < right)
            {
                int vOffset, hOffset;
                if (leftEdge) { vOffset = lineData[Y, bg].vofs; hOffset = lineHOffset; leftEdge = false; }
                else
                {
                    int hOffTile = ((hOff + left - 1) & offset2Mask) >> 3;
                    int sp = offsetSizeH == 8 ? (hOffTile > 31 ? s2 + (hOffTile & 0x1F) : s1 + hOffTile) : (hOffTile > 63 ? s2 + ((hOffTile >> 1) & 0x1F) : s1 + (hOffTile >> 1));
                    int hCell = W(sp), vCell;
                    if (voffoff != 0) vCell = W(sp + vOffsetOffset);
                    else if ((hCell & 0x8000) != 0) { vCell = hCell; hCell = 0; }
                    else vCell = 0;
                    vOffset = (vCell & enableMask) != 0 ? vCell + 1 : lineData[Y, bg].vofs;
                    hOffset = (hCell & enableMask) != 0 ? (hCell & ~7) | (lineHOffset & 7) : lineHOffset;
                }
                int virtAlign = ((Y + vOffset) & 7) << 3;
                int tilemapRow = (vOffset + Y) >> offsetShift;
                int t1, t2; if (((vOffset + Y) & 8) != 0) { t1 = 16; t2 = 0; } else { t1 = 0; t2 = 16; }
                int b1, b2; if ((tilemapRow & 0x20) != 0) { b1 = sc2; b2 = sc3; } else { b1 = sc0; b2 = sc1; }
                b1 += (tilemapRow & 0x1F) << 5; b2 += (tilemapRow & 0x1F) << 5;
                int hPos = (hOffset + left) & offsetMask, hTile = hPos >> 3;
                int t = tileSizeH == 8 ? (hTile > 31 ? b2 + (hTile & 0x1F) : b1 + hTile) : (hTile > 63 ? b2 + ((hTile >> 1) & 0x1F) : b1 + (hTile >> 1));
                int l = hPos & 7, w = Math.Min(8 - l, width);
                offset -= l;
                TileAt(W(t), t1, t2, hTile, offset, l, w, virtAlign);
                left += w; offset += 8; width -= w;
            }
        }
    }

    // ----------------------------------------------------------------- Mode 7 (DrawBGMode7Background16New / ...PrioNew)
    private static int Clip10(int a) => (a & 0x2000) != 0 ? (a | ~0x3FF) : (a & 0x3FF);

    /// <param name="prio">-1: BG1 with all 8 bits (Mode7Mask 0xFF); 0/1: BG2 pixels whose bit 7 is that priority (mask 0x7F).</param>
    private void DrawMode7(int line, int bg, int prio)
    {
        sprIndex = -1;
        Span<(int l, int r)> regs = stackalloc (int, int)[6];
        int nr = Regions(bg, regs);
        bool dc = bg == 0 && (R.Cgwsel & 1) != 0;
        if (dc) { directColour = true; SelectPalette(0); }
        var pal = dc ? directTmp : screenColors;
        var l = lineMatrix[line];
        int hOffset = (l.h << 19) >> 19, vOffset = (l.v << 19) >> 19, centreX = (l.cx << 19) >> 19, centreY = (l.cy << 19) >> 19;
        int starty = (R.M7sel & 2) != 0 ? 255 - (line + 1) : line + 1;
        int yy = Clip10(vOffset - centreY);
        int BB = ((l.b * starty) & ~63) + ((l.b * yy) & ~63) + (centreX << 8);
        int DD = ((l.d * starty) & ~63) + ((l.d * yy) & ~63) + (centreY << 8);
        int repeat = R.M7sel >> 6;
        int o = line * PPL;
        for (int clip = 0; clip < nr; clip++)
        {
            int left = regs[clip].l, right = regs[clip].r;
            int startx, aa, cc;
            if ((R.M7sel & 1) != 0) { startx = right - 1; aa = -l.a; cc = -l.c; } else { startx = left; aa = l.a; cc = l.c; }
            int xx = Clip10(hOffset - centreX);
            int AA = l.a * startx + ((l.a * xx) & ~63), CC = l.c * startx + ((l.c * xx) & ~63);
            for (int x = left; x < right; x++, AA += aa, CC += cc)
            {
                int X = (AA + BB) >> 8, Yv = (CC + DD) >> 8;
                int b;
                if (repeat == 0) { X &= 0x3FF; Yv &= 0x3FF; b = Mode7Pixel(X, Yv); }
                else if (((X | Yv) & ~0x3FF) == 0) b = Mode7Pixel(X, Yv);
                else if (repeat == 3) b = VB(1 + ((Yv & 7) << 4) + ((X & 7) << 1));
                else continue;
                if (prio >= 0 && ((b >> 7) & 1) != prio) continue;
                int pix = prio < 0 ? b & 0xFF : b & 0x7F;
                if (pix == 0) continue;
                DrawPixel(o, x, pal[pix]);
            }
        }
        directColour = false;
    }
    private int Mode7Pixel(int X, int Y) { int tile = VB(((Y & ~7) << 5) + ((X >> 2) & ~1)); return VB(1 + (tile << 7) + ((Y & 7) << 4) + ((X & 7) << 1)); }

    // ----------------------------------------------------------------- DrawOBJSNew (one OBJList for the frame, square sprites)
    private static readonly int[,] objSizes = { { 8, 8, 16, 16 }, { 8, 8, 32, 32 }, { 8, 8, 64, 64 }, { 16, 16, 32, 32 }, { 16, 16, 64, 64 }, { 32, 32, 64, 64 }, { 16, 32, 32, 64 }, { 16, 32, 32, 32 } };

    private void DrawOBJSNew(int Y, int prio)
    {
        var oam = ppu.Oam;
        tileAddress = (R.Obsel & 3) << 14; nameSelect = ((R.Obsel >> 3) & 3) << 13;
        startPalette = 128; directColour = false;
        SelectConverter(4, false);
        int sel = R.Obsel >> 5;
        int first = (R.Oamadd & 0x8000) != 0 ? (R.Oamadd >> 1) & 0x7F : 0;
        Array.Fill(sprZ, (byte)0xFF);   // the sprite Z of one priority pass
        Span<(int l, int r)> regs = stackalloc (int, int)[6];
        int nr = Regions(4, regs);
        int o = Y * PPL;
        for (int I = 0; I < 128; I++)
        {
            int Sx = (first + I) & 0x7F;
            int attr = oam[Sx * 4 + 3];
            if (((attr >> 4) & 3) != prio) continue;
            int hi = (oam[512 + (Sx >> 2)] >> ((Sx & 3) * 2)) & 3;
            int size = (hi & 2) != 0 ? objSizes[sel, 2] : objSizes[sel, 0];   // GFX.Sizes: the width, used for both directions
            int hpos = oam[Sx * 4] | ((hi & 1) << 8); if (hpos >= 256) hpos -= 512;
            int vpos = oam[Sx * 4 + 1]; if (vpos >= 240) vpos -= 256;
            int lineIn = Y - vpos;
            if (lineIn < 0 || lineIn >= size) continue;
            int name = oam[Sx * 4 + 2] | ((attr & 1) << 8), pal = (attr >> 1) & 7;
            int baseTile = name | (pal << 10), inc = 1;
            if ((attr & 0x40) != 0) { baseTile += ((size >> 3) - 1) | 0x4000; inc = -1; }
            if ((attr & 0x80) != 0) baseTile |= 0x8000;
            int yb = lineIn & ~7;
            int tile = (attr & 0x80) == 0 ? baseTile + (yb << 1) : baseTile + ((size - yb - 8) << 1);
            int tileLine = (lineIn & 7) << 3;
            sprIndex = I;
            for (int clip = 0; clip < nr; clip++)
            {
                int left = regs[clip].l, right = regs[clip].r;
                if (right <= left || hpos + size <= left || hpos >= right) continue;
                for (int c = 0, tx = hpos, tt = tile; c < (size >> 3); c++, tx += 8, tt += inc)
                {
                    int sp = Math.Max(0, left - tx), ep = Math.Min(8, right - tx);
                    if (ep <= sp) continue;
                    DrawClippedTile(tt, o + tx, sp, ep - sp, tileLine);
                }
            }
        }
        sprIndex = -1;
    }

    // ----------------------------------------------------------------- clip regions (Snes9x clip.cpp, the colour window left out)
    private static readonly byte[,] regionMap = { { 0, 0x01, 0x03, 0x07, 0x0f, 0x1f }, { 0, 0, 0x02, 0x06, 0x0e, 0x1e }, { 0, 0, 0, 0x04, 0x0c, 0x1c }, { 0, 0, 0, 0, 0x08, 0x18 }, { 0, 0, 0, 0, 0, 0x10 }, { 0, 0, 0, 0, 0, 0 } };

    private byte WinSel(int i) => (byte)(i switch { 0 => R.W12sel & 0xF, 1 => R.W12sel >> 4, 2 => R.W34sel & 0xF, 3 => R.W34sel >> 4, 4 => R.Wobjsel & 0xF, _ => R.Wobjsel >> 4 });
    private int Logic(int i) => i < 4 ? (R.Wbglog >> (i * 2)) & 3 : (R.Wobjlog >> ((i - 4) * 2)) & 3;

    private byte CalcWindowMask(int i, byte w1, byte w2)
    {
        int sel = WinSel(i);
        bool e1 = (sel & 2) != 0, in1 = (sel & 1) == 0, e2 = (sel & 8) != 0, in2 = (sel & 4) == 0;
        if (!e1) { if (!e2) return 0; return in2 ? w2 : (byte)~w2; }
        if (!e2) return in1 ? w1 : (byte)~w1;
        if (!in1) w1 = (byte)~w1; if (!in2) w2 = (byte)~w2;
        return Logic(i) switch { 0 => (byte)(w1 | w2), 1 => (byte)(w1 & w2), 2 => (byte)(w1 ^ w2), _ => (byte)~(w1 ^ w2) };
    }

    private void ComputeClipWindows()
    {
        int[] windows = { 0, 256, 256, 256, 256, 256, 256 };
        int n = 1, i, j;
        int w1l = R.Wh0, w1r = R.Wh1, w2l = R.Wh2, w2r = R.Wh3;
        if (w1l <= w1r)
        {
            if (w1l > 0) { windows[2] = 256; windows[1] = w1l; n = 2; }
            if (w1r < 255) { windows[n + 1] = 256; windows[n] = w1r + 1; n++; }
        }
        if (w2l <= w2r)
        {
            for (i = 0; i <= n; i++)
            {
                if (w2l == windows[i]) break;
                if (w2l < windows[i]) { for (j = n; j >= i; j--) windows[j + 1] = windows[j]; windows[i] = w2l; n++; break; }
            }
            for (; i <= n; i++)
            {
                if (w2r + 1 == windows[i]) break;
                if (w2r + 1 < windows[i]) { for (j = n; j >= i; j--) windows[j + 1] = windows[j]; windows[i] = w2r + 1; n++; break; }
            }
        }
        byte W1 = 0, W2 = 0;
        if (w1l <= w1r) { for (i = 0; i < 6 && windows[i] != w1l; i++) ; for (j = i; j < 6 && windows[j] != w1r + 1; j++) ; if (i < 6 && j < 6) W1 = regionMap[i, j]; }
        if (w2l <= w2r) { for (i = 0; i < 6 && windows[i] != w2l; i++) ; for (j = i; j < 6 && windows[j] != w2r + 1; j++) ; if (i < 6 && j < 6) W2 = regionMap[i, j]; }
        for (j = 0; j < 5; j++)
        {
            byte w = CalcWindowMask(j, W1, W2);
            for (int sub = 0; sub < 2; sub++)
            {
                byte en = sub == 0 ? R.Tmw : R.Tsw;
                byte mask = (en & (1 << j)) != 0 ? w : (byte)0;
                int ct = 0;
                for (int k = 0; k < n; k++)
                {
                    if ((mask & (1 << k)) != 0) continue;
                    if (ct > 0 && clipRight[sub, j, ct - 1] == windows[k]) clipRight[sub, j, ct - 1] = windows[k + 1];
                    else { clipLeft[sub, j, ct] = windows[k]; clipRight[sub, j, ct] = windows[k + 1]; ct++; }
                }
                clipCount[sub, j] = ct;
            }
        }
    }

    public void CopyTo(uint[] frame)
    {
        int w = Width, h = Math.Min(Height, MaxH);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int c = Screen[y * PPL + x];
                int r = (c >> 10) & 31, g = (c >> 5) & 31, b = c & 31;
                frame[y * w + x] = 0xFF000000u | (uint)((r << 3 | r >> 2) << 16 | (g << 3 | g >> 2) << 8 | (b << 3 | b >> 2));
            }
    }
}
