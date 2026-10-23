using System;
using System.Collections.Generic;
using System.Linq;
using NesEmulator.Mix;
using NesEmulator.Gb;

namespace NesEmulator;

/// <summary>
/// MIX LAB: the Game Boy picture chip, STOCK (the real PPU_GB, DMG or Game Boy Color, <see cref="MixConfig.GbPpuModel"/>) drawing a NES game -
/// and, through the NES picture bridges, a SNES, Master System, Game Gear or Genesis game. Discovered by CoreRegistry as PPU id "DMG";
/// <see cref="PPU_DMGS"/> is the same bridge on the enhanced chip (PPU_GBE).
///
/// A NES PPU core (MixConfig.NesFrontPpu, IPpuProbe - or any core through the PpuSharedState rescue) stays in front
/// for register semantics, timing, NMI and sprite-0 hit. Each frame the bridge records every NES line's scroll, then
/// builds Game Boy VRAM: NES background tiles -> the $8800 tile block (signed ids), the used sprite tiles -> $8000,
/// the 2x2 nametables -> the 32x32 Game Boy map row by row, and has the Game Boy chip draw a real frame, setting SCX/SCY
/// as each Game Boy line starts. The stock chip sees the middle of the NES screen: 160x144 of it, from NES (48,48).
///
/// Colours (the picture is upright on every path: bright is light, dark is dark, see <see cref="GbColour.ShadeOf"/>):
/// <list type="bullet">
/// <item>DMG: every NES colour becomes the Game Boy shade of its own brightness (as the PPU_FIX family draws it). The Game Boy has one background
/// palette (BGP) and two sprite palettes (OBP0/1) of 3 shades + colour 0: each frame the bridge picks the shades they hold from the shades the
/// picture uses (the least used one gives way when all four are), recodes every tile's colours to them (colour 0 stays colour 0: the backdrop,
/// and the see-through of sprites and of the background under "behind" sprites), and puts each NES sprite palette on whichever of OBP0 / OBP1
/// fits it. Before 2026-10-22 the colour index was the shade (BGP = E4), which drew the SNES and Sega bridges' pictures - whose colour indices run
/// dark to bright - inverted.</item>
/// <item>Game Boy Color: the NES palettes become CGB palettes, attributes per tile, the colours themselves.</item>
/// <item>The Game Boy look's inversions (<see cref="GbLook"/>): background (the backdrop included) and sprites apart, a shade s becomes 3 - s, a
/// CGB colour has its lightness mirrored (hue kept). The bezel around the stock chip's window never flips.</item>
/// </list>
/// 40 sprites, 10 per line (NES: 64 and 8).
/// </summary>
public class PPU_DMG : IPPU, IPpuProbe
{
    private readonly Bus bus;
    private readonly IPPU front;
    private readonly IPpuProbe? probe;
    private readonly IPpuFrameClock? clock;
    private readonly GbScreen gb;
    /// <summary>The Game Boy screen size (160x144 on the stock chip, bigger on the enhanced chip).</summary>
    private readonly int W, H;
    private readonly bool cgb;
    private readonly byte[] frame = new byte[256 * 240 * 4];
    private byte ctrl, oamAddr;
    private readonly byte[] oam = new byte[256];
    private readonly int[] lineX = new int[240], lineY = new int[240];
    private readonly byte[] lineMask = new byte[240];
    private readonly byte[] chr = new byte[0x2000], nt = new byte[0x1000], pal = new byte[32], frameOam = new byte[256];
    private byte frameCtrl;
    private int lastLine = -1;
    private readonly Func<ushort, byte> busRead;
    /// <summary>Top-left of the W x H window the Game Boy sees of the 256x240 NES screen (MixConfig.GbCrop).</summary>
    private readonly int cx, cy;
    private PpuSharedState? st;

    public PPU_DMG(Bus bus) : this(bus, PPU_GB.Width, PPU_GB.Height) { }

    protected PPU_DMG(Bus bus, int gbWidth, int gbHeight, string? frontId = null, bool enhanced = false)
    {
        this.bus = bus;
        W = gbWidth; H = gbHeight;
        frontId ??= MixConfig.NesFrontPpu;
        var t = CoreRegistry.PpuTypes.TryGetValue(frontId, out var ft) ? ft : throw new ArgumentException($"No NES PPU '{frontId}'");
        front = CoreRegistry.CreateInstance<IPPU>(t, bus) ?? throw new InvalidOperationException("front PPU");
        probe = front as IPpuProbe;
        clock = front as IPpuFrameClock;
        if (probe == null && (!MixConfig.RescueFront || clock == null))
            throw new NotSupportedException($"NES PPU {frontId} has no IPpuProbe - it cannot front a Game Boy PPU");
        busRead = probe != null ? probe.ProbePpuBusRead : StateRead;
        cx = Math.Clamp(MixConfig.GbCropX, 0, 256 - W); cy = Math.Clamp(MixConfig.GbCropY, 0, 240 - H);
        // Game Boy Color: asked for directly (MixConfig), or by the Game Boy look.
        cgb = MixConfig.GbPpuModel.Equals("cgb", StringComparison.OrdinalIgnoreCase) || GbLook.Color;
        gb = GbScreen.Create(cgb ? GbModel.Cgb : GbModel.Dmg, W, H, enhanced);
        gb.LineStarted += OnGbLine;
        rowChrs = new byte[Math.Max(1, H / 8)][];
        for (int i = 0; i < rowChrs.Length; i++) rowChrs[i] = new byte[0x2000];
    }

    /// <summary>The NES front chip when it is a PPU_FIXS, whose cross-console layers the enhanced chip carries over.</summary>
    public PPU_FIXS? LayerFront => front as PPU_FIXS;

    public string CoreName => $"DMG{(gb.Enhanced != null ? $"S {W}x{H}" : "")}:{(cgb ? "CGB" : "DMG")}+{front.CoreName}";
    public string Description => gb.Enhanced != null ? "MIX LAB: the enhanced Game Boy picture chip drawing a NES game whole (and the cross-console layers)" : "MIX LAB: a Game Boy PPU drawing the middle of a NES game";
    public int Performance => 0;
    public int Rating => 1;
    public string Category => "Experimental";

    // ---------------------------------------------------------------- the NES-facing surface (same as PPU_SNES)
    private byte StateRead(ushort a)
    {
        a &= 0x3FFF;
        if (a < 0x2000) return bus.cartridge.PPURead(a);
        if (a < 0x3F00)
        {
            int off = a & 0x0FFF, n = off / 0x400, inner = off % 0x400;
            int m = bus.cartridge.mirroringMode switch
            {
                Mirroring.Vertical => (n % 2) * 0x400 + inner,
                Mirroring.Horizontal => (n / 2) * 0x400 + inner,
                Mirroring.SingleScreenA => inner,
                Mirroring.SingleScreenB => 0x400 + inner,
                _ => off & 0x7FF,
            };
            return st!.vram[m & 0x7FF];
        }
        int p = a & 0x1F; if (p >= 0x10 && (p & 3) == 0) p -= 0x10;
        return st!.palette[p];
    }

    public void Step(int cycles)
    {
        if (probe == null) { StepByState(cycles); return; }
        while (cycles > 0)
        {
            int dot = probe.ProbeDot;
            int chunk = dot < 320 ? Math.Min(cycles, 320 - dot) : Math.Min(cycles, 341 - dot);
            if (chunk <= 0) chunk = 1;
            front.Step(chunk);
            cycles -= chunk;
            if (probe.ProbeDot == 320) OnDot320(probe.ProbeScanline);
            int line = probe.ProbeScanline;
            if (line == 240 && lastLine != 240) PresentFrame();
            lastLine = line;
        }
    }

    private void StepByState(int cycles)
    {
        while (cycles > 0)
        {
            int chunk = Math.Min(cycles, 8);
            front.Step(chunk); cycles -= chunk;
            int line = clock!.ProbeScanline;
            if (line == lastLine) continue;
            lastLine = line;
            if (line == 240) { PresentFrame(); continue; }
            if (line > 239) continue;
            st = front.GetState() as PpuSharedState;
            if (st == null) continue;
            if (line == 0) { ctrl = st.PPUCTRL; Array.Copy(st.oam, oam, 256); Snapshot(); }
            if (line == cy + H / 2) SnapshotChr();
            if (line >= cy && line < cy + H && ((line - cy) & 7) == 0) SnapshotRow((line - cy) >> 3);
            int v = st.v, t = st.t, fx = st.fineX;
            lineX[line] = ((t >> 10) & 1) * 256 + (t & 31) * 8 + fx;
            lineY[line] = ((v >> 11) & 1) * 256 + ((v >> 5) & 31) * 8 + ((v >> 12) & 7);
            lineMask[line] = st.PPUMASK;
        }
    }

    public byte ReadPPURegister(ushort address) => front.ReadPPURegister(address);
    public void WritePPURegister(ushort address, byte value)
    {
        switch (address & 7)
        {
            case 0: ctrl = value; break;
            case 3: oamAddr = value; break;
            case 4: oam[oamAddr++] = value; break;
        }
        front.WritePPURegister(address, value);
    }
    public void WriteOAMDMA(byte page)
    {
        for (int i = 0; i < 256; i++) oam[(oamAddr + i) & 0xFF] = bus.Read((ushort)((page << 8) | i));
        front.WriteOAMDMA(page);
    }
    public byte[] GetFrameBuffer() => frame;
    public void UpdateFrameBuffer() { }
    public object GetState() => front.GetState();
    public void SetState(object state) => front.SetState(state);
    public void GenerateStaticFrame() => front.GenerateStaticFrame();
    public void ClearBuffers() { front.ClearBuffers(); Array.Clear(frame); }
    public int ProbeScanline => clock?.ProbeScanline ?? probe!.ProbeScanline;
    public int ProbeDot => probe?.ProbeDot ?? 0;
    public byte ProbeMask => probe?.ProbeMask ?? 0;
    public byte ProbePpuBusRead(ushort address) => busRead(address);
    public void ProbePpuBusWrite(ushort address, byte value) => probe?.ProbePpuBusWrite(address, value);

    private void OnDot320(int line)
    {
        int next = line == 261 ? 0 : line + 1;
        if (line != 261 && line >= 239) return;
        if (next == 0) Snapshot();
        if (next == cy + H / 2) SnapshotChr();   // sprite tiles as the middle of the Game Boy window is drawn
        if (next >= cy && next < cy + H && ((next - cy) & 7) == 0) SnapshotRow((next - cy) >> 3);
        ushort v = front is IPpuFixTiming f ? f.ProbeV : (ushort)0; int fx = front is IPpuFixTiming f2 ? f2.ProbeFineX : 0;
        lineX[next] = ((v >> 10) & 1) * 256 + (v & 31) * 8 + fx;
        lineY[next] = ((v >> 11) & 1) * 256 + ((v >> 5) & 31) * 8 + ((v >> 12) & 7);
        lineMask[next] = probe!.ProbeMask;
    }

    /// <summary>Pattern tables, nametables, palette and OAM as the frame starts (mid-frame CHR switches are not followed).</summary>
    private void Snapshot()
    {
        SnapshotChr();
        for (int i = 0; i < 0x1000; i++) nt[i] = busRead((ushort)(0x2000 + i));
        for (int i = 0; i < 32; i++) pal[i] = busRead((ushort)(0x3F00 + i));
        Array.Copy(oam, frameOam, 256);
        frameCtrl = ctrl;
    }

    private void SnapshotChr() { for (int i = 0; i < 0x2000; i++) chr[i] = busRead((ushort)i); }
    private readonly byte[][] rowChrs;
    /// <summary>Distinct background tile patterns the last frame needed (over 256 = some cells show wrong tiles).</summary>
    public int BgSlotsUsed;
    private void SnapshotRow(int group) { if (group < rowChrs.Length) { var dst = rowChrs[group]; for (int i = 0; i < 0x2000; i++) dst[i] = busRead((ushort)i); } }

    // ---------------------------------------------------------------- the translation
    private int NesTile(int X, int Y, out int palette)
    {
        int ntx = (X >> 8) & 1, nty = (Y >> 8) & 1, col = (X & 255) >> 3, row = Math.Min((Y & 255) >> 3, 29);
        int baseAddr = (ntx + nty * 2) * 0x400;
        int attr = nt[baseAddr + 0x3C0 + (row >> 2) * 8 + (col >> 2)];
        palette = (attr >> (((row & 2) << 1) | (col & 2))) & 3;
        return nt[baseAddr + row * 32 + col];
    }

    // The frame's working memory, made once (the tables are cleared for each frame).
    private readonly int[] cellGroup = new int[32 * 32], cellAddr = new int[32 * 32], cellPal = new int[32 * 32];
    private readonly bool[] cellSet = new bool[32 * 32];
    private readonly Dictionary<(ulong, ulong), int> bgSlotOf = new();
    private readonly Dictionary<int, int> objSlotOf = new();
    private readonly byte[] recoded = new byte[16];
    private readonly long[] bgUse = new long[16], objUse = new long[16];
    private readonly byte[] bgShade = new byte[16], objShade = new byte[16];
    private readonly byte[] bgValue = new byte[16], objValue = new byte[16];
    private readonly int[] obpOf = new int[4];
    private readonly List<(int x, int y, int nesAddr, int at)> pieces = new();

    /// <summary>The Game Boy shades the last frame's palettes held (tests): BGP colour 0 (the backdrop) and colours 1-3, OBP0 and OBP1 colours 1-3, as shade numbers (0 lightest).</summary>
    public byte LastBgp { get; private set; }
    public byte LastObp0 { get; private set; }
    public byte LastObp1 { get; private set; }

    /// <summary>
    /// TEST ONLY (the inversion checks' negative control): draw with the colour index as the shade (BGP = OBP = E4, or 1B when inverted) as the bridge did before
    /// 2026-10-22, the mapping that drew the SNES and Sega bridges' pictures inverted. False in every app.
    /// </summary>
    internal static bool IndexShadesForTests;

    /// <summary>A NES colour's Game Boy shade by its brightness (as the PPU_FIX family draws the colour).</summary>
    private static byte ShadeOfNes(int nesColour) => GbColour.ShadeOf((uint)NesFixPalette.Rgb(nesColour));

    private void PresentFrame()
    {
        var vram = gb.Vram;
        int bgTable = (frameCtrl & 0x10) != 0 ? 0x1000 : 0;
        bool dmg = !cgb, legacy = dmg && IndexShadesForTests;
        // ---- which NES tile each Game Boy map cell shows: each Game Boy line's NES row lands in map row (Y >> 3) & 31, its columns at (X >> 3) & 31;
        // a cell takes its pattern from the CHR as it was when its 8-line group was drawn (games switch CHR banks mid-frame). A line spans (W + fine X) / 8
        // tiles; at W = 256 with a fine scroll that is 33, one more than the 32-column map holds - the last would land on the first, so it is left out (the
        // Game Boy map's own limit: the right edge repeats the left).
        Array.Clear(cellSet);
        for (int l = 0; l < H; l++)
        {
            int L = l + cy, X0 = (lineX[L] + cx) & 511, Y = lineY[L] & 511;
            int m = (Y >> 3) & 31;
            int group = Math.Min(l >> 3, rowChrs.Length - 1);
            int cols = Math.Min(32, (W + (X0 & 7) + 7) >> 3);
            for (int c = 0; c < cols; c++)
            {
                int X = (X0 + c * 8) & 511, mc = (X >> 3) & 31;
                int tile = NesTile(X, Y, out int p);
                int cell = m * 32 + mc;
                cellSet[cell] = true; cellGroup[cell] = group; cellAddr[cell] = bgTable + tile * 16; cellPal[cell] = p;
            }
        }
        // ---- the sprites in the window (8x16 NES = two 8x8), at most 40
        pieces.Clear();
        bool tall = (frameCtrl & 0x20) != 0; int sprTable = (frameCtrl & 0x08) != 0 ? 0x1000 : 0;
        for (int i = 0; i < 64 && pieces.Count < 40; i++)
        {
            int y = frameOam[i * 4] + 1 - cy, tile = frameOam[i * 4 + 1], at = frameOam[i * 4 + 2], x = frameOam[i * 4 + 3] - cx;
            bool vf = (at & 0x80) != 0;
            if (!tall) { AddPiece(x, y, sprTable + tile * 16, at); continue; }
            int tbl = (tile & 1) != 0 ? 0x1000 : 0, top = tile & 0xFE;
            AddPiece(x, y, tbl + (vf ? top + 1 : top) * 16, at);
            AddPiece(x, y + 8, tbl + (vf ? top : top + 1) * 16, at);
        }
        // ---- DMG: the shades the palettes hold, and every colour's value in them
        if (dmg) ChooseShades(legacy);
        else
        {
            // Game Boy Color: the colour index is kept, the palettes carry the colours (the DMG registers are not used)
            for (int i = 0; i < 16; i++) { bgValue[i] = (byte)(i & 3); objValue[i] = (byte)(i & 3); }
            LastBgp = LastObp0 = LastObp1 = 0xE4;
        }
        // ---- background tiles: NES tile patterns (their colours recoded to the palette values) -> the 256 signed Game Boy tile ids ($9000 + id*16 for id < 128,
        // $8800 + ... for id >= 128), packed by content; past 256 a slot is reused (visible as wrong tiles)
        bgSlotOf.Clear(); int nextBg = 0;
        for (int cell = 0; cell < 32 * 32; cell++)
        {
            if (!cellSet[cell]) continue;
            int p = cellPal[cell];
            var key = Recode(rowChrs[cellGroup[cell]], cellAddr[cell], bgValue, p * 4, dmg);
            if (!bgSlotOf.TryGetValue(key, out int id))
            {
                id = nextBg < 256 ? nextBg++ : (cellAddr[cell] >> 4) & 255;
                bgSlotOf[key] = id;
                int gbAddr = 0x1000 + (sbyte)id * 16;
                Array.Copy(recoded, 0, vram, gbAddr, 16);
            }
            vram[0x1800 + cell] = (byte)id;
            if (cgb) vram[0x2000 + 0x1800 + cell] = (byte)p;
        }
        BgSlotsUsed = nextBg;
        // ---- sprites: the used NES sprite tiles (per palette, recoded) -> $8000 slots 0-127, up to 40 Game Boy sprites
        Array.Fill(gb.Oam, (byte)0);
        objSlotOf.Clear(); int nextObj = 0, n = 0;
        foreach (var (x, y, nesAddr, at) in pieces)
        {
            int p = at & 3;
            int key = nesAddr | (dmg ? p << 16 : 0);
            if (!objSlotOf.TryGetValue(key, out int s))
            {
                if (nextObj >= 128) continue;
                s = nextObj++; objSlotOf[key] = s;
                Recode(chr, nesAddr, objValue, p * 4, dmg);
                Array.Copy(recoded, 0, vram, s * 16, 16);
            }
            int f = ((at & 0x20) != 0 ? 0x80 : 0) | ((at & 0x40) != 0 ? 0x20 : 0) | ((at & 0x80) != 0 ? 0x40 : 0) | (cgb ? p : obpOf[p] << 4);
            gb.Oam[n * 4] = (byte)(y + 16); gb.Oam[n * 4 + 1] = (byte)(x + 8); gb.Oam[n * 4 + 2] = (byte)s; gb.Oam[n * 4 + 3] = (byte)f; n++;
        }
        // ---- palettes
        if (cgb)
        {
            for (int p = 0; p < 4; p++)
                for (int c = 0; c < 4; c++)
                {
                    ushort bg = NesPalette.ToBgr555(pal[c == 0 ? 0 : p * 4 + c]), ob = NesPalette.ToBgr555(pal[16 + p * 4 + c]);
                    bg = GbLook.BackgroundColour(bg); ob = GbLook.SpriteColour(ob);   // the lightness mirrored when asked (hue kept)
                    gb.BgPalRam[p * 8 + c * 2] = (byte)bg; gb.BgPalRam[p * 8 + c * 2 + 1] = (byte)(bg >> 8);
                    gb.ObjPalRam[p * 8 + c * 2] = (byte)ob; gb.ObjPalRam[p * 8 + c * 2 + 1] = (byte)(ob >> 8);
                }
        }
        gb.Bgp = LastBgp; gb.Obp0 = LastObp0; gb.Obp1 = LastObp1;
        if (!cgb) for (int i = 0; i < 4; i++) gb.DmgColors[i] = GbLook.Argb(i);   // the chosen look, live
        CarryLayers();
        // Draw one Game Boy frame (SCX/SCY are set per line in OnGbLine).
        long target = gb.FrameCount + 1; int guard = 0;
        int guardMax = gb.FrameDots / 2;   // two frames' worth of 4-dot ticks
        while (gb.FrameCount < target && guard++ < guardMax) gb.Tick(4);
        // Present: the Game Boy screen centred where it came from, a dark bezel around it.
        var src = gb.FrameBuffer;
        for (int y = 0; y < 240; y++)
            for (int x = 0; x < 256; x++)
            {
                int o = (y * 256 + x) * 4, gx = x - cx, gy = y - cy;
                uint c = gx >= 0 && gx < W && gy >= 0 && gy < H ? src[gy * W + gx] : 0xFF1A1C1Eu;
                frame[o] = (byte)(c >> 16); frame[o + 1] = (byte)(c >> 8); frame[o + 2] = (byte)c; frame[o + 3] = 255;
            }
    }

    private void AddPiece(int x, int y, int nesAddr, int at)
    {
        if (pieces.Count >= 40) return;              // the Game Boy's 40 objects (an 8x16 NES sprite is two of them)
        if (x <= -8 || x >= W || y <= -8 || y >= H) return;
        if (x + 8 > 255 || y + 16 > 255) return;   // OAM coordinates are bytes: no object past X 247 / Y 239
        pieces.Add((x, y, nesAddr, at));
    }

    /// <summary>
    /// The DMG palettes of the frame (<see cref="LastBgp"/>, <see cref="LastObp0"/>, <see cref="LastObp1"/>) and every NES colour's value in them (<c>bgValue</c>,
    /// <c>objValue</c>: palette * 4 + colour index -> the Game Boy colour 0-3 the tile pixel is recoded to). Shades by brightness, inverted when the look asks.
    /// Background: colour 0 is the backdrop (its own shade); colours 1-3 hold the shades the background's pixels use most. Sprites: OBP0 and OBP1 are the two
    /// sets of 3 shades that draw the sprite pixels with the least error, each NES sprite palette on the better of the two.
    /// </summary>
    private void ChooseShades(bool legacy)
    {
        if (legacy)
        {
            // the old mapping: the colour index is the shade
            byte idx = GbLook.InvertBackground ? (byte)0x1B : (byte)0xE4, oidx = GbLook.InvertSprites ? (byte)0x1B : (byte)0xE4;
            LastBgp = idx; LastObp0 = LastObp1 = oidx;
            for (int i = 0; i < 16; i++) { bgValue[i] = (byte)(i & 3); objValue[i] = (byte)(i & 3); }
            Array.Clear(obpOf);
            return;
        }
        for (int p = 0; p < 4; p++)
            for (int c = 0; c < 4; c++)
            {
                bgShade[p * 4 + c] = (byte)GbLook.Background(ShadeOfNes(pal[c == 0 ? 0 : p * 4 + c]));
                objShade[p * 4 + c] = (byte)GbLook.Sprite(ShadeOfNes(pal[16 + p * 4 + c]));
            }
        // how many pixels of each palette colour the picture shows
        Array.Clear(bgUse); Array.Clear(objUse);
        for (int cell = 0; cell < 32 * 32; cell++) if (cellSet[cell]) Count(rowChrs[cellGroup[cell]], cellAddr[cell], cellPal[cell], bgUse);
        foreach (var (_, _, nesAddr, at) in pieces) Count(chr, nesAddr, at & 3, objUse);
        // background: the backdrop, then the three most used shades of the opaque pixels (sorted light to dark)
        Span<long> hist = stackalloc long[4];
        for (int p = 0; p < 4; p++) for (int c = 1; c < 4; c++) hist[bgShade[p * 4 + c]] += bgUse[p * 4 + c];
        Span<int> bgSet = stackalloc int[3];
        PickThree(hist, bgSet);
        LastBgp = (byte)(bgShade[0] | bgSet[0] << 2 | bgSet[1] << 4 | bgSet[2] << 6);
        for (int p = 0; p < 4; p++)
        {
            bgValue[p * 4] = 0;
            for (int c = 1; c < 4; c++) bgValue[p * 4 + c] = (byte)(1 + Nearest(bgSet, bgShade[p * 4 + c]));
        }
        // sprites: the pair of shade sets (of the four sets of 3 the 4 shades make) that draws the sprite pixels with the least error
        ReadOnlySpan<int> sets = stackalloc int[] { 0, 1, 2, 0, 1, 3, 0, 2, 3, 1, 2, 3 };
        long best = long.MaxValue; int bestA = 3, bestB = 3;
        for (int a = 0; a < 4; a++)
            for (int b = a; b < 4; b++)
            {
                long cost = 0;
                for (int p = 0; p < 4; p++) cost += Math.Min(SetCost(sets.Slice(a * 3, 3), p), SetCost(sets.Slice(b * 3, 3), p));
                if (cost < best) { best = cost; bestA = a; bestB = b; }
            }
        var setA = sets.Slice(bestA * 3, 3); var setB = sets.Slice(bestB * 3, 3);
        LastObp0 = (byte)(setA[0] << 2 | setA[1] << 4 | setA[2] << 6);
        LastObp1 = (byte)(setB[0] << 2 | setB[1] << 4 | setB[2] << 6);
        for (int p = 0; p < 4; p++)
        {
            obpOf[p] = SetCost(setB, p) < SetCost(setA, p) ? 1 : 0;
            var set = obpOf[p] == 1 ? setB : setA;
            objValue[p * 4] = 0;
            for (int c = 1; c < 4; c++) objValue[p * 4 + c] = (byte)(1 + Nearest(set, objShade[p * 4 + c]));
        }
    }

    /// <summary>The pixels of an 8x8 NES tile by colour index, added to <paramref name="use"/>[palette * 4 + colour].</summary>
    private static void Count(byte[] src, int at, int palette, long[] use)
    {
        for (int r = 0; r < 8; r++)
        {
            int lo = src[(at + r) & 0x1FFF], hi = src[(at + 8 + r) & 0x1FFF];
            for (int x = 0; x < 8; x++) use[palette * 4 + ((lo >> x) & 1 | ((hi >> x) & 1) << 1)]++;
        }
    }

    /// <summary>The cost (pixels times shade steps off) of drawing NES sprite palette <paramref name="p"/> with a set of 3 shades.</summary>
    private long SetCost(ReadOnlySpan<int> set, int p)
    {
        long cost = 0;
        for (int c = 1; c < 4; c++)
        {
            int s = objShade[p * 4 + c];
            cost += objUse[p * 4 + c] * Math.Abs(set[Nearest(set, s)] - s);
        }
        return cost;
    }

    /// <summary>The three most used shades (all of them when three or fewer are used), light to dark; an unused place repeats the last shade (or 1, 2, 3 for none).</summary>
    private static void PickThree(ReadOnlySpan<long> hist, Span<int> set)
    {
        int used = 0; for (int s = 0; s < 4; s++) if (hist[s] > 0) used++;
        if (used == 0) { set[0] = 1; set[1] = 2; set[2] = 3; return; }
        int drop = -1;
        if (used == 4) { drop = 0; for (int s = 1; s < 4; s++) if (hist[s] < hist[drop]) drop = s; }
        int n = 0;
        for (int s = 0; s < 4 && n < 3; s++) if (hist[s] > 0 && s != drop) set[n++] = s;
        while (n < 3) { set[n] = set[n - 1]; n++; }
    }

    /// <summary>The place in a 3-shade set (light to dark) of the shade nearest <paramref name="shade"/> (a tie goes to the lighter one).</summary>
    private static int Nearest(ReadOnlySpan<int> set, int shade)
    {
        int best = 0;
        for (int i = 1; i < set.Length; i++) if (Math.Abs(set[i] - shade) < Math.Abs(set[best] - shade)) best = i;
        return best;
    }

    /// <summary>A NES tile (planes at <paramref name="at"/> and +8) as a Game Boy tile (row by row, plane 0 then plane 1) into <c>recoded</c>, its colours
    /// through <paramref name="values"/>[<paramref name="offset"/> + colour] when <paramref name="map"/>; returns the 16 bytes as a key.</summary>
    private (ulong, ulong) Recode(byte[] src, int at, byte[] values, int offset, bool map)
    {
        for (int r = 0; r < 8; r++)
        {
            int lo = src[(at + r) & 0x1FFF], hi = src[(at + 8 + r) & 0x1FFF];
            if (map)
            {
                int nlo = 0, nhi = 0;
                for (int x = 0; x < 8; x++)
                {
                    int v = values[offset + ((lo >> x) & 1 | ((hi >> x) & 1) << 1)];
                    nlo |= (v & 1) << x; nhi |= (v >> 1 & 1) << x;
                }
                lo = nlo; hi = nhi;
            }
            recoded[r * 2] = (byte)lo; recoded[r * 2 + 1] = (byte)hi;
        }
        return (BitConverter.ToUInt64(recoded, 0), BitConverter.ToUInt64(recoded, 8));
    }

    /// <summary>
    /// PPU_DMGS: the NES front chip's cross-console picture (PPU_FIXS.Ext) shown by the enhanced Game Boy chip (PPU_GBE) too - the
    /// same <see cref="ExtPicture"/>, seen through the window, the Game Boy chip turning it into its own colours (shades by brightness,
    /// or CGB colours) with the Game Boy look's inversions.
    /// </summary>
    private void CarryLayers()
    {
        var dst = gb.Enhanced; var src = LayerFront;
        if (dst == null) return;
        if (src != null) src.ExtCompose = false;   // its NES picture is not shown: the Game Boy chip draws the extension
        dst.Ext = src?.Ext;
        dst.ExtOffsetX = cx; dst.ExtOffsetY = cy - (src?.ExtLineOffset ?? 8);
        dst.ExtInvertBackground = GbLook.InvertBackground; dst.ExtInvertSprites = GbLook.InvertSprites;
    }

    private void OnGbLine(int l)
    {
        int L = l + cy, X0 = (lineX[L] + cx) & 511, Y = lineY[L] & 511;
        gb.Scx = (byte)(X0 & 255); gb.Scy = (byte)((Y - l) & 255);
        byte mask = lineMask[L];
        gb.Lcdc = (byte)(0x80 | ((mask & 0x08) != 0 || cgb ? 0x01 : 0) | ((mask & 0x10) != 0 ? 0x02 : 0));
    }
}

/// <summary>
/// MIX LAB: the Game Boy picture chip, ENHANCED - <see cref="PPU_DMG"/> on the enhanced Game Boy chip PPU_GBE, fronted by the NES chip PPU_FIXS
/// (FIX enhanced): the whole picture (256x240 by default; --gb-res WxH picks another, 160-256 x 144-240) instead of the stock chip's middle
/// 160x144, and a game translated through the NES (SNES, Master System, Game Gear, Genesis) keeps its layers and all its sprites on the Game
/// Boy too, within Game Boy shades or CGB colours. Discovered by CoreRegistry as PPU id "DMGS" (it replaced "DMGX", the big screen without
/// the layers, on 2026-10-22: a stored DMGX is moved here by <see cref="CoreRegistry.MigratePpuId"/>).
/// </summary>
public sealed class PPU_DMGS : PPU_DMG
{
    public PPU_DMGS(Bus bus) : base(bus, MixConfig.GbHiResWidth, MixConfig.GbHiResHeight, "FIXS", enhanced: true) { }
}
