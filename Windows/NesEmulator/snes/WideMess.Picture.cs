using System;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NesEmulator.Snes;

// WideMess milestone 2: the rest of the picture (docs/projects/widemess-spec.md, sections 3.1-3.4).
//   - one wide compositor for the WideMess mode (8 layers) and the SNES modes 0-7 drawn 400 wide: main and sub screens, windows, colour math
//   - the SNES modes wide: layers past the SNES's 256 columns, offset-per-tile, mosaic, hi-res, Mode 7 (bsnes-hd's widescreen Mode 7)
//   - sprites: the SNES table across 400 columns, or the WideMess table (256 sprites, 64 a line, 8 x 8 to 64 x 64, 4 or 8 bpp, 10-bit X)
//   - the registers $21A0-$21AF (sprites) and $21B0-$21BF (windows and colour math)
// The SNES chip's own pipeline is untouched: the 256-wide SNES picture is still PPU_SFC.RenderLine. The SNES registers are read through
// PPU_SFC.WideMess.cs's read-only accessors. Every buffer here is allocated once; a line allocates nothing.

public sealed partial class PPU_WMS
{
    // ================================================================ registers $21A0-$21BF

    public const int OamBytes = 2048, TableSprites = 256, SpritesPerLine = 64, SliversPerLine = 128;

    /// <summary>WideMess OAM: the WideMess sprite table, 256 sprites of 8 bytes (spec 3.3).</summary>
    public readonly byte[] WideOam = new byte[OamBytes];

    private byte objCtrl, objBase, objStat;
    private int oamAddr;
    private readonly byte[] objSlot = { 12, 8, 4, 0 };            // WMOBJPRI0-3: BG slots in front of each sprite priority
    private readonly ushort[] winPos = new ushort[4];               // WMWH0-3
    private readonly bool[] winPhase = new bool[4];
    private readonly byte[] winLow = new byte[4];
    private byte winCtrl, wmTs, wmTmw, wmTsw, wmCmath, wmWlog14, wmWlog58;
    private readonly byte[] wmWsel = new byte[4];

    /// <summary>WMOBJ bit 0: the WideMess sprite table draws instead of the SNES's (in the WideMess mode and the wide SNES modes).</summary>
    public bool WideSpriteTable => (objCtrl & 1) != 0;
    /// <summary>WMOBJSTAT: bit 6 more than 64 of the table's sprites on a line, bit 7 more than 128 slivers, this frame.</summary>
    public byte SpriteStatus => objStat;

    private void ResetPicture()
    {
        objCtrl = objBase = objStat = 0; oamAddr = 0;
        objSlot[0] = 12; objSlot[1] = 8; objSlot[2] = 4; objSlot[3] = 0;
        Array.Clear(winPos); Array.Clear(winPhase); Array.Clear(winLow); Array.Clear(wmWsel);
        winCtrl = wmTs = wmTmw = wmTsw = wmCmath = wmWlog14 = wmWlog58 = 0;
        Array.Fill(winSig, -1L);
    }

    private void BeginFramePicture()
    {
        if (!ForcedBlank) objStat = 0;
    }

    private void WritePicture(byte reg, byte v)
    {
        switch (reg)
        {
            case 0xA0: objCtrl = (byte)(v & 1); return;
            case 0xA1: objBase = (byte)(v & 7); return;
            case 0xA2: oamAddr = (oamAddr & 0x700) | v; return;
            case 0xA3: oamAddr = (oamAddr & 0x0FF) | (v & 7) << 8; return;
            case 0xA4: if (VideoPortsOpen) WideOam[oamAddr] = v; oamAddr = (oamAddr + 1) & (OamBytes - 1); return;
            case 0xA6: case 0xA7: case 0xA8: case 0xA9: objSlot[reg - 0xA6] = v; return;
            case 0xB0: case 0xB1: case 0xB2: case 0xB3:
            {
                int i = reg - 0xB0;
                if (!winPhase[i]) { winLow[i] = v; winPhase[i] = true; return; }
                winPos[i] = (ushort)(winLow[i] | (v & 1) << 8);
                winPhase[i] = false;
                return;
            }
            case 0xB4: winCtrl = (byte)(v & 1); Array.Clear(winPhase); return;
            case 0xB5: wmTs = v; return;
            case 0xB6: wmTmw = v; return;
            case 0xB7: wmTsw = v; return;
            case 0xB8: case 0xB9: case 0xBA: case 0xBB: wmWsel[reg - 0xB8] = v; return;
            case 0xBC: wmWlog14 = v; return;
            case 0xBD: wmWlog58 = v; return;
            case 0xBE: wmCmath = v; return;
        }
    }

    /// <summary>The readable registers of $21A0-$21BF: WMOAMDATA and WMOBJSTAT; -1 (open bus) for the rest.</summary>
    private int ReadPicture(byte reg)
    {
        switch (reg)
        {
            case 0xA4: { byte b = WideOam[oamAddr]; oamAddr = (oamAddr + 1) & (OamBytes - 1); return b; }
            case 0xA5: return objStat;
            default: return -1;
        }
    }

    // ================================================================ the wide compositor

    private const ushort Clear = 0x8000;            // transparent: a BGR555 colour never sets bit 15
    private const byte ObjId = 8, BackId = 9;       // layer ids on the screens: BG 0-7, sprites 8, backdrop 9 (bit n of lnMath)
    private const int ColourWindow = 9;             // win[]: BG 0-7, sprites 8, the colour window 9
    /// <summary>Picture column of the SNES picture's left edge in a 400-wide picture (spec 3.1).</summary>
    public const int SnesOrigin = (WideWidth - Width) / 2;
    private const int MaxHalf = 2 * WideWidth;      // hi-res wide lines are 800 half-pixels

    private readonly byte[] rankM = new byte[MaxHalf], rankS = new byte[MaxHalf], layM = new byte[MaxHalf], layS = new byte[MaxHalf];
    private readonly ushort[] colM = new ushort[MaxHalf], colS = new ushort[MaxHalf];
    private readonly ushort[] objCol = new ushort[WideWidth];
    private readonly byte[] objPri = new byte[WideWidth];
    private readonly bool[] objMth = new bool[WideWidth];
    private readonly bool[][] win = NewMasks();                     // BG 0-7, sprites 8, the colour window 9
    private readonly long[] winSig = { -1, -1, -1, -1, -1, -1, -1, -1, -1, -1 };
    private readonly ushort[][] bgCol = { new ushort[MaxHalf], new ushort[MaxHalf], new ushort[MaxHalf], new ushort[MaxHalf] };
    private readonly byte[][] bgPri = { new byte[MaxHalf], new byte[MaxHalf], new byte[MaxHalf], new byte[MaxHalf] };
    private readonly byte[] rankBg = new byte[16], rankObj = new byte[4];   // [layer * 2 + priority], [sprite priority]: front-to-back rank, lower wins
    private (byte layer, byte prio)[]? rankOrder;
    private readonly byte[] lnSel = new byte[10], lnLog = new byte[10];     // this line's window select and logic per layer id
    private int lnMain, lnSub, lnMainWin, lnSubWin, lnMath;               // this line's layer bits: BG 0-7, sprites bit 8, backdrop bit 9 (math)
    private int fBlack, fMathRegion; private bool fSubtract, fHalf, fSubNeeded, fColourWindow; private ushort fFixed;
    private bool lineOverflow;

    private static readonly ulong[] Expand = PlaneExpandTable(false), ExpandFlip = PlaneExpandTable(true);

    private static bool[][] NewMasks()
    {
        var m = new bool[10][];
        for (int i = 0; i < m.Length; i++) m[i] = new bool[WideWidth];
        return m;
    }

    /// <summary>Test: in a wide SNES mode, also draw each line on the SNES chip's own pipeline (into <see cref="PPU_SFC.FrameBuffer"/>) and compare it with the
    /// wide line's columns 72-327, which must be the same pixels (spec 3.1). Lines where the wide sprite pass overflowed its limits are counted apart:
    /// the wide picture has more sprites in range there, so a SNES drops different ones.</summary>
    public bool CompareWithSnes { get; set; }
    public int CompareRows { get; set; }
    public int CompareMismatches { get; set; }
    public int CompareOverflowRows { get; set; }
    public int CompareFirstMismatch { get; set; } = -1;
    /// <summary>Test: the compared lines by what they used: [0-7] BGMODE's mode, [8] mosaic on a shown layer, [9] colour math, [10] a window on a shown layer, [11] sprites shown.</summary>
    public readonly int[] CompareUse = new int[12];

    private void CompareLine(int line)
    {
        int row = line - 1;
        if (row < 0 || row >= VisibleHeight || (ctrl & 1) == 0) return;
        base.RenderLine(line);
        CompareRows++;
        if (!ForcedBlank && MasterBrightness != 0)
        {
            int shown = RegTm | RegTs;
            CompareUse[RegBgmode & 7]++;
            if ((RegMosaic & shown & 0x0F) != 0 && (RegMosaic >> 4) != 0) CompareUse[8]++;
            if ((RegCgadsub & 0x3F) != 0 || (RegCgwsel & 0xC0) != 0) CompareUse[9]++;
            if (((RegTmw | RegTsw) & shown & 0x1F) != 0) CompareUse[10]++;
            if ((shown & 0x10) != 0) CompareUse[11]++;
        }
        var snes = FrameBuffer.AsSpan(row * Width, Width);
        var wide = WideBuffer.AsSpan(row * WideWidth + SnesOrigin, Width);
        if (snes.SequenceEqual(wide)) return;
        if (lineOverflow) { CompareOverflowRows++; return; }
        CompareMismatches++;
        if (CompareFirstMismatch < 0) CompareFirstMismatch = row;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort VramPair(int a)       // a 16-bit pair of bytes at an even byte address (wraps at 512 KB)
    {
        a &= VramBytes - 1;
        if (a < 0x10000) return Vram[a >> 1];
        int h = a - 0x10000;
        return (ushort)(VramHigh[h] | VramHigh[h + 1] << 8);
    }

    private static int FloorMod(int a, int m) { int r = a % m; return r < 0 ? r + m : r; }
    private static int Clip13(int n) => (n & 0x2000) != 0 ? (n | ~0x3FF) : (n & 0x3FF);

    /// <summary>One line in the WideMess mode or a wide SNES mode (WMCTRL != 0), or a locked SNES game's enhanced line (WideMess.Enhance.cs: <paramref name="wide"/> is widescreen).</summary>
    private void RenderWideLine(int line, bool wide, bool layers8)
    {
        int row = line - 1;
        if (row < 0 || row >= VisibleHeight) return;
        lineOverflow = false;
        int width = wide ? WideWidth : Width, origin = wide ? SnesOrigin : 0;
        Span<uint> dst = wide ? WideBuffer.AsSpan(row * WideWidth, WideWidth) : FrameBuffer.AsSpan(row * Width, Width);
        if (wide) { rowWide[row] = true; anyWide = true; }
        int brightness = MasterBrightness;
        if (ForcedBlank || brightness == 0) { dst.Fill(0xFF000000u); return; }
        if (layers8) WideMessModeLine(row, width, origin, dst, brightness);
        else SnesModeWideLine(line, row, width, origin, dst, brightness);
    }

    // ---------------------------------------------------------------- per-line setup

    private void ObjAndColourWindowSelects()
    {
        byte wo = RegWobjsel, wl = RegWobjlog;
        lnSel[8] = (byte)(wo & 0x0F); lnLog[8] = (byte)(wl & 3);
        lnSel[9] = (byte)(wo >> 4); lnLog[9] = (byte)((wl >> 2) & 3);
    }

    private void BuildSnesRanks(int mode, bool bg3Top)
    {
        var order = SnesModeOrder(mode, bg3Top);
        if (ReferenceEquals(order, rankOrder)) return;
        Array.Fill(rankBg, (byte)0xFF); Array.Fill(rankObj, (byte)0xFF);
        for (int i = 0; i < order.Length; i++)
        {
            var (layer, prio) = order[i];
            if (layer == 4) rankObj[prio] = (byte)i;
            else if (prio == 2) { rankBg[layer * 2] = (byte)i; rankBg[layer * 2 + 1] = (byte)i; }   // Mode 7's BG1: either priority
            else rankBg[layer * 2 + prio] = (byte)i;
        }
        rankOrder = order;
    }

    /// <summary>The WideMess mode's order (spec 3): BG slot k (0-7 the high tiles of BG1-8, 8-15 their low tiles) has rank 2k + 1; sprites of priority p sit in front of
    /// slot WMOBJPRIp, rank 2 x WMOBJPRIp.</summary>
    private void BuildWideMessRanks()
    {
        rankOrder = null;
        for (int layer = 0; layer < Layers; layer++)
        {
            rankBg[layer * 2 + 1] = (byte)(2 * layer + 1);
            rankBg[layer * 2] = (byte)(2 * (Layers + layer) + 1);
        }
        for (int p = 0; p < 4; p++) rankObj[p] = (byte)(2 * Math.Min((int)objSlot[p], 16));
    }

    /// <summary>Clip-to-black, the math region and the sub screen for this line; false when the line has no colour math at all (the main screen is the output).</summary>
    private bool MathSetup()
    {
        byte cgw = RegCgwsel, adsub = RegCgadsub;
        fBlack = (cgw >> 6) & 3; fMathRegion = (cgw >> 4) & 3;
        fSubtract = (adsub & 0x80) != 0; fHalf = (adsub & 0x40) != 0; fFixed = RegColdata;
        fSubNeeded = (cgw & 0x02) != 0 && lnMath != 0;
        fColourWindow = fMathRegion is 1 or 2 || fBlack is 1 or 2;
        return lnMath != 0 || fBlack != 0;
    }

    private void ClearScreens(int total)
    {
        ushort back = Cgram[0];
        rankM.AsSpan(0, total).Fill(0xFF); colM.AsSpan(0, total).Fill(back); layM.AsSpan(0, total).Fill(BackId);
        if (!fSubNeeded) return;
        rankS.AsSpan(0, total).Fill(0xFF); colS.AsSpan(0, total).Fill(back); layS.AsSpan(0, total).Fill(BackId);
    }

    // ---------------------------------------------------------------- the WideMess mode

    private void WideMessModeLine(int row, int width, int origin, Span<uint> dst, int brightness)
    {
        int dbg = DebugLayerMask;
        lnMain = mainLayers | (RegTm & dbg & 0x10) << 4;
        lnSub = wmTs | (RegTs & dbg & 0x10) << 4;
        lnMainWin = wmTmw | (RegTmw & 0x10) << 4;
        lnSubWin = wmTsw | (RegTsw & 0x10) << 4;
        lnMath = wmCmath | (RegCgadsub & 0x30) << 4;
        for (int i = 0; i < Layers; i++)
        {
            lnSel[i] = (byte)((wmWsel[i >> 1] >> ((i & 1) * 4)) & 0x0F);
            lnLog[i] = (byte)(((i < 4 ? wmWlog14 : wmWlog58) >> ((i & 3) * 2)) & 3);
        }
        ObjAndColourWindowSelects();
        BuildWideMessRanks();
        bool math = MathSetup();
        int used = lnMain | (fSubNeeded ? lnSub : 0);
        ComputeWideWindows((lnMainWin & lnMain) | (fSubNeeded ? lnSubWin & lnSub : 0), fColourWindow, width, origin);
        ClearScreens(width);
        for (int layer = 0; layer < Layers; layer++)
        {
            int bit = 1 << layer;
            if ((used & bit) == 0) continue;
            WideMessLayer(layer, row, width, (lnMain & bit) != 0, fSubNeeded && (lnSub & bit) != 0);
        }
        Sprites(row, width, origin, used, width, 0);
        Finish(dst, ColourTable(brightness), width, false, math);
    }

    /// <summary>One WideMess-mode layer on one line, 8 pixels of a tile row at a time, straight into the screens.</summary>
    private void WideMessLayer(int layer, int row, int width, bool onMain, bool onSub)
    {
        byte rHigh = rankBg[layer * 2 + 1], rLow = rankBg[layer * 2];
        int bit = 1 << layer;
        bool winMain = onMain && (lnMainWin & bit) != 0, winSub = onSub && (lnSubWin & bit) != 0;
        var mask = win[layer];
        byte cfg = config[layer];
        int tilesWide = (cfg & 2) != 0 ? 128 : 64, tilesHigh = (cfg & 1) != 0 ? 64 : 32;
        int bank = ((cfg >> 2) & 3) << 8, tileBase = ((cfg >> 4) & 7) << 16, map = mapBase[layer] << 11;
        int y = (row + scrollV[layer]) & (tilesHigh * 8 - 1);
        int fy = y & 7;
        int rowBase = map + (y >> 3) * tilesWide * 2;
        int pxWide = tilesWide * 8;
        int mx = scrollH[layer] & (pxWide - 1);
        int x = 0;
        while (x < width)
        {
            int fx = mx & 7;
            int run = Math.Min(8 - fx, width - x);
            int entry = VramPair(rowBase + (mx >> 3) * 2);
            int ta = tileBase + (entry & 0xFFF) * 64 + ((entry & 0x8000) != 0 ? 7 - fy : fy) * 2;
            var e = (entry & 0x4000) != 0 ? ExpandFlip : Expand;
            int p0 = VramPair(ta), p1 = VramPair(ta + 16), p2 = VramPair(ta + 32), p3 = VramPair(ta + 48);
            ulong pixels = e[p0 & 0xFF] | e[p0 >> 8] << 1 | e[p1 & 0xFF] << 2 | e[p1 >> 8] << 3
                         | e[p2 & 0xFF] << 4 | e[p2 >> 8] << 5 | e[p3 & 0xFF] << 6 | e[p3 >> 8] << 7;
            if (pixels != 0)
            {
                byte rk = (entry & 0x1000) != 0 ? rHigh : rLow;
                ulong opaque = pixels & (run == 8 ? ulong.MaxValue : ((1UL << (run << 3)) - 1) << (fx << 3));
                int xBase = x - fx;
                while (opaque != 0)
                {
                    int lane = BitOperations.TrailingZeroCount(opaque) >> 3;
                    int c = (int)(opaque >> (lane << 3)) & 0xFF;
                    opaque &= ~(0xFFUL << (lane << 3));
                    int px = xBase + lane;
                    int ci = bank + c;
                    ushort v = ci < 256 ? Cgram[ci] : CgramHigh[ci - 256];
                    if (onMain && rk < rankM[px] && !(winMain && mask[px])) { rankM[px] = rk; colM[px] = v; layM[px] = (byte)layer; }
                    if (onSub && rk < rankS[px] && !(winSub && mask[px])) { rankS[px] = rk; colS[px] = v; layS[px] = (byte)layer; }
                }
            }
            x += run;
            mx = (mx + run) & (pxWide - 1);
        }
    }

    // ---------------------------------------------------------------- the SNES modes, wide

    private void SnesModeWideLine(int line, int row, int width, int origin, Span<uint> dst, int brightness)
    {
        byte bgmode = RegBgmode;
        int mode = bgmode & 7;
        bool hires = mode is 5 or 6;
        int dbg = DebugLayerMask;
        int tm = RegTm & dbg, ts = RegTs & dbg, tmw = RegTmw, tsw = RegTsw, adsub = RegCgadsub;
        lnMain = (tm & 0x0F) | (tm & 0x10) << 4;
        lnSub = (ts & 0x0F) | (ts & 0x10) << 4;
        lnMainWin = (tmw & 0x0F) | (tmw & 0x10) << 4;
        lnSubWin = (tsw & 0x0F) | (tsw & 0x10) << 4;
        lnMath = (adsub & 0x0F) | (adsub & 0x30) << 4;
        byte w12 = RegW12sel, w34 = RegW34sel, wbg = RegWbglog;
        lnSel[0] = (byte)(w12 & 0x0F); lnSel[1] = (byte)(w12 >> 4); lnSel[2] = (byte)(w34 & 0x0F); lnSel[3] = (byte)(w34 >> 4);
        for (int i = 0; i < 4; i++) lnLog[i] = (byte)((wbg >> (i * 2)) & 3);
        ObjAndColourWindowSelects();
        BuildSnesRanks(mode, (bgmode & 0x08) != 0);
        bool math = MathSetup();
        int used = lnMain | (fSubNeeded ? lnSub : 0);
        ComputeWideWindows((lnMainWin & lnMain) | (fSubNeeded ? lnSubWin & lnSub : 0), fColourWindow, width, origin);
        int total = hires ? 2 * width : width, shift = hires ? 1 : 0;
        ClearScreens(total);
        if (lnEnh && enhWide != null) BuildRemap(row);   // milestone 6: the widescreen profile's side rules for this row (WideMess.Enhance.cs)
        byte mosaic = RegMosaic;
        for (int bg = 0; bg < 4; bg++)
        {
            int bit = 1 << bg, bpp = SnesModeBpp(mode, bg);
            if ((used & bit) == 0 || mode == 7 || bpp == 0) continue;
            bool onMain = (lnMain & bit) != 0, onSub = fSubNeeded && (lnSub & bit) != 0;
            bool mosaicActive = (mosaic & bit) != 0 && (mosaic >> 4) != 0;
            if (FastPaths && !hires && !mosaicActive && mode is not (2 or 4 or 6) && !Remapped(bg)) { SnesBgFused(bg, bpp, mode, line, width, origin, onMain, onSub); continue; }
            SnesBg(bg, bpp, mode, line, width, origin);
            ComposeBgLine(bg, onMain, onSub, total, shift);
        }
        if (mode == 7 && (used & 3) != 0)
        {
            SnesMode7(line, width, origin);
            ComposeBgLine(0, (lnMain & 1) != 0, fSubNeeded && (lnSub & 1) != 0, total, shift);
            if ((RegSetini & 0x40) != 0) ComposeBgLine(1, (lnMain & 2) != 0, fSubNeeded && (lnSub & 2) != 0, total, shift);
        }
        Sprites(row, width, origin, used, total, shift);
        Finish(dst, ColourTable(brightness), width, hires, math);
        if (lnEnh)
        {
            if (enhWide != null) BlackColumns(dst);
            else if (hires) EnhancedHiRes(row, ColourTable(brightness), math);   // no flicker alone, a hi-res line: its sharp 512 half-pixels too, as the SNES chip keeps them
        }
    }

    private int CharBase(int bg) => ((bg < 2 ? RegBg12nba >> (bg * 4) : RegBg34nba >> ((bg - 2) * 4)) & 0x0F) << 12;

    private ushort SnesMapEntry(int mapBase, bool wide, bool tall, int col, int row)
    {
        int addr = mapBase + ((row & 31) << 5) + (col & 31);
        if (wide && (col & 32) != 0) addr += 0x400;
        if (tall && (row & 32) != 0) addr += wide ? 0x800 : 0x400;
        return Vram[addr & 0x7FFF];
    }

    /// <summary>A SNES layer over the whole picture, one pixel at a time, into bgCol/bgPri (the path for mosaic, offset-per-tile and hi-res, and the reference for the rest):
    /// the SNES chip's RenderBg with x counted from the SNES picture's left edge.</summary>
    private void SnesBg(int bg, int bpp, int mode, int line, int width, int origin)
    {
        var colors = bgCol[bg]; var prios = bgPri[bg];
        int sc = RegBgsc(bg);
        int mapBase = (sc & 0xFC) << 8;
        bool wideMap = (sc & 1) != 0, tall = (sc & 2) != 0;
        bool hires = mode is 5 or 6;
        bool big = (RegBgmode & (0x10 << bg)) != 0;
        int tileWShift = big || hires ? 4 : 3, tileHShift = big ? 4 : 3;
        int mapWMask = ((wideMap ? 64 : 32) << tileWShift) - 1, mapHMask = ((tall ? 64 : 32) << tileHShift) - 1;
        int charBase = CharBase(bg);
        int wordsPerTile = bpp * 4;
        int paletteBase = mode == 0 ? bg * 32 : 0;
        bool direct = bpp == 8 && (RegCgwsel & 0x01) != 0;
        bool opt = mode is 2 or 4 or 6;
        byte mos = RegMosaic;
        int mosaicSize = (mos & (1 << bg)) != 0 ? (mos >> 4) + 1 : 1;
        int y = mosaicSize > 1 ? line - (line - 1) % mosaicSize : line;
        int total = hires ? 2 * width : width, org = hires ? 2 * origin : origin;
        int hofsBg = RegHofs(bg), vofsBg = RegVofs(bg);
        var vram = Vram; var cgram = Cgram;
        int lastKey = -1; ushort entry = 0;
        for (int sx = 0; sx < total; sx++)
        {
            int s = sx - org;
            int x = mosaicSize > 1 ? s - FloorMod(s, hires ? mosaicSize * 2 : mosaicSize) : s;
            int hs = hofsBg, vs = vofsBg;
            if (opt && bg < 2) SnesOffsetPerTile(bg, mode, hires ? x >> 1 : x, ref hs, ref vs);
            int bx = (hires ? x + (hs << 1) : x + hs) & mapWMask;
            int by = (y + vs) & mapHMask;
            int col = bx >> tileWShift, rowT = by >> tileHShift;
            int key = rowT << 8 | col;
            if (key != lastKey) { entry = SnesMapEntry(mapBase, wideMap, tall, col, rowT); lastKey = key; }
            int px = bx & ((1 << tileWShift) - 1), py = by & ((1 << tileHShift) - 1);
            if ((entry & 0x4000) != 0) px = (1 << tileWShift) - 1 - px;
            if ((entry & 0x8000) != 0) py = (1 << tileHShift) - 1 - py;
            int tile = entry & 0x3FF;
            if (px >= 8) tile += 1;
            if (py >= 8) tile += 16;
            int addr = charBase + (tile & 0x3FF) * wordsPerTile + (py & 7);
            int bit = 7 - (px & 7), c = 0;
            for (int plane = 0; plane < bpp; plane += 2)
            {
                ushort w = vram[(addr + plane * 4) & 0x7FFF];
                c |= ((w >> bit) & 1) << plane | ((w >> (8 + bit)) & 1) << (plane + 1);
            }
            if (c == 0) { colors[sx] = Clear; continue; }
            int pal = (entry >> 10) & 7;
            colors[sx] = direct ? SnesDirectColour(c, pal) : cgram[(paletteBase + (bpp == 8 ? 0 : pal << bpp) + c) & 0xFF];
            prios[sx] = (byte)((entry >> 13) & 1);
        }
    }

    /// <summary>Modes 2/4/6: BG3's map supplies per-column scroll overrides for BG1/BG2. The SNES picture's first tile column is never offset; the columns left of it count -1, -2, ...</summary>
    private void SnesOffsetPerTile(int bg, int mode, int screenX, ref int hs, ref int vs)
    {
        int column = (screenX + (RegHofs(bg) & 7)) >> 3;
        if (column == 0) return;
        int sc = RegBgsc(2);
        int mapBase = (sc & 0xFC) << 8;
        bool wide = (sc & 1) != 0, tall = (sc & 2) != 0;
        int col = ((RegHofs(2) >> 3) + column - 1) & (wide ? 63 : 31);
        int rowT = (RegVofs(2) >> 3) & (tall ? 63 : 31);
        ushort enableBit = (ushort)(0x2000 << bg);
        ushort h = SnesMapEntry(mapBase, wide, tall, col, rowT);
        if (mode == 4)
        {
            if ((h & enableBit) == 0) return;
            if ((h & 0x8000) != 0) vs = h & 0x3FF; else hs = (h & 0x3F8) | (hs & 7);
            return;
        }
        ushort v = SnesMapEntry(mapBase, wide, tall, col, rowT + 1);
        if ((h & enableBit) != 0) hs = (h & 0x3F8) | (hs & 7);
        if ((v & enableBit) != 0) vs = v & 0x3FF;
    }

    /// <summary>The common case (no mosaic, no offset-per-tile, not hi-res): a tile row's 8 pixels at a time, straight into the screens. Pixel-identical to SnesBg + ComposeBgLine.</summary>
    private void SnesBgFused(int bg, int bpp, int mode, int line, int width, int origin, bool onMain, bool onSub)
    {
        byte r0 = rankBg[bg * 2], r1 = rankBg[bg * 2 + 1];
        if (r0 == 0xFF && r1 == 0xFF) return;
        int sc = RegBgsc(bg);
        int mapBase = (sc & 0xFC) << 8;
        bool wideMap = (sc & 1) != 0, tall = (sc & 2) != 0;
        bool big = (RegBgmode & (0x10 << bg)) != 0;
        int tileShift = big ? 4 : 3;
        int mapWMask = ((wideMap ? 64 : 32) << tileShift) - 1, mapHMask = ((tall ? 64 : 32) << tileShift) - 1;
        int charBase = CharBase(bg);
        int wordsPerTile = bpp * 4;
        int paletteBase = mode == 0 ? bg * 32 : 0;
        bool direct = bpp == 8 && (RegCgwsel & 0x01) != 0;
        int bit = 1 << bg;
        bool winMain = onMain && (lnMainWin & bit) != 0, winSub = onSub && (lnSubWin & bit) != 0;
        var vram = Vram; var cgram = Cgram; var mask = win[bg];
        int hs = RegHofs(bg);
        int by = (line + RegVofs(bg)) & mapHMask;
        int rowT = by >> tileShift, pyRaw = by & ((1 << tileShift) - 1);
        int tileWMask = (1 << tileShift) - 1;
        int sx = 0;
        while (sx < width)
        {
            int bx = (sx - origin + hs) & mapWMask;
            ushort entry = SnesMapEntry(mapBase, wideMap, tall, bx >> tileShift, rowT);
            bool hflip = (entry & 0x4000) != 0;
            int py = (entry & 0x8000) != 0 ? (1 << tileShift) - 1 - pyRaw : pyRaw;
            int pxRaw = bx & tileWMask;
            int half = pxRaw >> 3;
            if (hflip && tileShift == 4) half ^= 1;
            int tile = ((entry & 0x3FF) + half + (py >= 8 ? 16 : 0)) & 0x3FF;
            int addr = charBase + tile * wordsPerTile + (py & 7);
            var e = hflip ? ExpandFlip : Expand;
            int p0 = vram[addr & 0x7FFF];
            ulong pixels = e[p0 & 0xFF] | e[p0 >> 8] << 1;
            if (bpp >= 4)
            {
                int p1 = vram[(addr + 8) & 0x7FFF];
                pixels |= e[p1 & 0xFF] << 2 | e[p1 >> 8] << 3;
                if (bpp == 8)
                {
                    int p2 = vram[(addr + 16) & 0x7FFF], p3 = vram[(addr + 24) & 0x7FFF];
                    pixels |= e[p2 & 0xFF] << 4 | e[p2 >> 8] << 5 | e[p3 & 0xFF] << 6 | e[p3 >> 8] << 7;
                }
            }
            int start = pxRaw & 7, count = Math.Min(8 - start, width - sx);
            int pal = (entry >> 10) & 7;
            int palOffset = paletteBase + (bpp == 8 ? 0 : pal << bpp);
            byte r = (entry & 0x2000) != 0 ? r1 : r0;
            if (pixels == 0 || r == 0xFF) { sx += count; continue; }
            ulong opaque = pixels & (count == 8 ? ulong.MaxValue : ((1UL << (count << 3)) - 1) << (start << 3));
            int xBase = sx - start;
            while (opaque != 0)
            {
                int lane = BitOperations.TrailingZeroCount(opaque) >> 3;
                int c = (int)(opaque >> (lane << 3)) & 0xFF;
                opaque &= ~(0xFFUL << (lane << 3));
                int x = xBase + lane;
                ushort v = direct ? SnesDirectColour(c, pal) : cgram[(palOffset + c) & 0xFF];
                if (onMain && r < rankM[x] && !(winMain && mask[x])) { rankM[x] = r; colM[x] = v; layM[x] = (byte)bg; }
                if (onSub && r < rankS[x] && !(winSub && mask[x])) { rankS[x] = r; colS[x] = v; layS[x] = (byte)bg; }
            }
            sx += count;
        }
    }

    /// <summary>Mode 7 over the whole picture: the SNES chip's transformation, screen x from -72 to 327 (bsnes-hd's widescreen Mode 7).</summary>
    private void SnesMode7(int line, int width, int origin)
    {
        var c1 = bgCol[0]; var c2 = bgCol[1]; var p2 = bgPri[1];
        byte m7sel = RegM7sel, mos = RegMosaic;
        bool extbg = (RegSetini & 0x40) != 0;
        int a = RegM7a, b = RegM7b, c = RegM7c, d = RegM7d, x0 = RegM7x, y0 = RegM7y;
        int mosaicSize = (mos & 1) != 0 ? (mos >> 4) + 1 : 1;
        int y = mosaicSize > 1 ? line - (line - 1) % mosaicSize : line;
        if ((m7sel & 0x02) != 0) y = 255 - y;
        int hc = Clip13(RegM7hofs - x0), vc = Clip13(RegM7vofs - y0);
        int psx = ((a * hc) & ~63) + ((b * vc) & ~63) + ((b * y) & ~63) + (x0 << 8);
        int psy = ((c * hc) & ~63) + ((d * vc) & ~63) + ((d * y) & ~63) + (y0 << 8);
        int outside = m7sel >> 6;
        bool direct = (RegCgwsel & 0x01) != 0;
        var vram = Vram; var cgram = Cgram;
        for (int sx = 0; sx < width; sx++)
        {
            int s = sx - origin;
            int x = mosaicSize > 1 ? s - FloorMod(s, mosaicSize) : s;
            int xx = (m7sel & 0x01) != 0 ? 255 - x : x;
            int px = (psx + a * xx) >> 8, py = (psy + c * xx) >> 8;
            int tile;
            if (((px | py) & ~0x3FF) != 0 && outside >= 2)
            {
                if (outside == 2) { c1[sx] = Clear; c2[sx] = Clear; continue; }
                tile = 0;
            }
            else tile = vram[(((py >> 3) & 127) << 7) | ((px >> 3) & 127)] & 0xFF;
            int color = vram[(tile << 6 | (py & 7) << 3 | (px & 7)) & 0x7FFF] >> 8;
            c1[sx] = color == 0 ? Clear : direct ? SnesDirectColour(color, 0) : cgram[color];
            if (extbg)
            {
                int c7 = color & 0x7F;
                c2[sx] = c7 == 0 ? Clear : cgram[c7];
                p2[sx] = (byte)(color >> 7);
            }
        }
    }

    private void ComposeBgLine(int layer, bool onMain, bool onSub, int total, int shift)
    {
        if (Remapped(layer))
        {
            // milestone 6: a widescreen side rule other than extend on this line (WideMess.Enhance.cs)
            if (onMain) ComposeBgMapped(layer, (lnMainWin & (1 << layer)) != 0, total, shift, rankM, colM, layM);
            if (onSub) ComposeBgMapped(layer, (lnSubWin & (1 << layer)) != 0, total, shift, rankS, colS, layS);
            return;
        }
        if (onMain) ComposeBg(layer, (lnMainWin & (1 << layer)) != 0, total, shift, rankM, colM, layM);
        if (onSub) ComposeBg(layer, (lnSubWin & (1 << layer)) != 0, total, shift, rankS, colS, layS);
    }

    private void ComposeBg(int layer, bool windowed, int total, int shift, byte[] rank, ushort[] color, byte[] lay)
    {
        byte r0 = rankBg[layer * 2], r1 = rankBg[layer * 2 + 1];
        if (r0 == 0xFF && r1 == 0xFF) return;
        var src = bgCol[layer]; var prio = bgPri[layer]; var mask = win[layer];
        for (int bx = 0; bx < total; bx++)
        {
            ushort v = src[bx];
            if (v == Clear || (windowed && mask[bx >> shift])) continue;
            byte r = prio[bx] != 0 ? r1 : r0;
            if (r < rank[bx]) { rank[bx] = r; color[bx] = v; lay[bx] = (byte)layer; }
        }
    }

    // ---------------------------------------------------------------- sprites

    private void Sprites(int row, int width, int origin, int used, int total, int shift)
    {
        if ((used & 0x100) == 0) return;
        if (lnEnh)
        {
            // milestone 6: a locked SNES game's sprites, enhanced (WideMess.Enhance.cs)
            EnhancedSprites(row, width, origin);
            if (Remapped(WmsWidescreen.Sprites))
            {
                if ((lnMain & 0x100) != 0) ComposeObjMapped((lnMainWin & 0x100) != 0, total, shift, rankM, colM, layM);
                if (fSubNeeded && (lnSub & 0x100) != 0) ComposeObjMapped((lnSubWin & 0x100) != 0, total, shift, rankS, colS, layS);
                return;
            }
        }
        else if ((objCtrl & 1) != 0) WideTableSprites(row, width); else SnesTableSprites(row, width, origin);
        if ((lnMain & 0x100) != 0) ComposeObj((lnMainWin & 0x100) != 0, total, shift, rankM, colM, layM);
        if (fSubNeeded && (lnSub & 0x100) != 0) ComposeObj((lnSubWin & 0x100) != 0, total, shift, rankS, colS, layS);
    }

    private void ComposeObj(bool windowed, int total, int shift, byte[] rank, ushort[] color, byte[] lay)
    {
        var mask = win[ObjId];
        for (int bx = 0; bx < total; bx++)
        {
            int x = bx >> shift;
            ushort v = objCol[x];
            if (v == Clear || (windowed && mask[x])) continue;
            byte r = rankObj[objPri[x]];
            if (r < rank[bx]) { rank[bx] = r; color[bx] = v; lay[bx] = ObjId; }
        }
    }

    /// <summary>The SNES sprite table over the whole picture (spec 3.3): the SNES chip's sprite pass, with X read from -184 to 327 at 400 wide (picture column 72 + X) and its
    /// limits (32 sprites, 34 slivers) counted over the whole picture.</summary>
    private void SnesTableSprites(int row, int width, int origin)
    {
        objCol.AsSpan(0, width).Fill(Clear);
        byte obsel = RegObsel; ushort oamadd = RegOamadd;
        int sizeSel = obsel >> 5;
        int nameBase = (obsel & 7) << 13;
        int nameGap = (((obsel >> 3) & 3) + 1) << 12;
        int first = (oamadd & 0x8000) != 0 ? (oamadd >> 1) & 0x7F : 0;   // OAM priority rotation
        int split = Width + origin;                                      // X at or above this is X - 512
        var oam = Oam; var vram = Vram; var cgram = Cgram;
        Span<int> list = stackalloc int[32];
        int count = 0;
        bool range = false, time = false;
        for (int i = 0; i < 128; i++)
        {
            int n = (first + i) & 127;
            int hi = (oam[0x200 + (n >> 2)] >> ((n & 3) * 2)) & 3;
            var (w, h) = SnesObjSize(sizeSel, (hi & 2) != 0);
            int x = oam[n * 4] | (hi & 1) << 8;
            if (x >= split) x -= 512;
            if (origin + x + w <= 0 && x != -256) continue;
            if (((row - oam[n * 4 + 1]) & 0xFF) >= h) continue;
            if (count == 32) { range = true; break; }
            list[count++] = n;
        }
        // last sprite first, 34 slivers at most (on overflow the front sprites lose theirs, the SNES rule); later draws overwrite earlier ones
        int slivers = 0;
        for (int k = count - 1; k >= 0 && !time; k--)
        {
            int n = list[k];
            int hi = (oam[0x200 + (n >> 2)] >> ((n & 3) * 2)) & 3;
            var (w, h) = SnesObjSize(sizeSel, (hi & 2) != 0);
            int x = oam[n * 4] | (hi & 1) << 8;
            if (x >= split) x -= 512;
            int tileLo = oam[n * 4 + 2], attr = oam[n * 4 + 3];
            bool hflip = (attr & 0x40) != 0;
            int pal = (attr >> 1) & 7, prio = (attr >> 4) & 3;
            int r = (row - oam[n * 4 + 1]) & 0xFF;
            if ((attr & 0x80) != 0) r = h - 1 - r;
            int ty = r >> 3, fy = r & 7;
            int table = (attr & 1) != 0 ? nameBase + nameGap : nameBase;
            int tilesWide = w >> 3;
            var e = hflip ? ExpandFlip : Expand;
            for (int tx = 0; tx < tilesWide; tx++)
            {
                int sx = origin + x + tx * 8;
                if (sx <= -8 || sx >= width) continue;
                if (++slivers > 34) { time = true; break; }
                int ctx = hflip ? tilesWide - 1 - tx : tx;
                int chr = (((tileLo >> 4) + ty) & 0x0F) << 4 | ((tileLo + ctx) & 0x0F);
                int addr = (table + chr * 16 + fy) & 0x7FFF;
                int p01 = vram[addr], p23 = vram[(addr + 8) & 0x7FFF];
                ulong pixels = e[p01 & 0xFF] | e[p01 >> 8] << 1 | e[p23 & 0xFF] << 2 | e[p23 >> 8] << 3;
                if (pixels == 0) continue;
                int start = sx < 0 ? -sx : 0, end = sx > width - 8 ? width - sx : 8;
                for (int i = start; i < end; i++)
                {
                    int c = (int)(pixels >> (i << 3)) & 0xFF;
                    if (c == 0) continue;
                    objCol[sx + i] = cgram[128 + (pal << 4) + c];
                    objPri[sx + i] = (byte)prio;
                    objMth[sx + i] = pal >= 4;
                }
            }
        }
        RaiseSpriteOverflow(range, time);
        lineOverflow = range || time;
    }

    /// <summary>The WideMess sprite table (spec 3.3): 256 sprites, the first 64 on the line in table order, 128 slivers; a lower index is in front.</summary>
    private void WideTableSprites(int row, int width)
    {
        objCol.AsSpan(0, width).Fill(Clear);
        var oam = WideOam;
        int baseAddr = objBase << 16;
        int count = 0, slivers = 0;
        bool range = false, time = false;
        for (int n = 0; n < TableSprites; n++)
        {
            int o = n * 8;
            byte size = oam[o + 7];
            int h = 8 << ((size >> 2) & 3);
            int r = row - ((oam[o + 2] | (oam[o + 3] & 3) << 8) << 22 >> 22);
            if ((uint)r >= (uint)h) continue;
            int w = 8 << (size & 3);
            int x = (oam[o] | (oam[o + 1] & 3) << 8) << 22 >> 22;
            if (x + w <= 0 || x >= width) continue;
            if (count == SpritesPerLine) { range = true; break; }
            count++;
            if (time) continue;     // out of slivers: the rest are counted, not drawn
            byte attr = oam[o + 6];
            bool hflip = (attr & 0x40) != 0, bpp8 = (size & 0x40) != 0;
            if ((attr & 0x80) != 0) r = h - 1 - r;
            int ty = r >> 3, fy = r & 7, tilesWide = w >> 3;
            int tile = oam[o + 4] | (oam[o + 5] & 0x0F) << 8;
            int tileBytes = bpp8 ? 64 : 32;
            int colourBase = bpp8 ? ((attr & 0x3F) >> 4) << 8 : (attr & 0x3F) << 4;
            byte prio = (byte)((size >> 4) & 3);
            bool math = (size & 0x80) != 0;
            var e = hflip ? ExpandFlip : Expand;
            for (int tx = 0; tx < tilesWide; tx++)
            {
                int sx = x + tx * 8;
                if (sx <= -8 || sx >= width) continue;
                if (slivers == SliversPerLine) { time = true; break; }
                slivers++;
                int ctx = hflip ? tilesWide - 1 - tx : tx;
                int a = baseAddr + ((tile + ty * 16 + ctx) & 0xFFF) * tileBytes + fy * 2;
                int p0 = VramPair(a), p1 = VramPair(a + 16);
                ulong pixels = e[p0 & 0xFF] | e[p0 >> 8] << 1 | e[p1 & 0xFF] << 2 | e[p1 >> 8] << 3;
                if (bpp8)
                {
                    int p2 = VramPair(a + 32), p3 = VramPair(a + 48);
                    pixels |= e[p2 & 0xFF] << 4 | e[p2 >> 8] << 5 | e[p3 & 0xFF] << 6 | e[p3 >> 8] << 7;
                }
                if (pixels == 0) continue;
                int start = sx < 0 ? -sx : 0, end = sx > width - 8 ? width - sx : 8;
                for (int i = start; i < end; i++)
                {
                    int c = (int)(pixels >> (i << 3)) & 0xFF;
                    if (c == 0 || objCol[sx + i] != Clear) continue;   // a lower index already holds this pixel
                    int ci = colourBase + c;
                    objCol[sx + i] = ci < 256 ? Cgram[ci] : CgramHigh[ci - 256];
                    objPri[sx + i] = prio;
                    objMth[sx + i] = math;
                }
            }
        }
        if (range) objStat |= 0x40;
        if (time) objStat |= 0x80;
        lineOverflow = range || time;
    }

    // ---------------------------------------------------------------- windows

    /// <summary>The window masks of the layers something reads this line (bits 0-8: BG 0-7 and the sprites; the colour window apart), rebuilt only when their inputs changed.</summary>
    private void ComputeWideWindows(int needed, bool colourNeeded, int width, int origin)
    {
        int l1, r1, l2, r2;
        if ((winCtrl & 1) != 0) { l1 = winPos[0]; r1 = winPos[1]; l2 = winPos[2]; r2 = winPos[3]; }
        else
        {
            // the SNES's positions, counted from the SNES picture's left edge; a window edge on the SNES picture's edge (left 0, right 255) reaches the picture's edge (spec 3.4)
            byte a = RegWh0, b = RegWh1, c = RegWh2, d = RegWh3;
            bool reach = !(lnEnh && enhWide is { WindowsReachEdge: false });   // milestone 6: a widescreen profile can keep the SNES's edges where they are (spec 6.1)
            l1 = a == 0 && reach ? 0 : origin + a; r1 = b == 255 && reach ? width - 1 : origin + b;
            l2 = c == 0 && reach ? 0 : origin + c; r2 = d == 255 && reach ? width - 1 : origin + d;
        }
        for (int layer = 0; layer < 10; layer++)
        {
            if (layer < 9 ? (needed & (1 << layer)) == 0 : !colourNeeded) continue;
            int sel = lnSel[layer], logic = lnLog[layer];
            long signature = (uint)(sel | logic << 4) | (long)l1 << 6 | (long)r1 << 16 | (long)l2 << 26 | (long)r2 << 36 | (long)width << 46;
            if (FastPaths && winSig[layer] == signature) continue;
            winSig[layer] = signature;
            var mask = win[layer];
            bool en1 = (sel & 0x02) != 0, en2 = (sel & 0x08) != 0;
            if (!en1 && !en2) { mask.AsSpan(0, width).Clear(); continue; }
            bool inv1 = (sel & 0x01) != 0, inv2 = (sel & 0x04) != 0;
            for (int x = 0; x < width; x++)
            {
                bool w1 = (x >= l1 && x <= r1) ^ inv1;
                bool w2 = (x >= l2 && x <= r2) ^ inv2;
                mask[x] = !en2 ? w1 : !en1 ? w2 : logic switch
                {
                    0 => w1 | w2,
                    1 => w1 & w2,
                    2 => w1 ^ w2,
                    _ => !(w1 ^ w2),
                };
            }
        }
    }

    // ---------------------------------------------------------------- colour math and output

    private void Finish(Span<uint> dst, uint[] lut, int width, bool hires, bool math)
    {
        if (!hires && !math)
        {
            for (int x = 0; x < width; x++) dst[x] = lut[colM[x]];
            return;
        }
        for (int x = 0; x < width; x++)
        {
            ushort c;
            if (!hires) c = FinalPixel(x, x);
            else
            {
                ushort a = FinalPixel(x * 2, x), b = FinalPixel(x * 2 + 1, x);
                c = (ushort)((((a & 0x7BDE) + (b & 0x7BDE)) >> 1) + (a & b & 0x0421));   // per-channel average
            }
            dst[x] = lut[c];
        }
    }

    /// <summary>Clip-to-black and colour math for one pixel (the SNES chip's rules), from the composed screens.</summary>
    private ushort FinalPixel(int bx, int x)
    {
        int layer = layM[bx];
        ushort color = colM[bx];
        bool w = fColourWindow && win[ColourWindow][x];
        bool black = fBlack == 3 || (fBlack == 1 && !w) || (fBlack == 2 && w);
        if (black) color = 0;
        bool region = fMathRegion == 0 || (fMathRegion == 1 && w) || (fMathRegion == 2 && !w);
        bool layerMath = layer == ObjId ? (lnMath & 0x100) != 0 && objMth[x] : (lnMath & (1 << layer)) != 0;
        if (!region || !layerMath) return color;
        ushort addend = fFixed;
        bool halve = fHalf && !black;
        if (fSubNeeded)
        {
            if (layS[bx] == BackId) halve = false;    // the sub screen shows only its backdrop: the fixed colour, not halved
            else addend = colS[bx];
        }
        return BlendPacked(color, addend, fSubtract, halve);
    }

    // ---------------------------------------------------------------- state

    /// <summary>Milestone 2's part of a WideMess state (the session writes it after milestone 4's work RAM): the sprite table, the registers of $21A0-$21BF and the rows drawn 400 wide.</summary>
    public void SavePicture(BinaryWriter w)
    {
        w.Write(objCtrl); w.Write(objBase); w.Write(objStat); w.Write(oamAddr);
        w.Write(objSlot);
        foreach (var p in winPos) w.Write(p);
        foreach (var p in winPhase) w.Write(p);
        w.Write(winLow);
        w.Write(winCtrl); w.Write(wmTs); w.Write(wmTmw); w.Write(wmTsw); w.Write(wmCmath); w.Write(wmWlog14); w.Write(wmWlog58);
        w.Write(wmWsel);
        w.Write(WideOam);
        // the picture in progress: the rows drawn 400 wide this frame, and those rows (the SNES chip saves its own 256-wide frame the same way)
        w.Write(anyWide);
        for (int row = 0; row < MaxHeight; row++) w.Write(rowWide[row]);
        for (int row = 0; row < MaxHeight; row++)
            if (rowWide[row]) w.Write(MemoryMarshal.AsBytes(WideBuffer.AsSpan(row * WideWidth, WideWidth)));
    }

    public void LoadPicture(BinaryReader r)
    {
        objCtrl = (byte)(r.ReadByte() & 1); objBase = (byte)(r.ReadByte() & 7); objStat = r.ReadByte(); oamAddr = r.ReadInt32() & (OamBytes - 1);
        r.Read(objSlot);
        for (int i = 0; i < 4; i++) winPos[i] = (ushort)(r.ReadUInt16() & 0x1FF);
        for (int i = 0; i < 4; i++) winPhase[i] = r.ReadBoolean();
        r.Read(winLow);
        winCtrl = (byte)(r.ReadByte() & 1); wmTs = r.ReadByte(); wmTmw = r.ReadByte(); wmTsw = r.ReadByte(); wmCmath = r.ReadByte(); wmWlog14 = r.ReadByte(); wmWlog58 = r.ReadByte();
        r.Read(wmWsel);
        if (r.Read(WideOam) != WideOam.Length) throw new EndOfStreamException("the WideMess sprite table is cut short");
        anyWide = r.ReadBoolean();
        for (int row = 0; row < MaxHeight; row++) rowWide[row] = r.ReadBoolean();
        for (int row = 0; row < MaxHeight; row++)
            if (rowWide[row]) SnesStateIo.Get(r, WideBuffer.AsSpan(row * WideWidth, WideWidth));
        Array.Fill(winSig, -1L);   // the masks describe the machine that was here before
        rankOrder = null;
    }
}
