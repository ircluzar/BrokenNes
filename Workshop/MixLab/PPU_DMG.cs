using System;
using System.Collections.Generic;
using System.Linq;
using BrokenNes.Workshop.MixLab;
using NesEmulator.Gb;

namespace NesEmulator;

/// <summary>
/// MIX LAB: a Game Boy PPU (the real PPU_GB, DMG or Game Boy Color, <see cref="MixConfig.GbPpuModel"/>) drawing a NES game.
///
/// A NES PPU core (MixConfig.NesFrontPpu, IPpuProbe - or any core through the PpuSharedState rescue) stays in front
/// for register semantics, timing, NMI and sprite-0 hit. Each frame the bridge records every NES line's scroll, then
/// builds Game Boy VRAM: NES background tiles -> the $8800 tile block (signed ids, so NES tile n = Game Boy tile n),
/// the used sprite tiles -> $8000, the 2x2 nametables -> the 32x32 Game Boy map row by row, and has PPU_GB draw a real
/// frame, setting SCX/SCY as each Game Boy line starts. The Game Boy sees the middle of the NES screen: 160x144 of it,
/// from NES (48,48). DMG: one BGP for everything, so the four NES palettes collapse into 4 greens by colour index;
/// Game Boy Color: NES palettes -> CGB palettes, attributes per tile. 40 sprites, 10 per line (NES: 64 and 8).
/// Discovered by CoreRegistry as PPU id "DMG". <see cref="PPU_DMGX"/> is the same bridge on the off-spec PPU_GBX.
/// </summary>
public class PPU_DMG : IPPU, IPpuProbe
{
    private readonly Bus bus;
    private readonly IPPU front;
    private readonly IPpuProbe? probe;
    private readonly IPpuFrameClock? clock;
    private readonly GbScreen gb;
    /// <summary>The Game Boy screen size (160x144, or bigger on PPU_GBX).</summary>
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

    protected PPU_DMG(Bus bus, int gbWidth, int gbHeight, string? frontId = null, bool layered = false)
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
        cgb = MixConfig.GbPpuModel.Equals("cgb", StringComparison.OrdinalIgnoreCase);
        gb = GbScreen.Create(cgb ? GbModel.Cgb : GbModel.Dmg, W, H, layered);
        if (!cgb) for (int i = 0; i < 4; i++) { var (r, g, b) = GbShades.Green[i]; gb.DmgColors[i] = 0xFF000000u | (uint)(r << 16 | g << 8 | b); }
        gb.LineStarted += OnGbLine;
    }

    /// <summary>The NES front chip when it is a PPU_FIXS, whose SNES-support layers a subclass can carry over.</summary>
    public PPU_FIXS? LayerFront => front as PPU_FIXS;

    public string CoreName => $"DMG{(W != PPU_GB.Width || H != PPU_GB.Height ? $"X {W}x{H}" : "")}:{(cgb ? "CGB" : "DMG")}+{front.CoreName}";
    public string Description => "MIX LAB: a Game Boy PPU drawing the middle of a NES game";
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
    private byte[][]? rowChrs;
    private byte[][] rowChr => rowChrs ??= Enumerable.Range(0, H / 8).Select(_ => new byte[0x2000]).ToArray();
    /// <summary>Distinct background tile patterns the last frame needed (over 256 = some cells show wrong tiles).</summary>
    public int BgSlotsUsed;
    private void SnapshotRow(int group) { var dst = rowChr[group]; for (int i = 0; i < 0x2000; i++) dst[i] = busRead((ushort)i); }

    // ---------------------------------------------------------------- the translation
    private int NesTile(int X, int Y, out int palette)
    {
        int ntx = (X >> 8) & 1, nty = (Y >> 8) & 1, col = (X & 255) >> 3, row = Math.Min((Y & 255) >> 3, 29);
        int baseAddr = (ntx + nty * 2) * 0x400;
        int attr = nt[baseAddr + 0x3C0 + (row >> 2) * 8 + (col >> 2)];
        palette = (attr >> (((row & 2) << 1) | (col & 2))) & 3;
        return nt[baseAddr + row * 32 + col];
    }

    private void PresentFrame()
    {
        var vram = gb.Vram;
        int bgTable = (frameCtrl & 0x10) != 0 ? 0x1000 : 0;
        // BG tiles: NES tile t -> Game Boy signed tile t ($9000 + t*16 for t < 128, $8800 + ... for t >= 128).
        // Background: each cell takes its tile pattern from the CHR as it was when its 8-line group was drawn (games
        // switch CHR banks mid-frame), and the patterns in use are packed into the 256 signed Game Boy tile ids.
        var slotOf = new Dictionary<string, int>(); int nextBg = 0;
        int BgSlot(byte[] src, int at)
        {
            string key = Convert.ToBase64String(src, at, 16);
            if (slotOf.TryGetValue(key, out var id)) return id;
            id = nextBg < 256 ? nextBg++ : (at >> 4) & 255;   // overflow: reuse a slot (visible as wrong tiles)
            slotOf[key] = id;
            int gbAddr = 0x1000 + (sbyte)id * 16;
            for (int r = 0; r < 8; r++) { vram[gbAddr + r * 2] = src[at + r]; vram[gbAddr + r * 2 + 1] = src[at + 8 + r]; }
            return id;
        }
        // Map: each Game Boy line's NES row lands in map row (Y >> 3) & 31, its columns at (X >> 3) & 31. A line spans
        // (W + fine X) / 8 tiles; at W = 256 with a fine scroll that is 33, one more than the 32-column map holds - the
        // last would land on the first, so it is left out (the Game Boy map's own limit: the right edge repeats the left).
        for (int l = 0; l < H; l++)
        {
            int L = l + cy, X0 = (lineX[L] + cx) & 511, Y = lineY[L] & 511;
            int m = (Y >> 3) & 31;
            var rc = rowChr[Math.Min(l >> 3, H / 8 - 1)];
            int cols = Math.Min(32, (W + (X0 & 7) + 7) >> 3);
            for (int c = 0; c < cols; c++)
            {
                int X = (X0 + c * 8) & 511, mc = (X >> 3) & 31;
                int tile = NesTile(X, Y, out int p);
                vram[0x1800 + m * 32 + mc] = (byte)BgSlot(rc, bgTable + tile * 16);
                if (cgb) vram[0x2000 + 0x1800 + m * 32 + mc] = (byte)p;
            }
        }
        BgSlotsUsed = nextBg;
        // Sprites: the used NES sprite tiles -> $8000 slots 0-127, up to 40 Game Boy sprites (8x16 NES = two 8x8).
        Array.Fill(gb.Oam, (byte)0);
        var slots = new Dictionary<int, int>(); int next = 0, n = 0;
        bool tall = (frameCtrl & 0x20) != 0; int sprTable = (frameCtrl & 0x08) != 0 ? 0x1000 : 0;
        int Slot(int nesAddr)
        {
            if (slots.TryGetValue(nesAddr, out var s)) return s;
            if (next >= 128) return -1;
            s = next++; slots[nesAddr] = s;
            for (int r = 0; r < 8; r++) { vram[s * 16 + r * 2] = chr[nesAddr + r]; vram[s * 16 + r * 2 + 1] = chr[nesAddr + 8 + r]; }
            return s;
        }
        void Put(int x, int y, int nesAddr, int at)
        {
            if (n >= 40 || x <= -8 || x >= W || y <= -8 || y >= H) return;
            if (x + 8 > 255 || y + 16 > 255) return;   // OAM coordinates are bytes: no object past X 247 / Y 239
            int s = Slot(nesAddr); if (s < 0) return;
            int f = ((at & 0x20) != 0 ? 0x80 : 0) | ((at & 0x40) != 0 ? 0x20 : 0) | ((at & 0x80) != 0 ? 0x40 : 0) | (cgb ? at & 3 : 0);
            gb.Oam[n * 4] = (byte)(y + 16); gb.Oam[n * 4 + 1] = (byte)(x + 8); gb.Oam[n * 4 + 2] = (byte)s; gb.Oam[n * 4 + 3] = (byte)f; n++;
        }
        for (int i = 0; i < 64; i++)
        {
            int y = frameOam[i * 4] + 1 - cy, tile = frameOam[i * 4 + 1], at = frameOam[i * 4 + 2], x = frameOam[i * 4 + 3] - cx;
            bool vf = (at & 0x80) != 0;
            if (!tall) { Put(x, y, sprTable + tile * 16, at); continue; }
            int tbl = (tile & 1) != 0 ? 0x1000 : 0, top = tile & 0xFE;
            Put(x, y, tbl + (vf ? top + 1 : top) * 16, at);
            Put(x, y + 8, tbl + (vf ? top : top + 1) * 16, at);
        }
        // Palettes.
        if (cgb)
        {
            for (int p = 0; p < 4; p++)
                for (int c = 0; c < 4; c++)
                {
                    ushort bg = NesPalette.ToBgr555(pal[c == 0 ? 0 : p * 4 + c]), ob = NesPalette.ToBgr555(pal[16 + p * 4 + c]);
                    gb.BgPalRam[p * 8 + c * 2] = (byte)bg; gb.BgPalRam[p * 8 + c * 2 + 1] = (byte)(bg >> 8);
                    gb.ObjPalRam[p * 8 + c * 2] = (byte)ob; gb.ObjPalRam[p * 8 + c * 2 + 1] = (byte)(ob >> 8);
                }
        }
        gb.Bgp = 0xE4; gb.Obp0 = 0xE4; gb.Obp1 = 0xE4;
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

    /// <summary>
    /// PPU_DMGS: the NES front chip's SNES-support layers and sprites (PPU_FIXS) onto the Game Boy chip's own (PPU_GBXS):
    /// NES 2-bit tiles -> Game Boy tile format, NES palette slots -> CGB palettes 0-3 (loaded from the NES palettes above;
    /// DMG: shades by colour index), the same maps, scrolls, priorities and ladder, shifted by the crop.
    /// </summary>
    private void CarryLayers()
    {
        var dst = gb.LayerChip; var src = LayerFront;
        if (dst == null) return;
        dst.ClearExtension();
        if (src == null) return;
        for (int k = 0; k < PPU_FIXS.ExtLayerCount; k++)
        {
            var a = src.Layers[k]; var b = dst.Layers[k];
            if (!a.Enabled) continue;
            for (int t = 0; t < 256; t++) for (int r = 0; r < 8; r++) { b.Chr[t * 16 + r * 2] = a.Chr[t * 16 + r]; b.Chr[t * 16 + r * 2 + 1] = a.Chr[t * 16 + 8 + r]; }
            Array.Copy(a.Map, b.Map, a.Map.Length);
            for (int i = 0; i < a.Attr.Length; i++) b.Attr[i] = (byte)((a.Attr[i] & 3) | ((a.Attr[i] & 4) != 0 ? 8 : 0));
            b.ScrollX = a.ScrollX + cx; b.ScrollY = a.ScrollY + cy; b.ZLow = a.ZLow; b.ZHigh = a.ZHigh;
            b.Enabled = true;
        }
        for (int t = 0; t < PPU_FIXS.ExtSpriteTiles; t++) for (int r = 0; r < 8; r++) { dst.ExtSpriteChr[t * 16 + r * 2] = src.ExtSpriteChr[t * 16 + r]; dst.ExtSpriteChr[t * 16 + r * 2 + 1] = src.ExtSpriteChr[t * 16 + 8 + r]; }
        if (dst.ExtSprites.Length < src.ExtSpriteCount) dst.ExtSprites = new PPU_GBXS.ExtSprite[src.ExtSpriteCount];
        for (int i = 0; i < src.ExtSpriteCount; i++)
        {
            var e = src.ExtSprites[i];
            dst.ExtSprites[i] = new PPU_GBXS.ExtSprite { X = (short)(e.X - cx), Y = (short)(e.Y - cy), Tile = e.Tile, Palette = cgb ? e.Palette : (byte)0, Priority = e.Priority, HFlip = e.HFlip, VFlip = e.VFlip };
        }
        dst.ExtSpriteCount = src.ExtSpriteCount;
        dst.ExtSpriteLimit = MixConfig.ExtSpriteLimit; dst.ExtSpritesPerLine = MixConfig.ExtSpritesPerLine;
        Array.Copy(src.SpriteZ, dst.SpriteZ, 4);
        dst.ExtFirstLine = src.ExtFirstLine - cy; dst.ExtLastLine = src.ExtLastLine - cy;
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
/// MIX LAB: <see cref="PPU_DMG"/> on the off-spec Game Boy picture chip PPU_GBX - a Game Boy with a 256x240 screen, so a
/// NES (or, through the NES, SNES) game is shown whole instead of cropped to the Game Boy's 160x144. Everything else
/// keeps the Game Boy's limits: 4 greens or CGB palettes, 256 background tiles, 40 objects with 10 per line, the one
/// 32x32 background map. --gb-res WxH picks another size (160-256 x 144-240). Discovered by CoreRegistry as PPU id "DMGX".
/// </summary>
public sealed class PPU_DMGX : PPU_DMG
{
    public PPU_DMGX(Bus bus) : base(bus, MixConfig.GbHiResWidth, MixConfig.GbHiResHeight) { }
}

/// <summary>
/// MIX LAB: the Game Boy bridge with SNES support - PPU_DMGX's big screen on PPU_GBXS, fronted by the NES chip PPU_FIXS,
/// so a SNES game translated through the NES keeps its layers and all its sprites on the Game Boy too (within Game Boy
/// tiles and palettes). Discovered by CoreRegistry as PPU id "DMGS".
/// </summary>
public sealed class PPU_DMGS : PPU_DMG
{
    public PPU_DMGS(Bus bus) : base(bus, MixConfig.GbHiResWidth, MixConfig.GbHiResHeight, "FIXS", layered: true) { }
}
