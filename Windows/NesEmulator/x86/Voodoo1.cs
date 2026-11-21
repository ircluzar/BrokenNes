// 3dfx Voodoo Graphics (SST-1, "Voodoo 1") PCI add-in card: register file, FIFO model, LFB, texture memory, video/DAC, buffer swaps.
// Written for BrokenNes (C:\BrokenNes-work\voodoo\SCOPE.md). The register semantics and the pixel pipeline (Voodoo1.Render.cs) are ported from
// MAME src/devices/video/voodoo.cpp / voodoo_regs.h / voodoo_render.cpp:
//   license: BSD-3-Clause; copyright-holders: Aaron Giles (see THIRD_PARTY_NOTICES.md).
// Differences from MAME, on purpose: the FIFO never fills (every command runs at once, so the card is never busy and frames are exact and
// deterministic: savestates + jit parity); the retrace runs on the machine's VGA frame clock; one board config: 2 MB frame buffer + one TMU
// with 2 MB (the common 4 MB Voodoo Graphics).

using System;
using System.IO;

namespace NesEmulator.X86;

public sealed partial class Voodoo1 : IPciMmio
{
    public const ushort Vendor = 0x121a, Device = 0x0001;
    public const uint MemSize = 16u << 20;
    public const string Id = "VOODOO1";
    public const int FbBytes = 2 << 20, TexBytes = 2 << 20;
    private const int FbMask = FbBytes - 1, TexMask = TexBytes - 1;
    private const int ChipMask = 3;   // FBI + TMU0

    // register numbers (dword index)
    public const int RStatus = 0x000 / 4, RVertexAx = 0x008 / 4, RVertexAy = 0x00c / 4, RVertexBx = 0x010 / 4, RVertexBy = 0x014 / 4, RVertexCx = 0x018 / 4, RVertexCy = 0x01c / 4,
        RStartR = 0x020 / 4, RStartG = 0x024 / 4, RStartB = 0x028 / 4, RStartZ = 0x02c / 4, RStartA = 0x030 / 4, RStartS = 0x034 / 4, RStartT = 0x038 / 4, RStartW = 0x03c / 4,
        RdRdX = 0x040 / 4, RdGdX = 0x044 / 4, RdBdX = 0x048 / 4, RdZdX = 0x04c / 4, RdAdX = 0x050 / 4, RdSdX = 0x054 / 4, RdTdX = 0x058 / 4, RdWdX = 0x05c / 4,
        RdRdY = 0x060 / 4, RdGdY = 0x064 / 4, RdBdY = 0x068 / 4, RdZdY = 0x06c / 4, RdAdY = 0x070 / 4, RdSdY = 0x074 / 4, RdTdY = 0x078 / 4, RdWdY = 0x07c / 4,
        RTriangleCmd = 0x080 / 4, RFTriangleCmd = 0x100 / 4, RFbzColorPath = 0x104 / 4, RFogMode = 0x108 / 4, RAlphaMode = 0x10c / 4, RFbzMode = 0x110 / 4,
        RLfbMode = 0x114 / 4, RClipLeftRight = 0x118 / 4, RClipLowYHighY = 0x11c / 4, RNopCmd = 0x120 / 4, RFastfillCmd = 0x124 / 4, RSwapbufferCmd = 0x128 / 4,
        RFogColor = 0x12c / 4, RZaColor = 0x130 / 4, RChromaKey = 0x134 / 4, RChromaRange = 0x138 / 4, RStipple = 0x140 / 4, RColor0 = 0x144 / 4, RColor1 = 0x148 / 4,
        RFbiPixelsIn = 0x14c / 4, RFbiPixelsOut = 0x15c / 4, RFogTable = 0x160 / 4,
        RFbiInit4 = 0x200 / 4, RVRetrace = 0x204 / 4, RBackPorch = 0x208 / 4, RVideoDimensions = 0x20c / 4, RFbiInit0 = 0x210 / 4, RFbiInit1 = 0x214 / 4,
        RFbiInit2 = 0x218 / 4, RFbiInit3 = 0x21c / 4, RHSync = 0x220 / 4, RVSync = 0x224 / 4, RClutData = 0x228 / 4, RDacData = 0x22c / 4, RMaxRgbDelta = 0x230 / 4,
        RTextureMode = 0x300 / 4, RTLod = 0x304 / 4, RTDetail = 0x308 / 4, RTexBaseAddr = 0x30c / 4, RTexBaseAddr1 = 0x310 / 4, RTexBaseAddr2 = 0x314 / 4,
        RTexBaseAddr38 = 0x318 / 4, RTrexInit0 = 0x31c / 4, RTrexInit1 = 0x320 / 4, RNccTable = 0x324 / 4;

    public PciFunction? Pci;
    /// <summary>The PCI initEnable register (config 0x40) as the machine last wrote it.</summary>
    private uint InitEnable => Pci?.Read32(0x40) ?? 0;

    /// <summary>FBI registers and TMU0 registers (256 dwords each), plus the 64-bit S/T/W iterators the integer views can't hold.</summary>
    public readonly uint[] Reg = new uint[256], TReg = new uint[256];
    private long _startW, _dwdx, _dwdy;                               // FBI W
    private long _tS, _tT, _tW, _tdSdX, _tdTdX, _tdWdX, _tdSdY, _tdTdY, _tdWdY;   // TMU0 S/T/W
    public readonly byte[] Fb = new byte[FbBytes], Tex = new byte[TexBytes];
    private readonly byte[] _dac = new byte[8];
    private uint _dacRead;
    private readonly uint[] _clut = new uint[33];
    private readonly byte[] _fogBlend = new byte[64], _fogDelta = new byte[64];
    private readonly uint[] _palette = new uint[256], _paletteA = new uint[256];
    private int _front, _back = 1;
    private int _swapsPending, _vblankCount, _vblankSwap;
    private bool _vblankSwapPending;
    private long _lastFrame = -1;
    /// <summary>Status / vRetrace reads since the last retrace: the retrace window is the first <see cref="VBlankReads"/> of them (see Sync).</summary>
    private int _readsSinceRetrace;
    private const int VBlankReads = 2;
    private int _rgbOffs0, _rgbOffs1, _rgbOffs2 = -1, _auxOffs = -1, _rowPixels = 64;

    public Voodoo1() { Reset(); }

    public void Reset()
    {
        Array.Clear(Reg); Array.Clear(TReg); Array.Clear(Fb); Array.Clear(Tex); Array.Clear(_dac); Array.Clear(_fogBlend); Array.Clear(_fogDelta);
        Array.Clear(_palette); Array.Clear(_paletteA);
        _startW = _dwdx = _dwdy = _tS = _tT = _tW = _tdSdX = _tdTdX = _tdWdX = _tdSdY = _tdTdY = _tdWdY = 0;
        _dacRead = 0; _front = 0; _back = 1; _swapsPending = _vblankCount = _vblankSwap = 0; _vblankSwapPending = false; _lastFrame = -1; _readsSinceRetrace = 0;
        for (int p = 0; p < 32; p++) _clut[p] = Raster3D.Argb(p, Raster3D.Pal5(p), Raster3D.Pal5(p), Raster3D.Pal5(p));
        _clut[32] = Raster3D.Argb(32, 0xff, 0xff, 0xff);
        _clutDirty = true;
        Reg[RFbiInit0] = 1 << 4 | 0x10 << 6;
        Reg[RFbiInit1] = 1 << 1 | 1 << 8 | 1 << 12 | 2 << 20;
        Reg[RFbiInit2] = 1 << 6 | 0x100u << 23;
        Reg[RFbiInit3] = 2 << 13 | 0xf << 17;
        Reg[RFbiInit4] = 1;
        RecomputeVideoMemory();
        _texDirty = true;
        _paletteDirty = 0xf;
    }

    // ================= display =================

    /// <summary>True while the Voodoo drives the monitor (pass-through switched to the 3D card) and the output isn't blanked.</summary>
    public bool Active => ((Reg[RFbiInit0] & 1) != ((Reg[RFbiInit3] >> 11) & 1)) && (Reg[RVideoDimensions] & 0x3ff) != 0;
    public int Width => (int)(Reg[RVideoDimensions] & 0x3ff) + 1;
    public int Height => (int)((Reg[RVideoDimensions] >> 16) & 0x3ff);

    private bool _clutDirty;
    private readonly int[] _pen = new int[65536];

    /// <summary>Draws the front buffer through the CLUT into <paramref name="vga"/>'s picture (when <see cref="Active"/>).</summary>
    public void RenderInto(Vga vga)
    {
        int w = Math.Max(8, Width), h = Math.Max(8, Height);
        vga.Ensure(w, h);
        w = vga.Width; h = vga.Height;
        if ((Reg[RFbiInit1] & (1 << 12)) != 0) { Array.Fill(vga.Frame, unchecked((int)0xff000000)); return; }
        if (_clutDirty) RebuildPens();
        int b = BufferOffset(_front);
        var f = vga.Frame;
        for (int y = 0; y < h; y++)
        {
            int row = b + y * _rowPixels * 2;
            for (int x = 0; x < w; x++)
            {
                int o = (row + x * 2) & FbMask;
                f[y * w + x] = _pen[Fb[o] | Fb[o + 1] << 8];
            }
        }
    }

    private void RebuildPens()
    {
        if ((_clut[32] & 0xffffff) == 0 && (_clut[31] & 0xffffff) != 0) _clut[32] = 0x20ffffff;
        Span<byte> rt = stackalloc byte[32], gt = stackalloc byte[64], bt = stackalloc byte[32];
        static int Ch(uint c, int sh) => (int)(c >> sh) & 0xff;
        for (int raw = 0; raw < 32; raw++)
        {
            int c = Raster3D.Pal5(raw);
            rt[raw] = (byte)((Ch(_clut[c >> 3], 16) * (8 - (c & 7)) + Ch(_clut[(c >> 3) + 1], 16) * (c & 7)) >> 3);
            bt[raw] = (byte)((Ch(_clut[c >> 3], 0) * (8 - (c & 7)) + Ch(_clut[(c >> 3) + 1], 0) * (c & 7)) >> 3);
        }
        for (int raw = 0; raw < 64; raw++)
        {
            int c = Raster3D.Pal6(raw);
            gt[raw] = (byte)((Ch(_clut[c >> 3], 8) * (8 - (c & 7)) + Ch(_clut[(c >> 3) + 1], 8) * (c & 7)) >> 3);
        }
        for (int pen = 0; pen < 65536; pen++) _pen[pen] = unchecked((int)0xff000000) | rt[pen >> 11] << 16 | gt[(pen >> 5) & 63] << 8 | bt[pen & 31];
        _clutDirty = false;
    }

    // ================= retrace / swaps (on the machine's frame clock) =================

    // Retrace timing is NOT read from the machine clock: compiled (JIT) code updates the instruction clock only at block exits, so a
    // clock-derived status read inside a block would differ from the interpreter's and break jit parity. Instead: the retrace starts at
    // the machine's frame boundary (TickFrame, the same point in both modes) and lasts for the next VBlankReads status / vRetrace reads
    // (the same instruction stream in both modes). Glide's "wait for retrace / wait for display" loops see both edges every frame.

    /// <summary>Once per machine frame (vertical retrace start): pending vsync'd swaps.</summary>
    public void TickFrame()
    {
        _lastFrame++;
        _readsSinceRetrace = 0;
        _vblankCount = Math.Min(_vblankCount + 1, 250);
        if (_vblankSwapPending && _vblankCount >= _vblankSwap) SwapBuffers();
    }
    private bool InVBlank => _readsSinceRetrace < VBlankReads;
    private void CountRead() { if (_readsSinceRetrace < int.MaxValue) _readsSinceRetrace++; }

    private void SwapBuffers()
    {
        int buffers = _rgbOffs2 < 0 ? 2 : 3;
        _front = (_front + 1) % buffers;
        _back = (_front + 1) % buffers;
        Reg[0x258 / 4] = Reg[0x258 / 4] << 4 | (uint)Math.Min(_vblankCount, 15);
        if (_swapsPending != 0) _swapsPending--;
        _vblankCount = 0;
        _vblankSwapPending = false;
    }

    // ================= memory layout =================

    private void RecomputeVideoMemory()
    {
        uint cfg = (Reg[RFbiInit2] >> 4) & 1;
        _rowPixels = (int)((Reg[RFbiInit1] >> 4) & 0xf) * 64;
        int pages = (int)((Reg[RFbiInit2] >> 11) & 0x1ff);
        _rgbOffs0 = 0;
        _rgbOffs1 = pages * 0x1000;
        if (cfg == 0) { _rgbOffs2 = -1; _auxOffs = 2 * pages * 0x1000; }
        else { _rgbOffs2 = 2 * pages * 0x1000; _auxOffs = -1; }
        if (_rgbOffs1 > FbMask) _rgbOffs1 = FbMask;
        if (_rgbOffs2 > FbMask) _rgbOffs2 = FbMask;
        if (_auxOffs > FbMask) _auxOffs = FbMask;
        if (_rgbOffs2 < 0) { if (_front == 2) _front = 0; if (_back == 2) _back = 0; }
    }
    private int BufferOffset(int n) => n switch { 0 => _rgbOffs0, 1 => _rgbOffs1, _ => _rgbOffs2 };
    /// <summary>The draw buffer for fbzMode/lfbMode's 2-bit select (0 front, 1 back), or -1.</summary>
    private int DrawBuffer(int sel) => sel switch { 0 => BufferOffset(_front), 1 => BufferOffset(_back), _ => -1 };
    private int LfbBuffer(int sel) => sel switch { 0 => BufferOffset(_front), 1 => BufferOffset(_back), 2 => _auxOffs, _ => -1 };
    private ushort Fb16(int byteOff) { byteOff &= FbMask & ~1; return (ushort)(Fb[byteOff] | Fb[byteOff + 1] << 8); }
    private void Fb16(int byteOff, int v) { byteOff &= FbMask & ~1; Fb[byteOff] = (byte)v; Fb[byteOff + 1] = (byte)(v >> 8); }

    // ================= PCI memory window =================

    public uint MmioRead(uint off, int size)
    {
        uint d;
        if (off < 0x400000) d = RegRead(off >> 2);
        else if (off < 0x800000) d = LfbRead((off - 0x400000) >> 2);
        else d = 0xffffffff;
        d >>= (int)(off & 3) * 8;
        return size == 4 ? d : size == 2 ? d & 0xffff : d & 0xff;
    }

    public void MmioWrite(uint off, uint v, int size)
    {
        uint sh = (off & 3) * 8, mask = size == 4 ? 0xffffffff : size == 2 ? 0xffffu << (int)sh : 0xffu << (int)sh;
        uint data = v << (int)sh;
        if (off < 0x400000) RegWrite(off >> 2, data);   // registers: partial writes land as the whole dword (as the PCI byte enables would on real cards, roughly)
        else if (off < 0x800000) LfbWrite((off - 0x400000) >> 2, data, mask);
        else TextureWrite((off - 0x800000) >> 2, data);
    }

    // ================= registers =================

    private struct Entry { public uint Mask; public byte Chips; public byte Kind; }
    // kinds
    private const byte KInvalid = 0, KPassive = 1, KStatus = 2, KStartS = 3, KStartT = 4, KStartW = 5, KdSdX = 6, KdTdX = 7, KdWdX = 8, KdSdY = 9, KdTdY = 10, KdWdY = 11,
        KTriangle = 12, KFPassive4 = 13, KFPassive12 = 14, KFStartS = 15, KFStartT = 16, KFStartW = 17, KFdSdX = 18, KFdTdX = 19, KFdWdX = 20, KFdSdY = 21, KFdTdY = 22, KFdWdY = 23,
        KNop = 24, KFastfill = 25, KSwap = 26, KFogTable = 27, KFbiInit = 28, KVideo = 29, KClut = 30, KDac = 31, KTexture = 32, KPalette = 33, KStats = 34, KVRetrace = 35, KUnimpl = 36;
    private const byte CFbi = 1, CTrex = 6, CFbiTrex = 7;
    private static readonly Entry[] Table = BuildTable();
    private static Entry[] BuildTable()
    {
        var t = new Entry[256];
        for (int i = 0; i < 256; i++) t[i] = new Entry { Mask = 0xffffffff, Chips = CFbi, Kind = KInvalid };
        static uint M(int bits) => bits == 0 ? 0 : bits == 32 ? 0xffffffff : (1u << bits) - 1;
        void E(int byteOff, byte kind, int bits, byte chips) => t[byteOff / 4] = new Entry { Mask = M(bits), Chips = chips, Kind = kind };
        E(0x000, KStatus, 32, CFbi);
        for (int o = 0x008; o <= 0x01c; o += 4) E(o, KPassive, 16, CFbiTrex);
        foreach (int o in new[] { 0x020, 0x024, 0x028, 0x030, 0x040, 0x044, 0x048, 0x050, 0x060, 0x064, 0x068, 0x070 }) E(o, KPassive, 24, CFbi);
        foreach (int o in new[] { 0x02c, 0x04c, 0x06c }) E(o, KPassive, 32, CFbi);
        E(0x034, KStartS, 32, CTrex); E(0x038, KStartT, 32, CTrex); E(0x03c, KStartW, 32, CFbiTrex);
        E(0x054, KdSdX, 32, CTrex); E(0x058, KdTdX, 32, CTrex); E(0x05c, KdWdX, 32, CFbiTrex);
        E(0x074, KdSdY, 32, CTrex); E(0x078, KdTdY, 32, CTrex); E(0x07c, KdWdY, 32, CFbiTrex);
        E(0x080, KTriangle, 32, CFbiTrex);
        for (int o = 0x088; o <= 0x09c; o += 4) E(o, KFPassive4, 32, CFbiTrex);
        foreach (int o in new[] { 0x0a0, 0x0a4, 0x0a8, 0x0ac, 0x0b0, 0x0c0, 0x0c4, 0x0c8, 0x0cc, 0x0d0, 0x0e0, 0x0e4, 0x0e8, 0x0ec, 0x0f0 }) E(o, KFPassive12, 32, CFbi);
        E(0x0b4, KFStartS, 32, CTrex); E(0x0b8, KFStartT, 32, CTrex); E(0x0bc, KFStartW, 32, CFbiTrex);
        E(0x0d4, KFdSdX, 32, CTrex); E(0x0d8, KFdTdX, 32, CTrex); E(0x0dc, KFdWdX, 32, CFbiTrex);
        E(0x0f4, KFdSdY, 32, CTrex); E(0x0f8, KFdTdY, 32, CTrex); E(0x0fc, KFdWdY, 32, CFbiTrex);
        E(0x100, KTriangle, 32, CFbiTrex);
        E(0x104, KPassive, 28, CFbiTrex); E(0x108, KPassive, 6, CFbiTrex); E(0x10c, KPassive, 32, CFbiTrex); E(0x110, KPassive, 21, CFbiTrex);
        E(0x114, KPassive, 17, CFbiTrex); E(0x118, KPassive, 26, CFbiTrex); E(0x11c, KPassive, 26, CFbiTrex);
        E(0x120, KNop, 1, CFbiTrex); E(0x124, KFastfill, 0, CFbi); E(0x128, KSwap, 9, CFbi);
        E(0x12c, KPassive, 24, CFbi); E(0x130, KPassive, 32, CFbi); E(0x134, KPassive, 24, CFbi);
        E(0x140, KPassive, 32, CFbi); E(0x144, KPassive, 32, CFbi); E(0x148, KPassive, 32, CFbi);
        for (int o = 0x14c; o <= 0x15c; o += 4) E(o, KStats, 24, CFbi);
        for (int o = 0x160; o <= 0x1dc; o += 4) E(o, KFogTable, 32, CFbi);
        E(0x200, KFbiInit, 28, CFbi); E(0x204, KVRetrace, 12, CFbi); E(0x208, KVideo, 24, CFbi); E(0x20c, KVideo, 26, CFbi);
        E(0x210, KFbiInit, 31, CFbi); E(0x214, KFbiInit, 32, CFbi); E(0x218, KFbiInit, 32, CFbi); E(0x21c, KFbiInit, 32, CFbi);
        E(0x220, KVideo, 26, CFbi); E(0x224, KVideo, 28, CFbi); E(0x228, KClut, 30, CFbi); E(0x22c, KDac, 12, CFbi); E(0x230, KUnimpl, 24, CFbi);
        E(0x300, KTexture, 32, CTrex); E(0x304, KTexture, 32, CTrex); E(0x308, KTexture, 17, CTrex);
        for (int o = 0x30c; o <= 0x318; o += 4) E(o, KTexture, 19, CTrex);
        E(0x31c, KPassive, 32, CTrex); E(0x320, KPassive, 32, CTrex);
        for (int o = 0x324; o <= 0x380; o += 4) E(o, KPalette, 32, CTrex);
        return t;
    }
    private static readonly byte[] AliasMap = BuildAlias();
    private static byte[] BuildAlias()
    {
        int[] a =
        {
            0x000, 0x004, 0x008, 0x00c, 0x010, 0x014, 0x018, 0x01c,
            0x020, 0x040, 0x060, 0x024, 0x044, 0x064, 0x028, 0x048,
            0x068, 0x02c, 0x04c, 0x06c, 0x030, 0x050, 0x070, 0x034,
            0x054, 0x074, 0x038, 0x058, 0x078, 0x03c, 0x05c, 0x07c,
            0x080, 0x084, 0x088, 0x08c, 0x090, 0x094, 0x098, 0x09c,
            0x0a0, 0x0c0, 0x0e0, 0x0a4, 0x0c4, 0x0e4, 0x0a8, 0x0c8,
            0x0e8, 0x0ac, 0x0cc, 0x0ec, 0x0b0, 0x0d0, 0x0f0, 0x0b4,
            0x0d4, 0x0f4, 0x0b8, 0x0d8, 0x0f8, 0x0bc, 0x0dc, 0x0fc,
        };
        var r = new byte[64];
        for (int i = 0; i < 64; i++) r[i] = (byte)(a[i] / 4);
        return r;
    }

    private static int ChipsFromOffset(uint dword) { int c = (int)(dword >> 8) & 0xf; if (c == 0) c = 0xf; return c & ChipMask; }

    private uint RegRead(uint dword)
    {
        int chips = ChipsFromOffset(dword), reg = (int)(dword & 0xff);
        var e = Table[reg];
        uint r = e.Kind switch
        {
            KStatus => Status(),
            KVRetrace => VRetrace(),
            KPassive or KFbiInit or KVideo or KStats => reg == RFbiInit2 && (InitEnable & 4) != 0 ? _dacRead : Reg[reg],
            _ => 0,
        };
        return r & e.Mask;
    }

    private uint VRetrace() { uint r = InVBlank ? 0u : (uint)Math.Min((long)(_readsSinceRetrace - VBlankReads + 1) * 16, Math.Max(1, Height) - 1); CountRead(); return r; }

    private uint Status()
    {
        bool vb = InVBlank; CountRead();
        uint r = 0x3f;                                       // PCI FIFO free (never fills)
        if (vb) r |= 1 << 6;
        r |= (uint)_front << 10;
        r |= 0xffffu << 12;                                  // memory FIFO free
        r |= (uint)Math.Min(_swapsPending, 7) << 28;
        return r;
    }

    public long Triangles, Pixels;

    private void RegWrite(uint dword, uint data)
    {
        int chips = ChipsFromOffset(dword);
        int reg = (int)(dword & 0xff);
        if ((dword & (1 << 18)) != 0 && (Reg[RFbiInit0] & 8) != 0) data = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(data);
        if ((dword & (1 << 19)) != 0 && (Reg[RFbiInit3] & 1) != 0 && reg < 0x40) reg = AliasMap[reg];
        var e = Table[reg];
        if (reg == RSwapbufferCmd) _swapsPending++;
        chips &= e.Chips;
        data &= e.Mask;
        bool fbi = (chips & 1) != 0, tmu = (chips & 2) != 0;
        switch (e.Kind)
        {
            case KPassive: if (fbi) Reg[reg] = data; if (tmu) TReg[reg] = data; break;
            case KFPassive4: { uint v = (uint)FloatToInt32(data, 4); int r = reg - 0x80 / 4; if (fbi) Reg[r] = v & Table[r].Mask; if (tmu) TReg[r] = v & Table[r].Mask; break; }
            case KFPassive12: { uint v = (uint)FloatToInt32(data, 12); int r = reg - 0x80 / 4; if (fbi) Reg[r] = v & Table[r].Mask; if (tmu) TReg[r] = v & Table[r].Mask; break; }
            case KStartS: if (tmu) _tS = (long)(int)data << 14; break;
            case KStartT: if (tmu) _tT = (long)(int)data << 14; break;
            case KdSdX: if (tmu) _tdSdX = (long)(int)data << 14; break;
            case KdTdX: if (tmu) _tdTdX = (long)(int)data << 14; break;
            case KdSdY: if (tmu) _tdSdY = (long)(int)data << 14; break;
            case KdTdY: if (tmu) _tdTdY = (long)(int)data << 14; break;
            case KFStartS: if (tmu) _tS = FloatToInt64(data, 32); break;
            case KFStartT: if (tmu) _tT = FloatToInt64(data, 32); break;
            case KFdSdX: if (tmu) _tdSdX = FloatToInt64(data, 32); break;
            case KFdTdX: if (tmu) _tdTdX = FloatToInt64(data, 32); break;
            case KFdSdY: if (tmu) _tdSdY = FloatToInt64(data, 32); break;
            case KFdTdY: if (tmu) _tdTdY = FloatToInt64(data, 32); break;
            case KStartW: { long d = (long)(int)data << 2; if (fbi) _startW = d; if (tmu) _tW = d; break; }
            case KdWdX: { long d = (long)(int)data << 2; if (fbi) _dwdx = d; if (tmu) _tdWdX = d; break; }
            case KdWdY: { long d = (long)(int)data << 2; if (fbi) _dwdy = d; if (tmu) _tdWdY = d; break; }
            case KFStartW: { long d = FloatToInt64(data, 32); if (fbi) _startW = d; if (tmu) _tW = d; break; }
            case KFdWdX: { long d = FloatToInt64(data, 32); if (fbi) _dwdx = d; if (tmu) _tdWdX = d; break; }
            case KFdWdY: { long d = FloatToInt64(data, 32); if (fbi) _dwdy = d; if (tmu) _tdWdY = d; break; }
            case KTriangle: Triangle(); break;
            case KNop: if ((data & 1) != 0) { for (int r = RFbiPixelsIn; r <= RFbiPixelsOut; r++) Reg[r] = 0; } break;
            case KFastfill: Fastfill(); break;
            case KSwap:
                _vblankSwapPending = true;
                _vblankSwap = (int)((data >> 1) & 0xff);
                if ((data & 1) == 0) SwapBuffers();
                break;
            case KFogTable:
                if (fbi) { int b = 2 * (reg - RFogTable); _fogDelta[b] = (byte)data; _fogBlend[b] = (byte)(data >> 8); _fogDelta[b + 1] = (byte)(data >> 16); _fogBlend[b + 1] = (byte)(data >> 24); }
                break;
            case KFbiInit:
                if (fbi && (InitEnable & 1) != 0)
                {
                    Reg[reg] = data;
                    if (reg == RFbiInit1 || reg == RFbiInit2) RecomputeVideoMemory();
                }
                break;
            case KVideo: if (fbi) Reg[reg] = data; break;
            case KClut:
                if (fbi && (Reg[RFbiInit1] & (1 << 8)) == 0)
                {
                    int idx = (int)(data >> 24);
                    if (idx <= 32 && _clut[idx] != data) { _clut[idx] = data; _clutDirty = true; }
                }
                break;
            case KDac:
                if (fbi)
                {
                    int r = (int)((data >> 8) & 7);
                    if ((data & 0x800) == 0) _dac[r] = (byte)data;
                    else
                    {
                        _dacRead = _dac[r];
                        switch (_dac[7]) { case 0x01: _dacRead = 0x55; break; case 0x07: _dacRead = 0x71; break; case 0x0b: _dacRead = 0x79; break; }
                    }
                }
                break;
            case KTexture: if (tmu && TReg[reg] != data) { TReg[reg] = data; _texDirty = true; } break;
            case KPalette: if (tmu) NccWrite(reg, data); break;
        }
    }

    private void NccWrite(int reg, uint data)
    {
        int idx = reg - RNccTable;
        if ((data & 0x80000000) != 0 && idx >= 4 && idx < 12)
        {
            int p = (int)((data >> 24) & 0x7f) << 1 | (idx & 1);
            uint rgb = 0xff000000 | data;
            uint argb = Raster3D.Argb(Raster3D.Pal6((int)(data >> 18)), Raster3D.Pal6((int)(data >> 12)), Raster3D.Pal6((int)(data >> 6)), Raster3D.Pal6((int)data));
            if (_palette[p] != rgb) { _palette[p] = rgb; _paletteDirty |= 1; }
            if (_paletteA[p] != argb) { _paletteA[p] = argb; _paletteDirty |= 2; }
            return;
        }
        if (TReg[reg] == data) return;
        TReg[reg] = data;
        _paletteDirty |= 1 << (2 + idx / 12);
    }

    public static int FloatToInt32(uint data, int fixedbits)
    {
        int exponent = (int)((data >> 23) & 0xff) - 127 - 23 + fixedbits;
        int result = (int)(data & 0x7fffff) | 0x800000;
        if (exponent < 0) result = exponent > -32 ? result >> -exponent : 0;
        else result = exponent < 32 ? result << exponent : 0x7fffffff;
        return (data & 0x80000000) != 0 ? -result : result;
    }
    public static long FloatToInt64(uint data, int fixedbits)
    {
        int exponent = (int)((data >> 23) & 0xff) - 127 - 23 + fixedbits;
        long result = (data & 0x7fffff) | 0x800000;
        if (exponent < 0) result = exponent > -64 ? result >> -exponent : 0;
        else result = exponent < 64 ? result << exponent : 0x7fffffffffffffff;
        return (data & 0x80000000) != 0 ? -result : result;
    }

    // ================= LFB =================

    private int YOrigin => (int)((Reg[RFbiInit3] >> 22) & 0x3ff);

    private uint LfbRead(uint dword)
    {
        uint offset = dword << 1;
        int x = (int)(offset & 1023), y = (int)(offset >> 10) & 0x3ff;
        uint lfb = Reg[RLfbMode];
        if ((lfb & (1 << 13)) != 0) y = YOrigin - y;
        int buf = LfbBuffer((int)(lfb >> 6) & 3);
        if (buf < 0) return 0xffffffff;
        int p = buf + (y * _rowPixels + x) * 2;
        if (p + 3 >= FbBytes || p < 0) return 0xffffffff;
        uint data = (uint)(Fb16(p) | Fb16(p + 2) << 16);
        if ((lfb & (1 << 15)) != 0) data = data << 16 | data >> 16;
        if ((lfb & (1 << 16)) != 0) data = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(data);
        return data;
    }

    private const int RgbP0 = 1, AlphaP0 = 2, DepthP0 = 4, DepthMsw0 = 8, Pixel0Mask = 0xf, Pixel1Mask = 0xf0;

    private void LfbWrite(uint dword, uint data, uint memMask)
    {
        uint lfb = Reg[RLfbMode];
        if ((lfb & (1 << 12)) != 0) { data = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(data); memMask = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(memMask); }
        if ((lfb & (1 << 11)) != 0) { data = data << 16 | data >> 16; memMask = memMask << 16 | memMask >> 16; }
        Span<uint> col = stackalloc uint[2]; Span<ushort> dep = stackalloc ushort[2];
        int mask = ExpandLfb(lfb, data, col, dep);
        uint offset = dword;
        if ((mask & Pixel1Mask) != 0) offset <<= 1;
        int x = (int)(offset & 1023), y = (int)(offset >> 10) & 0x3ff;
        if ((memMask & 0xffff) == 0) mask &= ~(Pixel0Mask - DepthMsw0);
        if ((memMask & 0xffff0000) == 0) mask &= ~(Pixel1Mask + DepthMsw0);
        int dest = DrawBuffer((int)(lfb >> 4) & 3);
        if (dest < 0) return;
        int depth = _auxOffs;
        uint fbz = Reg[RFbzMode];
        if ((lfb & (1 << 8)) == 0)
        {
            int scry = (lfb & (1 << 13)) != 0 ? YOrigin - y : y;
            var dither = new Dither(scry, fbz);
            for (int pix = 0; mask != 0; pix++)
            {
                if ((mask & Pixel0Mask) != 0)
                {
                    int o = (scry * _rowPixels + x) * 2;
                    uint c = col[pix];
                    if ((mask & RgbP0) != 0 && dest + o < FbBytes && dest + o >= 0) Fb16(dest + o, dither.Pixel(x, (int)(c >> 16) & 0xff, (int)(c >> 8) & 0xff, (int)c & 0xff));
                    if (depth >= 0 && depth + o < FbBytes && depth + o >= 0)
                    {
                        if ((fbz & (1 << 18)) != 0) { if ((mask & AlphaP0) != 0) Fb16(depth + o, (int)(c >> 24)); }
                        else if ((mask & (DepthP0 | DepthMsw0)) != 0) Fb16(depth + o, dep[pix]);
                    }
                    Reg[RFbiPixelsOut]++;
                }
                x++;
                mask >>= 4;
            }
        }
        else
        {
            int scry = (fbz & (1 << 17)) != 0 ? YOrigin - y : y;
            for (int pix = 0; mask != 0; pix++)
            {
                if ((mask & Pixel0Mask) != 0) PixelPipeline(dest, depth, x, scry, col[pix], dep[pix]);
                x++;
                mask >>= 4;
            }
        }
    }

    private static uint RgbExp(uint d, int bits0, int sh0, int bits1, int sh1, int bits2, int sh2, int a)
        => Raster3D.Argb(a, Exp(d >> sh0, bits0), Exp(d >> sh1, bits1), Exp(d >> sh2, bits2));
    private static int Exp(uint v, int bits) => bits switch { 1 => Raster3D.Pal1((int)v), 4 => Raster3D.Pal4((int)v), 5 => Raster3D.Pal5((int)v), 6 => Raster3D.Pal6((int)v), _ => (int)v & 0xff };
    private static uint ArgbExp(uint d, int ba, int sa, int b, int sr, int sg, int sb)
        => Raster3D.Argb(Exp(d >> sa, ba), Exp(d >> sr, b), Exp(d >> sg, b), Exp(d >> sb, b));

    private int ExpandLfb(uint lfb, uint d, Span<uint> c, Span<ushort> dep)
    {
        dep[0] = dep[1] = (ushort)Reg[RZaColor];
        int sa = (int)(Reg[RZaColor] >> 24);
        int lanes = (int)(lfb >> 9) & 3, fmt = (int)lfb & 0xf;
        switch (16 * lanes + fmt)
        {
            case 0: case 32: c[0] = RgbExp(d, 5, 11, 6, 5, 5, 0, sa); c[1] = RgbExp(d, 5, 27, 6, 21, 5, 16, sa); return RgbP0 | RgbP0 << 4;
            case 16: case 48: c[0] = RgbExp(d, 5, 0, 6, 5, 5, 11, sa); c[1] = RgbExp(d, 5, 16, 6, 21, 5, 27, sa); return RgbP0 | RgbP0 << 4;
            case 1: c[0] = RgbExp(d, 5, 10, 5, 5, 5, 0, sa); c[1] = RgbExp(d, 5, 26, 5, 21, 5, 16, sa); return RgbP0 | RgbP0 << 4;
            case 17: c[0] = RgbExp(d, 5, 0, 5, 5, 5, 10, sa); c[1] = RgbExp(d, 5, 16, 5, 21, 5, 26, sa); return RgbP0 | RgbP0 << 4;
            case 33: c[0] = RgbExp(d, 5, 11, 5, 6, 5, 1, sa); c[1] = RgbExp(d, 5, 27, 5, 22, 5, 17, sa); return RgbP0 | RgbP0 << 4;
            case 49: c[0] = RgbExp(d, 5, 1, 5, 6, 5, 11, sa); c[1] = RgbExp(d, 5, 17, 5, 22, 5, 27, sa); return RgbP0 | RgbP0 << 4;
            case 2: c[0] = ArgbExp(d, 1, 15, 5, 10, 5, 0); c[1] = ArgbExp(d, 1, 31, 5, 26, 21, 16); return 3 | 3 << 4;
            case 18: c[0] = ArgbExp(d, 1, 15, 5, 0, 5, 10); c[1] = ArgbExp(d, 1, 31, 5, 16, 21, 26); return 3 | 3 << 4;
            case 34: c[0] = ArgbExp(d, 1, 0, 5, 11, 6, 1); c[1] = ArgbExp(d, 1, 16, 5, 27, 22, 17); return 3 | 3 << 4;
            case 50: c[0] = ArgbExp(d, 1, 0, 5, 1, 6, 11); c[1] = ArgbExp(d, 1, 16, 5, 17, 22, 27); return 3 | 3 << 4;
            case 4: c[0] = RgbExp(d, 8, 16, 8, 8, 8, 0, sa); return RgbP0;
            case 20: c[0] = RgbExp(d, 8, 0, 8, 8, 8, 16, sa); return RgbP0;
            case 36: c[0] = RgbExp(d, 8, 24, 8, 16, 8, 8, sa); return RgbP0;
            case 52: c[0] = RgbExp(d, 8, 8, 8, 16, 8, 24, sa); return RgbP0;
            case 5: c[0] = ArgbExp(d, 8, 24, 8, 16, 8, 0); return 3;
            case 21: c[0] = ArgbExp(d, 8, 24, 8, 0, 8, 16); return 3;
            case 37: c[0] = ArgbExp(d, 8, 0, 8, 24, 16, 8); return 3;
            case 53: c[0] = ArgbExp(d, 8, 0, 8, 8, 16, 24); return 3;
            case 12: case 44: c[0] = RgbExp(d, 5, 11, 6, 5, 5, 0, sa); dep[0] = (ushort)(d >> 16); return RgbP0 | DepthMsw0;
            case 28: case 60: c[0] = RgbExp(d, 5, 0, 6, 5, 5, 11, sa); dep[0] = (ushort)(d >> 16); return RgbP0 | DepthMsw0;
            case 13: c[0] = RgbExp(d, 5, 10, 5, 5, 5, 0, sa); dep[0] = (ushort)(d >> 16); return RgbP0 | DepthMsw0;
            case 29: c[0] = RgbExp(d, 5, 0, 5, 5, 5, 10, sa); dep[0] = (ushort)(d >> 16); return RgbP0 | DepthMsw0;
            case 45: c[0] = RgbExp(d, 5, 11, 5, 6, 5, 1, sa); dep[0] = (ushort)(d >> 16); return RgbP0 | DepthMsw0;
            case 61: c[0] = RgbExp(d, 5, 1, 5, 6, 5, 11, sa); dep[0] = (ushort)(d >> 16); return RgbP0 | DepthMsw0;
            case 14: c[0] = ArgbExp(d, 1, 15, 5, 10, 5, 0); dep[0] = (ushort)(d >> 16); return 3 | DepthMsw0;
            case 30: c[0] = ArgbExp(d, 1, 15, 5, 0, 5, 10); dep[0] = (ushort)(d >> 16); return 3 | DepthMsw0;
            case 46: c[0] = ArgbExp(d, 1, 0, 5, 11, 6, 1); dep[0] = (ushort)(d >> 16); return 3 | DepthMsw0;
            case 62: c[0] = ArgbExp(d, 1, 0, 5, 1, 6, 11); dep[0] = (ushort)(d >> 16); return 3 | DepthMsw0;
            case 15: case 31: case 47: case 63: dep[0] = (ushort)d; dep[1] = (ushort)(d >> 16); return DepthP0 | DepthP0 << 4;
            default: return 0;
        }
    }

    // ================= texture memory writes =================

    private void TextureWrite(uint dword, uint data)
    {
        int tmu = (int)(dword >> 19) & 3;
        if (tmu != 0) return;
        bool seq8 = (TReg[RTextureMode] & 0x80000000) != 0;
        uint tlod = TReg[RTLod], tmode = TReg[RTextureMode];
        PrepareTexture();
        if ((tlod & (1 << 25)) != 0) data = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(data);
        if ((tlod & (1 << 26)) != 0) data = data >> 16 | data << 16;
        int bpt = ((tmode >> 8) & 0xf) < 8 ? 1 : 2;
        int lod = (int)(dword >> 15) & 0xf, tt = (int)(dword >> 7) & 0xff;
        int ts = (int)(dword << (seq8 && bpt == 1 ? 2 : 1)) & 0xff;
        if (lod > 8) return;
        int offs = tt * ((_tex.WMask >> lod) + 1) + ts;
        int p = (int)((_tex.LodOffset[lod] + ((bpt * offs) & ~3)) & TexMask);
        Tex[p] = (byte)data; Tex[(p + 1) & TexMask] = (byte)(data >> 8); Tex[(p + 2) & TexMask] = (byte)(data >> 16); Tex[(p + 3) & TexMask] = (byte)(data >> 24);
    }

    // ================= savestate =================

    public void Save(BinaryWriter w)
    {
        w.Write(3);   // version
        foreach (var v in Reg) w.Write(v);
        foreach (var v in TReg) w.Write(v);
        foreach (var v in new[] { _startW, _dwdx, _dwdy, _tS, _tT, _tW, _tdSdX, _tdTdX, _tdWdX, _tdSdY, _tdTdY, _tdWdY }) w.Write(v);
        w.Write(Fb); w.Write(Tex); w.Write(_dac); w.Write(_dacRead);
        foreach (var v in _clut) w.Write(v);
        w.Write(_fogBlend); w.Write(_fogDelta);
        foreach (var v in _palette) w.Write(v);
        foreach (var v in _paletteA) w.Write(v);
        w.Write(_front); w.Write(_back); w.Write(_swapsPending); w.Write(_vblankCount); w.Write(_vblankSwap); w.Write(_vblankSwapPending); w.Write(_lastFrame); w.Write(_readsSinceRetrace);
        w.Write(Triangles); w.Write(Pixels);
    }

    public void Load(BinaryReader r)
    {
        int ver = r.ReadInt32(); if (ver is not (2 or 3)) throw new InvalidDataException("this x86 state's Voodoo block version is not known");
        for (int i = 0; i < 256; i++) Reg[i] = r.ReadUInt32();
        for (int i = 0; i < 256; i++) TReg[i] = r.ReadUInt32();
        _startW = r.ReadInt64(); _dwdx = r.ReadInt64(); _dwdy = r.ReadInt64(); _tS = r.ReadInt64(); _tT = r.ReadInt64(); _tW = r.ReadInt64();
        _tdSdX = r.ReadInt64(); _tdTdX = r.ReadInt64(); _tdWdX = r.ReadInt64(); _tdSdY = r.ReadInt64(); _tdTdY = r.ReadInt64(); _tdWdY = r.ReadInt64();
        r.Read(Fb, 0, FbBytes); r.Read(Tex, 0, TexBytes); r.Read(_dac, 0, 8); _dacRead = r.ReadUInt32();
        for (int i = 0; i < 33; i++) _clut[i] = r.ReadUInt32();
        r.Read(_fogBlend, 0, 64); r.Read(_fogDelta, 0, 64);
        for (int i = 0; i < 256; i++) _palette[i] = r.ReadUInt32();
        for (int i = 0; i < 256; i++) _paletteA[i] = r.ReadUInt32();
        _front = r.ReadInt32(); _back = r.ReadInt32(); _swapsPending = r.ReadInt32(); _vblankCount = r.ReadInt32(); _vblankSwap = r.ReadInt32(); _vblankSwapPending = r.ReadBoolean(); _lastFrame = r.ReadInt64(); _readsSinceRetrace = ver >= 3 ? r.ReadInt32() : 0;
        Triangles = r.ReadInt64(); Pixels = r.ReadInt64();
        RecomputeVideoMemory();
        _clutDirty = true; _texDirty = true; _paletteDirty = 0xf;
    }
}
