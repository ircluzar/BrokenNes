using System;

namespace NesEmulator.Snes;

/// <summary>
/// SNES PPU (S-PPU1 + S-PPU2) - SFC family.
///
/// PHASE 1: the complete CPU-facing register file (VRAM/CGRAM/OAM ports with their latches, address
/// remapping and read prefetch, scroll write-twice latches, the Mode 7 multiplier, H/V counter
/// latching) plus a deliberately small whole-frame renderer: backgrounds in modes 0 and 1, 8x8
/// tiles, no sprites, no priority bits, no windows or color math. That is enough to show a test
/// ROM's text screen; everything the renderer skips is a known gap, not a bug.
///
/// VRAM is stored as 32K 16-bit words because that is how the chip addresses it.
/// </summary>
public sealed class PPU_SFC
{
    public string CoreName => "SFC";
    public string Description => "SNES PPU - phase 1 (full register ports, basic BG renderer)";
    public string Category => "Accuracy";

    public const int Width = 256, Height = 224;

    public readonly ushort[] Vram = new ushort[0x8000];
    public readonly ushort[] Cgram = new ushort[256];
    public readonly byte[] Oam = new byte[544];
    public readonly uint[] FrameBuffer = new uint[Width * Height];   // 0xAARRGGBB

    private byte inidisp = 0x80, obsel, bgmode, mosaic, bg12nba, bg34nba;
    private readonly byte[] bgsc = new byte[4];
    private readonly ushort[] hofs = new ushort[4], vofs = new ushort[4];
    private byte scrollLatch, hofsLatch;

    private byte vmain;
    private ushort vmadd, vramPrefetch;

    private byte cgadd, cgLatch;
    private bool cgHigh;

    private ushort oamadd, oamInternal;
    private byte oamLatch;

    private byte m7sel, m7Latch;
    private short m7a, m7b, m7c, m7d, m7x, m7y, m7hofs, m7vofs;

    private byte tm, ts, w12sel, w34sel, wobjsel, wh0, wh1, wh2, wh3, wbglog, wobjlog, tmw, tsw, cgwsel, cgadsub, setini;
    private ushort coldata;

    private ushort latchedH, latchedV;
    private bool counterLatched, ophctHigh, opvctHigh;
    private byte ppu1Mdr, ppu2Mdr;

    /// <summary>Supplied by the board: current (dot, scanline), used when $2137 latches the counters.</summary>
    public Func<(int dot, int line)>? CounterSource;

    public bool ForcedBlank => (inidisp & 0x80) != 0;

    public void Reset()
    {
        inidisp = 0x80;
        vmain = 0; vmadd = 0; cgadd = 0; cgHigh = false; oamadd = 0; oamInternal = 0;
        counterLatched = ophctHigh = opvctHigh = false;
    }

    // =====================================================================================
    //  VRAM port helpers
    // =====================================================================================

    private ushort VramAddress()
    {
        int a = vmadd;
        switch ((vmain >> 2) & 3)
        {
            case 1: a = (a & 0xFF00) | ((a & 0x001F) << 3) | ((a >> 5) & 7); break;
            case 2: a = (a & 0xFE00) | ((a & 0x003F) << 3) | ((a >> 6) & 7); break;
            case 3: a = (a & 0xFC00) | ((a & 0x007F) << 3) | ((a >> 7) & 7); break;
        }
        return (ushort)(a & 0x7FFF);
    }

    private void VramIncrement()
    {
        vmadd += (vmain & 3) switch { 0 => 1, 1 => 32, _ => 128 };
    }

    public ushort ReadVramWord(int wordAddress) => Vram[wordAddress & 0x7FFF];

    // =====================================================================================
    //  Registers ($2100-$213F; reg = low byte of the address)
    // =====================================================================================

    public void WriteRegister(byte reg, byte v)
    {
        switch (reg)
        {
            case 0x00: inidisp = v; break;
            case 0x01: obsel = v; break;
            case 0x02: oamadd = (ushort)((oamadd & 0x100) | v); oamInternal = (ushort)((oamadd & 0x1FF) << 1); break;
            case 0x03: oamadd = (ushort)((oamadd & 0xFF) | (v & 1) << 8 | (v & 0x80) << 8); oamInternal = (ushort)((oamadd & 0x1FF) << 1); break;
            case 0x04: WriteOam(v); break;
            case 0x05: bgmode = v; break;
            case 0x06: mosaic = v; break;
            case 0x07: case 0x08: case 0x09: case 0x0A: bgsc[reg - 0x07] = v; break;
            case 0x0B: bg12nba = v; break;
            case 0x0C: bg34nba = v; break;
            case 0x0D:   // BG1HOFS doubles as M7HOFS (13-bit signed, its own latch)
                m7hofs = (short)(((v << 8) | m7Latch) << 19 >> 19); m7Latch = v;
                goto case 0x0F;
            case 0x0F: case 0x11: case 0x13:
            {
                int bg = (reg - 0x0D) >> 1;
                hofs[bg] = (ushort)(((v << 8) | (scrollLatch & ~7) | (hofsLatch & 7)) & 0x3FF);
                scrollLatch = v; hofsLatch = v;
                break;
            }
            case 0x0E:   // BG1VOFS doubles as M7VOFS
                m7vofs = (short)(((v << 8) | m7Latch) << 19 >> 19); m7Latch = v;
                goto case 0x10;
            case 0x10: case 0x12: case 0x14:
            {
                int bg = (reg - 0x0E) >> 1;
                vofs[bg] = (ushort)(((v << 8) | scrollLatch) & 0x3FF);
                scrollLatch = v;
                break;
            }
            case 0x15: vmain = v; break;
            case 0x16: vmadd = (ushort)((vmadd & 0xFF00) | v); vramPrefetch = Vram[VramAddress()]; break;
            case 0x17: vmadd = (ushort)((vmadd & 0x00FF) | v << 8); vramPrefetch = Vram[VramAddress()]; break;
            case 0x18:
            {
                // Hardware drops VRAM writes during active display; phase 1 accepts them always.
                int a = VramAddress();
                Vram[a] = (ushort)((Vram[a] & 0xFF00) | v);
                if ((vmain & 0x80) == 0) VramIncrement();
                break;
            }
            case 0x19:
            {
                int a = VramAddress();
                Vram[a] = (ushort)((Vram[a] & 0x00FF) | v << 8);
                if ((vmain & 0x80) != 0) VramIncrement();
                break;
            }
            case 0x1A: m7sel = v; break;
            case 0x1B: m7a = (short)(v << 8 | m7Latch); m7Latch = v; break;
            case 0x1C: m7b = (short)(v << 8 | m7Latch); m7Latch = v; break;
            case 0x1D: m7c = (short)(v << 8 | m7Latch); m7Latch = v; break;
            case 0x1E: m7d = (short)(v << 8 | m7Latch); m7Latch = v; break;
            case 0x1F: m7x = (short)(((v << 8) | m7Latch) << 19 >> 19); m7Latch = v; break;
            case 0x20: m7y = (short)(((v << 8) | m7Latch) << 19 >> 19); m7Latch = v; break;
            case 0x21: cgadd = v; cgHigh = false; break;
            case 0x22:
                if (!cgHigh) cgLatch = v;
                else { Cgram[cgadd] = (ushort)(((v & 0x7F) << 8) | cgLatch); cgadd++; }
                cgHigh = !cgHigh;
                break;
            case 0x23: w12sel = v; break;
            case 0x24: w34sel = v; break;
            case 0x25: wobjsel = v; break;
            case 0x26: wh0 = v; break;
            case 0x27: wh1 = v; break;
            case 0x28: wh2 = v; break;
            case 0x29: wh3 = v; break;
            case 0x2A: wbglog = v; break;
            case 0x2B: wobjlog = v; break;
            case 0x2C: tm = v; break;
            case 0x2D: ts = v; break;
            case 0x2E: tmw = v; break;
            case 0x2F: tsw = v; break;
            case 0x30: cgwsel = v; break;
            case 0x31: cgadsub = v; break;
            case 0x32:
                if ((v & 0x20) != 0) coldata = (ushort)((coldata & ~0x001F) | (v & 0x1F));
                if ((v & 0x40) != 0) coldata = (ushort)((coldata & ~0x03E0) | (v & 0x1F) << 5);
                if ((v & 0x80) != 0) coldata = (ushort)((coldata & ~0x7C00) | (v & 0x1F) << 10);
                break;
            case 0x33: setini = v; break;
        }
    }

    /// <summary>Returns -1 for write-only registers (the board substitutes CPU open bus).</summary>
    public int ReadRegister(byte reg)
    {
        switch (reg)
        {
            case 0x34: case 0x35: case 0x36:
            {
                int product = m7a * (sbyte)(m7b >> 8);
                return ppu1Mdr = (byte)(product >> ((reg - 0x34) * 8));
            }
            case 0x37:
                if (CounterSource != null)
                {
                    var (dot, line) = CounterSource();
                    latchedH = (ushort)dot; latchedV = (ushort)line; counterLatched = true;
                }
                return -1;
            case 0x38:
            {
                byte v = oamInternal < 0x200 ? Oam[oamInternal] : Oam[0x200 + (oamInternal & 0x1F)];
                oamInternal = (ushort)((oamInternal + 1) & 0x3FF);
                return ppu1Mdr = v;
            }
            case 0x39:
            {
                byte v = (byte)vramPrefetch;
                if ((vmain & 0x80) == 0) { vramPrefetch = Vram[VramAddress()]; VramIncrement(); }
                return ppu1Mdr = v;
            }
            case 0x3A:
            {
                byte v = (byte)(vramPrefetch >> 8);
                if ((vmain & 0x80) != 0) { vramPrefetch = Vram[VramAddress()]; VramIncrement(); }
                return ppu1Mdr = v;
            }
            case 0x3B:
            {
                byte v;
                if (!cgHigh) v = (byte)Cgram[cgadd];
                else { v = (byte)((Cgram[cgadd] >> 8 & 0x7F) | (ppu2Mdr & 0x80)); cgadd++; }
                cgHigh = !cgHigh;
                return ppu2Mdr = v;
            }
            case 0x3C:
            {
                byte v = ophctHigh ? (byte)((latchedH >> 8 & 1) | (ppu2Mdr & 0xFE)) : (byte)latchedH;
                ophctHigh = !ophctHigh;
                return ppu2Mdr = v;
            }
            case 0x3D:
            {
                byte v = opvctHigh ? (byte)((latchedV >> 8 & 1) | (ppu2Mdr & 0xFE)) : (byte)latchedV;
                opvctHigh = !opvctHigh;
                return ppu2Mdr = v;
            }
            case 0x3E: return ppu1Mdr = (byte)(0x01 | (ppu1Mdr & 0x10));
            case 0x3F:
            {
                byte v = (byte)(0x03 | (counterLatched ? 0x40 : 0) | (ppu2Mdr & 0x20));
                counterLatched = false; ophctHigh = opvctHigh = false;
                return ppu2Mdr = v;
            }
        }
        return -1;
    }

    private void WriteOam(byte v)
    {
        if (oamInternal < 0x200)
        {
            if ((oamInternal & 1) == 0) oamLatch = v;
            else { Oam[oamInternal - 1] = oamLatch; Oam[oamInternal] = v; }
        }
        else Oam[0x200 + (oamInternal & 0x1F)] = v;
        oamInternal = (ushort)((oamInternal + 1) & 0x3FF);
    }

    /// <summary>Called by the board at the start of vblank, after the SNES frame's active lines.</summary>
    public void OnVBlankStart()
    {
        // OAM address reloads at vblank start when rendering is on.
        if (!ForcedBlank) oamInternal = (ushort)((oamadd & 0x1FF) << 1);
        RenderFrame();
    }

    // =====================================================================================
    //  Phase-1 renderer: BGs in modes 0/1, back-to-front, no priority bits, no sprites.
    // =====================================================================================

    private static readonly int[][] ModeBpp =
    {
        new[] { 2, 2, 2, 2 },  // mode 0
        new[] { 4, 4, 2, 0 },  // mode 1
    };

    private void RenderFrame()
    {
        int brightness = inidisp & 0x0F;
        if (ForcedBlank || brightness == 0) { Array.Fill(FrameBuffer, 0xFF000000u); return; }

        int mode = bgmode & 7;
        var bpps = mode < ModeBpp.Length ? ModeBpp[mode] : ModeBpp[1];
        uint backdrop = ToArgb(Cgram[0], brightness);
        Span<ushort> line = stackalloc ushort[Width];

        for (int y = 0; y < Height; y++)
        {
            line.Fill(0xFFFF);  // 0xFFFF = transparent (a real color never sets bit 15)
            for (int bg = 3; bg >= 0; bg--)
            {
                if ((tm & (1 << bg)) == 0 || bpps[bg] == 0) continue;
                DrawBgLine(bg, bpps[bg], mode == 0 ? bg * 32 : 0, y, line);
            }
            int row = y * Width;
            for (int x = 0; x < Width; x++)
                FrameBuffer[row + x] = line[x] == 0xFFFF ? backdrop : ToArgb(line[x], brightness);
        }
    }

    private void DrawBgLine(int bg, int bpp, int paletteBase, int screenY, Span<ushort> line)
    {
        int sc = bgsc[bg];
        int mapBase = (sc & 0xFC) << 8;
        bool wide = (sc & 1) != 0, tall = (sc & 2) != 0;
        int charBase = (bg < 2 ? (bg12nba >> (bg * 4)) : (bg34nba >> ((bg - 2) * 4))) & 0x0F;
        charBase <<= 12;

        int bgY = (screenY + 1 + vofs[bg]) & (tall ? 0x1FF : 0xFF);   // line 1 is the first visible line
        int tileRow = bgY >> 3, fineY = bgY & 7;
        int wordsPerTile = bpp * 4;

        for (int x = 0; x < Width; x++)
        {
            int bgX = (x + hofs[bg]) & (wide ? 0x1FF : 0xFF);
            int tileCol = bgX >> 3;
            int screenOffset = ((tileCol & 32) != 0 ? 0x400 : 0) + ((tileRow & 32) != 0 ? (wide ? 0x800 : 0x400) : 0);
            ushort entry = Vram[(mapBase + screenOffset + ((tileRow & 31) << 5) + (tileCol & 31)) & 0x7FFF];

            int tile = entry & 0x3FF, pal = (entry >> 10) & 7;
            int px = bgX & 7, py = fineY;
            if ((entry & 0x4000) != 0) px = 7 - px;
            if ((entry & 0x8000) != 0) py = 7 - py;

            int tileAddr = charBase + tile * wordsPerTile + py;
            int bit = 7 - px, color = 0;
            for (int plane = 0; plane < bpp; plane += 2)
            {
                ushort w = Vram[(tileAddr + plane * 4) & 0x7FFF];
                color |= ((w >> bit) & 1) << plane;
                color |= ((w >> (8 + bit)) & 1) << (plane + 1);
            }
            if (color == 0) continue;
            line[x] = Cgram[(paletteBase + pal * (1 << bpp) + color) & 0xFF];
        }
    }

    private static uint ToArgb(ushort bgr555, int brightness)
    {
        int r = bgr555 & 0x1F, g = (bgr555 >> 5) & 0x1F, b = (bgr555 >> 10) & 0x1F;
        r = r * brightness * 255 / (31 * 15);
        g = g * brightness * 255 / (31 * 15);
        b = b * brightness * 255 / (31 * 15);
        return 0xFF000000u | (uint)(r << 16 | g << 8 | b);
    }
}
