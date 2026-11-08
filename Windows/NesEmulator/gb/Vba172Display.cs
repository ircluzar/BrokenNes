// Vba172Display - VisualBoyAdvance 1.7.2's Game Boy picture code (2004-05-22), ported from the release tarball
// VisualBoyAdvance-src-1.7.2.tar.gz: src/gb/gbGfx.cpp (gbRenderLine, gbDrawSpriteTile, gbDrawSprites, gbInvertTab) and
// the parts of src/gb/GB.cpp that decide when it runs and what it shows (the mode 2/3 lengths 20 + 43 M-cycles, the
// gbWindowLine resets and the LCDC-write window rule, gbBgp/gbObp, the "Standard" palette of win32/GBColorDlg.cpp,
// gbColorOption off, systemColorMap32 = 5-bit << 3).
//
//   VisualBoyAdvance, Copyright (C) 1999-2003 Forgotten, (C) 2004 Forgotten and the VBA development team. GNU General
//   Public License, version 2 or later. See THIRD_PARTY_NOTICES.md (PPU harvest) and gb/THIRD_PARTY_NOTICES.md.
//
// PPU harvest climb (docs/projects/ppu-harvest-catalog.md, runners-up): an art instrument, ported FAITHFULLY with its
// flaws. It is the Game Boy picture core "VBA172": the stock chip (PPU_GB) keeps the console's timing and this renderer
// draws beside it (PPU_GB.TickObserver). Chosen over VBA-M's later gbGfx.cpp because it is the older, plainer and more
// wrong of the two: VBA-M's 650 lines are mostly the per-game fixes and priority repairs that 1.7.2 never had.
//
// Characteristic jank, kept on purpose:
//  * The whole line is drawn at once at the END of mode 3 (dot 252), from the registers as they stand then: mid-line
//    effects vanish, and SCX/SCY/LCDC writes made during the line's own transfer still land on it.
//  * DMG: LCDC bit 0 off fills the line with palette entry 0 (white), ignoring BGP, but the window still draws.
//  * The window shows on every line with LY >= WY (read live, every line) and WX-7 <= 159; its line counter only
//    resets when LY wraps to 0. Switching the window on while LY > WY and the counter unused hides it for the frame
//    (gbWindowLine = 144).
//  * Sprites: only sprites with 0 < X < 168 and 0 < Y < 160 count toward the 10 per line, taken in OAM order.
//  * Sprite-vs-sprite on DMG: once a lower-numbered sprite has drawn a pixel, any later sprite loses it unless both have
//    the very same X (then the lower number still wins), so overlapping sprites ignore the X rule.
//  * Behind-background sprites hide behind any non-zero background/window colour index; Color: a background tile with
//    its priority bit hides every sprite whose pixel lands on a non-zero index, and LCDC bit 0 (master priority) is ignored.
//  * Colours: 15-bit values shifted << 3 (white = 248,248,248); the DMG "Standard" greys 7FFF/56B5/318C/0000; Color
//    games raw (no colour filter: gbColorOption was off by default). A DMG game on a Color console is drawn as DMG.
namespace NesEmulator.Gb;

internal sealed class Vba172Display : IGbForeignView
{
    public const int W = 160, H = 144;
    public uint[] Screen { get; } = new uint[W * H];
    private readonly PPU_GB gb;
    private int drawnLy = -1, lastLy = -1;
    private int gbWindowLine = -1;
    private byte lastLcdc;
    private readonly ushort[] lineBuffer = new ushort[160];
    private static readonly ushort[] Standard = { 0x7FFF, 0x56B5, 0x318C, 0x0000, 0x7FFF, 0x56B5, 0x318C, 0x0000 };

    public Vba172Display(PPU_GB ppu) { gb = ppu; lastLcdc = ppu.Lcdc; ppu.TickObserver += Run; System.Array.Fill(Screen, 0xFFFFFFFFu); }
    public void Detach() => gb.TickObserver -= Run;

    private bool Cgb => gb.Model == GbModel.Cgb && !gb.CompatMode;

    private void Run()
    {
        byte lcdc = gb.Lcdc;
        // the LCDC write rule: don't draw the window if it was not enabled and not being drawn before
        if ((lastLcdc & 0x20) == 0 && (lcdc & 0x20) != 0 && gbWindowLine == -1 && gb.LY > gb.Wy) gbWindowLine = 144;
        lastLcdc = lcdc;
        if (!gb.LcdOn) { drawnLy = -1; lastLy = -1; return; }
        int ly = gb.LY;
        if (ly != lastLy) { if (ly == 0 && lastLy > 0) gbWindowLine = -1; lastLy = ly; }   // LY 153 -> 0: reset the window line
        if (ly >= H || drawnLy == ly || gb.Dot < 80 + 172) return;
        drawnLy = ly;
        RenderLine(ly);
        DrawSprites(ly);
    }


    private uint Rgb(int idx15)
    {
        int v = idx15 & 0x7FFF;
        return 0xFF000000u | (uint)(((v & 0x1F) << 3) << 16 | (((v >> 5) & 0x1F) << 3) << 8 | (((v >> 10) & 0x1F) << 3));
    }

    // gbPalette[c]: DMG = the Standard palette, CGB = palette RAM (0-31 background, 32-63 sprites)
    private uint Colour(int c)
    {
        if (!Cgb) return Rgb(Standard[c & 7]);
        var ram = c < 32 ? gb.BgPalRam : gb.ObjPalRam; int i = (c & 31) * 2;
        return Rgb(ram[i] | ram[i + 1] << 8);
    }

    private static int Shade(byte reg, int c) => (reg >> (c * 2)) & 3;

    private void RenderLine(int y)
    {
        var vram = gb.Vram; bool cgb = Cgb;
        int bank1 = 0x2000;
        byte lcdc = gb.Lcdc;
        int tile_map = (lcdc & 8) != 0 ? 0x1C00 : 0x1800;
        int tile_pattern = (lcdc & 16) != 0 ? 0x0000 : 0x0800;
        int x = 0;
        int row = y * W;
        int sx = gb.Scx, sy = (gb.Scy + y) & 255;
        int tx = sx >> 3, ty = sy >> 3;
        int bx = 1 << (7 - (sx & 7)), by = sy & 7;
        int tile_map_line_y = tile_map + ty * 32;
        int tile_map_address = tile_map_line_y + tx;
        int attrs = cgb ? vram[bank1 + tile_map_address] : 0;
        int tile = vram[tile_map_address];
        if ((lcdc & 16) == 0) tile = (tile + 128) & 0xFF;
        int tpa = tile_pattern + tile * 16 + by * 2;

        if ((lcdc & 0x01) != 0 || cgb)
        {
            while (x < 160)
            {
                if ((attrs & 0x40) != 0) tpa = tile_pattern + tile * 16 + (7 - by) * 2;
                int a, b;
                if ((attrs & 0x08) != 0) { a = vram[bank1 + (tpa & 0x1FFF)]; b = vram[bank1 + ((tpa + 1) & 0x1FFF)]; }
                else { a = vram[tpa & 0x1FFF]; b = vram[(tpa + 1) & 0x1FFF]; }
                if ((attrs & 0x20) != 0) { a = Invert(a); b = Invert(b); }
                while (bx > 0)
                {
                    int c = ((a & bx) != 0 ? 1 : 0) + ((b & bx) != 0 ? 2 : 0);
                    lineBuffer[x] = (ushort)c;
                    if ((attrs & 0x80) != 0) lineBuffer[x] |= 0x300;
                    c = cgb ? c + (attrs & 7) * 4 : Shade(gb.Bgp, c);
                    Screen[row + x] = Colour(c);
                    x++;
                    if (x >= 160) break;
                    bx >>= 1;
                }
                tx++; if (tx == 32) tx = 0;
                bx = 128;
                if (cgb) attrs = vram[bank1 + tile_map_line_y + tx];
                tile = vram[tile_map_line_y + tx];
                if ((lcdc & 16) == 0) tile = (tile + 128) & 0xFF;
                tpa = tile_pattern + tile * 16 + by * 2;
            }
        }
        else
        {
            for (int i = 0; i < 160; i++) { Screen[row + i] = Colour(0); lineBuffer[i] = 0; }
        }

        // the window
        if ((lcdc & 0x20) == 0) return;
        int wy = gb.Wy;
        if (y < wy) return;
        int wx = gb.Wx - 7;
        if (!(wx <= 159 && gbWindowLine <= 143)) return;
        tile_map = (lcdc & 0x40) != 0 ? 0x1C00 : 0x1800;
        if (gbWindowLine == -1) gbWindowLine = 0;
        tx = 0; ty = gbWindowLine >> 3;
        bx = 128; by = gbWindowLine & 7;
        if (wx < 0) { bx >>= -wx; wx = 0; }
        tile_map_line_y = tile_map + ty * 32;
        tile_map_address = tile_map_line_y + tx;
        x = wx;
        tile = vram[tile_map_address];
        attrs = cgb ? vram[bank1 + tile_map_address] : 0;
        if ((lcdc & 16) == 0) tile = (tile + 128) & 0xFF;
        tpa = tile_pattern + tile * 16 + by * 2;
        while (x < 160)
        {
            if ((attrs & 0x40) != 0) tpa = tile_pattern + tile * 16 + (7 - by) * 2;
            int a, b;
            if ((attrs & 0x08) != 0) { a = vram[bank1 + (tpa & 0x1FFF)]; b = vram[bank1 + ((tpa + 1) & 0x1FFF)]; }
            else { a = vram[tpa & 0x1FFF]; b = vram[(tpa + 1) & 0x1FFF]; }
            if ((attrs & 0x20) != 0) { a = Invert(a); b = Invert(b); }
            while (bx > 0)
            {
                int c = ((a & bx) != 0 ? 1 : 0) + ((b & bx) != 0 ? 2 : 0);
                lineBuffer[x] = (ushort)((attrs & 0x80) != 0 ? 0x300 + c : 0x100 + c);
                c = cgb ? c + (attrs & 7) * 4 : Shade(gb.Bgp, c);
                Screen[row + x] = Colour(c);
                x++;
                if (x >= 160) break;
                bx >>= 1;
            }
            tx++; if (tx == 32) tx = 0;
            bx = 128;
            tile = vram[tile_map_line_y + tx];
            if (cgb) attrs = vram[bank1 + tile_map_line_y + tx];
            if ((lcdc & 16) == 0) tile = (tile + 128) & 0xFF;
            tpa = tile_pattern + tile * 16 + by * 2;
        }
        gbWindowLine++;
    }

    private static int Invert(int v) { int r = 0; for (int i = 0; i < 8; i++) if ((v & (1 << i)) != 0) r |= 0x80 >> i; return r; }

    private void DrawSpriteTile(int tile, int x, int y, int t, int flags, bool size, int spriteNumber)
    {
        var vram = gb.Vram; bool cgb = Cgb;
        byte pal = (flags & 0x10) != 0 ? gb.Obp1 : gb.Obp0;
        bool flipx = (flags & 0x20) != 0;
        if ((flags & 0x40) != 0) t = (size ? 15 : 7) - t;
        bool prio = (flags & 0x80) != 0;
        int address = (tile * 16 + 2 * t) & 0x1FFF;
        int bank = cgb && (flags & 0x08) != 0 ? 0x2000 : 0;
        int a = vram[bank + address], b = vram[bank + ((address + 1) & 0x1FFF)];
        int row = y * W;
        for (int xx = 0; xx < 8; xx++)
        {
            int mask = 1 << (7 - xx);
            int c = ((a & mask) != 0 ? 1 : 0) + ((b & mask) != 0 ? 2 : 0);
            if (c == 0) continue;
            int xxx = flipx ? 7 - xx + x : xx + x;
            if (xxx < 0 || xxx > 159) continue;
            int color = lineBuffer[xxx];
            if (prio && color < 0x200 && (color & 0xFF) != 0) continue;
            if (color >= 0x300 && color != 0x300) continue;
            else if (color >= 0x200 && color < 0x300)
            {
                int sprite = color & 0xFF;
                int spriteX = gb.Oam[4 * sprite + 1] - 8;
                if (spriteX == x) { if (sprite < spriteNumber) continue; }
                else if (cgb) { if (sprite < spriteNumber) continue; }
                else if (spriteX < x + 8) continue;
            }
            lineBuffer[xxx] = (ushort)(0x200 + spriteNumber);
            c = cgb ? c + (flags & 0x07) * 4 + 32 : Shade(pal, c) + 4;
            Screen[row + xxx] = Colour(c);
        }
    }

    private void DrawSprites(int yc)
    {
        byte lcdc = gb.Lcdc;
        bool size = (lcdc & 4) != 0;
        if ((lcdc & 0x80) == 0 || (lcdc & 2) == 0) return;
        var oam = gb.Oam; int count = 0;
        for (int i = 0; i < 40; i++)
        {
            int y = oam[i * 4], x = oam[i * 4 + 1], tile = oam[i * 4 + 2], flags = oam[i * 4 + 3];
            if (size) tile &= 254;
            if (x > 0 && y > 0 && x < 168 && y < 160)
            {
                int t = yc - y + 16;
                if (size && t >= 0 && t < 16) { DrawSpriteTile(tile, x - 8, yc, t, flags, size, i); count++; }
                else if (!size && t >= 0 && t < 8) { DrawSpriteTile(tile, x - 8, yc, t, flags, size, i); count++; }
            }
            if (count >= 10) break;
        }
    }
}
