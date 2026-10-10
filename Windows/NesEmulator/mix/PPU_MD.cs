using System;
using System.Collections.Generic;
using NesEmulator.Mix;
using NesEmulator.Sega;

namespace NesEmulator;

/// <summary>
/// A NES game drawn by the Genesis video chip. A FIX-timed NES PPU stays in front (register behaviour, timing, NMI, the sprite-0 hit: see <see cref="NesFrontedPpu"/>); once a frame the
/// bridge replays the NES picture on a real <see cref="MdVdp"/> - the one the Genesis board uses - and the Genesis chip draws the picture the user sees:
/// <list type="bullet">
/// <item>NES 2-bit tiles become Genesis 4-bit tiles (colour n stays n for the background; sprite tiles use 4 + n, so one palette line carries a NES background palette in entries 1-3 and the
/// NES sprite palette of the same number in entries 5-7). The nametables become plane A (64 x 64 cells, the NES's 2 x 2 screens), each cell's attribute palette a palette line.</item>
/// <item>Scrolling is the VDP's: the horizontal scroll goes into the per-line scroll table (one entry per line, so status-bar splits survive), the vertical scroll's fine part into the vertical
/// scroll register; the map rows are laid out screen-aligned so a vertical split lands on the right rows.</item>
/// <item>The 64 NES sprites become the first 64 Genesis sprites in the same order; flips, palette and the NES's "behind the background" bit (the Genesis priority bit) carry over; 8x16 is a
/// 1x2-cell sprite. The Genesis allows 16 sprites a line in this width where the NES allows 8, so a NES game that flickers to cheat the limit shows all its sprites steadily.</item>
/// <item>Colours: each NES colour becomes the nearest of the Genesis's 512 (3 bits a channel, the VDP's own DAC levels); greyscale is applied, emphasis is approximated.</item>
/// </list>
/// The picture is the NES's 256 x 240 on the chip in its 32-cell, 240-line mode. What this cannot carry is counted (<see cref="NesFrontedPpu.Counters"/>): tile slots past the video memory,
/// lines whose fine vertical scroll disagrees with the frame's, lines that split inside a tile row, a palette written mid-frame, colour emphasis, left-column masks the Genesis can only apply to both layers.
/// Discovered by CoreRegistry as PPU id "MD".
/// </summary>
public sealed class PPU_MD : NesFrontedPpu
{
    // ---- the video memory plan (byte addresses)
    private const int SatAddr = 0xB000, HScrollAddr = 0xB800, PlaneAAddr = 0xC000, PlaneBAddr = 0xE000;
    private const int FirstTile = 1, TileSlots = 0xB000 / 32 - 1;   // tile 0 stays blank (plane B and the empty cells use it)

    private readonly MdVdp vdp = new(SegaTimeline.Genesis(SegaRegion.Ntsc));
    private readonly TilePool tiles = new(FirstTile, TileSlots);

    public PPU_MD(Bus bus) : base(bus) { InitChip(); }

    public override string CoreName => $"MD:Genesis+{front.CoreName}";
    public override string Description => "A Genesis video chip drawing a NES game, with a NES PPU keeping the register behaviour";
    public override string BridgeName => $"NES game on the Genesis picture chip (front {front.CoreName})";

    /// <summary>The Genesis chip drawing the picture (for tools).</summary>
    public MdVdp Vdp => vdp;

    private void Reg(int reg, int value) => vdp.WriteControl((ushort)(0x8000 | reg << 8 | value & 0xFF), vdp.Time);

    private void InitChip()
    {
        Reg(0, 0x04);
        Reg(1, 0x4C);                      // display on, Genesis mode, 30 rows (240 lines)
        Reg(2, PlaneAAddr >> 10);
        Reg(3, 0x00);
        Reg(4, PlaneBAddr >> 13);
        Reg(5, SatAddr >> 9);
        Reg(7, 0x00);                      // backdrop = palette line 0 entry 0
        Reg(10, 0xFF);
        Reg(11, 0x03);                     // per-line horizontal scroll, whole-screen vertical scroll
        Reg(12, 0x00);                     // 32 cells wide (256 pixels), no interlace, no shadow/highlight
        Reg(13, HScrollAddr >> 10);
        Reg(15, 0x02);
        Reg(16, 0x11);                     // planes 64 x 64 cells
        Reg(17, 0x00); Reg(18, 0x00);      // no window
    }

    private int Slot(long key, out bool isNew, int span = 1) => tiles.Get(key, out isNew, span);

    private void WriteTile(int slot, byte[] chr, int addr, bool sprite)
    {
        int o = slot * 32;
        var vram = vdp.Vram;
        for (int r = 0; r < 8; r++)
        {
            int p0 = chr[addr + r], p1 = chr[addr + 8 + r];
            for (int k = 0; k < 4; k++)
            {
                int c0 = 7 - 2 * k, c1 = 6 - 2 * k;
                int a = (p0 >> c0 & 1) | (p1 >> c0 & 1) << 1, b = (p0 >> c1 & 1) | (p1 >> c1 & 1) << 1;
                if (sprite) { if (a != 0) a += 4; if (b != 0) b += 4; }
                vram[o + r * 4 + k] = (byte)(a << 4 | b);
            }
        }
    }

    private static ushort Cram9(int rgb) => (ushort)SegaColour.Md9(rgb);

    protected override void Present()
    {
        var c = Counters;
        var vram = vdp.Vram;
        byte mask = DominantMask();
        tiles.BeginFrame();
        c.Level("chr versions", DistinctChrVersions());
        if ((mask & 1) != 0) c.Add("greyscale frames");
        if ((mask & 0xE0) != 0) c.Add("colour emphasis frames");
        for (int i = 0; i < 32; i++) if (Pal[i] != PalEnd[i]) { c.Add("palette changed mid-frame (frames)"); break; }

        // ---- palette: line p holds the NES background palette p in entries 1-3 and the sprite palette p in entries 5-7
        int bd = NesRgb(Pal[0], mask);
        for (int p = 0; p < 4; p++)
        {
            vdp.LoadColour(p * 16, Cram9(bd));
            vdp.LoadColour(p * 16 + 4, Cram9(bd));
            for (int k = 1; k < 4; k++)
            {
                vdp.LoadColour(p * 16 + k, Cram9(NesRgb(Pal[p * 4 + k], mask)));
                vdp.LoadColour(p * 16 + 4 + k, Cram9(NesRgb(Pal[16 + p * 4 + k], mask)));
            }
        }

        // ---- which lines draw the background / sprites; the frame's vertical fine offset (the most common Y - line, mod 8)
        var hist = new int[8];
        int bgLines = 0, objLines = 0, tallLines = 0;
        for (int l = 0; l < 240; l++)
        {
            if ((LineMask[l] & 0x08) != 0) { bgLines++; hist[(LineY[l] - l) & 7]++; }
            if ((LineMask[l] & 0x10) != 0) objLines++;
            if ((LineCtrl[l] & 0x20) != 0) tallLines++;
        }
        int V = 0; for (int i = 1; i < 8; i++) if (hist[i] > hist[V]) V = i;
        bool tall = tallLines * 2 > 240;
        if (tallLines != 0 && tallLines != 240) c.Add("sprite size changed mid-frame (frames)");

        // ---- plane A: map row ty shows the NES row of the screen lines (8 ty - V) .. (8 ty - V + 7), all 64 columns of it
        Array.Clear(vram, PlaneAAddr, 0x2000);
        int fineMismatch = 0, splitRows = 0;
        for (int ty = 0; ty < 31; ty++)
        {
            int l0 = Math.Max(0, 8 * ty - V), l1 = Math.Min(239, 8 * ty - V + 7);
            if (l0 > l1) continue;
            int pick = l0; bool any = false;
            for (int l = l0; l <= l1; l++) if ((LineMask[l] & 0x08) != 0) { if (!any) pick = l; any = true; }
            if (!any) continue;                                   // background off on the whole row: blank cells
            int nesRow = LineY[pick] >> 3;
            for (int l = l0; l <= l1; l++)
            {
                if ((LineMask[l] & 0x08) == 0) continue;
                if (((LineY[l] - l - V) & 7) != 0) fineMismatch++;
                else if ((LineY[l] >> 3) != nesRow) { splitRows++; break; }
            }
            int table = (LineCtrl[pick] & 0x10) != 0 ? 256 : 0;
            var chr = ChrAtLine(pick);
            for (int a = 0; a < 64; a++)
            {
                int t = NesCell(a * 8, LineY[pick], out int pal);
                int slot = Slot((long)chr.Id << 20 | (uint)(table + t) << 1, out bool isNew);
                if (slot < 0) { c.Add("tiles past the video memory"); continue; }
                if (isNew) WriteTile(slot, chr.Data, (table + t) * 16, false);
                int cellAddr = PlaneAAddr + (ty * 64 + a) * 2;
                int word = slot | pal << 13 | 0x8000;
                vram[cellAddr] = (byte)(word >> 8); vram[cellAddr + 1] = (byte)word;
            }
        }
        c.Add("lines with a fine vertical scroll the frame's does not share", fineMismatch);
        c.Add("tile rows split by a mid-row scroll change", splitRows);

        // ---- the per-line horizontal scroll table (plane A moves right by the value, the NES left by its scroll), vertical scroll
        for (int l = 0; l < 240; l++)
        {
            int hs = -LineX[l] & 0x3FF;
            vram[HScrollAddr + l * 4] = (byte)(hs >> 8); vram[HScrollAddr + l * 4 + 1] = (byte)hs;
            vram[HScrollAddr + l * 4 + 2] = 0; vram[HScrollAddr + l * 4 + 3] = 0;
        }
        vdp.Vsram[0] = (ushort)V; vdp.Vsram[1] = 0;

        // ---- sprites: the NES's 64, in order
        bool leftBg = (mask & 2) != 0, leftObj = (mask & 4) != 0;
        Reg(0, (leftBg || leftObj) ? 0x04 : 0x24);                 // both clipped in the left 8 pixels: the chip blanks that column
        if (leftBg != leftObj && (mask & 0x18) == 0x18) c.Add("left-column mask applies to one layer only (frames)");
        int onLine8 = 0; var perLine = new int[256];
        for (int i = 0; i < 64; i++)
        {
            int y = FrameOam[i * 4], tile = FrameOam[i * 4 + 1], at = FrameOam[i * 4 + 2], x = FrameOam[i * 4 + 3];
            int o = SatAddr + i * 8;
            bool visible = objLines > 0 && y < 239;
            int top = y + 1;
            int word = 0, ypos = 0, size = tall ? 0x01 : 0x00;
            if (visible)
            {
                int ctrlLine = Math.Clamp(top, 0, 239);
                var chr = ChrAtLine(ctrlLine);
                int slot; bool isNew;
                if (tall)
                {
                    int topIdx = (tile & 0xFE) + ((tile & 1) != 0 ? 256 : 0);
                    slot = Slot((long)chr.Id << 20 | 0x400 | (uint)topIdx << 1 | 1, out isNew, 2);
                    if (slot >= 0 && isNew) { WriteTile(slot, chr.Data, topIdx * 16, true); WriteTile(slot + 1, chr.Data, (topIdx + 1) * 16, true); }
                }
                else
                {
                    int idx = tile + ((LineCtrl[ctrlLine] & 0x08) != 0 ? 256 : 0);
                    slot = Slot((long)chr.Id << 20 | (uint)idx << 1 | 1, out isNew);
                    if (slot >= 0 && isNew) WriteTile(slot, chr.Data, idx * 16, true);
                }
                if (slot < 0) { c.Add("tiles past the video memory"); visible = false; }
                else
                {
                    bool behind = (at & 0x20) != 0;
                    word = slot | (at & 0x40) << 5 | (at & 0x80) << 5 | (at & 3) << 13 | (behind ? 0 : 0x8000);
                    ypos = top + 128;
                    int h = tall ? 16 : 8;
                    for (int l = Math.Max(0, top); l < Math.Min(240, top + h); l++) perLine[l]++;
                }
            }
            vram[o] = (byte)(ypos >> 8 & 3); vram[o + 1] = (byte)ypos;
            vram[o + 2] = (byte)size; vram[o + 3] = (byte)(i < 63 ? i + 1 : 0);
            vram[o + 4] = (byte)(word >> 8); vram[o + 5] = (byte)word;
            int xraw = x + 128;
            vram[o + 6] = (byte)(xraw >> 8 & 1); vram[o + 7] = (byte)xraw;
        }
        for (int l = 0; l < 240; l++) if (perLine[l] > 8) onLine8++;
        c.Add("lines with more than 8 sprites (all shown; a NES would drop the rest)", onLine8);
        for (int l = 0; l < 240; l++) if (perLine[l] > 16) { c.Add("sprites dropped by the Genesis 16-per-line limit", perLine[l] - 16); }

        c.Level("background tiles+sprite tiles in video memory", tiles.InUse);

        // ---- have the Genesis chip draw one frame (its memories were set directly - the cost a DMA in the vertical blank would be)
        vdp.ResetPictureState();
        for (int l = 0; l < 262; l++) vdp.RunTo(vdp.Time + MdVdp.ClocksPerLine);
        var pic = vdp.Picture;
        int w = Math.Min(vdp.PictureWidth, 256), h2 = Math.Min(vdp.PictureHeight, 240);
        for (int y = 0; y < h2; y++)
            for (int x = 0; x < w; x++) StoreRgb(x, y, pic[y * MdVdp.PictureStride + x]);
    }
}
