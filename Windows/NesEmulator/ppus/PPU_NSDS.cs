// PPU_NSDS - nesDS's picture side, rebuilt from its ARM assembly: arm9/source/RP2C02/RP2C02.s (2222 lines) + render.s,
// DS-Homebrew/NesDS @73b881b (https://github.com/DS-Homebrew/NesDS/tree/73b881baa7f92efef75c3bdfa8dbea1ce5ffcd9d).
//
//   nesDS: loopy (PocketNES, 2000), FluBBa, Huiminghao (nesDS 1.x/2.x), the DS-Homebrew maintainers. Public domain per its
//   README (the PocketNES-derived parts carry no licence). Art instrument (PPU harvest climb, round 2): see
//   THIRD_PARTY_NOTICES.md section 2b and docs/projects/ppu-harvest-catalog.md.
//
// The default display path (hardware rendering, flags&3 = 0 "flicker" scaling), ported as a model of the DS doing the work:
//  * The DS screen is 256x192: the 240 NES lines are squeezed by rescale(0xE000, -0x60000): DS row = (-6 + line * 0.875),
//    the last NES line landing on a row wins, so one line in eight is dropped and the top ~7 / bottom ~13 lines never show.
//    Every other frame the BG uses the second table, 0x3334 lower ("flicker" scaling), so the dropped lines alternate.
//    The DS screen is letterboxed in the BrokenNes frame (black above and below).
//  * Sprites are DS OBJs built ONCE a frame at line 0 (renderSprites) from the OAM and the CHR of that moment; they are not
//    scaled: an 8x8 sprite stays 8 rows tall on the 192-row screen, placed at spriteY_lookup[Y + 2]. No 8-per-line limit,
//    no overflow flag. The sprite size / table come from $2000 as latched at line 119 of the previous frame (ppuCtrl0Frame).
//  * The palette reaches the DS once a frame (agb_pal copied at newframe): mid-frame palette changes are not seen.
//  * BG: one DS map of the four nametables, 9-bit scroll per line from the HBlank DMA buffers; $2005's Y only takes effect
//    at the next frame, $2006 changes the vertical scroll at once (from the next line); rows 240-255 of a slot are blank.
//  * Display control per DS row: BG / OBJ enables, and the left-8 clip done with a DS window: BG clip hides the BG AND the
//    sprites there ("obj clip will be forced too, from window oddities"), OBJ clip hides only the sprites.
//  * Sprite 0 hit (checkSprite0Collision, every line until found): the sprite at the OAM address (normally 0) covers the line
//    (row = line - Y, the sprite's top being Y+1: one row early), BG and OBJ both on, its tile row (no flip handling: "I dont
//    really give a shit about Y flipping") has any pixel, X != 255, X != 0 under clipping. The BG is never looked at.
//    $2002 then reports the hit once the line is passed, or on that line once the CPU cycles into it reach the sprite's X
//    (pixels compared with CPU cycles: hits right of X=113 only show on the next line).
//  * $2004 writes to attribute bytes keep only bits E3; $2007 palette reads give the colour with the bus latch's top 2 bits.
//  * Colours: Loopy's palette through the DS's 15-bit colour (gamma 0).
// Not ported: the SOFTRENDER path (render.s, a per-game option), light gun, PAL timing and the per-line sprite (SPLINE) mode.
// Adaptation: the BG line is built from the nametables at the start of its NES line (the DS reads its map while it displays,
// which the original runs ahead of); pattern data likewise at that moment (nesDS's per-line BG CHR maps).
using System;

namespace NesEmulator
{
public sealed class PPU_NSDS : TileHostPpu
{
    public override string CoreName => "nesDS";
    public override string Description => "nesDS's DS tile-engine picture (73b881b): 240 lines squeezed onto the 192-line DS screen with flickering dropped lines, unscaled once-a-frame sprites with no line limit, per-frame palette, sprite-0 hit without looking at the BG.";

    const int HostH = 192, Top = (H - HostH) / 2;
    const int ScaleWord = 0xE000, StartY = -0x60000, FlickerOfs = 0x3334;

    public PPU_NSDS(Bus bus) : base(bus) { }

    int RowOf(int line, int ofs, int flick) { int v = (StartY + line * (ScaleWord | flick) + ofs) >> 16; return v < 0 ? 0 : v; }

    protected override void AfterBgLine(int line)
    {
        if ((sprite0Y & 0xFF) == 0) CheckSprite0Collision(line);
    }

    void CheckSprite0Collision(int line)
    {
        int s = oamAdr;
        int y = oam[s];
        int r = line - y;
        bool tall = (ctrl0 & 0x20) != 0;
        if (r < 0 || r >= (tall ? 16 : 8)) return;
        if ((ctrl1 & 0x18) != 0x18) return;
        int tile = oam[(s + 1) & 0xFF];
        int addr = tall ? ((tile & 1) << 12) | ((tile & 0xFE) << 4) : ((ctrl0 & 0x08) << 9) | (tile << 4);
        addr += (r & 7) + ((r & 8) << 1);
        if ((Mem(addr) | Mem(addr + 8)) == 0) return;
        int x = oam[(s + 3) & 0xFF];
        if (x == 0xFF) return;
        if ((ctrl1 & 0x06) != 0x06 && x == 0) return;
        sprite0X = x;
        if (line + 1 < 240) sprite0Y = line + 1;
    }

    protected override bool Sprite0HitNow()
    {
        if (scanline > sprite0Y && scanline < 0x200) return true;
        return scanline == sprite0Y && dot / 3 >= sprite0X;
    }

    readonly int[] rowLine = new int[HostH], rowCtl = new int[HostH];
    readonly int[] spriteTop = new int[64];

    protected override void Compose()
    {
        int flick = (int)(frame & 1);
        int bgOfs = flick != 0 ? FlickerOfs : 0;
        Array.Fill(rowLine, -1); Array.Fill(rowCtl, -1);
        for (int l = 0; l < H; l++)
        {
            int r0 = RowOf(l, 0, flick), rb = RowOf(l, bgOfs, flick);
            if (rb < HostH) rowLine[rb] = l;
            if (r0 < HostH) rowCtl[r0] = l;
        }
        bool tall = (frameSprCtrl & 0x20) != 0;
        int sh = tall ? 16 : 8;
        int ofs2 = ScaleWord < 0x14000 ? 2 : 1;
        for (int s = 0; s < 64; s++)
        {
            int y = frameOam[s * 4];
            // spriteY_lookup holds the +0x4000 table, unclamped, as a byte: a negative row wraps and the DS OBJ Y (8-bit) wraps with it
            int v = ((StartY + (y + ofs2) * (ScaleWord | flick) + 0x4000) >> 16) & 0xFF;
            spriteTop[s] = y > 239 ? int.MinValue : v >= 192 ? v - 256 : v;
        }
        var fb = Fb;
        Span<byte> sc = stackalloc byte[8];
        Span<byte> sprC = stackalloc byte[W];
        Span<byte> sprA = stackalloc byte[W];
        for (int y = 0; y < H; y++)
        {
            int o = y * W * 4, hr = y - Top;
            if (hr < 0 || hr >= HostH) { for (int x = 0; x < W; x++, o += 4) Black(fb, o); continue; }
            int l = rowLine[hr] < 0 ? 0 : rowLine[hr];
            int lc = rowCtl[hr] < 0 ? l : rowCtl[hr];
            byte c1 = lineCtrl1[lc];
            bool bgOn = (c1 & 0x08) != 0, objOn = (c1 & 0x10) != 0, bgClip = (c1 & 0x02) == 0, objClip = (c1 & 0x04) == 0;
            sprC.Clear();
            if (objOn)
            {
                for (int s = 63; s >= 0; s--)   // lower index drawn last = on top
                {
                    if (spriteTop[s] == int.MinValue) continue;
                    int row = hr - spriteTop[s];
                    if (row < 0 || row >= sh) continue;
                    SpriteRow(s, row, tall, sc);
                    int sx = frameOam[s * 4 + 3], attr = frameOam[s * 4 + 2];
                    for (int i = 0; i < 8; i++)
                    {
                        int px = sx + i;
                        if (px >= W || sc[i] == 0) continue;
                        sprC[px] = (byte)(0x10 | ((attr & 3) << 2) | sc[i]);
                        sprA[px] = (byte)(attr & 0x20);
                    }
                }
            }
            var bgl = lineBg.AsSpan(l * W, W);
            for (int x = 0; x < W; x++, o += 4)
            {
                bool left = x < 8;
                int bg = bgOn && !(left && bgClip) ? bgl[x] : 0;
                int sp = sprC[x];
                if (sp != 0 && left && (bgClip || objClip)) sp = 0;
                int col = sp != 0 && (sprA[x] == 0 || bg == 0) ? framePal[sp] : bg != 0 ? framePal[bg] : framePal[0];
                PutColour(fb, o, LoopyPal, col);
            }
        }
    }
}
}
