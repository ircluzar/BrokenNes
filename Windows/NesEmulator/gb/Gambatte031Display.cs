// Gambatte031Display - Gambatte 0.3.1's picture code (2007-10-26, the release before 0.4.0 brought cycle-accurate sprite
// mapping and WX sprite timing), ported from the release tarball gambatte_src-0.3.1.tar.gz, libgambatte/src/video.cpp:
// do_update, dmg_draw, cgb_draw, bg_drawPixels, cgb_bg_drawPixels, drawSprites, cgb_drawSprites, prioritizedBG_mask,
// cgb_prioritizedBG_mask, cgb_toplayerBG_mask, gbcToRgb32, the default DMG greys; video/sprite_mapper.cpp mapSprites;
// video/sc_reader.cpp (SCX/SCY 8-dot reader); video/wx_reader.h, video/wy.cpp + wy.h (WY readers 1, 2, 4),
// video/we_master_checker.* (the window "master" enable) and the drawStartCycle rule of scxChange.
//
//   Gambatte, Copyright (C) 2007 by Sindre Aamås. GNU General Public License, version 2. See THIRD_PARTY_NOTICES.md
//   (PPU harvest) and gb/THIRD_PARTY_NOTICES.md.
//
// PPU harvest climb (docs/projects/ppu-harvest-catalog.md, runners-up): an art instrument, ported FAITHFULLY with its
// flaws. It is the Game Boy picture core "GMB031": the stock chip (PPU_GB) keeps the console's timing and this renderer
// draws beside it (PPU_GB.TickObserver, once an M-cycle), pixel by pixel as the dots pass, the way Gambatte's
// update() catches up.
//
// Characteristic jank, kept on purpose:
//  * Pixels leave at a fixed rate from line dot 90 + (SCX & 7): sprites never stall the picture, so a mid-line raster
//    write lands at the same x whether the line has 0 or 10 sprites (Gambatte only charged the stall to the mode-0 IRQ).
//  * SCX/SCY go through Gambatte's ScReader: a write reaches the picture only at the next 8-dot boundary, one stage
//    late (8-16 dots), and the fine SCX bits are read live while the coarse ones lag.
//  * Sprites are drawn after the whole line, onto the finished pixels, with the palettes, scroll and window as they
//    stand at the line's end; the behind-background mask is rebuilt from the tile map then, not from what was drawn.
//  * The sprite map is the whole frame's 10-per-line table, rebuilt only at dot 82 of the line after an OAM or size
//    change (pre-0.4.0 sprite mapping); the sprite's X, tile and row come from live OAM at the line's end, so an OAM
//    change mid-line draws rows from outside the sprite (address wrapped in VRAM).
//  * A behind-background sprite drawn over a lower sprite lets the lower sprite show through where it is masked.
//  * DMG: LCDC bit 0 off fills the line with BGP colour 0 but the window still masks behind-background sprites.
//  * WY reaches the window only through Gambatte's readers (at dots 448/452 under their conditions, or at the next
//    frame); the "weird-ass WE-master enable on the WY line" bumps WY by one until the frame ends.
//  * Gambatte's colour matrix (r*13+g*2+b)/2 ... for Game Boy Color colours (dim, warm), plain greys 255/170/85/0 for
//    the DMG; a DMG game on a Color console is drawn as DMG (0.3.1 had no compatibility palettes).
// Left out (they only move the event times of things not drawn here): WyReader3 (WX-relative WY read), the window
// enable/disable checkers (LCDC bit 5 is read live), double-speed's +4 dot draw offset.
namespace NesEmulator.Gb;

internal sealed class Gambatte031Display : IGbForeignView
{
    public const int W = 160, H = 144;
    public uint[] Screen { get; } = new uint[W * H];
    private readonly PPU_GB gb;

    // ScReader
    private uint scx0, scx1, scy0, scy1; private int scStep = int.MinValue;
    // drawing
    private uint drawStartCycle = 90, xpos; private int curLy = -1;
    private uint winYPos = 0xFF;
    // WX reader
    private uint wx;
    // WY readers + WE master
    private uint wy, wySrcSeen; private bool r1, r2, r4, weMaster;
    private bool did82, did448, did452;
    // sprite map (12 shorts a line: 10 entries, count, cycles)
    private readonly ushort[] spritemap = new ushort[144 * 12];
    private readonly byte[] oamSnap = new byte[0xA0]; private bool largeSnap; private bool mapValid;
    private readonly ushort[] sortBuf = new ushort[11];

    private static readonly byte[] Xflip = BuildXflip();
    private static readonly uint[] DmgGrey = { 3 * 85 * 0x010101u, 2 * 85 * 0x010101u, 1 * 85 * 0x010101u, 0 };

    public Gambatte031Display(PPU_GB ppu)
    {
        gb = ppu; ppu.TickObserver += Run; System.Array.Fill(Screen, 0xFFFFFFFFu);
        scx0 = scx1 = gb.Scx; scy0 = scy1 = gb.Scy; wx = gb.Wx; wy = wySrcSeen = gb.Wy;
    }
    public void Detach() => gb.TickObserver -= Run;

    private bool Cgb => gb.Model == GbModel.Cgb && !gb.CompatMode;
    private bool Large => (gb.Lcdc & 0x04) != 0;

    private static byte[] BuildXflip()
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++) { int r = 0; for (int b = 0; b < 8; b++) if ((i & (1 << b)) != 0) r |= 0x80 >> b; t[i] = (byte)r; }
        return t;
    }

    private void Run()
    {
        if (!gb.LcdOn) { curLy = -1; winYPos = 0xFF; weMaster = false; scStep = int.MinValue; return; }
        int ly = gb.LY, dot = gb.Dot;
        if (ly != curLy)
        {
            if (curLy >= 0)
            {
                if (curLy < H && xpos < 160) Draw(xpos, (uint)curLy, 160);   // the line's end: finish it (update() at the line event)
                LineEvents(curLy, 456);
                if (ly == 0) FrameStart();
            }
            curLy = ly; xpos = 0; did82 = did448 = did452 = false;
        }
        // WY source changes arm the readers (wyChange)
        if (gb.Wy != wySrcSeen) { wySrcSeen = gb.Wy; r1 = r2 = r4 = true; }
        ScReaderStep(ly, dot);
        // scxChange: before line cycle 82 the draw start follows SCX & 7 at once (later writes wait for the next line)
        if (dot < 82) drawStartCycle = 90 + (uint)(gb.Scx & 7);
        // WxReader: a WX write lands at dot 82 + scx&7 + min(new, old) of this line or the next
        if (gb.Wx != wx && dot >= 82 + (gb.Scx & 7) + System.Math.Min(gb.Wx, (int)wx)) wx = gb.Wx;
        if (!did82 && dot >= 82) { did82 = true; MaybeRemap(); }
        if (ly < H)
        {
            uint endX = dot < drawStartCycle ? 0 : (uint)dot - drawStartCycle;
            if (endX > 160) endX = 160;
            if (xpos < endX) { Draw(xpos, (uint)ly, endX); xpos = endX; }
        }
        LineEvents(ly, dot);
    }

    private void FrameStart()
    {
        if (r4) { wy = gb.Wy; r4 = false; }   // WyReader4: frame cycle 0
    }

    private void LineEvents(int ly, int dot)
    {
        if (ly >= H && ly == H) { winYPos = 0xFF; weMaster = false; }   // do_update at ly 144
        if (!did448 && dot >= 448)
        {
            did448 = true;
            // WeMasterChecker at frame cycle wy*456+448 (scheduled while WE and WY < 143)
            if ((gb.Lcdc & 0x20) != 0 && gb.Wy < 143 && ly == gb.Wy)
            {
                if (!weMaster && wy == (uint)ly) { wy++; r4 = true; }   // weirdAssWeMasterEnableOnWyLineCase + reader4
                weMaster = true;
            }
            if (r1) { if (gb.Wy >= ly && !weMaster) wy = gb.Wy; r1 = false; }   // WyReader1
        }
        if (!did452 && dot >= 452)
        {
            did452 = true;
            if (r2) { if (wy == (uint)ly + 1 && gb.Wy > wy) wy = gb.Wy; r2 = false; }   // WyReader2
        }
    }

    // ScReader: shifts its two stages every 8 dots, phase (videoCycles - scReadOffset) & 7, scReadOffset = 90.
    private void ScReaderStep(int ly, int dot)
    {
        int fd = ly * 456 + dot - 90;
        int step = fd >= 0 ? fd >> 3 : -((-fd + 7) >> 3);
        if (scStep == int.MinValue) { scStep = step; scx0 = scx1 = gb.Scx; scy0 = scy1 = gb.Scy; return; }
        int n = step - scStep; if (n < 0) n = 2; if (n > 2) n = 2;
        scStep = step;
        while (n-- > 0) { scy0 = scy1; scy1 = gb.Scy; scx0 = scx1; scx1 = gb.Scx; }
    }
    private uint ScxEff => (scx0 & ~7u) | (uint)(gb.Scx & 7);
    private uint ScyEff => scy0;

    // ------------------------------------------------------------------ sprite mapper
    private void MaybeRemap()
    {
        bool large = Large;
        if (mapValid && large == largeSnap && System.MemoryExtensions.SequenceEqual<byte>(gb.Oam, oamSnap)) return;
        System.Array.Copy(gb.Oam, oamSnap, 0xA0); largeSnap = large; mapValid = true;
        MapSprites(large);
    }

    private void MapSprites(bool large)
    {
        for (int l = 0; l < 144; l++) { spritemap[l * 12 + 10] = 0; spritemap[l * 12 + 11] = 0; }
        uint spriteHeight = large ? 16u : 8u;
        var oam = gb.Oam;
        for (int i = 0; i < 0xA0; i += 4)
        {
            uint bottom_pos = unchecked((uint)oam[i] - (17 - spriteHeight));
            if (bottom_pos >= 143 + spriteHeight) continue;
            uint spx = oam[i + 1];
            ushort value = (ushort)((spx << 8) | (uint)i);
            int t = bottom_pos < spriteHeight ? 0 : (int)((bottom_pos + 1 - spriteHeight) * 12);
            int end = bottom_pos >= 143 ? 143 * 12 : (int)(bottom_pos * 12);
            do { if (spritemap[t + 10] < 10) spritemap[t + spritemap[t + 10]++] = value; t += 12; } while (t <= end);
        }
        bool cgb = Cgb;
        for (int m = 0; m < 144 * 12; m += 12)
        {
            int n = spritemap[m + 10]; if (n == 0) continue;
            if (cgb) { System.Array.Copy(spritemap, m, sortBuf, 0, 11); System.Array.Sort(sortBuf, 0, n); }   // CGB: sorted copy (cycle sum only); the map keeps OAM order
            else System.Array.Sort(spritemap, m, n);
        }
    }

    // ------------------------------------------------------------------ the line
    private bool EnableWindow(uint ypos) =>
        (weMaster || ypos == wy) && (gb.Lcdc & 0x20) != 0 && wy <= ypos && wx < 0xA7;

    private uint TileMap(bool win) => (gb.Lcdc & (win ? 0x40 : 0x08)) != 0 ? 0x1C00u : 0x1800u;
    private uint Sign => (gb.Lcdc & 0x10) != 0 ? 0u : 0x80u;
    private uint TileDataBase => (gb.Lcdc & 0x10) != 0 ? 0u : 0x1000u;

    private void Draw(uint x, uint ypos, uint endX)
    {
        if (Cgb) CgbDraw(x, ypos, endX); else DmgDraw(x, ypos, endX);
    }

    private void DmgDraw(uint x, uint ypos, uint endX)
    {
        uint effectiveScx = ScxEff;
        bool enableWindow = EnableWindow(ypos);
        if (enableWindow && winYPos == 0xFF) winYPos = 0;
        int line = (int)ypos * W;
        if ((gb.Lcdc & 0x01) != 0)
        {
            if (!(enableWindow && wx <= x + 7))
            {
                uint fby = ScyEff + ypos;
                uint end = System.Math.Min(enableWindow ? wx - 7 : 160u, endX);
                BgDrawPixels(line, x, end, effectiveScx, TileMap(false) + (fby & 0xF8) * 4, TileDataBase + (fby & 7) * 2);
            }
            if (enableWindow && endX + 7 > wx)
            {
                uint start = System.Math.Max(wx < 7 ? 0u : wx - 7, x);
                BgDrawPixels(line, start, endX, 0u - (wx - 7), TileMap(true) + (winYPos & 0xF8) * 4, TileDataBase + (winYPos & 7) * 2);
            }
        }
        else { uint c = DmgPal(gb.Bgp, 0); for (uint i = x; i < endX; i++) Screen[line + i] = c; }
        if (endX == 160)
        {
            if ((gb.Lcdc & 0x02) != 0) DrawSprites(line, ypos);
            if (enableWindow) ++winYPos;
        }
    }

    private void CgbDraw(uint x, uint ypos, uint endX)
    {
        uint effectiveScx = ScxEff;
        bool enableWindow = EnableWindow(ypos);
        if (enableWindow && winYPos == 0xFF) winYPos = 0;
        int line = (int)ypos * W;
        if (!(enableWindow && wx <= x + 7))
        {
            uint fby = ScyEff + ypos;
            uint end = System.Math.Min(enableWindow ? wx - 7 : 160u, endX);
            CgbBgDrawPixels(line, x, end, effectiveScx, TileMap(false) + (fby & 0xF8) * 4, TileDataBase, fby & 7);
        }
        if (enableWindow && endX + 7 > wx)
        {
            uint start = System.Math.Max(wx < 7 ? 0u : wx - 7, x);
            CgbBgDrawPixels(line, start, endX, 0u - (wx - 7), TileMap(true) + (winYPos & 0xF8) * 4, TileDataBase, winYPos & 7);
        }
        if (endX == 160)
        {
            if ((gb.Lcdc & 0x02) != 0) CgbDrawSprites(line, ypos);
            if (enableWindow) ++winYPos;
        }
    }

    private byte V(uint a) => gb.Vram[a & 0x3FFF];

    private void BgDrawPixels(int line, uint x, uint end, uint scx, uint tilemap, uint tiledata)
    {
        uint sign = Sign;
        while (x < end)
        {
            uint tile = V(tilemap + ((scx + x) >> 3 & 0x1F));
            uint d = tiledata + ((tile - (tile & sign) * 2) << 4);
            uint byte1 = V(d), byte2 = (uint)V(d + 1) << 1;
            int tmp = 7 - (int)((scx + x) & 7);
            do { Screen[line + x++] = DmgPal(gb.Bgp, (int)((byte2 >> tmp & 2) | (byte1 >> tmp & 1))); } while (tmp-- > 0 && x < end);
        }
    }

    private void CgbBgDrawPixels(int line, uint x, uint end, uint scx, uint tilemap, uint tiledata, uint tileline)
    {
        uint sign = Sign;
        while (x < end)
        {
            uint map = tilemap + ((scx + x) >> 3 & 0x1F);
            uint tile = V(map), attributes = V(map + 0x2000);
            uint d = tiledata + (attributes << 10 & 0x2000) + ((tile - (tile & sign) * 2) << 4) + ((attributes & 0x40) != 0 ? 7 - tileline : tileline) * 2;
            uint byte1 = V(d), byte2 = V(d + 1);
            if ((attributes & 0x20) != 0) { byte1 = Xflip[byte1]; byte2 = Xflip[byte2]; }
            byte2 <<= 1;
            int pal = (int)(attributes & 7) * 4;
            int tmp = 7 - (int)((scx + x) & 7);
            do { Screen[line + x++] = CgbPal(gb.BgPalRam, pal + (int)((byte2 >> tmp & 2) | (byte1 >> tmp & 1))); } while (tmp-- > 0 && x < end);
        }
    }

    private uint PrioritizedBgMask(uint spx, uint bgStart, uint bgEnd, uint scx, uint tilemap, uint tiledata, uint sign)
    {
        uint spStart = spx < bgStart + 8 ? bgStart - (spx - 8) : 0;
        uint pos = scx + (spx - 8) + spStart;
        uint tile = V(tilemap + (pos >> 3 & 0x1F));
        uint d = tiledata + ((tile - (tile & sign) * 2) << 4);
        uint bgbyte = (uint)(V(d) | V(d + 1));
        uint offset = pos & 7;
        if (offset != 0)
        {
            bgbyte <<= (int)offset;
            tile = V(tilemap + ((pos >> 3) + 1 & 0x1F));
            d = tiledata + ((tile - (tile & sign) * 2) << 4);
            bgbyte |= (uint)(V(d) | V(d + 1)) >> (int)(8 - offset);
        }
        bgbyte >>= (int)(spStart & 31);
        uint spEnd = spx > bgEnd ? bgEnd - (spx - 8) : 8;
        return ~bgbyte | (spEnd >= 32 ? 0 : 0xFFu >> (int)spEnd);
    }

    private uint CgbBgMask(bool prioritized, uint spx, uint bgStart, uint bgEnd, uint scx, uint tilemap, uint tiledata, uint tileline, uint sign)
    {
        uint spStart = spx < bgStart + 8 ? bgStart - (spx - 8) : 0;
        uint pos = scx + (spx - 8) + spStart;
        uint Byte(uint map)
        {
            uint attributes = V(map + 0x2000);
            if (!prioritized && (attributes & 0x80) == 0) return 0;   // cgb_toplayerBG_mask: only BG-priority tiles count
            uint tile = V(map);
            uint d = tiledata + (attributes << 10 & 0x2000) + ((tile - (tile & sign) * 2) << 4) + ((attributes & 0x40) != 0 ? 7 - tileline : tileline) * 2;
            uint b = (uint)(V(d) | V(d + 1));
            return (attributes & 0x20) != 0 ? Xflip[b] : b;
        }
        uint bgbyte = Byte(tilemap + (pos >> 3 & 0x1F));
        uint offset = pos & 7;
        if (offset != 0) { bgbyte <<= (int)offset; bgbyte |= Byte(tilemap + ((pos >> 3) + 1 & 0x1F)) >> (int)(8 - offset); }
        bgbyte >>= (int)(spStart & 31);
        uint spEnd = spx > bgEnd ? bgEnd - (spx - 8) : 8;
        return ~bgbyte | (spEnd >= 32 ? 0 : 0xFFu >> (int)spEnd);
    }

    private void DrawSprites(int line, uint ypos)
    {
        uint scy = ScyEff + ypos;
        uint wxs = wx < 7 ? 0 : wx - 7;
        bool enableWindow = EnableWindow(ypos);
        bool bgEnable = (gb.Lcdc & 0x01) != 0, large = Large;
        uint sign = Sign, tdata = TileDataBase;
        var oam = gb.Oam;
        int m = (int)ypos * 12;
        for (int i = spritemap[m + 10] - 1; i >= 0; --i)
        {
            int si = spritemap[m + i] & 0xFF;
            uint spx = oam[si + 1];
            if (spx >= 168 || spx == 0) continue;
            uint spLine = ypos - ((uint)oam[si] - 16);
            uint spTile = oam[si + 2], attributes = oam[si + 3];
            if (large) { if ((attributes & 0x40) != 0) spLine = 15 - spLine; if (spLine < 8) spTile &= 0xFE; else { spLine -= 8; spTile |= 1; } }
            else if ((attributes & 0x40) != 0) spLine = 7 - spLine;
            uint d = ((spTile << 4) + spLine * 2) & 0x1FFF;   // a stale map row lands outside the sprite (wrapped in VRAM)
            uint byte1 = gb.Vram[d], byte2 = gb.Vram[(d + 1) & 0x1FFF];
            if ((attributes & 0x20) != 0) { byte1 = Xflip[byte1]; byte2 = Xflip[byte2]; }
            if ((attributes & 0x80) != 0)
            {
                uint mask = 0xFF;
                if (bgEnable && !(enableWindow && (wxs == 0 || spx >= wxs + 8)))
                    mask = PrioritizedBgMask(spx, 0, enableWindow ? wxs : 160, ScxEff, TileMap(false) + ((scy & 0xF8) << 2), tdata + ((scy & 7) << 1), sign);
                if (enableWindow && spx > wxs)
                    mask &= PrioritizedBgMask(spx, wxs, 160, 0u - wxs, TileMap(true) + ((winYPos & 0xF8) << 2), tdata + ((winYPos & 7) << 1), sign);
                byte1 &= mask; byte2 &= mask;
            }
            byte2 <<= 1;
            byte obp = (attributes & 0x10) != 0 ? gb.Obp1 : gb.Obp0;
            PutSprite(line, spx, byte1, byte2, c => DmgPal(obp, c));
        }
    }

    private void CgbDrawSprites(int line, uint ypos)
    {
        uint scy = ScyEff + ypos;
        uint wxs = wx < 7 ? 0 : wx - 7;
        bool enableWindow = EnableWindow(ypos);
        bool bgEnable = (gb.Lcdc & 0x01) != 0, large = Large;
        uint sign = Sign, tdata = TileDataBase;
        var oam = gb.Oam;
        int m = (int)ypos * 12;
        for (int i = spritemap[m + 10] - 1; i >= 0; --i)
        {
            int si = spritemap[m + i] & 0xFF;
            uint spx = oam[si + 1];
            if (spx >= 168 || spx == 0) continue;
            uint spLine = ypos - ((uint)oam[si] - 16);
            uint spTile = oam[si + 2], attributes = oam[si + 3];
            if (large) { if ((attributes & 0x40) != 0) spLine = 15 - spLine; if (spLine < 8) spTile &= 0xFE; else { spLine -= 8; spTile |= 1; } }
            else if ((attributes & 0x40) != 0) spLine = 7 - spLine;
            uint d = ((attributes * 0x400) & 0x2000) + (((spTile << 4) + spLine * 2) & 0x1FFF);
            uint byte1 = gb.Vram[d & 0x3FFF], byte2 = gb.Vram[(d + 1) & 0x3FFF];
            if ((attributes & 0x20) != 0) { byte1 = Xflip[byte1]; byte2 = Xflip[byte2]; }
            if (bgEnable)
            {
                bool prio = (attributes & 0x80) != 0;
                uint mask = 0xFF;
                if (!(enableWindow && (wxs == 0 || spx >= wxs + 8)))
                    mask = CgbBgMask(prio, spx, 0, enableWindow ? wxs : 160, ScxEff, TileMap(false) + ((scy & 0xF8) << 2), tdata, scy & 7, sign);
                if (enableWindow && spx > wxs)
                    mask &= CgbBgMask(prio, spx, wxs, 160, 0u - wxs, TileMap(true) + ((winYPos & 0xF8) << 2), tdata, winYPos & 7, sign);
                byte1 &= mask; byte2 &= mask;
            }
            byte2 <<= 1;
            int pal = (int)(attributes & 7) * 4;
            PutSprite(line, spx, byte1, byte2, c => CgbPal(gb.ObjPalRam, pal + c));
        }
    }

    private void PutSprite(int line, uint spx, uint byte1, uint byte2, System.Func<int, uint> colour)
    {
        uint end = spx >= 160 ? 160 : spx;
        uint x = spx <= 8 ? 0 : spx - 8;
        int t = 7 - (int)(x - (spx - 8));
        while (x < end)
        {
            int c = (int)((byte2 >> t & 2) | (byte1 >> t & 1));
            if (c != 0) Screen[line + x] = colour(c);
            --t; ++x;
        }
    }

    private static uint DmgPal(byte reg, int c) => 0xFF000000u | DmgGrey[(reg >> (c * 2)) & 3];

    // gbcToRgb32
    private static uint CgbPal(byte[] ram, int i)
    {
        int bgr15 = ram[i * 2] | ram[i * 2 + 1] << 8;
        uint r = (uint)bgr15 & 0x1F, g = (uint)(bgr15 >> 5) & 0x1F, b = (uint)(bgr15 >> 10) & 0x1F;
        return 0xFF000000u | (((r * 13 + g * 2 + b) >> 1) << 16) | ((g * 3 + b) << 9) | ((r * 3 + g * 2 + b * 11) >> 1);
    }
}
