// 3dfx Voodoo Graphics: triangle setup, the TMU (texture fetch + combine) and the FBI pixel pipeline (colour combine, chroma key, alpha test,
// fog, alpha blend, dither, depth). Written for BrokenNes as a straight (non-templated, single-threaded) port of MAME's
// src/devices/video/voodoo_render.cpp and voodoo.cpp (triangle()):
//   license: BSD-3-Clause; copyright-holders: Aaron Giles (see THIRD_PARTY_NOTICES.md).

using System;

namespace NesEmulator.X86;

public sealed partial class Voodoo1
{
    // ================= dithering =================

    private readonly struct Dither
    {
        private readonly byte[] _lut; private readonly int _lutBase;
        private readonly byte[]? _raw; private readonly int _rawBase;
        private readonly int _raw4Base;
        public Dither(int y, uint fbz)
        {
            _raw4Base = (y & 3) * 4;
            if ((fbz & (1 << 8)) == 0) { _lut = Raster3D.NoDitherLookup; _lutBase = 0; _raw = null; _rawBase = 0; }
            else if ((fbz & (1 << 11)) == 0) { _lut = Raster3D.Dither4Lookup; _lutBase = (y & 3) << 11; _raw = Raster3D.Dither4x4; _rawBase = (y & 3) * 4; }
            else { _lut = Raster3D.Dither2Lookup; _lutBase = (y & 3) << 11; _raw = Raster3D.Dither2x2; _rawBase = (y & 3) * 4; }
        }
        public int Pixel(int x, int r, int g, int b)
        {
            int t = _lut == Raster3D.NoDitherLookup ? 0 : _lutBase + ((x & 3) << 9);
            return _lut[t + r] << 11 | _lut[t + g + 256] << 5 | _lut[t + b];
        }
        public int Raw4x4(int x) => Raster3D.Dither4x4[_raw4Base + (x & 3)];
        public int Subtract(int x) => _raw != null ? (15 - _raw[_rawBase + (x & 3)]) >> 1 : 0;
    }

    // ================= texel lookup tables (MAME shared_tables) =================

    private static readonly uint[] Rgb332 = new uint[256], Alpha8 = new uint[256], Int8 = new uint[256], Ai44 = new uint[256];
    private static readonly uint[] Rgb565 = new uint[65536], Argb1555 = new uint[65536], Argb4444 = new uint[65536];
    static Voodoo1()
    {
        for (int v = 0; v < 256; v++)
        {
            Rgb332[v] = Raster3D.Argb(0xff, Raster3D.Pal3(v >> 5), Raster3D.Pal3(v >> 2), Raster3D.Pal2(v));
            Alpha8[v] = Raster3D.Argb(v, v, v, v);
            Int8[v] = Raster3D.Argb(0xff, v, v, v);
            Ai44[v] = Raster3D.Argb(Raster3D.Pal4(v >> 4), Raster3D.Pal4(v), Raster3D.Pal4(v), Raster3D.Pal4(v));
        }
        for (int v = 0; v < 65536; v++)
        {
            Rgb565[v] = Raster3D.Argb(0xff, Raster3D.Pal5(v >> 11), Raster3D.Pal6(v >> 5), Raster3D.Pal5(v));
            Argb1555[v] = Raster3D.Argb(Raster3D.Pal1(v >> 15), Raster3D.Pal5(v >> 10), Raster3D.Pal5(v >> 5), Raster3D.Pal5(v));
            Argb4444[v] = Raster3D.Argb(Raster3D.Pal4(v >> 12), Raster3D.Pal4(v >> 8), Raster3D.Pal4(v >> 4), Raster3D.Pal4(v));
        }
    }

    // ================= TMU state (MAME rasterizer_texture) =================

    private sealed class TexState
    {
        public uint[] Lookup = Rgb332;
        public int WMask, HMask, DetailScale, LodMin, LodMax, LodBias, LodMask, DetailMax, DetailBias;
        public readonly uint[] LodOffset = new uint[9];
    }
    private readonly TexState _tex = new();
    private bool _texDirty;
    private int _paletteDirty;
    private readonly uint[] _palLookup = new uint[256], _palALookup = new uint[256], _ncc0 = new uint[256], _ncc1 = new uint[256];

    private void PrepareTexture()
    {
        if (!_texDirty && _paletteDirty == 0) return;
        uint tmode = TReg[RTextureMode];
        int fmt = (int)(tmode >> 8) & 0xf;
        uint[]? lookup = fmt switch { 0 or 8 => Rgb332, 2 => Alpha8, 3 or 13 => Int8, 4 => Ai44, 10 => Rgb565, 11 => Argb1555, 12 => Argb4444, _ => null };
        if (lookup == null)
        {
            if ((fmt & 7) == 1)
            {
                int sel = (int)(tmode >> 5) & 1;
                var dst = sel == 0 ? _ncc0 : _ncc1;
                if ((_paletteDirty & (4 << sel)) != 0) { ComputeNcc(RNccTable + 12 * sel, dst); _paletteDirty &= ~(4 << sel); }
                lookup = dst;
            }
            else if (fmt == 6) { if ((_paletteDirty & 2) != 0) { Array.Copy(_paletteA, _palALookup, 256); _paletteDirty &= ~2; } lookup = _palALookup; }
            else { if ((_paletteDirty & 1) != 0) { Array.Copy(_palette, _palLookup, 256); _paletteDirty &= ~1; } lookup = _palLookup; }
        }
        // a palette change alone does not move the LOD layout; the texture layout is recomputed on register changes
        var t = _tex;
        t.Lookup = lookup;
        if (!_texDirty) return;
        uint tlod = TReg[RTLod];
        t.LodMin = (int)(tlod & 0x3f) << 6;
        t.LodMax = (int)Math.Min((tlod >> 6) & 0x3f, 32) << 6;
        t.LodBias = (sbyte)(((tlod >> 12) & 0x3f) << 2) << 4;
        t.LodMask = 0x1ff;
        if ((tlod & (1 << 19)) != 0) t.LodMask = (tlod & (1 << 18)) != 0 ? 0x0aa : 0x155;
        t.WMask = t.HMask = 0xff;
        int aspect = (int)(tlod >> 21) & 3;
        if ((tlod & (1 << 20)) != 0) t.HMask >>= aspect; else t.WMask >>= aspect;
        int bppscale = fmt >> 3;
        uint baseAddr = TReg[RTexBaseAddr];
        Span<uint> lodstart = stackalloc uint[9];
        uint offset = 0;
        for (int lod = 0; lod <= 8; lod++)
        {
            lodstart[lod] = offset;
            if (((t.LodMask >> lod) & 1) != 0)
            {
                uint size = (uint)(((t.WMask >> lod) + 1) * ((t.HMask >> lod) + 1));
                if (lod >= 3 && size < 4) size = 4;
                offset += size << bppscale;
            }
        }
        Span<uint> lodbase = stackalloc uint[9];
        for (int lod = 0; lod <= 8; lod++) lodbase[lod] = baseAddr;
        if ((tlod & (1 << 24)) != 0 && (tlod >> 28) == 0)
        {
            lodbase[1] = TReg[RTexBaseAddr1]; lodbase[2] = TReg[RTexBaseAddr2];
            for (int lod = 3; lod <= 8; lod++) lodbase[lod] = TReg[RTexBaseAddr38];
        }
        for (int lod = 0; lod <= 8; lod++) t.LodOffset[lod] = (((lodbase[lod] & 0xfffff) << 3) + lodstart[lod]) & TexMask;
        uint det = TReg[RTDetail];
        t.DetailMax = (int)(det & 0xff);
        t.DetailBias = (sbyte)(((det >> 8) & 0x3f) << 2) << 6;
        t.DetailScale = (int)(det >> 14) & 7;
        _texDirty = false;
    }

    private void ComputeNcc(int regBase, uint[] dst)
    {
        for (int index = 0; index < 256; index++)
        {
            int yi = (index >> 4) & 15;
            int y = (int)(TReg[regBase + (yi >> 2)] >> ((yi & 3) * 8)) & 0xff;
            uint i = TReg[regBase + 4 + ((index >> 2) & 3)], q = TReg[regBase + 8 + (index & 3)];
            static int Sx9(uint v) => (int)(v << 23) >> 23;
            int r = Math.Clamp(y + Sx9(i >> 18) + Sx9(q >> 18), 0, 255);
            int g = Math.Clamp(y + Sx9(i >> 9) + Sx9(q >> 9), 0, 255);
            int b = Math.Clamp(y + Sx9(i) + Sx9(q), 0, 255);
            dst[index] = Raster3D.Argb(0xff, r, g, b);
        }
    }

    private uint Texel(int fmt, uint texbase, int s, int t)
    {
        var lk = _tex.Lookup;
        if (fmt < 8) return lk[Tex[(texbase + (uint)(t + s)) & TexMask]];
        int p = (int)((texbase + 2 * (uint)(t + s)) & TexMask) & ~1;
        int texel = Tex[p] | Tex[p + 1] << 8;
        if (fmt >= 10 && fmt <= 12) return lk[texel];
        return (lk[texel & 0xff] & 0xffffff) | (uint)(texel & 0xff00) << 16;
    }

    private const int BilinearMask = 0xf0;

    private Rgba FetchTexel(uint tmode, in Dither dither, int x, double iters, double itert, double iterw, ref int lod)
    {
        var tx = _tex;
        int s, t;
        if ((tmode & 1) != 0)
        {
            double recip = 256.0 / iterw;
            s = (int)(iters * recip);
            t = (int)(itert * recip);
            lod -= Raster3D.FastLog2(iterw, 32);
        }
        else
        {
            s = (int)(iters * (1.0 / (1 << 24)));
            t = (int)(itert * (1.0 / (1 << 24)));
        }
        if ((tmode & 8) != 0 && iterw < 0) s = t = 0;
        lod += tx.LodBias;
        if ((tmode & 16) != 0) lod += dither.Raw4x4(x) << 4;
        lod = Math.Min(Math.Max(lod, tx.LodMin), tx.LodMax);
        int ilod = lod >> 8;
        ilod += (~tx.LodMask >> ilod) & 1;
        ilod = Math.Min(ilod, (tx.LodMask & 0x100) != 0 ? 8 : 7);
        uint texbase = tx.LodOffset[ilod];
        int smax = tx.WMask >> ilod, tmax = tx.HMask >> ilod;
        int fmt = (int)(tmode >> 8) & 0xf;
        bool clampS = (tmode & 0x40) != 0, clampT = (tmode & 0x80) != 0;
        if ((lod == tx.LodMin && (tmode & 4) == 0) || (lod != tx.LodMin && (tmode & 2) == 0))
        {
            ilod += 8;
            s >>= ilod; t >>= ilod;
            if (clampS) s = Math.Clamp(s, 0, smax);
            if (clampT) t = Math.Clamp(t, 0, tmax);
            s &= smax; t &= tmax; t *= smax + 1;
            return Rgba.From(Texel(fmt, texbase, s, t));
        }
        s >>= ilod; t >>= ilod;
        s -= 0x80; t -= 0x80;
        int sfrac = s & BilinearMask, tfrac = t & BilinearMask;
        s >>= 8; t >>= 8;
        int s1 = s + 1, t1 = t + 1;
        if (clampS) { if (s < 0) s = s1 = 0; else if (s >= smax) s = s1 = smax; }
        s &= smax; s1 &= smax;
        if (clampT) { if (t < 0) t = t1 = 0; else if (t >= tmax) t = t1 = tmax; }
        t &= tmax; t1 &= tmax;
        t *= smax + 1; t1 *= smax + 1;
        return Rgba.Bilinear(Texel(fmt, texbase, s, t), Texel(fmt, texbase, s1, t), Texel(fmt, texbase, s, t1), Texel(fmt, texbase, s1, t1), sfrac, tfrac);
    }

    private Rgba CombineTexture(uint tm, in Rgba cLocal, in Rgba cOther, int lod)
    {
        var tx = _tex;
        Rgba blend = (tm & (1 << 12)) != 0 ? default : cOther;
        blend.A = (tm & (1 << 21)) != 0 ? 0 : cOther.A;
        bool subC = (tm & (1 << 13)) != 0, subA = (tm & (1 << 22)) != 0;
        if (subC || subA)
        {
            Rgba sub = subC ? cLocal : default;
            sub.A = subA ? cLocal.A : 0;
            blend.Sub(sub);
        }
        Rgba f = default;
        switch ((tm >> 14) & 7)
        {
            case 1: f = cLocal; break;
            case 2: f = cOther.SelectAlpha(); break;
            case 3: f = cLocal.SelectAlpha(); break;
            case 4: if (tx.DetailBias > lod) { int v = Math.Min((byte)(((tx.DetailBias - lod) << tx.DetailScale) >> 8), tx.DetailMax); f.SetAll(v); } break;
            case 5: f.SetAll(lod & 0xff); break;
        }
        switch ((tm >> 23) & 7)
        {
            case 1: case 3: f.A = cLocal.A; break;
            case 2: f.A = cOther.A; break;
            case 4: f.A = tx.DetailBias > lod ? Math.Min((byte)(((tx.DetailBias - lod) << tx.DetailScale) >> 8), tx.DetailMax) : 0; break;
            case 5: f.A = lod & 0xff; break;
            default: f.A = 0; break;
        }
        if ((tm & (1 << 17)) == 0) f.Xor(0, 0xff, 0xff, 0xff);
        if ((tm & (1 << 26)) == 0) f.Xor(0xff, 0, 0, 0);
        f.AddImm(1);
        Rgba add = ((tm >> 18) & 3) switch { 1 => cLocal, 2 => cLocal.SelectAlpha(), _ => default };
        add.A = (tm & (3u << 27)) != 0 ? cLocal.A : 0;
        blend.ScaleAddClamp(f, add);
        if ((tm & (1 << 20)) != 0) blend.Xor(0, 0xff, 0xff, 0xff);
        if ((tm & (1 << 29)) != 0) blend.Xor(0xff, 0, 0, 0);
        return blend;
    }

    // ================= FBI pipeline helpers =================

    private static int ClampedZ(int iterz, uint cp)
    {
        if ((cp & (1 << 28)) != 0) return Math.Clamp(iterz >> 12, 0, 0xffff);
        uint r = (uint)iterz >> 12;
        if (r == 0xfffff) return 0;
        if (r == 0x10000) return 0xffff;
        return (int)(r & 0xffff);
    }
    private static int ClampedW(long iterw, uint cp)
    {
        if ((cp & (1 << 28)) != 0) return Math.Clamp((int)(iterw >> 48), 0, 0xff);
        uint r = (uint)((ulong)iterw >> 48);
        if (r == 0xffff) return 0;
        if (r == 0x100) return 0xff;
        return (int)(r & 0xff);
    }
    private static Rgba ClampedArgb(in Rgba it, uint cp)
    {
        var r = new Rgba(it.A >> 20, it.R >> 20, it.G >> 20, it.B >> 20);
        if ((cp & (1 << 28)) != 0) { r.Clamp8(); return r; }
        static int F(int v) { v &= 0xfff; return v == 0xfff ? 0 : v == 0x100 ? 0xff : v & 0xff; }
        return new Rgba(F(r.A), F(r.R), F(r.G), F(r.B));
    }
    private static int WFloat(long iterw)
    {
        int exp = Raster3D.CountLeadingZeros64((ulong)iterw) - 16;
        if (exp < 0) return 0;
        if (exp >= 16) return 0xffff;
        return ((exp << 12) | (int)((iterw >> (35 - exp)) ^ 0x1fff)) + 1;
    }
    private int DepthVal(uint fbz, uint cp, int wfloat, int iterz)
    {
        int r;
        if ((fbz & (1 << 3)) != 0)
        {
            if ((fbz & (1 << 21)) == 0) r = wfloat;
            else if ((iterz & 0xf0000000) != 0) r = 0;
            else if ((iterz & 0x0ffff000) == 0) r = 0xffff;
            else { int exp = Raster3D.CountLeadingZeros32((uint)iterz) - 4; return ((exp << 12) | ((iterz >> (15 - exp)) ^ 0x1fff)) + 1; }
        }
        else r = ClampedZ(iterz, cp);
        if ((fbz & (1 << 16)) != 0) r = Math.Clamp(r + (short)Reg[RZaColor], 0, 0xffff);
        return r;
    }
    private static bool Stipple(uint fbz, int x, int scry, ref uint stipple)
    {
        if ((fbz & (1 << 12)) == 0) { stipple = stipple >> 1 | stipple << 31; return (int)stipple < 0; }
        return ((stipple >> (((scry & 3) << 3) | (~x & 7))) & 1) != 0;
    }
    private bool ChromaOk(in Rgba c, uint key)
    {
        uint col = c.ToArgb();
        uint range = Reg[RChromaRange];
        if ((range & (1 << 28)) == 0) return ((col ^ key) & 0xffffff) != 0;
        int res = (((int)(col & 0xff) >= (int)(key & 0xff) && (col & 0xff) <= (range & 0xff)) ? 1 : 0) ^ (int)((range >> 24) & 1);
        res <<= 1;
        res |= (((col >> 8) & 0xff) >= ((key >> 8) & 0xff) && ((col >> 8) & 0xff) <= ((range >> 8) & 0xff)) ? 1 : 0; res ^= (int)((range >> 25) & 1);
        res <<= 1;
        res |= (((col >> 16) & 0xff) >= ((key >> 16) & 0xff) && ((col >> 16) & 0xff) <= ((range >> 16) & 0xff)) ? 1 : 0; res ^= (int)((range >> 26) & 1);
        return (range & (1 << 27)) != 0 ? res == 0 : res != 7;
    }

    private bool CombineColor(ref Rgba color, uint cp, uint fbz, in Rgba texel, int iterz, long iterw, uint color0, uint color1, uint key)
    {
        Rgba cOther = (cp & 3) switch { 0 => color, 1 => texel, 2 => Rgba.From(color1), _ => default };
        if ((fbz & 2) != 0 && !ChromaOk(cOther, key)) return false;
        cOther.A = ((cp >> 2) & 3) switch { 0 => color.A, 1 => texel.A, 2 => (int)(color1 >> 24), _ => 0 };
        if ((fbz & (1 << 13)) != 0 && (cOther.A & 1) == 0) return false;
        Rgba cLocal;
        if ((cp & (1 << 7)) == 0) cLocal = (cp & (1 << 4)) == 0 ? color : Rgba.From(color0);
        else cLocal = (texel.A & 0x80) == 0 ? color : Rgba.From(color0);
        cLocal.A = ((cp >> 5) & 3) switch { 0 => color.A, 1 => (int)(color0 >> 24), 2 => (byte)(ClampedZ(iterz, cp) >> 8), _ => (byte)ClampedW(iterw, cp) };
        Rgba blend = (cp & (1 << 8)) != 0 ? default : cOther;
        blend.A = (cp & (1 << 17)) != 0 ? 0 : cOther.A;
        bool subC = (cp & (1 << 9)) != 0, subA = (cp & (1 << 18)) != 0;
        if (subC || subA)
        {
            Rgba sub = subC ? cLocal : default;
            sub.A = subA ? cLocal.A : 0;
            blend.Sub(sub);
        }
        Rgba f = ((cp >> 10) & 7) switch { 1 => cLocal, 2 => cOther.SelectAlpha(), 3 => cLocal.SelectAlpha(), 4 => texel.SelectAlpha(), 5 => texel, _ => default };
        f.A = ((cp >> 19) & 7) switch { 1 or 3 => cLocal.A, 2 => cOther.A, 4 => texel.A, _ => 0 };
        if ((cp & (1 << 13)) == 0) f.Xor(0, 0xff, 0xff, 0xff);
        if ((cp & (1 << 22)) == 0) f.Xor(0xff, 0, 0, 0);
        Rgba add = ((cp >> 14) & 3) switch { 1 => cLocal, 2 => cLocal.SelectAlpha(), _ => default };
        add.A = (cp & (3u << 23)) != 0 ? cLocal.A : 0;
        f.AddImm(1);
        blend.ScaleAddClamp(f, add);
        if ((cp & (1 << 25)) != 0) blend.Xor(0xff, 0, 0, 0);
        if ((cp & (1 << 16)) != 0) blend.Xor(0, 0xff, 0xff, 0xff);
        color = blend;
        return true;
    }

    private void Fog(ref Rgba color, uint fogcolor, uint depthbias, uint fbz, uint fog, uint cp, int x, in Dither dither, int wfloat, int iterz, long iterw, in Rgba iterargb)
    {
        Rgba fc = Rgba.From(fogcolor);
        if ((fog & (1 << 5)) != 0)
        {
            if ((fog & 4) == 0) { fc.Add(color); fc.Clamp8(); }
        }
        else
        {
            int blend = 0;
            if ((fog & 2) != 0) fc.Zero();
            if ((fog & 4) == 0) fc.Sub(color);
            switch ((fog >> 3) & 3)
            {
                case 0:
                {
                    int fd = wfloat;
                    if ((fbz & (1 << 16)) != 0) fd = Math.Clamp(fd + (short)depthbias, 0, 0xffff);
                    int delta = _fogDelta[fd >> 10];
                    int dv = (delta & 0xff) * ((fd >> 2) & 0xff);   // Voodoo 1: full delta mask
                    dv >>= 6;
                    dv >>= 4;
                    blend = _fogBlend[fd >> 10] + dv;
                    break;
                }
                case 1: blend = iterargb.A; break;
                case 2: blend = ClampedZ(iterz, cp) >> 8; break;
                case 3: blend = ClampedW(iterw, cp); break;
            }
            blend++;
            fc.ScaleImmClamp((short)blend);
            if ((fog & 4) == 0) { fc.Add(color); fc.Clamp8(); }
        }
        fc.A = color.A;
        color = fc;
    }

    private void AlphaBlend(ref Rgba color, uint fbz, uint am, int x, in Dither dither, int dpix, int depthOff, in Rgba prefog)
    {
        Rgba dst = Rgba.From(Rgb565[dpix]);
        int da = 0xff;
        if ((fbz & (1 << 18)) != 0) { da = depthOff >= 0 ? Fb16(depthOff) : 0; dst.A = da; }
        if ((fbz & (1 << 19)) != 0) { int d = dither.Subtract(x); dst.R += d; dst.G += d >> 1; dst.B += d; }
        int sa = color.A, ta;
        Rgba ss = default;
        switch ((am >> 8) & 15)
        {
            case 1: ss.SetAll(sa + 1); break;
            case 2: ss = dst; ss.AddImm(1); break;
            case 3: ss.SetAll(da + 1); break;
            case 4: ss.SetAll(256); break;
            case 5: ss.SetAll(0x100 - sa); break;
            case 6: ss.SetAll(0x100); ss.Sub(dst); break;
            case 7: ss.SetAll(0x100 - da); break;
            case 15: ta = 0x100 - da; if (sa < ta) ta = sa; ss.SetAll(ta + 1); break;
        }
        ss.A = ((am >> 16) & 15) == 4 ? 256 : 0;
        Rgba ds = default;
        switch ((am >> 12) & 15)
        {
            case 1: ds.SetAll(sa + 1); break;
            case 2: ds = color; ds.AddImm(1); break;
            case 3: ds.SetAll(da + 1); break;
            case 4: ds.SetAll(256); break;
            case 5: ds.SetAll(0x100 - sa); break;
            case 6: ds.SetAll(0x100); ds.Sub(color); break;
            case 7: ds.SetAll(0x100 - da); break;
            case 15: ds = prefog; ds.AddImm(1); break;
        }
        ds.A = ((am >> 20) & 15) == 4 ? 256 : 0;
        color.Scale2AddClamp(ss, dst, ds);
    }

    private void WritePixel(uint fbz, in Dither dither, int destOff, int depthOff, int x, in Rgba color, int depthval)
    {
        if ((fbz & (1 << 9)) != 0 && destOff >= 0) Fb16(destOff, dither.Pixel(x, color.R, color.G, color.B));
        if ((fbz & (1 << 10)) != 0 && depthOff >= 0) Fb16(depthOff, (fbz & (1 << 18)) == 0 ? depthval : color.A);
        Reg[RFbiPixelsOut]++;
    }

    /// <summary>One LFB pixel through the pipeline (lfbMode bit 8).</summary>
    private void PixelPipeline(int destBase, int depthBase, int x, int scry, uint src, ushort sz)
    {
        uint fbz = Reg[RFbzMode];
        Reg[RFbiPixelsIn]++;
        if ((fbz & 1) != 0)
        {
            int cl = (int)((Reg[RClipLeftRight] >> 16) & 0x3ff), cr = (int)(Reg[RClipLeftRight] & 0x3ff);
            int ct = (int)((Reg[RClipLowYHighY] >> 16) & 0x3ff), cb = (int)(Reg[RClipLowYHighY] & 0x3ff);
            if (x < cl || x >= cr || scry < ct || scry >= cb) return;
        }
        if (scry < 0 || x < 0) return;
        int o = (scry * _rowPixels + x) * 2;
        int destOff = destBase + o < FbBytes ? destBase + o : -1, depthOff = depthBase >= 0 && depthBase + o < FbBytes ? depthBase + o : -1;
        if ((fbz & 4) != 0) { uint st = Reg[RStipple]; if (!Stipple(fbz, x, scry, ref st)) return; }
        int depthval = sz;
        if ((fbz & 16) != 0 && depthOff >= 0 && !Raster3D.Compare((int)(fbz >> 5) & 7, (fbz & (1 << 20)) == 0 ? depthval : (ushort)Reg[RZaColor], Fb16(depthOff))) return;
        var color = Rgba.From(src);
        if ((fbz & 2) != 0 && !ChromaOk(color, Reg[RChromaKey])) return;
        if ((fbz & (1 << 13)) != 0 && (color.A & 1) == 0) return;
        uint am = Reg[RAlphaMode];
        if ((am & 1) != 0 && !Raster3D.Compare((int)(am >> 1) & 7, color.A, (int)(am >> 24))) return;
        uint fog = Reg[RFogMode];
        var dither = new Dither(scry, fbz);
        var prefog = color;
        if ((fog & 1) != 0)
        {
            int iterz = sz << 12;
            long iterw = (Reg[RLfbMode] & (1 << 14)) != 0 ? (uint)(Reg[RZaColor] << 16) : (uint)(sz << 16);
            Fog(ref color, Reg[RFogColor], Reg[RZaColor], fbz, fog, Reg[RFbzColorPath], x, dither, depthval, iterz, iterw, default);
        }
        if ((am & 16) != 0 && destOff >= 0) AlphaBlend(ref color, fbz, am, x, dither, Fb16(destOff), depthOff, prefog);
        WritePixel(fbz, dither, destOff, depthOff, x, color, depthval);
    }

    // ================= fastfill =================

    private void Fastfill()
    {
        uint fbz = Reg[RFbzMode];
        if ((fbz & (3 << 9)) == 0) return;
        int dest = DrawBuffer((int)(fbz >> 14) & 3);
        if (dest < 0) return;
        int x0 = (int)((Reg[RClipLeftRight] >> 16) & 0x3ff), x1 = (int)(Reg[RClipLeftRight] & 0x3ff);
        int y0 = (int)((Reg[RClipLowYHighY] >> 16) & 0x3ff), y1 = (int)(Reg[RClipLowYHighY] & 0x3ff);
        uint c1 = Reg[RColor1];
        Span<int> dpat = stackalloc int[16];
        for (int y = 0; y < 4; y++) { var d = new Dither(y, fbz); for (int x = 0; x < 4; x++) dpat[y * 4 + x] = d.Pixel(x, (int)(c1 >> 16) & 0xff, (int)(c1 >> 8) & 0xff, (int)c1 & 0xff); }
        int z = (ushort)Reg[RZaColor];
        for (int y = y0; y < y1; y++)
        {
            int scry = (fbz & (1 << 17)) != 0 ? YOrigin - y : y;
            if (scry < 0) continue;
            for (int x = x0; x < x1; x++)
            {
                int o = (scry * _rowPixels + x) * 2;
                if ((fbz & (1 << 9)) != 0 && dest + o < FbBytes) Fb16(dest + o, dpat[(y & 3) * 4 + (x & 3)]);
                if ((fbz & (1 << 10)) != 0 && _auxOffs >= 0 && _auxOffs + o < FbBytes) Fb16(_auxOffs + o, z);
            }
        }
    }

    // ================= triangles =================

    private static int Sx24(uint v) => (int)(v << 8) >> 8;

    private void Triangle()
    {
        Triangles++;
        uint fbz = Reg[RFbzMode];
        int dest = DrawBuffer((int)(fbz >> 14) & 3);
        if (dest < 0) return;
        int ax = (short)Reg[RVertexAx], ay = (short)Reg[RVertexAy];
        int startr = Sx24(Reg[RStartR]), startg = Sx24(Reg[RStartG]), startb = Sx24(Reg[RStartB]), starta = Sx24(Reg[RStartA]), startz = (int)Reg[RStartZ];
        long startw = _startW;
        int drdx = Sx24(Reg[RdRdX]), dgdx = Sx24(Reg[RdGdX]), dbdx = Sx24(Reg[RdBdX]), dadx = Sx24(Reg[RdAdX]), dzdx = (int)Reg[RdZdX];
        int drdy = Sx24(Reg[RdRdY]), dgdy = Sx24(Reg[RdGdY]), dbdy = Sx24(Reg[RdBdY]), dady = Sx24(Reg[RdAdY]), dzdy = (int)Reg[RdZdY];
        long dwdx = _dwdx, dwdy = _dwdy;
        uint cp = Reg[RFbzColorPath];
        if ((cp & (1 << 26)) != 0)
        {
            int dx = 8 - (ax & 15), dy = 8 - (ay & 15);
            startr += (dy * drdy + dx * drdx) >> 4; Reg[RStartR] = (uint)startr & 0xffffff;
            startg += (dy * dgdy + dx * dgdx) >> 4; Reg[RStartG] = (uint)startg & 0xffffff;
            startb += (dy * dbdy + dx * dbdx) >> 4; Reg[RStartB] = (uint)startb & 0xffffff;
            starta += (dy * dady + dx * dadx) >> 4; Reg[RStartA] = (uint)starta & 0xffffff;
            startz += (dy * dzdy + dx * dzdx) >> 4; Reg[RStartZ] = (uint)startz;
            startw += (dy * dwdy + dx * dwdx) >> 4; _startW = startw;
            _tW += (dy * _tdWdY + dx * _tdWdX) >> 4;
            _tS += (dy * _tdSdY + dx * _tdSdX) >> 4;
            _tT += (dy * _tdTdY + dx * _tdTdX) >> 4;
            // re-read the (24-bit) registers as MAME's poly picks up the adjusted values
            startr = Sx24(Reg[RStartR]); startg = Sx24(Reg[RStartG]); startb = Sx24(Reg[RStartB]); starta = Sx24(Reg[RStartA]);
        }
        // texture unit on unless fbiInit3 disables the TMUs or tLOD's lodmin is past 8.0 (MAME rasterizer_params::compute)
        bool texOn = (Reg[RFbiInit3] & 0x40) == 0 && (TReg[RTLod] & 0x3f) <= 32;
        bool sendConfig = ((TReg[RTrexInit1] >> 18) & 1) != 0;
        uint tmode = TReg[RTextureMode];
        if (texOn) PrepareTexture();
        long s0 = _tS, t0 = _tT, w0 = _tW, ds0dx = _tdSdX, dt0dx = _tdTdX, dw0dx = _tdWdX, ds0dy = _tdSdY, dt0dy = _tdTdY, dw0dy = _tdWdY;
        uint color0 = Reg[RColor0], color1 = Reg[RColor1], key = Reg[RChromaKey], fogcolor = Reg[RFogColor], za = Reg[RZaColor];
        uint stippleStart = Reg[RStipple];
        uint am = Reg[RAlphaMode], fog = Reg[RFogMode];
        int alpharef = (int)(am >> 24);
        int clipL = (int)((Reg[RClipLeftRight] >> 16) & 0x3ff), clipR = (int)(Reg[RClipLeftRight] & 0x3ff);
        int clipT = (int)((Reg[RClipLowYHighY] >> 16) & 0x3ff), clipB = (int)(Reg[RClipLowYHighY] & 0x3ff);
        int lodbase0 = texOn ? Raster3D.LodBase(ds0dx, ds0dy, dt0dx, dt0dy) : 0;
        int depthBase = _auxOffs;
        int yorigin = YOrigin;
        uint stipple = stippleStart;

        var va = new Raster3D.Vertex(ax * (1.0f / 16.0f), ay * (1.0f / 16.0f));
        var vb = new Raster3D.Vertex((short)Reg[RVertexBx] * (1.0f / 16.0f), (short)Reg[RVertexBy] * (1.0f / 16.0f));
        var vc = new Raster3D.Vertex((short)Reg[RVertexCx] * (1.0f / 16.0f), (short)Reg[RVertexCy] * (1.0f / 16.0f));

        Pixels += Raster3D.Triangle(va, vb, vc, (y, startx, stopx) =>
        {
            int scry = (fbz & (1 << 17)) != 0 ? yorigin - y : y;
            if ((fbz & 1) != 0)
            {
                if (scry < clipT || scry >= clipB) return;
                if (startx >= clipR) return;
                if (stopx > clipR) stopx = clipR;
                if (startx < clipL) startx = clipL;
            }
            if (scry < 0) return;
            int rowOff = scry * _rowPixels * 2;
            int dx = startx - (ax >> 4), dy = y - (ay >> 4);
            var it = new Rgba((starta + dy * dady + dx * dadx) << 8, (startr + dy * drdy + dx * drdx) << 8, (startg + dy * dgdy + dx * dgdx) << 8, (startb + dy * dbdy + dx * dbdx) << 8);
            var itd = new Rgba(dadx << 8, drdx << 8, dgdx << 8, dbdx << 8);
            int iterz = startz + dy * dzdy + dx * dzdx;
            long iterw = (startw + dy * dwdy + dx * dwdx) << 16, iterwd = dwdx << 16;
            double iters0 = 0, itert0 = 0, iterw0 = 0, ds = ds0dx, dt = dt0dx, dw = dw0dx;
            if (texOn)
            {
                iters0 = s0 + dy * ds0dy + dx * ds0dx;
                itert0 = t0 + dy * dt0dy + dx * dt0dx;
                iterw0 = w0 + dy * dw0dy + dx * dw0dx;
            }
            var dither = new Dither(scry, fbz);
            for (int x = startx; x < stopx; x++)
            {
                do
                {
                    Reg[RFbiPixelsIn]++;
                    if ((fbz & 4) != 0 && !Stipple(fbz, x, scry, ref stipple)) break;
                    int o = rowOff + x * 2;
                    if (x < 0 || o < 0) break;
                    int destOff = dest + o + 1 < FbBytes ? dest + o : -1;
                    int depthOff = depthBase >= 0 && depthBase + o + 1 < FbBytes ? depthBase + o : -1;
                    int wfloat = WFloat(iterw);
                    int depthval = DepthVal(fbz, cp, wfloat, iterz);
                    if ((fbz & 16) != 0 && depthOff >= 0 && !Raster3D.Compare((int)(fbz >> 5) & 7, (fbz & (1 << 20)) == 0 ? depthval : (ushort)za, Fb16(depthOff))) break;
                    Rgba texel = default;
                    if (texOn)
                    {
                        if (!sendConfig)
                        {
                            int lod0 = lodbase0;
                            var t0x = FetchTexel(tmode, dither, x, iters0, itert0, iterw0, ref lod0);
                            texel = CombineTexture(tmode, t0x, texel, lod0);
                        }
                        else texel = Rgba.From(0x11);
                    }
                    var color = ClampedArgb(it, cp);
                    if (!CombineColor(ref color, cp, fbz, texel, iterz, iterw, color0, color1, key)) break;
                    if ((am & 1) != 0 && !Raster3D.Compare((int)(am >> 1) & 7, color.A, alpharef)) break;
                    var prefog = color;
                    if ((fog & 1) != 0) Fog(ref color, fogcolor, za, fbz, fog, cp, x, dither, wfloat, iterz, iterw, it);
                    if ((am & 16) != 0 && destOff >= 0) AlphaBlend(ref color, fbz, am, x, dither, Fb16(destOff), depthOff, prefog);
                    WritePixel(fbz, dither, destOff, depthOff, x, color, depthval);
                } while (false);
                it.Add(itd);
                iterz += dzdx;
                iterw += iterwd;
                if (texOn) { iters0 += ds; itert0 += dt; iterw0 += dw; }
            }
        });
    }
}
