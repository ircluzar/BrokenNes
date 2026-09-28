using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NesEmulator.Snes;

/// <summary>
/// SNES PPU (S-PPU1 + S-PPU2) - SFC family.
///
/// Scanline renderer: the board calls <see cref="RenderLine"/> at the start of each visible line,
/// so anything HDMA or a raster IRQ changed during the previous line's hblank shows up on this one.
/// Covers BG modes 0-7 (8x8/16x16 tiles, offset-per-tile in 2/4/6, Mode 7 with EXTBG, hi-res 5/6
/// averaged down to 256 wide), 128 sprites with the 32-per-line / 34-tile limits and OAM priority
/// rotation, the per-mode priority tables (including mode 1's BG3-on-top bit), both windows with
/// their logic ops, main/sub screens, color math (add/sub, half, fixed color, clip-to-black),
/// mosaic, direct color, brightness and overscan.
///
/// Known approximations: interlace is rendered progressive, hi-res is averaged into FrameBuffer (the true 512-wide lines are in HiResBuffer, see GetHiResFrame), mid-line register
/// changes land on the next line, and sprite evaluation happens all at once per line.
///
/// VRAM is stored as 32K 16-bit words because that is how the chip addresses it.
///
/// PROVENANCE: written from memory and hardware documentation. The Mode 7 pre-rounding/clip formula
/// and the color-window region semantics follow bsnes/higan's approach. See THIRD_PARTY_NOTICES.md.
/// </summary>
public sealed class PPU_SFC
{
    public string CoreName => "SFC";
    public string Description => "SNES PPU - scanline renderer, all modes, sprites, windows, color math";
    public string Category => "Accuracy";

    public const int Width = 256, Height = 224, MaxHeight = 239;

    public readonly ushort[] Vram = new ushort[0x8000];
    public readonly ushort[] Cgram = new ushort[256];
    public readonly byte[] Oam = new byte[544];
    /// <summary>0xAARRGGBB, 256 wide; rows beyond <see cref="VisibleHeight"/> are stale.</summary>
    public readonly uint[] FrameBuffer = new uint[Width * MaxHeight];

    /// <summary>224, or 239 when the game enabled overscan ($2133 bit 2). Latched at frame start.</summary>
    public int VisibleHeight { get; private set; } = Height;

    /// <summary>Hardware ignores VRAM writes outside vblank/forced blank. Kept switchable for diagnosis.</summary>
    public bool DropVramWritesDuringDisplay { get; set; } = true;
    private bool inDisplay;         // between BeginFrame (line 0) and OnVBlankStart
    private bool rangeOver, timeOver;

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

    /// <summary>Debug: layers allowed on screen (bit 0-3 = BG1-4, bit 4 = OBJ). Masks TM and TS.</summary>
    public int DebugLayerMask { get; set; } = 0x1F;

    /// <summary>
    /// Read-only copy of the rendering registers, for tools and cross-family bridges (register values
    /// as written, not derived state). Taking it has no side effects.
    /// </summary>
    public readonly record struct RegisterSnapshot(
        byte Inidisp, byte Bgmode, byte Mosaic, byte Obsel, byte Bg1sc, byte Bg2sc, byte Bg3sc, byte Bg4sc,
        byte Bg12nba, byte Bg34nba, ushort Bg1hofs, ushort Bg1vofs, ushort Bg2hofs, ushort Bg2vofs,
        ushort Bg3hofs, ushort Bg3vofs, ushort Bg4hofs, ushort Bg4vofs, byte Tm, byte Ts, byte Tmw, byte Tsw,
        byte Cgwsel, byte Cgadsub, byte Setini, byte M7sel)
    {
        public override string ToString() =>
            $"INIDISP={Inidisp:X2} BGMODE={Bgmode:X2} MOSAIC={Mosaic:X2} OBSEL={Obsel:X2} " +
            $"BGnSC={Bg1sc:X2},{Bg2sc:X2},{Bg3sc:X2},{Bg4sc:X2} NBA={Bg12nba:X2},{Bg34nba:X2} " +
            $"SCROLL={Bg1hofs},{Bg1vofs} {Bg2hofs},{Bg2vofs} {Bg3hofs},{Bg3vofs} {Bg4hofs},{Bg4vofs} " +
            $"TM={Tm:X2} TS={Ts:X2} TMW={Tmw:X2} TSW={Tsw:X2} CGWSEL={Cgwsel:X2} CGADSUB={Cgadsub:X2} SETINI={Setini:X2} M7SEL={M7sel:X2}";
    }

    public RegisterSnapshot GetRegisterSnapshot() => new(
        inidisp, bgmode, mosaic, obsel, bgsc[0], bgsc[1], bgsc[2], bgsc[3], bg12nba, bg34nba,
        hofs[0], vofs[0], hofs[1], vofs[1], hofs[2], vofs[2], hofs[3], vofs[3], tm, ts, tmw, tsw,
        cgwsel, cgadsub, setini, m7sel);
    private bool VramWritable => !DropVramWritesDuringDisplay || !inDisplay || ForcedBlank;

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
            case 0x01: obsel = v; oamVersion++; break;
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
                // Outside vblank/forced blank the write is lost, but the address still increments.
                if (VramWritable) { int a = VramAddress(); Vram[a] = (ushort)((Vram[a] & 0xFF00) | v); }
                if ((vmain & 0x80) == 0) VramIncrement();
                break;
            }
            case 0x19:
            {
                if (VramWritable) { int a = VramAddress(); Vram[a] = (ushort)((Vram[a] & 0x00FF) | v << 8); }
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
            case 0x3E: return ppu1Mdr = (byte)((timeOver ? 0x80 : 0) | (rangeOver ? 0x40 : 0) | (ppu1Mdr & 0x10) | 0x01);
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
        oamVersion++;
        if (oamInternal < 0x200)
        {
            if ((oamInternal & 1) == 0) oamLatch = v;
            else { Oam[oamInternal - 1] = oamLatch; Oam[oamInternal] = v; }
        }
        else Oam[0x200 + (oamInternal & 0x1F)] = v;
        oamInternal = (ushort)((oamInternal + 1) & 0x3FF);
    }

    // =====================================================================================
    //  Frame / line hooks (called by the board)
    // =====================================================================================

    /// <summary>Line 0: the pre-render line. Latches overscan and clears the sprite overflow flags.</summary>
    // ---- Full-resolution output for hi-res frames (modes 5/6) ----
    // FrameBuffer always holds the 256-wide picture (hi-res pairs averaged), which keeps every
    // existing consumer and the golden hashes unchanged. When a frame has hi-res lines, those lines
    // are also kept at their true 512-pixel width here; GetHiResFrame() fills in the other lines by
    // doubling pixels, so a front end can show sharp hi-res text.
    public const int HiResWidth = 512;
    public readonly uint[] HiResBuffer = new uint[HiResWidth * MaxHeight];
    private readonly bool[] rowHiRes = new bool[MaxHeight];

    /// <summary>True when at least one line of the last frame was drawn in a hi-res mode.</summary>
    public bool FrameHasHiRes { get; private set; }

    /// <summary>The last frame at 512 pixels per line (only meaningful when <see cref="FrameHasHiRes"/>).</summary>
    public uint[] GetHiResFrame()
    {
        for (int row = 0; row < VisibleHeight; row++)
        {
            if (rowHiRes[row]) continue;
            int src = row * Width, dst = row * HiResWidth;
            for (int x = 0; x < Width; x++) { uint c = FrameBuffer[src + x]; HiResBuffer[dst + 2 * x] = c; HiResBuffer[dst + 2 * x + 1] = c; }
        }
        return HiResBuffer;
    }

    public void BeginFrame()
    {
        if (FrameHasHiRes) { Array.Clear(rowHiRes); FrameHasHiRes = false; }
        VisibleHeight = (setini & 0x04) != 0 ? MaxHeight : Height;
        if (!ForcedBlank) { rangeOver = false; timeOver = false; }
        inDisplay = true;
    }

    /// <summary>Start of vblank: OAM address reload, VRAM opens up.</summary>
    public void OnVBlankStart()
    {
        if (!ForcedBlank) oamInternal = (ushort)((oamadd & 0x1FF) << 1);
        inDisplay = false;
    }

    // =====================================================================================
    //  Scanline renderer
    // =====================================================================================

    private const ushort Transparent = 0x8000;      // a real BGR555 color never sets bit 15
    private const int Backdrop = 5, ObjLayer = 4, AnyPriority = 2;

    // BG line buffers are 512 wide so hi-res modes can render at full resolution before averaging.
    private readonly ushort[][] bgColor = { new ushort[512], new ushort[512], new ushort[512], new ushort[512] };
    private readonly byte[][] bgPrio = { new byte[512], new byte[512], new byte[512], new byte[512] };
    private readonly ushort[] objColor = new ushort[256];
    private readonly byte[] objPrio = new byte[256];
    private readonly bool[] objMath = new bool[256];
    private readonly bool[][] window = { new bool[256], new bool[256], new bool[256], new bool[256], new bool[256], new bool[256] };

    // Front-to-back draw order per mode, as (layer, priority). Layers 0-3 = BG1-4, 4 = OBJ.
    private static readonly (byte layer, byte prio)[] Order0 =
        { (4, 3), (0, 1), (1, 1), (4, 2), (0, 0), (1, 0), (4, 1), (2, 1), (3, 1), (4, 0), (2, 0), (3, 0) };
    private static readonly (byte layer, byte prio)[] Order1 =
        { (4, 3), (0, 1), (1, 1), (4, 2), (0, 0), (1, 0), (4, 1), (2, 1), (4, 0), (2, 0) };
    private static readonly (byte layer, byte prio)[] Order1Bg3Top =
        { (2, 1), (4, 3), (0, 1), (1, 1), (4, 2), (0, 0), (1, 0), (4, 1), (4, 0), (2, 0) };
    private static readonly (byte layer, byte prio)[] Order2To5 =
        { (4, 3), (0, 1), (4, 2), (1, 1), (4, 1), (0, 0), (4, 0), (1, 0) };
    private static readonly (byte layer, byte prio)[] Order6 =
        { (4, 3), (0, 1), (4, 2), (4, 1), (0, 0), (4, 0) };
    private static readonly (byte layer, byte prio)[] Order7 =
        { (4, 3), (4, 2), (1, 1), (4, 1), (0, AnyPriority), (4, 0), (1, 0) };

    private static readonly int[][] ModeBpp =
    {
        new[] { 2, 2, 2, 2 }, new[] { 4, 4, 2, 0 }, new[] { 4, 4, 0, 0 }, new[] { 8, 4, 0, 0 },
        new[] { 8, 2, 0, 0 }, new[] { 4, 2, 0, 0 }, new[] { 4, 0, 0, 0 }, new[] { 8, 0, 0, 0 },
    };

    private static readonly (int w, int h)[] ObjSmall = { (8, 8), (8, 8), (8, 8), (16, 16), (16, 16), (32, 32), (16, 32), (16, 32) };
    private static readonly (int w, int h)[] ObjLarge = { (16, 16), (32, 32), (64, 64), (32, 32), (64, 64), (64, 64), (32, 64), (32, 32) };

    // Per-line compositing buffers (512 wide for hi-res). rank = position in the mode's front-to-back
    // order (lower wins, 0xFF = nothing drawn yet); layer 5 = backdrop.
    private readonly byte[] rankMain = new byte[512], rankSub = new byte[512];
    private readonly ushort[] colorMain = new ushort[512], colorSub = new ushort[512];
    private readonly byte[] layerMain = new byte[512], layerSub = new byte[512];
    private readonly byte[] rankTable = new byte[20];   // [layer * 4 + priority] -> rank
    private (byte layer, byte prio)[]? rankTableOrder;
    /// <summary>
    /// false = use only the straightforward reference paths (e.g. per-pixel BG decode). Every fast path
    /// must stay pixel-identical to its reference twin; `--snesbench --reference-paths` checks that.
    /// </summary>
    public static bool FastPaths = true;
    private readonly uint[]?[] brightnessLut = new uint[16][];

    /// <summary>Render one visible line (1 = first visible line) into FrameBuffer row line-1.</summary>
    /// <summary>Debug: when non-null, RenderLine records the register snapshot it saw for each line.</summary>
    public RegisterSnapshot[]? DebugLineRegisters { get; set; }

    /// <summary>
    /// What a cross-console picture bridge needs of one line beyond <see cref="RegisterSnapshot"/>: the windows, the fixed
    /// colour, the Mode 7 matrix and the backdrop colour (CGRAM 0), all as this line was drawn with them.
    /// </summary>
    public readonly record struct BridgeLineState(
        RegisterSnapshot Regs, byte W12sel, byte W34sel, byte Wobjsel, byte Wh0, byte Wh1, byte Wh2, byte Wh3,
        byte Wbglog, byte Wobjlog, ushort Coldata, short M7a, short M7b, short M7c, short M7d, short M7x, short M7y,
        short M7hofs, short M7vofs, ushort Backdrop, byte[]? Oam = null);

    /// <summary>
    /// Bridges (mix/): when non-null, RenderLine records every line's <see cref="BridgeLineState"/> (index = line, 1-based
    /// like RenderLine). Read-only capture - rendering is unchanged.
    /// </summary>
    public BridgeLineState[]? BridgeLines { get; set; }
    // OAM as the bridge lines saw it: copied only when OAM or OBSEL changed since the last captured line (games that
    // rewrite OAM mid-frame, like Super Mario Kart's split screen, get one copy per part).
    private byte[]? bridgeOam; private int bridgeOamVersion = -1;

    public void RenderLine(int line)
    {
        if (DebugLineRegisters != null && line >= 0 && line < DebugLineRegisters.Length) DebugLineRegisters[line] = GetRegisterSnapshot();
        if (BridgeLines != null && line >= 0 && line < BridgeLines.Length)
        {
            if (bridgeOam == null || bridgeOamVersion != oamVersion) { bridgeOam = (byte[])Oam.Clone(); bridgeOamVersion = oamVersion; }
            BridgeLines[line] = new(GetRegisterSnapshot(), w12sel, w34sel, wobjsel, wh0, wh1, wh2, wh3, wbglog, wobjlog, coldata,
                m7a, m7b, m7c, m7d, m7x, m7y, m7hofs, m7vofs, Cgram[0], bridgeOam);
        }
        if (DebugLayerMask != 0x1F)
        {
            // Diagnostic only: render with TM/TS masked, then restore the game's values.
            byte savedTm = tm, savedTs = ts;
            tm &= (byte)DebugLayerMask; ts &= (byte)DebugLayerMask;
            try { RenderLineCore(line); } finally { tm = savedTm; ts = savedTs; }
            return;
        }
        RenderLineCore(line);
    }

    private void RenderLineCore(int line)
    {
        int row = line - 1;
        if (row < 0 || row >= VisibleHeight) return;
        var dst = FrameBuffer.AsSpan(row * Width, Width);
        int brightness = inidisp & 0x0F;
        if (ForcedBlank || brightness == 0) { dst.Fill(0xFF000000u); return; }

        int mode = bgmode & 7;
        bool hires = mode is 5 or 6;
        // The sub screen is only ever looked at by color math in "add sub screen" mode, so layers that
        // appear only on it are skipped otherwise.
        bool subNeeded = (cgwsel & 0x02) != 0 && (cgadsub & 0x3F) != 0;
        bool colorWindowUsed = ((cgwsel >> 4) & 3) is 1 or 2 || ((cgwsel >> 6) & 3) is 1 or 2;
        byte used = (byte)(tm | (subNeeded ? ts : 0));
        int width = hires ? 512 : 256, shift = hires ? 1 : 0;

        BuildRankTable(mode switch
        {
            0 => Order0,
            1 => (bgmode & 0x08) != 0 ? Order1Bg3Top : Order1,
            6 => Order6,
            7 => Order7,
            _ => Order2To5,
        });
        ComputeWindows((byte)((tmw & tm) | (subNeeded ? tsw & ts : 0)), colorWindowUsed);
        ClearScreen(rankMain, colorMain, layerMain, width);
        if (subNeeded) ClearScreen(rankSub, colorSub, layerSub, width);

        // Layers compose in any order: the lowest rank wins, and ranks are unique per (layer, priority).
        for (int bg = 0; bg < 4; bg++)
        {
            int bit = 1 << bg;
            if ((used & bit) == 0 || mode == 7 || ModeBpp[mode][bg] == 0) continue;
            bool onMain = (tm & bit) != 0, onSub = subNeeded && (ts & bit) != 0;
            // Mosaic only matters when a layer's enable bit is set AND the size is above 1 (games such
            // as SMW leave the enable bits on with size 0 all the time).
            bool mosaicActive = (mosaic & bit) != 0 && (mosaic >> 4) != 0;
            bool fusable = FastPaths && !hires && !mosaicActive && mode is not (2 or 4 or 6);
            if (fusable) { RenderBgFused(bg, ModeBpp[mode][bg], mode, line, onMain, onSub); continue; }
            RenderBg(bg, ModeBpp[mode][bg], mode, line);
            ComposeLayer(bg, onMain, onSub, width, shift);
        }
        if (mode == 7 && (used & 3) != 0)
        {
            RenderMode7(line);
            ComposeLayer(0, (tm & 1) != 0, subNeeded && (ts & 1) != 0, width, shift);
            if ((setini & 0x40) != 0) ComposeLayer(1, (tm & 2) != 0, subNeeded && (ts & 2) != 0, width, shift);
        }
        if ((used & 0x10) != 0)
        {
            RenderSprites(line);
            if ((tm & 0x10) != 0) ComposeObj(tmw, width, shift, rankMain, colorMain, layerMain);
            if (subNeeded && (ts & 0x10) != 0) ComposeObj(tsw, width, shift, rankSub, colorSub, layerSub);
        }

        var lut = brightnessLut[brightness] ??= BuildBrightnessLut(brightness);
        if (FastPaths && !hires && (cgadsub & 0x3F) == 0 && (cgwsel & 0xC0) == 0)
        {
            // No color math and no clip-to-black on this line: the main screen is the output.
            for (int x = 0; x < Width; x++) dst[x] = lut[colorMain[x]];
            return;
        }
        if (FastPaths && !hires) { FinalLine(dst, lut, subNeeded, colorWindowUsed); return; }
        int hiRow = row * HiResWidth;
        if (hires) { rowHiRes[row] = true; FrameHasHiRes = true; }
        for (int x = 0; x < Width; x++)
        {
            ushort c;
            if (!hires) c = FinalPixel(x, x, subNeeded, colorWindowUsed);
            else
            {
                ushort a = FinalPixel(x * 2, x, subNeeded, colorWindowUsed), b = FinalPixel(x * 2 + 1, x, subNeeded, colorWindowUsed);
                HiResBuffer[hiRow + 2 * x] = lut[a];
                HiResBuffer[hiRow + 2 * x + 1] = lut[b];
                c = (ushort)((((a & 0x7BDE) + (b & 0x7BDE)) >> 1) + (a & b & 0x0421));   // per-channel average
            }
            dst[x] = lut[c];
        }
    }

    private void BuildRankTable((byte layer, byte prio)[] order)
    {
        if (ReferenceEquals(order, rankTableOrder)) return;
        Array.Fill(rankTable, (byte)0xFF);
        for (int i = 0; i < order.Length; i++)
        {
            var (layer, prio) = order[i];
            // AnyPriority is a BG-only marker (Mode 7 BG1); for sprites 2 is a real priority level.
            if (prio == AnyPriority && layer < ObjLayer) { rankTable[layer * 4] = (byte)i; rankTable[layer * 4 + 1] = (byte)i; }
            else rankTable[layer * 4 + prio] = (byte)i;
        }
        rankTableOrder = order;
    }

    private void ClearScreen(byte[] rank, ushort[] color, byte[] layerOut, int width)
    {
        rank.AsSpan(0, width).Fill(0xFF);
        color.AsSpan(0, width).Fill(Cgram[0]);
        layerOut.AsSpan(0, width).Fill(Backdrop);
    }

    /// <summary>
    /// Merge one rendered BG layer (bgColor/bgPrio) into the main and/or sub screen: its opaque,
    /// unwindowed pixels win wherever their (layer, priority) rank beats what is already there.
    /// Equivalent to walking the priority list per pixel and taking the first hit.
    /// </summary>
    private void ComposeLayer(int layer, bool onMain, bool onSub, int width, int shift)
    {
        if (onMain) ComposeBg(layer, (tmw & (1 << layer)) != 0, width, shift, rankMain, colorMain, layerMain);
        if (onSub) ComposeBg(layer, (tsw & (1 << layer)) != 0, width, shift, rankSub, colorSub, layerSub);
    }

    private void ComposeBg(int layer, bool windowed, int width, int shift, byte[] rank, ushort[] color, byte[] layerOut)
    {
        byte r0 = rankTable[layer * 4], r1 = rankTable[layer * 4 + 1];
        if (r0 == 0xFF && r1 == 0xFF) return;
        var src = bgColor[layer]; var prio = bgPrio[layer];
        var win = window[layer];
        for (int bx = 0; bx < width; bx++)
        {
            ushort v = src[bx];
            if (v == Transparent || (windowed && win[bx >> shift])) continue;
            byte r = prio[bx] != 0 ? r1 : r0;
            if (r < rank[bx]) { rank[bx] = r; color[bx] = v; layerOut[bx] = (byte)layer; }
        }
    }

    private void ComposeObj(byte windowEnable, int width, int shift, byte[] rank, ushort[] color, byte[] layerOut)
    {
        bool windowed = (windowEnable & 0x10) != 0;
        var win = window[ObjLayer];
        for (int bx = 0; bx < width; bx++)
        {
            int x = bx >> shift;
            ushort v = objColor[x];
            if (v == Transparent || (windowed && win[x])) continue;
            byte r = rankTable[16 + objPrio[x]];
            if (r < rank[bx]) { rank[bx] = r; color[bx] = v; layerOut[bx] = ObjLayer; }
        }
    }

    /// <summary>
    /// FinalPixel for a whole non-hi-res line with every register decision hoisted out of the loop.
    /// Identical results (the reference path still calls FinalPixel per pixel).
    /// </summary>
    private void FinalLine(Span<uint> dst, uint[] lut, bool subNeeded, bool colorWindowUsed)
    {
        var lm = layerMain; var cm = colorMain; var ls = layerSub; var cs = colorSub;
        var om = objMath; var cw = window[5];
        int blackMode = (cgwsel >> 6) & 3, mathMode = (cgwsel >> 4) & 3;
        int adsub = cgadsub;
        bool subtract = (adsub & 0x80) != 0, halfOn = (adsub & 0x40) != 0;
        ushort fixedColor = coldata;

        if (!colorWindowUsed)
        {
            // Without the color window, clip-to-black and the math region are all-or-nothing for
            // the line (blackMode/mathMode can only be 0 or 3 here).
            bool black = blackMode == 3;
            if (mathMode == 3) { for (int x = 0; x < Width; x++) dst[x] = lut[black ? (ushort)0 : cm[x]]; return; }
            bool halve0 = halfOn && !black;
            // Unchecked refs: x < 256 on arrays of >= 256; colors are 15-bit, lut has 32768 entries.
            ref byte lmR = ref MemoryMarshal.GetArrayDataReference(lm);
            ref ushort cmR = ref MemoryMarshal.GetArrayDataReference(cm);
            ref byte lsR = ref MemoryMarshal.GetArrayDataReference(ls);
            ref ushort csR = ref MemoryMarshal.GetArrayDataReference(cs);
            ref bool omR = ref MemoryMarshal.GetArrayDataReference(om);
            ref uint lutR = ref MemoryMarshal.GetArrayDataReference(lut);
            ref uint dstR = ref MemoryMarshal.GetReference(dst);
            for (int x = 0; x < Width; x++)
            {
                int layer = Unsafe.Add(ref lmR, x);
                ushort color = black ? (ushort)0 : Unsafe.Add(ref cmR, x);
                bool layerMath = layer == ObjLayer ? (adsub & 0x10) != 0 && Unsafe.Add(ref omR, x) : (adsub & (1 << layer)) != 0;
                if (layerMath)
                {
                    ushort addend = fixedColor;
                    bool halve = halve0;
                    if (subNeeded)
                    {
                        if (Unsafe.Add(ref lsR, x) == Backdrop) halve = false;
                        else addend = Unsafe.Add(ref csR, x);
                    }
                    color = BlendPacked(color, addend, subtract, halve);
                }
                Unsafe.Add(ref dstR, x) = Unsafe.Add(ref lutR, color);
            }
            return;
        }

        for (int x = 0; x < Width; x++)
        {
            int layer = lm[x];
            ushort color = cm[x];
            bool w = colorWindowUsed && cw[x];
            bool black = blackMode == 3 || (blackMode == 1 && !w) || (blackMode == 2 && w);
            if (black) color = 0;
            bool region = mathMode == 0 || (mathMode == 1 && w) || (mathMode == 2 && !w);
            bool layerMath = layer == ObjLayer ? (adsub & 0x10) != 0 && om[x] : (adsub & (1 << layer)) != 0;
            if (region && layerMath)
            {
                ushort addend = fixedColor;
                bool halve = halfOn && !black;
                if (subNeeded)
                {
                    if (ls[x] == Backdrop) halve = false;
                    else addend = cs[x];
                }
                color = BlendPacked(color, addend, subtract, halve);
            }
            dst[x] = lut[color];
        }
    }

    /// <summary>
    /// Blend() on all three channels at once. Red and blue share one int (bits 0-4 and 10-14, each
    /// with a free bit above it for the carry or borrow); green gets its own. Same results as Blend:
    /// add = halve then clamp to 31, subtract = clamp to 0 then halve.
    /// </summary>
    internal static ushort BlendPacked(ushort a, ushort b, bool subtract, bool halve)
    {
        int rb, g;
        if (!subtract)
        {
            rb = (a & 0x7C1F) + (b & 0x7C1F);   // R sum in bits 0-5, B sum in bits 10-15
            g = (a & 0x03E0) + (b & 0x03E0);    // G sum in bits 5-10
            if (halve) return (ushort)(((rb >> 1) & 0x7C1F) | ((g >> 1) & 0x03E0));
            rb |= ((rb >> 5) & 1) * 0x1F | ((rb >> 15) & 1) * 0x7C00;   // saturate overflowed channels
            g |= ((g >> 10) & 1) * 0x3E0;
            return (ushort)((rb & 0x7C1F) | (g & 0x03E0));
        }
        rb = ((a & 0x7C1F) | 0x8020) - (b & 0x7C1F);   // guard bits 5 and 15 survive unless a channel borrowed
        g = ((a & 0x03E0) | 0x0400) - (b & 0x03E0);
        rb &= ((rb >> 5) & 1) * 0x1F | ((rb >> 15) & 1) * 0x7C00;
        g &= ((g >> 10) & 1) * 0x3E0;
        if (halve) { rb = (rb >> 1) & 0x7C1F; g = (g >> 1) & 0x03E0; }
        return (ushort)(rb | g);
    }

    /// <summary>Clip-to-black and color math for one pixel, from the already-composed screens.</summary>
    private ushort FinalPixel(int bx, int x, bool subNeeded, bool colorWindowUsed)
    {
        int mainLayer = layerMain[bx];
        ushort color = colorMain[bx];
        bool colorWindow = colorWindowUsed && window[5][x];

        int blackMode = (cgwsel >> 6) & 3;
        bool black = blackMode == 3 || (blackMode == 1 && !colorWindow) || (blackMode == 2 && colorWindow);
        if (black) color = 0;

        int mathMode = (cgwsel >> 4) & 3;
        bool mathRegion = mathMode == 0 || (mathMode == 1 && colorWindow) || (mathMode == 2 && !colorWindow);
        bool layerMath = mainLayer == ObjLayer
            ? (cgadsub & 0x10) != 0 && objMath[x]
            : (cgadsub & (1 << mainLayer)) != 0;   // bit 5 = backdrop
        if (!mathRegion || !layerMath) return color;

        ushort addend = coldata;
        bool halve = (cgadsub & 0x40) != 0 && !black;
        if (subNeeded)
        {
            if (layerSub[bx] == Backdrop) halve = false;   // transparent sub screen: fixed color, no halving
            else addend = colorSub[bx];
        }
        return Blend(color, addend, (cgadsub & 0x80) != 0, halve);
    }

    private static uint[] BuildBrightnessLut(int brightness)
    {
        var lut = new uint[0x8000];
        for (int c = 0; c < lut.Length; c++) lut[c] = ToArgb((ushort)c, brightness);
        return lut;
    }

    internal static ushort Blend(ushort a, ushort b, bool subtract, bool halve)
    {
        int r, g, bl;
        if (!subtract)
        {
            r = (a & 0x1F) + (b & 0x1F); g = ((a >> 5) & 0x1F) + ((b >> 5) & 0x1F); bl = ((a >> 10) & 0x1F) + ((b >> 10) & 0x1F);
            if (halve) { r >>= 1; g >>= 1; bl >>= 1; }
            r = Math.Min(r, 31); g = Math.Min(g, 31); bl = Math.Min(bl, 31);
        }
        else
        {
            r = Math.Max((a & 0x1F) - (b & 0x1F), 0); g = Math.Max(((a >> 5) & 0x1F) - ((b >> 5) & 0x1F), 0); bl = Math.Max(((a >> 10) & 0x1F) - ((b >> 10) & 0x1F), 0);
            if (halve) { r >>= 1; g >>= 1; bl >>= 1; }
        }
        return (ushort)(r | g << 5 | bl << 10);
    }

    // ---- Backgrounds ----

    private ushort MapEntry(int mapBase, bool wide, bool tall, int col, int row)
    {
        int addr = mapBase + ((row & 31) << 5) + (col & 31);
        if (wide && (col & 32) != 0) addr += 0x400;
        if (tall && (row & 32) != 0) addr += wide ? 0x800 : 0x400;
        return Vram[addr & 0x7FFF];
    }

    private void RenderBg(int bg, int bpp, int mode, int line)
    {
        var colors = bgColor[bg];
        var prios = bgPrio[bg];
        int sc = bgsc[bg];
        int mapBase = (sc & 0xFC) << 8;
        bool wide = (sc & 1) != 0, tall = (sc & 2) != 0;
        bool hires = mode is 5 or 6;
        bool big = (bgmode & (0x10 << bg)) != 0;
        int tileWShift = big || hires ? 4 : 3, tileHShift = big ? 4 : 3;
        int mapWMask = ((wide ? 64 : 32) << tileWShift) - 1, mapHMask = ((tall ? 64 : 32) << tileHShift) - 1;
        int charBase = ((bg < 2 ? bg12nba >> (bg * 4) : bg34nba >> ((bg - 2) * 4)) & 0x0F) << 12;
        int wordsPerTile = bpp * 4;
        int paletteBase = mode == 0 ? bg * 32 : 0;
        int palShift = bpp;   // palette stride = 1 << bpp colors
        bool direct = bpp == 8 && (cgwsel & 0x01) != 0;
        bool opt = mode is 2 or 4 or 6;

        int mosaicSize = (mosaic & (1 << bg)) != 0 ? (mosaic >> 4) + 1 : 1;
        int y = mosaicSize > 1 ? line - (line - 1) % mosaicSize : line;
        int width = hires ? 512 : 256;

        // Reference path, one pixel at a time. Also the only path for mosaic, offset-per-tile and
        // hi-res; everything else normally takes RenderBgFused.
        int lastKey = -1; ushort entry = 0;
        for (int sx = 0; sx < width; sx++)
        {
            int x = mosaicSize > 1 ? sx - sx % (hires ? mosaicSize * 2 : mosaicSize) : sx;
            int hs = hofs[bg], vs = vofs[bg];
            if (opt && bg < 2) ApplyOffsetPerTile(bg, mode, hires ? x >> 1 : x, ref hs, ref vs);
            int bx = (hires ? x + (hs << 1) : x + hs) & mapWMask;
            int by = (y + vs) & mapHMask;

            int col = bx >> tileWShift, rowT = by >> tileHShift;
            int key = rowT << 8 | col;
            if (key != lastKey) { entry = MapEntry(mapBase, wide, tall, col, rowT); lastKey = key; }

            int px = bx & ((1 << tileWShift) - 1), py = by & ((1 << tileHShift) - 1);
            if ((entry & 0x4000) != 0) px = (1 << tileWShift) - 1 - px;
            if ((entry & 0x8000) != 0) py = (1 << tileHShift) - 1 - py;
            int tile = entry & 0x3FF;
            if (px >= 8) tile += 1;
            if (py >= 8) tile += 16;
            int addr = charBase + (tile & 0x3FF) * wordsPerTile + (py & 7);
            int bit = 7 - (px & 7), c = 0;
            for (int plane = 0; plane < bpp; plane += 2)
            {
                ushort w = Vram[(addr + plane * 4) & 0x7FFF];
                c |= ((w >> bit) & 1) << plane | ((w >> (8 + bit)) & 1) << (plane + 1);
            }
            if (c == 0) { colors[sx] = Transparent; continue; }
            int pal = (entry >> 10) & 7;
            colors[sx] = direct ? DirectColor(c, pal) : Cgram[(paletteBase + (bpp == 8 ? 0 : pal << palShift) + c) & 0xFF];
            prios[sx] = (byte)((entry >> 13) & 1);
        }
    }

    // Bitplane byte -> 8 byte lanes (lane i = pixel i, left to right) holding that plane's bit.
    // Shifting lanes by the plane number and OR-ing planes together yields 8 color indices at once.
    private static readonly ulong[] PlaneExpand = BuildPlaneExpand(flipped: false);
    private static readonly ulong[] PlaneExpandFlipped = BuildPlaneExpand(flipped: true);

    private static ulong[] BuildPlaneExpand(bool flipped)
    {
        var t = new ulong[256];
        for (int b = 0; b < 256; b++)
            for (int i = 0; i < 8; i++)
                if ((b >> (flipped ? i : 7 - i) & 1) != 0) t[b] |= 1UL << (i * 8);
        return t;
    }

    /// <summary>
    /// Fast path for the common case (no mosaic, no offset-per-tile, not hi-res): fetch the tilemap
    /// entry and the tile row's bitplanes once per 8-pixel sliver, and compose straight into the
    /// main/sub rank buffers instead of going through bgColor + ComposeBg. Fully transparent slivers
    /// cost one test. Pixel-for-pixel identical to RenderBg + ComposeLayer (--reference-paths checks).
    /// </summary>
    private void RenderBgFused(int bg, int bpp, int mode, int line, bool onMain, bool onSub)
    {
        byte r0 = rankTable[bg * 4], r1 = rankTable[bg * 4 + 1];
        if (r0 == 0xFF && r1 == 0xFF) return;
        int sc = bgsc[bg];
        int mapBase = (sc & 0xFC) << 8;
        bool wide = (sc & 1) != 0, tall = (sc & 2) != 0;
        bool big = (bgmode & (0x10 << bg)) != 0;
        int tileWShift = big ? 4 : 3, tileHShift = big ? 4 : 3;
        int mapWMask = ((wide ? 64 : 32) << tileWShift) - 1, mapHMask = ((tall ? 64 : 32) << tileHShift) - 1;
        int charBase = ((bg < 2 ? bg12nba >> (bg * 4) : bg34nba >> ((bg - 2) * 4)) & 0x0F) << 12;
        int wordsPerTile = bpp * 4;
        int paletteBase = mode == 0 ? bg * 32 : 0;
        int palShift = bpp;
        bool direct = bpp == 8 && (cgwsel & 0x01) != 0;
        bool winMain = onMain && (tmw & (1 << bg)) != 0, winSub = onSub && (tsw & (1 << bg)) != 0;
        // Bounds-check-free base refs. Every index below is provably in range: VRAM indices are
        // masked with 0x7FFF (32K words), palette indices with 0xFF (256 entries), expansion-table
        // indices are bytes, and pixel x < 256 on arrays of 256 (window) or 512 (screens).
        ref ushort vram = ref MemoryMarshal.GetArrayDataReference(Vram);
        ref ushort cgram = ref MemoryMarshal.GetArrayDataReference(Cgram);
        ref bool win = ref MemoryMarshal.GetArrayDataReference(window[bg]);
        ref byte rankM = ref MemoryMarshal.GetArrayDataReference(rankMain);
        ref ushort colorM = ref MemoryMarshal.GetArrayDataReference(colorMain);
        ref byte layerM = ref MemoryMarshal.GetArrayDataReference(layerMain);
        ref byte rankS = ref MemoryMarshal.GetArrayDataReference(rankSub);
        ref ushort colorS = ref MemoryMarshal.GetArrayDataReference(colorSub);
        ref byte layerS = ref MemoryMarshal.GetArrayDataReference(layerSub);
        ref ulong expandN = ref MemoryMarshal.GetArrayDataReference(PlaneExpand);
        ref ulong expandF = ref MemoryMarshal.GetArrayDataReference(PlaneExpandFlipped);

        int hs = hofs[bg];
        int by = (line + vofs[bg]) & mapHMask;
        int rowT = by >> tileHShift, pyRaw = by & ((1 << tileHShift) - 1);
        int tileWMask = (1 << tileWShift) - 1;

        int sx = 0;
        while (sx < 256)
        {
            int bx = (sx + hs) & mapWMask;
            ushort entry = MapEntry(mapBase, wide, tall, bx >> tileWShift, rowT);
            bool hflip = (entry & 0x4000) != 0;
            int py = (entry & 0x8000) != 0 ? (1 << tileHShift) - 1 - pyRaw : pyRaw;
            int pxRaw = bx & tileWMask;
            int half = pxRaw >> 3;
            if (hflip && tileWShift == 4) half ^= 1;
            int tile = ((entry & 0x3FF) + half + (py >= 8 ? 16 : 0)) & 0x3FF;
            int addr = charBase + tile * wordsPerTile + (py & 7);

            // All 8 pixels of the row at once: byte lane i of `pixels` = color index of pixel i.
            ref ulong expand = ref hflip ? ref expandF : ref expandN;
            int p0 = Unsafe.Add(ref vram, addr & 0x7FFF);
            ulong pixels = Unsafe.Add(ref expand, p0 & 0xFF) | Unsafe.Add(ref expand, p0 >> 8) << 1;
            if (bpp >= 4)
            {
                int p1 = Unsafe.Add(ref vram, (addr + 8) & 0x7FFF);
                pixels |= Unsafe.Add(ref expand, p1 & 0xFF) << 2 | Unsafe.Add(ref expand, p1 >> 8) << 3;
                if (bpp == 8)
                {
                    int p2 = Unsafe.Add(ref vram, (addr + 16) & 0x7FFF), p3 = Unsafe.Add(ref vram, (addr + 24) & 0x7FFF);
                    pixels |= Unsafe.Add(ref expand, p2 & 0xFF) << 4 | Unsafe.Add(ref expand, p2 >> 8) << 5
                            | Unsafe.Add(ref expand, p3 & 0xFF) << 6 | Unsafe.Add(ref expand, p3 >> 8) << 7;
                }
            }

            int start = pxRaw & 7, count = Math.Min(8 - start, 256 - sx);
            int pal = (entry >> 10) & 7;
            int palOffset = paletteBase + (bpp == 8 ? 0 : pal << palShift);
            byte r = (entry & 0x2000) != 0 ? r1 : r0;
            if (pixels == 0 || r == 0xFF) { sx += count; continue; }

            // Visit only the opaque pixels inside [start, start+count): mask the lanes, then walk the
            // non-zero ones by trailing-zero count. Order doesn't matter: each pixel is independent.
            ulong laneMask = count == 8 ? ulong.MaxValue : ((1UL << (count << 3)) - 1) << (start << 3);
            ulong opaque = pixels & laneMask;
            int xBase = sx - start;   // x of lane 0
            while (opaque != 0)
            {
                int lane = BitOperations.TrailingZeroCount(opaque) >> 3;
                int c = (int)(opaque >> (lane << 3)) & 0xFF;
                opaque &= ~(0xFFUL << (lane << 3));
                int x = xBase + lane;
                ushort v = direct ? DirectColor(c, pal) : Unsafe.Add(ref cgram, (palOffset + c) & 0xFF);
                if (onMain && r < Unsafe.Add(ref rankM, x) && !(winMain && Unsafe.Add(ref win, x)))
                {
                    Unsafe.Add(ref rankM, x) = r; Unsafe.Add(ref colorM, x) = v; Unsafe.Add(ref layerM, x) = (byte)bg;
                }
                if (onSub && r < Unsafe.Add(ref rankS, x) && !(winSub && Unsafe.Add(ref win, x)))
                {
                    Unsafe.Add(ref rankS, x) = r; Unsafe.Add(ref colorS, x) = v; Unsafe.Add(ref layerS, x) = (byte)bg;
                }
            }
            sx += count;
        }
    }

    /// <summary>Modes 2/4/6: BG3's tilemap supplies per-column scroll overrides for BG1/BG2.</summary>
    private void ApplyOffsetPerTile(int bg, int mode, int screenX, ref int hs, ref int vs)
    {
        int column = (screenX + (hofs[bg] & 7)) >> 3;
        if (column == 0) return;   // the leftmost column is never affected
        int sc = bgsc[2];
        int mapBase = (sc & 0xFC) << 8;
        bool wide = (sc & 1) != 0, tall = (sc & 2) != 0;
        int col = ((hofs[2] >> 3) + column - 1) & (wide ? 63 : 31);
        int rowT = (vofs[2] >> 3) & (tall ? 63 : 31);
        ushort enableBit = (ushort)(0x2000 << bg);
        ushort h = MapEntry(mapBase, wide, tall, col, rowT);
        if (mode == 4)
        {
            if ((h & enableBit) == 0) return;
            if ((h & 0x8000) != 0) vs = h & 0x3FF; else hs = (h & 0x3F8) | (hs & 7);
            return;
        }
        ushort v = MapEntry(mapBase, wide, tall, col, rowT + 1);
        if ((h & enableBit) != 0) hs = (h & 0x3F8) | (hs & 7);
        if ((v & enableBit) != 0) vs = v & 0x3FF;
    }

    private static ushort DirectColor(int c, int pal) =>
        (ushort)(((c & 7) << 2 | (pal & 1) << 1) | (((c >> 3) & 7) << 2 | (pal & 2)) << 5 | (((c >> 6) & 3) << 3 | (pal & 4)) << 10);

    private static int Clip13(int n) => (n & 0x2000) != 0 ? (n | ~0x3FF) : (n & 0x3FF);

    private void RenderMode7(int line)
    {
        var c1 = bgColor[0]; var c2 = bgColor[1]; var p2 = bgPrio[1];
        bool extbg = (setini & 0x40) != 0;
        int a = m7a, b = m7b, c = m7c, d = m7d, x0 = m7x, y0 = m7y;
        int mosaicSize = (mosaic & 1) != 0 ? (mosaic >> 4) + 1 : 1;
        int y = mosaicSize > 1 ? line - (line - 1) % mosaicSize : line;
        if ((m7sel & 0x02) != 0) y = 255 - y;
        int hc = Clip13(m7hofs - x0), vc = Clip13(m7vofs - y0);
        int psx = ((a * hc) & ~63) + ((b * vc) & ~63) + ((b * y) & ~63) + (x0 << 8);
        int psy = ((c * hc) & ~63) + ((d * vc) & ~63) + ((d * y) & ~63) + (y0 << 8);
        int outside = m7sel >> 6;
        bool direct = (cgwsel & 0x01) != 0;

        for (int sx = 0; sx < 256; sx++)
        {
            int x = mosaicSize > 1 ? sx - sx % mosaicSize : sx;
            int xx = (m7sel & 0x01) != 0 ? 255 - x : x;
            int px = (psx + a * xx) >> 8, py = (psy + c * xx) >> 8;
            int tile;
            if (((px | py) & ~0x3FF) != 0 && outside >= 2)
            {
                if (outside == 2) { c1[sx] = Transparent; c2[sx] = Transparent; continue; }
                tile = 0;
            }
            else tile = Vram[(((py >> 3) & 127) << 7) | ((px >> 3) & 127)] & 0xFF;
            int color = Vram[(tile << 6 | (py & 7) << 3 | (px & 7)) & 0x7FFF] >> 8;
            c1[sx] = color == 0 ? Transparent : direct ? DirectColor(color, 0) : Cgram[color];
            if (extbg)
            {
                int c7 = color & 0x7F;
                c2[sx] = c7 == 0 ? Transparent : Cgram[c7];
                p2[sx] = (byte)(color >> 7);
            }
        }
    }

    // ---- Sprites ----

    // Decoded per-sprite attributes for the fast evaluation loop. oamVersion bumps on every OAM or
    // OBSEL write; external writers to Oam (save states, debug tools) must call InvalidateCaches().
    private int oamVersion, spriteCacheVersion = -1;
    private readonly byte[] spriteY = new byte[128], spriteH = new byte[128];
    private readonly bool[] spriteXVisible = new bool[128];

    /// <summary>Call after modifying Oam/Vram/registers directly (not through WriteRegister).</summary>
    public void InvalidateCaches() { oamVersion++; Array.Fill(windowSignature, -1); }

    private void RefreshSpriteCache()
    {
        int sizeSel = obsel >> 5;
        for (int n = 0; n < 128; n++)
        {
            int hi = (Oam[0x200 + (n >> 2)] >> ((n & 3) * 2)) & 3;
            var (w, h) = (hi & 2) != 0 ? ObjLarge[sizeSel] : ObjSmall[sizeSel];
            int x = Oam[n * 4] | (hi & 1) << 8;
            if (x >= 256) x -= 512;
            spriteXVisible[n] = !(x <= -w && x != -256);
            spriteY[n] = Oam[n * 4 + 1];
            spriteH[n] = (byte)h;
        }
        spriteCacheVersion = oamVersion;
    }

    private void RenderSprites(int line)
    {
        objColor.AsSpan().Fill(Transparent);
        int sizeSel = obsel >> 5;
        int nameBase = (obsel & 7) << 13;
        int nameGap = (((obsel >> 3) & 3) + 1) << 12;
        int first = (oamadd & 0x8000) != 0 ? (oamadd >> 1) & 0x7F : 0;   // OAM priority rotation
        int evalY = line - 1;   // sprites are evaluated one line ahead: OAM Y=0 shows on line 1

        Span<int> list = stackalloc int[32];
        int count = 0;
        if (FastPaths)
        {
            // Same test as the reference loop below, on attributes decoded once per OAM/OBSEL change.
            if (spriteCacheVersion != oamVersion) RefreshSpriteCache();
            var sy = spriteY; var sh = spriteH; var sxv = spriteXVisible;
            for (int i = 0; i < 128; i++)
            {
                int n = (first + i) & 127;
                if (((evalY - sy[n]) & 0xFF) >= sh[n] || !sxv[n]) continue;
                if (count == 32) { rangeOver = true; break; }
                list[count++] = n;
            }
        }
        else
        {
            for (int i = 0; i < 128; i++)
            {
                int n = (first + i) & 127;
                int hi = (Oam[0x200 + (n >> 2)] >> ((n & 3) * 2)) & 3;
                var (w, h) = (hi & 2) != 0 ? ObjLarge[sizeSel] : ObjSmall[sizeSel];
                int x = Oam[n * 4] | (hi & 1) << 8;
                if (x >= 256) x -= 512;
                if (x <= -w && x != -256) continue;
                if (((evalY - Oam[n * 4 + 1]) & 0xFF) >= h) continue;
                if (count == 32) { rangeOver = true; break; }
                list[count++] = n;
            }
        }

        // Tiles are fetched last-sprite-first, 34 per line at most, so on overflow it is the
        // highest-priority sprites that lose slivers. Drawing in that order also lets earlier
        // sprites overwrite later ones, which is the SNES rule (OAM order beats priority bits).
        int slivers = 0;
        for (int k = count - 1; k >= 0; k--)
        {
            int n = list[k];
            int hi = (Oam[0x200 + (n >> 2)] >> ((n & 3) * 2)) & 3;
            var (w, h) = (hi & 2) != 0 ? ObjLarge[sizeSel] : ObjSmall[sizeSel];
            int x = Oam[n * 4] | (hi & 1) << 8;
            if (x >= 256) x -= 512;
            int tileLo = Oam[n * 4 + 2], attr = Oam[n * 4 + 3];
            bool hflip = (attr & 0x40) != 0, vflip = (attr & 0x80) != 0;
            int pal = (attr >> 1) & 7, prio = (attr >> 4) & 3;
            int r = (evalY - Oam[n * 4 + 1]) & 0xFF;
            if (vflip) r = h - 1 - r;
            int ty = r >> 3, fy = r & 7;
            int table = (attr & 1) != 0 ? nameBase + nameGap : nameBase;
            int tilesWide = w >> 3;
            for (int tx = 0; tx < tilesWide; tx++)
            {
                int sx = x + tx * 8;
                if (sx <= -8 || sx >= 256) continue;
                if (++slivers > 34) { timeOver = true; return; }
                int ctx = hflip ? tilesWide - 1 - tx : tx;
                int chr = (((tileLo >> 4) + ty) & 0x0F) << 4 | ((tileLo + ctx) & 0x0F);
                int addr = (table + chr * 16 + fy) & 0x7FFF;
                ushort p01 = Vram[addr], p23 = Vram[(addr + 8) & 0x7FFF];
                if (FastPaths)
                {
                    var expand = hflip ? PlaneExpandFlipped : PlaneExpand;
                    ulong pixels = expand[p01 & 0xFF] | expand[p01 >> 8] << 1 | expand[p23 & 0xFF] << 2 | expand[p23 >> 8] << 3;
                    if (pixels == 0) continue;
                    int start = sx < 0 ? -sx : 0, end = sx > 248 ? 256 - sx : 8;
                    for (int i = start; i < end; i++)
                    {
                        int c = (int)(pixels >> (i << 3)) & 0xFF;
                        if (c == 0) continue;
                        objColor[sx + i] = Cgram[128 + (pal << 4) + c];
                        objPrio[sx + i] = (byte)prio;
                        objMath[sx + i] = pal >= 4;
                    }
                    continue;
                }
                for (int i = 0; i < 8; i++)
                {
                    int px = sx + i;
                    if ((uint)px >= 256) continue;
                    int bit = hflip ? i : 7 - i;
                    int c = (p01 >> bit & 1) | (p01 >> (8 + bit) & 1) << 1 | (p23 >> bit & 1) << 2 | (p23 >> (8 + bit) & 1) << 3;
                    if (c == 0) continue;
                    objColor[px] = Cgram[128 + (pal << 4) + c];
                    objPrio[px] = (byte)prio;
                    objMath[px] = pal >= 4;   // only palettes 4-7 take part in color math
                }
            }
        }
    }

    // ---- Windows ----

    private readonly long[] windowSignature = { -1, -1, -1, -1, -1, -1 };

    /// <summary>Window masks, only for the layers something will actually read this line.</summary>
    private void ComputeWindows(byte layersNeeded, bool colorWindowNeeded)
    {
        for (int layer = 0; layer < 6; layer++)
        {
            if (layer < 5 ? (layersNeeded & (1 << layer)) == 0 : !colorWindowNeeded) continue;
            int sel = layer switch
            {
                0 => w12sel & 0x0F, 1 => w12sel >> 4, 2 => w34sel & 0x0F, 3 => w34sel >> 4,
                4 => wobjsel & 0x0F, _ => wobjsel >> 4,
            };
            int logic = layer < 4 ? (wbglog >> (layer * 2)) & 3 : (wobjlog >> ((layer - 4) * 2)) & 3;
            // Window settings rarely change between lines; rebuild a mask only when its inputs did.
            long signature = sel | logic << 4 | wh0 << 8 | wh1 << 16 | (long)wh2 << 24 | (long)wh3 << 32;
            if (FastPaths && windowSignature[layer] == signature) continue;
            windowSignature[layer] = signature;
            var mask = window[layer];
            bool en1 = (sel & 0x02) != 0, en2 = (sel & 0x08) != 0;
            if (!en1 && !en2) { mask.AsSpan().Clear(); continue; }
            bool inv1 = (sel & 0x01) != 0, inv2 = (sel & 0x04) != 0;
            for (int x = 0; x < 256; x++)
            {
                bool w1 = (x >= wh0 && x <= wh1) ^ inv1;
                bool w2 = (x >= wh2 && x <= wh3) ^ inv2;
                mask[x] = !en2 ? w1 : !en1 ? w2 : logic switch
                {
                    0 => w1 | w2,
                    1 => w1 & w2,
                    2 => w1 ^ w2,
                    _ => !(w1 ^ w2),
                };
            }
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
