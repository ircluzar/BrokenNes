// PPU_NOFR - nofrendo's PPU, ported from retro-go @05e97e8 (2022-11-18),
// retro-core/components/nofrendo/nes/ppu.c + ppu.h (866 + 171 lines).
//
//   Nofrendo (c) 1998-2000 Matthew Conte (matt@conte.com)
//   Licensed under version 2 of the GNU Library General Public License (LGPL-2); see
//   THIRD_PARTY_NOTICES.md (PPU harvest). "Any permitted reproduction of these routines, in whole
//   or in part, must bear this legend."
//
// PPU harvest climb (docs/projects/ppu-harvest-catalog.md, #1): an art instrument, ported FAITHFULLY
// with its flaws. The renderer runs once per line at the START of the line (ppu_scanline), then the
// vertical increment happens at the end of the line (ppu_endscanline), as nofrendo's nes.c drives it.
//
// Characteristic jank, all of it kept on purpose:
//  * Whole-line renderer: the background and sprites of a line are drawn at dot 0 from whatever the
//    registers hold then. Mid-line $2000/$2001/$2005/$2006 writes only land on the next line.
//  * Sprite-0 hit is computed when the line is drawn and timed as "line start + (pixel within the
//    sprite / 3) CPU cycles": the sprite's X position is ignored, so the hit fires near the START of
//    the line even for a sprite at X=200 (status-bar splits come early, wobbly ones jump).
//  * OAM is evaluated on the same line it is drawn (its own TODO: "fetch valid OAM a scanline
//    before, like the Real Thing").
//  * The sprite-overflow flag is raised when the 8th sprite on a line is drawn, not the 9th, and the
//    flag is cleared at the start of every visible line.
//  * Writes to $2007 while rendering is on outside vblank store $FF ("corrupt") at the address;
//    reads return $FF into the buffer.
//  * Palette: a write to $3F00 or $3F10 sets colour 0 of ALL eight palettes; writes to $3Fx4, $3Fx8,
//    $3FxC are dropped. $2007 palette reads return the nametable underneath (no palette read path).
//  * No colour emphasis, no greyscale bit, no odd-frame dot skip, no NMI when $2000 bit 7 is set
//    inside vblank (NMI only at the start of line 241).
//  * Full vertical scroll copy happens at the start of line 0, not on the pre-render line.
//  * OAM DMA's "houdini" fix-up: after the DMA, OAM bytes 0-7 are re-copied from the source page
//    (from offset 8 if $2003 bit 2 was set, else from offset 0).
//  * MMC2/MMC4 latches are triggered through the cartridge's own pattern reads (the BrokenNes
//    mappers latch on the fetched address); nofrendo's latchfunc hook is not reproduced separately.
//  * The background fetches 33 tiles from (fine X) pixels left of the line; the left 8 columns are
//    blanked with the backdrop when $2001 bit 1 is clear, sprites restore the saved left column.
//
// Board adaptation (no jank of its own): nametables through the cartridge mirroring / mapper hook as
// the other in-house cores do; MMC3 counter clocked at dot 260, MMC5 line hook at dot 3 (as PPU_LQ).
using System;

namespace NesEmulator
{
public class PPU_NOFR : IPPU, IPpuFrameClock
{
    public string CoreName => "nofrendo";
    public string Description => "nofrendo's PPU (Matthew Conte, 1998-2000; retro-go 2022 build): whole-line renderer, sprite-0 hit timed from the line start, OAM evaluated on the drawn line.";
    public int Performance => 10;
    public int Rating => 2;
    public string Category => "Harvest";

    const byte BG_TRANS = 0x80, SP_PIXEL = 0x40;
    const int W = 256, H = 240, Margin = 8;

    readonly Bus bus;
    byte[] vram = new byte[2048];
    byte[] oam = new byte[256];
    byte[] palette = new byte[32];
    byte[]? frameBuffer;
    readonly byte[] line = new byte[Margin + W + 24];

    // Hardware registers (ppu_t)
    byte ctrl0, ctrl1, stat, oamAddr, latch, vdataLatch, tileXofs, flipflop;
    int vaddr = 0x2000, vaddrLatch = 0x2000, vaddrInc = 1;
    int objHeight = 8, objBase, bgBase;
    bool leftBgOn, leftObjOn, bgOn, objOn;
    bool strikeflag; long strikeDot = long.MaxValue;
    bool vramAccessible = true;
    int scanline, dot; long dotCounter;

    public PPU_NOFR(Bus bus) { this.bus = bus; }

    public int ProbeScanline => scanline;

    void EnsureFrameBuffer() { if (frameBuffer == null || frameBuffer.Length != W * H * 4) frameBuffer = new byte[W * H * 4]; }

    public void Step(int cycles)
    {
        for (int c = 0; c < cycles; c++)
        {
            if (dot == 0) Scanline(scanline);
            if (scanline < 240)
            {
                if (dot == 3 && bus?.cartridge?.mapper is Mapper5 mmc5) { mmc5.PpuScanlineHook(scanline, bgOn || objOn); if (mmc5.IsIrqAsserted()) bus.cpu.RequestIRQ(true); }
                if (dot == 256) EndScanline();
                if (dot == 260 && (bgOn || objOn) && bus?.cartridge?.mapper is Mapper4 mmc3) { mmc3.RunScanlineIRQ(); if (mmc3.IRQPending()) { bus.cpu.RequestIRQ(true); mmc3.ClearIRQ(); } }
            }
            dot++; dotCounter++;
            if (dot >= 341)
            {
                dot = 0; scanline++;
                if (scanline == 262) scanline = 0;
                if (bus?.Butcher is { } bnd) bnd.NesCoreLine(scanline, ref ctrl0, ref ctrl1);
            }
        }
    }

    // ppu_scanline
    void Scanline(int sl)
    {
        if (sl < 240)
        {
            stat &= unchecked((byte)~0x20);
            if (bgOn || objOn)
            {
                if (sl == 0) vaddr = vaddrLatch;
                else vaddr = (vaddr & ~0x041F) | (vaddrLatch & 0x041F);
            }
            if (bus?.cartridge == null) return;
            RenderBg();
            RenderOam(sl);
            EnsureFrameBuffer();
            var fb = frameBuffer!; int o = sl * W * 4;
            for (int x = 0; x < W; x++)
            {
                int p = (line[Margin + x] & 0x3F) * 3;
                fb[o++] = Pal[p]; fb[o++] = Pal[p + 1]; fb[o++] = Pal[p + 2]; fb[o++] = 255;
            }
        }
        else if (sl == 241)
        {
            stat |= 0x80; vramAccessible = true;
            if ((ctrl0 & 0x80) != 0) bus?.cpu?.RequestNMI();
        }
        else if (sl == 261)
        {
            stat &= 0x7F; strikeflag = false; strikeDot = long.MaxValue; vramAccessible = false;
        }
    }

    // ppu_endscanline
    void EndScanline()
    {
        if (!(bgOn || objOn)) return;
        if ((vaddr >> 12) == 7)
        {
            switch ((vaddr >> 5) & 0x1F)
            {
                case 29: vaddr &= ~0x73E0; vaddr ^= 0x0800; break;
                case 31: vaddr &= ~0x73E0; break;
                default: vaddr &= ~0x7000; vaddr += 0x20; break;
            }
        }
        else vaddr += 0x1000;
    }

    byte FullBg => (byte)(palette[0] | BG_TRANS);

    uint GetPatpix(int addr)
    {
        byte p1 = Mem(addr), p2 = Mem(addr + 8);
        return (uint)(((p2 & 0xAA) << 8) | ((p2 & 0x55) << 1) | ((p1 & 0xAA) << 7) | (p1 & 0x55));
    }

    static void BuildTileColors(bool flip, uint pattern, Span<byte> c)
    {
        if (flip)
        {
            c[7] = (byte)((pattern >> 14) & 3); c[6] = (byte)((pattern >> 6) & 3); c[5] = (byte)((pattern >> 12) & 3); c[4] = (byte)((pattern >> 4) & 3);
            c[3] = (byte)((pattern >> 10) & 3); c[2] = (byte)((pattern >> 2) & 3); c[1] = (byte)((pattern >> 8) & 3); c[0] = (byte)(pattern & 3);
        }
        else
        {
            c[0] = (byte)((pattern >> 14) & 3); c[1] = (byte)((pattern >> 6) & 3); c[2] = (byte)((pattern >> 12) & 3); c[3] = (byte)((pattern >> 4) & 3);
            c[4] = (byte)((pattern >> 10) & 3); c[5] = (byte)((pattern >> 2) & 3); c[6] = (byte)((pattern >> 8) & 3); c[7] = (byte)(pattern & 3);
        }
    }

    void RenderBg()
    {
        if (!bgOn) { Array.Fill(line, FullBg, Margin, W); return; }
        int bmp = Margin - tileXofs;
        int xTile = vaddr & 0x1F;
        int refreshVaddr = 0x2000 + (vaddr & 0x0FE0);
        int bgOffset = ((vaddr >> 12) & 7) + bgBase;
        int attribBase = (refreshVaddr & 0x2C00) + 0x3C0 + (((vaddr >> 5) & 0x1C) << 1);
        int attribAddr = attribBase + (xTile >> 2);
        int attrib = Mem(attribAddr); attribAddr++;
        int attribShift = (xTile & 2) + (((vaddr >> 5) & 2) << 1);
        int colHigh = ((attrib >> attribShift) & 3) << 2;
        for (int t = 0; t < 33; t++)
        {
            int tileIndex = Mem(refreshVaddr + xTile);
            uint pat = GetPatpix(bgOffset + (tileIndex << 4));
            line[bmp + 0] = palette[colHigh + ((pat >> 14) & 3)];
            line[bmp + 1] = palette[colHigh + ((pat >> 6) & 3)];
            line[bmp + 2] = palette[colHigh + ((pat >> 12) & 3)];
            line[bmp + 3] = palette[colHigh + ((pat >> 4) & 3)];
            line[bmp + 4] = palette[colHigh + ((pat >> 10) & 3)];
            line[bmp + 5] = palette[colHigh + ((pat >> 2) & 3)];
            line[bmp + 6] = palette[colHigh + ((pat >> 8) & 3)];
            line[bmp + 7] = palette[colHigh + (pat & 3)];
            bmp += 8;
            xTile++;
            if ((xTile & 1) == 0)
            {
                if ((xTile & 3) == 0)
                {
                    if (xTile == 32) { xTile = 0; refreshVaddr ^= 1 << 10; attribBase ^= 1 << 10; attribAddr = attribBase; }
                    attrib = Mem(attribAddr); attribAddr++;
                }
                attribShift ^= 2;
                colHigh = ((attrib >> attribShift) & 3) << 2;
            }
        }
        if (!leftBgOn) Array.Fill(line, FullBg, Margin, 8);
    }

    void RenderOam(int sl)
    {
        if (!objOn) return;
        Span<byte> save = stackalloc byte[8];
        line.AsSpan(Margin, 8).CopyTo(save);
        Span<byte> colors = stackalloc byte[8];
        for (int n = 0, count = 0; n < 64; n++)
        {
            int spriteY = oam[n * 4] + 1, tileIndex = oam[n * 4 + 1], attr = oam[n * 4 + 2], xLoc = oam[n * 4 + 3];
            if (spriteY > sl || spriteY <= sl - objHeight || spriteY == 0 || spriteY >= 240) continue;
            int tileAddr = objHeight == 16 ? ((tileIndex & 1) << 12) | ((tileIndex & 0xFE) << 4) : objBase + (tileIndex << 4);
            int yOffset = sl - spriteY;
            if (yOffset > 7) yOffset += 8;
            if ((attr & 0x80) != 0) { yOffset -= objHeight == 16 ? 23 : 7; tileAddr -= yOffset; }
            else tileAddr += yOffset;
            uint pat = GetPatpix(tileAddr);
            int surf = Margin + xLoc;
            if (n == 0 && !strikeflag && pat != 0)
            {
                BuildTileColors((attr & 0x40) != 0, pat, colors);
                for (int i = 0; i < 8; i++)
                    if (colors[i] != 0 && (line[surf + i] & BG_TRANS) == 0) { strikeDot = dotCounter + (i / 3) * 3; strikeflag = true; break; }
            }
            if (pat != 0)
            {
                BuildTileColors((attr & 0x40) != 0, pat, colors);
                int col = 16 + ((attr & 3) << 2);
                if ((attr & 0x20) != 0)
                {
                    for (int i = 0; i < 8; i++) if (colors[i] != 0) line[surf + i] = (byte)(SP_PIXEL | ((line[surf + i] & BG_TRANS) != 0 ? palette[col + colors[i]] : line[surf + i]));
                }
                else
                {
                    for (int i = 0; i < 8; i++) if (colors[i] != 0 && (line[surf + i] & SP_PIXEL) == 0) line[surf + i] = (byte)(SP_PIXEL | palette[col + colors[i]]);
                }
            }
            if (++count == 8) { stat |= 0x20; break; }
        }
        if (!leftObjOn) save.CopyTo(line.AsSpan(Margin, 8));
    }

    // PPU_MEM_READ / WRITE through the board (pattern tables via the cartridge, nametables via mirroring).
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
        if (bus?.cartridge?.mapper is IMapper m && m.TryPpuNametableRead((ushort)address, out byte v)) return v;
        return vram[MirrorNt(address)];
    }

    void MemWrite(int address, byte value)
    {
        address &= 0x3FFF;
        if (address < 0x2000) { bus?.cartridge?.PPUWrite((ushort)address, value); return; }
        if (bus?.cartridge?.mapper is IMapper m && m.TryPpuNametableWrite((ushort)address, value)) return;
        vram[MirrorNt(address)] = value;
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

    // ppu_read
    public byte ReadPPURegister(ushort address)
    {
        byte value;
        switch (address & 7)
        {
            case 2:
                value = (byte)((stat & 0xE0) | (latch & 0x1F));
                if (strikeflag && dotCounter >= strikeDot) value |= 0x40;
                stat &= 0x7F; flipflop = 0;
                break;
            case 7:
                value = latch = vdataLatch;
                if ((bgOn || objOn) && !vramAccessible) vdataLatch = 0xFF;
                else { int a = vaddr; if (a >= 0x3000) a -= 0x1000; vdataLatch = Mem(a); }
                vaddr = (vaddr + vaddrInc) & 0x3FFF;
                break;
            default: value = latch; break;
        }
        return value;
    }

    // ppu_write
    public void WritePPURegister(ushort address, byte value)
    {
        latch = value;
        switch (address & 7)
        {
            case 0:
                ctrl0 = value;
                objHeight = (value & 0x20) != 0 ? 16 : 8;
                bgBase = (value & 0x10) != 0 ? 0x1000 : 0;
                objBase = (value & 0x08) != 0 ? 0x1000 : 0;
                vaddrInc = (value & 0x04) != 0 ? 32 : 1;
                vaddrLatch = (vaddrLatch & ~0x0C00) | ((value & 3) << 10);
                break;
            case 1:
                ctrl1 = value;
                objOn = (value & 0x10) != 0; bgOn = (value & 0x08) != 0;
                leftObjOn = (value & 0x04) != 0; leftBgOn = (value & 0x02) != 0;
                break;
            case 3: oamAddr = value; break;
            case 4: oam[oamAddr] = bus?.Butcher is { SpritesActive: true } sb ? sb.FilterOamWrite(oamAddr, value) : value; oamAddr++; break;
            case 5:
                if (flipflop == 0) { vaddrLatch &= ~0x001F; vaddrLatch |= value >> 3; tileXofs = (byte)(value & 7); }
                else { vaddrLatch &= ~0x73E0; vaddrLatch |= (value & 0xF8) << 2; vaddrLatch |= (value & 7) << 12; }
                flipflop ^= 1;
                break;
            case 6:
                if (flipflop == 0) { vaddrLatch &= ~0xFF00; vaddrLatch |= (value & 0x3F) << 8; }
                else { vaddrLatch &= ~0x00FF; vaddrLatch |= value; vaddr = vaddrLatch; }
                flipflop ^= 1;
                break;
            case 7:
                if (vaddr < 0x3F00)
                {
                    if ((bgOn || objOn) && !vramAccessible) MemWrite(vaddr, 0xFF);   /* corrupt */
                    else MemWrite(vaddr, value);
                }
                else
                {
                    if ((vaddr & 0x0F) == 0) { for (int i = 0; i < 8; i++) palette[i << 2] = (byte)((value & 0x3F) | BG_TRANS); }
                    else if ((vaddr & 3) != 0) palette[vaddr & 0x1F] = (byte)(value & 0x3F);
                }
                vaddr = (vaddr + vaddrInc) & 0x3FFF;
                break;
        }
    }

    // ppu_oamdma, with its houdini fix-up
    public void WriteOAMDMA(byte page)
    {
        byte start = oamAddr;
        bus.FastOamDma(page, oam, ref oamAddr);
        ushort src = (ushort)(page << 8);
        if (((start >> 2) & 1) != 0)
        {
            for (int i = 4; i < 8; i++) oam[i] = bus.Read(src++);
            src += 248;
            for (int i = 0; i < 4; i++) oam[i] = bus.Read(src++);
        }
        else for (int i = 0; i < 8; i++) oam[i] = bus.Read(src++);
    }

    public byte[] GetFrameBuffer() { EnsureFrameBuffer(); return frameBuffer!; }
    public void UpdateFrameBuffer() { }
    public void ClearBuffers() { frameBuffer = null; }

    public void GenerateStaticFrame()
    {
        EnsureFrameBuffer();
        var r = new Random((int)(dotCounter & 0x7FFFFFFF));
        for (int i = 0; i < frameBuffer!.Length; i += 4) { byte g = (byte)r.Next(256); frameBuffer[i] = frameBuffer[i + 1] = frameBuffer[i + 2] = g; frameBuffer[i + 3] = 255; }
    }

    // Save states: the shared fields carry the memories; the rest of nofrendo's ppu_t rides in fixPipeline.
    public object GetState()
    {
        var p = new byte[40];
        int k = 0;
        p[k++] = ctrl0; p[k++] = ctrl1; p[k++] = stat; p[k++] = oamAddr; p[k++] = latch; p[k++] = vdataLatch; p[k++] = tileXofs; p[k++] = flipflop;
        void I(int v) { BitConverter.TryWriteBytes(p.AsSpan(k, 4), v); k += 4; }
        I(vaddr); I(vaddrLatch); I(dot);
        p[k++] = (byte)((strikeflag ? 1 : 0) | (vramAccessible ? 2 : 0));
        long rel = strikeDot == long.MaxValue ? -1 : strikeDot - dotCounter;
        BitConverter.TryWriteBytes(p.AsSpan(k, 8), rel); k += 8;
        return new PpuSharedState
        {
            vram = (byte[])vram.Clone(), palette = (byte[])palette.Clone(), oam = (byte[])oam.Clone(),
            PPUCTRL = ctrl0, PPUMASK = ctrl1, PPUSTATUS = stat, OAMADDR = oamAddr, v = (ushort)vaddr, t = (ushort)vaddrLatch, fineX = tileXofs,
            addrLatch = flipflop != 0, scanline = scanline, scanlineCycle = dot, ppuDataBuffer = vdataLatch, fixPipeline = p,
        };
    }

    public void SetState(object state)
    {
        PpuSharedState? s = HarvestPpuState.From(state);
        if (s == null) return;
        if (s.vram?.Length == 2048) vram = (byte[])s.vram.Clone();
        if (s.palette?.Length == 32) palette = (byte[])s.palette.Clone();
        if (s.oam?.Length == 256) oam = (byte[])s.oam.Clone();
        scanline = s.scanline; dot = s.scanlineCycle;
        var p = s.fixPipeline;
        if (p != null && p.Length == 40)
        {
            int k = 0;
            ctrl0 = p[k++]; ctrl1 = p[k++]; stat = p[k++]; oamAddr = p[k++]; latch = p[k++]; vdataLatch = p[k++]; tileXofs = p[k++]; flipflop = p[k++];
            vaddr = BitConverter.ToInt32(p, k); k += 4; vaddrLatch = BitConverter.ToInt32(p, k); k += 4; dot = BitConverter.ToInt32(p, k); k += 4;
            strikeflag = (p[k] & 1) != 0; vramAccessible = (p[k] & 2) != 0; k++;
            long rel = BitConverter.ToInt64(p, k);
            strikeDot = rel < 0 ? long.MaxValue : dotCounter + rel;
        }
        else
        {
            // A state from another core: take its registers the way nofrendo would hold them.
            ctrl0 = s.PPUCTRL; ctrl1 = s.PPUMASK; stat = s.PPUSTATUS; oamAddr = s.OAMADDR; vaddr = s.v; vaddrLatch = s.t; tileXofs = s.fineX;
            flipflop = (byte)(s.addrLatch ? 1 : 0); vdataLatch = s.ppuDataBuffer; strikeflag = false; strikeDot = long.MaxValue;
            vramAccessible = scanline >= 241 && scanline < 261;
            // nofrendo keeps colour 0 of every palette in step and marks it transparent.
            for (int i = 0; i < 32; i++) palette[i] &= 0x3F;
            for (int i = 0; i < 8; i++) palette[i << 2] = (byte)(palette[0] | BG_TRANS);
        }
        objHeight = (ctrl0 & 0x20) != 0 ? 16 : 8; bgBase = (ctrl0 & 0x10) != 0 ? 0x1000 : 0; objBase = (ctrl0 & 0x08) != 0 ? 0x1000 : 0; vaddrInc = (ctrl0 & 0x04) != 0 ? 32 : 1;
        objOn = (ctrl1 & 0x10) != 0; bgOn = (ctrl1 & 0x08) != 0; leftObjOn = (ctrl1 & 0x04) != 0; leftBgOn = (ctrl1 & 0x02) != 0;
    }

    // nofrendo's own default palette (nes_pal.c is not part of this file; the classic Nofrendo table is
    // not pinned here, so the board's common 64-colour table is used: a stated adaptation).
    static readonly byte[] Pal = {
        84,84,84, 0,30,116, 8,16,144, 48,0,136, 68,0,100, 92,0,48, 84,4,0, 60,24,0, 32,42,0, 8,58,0, 0,64,0, 0,60,0, 0,50,60, 0,0,0, 0,0,0, 0,0,0,
        152,150,152, 8,76,196, 48,50,236, 92,30,228, 136,20,176, 160,20,100, 152,34,32, 120,60,0, 84,90,0, 40,114,0, 8,124,0, 0,118,40, 0,102,120, 0,0,0, 0,0,0, 0,0,0,
        236,238,236, 76,154,236, 120,124,236, 176,98,236, 228,84,236, 236,88,180, 236,106,100, 212,136,32, 160,170,0, 116,196,0, 76,208,32, 56,204,108, 56,180,204, 60,60,60, 0,0,0, 0,0,0,
        236,238,236, 168,204,236, 188,188,236, 212,178,236, 236,174,236, 236,174,212, 236,180,176, 228,196,144, 204,210,120, 180,222,120, 168,226,144, 152,226,180, 160,214,228, 160,162,160, 0,0,0, 0,0,0,
    };
}
}
