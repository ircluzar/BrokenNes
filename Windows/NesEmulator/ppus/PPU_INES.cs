// PPU_INES - InfoNES's PPU, ported from jay-kumogata/InfoNES @fe3295c: src/InfoNES.cpp (InfoNES_Cycle,
// InfoNES_HSync, InfoNES_DrawLine, InfoNES_GetSprHitY, InfoNES_SetupChr), src/K6502_rw.h (the $2000-$2007
// read/write cases) and src/InfoNES.h (InfoNES_SetupScr, scanline table).
//
//   InfoNES - NES emulator. Copyright (C) 1999-2004 Jay's Factory <jays_factory@excite.co.jp>
//   Apache License 2.0 (LICENSE at the pinned commit); see THIRD_PARTY_NOTICES.md (PPU harvest).
//
// PPU harvest climb (docs/projects/ppu-harvest-catalog.md, #2): an art instrument, ported FAITHFULLY with
// its flaws. InfoNES runs the CPU for a whole line (113 cycles), THEN draws that line from the registers
// as they stand at the end of it, then moves the "_Next" scroll values into place.
//
// Characteristic jank, all of it kept on purpose:
//  * Drawn at the END of each line: mid-line register writes show on the line they were made on.
//  * Scroll is not loopy's v: the frame is drawn from "scroll byte/bit + line number", so the vertical
//    scroll is absolute for the whole frame. A $2006 write outside vblank only changes the nametable
//    (InfoNES_SetupScr has the scroll parts commented out), so $2006 split screens keep the old scroll.
//  * $2002 reads do NOT clear the vblank flag (the clear is under #if 0): it stays up until line 0.
//    Reading $2002 inside vblank with NMI off also resets the nametable select to 0.
//  * Vblank (flag + NMI) starts at line 243 (SCAN_VBLANK_START), three lines late.
//  * Sprite-0 hit: at the start of the frame InfoNES finds sprite 0's first non-blank row and later raises
//    the hit on that line, (X * 113 / 256) CPU cycles in, whether or not the background is opaque there.
//    If $2000 bit 6 is set and sprites are on, the hit also raises an NMI.
//  * No 8-sprite limit: every sprite on the line is drawn (lowest OAM index wins); the overflow flag
//    is set when 8 or more sit on the line.
//  * Screen-off lines, and the left-column clips, are painted with colour 0 of the host palette (black),
//    not the backdrop. The sprite clip blanks the left 8 pixels of the background too.
//  * With the background off, the sprite pass starts from the wrong pointer: sprites are drawn one line
//    up and shifted right by the fine X scroll (only front-priority ones show, there is nothing
//    "transparent" to sit behind).
//  * CHR-RAM games: patterns are decoded into ChrBuf once, at the top of the frame (InfoNES_SetupChr),
//    so pattern writes during the frame only show on the next one. CHR-ROM is read live (InfoNES's
//    mappers rebuild ChrBuf on every bank switch).
//  * Palette: $3F00/$3F10 writes set colour 0 of all eight palettes; $3Fx4/8/C writes are dropped.
//    $2004 reads advance the OAM address. No emphasis, no greyscale.
//
// Board adaptation (no jank of its own): the line is 341 dots on the board's 262-line frame (InfoNES has
// 263 lines of 113 cycles); MMC3 is clocked before the line is drawn (MapperHSync), MMC5 at dot 3.
using System;

namespace NesEmulator
{
public class PPU_INES : IPPU, IPpuFrameClock
{
    public string CoreName => "InfoNES";
    public string Description => "InfoNES's PPU (Jay's Factory, 1999-2004): each line drawn after the CPU ran it, frame-absolute vertical scroll, vblank flag never cleared by $2002, sprite-0 hit predicted at frame start.";
    public int Performance => 10;
    public int Rating => 2;
    public string Category => "Harvest";

    const int W = 256, H = 240;
    const int VblankStart = 243;
    const ushort Trans = 0x8000;

    readonly Bus bus;
    byte[] vram = new byte[2048];
    byte[] oam = new byte[256];
    byte[] palRam = new byte[32];        // PPURAM $3F00-$3F1F (what was written)
    ushort[] palTable = new ushort[32];  // PalTable: NES colour + 1 (0 = host black), | 0x8000 on the colour-0 entries
    byte[] chrBuf = new byte[0x2000];    // CHR-RAM snapshot taken at the top of the frame
    readonly ushort[] work = new ushort[W * H];
    byte[]? frameBuffer;

    byte r0, r1, r2, r3, r7;
    byte scrV, scrVNext, scrH, scrHNext;
    ushort addr, temp;
    int ntBank;          // 0-3 (NAME_TABLE0 + n)
    byte latchFlag;
    int spriteJustHit = 241;
    long hitDot = -1;
    int scanline, dot; long dotCounter;

    public PPU_INES(Bus bus) { this.bus = bus; }

    public int ProbeScanline => scanline;

    bool ChrRam => bus?.cartridge != null && bus.cartridge.chrBanks == 0;

    public void Step(int cycles)
    {
        for (int c = 0; c < cycles; c++)
        {
            if (dot == 0 && scanline < 240 && scanline == spriteJustHit)
                hitDot = dotCounter + (oam[3] * 113 / 256) * 3;   // nStep = SPR_X * STEP_PER_SCANLINE / NES_DISP_WIDTH
            if (hitDot >= 0 && dotCounter >= hitDot)
            {
                hitDot = -1;
                if ((r1 & 0x10) != 0 && (r1 & 0x08) != 0) r2 |= 0x40;
                if ((r0 & 0x40) != 0 && (r1 & 0x10) != 0) bus?.cpu?.RequestNMI();
            }
            if (dot == 3 && scanline < 240 && bus?.cartridge?.mapper is Mapper5 mmc5) { mmc5.PpuScanlineHook(scanline, (r1 & 0x18) != 0); if (mmc5.IsIrqAsserted()) bus.cpu.RequestIRQ(true); }
            if (dot == 340) HSync();
            dot++; dotCounter++;
            if (dot >= 341)
            {
                dot = 0; scanline++;
                if (scanline == 262) scanline = 0;
                if (bus?.Butcher is { } bnd) bnd.NesCoreLine(scanline, ref r0, ref r1);
            }
        }
    }

    // MapperHSync + InfoNES_HSync, at the end of the line the CPU just ran.
    void HSync()
    {
        if (scanline < 240 && (r1 & 0x18) != 0 && bus?.cartridge?.mapper is Mapper4 mmc3) { mmc3.RunScanlineIRQ(); if (mmc3.IRQPending()) { bus.cpu.RequestIRQ(true); mmc3.ClearIRQ(); } }
        if (scanline < 240 && bus?.cartridge != null) DrawLine(scanline);
        scrV = scrVNext; scrH = scrHNext;
        int next = scanline == 261 ? 0 : scanline + 1;
        if (next == 0)
        {
            r2 = 0;
            if (ChrRam) for (int a = 0; a < 0x2000; a++) chrBuf[a] = MemUnbent(a);
            GetSprHitY();
        }
        else if (next == 240) Present();
        else if (next == VblankStart)
        {
            r2 = 0x80; latchFlag = 0;
            if ((r0 & 0x80) != 0) bus?.cpu?.RequestNMI();
        }
    }

    byte Chr(int a) => ChrRam ? chrBuf[a & 0x1FFF] : Mem(a & 0x1FFF);

    // One decoded pixel row of a tile (ChrBuf layout: 8 two-bit pixels).
    void TileRow(int tileAddr, Span<byte> px)
    {
        byte lo = Chr(tileAddr), hi = Chr(tileAddr + 8);
        for (int i = 0; i < 8; i++) px[i] = (byte)(((lo >> (7 - i)) & 1) | (((hi >> (7 - i)) & 1) << 1));
    }

    void DrawLine(int sl)
    {
        int lineBase = sl * W;
        int p = lineBase;
        Span<byte> px = stackalloc byte[8];
        int bgBase = (r0 & 0x10) != 0 ? 0x1000 : 0, spBase = (r0 & 0x08) != 0 ? 0x1000 : 0, spH = (r0 & 0x20) != 0 ? 16 : 8;
        int scrHBit = scrH & 7, scrHByte = scrH >> 3;
        if ((r1 & 0x08) == 0)
        {
            Array.Clear(work, lineBase, W);
        }
        else
        {
            int nt = ntBank;
            int nY = (scrV >> 3) + (sl >> 3);
            int nYBit = (scrV & 7) + (sl & 7);
            if (nYBit > 7) { nY++; nYBit &= 7; }
            if (nY > 29) { nt ^= 2; nY -= 30; }
            int nX = scrHByte;
            int nY4 = (nY & 2) << 1;
            int ntAddr = 0x2000 + nt * 0x400;
            int Pal(int x) => ((Mem(ntAddr + 0x3C0 + (nY / 4) * 8 + (x >> 2)) >> ((x & 2) + nY4)) & 3) << 2;

            int pb = Pal(nX);
            TileRow(bgBase + (Mem(ntAddr + nY * 32 + nX) << 4) + nYBit, px);
            for (int i = scrHBit; i < 8; i++) work[p++] = palTable[pb + px[i]];
            for (nX++; nX < 32; nX++)
            {
                pb = Pal(nX);
                TileRow(bgBase + (Mem(ntAddr + nY * 32 + nX) << 4) + nYBit, px);
                for (int i = 0; i < 8; i++) work[p++] = palTable[pb + px[i]];
            }
            nt ^= 1; ntAddr = 0x2000 + nt * 0x400;
            for (nX = 0; nX < scrHByte; nX++)
            {
                pb = Pal(nX);
                TileRow(bgBase + (Mem(ntAddr + nY * 32 + nX) << 4) + nYBit, px);
                for (int i = 0; i < 8; i++) work[p++] = palTable[pb + px[i]];
            }
            pb = Pal(nX);
            TileRow(bgBase + (Mem(ntAddr + nY * 32 + nX) << 4) + nYBit, px);
            for (int i = 0; i < scrHBit; i++) work[p + i] = palTable[pb + px[i]];
            if ((r1 & 0x02) == 0) Array.Clear(work, lineBase, 8);
        }

        if ((r1 & 0x10) != 0)
        {
            r2 &= unchecked((byte)~0x20);
            Span<byte> spr = stackalloc byte[W + 7];
            int cnt = 0;
            for (int s = 63; s >= 0; s--)
            {
                int o = s * 4;
                int nY = oam[o] + 1;
                if (nY > sl || nY + spH <= sl) continue;
                cnt++;
                int attr = oam[o + 2];
                int yb = sl - nY;
                if ((attr & 0x80) != 0) yb = spH - yb - 1;
                int tile = oam[o + 1];
                int ta = spH == 16 ? ((tile & 1) << 12) + ((tile & 0xFE) << 4) + (yb >= 8 ? 16 + yb - 8 : yb) : spBase + (tile << 4) + yb;
                TileRow(ta, px);
                attr ^= 0x20;
                byte col = (byte)((attr & 0x23) << 2);
                int x = oam[o + 3];
                for (int i = 0; i < 8; i++)
                {
                    byte d = (attr & 0x40) != 0 ? px[7 - i] : px[i];
                    if (d != 0) spr[x + i] = (byte)(col | d);
                }
            }
            // pPoint -= (NES_DISP_WIDTH - PPU_Scr_H_Bit): right when the background was drawn, one line up otherwise.
            int pp = (r1 & 0x08) != 0 ? lineBase : lineBase - W + scrHBit;
            for (int x = 0; x < W; x++)
            {
                int d = spr[x];
                int t = pp + x;
                if (t < 0) continue;
                if (d != 0 && ((d & 0x80) != 0 || (work[t] & Trans) != 0)) work[t] = palTable[(d & 0xF) + 0x10];
            }
            if ((r1 & 0x04) == 0) Array.Clear(work, lineBase, 8);
            if (cnt >= 8) r2 |= 0x20;
        }
    }

    // InfoNES_GetSprHitY
    void GetSprHitY()
    {
        int spH = (r0 & 0x20) != 0 ? 16 : 8;
        bool vflip = (oam[2] & 0x80) != 0;
        int tile = oam[1];
        if (oam[0] + 1 <= 240 && oam[0] > 0)
        {
            spriteJustHit = 241;
            for (int line = 0; line < spH; line++)
            {
                int row = vflip ? spH - 1 - line : line;
                int ta = spH == 16 ? ((tile & 1) << 12) + ((tile & 0xFE) << 4) + (row >= 8 ? 16 + row - 8 : row) : ((r0 & 0x08) != 0 ? 0x1000 : 0) + (tile << 4) + row;
                if ((Chr(ta) | Chr(ta + 8)) != 0) { spriteJustHit = oam[0] + 1 + line; break; }
            }
            // (InfoNES leaves SpriteJustHit as it was when sprite 0 has no pixels at all.)
        }
        else spriteJustHit = 241;
    }

    void Present()
    {
        frameBuffer ??= new byte[W * H * 4];
        var fb = frameBuffer;
        for (int i = 0, o = 0; i < W * H; i++)
        {
            int c = work[i] & 0x7F;
            if (c == 0) { fb[o++] = 0; fb[o++] = 0; fb[o++] = 0; }
            else { int q = ((c - 1) & 0x3F) * 3; fb[o++] = Pal[q]; fb[o++] = Pal[q + 1]; fb[o++] = Pal[q + 2]; }
            fb[o++] = 255;
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
        if (address >= 0x3000) address -= 0x1000;
        if (bus?.cartridge?.mapper is IMapper m && m.TryPpuNametableRead((ushort)address, out byte v)) return v;
        return vram[MirrorNt(address)];
    }

    void MemWrite(int address, byte value)
    {
        address &= 0x3FFF;
        if (address < 0x2000) { bus?.cartridge?.PPUWrite((ushort)address, value); return; }
        if (address >= 0x3000) address -= 0x1000;
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

    public byte ReadPPURegister(ushort address)
    {
        switch (address & 7)
        {
            case 7:
            {
                int a = addr & 0x3FFF;
                byte ret = r7;
                addr += (ushort)((r0 & 0x04) != 0 ? 32 : 1);
                r7 = Mem(a);
                return ret;
            }
            case 4: return oam[r3++];
            case 2:
            {
                byte ret = r2;
                latchFlag = 0;
                if (scanline >= VblankStart && (r0 & 0x80) == 0) { r0 &= unchecked((byte)~0x03); ntBank = 0; }
                return ret;
            }
            default: return r7;
        }
    }

    public void WritePPURegister(ushort address, byte value)
    {
        switch (address & 7)
        {
            case 0:
                r0 = value; ntBank = value & 3;
                temp = (ushort)((temp & 0xF3FF) | ((value & 3) << 10));
                break;
            case 1: r1 = value; break;
            case 3: r3 = value; break;
            case 4: oam[r3] = bus?.Butcher is { SpritesActive: true } sb ? sb.FilterOamWrite(r3, value) : value; r3++; break;
            case 5:
                if (latchFlag != 0)
                {
                    scrVNext = (byte)(value > 239 ? value - 240 : value);
                    if (value > 239) ntBank ^= 2;
                    temp = (ushort)((temp & 0xFC1F) | ((value & 0xF8) << 2));
                    temp = (ushort)((temp & 0x8FFF) | ((value & 0x07) << 12));
                }
                else
                {
                    scrHNext = value;
                    temp = (ushort)((temp & 0xFFE0) | ((value & 0xF8) >> 3));
                }
                latchFlag ^= 1;
                break;
            case 6:
                if (latchFlag != 0)
                {
                    temp = (ushort)((temp & 0xFF00) | value);
                    addr = temp;
                    if ((r2 & 0x80) == 0) ntBank = (addr & 0x0C00) >> 10;   // InfoNES_SetupScr
                }
                else temp = (ushort)((temp & 0x00FF) | ((value & 0x3F) << 8));
                latchFlag ^= 1;
                break;
            case 7:
            {
                int a = addr;
                addr += (ushort)((r0 & 0x04) != 0 ? 32 : 1);
                a &= 0x3FFF;
                if (a < 0x2000) { if (ChrRam) MemWrite(a, value); }
                else if (a < 0x3F00) MemWrite(a, value);
                else if ((a & 0xF) == 0)
                {
                    for (int i = 0; i < 32; i += 4) { palRam[i] = value; palTable[i] = (ushort)(((value & 0x3F) + 1) | Trans); }
                }
                else if ((a & 3) != 0) { palRam[a & 0x1F] = value; palTable[a & 0x1F] = (ushort)((value & 0x3F) + 1); }
                break;
            }
        }
    }

    public void WriteOAMDMA(byte page) { bus.FastOamDma(page, oam, ref r3); }

    public byte[] GetFrameBuffer() { frameBuffer ??= new byte[W * H * 4]; return frameBuffer; }
    public void UpdateFrameBuffer() { }
    public void ClearBuffers() { frameBuffer = null; }
    public void GenerateStaticFrame()
    {
        frameBuffer ??= new byte[W * H * 4];
        var r = new Random((int)(dotCounter & 0x7FFFFFFF));
        for (int i = 0; i < frameBuffer.Length; i += 4) { byte g = (byte)r.Next(256); frameBuffer[i] = frameBuffer[i + 1] = frameBuffer[i + 2] = g; frameBuffer[i + 3] = 255; }
    }

    const int PipeLen = 32 + 0x2000 + 64 + W * H * 2;
    public object GetState()
    {
        var p = new byte[PipeLen];
        int k = 0;
        p[k++] = r0; p[k++] = r1; p[k++] = r2; p[k++] = r3; p[k++] = r7; p[k++] = scrV; p[k++] = scrVNext; p[k++] = scrH; p[k++] = scrHNext; p[k++] = latchFlag; p[k++] = (byte)ntBank;
        BitConverter.TryWriteBytes(p.AsSpan(k), addr); k += 2; BitConverter.TryWriteBytes(p.AsSpan(k), temp); k += 2;
        BitConverter.TryWriteBytes(p.AsSpan(k), spriteJustHit); k += 4;
        BitConverter.TryWriteBytes(p.AsSpan(k), hitDot < 0 ? -1 : (int)(hitDot - dotCounter)); k += 4;
        k = 32;
        chrBuf.CopyTo(p, k); k += 0x2000;
        for (int i = 0; i < 32; i++) { BitConverter.TryWriteBytes(p.AsSpan(k), palTable[i]); k += 2; }
        Buffer.BlockCopy(work, 0, p, k, W * H * 2);
        return new PpuSharedState
        {
            vram = (byte[])vram.Clone(), palette = (byte[])palRam.Clone(), oam = (byte[])oam.Clone(),
            PPUCTRL = r0, PPUMASK = r1, PPUSTATUS = r2, OAMADDR = r3, PPUADDR = addr, v = addr, t = temp, fineX = (byte)(scrH & 7),
            addrLatch = latchFlag != 0, scanline = scanline, scanlineCycle = dot, ppuDataBuffer = r7, fixPipeline = p,
        };
    }

    public void SetState(object state)
    {
        PpuSharedState? s = HarvestPpuState.From(state);
        if (s == null) return;
        if (s.vram?.Length == 2048) vram = (byte[])s.vram.Clone();
        if (s.oam?.Length == 256) oam = (byte[])s.oam.Clone();
        if (s.palette?.Length == 32) palRam = (byte[])s.palette.Clone();
        scanline = s.scanline; dot = s.scanlineCycle;
        var p = s.fixPipeline;
        if (p != null && p.Length == PipeLen)
        {
            int k = 0;
            r0 = p[k++]; r1 = p[k++]; r2 = p[k++]; r3 = p[k++]; r7 = p[k++]; scrV = p[k++]; scrVNext = p[k++]; scrH = p[k++]; scrHNext = p[k++]; latchFlag = p[k++]; ntBank = p[k++];
            addr = BitConverter.ToUInt16(p, k); k += 2; temp = BitConverter.ToUInt16(p, k); k += 2;
            spriteJustHit = BitConverter.ToInt32(p, k); k += 4;
            int rel = BitConverter.ToInt32(p, k);
            hitDot = rel < 0 ? -1 : dotCounter + rel;
            k = 32;
            Array.Copy(p, k, chrBuf, 0, 0x2000); k += 0x2000;
            for (int i = 0; i < 32; i++) { palTable[i] = BitConverter.ToUInt16(p, k); k += 2; }
            Buffer.BlockCopy(p, k, work, 0, W * H * 2);
        }
        else
        {
            // A state from another core: take its registers the way InfoNES would hold them.
            r0 = s.PPUCTRL; r1 = s.PPUMASK; r2 = s.PPUSTATUS; r3 = s.OAMADDR; r7 = s.ppuDataBuffer; addr = s.v; temp = s.t; latchFlag = (byte)(s.addrLatch ? 1 : 0);
            ntBank = (s.t >> 10) & 3;
            scrH = scrHNext = (byte)(((s.t & 0x1F) << 3) | (s.fineX & 7));
            scrV = scrVNext = (byte)((((s.t >> 5) & 0x1F) << 3) | ((s.t >> 12) & 7));
            hitDot = -1; spriteJustHit = 241;
            for (int i = 0; i < 32; i++) palTable[i] = (ushort)((palRam[i] & 0x3F) + 1);
            for (int i = 0; i < 32; i += 4) palTable[i] = (ushort)(((palRam[0] & 0x3F) + 1) | Trans);
            if (ChrRam) for (int a = 0; a < 0x2000; a++) chrBuf[a] = MemUnbent(a);
        }
    }

    // InfoNES's NesPalette is RGB555 (host-specific); the board's common 64-colour table stands in for it.
    static readonly byte[] Pal = {
        84,84,84, 0,30,116, 8,16,144, 48,0,136, 68,0,100, 92,0,48, 84,4,0, 60,24,0, 32,42,0, 8,58,0, 0,64,0, 0,60,0, 0,50,60, 0,0,0, 0,0,0, 0,0,0,
        152,150,152, 8,76,196, 48,50,236, 92,30,228, 136,20,176, 160,20,100, 152,34,32, 120,60,0, 84,90,0, 40,114,0, 8,124,0, 0,118,40, 0,102,120, 0,0,0, 0,0,0, 0,0,0,
        236,238,236, 76,154,236, 120,124,236, 176,98,236, 228,84,236, 236,88,180, 236,106,100, 212,136,32, 160,170,0, 116,196,0, 76,208,32, 56,204,108, 56,180,204, 60,60,60, 0,0,0, 0,0,0,
        236,238,236, 168,204,236, 188,188,236, 212,178,236, 236,174,236, 236,174,212, 236,180,176, 228,196,144, 204,210,120, 180,222,120, 168,226,144, 152,226,180, 160,214,228, 160,162,160, 0,0,0, 0,0,0,
    };
}
}
