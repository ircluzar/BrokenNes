// TileHostPpu - the shared frame of the PocketNES-family picture cores (PPU_NSDS = nesDS, PPU_PNES = PocketNES).
//
// Both emulators run the NES on a handheld's tile hardware (Nintendo DS / Game Boy Advance) instead of drawing pixels:
// the NES registers are turned into per-scanline buffers (scroll, BG control, display control) that the host's
// HBlank DMA replays, the nametables become a host tile map, the 64 NES sprites become host OBJs once a frame, and
// the palette is copied into the host palette once a frame. The host screen is smaller than the NES picture, so the
// 240 NES lines are squeezed onto it. This base reproduces that pipeline in software: the NES side (registers, line
// buffers, frame snapshots) is common; each core composes the host screen its own way (Compose) and the result is
// letterboxed into the 256x240 BrokenNes frame, black outside the host screen, as the handheld shows it.
//
// The register front end follows nesDS RP2C02.s (itself grown from PocketNES ppu.s): a 9-bit scrollX / scrollY pair
// instead of loopy's v/t for the picture (scrollX from $2005 #1 + $2000 bit 0, or from a $2006 pair; scrollY from a
// $2006 pair at once, from $2005 #2 + $2000 bit 1 only at the next frame), the 240-line nametables stacked in 256-line
// host map slots (scrollY skips 240..255), the line counter of ppuDoScanline (newframe at line 0, ppusync at the start
// of lines 0-239, VBlank at 241, status cleared at 261) and the "every other frame has 1 less PPU cycle" rule.
// See each core's header for its sources, authors and the jank it keeps.
//
// Board adaptation (no jank of its own): pattern reads through the cartridge, nametables through its mirroring or
// mapper hook (as the other in-house cores do); MMC3 counter at dot 260, MMC5 line hook at dot 3 (as PPU_NOFR).
using System;
using System.IO;

namespace NesEmulator
{
public abstract class TileHostPpu : IPPU, IPpuFrameClock
{
    public abstract string CoreName { get; }
    public abstract string Description { get; }
    public int Performance => 9;
    public int Rating => 2;
    public string Category => "Harvest";

    protected const int W = 256, H = 240;

    protected readonly Bus bus;
    protected byte[] vram = new byte[2048];
    protected byte[] oam = new byte[256];
    protected byte[] pal = new byte[32];            // paletteMem (NES colour numbers)
    byte[]? frameBuffer;

    // registers (globals of RP2C02.s / ppu.s)
    protected byte ctrl0, ctrl1, stat, oamAdr, busLatch, readTemp, toggle, ctrl0Frame;
    protected int vramAddr, vramAddr2, vramInc = 1, scrollX, scrollY, scrollYTemp;
    protected int sprite0Y = 0x200, sprite0X;
    protected int scanline = 261, dot;
    protected long frame, dotCounter;

    // frame snapshots (host palette / OBJ table / OBJ tiles, refreshed at newframe)
    protected byte[] framePal = new byte[32];
    protected byte[] frameOam = new byte[256];
    protected byte frameSprCtrl;
    protected byte[] sprPat = new byte[64 * 32];    // per OBJ: 16 rows x (low, high), unflipped

    // per NES line: the host BG line (0 = transparent, else palette slot 1-15) and the display control of that line
    protected byte[] lineBg = new byte[H * W];
    protected byte[] lineCtrl1 = new byte[H];
    protected int[] lineScrollX = new int[H];

    protected TileHostPpu(Bus bus) { this.bus = bus; }

    public int ProbeScanline => scanline;

    void EnsureFrameBuffer() { if (frameBuffer == null || frameBuffer.Length != W * H * 4) frameBuffer = new byte[W * H * 4]; }
    protected byte[] Fb { get { EnsureFrameBuffer(); return frameBuffer!; } }

    protected bool Rendering => (ctrl1 & 0x18) != 0;

    public void Step(int cycles)
    {
        for (int c = 0; c < cycles; c++)
        {
            if (dot == 0)
            {
                if (scanline == 0)
                {
                    NewFrame();
                    if ((frame & 1) == 0) { dot++; dotCounter++; }   // "Every other frame has 1 less PPU cycle."
                }
                if (scanline < 240) PpuSync(scanline);
                else if (scanline == 240) Compose();
                else if (scanline == 241) { stat |= 0x80; frame++; }
                else if (scanline == 261) { sprite0Y = 0x200; stat = 0; }
                if (scanline == 119) ctrl0Frame = ctrl0;           // midFrame: "Contra likes this"
            }
            if (scanline == 241 && dot == 6 && (ctrl0 & 0x80) != 0 && (stat & 0x80) != 0) bus?.cpu?.RequestNMI();   // NMIDELAY*3
            if (scanline < 240)
            {
                if (dot == 3 && bus?.cartridge?.mapper is Mapper5 mmc5) { mmc5.PpuScanlineHook(scanline, Rendering); if (mmc5.IsIrqAsserted()) bus.cpu.RequestIRQ(true); }
                if (dot == 260 && Rendering && bus?.cartridge?.mapper is Mapper4 mmc3) { mmc3.RunScanlineIRQ(); if (mmc3.IRQPending()) { bus.cpu.RequestIRQ(true); mmc3.ClearIRQ(); } }
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

    /// <summary>newframe (line 0): scroll from the frame latches, host palette + OBJ table + OBJ tiles copied.</summary>
    protected virtual void NewFrame()
    {
        scrollY = scrollYTemp;
        Array.Copy(pal, framePal, 32);
        Array.Copy(oam, frameOam, 256);
        frameSprCtrl = ctrl0Frame;
        SnapshotSprites(frameSprCtrl);
    }

    protected void SnapshotSprites(byte sprCtrl)
    {
        bool tall = (sprCtrl & 0x20) != 0;
        for (int s = 0; s < 64; s++)
        {
            int tile = frameOam[s * 4 + 1];
            int baseAddr = tall ? ((tile & 1) << 12) | ((tile & 0xFE) << 4) : ((sprCtrl & 0x08) << 9) | (tile << 4);
            int rows = tall ? 16 : 8;
            for (int r = 0; r < rows; r++)
            {
                int a = baseAddr + (r & 7) + ((r & 8) << 1);
                sprPat[s * 32 + r * 2] = Mem(a);
                sprPat[s * 32 + r * 2 + 1] = Mem(a + 8);
            }
        }
    }

    /// <summary>ppusync: the start of NES line 0-239.</summary>
    protected virtual void PpuSync(int line)
    {
        lineCtrl1[line] = ctrl1;
        lineScrollX[line] = scrollX;
        RenderBgLine(line);
        AfterBgLine(line);
        // scrollY steps one map row a line; the 16 rows under each 240-line nametable are skipped
        int y = scrollY + 1;
        if ((y & 0xFF) == 0) y ^= 0x100;   // "Page wraps with negative scroll"
        if (y == 0xF0 || y == 0x1F0) y += 16;
        scrollY = y & 0x1FF;
        oamAdr = 0;
    }

    protected virtual void AfterBgLine(int line) { }

    /// <summary>The host BG layer of one line: a 512x512 map of the four nametables, rows 240-255 of each slot blank (tile 0, palette 0).</summary>
    protected void RenderBgLine(int line)
    {
        var dst = lineBg.AsSpan(line * W, W);
        if ((ctrl1 & 0x08) == 0) { dst.Clear(); return; }
        int my = scrollY & 0x1FF, row = my & 0xFF, ntRow = my >> 8;
        int bgBase = (ctrl0 & 0x10) << 8;
        int x = 0, mx = scrollX & 0x1FF;
        while (x < W)
        {
            int ntCol = (mx >> 8) & 1, col = (mx & 0xFF) >> 3;
            int ntAddr = 0x2000 + ((ntRow << 1) | ntCol) * 0x400;
            int tile = 0, palHi = 0;
            if (row < 240)
            {
                tile = Mem(ntAddr + (row >> 3) * 32 + col);
                int at = Mem(ntAddr + 0x3C0 + (row >> 5) * 8 + (col >> 2));
                palHi = ((at >> (((row >> 2) & 4) | (col & 2))) & 3) << 2;
            }
            int pa = bgBase + (tile << 4) + (row & 7);
            byte lo = Mem(pa), hi = Mem(pa + 8);
            for (int px = mx & 7; px < 8 && x < W; px++, x++, mx = (mx + 1) & 0x1FF)
            {
                int cbit = 7 - px;
                int cc = ((lo >> cbit) & 1) | (((hi >> cbit) & 1) << 1);
                dst[x] = (byte)(cc == 0 ? 0 : palHi | cc);
            }
        }
    }

    /// <summary>One sprite row (0-15) of a snapshotted OBJ as 8 colour codes (0 = transparent), with the OBJ's flips.</summary>
    protected void SpriteRow(int s, int r, bool tall, Span<byte> outc)
    {
        int attr = frameOam[s * 4 + 2];
        int h = tall ? 16 : 8;
        if ((attr & 0x80) != 0) r = h - 1 - r;
        byte lo = sprPat[s * 32 + r * 2], hi = sprPat[s * 32 + r * 2 + 1];
        bool hf = (attr & 0x40) != 0;
        for (int i = 0; i < 8; i++)
        {
            int b = hf ? i : 7 - i;
            outc[i] = (byte)(((lo >> b) & 1) | (((hi >> b) & 1) << 1));
        }
    }

    /// <summary>The frame's host screen into the BrokenNes frame (line 240).</summary>
    protected abstract void Compose();

    // Loopy's palette (both emulators' default table), through the host's 15-bit colour (gamma 0: v >> 3).
    protected static void PutColour(byte[] fb, int o, byte[] rgb, int nesColour)
    {
        int p = (nesColour & 0x3F) * 3;
        for (int k = 0; k < 3; k++) { int v = rgb[p + k] >> 3; fb[o + k] = (byte)((v << 3) | (v >> 2)); }
        fb[o + 3] = 255;
    }

    protected static void Black(byte[] fb, int o) { fb[o] = fb[o + 1] = fb[o + 2] = 0; fb[o + 3] = 255; }

    // ---- PPU memory through the board ----
    protected byte Mem(int address)
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
        if (address >= 0x3000) address -= 0x1000;
        if (address < 0x2000) return bus?.cartridge?.PPURead((ushort)address) ?? 0;
        if (bus?.cartridge?.mapper is IMapper m && m.TryPpuNametableRead((ushort)address, out byte v)) return v;
        return vram[MirrorNt(address)];
    }

    protected void MemWrite(int address, byte value)
    {
        address &= 0x3FFF;
        if (address >= 0x3000 && address < 0x3F00) address -= 0x1000;
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

    // ---- registers ----
    public byte ReadPPURegister(ushort address)
    {
        switch (address & 7)
        {
            case 2: return StatRead();
            case 4: return OamDataRead();
            case 7: return VmdataRead();
            default: return busLatch;   // empty_PPU_R
        }
    }

    protected abstract bool Sprite0HitNow();

    byte StatRead()
    {
        toggle = 0;
        byte s = stat;
        if (Sprite0HitNow()) s |= 0x40;
        stat = (byte)(s & 0x7F);
        return (byte)(s | (busLatch & 0x1F));
    }

    protected virtual byte OamDataRead() { byte v = oam[oamAdr]; busLatch = v; return v; }

    byte VmdataRead()
    {
        int a = vramAddr & 0x3FFF;
        vramAddr = a + vramInc;
        byte under = Mem(a);
        if (a >= 0x3F00)
        {
            readTemp = under;
            byte v = (byte)(pal[a & 0x1F] | (busLatch & 0xC0));
            busLatch = v;
            return v;
        }
        byte old = readTemp;
        readTemp = under;
        busLatch = old;
        return old;
    }

    public void WritePPURegister(ushort address, byte value)
    {
        busLatch = value;
        switch (address & 7)
        {
            case 0:
            {
                vramInc = (value & 4) != 0 ? 32 : 1;
                scrollYTemp = (scrollYTemp & 0xFF) | ((value >> 1) & 1) << 8;
                scrollX = (scrollX & 0xFF) | (value & 1) << 8;
                byte old = ctrl0; ctrl0 = value;
                if (((old ^ value) & 0x80) != 0 && (value & 0x80) != 0 && (stat & 0x80) != 0) bus?.cpu?.RequestNMI();   // updateINTPin
                break;
            }
            case 1: ctrl1 = value; break;
            case 3: oamAdr = value; break;
            case 4: OamDataWrite(value); break;
            case 5:
                toggle ^= 1;
                if (toggle != 0) scrollX = (scrollX & 0x100) | value;
                else
                {
                    scrollYTemp = (scrollYTemp & 0x100) | value;
                    int r = (value >> 3) | ((value & 7) << 5);   // ror#3 folded: coarse Y in bits 0-4, fine Y in 5-7
                    vramAddr2 = (vramAddr2 & ~0x73E0) | ((r & 0x1F) << 5) | ((r >> 5) << 12);
                }
                break;
            case 6:
                toggle ^= 1;
                if (toggle != 0) vramAddr2 = (vramAddr2 & 0xFF) | (value & 0x3F) << 8;
                else
                {
                    vramAddr2 = (vramAddr2 & 0x7F00) | value;
                    vramAddr = vramAddr2;
                    int v = vramAddr2;
                    int sy = ((v & 0x7000) >> 12) | ((v & 0x03E0) >> 2) | ((v & 0x0800) >> 3);
                    scrollY = scrollYTemp = sy;
                    scrollX = (scrollX & 7) | ((v & 0x1F) << 3) | ((v & 0x400) >> 2);
                }
                break;
            case 7: VmdataWrite(value); break;
        }
    }

    protected virtual void OamDataWrite(byte value)
    {
        if ((oamAdr & 3) == 2) value &= 0xE3;   // "Only when writing attribute (2)."
        oam[oamAdr] = bus?.Butcher is { SpritesActive: true } sb ? sb.FilterOamWrite(oamAdr, value) : value;
        oamAdr++;
    }

    void VmdataWrite(byte value)
    {
        int a = vramAddr & 0x3FFF;
        vramAddr = a + vramInc;
        if (a < 0x3F00) { MemWrite(a, value); return; }
        PaletteWrite(a & 0x1F, (byte)(value & 0x3F));
    }

    protected virtual void PaletteWrite(int a, byte v)
    {
        if ((a & 3) == 0) a &= ~0x10;
        pal[a] = v;
        if ((a & 3) == 0) pal[a | 0x10] = v;
    }

    public virtual void WriteOAMDMA(byte page)
    {
        bus.FastOamDma(page, oam, ref oamAdr);
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

    // ---- save states: the shared fields carry the memories, everything else rides in fixPipeline ----
    const int StateTag = 0x54485031;   // "THP1"

    protected virtual void SaveExtra(BinaryWriter w) { }
    protected virtual void LoadExtra(BinaryReader r) { }

    public object GetState()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms))
        {
            w.Write(StateTag); w.Write(GetType().Name);
            w.Write(ctrl0); w.Write(ctrl1); w.Write(stat); w.Write(oamAdr); w.Write(busLatch); w.Write(readTemp); w.Write(toggle); w.Write(ctrl0Frame);
            w.Write(vramAddr); w.Write(vramAddr2); w.Write(vramInc); w.Write(scrollX); w.Write(scrollY); w.Write(scrollYTemp);
            w.Write(sprite0Y); w.Write(sprite0X); w.Write(scanline); w.Write(dot); w.Write(frame);
            w.Write(framePal); w.Write(frameOam); w.Write(frameSprCtrl); w.Write(sprPat); w.Write(lineBg); w.Write(lineCtrl1);
            foreach (var v in lineScrollX) w.Write(v);
            SaveExtra(w);
        }
        return new PpuSharedState
        {
            vram = (byte[])vram.Clone(), palette = (byte[])pal.Clone(), oam = (byte[])oam.Clone(),
            PPUCTRL = ctrl0, PPUMASK = ctrl1, PPUSTATUS = stat, OAMADDR = oamAdr, v = (ushort)vramAddr, t = (ushort)vramAddr2, fineX = (byte)(scrollX & 7),
            addrLatch = toggle != 0, scanline = scanline, scanlineCycle = dot, ppuDataBuffer = readTemp, fixPipeline = ms.ToArray(),
        };
    }

    public void SetState(object state)
    {
        PpuSharedState? s = HarvestPpuState.From(state);
        if (s == null) return;
        if (s.vram?.Length == 2048) vram = (byte[])s.vram.Clone();
        if (s.palette?.Length == 32) pal = (byte[])s.palette.Clone();
        if (s.oam?.Length == 256) oam = (byte[])s.oam.Clone();
        scanline = s.scanline; dot = s.scanlineCycle;
        var p = s.fixPipeline;
        bool mine = false;
        if (p != null && p.Length > 8)
        {
            try
            {
                using var r = new BinaryReader(new MemoryStream(p));
                if (r.ReadInt32() == StateTag && r.ReadString() == GetType().Name)
                {
                    ctrl0 = r.ReadByte(); ctrl1 = r.ReadByte(); stat = r.ReadByte(); oamAdr = r.ReadByte(); busLatch = r.ReadByte(); readTemp = r.ReadByte(); toggle = r.ReadByte(); ctrl0Frame = r.ReadByte();
                    vramAddr = r.ReadInt32(); vramAddr2 = r.ReadInt32(); vramInc = r.ReadInt32(); scrollX = r.ReadInt32(); scrollY = r.ReadInt32(); scrollYTemp = r.ReadInt32();
                    sprite0Y = r.ReadInt32(); sprite0X = r.ReadInt32(); scanline = r.ReadInt32(); dot = r.ReadInt32(); frame = r.ReadInt64();
                    r.Read(framePal, 0, 32); r.Read(frameOam, 0, 256); frameSprCtrl = r.ReadByte(); r.Read(sprPat, 0, sprPat.Length); r.Read(lineBg, 0, lineBg.Length); r.Read(lineCtrl1, 0, H);
                    for (int i = 0; i < H; i++) lineScrollX[i] = r.ReadInt32();
                    LoadExtra(r);
                    mine = true;
                }
            }
            catch (EndOfStreamException) { mine = false; }
        }
        if (!mine)
        {
            // A state from another core: take its registers the way this core holds them.
            ctrl0 = s.PPUCTRL; ctrl1 = s.PPUMASK; stat = s.PPUSTATUS; oamAdr = s.OAMADDR; vramAddr = s.v; vramAddr2 = s.t;
            toggle = (byte)(s.addrLatch ? 1 : 0); readTemp = s.ppuDataBuffer; ctrl0Frame = ctrl0;
            vramInc = (ctrl0 & 4) != 0 ? 32 : 1;
            int t = s.t;
            scrollYTemp = ((t & 0x7000) >> 12) | ((t & 0x03E0) >> 2) | ((t & 0x0800) >> 3);
            scrollX = (s.fineX & 7) | ((t & 0x1F) << 3) | ((t & 0x400) >> 2);
            scrollY = scrollYTemp; sprite0Y = 0x200;
            for (int i = 0; i < 32; i++) pal[i] &= 0x3F;
            Array.Copy(pal, framePal, 32); Array.Copy(oam, frameOam, 256); frameSprCtrl = ctrl0; SnapshotSprites(ctrl0);
        }
    }

    protected static readonly byte[] LoopyPal = {
        117,117,117, 39,27,143, 0,0,171, 71,0,159, 143,0,119, 171,0,19, 167,0,0, 127,11,0,
        67,47,0, 0,71,0, 0,81,0, 0,63,23, 27,63,95, 0,0,0, 0,0,0, 0,0,0,
        188,188,188, 0,115,239, 35,59,239, 131,0,243, 191,0,191, 231,0,91, 219,43,0, 203,79,15,
        139,115,0, 0,151,0, 0,171,0, 0,147,59, 0,131,139, 0,0,0, 0,0,0, 0,0,0,
        255,255,255, 63,191,255, 95,151,255, 167,139,253, 247,123,255, 255,119,183, 255,119,99, 255,155,59,
        243,191,63, 131,211,19, 79,223,75, 88,248,152, 0,235,219, 0,0,0, 0,0,0, 0,0,0,
        255,255,255, 171,231,255, 199,215,255, 215,203,255, 255,199,255, 255,199,219, 255,191,179, 255,219,171,
        255,231,163, 227,255,163, 171,243,191, 179,255,207, 159,255,243, 209,209,209, 17,17,17, 17,17,17,
    };
}
}
