// SameBoyV01Display - the very first SameBoy's picture code, ported from LIJI32/SameBoy tag v0.1 (2016-04-01),
// Core/display.c (391 lines): get_pixel, palette_changed/scale_channel and the drawing part of display_run.
//
//   SameBoy, Copyright (c) 2015-2016 Lior Halphon. MIT License (LICENSE at the tag); see THIRD_PARTY_NOTICES.md
//   (PPU harvest) and Windows/NesEmulator/gb/THIRD_PARTY_NOTICES.md.
//
// PPU harvest climb (docs/projects/ppu-harvest-catalog.md, #4): an art instrument, ported FAITHFULLY with its flaws.
// It is the Game Boy picture core "SB01". The stock chip (PPU_GB) keeps the console's timing - LY, STAT, the
// interrupts and VRAM locking - and this renderer draws its own picture beside it from the live registers, VRAM
// and OAM, at its own times (PPU_GB.TickObserver, once per M-cycle). display_run's own timing side (its STAT and
// interrupt rules, the "LY=151" and "LY 153 reads 0" guesses) would need it to own the console's clock, so it
// is not reproduced; its drawing times are.
//
// Characteristic jank, all of it kept on purpose:
//  * Pixels are drawn in 8-pixel chunks as the line's dots pass 80 + 8n ("& ~7"), each pixel computed alone
//    (get_pixel): mid-line register changes land on 8-pixel boundaries, from a fixed 80-dot mode 2.
//  * SCX's fine scroll is read once per line at mode 2 (its own TODO: "Figure out how the Gameboy handles
//    in-line changes to SCX"); the coarse part is read live per pixel.
//  * The window: "effective window" turns on when LY == WY with the window enabled and remembers that line as
//    its Y; the window's own line counter does not exist (y - effective_window_y), and WX is read per pixel.
//  * Sprites: the 10-a-line limit counts sprites as they are found per PIXEL, so the 11th sprite in OAM order
//    on the line hides only where it overlaps; on DMG the X-priority test BREAKS out of the scan when a later
//    sprite has an X >= the best one's, so sprites further down OAM never show over it.
//  * BG priority: the BG-over-OBJ flag and the CGB attribute's priority both use the raw colour index 0 test;
//    the CGB master priority bit (LCDC bit 0) is ignored, as is LCDC bit 0 on DMG (the background never turns off).
//  * A DMG game on a Game Boy Color is shown with the plain shades (v0.1 had no compatibility palettes).
//  * LCD off: the screen is filled white at the next vblank.
namespace NesEmulator.Gb;

internal sealed class SameBoyV01Display : IGbForeignView
{
    public const int W = 160, H = 144;
    public uint[] Screen { get; } = new uint[W * H];
    private readonly NesEmulator.Gb.PPU_GB gb;
    private bool effectiveWindowEnabled;
    private int effectiveWindowY = 0xFF;
    private int lineXBias, previousLcdcX;
    private int lastLy = -1;
    private long lastFrame = -1;

    public SameBoyV01Display(NesEmulator.Gb.PPU_GB ppu)
    {
        gb = ppu;
        ppu.TickObserver += Run;
        System.Array.Fill(Screen, 0xFFFFFFFFu);
    }

    public void Detach() => gb.TickObserver -= Run;

    private bool CgbMode => gb.Model == NesEmulator.Gb.GbModel.Cgb && !gb.CompatMode;

    // scale_channel + palette_changed: 5-bit to 8-bit by (x << 3) | (x >> 2).
    private static uint Cgb(byte[] pal, int palette, int index)
    {
        int o = palette * 8 + index * 2;
        int color = pal[o] | pal[o + 1] << 8;
        static uint S(int x) { x &= 0x1F; return (uint)((x << 3) | (x >> 2)); }
        return 0xFF000000u | S(color) << 16 | S(color >> 5) << 8 | S(color >> 10);
    }

    private uint BgRgb(int palette, int shade) => CgbMode ? Cgb(gb.BgPalRam, palette, shade) : gb.DmgColors[shade];
    private uint ObjRgb(int palette, int shade) => CgbMode ? Cgb(gb.ObjPalRam, palette, shade) : gb.DmgColors[shade];

    // display_run, the drawing side.
    private void Run()
    {
        if (gb.FrameCount != lastFrame)
        {
            // display_vblank: LCD off -> white.
            if (lastFrame >= 0 && !gb.LcdOn) System.Array.Fill(Screen, 0xFFFFFFFFu);
            lastFrame = gb.FrameCount;
        }
        if (!gb.LcdOn) { lastLy = -1; return; }
        int ly = gb.LY, cyc = gb.Dot;
        if (ly >= H)
        {
            effectiveWindowEnabled = false;
            effectiveWindowY = 0xFF;
            lastLy = ly;
            return;
        }
        if ((gb.Lcdc & 0x20) != 0 && ly == gb.Wy) effectiveWindowEnabled = true;
        if (cyc < 80)
        {
            if (effectiveWindowEnabled && effectiveWindowY == 0xFF) effectiveWindowY = ly;
            lineXBias = -(gb.Scx & 7);
            previousLcdcX = lineXBias;
            lastLy = ly;
            return;
        }
        if (lastLy != ly)
        {
            // The line began between two observations: mode 2 is entered all the same.
            if (effectiveWindowEnabled && effectiveWindowY == 0xFF) effectiveWindowY = ly;
            lineXBias = -(gb.Scx & 7); previousLcdcX = lineXBias; lastLy = ly;
        }
        int current = ((cyc - 80) & ~7) + lineXBias;
        for (; previousLcdcX < current; previousLcdcX++)
        {
            if (previousLcdcX >= 160 || previousLcdcX < 0) continue;
            Screen[ly * W + previousLcdcX] = GetPixel(previousLcdcX, ly);
        }
    }

    private uint GetPixel(int x8, int y8)
    {
        byte x = (byte)x8, y = (byte)y8;
        var vram = gb.Vram; var oam = gb.Oam;
        byte lcdc = gb.Lcdc;
        bool cgb = CgbMode;
        int map = 0x1800;
        int attributes = 0, spritePalette = 0, tileAddress;
        int backgroundPixel, spritePixel = 0;
        int spritesInLine = 0;
        bool mode816 = (lcdc & 4) != 0, spritesEnabled = (lcdc & 2) != 0;
        int lowestSpriteX = 0xFF;
        bool useObp1 = false, priority = false, inWindow = false;

        if (effectiveWindowEnabled && (lcdc & 0x20) != 0)
            if (y >= effectiveWindowY && x + 7 >= gb.Wx) inWindow = true;

        if (spritesEnabled)
        {
            for (int i = 0; i < 40; i++)
            {
                int o = i * 4;
                int spriteY = oam[o] - 16, spriteX = oam[o + 1] - 8;
                int tileN = oam[o + 2], flags = oam[o + 3];
                if (spriteY <= y && spriteY + (mode816 ? 16 : 8) > y)
                {
                    if (++spritesInLine == 11) break;
                    if (spriteX > x || spriteX + 8 <= x) continue;
                    int tileX = (byte)(x - spriteX), tileY = (byte)(y - spriteY);
                    if ((flags & 0x20) != 0) tileX = 7 - tileX;
                    if ((flags & 0x40) != 0) tileY = (mode816 ? 15 : 7) - tileY;
                    int lineAddress = (mode816 ? tileN & 0xFE : tileN) * 0x10 + tileY * 2;
                    if (cgb && (flags & 0x8) != 0) lineAddress += 0x2000;
                    lineAddress &= 0x3FFF;
                    int cur = ((vram[lineAddress] >> (~tileX & 7)) & 1) | (((vram[lineAddress + 1] >> (~tileX & 7)) & 1) << 1);
                    if (cur != 0)
                    {
                        if (!cgb && oam[o + 1] >= lowestSpriteX) break;
                        spritePixel = cur;
                        lowestSpriteX = oam[o + 1];
                        useObp1 = (flags & 0x10) != 0;
                        spritePalette = flags & 7;
                        priority = (flags & 0x80) != 0;
                        if (cgb) break;
                    }
                }
            }
        }

        if (inWindow) { x = (byte)(x - (gb.Wx - 7)); y = (byte)(y - effectiveWindowY); }
        else { x = (byte)(x + gb.Scx); y = (byte)(y + gb.Scy); }
        if ((lcdc & 0x08) != 0 && !inWindow) map = 0x1C00;
        else if ((lcdc & 0x40) != 0 && inWindow) map = 0x1C00;
        int tile = vram[map + x / 8 + y / 8 * 32];
        if (cgb) attributes = vram[map + x / 8 + y / 8 * 32 + 0x2000];
        if ((attributes & 0x80) != 0) priority = true;

        if (!priority && spritePixel != 0)
        {
            if (!cgb) { spritePixel = (useObp1 ? gb.Obp1 : gb.Obp0) >> (spritePixel << 1) & 3; spritePalette = useObp1 ? 1 : 0; }
            return ObjRgb(spritePalette, spritePixel);
        }
        tileAddress = (lcdc & 0x10) != 0 ? tile * 0x10 : (sbyte)tile * 0x10 + 0x1000;
        if ((attributes & 0x8) != 0) tileAddress += 0x2000;
        if ((attributes & 0x20) != 0) x = (byte)~x;
        if ((attributes & 0x40) != 0) y = (byte)~y;
        tileAddress &= 0x3FFF;
        backgroundPixel = ((vram[tileAddress + (y & 7) * 2] >> (~x & 7)) & 1) | (((vram[tileAddress + (y & 7) * 2 + 1] >> (~x & 7)) & 1) << 1);
        if (priority && spritePixel != 0 && backgroundPixel == 0)
        {
            if (!cgb) { spritePixel = (useObp1 ? gb.Obp1 : gb.Obp0) >> (spritePixel << 1) & 3; spritePalette = useObp1 ? 1 : 0; }
            return ObjRgb(spritePalette, spritePixel);
        }
        if (!cgb) backgroundPixel = gb.Bgp >> (backgroundPixel << 1) & 3;
        return BgRgb(attributes & 7, backgroundPixel);
    }
}
