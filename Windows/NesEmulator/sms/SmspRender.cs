// SmspRender - SMS Plus GX's Master System / Game Gear line renderer, ported from ducalex/retro-go tag 1.20,
// smsplusgx-go/components/smsplus/render.c (844 lines): render_line, render_bg_sms, render_obj_sms, tile_get, parse_satb,
// the bg/sprite priority look-up table (render_init), palette_sync with its expand tables and retro-go's RGB565 MAKE_PIXEL.
// The register decode (ntab, satb, extended) follows vdp.c of the same tag (viewport_check, vdp_reg_w).
//
//   SMS Plus, (C) 1998-2007 Charles MacDonald; additional code by Eke-Eke (SMS Plus GX); retro-go port by ducalex.
//   GNU GPL v2 or later (render.c header). See THIRD_PARTY_NOTICES.md (PPU harvest) and sega/THIRD_PARTY_NOTICES.md.
//
// PPU harvest climb (docs/projects/ppu-harvest-catalog.md, runners-up): an art instrument, ported FAITHFULLY with its flaws. It is the
// Master System / Game Gear picture core "SMSP": the stock VDP (SmsVdp) keeps the console's timing, its memories, the status flags and
// interrupts; this renderer draws each line whole as the stock chip starts it (SmsVdp.LineObserver), from the live registers, VRAM and CRAM.
//
// Characteristic jank, all of it kept on purpose:
//  * Whole line at once at line start (SMS Plus renders the line when it begins): mid-line palette / scroll / register writes vanish.
//  * The ESP32 colour path: CRAM levels expand to 0, 44, 124, 220 (not 0, 85, 170, 255) and go through RGB565, so the Master System
//    picture is darker with a green cast; Game Gear 4-bit levels are also squeezed through RGB565.
//  * Vertical-scroll lock (register 0 bit 7) re-reads the pattern row from the unscrolled line but keeps the SCROLLED name-table row,
//    so the locked right-hand columns show the wrong tiles' rows whenever the screen is scrolled vertically.
//  * Sprites for line L are found while line L-1 is drawn, with the line counter table: on line 0 the counter is 0xFF and the
//    Y > 240 wrap leaves sprites with Y = 255 out, so their top row never shows.
//  * The 208 end marker is honoured only in the 192-line mode; the original VDP's quirks (first four zoomed sprites only, the
//    sprite pattern / attribute masks of the 315-5124) are not modelled; the name-table mask bit 0 quirk applies to the SMS1 only.
//  * Zoomed sprites: the right-hand pixel of each pair is written even past the clip edge.
//  * The hscroll lock (register 0 bit 6) is not applied on the Game Gear; the left-column blank is not applied on the Game Gear.
//  * Not ported (they live in tms.c, outside the pinned file): the TMS9918 modes 0-3. Lines in those modes are left to the stock chip,
//    and the menu label says so. The collision and overflow flags stay the stock chip's.
using System;

namespace NesEmulator.Sega;

/// <summary>The common part of the PPU harvest's Master System renderers: a picture of the stock layout (256 wide), the lines left to the stock chip (TMS modes) copied at frame end.</summary>
internal abstract class SmsHarvestRender
{
    protected readonly SmsVdp v;
    protected readonly bool gg, sms1;
    public readonly uint[] Picture = new uint[256 * 240];
    private readonly bool[] stockLine = new bool[240];

    protected SmsHarvestRender(SmsVdp vdp)
    {
        v = vdp; gg = vdp.Revision == SmsVdpRevision.GameGear; sms1 = vdp.Revision == SmsVdpRevision.Sms1;
        v.LineObserver = OnLine;
    }

    public void Detach() { if (v.LineObserver == OnLine) v.LineObserver = null; }

    private void OnLine(int n)
    {
        if (!v.Mode4On) { if (n < 240) stockLine[n] = true; return; }
        if (n < 240) stockLine[n] = false;
        DrawLine(n);
    }

    /// <summary>Draws line <paramref name="n"/> of the frame (n &gt;= the picture height: border / blanking lines, where sprites are still parsed).</summary>
    protected abstract void DrawLine(int n);

    /// <summary>After the frame: the lines in a TMS mode, which these renderers do not draw, are the stock chip's.</summary>
    public void FinishFrame()
    {
        int h = Math.Min(240, v.FrameHeight);
        for (int y = 0; y < h; y++) if (stockLine[y]) Array.Copy(v.Frame, y * 256, Picture, y * 256, 256);
    }

    protected int VR(int a) => v.Vram[a & 0x3FFF];
    protected int Height => v.ActiveHeight;
}

internal sealed class SmspRender : SmsHarvestRender
{
    // the line buffer: index B is screen x 0 (render_bg_sms writes from linebuf[-shift], which lies before internal_buffer in C)
    private const int B = 16;
    private readonly byte[] linebuf = new byte[B + 0x200];
    private static readonly byte[] lut = new byte[0x10000];
    private static readonly int[] smsExpand = { 0, (5 << 3) + (1 << 2), (15 << 3) + (1 << 2), (27 << 3) + (1 << 2) };
    private readonly (int yrange, int xpos, int attr)[] objectInfo = new (int, int, int)[64];
    private int objectCount;
    private readonly byte[] data = new byte[8];

    static SmspRender()
    {
        for (int bx = 0; bx < 0x100; bx++)
            for (int sx = 0; sx < 0x100; sx++)
            {
                int b = bx & 0x0F, bp = (bx & 0x20) != 0 ? 1 : 0, bf = bx & 0x7F, s = sx & 0x0F, sf = (sx & 0x0F) | 0x10 | 0x40, c;
                if ((bx & 0x40) != 0) c = bf;
                else if (bp != 0) c = b != 0 ? bf | 0x40 : (s != 0 ? sf : bf);
                else c = s != 0 ? sf : bf;
                lut[bx << 8 | sx] = (byte)c;
            }
    }

    public SmspRender(SmsVdp vdp) : base(vdp) { }

    private int Extended => Height == 240 ? 2 : Height == 224 ? 1 : 0;
    private int Backdrop => 0x10 | (v.Regs[7] & 0x0F);

    // the line counter value vc_table gives a frame line (the active lines count from 0; the last line of the frame reads 0xFF)
    private int Vc(int line) => line == v.TotalLines - 1 ? 0xFF : line & 0xFF;

    protected override void DrawLine(int line)
    {
        if (line == 0) ParseSatb(v.TotalLines - 1);   // the sprites of line 0 were found while the frame's last line ran
        if (line >= Height) { if (line < v.TotalLines - 1) ParseSatb(line); return; }
        if ((v.Regs[1] & 0x40) != 0)
        {
            RenderBg(line);
            RenderObj(line);
            if ((v.Regs[0] & 0x20) != 0 && !gg) Array.Fill(linebuf, (byte)Backdrop, B, 8);
        }
        else Array.Fill(linebuf, (byte)Backdrop, B, 256);
        ParseSatb(line);
        int row = line * 256;
        for (int x = 0; x < 256; x++) Picture[row + x] = Pixel(linebuf[B + x] & 0x1F);
    }

    // tile_get: the 8 pixels of a pattern line, flips applied
    private byte[] TileGet(int attr, int line)
    {
        int name = attr & 0x1FF;
        int y = (attr & 0x400) != 0 ? (line ^ 7) : line;
        int a = name << 5 | y << 2;
        int p0 = VR(a), p1 = VR(a + 1), p2 = VR(a + 2), p3 = VR(a + 3);
        for (int x = 0; x < 8; x++)
        {
            int bit = 7 - x;
            int c = ((p0 >> bit) & 1) | ((p1 >> bit) & 1) << 1 | ((p2 >> bit) & 1) << 2 | ((p3 >> bit) & 1) << 3;
            data[(attr & 0x200) != 0 ? (x ^ 7) : x] = (byte)c;
        }
        return data;
    }

    private void RenderBg(int line)
    {
        var reg = v.Regs;
        bool locked = false;
        int yscrollMask = Extended != 0 ? 256 : 224;
        int vLine = (line + v.VscrollLatched) % yscrollMask;
        int vRow = (vLine & 7) << 3;
        int hscroll = ((reg[0] & 0x40) != 0 && line < 0x10 && !gg) ? 0 : (0x100 - reg[8]);
        int column = 0;
        int ntab = Extended != 0 ? ((reg[2] << 10) & 0x3000) | 0x0700 : (reg[2] << 10) & 0x3800;
        int ntAddr = (ntab + ((vLine >> 3) << 6)) & ((sms1 && (reg[2] & 1) == 0) ? ~0x400 : 0xFFFF);
        int nt = ntAddr;
        int ntScroll = hscroll >> 3;
        int shift = hscroll & 7;
        int dst = B - shift;

        if (shift != 0)
        {
            for (int x = shift; x < 8; x++) linebuf[B - shift + x] = 0;
            column++;
        }
        for (; column < 32; column++)
        {
            if ((reg[0] & 0x80) != 0 && !locked && column >= 24)
            {
                locked = true;
                vRow = (line & 7) << 3;
                nt = ntAddr;   // SMS Plus GX keeps the scrolled row here (the jank)
            }
            int ai = nt + (((column + ntScroll) & 0x1F) << 1);
            int attr = VR(ai) | VR(ai + 1) << 8;
            int atex = ((attr >> 11) & 3) << 4;
            var t = TileGet(attr, vRow >> 3);
            for (int x = 0; x < 8; x++) linebuf[dst + (column << 3) + x] = (byte)(t[x] | atex);
        }
        if (shift != 0)
        {
            int p = B - shift + (column << 3);
            int ai = nt + (((column + ntScroll) & 0x1F) << 1);
            int attr = VR(ai) | VR(ai + 1) << 8;
            int a = (attr >> 7) & 0x30;
            var t = TileGet(attr, vRow >> 3);
            for (int x = 0; x < shift; x++) linebuf[p + x] = (byte)(t[x] | a);
        }
    }

    private void RenderObj(int line)
    {
        var reg = v.Regs;
        int width = 8;
        if ((reg[1] & 0x01) != 0) width *= 2;
        for (int i = 0; i < objectCount; i++)
        {
            int start = 0, end = width;
            int xp = objectInfo[i].xpos, yp = objectInfo[i].yrange, n = objectInfo[i].attr;
            if ((reg[0] & 0x08) != 0) xp -= 8;
            if ((reg[6] & 0x04) != 0) n |= 0x0100;
            if ((reg[1] & 0x02) != 0) n &= 0x01FE;
            int lb = B + xp;
            if (xp < 0) start = -xp;
            if (xp + width > 256) end = 256 - xp;
            if ((reg[1] & 0x01) != 0)
            {
                var c = TileGet(n, yp >> 1);
                for (int x = start; x < end; x += 2)
                {
                    int sp = c[x >> 1];
                    if (sp != 0) { int bg = linebuf[lb + x]; linebuf[lb + x] = linebuf[lb + x + 1] = lut[bg << 8 | sp]; }
                }
            }
            else
            {
                var c = TileGet(n, yp);
                for (int x = start; x < end; x++)
                {
                    int sp = c[x];
                    if (sp != 0) { int bg = linebuf[lb + x]; linebuf[lb + x] = lut[bg << 8 | sp]; }
                }
            }
        }
    }

    private void ParseSatb(int line)
    {
        var reg = v.Regs;
        int satb = (reg[5] << 7) & 0x3F00;
        int vc = Vc(line);
        int height = 8;
        if ((reg[1] & 0x02) != 0) height <<= 1;
        if ((reg[1] & 0x01) != 0) height <<= 1;
        objectCount = 0;
        for (int i = 0; i < 64; i++)
        {
            int yp = VR(satb + i);
            if (Extended == 0 && yp == 208) return;
            if (yp > 240) yp -= 256;
            yp = vc - yp;
            if (yp >= 0 && yp < height)
            {
                if (objectCount == 8) return;   // option.spritelimit on (the overflow flag stays the stock chip's)
                objectInfo[objectCount] = (yp, VR(satb + 0x80 + (i << 1)), VR(satb + 0x81 + (i << 1)));
                objectCount++;
            }
        }
    }

    // palette_sync + retro-go's MAKE_PIXEL (RGB565), shown back as 8 bits a channel
    private uint Pixel(int index)
    {
        int r, g, b;
        if (gg)
        {
            int lo = v.CramByte(index << 1), hi = v.CramByte(index << 1 | 1);
            r = lo & 0x0F; g = (lo >> 4) & 0x0F; b = hi & 0x0F;
            r = r << 4 | r; g = g << 4 | g; b = b << 4 | b;
        }
        else
        {
            int c = v.CramByte(index);
            r = smsExpand[c & 3]; g = smsExpand[(c >> 2) & 3]; b = smsExpand[(c >> 4) & 3];
        }
        int r5 = r >> 3, g6 = g >> 2, b5 = b >> 3;
        return 0xFF000000u | (uint)((r5 << 3 | r5 >> 2) << 16 | (g6 << 2 | g6 >> 4) << 8 | (b5 << 3 | b5 >> 2));
    }
}
