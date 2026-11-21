// Shared pieces of the x86 PC's 3D cards' software rasteriser (Voodoo now; ViRGE / a GeForce later). Written for BrokenNes.
// Exact, deterministic, single-threaded: frames reproduce bit for bit for savestates and jit parity. A host-GPU path plugs in behind the
// card's command stream later and is checked against this one.
// Parts are ported from MAME (src/devices/video/poly.h, voodoo_render.cpp/.h, emu/video/rgbgen.h):
//   license: BSD-3-Clause; copyright-holders: Aaron Giles, Ville Linde (see THIRD_PARTY_NOTICES.md).

using System;
using System.Numerics;

namespace NesEmulator.X86;

/// <summary>A colour as four signed ints (MAME's rgbaint_t): the pipeline's working type. Channels may go out of 0..255 between steps.</summary>
public struct Rgba
{
    public int A, R, G, B;
    public Rgba(int a, int r, int g, int b) { A = a; R = r; G = g; B = b; }
    public static Rgba From(uint argb) => new((int)(argb >> 24), (int)(argb >> 16) & 0xff, (int)(argb >> 8) & 0xff, (int)argb & 0xff);
    public uint ToArgb() => (uint)(A & 0xff) << 24 | (uint)(R & 0xff) << 16 | (uint)(G & 0xff) << 8 | (uint)(B & 0xff);
    public void SetAll(int v) { A = R = G = B = v; }
    public void Zero() { A = R = G = B = 0; }
    public void ZeroRgb() { R = G = B = 0; }
    public Rgba SelectAlpha() => new(A, A, A, A);
    public void Add(in Rgba o) { A += o.A; R += o.R; G += o.G; B += o.B; }
    public void Sub(in Rgba o) { A -= o.A; R -= o.R; G -= o.G; B -= o.B; }
    public void AddImm(int v) { A += v; R += v; G += v; B += v; }
    public void Xor(int a, int r, int g, int b) { A ^= a; R ^= r; G ^= g; B ^= b; }
    public void Clamp8() { A = Math.Clamp(A, 0, 255); R = Math.Clamp(R, 0, 255); G = Math.Clamp(G, 0, 255); B = Math.Clamp(B, 0, 255); }
    /// <summary>this = this * scale >> 8 + add, clamped to 0..255 per channel.</summary>
    public void ScaleAddClamp(in Rgba s, in Rgba add)
    {
        A = (A * s.A >> 8) + add.A; R = (R * s.R >> 8) + add.R; G = (G * s.G >> 8) + add.G; B = (B * s.B >> 8) + add.B;
        Clamp8();
    }
    public void ScaleImmClamp(int s) { A = A * s >> 8; R = R * s >> 8; G = G * s >> 8; B = B * s >> 8; Clamp8(); }
    /// <summary>this = (this * s1 + o * s2) >> 8, clamped.</summary>
    public void Scale2AddClamp(in Rgba s1, in Rgba o, in Rgba s2)
    {
        A = (A * s1.A + o.A * s2.A) >> 8; R = (R * s1.R + o.R * s2.R) >> 8; G = (G * s1.G + o.G * s2.G) >> 8; B = (B * s1.B + o.B * s2.B) >> 8;
        Clamp8();
    }
    /// <summary>Bilinear blend of four ARGB texels, u/v = 8-bit fractions.</summary>
    public static Rgba Bilinear(uint c00, uint c01, uint c10, uint c11, int u, int v)
    {
        static int L(uint a, uint b, int sh, int f) { int x = (int)(a >> sh) & 0xff, y = (int)(b >> sh) & 0xff; return x + ((y - x) * f >> 8); }
        static int Ch(uint c00, uint c01, uint c10, uint c11, int sh, int u, int v) { int t = L(c00, c01, sh, u), b = L(c10, c11, sh, u); return t + ((b - t) * v >> 8); }
        return new(Ch(c00, c01, c10, c11, 24, u, v), Ch(c00, c01, c10, c11, 16, u, v), Ch(c00, c01, c10, c11, 8, u, v), Ch(c00, c01, c10, c11, 0, u, v));
    }
}

public static partial class Raster3D
{
    // ---------------- colour expansion (bit replication) ----------------
    public static int Pal1(int x) => (x & 1) != 0 ? 0xff : 0;
    public static int Pal2(int x) => (x & 3) * 0x55;
    public static int Pal3(int x) { x &= 7; return x << 5 | x << 2 | x >> 1; }
    public static int Pal4(int x) => (x & 15) * 0x11;
    public static int Pal5(int x) { x &= 31; return x << 3 | x >> 2; }
    public static int Pal6(int x) { x &= 63; return x << 2 | x >> 4; }
    public static uint Argb(int a, int r, int g, int b) => (uint)a << 24 | (uint)r << 16 | (uint)g << 8 | (uint)b;
    public static int Rgb565To888(int c) => Pal5(c >> 11) << 16 | Pal6(c >> 5) << 8 | Pal5(c);

    // ---------------- scan conversion (MAME poly.h, float, no clipping) ----------------
    public struct Vertex { public float X, Y; public Vertex(float x, float y) { X = x; Y = y; } }

    public static int RoundCoordinate(float v)
    {
        if (v >= int.MaxValue) return int.MaxValue;
        float ip = MathF.Floor(v);
        if (ip < int.MinValue) return int.MinValue;
        return (int)ip + (v - ip > 0.5f ? 1 : 0);
    }

    /// <summary>Calls <paramref name="span"/>(y, startx, stopx) for every scanline of the triangle, top to bottom (pixel centres at +0.5).</summary>
    public static int Triangle(Vertex a, Vertex b, Vertex c, Action<int, int, int> span)
    {
        Vertex v1 = a, v2 = b, v3 = c;
        if (v2.Y < v1.Y) (v1, v2) = (v2, v1);
        if (v3.Y < v2.Y) { (v2, v3) = (v3, v2); if (v2.Y < v1.Y) (v1, v2) = (v2, v1); }
        int y1 = RoundCoordinate(v1.Y), y3 = RoundCoordinate(v3.Y);
        y1 = Math.Max(y1, -4096); y3 = Math.Min(y3, 4096);
        if (y3 - y1 <= 0) return 0;
        float d12 = v2.Y == v1.Y ? 0f : (v2.X - v1.X) / (v2.Y - v1.Y);
        float d13 = v3.Y == v1.Y ? 0f : (v3.X - v1.X) / (v3.Y - v1.Y);
        float d23 = v3.Y == v2.Y ? 0f : (v3.X - v2.X) / (v3.Y - v2.Y);
        int pixels = 0;
        for (int y = y1; y < y3; y++)
        {
            float fy = y + 0.5f;
            float sx = v1.X + (fy - v1.Y) * d13;
            float ex = fy < v2.Y ? v1.X + (fy - v1.Y) * d12 : v2.X + (fy - v2.Y) * d23;
            int isx = RoundCoordinate(sx), iex = RoundCoordinate(ex);
            if (isx > iex) (isx, iex) = (iex, isx);
            if (isx >= iex) continue;
            pixels += iex - isx;
            span(y, isx, iex);
        }
        return pixels;
    }

    // ---------------- dithering (hardware-verified Voodoo equations, MAME dither_helper) ----------------
    public static readonly byte[] Dither4x4 = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
    public static readonly byte[] Dither2x2 = { 8, 10, 8, 10, 11, 9, 11, 9, 8, 10, 8, 10, 11, 9, 11, 9 };
    // [y&3][x&3][g][value]: 4*4*2*256
    public static readonly byte[] Dither4Lookup = new byte[256 * 4 * 4 * 2], Dither2Lookup = new byte[256 * 4 * 4 * 2], NoDitherLookup = new byte[256 * 4 * 2];
    static Raster3D()
    {
        for (int val = 0; val < 256 * 4 * 4 * 2; val++)
        {
            int color = val & 0xff, g = (val >> 8) & 1, x = (val >> 9) & 3, y = (val >> 11) & 3;
            Dither4Lookup[val] = g == 0 ? DitherRb(color, Dither4x4[y * 4 + x]) : DitherG(color, Dither4x4[y * 4 + x]);
            Dither2Lookup[val] = g == 0 ? DitherRb(color, Dither2x2[y * 4 + x]) : DitherG(color, Dither2x2[y * 4 + x]);
        }
        for (int val = 0; val < 256 * 4 * 2; val++) { int color = val & 0xff, g = (val >> 8) & 1; NoDitherLookup[val] = (byte)(g == 0 ? color >> 3 : color >> 2); }
    }
    private static byte DitherRb(int v, int d) => (byte)(((v << 1) - (v >> 4) + (v >> 7) + d) >> 4);
    private static byte DitherG(int v, int d) => (byte)(((v << 2) - (v >> 4) + (v >> 6) + d) >> 4);

    // ---------------- depth / alpha compare (the 8 GL-style functions) ----------------
    public static bool Compare(int func, int src, int dst) => func switch
    {
        0 => false, 1 => src < dst, 2 => src == dst, 3 => src <= dst, 4 => src > dst, 5 => src != dst, 6 => src >= dst, _ => true,
    };

    // ---------------- LOD helpers ----------------
    private static readonly byte[] Log2Table =
    {
          0,   2,   5,   8,  11,  14,  16,  19,  22,  25,  27,  30,  33,  35,  38,  40,
         43,  46,  48,  51,  53,  56,  58,  61,  63,  65,  68,  70,  73,  75,  77,  80,
         82,  84,  87,  89,  91,  93,  96,  98, 100, 102, 104, 106, 109, 111, 113, 115,
        117, 119, 121, 123, 125, 127, 129, 132, 134, 136, 138, 140, 141, 143, 145, 147,
        149, 151, 153, 155, 157, 159, 161, 162, 164, 166, 168, 170, 172, 173, 175, 177,
        179, 181, 182, 184, 186, 188, 189, 191, 193, 194, 196, 198, 200, 201, 203, 205,
        206, 208, 209, 211, 213, 214, 216, 218, 219, 221, 222, 224, 225, 227, 229, 230,
        232, 233, 235, 236, 238, 239, 241, 242, 244, 245, 247, 248, 250, 251, 253, 254,
    };
    /// <summary>log2 of a double as 24.8 fixed point (value assumed converted from a fixed-point int with <paramref name="fracbits"/> fraction bits).</summary>
    public static int FastLog2(double value, int fracbits = 0)
    {
        if (value < 0) return 0;
        ulong i = (ulong)BitConverter.DoubleToInt64Bits(value);
        uint ival = (uint)(i >> 45);
        int exp = (int)(ival >> 7) - 1023 - fracbits;
        return exp << 8 | Log2Table[ival & 127];
    }
    public static int LodBase(long dsdx, long dsdy, long dtdx, long dtdy)
    {
        double fdsdx = dsdx, fdsdy = dsdy, fdtdx = dtdx, fdtdy = dtdy;
        double texdx = fdsdx * fdsdx + fdtdx * fdtdx, texdy = fdsdy * fdsdy + fdtdy * fdtdy;
        return FastLog2(Math.Max(texdx, texdy), 64) / 2;
    }
    public static int CountLeadingZeros64(ulong v) => BitOperations.LeadingZeroCount(v);
    public static int CountLeadingZeros32(uint v) => BitOperations.LeadingZeroCount(v);
}
