using System;
using System.IO;

namespace NesEmulator.Gb;

/// <summary>
/// Game Boy / Game Boy Color picture processor. Dot-timed mode sequence (OAM scan, drawing with a variable
/// length, HBlank, VBlank) with STAT interrupts, CPU access blocking and the LY=153 quirk; pixels are produced
/// one line at a time when drawing starts (the scroll, window and sprite state the hardware latches then).
/// Written from Pan Docs "Rendering", "LCD Status Registers" and "Palettes".
/// </summary>
public sealed class PPU_GB
{
    public const int Width = 160, Height = 144, DotsPerLine = 456, Lines = 154;

    public readonly GbModel Model;
    /// <summary>CGB hardware running a DMG game: DMG registers, colour from the compatibility palettes.</summary>
    public bool CompatMode { get; set; }
    private bool Cgb => Model == GbModel.Cgb && !CompatMode;

    public readonly byte[] Vram = new byte[0x4000];      // 2 banks on CGB
    public readonly byte[] Oam = new byte[0xA0];
    public readonly byte[] BgPalRam = new byte[64], ObjPalRam = new byte[64];
    /// <summary>0xFFRRGGBB, 160x144. Written line by line; complete at VBlank.</summary>
    public readonly uint[] FrameBuffer = new uint[Width * Height];
    /// <summary>Per pixel: DMG shade 0-3 after palette mapping (DMG), or the colour index 0-3 (CGB). Handy for bridges.</summary>
    public readonly byte[] ShadeBuffer = new byte[Width * Height];
    /// <summary>The four DMG shades as 0xFFRRGGBB (lightest first). Frontends can recolour.</summary>
    public uint[] DmgColors = { 0xFFFFFFFF, 0xFFAAAAAA, 0xFF555555, 0xFF000000 };

    public byte Lcdc = 0x91, Scy, Scx, Lyc, Bgp = 0xFC, Obp0 = 0xFF, Obp1 = 0xFF, Wy, Wx;
    private byte statEnable;          // STAT bits 3-6
    private byte ly;                  // internal line counter 0-153
    private int dot;                  // dot within the line
    private int mode;                 // 0 HBlank, 1 VBlank, 2 OAM scan, 3 drawing
    private int mode3End;             // dot where this line's drawing ends
    private bool statLine;            // OR of the enabled STAT conditions (IRQ on the rising edge)
    private bool lycEqual;
    private bool lyReadsZero;         // line 153 after its first dots reads LY=0
    private int windowLine;           // internal window line counter
    private bool windowYTriggered;    // WY matched LY at some point this frame
    private bool firstLineAfterOn;    // no OAM scan on the first line after the LCD is switched on
    private bool skipFrame;           // the first frame after switching on is not shown
    private int offDots;              // LCD off: keep delivering blank frames at the normal rate
    private byte vbk, bcps, ocps, opri;
    public long FrameCount { get; private set; }

    /// <summary>Raised with the IF bit to set: 0 = VBlank, 1 = STAT.</summary>
    public Action<int>? RequestInterrupt;
    /// <summary>Raised when HBlank starts on a visible line (CGB HBlank DMA).</summary>
    public Action? HBlankStarted;

    public PPU_GB(GbModel model)
    {
        Model = model;
        for (int i = 0; i < 64; i += 2) { BgPalRam[i] = 0xFF; BgPalRam[i + 1] = 0x7F; ObjPalRam[i] = 0xFF; ObjPalRam[i + 1] = 0x7F; }
    }

    public bool LcdOn => (Lcdc & 0x80) != 0;
    public int Mode => mode;
    public int LY => ly;
    public int Dot => dot;

    /// <summary>State right after the boot ROM: LCD on, in VBlank of line 0's frame, as Pan Docs lists.</summary>
    public void ResetPostBoot()
    {
        Lcdc = 0x91; Bgp = 0xFC; Obp0 = 0xFF; Obp1 = 0xFF; Scx = Scy = Wy = Wx = Lyc = 0; statEnable = 0;
        ly = 0; dot = 0; mode = 2; windowLine = 0; windowYTriggered = false; firstLineAfterOn = false; skipFrame = false;
        Array.Fill(FrameBuffer, DmgColors[0]);
        UpdateLyc(); UpdateStatLine();
    }

    // =================================================================================== timing
    /// <summary>Advance by <paramref name="dots"/> dots (4 per M-cycle at single speed, 2 at double speed).</summary>
    public void Tick(int dots)
    {
        if (!LcdOn)
        {
            offDots += dots;
            if (offDots >= DotsPerLine * Lines) { offDots -= DotsPerLine * Lines; FrameCount++; }
            return;
        }
        dot += dots;
        while (true)
        {
            if (ly < Height)
            {
                if (mode == 2 && dot >= 80) StartDrawing();
                else if (mode == 3 && dot >= mode3End) SetMode(0, hblank: true);
                else if (mode == 0 && firstLineAfterOn && dot >= 80 && mode3End == 0) StartDrawing();
                else if (dot >= DotsPerLine) NextLine();
                else break;
            }
            else
            {
                if (ly == 153 && !lyReadsZero && dot >= 4) { lyReadsZero = true; UpdateLyc(); UpdateStatLine(); }
                if (dot >= DotsPerLine) NextLine();
                else break;
            }
        }
    }

    private void NextLine()
    {
        dot -= DotsPerLine;
        firstLineAfterOn = false;
        if (ly < Height && windowRendered) windowLine++;
        windowRendered = false;
        ly++;
        lyReadsZero = false;
        if (ly == 154) { ly = 0; windowLine = 0; windowYTriggered = false; }
        UpdateLyc();
        if (ly == Height)
        {
            SetMode(1);
            RequestInterrupt?.Invoke(0);
            // DMG quirk: the mode-2 STAT source also fires at the start of line 144.
            if ((statEnable & 0x20) != 0 && !statLine) { statLine = true; RequestInterrupt?.Invoke(1); }
            if (skipFrame) { skipFrame = false; Array.Fill(FrameBuffer, Cgb ? 0xFFFFFFFF : DmgColors[0]); }
            FrameCount++;
        }
        else if (ly < Height) { mode3End = 0; SetMode(2); }
        else UpdateStatLine();
    }

    private void SetMode(int m, bool hblank = false)
    {
        mode = m;
        UpdateStatLine();
        if (hblank) HBlankStarted?.Invoke();
    }

    private void UpdateLyc()
    {
        int reported = ly == 153 && lyReadsZero ? 0 : ly;
        lycEqual = reported == Lyc;
    }

    private void UpdateStatLine()
    {
        bool line = (lycEqual && (statEnable & 0x40) != 0)
            || (mode == 0 && (statEnable & 0x08) != 0)
            || (mode == 1 && (statEnable & 0x10) != 0)
            || (mode == 2 && (statEnable & 0x20) != 0);
        if (line && !statLine) RequestInterrupt?.Invoke(1);
        statLine = line;
    }

    // =================================================================================== rendering
    private readonly int[] lineSprites = new int[10];
    private int lineSpriteCount;
    private bool windowRendered;

    private void StartDrawing()
    {
        if (ly == Wy) windowYTriggered = true;
        SelectSprites();
        bool window = (Lcdc & 0x20) != 0 && windowYTriggered && Wx <= 166 && (Cgb || (Lcdc & 0x01) != 0 || true);
        // Drawing length: 172 dots, plus the fine-scroll discard, plus 6 when the window starts, plus per-object fetches.
        int len = 172 + (Scx & 7);
        if (window && Wx <= 166) len += 6;
        for (int i = 0; i < lineSpriteCount; i++)
        {
            int x = Oam[lineSprites[i] * 4 + 1];
            len += 11 - Math.Min(5, (x + Scx) & 7);
        }
        mode3End = 80 + Math.Min(len, 289);
        mode = 3; UpdateStatLine();
        if (!skipFrame) RenderLine(window);
    }

    private void SelectSprites()
    {
        lineSpriteCount = 0;
        if ((Lcdc & 0x02) == 0 && !Cgb) { }
        int h = (Lcdc & 0x04) != 0 ? 16 : 8;
        for (int i = 0; i < 40 && lineSpriteCount < 10; i++)
        {
            int y = Oam[i * 4] - 16;
            if (ly >= y && ly < y + h) lineSprites[lineSpriteCount++] = i;
        }
        if (!Cgb || opri != 0)
        {
            // DMG priority: smaller X first, OAM order breaks ties (stable insertion sort).
            for (int i = 1; i < lineSpriteCount; i++)
            {
                int s = lineSprites[i], x = Oam[s * 4 + 1], j = i - 1;
                while (j >= 0 && Oam[lineSprites[j] * 4 + 1] > x) { lineSprites[j + 1] = lineSprites[j]; j--; }
                lineSprites[j + 1] = s;
            }
        }
    }

    private readonly byte[] bgColor = new byte[Width];     // colour index 0-3
    private readonly byte[] bgAttr = new byte[Width];      // CGB attributes (palette, priority)

    private void RenderLine(bool windowActive)
    {
        int row = ly * Width;
        bool bgEnable = (Lcdc & 0x01) != 0;
        bool tileData8000 = (Lcdc & 0x10) != 0;
        int bgMap = (Lcdc & 0x08) != 0 ? 0x1C00 : 0x1800;
        int winMap = (Lcdc & 0x40) != 0 ? 0x1C00 : 0x1800;
        int winStart = windowActive ? Wx - 7 : Width;

        for (int x = 0; x < Width; x++)
        {
            if (!bgEnable && !Cgb) { bgColor[x] = 0; bgAttr[x] = 0; continue; }
            int map, tx, ty;
            if (x >= winStart)
            {
                windowRendered = true;
                map = winMap; tx = x - winStart; ty = windowLine;
            }
            else
            {
                map = bgMap; tx = (x + Scx) & 0xFF; ty = (ly + Scy) & 0xFF;
            }
            int mapAddr = map + (ty >> 3) * 32 + (tx >> 3);
            int tile = Vram[mapAddr];
            byte attr = Cgb ? Vram[0x2000 + mapAddr] : (byte)0;
            int fy = ty & 7, fx = tx & 7;
            if ((attr & 0x40) != 0) fy = 7 - fy;
            if ((attr & 0x20) != 0) fx = 7 - fx;
            int addr = tileData8000 ? tile * 16 : 0x1000 + (sbyte)tile * 16;
            if ((attr & 0x08) != 0) addr += 0x2000;
            addr += fy * 2;
            int bit = 7 - fx;
            bgColor[x] = (byte)((Vram[addr] >> bit & 1) | (Vram[addr + 1] >> bit & 1) << 1);
            bgAttr[x] = attr;
        }
        if (windowActive && winStart < Width) windowRendered = true;

        // Objects: highest-priority object with an opaque pixel wins that pixel.
        Span<sbyte> objAt = stackalloc sbyte[Width];
        objAt.Fill(-1);
        Span<byte> objCol = stackalloc byte[Width];
        if ((Lcdc & 0x02) != 0)
        {
            int h = (Lcdc & 0x04) != 0 ? 16 : 8;
            for (int k = 0; k < lineSpriteCount; k++)
            {
                int s = lineSprites[k], o = s * 4;
                int sy = Oam[o] - 16, sx = Oam[o + 1] - 8, tile = Oam[o + 2]; byte flags = Oam[o + 3];
                int line = ly - sy;
                if ((flags & 0x40) != 0) line = h - 1 - line;
                if (h == 16) tile &= 0xFE;
                int addr = tile * 16 + line * 2;
                if (Cgb && (flags & 0x08) != 0) addr += 0x2000;
                byte lo = Vram[addr], hi = Vram[addr + 1];
                for (int px = 0; px < 8; px++)
                {
                    int x = sx + px;
                    if (x < 0 || x >= Width || objAt[x] >= 0) continue;
                    int bit = (flags & 0x20) != 0 ? px : 7 - px;
                    int c = (lo >> bit & 1) | (hi >> bit & 1) << 1;
                    if (c == 0) continue;
                    objAt[x] = (sbyte)k; objCol[x] = (byte)c;
                }
            }
        }

        for (int x = 0; x < Width; x++)
        {
            int bc = bgColor[x];
            uint color; byte shade;
            int k = objAt[x];
            bool objWins = false;
            byte flags = 0;
            if (k >= 0)
            {
                flags = Oam[lineSprites[k] * 4 + 3];
                if (Cgb)
                    objWins = !bgEnable || bc == 0 || ((flags & 0x80) == 0 && (bgAttr[x] & 0x80) == 0);
                else
                    objWins = (flags & 0x80) == 0 || bc == 0;
            }
            if (objWins)
            {
                int c = objCol[x];
                if (Cgb) { color = CgbColor(ObjPalRam, flags & 7, c); shade = (byte)c; }
                else
                {
                    byte pal = (flags & 0x10) != 0 ? Obp1 : Obp0;
                    shade = (byte)(pal >> (c * 2) & 3);
                    color = CompatMode ? CgbColor(ObjPalRam, (flags & 0x10) != 0 ? 1 : 0, shade) : DmgColors[shade];
                }
            }
            else if (Cgb) { color = CgbColor(BgPalRam, bgAttr[x] & 7, bc); shade = (byte)bc; }
            else
            {
                shade = (byte)(Bgp >> (bc * 2) & 3);
                color = CompatMode ? CgbColor(BgPalRam, 0, shade) : DmgColors[shade];
            }
            FrameBuffer[row + x] = color;
            ShadeBuffer[row + x] = shade;
        }
    }

    private static uint CgbColor(byte[] pal, int palette, int index)
    {
        int o = palette * 8 + index * 2;
        int c = pal[o] | pal[o + 1] << 8;
        int r = c & 0x1F, g = c >> 5 & 0x1F, b = c >> 10 & 0x1F;
        return 0xFF000000u | (uint)((r << 3 | r >> 2) << 16 | (g << 3 | g >> 2) << 8 | (b << 3 | b >> 2));
    }

    // =================================================================================== CPU access
    public bool VramBlocked => LcdOn && mode == 3;
    public bool OamBlocked => LcdOn && (mode == 2 || mode == 3) && !firstLineAfterOnOamFree;
    private bool firstLineAfterOnOamFree => firstLineAfterOn && mode == 0;

    public byte ReadVram(ushort a) => VramBlocked ? (byte)0xFF : Vram[(a & 0x1FFF) | (Cgb || Model == GbModel.Cgb ? (vbk & 1) << 13 : 0)];
    public void WriteVram(ushort a, byte v) { if (!VramBlocked) Vram[(a & 0x1FFF) | (Model == GbModel.Cgb ? (vbk & 1) << 13 : 0)] = v; }
    /// <summary>Direct VRAM write for CGB HDMA (the transfer happens in HBlank, so blocking does not apply).</summary>
    public void DmaWriteVram(int offset, byte v) => Vram[(offset & 0x1FFF) | (Model == GbModel.Cgb ? (vbk & 1) << 13 : 0)] = v;
    public byte ReadOam(ushort a) => OamBlocked ? (byte)0xFF : a < 0xFEA0 ? Oam[a - 0xFE00] : (byte)0x00;
    public void WriteOam(ushort a, byte v) { if (!OamBlocked && a < 0xFEA0) Oam[a - 0xFE00] = v; }

    public byte ReadRegister(int reg)
    {
        switch (reg)
        {
            case 0x40: return Lcdc;
            case 0x41:
                return (byte)(0x80 | statEnable | (lycEqual ? 4 : 0) | (LcdOn ? mode : 0));
            case 0x42: return Scy;
            case 0x43: return Scx;
            case 0x44: return (byte)(!LcdOn ? 0 : ly == 153 && lyReadsZero ? 0 : ly);
            case 0x45: return Lyc;
            case 0x47: return Bgp;
            case 0x48: return Obp0;
            case 0x49: return Obp1;
            case 0x4A: return Wy;
            case 0x4B: return Wx;
            case 0x4F: return Model == GbModel.Cgb ? (byte)(0xFE | vbk) : (byte)0xFF;
            case 0x68: return Model == GbModel.Cgb ? (byte)(bcps | 0x40) : (byte)0xFF;
            case 0x69: return Model == GbModel.Cgb ? (VramBlocked ? (byte)0xFF : BgPalRam[bcps & 0x3F]) : (byte)0xFF;
            case 0x6A: return Model == GbModel.Cgb ? (byte)(ocps | 0x40) : (byte)0xFF;
            case 0x6B: return Model == GbModel.Cgb ? (VramBlocked ? (byte)0xFF : ObjPalRam[ocps & 0x3F]) : (byte)0xFF;
            case 0x6C: return Model == GbModel.Cgb ? (byte)(0xFE | opri) : (byte)0xFF;
            default: return 0xFF;
        }
    }

    public void WriteRegister(int reg, byte v)
    {
        switch (reg)
        {
            case 0x40:
            {
                bool wasOn = LcdOn;
                Lcdc = v;
                if (wasOn && !LcdOn)
                {
                    ly = 0; dot = 0; mode = 0; lyReadsZero = false; offDots = 0;
                    Array.Fill(FrameBuffer, Cgb ? 0xFFFFFFFF : DmgColors[0]);
                    UpdateLyc(); statLine = false;
                }
                else if (!wasOn && LcdOn)
                {
                    ly = 0; dot = 4 * 0; mode = 0; mode3End = 0; firstLineAfterOn = true; skipFrame = true;
                    windowLine = 0; windowYTriggered = false; windowRendered = false;
                    UpdateLyc(); UpdateStatLine();
                }
                break;
            }
            case 0x41: statEnable = (byte)(v & 0x78); UpdateStatLine(); break;
            case 0x42: Scy = v; break;
            case 0x43: Scx = v; break;
            case 0x44: break;   // read-only
            case 0x45: Lyc = v; if (LcdOn) { UpdateLyc(); UpdateStatLine(); } break;
            case 0x47: Bgp = v; break;
            case 0x48: Obp0 = v; break;
            case 0x49: Obp1 = v; break;
            case 0x4A: Wy = v; break;
            case 0x4B: Wx = v; break;
            case 0x4F: if (Model == GbModel.Cgb) vbk = (byte)(v & 1); break;
            case 0x68: if (Model == GbModel.Cgb) bcps = (byte)(v & 0xBF); break;
            case 0x69:
                if (Model != GbModel.Cgb) break;
                if (!VramBlocked) BgPalRam[bcps & 0x3F] = v;
                if ((bcps & 0x80) != 0) bcps = (byte)(0x80 | ((bcps + 1) & 0x3F));
                break;
            case 0x6A: if (Model == GbModel.Cgb) ocps = (byte)(v & 0xBF); break;
            case 0x6B:
                if (Model != GbModel.Cgb) break;
                if (!VramBlocked) ObjPalRam[ocps & 0x3F] = v;
                if ((ocps & 0x80) != 0) ocps = (byte)(0x80 | ((ocps + 1) & 0x3F));
                break;
            case 0x6C: if (Model == GbModel.Cgb) opri = (byte)(v & 1); break;
        }
    }

    /// <summary>Load the DMG-on-CGB compatibility palettes (what the CGB boot ROM would have picked), BGR555 x 4 each.</summary>
    public void SetCompatPalettes(ushort[] bg, ushort[] obj0, ushort[] obj1)
    {
        for (int i = 0; i < 4; i++)
        {
            BgPalRam[i * 2] = (byte)bg[i]; BgPalRam[i * 2 + 1] = (byte)(bg[i] >> 8);
            ObjPalRam[i * 2] = (byte)obj0[i]; ObjPalRam[i * 2 + 1] = (byte)(obj0[i] >> 8);
            ObjPalRam[8 + i * 2] = (byte)obj1[i]; ObjPalRam[8 + i * 2 + 1] = (byte)(obj1[i] >> 8);
        }
    }

    // =================================================================================== savestate
    public void SaveState(BinaryWriter w)
    {
        w.Write(Vram); w.Write(Oam); w.Write(BgPalRam); w.Write(ObjPalRam);
        w.Write(Lcdc); w.Write(Scy); w.Write(Scx); w.Write(Lyc); w.Write(Bgp); w.Write(Obp0); w.Write(Obp1); w.Write(Wy); w.Write(Wx);
        w.Write(statEnable); w.Write(ly); w.Write(dot); w.Write(mode); w.Write(mode3End); w.Write(statLine); w.Write(lycEqual); w.Write(lyReadsZero);
        w.Write(windowLine); w.Write(windowYTriggered); w.Write(firstLineAfterOn); w.Write(skipFrame); w.Write(offDots);
        w.Write(vbk); w.Write(bcps); w.Write(ocps); w.Write(opri); w.Write(CompatMode); w.Write(FrameCount);
    }
    public void LoadState(BinaryReader r)
    {
        r.ReadBytes(Vram.Length).CopyTo(Vram, 0); r.ReadBytes(Oam.Length).CopyTo(Oam, 0);
        r.ReadBytes(64).CopyTo(BgPalRam, 0); r.ReadBytes(64).CopyTo(ObjPalRam, 0);
        Lcdc = r.ReadByte(); Scy = r.ReadByte(); Scx = r.ReadByte(); Lyc = r.ReadByte(); Bgp = r.ReadByte(); Obp0 = r.ReadByte(); Obp1 = r.ReadByte(); Wy = r.ReadByte(); Wx = r.ReadByte();
        statEnable = r.ReadByte(); ly = r.ReadByte(); dot = r.ReadInt32(); mode = r.ReadInt32(); mode3End = r.ReadInt32(); statLine = r.ReadBoolean(); lycEqual = r.ReadBoolean(); lyReadsZero = r.ReadBoolean();
        windowLine = r.ReadInt32(); windowYTriggered = r.ReadBoolean(); firstLineAfterOn = r.ReadBoolean(); skipFrame = r.ReadBoolean(); offDots = r.ReadInt32();
        vbk = r.ReadByte(); bcps = r.ReadByte(); ocps = r.ReadByte(); opri = r.ReadByte(); CompatMode = r.ReadBoolean(); FrameCount = r.ReadInt64();
    }
}
