using System;
using System.IO;

namespace NesEmulator.Gb;

/// <summary>
/// Game Boy / Game Boy Color picture processor, dot accurate. Each line runs OAM scan, then a pixel-FIFO
/// drawing phase (background/window fetcher feeding an 8-pixel FIFO, objects fetched into their own FIFO and
/// stalling the output as they are met), then HBlank; STAT, LY/LYC, interrupts and CPU access blocking follow
/// the documented per-dot timeline. Written from Pan Docs ("Rendering", "Pixel FIFO", "STAT", "Palettes"),
/// the GBEDG PPU notes and The Cycle-Accurate Game Boy Docs.
///
/// Timing frame: <c>dot</c> counts dots since LY last changed. The board ticks a whole M-cycle and then lets
/// the CPU access the bus, so an access sees the state after that M-cycle's dots. In that frame, per line:
/// dot 0 LY changes (the mode-2 interrupt fires, STAT still reads mode 0), dot 4 STAT reads mode 2 (or 1 on
/// line 144, with the VBlank interrupt), dot 80 drawing starts internally, dot 84 STAT reads mode 3, drawing
/// ends at 252 + penalties (mode-0 interrupt) and STAT reads mode 0 four dots later.
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
    /// <summary>0xFFRRGGBB, 160x144. Written pixel by pixel; complete at VBlank.</summary>
    public readonly uint[] FrameBuffer = new uint[Width * Height];
    /// <summary>Per pixel: DMG shade 0-3 after palette mapping (DMG), or the colour index 0-3 (CGB). Handy for bridges.</summary>
    public readonly byte[] ShadeBuffer = new byte[Width * Height];
    /// <summary>The four DMG shades as 0xFFRRGGBB (lightest first). Frontends can recolour.</summary>
    public uint[] DmgColors = { 0xFFFFFFFF, 0xFFAAAAAA, 0xFF555555, 0xFF000000 };

    public byte Lcdc = 0x91, Scy, Scx, Lyc, Bgp = 0xFC, Obp0 = 0xFF, Obp1 = 0xFF, Wy, Wx;
    private byte statEnable;          // STAT bits 3-6
    private byte vbk, bcps, ocps, opri;
    public long FrameCount { get; private set; }

    /// <summary>Raised with the IF bit to set: 0 = VBlank, 1 = STAT.</summary>
    public Action<int>? RequestInterrupt;
    /// <summary>True while <see cref="RequestInterrupt"/> runs for an interrupt that arrives late in its M-cycle:
    /// after the point where a halted CPU samples IF, so it wakes the CPU one M-cycle later.</summary>
    public bool InterruptLate { get; private set; }
    /// <summary>Raised when HBlank starts on a visible line (CGB HBlank DMA).</summary>
    public Action? HBlankStarted;

    public PPU_GB(GbModel model)
    {
        Model = model;
        for (int i = 0; i < 64; i += 2) { BgPalRam[i] = 0xFF; BgPalRam[i + 1] = 0x7F; ObjPalRam[i] = 0xFF; ObjPalRam[i + 1] = 0x7F; }
    }

    public bool LcdOn => (Lcdc & 0x80) != 0;
    public int Mode => visMode;
    public int LY => ly;
    public int Dot => dot;

    // =================================================================================== line state
    private int ly;                   // internal line 0-153
    private int dot;                  // dots since LY changed, 0-455
    private int visMode;              // mode as STAT reports it (and as CPU access blocking sees it)
    private int nextEvent;            // dot of the next scheduled line event (when not drawing)
    private int phase;                // which event nextEvent is
    private bool drawing;             // mode 3 pixel pipeline running
    // The interrupt side of the PPU runs a few dots ahead of what the CPU reads back from STAT: the LY=LYC
    // comparison and the mode sources change for the interrupt line one M-cycle before STAT shows them.
    private int lyCmpVis, lyCmpIrq;   // values the two LYC comparisons see, -1 while LY is changing
    private bool lycEqual, lycIrq;
    private bool lyReadsZero;         // line 153 after its first dots reads LY=0
    private bool src0, src1, src2;    // STAT interrupt sources: HBlank, VBlank, OAM scan
    private bool statLine;            // OR of the enabled sources (the interrupt fires on its rising edge)
    private bool firstLine;           // the line the LCD was switched on in: no OAM scan
    private bool skipFrame;           // the first frame after switching on is not shown
    private int offDots;              // LCD off: keep delivering blank frames at the normal rate
    private int windowLine;           // internal window line counter
    private bool windowYTriggered;    // WY matched LY at some point this frame

    // Line events (phase values).
    private const int PhLineStart = 0, PhDot4 = 1, PhDraw = 2, PhHblankIrq = 3, PhHblank = 4, PhPreLine = 5, PhIrqLine = 6,
        PhL153Dot2 = 7, PhL153Dot6 = 8, PhL153Dot8 = 9, PhL153Dot12 = 10;
    /// <summary>Dot at which the pixel pipeline starts (the first pixel leaves it 12 dots later).</summary>
    private const int DrawStart = 78;

    /// <summary>State right after the boot ROM hands over: line 153 of the last boot frame, 60 dots before line 0.</summary>
    public void ResetPostBoot()
    {
        Lcdc = 0x91; Bgp = 0xFC; Obp0 = 0xFF; Obp1 = 0xFF; Scx = Scy = Wy = Wx = Lyc = 0; statEnable = 0;
        ly = 153; dot = 396; visMode = 1; drawing = false; lyReadsZero = true; lyCmpVis = 0; lyCmpIrq = 0;
        src0 = false; src1 = true; src2 = false; statLine = false;
        windowLine = 0; windowYTriggered = false; firstLine = false; skipFrame = false; offDots = 0;
        phase = PhPreLine; nextEvent = 452;
        Array.Fill(FrameBuffer, DmgColors[0]);
        UpdateLyc(); UpdateStatLine();
    }

    // =================================================================================== timing
    private int tickEnd, tickHalf;    // the M-cycle being run: its last dot, and half its length

    /// <summary>Advance by <paramref name="dots"/> dots (4 per M-cycle at single speed, 2 at double speed).</summary>
    public void Tick(int dots)
    {
        if (!LcdOn)
        {
            offDots += dots;
            if (offDots >= DotsPerLine * Lines) { offDots -= DotsPerLine * Lines; FrameCount++; }
            return;
        }
        int target = dot + dots;
        tickEnd = target; tickHalf = dots >> 1;
        while (true)
        {
            if (drawing)
            {
                while (dot < target)
                {
                    StepDraw();
                    dot++;
                    if (!drawing) break;
                }
                if (drawing) return;
                phase = PhHblankIrq; nextEvent = dot;
            }
            if (nextEvent > target) { dot = target; return; }
            int ev = nextEvent;
            if (ev >= DotsPerLine) { target -= DotsPerLine; tickEnd = target; ev -= DotsPerLine; }
            dot = ev;
            RunEvent();
        }
    }

    // Per line: LY changes at dot 0 (STAT still reads mode 0), STAT shows the new mode and LY=LYC at dot 4, drawing
    // runs from DrawStart, HBlank follows. The interrupt side leads: the OAM-scan source rises at dot 452 of the
    // line before, the LYC comparison and the VBlank interrupt move to the next line at dot 454.
    private void RunEvent()
    {
        switch (phase)
        {
            case PhLineStart:
                NextLine();
                if (ly == 153) { phase = PhL153Dot2; nextEvent = 2; }
                else { phase = PhDot4; nextEvent = 4; }
                break;
            case PhDot4:
                if (ly < Height) { if (!firstLine) visMode = 2; }
                else if (ly == Height) visMode = 1;
                lyCmpVis = ly;
                if (ly == 153) { lyReadsZero = true; phase = PhL153Dot6; nextEvent = 6; }
                else if (ly < Height) { phase = PhDraw; nextEvent = DrawStart; }
                else { phase = PhPreLine; nextEvent = 452; }
                UpdateLyc(); UpdateStatLine();
                break;
            // Line 153: LY reads 0 from dot 4, and both comparisons pass through "no value" on the way from 153 to 0.
            case PhL153Dot2:
                lyCmpIrq = -1; UpdateLyc(); UpdateStatLine();
                phase = PhDot4; nextEvent = 4;
                break;
            case PhL153Dot6:
                lyCmpIrq = 0; UpdateLyc(); UpdateStatLine();
                phase = PhL153Dot8; nextEvent = 8;
                break;
            case PhL153Dot8:
                lyCmpVis = -1; UpdateLyc(); UpdateStatLine();
                phase = PhL153Dot12; nextEvent = 12;
                break;
            case PhL153Dot12:
                lyCmpVis = 0; UpdateLyc(); UpdateStatLine();
                phase = PhPreLine; nextEvent = 452;
                break;
            case PhDraw:              // OAM scan done, drawing starts
                src2 = false; UpdateStatLine();
                StartDrawing();
                break;
            case PhHblankIrq:         // drawing just finished
                src0 = true; UpdateStatLine();
                phase = PhHblank; nextEvent = dot + 4;
                break;
            case PhHblank:
                visMode = 0;
                HBlankStarted?.Invoke();
                phase = PhPreLine; nextEvent = 452;
                break;
            case PhPreLine:
            {
                int next = ly == 153 ? 0 : ly + 1;
                if (next != 0) lyCmpIrq = -1;
                if (next <= Height)
                {
                    // The OAM-scan source rises for the next visible line (and also, as a quirk, for line 144).
                    src0 = false; src1 = false; src2 = true;
                }
                UpdateLyc(); UpdateStatLine();
                phase = PhIrqLine; nextEvent = 454;
                break;
            }
            case PhIrqLine:
            {
                int next = ly == 153 ? 0 : ly + 1;
                lyCmpIrq = next;
                if (next == Height)
                {
                    src2 = false; src1 = true;
                    InterruptLate = tickEnd - dot < tickHalf;
                    RequestInterrupt?.Invoke(0);
                    InterruptLate = false;
                }
                UpdateLyc(); UpdateStatLine();
                phase = PhLineStart; nextEvent = DotsPerLine;
                break;
            }
        }
    }

    private void NextLine()
    {
        if (ly < Height && windowRendered) windowLine++;
        windowRendered = false;
        firstLine = false;
        ly++;
        lyReadsZero = false;
        if (ly == Lines) { ly = 0; windowLine = 0; windowYTriggered = false; }
        if (ly != 0) lyCmpVis = -1;   // LY has read 0 since early line 153: line 0 keeps matching
        if (ly <= Height) visMode = 0;
        if (ly == Height)
        {
            if (skipFrame) { skipFrame = false; Array.Fill(FrameBuffer, Cgb ? 0xFFFFFFFF : DmgColors[0]); }
            FrameCount++;
        }
        UpdateLyc();
    }

    private void UpdateLyc()
    {
        lycEqual = lyCmpVis == Lyc;
        lycIrq = lyCmpIrq == Lyc;
    }

    /// <summary>Re-evaluate the STAT interrupt line; a rising edge requests the interrupt. An interrupt raised in the
    /// last half of the M-cycle being run is flagged late (a halted CPU only notices it one M-cycle later).</summary>
    private void UpdateStatLine()
    {
        bool line = (lycIrq && (statEnable & 0x40) != 0)
            || (src0 && (statEnable & 0x08) != 0)
            || (src1 && (statEnable & 0x10) != 0)
            || (src2 && (statEnable & 0x20) != 0);
        if (line && !statLine)
        {
            InterruptLate = tickEnd - dot < tickHalf;
            RequestInterrupt?.Invoke(1);
            InterruptLate = false;
        }
        statLine = line;
    }

    // =================================================================================== drawing (mode 3)
    // Objects found by the OAM scan, sorted by X (OAM order breaks ties): the order they are fetched in.
    private readonly int[] lineSprites = new int[10];
    private readonly byte[] spriteX = new byte[10];
    private int lineSpriteCount, nextSprite;
    private bool windowRendered;

    private int m;                    // dots since drawing started
    private int lcdX;                 // next screen pixel to output
    private int discard;              // fine-scroll pixels still to drop
    private bool scxLatched;
    // Background/window fetcher.
    private int fetchDots;            // dots spent on the current tile, 6 = fetched and waiting to push
    private bool dummyFetch;          // the first fetch of a line is thrown away
    private int fetchX;               // background tile column counter
    private bool fetchWindow, windowActive;
    private int winFetchX;
    private byte tileIdx, tileAttr, tileLo, tileHi;
    // Background FIFO: it only ever holds one tile's pixels (a tile is pushed when it is empty).
    private int bgCount;
    private byte bgLo, bgHi, bgAttr;
    // Object FIFO: 8 slots relative to the output position. Colour 0 = transparent / empty.
    private readonly byte[] objColor = new byte[8], objAttr = new byte[8], objIndex = new byte[8];
    private int objHead;
    // Object fetch.
    private int spriteFetchDots;      // >0 while fetching an object
    private bool spriteWait;          // an object is due and waits for the background fetch

    private void StartDrawing()
    {
        if (ly == Wy) windowYTriggered = true;
        if (firstLine) lineSpriteCount = 0; else SelectSprites();
        nextSprite = 0;
        drawing = true;
        m = 0; lcdX = 0; discard = 0; scxLatched = false;
        fetchDots = 0; dummyFetch = true; fetchX = 0; fetchWindow = false; windowActive = false; winFetchX = 0;
        bgCount = 0;
        Array.Clear(objColor); objHead = 0;
        spriteFetchDots = 0; spriteWait = false;
    }

    private void SelectSprites()
    {
        lineSpriteCount = 0;
        int h = (Lcdc & 0x04) != 0 ? 16 : 8;
        for (int i = 0; i < 40 && lineSpriteCount < 10; i++)
        {
            int y = Oam[i * 4] - 16;
            if (ly >= y && ly < y + h) { lineSprites[lineSpriteCount] = i; spriteX[lineSpriteCount] = Oam[i * 4 + 1]; lineSpriteCount++; }
        }
        // Fetch order: by X, OAM order breaking ties (stable insertion sort).
        for (int i = 1; i < lineSpriteCount; i++)
        {
            int s = lineSprites[i]; byte x = spriteX[i]; int j = i - 1;
            while (j >= 0 && spriteX[j] > x) { lineSprites[j + 1] = lineSprites[j]; spriteX[j + 1] = spriteX[j]; j--; }
            lineSprites[j + 1] = s; spriteX[j + 1] = x;
        }
    }

    /// <summary>One dot of mode 3. <see cref="dot"/> is the dot being processed.</summary>
    private void StepDraw()
    {
        m++;
        if (dot == 83) visMode = 3;     // STAT reads mode 3 from dot 84

        // Object fetch in progress: the background fetcher and the output are stalled.
        if (spriteFetchDots > 0)
        {
            if (++spriteFetchDots > 6) { FetchSprite(); spriteFetchDots = 0; }
            else return;
        }

        // Push a fetched tile into an empty background FIFO.
        if (fetchDots >= 6 && bgCount == 0)
        {
            fetchDots = 0;
            if (dummyFetch) dummyFetch = false;
            else
            {
                bgLo = tileLo; bgHi = tileHi; bgAttr = tileAttr; bgCount = 8;
                if (fetchWindow) winFetchX++; else fetchX++;
                if (!scxLatched) { scxLatched = true; discard = Scx & 7; }
            }
        }

        // An object at the output position: wait for the background fetch to get far enough, then fetch it.
        if (bgCount > 0 && discard == 0 && nextSprite < lineSpriteCount && spriteX[nextSprite] <= lcdX + 8)
        {
            if ((Lcdc & 0x02) == 0 && !Cgb) { nextSprite++; }
            else if (fetchDots >= 5)
            {
                spriteFetchDots = 1;
                return;
            }
            else { BgFetchStep(); return; }
        }

        BgFetchStep();

        if (bgCount == 0) return;

        if (discard > 0) { bgHi <<= 1; bgLo <<= 1; bgCount--; discard--; return; }

        // Window start: throw away the background and fetch the window instead.
        if (!windowActive && (Lcdc & 0x20) != 0 && windowYTriggered && (lcdX + 7 == Wx || (Wx < 7 && lcdX == 0)))
        {
            windowActive = true; windowRendered = true; fetchWindow = true; winFetchX = 0;
            fetchDots = 0; bgCount = 0;
            if (Wx < 7) discard = 7 - Wx;
            return;
        }

        // Shift one pixel out.
        int bc = (bgHi >> 6 & 2) | (bgLo >> 7 & 1);
        bgHi <<= 1; bgLo <<= 1; bgCount--;
        int oh = objHead;
        int oc = objColor[oh]; byte oa = objAttr[oh];
        objColor[oh] = 0; objHead = (oh + 1) & 7;
        OutputPixel(bc, oc, oa);
    }

    private void BgFetchStep()
    {
        if (fetchDots >= 6) return;
        fetchDots++;
        switch (fetchDots)
        {
            case 2: FetchTileIndex(); break;
            case 4: tileLo = FetchTileData(0); break;
            case 6: tileHi = FetchTileData(1); break;
        }
    }

    private void FetchTileIndex()
    {
        int addr;
        if (fetchWindow)
        {
            int map = (Lcdc & 0x40) != 0 ? 0x1C00 : 0x1800;
            addr = map + (windowLine >> 3) * 32 + (winFetchX & 31);
        }
        else
        {
            int map = (Lcdc & 0x08) != 0 ? 0x1C00 : 0x1800;
            addr = map + (((ly + Scy) & 0xFF) >> 3) * 32 + (((Scx >> 3) + fetchX) & 31);
        }
        tileIdx = Vram[addr];
        tileAttr = Cgb ? Vram[0x2000 + addr] : (byte)0;
    }

    private byte FetchTileData(int plane)
    {
        int row = fetchWindow ? windowLine & 7 : (ly + Scy) & 7;
        if ((tileAttr & 0x40) != 0) row = 7 - row;
        int addr = (Lcdc & 0x10) != 0 ? tileIdx * 16 : 0x1000 + (sbyte)tileIdx * 16;
        if ((tileAttr & 0x08) != 0) addr += 0x2000;
        byte b = Vram[addr + row * 2 + plane];
        if ((tileAttr & 0x20) != 0) b = Reverse(b);
        return b;
    }

    private static byte Reverse(byte b)
    {
        b = (byte)((b & 0xF0) >> 4 | (b & 0x0F) << 4);
        b = (byte)((b & 0xCC) >> 2 | (b & 0x33) << 2);
        return (byte)((b & 0xAA) >> 1 | (b & 0x55) << 1);
    }

    private void FetchSprite()
    {
        int k = nextSprite++;
        int s = lineSprites[k], o = s * 4;
        int h = (Lcdc & 0x04) != 0 ? 16 : 8;
        int sy = Oam[o] - 16, tile = Oam[o + 2]; byte flags = Oam[o + 3];
        int line = (ly - sy) & 15;
        if ((flags & 0x40) != 0) line = h - 1 - line;
        if (h == 16) tile &= 0xFE;
        line &= h - 1;
        int addr = tile * 16 + line * 2;
        if (Cgb && (flags & 0x08) != 0) addr += 0x2000;
        byte lo = Vram[addr], hi = Vram[addr + 1];
        if ((flags & 0x20) != 0) { lo = Reverse(lo); hi = Reverse(hi); }
        int x = spriteX[k];
        int skip = x < 8 ? 8 - x : 0;
        bool cgbPri = Cgb && opri == 0;
        for (int px = skip; px < 8; px++)
        {
            int c = (hi >> (7 - px) << 1 & 2) | (lo >> (7 - px) & 1);
            if (c == 0) continue;
            int slot = (objHead + px - skip) & 7;
            if (objColor[slot] != 0)
            {
                if (!cgbPri || objIndex[slot] < s) continue;
            }
            objColor[slot] = (byte)c; objAttr[slot] = flags; objIndex[slot] = (byte)s;
        }
    }

    private void OutputPixel(int bc, int oc, byte oa)
    {
        int i = ly * Width + lcdX;
        uint color; byte shade;
        if (Cgb)
        {
            byte attr = bgAttr;
            bool objWins = oc != 0 && (Lcdc & 0x02) != 0
                && ((Lcdc & 0x01) == 0 || bc == 0 || ((oa & 0x80) == 0 && (attr & 0x80) == 0));
            if (objWins) { color = CgbColor(ObjPalRam, oa & 7, oc); shade = (byte)oc; }
            else { color = CgbColor(BgPalRam, attr & 7, bc); shade = (byte)bc; }
        }
        else
        {
            if ((Lcdc & 0x01) == 0) bc = 0;
            bool objWins = oc != 0 && (Lcdc & 0x02) != 0 && ((oa & 0x80) == 0 || bc == 0);
            if (objWins)
            {
                byte pal = (oa & 0x10) != 0 ? Obp1 : Obp0;
                shade = (byte)(pal >> (oc * 2) & 3);
                color = CompatMode ? CgbColor(ObjPalRam, (oa & 0x10) != 0 ? 1 : 0, shade) : DmgColors[shade];
            }
            else
            {
                shade = (byte)(Bgp >> (bc * 2) & 3);
                color = CompatMode ? CgbColor(BgPalRam, 0, shade) : DmgColors[shade];
            }
        }
        FrameBuffer[i] = color;
        ShadeBuffer[i] = shade;
        if (++lcdX == Width) drawing = false;
    }

    private static uint CgbColor(byte[] pal, int palette, int index)
    {
        int o = palette * 8 + index * 2;
        int c = pal[o] | pal[o + 1] << 8;
        int r = c & 0x1F, g = c >> 5 & 0x1F, b = c >> 10 & 0x1F;
        return 0xFF000000u | (uint)((r << 3 | r >> 2) << 16 | (g << 3 | g >> 2) << 8 | (b << 3 | b >> 2));
    }

    // =================================================================================== CPU access
    public bool VramBlocked => LcdOn && visMode == 3;
    public bool OamBlocked => LcdOn && (visMode == 2 || visMode == 3);

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
                return (byte)(0x80 | statEnable | (LcdOn && lycEqual ? 4 : 0) | (LcdOn ? visMode : 0));
            case 0x42: return Scy;
            case 0x43: return Scx;
            case 0x44: return (byte)(!LcdOn ? 0 : lyReadsZero ? 0 : ly);
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
                    ly = 0; dot = 0; visMode = 0; drawing = false; lyReadsZero = false; offDots = 0;
                    src0 = src1 = src2 = false; lyCmpVis = lyCmpIrq = 0;
                    Array.Fill(FrameBuffer, Cgb ? 0xFFFFFFFF : DmgColors[0]);
                    UpdateLyc(); statLine = false;
                }
                else if (!wasOn && LcdOn)
                {
                    // The first line starts 4 dots in, without an OAM scan (STAT reads mode 0 until drawing).
                    ly = 0; dot = 4; visMode = 0; drawing = false; firstLine = true; skipFrame = true; lyReadsZero = false;
                    windowLine = 0; windowYTriggered = false; windowRendered = false;
                    src0 = src1 = src2 = false; lyCmpVis = lyCmpIrq = 0;
                    phase = PhDraw; nextEvent = DrawStart + 2;   // drawing starts 2 dots later on this line
                    UpdateLyc(); UpdateStatLine();
                }
                break;
            }
            case 0x41:
                statEnable = (byte)(v & 0x78);
                if (LcdOn) UpdateStatLine();
                break;
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
        w.Write(statEnable); w.Write(vbk); w.Write(bcps); w.Write(ocps); w.Write(opri); w.Write(CompatMode); w.Write(FrameCount);
        w.Write(ly); w.Write(dot); w.Write(visMode); w.Write(nextEvent); w.Write(phase); w.Write(drawing); w.Write(lyCmpVis); w.Write(lyCmpIrq); w.Write(lycEqual); w.Write(lycIrq);
        w.Write(lyReadsZero); w.Write(src0); w.Write(src1); w.Write(src2); w.Write(statLine); w.Write(firstLine); w.Write(skipFrame);
        w.Write(offDots); w.Write(windowLine); w.Write(windowYTriggered); w.Write(windowRendered);
        w.Write(lineSpriteCount); w.Write(nextSprite);
        for (int i = 0; i < 10; i++) { w.Write(lineSprites[i]); w.Write(spriteX[i]); }
        w.Write(m); w.Write(lcdX); w.Write(discard); w.Write(scxLatched); w.Write(fetchDots); w.Write(dummyFetch); w.Write(fetchX);
        w.Write(fetchWindow); w.Write(windowActive); w.Write(winFetchX); w.Write(tileIdx); w.Write(tileAttr); w.Write(tileLo); w.Write(tileHi);
        w.Write(bgCount); w.Write(bgLo); w.Write(bgHi); w.Write(bgAttr);
        w.Write(objColor); w.Write(objAttr); w.Write(objIndex); w.Write(objHead); w.Write(spriteFetchDots); w.Write(spriteWait);
    }

    public void LoadState(BinaryReader r)
    {
        r.ReadBytes(Vram.Length).CopyTo(Vram, 0); r.ReadBytes(Oam.Length).CopyTo(Oam, 0);
        r.ReadBytes(64).CopyTo(BgPalRam, 0); r.ReadBytes(64).CopyTo(ObjPalRam, 0);
        Lcdc = r.ReadByte(); Scy = r.ReadByte(); Scx = r.ReadByte(); Lyc = r.ReadByte(); Bgp = r.ReadByte(); Obp0 = r.ReadByte(); Obp1 = r.ReadByte(); Wy = r.ReadByte(); Wx = r.ReadByte();
        statEnable = r.ReadByte(); vbk = r.ReadByte(); bcps = r.ReadByte(); ocps = r.ReadByte(); opri = r.ReadByte(); CompatMode = r.ReadBoolean(); FrameCount = r.ReadInt64();
        ly = r.ReadInt32(); dot = r.ReadInt32(); visMode = r.ReadInt32(); nextEvent = r.ReadInt32(); phase = r.ReadInt32(); drawing = r.ReadBoolean(); lyCmpVis = r.ReadInt32(); lyCmpIrq = r.ReadInt32(); lycEqual = r.ReadBoolean(); lycIrq = r.ReadBoolean();
        lyReadsZero = r.ReadBoolean(); src0 = r.ReadBoolean(); src1 = r.ReadBoolean(); src2 = r.ReadBoolean(); statLine = r.ReadBoolean(); firstLine = r.ReadBoolean(); skipFrame = r.ReadBoolean();
        offDots = r.ReadInt32(); windowLine = r.ReadInt32(); windowYTriggered = r.ReadBoolean(); windowRendered = r.ReadBoolean();
        lineSpriteCount = r.ReadInt32(); nextSprite = r.ReadInt32();
        for (int i = 0; i < 10; i++) { lineSprites[i] = r.ReadInt32(); spriteX[i] = r.ReadByte(); }
        m = r.ReadInt32(); lcdX = r.ReadInt32(); discard = r.ReadInt32(); scxLatched = r.ReadBoolean(); fetchDots = r.ReadInt32(); dummyFetch = r.ReadBoolean(); fetchX = r.ReadInt32();
        fetchWindow = r.ReadBoolean(); windowActive = r.ReadBoolean(); winFetchX = r.ReadInt32(); tileIdx = r.ReadByte(); tileAttr = r.ReadByte(); tileLo = r.ReadByte(); tileHi = r.ReadByte();
        bgCount = r.ReadInt32(); bgLo = r.ReadByte(); bgHi = r.ReadByte(); bgAttr = r.ReadByte();
        r.ReadBytes(8).CopyTo(objColor, 0); r.ReadBytes(8).CopyTo(objAttr, 0); r.ReadBytes(8).CopyTo(objIndex, 0);
        objHead = r.ReadInt32(); spriteFetchDots = r.ReadInt32(); spriteWait = r.ReadBoolean();
    }
}
