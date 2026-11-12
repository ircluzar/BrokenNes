// PPU_PNES - PocketNES's picture side, rebuilt from its ARM assembly: src/ppu.s (5050 lines) + io.s (spriteinit), Dwedit/PocketNES
// @9c07f3d (https://github.com/Dwedit/PocketNES/tree/9c07f3d1d2a26b957769c5cde942060cc9ea6e64/src).
//
//   PocketNES: loopy (2000-2001), FluBBa, Dwedit (Dan Weiss) and contributors. No licence stated in the repository. Art instrument
//   (PPU harvest climb, round 2): see THIRD_PARTY_NOTICES.md section 2b and docs/projects/ppu-harvest-catalog.md.
//
// The default display type (SCALED_SPRITES, main.c "default mode is scaled with sprites"), ported as a model of the GBA doing
// the work (TileHostPpu: per-line buffers replayed by HBlank DMA, host tile map, once-a-frame OBJs):
//  * The GBA screen is 240x160. NES columns 8-247 show (SCREEN_LEFT = 8): the left and right 8 pixels are never seen.
//  * scale75: from windowtop_scaled6_8 = 16, every group of four NES lines gives three GBA rows: one line in four is dropped,
//    and the dropped line "twitches" to another position every other frame (twitch/flicker). Lines 0-15 and below ~229 are cut.
//    (scale75_findscanline picks the twitch line from the scroll value; this model drops by line number: a stated simplification.)
//  * Sprites are GBA affine OBJs in double-size boxes scaled to 0.75 vertically (YSCALE_LOOKUP = -17 + 0.75 * Y, 8x16 sprites
//    looked up at Y - 5), so they shrink with the BG but land on their own rounding; a horizontally flipped sprite moves one
//    pixel right ("add one pixel to X coordinate when flipping if scaling"). No 8-per-line limit, no overflow flag.
//  * A sprite's tiles are fetched with the CHR banks of the 8-line band holding its Y (bankbuffer[(y & 0xF8) ...]), and an 8x8
//    sprite's pattern table is the $2000 of that band: OBJ tiles follow mid-frame bank switches only by band.
//  * Palette: at most four palettes a frame (store_palette with the scanline it applies from); later changes are dropped.
//  * $2004 reads while the screen is on give 0 early in a line and $FF late (not the OAM); with the screen off attribute bytes
//    read back masked to E3. $2004 writes are stored whole.
//  * Sprite 0 hit is a timeout set from the sprite's position alone: (Y + 2) lines + X dots - 12 CPU cycles into the frame,
//    when sprite 0 has any pixel at all and the screen is on; the BG is never consulted.
//  * Colours: Loopy's palette as PocketNES carries it, with its typo in row 3 (one byte missing in colour $35 shifts $35-$37),
//    through the GBA's 15-bit colour (gamma 0).
// Shared with nesDS (TileHostPpu): the register front end (PocketNES's mid-line scroll-X timeouts, sprite-0 refinement and
// the unscaled / auto-scroll display types are not modelled), the host map with blank rows 240-255, per-line BG enable.
// The GBA screen is letterboxed in the BrokenNes frame (black around it).
using System;
using System.IO;

namespace NesEmulator
{
public sealed class PPU_PNES : TileHostPpu
{
    public override string CoreName => "PocketNES";
    public override string Description => "PocketNES's GBA tile-engine picture (9c07f3d): the 240x160 screen shows NES columns 8-247 and three lines of every four from line 16, the dropped line twitching per frame; sprites scaled to 0.75 as affine OBJs, banked by 8-line band; at most four palettes a frame.";

    const int HostW = 240, HostH = 160, Left = 8, Top = (H - HostH) / 2, WindowTop = 16;

    // up to four palettes a frame, each with the line it starts at
    readonly byte[] palSets = new byte[4 * 32];
    readonly int[] palStart = new int[4];
    int palCount;
    bool palDirty;
    readonly byte[] sprBand = new byte[64];   // 1 = this OBJ's tiles were fetched for the frame

    public PPU_PNES(Bus bus) : base(bus) { }

    protected override void NewFrame()
    {
        base.NewFrame();   // scroll latch, OAM snapshot (frameOam), band fetch below replaces the line-0 tile snapshot
        Array.Clear(sprBand);
        palCount = 0; palDirty = false;
        StorePalette(0);
    }

    void StorePalette(int line)
    {
        if (palCount >= 4 || line >= 240) return;
        Array.Copy(pal, 0, palSets, palCount * 32, 32);
        palStart[palCount] = line;
        palCount++;
    }

    protected override void PaletteWrite(int a, byte v)
    {
        base.PaletteWrite(a, v);
        palDirty = true;
    }

    protected override void AfterBgLine(int line)
    {
        if (palDirty) { palDirty = false; StorePalette(line); }
        // OBJ tiles: fetched at the band (y & 0xF8) holding each sprite's Y, with that band's banks and $2000
        if ((line & 7) != 0) return;
        bool tall = (frameSprCtrl & 0x20) != 0;
        for (int s = 0; s < 64; s++)
        {
            int y = frameOam[s * 4];
            if (y > 239 || (y & 0xF8) != line) continue;
            int tile = frameOam[s * 4 + 1];
            int baseAddr = tall ? ((tile & 1) << 12) | ((tile & 0xFE) << 4) : ((ctrl0 & 0x08) << 9) | (tile << 4);
            for (int r = 0; r < (tall ? 16 : 8); r++)
            {
                int a = baseAddr + (r & 7) + ((r & 8) << 1);
                sprPat[s * 32 + r * 2] = Mem(a);
                sprPat[s * 32 + r * 2 + 1] = Mem(a + 8);
            }
            sprBand[s] = 1;
        }
    }

    protected override byte OamDataRead()
    {
        if (scanline < 240 && Rendering) return dot < 256 ? (byte)0 : (byte)0xFF;
        byte v = oam[oamAdr];
        if ((oamAdr & 3) == 2) v &= 0xE3;
        return v;
    }

    protected override void OamDataWrite(byte value)
    {
        oam[oamAdr] = bus?.Butcher is { SpritesActive: true } sb ? sb.FilterOamWrite(oamAdr, value) : value;
        oamAdr++;
    }

    protected override bool Sprite0HitNow()
    {
        if (sprite0Y == 0x200) return false;
        long now = (long)(scanline == 261 ? -1 : scanline) * 341 + dot;
        return now >= sprite0Y;   // sprite0Y holds the hit time in dots from line 0 (0x200 = none)
    }

    protected override void PpuSync(int line)
    {
        if (line == 0) ArmSprite0();
        base.PpuSync(line);
    }

    void ArmSprite0()
    {
        sprite0Y = 0x200;
        if (!Rendering) return;
        int y = frameOam[0], x = frameOam[3];
        if (y >= 240 || x >= 255) return;
        bool tall = (frameSprCtrl & 0x20) != 0;
        int tile = frameOam[1];
        int baseAddr = tall ? ((tile & 1) << 12) | ((tile & 0xFE) << 4) : ((ctrl0 & 0x08) << 9) | (tile << 4);
        int any = 0;
        for (int r = 0; r < (tall ? 16 : 8) && any == 0; r++) { int a = baseAddr + (r & 7) + ((r & 8) << 1); any |= Mem(a) | Mem(a + 8); }
        if (any == 0) return;
        int t = (y + 2) * 341 + x - 36;
        sprite0Y = t == 0x200 ? t + 1 : t;
    }

    readonly int[] rowLine = new int[HostH];

    protected override void Compose()
    {
        int twitch = (int)(frame & 1);
        int skipPos = twitch != 0 ? 3 : 1;
        int n = 0;
        for (int l = WindowTop; l < H && n < HostH; l++)
        {
            if ((l & 3) == skipPos) continue;
            rowLine[n++] = l;
        }
        for (; n < HostH; n++) rowLine[n] = -1;

        bool tall = (frameSprCtrl & 0x20) != 0;
        int sh = tall ? 16 : 8, ch = sh * 3 / 4;
        var fb = Fb;
        Span<byte> sc = stackalloc byte[8];
        Span<byte> sprC = stackalloc byte[HostW];
        Span<byte> sprA = stackalloc byte[HostW];
        for (int y = 0; y < H; y++)
        {
            int o = y * W * 4, hr = y - Top;
            if (hr < 0 || hr >= HostH || rowLine[hr] < 0) { for (int x = 0; x < W; x++, o += 4) Black(fb, o); continue; }
            int l = rowLine[hr];
            byte c1 = lineCtrl1[l];
            bool bgOn = (c1 & 0x08) != 0, objOn = (c1 & 0x10) != 0;
            int pi = 0;
            for (int k = 1; k < palCount; k++) if (palStart[k] <= l) pi = k;
            var p = palSets.AsSpan(pi * 32, 32);
            sprC.Clear();
            if (objOn)
            {
                for (int s = 63; s >= 0; s--)
                {
                    int sy = frameOam[s * 4];
                    if (sy > 239 || sprBand[s] == 0) continue;
                    int ly = tall ? (sy - 5) & 0xFF : sy;
                    int boxTop = (sbyte)(byte)((0xEF000000u + (uint)ly * 0x00C00000u) >> 24);
                    int centre = boxTop + sh;
                    int r0 = hr - (centre - ch / 2);
                    if (r0 < 0 || r0 >= ch) continue;
                    int tr = (r0 * 4 + 2) / 3;
                    if (tr >= sh) tr = sh - 1;
                    SpriteRow(s, tr, tall, sc);
                    int attr = frameOam[s * 4 + 2];
                    int sx = frameOam[s * 4 + 3] - Left + ((attr & 0x40) != 0 ? 1 : 0);
                    for (int i = 0; i < 8; i++)
                    {
                        int px = sx + i;
                        if (px < 0 || px >= HostW || sc[i] == 0) continue;
                        sprC[px] = (byte)(0x10 | ((attr & 3) << 2) | sc[i]);
                        sprA[px] = (byte)(attr & 0x20);
                    }
                }
            }
            var bgl = lineBg.AsSpan(l * W, W);
            for (int x = 0; x < W; x++, o += 4)
            {
                int hx = x - Left;
                if (hx < 0 || hx >= HostW) { Black(fb, o); continue; }
                int bg = bgOn ? bgl[hx + Left] : 0;
                int sp = sprC[hx];
                int col = sp != 0 && (sprA[hx] == 0 || bg == 0) ? p[sp] : bg != 0 ? p[bg] : p[0];
                PutColour(fb, o, PocketPal, col);
            }
        }
    }

    protected override void SaveExtra(BinaryWriter w)
    {
        w.Write(palSets); foreach (var v in palStart) w.Write(v); w.Write(palCount); w.Write(palDirty); w.Write(sprBand);
    }

    protected override void LoadExtra(BinaryReader r)
    {
        r.Read(palSets, 0, palSets.Length); for (int i = 0; i < 4; i++) palStart[i] = r.ReadInt32(); palCount = r.ReadInt32(); palDirty = r.ReadBoolean(); r.Read(sprBand, 0, 64);
    }

    // PocketNES nes_rgb, verbatim: the row starting at colour $30 lists "255,199,219,255" (a stray byte) and ends "219,171",
    // so colours $35-$37 come out shifted. 192 bytes as stored.
    static readonly byte[] PocketPal = {
        117,117,117, 39,27,143, 0,0,171, 71,0,159, 143,0,119, 171,0,19, 167,0,0, 127,11,0,
        67,47,0, 0,71,0, 0,81,0, 0,63,23, 27,63,95, 0,0,0, 0,0,0, 0,0,0,
        188,188,188, 0,115,239, 35,59,239, 131,0,243, 191,0,191, 231,0,91, 219,43,0, 203,79,15,
        139,115,0, 0,151,0, 0,171,0, 0,147,59, 0,131,139, 0,0,0, 0,0,0, 0,0,0,
        255,255,255, 63,191,255, 95,151,255, 167,139,253, 247,123,255, 255,119,183, 255,119,99, 255,155,59,
        243,191,63, 131,211,19, 79,223,75, 88,248,152, 0,235,219, 102,102,102, 0,0,0, 0,0,0,
        255,255,255, 171,231,255, 199,215,255, 215,203,255, 255,199,255, 255,199,219,255, 191,179,255, 219,171,
        255,231,163, 227,255,163, 171,243,191, 179,255,207, 159,255,243, 209,209,209, 0,0,0, 0,0,0,
    };
}
}
