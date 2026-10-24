using System;
using System.Collections.Generic;

namespace NesEmulator.Mix;

/// <summary>
/// A NES picture as the cross-console layers (<see cref="ExtPicture"/>): the translator behind a NES game on the Game Boy enhanced chip (PPU_DMGS). The NES's
/// picture is one background layer and its sprites, at its own 256 x 240, so the enhanced chip shows all of it: the whole 2 x 2 nametable map (64 x 64 cells,
/// mirroring as the cartridge maps it, the rows 30 and 31 of a nametable being its attribute bytes as on the NES) with each line's own scroll, each 8-line
/// group's own pattern table (games switch CHR banks mid-frame), the per-line background / sprite enables and left-8 clips, the four background and four sprite
/// palettes exactly (a NES tile IS 3 colours + see-through: the levels are the colour indices), 64 sprites (8 x 16 as two pieces) with the NES's own 8 a line,
/// its behind-the-background flag, its flips and its order (the first sprite with a pixel owns it, as on the NES). Not carried: emphasis and greyscale,
/// palette writes in the middle of a frame (the frame's palette is used), sprite tiles switched in the middle of a frame (the snapshot of the middle line is used).
/// Everything is kept between frames: once warm it allocates nothing.
/// </summary>
internal sealed class NesToLayers
{
    private readonly Dictionary<(ulong, ulong), int> bgTileOf = new(512);
    private readonly Dictionary<int, int> objTileOf = new(128);
    private readonly int[] cellGroup = new int[64 * 64];
    private readonly bool[] cellSeen = new bool[64 * 64];
    /// <summary>The cells only a wide view's side columns show (<see cref="ExtPicture.ViewWidth"/>): given tiles after the guest's own.</summary>
    private readonly bool[] cellSide = new bool[64 * 64];
    private readonly byte[] pattern = new byte[16];

    /// <summary>Background tiles past the layer's 1023 in the last frame (shown as the closest by number).</summary>
    public int TileOverflow { get; private set; }
    /// <summary>The same for the tiles only a wide view's side columns show (counted apart: the guest's own picture gets its tiles first).</summary>
    public int SideOverflow { get; private set; }

    /// <summary>How wide the NES's background map really is (the wide view's report, <see cref="ExtPicture.Layer.RealWidth"/>): 512 when the left and right nametables are different memory (vertical mirroring, or
    /// a mapper that gives the quadrants different nametables), else 256 (horizontal or one-screen mirroring: the right nametable is the left one again).</summary>
    public static int NametableWidth(Cartridge? cart)
    {
        if (cart == null) return 256;
        if (cart.mapper is IMapper m)
        {
            int a = m.GetMmc5NtModeForAddress(0x2000), b = m.GetMmc5NtModeForAddress(0x2400);
            if (a >= 0 || b >= 0) return a != b ? 512 : 256;
        }
        return cart.mirroringMode == Mirroring.Vertical ? 512 : 256;
    }

    private static uint Rgb(int nes) => (uint)NesPalette.Rgb[nes & 0x3F];

    /// <param name="nt">The 4 KB of nametables as the PPU reads them ($2000-$2FFF, the cartridge's mirroring applied).</param>
    /// <param name="pal">The 32 palette bytes.</param>
    /// <param name="rowChr">The pattern tables as each 8-line group was drawn (index = line / 8).</param>
    /// <param name="chr">The pattern tables for the sprites (the middle of the frame).</param>
    /// <param name="oam">The 256 OAM bytes as the frame started.</param>
    /// <param name="ctrl">PPUCTRL as the frame started (pattern tables, 8 x 16 sprites).</param>
    /// <param name="lineX">Per line: the map pixel at x = 0 (0-511), as the PPU's v and fine X gave it.</param>
    /// <param name="lineY">Per line: the map line (0-511, 256 a nametable).</param>
    /// <param name="lineMask">Per line: PPUMASK.</param>
    /// <param name="realWidth">How wide the background map really is (<see cref="NametableWidth"/>: 512 with vertical mirroring, else 256), the layer's <see cref="ExtPicture.Layer.RealWidth"/>.</param>
    public void Fill(ExtPicture e, byte[] nt, byte[] pal, byte[][] rowChr, byte[] chr, byte[] oam, byte ctrl, int[] lineX, int[] lineY, byte[] lineMask, int realWidth)
    {
        const int W = 256, H = 240;
        e.BeginFrame(W, H);
        e.Guest = "NES";
        var L = e.Layers[0];
        L.Enabled = true; L.Name = "background"; L.MapWidth = 64; L.MapHeight = 64; L.SnesLayer = 0;
        L.RealWidth = realWidth;
        e.SpritesBeyondEdges = true;     // the part of a sprite past the right edge is where the game put it (a NES sprite never starts beyond the 256 columns)
        int sideCells = e.SideColumns > 0 ? (e.SideColumns + 7) / 8 + 1 : 0;     // a wide view: the cells its side columns show, after the guest's own
        e.LayerCount = 1;
        for (int p = 0; p < 4; p++)
            for (int c = 1; c < 4; c++) { e.BgRgb[p * 4 + c] = Rgb(pal[p * 4 + c]); e.ObjRgb[p * 4 + c] = Rgb(pal[16 + p * 4 + c]); }
        int bgTable = (ctrl & 0x10) != 0 ? 0x1000 : 0;
        Array.Clear(cellSeen);
        if (sideCells > 0) Array.Clear(cellSide);
        bool anyObjHidden = false;
        for (int y = 0; y < H; y++)
        {
            e.BackdropRgb[y] = e.BackdropAltRgb[y] = Rgb(pal[0]);
            byte mask = lineMask[y];
            int X = lineX[y] & 511, Y = lineY[y] & 511;
            if ((mask & 0x08) != 0)
            {
                L.On[y] = true;
                L.ScrollX[y] = (short)X; L.ScrollY[y] = (short)((Y - y) & 511);
                L.ZLow[y] = L.ZHigh[y] = 5;
                L.BlankLeft[y] = (byte)((mask & 0x02) == 0 ? 8 : 0);
                int row = (Y >> 3) & 63, group = Math.Min(y >> 3, rowChr.Length - 1);
                for (int c = 0; c <= 32; c++)
                {
                    int cell = row * 64 + (((X + c * 8) >> 3) & 63);
                    cellSeen[cell] = true; cellGroup[cell] = group;
                }
                if (sideCells > 0)
                for (int c = -sideCells; c <= 32 + sideCells; c++)
                {
                    if (c == 0) c = 33;
                    int cell = row * 64 + (((X + c * 8) >> 3) & 63);
                    if (!cellSeen[cell]) { cellSide[cell] = true; cellGroup[cell] = group; }
                }
            }
            if ((mask & 0x10) != 0)
            {
                e.ObjOn[y] = true;
                e.ObjZ[y * 4 + 0] = 4;     // behind the background: over the backdrop, under a background pixel
                e.ObjZ[y * 4 + 1] = 10;    // in front
                if ((mask & 0x04) == 0)
                {
                    if (!anyObjHidden) { Array.Clear(e.ObjHidden); anyObjHidden = true; }
                    for (int x = 0; x < 8; x++) e.ObjHidden[y * ExtPicture.Stride + x] = true;
                }
            }
        }
        e.AnyObjHidden = anyObjHidden;
        // ---- the background tiles the frame shows: one per pattern (by content), the colour index its level
        bgTileOf.Clear(); int next = 1, overflow = 0;
        SideOverflow = 0;
        int passes = sideCells > 0 ? 2 : 1;     // the guest's own cells first, then the side columns' (their tiles after the guest's)
        for (int pass = 0; pass < passes; pass++)
        for (int cell = 0; cell < 64 * 64; cell++)
        {
            if (pass == 0 ? !cellSeen[cell] : (!cellSide[cell] || cellSeen[cell])) continue;
            int row = cell >> 6, col = cell & 63;
            int baseAddr = ((row >> 5) * 2 + (col >> 5)) * 0x400, r = row & 31, cc = col & 31;
            int tile = nt[baseAddr + r * 32 + cc];
            int attr = nt[baseAddr + 0x3C0 + (r >> 2) * 8 + (cc >> 2)];
            int palette = (attr >> (((r & 2) << 1) | (cc & 2))) & 3;
            var src = rowChr[cellGroup[cell]];
            int at = bgTable + tile * 16;
            for (int i = 0; i < 16; i++) pattern[i] = src[(at + i) & 0x1FFF];
            var key = (BitConverter.ToUInt64(pattern, 0), BitConverter.ToUInt64(pattern, 8));
            if (!bgTileOf.TryGetValue(key, out int t))
            {
                if (next >= ExtPicture.MaxTiles) { if (pass == 0) overflow++; else SideOverflow++; t = 1 + tile % (ExtPicture.MaxTiles - 1); }
                else
                {
                    t = next++; bgTileOf[key] = t;
                    for (int y = 0; y < 8; y++)
                        for (int x = 0; x < 8; x++)
                            L.Tiles[t * 64 + y * 8 + x] = (byte)((pattern[y] >> (7 - x) & 1) | (pattern[8 + y] >> (7 - x) & 1) << 1);
                }
            }
            L.Map[cell] = (ushort)t; L.Attr[cell] = (byte)palette;
        }
        TileOverflow = overflow;
        // ---- the sprites in OAM order (earlier in front), 8 x 16 as two pieces, the NES's 8 a line
        objTileOf.Clear();
        bool tall = (ctrl & 0x20) != 0; int sprTable = (ctrl & 0x08) != 0 ? 0x1000 : 0;
        int n = 0;
        if (e.Sprites.Length < 128) e.Sprites = new ExtPicture.Sprite[128];
        for (int i = 0; i < 64; i++)
        {
            int y = oam[i * 4] + 1, tile = oam[i * 4 + 1], at = oam[i * 4 + 2], x = oam[i * 4 + 3];
            if (y >= 240) continue;
            bool vf = (at & 0x80) != 0, hf = (at & 0x40) != 0;
            if (!tall) { Piece(e, ref n, x, y, sprTable + tile * 16, at, hf, vf, chr); continue; }
            int tbl = (tile & 1) != 0 ? 0x1000 : 0, top = tile & 0xFE;
            Piece(e, ref n, x, y, tbl + (vf ? top + 1 : top) * 16, at, hf, vf, chr);
            Piece(e, ref n, x, y + 8, tbl + (vf ? top : top + 1) * 16, at, hf, vf, chr);
        }
        e.SpriteCount = n; e.SpriteLimit = 1024; e.SpritesPerLine = 8;
        e.PaletteVersion++;
    }

    private void Piece(ExtPicture e, ref int n, int x, int y, int addr, int at, bool hf, bool vf, byte[] chr)
    {
        if (!objTileOf.TryGetValue(addr, out int t))
        {
            t = objTileOf.Count; objTileOf[addr] = t;
            for (int r = 0; r < 8; r++)
            {
                int lo = chr[(addr + r) & 0x1FFF], hi = chr[(addr + 8 + r) & 0x1FFF];
                for (int c = 0; c < 8; c++) e.SpriteTiles[t * 64 + r * 8 + c] = (byte)((lo >> (7 - c) & 1) | (hi >> (7 - c) & 1) << 1);
            }
        }
        e.Sprites[n++] = new ExtPicture.Sprite
        {
            X = (short)x, Y = (short)y, FirstLine = 0, LastLine = 239, Tile = (ushort)t, Palette = (byte)(at & 3),
            Priority = (byte)((at & 0x20) != 0 ? 0 : 1), HFlip = hf, VFlip = vf,
        };
    }
}
