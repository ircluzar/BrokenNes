// GpgxM4Render - Genesis Plus GX's Mode 4 (Master System / Game Gear) line renderer, ported from ekeeke/Genesis-Plus-GX @cbf8f4e
// (the parent of 42ea79c, 2013-10-20), core/vdp_render.c: render_line (the Master System branch), render_bg_m4, render_obj_m4 with
// DRAW_SPRITE_TILE_ACCURATE / _2X, parse_satb_m4, make_lut_bgobj_m4, make_bp_lut + update_bg_pattern_cache_m4 (read straight from
// VRAM here, which is what the cache holds), palette_init's Mode 4 table and color_update_m4. The Mode 5 half of the same file is
// md/GpgxRender.cs (the Genesis picture core "GPGX"); this is the Mode 4 half that port skipped, offered on the Master System and the
// Game Gear under the same id.
//
//   Genesis Plus GX, (C) 1998-2003 Charles Mac Donald, (C) 2007-2013 Eke-Eke. Non-commercial licence (vdp_render.c header).
//   See THIRD_PARTY_NOTICES.md (PPU harvest) and sega/THIRD_PARTY_NOTICES.md.
//
// PPU harvest climb (docs/projects/ppu-harvest-catalog.md, #9 Mode 4 path): an art instrument, ported FAITHFULLY with its flaws. The
// stock VDP (SmsVdp) keeps the timing, the memories, the status flags and interrupts; this draws each line whole as the stock chip
// starts it (SmsVdp.LineObserver).
//
// Characteristic jank, all of it kept on purpose:
//  * Whole line at once at line start: mid-line palette / scroll / register writes vanish.
//  * Colour levels: 2-bit CRAM expands to 4 bits ((c << 2) | c), so 00 55 AA FF instead of the chip's 00 52 AD FF.
//  * The sprite end marker is Y = picture height + 16: 208 in 192-line mode, but also Y = 240 in the 224-line mode (where the
//    real chip has no marker), which cuts the sprite list there.
//  * A zoomed sprite clipped on the left edge skips source pixels one per screen pixel, so its visible part is shifted.
//  * The SMS1 (315-5124) quirks are modelled the GPGX 2013 way: only the first four sprites zoom, the register 2 bit 0 / register 5
//    and 6 mask bits apply.
//  * Not ported: the TMS9918 modes 0-3 (render_bg_m0..m3, which need the TMS pattern caches); lines in those modes are left to the
//    stock chip and the menu label says so. The collision and overflow flags stay the stock chip's.
using System;

namespace NesEmulator.Sega;

internal sealed class GpgxM4Render : SmsHarvestRender
{
    private readonly byte[] linebuf = new byte[0x20 + 0x200 + 0x20];
    private static readonly byte[] lut5 = new byte[0x10000];
    private readonly (int ypos, int xpos, int attr)[] objectInfo = new (int, int, int)[8];
    private int objectCount;

    static GpgxM4Render()
    {
        for (int bx = 0; bx < 0x100; bx++)
            for (int sx = 0; sx < 0x100; sx++)
            {
                int bf = bx & 0x3F, bs = bx & 0x80, bp = bx & 0x20, b = bx & 0x0F, s = sx & 0x0F, sf = s | 0x10, c;
                if (s == 0 || bs != 0) c = bx;
                else c = (bp != 0 ? (b != 0 ? bf : sf) : sf) | 0x80;
                lut5[bx << 8 | sx] = (byte)c;
            }
    }

    public GpgxM4Render(SmsVdp vdp) : base(vdp) { }

    private bool AboveSms => !sms1;   // system_hw > SYSTEM_SMS: the SMS2 and Game Gear VDPs

    protected override void DrawLine(int line)
    {
        var reg = v.Regs;
        if (line == 0) ParseSatb(-1);   // the sprites of line 0 were parsed as the frame's last line ended (line -1)
        if (line >= Height) return;
        const int width = 256;
        if ((reg[1] & 0x40) != 0)
        {
            RenderBg(line, width);
            RenderObj(width);
            if ((reg[0] & 0x20) != 0) Array.Fill(linebuf, (byte)0x40, 0x20, 8);
            if (line < Height - 1) ParseSatb(line);
        }
        else
        {
            ParseSatb(line);   // sprites are still parsed when the display is off
            Array.Fill(linebuf, (byte)0x40, 0x20, width);
        }
        int row = line * 256;
        for (int x = 0; x < 256; x++) { int c = linebuf[0x20 + x]; Picture[row + x] = c == 0x40 ? Pixel(0x10 | (reg[7] & 0x0F)) : Pixel(c & 0x1F); }
    }

    // the cached pattern line (bg_pattern_cache[((attr & 0x7FF) << 6) | (row << 3) | x]): flips in name bits 9 and 10
    private int Cache(int index, int x)
    {
        int name = (index >> 6) & 0x7FF;
        bool hflip = (name & 0x200) != 0, vflip = (name & 0x400) != 0;
        int tile = name & 0x1FF;
        int row = (index >> 3) & 7;
        if (vflip) row ^= 7;
        int px = hflip ? x ^ 7 : x;
        int a = tile << 5 | row << 2;
        int bit = 7 - px;
        return ((VR(a) >> bit) & 1) | ((VR(a + 1) >> bit) & 1) << 1 | ((VR(a + 2) >> bit) & 1) << 2 | ((VR(a + 3) >> bit) & 1) << 3;
    }

    private void RenderBg(int line, int width)
    {
        var reg = v.Regs;
        int index = ((reg[0] & 0x40) != 0 && line < 0x10) ? 0x100 : reg[8];
        int shift = index & 7;
        int dst = 0x20 + shift;
        int vLine = line + v.VscrollLatched;
        int ntMask = (~0x3C00 ^ (reg[2] << 10)) & 0xFFFF;
        if (AboveSms) ntMask |= 0x400;
        bool ext = Height > 192;
        int nt;
        if (ext) { vLine %= 256; nt = (0x3700 & ntMask) + ((vLine >> 3) << 6); }
        else { vLine %= 224; nt = (0x3800 + ((vLine >> 3) << 6)) & ntMask; }
        vLine = (vLine & 7) << 3;
        index = (0x100 - index) >> 3;
        if (shift != 0) { Array.Fill(linebuf, (byte)0, 0x20, shift); index++; }
        width >>= 3;
        for (int column = 0; column < width; column++, index++)
        {
            if (column == 24 && (reg[0] & 0x80) != 0)
            {
                nt = ext ? (0x3700 & ntMask) + ((line >> 3) << 6) : (0x3800 + ((line >> 3) << 6)) & ntMask;
                vLine = (line & 7) << 3;
            }
            int ai = nt + ((index % width) << 1);
            int attr = VR(ai) | VR(ai + 1) << 8;
            int atex = ((attr >> 11) & 3) << 4;
            int src = ((attr & 0x7FF) << 6) | vLine;
            for (int x = 0; x < 8; x++) linebuf[dst++] = (byte)(Cache(src, x) | atex);
        }
    }

    private void RenderObj(int maxWidth)
    {
        var reg = v.Regs;
        int width = 8;
        int sgMask = (~0x1C0 ^ (reg[6] << 6)) & ~((reg[1] & 0x02) >> 1);
        width <<= reg[1] & 0x01;
        if (AboveSms) sgMask |= 0xC0;
        for (int count = 0; count < objectCount; count++)
        {
            if (count == 4 && sms1) width = 8;   // only the first four sprites can be zoomed on the 315-5124
            int temp = (objectInfo[count].attr | 0x100) & sgMask;
            int src = (temp << 6) | (objectInfo[count].ypos << 3);
            int xpos = objectInfo[count].xpos;
            xpos -= reg[0] & 0x08;
            int end, skip = 0;
            if (xpos < 0) { skip = -xpos; end = xpos + width; xpos = 0; }
            else if (xpos + width > maxWidth) end = maxWidth - xpos;
            else end = width;
            int lb = 0x20 + xpos;
            int s = src + skip;   // src = src - xpos
            if (width > 8)
            {
                for (int i = 0; i < end; i += 2)
                {
                    int t = CachePixel(s++);
                    if ((t & 0x0F) != 0) { linebuf[lb + i] = lut5[linebuf[lb + i] << 8 | t]; linebuf[lb + i + 1] = lut5[linebuf[lb + i + 1] << 8 | t]; }
                }
            }
            else
            {
                for (int i = 0; i < end; i++)
                {
                    int t = CachePixel(s++);
                    if ((t & 0x0F) != 0) linebuf[lb + i] = lut5[linebuf[lb + i] << 8 | t];
                }
            }
        }
    }

    // a byte of the pattern cache by its flat index (pattern << 6 | row << 3 | x); walking past x = 7 runs into the next row, as in C
    private int CachePixel(int flat) => Cache(flat & ~7, flat & 7);

    private void ParseSatb(int line)
    {
        var reg = v.Regs;
        int count = 0;
        int height = 8 + ((reg[1] & 0x02) << 2);
        int stMask = (~0x3F80 ^ (reg[5] << 7)) & 0xFFFF;
        if (AboveSms) stMask |= 0x80;
        int st = stMask & 0x3F00;
        int i = 0;
        do
        {
            int ypos = VR(st + i);
            if (ypos == Height + 16) break;
            if (ypos >= 240) ypos -= 256;
            ypos = line - ypos;
            ypos >>= reg[1] & 0x01;
            if (ypos >= 0 && ypos < height)
            {
                if (count == 8) break;   // the overflow flag stays the stock chip's
                objectInfo[count] = (ypos, VR(st + ((0x80 + (i << 1)) & stMask)), VR(st + ((0x81 + (i << 1)) & stMask)));
                count++;
            }
        }
        while (++i < 64);
        objectCount = count;
    }

    // palette_init (pixel_lut_m4) + color_update_m4; 4 bits a channel shown as 8 (x 17)
    private uint Pixel(int index)
    {
        int r, g, b;
        if (gg)
        {
            int d = v.CramByte(index << 1) | v.CramByte(index << 1 | 1) << 8;
            r = d & 0x0F; g = (d >> 4) & 0x0F; b = (d >> 8) & 0x0F;
        }
        else
        {
            int c = v.CramByte(index);
            r = c & 3; g = (c >> 2) & 3; b = (c >> 4) & 3;
            r = r << 2 | r; g = g << 2 | g; b = b << 2 | b;
        }
        return 0xFF000000u | (uint)(r * 17 << 16 | g * 17 << 8 | b * 17);
    }
}
