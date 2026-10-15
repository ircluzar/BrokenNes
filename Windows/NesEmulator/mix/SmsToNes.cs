using System;
using System.Collections.Generic;
using System.Linq;
using NesEmulator.Sega;

namespace NesEmulator.Mix;

/// <summary>The lines of a Master System / Game Gear frame as the chip drew them: the registers that change from line to line (scroll, display switch, backdrop), noted as each line begins.</summary>
/// <remarks>
/// Taken by polling the chip's line number at every Z80 instruction boundary (the board's own <see cref="BOARD_SMS.InstructionHook"/>, chained), so no change to the board or the chip is needed: the register
/// file read at the first instruction of a line is what the line was drawn with, to within the instruction (a write lands about 31 pixels after the Z80 makes it, so one made at the end of the line before
/// is already in; one made in the first instruction of the line is attributed to the line before, which is also how the chip latches the horizontal scroll).
/// </remarks>
internal sealed class SmsLineCapture
{
    public const int Lines = 240;
    public readonly byte[] R0 = new byte[Lines], R1 = new byte[Lines], R7 = new byte[Lines], R8 = new byte[Lines];
    public readonly uint[] Backdrop = new uint[Lines];
    public readonly ulong[] PaletteHash = new ulong[Lines];
    public readonly bool[] Seen = new bool[Lines];
    /// <summary>The vertical scroll the frame latched (register 9 at its first line).</summary>
    public byte R9;
    private readonly SmsVdp vdp;
    private int last = -1;

    public SmsLineCapture(BOARD_SMS board)
    {
        vdp = board.Vdp;
        var chained = board.InstructionHook;
        board.InstructionHook = pc => { chained?.Invoke(pc); Poll(); };
    }

    private void Poll()
    {
        int ln = vdp.Line;
        if (ln == last) return;
        last = ln;
        if ((uint)ln >= Lines) return;
        var r = vdp.Regs;
        R0[ln] = r[0]; R1[ln] = r[1]; R7[ln] = r[7]; R8[ln] = r[8];
        if (ln == 0) R9 = r[9];
        var pal = vdp.Palette;
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < pal.Length; i++) h = (h ^ pal[i]) * 1099511628211UL;
        PaletteHash[ln] = h;
        Backdrop[ln] = pal[16 + (r[7] & 15)];
        Seen[ln] = true;
    }

    /// <summary>Fill in a line the poll never saw (no instruction boundary fell in it): it was drawn as the line before.</summary>
    public void Complete(int lines)
    {
        for (int l = 0; l < Math.Min(lines, Lines); l++)
        {
            if (Seen[l]) continue;
            int p = Math.Max(0, l - 1);
            R0[l] = R0[p]; R1[l] = R1[p]; R7[l] = R7[p]; R8[l] = R8[p]; Backdrop[l] = Backdrop[p]; PaletteHash[l] = PaletteHash[p];
        }
    }
}

/// <summary>Which part of a Sega picture the NES picture shows, and where on the NES's 256 x 240 it sits: source rectangle (X0, Y0, W, H) drawn with its top left at (OffsetX, OffsetY).</summary>
internal readonly record struct PictureWindow(int X0, int Y0, int W, int H, int OffsetX, int OffsetY)
{
    /// <summary>Source x of the pixel drawn at canvas x.</summary>
    public int Dx => X0 - OffsetX;
    public static PictureWindow Centered(int x0, int y0, int w, int h) => new(x0, y0, w, h, (256 - w) / 2, (240 - h) / 2);
}

/// <summary>
/// MIX LAB: the Master System / Game Gear -> NES picture bridge. After each frame, what the VDP was programmed to show is turned into what a NES picture chip can draw:
/// <list type="bullet">
/// <item><b>Layered path</b> (PPU_FIXS, and the Game Boy bridges fronted by it): the background becomes an extension layer with the chip's own per-line horizontal scroll, vertical scroll, scroll locks and left-column
/// mask; its 4-bit tiles are reduced to 2-bit ones (3 colours + see-through) grouped into 8 palettes by the colours each tile really uses; the 64 sprites become pieces in the chip's own order and under the chip's own
/// rules (8 a line, the 0xD0 terminator, zoom, tall sprites, the 8-pixel shift), drawn in front of or behind the background tiles that have the priority bit. Backdrop colour is per line.</item>
/// <item><b>Flat path</b> (every other NES chip): one background (256 tiles, 4 palettes of 3 colours per 16 x 16 block, the NES's single fine scroll), sprites as 8x8 NES sprites (64, the chip enforces 8 a line);
/// everything the NES cannot hold is counted.</item>
/// </list>
/// The 16-colour palettes are reduced by <see cref="SnesToNes.Quantizer"/> (the same grouping the SNES bridge uses): tiles are grouped by the colours they use, each group gets the 3 colours that fit its pixels best, and the
/// colours are then matched to the NES chips' palette (as the FIX family draws it). The Master System's own picture is untouched by all of this: the board keeps running and is the reference.
/// </summary>
internal sealed class SmsToNes : IVariantLevels
{
    private readonly NesPictureSink sink;
    public readonly BridgeCounters Counters = new();
    private readonly bool gg;
    public string NesPpuId => sink.PpuId;
    /// <summary>The window of the last frame (for the reference picture).</summary>
    public PictureWindow Window { get; private set; }

    // palette solvers that keep their last solution as the next frame's start (steady colours)
    private readonly SegaQuantizer layeredBgQ = new(), layeredObjQ = new(), flatBgQ = new(), flatObjQ = new();

    // The working memory of a frame, made once and cleared for each frame: a bridge that makes its tables anew allocates a few hundred KB a frame
    // (`--picture-bridge-test --only alloc` measures what is left).
    private readonly NesPicture scratch = new();
    private readonly Dictionary<ulong, int> canonFirst = new(512);
    private readonly int[] pal0 = new int[16], pal1 = new int[16];
    private readonly int[][] bgPalette;
    private readonly List<Piece> pieceBuf = new(), itemPiece = new();
    private readonly bool[] seen0 = new bool[32 * 32], seen1 = new bool[32 * 32];
    private readonly int[] fsLine = new int[ExtPicture.Lines], perLineLayered = new int[ExtPicture.Lines], perLineFlat = new int[240];
    private readonly Dictionary<int, int> cellItem = new(), itemOf = new(), variantUses = new();
    private readonly Dictionary<long, int> pieceItem = new();
    private readonly Dictionary<int, int>[] tileOf = { new(), new() };
    private readonly int[] nextTile = new int[2], fineXVotes = new int[8];
    private readonly List<(int layer, int cell, int variant, int item, Cell c)> variants = new();
    private readonly List<(Piece p, int item)> kept = new();
    private readonly Cell?[,] cellGrid = new Cell?[30, 33];
    private readonly Dictionary<int, Cell> variantCell = new();
    private readonly int[,,] votes = new int[15, 17, 4];
    private readonly FlatTilePacker packer = new();
    // what FillLevels needs from the frame being drawn (the packer asks for the pixels of a variant while the flat path runs)
    private SegaQuantizer? levelsQ; private byte[]? vramNow; private int bdNow;

    public SmsToNes(string nesPpuId, bool gameGear) { sink = new NesPictureSink(nesPpuId); gg = gameGear; bgPalette = new[] { pal0, pal1 }; }

    public bool Layered => (sink.Ppu as PPU_FIXS ?? (sink.Ppu as PPU_DMG)?.LayerFront) != null;

    private static int Rgb(uint argb) => (int)(argb & 0xFFFFFF);

    /// <summary>The window of the frame: the Master System's whole picture (at most 224 lines, the middle of a 240-line one), or the Game Gear's 160 x 144 LCD.</summary>
    public PictureWindow WindowFor(SmsVdp vdp)
    {
        int h = vdp.ActiveHeight;
        if (gg) return PictureWindow.Centered(48, Math.Max(0, (h - 144) / 2), 160, 144);
        int vis = Math.Min(h, 224);
        return PictureWindow.Centered(0, (h - vis) / 2, 256, vis);
    }

    /// <summary>Draw the VDP's state as a NES picture; RGBA 256 x 240.</summary>
    public byte[] Render(SmsVdp vdp, SmsLineCapture cap)
    {
        Counters.BeginFrame();
        var win = Window = WindowFor(vdp);
        cap.Complete(vdp.ActiveHeight);
        BuildCanon(vdp.Vram);
        bool mode4 = vdp.Revision != SmsVdpRevision.Tms9918 && (vdp.Regs[0] & 4) != 0;
        var layered = sink.Ppu as PPU_FIXS ?? (sink.Ppu as PPU_DMG)?.LayerFront;
        if (!mode4)
        {
            // the TMS9918 modes of the SG-1000 are not translated: say so and show the backdrop, nothing else
            Counters.Add("frames in a TMS9918 graphics mode (not translated)");
            if (layered != null) { var e0 = layered.Ext ??= new ExtPicture(); e0.BeginFrame(); layered.ExtLineOffset = 8; }
            scratch.ClearToBlank();
            return sink.Present(scratch);
        }
        for (int l = 0; l < win.H; l++) if (cap.PaletteHash[win.Y0 + l] != cap.PaletteHash[Math.Min(vdp.ActiveHeight, SmsLineCapture.Lines) - 1]) { Counters.Add("lines drawn with a palette other than the frame's"); }
        return layered != null ? RenderLayered(vdp, cap, win, layered) : RenderFlat(vdp, cap, win);
    }

    // ----------------------------------------------------------------------------------------------------------------------- shared reading of the chip's memory
    private readonly record struct Cell(int Tile, bool HFlip, bool VFlip, bool Pal2, bool Prio);

    // Tiles are told apart by what is in them: every tile number is replaced by the lowest number with the same pixels (a game that clears with several different blank tiles shows one).
    private readonly int[] canon = new int[512];
    private void BuildCanon(byte[] vram)
    {
        var first = canonFirst; first.Clear();
        for (int t = 0; t < 512; t++)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < 32; i++) h = (h ^ vram[(t * 32 + i) & 0x3FFF]) * 1099511628211UL;
            canon[t] = first.TryGetValue(h, out int f) ? f : (first[h] = t);
        }
    }

    private Cell ReadCell(SmsVdp vdp, int ntBase, int row, int col)
    {
        int nt = ntBase + row * 64 + col * 2;
        if (vdp.Revision == SmsVdpRevision.Sms1 && (vdp.Regs[2] & 1) == 0) nt &= ~0x400;
        nt &= 0x3FFF;
        var v = vdp.Vram;
        int entry = v[nt] | v[(nt + 1) & 0x3FFF] << 8;
        return new Cell(canon[entry & 0x1FF], (entry & 0x200) != 0, (entry & 0x400) != 0, (entry & 0x800) != 0, (entry & 0x1000) != 0);
    }

    private static int NameTableBase(SmsVdp vdp) => vdp.ActiveHeight != 192 ? ((vdp.Regs[2] & 0x0C) << 10) | 0x700 : (vdp.Regs[2] & 0x0E) << 10;

    /// <summary>Colour index (0-15) of pixel (x, y) of the 4-bit tile at VRAM address <paramref name="addr"/>.</summary>
    private static int Pixel(byte[] vram, int addr, int y, int x)
    {
        int a = (addr + y * 4) & 0x3FFF, bit = 7 - x;
        return (vram[a] >> bit & 1) | (vram[(a + 1) & 0x3FFF] >> bit & 1) << 1 | (vram[(a + 2) & 0x3FFF] >> bit & 1) << 2 | (vram[(a + 3) & 0x3FFF] >> bit & 1) << 3;
    }

    /// <summary>The 16 colours of a palette (0 background, 1 sprite) as RGB, written into <paramref name="c"/>.</summary>
    private static void Colours(SmsVdp vdp, int bank, int[] c)
    {
        var pal = vdp.Palette;
        for (int i = 0; i < 16; i++) c[i] = Rgb(pal[bank * 16 + i]);
    }

    /// <summary>Both palettes of the frame (<c>bgPalette</c>[0] the background's, [1] the sprites' - the sprites read the same one).</summary>
    private int[][] FramePalettes(SmsVdp vdp) { Colours(vdp, 0, pal0); Colours(vdp, 1, pal1); return bgPalette; }

    /// <summary>One sprite piece (an 8x8 tile of a sprite) on the chip's picture.</summary>
    private readonly record struct Piece(int X, int Y, int Tile, int Quadrant, bool Zoom);

    /// <summary>The chip's sprite list as it draws it: the 0xD0 terminator, the 8-pixel shift, tall and zoomed sprites, positions that wrap to the top. Pieces are in the chip's own priority order.</summary>
    private List<Piece> ReadSprites(SmsVdp vdp, out int sprites, out bool zoom)
    {
        var v = vdp.Vram; var r = vdp.Regs; var list = pieceBuf; list.Clear();
        int satBase = (r[5] & 0x7E) << 7;
        int xnBase = vdp.Revision == SmsVdpRevision.Sms1 && (r[5] & 1) == 0 ? satBase : satBase + 0x80;
        int shift = (r[0] & 0x08) != 0 ? 8 : 0, tileHi = (r[6] & 0x04) != 0 ? 256 : 0;
        bool tall = (r[1] & 2) != 0; zoom = (r[1] & 1) != 0;
        bool terminator = vdp.ActiveHeight == 192;
        sprites = 0;
        for (int i = 0; i < 64; i++)
        {
            int y = v[(satBase + i) & 0x3FFF];
            if (y == 0xD0 && terminator) break;
            int x = v[(xnBase + 2 * i) & 0x3FFF] - shift, tn = v[(xnBase + 2 * i + 1) & 0x3FFF];
            int top = (y + 1) & 0xFF;
            sprites++;
            int rows = tall ? 2 : 1;
            for (int pr = 0; pr < rows; pr++)
            {
                int tile = canon[(tall ? ((tn & 0xFE) | tileHi) + pr : tn | tileHi) & 0x1FF];
                if (!zoom)
                {
                    int py = top + 8 * pr; if (py >= 256) py -= 256;
                    list.Add(new Piece(x, py, tile, 0, false));
                    if (top + 8 * pr + 8 > 256 && top + 8 * pr < 256) { /* the part past the bottom of the 256-line counter wraps to the top: a second copy at y - 256 */ list.Add(new Piece(x, top + 8 * pr - 256, tile, 0, false)); }
                }
                else
                    for (int q = 0; q < 4; q++)
                    {
                        int py = top + 16 * pr + 8 * (q >> 1); if (py >= 256) py -= 256;
                        list.Add(new Piece(x + 8 * (q & 1), py, tile, q, true));
                    }
            }
        }
        return list;
    }

    /// <summary>Level 0 (see-through) or colour index of a sprite piece pixel, after zoom.</summary>
    private static int PiecePixel(byte[] vram, Piece p, int y, int x)
    {
        int addr = p.Tile * 32;
        if (!p.Zoom) return Pixel(vram, addr, y, x);
        // a zoomed tile shows each pixel twice, each row twice: quadrant q of the 16x16 picture
        int fy = (p.Quadrant >> 1) * 8 + y, fx = (p.Quadrant & 1) * 8 + x;
        return Pixel(vram, addr, fy >> 1, fx >> 1);
    }

    // ======================================================================================================================= the layered path
    private byte[] RenderLayered(SmsVdp vdp, SmsLineCapture cap, PictureWindow win, PPU_FIXS fx)
    {
        var e = fx.Ext ??= new ExtPicture();
        e.BeginFrame();
        fx.ExtLineOffset = win.OffsetY;
        var vram = vdp.Vram;
        int H = vdp.ActiveHeight; bool ext = H != 192;
        int dx = win.Dx, h = win.H;
        bool zoomAny;
        var pieces = ReadSprites(vdp, out int spriteCount, out zoomAny);
        e.SpriteLimit = 1024; e.SpritesPerLine = zoomAny ? 16 : 8;
        int ntBase = NameTableBase(vdp);
        int bdFrame = Rgb(cap.Backdrop[Math.Min(H, SmsLineCapture.Lines) - 1]);
        var bgPalette = FramePalettes(vdp);
        var objPalette = pal1;

        // ---- per line: backdrop, which layers and sprites are on, scroll, masks
        var L0 = e.Layers[0]; var L1 = e.Layers[1];
        L0.On.AsSpan().Clear(); L1.On.AsSpan().Clear();
        bool vlockAny = false, anyBg = false, anyObj = false;
        Array.Clear(seen0); Array.Clear(seen1); Array.Clear(fsLine);
        for (int el = 0; el < h; el++)
        {
            int l = win.Y0 + el;
            int bd = Rgb(cap.Backdrop[l]);
            e.BackdropRgb[el] = e.BackdropAltRgb[el] = (uint)NesFixPalette.ForTextbook(bd);
            bool on = (cap.R1[l] & 0x40) != 0;
            if (!on) continue;
            anyBg = true;
            int r0 = cap.R0[l];
            bool hLock = (r0 & 0x40) != 0 && l < 16, vLock = (r0 & 0x80) != 0;
            int hs = hLock ? 0 : cap.R8[l], vs = cap.R9;
            int my = ext ? (l + vs) & 0xFF : (l + vs) % 224;
            L0.On[el] = true; L0.ScrollX[el] = (short)(dx - hs); L0.ScrollY[el] = (short)(my - el);
            L0.ZLow[el] = 1; L0.ZHigh[el] = 5;
            for (int c = 0; c < 32; c++) seen0[(my >> 3) * 32 + c] = true;
            fsLine[el] = hs & 7;
            if (vLock)
            {
                // the lock applies to the scrolled tile columns 24-31 of the line, which start 8 x 24 pixels after the fine scroll's shift
                vlockAny = true;
                int my1 = ext ? l & 0xFF : l % 224;
                L1.On[el] = true; L1.ScrollX[el] = (short)(dx - hs); L1.ScrollY[el] = (short)(my1 - el);
                L1.ZLow[el] = 1; L1.ZHigh[el] = 5;
                for (int sx = 192 + (hs & 7); sx < 256; sx += 8)
                {
                    seen1[(my1 >> 3) * 32 + (((sx - hs) & 255) >> 3)] = true;
                    seen1[(my1 >> 3) * 32 + (((sx + 7 - hs) & 255) >> 3)] = true;
                }
            }
            e.ObjOn[el] = true; anyObj = true;
            for (int p = 0; p < 4; p++) e.ObjZ[el * 4 + p] = 3;
        }
        // masks: outside the window (and the left-column mask, and the vertical-lock split) hide a layer's pixels
        HideLayer(L0, false, cap, win, h, dx);
        if (vlockAny) HideLayer(L1, true, cap, win, h, dx);
        for (int el = 0; el < h; el++)
            for (int x = 0; x < 256; x++)
            {
                int sx = x + dx;
                bool hd = HiddenCol(win, sx) || ((cap.R0[win.Y0 + el] & 0x20) != 0 && sx < 8);
                e.ObjHidden[el * 256 + x] = hd;
                if (hd) e.AnyObjHidden = true;
            }
        // outside the window (the Game Gear's bezel, the lines above and below): black
        for (int el = 0; el < h; el++)
            for (int x = 0; x < 256; x++) if (HiddenCol(win, x + dx)) { e.Black[el * 256 + x] = true; e.AnyBlack = true; }

        if (!anyBg) { return Finish(e, 0, 0, 0); }

        // ---- background tiles: one item per (tile, palette), 3 colours each, 8 palettes of them
        var bgQ = layeredBgQ; bgQ.BeginFrame();
        cellItem.Clear(); variants.Clear();
        AddCells(vdp, seen0, 0, 0, 32, ntBase, bdFrame, bgQ);
        if (vlockAny) AddCells(vdp, seen1, 1, 0, 32, ntBase, bdFrame, bgQ);
        bgQ.Solve(ExtPicture.Palettes);
        for (int g = 0; g < ExtPicture.Palettes; g++) for (int lv = 1; lv < 4; lv++) e.BgRgb[g * 4 + lv] = (uint)NesFixPalette.ForTextbook(bgQ.Palette[g][lv]);
        tileOf[0].Clear(); tileOf[1].Clear(); nextTile[0] = 1; nextTile[1] = 1;
        int overflow = 0;
        e.LayerCount = vlockAny ? 2 : 1;
        for (int k = 0; k < e.LayerCount; k++) { var X = e.Layers[k]; X.Enabled = true; X.MapWidth = 32; X.MapHeight = 32; X.SnesLayer = 0; }
        foreach (var (layer, cellIx, variant, item, cell) in variants)
        {
            var X = e.Layers[layer];
            if (!tileOf[layer].TryGetValue(variant, out int t))
            {
                if (nextTile[layer] >= ExtPicture.MaxTiles) { overflow++; t = 1 + cell.Tile % (ExtPicture.MaxTiles - 1); }
                else
                {
                    t = nextTile[layer]++; tileOf[layer][variant] = t;
                    var pal = bgPalette[cell.Pal2 ? 1 : 0];
                    for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
                    {
                        int ci = Pixel(vram, cell.Tile * 32, cell.VFlip ? 7 - y : y, cell.HFlip ? 7 - x : x);
                        X.Tiles[t * 64 + y * 8 + x] = ci == 0 && pal[0] == bdFrame ? (byte)0 : bgQ.Level(item, pal[ci]);
                    }
                }
            }
            X.Map[cellIx] = (ushort)t; X.Attr[cellIx] = (byte)(bgQ.Group[item] | (cell.Prio ? 8 : 0));
        }

        // ---- sprites: the chip's own list, each piece a tile of its own palette group
        var objQ = layeredObjQ; objQ.BeginFrame();
        pieceItem.Clear(); itemPiece.Clear(); kept.Clear();
        int wanted = 0;
        foreach (var p in pieces)
        {
            // pieces wholly outside the window or off the screen cost nothing
            int ey = p.Y - win.Y0, ex = p.X - dx;
            if (ey + 8 <= 0 || ey >= h || ex + 8 <= 0 || ex >= 256) continue;
            wanted++;
            long key = (long)p.Tile << 4 | (p.Zoom ? (long)(p.Quadrant + 1) : 0);
            if (!pieceItem.TryGetValue(key, out int item))
            {
                item = objQ.Add(key, out var hist);
                if (hist != null)
                    for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
                    {
                        int ci = PiecePixel(vram, p, y, x);
                        if (ci == 0) continue;
                        int rgb = objPalette[ci]; hist[rgb] = hist.GetValueOrDefault(rgb) + 1;
                    }
                pieceItem[key] = item; itemPiece.Add(p);
            }
            kept.Add((p, item));
        }
        objQ.Solve(ExtPicture.Palettes);
        for (int g = 0; g < ExtPicture.Palettes; g++) for (int lv = 1; lv < 4; lv++) e.ObjRgb[g * 4 + lv] = (uint)NesFixPalette.ForTextbook(objQ.Palette[g][lv]);
        int objTilesOver = 0;
        for (int item = 0; item < itemPiece.Count; item++)
        {
            if (item >= ExtPicture.MaxSpriteTiles) { objTilesOver++; continue; }
            var pp = itemPiece[item];
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
            {
                int ci = PiecePixel(vram, pp, y, x);
                e.SpriteTiles[item * 64 + y * 8 + x] = ci == 0 ? (byte)0 : objQ.Level(item, objPalette[ci]);
            }
        }
        if (e.Sprites.Length < kept.Count) e.Sprites = new ExtPicture.Sprite[kept.Count];
        int n = 0;
        foreach (var (p, item) in kept)
            e.Sprites[n++] = new ExtPicture.Sprite
            {
                X = (short)(p.X - dx), Y = (short)(p.Y - win.Y0), FirstLine = 0, LastLine = (short)(h - 1),
                Tile = (ushort)(item % ExtPicture.MaxSpriteTiles), Palette = (byte)objQ.Group[item], Priority = 1,
            };
        e.SpriteCount = n;
        e.PaletteVersion++;
        // sprites the chip itself drops (more than 8 pieces on a line): counted from the list, not from the compositor (which runs twice a frame)
        var perLine = perLineLayered; Array.Clear(perLine); int excess = 0;
        foreach (var (p, _) in kept) for (int ln = Math.Max(0, p.Y - win.Y0); ln < Math.Min(h, p.Y - win.Y0 + 8); ln++) perLine[ln]++;
        for (int ln = 0; ln < h; ln++) if (perLine[ln] > e.SpritesPerLine) excess += perLine[ln] - e.SpritesPerLine;
        return Finish(e, overflow, objTilesOver, excess);
    }

    /// <summary>The layered path's background cells that were seen: the tile each shows (one item per tile and palette line), and the variant (tile, palette line, flips) the layer's map will point at.</summary>
    private void AddCells(SmsVdp vdp, bool[] seen, int layer, int col0, int col1, int ntBase, int bdFrame, SegaQuantizer bgQ)
    {
        var vram = vdp.Vram;
        for (int row = 0; row < 32; row++)
            for (int col = col0; col < col1; col++)
            {
                if (!seen[row * 32 + col]) continue;
                var cell = ReadCell(vdp, ntBase, row, col);
                int key = cell.Tile << 1 | (cell.Pal2 ? 1 : 0);
                if (!cellItem.TryGetValue(key, out int item))
                {
                    var pal = bgPalette[cell.Pal2 ? 1 : 0];
                    item = bgQ.Add(key, out var hist);
                    if (hist != null)
                        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
                        {
                            int ci = Pixel(vram, cell.Tile * 32, y, x);
                            if (ci == 0 && pal[0] == bdFrame) continue;
                            int rgb = pal[ci]; hist[rgb] = hist.GetValueOrDefault(rgb) + 1;
                        }
                    cellItem[key] = item;
                }
                variants.Add((layer, row * 32 + col, cell.Tile | (cell.Pal2 ? 1 << 9 : 0) | (cell.HFlip ? 1 << 10 : 0) | (cell.VFlip ? 1 << 11 : 0), item, cell));
            }
    }

    private static bool HiddenCol(in PictureWindow win, int srcX) => srcX < win.X0 || srcX >= win.X0 + win.W;

    /// <summary>The pixels of a layer that the window, the left-column mask and the vertical-lock split hide (the vertical-lock layer holds the columns from 192 on, the other layer is hidden there).</summary>
    private void HideLayer(ExtPicture.Layer L, bool lockLayer, SmsLineCapture cap, in PictureWindow win, int h, int dx)
    {
        bool any = false;
        for (int el = 0; el < h; el++)
        {
            int r0 = cap.R0[win.Y0 + el], from = 192 + fsLine[el];
            for (int x = 0; x < 256; x++)
            {
                int sx = x + dx;
                bool hd = HiddenCol(win, sx) || ((r0 & 0x20) != 0 && sx < 8) || (lockLayer ? sx < from : (r0 & 0x80) != 0 && sx >= from);
                if (hd) any = true;
                L.Hidden[el * 256 + x] = hd;
            }
        }
        L.AnyHidden = any;
    }

    private byte[] Finish(ExtPicture e, int bgOverflow, int objTilesOver, int spriteExcess)
    {
        // the native NES picture stays off: only the extension draws
        scratch.ClearToBlank();
        var frame = sink.Present(scratch);
        Counters.Add("background tiles past the layer's 1023 (wrong tiles shown)", bgOverflow);
        Counters.Add("sprite tiles past the 1024 the extension holds", objTilesOver);
        Counters.Add("sprite pieces over the 8-per-line limit (the chip drops them too)", spriteExcess);
        Counters.Level("sprite pieces", e.SpriteCount);
        return frame;
    }

    // ======================================================================================================================= the flat path
    private byte[] RenderFlat(SmsVdp vdp, SmsLineCapture cap, PictureWindow win)
    {
        var c = Counters;
        var vram = vdp.Vram; var pic = scratch; pic.Clear();
        int H = vdp.ActiveHeight; bool ext = H != 192;
        int ox = win.OffsetX, oy = win.OffsetY, dx = win.Dx, h = win.H;
        int ntBase = NameTableBase(vdp);
        int vs = cap.R9;
        int bdFrame = Rgb(cap.Backdrop[Math.Min(H, SmsLineCapture.Lines) - 1]);
        bool displayOn = (cap.R1[Math.Min(win.Y0 + h / 2, SmsLineCapture.Lines - 1)] & 0x40) != 0;
        pic.Blank = !displayOn;
        // the NES shows the nametable shifted by one fine X and one fine Y for the whole frame: pick the ones most lines of the picture share
        var xh = fineXVotes; Array.Clear(xh);
        int EffHs(int l) => (cap.R0[l] & 0x40) != 0 && l < 16 ? 0 : cap.R8[l];       // the top rows' scroll lock makes the line's scroll 0
        for (int el = 0; el < h; el++) xh[-EffHs(win.Y0 + el) & 7]++;
        int fineX = 0; for (int i = 1; i < 8; i++) if (xh[i] > xh[fineX]) fineX = i;
        int fineY = (vs + win.Y0 - oy) & 7;
        pic.FineX = fineX; pic.FineY = fineY;
        int linesOffFine = 0; for (int el = 0; el < h; el++) if ((-EffHs(win.Y0 + el) & 7) != fineX) linesOffFine++;
        c.Add("lines with a fine scroll other than the frame's", linesOffFine);

        // cells: 33 x 30 of the NES screen, each the chip's tile under the cell's middle
        var cells = cellGrid; Array.Clear(cells);
        for (int cy = 0; cy < 30; cy++)
        {
            int cellTop = cy * 8 - fineY - oy + win.Y0;                // the source line of the cell's first line (the cell may stick out above or below the window)
            if (cellTop + 7 < win.Y0 || cellTop >= win.Y0 + h) continue;
            int srcY = Math.Clamp(cellTop + 4, win.Y0, win.Y0 + h - 1);    // a line through the middle of the part of the cell the window shows
            int lineHs = (cap.R0[srcY] & 0x40) != 0 && srcY < 16 ? 0 : cap.R8[srcY];
            bool vLock = (cap.R0[srcY] & 0x80) != 0;
            int my = ext ? (srcY + vs) & 0xFF : (srcY + vs) % 224;
            int myLocked = ext ? srcY & 0xFF : srcY % 224;
            for (int cx = 0; cx < 33; cx++)
            {
                int cellLeft = cx * 8 - fineX - ox + win.X0;
                if (cellLeft + 7 < win.X0 || cellLeft >= win.X0 + win.W) continue;
                int srcX = Math.Clamp(cellLeft + 4, win.X0, win.X0 + win.W - 1);
                int mx = (srcX - lineHs) & 255;
                cells[cy, cx] = ReadCell(vdp, ntBase, (vLock && srcX >= 192 + (lineHs & 7) ? myLocked : my) >> 3, mx >> 3);
            }
        }
        // tiles -> items (colours each uses) -> 4 palettes
        var bgPalette = FramePalettes(vdp);
        var bgQ = flatBgQ; bgQ.BeginFrame(); itemOf.Clear();
        for (int cy = 0; cy < 30; cy++) for (int cx = 0; cx < 33; cx++)
        {
            if (cells[cy, cx] is not { } cell) continue;
            int key = cell.Tile << 1 | (cell.Pal2 ? 1 : 0);
            if (itemOf.ContainsKey(key)) continue;
            var pal = bgPalette[cell.Pal2 ? 1 : 0];
            int newItem = bgQ.Add(key, out var hist);
            if (hist != null)
                for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
                {
                    int ci = Pixel(vram, cell.Tile * 32, y, x);
                    if (ci == 0 && pal[0] == bdFrame) continue;
                    int rgb = pal[ci]; hist[rgb] = hist.GetValueOrDefault(rgb) + 1;
                }
            itemOf[key] = newItem;
        }
        bgQ.Solve(4);
        pic.Pal[0] = NesFixPalette.Nearest(bdFrame >> 16 & 0xFF, bdFrame >> 8 & 0xFF, bdFrame & 0xFF);
        for (int g = 0; g < 4; g++) for (int lv = 1; lv < 4; lv++)
        {
            int rgb = bgQ.Palette[g][lv];
            pic.Pal[g * 4 + lv] = NesFixPalette.Nearest(rgb >> 16 & 0xFF, rgb >> 8 & 0xFF, rgb & 0xFF);
        }
        // the tile variants (a tile of the video memory in a palette and a flip) the picture needs, by how many cells use each; the chip holds 255, the most used get one each and the rest are shown as the closest
        variantUses.Clear(); variantCell.Clear();
        for (int cy = 0; cy < 30; cy++) for (int cx = 0; cx < 33; cx++)
        {
            if (cells[cy, cx] is not { } cell) continue;
            int vk = VariantKey(cell);
            variantUses[vk] = variantUses.GetValueOrDefault(vk) + 1; variantCell[vk] = cell;
        }
        levelsQ = bgQ; vramNow = vram; bdNow = bdFrame;          // what FillLevels reads while the packer asks for the pixels of the variants
        var bgMap = packer.Pack(variantUses, this, 255, out int bgOver);
        for (int t = 1; t <= 255; t++)
            if (packer.Kept(t) is { } lv) for (int y = 0; y < 8; y++) { var (p0, p1) = FlatTilePacker.Row(lv, y); pic.ChrBg[t * 16 + y] = p0; pic.ChrBg[t * 16 + 8 + y] = p1; }
        var bgTiles = bgMap;
        Array.Clear(votes); int conflicts = 0;
        for (int cy = 0; cy < 30; cy++) for (int cx = 0; cx < 33; cx++)
        {
            if (cells[cy, cx] is not { } cell) continue;
            int t = bgMap[VariantKey(cell)];
            if (cx < 32) pic.Nt0[cy * 32 + cx] = (byte)t; else pic.Nt1[cy * 32] = (byte)t;
            int g = bgQ.Group[itemOf[cell.Tile << 1 | (cell.Pal2 ? 1 : 0)]];
            votes[cy >> 1, cx >> 1, g]++;
        }
        for (int by = 0; by < 15; by++) for (int bx = 0; bx < 17; bx++)
        {
            int best = 0, total = 0; for (int g = 0; g < 4; g++) { total += votes[by, bx, g]; if (votes[by, bx, g] > votes[by, bx, best]) best = g; }
            conflicts += total - votes[by, bx, best];
            int cx = bx * 2, cy = by * 2, table = cx < 32 ? 0 : 1, lx = cx & 31;
            var nt = table == 0 ? pic.Nt0 : pic.Nt1;
            int ai = 0x3C0 + (cy >> 2) * 8 + (lx >> 2), sh = ((cy & 2) << 1) | (lx & 2);
            nt[ai] = (byte)((nt[ai] & ~(3 << sh)) | (best << sh));
        }
        c.Add("tiles whose 16x16 block holds another palette (shown in the block's colours)", conflicts);
        c.Add("background tile variants past the 255 the NES holds (shown as the closest kept tile)", bgOver);
        c.Level("background tiles", Math.Min(255, bgTiles.Count));

        // ---- sprites: 8x8 pieces in the chip's order; 64 at most (the NES chip drops what it cannot show on a line)
        var pieces = ReadSprites(vdp, out _, out bool zoom);
        var objPalette = pal1;
        var objQ = flatObjQ; objQ.BeginFrame(); pieceItem.Clear(); itemPiece.Clear();
        var keptPieces = kept; keptPieces.Clear();
        foreach (var p in pieces)
        {
            int ey = p.Y - win.Y0, ex = p.X - dx;
            if (ey + 8 <= 0 || ey >= h || ex + 8 <= 0 || ex >= 256) continue;
            long key = (long)p.Tile << 4 | (p.Zoom ? (long)(p.Quadrant + 1) : 0);
            if (!pieceItem.TryGetValue(key, out int item))
            {
                item = objQ.Add(key, out var hist);
                if (hist != null)
                    for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) { int ci = PiecePixel(vram, p, y, x); if (ci != 0) { int rgb = objPalette[ci]; hist[rgb] = hist.GetValueOrDefault(rgb) + 1; } }
                pieceItem[key] = item; itemPiece.Add(p);
            }
            keptPieces.Add((p, item));
        }
        objQ.Solve(4);
        for (int g = 0; g < 4; g++) for (int lv = 1; lv < 4; lv++)
        {
            int rgb = objQ.Palette[g][lv];
            pic.Pal[16 + g * 4 + lv] = NesFixPalette.Nearest(rgb >> 16 & 0xFF, rgb >> 8 & 0xFF, rgb & 0xFF);
        }
        for (int item = 0; item < itemPiece.Count && item < 256; item++)
        {
            var pp = itemPiece[item];
            for (int y = 0; y < 8; y++)
            {
                byte p0 = 0, p1 = 0;
                for (int x = 0; x < 8; x++)
                {
                    int ci = PiecePixel(vram, pp, y, x);
                    int lv = ci == 0 ? 0 : objQ.Level(item, objPalette[ci]);
                    p0 |= (byte)((lv & 1) << (7 - x)); p1 |= (byte)((lv >> 1 & 1) << (7 - x));
                }
                pic.ChrObj[item * 16 + y] = p0; pic.ChrObj[item * 16 + 8 + y] = p1;
            }
        }
        Array.Fill(pic.Oam, (byte)0xFF);
        int nOam = 0, dropped = 0; var perLine = perLineFlat; Array.Clear(perLine);
        foreach (var (p, item) in keptPieces)
        {
            if (nOam >= 64 || item >= 256) { dropped++; continue; }
            int ny = p.Y - win.Y0 + oy - 1, nx = p.X - dx;
            if (ny < -1 || ny > 238 || nx < 0 || nx > 255) { dropped++; continue; }
            int o = nOam++ * 4;
            // the chip's tile priority bit covers a sprite: a NES sprite "behind the background" is the nearest it comes (both chips: only where the background has a colour)
            int cx = Math.Clamp((nx + fineX + 4) >> 3, 0, 32), cy = Math.Clamp((ny + 1 + fineY + 4) >> 3, 0, 29);
            bool behind = cells[cy, cx] is { Prio: true };
            pic.Oam[o] = (byte)ny; pic.Oam[o + 1] = (byte)item; pic.Oam[o + 2] = (byte)(objQ.Group[item] | (behind ? 0x20 : 0)); pic.Oam[o + 3] = (byte)nx;
            for (int ln = Math.Max(0, ny + 1); ln < Math.Min(240, ny + 9); ln++) perLine[ln]++;
        }
        c.Add("sprite pieces past the NES's 64 (or its 256 sprite tiles)", dropped);
        int crowded = 0; for (int ln = 0; ln < perLine.Length; ln++) if (perLine[ln] > 8) crowded++;
        c.Add("lines with more than 8 sprite pieces (the NES chip drops the rest)", crowded);
        return sink.Present(pic);
    }

    private static int VariantKey(Cell cell) => cell.Tile | (cell.Pal2 ? 1 << 9 : 0) | (cell.HFlip ? 1 << 10 : 0) | (cell.VFlip ? 1 << 11 : 0);

    /// <summary>The 64 levels of a background tile variant (a tile of the video memory in a palette line and a flip) on the palettes the flat path chose this frame.</summary>
    void IVariantLevels.FillLevels(int variantKey, byte[] lvs)
    {
        var cell = variantCell[variantKey];
        int item = itemOf[cell.Tile << 1 | (cell.Pal2 ? 1 : 0)];
        var pal = bgPalette[cell.Pal2 ? 1 : 0];
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
        {
            int ci = Pixel(vramNow!, cell.Tile * 32, cell.VFlip ? 7 - y : y, cell.HFlip ? 7 - x : x);
            lvs[y * 8 + x] = (byte)(ci == 0 && pal[0] == bdNow ? 0 : levelsQ!.Level(item, pal[ci]));
        }
    }
}
