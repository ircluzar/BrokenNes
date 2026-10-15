using System;

namespace NesEmulator.Sega;

/// <summary>
/// The picture side of the Genesis video chip: the two scroll planes, the window, the sprites, the priority ladder, shadow and highlight, and the colour output. Written clean-room from the public
/// documentation (Sega's Genesis Technical Overview, MacDonald's genvdp.txt and window test notes, Kabuto's and Nemesis' notes, Plutiedev); BlastEm was only run and looked at.
/// </summary>
/// <remarks>
/// <para><b>Per line, caught up lazily.</b> A line is set up when the VDP enters it (<see cref="BeginLineRender"/>: the horizontal scroll values and the sprites on the line, which the hardware fetches during
/// the end of the line before) and is drawn in two stages that follow the output pixel: the <i>index</i> stage (planes, window, sprites, priority and shadow state into one byte per pixel) and the
/// <i>colour</i> stage (that byte through colour RAM into ARGB). Before anything that changes what a pixel looks like (a register, colour RAM, VSRAM, VRAM) the VDP is made to draw the pixels the
/// hardware has already put out (<see cref="SyncRender"/>), so a write in the middle of a line splits the line where it happened: the index stage runs a column ahead of the output (the
/// hardware fetches ahead), the colour stage stops at the pixel being output, which is what makes mid-line palette changes, the colour-RAM dots and the direct-colour DMA picture come out.</para>
/// <para><b>Index byte.</b> Layer pixels are one byte: bit 7 priority, bits 5-4 palette line, bits 3-0 colour, and zero is transparent. The final byte per pixel is the palette index (bits 5-0) with the
/// shadow state in bits 7-6 (0 normal, 1 shadow, 2 highlight), which is also the index into <c>pal</c>, a table of 192 colours.</para>
/// </remarks>
public sealed partial class MdVdp
{
    public const int PictureStride = 320, PictureMaxHeight = 240;

    private uint[] picture = new uint[PictureStride * PictureMaxHeight];
    private uint[] building = new uint[PictureStride * PictureMaxHeight];

    /// <summary>The last finished frame: <see cref="PictureWidth"/> by <see cref="PictureHeight"/> pixels at a row stride of <see cref="PictureStride"/>, 0xAARRGGBB.</summary>
    public uint[] Picture => picture;
    public int PictureWidth { get; private set; } = 320;
    public int PictureHeight { get; private set; } = 224;
    private readonly uint[] woven = new uint[PictureStride * PictureMaxHeight * 2];
    private bool wovenValid;
    /// <summary>In interlace mode 2 the double-height picture: the lines of both fields woven (this field's rows are new, the other field's are from the frame before), <see cref="WovenHeight"/> rows at a stride of <see cref="PictureStride"/>.
    /// <see cref="Picture"/> is always the one field this frame drew, so a consumer that knows nothing of interlace keeps seeing a normal picture.</summary>
    public uint[] WovenPicture => woven;
    public int WovenHeight { get; private set; } = 448;
    /// <summary>The last finished frame was drawn in interlace mode 2: <see cref="WovenPicture"/> is the picture to show.</summary>
    public bool WovenValid => wovenValid;
    /// <summary>Frames the renderer has finished.</summary>
    public long FramesRendered { get; private set; }

    /// <summary>The 15 output levels of the video DAC (hardware measurements as the oracle shows them): shadow uses steps 0-7, normal the even steps, highlight steps 7-14.</summary>
    private static readonly byte[] Levels15 = { 0, 27, 49, 71, 87, 103, 119, 130, 146, 157, 174, 190, 206, 228, 255 };
    private readonly uint[] pal = new uint[192];

    // ---- the line being drawn
    private readonly byte[] lineIdx = new byte[PictureStride + 32];
    private readonly byte[] bufA = new byte[PictureStride + 32], bufB = new byte[PictureStride + 32], sprLine = new byte[PictureStride + 32];
    private bool lineActive;          // the current line is an active one and has been begun
    private int lineY, lineWidth, linePxDiv, renderX, colourX, rowBase;
    // interlace mode 2 (double resolution): the field's line y is the picture row 2y + field of a picture twice as tall, cells are 8x16 (cellShift 4, tiles two patterns of 32 bytes)
    private bool dbl;
    private int imgRow, cellShift = 3;
    private int hscrollA, hscrollB;
    private bool sprOverflow, sprCollision;
    /// <summary>Census: picture lines on which more sprites than the per-line limit were found.</summary>
    public long SpriteOverflowLines;
    private bool sprDirty;
    private bool prevLineDotLimit;     // the previous line used up its pixel budget: the first X=0 sprite of this line then masks (Nemesis' sprite masking test)

    /// <summary>How many pixels the index stage runs ahead of the output pixel when it is synchronised (the hardware fetches a couple of cells ahead).</summary>
    public int RenderLead = 16;

    /// <summary>Palette index for the backdrop, shadow state excluded.</summary>
    private int BackdropIndex => Reg[7] & 0x3F;

    private void InitRender()
    {
        for (int i = 0; i < 64; i++) UpdatePal(i);
        lineActive = false; renderX = colourX = 0;
        sprOverflow = sprCollision = false; sprDirty = false;
        Array.Clear(picture); Array.Clear(building);
        FramesRendered = 0; PictureWidth = 320; PictureHeight = 224;
    }

    private static uint Argb(int c, int mode)
    {
        // mode 0 normal, 1 shadow, 2 highlight; c is the 9-bit colour (0000BBB0GGG0RRR0)
        int r = c >> 1 & 7, g = c >> 5 & 7, b = c >> 9 & 7;
        int Lv(int v) => mode == 0 ? Levels15[v * 2] : mode == 1 ? Levels15[v] : Levels15[v + 7];
        return 0xFF000000u | (uint)(Lv(r) << 16 | Lv(g) << 8 | Lv(b));
    }

    private void UpdatePal(int i)
    {
        int c = Cram[i];
        pal[i] = Argb(c, 0); pal[64 + i] = Argb(c, 1); pal[128 + i] = Argb(c, 2);
    }

    // =================================================================================================================
    //  Time to pixel, and the stages
    // =================================================================================================================

    /// <summary>The pixel being output at the VDP's clock (0 when the line has not begun its picture, <see cref="lineWidth"/> or more when the active part is over). The line starts at its H counter 0; the
    /// first picture pixel is <see cref="PixelOrigin"/> clocks later.</summary>
    private int PixelNow()
    {
        long off = Time - lineStart - PixelOrigin;
        if (off <= 0) return 0;
        long p = off / linePxDiv;
        return p > lineWidth ? lineWidth : (int)p;
    }

    /// <summary>Draw what the hardware has put out up to now, so that a change made now only shows from here on.</summary>
    private void SyncRender()
    {
        LatchWindowIfDue();
        if (!lineActive) return;
        int px = PixelNow();
        RenderTo(px + RenderLead);
        ConvertTo(px);
    }

    // The window's position registers are taken as the line's active picture ends and used for the next line: a write after that moment (MacDonald's window test polls the V counter and
    // switches the window off, and the oracle still shows it on the line the counter names) first shows on the line after.
    public const int WindowLatchOffset = 2560;
    private bool winLatchDone;
    private byte winNext17, winNext18, winCur17, winCur18;

    private void LatchWindowIfDue()
    {
        if (winLatchDone || Time - lineStart < WindowLatchOffset) return;
        winNext17 = Reg[17]; winNext18 = Reg[18]; winLatchDone = true;
    }

    /// <summary>Pixels the index stage drew ahead of the output are drawn again after a change that acts at the output.</summary>
    private void RewindIndexStage()
    {
        if (!lineActive) return;
        int px = PixelNow();
        if (renderX > px) renderX = px;
    }

    private void BeginLineRender()
    {
        if (!winLatchDone) { winNext17 = Reg[17]; winNext18 = Reg[18]; }
        winCur17 = winNext17; winCur18 = winNext18; winLatchDone = false;
        lineActive = Line < ActiveLines && Line < PictureMaxHeight;
        if (!lineActive) return;
        lineY = Line;
        dbl = Interlace == 3;
        imgRow = dbl ? lineY * 2 + (oddFrame ? 1 : 0) : lineY;
        cellShift = dbl ? 4 : 3;
        lineWidth = H40 ? 320 : 256;
        linePxDiv = H40 ? 8 : 10;
        renderX = colourX = 0;
        rowBase = lineY * PictureStride;
        // horizontal scroll: the table entry of this line (the hardware reads it as the line begins)
        int mask = (Reg[11] & 3) switch { 0 => 0, 1 => 7, 2 => 0xF8, _ => 0xFF };
        int ea = ((Reg[13] & 0x3F) << 10) + ((lineY & mask) << 2);
        hscrollA = (Vram[ea & 0xFFFF] << 8 | Vram[ea + 1 & 0xFFFF]) & 0x3FF;
        hscrollB = (Vram[ea + 2 & 0xFFFF] << 8 | Vram[ea + 3 & 0xFFFF]) & 0x3FF;
        BuildSprites();
    }

    /// <summary>Tools: keep, for the finished frame, each pixel's layer values (the plane A, plane B and sprite bytes of the index stage: bit 7 priority, bits 5-4 palette line, bits 3-0 colour) in
    /// <see cref="DebugA"/>, <see cref="DebugB"/>, <see cref="DebugS"/> (rows of <see cref="PictureStride"/>, the field's lines).</summary>
    public bool DebugKeepLayers;
    public byte[]? DebugA, DebugB, DebugS;

    private void FinishLineRender()
    {
        if (!lineActive) return;
        RenderTo(lineWidth);
        ConvertTo(lineWidth);
        if (DebugKeepLayers)
        {
            DebugA ??= new byte[PictureStride * PictureMaxHeight]; DebugB ??= new byte[PictureStride * PictureMaxHeight]; DebugS ??= new byte[PictureStride * PictureMaxHeight];
            Array.Copy(bufA, 0, DebugA, rowBase, lineWidth); Array.Copy(bufB, 0, DebugB, rowBase, lineWidth); Array.Copy(sprLine, 0, DebugS, rowBase, lineWidth);
        }
        lineActive = false;
        if (dbl) Array.Copy(building, rowBase, woven, imgRow * PictureStride, lineWidth);       // the field's line goes to its row of the double-height picture; the other field's rows stay from the frame before
        if (Line + 1 >= ActiveLines || Line + 1 >= PictureMaxHeight)
        {
            // the last active line: the frame is finished, hand it over
            (picture, building) = (building, picture);
            PictureWidth = lineWidth; PictureHeight = Math.Min(ActiveLines, PictureMaxHeight);
            wovenValid = dbl; WovenHeight = Math.Min(ActiveLines, PictureMaxHeight) * 2;
            FramesRendered++;
        }
    }

    private void ConvertTo(int x1)
    {
        if (x1 > lineWidth) x1 = lineWidth;
        if (x1 <= colourX) return;
        var dst = building; var idx = lineIdx; var p = pal;
        int o = rowBase;
        for (int x = colourX; x < x1; x++) dst[o + x] = p[idx[x]];
        colourX = x1;
    }

    /// <summary>The index stage up to pixel <paramref name="x1"/> of the line: planes, window, sprites, priority.</summary>
    private void RenderTo(int x1)
    {
        if (x1 > lineWidth) x1 = lineWidth;
        if (x1 <= renderX) return;
        int x0 = renderX; renderX = x1;
        int bd = BackdropIndex;
        if (!DisplayEnabled || !Mode5)
        {
            for (int x = x0; x < x1; x++) lineIdx[x] = (byte)bd;
            return;
        }
        DrawPlane(bufB, false, x0, x1);
        DrawPlane(bufA, true, x0, x1);
        Compose(x0, x1, bd);
    }

    // =================================================================================================================
    //  Planes
    // =================================================================================================================

    private void DrawPlane(byte[] dst, bool isA, int x0, int x1)
    {
        if (!isA) { DrawScrollRun(dst, false, x0, x1, -1, -1); return; }
        // the window replaces plane A where it covers the line: everywhere on a line the vertical part reaches, else the stretch the horizontal part names
        int wx0 = 0, wx1 = 0;
        int vpos = (winCur18 & 0x1F) * 8;
        bool vwin = (winCur18 & 0x80) != 0 ? lineY >= vpos : lineY < vpos;
        bool leftWindow = false;
        if (vwin) { wx0 = 0; wx1 = lineWidth; }
        else
        {
            int hpos = (winCur17 & 0x1F) * 16;
            if ((winCur17 & 0x80) != 0) { wx0 = Math.Min(hpos, lineWidth); wx1 = lineWidth; }
            else { wx0 = 0; wx1 = Math.Min(hpos, lineWidth); leftWindow = wx1 > 0 && wx1 < lineWidth; }
        }
        // the window bug (MacDonald): with the window at the left and the plane's fine scroll not a multiple of 16, the two cells after it take their name table data from two cells further on
        int bugX0 = -1, bugX1 = -1;
        if (leftWindow && WindowBugApplies(hscrollA)) { bugX0 = wx1; bugX1 = wx1 + 16; }
        int a = x0, b = Math.Min(x1, wx0);
        if (b > a) DrawScrollRun(dst, true, a, b, bugX0, bugX1);
        a = Math.Max(x0, wx0); b = Math.Min(x1, wx1);
        if (b > a) DrawWindowRun(dst, a, b);
        a = Math.Max(x0, wx1); b = x1;
        if (b > a) DrawScrollRun(dst, true, a, b, bugX0, bugX1);
    }

    /// <summary>Experiment hook for the window distortion bug's condition on the plane's fine scroll (0: any of the low four bits; 1: bit 3).</summary>
    public static int WindowBugMode = 0;
    /// <summary>Experiment: the column whose vertical scroll entry a 16-pixel column uses is this many columns further on.</summary>
    public static int VsramColumnShift = 0;
    public static bool ColumnPairRule = true;
    /// <summary>The window-edge stretch of the first plane A pair uses the entry of the pair after it (false: the entry the pair would have without the window).</summary>
    public static bool WindowStretchColumn = true;
    /// <summary>The left stretch of the 2-cell column scroll in H32 mode (the plane's partial pair at the left edge) uses vertical scroll 0, not the two last entries ANDed as in H40.</summary>
    public static bool H32StretchZero = true;
    /// <summary>Interlace mode 2 uses eleven bits of the vertical scroll value (the oracle's 128-cell-high plane scenes need it), the other modes ten.</summary>
    public static bool VsramIm2Mask11 = true;
    /// <summary>Experiment: the lowest sprite colour (palettes 0 to 2) that is drawn at normal brightness over shadow: 14 (colour 14 only) is what the oracle shows; 14 with 15 as well was the first guess.</summary>
    public static int SpriteNormalFromColour = 14;
    public static int SpriteNormalToColour = 14;
    private static bool WindowBugApplies(int hscroll) => WindowBugMode switch { 0 => (hscroll & 0xF) != 0, 1 => (hscroll & 8) != 0, 2 => false, _ => (hscroll & 0xF) != 0 };

    private void DrawWindowRun(byte[] dst, int xa, int xb)
    {
        int nt = (Reg[3] & (H40 ? 0x3C : 0x3E)) << 10;
        int cells = H40 ? 64 : 32;
        int row = imgRow >> cellShift, r = imgRow & (1 << cellShift) - 1;
        int x = xa;
        while (x < xb)
        {
            int cell = x >> 3, fine = x & 7;
            int len = Math.Min(8 - fine, xb - x);
            int ea = nt + (row * cells + cell) * 2;
            int e = Vram[ea & 0xFFFF] << 8 | Vram[ea + 1 & 0xFFFF];
            DrawTileRun(dst, x, len, e, r, fine);
            x += len;
        }
    }

    /// <summary>Experiment: what a combination of width and height codes that does not fit the 4096 entries of a plane (128 x 64, 128 x 128, 64 x 128) draws.</summary>
    public static int PlaneSizeRule = 6;
    /// <summary>Experiment: false restores the old rule (a sprite pixel acts even where a visible high-priority plane pixel covers it).</summary>
    public static bool ShadowSpriteOverHighPlane = true;
    private static void PlaneSizeFix(ref int ws, ref int hs)
    {
        switch (PlaneSizeRule)
        {
            case 1: if (ws == 3 && hs != 0) hs = 0; else if (hs == 3 && ws != 0) ws = 0; break;
            case 2: if (hs == 3 && ws != 0) ws = 0; else if (ws == 3 && hs != 0) hs = 0; break;
            case 3: if (ws == 3 && hs != 0) hs = 0; if (hs == 3 && ws != 0) ws = 0; break;
            case 4: if (ws == 3 && hs != 0) hs = 0; if (hs == 3 && ws != 0) ws = 1; break;
            case 5: if (ws == 3 && hs != 0) hs = 0; else if (hs == 3 && ws != 0) hs = 0; break;
            case 6: if (ws == 3 && hs != 0) hs = 0; else if (hs == 3 && ws != 0) hs = 1; break;
            case 7: if (ws == 3 && hs != 0) hs = 0; else if (hs == 3 && ws != 0) hs = 2; break;
        }
    }

    private void DrawScrollRun(byte[] dst, bool isA, int xa, int xb, int bugX0, int bugX1)
    {
        int nt = isA ? (Reg[2] & 0x38) << 10 : (Reg[4] & 7) << 13;
        int ws = Reg[16] & 3, hs2 = Reg[16] >> 4 & 3;
        PlaneSizeFix(ref ws, ref hs2);
        int wCells = ws == 1 ? 64 : ws == 3 ? 128 : 32;
        int hCells = hs2 == 1 ? 64 : hs2 == 3 ? 128 : 32;
        int wMask = wCells * 8 - 1, hMask = (hCells << cellShift) - 1;
        int rowStride = ws == 2 ? 0 : wCells;        // the invalid width code 2 makes every row read row 0 (MacDonald's window test: 'row 0 repeat')
        int hs = isA ? hscrollA : hscrollB;
        bool colV = (Reg[11] & 4) != 0;
        int vsOff = isA ? 0 : 1;
        int vsMask = dbl && VsramIm2Mask11 ? 0x7FF : 0x3FF;      // eleven bits of vertical scroll in interlace mode 2 (a 128-cell plane is 2048 lines there)
        int vsFull = Vsram[vsOff] & vsMask;
        int x = xa;
        int bugColumn0 = bugX0 >= 0 ? (bugX0 - hs & wMask) >> 4 : 0;
        while (x < xb)
        {
            int vs, limit = xb;
            if (colV)
            {
                // 2-cell column scroll: a vertical scroll entry covers a pair of the plane's cells, so the pair boundaries sit where the scrolled plane has them (screen x = hscroll mod 16 + 16 k), and the entry
                // number counts the pairs from the first boundary. The stretch before it (the plane's partial pair at the left edge) uses the last column's two entries ANDed (probe scenes on the oracle: every
                // pair, every fine and coarse scroll, both planes; the left stretch decodes as VSRAM[last A] & VSRAM[last B])
                int b0 = ColumnPairRule ? hs & 15 : 0, lastCol = H40 ? 19 : 15;
                if (x < b0) { vs = H40 || !H32StretchZero ? (Vsram[lastCol << 1] & Vsram[(lastCol << 1) + 1]) & vsMask : 0; limit = Math.Min(xb, b0); }
                else
                {
                    int col = x - b0 >> 4;
                    limit = Math.Min(xb, b0 + (col + 1 << 4));
                    // after a window at the left the part of the first pair that the window leaves (x from the window's edge to the edge plus the fine scroll) takes the entry of the pair after it (oracle: probe 3)
                    if (WindowStretchColumn && bugX0 >= 0 && x >= bugX0 && x < bugX0 + b0) { col = bugX0 >> 4; limit = Math.Min(xb, bugX0 + b0); }
                    vs = Vsram[(col + VsramColumnShift << 1) + vsOff & 0x3F] & vsMask;
                }
            }
            else vs = vsFull;
            int py = imgRow + vs & hMask;
            int px = x - hs & wMask;
            int cell = px >> 3, fine = px & 7;
            int len = Math.Min(8 - fine, limit - x);
            // the window bug: the plane column (a pair of cells) that supplies the left part of the first screen column after the window is read from the column after it
            if (x >= bugX0 && x < bugX1 && (cell >> 1) == bugColumn0) cell = cell + 2 & (wCells - 1);
            int ea = nt + ((py >> cellShift) * rowStride + cell) * 2;
            int e = Vram[ea & 0xFFFF] << 8 | Vram[ea + 1 & 0xFFFF];
            DrawTileRun(dst, x, len, e, py & (1 << cellShift) - 1, fine);
            x += len;
        }
    }

    /// <summary>Put <paramref name="len"/> pixels of one tile row on the line at <paramref name="x"/>, starting at pixel <paramref name="fine"/> of the tile row; <paramref name="e"/> is the name table entry.</summary>
    private void DrawTileRun(byte[] dst, int x, int len, int e, int row, int fine)
    {
        int a = ((e & 0x7FF) << cellShift + 2) + (((e & 0x1000) != 0 ? (1 << cellShift) - 1 - row : row) << 2);
        uint bits = (uint)(Vram[a & 0xFFFF] << 24 | Vram[a + 1 & 0xFFFF] << 16 | Vram[a + 2 & 0xFFFF] << 8 | Vram[a + 3 & 0xFFFF]);
        int flags = e >> 8 & 0x80 | e >> 9 & 0x30;
        if ((e & 0x800) != 0)
        {
            for (int k = 0; k < len; k++)
            {
                int c = (int)(bits >> (fine + k) * 4) & 15;
                dst[x + k] = c == 0 ? (byte)(flags & 0x80) : (byte)(flags | c);
            }
        }
        else
        {
            for (int k = 0; k < len; k++)
            {
                int c = (int)(bits >> 28 - (fine + k) * 4) & 15;
                dst[x + k] = c == 0 ? (byte)(flags & 0x80) : (byte)(flags | c);
            }
        }
    }

    // =================================================================================================================
    //  Sprites
    // =================================================================================================================

    /// <summary>A sprite's Y position has 9 bits (bit 9 of the table word is ignored) except in interlace mode 2, where it has 10 (measured on BlastEm with attribute-cache scenes whose cached Y words carry random bit 9).</summary>
    public static bool SpriteY9 = true;
    private int SpriteYMask => SpriteY9 && !dbl ? 0x1FF : 0x3FF;

    private readonly int[] lineSprites = new int[24];
    private int lineSpriteCount;
    private bool lineSpriteOverflow;

    /// <summary>The sprites that touch this line, in link-list order, up to the per-line limit (20 in H40, 16 in H32); the table is followed from sprite 0 for at most 80 (64) sprites.</summary>
    private void BuildSprites()
    {
        lineSpriteCount = 0; lineSpriteOverflow = false;
        int maxSprites = H40 ? 80 : 64, maxLine = H40 ? 20 : 16;
        int sat = (Reg[5] & (H40 ? 0x7E : 0x7F)) << 9;
        int idx = 0;
        for (int n = 0; n < maxSprites; n++)
        {
            int a = sat + idx * 8;
            int ypos, size, link;
            if (SpriteCacheOn) { int c = idx * 4; ypos = (SpriteCache[c] << 8 | SpriteCache[c + 1]) & SpriteYMask; size = SpriteCache[c + 2]; link = SpriteCache[c + 3] & 0x7F; }
            else { ypos = (Vram[a & 0xFFFF] << 8 | Vram[a + 1 & 0xFFFF]) & SpriteYMask; size = Vram[a + 2 & 0xFFFF]; link = Vram[a + 3 & 0xFFFF] & 0x7F; }
            int h = (size & 3) + 1;
            int top = ypos - (128 << (dbl ? 1 : 0));
            if (imgRow >= top && imgRow < top + (h << cellShift))
            {
                if (lineSpriteCount == maxLine) { lineSpriteOverflow = true; break; }
                lineSprites[lineSpriteCount++] = idx;
            }
            idx = link;
            if (idx == 0 || idx >= (SpriteLinks80 ? 80 : maxSprites)) break;      // a link of 0 ends the list, and so does one past the 80 cached sprites (past 64 in H32 only with SpriteLinks80 off)
        }
        if (lineSpriteOverflow) { sprOverflow = true; SpriteOverflowLines++; }
        DrawSpritesToLine();
    }

    private void DrawSpritesToLine()
    {
        if (sprDirty) { Array.Clear(sprLine, 0, lineWidth + 16); sprDirty = false; }
        if (lineSpriteCount == 0) { prevLineDotLimit = false; return; }
        sprDirty = true;
        int sat = (Reg[5] & (H40 ? 0x7E : 0x7F)) << 9;
        int cellsLeft = H40 ? 40 : 32;
        bool masked = false, seenVisible = prevLineDotLimit && lineY != 0;
        bool limitHit = false;
        for (int i = 0; i < lineSpriteCount && cellsLeft > 0; i++)
        {
            int a = sat + lineSprites[i] * 8;
            int size = SpriteCacheOn ? SpriteCache[lineSprites[i] * 4 + 2] : Vram[a + 2 & 0xFFFF];
            int w = (size >> 2 & 3) + 1, h = (size & 3) + 1;
            int attr = Vram[a + 4 & 0xFFFF] << 8 | Vram[a + 5 & 0xFFFF];
            int xraw = (Vram[a + 6 & 0xFFFF] << 8 | Vram[a + 7 & 0xFFFF]) & 0x1FF;
            int ypos = (SpriteCacheOn ? (SpriteCache[lineSprites[i] * 4] << 8 | SpriteCache[lineSprites[i] * 4 + 1]) & SpriteYMask : (Vram[a & 0xFFFF] << 8 | Vram[a + 1 & 0xFFFF]) & SpriteYMask) - (128 << (dbl ? 1 : 0));
            // a sprite at X=0 hides the sprites after it, but only once a sprite with another X has come before it on the line (or the line before ran out of pixels)
            if (xraw == 0) { if (seenVisible) masked = true; }
            else seenVisible = true;
            int draw = Math.Min(w, cellsLeft);
            cellsLeft -= draw;
            if (draw < w) sprOverflow = true;
            if (cellsLeft <= 0) limitHit = true;
            if (masked) continue;
            int sy = imgRow - ypos;
            bool vflip = (attr & 0x1000) != 0, hflip = (attr & 0x800) != 0;
            if (vflip) sy = (h << cellShift) - 1 - sy;
            int cellRow = sy >> cellShift, row = sy & (1 << cellShift) - 1;
            int tileBase = attr & 0x7FF;
            int flags = attr >> 8 & 0x80 | attr >> 9 & 0x30;
            int sx = xraw - 128;
            for (int k = 0; k < draw; k++)
            {
                int x0 = sx + k * 8;
                if (x0 >= lineWidth) break;
                if (x0 + 8 <= 0) continue;
                int c = hflip ? w - 1 - k : k;
                int tile = tileBase + c * h + cellRow & 0x7FF;
                int ta = (tile << cellShift + 2) + (row << 2);
                uint bits = (uint)(Vram[ta & 0xFFFF] << 24 | Vram[ta + 1 & 0xFFFF] << 16 | Vram[ta + 2 & 0xFFFF] << 8 | Vram[ta + 3 & 0xFFFF]);
                for (int j = 0; j < 8; j++)
                {
                    int x = x0 + j;
                    if ((uint)x >= (uint)lineWidth) continue;
                    int col = hflip ? (int)(bits >> j * 4) & 15 : (int)(bits >> 28 - j * 4) & 15;
                    if (col == 0) continue;
                    if (sprLine[x] != 0) sprCollision = true;
                    else sprLine[x] = (byte)(flags | col);
                }
            }
        }
        prevLineDotLimit = limitHit;
    }

    // =================================================================================================================
    //  Priority and colour state
    // =================================================================================================================

    private void Compose(int x0, int x1, int bd)
    {
        var a = bufA; var b = bufB; var s = sprLine; var o = lineIdx;
        if ((Reg[12] & 8) == 0)
        {
            // priority ladder, top to bottom: sprite, A, B with the priority bit set; then sprite, A, B without it; then the backdrop
            for (int x = x0; x < x1; x++)
            {
                int sv = s[x], av = a[x], bv = b[x], v;
                if ((sv & 15) != 0 && (sv & 0x80) != 0) v = sv;
                else if ((av & 15) != 0 && (av & 0x80) != 0) v = av;
                else if ((bv & 15) != 0 && (bv & 0x80) != 0) v = bv;
                else if ((sv & 15) != 0) v = sv;
                else if ((av & 15) != 0) v = av;
                else if ((bv & 15) != 0) v = bv;
                else v = bd;
                o[x] = (byte)(v & 0x3F);
            }
        }
        else
        {
            // shadow and highlight: a pixel is in shadow unless a plane's tile has the priority bit (even where that tile is transparent: the backdrop under a high-priority tile is not darkened).
            // A sprite pixel takes part in the priority ladder first: it wins over a plane pixel unless that one is visible and has the priority bit and the sprite has not; only a sprite that wins acts:
            // the operators (palette 3, colour 14 highlights, 15 shadows) change the state of what lies under them, an ordinary pixel is drawn normal when its priority bit is set or its colour is 14 or 15
            // and shadowed when the state is shadow (all measured on the oracle with random scenes: sprites over every plane combination)
            bool dupCell = LeftBlankShadowRule >= 3 && (Reg[0] & 0x20) != 0;
            for (int x = x0; x < x1; x++)
            {
                int sv = s[x], av = a[x], bv = b[x], pl;
                if (dupCell && x >= 8 && x < 16) { av = a[x - 8]; bv = b[x - 8]; }
                int state = ((av | bv) & 0x80) != 0 ? 0 : 1;
                bool aHi = (av & 15) != 0 && (av & 0x80) != 0, bHi = (bv & 15) != 0 && (bv & 0x80) != 0;
                if (aHi) pl = av;
                else if (bHi) pl = bv;
                else if ((av & 15) != 0) pl = av;
                else if ((bv & 15) != 0) pl = bv;
                else pl = bd;
                int idx = pl & 0x3F;
                if ((sv & 15) != 0 && ((sv & 0x80) != 0 || !(aHi || bHi) || !ShadowSpriteOverHighPlane))
                {
                    int sp = sv & 0x3F;
                    if (sp == 0x3E) state = state == 1 ? 0 : 2;          // highlight: a shadowed pixel becomes normal, a normal one brighter
                    else if (sp == 0x3F) state = 1;                       // shadow
                    else { idx = sp; state = (sv & 0x80) != 0 || (sv & 15) >= SpriteNormalFromColour && (sv & 15) <= SpriteNormalToColour ? 0 : state; }
                }
                o[x] = (byte)(idx | state << 6);
            }
        }
        // the left column blank bit shows the backdrop in the first eight pixels
        if ((Reg[0] & 0x20) != 0)
        {
            bool sh = (Reg[12] & 8) != 0 && LeftBlankShadowRule > 0;
            for (int x = x0; x < Math.Min(x1, 8); x++) o[x] = (byte)(bd | (sh ? 0x40 : 0));
        }
    }

    /// <summary>Left column blank (R0 bit 5) in shadow/highlight mode, as the oracle draws it (4 random-scene families, ten scenes 900 to 3700 pixels off before, 2 to 35 after): 0 = the old picture (backdrop at
    /// normal brightness, nothing else); 1 = the blank column is the backdrop in shadow; 3 (the default) = also the planes (A, window, B) in the second cell (x 8-15) are the first cell's planes, while the sprites
    /// stay where they are. Without shadow/highlight neither happens. The second part may be an oracle quirk that the hardware does not share; no hardware measurement says either way.</summary>
    public static int LeftBlankShadowRule = 3;

    // =================================================================================================================
    //  Hooks the rest of the VDP calls
    // =================================================================================================================

    /// <summary>A picture line is being drawn (the display may still be switched off).</summary>
    public bool LineIsActive => lineActive;

    private void OnCramWrite(int index, ushort data)
    {
        if (lineActive) ActiveCramWrites++;
        // colour RAM changes: what has been put out keeps the old colours; the pixel being output at the moment of the write shows the written colour (the colour RAM dot)
        SyncRender();
        Cram[index] = (ushort)(data & 0x0EEE);
        UpdatePal(index);
        // also with the display switched off: the picture is then the backdrop and the dots are all there is (the direct colour DMA demo). Before the first picture pixel there is nothing to put a dot on (the
        // colour shows from pixel 0 on through the table); a second write in the same pixel replaces the first one's dot
        if (lineActive && Time - lineStart >= PixelOrigin)
        {
            int px = PixelNow();
            if (px < lineWidth && px >= colourX - 1)
            {
                building[rowBase + px] = Argb(data, 0);
                colourX = px + 1;
            }
        }
    }

    /// <summary>Tools: set colour RAM entry <paramref name="index"/> directly (no dot, nothing drawn).</summary>
    public void LoadColour(int index, ushort data) { Cram[index & 0x3F] = (ushort)(data & 0x0EEE); UpdatePal(index & 0x3F); }

    /// <summary>Tools: after the memories and registers were set directly, begin the current line afresh.</summary>
    public void ResetPictureState() { for (int i = 0; i < 64; i++) UpdatePal(i); RebuildSpriteCache(); BeginLineRender(); }

    /// <summary>The debug picture of the picture itself (what the session shows), for tools: a copy of the finished frame at its real size.</summary>
    public void CopyPicture(uint[] dst)
    {
        int w = PictureWidth, h = PictureHeight;
        for (int y = 0; y < h; y++) Array.Copy(picture, y * PictureStride, dst, y * w, w);
    }

    /// <summary>The double-height picture of interlace mode 2 (see <see cref="WovenPicture"/>) as a compact <see cref="PictureWidth"/> x <see cref="WovenHeight"/> image.</summary>
    public void CopyWovenPicture(uint[] dst)
    {
        int w = PictureWidth, h = WovenHeight;
        for (int y = 0; y < h; y++) Array.Copy(woven, y * PictureStride, dst, y * w, w);
    }
}
