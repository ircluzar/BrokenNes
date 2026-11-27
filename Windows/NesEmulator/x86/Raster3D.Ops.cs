// Chip-neutral pixel-pipeline operations (added by the ViRGE rung beside the Voodoo's Raster3D.cs), shared by the emulated 3D cards (the S3 ViRGE today; the Voodoo and a GeForce later).
// Written for BrokenNes. Everything here is exact integer arithmetic: the same inputs give the same pixels on every host, so savestates,
// JIT parity and golden hashes stay valid. A chip decides WHAT to draw (its own register formats, edge walk and fixed-point steps) and calls
// these for HOW a texel, a depth test, a blend or a framebuffer pixel works. A host-GPU back end can later replace a chip's span loop as a
// whole (IRaster3DBackend), keeping this software path as the reference it is checked against.

using System;

namespace NesEmulator.X86;

/// <summary>The seam for a later host-GPU renderer: a chip hands its decoded triangle to a back end; the software one draws it at once.</summary>
public interface IRaster3DBackend
{
    string Name { get; }
    /// <summary>Called before the CPU reads or the display shows video memory the back end may still be drawing into.</summary>
    void Flush();
}

public static partial class Raster3D
{
    public static int Clamp8(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    // ---------------- texel formats (16-bit and 32-bit, as 3D chips of the era store them) ----------------

    public static Rgba Argb1555(int v) => new((v & 0x8000) != 0 ? 255 : 0, ((v >> 7) & 0xf8) | ((v >> 12) & 7), ((v >> 2) & 0xf8) | ((v >> 7) & 7), ((v << 3) & 0xf8) | ((v >> 2) & 7));
    public static Rgba Argb4444(int v) => new(((v >> 12) & 15) * 17, ((v >> 8) & 15) * 17, ((v >> 4) & 15) * 17, (v & 15) * 17);
    public static Rgba Argb8888(uint v) => new((int)(v >> 24), (int)(v >> 16) & 255, (int)(v >> 8) & 255, (int)v & 255);
    public static Rgba Rgb565(int v) => new(255, ((v >> 8) & 0xf8) | ((v >> 13) & 7), ((v >> 3) & 0xfc) | ((v >> 9) & 3), ((v << 3) & 0xf8) | ((v >> 2) & 7));

    /// <summary>Bilinear weights from 8-bit fractions: the four texels (u0v0, u1v0, u0v1, u1v1) summed and divided by 65536.</summary>
    public static Rgba Bilinear(in Rgba t00, in Rgba t10, in Rgba t01, in Rgba t11, int fu, int fv)
    {
        int w0 = (256 - fu) * (256 - fv), w1 = fu * (256 - fv), w2 = (256 - fu) * fv, w3 = fu * fv;
        return new((t00.A * w0 + t10.A * w1 + t01.A * w2 + t11.A * w3) >> 16, (t00.R * w0 + t10.R * w1 + t01.R * w2 + t11.R * w3) >> 16, (t00.G * w0 + t10.G * w1 + t01.G * w2 + t11.G * w3) >> 16, (t00.B * w0 + t10.B * w1 + t01.B * w2 + t11.B * w3) >> 16);
    }

    // ---------------- depth ----------------

    /// <summary>The 8 depth functions in the common order: 0 never, 1 greater, 2 equal, 3 greater-or-equal, 4 less, 5 not-equal, 6 less-or-equal, 7 always.
    /// (A chip whose bit encoding differs maps to this order.)</summary>
    public static bool DepthPass(int func, uint src, uint dst) => func switch
    {
        0 => false, 1 => src > dst, 2 => src == dst, 3 => src >= dst, 4 => src < dst, 5 => src != dst, 6 => src <= dst, _ => true,
    };

    // ---------------- combining ----------------

    /// <summary>src * a + dst * (255 - a), divided by 255 (the "source alpha" blend most fixed-function chips start with).</summary>
    public static int Lerp255(int src, int dst, int a) => (src * a + dst * (255 - a)) / 255;
    public static void AlphaBlend(ref Rgba c, in Rgba dst, int a) { c.R = Lerp255(c.R, dst.R, a); c.G = Lerp255(c.G, dst.G, a); c.B = Lerp255(c.B, dst.B, a); }
    public static void Modulate(ref Rgba t, in Rgba c) { t.R = t.R * c.R >> 8; t.G = t.G * c.G >> 8; t.B = t.B * c.B >> 8; }

    // ---------------- framebuffer pixels ----------------

    public static int Pack555(in Rgba c) => (c.B >> 3) | (c.G >> 3) << 5 | (c.R >> 3) << 10;
    public static int Pack565(in Rgba c) => (c.B >> 3) | (c.G >> 2) << 5 | (c.R >> 3) << 11;
    public static int Pack888(in Rgba c) => c.B | c.G << 8 | c.R << 16;
    public static Rgba Unpack555(int v) => new(255, ((v >> 7) & 0xf8) | ((v >> 12) & 7), ((v >> 2) & 0xf8) | ((v >> 7) & 7), ((v << 3) & 0xf8) | ((v >> 2) & 7));

    // ---------------- 2D: the ternary raster operation (Windows ROP3: index = P<<2 | S<<1 | D) ----------------

    public static uint Rop3(int rop, uint p, uint s, uint d)
    {
        switch (rop)   // the common ones straight away
        {
            case 0x00: return 0;
            case 0xcc: return s;
            case 0xf0: return p;
            case 0xaa: return d;
            case 0xff: return 0xffffffff;
            case 0x66: return s ^ d;
            case 0x5a: return p ^ d;
            case 0x55: return ~d;
            case 0x88: return s & d;
            case 0xee: return s | d;
            case 0x33: return ~s;
            case 0xb8: return (p & ~s) | (d & s);   // PSDPxax
            case 0xe2: return (d & ~s) | (p & s);   // DSPDxax
        }
        uint r = 0;
        for (int i = 0; i < 8; i++)
        {
            if (((rop >> i) & 1) == 0) continue;
            uint m = ((i & 4) != 0 ? p : ~p) & ((i & 2) != 0 ? s : ~s) & ((i & 1) != 0 ? d : ~d);
            r |= m;
        }
        return r;
    }
}
