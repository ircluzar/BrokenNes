// PPU_MES010 - Mesen 0.1.0's picture chip, ported from SourMesen/Mesen tag 0.1.0 (2016-06-22, the public import day):
// Core/PPU.cpp (959 lines) and Core/PPU.h. No header in the file; the repository is GPL-3.0.
//   Mesen, Copyright (C) Sour (M. Bibaud). GNU General Public License, version 3. See THIRD_PARTY_NOTICES.md (2b, PPU harvest).
//
// PPU harvest climb, runners-up (docs/projects/ppu-harvest-catalog.md): an art instrument, ported FAITHFULLY with its flaws.
// It is already a dot-based PPU (one Exec per dot, scanline -1..260), so most games look right; the jank is in the corners
// the early Mesen still "approximated", which later releases fixed one by one.
//
// Characteristic jank, all of it kept on purpose:
//  * Sprite pattern fetches are one step per sprite at dots 260, 268, ... 316 ("This is an approximation"), not the
//    8-dot fetch sequence. Secondary-OAM entries with Y >= 240 are fetched as tile $FF instead (it never checks the scanline again).
//  * Two $2007 reads within two CPU cycles: the second is ignored and returns open bus (the "simplest solution" comment).
//  * OAM DMA writes through a 256-byte counter that starts at OAMADDR, bypassing the during-rendering $2004 rule (its
//    own "probably not the correct solution" comment).
//  * $2004 writes during rendering bump OAMADDR by 4 and write nothing; byte 2 is masked to $E3 only outside rendering.
//  * The sprite-0 hit is latched one dot late and only raised at dot 340 of the line, unless a $2002 read in between
//    catches it (UpdateStatusFlag); never at x=255.
//  * The rendering-enable bit takes effect one dot late; the odd-frame dot skip goes by its own frame counter's parity.
//  * The OAMADDR >= 8 corruption copy runs at dot 0 of the pre-render line; sprite evaluation (with the overflow bug's
//    diagonal H+L increment) also runs on the pre-render line, where nothing is ever in range.
//  * A $2002 read clears the blue-emphasis flag (but not the emphasis bits already in the pixels).
//  * Per-bit open-bus decay after 30 frames, by the PPU's frame counter.
//  * Secondary OAM is cleared over dots 1-64 two dots a byte, so dot 64 aims one byte past its end (C++ overran into the
//    next member; the stray write is dropped here).
//  * Forced blanking with v in $3F00-$3FFF shows that palette entry instead of the backdrop.
//
// Board adaptation (no jank of its own, listed so nothing is hidden):
//  * NTSC only (Mesen's PAL/Dendy branches are dropped; the board is NTSC). The "remove sprite limit" option is off.
//  * Mesen's _ignoreVramRead counted CPU cycles from inside the PPU; this board steps the PPU in batches, so the rule is
//    kept by CPU cycle instead (Bus.CurrentCpuAccessCycle): a $2007 read on the cycle right after the last one is ignored.
//    On a run path that does not mark instructions the cycle is unknown and nothing is ignored.
//  * Mesen raised and lowered a level NMI line (SetNMIFlag/ClearNMIFlag). Here the vblank NMI is held until dot 3 of
//    line 241 and dropped if a $2002 read or a $2000 write cleared it first, which is what ClearNMIFlag achieved.
//  * MMC3 counted A12 rises inside Mesen's MemoryManager. Here it is clocked at the first sprite fetch (dot 260) of
//    visible and pre-render lines while rendering is on; the $2006/$2007 A12 clocks are not reproduced. MMC5's line
//    hook runs at dot 3. MMC2/MMC4 latching happens in the cartridge, as for every core.
//  * The RGB palette and emphasis come from Mesen's VideoDecoder, which is not part of this port: the board's 64-colour
//    table stands in, with the classic 3/4 attenuation for emphasis.
using System;
using System.IO;

namespace NesEmulator
{
public class PPU_MES010 : IPPU, IPpuFrameClock
{
    public string CoreName => "Mesen 0.1.0";
    public string Description => "Mesen's first public picture chip (2016, Sour): dot-based, with its early approximations - one-step sprite fetches, a doubled-$2007-read ignore, OAM DMA via a counter, sprite-0 hit raised at line end.";
    public int Performance => 6;
    public int Rating => 4;
    public string Category => "Harvest";

    const int W = 256, H = 240;
    const int VblankEnd = 260;

    readonly Bus bus;
    byte[] ntaram = new byte[0x800];

    // PPUState
    byte control, mask, status; int spriteRamAddr; ushort videoRamAddr; byte xScroll; ushort tmpVideoRamAddr; bool writeToggle;
    ushort highBitShift, lowBitShift;
    // PPUControlFlags
    bool fVerticalWrite; ushort fSpritePatternAddr, fBackgroundPatternAddr; bool fLargeSprites, fVBlank, fGrayscale, fBackgroundMask, fSpriteMask, fBackgroundEnabled, fSpritesEnabled, fIntensifyRed, fIntensifyGreen, fIntensifyBlue;
    // PPUStatusFlags
    bool sSpriteOverflow, sSprite0Hit, sVerticalBlank;

    int scanline, cycle; uint frameCount; byte memoryReadBuffer;
    readonly byte[] paletteRAM = new byte[0x20];
    byte[] spriteRAM = new byte[0x100];
    readonly byte[] secondarySpriteRAM = new byte[0x20];
    readonly ushort[] outputBuffer = new ushort[W * H];
    ushort intensifyColorBits; byte paletteRamMask = 0x3F;

    struct Tile { public byte Low, High; public int PaletteOffset; public ushort TileAddr; public byte OffsetY; }
    struct Sprite { public byte Low, High; public int PaletteOffset; public bool HorizontalMirror, BackgroundPriority, VerticalMirror; public byte SpriteX; }
    Tile currentTile, nextTile, previousTile;
    readonly Sprite[] spriteTiles = new Sprite[64];
    int spriteCount, secondaryOAMAddr; bool sprite0Visible; int overflowSpriteAddr, spriteIndex;
    byte openBus; readonly int[] openBusDecayStamp = new int[8];
    long lastVramReadCycle = -1; int spriteDmaCounter, spriteDmaAddr;
    byte oamCopybuffer; bool spriteInRange, sprite0Added; int sprite0HitCycle; byte spriteAddrH, spriteAddrL; bool oamCopyDone; bool renderingEnabled;
    // board adaptation state
    bool nmiPending; long dotCounter;
    byte[]? frameBuffer;

    public PPU_MES010(Bus bus)
    {
        this.bus = bus;
        byte[] boot = { 0x09, 0x01, 0x00, 0x01, 0x00, 0x02, 0x02, 0x0D, 0x08, 0x10, 0x08, 0x24, 0x00, 0x00, 0x04, 0x2C,
                        0x09, 0x01, 0x34, 0x03, 0x00, 0x04, 0x00, 0x14, 0x08, 0x3A, 0x00, 0x02, 0x00, 0x20, 0x2C, 0x08 };
        Array.Copy(boot, paletteRAM, 0x20);
        Array.Fill(spriteRAM, (byte)0xFF);
        Array.Fill(secondarySpriteRAM, (byte)0xFF);
        Reset();
    }

    void Reset()
    {
        sprite0HitCycle = -1; renderingEnabled = false; lastVramReadCycle = -1; openBus = 0; Array.Clear(openBusDecayStamp);
        control = mask = status = 0; spriteRamAddr = 0; videoRamAddr = 0; xScroll = 0; tmpVideoRamAddr = 0; writeToggle = false; highBitShift = lowBitShift = 0;
        SetControlFlags(0); SetMaskFlags(0);
        sSpriteOverflow = sSprite0Hit = sVerticalBlank = false;
        scanline = 0; cycle = 0; frameCount = 1; memoryReadBuffer = 0;
    }

    public int ProbeScanline => scanline < 0 ? 261 : scanline;

    public void Step(int cycles)
    {
        for (int c = 0; c < cycles; c++)
        {
            Exec();
            dotCounter++;
        }
    }

    void Exec()
    {
        if (cycle == 340)
        {
            cycle = -1;
            if (++scanline > VblankEnd) scanline = -1;
            if (bus?.Butcher is { } bnd)
            {
                byte c0 = control, m0 = mask, c1 = control, m1 = mask;
                bnd.NesCoreLine(ProbeScanline, ref c1, ref m1);
                if (c1 != c0) { control = c1; SetControlFlags(c1); }
                if (m1 != m0) { mask = m1; SetMaskFlags(m1); }
            }
        }
        cycle++;

        if (scanline != -1 && scanline < 240) ProcessVisibleScanline();
        else if (scanline == -1) ProcessPrerenderScanline();
        else if (scanline == 241) BeginVBlank();
        else if (scanline == VblankEnd) EndVBlank();

        BoardHooks();

        renderingEnabled = fBackgroundEnabled || fSpritesEnabled;
    }

    void BoardHooks()
    {
        if (scanline == 241 && cycle == 3 && nmiPending) { nmiPending = false; bus?.cpu?.RequestNMI(); }
        if (scanline >= 0 && scanline < 240 && cycle == 3 && bus?.cartridge?.mapper is Mapper5 mmc5)
        {
            mmc5.PpuScanlineHook(scanline, renderingEnabled);
            if (mmc5.IsIrqAsserted()) bus.cpu.RequestIRQ(true);
        }
        if (scanline < 240 && cycle == 260 && renderingEnabled && bus?.cartridge?.mapper is Mapper4 mmc3)
        {
            mmc3.ClockScanlineCounterHardware();
            if (mmc3.IRQPending()) { bus.cpu.RequestIRQ(true); mmc3.ClearIRQ(); }
        }
    }

    void SetNMIFlag()
    {
        if (scanline == 241 && cycle < 3) nmiPending = true; else bus?.cpu?.RequestNMI();
    }
    void ClearNMIFlag() { nmiPending = false; }

    void UpdateVideoRamAddr()
    {
        if (scanline >= 239 || !renderingEnabled)
        {
            videoRamAddr = (ushort)(videoRamAddr + (fVerticalWrite ? 32 : 1));
            ReadVRAM(videoRamAddr);
        }
        else { IncHorizontalScrolling(); IncVerticalScrolling(); }
    }

    void SetOpenBus(byte m, byte value)
    {
        if (m == 0xFF) { openBus = value; for (int i = 0; i < 8; i++) openBusDecayStamp[i] = (int)frameCount; return; }
        int ob = openBus << 8;
        for (int i = 0; i < 8; i++)
        {
            ob >>= 1;
            if ((m & 1) != 0) { if ((value & 1) != 0) ob |= 0x80; else ob &= 0xFF7F; openBusDecayStamp[i] = (int)frameCount; }
            else if ((int)frameCount - openBusDecayStamp[i] > 30) ob &= 0xFF7F;
            value >>= 1; m >>= 1;
        }
        openBus = (byte)(ob & 0xFF);
    }

    byte ApplyOpenBus(byte m, byte value) { SetOpenBus((byte)~m, value); return (byte)(value | (openBus & m)); }

    public byte ReadPPURegister(ushort address)
    {
        byte openBusMask = 0xFF, ret = 0;
        switch (address & 7)
        {
            case 2:
                writeToggle = false;
                fIntensifyBlue = false;
                UpdateStatusFlag();
                ret = status; openBusMask = 0x1F;
                break;
            case 4:
                if (scanline <= 240 && renderingEnabled && (cycle >= 257 || cycle <= 64))
                {
                    if (cycle >= 257 && cycle <= 320)
                    {
                        int step = ((cycle - 257) % 8) > 3 ? 3 : ((cycle - 257) % 8);
                        secondaryOAMAddr = (cycle - 257) / 8 * 4 + step;
                        oamCopybuffer = secondarySpriteRAM[secondaryOAMAddr];
                    }
                    ret = oamCopybuffer;
                }
                else ret = spriteRAM[spriteRamAddr];
                openBusMask = 0x00;
                break;
            case 7:
                long cyc = bus?.CurrentCpuAccessCycle ?? -1;
                if (cyc >= 0 && lastVramReadCycle >= 0 && cyc - lastVramReadCycle == 1) openBusMask = 0xFF;
                else
                {
                    ret = memoryReadBuffer;
                    memoryReadBuffer = ReadVRAM(videoRamAddr);
                    if ((videoRamAddr & 0x3FFF) >= 0x3F00) { ret = (byte)(ReadPaletteRAM(videoRamAddr) | (openBus & 0xC0)); openBusMask = 0xC0; }
                    else openBusMask = 0x00;
                    UpdateVideoRamAddr();
                    lastVramReadCycle = cyc;
                }
                break;
        }
        return ApplyOpenBus(openBusMask, ret);
    }

    public void WritePPURegister(ushort address, byte value)
    {
        SetOpenBus(0xFF, value);
        switch (address & 7)
        {
            case 0: SetControlRegister(value); break;
            case 1: SetMaskRegister(value); break;
            case 3: spriteRamAddr = value; break;
            case 4: WriteSpriteData(value); break;
            case 5:
                if (writeToggle) tmpVideoRamAddr = (ushort)((tmpVideoRamAddr & ~0x73E0) | ((value & 0xF8) << 2) | ((value & 0x07) << 12));
                else { xScroll = (byte)(value & 7); tmpVideoRamAddr = (ushort)((tmpVideoRamAddr & ~0x001F) | (value >> 3)); }
                writeToggle = !writeToggle;
                break;
            case 6:
                if (writeToggle) { tmpVideoRamAddr = (ushort)((tmpVideoRamAddr & ~0x00FF) | value); videoRamAddr = tmpVideoRamAddr; ReadVRAM(videoRamAddr); }
                else tmpVideoRamAddr = (ushort)((tmpVideoRamAddr & ~0xFF00) | ((value & 0x3F) << 8));
                writeToggle = !writeToggle;
                break;
            case 7:
                if ((videoRamAddr & 0x3FFF) >= 0x3F00) WritePaletteRAM(videoRamAddr, value);
                else WriteVRAM(videoRamAddr, value);
                UpdateVideoRamAddr();
                break;
        }
    }

    void WriteSpriteData(byte value)
    {
        if (bus?.Butcher is { SpritesActive: true } sb) value = sb.FilterOamWrite((byte)(spriteDmaCounter > 0 ? spriteDmaAddr : spriteRamAddr), value);
        if (spriteDmaCounter > 0) { spriteRAM[spriteDmaAddr & 0xFF] = value; spriteDmaAddr++; spriteDmaCounter--; }
        else if (scanline >= 240 || !renderingEnabled)
        {
            if ((spriteRamAddr & 3) == 2) value &= 0xE3;
            spriteRAM[spriteRamAddr] = value;
            spriteRamAddr = (spriteRamAddr + 1) & 0xFF;
        }
        else spriteRamAddr = (spriteRamAddr + 4) & 0xFF;
    }

    // SpriteDMA: _spriteDmaCounter = 0x100 from OAMADDR, then CPU::RunDMATransfer's 256 writes to $2004.
    public void WriteOAMDMA(byte page)
    {
        spriteDmaCounter = 0x100; spriteDmaAddr = spriteRamAddr;
        ushort t = (ushort)(page << 8);
        for (int x = 0; x < 256; x++) WriteSpriteData(bus.Read((ushort)(t + x)));
    }

    byte ReadPaletteRAM(int addr)
    {
        addr &= 0x1F;
        if (addr == 0x10 || addr == 0x14 || addr == 0x18 || addr == 0x1C) addr &= ~0x10;
        return (byte)(paletteRAM[addr] & paletteRamMask);
    }

    void WritePaletteRAM(int addr, byte value)
    {
        addr &= 0x1F;
        if (addr == 0x10 || addr == 0x14 || addr == 0x18 || addr == 0x1C) addr &= ~0x10;
        paletteRAM[addr] = value;
    }

    void SetControlFlags(byte v)
    {
        fVerticalWrite = (v & 0x04) != 0;
        fSpritePatternAddr = (ushort)((v & 0x08) != 0 ? 0x1000 : 0);
        fBackgroundPatternAddr = (ushort)((v & 0x10) != 0 ? 0x1000 : 0);
        fLargeSprites = (v & 0x20) != 0;
        fVBlank = (v & 0x80) != 0;
    }

    void SetControlRegister(byte value)
    {
        control = value;
        tmpVideoRamAddr = (ushort)((tmpVideoRamAddr & ~0x0C00) | ((value & 3) << 10));
        bool originalVBlank = fVBlank;
        SetControlFlags(value);
        if (!originalVBlank && fVBlank && sVerticalBlank && (scanline != -1 || cycle != 0)) SetNMIFlag();
        if (scanline == 241 && cycle < 3 && !fVBlank) ClearNMIFlag();
    }

    void SetMaskFlags(byte v)
    {
        fGrayscale = (v & 0x01) != 0; fBackgroundMask = (v & 0x02) != 0; fSpriteMask = (v & 0x04) != 0;
        fBackgroundEnabled = (v & 0x08) != 0; fSpritesEnabled = (v & 0x10) != 0; fIntensifyBlue = (v & 0x80) != 0;
        paletteRamMask = (byte)(fGrayscale ? 0x30 : 0x3F);
        fIntensifyRed = (v & 0x20) != 0; fIntensifyGreen = (v & 0x40) != 0;
        intensifyColorBits = (ushort)((v & 0xE0) << 1);
    }

    void SetMaskRegister(byte value) { mask = value; SetMaskFlags(value); }

    void UpdateStatusFlag()
    {
        if (sprite0HitCycle >= 0 && sprite0HitCycle < cycle) { sSprite0Hit = true; sprite0HitCycle = -1; }
        status = (byte)((sSpriteOverflow ? 0x20 : 0) | (sSprite0Hit ? 0x40 : 0) | (sVerticalBlank ? 0x80 : 0));
        sVerticalBlank = false;
        if (scanline == 241 && cycle < 3)
        {
            sVerticalBlank = false;
            ClearNMIFlag();
            if (cycle == 0) status = (byte)((sSpriteOverflow ? 0x20 : 0) | (sSprite0Hit ? 0x40 : 0));
        }
    }

    void IncVerticalScrolling()
    {
        int addr = videoRamAddr;
        if ((addr & 0x7000) != 0x7000) addr += 0x1000;
        else
        {
            addr &= ~0x7000;
            int y = (addr & 0x03E0) >> 5;
            if (y == 29) { y = 0; addr ^= 0x0800; }
            else if (y == 31) y = 0;
            else y++;
            addr = (addr & ~0x03E0) | (y << 5);
        }
        videoRamAddr = (ushort)addr;
    }

    void IncHorizontalScrolling()
    {
        int addr = videoRamAddr;
        if ((addr & 0x001F) == 31) addr = (addr & ~0x001F) ^ 0x0400; else addr++;
        videoRamAddr = (ushort)addr;
    }

    int GetNameTableAddr() => 0x2000 | (videoRamAddr & 0x0FFF);
    int GetAttributeAddr() => 0x23C0 | (videoRamAddr & 0x0C00) | ((videoRamAddr >> 4) & 0x38) | ((videoRamAddr >> 2) & 0x07);

    void LoadTileInfo()
    {
        if (!renderingEnabled) return;
        switch ((cycle - 1) & 7)
        {
            case 0:
                previousTile = currentTile;
                currentTile = nextTile;
                if (cycle > 1 && cycle < 256) LoadNextTile();
                int tileIndex = ReadVRAM(GetNameTableAddr());
                nextTile.TileAddr = (ushort)((tileIndex << 4) | (videoRamAddr >> 12) | fBackgroundPatternAddr);
                nextTile.OffsetY = (byte)(videoRamAddr >> 12);
                break;
            case 2:
                int shift = ((videoRamAddr >> 4) & 0x04) | (videoRamAddr & 0x02);
                nextTile.PaletteOffset = ((ReadVRAM(GetAttributeAddr()) >> shift) & 3) << 2;
                break;
            case 3: nextTile.Low = ReadVRAM(nextTile.TileAddr); break;
            case 5:
                nextTile.High = ReadVRAM(nextTile.TileAddr + 8);
                if (cycle == 334) InitializeShiftRegisters();
                break;
        }
    }

    void LoadSprite(byte spriteY, byte tileIndex, byte attributes, byte spriteX)
    {
        bool backgroundPriority = (attributes & 0x20) != 0, horizontalMirror = (attributes & 0x40) != 0, verticalMirror = (attributes & 0x80) != 0;
        byte lineOffset = verticalMirror ? (byte)((fLargeSprites ? 15 : 7) - (scanline - spriteY)) : (byte)(scanline - spriteY);
        int tileAddr = fLargeSprites
            ? ((((tileIndex & 1) != 0) ? 0x1000 : 0) | ((tileIndex & ~1) << 4)) + (lineOffset >= 8 ? lineOffset + 8 : lineOffset)
            : ((tileIndex << 4) | fSpritePatternAddr) + lineOffset;

        if (spriteIndex < spriteCount && spriteY < 240)
        {
            ref var s = ref spriteTiles[spriteIndex];
            s.BackgroundPriority = backgroundPriority; s.HorizontalMirror = horizontalMirror; s.VerticalMirror = verticalMirror;
            s.PaletteOffset = ((attributes & 3) << 2) | 0x10;
            s.Low = ReadVRAM(tileAddr); s.High = ReadVRAM(tileAddr + 8);
            s.SpriteX = spriteX;
        }
        else
        {
            // "Fetches to sprite 0xFF for remaining sprites/hidden - used by MMC3 IRQ counter"
            tileIndex = 0xFF; lineOffset = 0;
            tileAddr = fLargeSprites ? (0x1000 | ((tileIndex & ~1) << 4)) : ((tileIndex << 4) | fSpritePatternAddr);
            ReadVRAM(tileAddr); ReadVRAM(tileAddr + 8);
        }
        spriteIndex++;
    }

    void LoadSpriteTileInfo()
    {
        int a = spriteIndex * 4;
        if (a > 0x1C) { spriteIndex++; return; }   // C++ read past secondary OAM into the next members here; never reached at 8 fetches a line
        LoadSprite(secondarySpriteRAM[a], secondarySpriteRAM[a + 1], secondarySpriteRAM[a + 2], secondarySpriteRAM[a + 3]);
    }

    void LoadNextTile() { lowBitShift |= nextTile.Low; highBitShift |= nextTile.High; }
    void InitializeShiftRegisters() { lowBitShift = (ushort)((currentTile.Low << 8) | nextTile.Low); highBitShift = (ushort)((currentTile.High << 8) | nextTile.High); }
    void ShiftTileRegisters() { lowBitShift <<= 1; highBitShift <<= 1; }

    int GetPixelColor(out int paletteOffset)
    {
        int offset = xScroll;
        int backgroundColor = 0;
        if ((cycle > 8 || fBackgroundMask) && fBackgroundEnabled)
            backgroundColor = (((lowBitShift << offset) & 0x8000) >> 15) | (((highBitShift << offset) & 0x8000) >> 14);

        if ((cycle > 8 || fSpriteMask) && fSpritesEnabled)
        {
            for (int i = 0; i < spriteCount; i++)
            {
                int shift = -(spriteTiles[i].SpriteX - cycle + 1);
                if (shift >= 0 && shift < 8)
                {
                    ref var sp = ref spriteTiles[i];
                    int spriteColor = sp.HorizontalMirror
                        ? ((sp.Low >> shift) & 1) | (((sp.High >> shift) & 1) << 1)
                        : (((sp.Low << shift) & 0x80) >> 7) | (((sp.High << shift) & 0x80) >> 6);
                    if (spriteColor != 0)
                    {
                        if (i == 0 && backgroundColor != 0 && sprite0Visible && cycle != 256 && fBackgroundEnabled && !sSprite0Hit && sprite0HitCycle == -1)
                            sprite0HitCycle = cycle;
                        if (backgroundColor == 0 || !sp.BackgroundPriority) { paletteOffset = sp.PaletteOffset; return spriteColor; }
                        break;
                    }
                }
            }
        }
        paletteOffset = ((offset + ((cycle - 1) & 7) < 8) ? previousTile : currentTile).PaletteOffset;
        return backgroundColor;
    }

    void DrawPixel()
    {
        int idx = (scanline << 8) + cycle - 1;
        ushort pixel;
        if (renderingEnabled || (videoRamAddr & 0x3F00) != 0x3F00)
        {
            int color = GetPixelColor(out int paletteOffset);
            pixel = (ushort)((color == 0 ? ReadPaletteRAM(0x3F00) : ReadPaletteRAM(0x3F00 + paletteOffset + color)) | intensifyColorBits);
        }
        else pixel = (ushort)(ReadPaletteRAM(videoRamAddr) | intensifyColorBits);
        outputBuffer[idx] = pixel;
    }

    void ProcessPreVBlankScanline()
    {
        if (renderingEnabled)
        {
            if ((cycle > 0 && cycle < 256 && (cycle & 7) == 0) || cycle == 328 || cycle == 336) IncHorizontalScrolling();
            else if (cycle == 256) IncVerticalScrolling();
            else if (cycle == 257) videoRamAddr = (ushort)((videoRamAddr & ~0x041F) | (tmpVideoRamAddr & 0x041F));
        }
        if (cycle >= 257 && cycle <= 320)
        {
            if (renderingEnabled)
            {
                spriteRamAddr = 0;
                if ((cycle - 260) % 8 == 0) LoadSpriteTileInfo();
                else if (cycle == 257) spriteIndex = 0;
            }
        }
        else if (cycle == 321 && renderingEnabled) oamCopybuffer = secondarySpriteRAM[0];
    }

    void ProcessPrerenderScanline()
    {
        ProcessPreVBlankScanline();
        if (renderingEnabled && cycle == 0 && spriteRamAddr >= 0x08) Array.Copy(spriteRAM, spriteRamAddr & 0xF8, spriteRAM, 0, 8);
        if (cycle == 1) { sSpriteOverflow = false; sSprite0Hit = false; sVerticalBlank = false; }

        if (cycle >= 1 && cycle <= 256) { LoadTileInfo(); CopyOAMData(); }
        else if (cycle >= 280 && cycle <= 304) { if (renderingEnabled) videoRamAddr = (ushort)((videoRamAddr & ~0x7BE0) | (tmpVideoRamAddr & 0x7BE0)); }
        else if (cycle == 339 && renderingEnabled && (frameCount & 1) != 0) { cycle = -1; scanline = 0; }
        else if (cycle >= 321 && cycle <= 336) LoadTileInfo();
    }

    void ProcessVisibleScanline()
    {
        if (cycle > 0 && cycle <= 256)
        {
            LoadTileInfo();
            DrawPixel();
            ShiftTileRegisters();
            if (renderingEnabled) CopyOAMData();
        }
        else if (cycle >= 321 && cycle <= 336) LoadTileInfo();
        else if (cycle == 340 && sprite0HitCycle >= 0) { sSprite0Hit = true; sprite0HitCycle = -1; }
        ProcessPreVBlankScanline();
    }

    void CopyOAMData()
    {
        // Dot 64 writes index 32, one past secondary OAM (C++ wrote into the next member); dropped here.
        if (cycle < 65) { oamCopybuffer = 0xFF; if ((cycle >> 1) < 0x20) secondarySpriteRAM[cycle >> 1] = oamCopybuffer; return; }
        if (cycle == 65)
        {
            sprite0Added = false; spriteInRange = false; secondaryOAMAddr = 0; overflowSpriteAddr = 0; oamCopyDone = false;
            spriteAddrH = (byte)((spriteRamAddr >> 2) & 0x3F); spriteAddrL = (byte)(spriteRamAddr & 3);
        }
        else if (cycle == 256) { sprite0Visible = sprite0Added; spriteCount = secondaryOAMAddr >> 2; }

        if ((cycle & 1) != 0) { oamCopybuffer = spriteRAM[(spriteAddrH << 2) + spriteAddrL]; return; }
        if (oamCopyDone) { spriteAddrH = (byte)((spriteAddrH + 1) & 0x3F); return; }

        if (!spriteInRange && scanline >= oamCopybuffer && scanline < oamCopybuffer + (fLargeSprites ? 16 : 8)) spriteInRange = true;
        if (secondaryOAMAddr < 0x20)
        {
            secondarySpriteRAM[secondaryOAMAddr] = oamCopybuffer;
            if (spriteInRange)
            {
                spriteAddrL++; secondaryOAMAddr++;
                if (spriteAddrH == 0) sprite0Added = true;
                if (spriteAddrL == 4)
                {
                    spriteInRange = false; spriteAddrL = 0; spriteAddrH = (byte)((spriteAddrH + 1) & 0x3F);
                    if (spriteAddrH == 0) oamCopyDone = true;
                }
            }
            else { spriteAddrH = (byte)((spriteAddrH + 1) & 0x3F); if (spriteAddrH == 0) oamCopyDone = true; }
        }
        else
        {
            if (overflowSpriteAddr == 0) overflowSpriteAddr = spriteAddrH * 4;
            if (spriteInRange)
            {
                sSpriteOverflow = true;
                spriteAddrL = (byte)((spriteAddrL + 1) & 3);
                if (spriteAddrL == 4) { spriteInRange = false; spriteAddrH = (byte)((spriteAddrH + 1) & 0x3F); oamCopyDone = true; spriteAddrL = 0; }   // never true after the &3: kept
            }
            else
            {
                spriteAddrH = (byte)((spriteAddrH + 1) & 0x3F); spriteAddrL = (byte)((spriteAddrL + 1) & 3);
                if (spriteAddrH == 0) oamCopyDone = true;
            }
        }
    }

    void BeginVBlank()
    {
        if (cycle != 0) return;
        SendFrame();
        sVerticalBlank = true;
        if (fVBlank) SetNMIFlag();
    }

    void EndVBlank() { if (cycle == 340) frameCount++; }

    void SendFrame()
    {
        frameBuffer ??= new byte[W * H * 4];
        var fb = frameBuffer;
        for (int i = 0, o = 0; i < W * H; i++)
        {
            int v = outputBuffer[i];
            int q = (v & 0x3F) * 3;
            int r = Pal[q], g = Pal[q + 1], b = Pal[q + 2];
            int e = (v >> 6) & 7;
            if (e != 0)
            {
                if (e == 7) { r = r * 3 / 4; g = g * 3 / 4; b = b * 3 / 4; }
                else { if ((e & 1) == 0) r = r * 3 / 4; if ((e & 2) == 0) g = g * 3 / 4; if ((e & 4) == 0) b = b * 3 / 4; }
            }
            fb[o++] = (byte)r; fb[o++] = (byte)g; fb[o++] = (byte)b; fb[o++] = 255;
        }
    }

    // MemoryManager::ReadVRAM / WriteVRAM, through the board (and the Butchering bends, as the other harvest cores do).
    byte ReadVRAM(int address)
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

    void WriteVRAM(int address, byte value)
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

    public byte[] GetFrameBuffer() { frameBuffer ??= new byte[W * H * 4]; return frameBuffer; }
    public void UpdateFrameBuffer() { }
    public void ClearBuffers() { frameBuffer = null; }
    public void GenerateStaticFrame()
    {
        frameBuffer ??= new byte[W * H * 4];
        var r = new Random((int)(dotCounter & 0x7FFFFFFF));
        for (int i = 0; i < frameBuffer.Length; i += 4) { byte g = (byte)r.Next(256); frameBuffer[i] = frameBuffer[i + 1] = frameBuffer[i + 2] = g; frameBuffer[i + 3] = 255; }
    }

    // StreamState, plus the output buffer and the board adaptation's NMI hold.
    public object GetState()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((byte)0x4D);
        w.Write(control); w.Write(mask); w.Write(status); w.Write(spriteRamAddr); w.Write(videoRamAddr); w.Write(xScroll); w.Write(tmpVideoRamAddr); w.Write(writeToggle);
        w.Write(highBitShift); w.Write(lowBitShift);
        w.Write(sSpriteOverflow); w.Write(sSprite0Hit); w.Write(sVerticalBlank); w.Write(fIntensifyBlue);
        w.Write(scanline); w.Write(cycle); w.Write(frameCount); w.Write(memoryReadBuffer);
        w.Write(paletteRAM); w.Write(spriteRAM); w.Write(secondarySpriteRAM);
        foreach (var t in new[] { currentTile, nextTile, previousTile }) { w.Write(t.Low); w.Write(t.High); w.Write(t.PaletteOffset); w.Write(t.TileAddr); w.Write(t.OffsetY); }
        foreach (var s in spriteTiles) { w.Write(s.SpriteX); w.Write(s.Low); w.Write(s.High); w.Write(s.PaletteOffset); w.Write(s.HorizontalMirror); w.Write(s.BackgroundPriority); w.Write(s.VerticalMirror); }
        w.Write(spriteIndex); w.Write(spriteCount); w.Write(secondaryOAMAddr); w.Write(sprite0Visible); w.Write(overflowSpriteAddr);
        w.Write(oamCopybuffer); w.Write(spriteInRange); w.Write(sprite0Added); w.Write(sprite0HitCycle); w.Write(spriteAddrH); w.Write(spriteAddrL); w.Write(oamCopyDone);
        w.Write(spriteDmaAddr); w.Write(spriteDmaCounter); w.Write(renderingEnabled); w.Write(openBus); foreach (var d in openBusDecayStamp) w.Write(d);
        w.Write(lastVramReadCycle); w.Write(nmiPending);
        foreach (var p in outputBuffer) w.Write(p);
        return new PpuSharedState
        {
            vram = (byte[])ntaram.Clone(), palette = (byte[])paletteRAM.Clone(), oam = (byte[])spriteRAM.Clone(),
            PPUCTRL = control, PPUMASK = mask, PPUSTATUS = status, OAMADDR = (byte)spriteRamAddr, PPUADDR = videoRamAddr, v = videoRamAddr, t = tmpVideoRamAddr, fineX = xScroll,
            addrLatch = writeToggle, scanline = ProbeScanline, scanlineCycle = Math.Max(0, cycle), ppuDataBuffer = memoryReadBuffer, fixPipeline = ms.ToArray(),
        };
    }

    public void SetState(object state)
    {
        PpuSharedState? s = HarvestPpuState.From(state);
        if (s == null) return;
        if (s.vram?.Length == 0x800) ntaram = (byte[])s.vram.Clone();
        if (s.oam?.Length == 0x100) spriteRAM = (byte[])s.oam.Clone();
        var p = s.fixPipeline;
        if (p != null && p.Length > W * H * 2 && p[0] == 0x4D)
        {
            var r = new BinaryReader(new MemoryStream(p));
            r.ReadByte();
            control = r.ReadByte(); mask = r.ReadByte(); status = r.ReadByte(); spriteRamAddr = r.ReadInt32(); videoRamAddr = r.ReadUInt16(); xScroll = r.ReadByte(); tmpVideoRamAddr = r.ReadUInt16(); writeToggle = r.ReadBoolean();
            highBitShift = r.ReadUInt16(); lowBitShift = r.ReadUInt16();
            SetControlFlags(control); SetMaskFlags(mask);
            sSpriteOverflow = r.ReadBoolean(); sSprite0Hit = r.ReadBoolean(); sVerticalBlank = r.ReadBoolean(); fIntensifyBlue = r.ReadBoolean();
            scanline = r.ReadInt32(); cycle = r.ReadInt32(); frameCount = r.ReadUInt32(); memoryReadBuffer = r.ReadByte();
            r.Read(paletteRAM, 0, 0x20); r.Read(spriteRAM, 0, 0x100); r.Read(secondarySpriteRAM, 0, 0x20);
            Tile RT() => new Tile { Low = r.ReadByte(), High = r.ReadByte(), PaletteOffset = r.ReadInt32(), TileAddr = r.ReadUInt16(), OffsetY = r.ReadByte() };
            currentTile = RT(); nextTile = RT(); previousTile = RT();
            for (int i = 0; i < 64; i++) spriteTiles[i] = new Sprite { SpriteX = r.ReadByte(), Low = r.ReadByte(), High = r.ReadByte(), PaletteOffset = r.ReadInt32(), HorizontalMirror = r.ReadBoolean(), BackgroundPriority = r.ReadBoolean(), VerticalMirror = r.ReadBoolean() };
            spriteIndex = r.ReadInt32(); spriteCount = r.ReadInt32(); secondaryOAMAddr = r.ReadInt32(); sprite0Visible = r.ReadBoolean(); overflowSpriteAddr = r.ReadInt32();
            oamCopybuffer = r.ReadByte(); spriteInRange = r.ReadBoolean(); sprite0Added = r.ReadBoolean(); sprite0HitCycle = r.ReadInt32(); spriteAddrH = r.ReadByte(); spriteAddrL = r.ReadByte(); oamCopyDone = r.ReadBoolean();
            spriteDmaAddr = r.ReadInt32(); spriteDmaCounter = r.ReadInt32(); renderingEnabled = r.ReadBoolean(); openBus = r.ReadByte(); for (int i = 0; i < 8; i++) openBusDecayStamp[i] = r.ReadInt32();
            lastVramReadCycle = r.ReadInt64(); nmiPending = r.ReadBoolean();
            for (int i = 0; i < W * H; i++) outputBuffer[i] = r.ReadUInt16();
        }
        else
        {
            // A state from another core: take its registers the way this chip holds them.
            if (s.palette?.Length == 32) for (int i = 0; i < 32; i++) paletteRAM[i] = (byte)(s.palette[i] & 0x3F);
            control = s.PPUCTRL; SetControlFlags(control); mask = s.PPUMASK; SetMaskFlags(mask);
            sVerticalBlank = (s.PPUSTATUS & 0x80) != 0; sSprite0Hit = (s.PPUSTATUS & 0x40) != 0; sSpriteOverflow = (s.PPUSTATUS & 0x20) != 0;
            spriteRamAddr = s.OAMADDR; videoRamAddr = s.v; tmpVideoRamAddr = s.t; xScroll = (byte)(s.fineX & 7); writeToggle = s.addrLatch; memoryReadBuffer = s.ppuDataBuffer;
            scanline = s.scanline >= 261 ? -1 : s.scanline; cycle = Math.Clamp(s.scanlineCycle, 0, 340);
            renderingEnabled = fBackgroundEnabled || fSpritesEnabled; sprite0HitCycle = -1; spriteCount = 0; spriteDmaCounter = 0; nmiPending = false; lastVramReadCycle = -1;
        }
    }

    // Mesen's VideoDecoder/default palette is not part of this port; the board's common 64-colour table stands in for it.
    static readonly byte[] Pal = {
        84,84,84, 0,30,116, 8,16,144, 48,0,136, 68,0,100, 92,0,48, 84,4,0, 60,24,0, 32,42,0, 8,58,0, 0,64,0, 0,60,0, 0,50,60, 0,0,0, 0,0,0, 0,0,0,
        152,150,152, 8,76,196, 48,50,236, 92,30,228, 136,20,176, 160,20,100, 152,34,32, 120,60,0, 84,90,0, 40,114,0, 8,124,0, 0,118,40, 0,102,120, 0,0,0, 0,0,0, 0,0,0,
        236,238,236, 76,154,236, 120,124,236, 176,98,236, 228,84,236, 236,88,180, 236,106,100, 212,136,32, 160,170,0, 116,196,0, 76,208,32, 56,204,108, 56,180,204, 60,60,60, 0,0,0, 0,0,0,
        236,238,236, 168,204,236, 188,188,236, 212,178,236, 236,174,236, 236,174,212, 236,180,176, 228,196,144, 204,210,120, 180,222,120, 168,226,144, 152,226,180, 160,214,228, 160,162,160, 0,0,0, 0,0,0,
    };
}
}
