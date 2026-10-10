using System;
using System.Collections.Generic;
using NesEmulator.Mix;
using NesEmulator.Sega;

namespace NesEmulator;

/// <summary>
/// A NES game drawn by the Master System video chip. A FIX-timed NES PPU stays in front (register behaviour, timing, NMI, the sprite-0 hit: see <see cref="NesFrontedPpu"/>); once a frame the
/// bridge replays the NES picture on a real <see cref="SmsVdp"/> - the board's own, in its Mode 4 and the 240-line mode of the Master System II - through its ports, and the chip draws the picture the user sees:
/// <list type="bullet">
/// <item>NES 2-bit tiles become Master System 4-bit tiles. A Master System tile picks one of two 16-colour palettes, the NES a 4-colour palette per 16 x 16 block, so the NES palette is baked into the tile
/// instead: colour n of NES palette p becomes colour 4p + n of the background palette (and of the sprite palette for sprites), one tile per (pattern, palette) pair actually on screen.</item>
/// <item>The nametables become the chip's single 32-column map, laid out as the NES scrolls: map slot = NES column modulo 32, each map row showing the NES row of the screen lines it covers, and the chip's per-line
/// horizontal scroll register set from every NES line (status-bar splits survive). A NES line shows 33 columns while the fine scroll is not zero and the chip's map holds 32, so the last column repeats
/// the first (counted). The vertical scroll register carries the fine vertical offset of the frame.</item>
/// <item>The 64 NES sprites become the first 64 chip sprites in the same order (both chips allow 8 a line, the 9th is dropped by either); the chip's sprites cannot flip, so flips are baked into extra tiles; the NES
/// "behind the background" bit becomes the priority bit of the background cells under the sprite (the chip sets it per tile, the NES per sprite; a cell wanted both ways is counted).</item>
/// <item>Colours: each NES colour (as the FIX family draws it) becomes the nearest of the chip's 64 (2 bits a channel).</item>
/// </list>
/// Tile slots, palette changes in mid-frame and the like are counted (<see cref="NesFrontedPpu.Counters"/>). Discovered by CoreRegistry as PPU id "SMS".
/// </summary>
public sealed class PPU_SMS : NesFrontedPpu
{
    // ---- the 16 KB of video memory: tiles 0-439, the 240-line name table at $3700, the sprite attribute table at $3F00
    private const int NameTable = 0x3700, SatBase = 0x3F00, TileSlots = 440;

    private readonly SmsVdp vdp = new(SmsVdpRevision.Sms2, pal: false) { WritesImmediate = true };
    private readonly TilePool tiles = new(1, TileSlots - 1);   // slot 0 stays blank
    private readonly byte[] shadow = new byte[0x4000];         // what the chip's video memory holds
    private readonly byte[] image = new byte[0x4000];          // what it should hold after this frame

    public PPU_SMS(Bus bus) : base(bus) { InitChip(); }

    public override string CoreName => $"SMS:Master System+{front.CoreName}";
    public override string Description => "A Master System video chip drawing a NES game, with a NES PPU keeping the register behaviour";
    public override string BridgeName => $"NES game on the Master System picture chip (front {front.CoreName})";

    /// <summary>The Master System chip drawing the picture (for tools).</summary>
    public SmsVdp Vdp => vdp;

    // ---- the chip's ports (what a Z80 would do), at the start of a line: the write pipeline is switched off, so a write lands at once
    private void Reg(int reg, int value) { vdp.WriteControl((byte)value, 0); vdp.WriteControl((byte)(0x80 | reg), 0); }

    private void SetAddress(int address, int code) { vdp.WriteControl((byte)address, 0); vdp.WriteControl((byte)(code << 6 | address >> 8 & 0x3F), 0); }

    private void InitChip()
    {
        Reg(0, 0x06);                 // Mode 4 with the extended-height bits
        Reg(1, 0x48);                 // display on, 240 lines
        Reg(2, 0x0E);                 // name table: $3700 in the 240-line mode
        Reg(5, 0x7E);                 // sprite table at $3F00
        Reg(6, 0xFB);                 // sprite tiles from the first 256
        Reg(7, 0x00);                 // backdrop = sprite palette entry 0
        Reg(8, 0); Reg(9, 0); Reg(10, 0xFF);
    }

    private static int Rgb6(int rgb) => SegaColour.Sms6(rgb);

    /// <summary>Write bytes of <see cref="image"/> to the chip's video memory through the data port, only where they differ from what it already holds.</summary>
    private void Upload(int address, int length)
    {
        int i = 0;
        while (i < length)
        {
            while (i < length && shadow[address + i] == image[address + i]) i++;
            if (i >= length) break;
            int start = i, equal = 0;
            // a run: stop after a stretch of 8 equal bytes (a fresh address costs two control writes)
            while (i < length && equal < 8) { equal = shadow[address + i] == image[address + i] ? equal + 1 : 0; i++; }
            int end = i - equal;
            SetAddress(address + start, 1);
            for (int k = start; k < end; k++) { vdp.WriteData(image[address + k], 0); shadow[address + k] = image[address + k]; }
        }
    }

    /// <summary>One tile image into <see cref="image"/>: 8 rows of 4 bitplane bytes, the NES palette <paramref name="pal"/> baked in as colours 4 pal + n.</summary>
    private void BakeTile(int slot, byte[] chr, int addr, int pal, bool hflip, bool vflip)
    {
        int o = slot * 32;
        for (int r = 0; r < 8; r++)
        {
            int sr = vflip ? 7 - r : r;
            int p0 = chr[addr + sr], p1 = chr[addr + 8 + sr];
            int b0 = 0, b1 = 0, b2 = 0, b3 = 0;
            for (int x = 0; x < 8; x++)
            {
                int sx = hflip ? 7 - x : x;
                int c = (p0 >> (7 - sx) & 1) | (p1 >> (7 - sx) & 1) << 1;
                if (c == 0) continue;
                int n = pal * 4 + c, bit = 7 - x;
                b0 |= (n & 1) << bit; b1 |= (n >> 1 & 1) << bit; b2 |= (n >> 2 & 1) << bit; b3 |= (n >> 3 & 1) << bit;
            }
            image[o + r * 4] = (byte)b0; image[o + r * 4 + 1] = (byte)b1; image[o + r * 4 + 2] = (byte)b2; image[o + r * 4 + 3] = (byte)b3;
        }
    }

    protected override void Present()
    {
        var c = Counters;
        byte mask = DominantMask();
        tiles.BeginFrame();
        c.Level("chr versions", DistinctChrVersions());
        if ((mask & 1) != 0) c.Add("greyscale frames");
        if ((mask & 0xE0) != 0) c.Add("colour emphasis frames");
        for (int i = 0; i < 32; i++) if (Pal[i] != PalEnd[i]) { c.Add("palette changed mid-frame (frames)"); break; }

        // ---- which lines draw what; the frame's fine vertical offset (the most common Y - line, mod 8)
        var hist = new int[8];
        int objLines = 0, tallLines = 0;
        for (int l = 0; l < 240; l++)
        {
            if ((LineMask[l] & 0x08) != 0) hist[(LineY[l] - l) & 7]++;
            if ((LineMask[l] & 0x10) != 0) objLines++;
            if ((LineCtrl[l] & 0x20) != 0) tallLines++;
        }
        int V = 0; for (int i = 1; i < 8; i++) if (hist[i] > hist[V]) V = i;
        bool tall = tallLines * 2 > 240;
        if (tallLines != 0 && tallLines != 240) c.Add("sprite size changed mid-frame (frames)");

        Array.Clear(image, NameTable, 0x4000 - NameTable);   // the map and the sprite table are rebuilt every frame; the tiles stay where they were put

        // ---- sprites first (their tiles must sit in the first 256 slots): the NES's 64, in order
        bool leftBg = (mask & 2) != 0, leftObj = (mask & 4) != 0;
        var boxes = new List<(int x, int top, int h, bool behind)>();
        var perLine = new int[256];
        for (int i = 0; i < 64; i++)
        {
            int y = FrameOam[i * 4], tile = FrameOam[i * 4 + 1], at = FrameOam[i * 4 + 2], x = FrameOam[i * 4 + 3];
            bool visible = objLines > 0 && y < 239;
            int ySlot = SatBase + i, xnSlot = SatBase + 0x80 + i * 2;
            int top = y + 1;
            if (visible)
            {
                int ctrlLine = Math.Clamp(top, 0, 239);
                var chr = ChrAtLine(ctrlLine);
                int slot; bool isNew;
                bool hf = (at & 0x40) != 0, vf = (at & 0x80) != 0; int pal = at & 3;
                long flags = (long)chr.Id << 24 | (long)pal << 20 | (hf ? 0x40000 : 0) | (vf ? 0x20000 : 0);
                if (tall)
                {
                    int topIdx = (tile & 0xFE) + ((tile & 1) != 0 ? 256 : 0);
                    slot = tiles.Get(flags | 0x80000 | (uint)topIdx << 1 | 1, out isNew, 2, 2, 1, 256);
                    if (slot >= 0 && isNew)
                    {
                        // a vertical flip swaps the two halves
                        BakeTile(slot, chr.Data, (vf ? topIdx + 1 : topIdx) * 16, pal, hf, vf);
                        BakeTile(slot + 1, chr.Data, (vf ? topIdx : topIdx + 1) * 16, pal, hf, vf);
                    }
                }
                else
                {
                    int idx = tile + ((LineCtrl[ctrlLine] & 0x08) != 0 ? 256 : 0);
                    slot = tiles.Get(flags | (uint)idx << 1 | 1, out isNew, 1, 1, 1, 256);
                    if (slot >= 0 && isNew) BakeTile(slot, chr.Data, idx * 16, pal, hf, vf);
                }
                if (slot < 0) { c.Add("sprite tiles past the first 256 slots"); visible = false; }
                else
                {
                    image[ySlot] = (byte)y; image[xnSlot] = (byte)x; image[xnSlot + 1] = (byte)slot;
                    int h = tall ? 16 : 8;
                    for (int l = Math.Max(0, top); l < Math.Min(240, top + h); l++) perLine[l]++;
                    boxes.Add((x, top, h, (at & 0x20) != 0));
                }
            }
            if (!visible) { image[ySlot] = 0xEF; image[xnSlot] = 0; image[xnSlot + 1] = 0; }
        }

        // ---- the name table: map row ty shows the NES row of the screen lines (8 ty - V) .. (8 ty - V + 7)
        int fineMismatch = 0, splitRows = 0, edgeLines = 0;
        var rowHs = new int[32];
        for (int ty = 0; ty < 31; ty++)
        {
            int l0 = Math.Max(0, 8 * ty - V), l1 = Math.Min(239, 8 * ty - V + 7);
            if (l0 > l1) continue;
            int pick = l0; bool any = false;
            for (int l = l0; l <= l1; l++) if ((LineMask[l] & 0x08) != 0) { if (!any) pick = l; any = true; }
            rowHs[ty] = -LineX[pick] & 0xFF;
            if (!any) continue;
            int nesRow = LineY[pick] >> 3, c0 = LineX[pick] >> 3;
            bool split = false;
            for (int l = l0; l <= l1; l++)
            {
                if ((LineMask[l] & 0x08) == 0) continue;
                if (((LineY[l] - l - V) & 7) != 0) fineMismatch++;
                else if ((LineY[l] >> 3) != nesRow || (LineX[l] >> 3) != c0 || ((LineX[l] ^ LineX[pick]) & 7) != 0) split = true;
                if ((LineX[l] & 7) != 0) edgeLines++;
            }
            if (split) splitRows++;
            int table = (LineCtrl[pick] & 0x10) != 0 ? 256 : 0;
            var chr = ChrAtLine(pick);
            for (int k = 0; k < 32; k++)
            {
                int a = (c0 + k) & 63, ring = a & 31;
                int t = NesCell(a * 8, LineY[pick], out int pal);
                long key = (long)chr.Id << 24 | (long)pal << 20 | (uint)(table + t) << 1;
                int slot = tiles.Find(key); bool isNew = false;
                if (slot < 0) slot = tiles.Alloc(key, out isNew, 1, 1, 256, TileSlots);          // the background prefers the slots above the sprites'
                if (slot < 0) slot = tiles.Alloc(key, out isNew, 1, 1, 1, TileSlots);
                if (slot < 0) { c.Add("background tiles past the video memory"); continue; }
                if (isNew) BakeTile(slot, chr.Data, (table + t) * 16, pal, false, false);
                int at = NameTable + ty * 64 + ring * 2;
                image[at] = (byte)slot; image[at + 1] = (byte)(slot >> 8);      // tile number; no flips, palette 0, priority later
            }
        }
        c.Add("lines with a fine vertical scroll the frame's does not share", fineMismatch);
        c.Add("tile rows split by a mid-row scroll change", splitRows);
        c.Add("lines whose right edge column repeats the left", edgeLines);

        // ---- the priority bit: a NES sprite "behind the background" becomes the priority bit of the background cells under it
        var behindCells = new HashSet<int>(); var frontCells = new HashSet<int>();
        foreach (var (x, top, h, behind) in boxes)
            for (int l = Math.Max(0, top); l < Math.Min(240, top + h); l += 4)
            {
                int ty = Math.Min((l + V) >> 3, 31);
                for (int sx = x; sx < x + 8; sx += 7)
                {
                    if (sx > 255) continue;
                    int hs = rowHs[ty], col = (sx - (hs & 7)) >> 3, ring = (col - (hs >> 3)) & 31;
                    (behind ? behindCells : frontCells).Add(ty * 32 + ring);
                }
            }
        int conflicts = 0;
        foreach (int cell in behindCells)
        {
            if (frontCells.Contains(cell)) { conflicts++; continue; }
            int at = NameTable + (cell >> 5) * 64 + (cell & 31) * 2;
            image[at + 1] |= 0x10;       // the background's colours cover the sprites here
        }
        c.Add("background cells wanted both in front of and behind a sprite", conflicts);
        int over8 = 0; for (int l = 0; l < 240; l++) if (perLine[l] > 8) over8++;
        c.Add("lines with more than 8 sprites (the 9th and later are dropped, as on a NES)", over8);
        if (leftBg != leftObj && (mask & 0x18) == 0x18) c.Add("left-column mask applies to one layer only (frames)");
        c.Level("tiles in video memory", tiles.InUse);

        // ---- palettes: background entries 4p + n, sprite entries 16 + 4p + n; the backdrop is sprite entry 0
        int bd = NesRgb(Pal[0], mask);
        var cram = new byte[32];
        for (int p = 0; p < 4; p++)
            for (int n = 0; n < 4; n++)
            {
                cram[p * 4 + n] = (byte)Rgb6(n == 0 ? bd : NesRgb(Pal[p * 4 + n], mask));
                cram[16 + p * 4 + n] = (byte)Rgb6(n == 0 ? bd : NesRgb(Pal[16 + p * 4 + n], mask));
            }
        SetAddress(0, 3);
        for (int i = 0; i < 32; i++) vdp.WriteData(cram[i], 0);

        // ---- the chip: registers, memory, one frame, line by line
        Reg(0, (leftBg || leftObj) ? 0x06 : 0x26);                 // both clipped in the left 8 pixels: the chip blanks that column
        Reg(1, tall ? 0x4A : 0x48);
        Reg(9, V);
        Upload(0, 0x4000);
        for (int l = 0; l < vdp.TotalLines; l++)
        {
            if (l < 240) Reg(8, -LineX[l] & 0xFF);
            vdp.BeginLine(l);
            vdp.EndLine();
        }
        var src = vdp.Frame;
        for (int y = 0; y < 240; y++)
            for (int x = 0; x < 256; x++) StoreRgb(x, y, src[y * 256 + x]);
    }
}
