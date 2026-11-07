// GnuboyDisplay - gnuboy 1.0.3's picture code, ported from rofl0r/gnuboy @29076d4 ("initial 1.0.3 import", 2012-06-23):
// lcd.c lcd_begin, lcd_refreshline, tilebuf, bg_scan(_color/_pri), wnd_scan(_color/_pri), spr_enum, spr_scan, updatepalette,
// pal_write_dmg and its default palette (patpix decoded on the fly instead of cached: the same pixels).
//
//   gnuboy, (C) Laguna and Gilgamesh. GNU General Public License, version 2. See THIRD_PARTY_NOTICES.md (PPU harvest)
//   and gb/THIRD_PARTY_NOTICES.md.
//
// PPU harvest climb (docs/projects/ppu-harvest-catalog.md, #6): an art instrument, ported FAITHFULLY with its flaws. It is
// the Game Boy picture core "GNUB": the stock chip (PPU_GB) keeps the timing and this renderer draws beside it
// (PPU_GB.TickObserver). gnuboy draws a whole line when mode 3 begins (dot 80 here).
//
// Characteristic jank, all of it kept on purpose (the window ones were fixed only in 2021, 758addc):
//  * WY is latched once a frame (lcd_begin); the window's line is LY - WY, not the window's own line counter, so a
//    window switched off and on mid-frame jumps ahead instead of resuming.
//  * A window with WY > 143 or WX outside -7..159 vanishes for the frame; WX = 166 hides it.
//  * LCDC bit 0 is ignored: the DMG background always draws.
//  * The 10 sprites a line are the first 10 in OAM order, THEN sorted by X (DMG); behind-background sprites test the
//    colour index, not the shade; the CGB attribute priority is used only when some tile of the line's row has it.
//  * Whole line at once: mid-line register writes vanish.
//  * gnuboy's default DMG palette (#E0D098 .. #3C3C2C, a pale blue-grey) instead of the console's look, kept for
//    background, window and both sprite palettes, each through gnuboy's 15-bit round trip.
namespace NesEmulator.Gb;

internal sealed class GnuboyDisplay : IGbForeignView
{
    public const int W = 160, H = 144;
    public uint[] Screen { get; } = new uint[W * H];
    private readonly PPU_GB gb;
    private int latchedWy; private bool latched;
    private int drawnLy = -1;
    private readonly byte[] buf = new byte[256], bgdup = new byte[256], pri = new byte[256];
    private readonly int[] bgTiles = new int[64], wndTiles = new int[64];
    private readonly (int x, int pat, int v, byte pal, bool pri)[] vs = new (int, int, int, byte, bool)[10], ts = new (int, int, int, byte, bool)[10];
    private static readonly int[] DefPal = { 0x98d0e0, 0x68a0b0, 0x60707C, 0x2C3C3C };

    public GnuboyDisplay(PPU_GB ppu) { gb = ppu; ppu.TickObserver += Run; System.Array.Fill(Screen, 0xFFFFFFFFu); }
    public void Detach() => gb.TickObserver -= Run;

    private bool Cgb => gb.Model == GbModel.Cgb && !gb.CompatMode;

    private void Run()
    {
        if (!gb.LcdOn) { drawnLy = -1; latched = false; return; }
        int ly = gb.LY;
        if (ly >= H) { drawnLy = -1; latched = false; return; }
        if (!latched) { latched = true; latchedWy = gb.Wy; }   // lcd_begin
        if (gb.Dot >= 80 && drawnLy != ly) { drawnLy = ly; RefreshLine(ly); }
    }

    // patpix[p][v][x]: p = tile (0-1023, bank 1 from 512) | 0x400 x-flip | 0x800 y-flip.
    private int Pat(int p, int v, int x)
    {
        var vram = gb.Vram;
        int a = ((p & 0x3FF) << 4) + (((p & 0x800) != 0 ? 7 - v : v) << 1);
        int bit = (p & 0x400) != 0 ? x : 7 - x;
        return ((vram[a & 0x3FFF] >> bit) & 1) | (((vram[(a + 1) & 0x3FFF] >> bit) & 1) << 1);
    }

    private void RefreshLine(int L)
    {
        var vram = gb.Vram; byte lcdc = gb.Lcdc;
        bool cgb = Cgb;
        int X = gb.Scx, Y = (gb.Scy + L) & 0xFF;
        int S = X >> 3, T = Y >> 3, U = X & 7, V = Y & 7;
        int WX = gb.Wx - 7, WY = latchedWy;
        if (WY > L || WY < 0 || WY > 143 || WX < -7 || WX > 159 || (lcdc & 0x20) == 0) WX = 160;
        int WT = (L - WY) >> 3, WV = (L - WY) & 7;
        System.Array.Clear(buf); System.Array.Clear(pri);

        int ns = SprEnum(L, lcdc, cgb);

        // tilebuf
        int bgBase = ((lcdc & 0x08) != 0 ? 0x1C00 : 0x1800) + (T << 5);
        int cnt = ((WX + 7) >> 3) + 1;
        for (int i = 0, col = S; i < cnt; i++, col = (col + 1) & 31)
        {
            int t = vram[bgBase + col], at = cgb ? vram[0x2000 + bgBase + col] : 0;
            int p = (lcdc & 0x10) != 0 ? t : 256 + (sbyte)t;
            if (cgb) p |= ((at & 0x08) << 6) | ((at & 0x60) << 5);
            bgTiles[i * 2] = p; bgTiles[i * 2 + 1] = (at & 7) << 2;
        }
        int wBase = ((lcdc & 0x40) != 0 ? 0x1C00 : 0x1800) + ((WT << 5) & 0x3FF);
        if (WX < 160)
        {
            int wc = ((160 - WX) >> 3) + 1;
            for (int i = 0; i < wc && i < 32; i++)
            {
                int t = vram[wBase + i], at = cgb ? vram[0x2000 + wBase + i] : 0;
                int p = (lcdc & 0x10) != 0 ? t : 256 + (sbyte)t;
                if (cgb) p |= ((at & 0x08) << 6) | ((at & 0x60) << 5);
                wndTiles[i * 2] = p; wndTiles[i * 2 + 1] = (at & 7) << 2;
            }
        }

        // bg_scan(_color)
        if (WX > 0)
        {
            int d = 0, ti = 0, c = WX;
            int n = System.Math.Min(8 - U, c);
            for (int k = 0; k < n; k++) buf[d++] = (byte)(Pat(bgTiles[0], V, U + k) | bgTiles[1]);
            c -= 8 - U; ti = 1;
            while (c > 0)
            {
                int m = System.Math.Min(8, c);
                for (int k = 0; k < m; k++) buf[d++] = (byte)(Pat(bgTiles[ti * 2], V, k) | bgTiles[ti * 2 + 1]);
                c -= 8; ti++;
            }
        }
        // wnd_scan(_color)
        if (WX < 160)
        {
            int d = WX, c = 160 - WX, ti = 0;
            while (c > 0)
            {
                int m = System.Math.Min(8, c);
                for (int k = 0; k < m; k++) buf[d++] = (byte)(Pat(wndTiles[ti * 2], WV, k) | wndTiles[ti * 2 + 1]);
                c -= 8; ti++;
            }
            if (!cgb) for (int x = WX; x < 160; x++) buf[x] |= 0x04;   // recolor: the window's own DMG palette slot
        }
        // bg_scan_pri / wnd_scan_pri (CGB, only when sprites are on the line)
        if (cgb && ns > 0)
        {
            int rowBg = 0x2000 + bgBase;
            bool used = false; for (int k = 0; k < 32; k++) used |= (vram[rowBg + k] & 0x80) != 0;
            if (used && WX > 0) { int i = S, d = 0, c = WX; int n = System.Math.Min(8 - U, c); for (int k = 0; k < n; k++) pri[d++] = (byte)(vram[rowBg + (i & 31)] & 128); i++; c -= 8 - U; while (c > 0) { int m = System.Math.Min(8, c); for (int k = 0; k < m; k++) pri[d++] = (byte)(vram[rowBg + (i & 31)] & 128); i++; c -= 8; } }
            if (WX < 160)
            {
                int rowW = 0x2000 + wBase; bool wused = false; for (int k = 0; k < 32; k++) wused |= (vram[(rowW + k) & 0x3FFF] & 0x80) != 0;
                if (wused) for (int x = WX, i = 0; x < 160; x++) pri[x] = (byte)(vram[(rowW + ((x - WX) >> 3) + i) & 0x3FFF] & 128);
            }
        }

        // spr_scan
        if (ns > 0)
        {
            System.Array.Copy(buf, bgdup, 256);
            for (int s = ns - 1; s >= 0; s--)
            {
                var o = vs[s];
                int x = o.x;
                if (x >= 160 || x <= -8) continue;
                int srcOff, dest, n;
                if (x < 0) { srcOff = -x; dest = 0; n = 8 + x; }
                else { srcOff = 0; dest = x; n = x > 152 ? 160 - x : 8; }
                while (n-- > 0)
                {
                    int b = Pat(o.pat, o.v, srcOff + n);
                    if (b == 0) continue;
                    if (o.pri) { if ((bgdup[dest + n] & 3) == 0) buf[dest + n] = (byte)(o.pal | b); }
                    else if (cgb) { if (pri[dest + n] == 0 || (bgdup[dest + n] & 3) == 0) buf[dest + n] = (byte)(o.pal | b); }
                    else buf[dest + n] = (byte)(o.pal | b);
                }
            }
        }

        int row = L * W;
        for (int x = 0; x < W; x++) Screen[row + x] = Colour(buf[x], cgb);
    }

    private int SprEnum(int L, byte lcdc, bool cgb)
    {
        if ((lcdc & 0x02) == 0) return 0;
        var oam = gb.Oam; int ns = 0;
        for (int i = 0; i < 40; i++)
        {
            int oy = oam[i * 4], ox = oam[i * 4 + 1], pat = oam[i * 4 + 2], fl = oam[i * 4 + 3];
            if (L >= oy || L + 16 < oy) continue;
            if (L + 8 >= oy && (lcdc & 0x04) == 0) continue;
            int v = L - oy + 16;
            byte pal; int p;
            if (cgb) { p = pat | ((fl & 0x60) << 5) | ((fl & 0x08) << 6); pal = (byte)(32 + ((fl & 0x07) << 2)); }
            else { p = pat | ((fl & 0x60) << 5); pal = (byte)(32 + ((fl & 0x10) >> 2)); }
            if ((lcdc & 0x04) != 0) { p &= ~1; if (v >= 8) { v -= 8; p++; } if ((fl & 0x40) != 0) p ^= 1; }
            vs[ns] = (ox - 8, p, v, pal, (fl & 0x80) != 0);
            if (++ns == 10) break;
        }
        if (cgb) return ns;
        for (int i = 0; i < ns; i++)
        {
            int l = 0, x = vs[0].x;
            for (int j = 1; j < ns; j++) if (vs[j].x < x) { l = j; x = vs[j].x; }
            ts[i] = vs[l]; vs[l].x = 160;
        }
        System.Array.Copy(ts, vs, ns);
        return ns;
    }

    // updatepalette: the 15-bit colour, expanded r<<3 | r>>2.
    private uint Colour(byte idx, bool cgb)
    {
        int c;
        if (cgb)
        {
            var pal = idx < 32 ? gb.BgPalRam : gb.ObjPalRam; int i = (idx & 31) << 1;
            c = (pal[i] | pal[i + 1] << 8) & 0x7FFF;
        }
        else
        {
            // pal_write_dmg: slots 0-3 BG (BGP), 4-7 window (BGP), 32-35 OBP0, 36-39 OBP1, through dmg_pal (DEF_PAL).
            int reg = idx >= 36 ? gb.Obp1 : idx >= 32 ? gb.Obp0 : gb.Bgp;
            int rgb = DefPal[(reg >> ((idx & 3) * 2)) & 3];
            c = ((rgb & 0xF8) >> 3) | ((rgb & 0xF800) >> 6) | ((rgb & 0xF80000) >> 9);
        }
        int r = (c & 0x1F) << 3, g = (c & 0x3E0) >> 2, b = (c & 0x7C00) >> 7;
        r |= r >> 5; g |= g >> 5; b |= b >> 5;
        return 0xFF000000u | (uint)(r << 16 | g << 8 | b);
    }
}
