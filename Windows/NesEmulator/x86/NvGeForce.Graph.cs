// GeForce 256: PGRAPH, the 2D object classes the NVIDIA Win9x driver uses (NV04-era classes, still on NV10), executed synchronously.
// Method numbers from envytools rnndb (graph classes) and the driver's own method log. Each object's methods are kept per PRAMIN
// instance; context methods (0x180-0x1A0: a handle) are resolved to the instance at once, so an operation finds its surfaces / clip /
// ROP / pattern objects by class.
//
// Classes: 0x19 clip, 0x39 memory to memory, 0x42 / 0x62 surfaces 2D, 0x43 ROP, 0x44 pattern, 0x4A GDI rectangle + text,
// 0x5F / 0x9F blit, 0x61 / 0x65 image from CPU. Not yet: 0x5C lines, 0x64 indexed image, scaled images, 3D.

using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;

namespace NesEmulator.X86;

public sealed partial class NvGeForce
{
    private readonly uint[] _notifyObj = new uint[8];
    private readonly SortedDictionary<ulong, uint> _gs = new();   // (instance << 16 | method) -> last value written (| 0x8000: a context's instance)
    private int _ifcPix;                                          // pixels of the current image-from-CPU / text transfer done
    public long GraphOps;                                         // diagnostics
    public Action<string>? OpLog;

    private uint G(uint inst, int m) => _gs.TryGetValue((ulong)inst << 16 | (uint)m, out var v) ? v : 0;
    private void GSet(uint inst, int m, uint v) => _gs[(ulong)inst << 16 | (uint)m] = v;
    private int ClassAt(uint inst) => Alias((int)(Ramin32(inst) & 0xfff));
    /// <summary>The NV30 (FX) 2D classes keep the NV04/NV10 method layouts (envytools nv_object.xml): run them as those.</summary>
    // (X86_NVNOALIAS=362,389,...: diagnostics, leave those classes unaliased)
    private static readonly HashSet<int> NoAlias = new((Environment.GetEnvironmentVariable("X86_NVNOALIAS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => Convert.ToInt32(x.Trim(), 16)));
    private static int Alias(int c) => NoAlias.Contains(c) ? c : c switch { 0x362 => 0x62, 0x389 => 0x89, 0x39e => 0x52, 0x37b => 0x7b, 0x38a => 0x61, 0x366 => 0x66, 0x364 => 0x64, _ => c };

    /// <summary>The context object of one of these classes an object was given (methods 0x184-0x1A0), or 0.</summary>
    private uint Ctx(uint inst, params int[] classes)
    {
        for (int m = 0x184; m <= 0x1a0; m += 4)
        {
            uint ci = G(inst, m | 0x8000);
            if (ci != 0 && Array.IndexOf(classes, ClassAt(ci)) >= 0) return ci;
        }
        return 0;
    }

    private void WriteNotify(int subc, uint inst, int index)
    {
        uint n = G(inst, 0x180 | 0x8000);
        if (n == 0) n = _notifyObj[subc];
        if (n == 0) return;
        long ns = TimeNs(0);
        uint o = (uint)index * 16;
        DmaWrite32(n, o, (uint)ns); DmaWrite32(n, o + 4, (uint)(ns >> 32));
        DmaWrite32(n, o + 8, 0); DmaWrite32(n, o + 12, 0);   // return value 0, status 0 = done
    }

    private void Graph(int subc, uint inst, int cls, int mthd, uint data)
    {
        GraphOps++;
        GSet(inst, mthd, data);
        if (cls is 0x44 or 0x18 && mthd is >= 0x400 and < 0x800) GSet(inst, 0x7ffc, (uint)(mthd & 0x700));   // which colour pattern block was loaded
        if (mthd is >= 0x180 and <= 0x1a0 || (IsRankine(cls) && mthd is > 0x1a0 and <= 0x1b0)) { uint ci = RamhtLookup(data, ActiveChid); GSet(inst, mthd | 0x8000, ci); if (mthd == 0x180) _notifyObj[subc] = ci; return; }
        if (mthd == 0x104) { WriteNotify(subc, inst, 0); return; }
        if (mthd == 0x100) return;
        switch (cls)
        {
            case 0x39: if (mthd == 0x328) { M2mf(inst); WriteNotify(subc, inst, 1); } break;
            case 0x4a or 0x4b: Gdi(inst, mthd, data); break;
            case 0x5f or 0x9f: if (mthd == 0x308) Blit(inst); break;
            case 0x61 or 0x65 or 0x21: Ifc(inst, mthd, data); break;
            case 0x60 or 0x64: Index(inst, mthd, data); break;
            case 0x56 or 0x96 or 0x98 or 0x99: Celsius(inst, mthd, data); break;
            case 0x7b: SwzUpload(inst, mthd, data); break;
            case 0x397 or 0x497 or 0x697: Rankine(inst, mthd, data); break;
            case 0x77 or 0x89 or 0x63: if (mthd == 0x40c) Sifm(inst); break;
            case 0x5e or 0x1e: if (mthd is >= 0x400 and < 0x480 && (mthd & 4) != 0) SolidRect(inst, mthd, data); break;
        }
    }

    // ================= surfaces =================

    private readonly struct Surf
    {
        public readonly uint Base; public readonly int Pitch, Bpp; public readonly bool Ok;
        public Surf(uint b, int p, int bpp) { Base = b; Pitch = p; Bpp = bpp; Ok = bpp != 0 && p != 0; }
    }

    private static int FmtBpp(uint f) => f switch { 1 => 1, 2 or 3 or 4 or 5 => 2, 6 or 7 or 8 or 9 or 0xa or 0xb => 4, _ => 0 };

    /// <summary>The destination (or source) surface of an object's surfaces context (0x42 / 0x62).</summary>
    private Surf SurfOf(uint inst, bool src)
    {
        uint s = Ctx(inst, 0x42, 0x62);
        if (s == 0) return default;
        int bpp = FmtBpp(G(s, 0x300) & 0xff);
        uint pitches = G(s, 0x304);
        int pitch = (int)(src ? pitches & 0xffff : pitches >> 16);
        uint off = G(s, src ? 0x308 : 0x30c);
        uint dma = G(s, (src ? 0x184 : 0x188) | 0x8000);
        uint a = dma != 0 ? DmaAddr(dma, off).addr : off;
        return new Surf(a & (VramMask), pitch, bpp);
    }

    private uint Px(in Surf s, int x, int y)
    {
        uint a = (uint)(s.Base + y * s.Pitch + x * s.Bpp) & (VramMask);
        var r = _v.Ram;
        return s.Bpp switch { 1 => r[a], 2 => (uint)(r[a] | r[(a + 1) & (VramMask)] << 8), _ => a <= VramBytes - 4 ? BitConverter.ToUInt32(r, (int)a) : 0 };
    }

    private void Put(in Surf s, int x, int y, uint v)
    {
        uint a = (uint)(s.Base + y * s.Pitch + x * s.Bpp) & (VramMask);
        var r = _v.Ram;
        switch (s.Bpp)
        {
            case 1: r[a] = (byte)v; break;
            case 2: r[a] = (byte)v; r[(a + 1) & (VramMask)] = (byte)(v >> 8); break;
            default: if (a <= VramBytes - 4) BitConverter.TryWriteBytes(r.AsSpan((int)a), v); break;
        }
    }

    // ================= clip, ROP, pattern =================

    private (int x0, int y0, int x1, int y1) ClipOf(uint inst)
    {
        uint c = Ctx(inst, 0x19);
        if (c == 0) return (0, 0, 0x7fff, 0x7fff);
        uint p = G(c, 0x300), z = G(c, 0x304);
        int x = (short)(p & 0xffff), y = (short)(p >> 16);
        return (x, y, x + (int)(z & 0xffff), y + (int)(z >> 16));
    }

    private int Rop(uint inst)
    {
        int op = (int)G(inst, 0x2fc);
        if (op != 1) return 0xcc;                    // SRCCOPY (operation 3; the AND / blend forms drawn as a copy for now)
        uint r = Ctx(inst, 0x43);
        return r != 0 ? (int)(G(r, 0x300) & 0xff) : 0xcc;
    }

    private uint Pattern(uint inst, int x, int y)
    {
        uint p = Ctx(inst, 0x44, 0x18);
        if (p == 0) return 0xffffffff;
        if (G(p, 0x30c) == 2)   // colour pattern: 8x8 at 0x400 (Y8, 4 a word), 0x500 (R5G6B5, 2 a word), 0x600 (X1R5G5B5), 0x700 (X8R8G8B8)
        {
            int i = (y & 7) * 8 + (x & 7);
            switch (G(p, 0x7ffc))
            {
                case 0x400: return (G(p, 0x400 + (i >> 2) * 4) >> ((i & 3) * 8)) & 0xff;
                case 0x500: case 0x600: return (G(p, (int)G(p, 0x7ffc) + (i >> 1) * 4) >> ((i & 1) * 16)) & 0xffff;
                default: return G(p, 0x700 + i * 4);
            }
        }
        uint b0 = G(p, 0x318), b1 = G(p, 0x31c);
        int bit = (y & 7) * 8 + (x & 7);
        bool on = bit < 32 ? ((b0 >> (bit ^ 7)) & 1) != 0 : ((b1 >> ((bit - 32) ^ 7)) & 1) != 0;
        return on ? G(p, 0x314) : G(p, 0x310);
    }

    private static uint Rop3(int rop, uint p, uint s, uint d)
    {
        switch (rop)
        {
            case 0xcc: return s; case 0xf0: return p; case 0x00: return 0; case 0xff: return 0xffffffff;
            case 0x55: return ~d; case 0x66: return s ^ d; case 0x5a: return p ^ d; case 0x88: return s & d; case 0xee: return s | d;
        }
        uint r = 0;
        for (int i = 0; i < 8; i++)
            if (((rop >> i) & 1) != 0) r |= ((i & 4) != 0 ? p : ~p) & ((i & 2) != 0 ? s : ~s) & ((i & 1) != 0 ? d : ~d);
        return r;
    }

    private void Draw(uint inst, in Surf d, int x, int y, uint src, int rop, (int x0, int y0, int x1, int y1) clip)
    {
        if (x < clip.x0 || y < clip.y0 || x >= clip.x1 || y >= clip.y1 || x < 0 || y < 0) return;
        uint v = rop == 0xcc ? src : Rop3(rop, Pattern(inst, x, y), src, Px(d, x, y));
        Put(d, x, y, v);
    }

    // ================= 0x39 memory to memory =================

    private void M2mf(uint inst)
    {
        uint din = G(inst, 0x184 | 0x8000), dout = G(inst, 0x188 | 0x8000);
        uint oin = G(inst, 0x30c), oout = G(inst, 0x310);
        int pin = (int)G(inst, 0x314), pout = (int)G(inst, 0x318), len = (int)G(inst, 0x31c), lines = (int)G(inst, 0x320);
        if (len <= 0 || lines <= 0 || lines > 1 << 16 || len > 1 << 20) return;
        if (OpLog != null && len * lines <= 16)
        {
            var (vi, ai) = DmaAddr(din, oin); var (vo, ao) = DmaAddr(dout, oout);
            OpLog($"m2mf small {len}x{lines} in {din:X}+{oin:X} -> {(vi ? "vram" : "sys")} {ai:X} [{Ramin32(din):X8} {Ramin32(din + 4):X8} {Ramin32(din + 8):X8}] = {DmaRead8(din, oin + 3):X2}{DmaRead8(din, oin + 2):X2}{DmaRead8(din, oin + 1):X2}{DmaRead8(din, oin):X2} out {dout:X}+{oout:X} -> {(vo ? "vram" : "sys")} {ao:X} [{Ramin32(dout):X8} {Ramin32(dout + 4):X8} {Ramin32(dout + 8):X8}]");
        }
        bool words = (len & 3) == 0 && ((oin | oout | (uint)pin | (uint)pout) & 3) == 0;   // whole 32-bit words (a register write stays one write)
        for (int l = 0; l < lines; l++)
            if (words) for (int i = 0; i < len; i += 4) DmaWrite32(dout, oout + (uint)(l * pout + i), DmaRead32(din, oin + (uint)(l * pin + i)));
            else for (int i = 0; i < len; i++)
                DmaWrite8(dout, oout + (uint)(l * pout + i), DmaRead8(din, oin + (uint)(l * pin + i)));
    }

    private byte DmaRead8(uint inst, uint off)
    {
        if (inst == 0) return 0;
        var (vr, a) = DmaAddr(inst, off);
        if (vr) return _v.Ram[a & (VramMask)];
        uint w = SysRead32?.Invoke(a & ~3u) ?? 0; return (byte)(w >> (int)((a & 3) * 8));
    }

    private void DmaWrite8(uint inst, uint off, byte b)
    {
        if (inst == 0) return;
        var (vr, a) = DmaAddr(inst, off);
        if (vr) { _v.Ram[a & (VramMask)] = b; return; }
        uint w = SysRead32?.Invoke(a & ~3u) ?? 0; int sh = (int)(a & 3) * 8;
        SysWrite32?.Invoke(a & ~3u, (w & ~(0xffu << sh)) | ((uint)b << sh));
    }

    // ================= 0x4A GDI rectangle and text =================

    private void Gdi(uint inst, int mthd, uint data)
    {
        var d = SurfOf(inst, false);
        if (!d.Ok) return;
        int rop = Rop(inst);
        if (mthd is >= 0x400 and < 0x500 && (mthd & 4) != 0)   // UNCLIPPED_RECTANGLE size (w << 16 | h) after the point (x << 16 | y)
        {
            uint pt = G(inst, mthd - 4);
            int x = (int)(pt >> 16), y = (int)(pt & 0xffff), w = (int)(data >> 16), h = (int)(data & 0xffff);
            uint col = G(inst, 0x3fc);
            var clip = ClipOf(inst);
            OpLog?.Invoke($"rectA {x},{y} {w}x{h} col {col:X} rop {rop:X2} surf {d.Base:X}/{d.Pitch}/{d.Bpp} clip {clip}");
            for (int yy = y; yy < y + h; yy++) for (int xx = x; xx < x + w; xx++) Draw(inst, d, xx, yy, col, rop, clip);
            return;
        }
        if (mthd is >= 0x600 and < 0x700 && (mthd & 4) != 0)   // CLIPPED_RECTANGLE: top left / bottom right (y << 16 | x), clip B 0x5F4 / 0x5F8, colour 0x5FC
        {
            uint tl = G(inst, mthd - 4);
            int x0 = (short)(tl & 0xffff), y0 = (short)(tl >> 16), x1 = (short)(data & 0xffff), y1 = (short)(data >> 16);
            var clip = Isect(ClipOf(inst), G(inst, 0x5f4), G(inst, 0x5f8));
            uint col = G(inst, 0x5fc);
            OpLog?.Invoke($"rectB {x0},{y0}-{x1},{y1} col {col:X} rop {rop:X2} surf {d.Base:X}/{d.Pitch} clip {clip}");
            for (int yy = y0; yy < y1; yy++) for (int xx = x0; xx < x1; xx++) Draw(inst, d, xx, yy, col, rop, clip);
            return;
        }
        if (mthd == 0x7fc) _ifcPix = 0;
        if (mthd is >= 0x800 and < 0xa00) MonoC(inst, d, rop, data);
    }

    private static (int x0, int y0, int x1, int y1) Isect((int x0, int y0, int x1, int y1) c, uint tl, uint br) =>
        (Math.Max(c.x0, (short)(tl & 0xffff)), Math.Max(c.y0, (short)(tl >> 16)), Math.Min(c.x1, (short)(br & 0xffff)), Math.Min(c.y1, (short)(br >> 16)));

    // mono text C (from the driver's use): size 0x7F8 (h << 16 | w), point 0x7FC (y << 16 | x), clip 0xFF4 / 0xFF8, colour 0xFFC,
    // bits 0x800+: one continuous bit stream, MSB first in each byte; a 0 bit leaves the pixel alone
    private void MonoC(uint inst, in Surf d, int rop, uint data)
    {
        uint sz = G(inst, 0x7f8), pt = G(inst, 0x7fc);
        int w = (int)(sz & 0xffff), h = (int)(sz >> 16), px = (short)(pt & 0xffff), py = (short)(pt >> 16);
        if (w <= 0) return;
        var clip = Isect(ClipOf(inst), G(inst, 0xff4), G(inst, 0xff8));
        uint col = G(inst, 0xffc);
        for (int b = 0; b < 32; b++, _ifcPix++)
        {
            int x = _ifcPix % w, y = _ifcPix / w;
            if (y >= h) return;
            if (((data >> (b ^ 7)) & 1) != 0) Draw(inst, d, px + x, py + y, col, rop, clip);
        }
    }

    // 0x5E solid rectangle (D3D Clear): colour 0x304, point 0x400 + 8i (y << 16 | x), size 0x404 + 8i (h << 16 | w)
    private void SolidRect(uint inst, int mthd, uint data)
    {
        var d = SurfOf(inst, false);
        if (!d.Ok) return;
        uint pt = G(inst, mthd - 4), col = G(inst, 0x304);
        int x = (short)(pt & 0xffff), y = (short)(pt >> 16), w = (int)(data & 0xffff), h = (int)(data >> 16);
        int rop = Rop(inst); var clip = ClipOf(inst);
        OpLog?.Invoke($"solid {x},{y} {w}x{h} col {col:X} surf {d.Base:X}/{d.Pitch}/{d.Bpp}");
        for (int yy = y; yy < y + h; yy++) for (int xx = x; xx < x + w; xx++) Draw(inst, d, xx, yy, col, rop, clip);
    }

    // 0x7B texture upload from CPU into a swizzled surface (0x52: 0x300 = format | log2 w << 16 | log2 h << 24, 0x304 offset, DMA 0x184):
    // 0x304 point (y << 16 | x), 0x308 size (h << 16 | w), 0x400+ pixels (2 per word at 16 bpp); 0x30C / 0x310 (clip) not used
    private void SwzUpload(uint inst, int mthd, uint data)
    {
        if (mthd is 0x304 or 0x308) _ifcPix = 0;
        if (mthd < 0x400 || mthd >= 0x2000) return;
        uint sf = Ctx(inst, 0x52);
        if (sf == 0)
        {
            // a linear destination (surfaces 2D, 0x42 / 0x62): the OpenGL ICD loads its texture palettes this way (Quake II's shared palette,
            // 256 A8R8G8B8 entries at the surface offset), pixels in the surface's format
            var d = SurfOf(inst, false);
            if (!d.Ok) return;
            uint lp = G(inst, 0x304), ls = G(inst, 0x308);
            int lx = (int)(lp & 0xffff), ly = (int)(lp >> 16), lw2 = (int)(ls & 0xffff), lh2 = (int)(ls >> 16);
            if (lw2 <= 0) return;
            int lper = d.Bpp == 1 ? 4 : d.Bpp == 2 ? 2 : 1;
            for (int k = 0; k < lper; k++, _ifcPix++)
            {
                int x = _ifcPix % lw2, y = _ifcPix / lw2;
                if (y >= lh2) return;
                uint v = lper == 1 ? data : lper == 2 ? (data >> (16 * k)) & 0xffff : (data >> (8 * k)) & 0xff;
                Draw(inst, d, lx + x, ly + y, v, 0xcc, (0, 0, 32767, 32767));
            }
            return;
        }
        uint fmt = G(sf, 0x300); int lw = (int)(fmt >> 16) & 15, lh = (int)(fmt >> 24) & 15, W = 1 << lw, H = 1 << lh;
        int bpp = (fmt & 0xff) is 0xa or 0xb or 6 or 7 or 8 or 9 ? 4 : (fmt & 0xff) == 1 ? 1 : 2;
        uint dma = G(sf, 0x184 | 0x8000), off = G(sf, 0x304);
        uint pt = G(inst, 0x304), sz = G(inst, 0x308);
        int px = (int)(pt & 0xffff), py = (int)(pt >> 16), w = (int)(sz & 0xffff), h = (int)(sz >> 16);
        if (w <= 0) return;
        int per = 4 / bpp;
        for (int k = 0; k < per; k++, _ifcPix++)
        {
            int x = _ifcPix % w, y = _ifcPix / w;
            if (y >= h) return;
            int dx = px + x, dy = py + y;
            if (dx >= W || dy >= H) continue;
            uint a = off + Swz(dx, dy, W, H) * (uint)bpp;
            uint v = per == 1 ? data : per == 2 ? (data >> (16 * k)) & 0xffff : (data >> (8 * k)) & 0xff;
            for (int b = 0; b < bpp; b++) DmaWrite8(dma, a + (uint)b, (byte)(v >> (8 * b)));
        }
    }

    // ================= 0x77 / 0x89 scaled image from memory (stretch blit, colour conversion) =================
    // 0x300 source format (1 A1R5G5B5, 2 X1R5G5B5, 3 A8R8G8B8, 4 X8R8G8B8, 7 R5G6B5, 8 Y8), 0x308 / 0x30C clip point / size (y << 16 | x,
    // h << 16 | w), 0x310 / 0x314 out point / size, 0x318 / 0x31C dsdx / dtdy (12.20), 0x400 in size, 0x404 pitch (bits 15:0), origin, filter,
    // 0x408 in offset (DMA 0x184), 0x40C in point (12.4 t << 16 | s) = go. Destination: the surfaces context. Nearest sampling.
    private void Sifm(uint inst)
    {
        uint swz = Ctx(inst, 0x52);
        var d = swz != 0 ? default : SurfOf(inst, false);
        if (swz == 0 && !d.Ok) return;
        int sW = 0, sH = 0, swBpp = 0; uint swDma = 0, swOff = 0, swFmt = 0;
        if (swz != 0)
        {
            swFmt = G(swz, 0x300); sW = 1 << (int)((swFmt >> 16) & 15); sH = 1 << (int)((swFmt >> 24) & 15);
            swBpp = (swFmt & 0xff) is 0xa or 0xb or 6 or 7 or 8 or 9 ? 4 : (swFmt & 0xff) == 1 ? 1 : 2;
            swDma = G(swz, 0x184 | 0x8000); swOff = G(swz, 0x304);
        }
        uint sdma = G(inst, 0x184 | 0x8000);
        int sf = (int)G(inst, 0x300);
        int sbpp = sf is 3 or 4 ? 4 : sf == 8 ? 1 : 2;
        uint cp = G(inst, 0x308), cs = G(inst, 0x30c), op = G(inst, 0x310), os = G(inst, 0x314);
        int ox = (short)(op & 0xffff), oy = (short)(op >> 16), ow = (int)(os & 0xffff), oh = (int)(os >> 16);
        var clip = ClipOf(inst);
        int cx = (short)(cp & 0xffff), cy = (short)(cp >> 16);
        clip = (Math.Max(clip.x0, cx), Math.Max(clip.y0, cy), Math.Min(clip.x1, cx + (int)(cs & 0xffff)), Math.Min(clip.y1, cy + (int)(cs >> 16)));
        long dsdx = G(inst, 0x318), dtdy = G(inst, 0x31c);
        uint isz = G(inst, 0x400); int iw = (int)(isz & 0xffff), ih = (int)(isz >> 16);
        int pitch = (int)(G(inst, 0x404) & 0xffff); uint ioff = G(inst, 0x408), ipt = G(inst, 0x40c);
        if (ow <= 0 || oh <= 0 || iw <= 0 || ih <= 0 || (long)ow * oh > 4 << 20) return;
        long s0 = (long)(ipt & 0xffff) << 16, t0 = (long)(ipt >> 16) << 16;   // 12.4 -> 12.20
        int rop = Rop(inst);
        OpLog?.Invoke($"sifm fmt {sf} in {iw}x{ih} pitch {pitch} off {ioff:X} -> {ox},{oy} {ow}x{oh} ds {dsdx:X} dt {dtdy:X} dst {(swz != 0 ? $"swz {swOff:X} {sW}x{sH} fmt {swFmt & 0xff:X}" : $"{d.Base:X}/{d.Pitch}/{d.Bpp}")} clip {clip}");
        for (int y = 0; y < oh; y++)
        {
            int sy = (int)Math.Clamp((t0 + y * dtdy) >> 20, 0, ih - 1);
            for (int x = 0; x < ow; x++)
            {
                int dx = ox + x, dy = oy + y;
                if (dx < clip.x0 || dy < clip.y0 || dx >= clip.x1 || dy >= clip.y1) continue;
                int sx = (int)Math.Clamp((s0 + x * dsdx) >> 20, 0, iw - 1);
                uint a = ioff + (uint)(sy * pitch + sx * sbpp);
                uint raw = sbpp == 4 ? DmaRead32Any(sdma, a) : sbpp == 2 ? (uint)(DmaRead8(sdma, a) | DmaRead8(sdma, a + 1) << 8) : DmaRead8(sdma, a);
                uint argb = sf switch
                {
                    1 or 2 => (sf == 2 || (raw & 0x8000) != 0 ? 0xff000000 : 0) | (uint)Raster3D.Pal5((int)(raw >> 10)) << 16 | (uint)Raster3D.Pal5((int)(raw >> 5)) << 8 | (uint)Raster3D.Pal5((int)raw),
                    7 => 0xff000000 | (uint)Raster3D.Rgb565To888((int)raw),
                    8 => 0xff000000 | raw * 0x010101,
                    _ => raw,
                };
                if (swz != 0)
                {
                    if (dx < 0 || dy < 0 || dx >= sW || dy >= sH) continue;
                    uint fm = swFmt & 0xff;
                    uint sv = swBpp == 4 ? argb : swBpp == 1 ? argb & 0xff
                        : fm is 2 or 3 ? ((argb >> 9) & 0x7c00) | ((argb >> 6) & 0x03e0) | ((argb >> 3) & 0x1f) | ((argb >> 31) << 15)
                        : ((argb >> 8) & 0xf800) | ((argb >> 5) & 0x07e0) | ((argb >> 3) & 0x1f);
                    uint da = swOff + Swz(dx, dy, sW, sH) * (uint)swBpp;
                    for (int k = 0; k < swBpp; k++) DmaWrite8(swDma, da + (uint)k, (byte)(sv >> (8 * k)));
                    continue;
                }
                uint v = d.Bpp == 2 ? (G(SurfCtx(inst), 0x300) & 0xff) is 2 or 3 ? ((argb >> 9) & 0x7c00) | ((argb >> 6) & 0x03e0) | ((argb >> 3) & 0x1f) : ((argb >> 8) & 0xf800) | ((argb >> 5) & 0x07e0) | ((argb >> 3) & 0x1f)
                       : d.Bpp == 1 ? argb & 0xff : argb;
                Draw(inst, d, dx, dy, v, rop, clip);
            }
        }
    }

    private uint SurfCtx(uint inst) => Ctx(inst, 0x42, 0x62);

    private uint DmaRead32Any(uint dma, uint off) => (off & 3) == 0 ? DmaRead32(dma, off) : (uint)(DmaRead8(dma, off) | DmaRead8(dma, off + 1) << 8 | DmaRead8(dma, off + 2) << 16 | DmaRead8(dma, off + 3) << 24);

    // ================= 0x5F blit =================

    private void Blit(uint inst)
    {
        var s = SurfOf(inst, true); var d = SurfOf(inst, false);
        if (!s.Ok || !d.Ok) return;
        uint pi = G(inst, 0x300), po = G(inst, 0x304), sz = G(inst, 0x308);
        int sx = (short)(pi & 0xffff), sy = (short)(pi >> 16), dx = (short)(po & 0xffff), dy = (short)(po >> 16), w = (int)(sz & 0xffff), h = (int)(sz >> 16);
        if (w <= 0 || h <= 0 || (long)w * h > 4 << 20) return;
        int rop = Rop(inst); var clip = ClipOf(inst);
        OpLog?.Invoke($"blit {sx},{sy} -> {dx},{dy} {w}x{h} rop {rop:X2} src {s.Base:X}/{s.Pitch} dst {d.Base:X}/{d.Pitch} clip {clip}");
        var buf = new uint[w * h];   // (copied out first: overlapping moves come out right in any direction)
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) buf[y * w + x] = Px(s, sx + x, sy + y);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) Draw(inst, d, dx + x, dy + y, buf[y * w + x], rop, clip);
    }

    // ================= 0x61 image from CPU =================

    private void Ifc(uint inst, int mthd, uint data)
    {
        if (mthd is 0x304 or 0x30c) _ifcPix = 0;
        if (mthd == 0x30c && OpLog != null) OpLog($"ifc {(short)G(inst, 0x304)},{G(inst, 0x304) >> 16} out {G(inst, 0x308) & 0xffff}x{G(inst, 0x308) >> 16} in {data & 0xffff}x{data >> 16} fmt {G(inst, 0x300)} surf {SurfOf(inst, false).Base:X}");
        if (mthd < 0x400 || mthd >= 0x2000) return;
        var d = SurfOf(inst, false);
        if (!d.Ok) return;
        uint pt = G(inst, 0x304), szo = G(inst, 0x308), szi = G(inst, 0x30c);
        int px = (short)(pt & 0xffff), py = (short)(pt >> 16), wi = (int)(szi & 0xffff), hi = (int)(szi >> 16), wo = (int)(szo & 0xffff), ho = (int)(szo >> 16);
        if (wi <= 0) return;
        int per = d.Bpp == 1 ? 4 : d.Bpp == 2 ? 2 : 1;
        int rop = Rop(inst); var clip = ClipOf(inst);
        clip = (Math.Max(clip.x0, px), Math.Max(clip.y0, py), Math.Min(clip.x1, px + wo), Math.Min(clip.y1, py + ho));
        for (int k = 0; k < per; k++, _ifcPix++)
        {
            int x = _ifcPix % wi, y = _ifcPix / wi;
            if (y >= hi) return;
            uint v = per == 1 ? data : per == 2 ? (data >> (16 * k)) & 0xffff : (data >> (8 * k)) & 0xff;
            Draw(inst, d, px + x, py + y, v, rop, clip);
        }
    }

    // ================= 0x64 indexed image from CPU =================
    // (from the driver's use: 0x3EC index format 0 = 8 bpp / 1 = 4 bpp, 0x3F0 LUT offset in the 0x184 DMA (entries in the destination
    // format), 0x3F4 point, 0x3F8 size out, 0x3FC size in, 0x400+ indices, LSB first)
    private void Index(uint inst, int mthd, uint data)
    {
        if (mthd is 0x3f4 or 0x3fc) _ifcPix = 0;
        if (mthd == 0x3fc && OpLog != null) OpLog($"index {(short)G(inst, 0x3f4)},{G(inst, 0x3f4) >> 16} out {G(inst, 0x3f8) & 0xffff}x{G(inst, 0x3f8) >> 16} in {data & 0xffff}x{data >> 16} ifmt {G(inst, 0x3ec)} lut {G(inst, 0x3f0):X} surf {SurfOf(inst, false).Base:X}");
        if (mthd < 0x400 || mthd >= 0x2000) return;
        var d = SurfOf(inst, false);
        if (!d.Ok) return;
        uint pt = G(inst, 0x3f4), szo = G(inst, 0x3f8), szi = G(inst, 0x3fc), lut = G(inst, 0x3f0);
        uint lutDma = G(inst, 0x184 | 0x8000);
        int px = (short)(pt & 0xffff), py = (short)(pt >> 16), wi = (int)(szi & 0xffff), hi = (int)(szi >> 16), wo = (int)(szo & 0xffff), ho = (int)(szo >> 16);
        if (wi <= 0) return;
        bool four = G(inst, 0x3ec) == 1;
        int per = four ? 8 : 4, bits = four ? 4 : 8;
        int rop = Rop(inst); var clip = ClipOf(inst);
        clip = (Math.Max(clip.x0, px), Math.Max(clip.y0, py), Math.Min(clip.x1, px + wo), Math.Min(clip.y1, py + ho));
        for (int k = 0; k < per; k++, _ifcPix++)
        {
            int x = _ifcPix % wi, y = _ifcPix / wi;
            if (y >= hi) return;
            uint idx = (data >> (bits * k)) & (four ? 0xfu : 0xffu);
            uint c = DmaRead32(lutDma, lut + idx * 4);          // LUT entries are X8R8G8B8 whatever the destination
            uint v = d.Bpp == 2 ? ((c >> 8) & 0xf800) | ((c >> 5) & 0x07e0) | ((c >> 3) & 0x1f) : d.Bpp == 1 ? c & 0xff : c;
            Draw(inst, d, px + x, py + y, v, rop, clip);
        }
    }

    private void SaveGraph(BinaryWriter w)
    {
        foreach (var o in _notifyObj) w.Write(o);
        w.Write(_ifcPix);
        w.Write(_gs.Count);
        foreach (var (k, v) in _gs) { w.Write(k); w.Write(v); }
        SaveCelsius(w);
    }

    private void LoadGraph(BinaryReader r, int ver)
    {
        for (int i = 0; i < 8; i++) _notifyObj[i] = r.ReadUInt32();
        _ifcPix = r.ReadInt32();
        _gs.Clear();
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++) { ulong k = r.ReadUInt64(); _gs[k] = r.ReadUInt32(); }
        if (ver >= 6) LoadCelsius(r); else { _cPrim = 0; _cWords.Clear(); _cVerts.Clear(); }
    }
}
