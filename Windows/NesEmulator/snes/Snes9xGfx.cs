// Snes9xGfx - Snes9x's picture renderer before anomie's hires fix, ported from libretro/snes9x @88f3702 (the parent of 795542d,
// 2013-01-12): gfx.cpp (S9xStartScreenRefresh, RenderLine, S9xUpdateScreen, RenderScreen, SetupOBJ, DrawOBJS, DrawBackground,
// DrawBackgroundMosaic, DrawBackgroundOffset, DrawBackgroundMode7, DrawBackdrop), tile.cpp (the tile converters ConvertTile2/4/8 and
// the hires odd/even ones, GET_CACHED_TILE, SELECT_PALETTE, the DrawTile16 / DrawClippedTile16 / DrawMosaicPixel16 / DrawBackdrop16 /
// DrawMode7BG1/BG2 renderers in their Normal1x1, Normal2x1 and Hires variants, the seven math modes NOMATH, REGMATH, MATHF1_2, MATHS1_2)
// and clip.cpp (S9xComputeClipWindows, CalcWindowMask, StoreWindowRegions), with gfx.h's COLOR_ADD/ADD1_2/SUB/SUB1_2.
//
//   Snes9x - Portable Super Nintendo Entertainment System (TM) emulator. (c) Copyright 1996 - 2002 Gary Henderson, Jerremy Koot;
//   2002 - 2011 Matthew Kendora, Peter Bortas, Joel Yliluoma, John Weidman, funkyass, Kris Bleakley, Brad Jorsch (anomie), Nach, zones,
//   nitsuja, BearOso, OV2 and the others named in the file headers. Snes9x licence: non-commercial use only, this notice kept with
//   any derived work. See THIRD_PARTY_NOTICES.md (PPU harvest) and snes/THIRD_PARTY_NOTICES.md.
//
// PPU harvest climb (docs/projects/ppu-harvest-catalog.md, #9): an art instrument, ported FAITHFULLY with its flaws. It is the SNES picture
// core "S9X": the stock S-PPU (PPU_SFC) keeps the timing, VRAM/CGRAM/OAM and the register decoding; at each line it draws (LineObserver)
// this renderer flushes that line the way S9xUpdateScreen would, from the live registers.
//
// Characteristic jank, all of it kept on purpose:
//  * The hires pixel logic BEFORE anomie's fix (795542d): DRAW_PIXEL_H2x1 writes the even column as MATH(sub screen pixel, this pixel's
//    REAL colour) and the odd column as MATH(this pixel, sub screen); with the colour window clipping, the even column gets black as its
//    "main". Main-screen layers convert their tiles from the ODD pixels of a 16-pixel pair, the sub screen and mosaic from the EVEN ones,
//    and a flipped tile takes the other parity. Sprites on the main screen in hires go through the same H2x1 writer.
//  * The frame turns 512 wide at the first hires line; the lines drawn before it are doubled after the fact ("back out of the speed hack").
//  * Colour math on 5-bit channels with Snes9x's averaging (ADD1_2 drops a low bit, SUB1_2 halves before clamping); a clipped colour is
//    black, and "half" math with the colour window clipping falls back to full math against the fixed colour.
//  * Offset-per-tile never applies to the leftmost tile column; mosaic + offset-per-tile is drawn as offset-per-tile (DrawBackgroundOffset).
//  * Sprites: the time-over budget of 34 tiles counts every sprite's visible tiles in list order and drops the earliest-listed tiles;
//    an X of -256 is treated as 256 (hidden) on the normal path.
// Not ported: interlace (the even field only), the FirstSprite+Y priority-rotation path (the normal path is used), Snes9x's own register
// latches (the stock chip's decoded values are read) and its RGB565 green bit (colours are 5 bits a channel throughout).
using System;

namespace NesEmulator.Snes;

internal sealed class Snes9xGfx : ISnesHarvestPicture
{
    const int PPL = 512, MaxH = 239;
    private readonly PPU_SFC ppu;
    private PPU_SFC.DecodedRegs R;
    public readonly ushort[] Screen = new ushort[PPL * MaxH];
    private readonly ushort[] subScreen = new ushort[PPL * MaxH];
    private readonly byte[] zbuf = new byte[PPL * MaxH], subZ = new byte[PPL * MaxH];
    private readonly ushort[] screenColors = new ushort[256];
    private static readonly ushort[] blackMap = new ushort[256];
    private ushort fixedColour;
    public bool DoubleWidthPixels;
    private readonly (ushort hofs, ushort vofs)[,] lineData = new (ushort, ushort)[MaxH + 1, 4];
    private readonly (short a, short b, short c, short d, short cx, short cy, short h, short v)[] lineMatrix = new (short, short, short, short, short, short, short, short)[MaxH + 1];

    // Per draw: GFX/BG globals
    private ushort[] S = null!; private byte[] DB = null!;
    private byte z1, z2;
    private bool clipColors;
    private int mathMode;         // 0..6 (NOMATH, ADD, ADDF1_2, ADDS1_2, SUB, SUBF1_2, SUBS1_2)
    private int kind;             // 0 Normal1x1, 1 Normal2x1, 2 Hires
    private int tileAddress, nameSelect, tileShift, paletteShift, paletteMask, startPalette, depth;
    private int conv, convFlip;   // 0 normal, 1 h_odd, 2 h_even
    private bool directColour, enableMath;
    private int tileSizeH, tileSizeV, offsetSizeH, offsetSizeV;

    // Clip windows: [sub][0..5]
    private readonly int[,] clipCount = new int[2, 6];
    private readonly int[,,] clipLeft = new int[2, 6, 6], clipRight = new int[2, 6, 6], clipMode = new int[2, 6, 6];

    public Snes9xGfx(PPU_SFC p) { ppu = p; p.LineObserver += Line; p.FrameObserver += StartScreenRefresh; }
    public void Detach() { ppu.LineObserver -= Line; ppu.FrameObserver -= StartScreenRefresh; }
    public int Height => ppu.VisibleHeight;
    public int Width => DoubleWidthPixels ? 512 : 256;

    private bool Hires => (R.Bgmode & 7) is 5 or 6 || (R.Setini & 8) != 0;

    private void StartScreenRefresh()
    {
        R = ppu.Decoded;
        DoubleWidthPixels = Hires;
        Array.Clear(zbuf); Array.Clear(subZ);
    }

    // ----------------------------------------------------------------- RenderLine + S9xUpdateScreen for one line
    private void Line(int row)
    {
        if (row < 0 || row >= MaxH) return;
        R = ppu.Decoded;
        lineData[row, 0] = (R.Hofs0, (ushort)(R.Vofs0 + 1)); lineData[row, 1] = (R.Hofs1, (ushort)(R.Vofs1 + 1));
        lineData[row, 2] = (R.Hofs2, (ushort)(R.Vofs2 + 1)); lineData[row, 3] = (R.Hofs3, (ushort)(R.Vofs3 + 1));
        lineMatrix[row] = (R.M7a, R.M7b, R.M7c, R.M7d, R.M7x, R.M7y, R.M7hofs, R.M7vofs);
        int o = row * PPL;
        if ((R.Inidisp & 0x80) != 0) { Array.Fill(Screen, (ushort)0, o, PPL); return; }
        ComputeClipWindows();
        if (!DoubleWidthPixels && Hires)
        {
            for (int y = 0; y < row; y++) { int b = y * PPL; for (int x = 255; x >= 0; x--) Screen[b + 2 * x] = Screen[b + 2 * x + 1] = Screen[b + x]; }
            DoubleWidthPixels = true;
        }
        int bright = R.Inidisp & 0x0F;
        int XB(int c) => c * bright / 15;
        for (int i = 0; i < 256; i++) { int c = ppu.Cgram[i]; screenColors[i] = Build(XB(c & 0x1F), XB((c >> 5) & 0x1F), XB((c >> 10) & 0x1F)); }
        fixedColour = Build(XB(R.Coldata & 0x1F), XB((R.Coldata >> 5) & 0x1F), XB((R.Coldata >> 10) & 0x1F));
        // zbuffers for this line start empty (the line is drawn once)
        Array.Clear(zbuf, o, PPL); Array.Clear(subZ, o, PPL);
        if (Hires || ((R.Cgwsel & 0x30) != 0x30 && (R.Cgwsel & 2) != 0 && (R.Cgadsub & 0x3F) != 0 && (R.Ts & 0x1F) != 0))
            RenderScreen(row, true);
        else Array.Clear(subScreen, o, PPL);
        RenderScreen(row, false);
    }

    private static ushort Build(int r, int g, int b) => (ushort)(r << 10 | g << 5 | b);

    // ----------------------------------------------------------------- colour math (gfx.h)
    private static ushort Add(ushort a, ushort b) => Build(Math.Min(31, (a >> 10) + (b >> 10)), Math.Min(31, ((a >> 5) & 31) + ((b >> 5) & 31)), Math.Min(31, (a & 31) + (b & 31)));
    private static ushort Add12(ushort a, ushort b) => Build(((a >> 10) + (b >> 10)) >> 1, (((a >> 5) & 31) + ((b >> 5) & 31)) >> 1, ((a & 31) + (b & 31)) >> 1);
    private static ushort Sub(ushort a, ushort b) => Build(Math.Max(0, (a >> 10) - (b >> 10)), Math.Max(0, ((a >> 5) & 31) - ((b >> 5) & 31)), Math.Max(0, (a & 31) - (b & 31)));
    private static ushort Sub12(ushort a, ushort b) => Build(Math.Max(0, (a >> 10) - ((b >> 10) & ~1)) >> 1, Math.Max(0, ((a >> 5) & 31) - (((b >> 5) & 31) & ~1)) >> 1, Math.Max(0, (a & 31) - ((b & 31) & ~1)) >> 1);

    private ushort Math_(ushort main, ushort sub, byte sd)
    {
        switch (mathMode)
        {
            case 0: return main;
            case 1: return Add(main, (sd & 0x20) != 0 ? sub : fixedColour);
            case 2: return clipColors ? Add(main, fixedColour) : Add12(main, fixedColour);
            case 3: return clipColors ? Add(main, (sd & 0x20) != 0 ? sub : fixedColour) : ((sd & 0x20) != 0 ? Add12(main, sub) : Add(main, fixedColour));
            case 4: return Sub(main, (sd & 0x20) != 0 ? sub : fixedColour);
            case 5: return clipColors ? Sub(main, fixedColour) : Sub12(main, fixedColour);
            default: return clipColors ? Sub(main, (sd & 0x20) != 0 ? sub : fixedColour) : ((sd & 0x20) != 0 ? Sub12(main, sub) : Sub(main, fixedColour));
        }
    }

    // DRAW_PIXEL for the three writers; realColor = GFX.RealScreenColors[Pix], color = GFX.ScreenColors[Pix]
    private void DrawPixel(int offset, int n, ushort color, ushort realColor)
    {
        if (kind == 0)
        {
            int i = offset + n;
            if ((uint)i >= (uint)S.Length || z1 <= DB[i]) return;
            S[i] = Math_(color, subScreen[i], subZ[i]); DB[i] = z2;
        }
        else
        {
            int i = offset + 2 * n;
            if ((uint)(i + 1) >= (uint)S.Length || z1 <= DB[i]) return;
            if (kind == 1) S[i] = S[i + 1] = Math_(color, subScreen[i], subZ[i]);
            else
            {
                S[i] = Math_(clipColors ? (ushort)0 : subScreen[i], realColor, subZ[i]);
                S[i + 1] = Math_(color, subScreen[i], subZ[i]);
            }
            DB[i] = DB[i + 1] = z2;
        }
    }

    // ----------------------------------------------------------------- tile converters (pixels of one row)
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

    /// <summary>pCache[row*8 + x] of GET_CACHED_TILE for this Tile.</summary>
    private int CachePixel(int tile, int row, int x)
    {
        int tileAddr = tileAddress + ((tile & 0x3FF) << tileShift);
        if ((tile & 0x100) != 0) tileAddr += nameSelect;
        tileAddr &= 0xFFFF;
        int c = (tile & 0x4000) != 0 ? convFlip : conv;
        if (c == 0) return Px(tileAddr, row, x);
        // ConvertTile{2,4}h_odd / _even: the 8 cached pixels are every other pixel of this tile and the next one.
        int next = (tile & 0x3FF) == 0x3FF ? tileAddr - (0x3FF << tileShift) : tileAddr + (1 << tileShift);
        int srcTile = x < 4 ? tileAddr : next;
        int srcX = (x & 3) * 2 + (c == 1 ? 1 : 0);
        return Px(srcTile & 0xFFFF, row, srcX);
    }

    private (ushort[] real, int bas) SelectPalette(int tile)
    {
        if (directColour) return (DirectMap((tile >> 10) & 7), 0);
        return (screenColors, ((tile >> paletteShift) & paletteMask) + startPalette);
    }

    private readonly ushort[] directTmp = new ushort[256];
    private ushort[] DirectMap(int p)
    {
        int bright = R.Inidisp & 0x0F;
        int XB(int c) => c * bright / 15;
        for (int c = 0; c < 256; c++) directTmp[c] = Build(XB(((c & 7) << 2) | ((p & 1) << 1)), XB(((c & 0x38) >> 1) | (p & 2)), XB(((c & 0xC0) >> 3) | (p & 4)));
        return directTmp;
    }

    // DrawClippedTile16 (DrawTile16 is the same with StartPixel 0, Width 8), one line.
    private void DrawClippedTile(int tile, int offset, int startPixel, int width, int virtAlign)
    {
        var (real, bas) = SelectPalette(tile);
        int row = virtAlign >> 3;
        if ((tile & 0x8000) != 0) row = 7 - row;
        bool hf = (tile & 0x4000) != 0;
        for (int n = startPixel, w = width; w > 0 && n < 8; n++, w--)
        {
            int pix = CachePixel(tile, row, hf ? 7 - n : n);
            if (pix == 0) continue;
            ushort rc = real[(bas + pix) & 0xFF];
            DrawPixel(offset, n, clipColors ? blackMap[0] : rc, rc);
        }
    }

    private void DrawMosaicPixel(int tile, int offset, int virtAlign, int startPixel, int width)
    {
        var (real, bas) = SelectPalette(tile);
        if ((tile & 0x4000) != 0) startPixel = 7 - startPixel;
        int row = virtAlign >> 3; if ((tile & 0x8000) != 0) row = 7 - row;
        int pix = CachePixel(tile, row, startPixel);
        if (pix == 0) return;
        ushort rc = real[(bas + pix) & 0xFF];
        for (int w = width - 1; w >= 0; w--) DrawPixel(offset, w, clipColors ? blackMap[0] : rc, rc);
    }

    private static int TilePlus(int t, int v) => (t & 0xFC00) | ((t + v) & 0x3FF);

    // ----------------------------------------------------------------- S9xSelectTileRenderers / S9xSelectTileConverter
    private void SelectRenderers(bool sub, bool obj)
    {
        bool hires = !sub && Hires;
        kind = !DoubleWidthPixels ? 0 : hires ? 2 : 1;
        int i = (R.Cgadsub & 0x80) != 0 ? 4 : 1;
        if ((R.Cgadsub & 0x40) != 0) { i++; if ((R.Cgwsel & 2) != 0) i++; }
        mathIndex = i;
    }
    private int mathIndex;

    private void SelectConverter(int d, bool hires, bool sub, bool mosaic)
    {
        depth = d;
        switch (d)
        {
            case 8: conv = convFlip = 0; tileShift = 6; paletteShift = 0; paletteMask = 0; directColour = (R.Cgwsel & 1) != 0; break;
            case 4: if (hires) { if (sub || mosaic) { conv = 2; convFlip = 1; } else { conv = 1; convFlip = 2; } } else conv = convFlip = 0;
                tileShift = 5; paletteShift = 10 - 4; paletteMask = 7 << 4; directColour = false; break;
            default: if (hires) { if (sub || mosaic) { conv = 2; convFlip = 1; } else { conv = 1; convFlip = 2; } } else conv = convFlip = 0;
                tileShift = 4; paletteShift = 10 - 2; paletteMask = 7 << 2; directColour = false; break;
        }
    }

    private void SetMath(int drawMode) { clipColors = (drawMode & 1) == 0; mathMode = enableMath && (drawMode & 2) != 0 ? mathIndex : 0; }

    // ----------------------------------------------------------------- RenderScreen
    private void RenderScreen(int y, bool sub)
    {
        int s = sub ? 1 : 0;
        S = sub ? subScreen : Screen; DB = sub ? subZ : zbuf;
        int bgActive = sub ? R.Ts : R.Tm;
        int D = sub ? (R.Cgwsel & 2) << 4 : 32;
        int mode = R.Bgmode & 7;
        if ((bgActive & 0x10) != 0)
        {
            tileAddress = (R.Obsel & 3) << 14; nameSelect = ((R.Obsel >> 3) & 3) << 13;
            enableMath = !sub && (R.Cgadsub & 0x10) != 0; startPalette = 128;
            SelectConverter(4, false, sub, false); SelectRenderers(sub, true);
            DrawOBJS(y, D + 4, s);
        }
        nameSelect = 0;
        SelectRenderers(sub, false);
        void DoBg(int n, int pal, int d, bool hires, bool offset, int zh, int zl, int voffoff)
        {
            if ((bgActive & (1 << n)) == 0) return;
            startPalette = pal;
            enableMath = !sub && (R.Cgadsub & (1 << n)) != 0;
            bool size = ((R.Bgmode >> (4 + n)) & 1) != 0;
            tileSizeH = !hires && size ? 16 : 8; tileSizeV = size ? 16 : 8;
            bool mos = ((R.Mosaic >> n) & 1) != 0; int mosaic = (R.Mosaic >> 4) + 1;
            SelectConverter(d, hires, sub, mos);
            if (offset)
            {
                bool s3 = ((R.Bgmode >> 6) & 1) != 0;
                offsetSizeH = !hires && s3 ? 16 : 8; offsetSizeV = s3 ? 16 : 8;
                DrawBackgroundOffset(y, n, (byte)(D + zh), (byte)(D + zl), voffoff, s);
            }
            else if (mos && (hires || mosaic > 1)) DrawBackgroundMosaic(y, n, (byte)(D + zh), (byte)(D + zl), s, mosaic);
            else DrawBackground(y, n, (byte)(D + zh), (byte)(D + zl), s);
        }
        switch (mode)
        {
            case 0: DoBg(0, 0, 2, false, false, 15, 11, 0); DoBg(1, 32, 2, false, false, 14, 10, 0); DoBg(2, 64, 2, false, false, 7, 3, 0); DoBg(3, 96, 2, false, false, 6, 2, 0); break;
            case 1: DoBg(0, 0, 4, false, false, 15, 11, 0); DoBg(1, 0, 4, false, false, 14, 10, 0); DoBg(2, 0, 2, false, false, (R.Bgmode & 8) != 0 ? 17 : 7, 3, 0); break;
            case 2: DoBg(0, 0, 4, false, true, 15, 7, 8); DoBg(1, 0, 4, false, true, 11, 3, 8); break;
            case 3: DoBg(0, 0, 8, false, false, 15, 7, 0); DoBg(1, 0, 4, false, false, 11, 3, 0); break;
            case 4: DoBg(0, 0, 8, false, true, 15, 7, 0); DoBg(1, 0, 2, false, true, 11, 3, 0); break;
            case 5: DoBg(0, 0, 4, true, false, 15, 7, 0); DoBg(1, 0, 2, true, false, 11, 3, 0); break;
            case 6: DoBg(0, 0, 4, true, true, 15, 7, 8); break;
            case 7:
                if ((bgActive & 1) != 0) { enableMath = !sub && (R.Cgadsub & 1) != 0; DrawMode7(y, 0, D, s); }
                if ((R.Setini & 0x40) != 0 && (bgActive & 2) != 0) { enableMath = !sub && (R.Cgadsub & 2) != 0; DrawMode7(y, 1, D, s); }
                break;
        }
        enableMath = !sub && (R.Cgadsub & 0x20) != 0;
        // DrawBackdrop: colour 0, Z 1
        int o = y * PPL;
        for (int c = 0; c < clipCount[s, 5]; c++)
        {
            SetMath(clipMode[s, 5, c]);
            z1 = z2 = 1;
            for (int x = clipLeft[s, 5, c]; x < clipRight[s, 5, c]; x++) DrawPixel(o, x, clipColors ? (ushort)0 : screenColors[0], screenColors[0]);
        }
    }

    // ----------------------------------------------------------------- backgrounds
    private (int sc0, int sc1, int sc2, int sc3) Screens(int bg)
    {
        byte sc = bg switch { 0 => R.Bgsc0, 1 => R.Bgsc1, 2 => R.Bgsc2, _ => R.Bgsc3 };
        int baseW = (sc & 0x7C) << 8, size = sc & 3;   // word addresses
        int sc0 = baseW, sc1 = (size & 1) != 0 ? sc0 + 1024 : sc0; sc1 &= 0x7FFF;
        int sc2 = (size & 2) != 0 ? sc1 + 1024 : sc0; sc2 &= 0x7FFF;
        int sc3 = (size & 1) != 0 ? sc2 + 1024 : sc2; sc3 &= 0x7FFF;
        return (sc0, sc1, sc2, sc3);
    }
    private int NameBase(int bg) => bg switch { 0 => (R.Bg12nba & 0xF) << 12, 1 => (R.Bg12nba >> 4) << 12, 2 => (R.Bg34nba & 0xF) << 12, _ => (R.Bg34nba >> 4) << 12 };
    private int W(int wordAddr) => ppu.Vram[wordAddr & 0x7FFF];

    private void DrawBackground(int Y, int bg, byte zh, byte zl, int s)
    {
        tileAddress = NameBase(bg) << 1;
        var (sc0, sc1, sc2, sc3) = Screens(bg);
        int offsetMask = tileSizeH == 16 ? 0x3FF : 0x1FF, offsetShift = tileSizeV == 16 ? 4 : 3, pixWidth = DoubleWidthPixels ? 2 : 1;
        for (int clip = 0; clip < clipCount[s, bg]; clip++)
        {
            SetMath(clipMode[s, bg, clip]);
            int vOffset = lineData[Y, bg].vofs, hOffset = lineData[Y, bg].hofs;
            int virtAlign = ((Y + vOffset) & 7) << 3;
            int tilemapRow = (vOffset + Y) >> offsetShift;
            int t1, t2; if (((vOffset + Y) & 8) != 0) { t1 = 16; t2 = 0; } else { t1 = 0; t2 = 16; }
            int b1, b2; if ((tilemapRow & 0x20) != 0) { b1 = sc2; b2 = sc3; } else { b1 = sc0; b2 = sc1; }
            b1 += (tilemapRow & 0x1F) << 5; b2 += (tilemapRow & 0x1F) << 5;
            int left = clipLeft[s, bg, clip], right = clipRight[s, bg, clip];
            int offset = left * pixWidth + Y * PPL;
            int hPos = (hOffset + left) & offsetMask, hTile = hPos >> 3;
            int t = tileSizeH == 8 ? (hTile > 31 ? b2 + (hTile & 0x1F) : b1 + hTile) : (hTile > 63 ? b2 + ((hTile >> 1) & 0x1F) : b1 + (hTile >> 1));
            int width = right - left;
            void One(int startPixel, int w)
            {
                int tile = W(t);
                z1 = z2 = (tile & 0x2000) != 0 ? zh : zl;
                if (tileSizeV == 16) tile = TilePlus(tile, (tile & 0x8000) != 0 ? t2 : t1);
                if (tileSizeH == 8) DrawClippedTile(tile, offset, startPixel, w, virtAlign);
                else DrawClippedTile(TilePlus(tile, (tile & 0x4000) == 0 ? (hTile & 1) : 1 - (hTile & 1)), offset, startPixel, w, virtAlign);
            }
            void Advance()
            {
                if (tileSizeH == 8) { t++; if (hTile == 31) t = b2; else if (hTile == 63) t = b1; }
                else { t += hTile & 1; if (hTile == 63) t = b2; else if (hTile == 127) t = b1; }
                hTile++;
            }
            if ((hPos & 7) != 0)
            {
                int l = hPos & 7, w = Math.Min(8 - l, width);
                offset -= l * pixWidth;
                One(l, w); Advance();
                offset += 8 * pixWidth; width -= w;
            }
            while (width >= 8) { One(0, 8); Advance(); offset += 8 * pixWidth; width -= 8; }
            if (width > 0) One(0, width);
        }
    }

    private void DrawBackgroundMosaic(int Y, int bg, byte zh, byte zl, int s, int mosaic)
    {
        tileAddress = NameBase(bg) << 1;
        var (sc0, sc1, sc2, sc3) = Screens(bg);
        int offsetMask = tileSizeH == 16 ? 0x3FF : 0x1FF, offsetShift = tileSizeV == 16 ? 4 : 3, pixWidth = DoubleWidthPixels ? 2 : 1;
        int mosaicStart = Y % mosaic;   // PPU.MosaicStart: the frame start (Snes9x's mid-frame restart point is not tracked)
        int yTop = Y - mosaicStart;
        for (int clip = 0; clip < clipCount[s, bg]; clip++)
        {
            SetMath(clipMode[s, bg, clip]);
            int vOffset = lineData[yTop, bg].vofs, hOffset = lineData[yTop, bg].hofs;
            int virtAlign = ((yTop + vOffset) & 7) << 3;
            int tilemapRow = (vOffset + yTop) >> offsetShift;
            int t1, t2; if (((vOffset + yTop) & 8) != 0) { t1 = 16; t2 = 0; } else { t1 = 0; t2 = 16; }
            int b1, b2; if ((tilemapRow & 0x20) != 0) { b1 = sc2; b2 = sc3; } else { b1 = sc0; b2 = sc1; }
            b1 += (tilemapRow & 0x1F) << 5; b2 += (tilemapRow & 0x1F) << 5;
            int left = clipLeft[s, bg, clip], right = clipRight[s, bg, clip];
            int offset = left * pixWidth + Y * PPL;
            int hPos = (hOffset + left - (left % mosaic)) & offsetMask, hTile = hPos >> 3;
            int t = tileSizeH == 8 ? (hTile > 31 ? b2 + (hTile & 0x1F) : b1 + hTile) : (hTile > 63 ? b2 + ((hTile >> 1) & 0x1F) : b1 + (hTile >> 1));
            int width = right - left;
            hPos &= 7;
            while (left < right)
            {
                int w = Math.Min(mosaic - (left % mosaic), width);
                int tile = W(t);
                z1 = z2 = (tile & 0x2000) != 0 ? zh : zl;
                if (tileSizeV == 16) tile = TilePlus(tile, (tile & 0x8000) != 0 ? t2 : t1);
                if (tileSizeH == 8) DrawMosaicPixel(tile, offset, virtAlign, hPos & 7, w);
                else DrawMosaicPixel(TilePlus(tile, (tile & 0x4000) == 0 ? (hTile & 1) : 1 - (hTile & 1)), offset, virtAlign, hPos & 7, w);
                hPos += mosaic;
                while (hPos >= 8)
                {
                    hPos -= 8;
                    if (tileSizeH == 8) { t++; if (hTile == 31) t = b2; else if (hTile == 63) t = b1; }
                    else { t += hTile & 1; if (hTile == 63) t = b2; else if (hTile == 127) t = b1; }
                    hTile++;
                }
                offset += w * pixWidth; width -= w; left += w;
            }
        }
    }

    private void DrawBackgroundOffset(int Y, int bg, byte zh, byte zl, int voffoff, int s)
    {
        tileAddress = NameBase(bg) << 1;
        var (bps0, bps1, bps2, bps3) = Screens(2);
        var (sc0, sc1, sc2, sc3) = Screens(bg);
        int offsetMask = tileSizeH == 16 ? 0x3FF : 0x1FF, offsetShift = tileSizeV == 16 ? 4 : 3;
        int offset2Mask = offsetSizeH == 16 ? 0x3FF : 0x1FF, offset2Shift = offsetSizeV == 16 ? 4 : 3;
        int enableMask = 0x2000 << bg, pixWidth = DoubleWidthPixels ? 2 : 1;
        for (int clip = 0; clip < clipCount[s, bg]; clip++)
        {
            SetMath(clipMode[s, bg, clip]);
            int vOff = lineData[Y, 2].vofs - 1, hOff = lineData[Y, 2].hofs;
            int hOffsetRow = vOff >> offset2Shift, vOffsetRow = (vOff + voffoff) >> offset2Shift;
            int s1, s2; if ((hOffsetRow & 0x20) != 0) { s1 = bps2; s2 = bps3; } else { s1 = bps0; s2 = bps1; }
            s1 += (hOffsetRow & 0x1F) << 5; s2 += (hOffsetRow & 0x1F) << 5;
            int sv = ((vOffsetRow & 0x20) != 0 ? bps2 : bps0) + ((vOffsetRow & 0x1F) << 5);
            int vOffsetOffset = sv - s1;
            int left = clipLeft[s, bg, clip], right = clipRight[s, bg, clip];
            int offset = left * pixWidth + Y * PPL;
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
                offset -= l * pixWidth;
                int tile = W(t);
                z1 = z2 = (tile & 0x2000) != 0 ? zh : zl;
                if (tileSizeV == 16) tile = TilePlus(tile, (tile & 0x8000) != 0 ? t2 : t1);
                if (tileSizeH == 8) DrawClippedTile(tile, offset, l, w, virtAlign);
                else DrawClippedTile(TilePlus(tile, (tile & 0x4000) == 0 ? (hTile & 1) : 1 - (hTile & 1)), offset, l, w, virtAlign);
                left += w; offset += 8 * pixWidth; width -= w;
            }
        }
    }

    private static int Clip10(int a) => (a & 0x2000) != 0 ? (a | ~0x3FF) : (a & 0x3FF);

    private void DrawMode7(int line, int bg, int D, int s)
    {
        for (int clip = 0; clip < clipCount[s, bg]; clip++)
        {
            SetMath(clipMode[s, bg, clip]);
            int left = clipLeft[s, bg, clip], right = clipRight[s, bg, clip];
            bool dc = bg == 0 && (R.Cgwsel & 1) != 0;
            var real = dc ? DirectMap(0) : screenColors;
            var l = lineMatrix[line];
            int hOffset = (l.h << 19) >> 19, vOffset = (l.v << 19) >> 19, centreX = (l.cx << 19) >> 19, centreY = (l.cy << 19) >> 19;
            int starty = (R.M7sel & 2) != 0 ? 255 - (line + 1) : line + 1;
            int yy = Clip10(vOffset - centreY);
            int BB = ((l.b * starty) & ~63) + ((l.b * yy) & ~63) + (centreX << 8);
            int DD = ((l.d * starty) & ~63) + ((l.d * yy) & ~63) + (centreY << 8);
            int startx, aa, cc;
            if ((R.M7sel & 1) != 0) { startx = right - 1; aa = -l.a; cc = -l.c; } else { startx = left; aa = l.a; cc = l.c; }
            int xx = Clip10(hOffset - centreX);
            int AA = l.a * startx + ((l.a * xx) & ~63), CC = l.c * startx + ((l.c * xx) & ~63);
            int repeat = R.M7sel >> 6;
            int o = line * PPL;
            for (int x = left; x < right; x++, AA += aa, CC += cc)
            {
                int X = (AA + BB) >> 8, Yv = (CC + DD) >> 8;
                int b;
                if (repeat == 0) { X &= 0x3FF; Yv &= 0x3FF; b = Mode7Pixel(X, Yv); }
                else if (((X | Yv) & ~0x3FF) == 0) b = Mode7Pixel(X, Yv);
                else if (repeat == 3) b = VB(1 + ((Yv & 7) << 4) + ((X & 7) << 1));
                else continue;
                int pix = bg == 0 ? b & 0xFF : b & 0x7F;
                if (pix == 0) continue;
                z1 = z2 = (byte)(bg == 0 ? D + 7 : D + ((b & 0x80) != 0 ? 11 : 3));
                ushort rc = real[pix];
                DrawPixel(o, x, clipColors ? (ushort)0 : rc, rc);
            }
        }
    }
    private int Mode7Pixel(int X, int Y) { int tile = VB(((Y & ~7) << 5) + ((X >> 2) & ~1)); return VB(1 + (tile << 7) + ((Y & 7) << 4) + ((X & 7) << 1)); }

    // ----------------------------------------------------------------- sprites: SetupOBJ (normal path) + DrawOBJS, one line
    private static readonly int[,] objSizes = { { 8, 8, 16, 16 }, { 8, 8, 32, 32 }, { 8, 8, 64, 64 }, { 16, 16, 32, 32 }, { 16, 16, 64, 64 }, { 32, 32, 64, 64 }, { 16, 32, 32, 64 }, { 16, 32, 32, 32 } };
    private readonly int[] lineObj = new int[32], lineObjLine = new int[32], objVisibleTiles = new int[128], objWidths = new int[128];

    private void DrawOBJS(int Y, int D, int s)
    {
        var oam = ppu.Oam;
        int sel = R.Obsel >> 5;
        int sw = objSizes[sel, 0], sh = objSizes[sel, 1], lw = objSizes[sel, 2], lh = objSizes[sel, 3];
        int first = (R.Oamadd & 0x8000) != 0 ? (R.Oamadd >> 1) & 0x7F : 0;
        int n = 0, tiles = 34;
        int S0 = first;
        do
        {
            int S1 = S0;
            int hi = (oam[512 + (S1 >> 2)] >> ((S1 & 3) * 2)) & 3;
            bool big = (hi & 2) != 0;
            objWidths[S1] = big ? lw : sw; int height = big ? lh : sh;
            int hpos = oam[S1 * 4] | ((hi & 1) << 8); if (hpos >= 256) hpos -= 512;
            if (hpos == -256) hpos = 0;
            if (hpos > -objWidths[S1] && hpos <= 256)
            {
                objVisibleTiles[S1] = hpos < 0 ? (objWidths[S1] + hpos + 7) >> 3 : hpos + objWidths[S1] > 255 ? (256 - hpos + 7) >> 3 : objWidths[S1] >> 3;
                int vpos = oam[S1 * 4 + 1];
                int lineIn = (byte)(Y - vpos);
                if (lineIn < height && n < 32 && Y < 240)
                {
                    tiles -= objVisibleTiles[S1];
                    bool vflip = (oam[S1 * 4 + 3] & 0x80) != 0;
                    lineObj[n] = S1; lineObjLine[n] = vflip ? lineIn ^ (objWidths[S1] - 1) : lineIn; n++;
                }
            }
            S0 = (S0 + 1) & 0x7F;
        } while (S0 != first);

        int pixWidth = DoubleWidthPixels ? 2 : 1;
        int offset = Y * PPL;
        z1 = 2;
        for (int I = 0; I < n; I++)
        {
            int Sx = lineObj[I];
            tiles += objVisibleTiles[Sx];
            if (tiles <= 0) continue;
            int name = oam[Sx * 4 + 2] | ((oam[Sx * 4 + 3] & 1) << 8), attr = oam[Sx * 4 + 3], pal = (attr >> 1) & 7, prio = (attr >> 4) & 3;
            int objLine = lineObjLine[I];
            int baseTile = (((objLine << 1) + (name & 0xF0)) & 0xF0) | (name & 0x100) | (pal << 10);
            int tileX = name & 0x0F, tileLine = (objLine & 7) * 8, tileInc = 1;
            if ((attr & 0x40) != 0) { tileX = (tileX + (objWidths[Sx] >> 3) - 1) & 0x0F; baseTile |= 0x4000; tileInc = -1; }
            z2 = (byte)(D + prio * 4);
            int hi = (oam[512 + (Sx >> 2)] >> ((Sx & 3) * 2)) & 1;
            int X = oam[Sx * 4] | (hi << 8); if (X >= 256) X -= 512;
            int hp = X; if (X == -256) X = 256;
            int drawMode = 3, clip = 0, nextClip = -1000;
            for (int t = tiles, O = offset + X * pixWidth; X <= 256 && X < hp + objWidths[Sx]; tileX = (tileX + tileInc) & 0x0F, X += 8, O += 8 * pixWidth)
            {
                if (X < -7 || --t < 0 || X == 256) continue;
                for (int x = X; x < X + 8;)
                {
                    if (x >= nextClip)
                    {
                        for (; clip < clipCount[s, 4] && clipLeft[s, 4, clip] <= x; clip++) ;
                        if (clip == 0 || x >= clipRight[s, 4, clip - 1]) { drawMode = 0; nextClip = clip < clipCount[s, 4] ? clipLeft[s, 4, clip] : 1000; }
                        else
                        {
                            drawMode = clipMode[s, 4, clip - 1]; nextClip = clipRight[s, 4, clip - 1];
                            clipColors = (drawMode & 1) == 0;
                            mathMode = enableMath && (pal & 4) != 0 && (drawMode & 2) != 0 ? mathIndex : 0;
                        }
                    }
                    if (x == X && x + 8 < nextClip) { if (drawMode != 0) DrawClippedTile(baseTile | tileX, O, 0, 8, tileLine); x += 8; }
                    else
                    {
                        int w = nextClip <= X + 8 ? nextClip - x : X + 8 - x;
                        if (drawMode != 0) DrawClippedTile(baseTile | tileX, O, x - X, w, tileLine);
                        x += w;
                    }
                }
            }
        }
    }

    // ----------------------------------------------------------------- clip.cpp
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

    private void StoreWindowRegions(byte mask, int sub, int j, int nRegions, int[] windows, int[] modes, bool isSub, bool storeMode0)
    {
        int ct = 0;
        for (int k = 0; k < nRegions; k++)
        {
            int dm = modes[k];
            if (isSub) dm |= 1;
            if ((mask & (1 << k)) != 0) dm = 0;
            if (!storeMode0 && dm == 0) continue;
            if (ct > 0 && clipRight[sub, j, ct - 1] == windows[k] && clipMode[sub, j, ct - 1] == dm) clipRight[sub, j, ct - 1] = windows[k + 1];
            else { clipLeft[sub, j, ct] = windows[k]; clipRight[sub, j, ct] = windows[k + 1]; clipMode[sub, j, ct] = dm; ct++; }
        }
        clipCount[sub, j] = ct;
    }

    private void ComputeClipWindows()
    {
        int[] windows = { 0, 256, 256, 256, 256, 256, 256 };
        int[] modes = new int[6];
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
        byte cw = CalcWindowMask(5, W1, W2);
        byte cwColor = (R.Cgwsel & 0xC0) switch { 0x00 => 0, 0x40 => (byte)~cw, 0x80 => cw, _ => 0xFF };
        byte cwMath = (R.Cgwsel & 0x30) switch { 0x00 => 0, 0x10 => (byte)~cw, 0x20 => cw, _ => 0xFF };
        for (i = 0; i < n; i++) { if ((cwColor & (1 << i)) == 0) modes[i] |= 1; if ((cwMath & (1 << i)) == 0) modes[i] |= 2; }
        StoreWindowRegions(0, 0, 5, n, windows, modes, false, true);
        StoreWindowRegions(0, 1, 5, n, windows, modes, true, true);
        for (j = 0; j < 5; j++)
        {
            byte w = CalcWindowMask(j, W1, W2);
            for (int sub = 0; sub < 2; sub++)
            {
                byte en = sub == 0 ? R.Tmw : R.Tsw;
                StoreWindowRegions((en & (1 << j)) != 0 ? w : (byte)0, sub, j, n, windows, modes, sub == 1, false);
            }
        }
    }

    /// <summary>The finished picture as 0xFFRRGGBB, <see cref="Width"/> x <see cref="Height"/>.</summary>
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
