// PeanutGbDisplay - Peanut-GB's picture code, ported from deltabeard/Peanut-GB @8a692e4 (the parent of 1a74227
// "go to OAM Search after VBLANK", before the 2025 LCD fixes): peanut_gb.h __gb_draw_line (with its default
// PEANUT_GB_HIGH_LCD_ACCURACY = 1 sprite sort) and the LCD-mode part of __gb_step_cpu that decides when it runs.
//
//   Peanut-GB, Copyright (c) 2018-2023 Mahyar Koshkouei, with parts from SameBoy (Lior Halphon). MIT License
//   (LICENSE at the pinned commit); see THIRD_PARTY_NOTICES.md (PPU harvest) and gb/THIRD_PARTY_NOTICES.md.
//
// PPU harvest climb (docs/projects/ppu-harvest-catalog.md, #5): an art instrument, ported FAITHFULLY with its flaws.
// It is the Game Boy picture core "PGB": the stock chip (PPU_GB) keeps the console's timing and this renderer draws its
// own picture beside it (PPU_GB.TickObserver), when Peanut-GB would.
//
// Characteristic jank, all of it kept on purpose:
//  * Peanut-GB's line runs HBlank first (0-203), OAM search (204), transfer (284): the WHOLE line is drawn at once, 284
//    dots after LY changed - after the real line was shown - from the registers as they stand then. Mid-line effects
//    vanish and late register writes land a line early.
//  * WY is latched once a frame, at line 0; WY writes during the frame are ignored. The window shows on every line
//    with LY >= that WY and WX <= 166, its own line counter advancing only on lines it is drawn.
//  * Sprite-behind-background compares the background's SHADE with BGP colour 0's shade, so a palette that maps two
//    indexes to the same shade lets sprites show through (or hide) where they should not.
//  * Sprites with X = 0 or X >= 168 still take one of the line's 10 places.
//  * DMG only: on a Game Boy Color game it draws from VRAM bank 0 with BGP/OBP0/OBP1 and no attributes (the colour
//    game shown the way Peanut-GB would show it - wrongly).
//  * LCDC bit 0 off leaves the line's background at shade 0.
namespace NesEmulator.Gb;

internal sealed class PeanutGbDisplay : IGbForeignView
{
    public const int W = 160, H = 144;
    public uint[] Screen { get; } = new uint[W * H];
    private readonly PPU_GB gb;
    private int displayWY, windowClear;
    private int drawnLy = -1;
    private bool latched;
    private readonly byte[] pixels = new byte[W];
    private readonly (byte n, byte x)[] spr = new (byte, byte)[40];

    public PeanutGbDisplay(PPU_GB ppu)
    {
        gb = ppu;
        ppu.TickObserver += Run;
        System.Array.Fill(Screen, 0xFFFFFFFFu);
    }

    public void Detach() => gb.TickObserver -= Run;

    private void Run()
    {
        if (!gb.LcdOn) { drawnLy = -1; return; }
        int ly = gb.LY, cyc = gb.Dot;
        if (ly >= H) { drawnLy = -1; latched = false; return; }
        if (ly == 0 && !latched) { latched = true; displayWY = gb.Wy; windowClear = 0; }   // "Clear Screen" as line 0's HBlank starts
        if (cyc >= 284 && drawnLy != ly)
        {
            drawnLy = ly;
            DrawLine(ly);
        }
    }

    private void DrawLine(int ly)
    {
        var vram = gb.Vram; var oam = gb.Oam;
        byte lcdc = gb.Lcdc, scx = gb.Scx, scy = gb.Scy, wx = gb.Wx, bgp = gb.Bgp, obp0 = gb.Obp0, obp1 = gb.Obp1;
        System.Array.Clear(pixels);
        int BgPal(int c) => (bgp >> (c * 2)) & 3;
        int Tile(int idx) => (lcdc & 0x10) != 0 ? idx * 0x10 : 0x800 + ((idx + 0x80) % 0x100) * 0x10;

        if ((lcdc & 0x01) != 0)
        {
            byte bgY = (byte)(ly + scy);
            int bgMap = ((lcdc & 0x08) != 0 ? 0x1C00 : 0x1800) + (bgY >> 3) * 0x20;
            byte dispX = W - 1;
            byte bgX = (byte)(dispX + scx);
            int py = bgY & 7, px = 7 - (bgX & 7);
            int tile = Tile(vram[bgMap + (bgX >> 3)]) + 2 * py;
            int t1 = vram[tile] >> px, t2 = vram[tile + 1] >> px;
            for (; dispX != 0xFF; dispX--)
            {
                if (px == 8)
                {
                    px = 0;
                    bgX = (byte)(dispX + scx);
                    tile = Tile(vram[bgMap + (bgX >> 3)]) + 2 * py;
                    t1 = vram[tile]; t2 = vram[tile + 1];
                }
                pixels[dispX] = (byte)BgPal((t1 & 1) | ((t2 & 1) << 1));
                t1 >>= 1; t2 >>= 1; px++;
            }
        }

        if ((lcdc & 0x20) != 0 && ly >= displayWY && wx <= 166)
        {
            int winLine = ((lcdc & 0x40) != 0 ? 0x1C00 : 0x1800) + (windowClear >> 3) * 0x20;
            byte dispX = W - 1;
            byte winX = (byte)(dispX - wx + 7);
            int py = windowClear & 7, px = 7 - (winX & 7);
            int tile = Tile(vram[winLine + (winX >> 3)]) + 2 * py;
            int t1 = vram[tile] >> px, t2 = vram[tile + 1] >> px;
            byte end = (byte)((wx < 7 ? 0 : wx - 7) - 1);
            for (; dispX != end; dispX--)
            {
                if (px == 8)
                {
                    px = 0;
                    winX = (byte)(dispX - wx + 7);
                    tile = Tile(vram[winLine + (winX >> 3)]) + 2 * py;
                    t1 = vram[tile]; t2 = vram[tile + 1];
                }
                pixels[dispX] = (byte)BgPal((t1 & 1) | ((t2 & 1) << 1));
                t1 >>= 1; t2 >>= 1; px++;
            }
            windowClear++;
        }

        if ((lcdc & 0x02) != 0)
        {
            bool tall = (lcdc & 0x04) != 0;
            int count = 0;
            for (int s = 0; s < 40; s++)
            {
                byte oy = oam[4 * s], ox = oam[4 * s + 1];
                if (ly + (tall ? 0 : 8) >= oy || ly + 16 < oy) continue;
                spr[count++] = ((byte)s, ox);
            }
            // qsort(compare_sprites): by X, then OAM number.
            System.Array.Sort(spr, 0, count, System.Collections.Generic.Comparer<(byte n, byte x)>.Create((a, b) => a.x != b.x ? a.x - b.x : a.n - b.n));
            if (count > 10) count = 10;
            int bg0 = BgPal(0);
            for (int k = count - 1; k >= 0; k--)
            {
                int s = spr[k].n;
                byte oy = oam[4 * s], ox = oam[4 * s + 1], of = oam[4 * s + 3];
                int ot = oam[4 * s + 2] & (tall ? 0xFE : 0xFF);
                if (ox == 0 || ox >= 168) continue;
                byte py = (byte)(ly - oy + 16);
                if ((of & 0x40) != 0) py = (byte)((tall ? 15 : 7) - py);
                int t1 = vram[(ot * 0x10 + 2 * py) & 0x1FFF], t2 = vram[(ot * 0x10 + 2 * py + 1) & 0x1FFF];
                byte dir, start, end, shift;
                if ((of & 0x20) != 0)
                {
                    dir = 1; start = (byte)(ox < 8 ? 0 : ox - 8); end = (byte)System.Math.Min((int)ox, W); shift = (byte)(8 - ox + start);
                }
                else
                {
                    dir = 0xFF; start = (byte)(System.Math.Min((int)ox, W) - 1); end = (byte)((ox < 8 ? 0 : ox - 8) - 1); shift = (byte)(ox - (start + 1));
                }
                t1 >>= shift; t2 >>= shift;
                byte pal = (of & 0x10) != 0 ? obp1 : obp0;
                for (byte dx = start; dx != end; dx += dir)
                {
                    int c = (t1 & 1) | ((t2 & 1) << 1);
                    if (c != 0 && !((of & 0x80) != 0 && (pixels[dx] & 3) != bg0)) pixels[dx] = (byte)((pal >> (c * 2)) & 3);
                    t1 >>= 1; t2 >>= 1;
                }
            }
        }

        var shades = gb.DmgColors;
        int o = ly * W;
        for (int x = 0; x < W; x++) Screen[o + x] = shades[pixels[x] & 3];
    }
}

/// <summary>A ported foreign Game Boy renderer drawing beside the stock chip (the PPU harvest).</summary>
internal interface IGbForeignView
{
    uint[] Screen { get; }
    void Detach();
}
