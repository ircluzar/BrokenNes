using System;

namespace NesEmulator.Sega;

// Picture generation: Mode 4 (background from the name table with scroll, priority, flips and two palettes; up to 8 sprites per line out of 64) and the TMS9918 modes 0-3.
// Sprites for a line are worked out when the line begins (the chip fetches them during the line before); the background is drawn pixel by pixel, lazily, so a write in the
// middle of a line (register, name table, pattern, palette) shows from exactly that pixel on.

public sealed partial class SmsVdp
{
    private readonly byte[] sprLine = new byte[256];       // Mode 4: 0 = no sprite pixel, else the sprite palette colour 1..15
    private readonly uint[] lineBuf = new uint[256];       // TMS modes: the finished line

    /// <summary>The 16 colours of the TMS9918 (colour 0, transparent, shows the backdrop). Used by Modes 0-3 on every revision.</summary>
    private static readonly uint[] TmsPalette =
    {
        0xFF000000, 0xFF000000, 0xFF21C842, 0xFF5EDC78, 0xFF5455ED, 0xFF7D76FC, 0xFFD4524D, 0xFF42EBF5,
        0xFFFC5554, 0xFFFF7978, 0xFFD4C154, 0xFFE6CE80, 0xFF21B03B, 0xFFC95BBA, 0xFFCCCCCC, 0xFFFFFFFF,
    };

    private bool Mode4Active => HasMode4 && (Regs[0] & 0x04) != 0;

    /// <summary>The colour shown when nothing else is: the sprite palette entry register 7 selects (Mode 4), or the TMS colour of its low nibble.</summary>
    private uint Backdrop => Mode4Active ? palArgb[16 + (Regs[7] & 0x0F)] : TmsPalette[Regs[7] & 0x0F];

    /// <summary>Called when a picture line begins (before any of its pixels): works out which sprites sit on it.</summary>
    private void BeginLineSprites()
    {
        if (Line >= activeHeight) return;
        if (Mode4Active) EvaluateSpritesMode4();
        else BuildTmsLine();
    }

    // =================================================================================== pixel output

    private void RenderSpan(int x0, int x1)
    {
        int row = Line * 256;
        if ((Regs[1] & 0x40) == 0)
        {
            uint bd = Backdrop;
            for (int x = x0; x < x1; x++) Frame[row + x] = bd;
            return;
        }
        if (Mode4Active) RenderMode4(row, x0, x1);
        else
            for (int x = x0; x < x1; x++) Frame[row + x] = lineBuf[x];
    }

    // =================================================================================== Mode 4

    private void RenderMode4(int row, int x0, int x1)
    {
        bool ext = activeHeight != 192;
        bool hLock = (Regs[0] & 0x40) != 0 && Line < 16;
        bool vLock = (Regs[0] & 0x80) != 0;
        bool maskCol = (Regs[0] & 0x20) != 0;
        int hs = hLock ? 0 : hscrollLatched;
        int fs = hs & 7, mapCol0 = hs >> 3;

        int ntBase = ext ? ((Regs[2] & 0x0C) << 10) | 0x700 : (Regs[2] & 0x0E) << 10;
        bool sms1Mask = Revision == SmsVdpRevision.Sms1 && (Regs[2] & 1) == 0;
        uint backdrop = palArgb[16 + (Regs[7] & 0x0F)];

        int x = x0;
        while (x < x1)
        {
            // one tile column at a time: screen column c covers x = fs + 8c .. fs + 8c + 7
            int c = (x - fs) >> 3;
            int tileStart = fs + 8 * c;
            int tileEnd = Math.Min(x1, tileStart + 8);
            int vs = vLock && c >= 24 ? 0 : vscrollLatched;
            int yy = Line + vs;
            yy = ext ? yy & 0xFF : yy % 224;
            int ty = yy >> 3, fy = yy & 7;
            int mapCol = (c - mapCol0) & 31;
            int nt = ntBase + ty * 64 + mapCol * 2;
            if (sms1Mask) nt &= ~0x400;
            nt &= 0x3FFF;
            int entry = Vram[nt] | Vram[(nt + 1) & 0x3FFF] << 8;
            int tile = entry & 0x1FF;
            bool hflip = (entry & 0x200) != 0, vflip = (entry & 0x400) != 0, pal2 = (entry & 0x800) != 0, prio = (entry & 0x1000) != 0;
            int a = (tile * 32 + (vflip ? 7 - fy : fy) * 4) & 0x3FFF;
            int p0 = Vram[a], p1 = Vram[a + 1], p2 = Vram[a + 2], p3 = Vram[a + 3];
            int palBase = pal2 ? 16 : 0;
            for (; x < tileEnd; x++)
            {
                if (maskCol && x < 8) { Frame[row + x] = backdrop; continue; }
                int px = x - tileStart;
                int bit = hflip ? px : 7 - px;
                int color = ((p0 >> bit) & 1) | ((p1 >> bit) & 1) << 1 | ((p2 >> bit) & 1) << 2 | ((p3 >> bit) & 1) << 3;
                int spr = sprLine[x];
                int idx = spr != 0 && !(prio && color != 0) ? 16 + spr : palBase + color;
                Frame[row + x] = palArgb[idx];
            }
        }
    }

    private void EvaluateSpritesMode4()
    {
        Array.Clear(sprLine);
        bool tall = (Regs[1] & 2) != 0, zoom = (Regs[1] & 1) != 0;
        int height = (tall ? 16 : 8) * (zoom ? 2 : 1);
        int satBase = (Regs[5] & 0x7E) << 7;
        // the 315-5124 ANDs register 5 bit 0 into address line A7, so the X/tile pairs read from where the Y bytes are
        int xnBase = Revision == SmsVdpRevision.Sms1 && (Regs[5] & 1) == 0 ? satBase : satBase + 0x80;
        int shift = (Regs[0] & 0x08) != 0 ? 8 : 0;
        int tileHi = (Regs[6] & 0x04) != 0 ? 256 : 0;
        bool terminator = activeHeight == 192;
        int found = 0, colX = int.MaxValue;
        for (int i = 0; i < 64; i++)
        {
            int y = Vram[(satBase + i) & 0x3FFF];
            if (y == 0xD0 && terminator) break;
            int r = (Line - y - 1) & 0xFF;
            if (r >= height) continue;
            if (found == 8)
            {
                // the ninth sprite on the line
                if (ovrAtMc == int.MaxValue) ovrAtMc = Math.Max(curMc, OverflowFlagMc);
                break;
            }
            found++;
            int x = Vram[(xnBase + 2 * i) & 0x3FFF] - shift;
            int tn = Vram[(xnBase + 2 * i + 1) & 0x3FFF];
            int srcRow = zoom ? r >> 1 : r;
            int tileNo;
            if (tall) tileNo = ((tn & 0xFE) | tileHi) + (srcRow >> 3); else tileNo = tn | tileHi;
            int a = (tileNo * 32 + (srcRow & 7) * 4) & 0x3FFF;
            int p0 = Vram[a], p1 = Vram[a + 1], p2 = Vram[a + 2], p3 = Vram[a + 3];
            int w = zoom ? 16 : 8;
            for (int k = 0; k < w; k++)
            {
                int sx = x + k;
                if (sx < 0 || sx > 255) continue;
                int bit = 7 - (zoom ? k >> 1 : k);
                int color = ((p0 >> bit) & 1) | ((p1 >> bit) & 1) << 1 | ((p2 >> bit) & 1) << 2 | ((p3 >> bit) & 1) << 3;
                if (color == 0) continue;
                if (sprLine[sx] != 0) { if (sx < colX) colX = sx; }
                else sprLine[sx] = (byte)color;
            }
        }
        if (colX != int.MaxValue && (Regs[1] & 0x40) != 0)
        {
            int t = PixelOriginMc + 2 * colX + CollisionDelayMc;
            if (t < colAtMc) colAtMc = Math.Max(t, curMc);
        }
    }

    // =================================================================================== TMS9918 modes

    private readonly byte[] tmsBg = new byte[256];       // colour 0..15 of the background layer, 0 = transparent
    private readonly byte[] tmsSprOcc = new byte[256];   // 0 none, 1 occupied by a sprite pixel
    private readonly byte[] tmsSprColor = new byte[256];

    private void BuildTmsLine()
    {
        int y = Line;
        bool m3 = (Regs[0] & 0x02) != 0;     // Graphics II
        bool multi = (Regs[1] & 0x08) != 0;  // Multicolor
        bool text = (Regs[1] & 0x10) != 0;   // Text
        Array.Clear(tmsBg);
        uint bdColor = TmsPalette[Regs[7] & 0x0F];
        int ntBase = (Regs[2] & 0x0F) << 10;
        int row = y >> 3, fy = y & 7;
        if (text)
        {
            int fg = Regs[7] >> 4, bg = Regs[7] & 0x0F;
            int pat = (Regs[4] & 7) << 11;
            for (int x = 0; x < 256; x++) lineBuf[x] = bdColor;
            for (int col = 0; col < 40; col++)
            {
                int name = Vram[(ntBase + row * 40 + col) & 0x3FFF];
                int bits = Vram[(pat + name * 8 + fy) & 0x3FFF];
                for (int p = 0; p < 6; p++)
                {
                    int colr = (bits & (0x80 >> p)) != 0 ? fg : bg;
                    lineBuf[8 + col * 6 + p] = colr == 0 ? bdColor : TmsPalette[colr];
                }
            }
            return;   // the text mode has no sprites
        }
        if (multi)
        {
            int pat = (Regs[4] & 7) << 11;
            for (int col = 0; col < 32; col++)
            {
                int name = Vram[(ntBase + row * 32 + col) & 0x3FFF];
                int b = Vram[(pat + name * 8 + (row & 3) * 2 + (fy >> 2)) & 0x3FFF];
                tmsBg[col * 8 + 0] = tmsBg[col * 8 + 1] = tmsBg[col * 8 + 2] = tmsBg[col * 8 + 3] = (byte)(b >> 4);
                tmsBg[col * 8 + 4] = tmsBg[col * 8 + 5] = tmsBg[col * 8 + 6] = tmsBg[col * 8 + 7] = (byte)(b & 0x0F);
            }
        }
        else if (m3)
        {
            int third = row >> 3;
            int patMask = ((Regs[4] & 3) << 8) | 0xFF;
            int colMask = ((Regs[3] & 0x7F) << 3) | 7;
            int patBase = (Regs[4] & 4) << 11, colBase = (Regs[3] & 0x80) << 6;
            for (int col = 0; col < 32; col++)
            {
                int name = Vram[(ntBase + row * 32 + col) & 0x3FFF];
                int idx = (third << 8) | name;
                int bits = Vram[(patBase + ((idx & patMask) << 3) + fy) & 0x3FFF];
                int cl = Vram[(colBase + ((idx & colMask) << 3) + fy) & 0x3FFF];
                for (int p = 0; p < 8; p++) tmsBg[col * 8 + p] = (byte)((bits & (0x80 >> p)) != 0 ? cl >> 4 : cl & 0x0F);
            }
        }
        else
        {
            int pat = (Regs[4] & 7) << 11, colTab = Regs[3] << 6;
            for (int col = 0; col < 32; col++)
            {
                int name = Vram[(ntBase + row * 32 + col) & 0x3FFF];
                int bits = Vram[(pat + name * 8 + fy) & 0x3FFF];
                int cl = Vram[(colTab + (name >> 3)) & 0x3FFF];
                for (int p = 0; p < 8; p++) tmsBg[col * 8 + p] = (byte)((bits & (0x80 >> p)) != 0 ? cl >> 4 : cl & 0x0F);
            }
        }

        // sprites: 32 of them, 4 to a line, 8x8 or 16x16, optionally doubled
        Array.Clear(tmsSprOcc); Array.Clear(tmsSprColor);
        bool big = (Regs[1] & 2) != 0, mag = (Regs[1] & 1) != 0;
        int size = (big ? 16 : 8) << (mag ? 1 : 0);
        int sat = (Regs[5] & 0x7F) << 7, sprPat = (Regs[6] & 7) << 11;
        int onLine = 0, colX = int.MaxValue;
        for (int i = 0; i < 32; i++)
        {
            int sy = Vram[(sat + i * 4) & 0x3FFF];
            if (sy == 0xD0) break;
            int top = (sy > 0xE0 ? sy - 256 : sy) + 1;
            int r = y - top;
            if (r < 0 || r >= size) continue;
            if (++onLine > 4)
            {
                if (fifthSpriteNumber == 0x1F && !fifthSeen) { fifthSeen = true; fifthSpriteNumber = i; }
                tmsFifth = true;
                break;
            }
            int sx = Vram[(sat + i * 4 + 1) & 0x3FFF];
            int name = Vram[(sat + i * 4 + 2) & 0x3FFF];
            int attr = Vram[(sat + i * 4 + 3) & 0x3FFF];
            if ((attr & 0x80) != 0) sx -= 32;
            int colr = attr & 0x0F;
            if (big) name &= 0xFC;
            int rr = mag ? r >> 1 : r;
            int left = Vram[(sprPat + name * 8 + rr) & 0x3FFF];
            int right = big ? Vram[(sprPat + name * 8 + 16 + rr) & 0x3FFF] : 0;
            int w = (big ? 16 : 8) << (mag ? 1 : 0);
            for (int k = 0; k < w; k++)
            {
                int px = sx + k;
                if (px < 0 || px > 255) continue;
                int cc = mag ? k >> 1 : k;
                int bits = cc < 8 ? left : right;
                if ((bits & (0x80 >> (cc & 7))) == 0) continue;
                if (tmsSprOcc[px] != 0) { if (px < colX) colX = px; continue; }
                tmsSprOcc[px] = 1; tmsSprColor[px] = (byte)colr;
            }
        }
        if (colX != int.MaxValue)
        {
            int t = PixelOriginMc + 2 * colX;
            if (t < colAtMc) colAtMc = Math.Max(t, curMc);
        }
        if (tmsFifth) { tmsFifth = false; if (ovrAtMc == int.MaxValue) ovrAtMc = Math.Max(curMc, OverflowFlagMc); }

        for (int x = 0; x < 256; x++)
        {
            int c = tmsBg[x];
            if (tmsSprOcc[x] != 0 && tmsSprColor[x] != 0) c = tmsSprColor[x];
            lineBuf[x] = c == 0 ? bdColor : TmsPalette[c];
        }
    }

    private bool fifthSeen, tmsFifth;
}
