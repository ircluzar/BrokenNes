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
    private int hscrollA, hscrollB;
    private bool sprOverflow, sprCollision;
    private bool sprDirty;

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

    /// <summary>The pixel being output at the VDP's clock (0 when the line has not begun its picture, <see cref="lineWidth"/> or more when the active part is over).</summary>
    private int PixelNow()
    {
        long off = Time - lineStart;
        if (off <= 0) return 0;
        long p = off / linePxDiv;
        return p > lineWidth ? lineWidth : (int)p;
    }

    /// <summary>Draw what the hardware has put out up to now, so that a change made now only shows from here on.</summary>
    private void SyncRender()
    {
        if (!lineActive) return;
        int px = PixelNow();
        RenderTo(px + RenderLead);
        ConvertTo(px);
    }

    private void BeginLineRender()
    {
        lineActive = Line < ActiveLines && Line < PictureMaxHeight;
        if (!lineActive) return;
        lineY = Line;
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

    private void FinishLineRender()
    {
        if (!lineActive) return;
        RenderTo(lineWidth);
        ConvertTo(lineWidth);
        lineActive = false;
        if (Line + 1 >= ActiveLines || Line + 1 >= PictureMaxHeight)
        {
            // the last active line: the frame is finished, hand it over
            (picture, building) = (building, picture);
            PictureWidth = lineWidth; PictureHeight = Math.Min(ActiveLines, PictureMaxHeight);
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
        int vpos = (Reg[18] & 0x1F) * 8;
        bool vwin = (Reg[18] & 0x80) != 0 ? lineY >= vpos : lineY < vpos;
        bool leftWindow = false;
        if (vwin) { wx0 = 0; wx1 = lineWidth; }
        else
        {
            int hpos = (Reg[17] & 0x1F) * 16;
            if ((Reg[17] & 0x80) != 0) { wx0 = Math.Min(hpos, lineWidth); wx1 = lineWidth; }
            else { wx0 = 0; wx1 = Math.Min(hpos, lineWidth); leftWindow = wx1 > 0 && wx1 < lineWidth; }
        }
        // the window bug (MacDonald): with the window at the left and the plane's fine scroll not a multiple of 16, the two cells after it take their name table data from two cells further on
        int bugX0 = -1, bugX1 = -1;
        if (leftWindow && (hscrollA & 0xF) != 0) { bugX0 = wx1; bugX1 = wx1 + 16; }
        int a = x0, b = Math.Min(x1, wx0);
        if (b > a) DrawScrollRun(dst, true, a, b, bugX0, bugX1);
        a = Math.Max(x0, wx0); b = Math.Min(x1, wx1);
        if (b > a) DrawWindowRun(dst, a, b);
        a = Math.Max(x0, wx1); b = x1;
        if (b > a) DrawScrollRun(dst, true, a, b, bugX0, bugX1);
    }

    private void DrawWindowRun(byte[] dst, int xa, int xb)
    {
        int nt = (Reg[3] & (H40 ? 0x3C : 0x3E)) << 10;
        int cells = H40 ? 64 : 32;
        int row = lineY >> 3, r = lineY & 7;
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

    private void DrawScrollRun(byte[] dst, bool isA, int xa, int xb, int bugX0, int bugX1)
    {
        int nt = isA ? (Reg[2] & 0x38) << 10 : (Reg[4] & 7) << 13;
        int ws = Reg[16] & 3, hs2 = Reg[16] >> 4 & 3;
        int wCells = ws == 1 ? 64 : ws == 3 ? 128 : 32;
        int hCells = hs2 == 1 ? 64 : hs2 == 3 ? 128 : 32;
        int wMask = wCells * 8 - 1, hMask = hCells * 8 - 1;
        int hs = isA ? hscrollA : hscrollB;
        bool colV = (Reg[11] & 4) != 0;
        int vsOff = isA ? 0 : 1;
        int vsFull = Vsram[vsOff] & 0x3FF;
        int x = xa;
        while (x < xb)
        {
            int vs, limit = xb;
            if (colV) { int col = x >> 4; vs = Vsram[(col << 1) + vsOff & 0x3F] & 0x3FF; limit = Math.Min(xb, (col + 1) << 4); }
            else vs = vsFull;
            int py = lineY + vs & hMask;
            int px = x - hs & wMask;
            int cell = px >> 3, fine = px & 7;
            int len = Math.Min(8 - fine, limit - x);
            if (x >= bugX0 && x < bugX1) cell = cell + 2 & (wCells - 1);
            int ea = nt + ((py >> 3) * wCells + cell) * 2;
            int e = Vram[ea & 0xFFFF] << 8 | Vram[ea + 1 & 0xFFFF];
            DrawTileRun(dst, x, len, e, py & 7, fine);
            x += len;
        }
    }

    /// <summary>Put <paramref name="len"/> pixels of one tile row on the line at <paramref name="x"/>, starting at pixel <paramref name="fine"/> of the tile row; <paramref name="e"/> is the name table entry.</summary>
    private void DrawTileRun(byte[] dst, int x, int len, int e, int row, int fine)
    {
        int a = ((e & 0x7FF) << 5) + (((e & 0x1000) != 0 ? 7 - row : row) << 2);
        uint bits = (uint)(Vram[a & 0xFFFF] << 24 | Vram[a + 1 & 0xFFFF] << 16 | Vram[a + 2 & 0xFFFF] << 8 | Vram[a + 3 & 0xFFFF]);
        int flags = e >> 8 & 0x80 | e >> 9 & 0x30;
        if ((e & 0x800) != 0)
        {
            for (int k = 0; k < len; k++)
            {
                int c = (int)(bits >> (fine + k) * 4) & 15;
                dst[x + k] = c == 0 ? (byte)0 : (byte)(flags | c);
            }
        }
        else
        {
            for (int k = 0; k < len; k++)
            {
                int c = (int)(bits >> 28 - (fine + k) * 4) & 15;
                dst[x + k] = c == 0 ? (byte)0 : (byte)(flags | c);
            }
        }
    }

    // =================================================================================================================
    //  Sprites
    // =================================================================================================================

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
            int ypos = (Vram[a & 0xFFFF] << 8 | Vram[a + 1 & 0xFFFF]) & 0x3FF;
            int size = Vram[a + 2 & 0xFFFF], link = Vram[a + 3 & 0xFFFF] & 0x7F;
            int h = (size & 3) + 1;
            int top = ypos - 128;
            if (lineY >= top && lineY < top + h * 8)
            {
                if (lineSpriteCount == maxLine) { lineSpriteOverflow = true; break; }
                lineSprites[lineSpriteCount++] = idx;
            }
            idx = link;
            if (idx == 0) break;
        }
        if (lineSpriteOverflow) sprOverflow = true;
        DrawSpritesToLine();
    }

    private void DrawSpritesToLine()
    {
        if (sprDirty) { Array.Clear(sprLine, 0, lineWidth + 16); sprDirty = false; }
        if (lineSpriteCount == 0) return;
        sprDirty = true;
        int sat = (Reg[5] & (H40 ? 0x7E : 0x7F)) << 9;
        int cellsLeft = H40 ? 40 : 32;
        bool masked = false;
        for (int i = 0; i < lineSpriteCount && cellsLeft > 0; i++)
        {
            int a = sat + lineSprites[i] * 8;
            int size = Vram[a + 2 & 0xFFFF];
            int w = (size >> 2 & 3) + 1, h = (size & 3) + 1;
            int attr = Vram[a + 4 & 0xFFFF] << 8 | Vram[a + 5 & 0xFFFF];
            int xraw = (Vram[a + 6 & 0xFFFF] << 8 | Vram[a + 7 & 0xFFFF]) & 0x1FF;
            int ypos = ((Vram[a & 0xFFFF] << 8 | Vram[a + 1 & 0xFFFF]) & 0x3FF) - 128;
            if (xraw == 0 && i > 0) masked = true;
            int draw = Math.Min(w, cellsLeft);
            cellsLeft -= draw;
            if (draw < w) sprOverflow = true;
            if (masked) continue;
            int sy = lineY - ypos;
            bool vflip = (attr & 0x1000) != 0, hflip = (attr & 0x800) != 0;
            if (vflip) sy = h * 8 - 1 - sy;
            int cellRow = sy >> 3, row = sy & 7;
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
                int ta = (tile << 5) + (row << 2);
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
                if (sv != 0 && (sv & 0x80) != 0) v = sv;
                else if (av != 0 && (av & 0x80) != 0) v = av;
                else if (bv != 0 && (bv & 0x80) != 0) v = bv;
                else if (sv != 0) v = sv;
                else if (av != 0) v = av;
                else if (bv != 0) v = bv;
                else v = bd;
                o[x] = (byte)(v & 0x3F);
            }
        }
        else
        {
            for (int x = x0; x < x1; x++)
            {
                int sv = s[x], av = a[x], bv = b[x], pl, state;
                if (av != 0 && (av & 0x80) != 0) { pl = av; state = 0; }
                else if (bv != 0 && (bv & 0x80) != 0) { pl = bv; state = 0; }
                else if (av != 0) { pl = av; state = 1; }
                else if (bv != 0) { pl = bv; state = 1; }
                else { pl = bd; state = 1; }
                int idx = pl & 0x3F;
                if (sv != 0)
                {
                    int sp = sv & 0x3F;
                    if (sp == 0x3E) state = state == 1 ? 0 : 2;          // highlight: a shadowed pixel becomes normal, a normal one brighter
                    else if (sp == 0x3F) state = 1;                       // shadow
                    else if ((sv & 0x80) != 0 || state == 1) { idx = sp; state = (sv & 0x80) != 0 ? 0 : 1; }
                }
                o[x] = (byte)(idx | state << 6);
            }
        }
        // the left column blank bit shows the backdrop in the first eight pixels
        if ((Reg[0] & 0x20) != 0)
            for (int x = x0; x < Math.Min(x1, 8); x++) o[x] = (byte)bd;
    }

    // =================================================================================================================
    //  Hooks the rest of the VDP calls
    // =================================================================================================================

    private void OnCramWrite(int index, ushort data)
    {
        // colour RAM changes: what has been put out keeps the old colours; the pixel being output at the moment of the write shows the written colour (the colour RAM dot)
        SyncRender();
        Cram[index] = (ushort)(data & 0x0EEE);
        UpdatePal(index);
        if (lineActive && DisplayEnabled)
        {
            int px = PixelNow();
            if (px < lineWidth && px >= colourX)
            {
                building[rowBase + px] = Argb(data, 0);
                colourX = px + 1;
            }
        }
    }

    /// <summary>Tools: set colour RAM entry <paramref name="index"/> directly (no dot, nothing drawn).</summary>
    public void LoadColour(int index, ushort data) { Cram[index & 0x3F] = (ushort)(data & 0x0EEE); UpdatePal(index & 0x3F); }

    /// <summary>Tools: after the memories and registers were set directly, begin the current line afresh.</summary>
    public void ResetPictureState() { for (int i = 0; i < 64; i++) UpdatePal(i); BeginLineRender(); }

    /// <summary>The debug picture of the picture itself (what the session shows), for tools: a copy of the finished frame at its real size.</summary>
    public void CopyPicture(uint[] dst)
    {
        int w = PictureWidth, h = PictureHeight;
        for (int y = 0; y < h; y++) Array.Copy(picture, y * PictureStride, dst, y * w, w);
    }
}
