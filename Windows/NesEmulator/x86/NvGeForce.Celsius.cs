// GeForce 256: the Celsius 3D class (0x56 NV10TCL, also 0x96 / 0x98 / 0x99), a deterministic software rasteriser. Register layout from
// envytools rnndb graph/nv10_3d.xml + nv_3ddefs.xml; the inline vertex layout and the combiner use from the NVIDIA driver's own streams.
//
// Done: inline vertex arrays (0x17FC begin/end + 0x1800 data, layout from VTXBUF_FMT 0xD04 + 8i: position first, then attributes 7..1),
// immediate vertices (0xC00-0xCEC + 0xDFC), pre-transformed (HOMOGENEOUS) and transformed positions (projection x modelview + viewport),
// triangles / strips / fans / quads, cull, perspective-correct colour + 2 texture units (swizzled + RECT formats, nearest / bilinear),
// the register combiners (2 general stages + final), alpha test, Z16 / Z24 depth test and write, blending, colour mask.
// Also: mipmaps + min/mag filters (LOD from screen derivatives), DXT1/3/5, fog (register 3 of the final combiner), stencil (Z24S8), points / lines.
// Not yet: lighting, texgen, texture matrices, polygon modes.

using System;
using System.Collections.Generic;
using System.IO;

namespace NesEmulator.X86;

public sealed partial class NvGeForce
{
    private int _cPrim;                                  // begin/end primitive (0 = none)
    private readonly List<uint> _cWords = new();         // inline vertex words of the current primitive
    private readonly List<CVert> _cVerts = new();        // immediate-mode vertices of the current primitive
    private CVert _cCur;                                 // the immediate-mode vertex being built
    public long Triangles3D, Pixels3D;                   // diagnostics

    private struct CVert
    {
        public float X, Y, Z, W;                         // after transform: screen x, y, depth 0..1, 1/w
        public float A, R, G, B, SA, SR, SG, SB;         // diffuse / specular 0..1
        public float U0, V0, U1, V1;
        public float NX, NY, NZ;                         // object-space normal (lighting)
        public float Fog, EyeZ;
        public float CX, CY, CZ, CW; public bool Clip;    // clip-space position of a transformed vertex (near-plane clipping)                          // vertex fog coordinate, eye-space depth (fog sources)
    }

    private static float F(uint v) => BitConverter.Int32BitsToSingle((int)v);

    private void Celsius(uint inst, int mthd, uint data)
    {
        if (mthd == 0x17fc || mthd == 0xdfc)
        {
            if (data != 0) { _cPrim = (int)data; _cWords.Clear(); _cVerts.Clear(); return; }
            if (_cWords.Count > 0) DecodeInline(inst);   // (inline 0x1800 words come with either begin/end: the GL ICD sends them inside 0xDFC)
            DrawPrim(inst, _cPrim, _cVerts);
            _cPrim = 0; _cWords.Clear(); _cVerts.Clear();
            return;
        }
        if (mthd is >= 0x1800 and < 0x1c00) { if (_cPrim != 0) _cWords.Add(data); return; }
        if (mthd is >= 0xc00 and < 0xcf0) Immediate(inst, mthd, data);
    }

    // ================= vertices =================

    private void Immediate(uint inst, int mthd, uint data)
    {
        switch (mthd)
        {
            case 0xc6c: SetColor(ref _cCur, data, false); break;
            case 0xc8c: SetColor(ref _cCur, data, true); break;
            case 0xc90: _cCur.U0 = F(data); break; case 0xc94: _cCur.V0 = F(data); break;
            case 0xce0: _cCur.Fog = F(data); break;
            case 0xc30: _cCur.NX = F(data); break; case 0xc34: _cCur.NY = F(data); break; case 0xc38: _cCur.NZ = F(data); break;
            case 0xcb8: _cCur.U1 = F(data); break; case 0xcbc: _cCur.V1 = F(data); break;
            case 0xc50: _cCur.R = F(data); break; case 0xc54: _cCur.G = F(data); break; case 0xc58: _cCur.B = F(data); break; case 0xc5c: _cCur.A = F(data); break;
            case 0xc08:   // VERTEX_POS_3F z: emits
            case 0xc24:   // VERTEX_POS_4F w: emits
            {
                float x = F(G(inst, mthd == 0xc08 ? 0xc00 : 0xc18)), y = F(G(inst, mthd == 0xc08 ? 0xc04 : 0xc1c)), z = F(G(inst, mthd == 0xc08 ? 0xc08 : 0xc20)), w = mthd == 0xc08 ? 1 : F(data);
                var v = _cCur; Transform(inst, ref v, x, y, z, w, false);
                _cVerts.Add(v);
                break;
            }
        }
    }

    private static void SetColor(ref CVert v, uint c, bool spec)
    {
        // D3D colour dword: A R G B in bits 31..0 (B8G8R8A8 in memory)
        float a = (c >> 24) / 255f, r = ((c >> 16) & 0xff) / 255f, g = ((c >> 8) & 0xff) / 255f, b = (c & 0xff) / 255f;
        if (spec) { v.SA = a; v.SR = r; v.SG = g; v.SB = b; } else { v.A = a; v.R = r; v.G = g; v.B = b; }
    }

    /// <summary>The inline words of a primitive into vertices: attributes 7..1 (fog, weight, normal, tex1, tex0, colour2, colour), then the position.</summary>
    private void DecodeInline(uint inst)
    {
        var fmt = new uint[8];
        for (int i = 0; i < 8; i++) fmt[i] = G(inst, 0xd04 + i * 8);
        int Words(uint f) { int t = (int)(f & 15), n = (int)(f >> 4) & 15; if (n == 0) return 0; return t switch { 0 or 4 => 1, 1 => (n + 1) / 2, _ => n }; }
        int per = 0; for (int i = 0; i < 8; i++) per += Words(fmt[i]);
        if (per == 0) return;
        bool pre = (fmt[0] & 0x01000000) != 0;
        for (int b = 0; b + per <= _cWords.Count; b += per)
        {
            int o = b;
            var v = new CVert { A = 1, R = 1, G = 1, B = 1 };
            for (int i = 7; i >= 1; i--)
            {
                int n = Words(fmt[i]); if (n == 0) continue;
                switch (i)
                {
                    case 1: if ((fmt[i] & 15) == 0) SetColor(ref v, _cWords[o], false); else { v.R = F(_cWords[o]); v.G = F(_cWords[o + 1]); v.B = F(_cWords[o + 2]); v.A = n > 3 ? F(_cWords[o + 3]) : 1; } break;
                    case 2: if ((fmt[i] & 15) == 0) SetColor(ref v, _cWords[o], true); else { v.SR = F(_cWords[o]); v.SG = F(_cWords[o + 1]); v.SB = F(_cWords[o + 2]); } break;
                    case 3: v.U0 = F(_cWords[o]); v.V0 = n > 1 ? F(_cWords[o + 1]) : 0; if (n > 3) { float q = F(_cWords[o + 3]); if (q != 0) { v.U0 /= q; v.V0 /= q; } } break;
                    case 7: v.Fog = F(_cWords[o]); break;
                    case 5: if ((fmt[i] & 15) == 2 && n >= 3) { v.NX = F(_cWords[o]); v.NY = F(_cWords[o + 1]); v.NZ = F(_cWords[o + 2]); } break;
                    case 4: v.U1 = F(_cWords[o]); v.V1 = n > 1 ? F(_cWords[o + 1]) : 0; if (n > 3) { float q = F(_cWords[o + 3]); if (q != 0) { v.U1 /= q; v.V1 /= q; } } break;
                }
                o += n;
            }
            int np = (int)(fmt[0] >> 4) & 15;   // the position comes last
            float x = F(_cWords[o]), y = np > 1 ? F(_cWords[o + 1]) : 0, z = np > 2 ? F(_cWords[o + 2]) : 0, w = np > 3 ? F(_cWords[o + 3]) : 1;
            Transform(inst, ref v, x, y, z, w, pre);
            _cVerts.Add(v);
        }
    }

    /// <summary>Screen position: pre-transformed vertices carry x, y, z and 1/w; others go through the projection x modelview matrices and the viewport.</summary>
    private void Transform(uint inst, ref CVert v, float x, float y, float z, float w, bool pre)
    {
        if (pre) { v.X = x; v.Y = y; v.Z = z; v.W = w; v.EyeZ = w != 0 ? 1 / w : 0; return; }
        // the ICD loads the composite (projection x modelview x viewport scale) at 0x500, ROW-major (translation in 0x50C / 0x51C / 0x52C);
        // VIEWPORT_TRANSLATE (0x6E8) is in the chip's window space, biased by -2048 (the clip rectangles carry the +0x800); Z comes out in
        // depth-buffer units (0x6F0 = half the range)
        float[] m = Mat(inst, 0x500);
        float cx = m[0] * x + m[1] * y + m[2] * z + m[3] * w, cy = m[4] * x + m[5] * y + m[6] * z + m[7] * w,
              cz = m[8] * x + m[9] * y + m[10] * z + m[11] * w, cw = m[12] * x + m[13] * y + m[14] * z + m[15] * w;
        v.EyeZ = cw;   // (clip w = eye distance for a perspective projection: the fog source)
        if (G(inst, 0x314) != 0) Light(inst, ref v);
        v.CX = cx; v.CY = cy; v.CZ = cz; v.CW = cw; v.Clip = true;
        Project(inst, ref v);
    }

    private void Project(uint inst, ref CVert v)
    {
        float cw = v.CW == 0 ? 1e-6f : v.CW;
        float iw = 1 / cw;
        v.X = v.CX * iw + F(G(inst, 0x6e8)) + 2048; v.Y = v.CY * iw + F(G(inst, 0x6ec)) + 2048;
        bool zz16 = ((G(inst, 0x208) >> 4) & 3) == 1 || (int)(G(inst, 0x20c) >> 16) < (int)(G(inst, 0x200) >> 16) * 4;
        float zmax = zz16 ? 65535f : 16777215f;
        v.Z = (v.CZ * iw + F(G(inst, 0x6f0))) / zmax; v.W = iw;
    }

    private const float NearW = 1e-3f;

    private static CVert Lerp(in CVert a, in CVert b, float t)
    {
        static float L(float x, float y, float t) => x + (y - x) * t;
        return new CVert
        {
            A = L(a.A, b.A, t), R = L(a.R, b.R, t), G = L(a.G, b.G, t), B = L(a.B, b.B, t), SA = L(a.SA, b.SA, t), SR = L(a.SR, b.SR, t), SG = L(a.SG, b.SG, t), SB = L(a.SB, b.SB, t),
            U0 = L(a.U0, b.U0, t), V0 = L(a.V0, b.V0, t), U1 = L(a.U1, b.U1, t), V1 = L(a.V1, b.V1, t), NX = L(a.NX, b.NX, t), NY = L(a.NY, b.NY, t), NZ = L(a.NZ, b.NZ, t),
            Fog = L(a.Fog, b.Fog, t), EyeZ = L(a.EyeZ, b.EyeZ, t), CX = L(a.CX, b.CX, t), CY = L(a.CY, b.CY, t), CZ = L(a.CZ, b.CZ, t), CW = L(a.CW, b.CW, t), Clip = true,
        };
    }

    /// <summary>A triangle clipped against the near plane (clip w > NearW) before the divide: vertices behind the eye would wrap across the screen.</summary>
    private void ClipTri(uint inst, in CVert a, in CVert b, in CVert c)
    {
        if (!a.Clip || !b.Clip || !c.Clip || (a.CW > NearW && b.CW > NearW && c.CW > NearW)) { Tri(inst, a, b, c); return; }
        if (a.CW <= NearW && b.CW <= NearW && c.CW <= NearW) return;
        var inp = new[] { a, b, c }; var outp = new List<CVert>(4);
        for (int i = 0; i < 3; i++)
        {
            var p = inp[i]; var q = inp[(i + 1) % 3];
            bool pin = p.CW > NearW, qin = q.CW > NearW;
            if (pin) outp.Add(p);
            if (pin != qin) { var r = Lerp(p, q, (NearW - p.CW) / (q.CW - p.CW)); Project(inst, ref r); outp.Add(r); }
        }
        for (int i = 1; i + 1 < outp.Count; i++) Tri(inst, outp[0], outp[i], outp[i + 1]);
    }

    /// <summary>Fixed-function lighting (ambient + diffuse + specular, the driver pre-multiplies the light colours by the material):
    /// the normal goes to eye space through the inverse modelview (0x480, 3 rows of 4, applied as n x M); lights are treated as
    /// directional (DIRECTION / POSITION normalised, HALF_VECTOR for the specular). Positional attenuation and spots: not yet.</summary>
    private void Light(uint inst, ref CVert v)
    {
        float nx = v.NX, ny = v.NY, nz = v.NZ;
        if (G(inst, 0x480) != 0 || G(inst, 0x494) != 0 || G(inst, 0x4a8) != 0)
        {
            float M(int r, int c) => F(G(inst, 0x480 + (r * 4 + c) * 4));
            float ex = nx * M(0, 0) + ny * M(1, 0) + nz * M(2, 0), ey = nx * M(0, 1) + ny * M(1, 1) + nz * M(2, 1), ez = nx * M(0, 2) + ny * M(1, 2) + nz * M(2, 2);
            nx = ex; ny = ey; nz = ez;
        }
        float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz); if (len > 0) { nx /= len; ny /= len; nz /= len; }
        uint cm = G(inst, 0x298);
        // scene ambient + emission (the driver folds the material in); colour-material: the vertex colour replaces the material term
        float r = F(G(inst, 0x6c4)), g = F(G(inst, 0x6c8)), b = F(G(inst, 0x6cc));
        float sr = 0, sg = 0, sb = 0;
        uint en = G(inst, 0x3bc);
        for (int i = 0; i < 8; i++)
        {
            int mode = (int)(en >> (i * 2)) & 3; if (mode == 0) continue;
            int L = 0x800 + i * 0x80;
            float lx, ly, lz;
            if (mode == 2) { lx = F(G(inst, L + 0x5c)); ly = F(G(inst, L + 0x60)); lz = F(G(inst, L + 0x64)); }
            else { lx = F(G(inst, L + 0x34)); ly = F(G(inst, L + 0x38)); lz = F(G(inst, L + 0x3c)); }
            float ll = MathF.Sqrt(lx * lx + ly * ly + lz * lz); if (ll > 0) { lx /= ll; ly /= ll; lz /= ll; }
            float nl = MathF.Max(0, nx * lx + ny * ly + nz * lz);
            float ar = F(G(inst, L)), ag = F(G(inst, L + 4)), ab = F(G(inst, L + 8));
            float dr = F(G(inst, L + 0xc)), dg = F(G(inst, L + 0x10)), db = F(G(inst, L + 0x14));
            if ((cm & 2) != 0) { ar *= v.R; ag *= v.G; ab *= v.B; }
            if ((cm & 4) != 0) { dr *= v.R; dg *= v.G; db *= v.B; }
            r += ar + dr * nl; g += ag + dg * nl; b += ab + db * nl;
            if (nl > 0)
            {
                float hx = F(G(inst, L + 0x28)), hy = F(G(inst, L + 0x2c)), hz = F(G(inst, L + 0x30));
                float hl = MathF.Sqrt(hx * hx + hy * hy + hz * hz);
                if (hl > 0)
                {
                    float nh = MathF.Max(0, (nx * hx + ny * hy + nz * hz) / hl);
                    float shin = F(G(inst, 0x6a0)); float e = shin > 0 && shin < 128 ? shin : 16;   // (the 6 shininess words are curve coefficients; first used as the exponent: approximate)
                    float sp = MathF.Pow(nh, e);
                    float pr = F(G(inst, L + 0x18)), pg = F(G(inst, L + 0x1c)), pb = F(G(inst, L + 0x20));
                    sr += pr * sp; sg += pg * sp; sb += pb * sp;
                }
            }
        }
        float a = (cm & 4) != 0 ? v.A : F(G(inst, 0x3b4));
        if (a == 0 && (cm & 4) == 0 && G(inst, 0x3b4) == 0) a = 1;
        bool sep = G(inst, 0x3b8) != 0;
        v.R = Math.Clamp(r + (sep ? 0 : sr), 0, 1); v.G = Math.Clamp(g + (sep ? 0 : sg), 0, 1); v.B = Math.Clamp(b + (sep ? 0 : sb), 0, 1); v.A = Math.Clamp(a, 0, 1);
        if (sep) { v.SR = Math.Clamp(sr, 0, 1); v.SG = Math.Clamp(sg, 0, 1); v.SB = Math.Clamp(sb, 0, 1); }
    }

    private float[] Mat(uint inst, int b)
    {
        var m = new float[16];
        bool any = false;
        for (int i = 0; i < 16; i++) { uint u = G(inst, b + i * 4); m[i] = F(u); any |= u != 0; }
        if (!any) { m[0] = m[5] = m[10] = m[15] = 1; }
        return m;
    }

    // ================= primitives =================

    private void DrawPrim(uint inst, int prim, List<CVert> v)
    {
        int n = v.Count;
        switch (prim)
        {
            case 1: for (int i = 0; i < n; i++) Point(inst, v[i]); break;
            case 2: for (int i = 0; i + 1 < n; i += 2) Line(inst, v[i], v[i + 1]); break;
            case 3: for (int i = 0; i + 1 < n; i++) Line(inst, v[i], v[i + 1]); if (n > 2) Line(inst, v[n - 1], v[0]); break;
            case 4: for (int i = 0; i + 1 < n; i++) Line(inst, v[i], v[i + 1]); break;
            case 5: for (int i = 0; i + 2 < n; i += 3) ClipTri(inst, v[i], v[i + 1], v[i + 2]); break;
            case 6: for (int i = 0; i + 2 < n; i++) { if ((i & 1) == 0) ClipTri(inst, v[i], v[i + 1], v[i + 2]); else ClipTri(inst, v[i + 1], v[i], v[i + 2]); } break;
            case 7: case 10: for (int i = 1; i + 1 < n; i++) ClipTri(inst, v[0], v[i], v[i + 1]); break;
            case 8: for (int i = 0; i + 3 < n; i += 4) { ClipTri(inst, v[i], v[i + 1], v[i + 2]); ClipTri(inst, v[i], v[i + 2], v[i + 3]); } break;
            case 9: for (int i = 0; i + 3 < n; i += 2) { ClipTri(inst, v[i], v[i + 1], v[i + 3]); ClipTri(inst, v[i], v[i + 3], v[i + 2]); } break;
        }
    }

    private struct Tex { public uint Base; public bool Vram, Ok, Rect, Lin, Mip; public int Fmt, W, H, Pitch, WrapS, WrapT, MinF, MagF, Levels; public float Bias; public uint Dma; }

    /// <summary>A line as a quad of LINE_WIDTH (1/8 pixel units, at least 1 pixel) across its minor axis, half a pixel longer at each end.</summary>
    private void Line(uint inst, in CVert a, in CVert b)
    {
        float wdt = Math.Max(1f, (G(inst, 0x380) & 0x1ff) / 8f) * 0.5f;
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float ox = 0, oy = 0;
        if (MathF.Abs(dx) >= MathF.Abs(dy)) oy = wdt; else ox = wdt;
        CVert a0 = a, a1 = a, b0 = b, b1 = b;
        a0.X -= ox; a0.Y -= oy; a1.X += ox; a1.Y += oy; b0.X -= ox; b0.Y -= oy; b1.X += ox; b1.Y += oy;
        if (oy != 0) { float e = dx >= 0 ? 0.5f : -0.5f; a0.X -= e; a1.X -= e; b0.X += e; b1.X += e; }
        else { float e = dy >= 0 ? 0.5f : -0.5f; a0.Y -= e; a1.Y -= e; b0.Y += e; b1.Y += e; }
        TriNoCull(inst, a0, b0, b1); TriNoCull(inst, a0, b1, a1);
    }

    /// <summary>A point as a square of POINT_SIZE (1/8 pixel units, at least 1 pixel).</summary>
    private void Point(uint inst, in CVert p)
    {
        float h = Math.Max(1f, (G(inst, 0x3ec) & 0x1ff) / 8f) * 0.5f;
        CVert a = p, b = p, c = p, d = p;
        a.X -= h; a.Y -= h; b.X += h; b.Y -= h; c.X += h; c.Y += h; d.X -= h; d.Y += h;
        TriNoCull(inst, a, b, c); TriNoCull(inst, a, c, d);
    }

    private bool _noCull;
    private void TriNoCull(uint inst, in CVert a, in CVert b, in CVert c) { _noCull = true; try { Tri(inst, a, b, c); } finally { _noCull = false; } }

    private Tex TexUnit(uint inst, int u)
    {
        var t = new Tex();
        if ((G(inst, 0x228 + u * 4) & 0x40000000) == 0) return t;
        uint f = G(inst, 0x220 + u * 4);
        t.Dma = G(inst, ((f & 2) != 0 ? 0x188 : 0x184) | 0x8000);
        t.Fmt = (int)(f >> 7) & 0x1f;
        if (t.Fmt == 0xb) t.Fmt = 3;   // (the 5.32 ICD's 16-bit textures read as 0x0B: bits 9-7 = 3, X1R5G5B5, the same as the D3D test's 0x03)
        t.Rect = t.Fmt is >= 0x10 and <= 0x13;
        if (t.Rect) { uint sz = G(inst, 0x240 + u * 4); t.W = (int)(sz >> 16); t.H = (int)(sz & 0xffff); t.Pitch = (int)(G(inst, 0x230 + u * 4) >> 16); }
        else { t.W = 1 << (int)((f >> 16) & 15); t.H = 1 << (int)((f >> 20) & 15); }
        t.WrapS = (int)(f >> 24) & 15; t.WrapT = (int)(f >> 28) & 15;
        t.Base = G(inst, 0x218 + u * 4);
        uint flt = G(inst, 0x248 + u * 4);
        t.MagF = (int)(flt >> 28) & 15; t.MinF = (int)(flt >> 24) & 15;
        t.Lin = t.MagF == 2;
        t.Bias = ((int)(flt << 20) >> 28) * 0.5f;          // LOD_BIAS bits 11:8, signed (scale approximate)
        t.Mip = !t.Rect && (f & 0x8000) != 0 && t.MinF >= 3;
        t.Levels = 1;
        if (t.Mip) { int m = Math.Max(t.W, t.H); while (m > 1) { m >>= 1; t.Levels++; } }
        t.Ok = t.W > 0 && t.H > 0 && t.Dma != 0;
        return t;
    }

    private static int TexBpp(int fmt) => fmt switch { 0 or 1 or 0xb or 0x13 => 1, 6 or 7 or 0x12 => 4, _ => 2 };
    private static bool IsDxt(int fmt) => fmt is 0xc or 0xe or 0xf;

    /// <summary>Byte size of one level of a swizzled / compressed texture.</summary>
    private static uint LevelBytes(int fmt, int w, int h)
    {
        if (IsDxt(fmt)) return (uint)(Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * (fmt == 0xc ? 8 : 16));
        return (uint)(w * h * TexBpp(fmt));
    }

    /// <summary>Level L of a texture: offset from the base, width, height (levels packed one after another).</summary>
    private static (uint off, int w, int h) Level(in Tex t, int l)
    {
        uint off = 0; int w = t.W, h = t.H;
        for (int i = 0; i < l; i++) { off += LevelBytes(t.Fmt, w, h); w = Math.Max(1, w >> 1); h = Math.Max(1, h >> 1); }
        return (off, w, h);
    }

    private uint TexRead(in Tex t, uint off, int n)
    {
        var (vr, a) = DmaAddr(t.Dma, t.Base + off);
        if (vr) { a &= VramBytes - 1; return n == 1 ? _v.Ram[a] : n == 2 ? (uint)(_v.Ram[a] | _v.Ram[(a + 1) & (VramBytes - 1)] << 8) : a <= VramBytes - 4 ? BitConverter.ToUInt32(_v.Ram, (int)a) : 0; }
        uint wv = SysRead32?.Invoke(a & ~3u) ?? 0;
        if (n == 4) { if ((a & 3) == 0) return wv; uint hi = SysRead32?.Invoke((a & ~3u) + 4) ?? 0; return (uint)((((ulong)hi << 32) | wv) >> (int)((a & 3) * 8)); }
        return n == 2 ? (wv >> (int)((a & 2) * 8)) & 0xffff : (wv >> (int)((a & 3) * 8)) & 0xff;
    }

    private static uint C565(uint c) => 0xff000000 | (uint)Raster3D.Rgb565To888((int)c);

    private static uint MixC(uint a, uint b, int wa, int wb, int d) =>
        0xff000000 | (uint)((((a >> 16) & 0xff) * wa + ((b >> 16) & 0xff) * wb) / d) << 16 | (uint)((((a >> 8) & 0xff) * wa + ((b >> 8) & 0xff) * wb) / d) << 8 | (uint)(((a & 0xff) * wa + (b & 0xff) * wb) / d);

    /// <summary>One texel of a DXT1/3/5 level (4x4 blocks in row order, not swizzled).</summary>
    private uint DxtTexel(in Tex t, uint lvOff, int w, int x, int y)
    {
        int bw = Math.Max(1, (w + 3) / 4), bs = t.Fmt == 0xc ? 8 : 16;
        uint bo = lvOff + (uint)(((y >> 2) * bw + (x >> 2)) * bs);
        uint co = t.Fmt == 0xc ? bo : bo + 8;
        uint c0 = TexRead(t, co, 2), c1 = TexRead(t, co + 2, 2), bits = TexRead(t, co + 4, 4);
        int idx = (int)(bits >> (((y & 3) * 4 + (x & 3)) * 2)) & 3;
        uint p0 = C565(c0), p1 = C565(c1);
        bool four = c0 > c1 || t.Fmt != 0xc;
        uint rgb = idx switch { 0 => p0, 1 => p1, 2 => four ? MixC(p0, p1, 2, 1, 3) : MixC(p0, p1, 1, 1, 2), _ => four ? MixC(p0, p1, 1, 2, 3) : 0 };
        if (t.Fmt == 0xc) return rgb;
        int pix = (y & 3) * 4 + (x & 3);
        uint alpha;
        if (t.Fmt == 0xe) alpha = ((TexRead(t, bo + (uint)(pix >> 1), 1) >> ((pix & 1) * 4)) & 15) * 17;
        else
        {
            uint a0 = TexRead(t, bo, 1), a1 = TexRead(t, bo + 1, 1);
            ulong ab = TexRead(t, bo + 2, 4) | (ulong)TexRead(t, bo + 6, 2) << 32;
            int ai = (int)(ab >> (pix * 3)) & 7;
            alpha = ai == 0 ? a0 : ai == 1 ? a1 : a0 > a1 ? (uint)(((8 - ai) * a0 + (ai - 1) * a1) / 7) : ai == 6 ? 0 : ai == 7 ? 255u : (uint)(((6 - ai) * a0 + (ai - 1) * a1) / 5);
        }
        return (rgb & 0xffffff) | alpha << 24;
    }

    private static uint Swz(int x, int y, int w, int h)
    {
        uint o = 0; int bit = 0;
        for (int s = 1; s < w || s < h; s <<= 1)
        {
            if (s < w) { if ((x & s) != 0) o |= 1u << bit; bit++; }
            if (s < h) { if ((y & s) != 0) o |= 1u << bit; bit++; }
        }
        return o;
    }

    private uint Texel(in Tex t, int x, int y) => Texel(t, 0, t.W, t.H, x, y);

    private uint Texel(in Tex t, uint lvOff, int lw, int lh, int x, int y)
    {
        x = Wrap(x, lw, t.WrapS); y = Wrap(y, lh, t.WrapT);
        if (IsDxt(t.Fmt)) return DxtTexel(t, lvOff, lw, x, y);
        int bpp = TexBpp(t.Fmt);
        uint off = lvOff + (t.Rect ? (uint)(y * t.Pitch + x * bpp) : Swz(x, y, lw, lh) * (uint)bpp);
        uint raw = TexRead(t, off, bpp);
        switch (t.Fmt)
        {
            case 0: return 0xff000000 | raw * 0x010101;                                     // L8
            case 1: case 0x13: return raw * 0x01010101;                                      // I8
            case 2: case 0x10: case 3: { uint r = (raw >> 10) & 31, g = (raw >> 5) & 31, b = raw & 31; return ((raw & 0x8000) != 0 || t.Fmt == 3 ? 0xff000000 : 0) | (r << 3 | r >> 2) << 16 | (g << 3 | g >> 2) << 8 | (b << 3 | b >> 2); }
            case 4: { uint a4 = raw >> 12, r = (raw >> 8) & 15, g = (raw >> 4) & 15, b = raw & 15; return a4 * 0x11 << 24 | r * 0x11 << 16 | g * 0x11 << 8 | b * 0x11; }
            case 5: case 0x11: return 0xff000000 | (uint)Raster3D.Rgb565To888((int)raw);
            case 7: return raw | 0xff000000;
            default: return raw;                                                              // A8R8G8B8
        }
    }

    private static int Wrap(int c, int n, int mode)
    {
        switch (mode)
        {
            case 2: { int p = c & (2 * n - 1); if (c < 0) p = ((c % (2 * n)) + 2 * n) % (2 * n); return p < n ? p : 2 * n - 1 - p; }
            case 3: case 4: case 5: return Math.Clamp(c, 0, n - 1);
            default: return ((c % n) + n) % n;
        }
    }

    private uint Sample(in Tex t, float u, float v) => SampleLevel(t, 0, u, v, t.Lin);

    private uint SampleLevel(in Tex t, int l, float u, float v, bool lin)
    {
        var (off, lw, lh) = l == 0 ? (0u, t.W, t.H) : Level(t, l);
        float fu = t.Rect ? u : u * lw, fv = t.Rect ? v : v * lh;
        if (!lin) return Texel(t, off, lw, lh, (int)MathF.Floor(fu), (int)MathF.Floor(fv));
        fu -= 0.5f; fv -= 0.5f;
        int x0 = (int)MathF.Floor(fu), y0 = (int)MathF.Floor(fv);
        int fx = (int)((fu - x0) * 256), fy = (int)((fv - y0) * 256);
        return Rgba.Bilinear(Texel(t, off, lw, lh, x0, y0), Texel(t, off, lw, lh, x0 + 1, y0), Texel(t, off, lw, lh, x0, y0 + 1), Texel(t, off, lw, lh, x0 + 1, y0 + 1), fx, fy).ToArgb();
    }

    /// <summary>Filtered sample with the level of detail from the screen-space derivatives: magnify / minify filters and mipmaps.</summary>
    private uint SampleLod(in Tex t, float u, float v, float dudx, float dvdx, float dudy, float dvdy)
    {
        float sx = MathF.Sqrt(dudx * dudx * t.W * t.W + dvdx * dvdx * t.H * t.H), sy = MathF.Sqrt(dudy * dudy * t.W * t.W + dvdy * dvdy * t.H * t.H);
        float rho = MathF.Max(sx, sy);
        float lod = (rho > 0 ? MathF.Log2(rho) : -16) + t.Bias;
        if (lod <= 0) return SampleLevel(t, 0, u, v, t.MagF == 2);
        bool lin = t.MinF is 2 or 4 or 6;
        if (!t.Mip) return SampleLevel(t, 0, u, v, lin);
        float maxL = t.Levels - 1;
        if (lod > maxL) lod = maxL;
        if (t.MinF is 3 or 4) return SampleLevel(t, (int)(lod + 0.5f), u, v, lin);   // *_MIPMAP_NEAREST
        int l0 = (int)lod, l1 = Math.Min(l0 + 1, t.Levels - 1);
        uint c0 = SampleLevel(t, l0, u, v, lin);
        if (l1 == l0) return c0;
        return Rgba.Bilinear(c0, SampleLevel(t, l1, u, v, lin), 0, 0, (int)((lod - l0) * 256), 0).ToArgb();
    }

    // ================= the triangle =================

    private static void Persp(in CVert a, in CVert b, in CVert c, float inv, float px, float py, out float q0, out float q1, out float q2)
    {
        float w0 = ((b.X - px) * (c.Y - py) - (c.X - px) * (b.Y - py)) * inv;
        float w1 = ((c.X - px) * (a.Y - py) - (a.X - px) * (c.Y - py)) * inv;
        float w2 = 1 - w0 - w1;
        q0 = w0 * a.W; q1 = w1 * b.W; q2 = w2 * c.W;
        float qs = q0 + q1 + q2; if (qs == 0) qs = 1e-12f;
        q0 /= qs; q1 /= qs; q2 /= qs;
    }

    private static int StencilOp(uint op, int s, int r) => op switch
    {
        0 => 0, 0x1e01 => r, 0x1e02 => Math.Min(s + 1, 255), 0x1e03 => Math.Max(s - 1, 0), 0x150a => ~s & 0xff,
        0x8507 => (s + 1) & 0xff, 0x8508 => (s - 1) & 0xff, _ => s,
    };

    private bool _fogLogged;

    private void Tri(uint inst, in CVert a, in CVert b, in CVert c)
    {
        float area = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
        if (area == 0 || float.IsNaN(area)) return;
        if (!_noCull && G(inst, 0x308) != 0)   // cull: front face CW (0x900) / CCW (0x901), in screen space with y down
        {
            bool cw = area > 0;    // (y down: a positive area is clockwise on screen)
            bool front = G(inst, 0x3a0) == 0x900 ? cw : !cw;
            uint cf = G(inst, 0x39c);
            if (cf == 0x408 || (cf == 0x405 && !front) || (cf == 0x404 && front)) return;
        }
        Triangles3D++;
        if (Triangles3D < 40) OpLog?.Invoke($"tri ({a.X:F1},{a.Y:F1},{a.Z:F2},{a.W:F2} c{a.R:F2}) ({b.X:F1},{b.Y:F1}) ({c.X:F1},{c.Y:F1}) rt {G(inst, 0x210):X} pitch {G(inst, 0x20c):X} fmt {G(inst, 0x208):X}");
        // the target
        uint rtf = G(inst, 0x208), pitches = G(inst, 0x20c);
        int cfmt = (int)(rtf & 15), cbpp = cfmt == 3 ? 2 : cfmt == 9 ? 1 : 4;
        uint cdma = G(inst, 0x194 | 0x8000), zdma = G(inst, 0x198 | 0x8000);
        uint cbase = (cdma != 0 ? DmaAddr(cdma, G(inst, 0x210)).addr : G(inst, 0x210)) & (VramBytes - 1);
        uint zbase = (zdma != 0 ? DmaAddr(zdma, G(inst, 0x214)).addr : G(inst, 0x214)) & (VramBytes - 1);
        int cpitch = (int)(pitches & 0xffff), zpitch = (int)(pitches >> 16);
        // Z16 when RT_FORMAT says so, or when the zeta pitch has no room for 4-byte depth (the 5.32 driver: format 0x103 with a 640-wide Z16
        // buffer at pitch 0x600; reading that as Z24S8 wrapped every row at x = 384: a black band in 3D scenes)
        bool z16 = ((rtf >> 4) & 3) == 1 || zpitch < (int)(G(inst, 0x200) >> 16) * 4;
        bool zTest = G(inst, 0x30c) != 0 && zpitch != 0, zWrite = G(inst, 0x35c) != 0 && zpitch != 0;
        int zFunc = (int)G(inst, 0x354) & 7;
        // stencil (Z24S8 only: the low byte of the zeta word)
        bool sTest = G(inst, 0x32c) != 0 && zpitch != 0 && !z16;
        int sRef = (int)G(inst, 0x368) & 0xff, sMask = (int)G(inst, 0x36c) & 0xff, sWMask = (int)G(inst, 0x360) & 0xff, sFunc = (int)G(inst, 0x364) & 7;
        uint sFail = G(inst, 0x370), sZFail = G(inst, 0x374), sZPass = G(inst, 0x378);
        uint hz = G(inst, 0x200), vt = G(inst, 0x204);
        int cx0 = (int)(hz & 0xffff), cy0 = (int)(vt & 0xffff), cx1 = cx0 + (int)(hz >> 16), cy1 = cy0 + (int)(vt >> 16);
        // the bounding box, pixel centres at +0.5
        int minX = Math.Max(cx0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)))), maxX = Math.Min(cx1 - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        int minY = Math.Max(cy0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)))), maxY = Math.Min(cy1 - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        if (minX > maxX || minY > maxY) return;
        var t0 = TexUnit(inst, 0); var t1 = TexUnit(inst, 1);
        if (Triangles3D < 60 && OpLog != null)
        {
            string Td(in Tex t) { if (!t.Ok) return "off"; var (vr, ad) = DmaAddr(t.Dma, t.Base); return $"fmt {t.Fmt} {t.W}x{t.H} dma {t.Dma:X} {(vr ? "vram" : "sys")} {ad:X} texel0 {Texel(t, 0, 0):X8} texel(5,5) {Texel(t, 5, 5):X8}"; }
            OpLog($"uv {a.U0:F2},{a.V0:F2} tex0 {Td(t0)} | tex1 {Td(t1)} | col {a.R:F2},{a.G:F2},{a.B:F2},{a.A:F2} comb {G(inst, 0x268):X8} {G(inst, 0x280):X8} {G(inst, 0x288):X8} {G(inst, 0x28c):X8}");
        }
        bool lod0 = t0.Ok && !t0.Rect && (t0.Mip || t0.MinF != t0.MagF), lod1 = t1.Ok && !t1.Rect && (t1.Mip || t1.MinF != t1.MagF);
        bool flat = G(inst, 0x37c) == 0x1d00;
        bool aTest = G(inst, 0x300) != 0; int aFunc = (int)G(inst, 0x33c) & 7, aRef = (int)G(inst, 0x340) & 0xff;
        bool blend = G(inst, 0x304) != 0;
        uint cmask = G(inst, 0x358);
        // fog: the final combiner's FOG input (register 3) = fog colour rgb + fog factor alpha
        bool fog = G(inst, 0x2a4) != 0;
        uint fogMode = G(inst, 0x29c), fogSrc = G(inst, 0x2a0), fcol = G(inst, 0x2a8);
        float fk0 = F(G(inst, 0x680)), fk1 = F(G(inst, 0x684)), fk2 = F(G(inst, 0x688));
        var fogC = new Vec4((fcol & 0xff) / 255f, ((fcol >> 8) & 0xff) / 255f, ((fcol >> 16) & 0xff) / 255f, 1);
        if (fog && !_fogLogged) { _fogLogged = true; OpLog?.Invoke($"fog mode {fogMode:X} src {fogSrc} k {fk0} {fk1} {fk2} col {fcol:X8} vfog {a.Fog} {b.Fog} eyez {a.EyeZ} specA {a.SA}"); }
        float inv = 1 / area;
        for (int y = minY; y <= maxY; y++)
        {
            float py = y + 0.5f;
            for (int x = minX; x <= maxX; x++)
            {
                float px = x + 0.5f;
                float w0 = ((b.X - px) * (c.Y - py) - (c.X - px) * (b.Y - py)) * inv;
                float w1 = ((c.X - px) * (a.Y - py) - (a.X - px) * (c.Y - py)) * inv;
                float w2 = 1 - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                float z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                // stencil + depth
                uint za = (uint)(zbase + y * zpitch + x * (z16 ? 2 : 4)) & (VramBytes - 1);
                uint zv = z16 ? (uint)Math.Clamp(z * 65535f, 0, 65535) : (uint)Math.Clamp(z * 16777215f, 0, 16777215);
                int sOld = 0, sNew = -1;
                if (zTest || zWrite || sTest)
                {
                    uint word = z16 ? (uint)(_v.Ram[za] | _v.Ram[za + 1] << 8) : BitConverter.ToUInt32(_v.Ram, (int)za);
                    uint old = z16 ? word : word >> 8;
                    sOld = (int)(word & 0xff);
                    if (sTest && !Raster3D.Compare(sFunc, sRef & sMask, sOld & sMask))
                    {
                        WriteStencil(za, sOld, StencilOp(sFail, sOld, sRef), sWMask);
                        continue;
                    }
                    if (zTest && !Raster3D.Compare(zFunc, (int)zv, (int)old))
                    {
                        if (sTest) WriteStencil(za, sOld, StencilOp(sZFail, sOld, sRef), sWMask);
                        continue;
                    }
                    if (sTest) sNew = StencilOp(sZPass, sOld, sRef);
                }
                // perspective-correct attributes
                float q0 = w0 * a.W, q1 = w1 * b.W, q2 = w2 * c.W, qs = q0 + q1 + q2;
                if (qs == 0) qs = 1e-12f;
                q0 /= qs; q1 /= qs; q2 /= qs;
                var col = flat ? new Vec4(c.R, c.G, c.B, c.A) : new Vec4(q0 * a.R + q1 * b.R + q2 * c.R, q0 * a.G + q1 * b.G + q2 * c.G, q0 * a.B + q1 * b.B + q2 * c.B, q0 * a.A + q1 * b.A + q2 * c.A);
                var spec = new Vec4(q0 * a.SR + q1 * b.SR + q2 * c.SR, q0 * a.SG + q1 * b.SG + q2 * c.SG, q0 * a.SB + q1 * b.SB + q2 * c.SB, q0 * a.SA + q1 * b.SA + q2 * c.SA);
                Vec4 tx0 = default, tx1 = default;
                float r0 = 0, r1 = 0, r2 = 0, d0 = 0, d1 = 0, d2 = 0;
                if (lod0 || lod1) { Persp(a, b, c, inv, px + 1, py, out r0, out r1, out r2); Persp(a, b, c, inv, px, py + 1, out d0, out d1, out d2); }
                if (t0.Ok)
                {
                    float u = q0 * a.U0 + q1 * b.U0 + q2 * c.U0, v = q0 * a.V0 + q1 * b.V0 + q2 * c.V0;
                    tx0 = Vec4.FromArgb(lod0 ? SampleLod(t0, u, v, r0 * a.U0 + r1 * b.U0 + r2 * c.U0 - u, r0 * a.V0 + r1 * b.V0 + r2 * c.V0 - v, d0 * a.U0 + d1 * b.U0 + d2 * c.U0 - u, d0 * a.V0 + d1 * b.V0 + d2 * c.V0 - v) : Sample(t0, u, v));
                }
                if (t1.Ok)
                {
                    float u = q0 * a.U1 + q1 * b.U1 + q2 * c.U1, v = q0 * a.V1 + q1 * b.V1 + q2 * c.V1;
                    tx1 = Vec4.FromArgb(lod1 ? SampleLod(t1, u, v, r0 * a.U1 + r1 * b.U1 + r2 * c.U1 - u, r0 * a.V1 + r1 * b.V1 + r2 * c.V1 - v, d0 * a.U1 + d1 * b.U1 + d2 * c.U1 - u, d0 * a.V1 + d1 * b.V1 + d2 * c.V1 - v) : Sample(t1, u, v));
                }
                Vec4 fogV = default;
                if (fog)
                {
                    float fc = fogSrc == 0 ? q0 * a.Fog + q1 * b.Fog + q2 * c.Fog : MathF.Abs(q0 * a.EyeZ + q1 * b.EyeZ + q2 * c.EyeZ);
                    float ff = fogMode switch
                    {
                        0x2601 => fk0 + fk1 * fc + fk2 * fc * fc - 1,           // LINEAR: k0 = 2 + start/(end-start), k1 = -1/(end-start) (nouveau nv10)
                        0x0803 => MathF.Exp(-MathF.Pow(fk1 / -0.21f * fc, 2)), // EXP2 (coefficient scale approximate)
                        _ => MathF.Exp(fk1 / 0.09f * MathF.Abs(fc)),           // EXP / EXP_ABS (k1 = -0.09 density, approximate)
                    };
                    fogV = new Vec4(fogC.X, fogC.Y, fogC.Z, Math.Clamp(ff, 0, 1));
                }
                var o = Combine(inst, col, spec, tx0, tx1, fogV);
                int oa = (int)(Math.Clamp(o.W, 0, 1) * 255 + 0.5f);
                if (aTest && !Raster3D.Compare(aFunc, oa, aRef)) continue;
                uint ca = (uint)(cbase + y * cpitch + x * cbpp) & (VramBytes - 1);
                uint dst = cbpp == 2 ? 0xff000000 | (uint)Raster3D.Rgb565To888(_v.Ram[ca] | _v.Ram[ca + 1] << 8) : cbpp == 4 ? BitConverter.ToUInt32(_v.Ram, (int)ca) : _v.Ram[ca];
                if (blend) o = Blend(inst, o, Vec4.FromArgb(dst));
                uint outc = o.ToArgb();
                if (cmask != 0x01010101 && cmask != 0) outc = (outc & MaskBits(cmask)) | (dst & ~MaskBits(cmask));
                {
                    if (cbpp == 2) { uint p = ((outc >> 8) & 0xf800) | ((outc >> 5) & 0x07e0) | ((outc >> 3) & 0x1f); _v.Ram[ca] = (byte)p; _v.Ram[ca + 1] = (byte)(p >> 8); }
                    else if (cbpp == 4) BitConverter.TryWriteBytes(_v.Ram.AsSpan((int)ca), cfmt == 5 ? outc | 0xff000000 : outc);
                    else _v.Ram[ca] = (byte)outc;
                }
                if (zWrite)
                {
                    if (z16) { _v.Ram[za] = (byte)zv; _v.Ram[za + 1] = (byte)(zv >> 8); }
                    else { uint old = BitConverter.ToUInt32(_v.Ram, (int)za); BitConverter.TryWriteBytes(_v.Ram.AsSpan((int)za), (zv << 8) | (old & 0xff)); }
                }
                if (sNew >= 0) WriteStencil(za, sOld, sNew, sWMask);
                Pixels3D++;
            }
        }
    }

    private void WriteStencil(uint za, int sOld, int sNew, int wmask) => _v.Ram[za] = (byte)((sOld & ~wmask) | (sNew & wmask));

    private static uint MaskBits(uint m) => ((m >> 24) & 1) * 0xff000000 | ((m >> 16) & 1) * 0x00ff0000 | ((m >> 8) & 1) * 0x0000ff00 | (m & 1) * 0x000000ff;

    // ================= register combiners =================

    private struct Vec4
    {
        public float X, Y, Z, W;   // r, g, b, a in -1..1
        public Vec4(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
        public static Vec4 FromArgb(uint c) => new(((c >> 16) & 0xff) / 255f, ((c >> 8) & 0xff) / 255f, (c & 0xff) / 255f, (c >> 24) / 255f);
        public uint ToArgb()
        {
            static uint B(float v) => (uint)(Math.Clamp(v, 0, 1) * 255 + 0.5f);
            return B(W) << 24 | B(X) << 16 | B(Y) << 8 | B(Z);
        }
    }

    private Vec4 Combine(uint inst, Vec4 prim, Vec4 sec, Vec4 t0, Vec4 t1, Vec4 fog)
    {
        var reg = new Vec4[16];
        reg[1] = Vec4.FromArgb(G(inst, 0x270)); reg[2] = Vec4.FromArgb(G(inst, 0x274));
        reg[3] = fog; reg[4] = prim; reg[5] = sec; reg[8] = t0; reg[9] = t1;
        reg[12] = new Vec4(0, 0, 0, t0.W);   // spare0 starts with texture 0's alpha (GL rule)
        for (int s = 0; s < 2; s++)
        {
            uint inA = G(inst, 0x260 + s * 4), inC = G(inst, 0x268 + s * 4), outA = G(inst, 0x278 + s * 4), outC = G(inst, 0x280 + s * 4);
            if (inC == 0 && inA == 0 && outC == 0 && outA == 0) continue;
            // rgb
            Vec4 A = In(reg, inC >> 24, false), B = In(reg, inC >> 16, false), C = In(reg, inC >> 8, false), D = In(reg, inC, false);
            Vec4 ab = (outC & 0x2000) != 0 ? Dot(A, B) : new Vec4(A.X * B.X, A.Y * B.Y, A.Z * B.Z, 0);
            Vec4 cd = (outC & 0x1000) != 0 ? Dot(C, D) : new Vec4(C.X * D.X, C.Y * D.Y, C.Z * D.Z, 0);
            Vec4 sum = (outC & 0x4000) != 0 ? (reg[12].W >= 0.5f ? cd : ab) : new Vec4(ab.X + cd.X, ab.Y + cd.Y, ab.Z + cd.Z, 0);
            // alpha
            float aA = InA(reg, inA >> 24), aB = InA(reg, inA >> 16), aC = InA(reg, inA >> 8), aD = InA(reg, inA);
            float aab = aA * aB, acd = aC * aD, asum = (outA & 0x4000) != 0 ? (reg[12].W >= 0.5f ? acd : aab) : aab + acd;
            var nr = (Vec4[])reg.Clone();
            WriteOut(nr, (int)(outC >> 4) & 15, Scale(ab, outC), true); WriteOut(nr, (int)outC & 15, Scale(cd, outC), true); WriteOut(nr, (int)(outC >> 8) & 15, Scale(sum, outC), true);
            WriteOutA(nr, (int)(outA >> 4) & 15, ScaleA(aab, outA)); WriteOutA(nr, (int)outA & 15, ScaleA(acd, outA)); WriteOutA(nr, (int)(outA >> 8) & 15, ScaleA(asum, outA));
            reg = nr;
        }
        // final: rgb = A*B + (1-A)*C + D, alpha = G; E*F and spare0+secondary are inputs
        uint f0 = G(inst, 0x288), f1 = G(inst, 0x28c);
        var E = In(reg, f1 >> 24, true); var Fv = In(reg, f1 >> 16, true);
        reg[15] = new Vec4(E.X * Fv.X, E.Y * Fv.Y, E.Z * Fv.Z, 0);
        reg[14] = new Vec4(reg[12].X + reg[5].X, reg[12].Y + reg[5].Y, reg[12].Z + reg[5].Z, 0);
        if ((f1 & 0x80) != 0) reg[14] = new Vec4(Math.Min(reg[14].X, 1), Math.Min(reg[14].Y, 1), Math.Min(reg[14].Z, 1), 0);
        Vec4 fa = In(reg, f0 >> 24, true), fb = In(reg, f0 >> 16, true), fc = In(reg, f0 >> 8, true), fd = In(reg, f0, true);
        var o = new Vec4(fa.X * fb.X + (1 - fa.X) * fc.X + fd.X, fa.Y * fb.Y + (1 - fa.Y) * fc.Y + fd.Y, fa.Z * fb.Z + (1 - fa.Z) * fc.Z + fd.Z, InA(reg, f1 >> 8, true));
        return o;
    }

    private static Vec4 Dot(Vec4 a, Vec4 b) { float d = a.X * b.X + a.Y * b.Y + a.Z * b.Z; return new Vec4(d, d, d, 0); }

    private static float Map(float v, int m) => m switch
    {
        0 => Math.Max(v, 0), 1 => 1 - Math.Clamp(v, 0, 1), 2 => 2 * Math.Max(v, 0) - 1, 3 => -2 * Math.Max(v, 0) + 1,
        4 => Math.Max(v, 0) - 0.5f, 5 => -Math.Max(v, 0) + 0.5f, 6 => v, _ => -v,
    };

    /// <summary>An RGB input byte (variable bits 3-0, alpha usage bit 4, mapping bits 7-5).</summary>
    private static Vec4 In(Vec4[] reg, uint b, bool final)
    {
        int var = (int)b & 15, m = (int)(b >> 5) & 7; bool alpha = (b & 0x10) != 0;
        var s = reg[var];
        if (var == 0) s = default;
        if (alpha) s = new Vec4(s.W, s.W, s.W, s.W);
        if (final) m &= 1;   // the final combiner has only identity / invert
        return new Vec4(Map(s.X, m), Map(s.Y, m), Map(s.Z, m), Map(s.W, m));
    }

    /// <summary>An alpha input byte (bit 4: alpha, else blue).</summary>
    private static float InA(Vec4[] reg, uint b, bool final = false)
    {
        int var = (int)b & 15, m = (int)(b >> 5) & 7;
        if (var == 0) return Map(0, final ? m & 1 : m);
        var s = reg[var];
        return Map((b & 0x10) != 0 ? s.W : s.Z, final ? m & 1 : m);
    }

    private static Vec4 Scale(Vec4 v, uint o)
    {
        float bias = (o & 0x8000) != 0 ? -0.5f : 0, sc = ((o >> 16) & 3) switch { 1 => 2, 2 => 4, 3 => 0.5f, _ => 1 };
        static float Cl(float x) => Math.Clamp(x, -1, 1);
        return new Vec4(Cl((v.X + bias) * sc), Cl((v.Y + bias) * sc), Cl((v.Z + bias) * sc), v.W);
    }

    private static float ScaleA(float v, uint o)
    {
        float bias = (o & 0x8000) != 0 ? -0.5f : 0, sc = ((o >> 16) & 3) switch { 1 => 2, 2 => 4, 3 => 0.5f, _ => 1 };
        return Math.Clamp((v + bias) * sc, -1, 1);
    }

    private static void WriteOut(Vec4[] r, int reg, Vec4 v, bool rgb) { if (reg == 0) return; r[reg] = new Vec4(v.X, v.Y, v.Z, r[reg].W); }
    private static void WriteOutA(Vec4[] r, int reg, float a) { if (reg == 0) return; r[reg] = new Vec4(r[reg].X, r[reg].Y, r[reg].Z, a); }

    // ================= blending =================

    private Vec4 Blend(uint inst, Vec4 s, Vec4 d)
    {
        uint sf = G(inst, 0x344), df = G(inst, 0x348), eq = G(inst, 0x350);
        Vec4 cc = Vec4.FromArgb(G(inst, 0x34c));
        Vec4 Fac(uint f) => f switch
        {
            0 => new(0, 0, 0, 0), 1 => new(1, 1, 1, 1),
            0x300 => s, 0x301 => new(1 - s.X, 1 - s.Y, 1 - s.Z, 1 - s.W),
            0x302 => new(s.W, s.W, s.W, s.W), 0x303 => new(1 - s.W, 1 - s.W, 1 - s.W, 1 - s.W),
            0x304 => new(d.W, d.W, d.W, d.W), 0x305 => new(1 - d.W, 1 - d.W, 1 - d.W, 1 - d.W),
            0x306 => d, 0x307 => new(1 - d.X, 1 - d.Y, 1 - d.Z, 1 - d.W),
            0x308 => new(Math.Min(s.W, 1 - d.W), Math.Min(s.W, 1 - d.W), Math.Min(s.W, 1 - d.W), 1),
            0x8001 => cc, 0x8002 => new(1 - cc.X, 1 - cc.Y, 1 - cc.Z, 1 - cc.W),
            0x8003 => new(cc.W, cc.W, cc.W, cc.W), 0x8004 => new(1 - cc.W, 1 - cc.W, 1 - cc.W, 1 - cc.W),
            _ => new(1, 1, 1, 1),
        };
        var a = Fac(sf); var b = Fac(df);
        float Op(float x, float y) => eq switch { 0x800a => x - y, 0x800b => y - x, 0x8007 => Math.Min(x, y), 0x8008 => Math.Max(x, y), _ => x + y };
        if (eq is 0x8007 or 0x8008) return new Vec4(Op(s.X, d.X), Op(s.Y, d.Y), Op(s.Z, d.Z), Op(s.W, d.W));
        return new Vec4(Op(s.X * a.X, d.X * b.X), Op(s.Y * a.Y, d.Y * b.Y), Op(s.Z * a.Z, d.Z * b.Z), Op(s.W * a.W, d.W * b.W));
    }

    private void SaveCelsius(BinaryWriter w)
    {
        w.Write(_cPrim); w.Write(_cWords.Count); foreach (var x in _cWords) w.Write(x);
    }

    private void LoadCelsius(BinaryReader r)
    {
        _cPrim = r.ReadInt32(); _cWords.Clear(); int n = r.ReadInt32(); for (int i = 0; i < n; i++) _cWords.Add(r.ReadUInt32());
        _cVerts.Clear();
    }
}
