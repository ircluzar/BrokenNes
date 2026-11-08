// BsnesV063Render - bsnes v063's scanline PPU renderer (bPPU, the last one before v064's cycle-based PPU), ported from the
// bsnes_v063.tar.bz2 release tarball (src/ppu/bppu/render: render.cpp, line.cpp, bg.cpp, oam.cpp, mode7.cpp, windows.cpp,
// addsub.cpp, cache.cpp, plus bppu.cpp's scanline()/mosaic counter, OBSEL latch and light/mosaic tables).
//
//   bsnes (C) 2004-2010 byuu; mode 7 base algorithm by anomie; colour add/sub algorithms by blargg. GPL (the v063 tarball's
//   licence, GPL v2). See THIRD_PARTY_NOTICES.md (PPU harvest, section 2b) and snes/THIRD_PARTY_NOTICES.md.
//
// PPU harvest runners-up (docs/projects/ppu-harvest-catalog.md, section 2): an art instrument, ported FAITHFULLY with its flaws. It is the
// SNES picture core "BS63": the stock S-PPU (PPU_SFC) keeps the timing, VRAM/CGRAM/OAM and the register decoding; at each line it draws
// (LineObserver) this renderer draws the whole line at once from the live registers, the way bPPU::render_scanline does at H=512.
//
// Characteristic jank, all of it kept on purpose:
//  * Whole-line rendering: every register is read once per line, so mid-line raster effects (Air Strike Patrol's shadow, Uniracers'
//    mid-frame OAM tricks) are lost - the reason v064 went cycle-based.
//  * OBSEL (sprite size, name select, tile base) is latched at H=1152, i.e. AFTER the line is drawn: a change shows one line late.
//  * Sprite evaluation per line with the 32-sprite / 34-tile limits; tiles are loaded from the LAST in-range sprite back to the first,
//    so when time-over hits it drops the earliest-listed sprites' tiles. x == 256 loads every tile (a special case kept from the source).
//  * Hires (modes 5/6, pseudo-hires): each output pixel is averaged with the previous one ((prev + curr) >> 1 with the 0x0421 carry
//    trick), and `prev` starts at 0 on every line, so the left-most column is half-dark. Main-screen BG pixels come from the odd
//    half-dots, the sub screen from the even ones.
//  * The "colour exemption" flag is set for sprite palettes BELOW 192 (the source's comment says >= 192): sprites with palettes 0-3
//    never take colour math, palettes 4-7 do.
//  * Mode 7 repeat modes 0 and 1 are the same; BG2 EXTBG takes its vertical mosaic from BG1's enable.
//  * Offset-per-tile never applies to the left-most tile (opt_x < 8); in mode 4 the one OPT word picks H or V by bit 15.
//  * Brightness uses bsnes's rounded light tables (round(c * l / 15)).
// Not ported / approximated: the background tile cache (tiles are decoded from VRAM directly; the source re-decodes dirty tiles, so the
// pictures match), the Mode 7 register cache at H=10 (the registers are read when the stock chip draws the line), the OAM first-sprite
// rotation is computed at frame start from $2102/3, interlace shows the even field only.
using System;

namespace NesEmulator.Snes;

/// <summary>A foreign renderer drawing beside the stock S-PPU (PPU harvest): it owns the finished picture.</summary>
internal interface ISnesHarvestPicture
{
    void Detach();
    int Width { get; }
    int Height { get; }
    void CopyTo(uint[] frame);
}

internal sealed class BsnesV063Render : ISnesHarvestPicture
{
    const int PPL = 512, MaxH = 239;
    const int BG1 = 0, BG2 = 1, BG3 = 2, BG4 = 3, OAM = 4, BACK = 5, COL = 5;
    private readonly PPU_SFC ppu;
    private PPU_SFC.DecodedRegs R;
    private readonly ushort[] screen = new ushort[PPL * MaxH];   // BGR555, every line 512 wide (normal lines are stored doubled)
    private bool frameHires;

    private static readonly ushort[,] mosaicTable = new ushort[16, 4096];
    private static readonly ushort[,] lightB = new ushort[16, 32], lightGR = new ushort[16, 1024];
    static BsnesV063Render()
    {
        for (int l = 0; l < 16; l++) for (int i = 0; i < 4096; i++) mosaicTable[l, i] = (ushort)(i / (l + 1) * (l + 1));
        for (int l = 0; l < 16; l++)
        {
            double m = l / 15.0;
            for (int i = 0; i < 1024; i++)
            {
                int r = (int)((i & 31) * m + 0.5), g = (int)(((i >> 5) & 31) * m + 0.5);
                r = Math.Clamp(r, 0, 31); g = Math.Clamp(g, 0, 31);
                if (i < 32) lightB[l, i] = (ushort)(r << 10);
                lightGR[l, i] = (ushort)(g << 5 | r);
            }
        }
    }

    // the decoded registers, bsnes-named, refreshed per line
    private bool displayDisabled, pseudoHires, interlace, oamInterlace, overscan, bg3Priority, mode7ExtBg, mode7HFlip, mode7VFlip, directColour;
    private bool addsubMode, colorMode, colorHalve;
    private int bgMode, brightness, mosaicSize, mode7Repeat, colorMask, colorsubMask;
    private ushort colorRgb;
    private readonly bool[] bgEnabled = new bool[5], bgsubEnabled = new bool[5], mosaicEnabled = new bool[4], bgTilesize = new bool[4];
    private readonly bool[] windowEnabled = new bool[5], subWindowEnabled = new bool[5], colorEnabled = new bool[6];
    private readonly bool[] w1Enabled = new bool[6], w2Enabled = new bool[6], w1Invert = new bool[6], w2Invert = new bool[6];
    private readonly int[] windowMask = new int[6];
    private readonly int[] bgScaddr = new int[4], bgScsize = new int[4], bgTdaddr = new int[4];
    private readonly ushort[] bgHofs = new ushort[4], bgVofs = new ushort[4], bgY = new ushort[4];
    private int w1Left, w1Right, w2Left, w2Right;
    private int mosaicCountdown, oamFirstSprite;
    // OBSEL as latched at H=1152 (after the line is drawn)
    private int cacheOamBasesize, cacheOamNameselect, cacheOamTdaddr;
    private int line;

    public BsnesV063Render(PPU_SFC p) { ppu = p; p.LineObserver += Line; p.FrameObserver += Frame; }
    public void Detach() { ppu.LineObserver -= Line; ppu.FrameObserver -= Frame; }
    public int Height => ppu.VisibleHeight;
    public int Width => frameHires ? 512 : 256;

    private int VB(int a) { int w = ppu.Vram[(a & 0xFFFF) >> 1]; return (a & 1) != 0 ? w >> 8 : w & 0xFF; }
    private ushort GetPalette(int index) => (ushort)(ppu.Cgram[index & 0xFF] & 0x7FFF);

    private void Frame()
    {
        R = ppu.Decoded;
        frameHires = false;
        // bppu.cpp: at V=225 (display on) oam_addr = baseaddr << 1, first sprite = priority ? (oam_addr >> 2) & 127 : 0
        oamFirstSprite = (R.Oamadd & 0x8000) == 0 ? 0 : (((R.Oamadd & 0x1FF) << 1) >> 2) & 127;
    }

    private void Decode()
    {
        R = ppu.Decoded;
        displayDisabled = (R.Inidisp & 0x80) != 0; brightness = R.Inidisp & 15;
        bgMode = R.Bgmode & 7; bg3Priority = (R.Bgmode & 8) != 0;
        for (int i = 0; i < 4; i++) bgTilesize[i] = (R.Bgmode & (0x10 << i)) != 0;
        mosaicSize = (R.Mosaic >> 4) & 15; for (int i = 0; i < 4; i++) mosaicEnabled[i] = (R.Mosaic & (1 << i)) != 0;
        byte[] sc = { R.Bgsc0, R.Bgsc1, R.Bgsc2, R.Bgsc3 };
        for (int i = 0; i < 4; i++) { bgScaddr[i] = (sc[i] & 0x7C) << 9; bgScsize[i] = sc[i] & 3; }
        bgTdaddr[0] = (R.Bg12nba & 7) << 13; bgTdaddr[1] = (R.Bg12nba & 0x70) << 9;
        bgTdaddr[2] = (R.Bg34nba & 7) << 13; bgTdaddr[3] = (R.Bg34nba & 0x70) << 9;
        bgHofs[0] = R.Hofs0; bgHofs[1] = R.Hofs1; bgHofs[2] = R.Hofs2; bgHofs[3] = R.Hofs3;
        bgVofs[0] = R.Vofs0; bgVofs[1] = R.Vofs1; bgVofs[2] = R.Vofs2; bgVofs[3] = R.Vofs3;
        mode7Repeat = (R.M7sel >> 6) & 3; mode7VFlip = (R.M7sel & 2) != 0; mode7HFlip = (R.M7sel & 1) != 0;
        for (int i = 0; i < 5; i++) { bgEnabled[i] = (R.Tm & (1 << i)) != 0; bgsubEnabled[i] = (R.Ts & (1 << i)) != 0; windowEnabled[i] = (R.Tmw & (1 << i)) != 0; subWindowEnabled[i] = (R.Tsw & (1 << i)) != 0; }
        int[] wsel = { R.W12sel & 15, R.W12sel >> 4, R.W34sel & 15, R.W34sel >> 4, R.Wobjsel & 15, R.Wobjsel >> 4 };
        for (int i = 0; i < 6; i++) { w1Invert[i] = (wsel[i] & 1) != 0; w1Enabled[i] = (wsel[i] & 2) != 0; w2Invert[i] = (wsel[i] & 4) != 0; w2Enabled[i] = (wsel[i] & 8) != 0; }
        for (int i = 0; i < 4; i++) windowMask[i] = (R.Wbglog >> (2 * i)) & 3;
        windowMask[OAM] = R.Wobjlog & 3; windowMask[COL] = (R.Wobjlog >> 2) & 3;
        w1Left = R.Wh0; w1Right = R.Wh1; w2Left = R.Wh2; w2Right = R.Wh3;
        colorMask = (R.Cgwsel >> 6) & 3; colorsubMask = (R.Cgwsel >> 4) & 3; addsubMode = (R.Cgwsel & 2) != 0; directColour = (R.Cgwsel & 1) != 0;
        colorMode = (R.Cgadsub & 0x80) != 0; colorHalve = (R.Cgadsub & 0x40) != 0;
        for (int i = 0; i < 6; i++) colorEnabled[i] = (R.Cgadsub & (1 << i)) != 0;
        colorRgb = (ushort)(R.Coldata & 0x7FFF);
        mode7ExtBg = (R.Setini & 0x40) != 0; pseudoHires = (R.Setini & 8) != 0; overscan = (R.Setini & 4) != 0;
        oamInterlace = (R.Setini & 2) != 0; interlace = (R.Setini & 1) != 0;
    }

    // ----------------------------------------------------------------- bPPU::scanline + render_scanline + the H=1152 OBSEL latch
    private void Line(int row)
    {
        if (row < 0 || row >= MaxH) return;
        line = row + 1;
        Decode();
        if (line == 1)
        {
            for (int bg = BG1; bg <= BG4; bg++) bgY[bg] = 1;
            mosaicCountdown = mosaicSize + 1; mosaicCountdown--;
        }
        else
        {
            for (int bg = BG1; bg <= BG4; bg++) if (!mosaicEnabled[bg] || mosaicCountdown == 0) bgY[bg] = (ushort)line;
            if (mosaicCountdown == 0) mosaicCountdown = mosaicSize + 1;
            mosaicCountdown--;
        }
        if (line < (!overscan ? 225 : 240))
        {
            RenderLineOamRto();
            RenderLine(row);
        }
        cacheOamBasesize = (R.Obsel >> 5) & 7; cacheOamNameselect = (R.Obsel >> 3) & 3; cacheOamTdaddr = (R.Obsel & 3) << 14;
    }

    private bool LineHires => pseudoHires || bgMode == 5 || bgMode == 6;

    private void RenderLine(int row)
    {
        bool hires = LineHires;
        if (hires && !frameHires) frameHires = true;
        if (displayDisabled) { Array.Fill(screen, (ushort)0, row * PPL, PPL); return; }
        FlushPixelCache();
        BuildWindowTables(COL);
        UpdateBgInfo();
        switch (bgMode)
        {
            case 0: Bg(0, BG1, 0, 8, 11); Bg(0, BG2, 0, 7, 10); Bg(0, BG3, 0, 2, 5); Bg(0, BG4, 0, 1, 4); RenderLineOam(3, 6, 9, 12); break;
            case 1:
                if (bg3Priority) { Bg(1, BG1, 1, 5, 8); Bg(1, BG2, 1, 4, 7); Bg(1, BG3, 0, 1, 10); RenderLineOam(2, 3, 6, 9); }
                else { Bg(1, BG1, 1, 6, 9); Bg(1, BG2, 1, 5, 8); Bg(1, BG3, 0, 1, 3); RenderLineOam(2, 4, 7, 10); }
                break;
            case 2: Bg(2, BG1, 1, 3, 7); Bg(2, BG2, 1, 1, 5); RenderLineOam(2, 4, 6, 8); break;
            case 3: Bg(3, BG1, 2, 3, 7); Bg(3, BG2, 1, 1, 5); RenderLineOam(2, 4, 6, 8); break;
            case 4: Bg(4, BG1, 2, 3, 7); Bg(4, BG2, 0, 1, 5); RenderLineOam(2, 4, 6, 8); break;
            case 5: Bg(5, BG1, 1, 3, 7); Bg(5, BG2, 0, 1, 5); RenderLineOam(2, 4, 6, 8); break;
            case 6: Bg(6, BG1, 1, 2, 5); RenderLineOam(1, 3, 4, 6); break;
            case 7:
                if (!mode7ExtBg) { Mode7(BG1, 2, 2); RenderLineOam(1, 3, 4, 5); }
                else { Mode7(BG1, 3, 3); Mode7(BG2, 1, 5); RenderLineOam(2, 4, 6, 7); }
                break;
        }
        RenderLineOutput(row, hires);
    }

    // ----------------------------------------------------------------- cache.cpp: pixel cache
    private struct Pixel { public ushort SrcMain, SrcSub; public byte BgMain, BgSub; public bool CeMain, CeSub; public byte PriMain, PriSub; }
    private readonly Pixel[] pc = new Pixel[256];

    private void FlushPixelCache()
    {
        ushort main = GetPalette(0);
        ushort sub = LineHires ? main : colorRgb;
        for (int i = 0; i < 256; i++) pc[i] = new Pixel { SrcMain = main, SrcSub = sub, BgMain = BACK, BgSub = BACK };
    }

    // render_bg_tile, decoded live from VRAM (the source caches it, re-decoding dirty tiles: same pixels)
    private int TilePixel(int depth, int tileNum, int py, int px)
    {
        int bit = 0x80 >> px;
        if (depth == 0) { int pos = tileNum * 16 + py * 2; return ((VB(pos) & bit) != 0 ? 1 : 0) | ((VB(pos + 1) & bit) != 0 ? 2 : 0); }
        if (depth == 1)
        {
            int pos = tileNum * 32 + py * 2;
            return ((VB(pos) & bit) != 0 ? 1 : 0) | ((VB(pos + 1) & bit) != 0 ? 2 : 0) | ((VB(pos + 16) & bit) != 0 ? 4 : 0) | ((VB(pos + 17) & bit) != 0 ? 8 : 0);
        }
        int p8 = tileNum * 64 + py * 2, c = 0;
        for (int k = 0; k < 4; k++) { if ((VB(p8 + 16 * k) & bit) != 0) c |= 1 << (2 * k); if ((VB(p8 + 16 * k + 1) & bit) != 0) c |= 2 << (2 * k); }
        return c;
    }

    // ----------------------------------------------------------------- windows.cpp: 1 = masked for BG/OAM; for COL main 0 = clip to black, sub 0 = no math
    private readonly byte[,] winMain = new byte[6, 256], winSub = new byte[6, 256];

    private void BuildWindowTable(int bg, bool screenSub)
    {
        byte set = 1, clr = 0;
        var t = screenSub ? winSub : winMain;
        if (bg != COL)
        {
            if ((!screenSub && !windowEnabled[bg]) || (screenSub && !subWindowEnabled[bg])) { for (int x = 0; x < 256; x++) t[bg, x] = 0; return; }
        }
        else
        {
            switch (!screenSub ? colorMask : colorsubMask)
            {
                case 0: for (int x = 0; x < 256; x++) t[bg, x] = 1; return;
                case 3: for (int x = 0; x < 256; x++) t[bg, x] = 0; return;
                case 1: set = 1; clr = 0; break;
                case 2: set = 0; clr = 1; break;
            }
        }
        if (!w1Enabled[bg] && !w2Enabled[bg]) { for (int x = 0; x < 256; x++) t[bg, x] = clr; return; }
        if (w1Enabled[bg] && !w2Enabled[bg])
        {
            if (w1Invert[bg]) (set, clr) = (clr, set);
            for (int x = 0; x < 256; x++) t[bg, x] = x >= w1Left && x <= w1Right ? set : clr;
            return;
        }
        if (!w1Enabled[bg] && w2Enabled[bg])
        {
            if (w2Invert[bg]) (set, clr) = (clr, set);
            for (int x = 0; x < 256; x++) t[bg, x] = x >= w2Left && x <= w2Right ? set : clr;
            return;
        }
        for (int x = 0; x < 256; x++)
        {
            bool m1 = (x >= w1Left && x <= w1Right) ^ w1Invert[bg];
            bool m2 = (x >= w2Left && x <= w2Right) ^ w2Invert[bg];
            bool v = windowMask[bg] switch { 0 => m1 | m2, 1 => m1 & m2, 2 => m1 ^ m2, _ => !(m1 ^ m2) };
            t[bg, x] = v ? set : clr;
        }
    }
    private void BuildWindowTables(int bg) { BuildWindowTable(bg, false); BuildWindowTable(bg, true); }

    // ----------------------------------------------------------------- bg.cpp
    private readonly int[] tw = new int[4], th = new int[4], mx = new int[4], my = new int[4], scx = new int[4], scy = new int[4];

    private void UpdateBgInfo()
    {
        bool hires = bgMode == 5 || bgMode == 6;
        int width = !hires ? 256 : 512;
        for (int bg = 0; bg < 4; bg++)
        {
            th[bg] = bgTilesize[bg] ? 4 : 3;
            tw[bg] = hires ? 4 : th[bg];
            mx[bg] = th[bg] == 4 ? width << 1 : width;
            my[bg] = mx[bg];
            if ((bgScsize[bg] & 1) != 0) mx[bg] <<= 1;
            if ((bgScsize[bg] & 2) != 0) my[bg] <<= 1;
            mx[bg]--; my[bg]--;
            scy[bg] = (bgScsize[bg] & 2) != 0 ? 32 << 5 : 0;
            scx[bg] = (bgScsize[bg] & 1) != 0 ? 32 << 5 : 0;
            if (bgScsize[bg] == 3) scy[bg] <<= 1;
        }
    }

    private int BgGetTile(int bg, int x, int y)
    {
        x = ((x & 0xFFFF) & mx[bg]) >> tw[bg];
        y = ((y & 0xFFFF) & my[bg]) >> th[bg];
        int pos = ((y & 0x1F) << 5) + (x & 0x1F);
        if ((y & 0x20) != 0) pos += scy[bg];
        if ((x & 0x20) != 0) pos += scx[bg];
        int addr = (bgScaddr[bg] + (pos << 1)) & 0xFFFF;
        return VB(addr) + (VB(addr + 1) << 8);
    }

    private static ushort DirectColour(int p, int t) =>
        (ushort)(((t & 7) << 2) | ((p & 1) << 1) | (((t >> 3) & 7) << 7) | (((p >> 1) & 1) << 6) | ((t >> 6) << 13) | ((p >> 2) << 12));

    private void SetMain(int x, int pri, int bg, ushort col, bool ce)
    {
        ref var p = ref pc[x];
        if (p.PriMain < pri) { p.PriMain = (byte)pri; p.BgMain = (byte)bg; p.SrcMain = col; p.CeMain = ce; }
    }
    private void SetSub(int x, int pri, int bg, ushort col, bool ce)
    {
        ref var p = ref pc[x];
        if (p.PriSub < pri) { p.PriSub = (byte)pri; p.BgSub = (byte)bg; p.SrcSub = col; p.CeSub = ce; }
    }

    private void Bg(int mode, int bg, int colorDepth, int pri0, int pri1)
    {
        if (!bgEnabled[bg] && !bgsubEnabled[bg]) return;
        bool en = bgEnabled[bg], subEn = bgsubEnabled[bg];
        int optValidBit = bg == BG1 ? 0x2000 : bg == BG2 ? 0x4000 : 0;
        int bgpalIndex = mode == 0 ? bg << 5 : 0;
        int palSize = 2 << colorDepth;
        int tileMask = 0x0FFF >> colorDepth;
        int tiledataIndex = bgTdaddr[bg] >> (4 + colorDepth);
        int tileWidth = tw[bg], tileHeight = th[bg], maskX = mx[bg], maskY = my[bg];
        int y = bgY[bg], hscroll = bgHofs[bg], vscroll = bgVofs[bg];
        bool hires = mode == 5 || mode == 6;
        int width = !hires ? 256 : 512;
        if (hires)
        {
            hscroll = (hscroll << 1) & 0xFFFF;
            if (interlace) y = (y << 1) + 0;   // field() - the even field only
        }
        int msz = mosaicEnabled[bg] ? mosaicSize : 0;
        bool isOpt = mode == 2 || mode == 4 || mode == 6;
        bool isDirect = directColour && bg == BG1 && (mode == 3 || mode == 4);
        BuildWindowTables(bg);

        int prevX = 0xFFFF, prevY = 0xFFFF, prevOptx = 0xFFFF;
        int hval = 0, vval = 0, tilePri = 0, tileNum = 0, palIndex = 0, palNum = 0, tileRowY = 0, tileId = 0;
        bool mirrorX = false, mirrorY = false;
        for (int x = 0; x < width; x++)
        {
            int hoffset = (mosaicTable[msz, x] + hscroll) & 0xFFFF;
            int voffset = (y + vscroll) & 0xFFFF;
            if (isOpt)
            {
                int optX = (x + (hscroll & 7)) & 0xFFFF;
                if (optX >= 8)
                {
                    if ((optX >> 3) != (prevOptx >> 3))
                    {
                        prevOptx = optX;
                        hval = BgGetTile(BG3, (optX - 8) + (bgHofs[BG3] & ~7), bgVofs[BG3]);
                        if (mode != 4) vval = BgGetTile(BG3, (optX - 8) + (bgHofs[BG3] & ~7), bgVofs[BG3] + 8);
                    }
                    if (mode == 4)
                    {
                        if ((hval & optValidBit) != 0)
                        {
                            if ((hval & 0x8000) == 0) hoffset = (optX + (hval & ~7)) & 0xFFFF;
                            else voffset = (y + hval) & 0xFFFF;
                        }
                    }
                    else
                    {
                        if ((hval & optValidBit) != 0) hoffset = (optX + (hval & ~7)) & 0xFFFF;
                        if ((vval & optValidBit) != 0) voffset = (y + vval) & 0xFFFF;
                    }
                }
            }
            hoffset &= maskX; voffset &= maskY;
            if ((hoffset >> 3) != prevX || (voffset >> 3) != prevY)
            {
                prevX = hoffset >> 3; prevY = voffset >> 3;
                tileNum = BgGetTile(bg, hoffset, voffset);
                mirrorY = (tileNum & 0x8000) != 0;
                mirrorX = (tileNum & 0x4000) != 0;
                tilePri = (tileNum & 0x2000) != 0 ? pri1 : pri0;
                palNum = (tileNum >> 10) & 7;
                palIndex = (bgpalIndex + (palNum << palSize)) & 0xFF;
                if (tileWidth == 4 && ((hoffset & 8) != 0) != mirrorX) tileNum++;
                if (tileHeight == 4 && ((voffset & 8) != 0) != mirrorY) tileNum += 16;
                tileNum &= 0x03FF; tileNum += tiledataIndex; tileNum &= tileMask;
                tileId = tileNum;
                int vo = voffset;
                if (mirrorY) vo ^= 7;
                tileRowY = vo & 7;
            }
            int ho = hoffset;
            if (mirrorX) ho ^= 7;
            int col = TilePixel(colorDepth, tileId, tileRowY, ho & 7);
            if (col == 0) continue;
            ushort c = isDirect ? DirectColour(palNum, col) : GetPalette(col + palIndex);
            if (!hires)
            {
                if (en && winMain[bg, x] == 0) SetMain(x, tilePri, bg, c, false);
                if (subEn && winSub[bg, x] == 0) SetSub(x, tilePri, bg, c, false);
            }
            else
            {
                int hx = x >> 1;
                if ((x & 1) != 0) { if (en && winMain[bg, hx] == 0) SetMain(hx, tilePri, bg, c, false); }
                else { if (subEn && winSub[bg, hx] == 0) SetSub(hx, tilePri, bg, c, false); }
            }
        }
    }

    // ----------------------------------------------------------------- mode7.cpp
    private static int SClip(int v, int bits) { int s = 32 - bits; return (v << s) >> s; }
    private static int Clip13(int x) => (x & 0x2000) != 0 ? (x | ~0x03FF) : (x & 0x03FF);

    private void Mode7(int bg, int pri0, int pri1)
    {
        if (!bgEnabled[bg] && !bgsubEnabled[bg]) return;
        int a = SClip(R.M7a, 16), b = SClip(R.M7b, 16), c = SClip(R.M7c, 16), d = SClip(R.M7d, 16);
        int cx = SClip(R.M7x, 13), cy = SClip(R.M7y, 13), hofs = SClip(R.M7hofs, 13), vofs = SClip(R.M7vofs, 13);
        BuildWindowTables(bg);
        int y = !mode7VFlip ? line : 255 - line;
        int mx_ = bg == BG1 ? (mosaicEnabled[BG1] ? mosaicSize : 0) : (mosaicEnabled[BG2] ? mosaicSize : 0);
        int my_ = mosaicEnabled[BG1] ? mosaicSize : 0;
        int yi = y & 0xFFF;
        int psx = ((a * Clip13(hofs - cx)) & ~63) + ((b * Clip13(vofs - cy)) & ~63) + ((b * mosaicTable[my_, yi]) & ~63) + (cx << 8);
        int psy = ((c * Clip13(hofs - cx)) & ~63) + ((d * Clip13(vofs - cy)) & ~63) + ((d * mosaicTable[my_, yi]) & ~63) + (cy << 8);
        for (int x = 0; x < 256; x++)
        {
            int px = (psx + a * mosaicTable[mx_, x]) >> 8;
            int py = (psy + c * mosaicTable[mx_, x]) >> 8;
            int tile, palette;
            switch (mode7Repeat)
            {
                default:
                    px &= 1023; py &= 1023;
                    tile = VB(((py >> 3 & 127) * 128 + (px >> 3 & 127)) << 1);
                    palette = VB((((tile << 6) + ((py & 7) << 3) + (px & 7)) << 1) + 1);
                    break;
                case 2:
                    if (px < 0 || px > 1023 || py < 0 || py > 1023) palette = 0;
                    else
                    {
                        px &= 1023; py &= 1023;
                        tile = VB(((py >> 3 & 127) * 128 + (px >> 3 & 127)) << 1);
                        palette = VB((((tile << 6) + ((py & 7) << 3) + (px & 7)) << 1) + 1);
                    }
                    break;
                case 3:
                    if (px < 0 || px > 1023 || py < 0 || py > 1023) tile = 0;
                    else { px &= 1023; py &= 1023; tile = VB(((py >> 3 & 127) * 128 + (px >> 3 & 127)) << 1); }
                    palette = VB((((tile << 6) + ((py & 7) << 3) + (px & 7)) << 1) + 1);
                    break;
            }
            int pri;
            if (bg == BG1) pri = pri0;
            else { pri = (palette >> 7) != 0 ? pri1 : pri0; palette &= 0x7F; }
            if (palette == 0) continue;
            int sx = !mode7HFlip ? x : 255 - x;
            ushort col = directColour && bg == BG1 ? DirectColour(0, palette) : GetPalette(palette);
            if (bgEnabled[bg] && winMain[bg, sx] == 0) SetMain(sx, pri, bg, col, false);
            if (bgsubEnabled[bg] && winSub[bg, sx] == 0) SetSub(sx, pri, bg, col, false);
        }
    }

    // ----------------------------------------------------------------- oam.cpp
    private struct Sprite { public int Width, Height, X, Y, Character; public bool UseNameselect, VFlip, HFlip; public int Palette, Priority; }
    private struct TileItem { public int X, Y, Pri, Pal, Tile; public bool HFlip; }
    private readonly Sprite[] sprites = new Sprite[128];
    private readonly int[] itemList = new int[32];
    private readonly TileItem[] tileList = new TileItem[34];
    private readonly byte[] oamLinePal = new byte[256], oamLinePri = new byte[256];
    private int oamTilecount, oamItemcount;
    const int OAM_PRI_NONE = 4;

    private void BuildSpriteList()
    {
        var oam = ppu.Oam;
        for (int i = 0; i < 128; i++)
        {
            int tb = oam[512 + (i >> 2)];
            bool x = (tb & (1 << ((i & 3) << 1))) != 0, size = (tb & (2 << ((i & 3) << 1))) != 0;
            ref var s = ref sprites[i];
            switch (cacheOamBasesize)
            {
                case 0: s.Width = s.Height = !size ? 8 : 16; break;
                case 1: s.Width = s.Height = !size ? 8 : 32; break;
                case 2: s.Width = s.Height = !size ? 8 : 64; break;
                case 3: s.Width = s.Height = !size ? 16 : 32; break;
                case 4: s.Width = s.Height = !size ? 16 : 64; break;
                case 5: s.Width = s.Height = !size ? 32 : 64; break;
                case 6: s.Width = !size ? 16 : 32; s.Height = !size ? 32 : 64; if (oamInterlace && !size) s.Height = 16; break;
                default: s.Width = !size ? 16 : 32; s.Height = 32; if (oamInterlace && !size) s.Height = 16; break;
            }
            int a = i * 4;
            s.X = ((x ? 1 : 0) << 8) + oam[a];
            s.Y = (oam[a + 1] + 1) & 0xFF;
            s.Character = oam[a + 2];
            s.VFlip = (oam[a + 3] & 0x80) != 0; s.HFlip = (oam[a + 3] & 0x40) != 0;
            s.Priority = (oam[a + 3] >> 4) & 3; s.Palette = (oam[a + 3] >> 1) & 7; s.UseNameselect = (oam[a + 3] & 1) != 0;
        }
    }

    private bool IsSpriteOnScanline(int idx)
    {
        ref var spr = ref sprites[idx];
        if (spr.X > 256 && (spr.X + spr.Width - 1) < 512) return false;
        int h = !oamInterlace ? spr.Height : spr.Height >> 1;
        if (line >= spr.Y && line < spr.Y + h) return true;
        if (spr.Y + h >= 256 && line < ((spr.Y + h) & 255)) return true;
        return false;
    }

    private void LoadOamTiles(int idx)
    {
        ref var spr = ref sprites[idx];
        int tileWidth = spr.Width >> 3;
        int x = spr.X;
        int y = (line - spr.Y) & 0xFF;
        if (oamInterlace) y <<= 1;
        if (spr.VFlip)
        {
            if (spr.Width == spr.Height) y = (spr.Height - 1) - y;
            else y = y < spr.Width ? (spr.Width - 1) - y : spr.Width + ((spr.Width - 1) - (y - spr.Width));
        }
        // interlace: y +/- field() with field 0
        x &= 511; y &= 255;
        int tdaddr = cacheOamTdaddr;
        int chrx = spr.Character & 15, chry = (spr.Character >> 4) & 15;
        if (spr.UseNameselect) tdaddr += (256 * 32) + (cacheOamNameselect << 13);
        chry += y >> 3; chry &= 15; chry <<= 4;
        for (int t = 0; t < tileWidth; t++)
        {
            int sx = (x + (t << 3)) & 511;
            if (x != 256 && sx >= 256 && (sx + 7) < 512) continue;
            if (oamTilecount++ >= 34) break;
            int n = oamTilecount - 1;
            int mxx = !spr.HFlip ? t : (tileWidth - 1) - t;
            int pos = tdaddr + ((chry + ((chrx + mxx) & 15)) << 5);
            tileList[n] = new TileItem { X = sx, Y = y, Pri = spr.Priority, Pal = 128 + (spr.Palette << 4), HFlip = spr.HFlip, Tile = (pos >> 5) & 0x07FF };
        }
    }

    private void RenderLineOamRto()
    {
        BuildSpriteList();
        oamItemcount = 0; oamTilecount = 0;
        Array.Fill(oamLinePri, (byte)OAM_PRI_NONE);
        Array.Fill(itemList, 0xFF);
        for (int s = 0; s < 34; s++) tileList[s].Tile = 0xFFFF;
        for (int s = 0; s < 128; s++)
        {
            int a = (s + oamFirstSprite) & 127;
            if (!IsSpriteOnScanline(a)) continue;
            if (oamItemcount++ >= 32) break;
            itemList[oamItemcount - 1] = a;
        }
        for (int s = 31; s >= 0; s--)
        {
            if (itemList[s] == 0xFF) continue;
            LoadOamTiles(itemList[s]);
        }
    }

    private void RenderOamTile(int n)
    {
        ref var t = ref tileList[n];
        int sx = t.X;
        for (int x = 0; x < 8; x++)
        {
            sx &= 511;
            if (sx < 256)
            {
                int col = TilePixel(1, t.Tile, t.Y & 7, !t.HFlip ? x : 7 - x);
                if (col != 0) { oamLinePal[sx] = (byte)(col + t.Pal); oamLinePri[sx] = (byte)t.Pri; }
            }
            sx++;
        }
    }

    private void RenderLineOam(int p0, int p1, int p2, int p3)
    {
        if (!bgEnabled[OAM] && !bgsubEnabled[OAM]) return;
        for (int s = 0; s < 34; s++) { if (tileList[s].Tile == 0xFFFF) continue; RenderOamTile(s); }
        BuildWindowTables(OAM);
        Span<int> priTbl = stackalloc int[] { p0, p1, p2, p3 };
        for (int x = 0; x < 256; x++)
        {
            if (oamLinePri[x] == OAM_PRI_NONE) continue;
            int pri = priTbl[oamLinePri[x]];
            ushort col = GetPalette(oamLinePal[x]);
            bool ce = oamLinePal[x] < 192;
            if (bgEnabled[OAM] && winMain[OAM, x] == 0) SetMain(x, pri, OAM, col, ce);
            if (bgsubEnabled[OAM] && winSub[OAM, x] == 0) SetSub(x, pri, OAM, col, ce);
        }
    }

    // ----------------------------------------------------------------- addsub.cpp + line.cpp
    private ushort AddSub(uint x, uint y, bool halve)
    {
        if (!colorMode)
        {
            if (!halve) { uint sum = x + y; uint carry = (sum - ((x ^ y) & 0x0421)) & 0x8420; return (ushort)((sum - carry) | (carry - (carry >> 5))); }
            return (ushort)((x + y - ((x ^ y) & 0x0421)) >> 1);
        }
        uint diff = x - y + 0x8420;
        uint borrow = (diff - ((x ^ y) & 0x8420)) & 0x8420;
        if (!halve) return (ushort)((diff - borrow) & (borrow - (borrow >> 5)));
        return (ushort)((((diff - borrow) & (borrow - (borrow >> 5))) & 0x7BDE) >> 1);
    }

    private ushort GetPixel(int x, bool swap)
    {
        ref var p = ref pc[x];
        ushort srcMain = swap ? p.SrcSub : p.SrcMain, srcSub; byte bgSub;
        if (!addsubMode) { bgSub = BACK; srcSub = colorRgb; }
        else { bgSub = swap ? p.BgMain : p.BgSub; srcSub = swap ? p.SrcMain : p.SrcSub; }
        if (winMain[COL, x] == 0)
        {
            if (winSub[COL, x] == 0) return 0;
            srcMain = 0;
        }
        bool ce = swap ? p.CeSub : p.CeMain; byte bgm = swap ? p.BgSub : p.BgMain;
        if (!ce && colorEnabled[bgm] && winSub[COL, x] != 0)
        {
            bool halve = false;
            if (colorHalve && winMain[COL, x] != 0) { if (!(addsubMode && bgSub == BACK)) halve = true; }
            return AddSub(srcMain, srcSub, halve);
        }
        return srcMain;
    }

    private ushort Luma(ushort c) => (ushort)(lightB[brightness, (c >> 10) & 31] + lightGR[brightness, c & 0x3FF]);

    private void RenderLineOutput(int row, bool hires)
    {
        int o = row * PPL;
        if (!hires)
        {
            for (int x = 0; x < 256; x++)
            {
                ushort c = GetPixel(x, false);
                if (brightness != 15) c = Luma(c);
                screen[o + 2 * x] = screen[o + 2 * x + 1] = c;
            }
            return;
        }
        uint prev = 0;
        for (int x = 0; x < 256; x++)
        {
            uint curr = GetPixel(x, true);
            if (brightness != 15) curr = Luma((ushort)curr);
            screen[o + 2 * x] = (ushort)((prev + curr - ((prev ^ curr) & 0x0421)) >> 1);
            prev = curr;
            curr = GetPixel(x, false);
            if (brightness != 15) curr = Luma((ushort)curr);
            screen[o + 2 * x + 1] = (ushort)((prev + curr - ((prev ^ curr) & 0x0421)) >> 1);
            prev = curr;
        }
    }

    /// <summary>The finished picture as 0xFFRRGGBB, <see cref="Width"/> x <see cref="Height"/>.</summary>
    public void CopyTo(uint[] frame)
    {
        int w = Width, h = Math.Min(Height, MaxH), step = frameHires ? 1 : 2;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int c = screen[y * PPL + x * step];
                int r = c & 31, g = (c >> 5) & 31, b = (c >> 10) & 31;
                frame[y * w + x] = 0xFF000000u | (uint)((r << 3 | r >> 2) << 16 | (g << 3 | g >> 2) << 8 | (b << 3 | b >> 2));
            }
    }
}
