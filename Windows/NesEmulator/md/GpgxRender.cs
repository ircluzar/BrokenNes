// GpgxRender - Genesis Plus GX's Mode 5 line renderer, ported from ekeeke/Genesis-Plus-GX @cbf8f4e (the parent of 42ea79c, 2013-10-20):
// core/vdp_render.c, the default (non-ALT_RENDERER) path: render_line, render_bg_m5, render_bg_m5_vs, render_obj_m5, render_obj_m5_ste,
// parse_satb_m5, merge and the five layer tables (make_lut_bg, make_lut_bg_ste, make_lut_obj, make_lut_bgobj, make_lut_bgobj_ste),
// make_name_lut, window_clip, color_update_m5 / palette_init (MAKE_PIXEL at 4 bits a channel).
//
//   Genesis Plus GX, (C) 1998-2003 Charles Mac Donald, (C) 2007-2013 Eke-Eke. Non-commercial licence (vdp_render.c header).
//   See THIRD_PARTY_NOTICES.md (PPU harvest) and sega/THIRD_PARTY_NOTICES.md.
//
// PPU harvest climb (docs/projects/ppu-harvest-catalog.md, #8): an art instrument, ported FAITHFULLY with its flaws. It is the Genesis
// picture core "GPGX": the stock VDP (MdVdp) keeps the console's timing, its memories, status flags and interrupts; this renderer draws
// each line, whole, as the stock chip starts it (MdVdp.LineObserver), from the live registers, VRAM, VSRAM, CRAM and sprite cache.
//
// Characteristic jank, all of it kept on purpose (the 2013 pixel budget rework, 42ea79c, and later fixes are what it predates):
//  * Whole line at once at line start: mid-line CRAM/register changes (Mickey Mania's and Overdrive's raster tricks) vanish.
//  * Sprite limit by WIDTH SUM, off-screen sprites included: 256 or 320 pixels of sprite width a line, the last sprite cut to the
//    remainder; reaching it masks the next line's sprites until one with X > 0 (spr_ovr) - the 2013 model, not the per-dot budget.
//  * Sprites for line L are found while line L-1 ends (parse_satb), attributes and X read from VRAM then: changes in between are missed.
//  * X = 0 sprite masking needs an earlier sprite with X > 0 on the line, or a full line above.
//  * Column vertical scroll: the partly shown left column scrolls by 0 in H32 and by VSRAM[38] AND VSRAM[39] in H40.
//  * The "window bug": plane A's partial left column next to a left window takes the next column's tile.
//  * No interlace-mode-2 renderer (drawn as normal lines); the collision and overflow flags stay the stock chip's.
using System;

namespace NesEmulator.Sega;

internal sealed class GpgxRender
{
    private readonly MdVdp v;
    private readonly byte[][] linebuf = { new byte[0x200], new byte[0x200] };
    private static readonly byte[][] lut = new byte[5][];
    private static readonly sbyte[] nameLut = new sbyte[0x400];
    private readonly (int attr, int xpos, int ypos, int size)[] obj = new (int, int, int, int)[20];
    private int objectCount; private bool sprOvr; private int parsedFor = -2;
    public readonly uint[] Picture = new uint[320 * 240];
    public int Width = 320, Height = 224;

    static GpgxRender()
    {
        for (int i = 0; i < 5; i++) lut[i] = new byte[0x10000];
        for (int bx = 0; bx < 0x100; bx++)
            for (int ax = 0; ax < 0x100; ax++)
            {
                int index = bx << 8 | ax;
                lut[0][index] = LutBg(bx, ax); lut[1][index] = LutBgObj(bx, ax); lut[2][index] = LutBgSte(bx, ax);
                lut[3][index] = LutObj(bx, ax); lut[4][index] = LutBgObjSte(bx, ax);
            }
        for (int i = 0; i < 0x400; i++)
        {
            int vcol = i & 3, vrow = (i >> 2) & 3, height = (i >> 4) & 3, width = (i >> 6) & 3, flipx = (i >> 8) & 1, flipy = (i >> 9) & 1;
            if (vrow > height || vcol > width) nameLut[i] = -1;
            else { if (flipx != 0) vcol = width - vcol; if (flipy != 0) vrow = height - vrow; nameLut[i] = (sbyte)(vrow + vcol * (height + 1)); }
        }
    }

    static byte LutBg(int bx, int ax)
    {
        int bf = bx & 0x7F, bp = bx & 0x40, b = bx & 0x0F, af = ax & 0x7F, ap = ax & 0x40, a = ax & 0x0F;
        int c = ap != 0 ? (a != 0 ? af : bf) : (bp != 0 ? (b != 0 ? bf : af) : (a != 0 ? af : bf));
        if ((c & 0x0F) == 0) c &= 0x80;
        return (byte)c;
    }
    static byte LutBgSte(int bx, int ax)
    {
        int bf = bx & 0x7F, bp = bx & 0x40, b = bx & 0x0F, af = ax & 0x7F, ap = ax & 0x40, a = ax & 0x0F;
        int c = ap != 0 ? (a != 0 ? af : bf) : (bp != 0 ? (b != 0 ? bf : af) : (a != 0 ? af : bf));
        c |= (ap | bp) << 1;
        if ((c & 0x0F) == 0) c &= 0x80;
        return (byte)c;
    }
    static byte LutObj(int bx, int sx)
    {
        int bf = bx & 0x7F, bs = bx & 0x80, sf = sx & 0x7F;
        if ((sx & 0x0F) == 0) return (byte)bx;
        int c = bs != 0 ? bf : sf;
        if ((c & 0x0F) == 0) c &= 0xC0;
        return (byte)(c | 0x80);
    }
    static byte LutBgObj(int bx, int sx)
    {
        int bf = bx & 0x3F, bs = bx & 0x80, bp = bx & 0x40, b = bx & 0x0F, sf = sx & 0x3F, sp = sx & 0x40, s = sx & 0x0F;
        if (s == 0) return (byte)bx;
        if (bs != 0) return (byte)bx;
        int c = sp != 0 ? sf : (bp != 0 ? (b != 0 ? bf : sf) : sf);
        if ((c & 0x0F) == 0) c &= 0x80;
        return (byte)(c | 0x80);
    }
    static byte LutBgObjSte(int bx, int sx)
    {
        int bf = bx & 0x3F, bp = bx & 0x40, b = bx & 0x0F, bi = (bx & 0x80) >> 1, sf = sx & 0x3F, sp = sx & 0x40, s = sx & 0x0F, si = sp | bi;
        int Spr() => (sf & 0x3E) == 0x3E ? ((sf & 1) != 0 ? bf : ((bx & 0x80) != 0 ? bf | 0x80 : bf | 0x40)) : (sf == 0x0E || sf == 0x1E || sf == 0x2E ? sf | 0x40 : sf | si);
        int c;
        if (sp != 0) c = s != 0 ? Spr() : bf | bi;
        else if (bp != 0) c = b != 0 ? bf | bi : (s != 0 ? Spr() : bf | bi);
        else c = s != 0 ? Spr() : bf | bi;
        if ((c & 0x0F) == 0) c &= 0xC0;
        return (byte)c;
    }

    public GpgxRender(MdVdp vdp) { v = vdp; v.LineObserver += RenderLine; }
    public void Detach() => v.LineObserver -= RenderLine;
    /// <summary>After a state load: the sprite list and masking of the old timeline are dropped (re-parsed at the next line).</summary>
    public void Reset() { objectCount = 0; sprOvr = false; parsedFor = -2; }

    private int W16(int a) => v.Vram[a & 0xFFFF] << 8 | v.Vram[(a + 1) & 0xFFFF];
    private byte[] Reg => v.Reg;

    // DRAW_COLUMN: the two cells of a name-table pair, 16 pixels of 0Pppcccc.
    private void DrawCell(byte[] dst, int at, int attr, int row)
    {
        int atex = ((attr >> 15) & 1) << 6 | ((attr >> 13) & 3) << 4;
        int name = attr & 0x7FF; bool hf = (attr & 0x800) != 0, vf = (attr & 0x1000) != 0;
        int r = vf ? 7 - row : row;
        int a = name * 32 + r * 4;
        for (int x = 0; x < 8; x++)
        {
            int px = hf ? 7 - x : x;
            int c = (v.Vram[(a + (px >> 1)) & 0xFFFF] >> ((px & 1) != 0 ? 0 : 4)) & 0x0F;
            int i = at + x; if (i >= 0 && i < dst.Length) dst[i] = (byte)(c | atex);
        }
    }
    private void DrawColumn(byte[] dst, int at, int ntPair, int row) { DrawCell(dst, at, W16(ntPair), row); DrawCell(dst, at + 8, W16(ntPair + 2), row); }

    private void RenderLine(int line)
    {
        bool h40 = (Reg[12] & 1) != 0;
        int width = h40 ? 320 : 256;
        Width = width; Height = v.PictureHeight;
        if (parsedFor != line - 1) ParseSatb(line - 1);   // first line of the frame or after a load: the list of "the line before"
        if ((Reg[1] & 0x40) != 0)
        {
            if ((Reg[11] & 4) != 0) RenderBgVs(line, width); else RenderBg(line, width);
            if ((Reg[12] & 8) != 0) RenderObjSte(width); else RenderObj(width);
            if ((Reg[0] & 0x20) != 0) Array.Fill(linebuf[0], (byte)0x40, 0x20, 8);
            if (line < 239) ParseSatb(line);
        }
        else Array.Fill(linebuf[0], (byte)0x40, 0x20, width);
        // remap_line through color_update_m5's pixel table
        bool ste = (Reg[12] & 8) != 0;
        int row = line * width;
        if (line >= 240) return;
        for (int x = 0; x < width; x++)
        {
            int p = linebuf[0][0x20 + x];
            int data = v.Cram[p & 0x3F];
            data = ((data >> 1) & 7) | ((data >> 5) & 7) << 3 | ((data >> 9) & 7) << 6;   // CRAM to BBBGGGRRR
            if ((Reg[0] & 0x04) == 0) data &= 0x49;
            int mode = ste ? p >> 6 : 1;   // 0 shadow, 1 normal, 2 highlight
            int R = data & 7, G = (data >> 3) & 7, B = (data >> 6) & 7;
            (R, G, B) = mode switch { 0 => (R, G, B), 2 => (R + 7, G + 7, B + 7), _ => (R << 1, G << 1, B << 1) };
            Picture[row + x] = 0xFF000000u | (uint)(R * 17 << 16 | G * 17 << 8 | B * 17);
        }
    }

    private (int shift, int colMask, int rowMask) Playfield()
    {
        int r = Reg[16];
        int shift = (r & 3) switch { 1 => 7, 3 => 8, _ => 6 };
        int colMask = (r & 3) switch { 1 => 0x1F, 3 => 0x3F, _ => 0x0F };
        int rowMask = ((r >> 4) & 3) switch { 1 => 0x1FF, 3 => 0x3FF, _ => 0x0FF };
        return (shift, colMask, rowMask);
    }

    // window_clip(reg[17], reg[12] & 1)
    private (bool en, int left, int right)[] Clip()
    {
        var clip = new (bool en, int left, int right)[2];
        int data = Reg[17], hp = data & 0x1F, hf = (data >> 7) & 1, a = hf, w = hf ^ 1, sw = 16 + ((Reg[12] & 1) << 2);
        if (hp != 0)
        {
            if (hp > sw) { clip[w] = (true, 0, sw); clip[a].en = false; }
            else { clip[w].left = 0; clip[a].right = sw; clip[a].left = clip[w].right = hp; clip[0].en = clip[1].en = true; }
        }
        else { clip[a] = (true, 0, sw); clip[w].en = false; }
        return clip;
    }

    private void RenderBg(int line, int width) => RenderBgCommon(line, width, false);
    private void RenderBgVs(int line, int width) => RenderBgCommon(line, width, true);

    private void RenderBgCommon(int line, int width, bool vs)
    {
        var (pfShift, colMask, rowMask) = Playfield();
        int hscrollMask = (Reg[11] & 3) switch { 0 => 0, 1 => 7, 2 => 0xF8, _ => 0xFF };
        int hscb = (Reg[13] & 0x3F) << 10;
        int hA = W16(hscb + ((line & hscrollMask) << 2)), hB = W16(hscb + ((line & hscrollMask) << 2) + 2);
        int ntab = (Reg[2] & 0x38) << 10, ntbb = (Reg[4] & 7) << 13, ntwb = (Reg[3] & ((Reg[12] & 1) != 0 ? 0x3C : 0x3E)) << 10;
        int ysA = v.Vsram[0], ysB = v.Vsram[1];
        int leftVs = vs ? ((Reg[12] & 1) != 0 ? v.Vsram[38] & v.Vsram[39] : 0) : 0;   // vs[19] & (vs[19] >> 16)
        int end = width >> 4;
        var b0 = linebuf[0]; var b1 = linebuf[1];

        // Plane B -> linebuf[0]
        int shift = hB & 0x0F, index = colMask + 1 - ((hB >> 4) & colMask);
        int VLine(int ys) => (line + ys) & rowMask;
        int NtRow(int baseAddr, int vl) => baseAddr + (((vl >> 3) << pfShift) & 0x1FC0);
        if (shift != 0)
        {
            int vl = VLine(vs ? (leftVs >> 0) : ysB);
            if (vs) vl = VLine((Reg[12] & 1) != 0 ? leftVs : 0);
            DrawColumn(b0, 0x10 + shift, NtRow(ntbb, vl) + (((index - 1) & colMask) << 2), vl & 7);
        }
        int dst = shift != 0 ? 0x10 + shift + 16 : 0x20;
        for (int column = 0; column < end; column++, index++, dst += 16)
        {
            int vl = VLine(vs ? v.Vsram[(column * 2 + 1) & 0x3F] : ysB);
            DrawColumn(b0, dst, NtRow(ntbb, vl) + ((index & colMask) << 2), vl & 7);
        }

        // Plane A and the window -> linebuf[1]
        int wa = (Reg[18] & 0x1F) << 3, ww = (Reg[18] >> 7) & 1;
        var clip = Clip();
        bool aOn, wOn; int start = 0; end = width >> 4;
        if (ww == (line >= wa ? 1 : 0)) { aOn = false; wOn = true; }
        else { aOn = clip[0].en; wOn = clip[1].en; }
        if (aOn)
        {
            start = clip[0].left; end = clip[0].right;
            shift = hA & 0x0F; index = colMask + start + 1 - ((hA >> 4) & colMask);
            if (shift != 0)
            {
                int vl = VLine(vs ? ((Reg[12] & 1) != 0 ? leftVs : 0) : ysA);
                int nt = NtRow(ntab, vl) + ((start != 0 ? index & colMask : (index - 1) & colMask) << 2);   // the window bug
                DrawColumn(b1, 0x10 + shift + (start << 4), nt, vl & 7);
            }
            dst = shift != 0 ? 0x10 + shift + (start << 4) + 16 : 0x20 + (start << 4);
            for (int column = start; column < end; column++, index++, dst += 16)
            {
                int vl = VLine(vs ? v.Vsram[(column * 2) & 0x3F] : ysA);
                DrawColumn(b1, dst, NtRow(ntab, vl) + ((index & colMask) << 2), vl & 7);
            }
            start = clip[1].left; end = clip[1].right;
        }
        if (wOn)
        {
            int nt = ntwb | ((line >> 3) << (6 + (Reg[12] & 1)));
            dst = 0x20 + (start << 4);
            for (int column = start; column < end; column++, dst += 16) DrawColumn(b1, dst, nt + (column << 2), line & 7);
        }
    }

    private void Merge(byte[] srca, byte[] srcb, byte[] dst, byte[] table, int width)
    {
        for (int i = 0x20; i < 0x20 + width; i++) dst[i] = table[srcb[i] << 8 | srca[i]];
    }

    private void RenderObj(int maxWidth) => RenderObjCommon(maxWidth, false);
    private void RenderObjSte(int maxWidth) => RenderObjCommon(maxWidth, true);

    private void RenderObjCommon(int maxWidth, bool ste)
    {
        Merge(linebuf[1], linebuf[0], linebuf[0], ste ? lut[2] : lut[0], maxWidth);
        if (ste) Array.Clear(linebuf[1], 0, Math.Min(linebuf[1].Length, maxWidth + 0x40));
        var target = ste ? linebuf[1] : linebuf[0];
        var table = ste ? lut[3] : lut[1];
        int pixelcount = 0; bool masked = false;
        for (int count = 0; count < objectCount; count++)
        {
            int xpos = obj[count].xpos;
            if (xpos != 0) sprOvr = true;
            else if (sprOvr) masked = true;
            xpos -= 0x80;
            int temp = obj[count].size;
            int width = 8 + ((temp & 0x0C) << 1);
            pixelcount += width;
            if (xpos + width > 0 && xpos < maxWidth && !masked)
            {
                int attr = obj[count].attr, vline = obj[count].ypos;
                int atex = (attr >> 9) & 0x70;
                int name = attr & 0x7FF;
                int flips = attr & 0x1800;
                int s = ((flips >> 3) & 0x300) | (temp << 4) | ((vline & 0x18) >> 1);
                int lb = 0x20 + xpos;
                if (pixelcount > maxWidth) width = width - pixelcount + maxWidth;
                width >>= 3;
                int row = vline & 7;
                for (int column = 0; column < width; column++, lb += 8)
                {
                    int t = (name + nameLut[s + column]) & 0x7FF;
                    bool hf = (flips & 0x800) != 0, vf = (flips & 0x1000) != 0;
                    int a = t * 32 + (vf ? 7 - row : row) * 4;
                    for (int i = 0; i < 8; i++)
                    {
                        int px = hf ? 7 - i : i;
                        int c = (v.Vram[(a + (px >> 1)) & 0xFFFF] >> ((px & 1) != 0 ? 0 : 4)) & 0x0F;
                        if (c == 0) continue;
                        int k = lb + i; if (k < 0 || k >= target.Length) continue;
                        target[k] = table[(c | atex) | target[k] << 8];
                    }
                }
            }
            if (pixelcount >= maxWidth)
            {
                sprOvr = true;
                if (ste) Merge(linebuf[1], linebuf[0], linebuf[0], lut[4], maxWidth);
                return;
            }
        }
        sprOvr = false;
        if (ste) Merge(linebuf[1], linebuf[0], linebuf[0], lut[4], maxWidth);
    }

    // parse_satb_m5: y/size/link from the VDP's internal sprite cache, attribute and X from VRAM.
    private void ParseSatb(int line)
    {
        parsedFor = line;
        bool h40 = (Reg[12] & 1) != 0;
        int max = 16 + ((Reg[12] & 1) << 2), total = max << 2;
        int satb = (Reg[5] & (h40 ? 0x7E : 0x7F)) << 9;
        int link = 0, count = 0;
        line += 0x81;
        do
        {
            int n = link >> 2;
            var sc = v.SpriteCache;
            int ypos = (sc[n * 4] << 8 | sc[n * 4 + 1]) & 0x1FF;
            int size = sc[n * 4 + 2];
            int height = 8 + ((size & 3) << 3);
            ypos = line - ypos;
            if (ypos >= 0 && ypos < height)
            {
                if (count == max) break;
                obj[count] = (W16(satb + n * 8 + 4), W16(satb + n * 8 + 6) & 0x1FF, ypos, size & 0x0F);
                count++;
            }
            link = (sc[n * 4 + 3] & 0x7F) << 2;
            if (link == 0 || (link >> 2) >= 80) break;
        }
        while (--total > 0);
        objectCount = count;
    }
}
