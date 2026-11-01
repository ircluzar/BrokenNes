using System.Linq;
using System;
using NesEmulator.Mix;

namespace NesEmulator;

/// <summary>
/// MIX LAB: a SNES PPU core (any <see cref="ISnesPpuCore"/>, <see cref="MixConfig.SnesPpu"/>) drawing a NES game.
///
/// A NES PPU core (any IPPU with IPpuProbe, <see cref="MixConfig.NesFrontPpu"/>) stays in front: it owns the
/// NES register semantics, timing, NMI, sprite-0 hit and status - the SNES chip has none of those. Once per
/// frame (pre-render, dot 320) the bridge translates the NES picture into SNES memory:
///   CHR (both pattern tables, read through the NES PPU bus) -> 2bpp BG tiles + 4bpp sprite tiles,
///   the four nametables -> one 64x64 BG1 tilemap, attributes -> per-tile palette numbers,
///   palette RAM -> CGRAM (through the 2C02 master palette), OAM -> 64 or 128 SNES sprites (8x16 = two 8x8).
/// Then per NES line it copies that line's scroll (v and fine X, sampled at dot 320 of the line before) into
/// BG1HOFS/VOFS and has the SNES PPU render the line. Mid-frame scroll splits survive; mid-frame CHR bank
/// switches do not (tiles are converted once per frame). SNES mode 0, overscan on (239 lines), BG1 + OBJ.
/// </summary>
public sealed class PPU_SNES : IPPU, IPpuProbe
{
    private readonly Bus bus;
    private readonly IPPU front;
    private readonly IPpuProbe probe;
    private readonly ISnesPpuCore back;
    private readonly byte[] frame = new byte[256 * 240 * 4];
    private byte ctrl, oamAddr;
    private readonly byte[] oam = new byte[256];
    private int lastLine = -1;

    // SNES VRAM layout (word addresses)
    private const int MapBase = 0x0000;   // 64x64 BG1 map: 4 screens of 32x32 = 0x1000 words
    private const int BgChr = 0x2000;     // 512 2bpp tiles (both NES pattern tables) = 0x1000 words
    private const int ObjChr = 0x4000;    // 512 4bpp tiles = 0x2000 words

    public PPU_SNES(Bus bus)
    {
        this.bus = bus;
        var t = CoreRegistry.PpuTypes.TryGetValue(MixConfig.NesFrontPpu, out var ft) ? ft : throw new ArgumentException($"No NES PPU '{MixConfig.NesFrontPpu}'");
        front = CoreRegistry.CreateInstance<IPPU>(t, bus) ?? throw new InvalidOperationException("front PPU");
        probe = front as IPpuProbe;
        clock = front as IPpuFrameClock;
        // Without IPpuProbe the bridge can't see the dot or the PPU bus. RESCUE: follow the core through the state
        // every NES PPU already exports for hot-swaps (PpuSharedState), sampled once per new line.
        if (probe == null && (!MixConfig.RescueFront || clock == null))
            throw new NotSupportedException($"NES PPU {MixConfig.NesFrontPpu} has no IPpuProbe - it cannot front a SNES PPU");
        back = SnesCores.CreatePpu(MixConfig.SnesPpu);
        busRead = probe != null ? probe.ProbePpuBusRead : StateRead;
        InitBack();
    }

    private readonly IPpuFrameClock? clock;
    private Func<ushort, byte> busRead;
    private PpuSharedState? st;

    // PPU-bus reads from exported state: CHR through the cartridge, nametables through its mirroring, palette RAM.
    private byte StateRead(ushort a)
    {
        a &= 0x3FFF;
        if (a < 0x2000) return bus.cartridge.PPURead(a);
        if (a < 0x3F00)
        {
            int off = a & 0x0FFF, nt = off / 0x400, inner = off % 0x400;
            int m = bus.cartridge.mirroringMode switch
            {
                Mirroring.Vertical => (nt % 2) * 0x400 + inner,
                Mirroring.Horizontal => (nt / 2) * 0x400 + inner,
                Mirroring.SingleScreenA => inner,
                Mirroring.SingleScreenB => 0x400 + inner,
                _ => off & 0x7FF,
            };
            return st!.vram[m & 0x7FF];
        }
        int p = a & 0x1F; if (p >= 0x10 && (p & 3) == 0) p -= 0x10;
        return st!.palette[p];
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
            if (line > 238) continue;
            st = front.GetState() as PpuSharedState;
            if (st == null) continue;
            if (line == 0)
            {
                ctrl = st.PPUCTRL; Array.Copy(st.oam, oam, 256);
                UploadFrame(); back.BeginFrame();
            }
            // Vertical scroll from v (already on this line), horizontal from t: at dot 0 v's coarse X has run two
            // tiles ahead in the prefetch, while t still holds what the game last wrote.
            int v = st.v, t = st.t, fx = st.fineX;
            int x = ((t >> 10) & 1) * 256 + (t & 31) * 8 + fx;
            int y = ((v >> 11) & 1) * 256 + ((v >> 5) & 31) * 8 + ((v >> 12) & 7);
            ApplyLine(line, x, y, st.PPUMASK);
        }
    }

    public IPPU Front => front;
    public ISnesPpuCore Back => back;
    public string CoreName => $"SNES:{back.Id}+{front.CoreName}";
    public string Description => "MIX LAB: a SNES PPU drawing a NES game, with a NES PPU keeping the register semantics";
    public int Performance => 0;
    public int Rating => 1;
    public string Category => "Experimental";

    private void InitBack()
    {
        var b = back;
        b.WriteRegister(0x00, 0x8F);                 // forced blank while we set up
        b.WriteRegister(0x05, 0x00);                 // mode 0, 8x8 tiles
        b.WriteRegister(0x07, (byte)((MapBase >> 8) | 0x03)); // BG1 map, 64x64
        b.WriteRegister(0x0B, (byte)(BgChr >> 12));  // BG1 chr base
        b.WriteRegister(0x01, (byte)(ObjChr >> 13)); // OBJ 8x8/16x16, name base, no gap (tiles 256-511 follow)
        b.WriteRegister(0x2C, 0x11);                 // main screen: BG1 + OBJ
        b.WriteRegister(0x2D, 0x00);
        b.WriteRegister(0x33, 0x04);                 // overscan: 239 lines
        b.WriteRegister(0x00, 0x0F);                 // display on, full brightness
    }

    // ---------------------------------------------------------------- the NES-facing surface
    public void Step(int cycles)
    {
        if (probe == null) { StepByState(cycles); return; }
        while (cycles > 0)
        {
            int dot = probe!.ProbeDot;
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

    public static bool ShowFront = Environment.GetEnvironmentVariable("MIX_FRONTFB") == "1";
    public byte[] GetFrameBuffer() => ShowFront ? front.GetFrameBuffer() : frame;
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

    // ---------------------------------------------------------------- the translation
    private void OnDot320(int line)
    {
        int next = line == 261 ? 0 : line + 1;
        if (line != 261 && line >= 239) return;
        if (next == 0) { UploadFrame(); back.BeginFrame(); }

        ushort v = ProbeV(); int fx = ProbeFineX();
        int coarseX = v & 31, coarseY = (v >> 5) & 31, ntx = (v >> 10) & 1, nty = (v >> 11) & 1, fineY = (v >> 12) & 7;
        int x = ntx * 256 + coarseX * 8 + fx;
        int y = nty * 256 + coarseY * 8 + fineY;       // NES nametables are 240 tall, the SNES screens 256: map row for row
        ApplyLine(next, x, y, probe!.ProbeMask);
    }

    /// <summary>Scroll + mask for NES line <paramref name="next"/>, then have the SNES PPU draw it.</summary>
    private void ApplyLine(int next, int x, int y, byte mask)
    {
        int hofs = x & 0x3FF, vofs = (y - (next + 1)) & 0x3FF;
        back.WriteRegister(0x0D, (byte)hofs); back.WriteRegister(0x0D, (byte)(hofs >> 8));
        back.WriteRegister(0x0E, (byte)vofs); back.WriteRegister(0x0E, (byte)(vofs >> 8));

        back.WriteRegister(0x2C, (byte)(((mask & 0x08) != 0 && !NoBg ? 0x01 : 0) | ((mask & 0x10) != 0 && !NoObj ? 0x10 : 0)));
        // NES left-8-pixel clipping -> SNES window 1 over x 0..7 on BG1 / OBJ.
        bool clipBg = (mask & 0x02) == 0, clipObj = (mask & 0x04) == 0;
        back.WriteRegister(0x26, 0); back.WriteRegister(0x27, 7);
        back.WriteRegister(0x23, (byte)(clipBg ? 0x02 : 0));
        back.WriteRegister(0x25, (byte)(clipObj ? 0x02 : 0));
        back.WriteRegister(0x2E, (byte)((clipBg ? 0x01 : 0) | (clipObj ? 0x10 : 0)));

        rendered[next] = true;
        back.RenderLine(next + 1);
    }

    private ushort ProbeV() => front switch { PPU_FIX f => f.ProbeV, _ => ReflectV() };
    private int ProbeFineX() => front switch { PPU_FIX f => f.ProbeFineX, _ => ReflectFineX() };
    // IPpuProbe has no v / fine X (only PPU_FIX exposes them), so other front cores are read by field name.
    // A contract gap, recorded as a finding rather than fixed here.
    private static readonly System.Reflection.BindingFlags RF = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
    private object? Field(params string[] names)
    {
        foreach (var n in names) { var f = front.GetType().GetField(n, RF); if (f != null) return f.GetValue(front); }
        return null;
    }
    private ushort ReflectV() => Field("v", "vramAddr", "V") is object o ? Convert.ToUInt16(o) : (ushort)0;
    private int ReflectFineX() => Field("fineX", "x", "fineXScroll") is object o ? Convert.ToInt32(o) : 0;

    private void UploadFrame()
    {
        var vram = back.Vram;
        // Pattern tables: NES tile = 8 bytes plane 0 + 8 bytes plane 1; SNES 2bpp = 8 words (plane0 | plane1 << 8).
        for (int tile = 0; tile < 512; tile++)
        {
            for (int r = 0; r < 8; r++)
            {
                byte p0 = busRead((ushort)(tile * 16 + r));
                byte p1 = busRead((ushort)(tile * 16 + 8 + r));
                vram[BgChr + tile * 8 + r] = (ushort)(p0 | (p1 << 8));
                vram[ObjChr + tile * 16 + r] = (ushort)(p0 | (p1 << 8));
                vram[ObjChr + tile * 16 + 8 + r] = 0;   // planes 2/3 empty: colours 0-3 of each 16-colour palette
            }
        }
        // Nametables -> 64x64 map. SNES screens: 0 = top-left, 1 = top-right, 2 = bottom-left, 3 = bottom-right.
        int bgTable = (ctrl & 0x10) != 0 ? 256 : 0;
        for (int nt = 0; nt < 4; nt++)
        {
            int screen = nt;                      // NES $2000/$2400/$2800/$2C00 map to the same quadrants
            for (int ty = 0; ty < 32; ty++)
            for (int tx = 0; tx < 32; tx++)
            {
                ushort entry = 0;
                if (ty < 30)
                {
                    int ntBase = 0x2000 + nt * 0x400;
                    int tileIdx = busRead((ushort)(ntBase + ty * 32 + tx));
                    int attr = busRead((ushort)(ntBase + 0x3C0 + (ty >> 2) * 8 + (tx >> 2)));
                    int pal = (attr >> (((ty & 2) << 1) | (tx & 2))) & 3;
                    entry = (ushort)((bgTable + tileIdx) | (pal << 10) | 0x2000); // priority 1: above "behind" sprites
                }
                vram[MapBase + screen * 0x400 + ty * 32 + tx] = entry;
            }
        }
        // Palettes: BG palettes 0-3 -> CGRAM 0-15 (mode 0 BG1), sprite palettes -> CGRAM 128 + 16p.
        var cg = back.Cgram;
        for (int i = 0; i < 16; i++) cg[i] = NesPalette.ToBgr555(busRead((ushort)(0x3F00 + ((i & 3) == 0 ? 0 : i))));
        for (int p = 0; p < 4; p++)
            for (int c = 1; c < 4; c++) cg[128 + p * 16 + c] = NesPalette.ToBgr555(busRead((ushort)(0x3F10 + p * 4 + c)));
        // OAM: SNES sprite i <- NES sprite i (8x16 NES sprites become two 8x8 SNES sprites).
        var so = back.Oam;
        Array.Clear(so);
        bool tall = (ctrl & 0x20) != 0;
        int sprTable = (ctrl & 0x08) != 0 ? 256 : 0;
        int n = 0;
        for (int i = 0; i < 64 && n < 128; i++)
        {
            int y = oam[i * 4], tile = oam[i * 4 + 1], at = oam[i * 4 + 2], x = oam[i * 4 + 3];
            bool hf = (at & 0x40) != 0, vf = (at & 0x80) != 0, behind = (at & 0x20) != 0;
            int prio = behind ? 2 : 3;   // mode 0: OBJ2 sits under BG1-high, OBJ3 over it
            int attrBase = ((at & 3) << 1) | (prio << 4) | (hf ? 0x40 : 0) | (vf ? 0x80 : 0);
            if (!tall) { PutSprite(so, n++, x, y + 1, sprTable + tile, attrBase); continue; }
            int tbl = (tile & 1) != 0 ? 256 : 0, top = tile & 0xFE;
            PutSprite(so, n++, x, y + 1, tbl + (vf ? top + 1 : top), attrBase);
            if (n < 128) PutSprite(so, n++, x, y + 9, tbl + (vf ? top : top + 1), attrBase);
        }
        for (; n < 128; n++) PutSprite(so, n, 0, 240, 0, 0);  // park the rest off screen
        back.InvalidateCaches();
    }

    private static void PutSprite(byte[] so, int n, int x, int y, int tile, int attr)
    {
        so[n * 4] = (byte)x; so[n * 4 + 1] = (byte)y; so[n * 4 + 2] = (byte)tile;
        so[n * 4 + 3] = (byte)(attr | ((tile >> 8) & 1));
        int hi = 512 + (n >> 2), sh = (n & 3) * 2;
        so[hi] = (byte)(so[hi] & ~(3 << sh));             // x bit 8 = 0, small size
    }

    private static readonly bool LogLines = Environment.GetEnvironmentVariable("MIX_PPULOG") is string s && s.Length > 0;
    private static readonly int LogFrame = int.TryParse(Environment.GetEnvironmentVariable("MIX_PPULOG"), out var lf) ? lf : -1;
    private int frames;
    private readonly bool[] rendered = new bool[262];
    private static readonly bool NoObj = Environment.GetEnvironmentVariable("MIX_NOOBJ") == "1", NoBg = Environment.GetEnvironmentVariable("MIX_NOBG") == "1";
    private void PresentFrame()
    {
        frames++;
        if (LogLines && frames == LogFrame) { var miss = new System.Collections.Generic.List<int>(); for (int i = 0; i < 239; i++) if (!rendered[i]) miss.Add(i); Console.Error.WriteLine($"frame {frames}: {miss.Count} lines never rendered: {string.Join(",", miss.GetRange(0, Math.Min(40, miss.Count)))}"); }
        if (LogLines && frames == LogFrame)
        {
            var vr = back.Vram; ushort e = vr[MapBase + 0]; int tile = e & 0x3FF;
            Console.Error.WriteLine($"map(0,0)=${e:X4} tile={tile} pal={(e >> 10) & 7}; snes words: " + string.Join(" ", System.Linq.Enumerable.Range(0, 8).Select(r => vr[BgChr + tile * 8 + r].ToString("X4"))));
            Console.Error.WriteLine("nes bytes: " + string.Join(" ", System.Linq.Enumerable.Range(0, 16).Select(i => busRead((ushort)(tile * 16 + i)).ToString("X2"))) + $"  ctrl=${ctrl:X2} cg0-3=" + string.Join(",", System.Linq.Enumerable.Range(0,4).Select(i => back.Cgram[i].ToString("X4"))));
        }
        Array.Clear(rendered);
        back.OnVBlankStart();
        var src = back.FrameBuffer;
        int rows = Math.Min(back.VisibleHeight, 239);
        Array.Clear(frame, 239 * 256 * 4, 256 * 4);       // NES line 239: past the SNES overscan height
        for (int row = 0; row < rows; row++)
        for (int x = 0; x < 256; x++)
        {
            uint c = src[row * 256 + x]; int o = (row * 256 + x) * 4;   // SNES row r = NES line r (RenderLine(L+1) draws row L)
            frame[o] = (byte)(c >> 16); frame[o + 1] = (byte)(c >> 8); frame[o + 2] = (byte)c; frame[o + 3] = 255;
        }
    }
}
