namespace NesEmulator
{
// Low Quality PPU core ("LQ")
// Original version aggressively halved horizontal & vertical resolution which caused layout
// issues in many games (HUD truncation / attribute clash looking like missing right edge).
// This revised version keeps the behavioral / timing degradations but restores *almost* full
// pixel resolution so game logic expecting per‑pixel detail is unaffected.
// Current degradations retained:
//  * Mild background fetch bandwidth throttling (rare right-edge fallback when sprite heavy).
//  * Occasional DRAM refresh scanline duplicate (very small probability shimmer).
//  * Color precision crush to a coarse bus (2-2-2) with light attenuation.
// Removed or softened from earlier LQ:
//  * NO forced horizontal pixel pairing anymore (full 256 wide sampling kept).
//  * NO vertical odd-line duplication; every scanline rendered distinctly.
// These changes address the "screen buffer space" complaints while preserving a stylistic
// lower-spec feel.
public class PPU_LQ : IPPU
{
    private Bus bus;
    private byte[] vram; //2KB
    private byte[] paletteRAM; //32 bytes
    private byte[] oam; //256 bytes

    private const int ScreenWidth = 256;
    private const int ScreenHeight = 240;
    private const int TotalScanlines = 262;

    // Registers / latches
    private byte PPUCTRL, PPUMASK, PPUSTATUS, OAMADDR, OAMDATA, PPUSCROLLX, PPUSCROLLY, PPUDATA;
    private ushort PPUADDR;
    private bool addrLatch;
    private byte ppuDataBuffer;
    private byte fineX; private bool scrollLatch; private ushort v; private ushort t;
    private int scanlineCycle; private int scanline;

    // Lazy framebuffer allocation to reduce memory; allocate on first use
    private byte[]? frameBuffer = null;
    private readonly bool[] spritePixelDrawnReuse = new bool[ScreenWidth];
    private int staticFrameCounter;
    private readonly bool[] bgMask = new bool[ScreenWidth];

    // Degradation model:
    //  * Background fetch budget is close to full (restored from 192 -> 252) so most lines render fully.
    //    Heavy sprite lines may still lose a handful of rightmost pixels (ub color fill).
    //  * Sprite penalty lowered to keep visual completeness.
    //  * Max sprites per scanline still slightly reduced vs NES (6 < 8) to preserve flavor.
    //  * Occasional DRAM stall retained with small probability.
    private const int BaseBackgroundFetchBudget = 252; // near-full (vs 256) to keep subtle degradation.
    private const int MinBackgroundFetchBudget = 240;  // ensure majority of scanline always fetched.
    private const int SpritePenaltyPerSprite = 2;      // gentler penalty per sprite.
    private const int MaxSpritesPerScanlineLQ = 6;     // still slightly reduced capability.
    private const double DramRefreshSkipProbability = 0.01; // 1% chance: rarer shimmer.
    private readonly System.Random rand = new System.Random(0xB10C4E); // deterministic for reproducibility.
    private int currentBgFetchBudget;
    private bool duplicateScanlineFromPrevious; // set when DRAM stall triggers.

    public PPU_LQ(Bus bus)
    {
        this.bus = bus;
        vram = new byte[2048]; paletteRAM = new byte[32]; oam = new byte[256];
        InitializeDefaultPalette();
        // Defer framebuffer allocation until first rendering or access
    }

    private void EnsureFrameBuffer()
    {
        if (frameBuffer == null || frameBuffer.Length != ScreenWidth * ScreenHeight * 4)
            frameBuffer = new byte[ScreenWidth * ScreenHeight * 4];
    }

    public void Step(int elapsedCycles)
    {
        for (int c = 0; c < elapsedCycles; c++)
        {
            if (scanline == 0 && scanlineCycle == 0) PPUSTATUS &= 0x3F; // new frame start
            if (scanline >= 0 && scanline < 240 && scanlineCycle == 260)
            {
                if ((PPUMASK & 0x18) != 0 && bus.cartridge.mapper is Mapper4 mmc3)
                {
                    mmc3.RunScanlineIRQ();
                    if (mmc3.IRQPending()) { bus.cpu.RequestIRQ(true); mmc3.ClearIRQ(); }
                }
            }
            scanlineCycle++;
            if (scanlineCycle >= 341)
            {
                scanlineCycle = 0;
                if (scanline >= 0 && scanline < 240)
                {
                    EnsureFrameBuffer();
                    CopyXFromTToV();
                    PrepareScanlineBandwidth(scanline);
                    if (duplicateScanlineFromPrevious && scanline > 0)
                    {
                        // DRAM refresh stall: duplicate the previous scanline (looks like vertical shimmer on motion)
                        System.Buffer.BlockCopy(frameBuffer!, (scanline - 1) * ScreenWidth * 4, frameBuffer!, scanline * ScreenWidth * 4, ScreenWidth * 4);
                    }
                    else
                    {
                        RenderScanline(scanline);
                    }
                    IncrementY();
                }
                if (scanline == 241)
                {
                    PPUSTATUS |= 0x80; if ((PPUCTRL & 0x80) != 0) bus.cpu.RequestNMI();
                }
                if (scanline == 261) v = t;
                scanline++; if (scanline == TotalScanlines) scanline = 0;
            }
        }
    }

    private void RenderScanline(int y)
    {
    EnsureFrameBuffer();
        if (bus?.cartridge == null) return; // test pattern only
        bool bgEnabled = (PPUMASK & 0x08) != 0;
        bool sprEnabled = (PPUMASK & 0x10) != 0;
        if (!bgEnabled && !sprEnabled)
        {
            // Fill with universal background color so stale pixels not shown
            byte ubIdx = paletteRAM[0]; int p = (ubIdx & 0x3F) * 3; byte r = PaletteBytes[p], g = PaletteBytes[p + 1], b = PaletteBytes[p + 2];
            int baseIndex = y * ScreenWidth * 4;
            for (int x = 0; x < ScreenWidth; x++) { int fi = baseIndex + x * 4; frameBuffer![fi] = r; frameBuffer![fi + 1] = g; frameBuffer![fi + 2] = b; frameBuffer![fi + 3] = 255; }
            ApplyLowQualityScanline(y);
            return;
        }
        System.Array.Clear(bgMask, 0, ScreenWidth);
        if (bgEnabled) RenderBackground(y, bgMask);
        if (sprEnabled) RenderSprites(y, bgMask);
        ApplyLowQualityScanline(y);
    }

    // Degrade output after each logical scanline.
    private void ApplyLowQualityScanline(int y)
    {
        // Full per-line quantization only (no vertical duplication anymore).
        QuantizeScanlineColors(y);
    }

    private void QuantizeScanlineColors(int y)
    {
        int baseIdx = y * ScreenWidth * 4;
        for (int x = 0; x < ScreenWidth; x++)
        {
            int idx = baseIdx + x * 4;
            byte r = frameBuffer![idx]; byte g = frameBuffer![idx + 1]; byte b = frameBuffer![idx + 2];
            QuantizeColor(ref r, ref g, ref b);
            frameBuffer![idx] = r; frameBuffer![idx + 1] = g; frameBuffer![idx + 2] = b; frameBuffer![idx + 3] = 255;
        }
    }

    // Reduce precision: RGB 3-3-2 plus mild darken for a dull CRT feel.
    private void QuantizeColor(ref byte r, ref byte g, ref byte b)
    {
        // Fixed hardware-ish 2-2-2 bus (keep it stable frame to frame)
        r &= 0xC0; g &= 0xC0; b &= 0xC0;
        // Light global attenuation to mimic weaker DAC (consistent, not dynamic)
        r = (byte)(r * 5 / 6); g = (byte)(g * 5 / 6); b = (byte)(b * 5 / 6);
    }

    public byte[] GetFrameBuffer() { EnsureFrameBuffer(); return frameBuffer!; }

    public void ClearBuffers()
    {
        // Release framebuffer so it is lazily reallocated on next use.
        frameBuffer = null;
        // Reset minor per-frame flags so next frame starts cleanly.
        duplicateScanlineFromPrevious = false;
        currentBgFetchBudget = 0;
    }

    public void GenerateStaticFrame()
    {
    EnsureFrameBuffer();
        int w = ScreenWidth; int h = ScreenHeight;
        uint frameSeed = (uint)staticFrameCounter * 0x9E3779B1u + 0xB5297A4Du;
        for (int y = 0; y < h; y++)
        {
            uint rowSeed = frameSeed ^ (uint)(y * 0x1F123BB5u);
            for (int x = 0; x < w; x++)
            {
                uint h0 = rowSeed ^ (uint)(x * 0xA24BAEDCu);
                h0 ^= h0 >> 15; h0 *= 0x2C1B3C6Du; h0 ^= h0 >> 12; h0 *= 0x297A2D39u; h0 ^= h0 >> 15;
                byte intensity = (byte)(h0 >> 24);
                byte r = intensity, g = intensity, b = intensity;
                QuantizeColor(ref r, ref g, ref b);
                int idx = (y * w + x) * 4;
                frameBuffer![idx] = r; frameBuffer![idx + 1] = g; frameBuffer![idx + 2] = b; frameBuffer![idx + 3] = 255;
            }
            ApplyLowQualityScanline(y);
        }
        staticFrameCounter++;
    }

    public void UpdateFrameBuffer()
    {
        if (bus?.cartridge == null) AddAnimatedTestElements();
    }

    private void RenderBackground(int scanline, bool[] bgMask)
    {
    EnsureFrameBuffer();
        if ((PPUMASK & 0x08) == 0) return;
        byte ubIdx = paletteRAM[0]; int ubp = (ubIdx & 0x3F) * 3; byte ubR = PaletteBytes[ubp], ubG = PaletteBytes[ubp + 1], ubB = PaletteBytes[ubp + 2];
        ushort renderV = v;
        int tileLimit = 33; // nominal tiles to cover 256 + fine scroll
        int fetchedPixels = 0;
        for (int tile = 0; tile < tileLimit; tile++)
        {
            if (fetchedPixels >= currentBgFetchBudget)
            {
                // Fill remainder with backdrop due to exhausted fetch bandwidth
                int scanlineBaseFill = scanline * ScreenWidth * 4;
                for (int pixel = (tile * 8) - fineX; pixel < ScreenWidth; pixel++)
                {
                    if (pixel < 0) continue;
                    int fi = scanlineBaseFill + pixel * 4;
                    frameBuffer![fi] = ubR; frameBuffer![fi + 1] = ubG; frameBuffer![fi + 2] = ubB; frameBuffer![fi + 3] = 255;
                }
                return;
            }
            int coarseX = renderV & 0x001F; int coarseY = (renderV >> 5) & 0x001F; int nameTable = (renderV >> 10) & 0x0003;
            int baseNTAddr = 0x2000 + (nameTable * 0x400);
            int tileAddr = baseNTAddr + coarseY * 32 + coarseX; byte tileIndex = Read((ushort)tileAddr);
            int fineY = (renderV >> 12) & 0x7;
            int patternTable = (PPUCTRL & 0x10) != 0 ? 0x1000 : 0x0000;
            int patternAddr = patternTable + tileIndex * 16 + fineY;
            byte plane0 = Read((ushort)patternAddr); byte plane1 = Read((ushort)(patternAddr + 8));
            int attributeX = coarseX / 4; int attributeY = coarseY / 4; int attrAddr = baseNTAddr + 0x3C0 + attributeY * 8 + attributeX; byte attrByte = Read((ushort)attrAddr);
            int attrShift = ((coarseY % 4) / 2) * 4 + ((coarseX % 4) / 2) * 2; int paletteIndex = (attrByte >> attrShift) & 0x03;
            int scanlineBase = scanline * ScreenWidth * 4;
            for (int i = 0; i < 8; i++)
            {
                int pixel = tile * 8 + i - fineX; if (pixel < 0 || pixel >= ScreenWidth) continue;
                int bitIndex = 7 - i; int bit0 = (plane0 >> bitIndex) & 1; int bit1 = (plane1 >> bitIndex) & 1; int colorIndex = bit0 | (bit1 << 1);
                int frameIndex = scanlineBase + pixel * 4;
                if (colorIndex == 0)
                {
                    frameBuffer![frameIndex] = ubR; frameBuffer![frameIndex + 1] = ubG; frameBuffer![frameIndex + 2] = ubB; frameBuffer![frameIndex + 3] = 255;
                }
                else
                {
                    bgMask[pixel] = true;
                    int paletteBase = 1 + (paletteIndex << 2); byte idx = paletteRAM[(paletteBase + colorIndex - 1) & 0x1F]; int p = (idx & 0x3F) * 3;
                    frameBuffer![frameIndex] = PaletteBytes[p]; frameBuffer![frameIndex + 1] = PaletteBytes[p + 1]; frameBuffer![frameIndex + 2] = PaletteBytes[p + 2]; frameBuffer![frameIndex + 3] = 255;
                }
                fetchedPixels++;
                if (fetchedPixels >= currentBgFetchBudget) break; // after writing this pixel we may be out
            }
            IncrementX(ref renderV);
        }
    }

    private void RenderSprites(int scanline, bool[] bgMask)
    {
    EnsureFrameBuffer();
    bool showSprites = (PPUMASK & 0x10) != 0; if (!showSprites) return;
        bool isSprite8x16 = (PPUCTRL & 0x20) != 0; System.Array.Clear(spritePixelDrawnReuse, 0, spritePixelDrawnReuse.Length);
        int drawnThisLine = 0;
        for (int i = 0; i < 64; i++)
        {
            if (drawnThisLine >= MaxSpritesPerScanlineLQ) break;
            int offset = i * 4; byte spriteY = oam[offset]; byte tileIndex = oam[offset + 1]; byte attributes = oam[offset + 2]; byte spriteX = oam[offset + 3];
            int tileHeight = isSprite8x16 ? 16 : 8; if (scanline < spriteY || scanline >= spriteY + tileHeight) continue;
            int paletteIndex = attributes & 0b11; bool flipX = (attributes & 0x40) != 0; bool flipY = (attributes & 0x80) != 0; bool priority = (attributes & 0x20) == 0;
            int subY = scanline - spriteY; if (flipY) subY = tileHeight - 1 - subY;
            int subTileIndex = isSprite8x16 ? (tileIndex & 0xFE) + (subY / 8) : tileIndex;
            int patternTable = isSprite8x16 ? ((tileIndex & 1) != 0 ? 0x1000 : 0x0000) : ((PPUCTRL & 0x08) != 0 ? 0x1000 : 0x0000);
            int baseAddr = patternTable + subTileIndex * 16; byte plane0 = Read((ushort)(baseAddr + (subY % 8))); byte plane1 = Read((ushort)(baseAddr + (subY % 8) + 8));
            for (int x = 0; x < 8; x++)
            {
                int bit = flipX ? x : 7 - x; int bit0 = (plane0 >> bit) & 1; int bit1 = (plane1 >> bit) & 1; int color = bit0 | (bit1 << 1); if (color == 0) continue;
                int px = spriteX + x; if (px < 0 || px >= ScreenWidth) continue;
                if (i == 0 && bgMask[px] && color != 0) PPUSTATUS |= 0x40;
                if (spritePixelDrawnReuse[px]) continue;
                if (!priority && bgMask[px]) continue;
                var sc = GetSpriteColor(color, paletteIndex); int frameIndex = (scanline * ScreenWidth + px) * 4;
                frameBuffer![frameIndex] = sc.r; frameBuffer![frameIndex + 1] = sc.g; frameBuffer![frameIndex + 2] = sc.b; frameBuffer![frameIndex + 3] = 255; spritePixelDrawnReuse[px] = true;
            }
            drawnThisLine++;
        }
    }

    private (byte r, byte g, byte b) GetSpriteColor(int colorIndex, int paletteIndex)
    { int paletteBase = 0x11 + (paletteIndex << 2); byte idx = paletteRAM[paletteBase + (colorIndex - 1)]; int p = (idx & 0x3F) * 3; return (PaletteBytes[p], PaletteBytes[p + 1], PaletteBytes[p + 2]); }

    private void AddAnimatedTestElements()
    {
        int frame = scanline + scanlineCycle / 100;
    EnsureFrameBuffer();
    for (int i = 0; i < 2; i++)
        {
            int spriteX = (32 + i * 96 + frame * (i + 1)) % (ScreenWidth - 16);
            int spriteY = 160 + (int)(System.Math.Sin(frame * 0.05 + i) * 30);
            DrawTestSprite(spriteX, spriteY, i);
        }
    }

    private void DrawTestSprite(int x, int y, int spriteType)
    {
        int[] indices = { 0x0F, 0x16, 0x2A, 0x12 }; int idx = indices[spriteType % 4] & 0x3F; int p = idx * 3; (byte r, byte g, byte b) color = (PaletteBytes[p], PaletteBytes[p + 1], PaletteBytes[p + 2]);
    EnsureFrameBuffer();
    for (int dy = 0; dy < 8; dy++) for (int dx = 0; dx < 8; dx++) { int px = x + dx; int py = y + dy; if (px < 0 || px >= ScreenWidth || py < 0 || py >= ScreenHeight) continue; bool draw = (dx == 4) || (dy == 4) || (dx == dy) || (dx == 7 - dy); if (!draw) continue; int index = (py * ScreenWidth + px) * 4; frameBuffer![index] = color.r; frameBuffer![index + 1] = color.g; frameBuffer![index + 2] = color.b; frameBuffer![index + 3] = 255; }
    }

    public byte ReadPPURegister(ushort address)
    {
        byte result = 0x00; switch (address & 0x0007)
        {
            case 0x0002: result = PPUSTATUS; PPUSTATUS &= 0x3F; addrLatch = false; return result;
            case 0x0004: return oam[OAMADDR];
            case 0x0007: result = ppuDataBuffer; ppuDataBuffer = Read(PPUADDR); if (PPUADDR >= 0x3F00) result = ppuDataBuffer; PPUADDR += (ushort)((PPUCTRL & 0x04) != 0 ? 32 : 1); return result;
            default: return 0;
        }
    }

    public void WritePPURegister(ushort address, byte value)
    {
        switch (address & 0x0007)
        {
            case 0x0000: PPUCTRL = value; t = (ushort)((t & 0xF3FF) | ((value & 0x03) << 10)); break;
            case 0x0001: PPUMASK = value; break;
            case 0x0002: PPUSTATUS &= 0x7F; scrollLatch = false; break;
            case 0x0003: OAMADDR = value; break;
            case 0x0004: OAMDATA = value; oam[OAMADDR++] = OAMDATA; break;
            case 0x0005:
                if (!scrollLatch) { PPUSCROLLX = value; fineX = (byte)(value & 0x07); t = (ushort)((t & 0xFFE0) | (value >> 3)); }
                else { PPUSCROLLY = value; t = (ushort)((t & 0x8FFF) | ((value & 0x07) << 12)); t = (ushort)((t & 0xFC1F) | ((value & 0xF8) << 2)); }
                scrollLatch = !scrollLatch; break;
            case 0x0006:
                if (!addrLatch) { t = (ushort)((value << 8) | (t & 0x00FF)); PPUADDR = t; }
                else { t = (ushort)((t & 0xFF00) | value); PPUADDR = t; v = t; }
                addrLatch = !addrLatch; break;
            case 0x0007: PPUDATA = value; Write(PPUADDR, PPUDATA); PPUADDR += (ushort)((PPUCTRL & 0x04) != 0 ? 32 : 1); v = PPUADDR; break;
        }
    }

    public byte Read(ushort address)
    {
        address = (ushort)(address & 0x3FFF);
        if (address < 0x2000) return bus.cartridge.PPURead(address);
        else if (address <= 0x3EFF) { ushort mirrored = MirrorVRAMAddress(address); return vram[mirrored]; }
        else if (address <= 0x3FFF) { ushort mirrored = (ushort)(address & 0x1F); if (mirrored >= 0x10 && (mirrored % 4) == 0) mirrored -= 0x10; return paletteRAM[mirrored]; }
        return 0;
    }

    public void Write(ushort address, byte value)
    {
        address = (ushort)(address & 0x3FFF);
        if (address < 0x2000) bus.cartridge.PPUWrite(address, value);
        else if (address <= 0x3EFF) { ushort mirrored = MirrorVRAMAddress(address); vram[mirrored] = value; }
        else if (address <= 0x3FFF) { ushort mirrored = (ushort)(address & 0x1F); if (mirrored >= 0x10 && (mirrored % 4) == 0) mirrored -= 0x10; paletteRAM[mirrored] = value; }
    }

    private ushort MirrorVRAMAddress(ushort address)
    {
        ushort offset = (ushort)(address & 0x0FFF); int ntIndex = offset / 0x400; int innerOffset = offset % 0x400;
        return bus.cartridge.mirroringMode switch
        {
            Mirroring.Vertical => (ushort)((ntIndex % 2) * 0x400 + innerOffset),
            Mirroring.Horizontal => (ushort)(((ntIndex / 2) * 0x400) + innerOffset),
            Mirroring.SingleScreenA => (ushort)(innerOffset),
            Mirroring.SingleScreenB => (ushort)(0x400 + innerOffset),
            _ => offset
        };
    }

    public void WriteOAMDMA(byte page)
    { bus.FastOamDma(page, oam, ref OAMADDR); }

    private void IncrementY()
    {
        if ((v & 0x7000) != 0x7000) v += 0x1000; else { v &= 0x8FFF; int y = (v & 0x03E0) >> 5; if (y == 29) { y = 0; v ^= 0x0800; } else if (y == 31) { y = 0; } else y++; v = (ushort)((v & 0xFC1F) | (y << 5)); }
    }
    private void IncrementX(ref ushort addr)
    { if ((addr & 0x001F) == 31) { addr &= 0xFFE0; addr ^= 0x0400; } else addr++; }
    private void CopyXFromTToV() { v = (ushort)((v & 0xFBE0) | (t & 0x041F)); }

    private void InitializeTestFrameBuffer()
    {
        EnsureFrameBuffer();
        for (int y = 0; y < ScreenHeight; y++)
        {
            for (int x = 0; x < ScreenWidth; x++)
            {
                int index = (y * ScreenWidth + x) * 4; byte r, g, b;
                if (y < 60) { int paletteIndex = (x / 4) % 64; int p = (paletteIndex & 0x3F) * 3; r = PaletteBytes[p]; g = PaletteBytes[p + 1]; b = PaletteBytes[p + 2]; }
                else if (y < 120) { int barHeight = 10; int barIndex = (y - 60) / barHeight; int barPos = (y - 60) % barHeight; switch (barIndex % 6) { case 0: r = (byte)(x * 255 / ScreenWidth); g = (byte)(barPos * 255 / barHeight); b = 0; break; case 1: r = 0; g = (byte)(x * 255 / ScreenWidth); b = (byte)(barPos * 255 / barHeight); break; case 2: r = (byte)(barPos * 255 / barHeight); g = 0; b = (byte)(x * 255 / ScreenWidth); break; case 3: r = 0; g = (byte)(x * 255 / ScreenWidth); b = (byte)(x * 255 / ScreenWidth); break; case 4: r = (byte)(x * 255 / ScreenWidth); g = 0; b = (byte)(x * 255 / ScreenWidth); break; case 5: r = (byte)(x * 255 / ScreenWidth); g = (byte)(x * 255 / ScreenWidth); b = 0; break; default: r = g = b = 128; break; } }
                else if (y < 180) { int strip = (x / 32) % 8; int nesColorIndex = strip * 8 + ((y - 120) / 8); if (nesColorIndex < 64) { int p2 = (nesColorIndex & 0x3F) * 3; r = PaletteBytes[p2]; g = PaletteBytes[p2 + 1]; b = PaletteBytes[p2 + 2]; } else r = g = b = 64; }
                else { bool checker = ((x / 8) + (y / 8)) % 2 == 0; if (checker) { double centerX = ScreenWidth / 2.0; double centerY = 200; double distance = System.Math.Sqrt((x - centerX) * (x - centerX) + (y - centerY) * (y - centerY)); int colorIndex = ((int)distance / 4) % 64; int p3 = (colorIndex & 0x3F) * 3; r = PaletteBytes[p3]; g = PaletteBytes[p3 + 1]; b = PaletteBytes[p3 + 2]; } else { r = (byte)(128 + System.Math.Sin(x * 0.1) * 127); g = (byte)(128 + System.Math.Sin(y * 0.1) * 127); b = (byte)(128 + System.Math.Sin((x + y) * 0.05) * 127); } }
                frameBuffer![index] = r; frameBuffer![index + 1] = g; frameBuffer![index + 2] = b; frameBuffer![index + 3] = 255;
            }
            ApplyLowQualityScanline(y); // degrade pattern too
        }
    }
    // Pre-scan OAM to estimate sprite cost and configure background fetch budget & stall
    private void PrepareScanlineBandwidth(int y)
    {
        int spriteCount = 0;
        bool isSprite8x16 = (PPUCTRL & 0x20) != 0;
        for (int i = 0; i < 64; i++)
        {
            int offset = i * 4; byte spriteY = oam[offset]; int tileHeight = isSprite8x16 ? 16 : 8; if (y >= spriteY && y < spriteY + tileHeight) spriteCount++;
            if (spriteCount >= 16) break; // don't need exact beyond this for budgeting
        }
        int penalty = spriteCount * SpritePenaltyPerSprite;
        currentBgFetchBudget = BaseBackgroundFetchBudget - penalty;
        if (currentBgFetchBudget < MinBackgroundFetchBudget) currentBgFetchBudget = MinBackgroundFetchBudget;
        duplicateScanlineFromPrevious = rand.NextDouble() < DramRefreshSkipProbability;
    }

    private void InitializeDefaultPalette()
    {
        paletteRAM[0x00] = 0x0F; paletteRAM[0x01] = 0x00; paletteRAM[0x02] = 0x10; paletteRAM[0x03] = 0x30;
        paletteRAM[0x04] = 0x0F; paletteRAM[0x05] = 0x06; paletteRAM[0x06] = 0x16; paletteRAM[0x07] = 0x26;
        paletteRAM[0x08] = 0x0F; paletteRAM[0x09] = 0x0A; paletteRAM[0x0A] = 0x1A; paletteRAM[0x0B] = 0x2A;
        paletteRAM[0x0C] = 0x0F; paletteRAM[0x0D] = 0x02; paletteRAM[0x0E] = 0x12; paletteRAM[0x0F] = 0x22;
        paletteRAM[0x10] = 0x0F; paletteRAM[0x11] = 0x14; paletteRAM[0x12] = 0x24; paletteRAM[0x13] = 0x34;
        paletteRAM[0x14] = 0x0F; paletteRAM[0x15] = 0x07; paletteRAM[0x16] = 0x17; paletteRAM[0x17] = 0x27;
        paletteRAM[0x18] = 0x0F; paletteRAM[0x19] = 0x13; paletteRAM[0x1A] = 0x23; paletteRAM[0x1B] = 0x33;
        paletteRAM[0x1C] = 0x0F; paletteRAM[0x1D] = 0x15; paletteRAM[0x1E] = 0x25; paletteRAM[0x1F] = 0x35;
    }

    // NES 64 color palette
    static readonly byte[] PaletteBytes = new byte[] {
        84,84,84, 0,30,116, 8,16,144, 48,0,136,
        68,0,100, 92,0,48, 84,4,0, 60,24,0,
        32,42,0, 8,58,0, 0,64,0, 0,60,0,
        0,50,60, 0,0,0, 0,0,0, 0,0,0,
        152,150,152, 8,76,196, 48,50,236, 92,30,228,
        136,20,176, 160,20,100, 152,34,32, 120,60,0,
        84,90,0, 40,114,0, 8,124,0, 0,118,40,
        0,102,120, 0,0,0, 0,0,0, 0,0,0,
        236,238,236, 76,154,236, 120,124,236, 176,98,236,
        228,84,236, 236,88,180, 236,106,100, 212,136,32,
        160,170,0, 116,196,0, 76,208,32, 56,204,108,
        56,180,204, 60,60,60, 0,0,0, 0,0,0,
        236,238,236, 168,204,236, 188,188,236, 212,178,236,
        236,174,236, 236,174,212, 236,180,176, 228,196,144,
        204,210,120, 180,222,120, 168,226,144, 152,226,180,
        160,214,228, 160,162,160, 0,0,0, 0,0,0
    };

    public object GetState() => new PpuSharedState { vram=(byte[])vram.Clone(), palette=(byte[])paletteRAM.Clone(), oam=(byte[])oam.Clone(), /* frame omitted */ PPUCTRL=PPUCTRL,PPUMASK=PPUMASK,PPUSTATUS=PPUSTATUS,OAMADDR=OAMADDR,PPUSCROLLX=PPUSCROLLX,PPUSCROLLY=PPUSCROLLY,PPUDATA=PPUDATA,PPUADDR=PPUADDR,fineX=fineX,scrollLatch=scrollLatch,addrLatch=addrLatch,v=v,t=t,scanline=scanline,scanlineCycle=scanlineCycle, ppuDataBuffer=ppuDataBuffer, staticFrameCounter=staticFrameCounter };
    public void SetState(object state)
    {
        if (state is PpuSharedState s)
        {
            vram=(byte[])s.vram.Clone(); paletteRAM=(byte[])s.palette.Clone(); oam=(byte[])s.oam.Clone(); if (s.frame != null && s.frame.Length==ScreenWidth*ScreenHeight*4) { EnsureFrameBuffer(); frameBuffer=(byte[])s.frame.Clone(); } PPUCTRL=s.PPUCTRL;PPUMASK=s.PPUMASK;PPUSTATUS=s.PPUSTATUS;OAMADDR=s.OAMADDR;PPUSCROLLX=s.PPUSCROLLX;PPUSCROLLY=s.PPUSCROLLY;PPUDATA=s.PPUDATA;PPUADDR=s.PPUADDR;fineX=s.fineX;scrollLatch=s.scrollLatch;addrLatch=s.addrLatch;v=s.v;t=s.t;scanline=s.scanline;scanlineCycle=s.scanlineCycle;ppuDataBuffer=s.ppuDataBuffer; staticFrameCounter=s.staticFrameCounter; return;
        }
        if (state is System.Text.Json.JsonElement je)
        {
            if (je.TryGetProperty("vram", out var pVram) && pVram.ValueKind==System.Text.Json.JsonValueKind.Array){ int i=0; foreach(var el in pVram.EnumerateArray()){ if(i>=vram.Length) break; vram[i++]=(byte)el.GetInt32(); } }
            if (je.TryGetProperty("palette", out var pPal) && pPal.ValueKind==System.Text.Json.JsonValueKind.Array){ int i=0; foreach(var el in pPal.EnumerateArray()){ if(i>=paletteRAM.Length) break; paletteRAM[i++]=(byte)el.GetInt32(); } }
            if (je.TryGetProperty("oam", out var pOam) && pOam.ValueKind==System.Text.Json.JsonValueKind.Array){ int i=0; foreach(var el in pOam.EnumerateArray()){ if(i>=oam.Length) break; oam[i++]=(byte)el.GetInt32(); } }
            if (je.TryGetProperty("frame", out var pFrame) && pFrame.ValueKind==System.Text.Json.JsonValueKind.Array){ EnsureFrameBuffer(); int i=0; foreach(var el in pFrame.EnumerateArray()){ if(i>=frameBuffer!.Length) break; frameBuffer![i++]=(byte)el.GetInt32(); } }
            byte GetB(string name)=>je.TryGetProperty(name,out var p)?(byte)p.GetInt32():(byte)0; ushort GetU16(string name)=>je.TryGetProperty(name,out var p)?(ushort)p.GetInt32():(ushort)0;
            PPUCTRL=GetB("PPUCTRL");PPUMASK=GetB("PPUMASK");PPUSTATUS=GetB("PPUSTATUS");OAMADDR=GetB("OAMADDR");PPUSCROLLX=GetB("PPUSCROLLX");PPUSCROLLY=GetB("PPUSCROLLY");PPUDATA=GetB("PPUDATA");PPUADDR=GetU16("PPUADDR");fineX=GetB("fineX");scrollLatch=je.TryGetProperty("scrollLatch", out var psl)&&psl.GetBoolean();addrLatch=je.TryGetProperty("addrLatch", out var pal)&&pal.GetBoolean();v=GetU16("v");t=GetU16("t"); if(je.TryGetProperty("scanline", out var psl2)) scanline=psl2.GetInt32(); if(je.TryGetProperty("scanlineCycle", out var psc)) scanlineCycle=psc.GetInt32(); if(je.TryGetProperty("ppuDataBuffer", out var pdb)) ppuDataBuffer=(byte)pdb.GetInt32();
        }
    }
}
}
