// PPU_FCEU - the old FCE Ultra / FCEUX picture code, the last src/ppu.cpp before "newppu", ported from
// TASEmulators/fceux @2b2d70b (2008-08-08): src/ppu.cpp (1473 lines) and src/pputile.inc.
//
//   FCE Ultra - NES/Famicom Emulator. Copyright notice for this file:
//   Copyright (C) 1998 BERO, Copyright (C) 2003 Xodnizel. GNU General Public License, version 2 or later.
//   See THIRD_PARTY_NOTICES.md (PPU harvest).
//
// PPU harvest climb (docs/projects/ppu-harvest-catalog.md, #3): an art instrument, ported FAITHFULLY with its
// flaws. The background is drawn by catch-up: every $2000/$2001/$2002/$2005/$2006/$2007 access first renders
// the tiles up to "now" (FCEUPPU_LineUpdate -> RefreshLine), a whole tile (8 pixels) at a time, from a line
// clock that starts 16 dots before the line (the two prefetched tiles). Sprites are composed onto the whole
// line at dot 256 from data fetched during the previous line's hblank.
//
// Characteristic jank, all of it kept on purpose:
//  * Tile-granular catch-up: a mid-line register write lands on the next 8-pixel tile boundary, not the
//    pixel; the "lasttile++" sprite-0 hack renders one tile ahead while a hit is pending.
//  * Sprite-0 hit is checked against the background as it is drawn, up to "lastpixel - 16"; it can hit on
//    pixel 255 (the old-PPU bug that broke the Battletoads snake pit).
//  * $2004 writes go through PPUSPL: the first eight writes after $2003 land at ($2003 & 7) + n, then at
//    $2003 only when it is >= 8. OAM DMA is 256 such writes, so a non-zero $2003 mangles OAM's first bytes.
//    $2004 reads return the I/O latch, not OAM. $2003 and PPUSPL are zeroed at the start of vblank.
//  * $2007: palette reads give the nametable under it (no palette read path); $3F00/$3F10 writes set
//    colour 0 of the four background palettes, $3Fx4/8/C writes are dropped; pattern writes only on CHR-RAM.
//  * Colour emphasis is per frame: the most common non-zero emphasis value over the frame tints every line
//    that had any emphasis on (and $2001 writes with no emphasis bits never clear it).
//  * After a reset the PPU is "dead" for two frames: no vblank flag, no NMI, a grey screen (Knight Rider).
//  * The pre-render line drops one dot every other frame whether or not rendering is on ("kook").
//  * With rendering fully off, the backdrop fill counts the two prefetch tiles as pixels, so a line that
//    turns rendering on part-way through is shifted.
//
// Board adaptation (no jank of its own): MMC5's "MMC5Hack" CHR paths and the PPU_hook callbacks are not
// reproduced; pattern data comes through the cartridge (which does its own MMC2/MMC4 latching). MMC3 is
// clocked by the GameHBIRQHook slot (dot 266 of visible lines, dot 256 of the pre-render line), MMC5's line
// hook at dot 0. Emphasis tint is the classic 3/4 attenuation of the other two channels.
using System;
using System.IO;

namespace NesEmulator
{
public class PPU_FCEU : IPPU, IPpuFrameClock
{
    public string CoreName => "FCE Ultra (old PPU)";
    public string Description => "FCEUX's picture code before newppu (2008, BERO/Xodnizel lineage): tile-at-a-time catch-up rendering, sprites composed at the end of the line, sprite-0 hit on pixel 255.";
    public int Performance => 10;
    public int Rating => 3;
    public string Category => "Harvest";

    const int W = 256, H = 240;

    readonly Bus bus;
    byte[] ntaram = new byte[0x800];
    byte[] palram = new byte[0x20];
    byte[] spram = new byte[0x100];
    byte[] sprbuf = new byte[0x100];
    readonly byte[] xbuf = new byte[W * H];
    readonly byte[] line = new byte[W + 40];
    readonly byte[] sprlinebuf = new byte[W + 8];
    byte[]? frameBuffer;

    readonly byte[] ppu = new byte[4];   // PPU[0], PPU[1], PPU_status, PPU[3]
    byte ppuspl, genLatch, vramBuffer, vtoggle, xOffset;
    int tempAddr, refreshAddr;
    // RefreshLine state
    int pline = -1, firsttile; long linestartDot; bool tofix;
    uint pshift0, pshift1, atlatch;
    int sphitx = 0x100; byte sphitdata;
    bool spork; int numsprites; bool spriteBlurp;
    int deemp; readonly int[] deempcnt = new int[8]; readonly byte[] lineDeemp = new byte[H];
    int kook, ppudead = 2;
    int scanline = 240, dot; long dotCounter;

    static readonly uint[] lut1 = new uint[256], lut2 = new uint[256], lut3 = new uint[128];
    static PPU_FCEU()
    {
        for (int x = 0; x < 256; x++)
        {
            uint v = 0;
            for (int y = 0; y < 8; y++) v |= (uint)(((x >> (7 - y)) & 1) << (y * 4));
            lut1[x] = v; lut2[x] = v << 1;
        }
        for (int cc = 0; cc < 16; cc++)
            for (int xo = 0; xo < 8; xo++)
            {
                uint v = 0;
                for (int pixel = 0; pixel < 8; pixel++) { int shiftr = (pixel + xo) / 8 * 2; v |= (uint)(((cc >> shiftr) & 3) << (2 + pixel * 4)); }
                lut3[xo | (cc << 3)] = v;
            }
    }

    public PPU_FCEU(Bus bus) { this.bus = bus; }

    public int ProbeScanline => scanline;

    bool ScreenON => (ppu[1] & 0x08) != 0;
    bool SpriteON => (ppu[1] & 0x10) != 0;
    bool Rendering => (ppu[1] & 0x18) != 0;

    public void Step(int cycles)
    {
        for (int c = 0; c < cycles; c++)
        {
            Tick();
            dot++; dotCounter++;
            if (scanline == 261 && dot == 340 && kook == 1) { dot++; }   // the "16-kook" short pre-render line
            if (dot >= 341)
            {
                dot = 0; scanline++;
                if (scanline == 262) { scanline = 0; }
                if (bus?.Butcher is { } bnd) bnd.NesCoreLine(scanline, ref ppu[0], ref ppu[1]);
            }
        }
    }

    void Tick()
    {
        if (ppudead > 0)
        {
            if (scanline == 239 && dot == 340) { Array.Fill(xbuf, (byte)0x80); Present(0); ppudead--; }
            return;
        }
        if (scanline < 240)
        {
            if (dot == 0)
            {
                deempcnt[deemp]++;
                if (bus?.cartridge?.mapper is Mapper5 mmc5 && Rendering) { mmc5.PpuScanlineHook(scanline, true); if (mmc5.IsIrqAsserted()) bus.cpu.RequestIRQ(true); }
            }
            else if (dot == 256) EndOfLine();
            else if (dot == 262) Fixit2();
            else if (dot == 266) HbIrq();
            else if (dot == 325)
            {
                if (SpriteON) RefreshSprites();
                if (scanline + 1 < 240) ResetRL();
            }
        }
        else if (scanline == 241)
        {
            if (dot == 0) { ppu[2] |= 0x80; ppu[3] = ppuspl = 0; }
            else if (dot == 12 && (ppu[0] & 0x80) != 0) bus?.cpu?.RequestNMI();
        }
        else if (scanline == 261)
        {
            if (dot == 0) ppu[2] &= 0x1F;
            else if (dot == 256) { if (Rendering) HbIrq(); }
            else if (dot == 325)
            {
                if (Rendering) refreshAddr = tempAddr;
                spork = false; numsprites = 0;
                deemp = ppu[1] >> 5;
                ResetRL();
                kook ^= 1;
            }
        }
    }

    void HbIrq()
    {
        if (!Rendering || (ppu[0] & 0x38) == 0x18) return;
        if (bus?.cartridge?.mapper is Mapper4 mmc3) { mmc3.RunScanlineIRQ(); if (mmc3.IRQPending()) { bus.cpu.RequestIRQ(true); mmc3.ClearIRQ(); } }
    }

    // ResetRL: a new line clock, 16 dots before the line (the two prefetched tiles).
    void ResetRL()
    {
        Array.Fill(line, (byte)0xFF, 0, W);
        pline = 0; firsttile = 0;
        linestartDot = dotCounter;
        tofix = false;
        LineUpdate();
        tofix = true;
    }

    void LineUpdate()
    {
        if (pline >= 0) RefreshLine((int)(dotCounter - linestartDot));
    }

    // The rest of DoLine after X6502_Run(256).
    void EndOfLine()
    {
        RefreshLine(272);
        if (tofix) Fixit1();
        CheckSpriteHit(272);
        pline = -1;
        int sl = scanline;
        Buffer.BlockCopy(line, 0, xbuf, sl * W, W);
        if (SpriteON) CopySprites(sl * W);
        int o = sl * W;
        if (Rendering && (ppu[1] & 0x01) != 0) for (int x = 0; x < W; x++) xbuf[o + x] &= 0x30;
        if ((ppu[1] >> 5) == 7) for (int x = 0; x < W; x++) xbuf[o + x] = (byte)((xbuf[o + x] & 0x3F) | 0xC0);
        else if ((ppu[1] & 0xE0) != 0) for (int x = 0; x < W; x++) xbuf[o + x] |= 0x40;
        else for (int x = 0; x < W; x++) xbuf[o + x] = (byte)((xbuf[o + x] & 0x3F) | 0x80);
        sphitx = 0x100;
        if (Rendering) FetchSpriteData();
        if (sl == 239)
        {
            int max = 0, maxref = 0;
            for (int x = 1; x < 7; x++) { if (deempcnt[x] > max) { max = deempcnt[x]; maxref = x; } deempcnt[x] = 0; }
            deempcnt[0] = deempcnt[7] = 0;
            Present(maxref);
        }
    }

    void CheckSpriteHit(int p)
    {
        int l = p - 16;
        if (sphitx == 0x100) return;
        for (int x = sphitx; x < sphitx + 8 && x < l; x++)
        {
            if ((sphitdata & (0x80 >> (x - sphitx))) != 0 && (line[x] & 64) == 0)
            {
                ppu[2] |= 0x40;
                sphitx = 0x100;
                break;
            }
        }
    }

    void RefreshLine(int lastpixel)
    {
        if (pline < 0) return;   // no line clock yet (just swapped in mid-frame): FCEU never gets here without ResetRL
        int lasttile = lastpixel >> 3;
        if (sphitx != 0x100 && (ppu[2] & 0x40) == 0)
        {
            if (sphitx < lastpixel - 16 && !(sphitx < (lasttile - 2) * 8)) lasttile++;
        }
        if (lasttile > 34) lasttile = 34;
        int numtiles = lasttile - firsttile;
        if (numtiles <= 0) return;
        int P = pline;
        int vofs = ((ppu[0] & 0x10) << 8) | ((refreshAddr >> 12) & 7);

        if (!ScreenON && !SpriteON)
        {
            byte tem = (byte)(palram[0] | 0x40);
            for (int i = 0; i < numtiles * 8 && P + i < line.Length; i++) line[P + i] = tem;
            P += numtiles * 8;
            pline = Math.Min(P, line.Length - 8);
            firsttile = lasttile;
            if (lastpixel >= 272 - 4 && tofix) { Fixit1(); tofix = false; }
            return;
        }

        palram[0] |= 64; palram[4] |= 64; palram[8] |= 64; palram[0xC] |= 64;
        for (int X1 = firsttile; X1 < lasttile; X1++)
        {
            if (X1 >= 2)
            {
                uint pixdata = lut1[(pshift0 >> (8 - xOffset)) & 0xFF] | lut2[(pshift1 >> (8 - xOffset)) & 0xFF];
                pixdata |= lut3[xOffset | (atlatch << 3)];
                for (int i = 0; i < 8; i++) { if (P + i < line.Length) line[P + i] = palram[pixdata & 0xF]; pixdata >>= 4; }
                P += 8;
            }
            int zz = refreshAddr & 0x1F;
            int ntBase = 0x2000 | (refreshAddr & 0xC00);
            int vadr = (Mem(ntBase | (refreshAddr & 0x3FF)) << 4) + vofs;
            int cc = Mem(ntBase | (0x3C0 + (zz >> 2) + ((refreshAddr & 0x380) >> 4)));
            cc = (cc >> ((zz & 2) + ((refreshAddr & 0x40) >> 4))) & 3;
            atlatch >>= 2; atlatch |= (uint)(cc << 2);
            pshift0 <<= 8; pshift1 <<= 8;
            pshift0 |= Mem(vadr & 0x1FFF); pshift1 |= Mem((vadr + 8) & 0x1FFF);
            if ((refreshAddr & 0x1F) == 0x1F) refreshAddr ^= 0x41F; else refreshAddr++;
        }
        palram[0] &= 63; palram[4] &= 63; palram[8] &= 63; palram[0xC] &= 63;

        if (firsttile <= 2 && 2 < lasttile && (ppu[1] & 2) == 0)
            for (int i = 0; i < 8; i++) line[i] = (byte)(palram[0] | 0x40);
        if (!ScreenON)
        {
            int tcount = lasttile - firsttile, tstart = firsttile - 2;
            if (tstart < 0) { tcount += tstart; tstart = 0; }
            for (int i = 0; i < tcount * 8; i++) if (tstart * 8 + i < W) line[tstart * 8 + i] = (byte)(palram[0] | 0x40);
        }
        if (lastpixel >= 272 - 4 && tofix) { Fixit1(); tofix = false; }
        CheckSpriteHit(lastpixel);
        pline = P;
        firsttile = lasttile;
    }

    void Fixit2()
    {
        if (Rendering) refreshAddr = (refreshAddr & 0xFBE0) | (tempAddr & 0x041F);
    }

    void Fixit1()
    {
        if (!Rendering) return;
        int rad = refreshAddr;
        if ((rad & 0x7000) == 0x7000)
        {
            rad ^= 0x7000;
            if ((rad & 0x3E0) == 0x3A0) { rad ^= 0x3A0; rad ^= 0x800; }
            else if ((rad & 0x3E0) == 0x3E0) rad ^= 0x3E0;
            else rad += 0x20;
        }
        else rad += 0x1000;
        refreshAddr = rad;
    }

    void FetchSpriteData()
    {
        byte p0 = ppu[0];
        int h = 8 + ((p0 & 0x20) >> 2);
        int vofs = (p0 & 0x8 & (((p0 & 0x20) ^ 0x20) >> 2)) << 9;
        int ns = 0; bool sb = false;
        for (int i = 0; i < 64; i++)
        {
            int o = i * 4;
            if ((uint)(scanline - spram[o]) >= (uint)h) continue;
            if (ns < 8)
            {
                if (i == 0) sb = true;
                int t = scanline - spram[o];
                int no = spram[o + 1];
                int vadr = (p0 & 0x20) != 0 ? ((no & 1) << 12) + ((no & 0xFE) << 4) : (no << 4) + vofs;
                if ((spram[o + 2] & 0x80) != 0) { vadr += 7; vadr -= t; vadr += (p0 & 0x20) >> 1; vadr -= t & 8; }
                else { vadr += t; vadr += t & 8; }
                sprbuf[ns * 4] = Mem(vadr & 0x1FFF);
                sprbuf[ns * 4 + 1] = Mem((vadr + 8) & 0x1FFF);
                sprbuf[ns * 4 + 2] = spram[o + 2];
                sprbuf[ns * 4 + 3] = spram[o + 3];
                ns++;
            }
            else { ppu[2] |= 0x20; break; }
        }
        numsprites = ns;
        spriteBlurp = sb;
    }

    void RefreshSprites()
    {
        spork = false;
        if (numsprites == 0) return;
        Array.Fill(sprlinebuf, (byte)0x80, 0, W);
        for (int n = numsprites - 1; n >= 0; n--)
        {
            byte ca0 = sprbuf[n * 4], ca1 = sprbuf[n * 4 + 1], atr = sprbuf[n * 4 + 2]; int x = sprbuf[n * 4 + 3];
            uint pixdata = lut1[ca0] | lut2[ca1];
            int J = ca0 | ca1;
            if (J == 0) continue;
            if (n == 0 && spriteBlurp && (ppu[2] & 0x40) == 0)
            {
                sphitx = x;
                sphitdata = (byte)J;
                if ((atr & 0x40) != 0)
                    sphitdata = (byte)(((J << 7) & 0x80) | ((J << 5) & 0x40) | ((J << 3) & 0x20) | ((J << 1) & 0x10) | ((J >> 1) & 0x08) | ((J >> 3) & 0x04) | ((J >> 5) & 0x02) | ((J >> 7) & 0x01));
            }
            int vb = 0x10 + ((atr & 3) << 2);
            byte back = (byte)((atr & 0x20) != 0 ? 0x40 : 0);
            bool hf = (atr & 0x40) != 0;
            for (int k = 0; k < 8; k++)
            {
                if ((J & (0x80 >> k)) != 0)
                {
                    int dst = x + (hf ? 7 - k : k);
                    sprlinebuf[dst] = (byte)(palram[vb + (int)(pixdata & 3)] | back);
                }
                pixdata >>= 4;
            }
        }
        spriteBlurp = false;
        spork = true;
    }

    void CopySprites(int target)
    {
        int n = ((ppu[1] & 4) ^ 4) << 1;
        if (!spork) return;
        spork = false;
        for (; n < W; n++)
        {
            byte s = sprlinebuf[n];
            if ((s & 0x80) != 0) continue;
            if ((s & 0x40) == 0 || (xbuf[target + n] & 0x40) != 0) xbuf[target + n] = s;
        }
    }

    void Present(int maxref)
    {
        frameBuffer ??= new byte[W * H * 4];
        var fb = frameBuffer;
        for (int i = 0, o = 0; i < W * H; i++)
        {
            int v = xbuf[i];
            int q = (v & 0x3F) * 3;
            int r = Pal[q], g = Pal[q + 1], b = Pal[q + 2];
            int e = (v & 0xC0) == 0xC0 ? 7 : (v & 0xC0) == 0x40 ? maxref : 0;
            if (e != 0)
            {
                if ((e & 1) == 0) r = r * 3 / 4;
                if ((e & 2) == 0) g = g * 3 / 4;
                if ((e & 4) == 0) b = b * 3 / 4;
                if (e == 7) { r = r * 3 / 4; g = g * 3 / 4; b = b * 3 / 4; }
            }
            fb[o++] = (byte)r; fb[o++] = (byte)g; fb[o++] = (byte)b; fb[o++] = 255;
        }
    }

    byte Mem(int address)
    {
        address &= 0x3FFF;
        var bb = bus?.Butcher;
        if (bb?.Console != null && address < 0x3F00)
        {
            int a = bb.ConsoleReadAddr((ushort)address, out bool bendData);
            if (a >= 0) { byte v = MemUnbent(a); return bendData ? bb.ConsoleData(v) : v; }
        }
        return MemUnbent(address);
    }

    byte MemUnbent(int address)
    {
        address &= 0x3FFF;
        if (address < 0x2000) return bus?.cartridge?.PPURead((ushort)address) ?? 0;
        if (bus?.cartridge?.mapper is IMapper m && m.TryPpuNametableRead((ushort)(0x2000 | (address & 0xFFF)), out byte v)) return v;
        return ntaram[MirrorNt(address)];
    }

    void MemWrite(int address, byte value)
    {
        address &= 0x3FFF;
        if (address < 0x2000) { bus?.cartridge?.PPUWrite((ushort)address, value); return; }
        if (bus?.cartridge?.mapper is IMapper m && m.TryPpuNametableWrite((ushort)(0x2000 | (address & 0xFFF)), value)) return;
        ntaram[MirrorNt(address)] = value;
    }

    int MirrorNt(int address)
    {
        int off = address & 0x0FFF, nt = off >> 10, inner = off & 0x3FF;
        return (bus?.cartridge?.mirroringMode ?? Mirroring.Horizontal) switch
        {
            Mirroring.Vertical => (nt & 1) * 0x400 + inner,
            Mirroring.Horizontal => (nt >> 1) * 0x400 + inner,
            Mirroring.SingleScreenA => inner,
            Mirroring.SingleScreenB => 0x400 + inner,
            _ => off & 0x7FF,
        };
    }

    public byte ReadPPURegister(ushort address)
    {
        switch (address & 7)
        {
            case 2:
            {
                LineUpdate();
                byte ret = (byte)(ppu[2] | (genLatch & 0x1F));
                vtoggle = 0; ppu[2] &= 0x7F; genLatch = ret;
                return ret;
            }
            case 7:
            {
                int tmp = refreshAddr & 0x3FFF;
                LineUpdate();
                byte ret = vramBuffer;
                genLatch = vramBuffer;
                vramBuffer = tmp < 0x2000 ? Mem(tmp) : Mem(0x2000 | (tmp & 0xFFF));
                refreshAddr += (ppu[0] & 0x04) != 0 ? 32 : 1;
                return ret;
            }
            default:
                LineUpdate();
                return genLatch;
        }
    }

    public void WritePPURegister(ushort address, byte v)
    {
        switch (address & 7)
        {
            case 0:
                LineUpdate();
                genLatch = v;
                if ((ppu[0] & 0x80) == 0 && (v & 0x80) != 0 && (ppu[2] & 0x80) != 0) bus?.cpu?.RequestNMI();
                ppu[0] = v;
                tempAddr = (tempAddr & 0xF3FF) | ((v & 3) << 10);
                break;
            case 1:
                LineUpdate();
                genLatch = v; ppu[1] = v;
                if ((v & 0xE0) != 0) deemp = v >> 5;
                break;
            case 2: genLatch = v; break;
            case 3: genLatch = v; ppu[3] = v; ppuspl = (byte)(v & 7); break;
            case 4: B2004(v); break;
            case 5:
            {
                int tmp = tempAddr;
                LineUpdate();
                genLatch = v;
                if (vtoggle == 0) { tmp &= 0xFFE0; tmp |= v >> 3; xOffset = (byte)(v & 7); }
                else { tmp &= 0x8C1F; tmp |= (v & ~7) << 2; tmp |= (v & 7) << 12; }
                tempAddr = tmp;
                vtoggle ^= 1;
                break;
            }
            case 6:
                LineUpdate();
                genLatch = v;
                if (vtoggle == 0) { tempAddr &= 0x00FF; tempAddr |= (v & 0x3F) << 8; }
                else { tempAddr &= 0xFF00; tempAddr |= v; refreshAddr = tempAddr; }
                vtoggle ^= 1;
                break;
            case 7:
            {
                int tmp = refreshAddr & 0x3FFF;
                genLatch = v;
                if (tmp >= 0x3F00)
                {
                    if ((tmp & 0xF) == 0) palram[0] = palram[4] = palram[8] = palram[0xC] = (byte)(v & 0x3F);
                    else if ((tmp & 3) != 0) palram[tmp & 0x1F] = (byte)(v & 0x3F);
                }
                else if (tmp < 0x2000) { if (bus?.cartridge != null && bus.cartridge.chrBanks == 0) MemWrite(tmp, v); }
                else MemWrite(tmp, v);
                refreshAddr += (ppu[0] & 0x04) != 0 ? 32 : 1;
                break;
            }
        }
    }

    void B2004(byte v)
    {
        genLatch = v;
        byte val = bus?.Butcher is { SpritesActive: true } sb ? sb.FilterOamWrite(ppu[3], v) : v;
        if (ppuspl >= 8) { if (ppu[3] >= 8) spram[ppu[3]] = val; }
        else spram[ppuspl] = val;
        ppu[3]++; ppuspl++;
    }

    // B4014: 256 writes to $2004, with all their PPUSPL quirks.
    public void WriteOAMDMA(byte page)
    {
        ushort t = (ushort)(page << 8);
        for (int x = 0; x < 256; x++) B2004(bus.Read((ushort)(t + x)));
    }

    public byte[] GetFrameBuffer() { frameBuffer ??= new byte[W * H * 4]; return frameBuffer; }
    public void UpdateFrameBuffer() { }
    public void ClearBuffers() { frameBuffer = null; }
    public void GenerateStaticFrame()
    {
        frameBuffer ??= new byte[W * H * 4];
        var r = new Random((int)(dotCounter & 0x7FFFFFFF));
        for (int i = 0; i < frameBuffer.Length; i += 4) { byte g = (byte)r.Next(256); frameBuffer[i] = frameBuffer[i + 1] = frameBuffer[i + 2] = g; frameBuffer[i + 3] = 255; }
    }

    public object GetState()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((byte)1);
        w.Write(ppu); w.Write(ppuspl); w.Write(genLatch); w.Write(vramBuffer); w.Write(vtoggle); w.Write(xOffset);
        w.Write(tempAddr); w.Write(refreshAddr);
        w.Write(pline); w.Write(firsttile); w.Write(dotCounter - linestartDot); w.Write(tofix);
        w.Write(pshift0); w.Write(pshift1); w.Write(atlatch);
        w.Write(sphitx); w.Write(sphitdata); w.Write(spork); w.Write(numsprites); w.Write(spriteBlurp);
        w.Write(deemp); foreach (var d in deempcnt) w.Write(d);
        w.Write(kook); w.Write(ppudead); w.Write(dot);
        w.Write(sprbuf); w.Write(sprlinebuf); w.Write(line); w.Write(xbuf); w.Write(palram);
        return new PpuSharedState
        {
            vram = (byte[])ntaram.Clone(), palette = (byte[])palram.Clone(), oam = (byte[])spram.Clone(),
            PPUCTRL = ppu[0], PPUMASK = ppu[1], PPUSTATUS = ppu[2], OAMADDR = ppu[3], PPUADDR = (ushort)refreshAddr, v = (ushort)refreshAddr, t = (ushort)tempAddr, fineX = xOffset,
            addrLatch = vtoggle != 0, scanline = scanline, scanlineCycle = dot, ppuDataBuffer = vramBuffer, fixPipeline = ms.ToArray(),
        };
    }

    public void SetState(object state)
    {
        PpuSharedState? s = HarvestPpuState.From(state);
        if (s == null) return;
        if (s.vram?.Length == 0x800) ntaram = (byte[])s.vram.Clone();
        if (s.oam?.Length == 0x100) spram = (byte[])s.oam.Clone();
        scanline = s.scanline; dot = s.scanlineCycle;
        var p = s.fixPipeline;
        if (p != null && p.Length > 1 && p[0] == 1 && p.Length > W * H)
        {
            var r = new BinaryReader(new MemoryStream(p));
            r.ReadByte();
            r.Read(ppu, 0, 4); ppuspl = r.ReadByte(); genLatch = r.ReadByte(); vramBuffer = r.ReadByte(); vtoggle = r.ReadByte(); xOffset = r.ReadByte();
            tempAddr = r.ReadInt32(); refreshAddr = r.ReadInt32();
            pline = r.ReadInt32(); firsttile = r.ReadInt32(); linestartDot = dotCounter - r.ReadInt64(); tofix = r.ReadBoolean();
            pshift0 = r.ReadUInt32(); pshift1 = r.ReadUInt32(); atlatch = r.ReadUInt32();
            sphitx = r.ReadInt32(); sphitdata = r.ReadByte(); spork = r.ReadBoolean(); numsprites = r.ReadInt32(); spriteBlurp = r.ReadBoolean();
            deemp = r.ReadInt32(); for (int i = 0; i < 8; i++) deempcnt[i] = r.ReadInt32();
            kook = r.ReadInt32(); ppudead = r.ReadInt32(); dot = r.ReadInt32();
            r.Read(sprbuf, 0, sprbuf.Length); r.Read(sprlinebuf, 0, sprlinebuf.Length); r.Read(line, 0, line.Length); r.Read(xbuf, 0, xbuf.Length); r.Read(palram, 0, palram.Length);
        }
        else
        {
            // A state from another core: take its registers the way the old FCEU PPU would hold them.
            if (s.palette?.Length == 32) for (int i = 0; i < 32; i++) palram[i] = (byte)(s.palette[i] & 0x3F);
            palram[4] = palram[8] = palram[0xC] = palram[0];
            ppu[0] = s.PPUCTRL; ppu[1] = s.PPUMASK; ppu[2] = s.PPUSTATUS; ppu[3] = s.OAMADDR; ppuspl = (byte)(s.OAMADDR & 7);
            refreshAddr = s.v; tempAddr = s.t; xOffset = s.fineX; vtoggle = (byte)(s.addrLatch ? 1 : 0); vramBuffer = s.ppuDataBuffer;
            ppudead = 0; pline = -1; sphitx = 0x100; spork = false; numsprites = 0;
        }
    }

    // FCEU's palette.c is not part of this port; the board's common 64-colour table stands in for it.
    static readonly byte[] Pal = {
        84,84,84, 0,30,116, 8,16,144, 48,0,136, 68,0,100, 92,0,48, 84,4,0, 60,24,0, 32,42,0, 8,58,0, 0,64,0, 0,60,0, 0,50,60, 0,0,0, 0,0,0, 0,0,0,
        152,150,152, 8,76,196, 48,50,236, 92,30,228, 136,20,176, 160,20,100, 152,34,32, 120,60,0, 84,90,0, 40,114,0, 8,124,0, 0,118,40, 0,102,120, 0,0,0, 0,0,0, 0,0,0,
        236,238,236, 76,154,236, 120,124,236, 176,98,236, 228,84,236, 236,88,180, 236,106,100, 212,136,32, 160,170,0, 116,196,0, 76,208,32, 56,204,108, 56,180,204, 60,60,60, 0,0,0, 0,0,0,
        236,238,236, 168,204,236, 188,188,236, 212,178,236, 236,174,236, 236,174,212, 236,180,176, 228,196,144, 204,210,120, 180,222,120, 168,226,144, 152,226,180, 160,214,228, 160,162,160, 0,0,0, 0,0,0,
    };
}
}
