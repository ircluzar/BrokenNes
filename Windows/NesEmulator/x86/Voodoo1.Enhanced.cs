// 3dfx Voodoo Graphics, the Enhanced card (card plan step 4, user 2026-11-22: "think of it like a core enhancement like the other PPUs
// in brokennes"): every triangle and fastfill is drawn a second time at an integer scale (2x/3x/4x) into a presentation-only shadow
// framebuffer (colour buffers + depth), and the monitor shows the shadow. The guest-visible framebuffer, the registers and the counters are
// exactly the Stock card's: the shadow pass runs first and leaves no trace, so guest readback, savestates and JIT parity are unchanged.
// The shadow is never saved: a loaded state (or a framebuffer layout change) rebuilds it from the native framebuffer (nearest blow-up),
// and the next frame the game draws is sharp again. LFB writes (2D overlays, movies) are mirrored as nearest blow-ups.
// Written for BrokenNes.

using System;

namespace NesEmulator.X86;

public sealed partial class Voodoo1
{
    /// <summary>Render scale of the presentation shadow (1 = Stock card, no shadow).</summary>
    public int Scale { get; private set; } = 1;
    private byte[]? _sfb;
    private int _sMask;

    /// <summary>The largest picture the Enhanced card hands the monitor (800x600 at 4x).</summary>
    public const int MaxShadowWidth = 3200, MaxShadowHeight = 2400;

    private void InitShadow(int scale)
    {
        if (scale is < 2 or > 4) throw new ArgumentException("the Voodoo render scale is 2, 3 or 4");
        Scale = scale;
        int need = FbBytes * scale * scale, size = 1;
        while (size < need) size <<= 1;
        _sfb = new byte[size];
        _sMask = size - 1;
    }

    private int ShadowLimit => FbBytes * Scale * Scale;

    private ushort NativeFb16(int byteOff) { byteOff &= FbMask & ~1; return (ushort)(Fb[byteOff] | Fb[byteOff + 1] << 8); }

    private void ShadowPut(int off, int v) { off &= _sMask & ~1; _sfb![off] = (byte)v; _sfb[off + 1] = (byte)(v >> 8); }

    /// <summary>Copies one native pixel (buffer at <paramref name="baseN"/>, byte offset <paramref name="o"/> inside it) into its N x N shadow block.</summary>
    private void MirrorPixel(int baseN, int o)
    {
        if (_sfb == null || baseN < 0 || o < 0 || baseN + o + 1 >= FbBytes) return;
        int n = Scale, p = o >> 1, y = p / _rowPixels, x = p % _rowPixels, rp = _rowPixels * n, lim = ShadowLimit;
        int v = NativeFb16(baseN + o), b = baseN * n * n;
        for (int j = 0; j < n; j++)
        {
            int row = b + ((y * n + j) * rp + x * n) * 2;
            for (int i = 0; i < n; i++) { int so = row + i * 2; if (so + 1 < lim) ShadowPut(so, v); }
        }
    }

    /// <summary>Rebuilds the whole shadow from the native framebuffer (after a state load or a layout change).</summary>
    private void ShadowFromNative()
    {
        if (_sfb == null) return;
        Array.Clear(_sfb);
        foreach (int b in new[] { _rgbOffs0, _rgbOffs1, _rgbOffs2, _auxOffs })
        {
            if (b < 0) continue;
            int pixels = Math.Min((FbBytes - b) / 2, 1024 * _rowPixels);
            for (int p = 0; p < pixels; p++) MirrorPixel(b, p * 2);
        }
    }

    private void RenderShadowInto(Vga vga)
    {
        int n = Scale;
        int w = Math.Max(8, Width) * n, h = Math.Max(8, Height) * n;
        vga.Ensure(w, h, MaxShadowWidth, MaxShadowHeight);
        w = vga.Width; h = vga.Height;
        if ((Reg[RFbiInit1] & (1 << 12)) != 0) { Array.Fill(vga.Frame, unchecked((int)0xff000000)); return; }
        if (_clutDirty) RebuildPens();
        int b = BufferOffset(_front) * n * n, rp = _rowPixels * n;
        var f = vga.Frame; var s = _sfb!;
        for (int y = 0; y < h; y++)
        {
            int row = b + y * rp * 2;
            for (int x = 0; x < w; x++)
            {
                int o = (row + x * 2) & _sMask & ~1;
                f[y * w + x] = _pen[s[o] | s[o + 1] << 8];
            }
        }
    }

    private void FastfillShadow()
    {
        uint fbz = Reg[RFbzMode];
        if ((fbz & (3 << 9)) == 0) return;
        int destN = DrawBuffer((int)(fbz >> 14) & 3);
        if (destN < 0) return;
        int n = Scale, nn = n * n, lim = ShadowLimit, rp = _rowPixels * n;
        int dest = destN * nn, aux = _auxOffs >= 0 ? _auxOffs * nn : -1;
        int x0 = (int)((Reg[RClipLeftRight] >> 16) & 0x3ff) * n, x1 = (int)(Reg[RClipLeftRight] & 0x3ff) * n;
        int y0 = (int)((Reg[RClipLowYHighY] >> 16) & 0x3ff) * n, y1 = (int)(Reg[RClipLowYHighY] & 0x3ff) * n;
        uint c1 = Reg[RColor1];
        Span<int> dpat = stackalloc int[16];
        for (int y = 0; y < 4; y++) { var d = new Dither(y, fbz); for (int x = 0; x < 4; x++) dpat[y * 4 + x] = d.Pixel(x, (int)(c1 >> 16) & 0xff, (int)(c1 >> 8) & 0xff, (int)c1 & 0xff); }
        int z = (ushort)Reg[RZaColor];
        int yo = YOrigin * n + n - 1;
        for (int y = y0; y < y1; y++)
        {
            int scry = (fbz & (1 << 17)) != 0 ? yo - y : y;
            if (scry < 0) continue;
            for (int x = x0; x < x1; x++)
            {
                int o = (scry * rp + x) * 2;
                if ((fbz & (1 << 9)) != 0 && dest + o + 1 < lim) ShadowPut(dest + o, dpat[(y & 3) * 4 + (x & 3)]);
                if ((fbz & (1 << 10)) != 0 && aux >= 0 && aux + o + 1 < lim) ShadowPut(aux + o, z);
            }
        }
    }

    /// <summary>The triangle in the registers, drawn into the shadow at Scale x: vertices x N, gradients / N, clip x N. Leaves every
    /// guest-visible register, iterator and counter as it found them.</summary>
    private void TriangleShadow()
    {
        int n = Scale, nn = n * n, lim = ShadowLimit;
        uint fbz = Reg[RFbzMode];
        int destN = DrawBuffer((int)(fbz >> 14) & 3);
        if (destN < 0) return;
        int dest = destN * nn, depthBase = _auxOffs >= 0 ? _auxOffs * nn : -1, rp = _rowPixels * n;
        int ax = (short)Reg[RVertexAx] * n, ay = (short)Reg[RVertexAy] * n;   // 1/16 shadow pixels
        long startr = Sx24(Reg[RStartR]), startg = Sx24(Reg[RStartG]), startb = Sx24(Reg[RStartB]), starta = Sx24(Reg[RStartA]), startz = (int)Reg[RStartZ];
        long startw = _startW;
        int drdx = Sx24(Reg[RdRdX]), dgdx = Sx24(Reg[RdGdX]), dbdx = Sx24(Reg[RdBdX]), dadx = Sx24(Reg[RdAdX]), dzdx = (int)Reg[RdZdX];
        int drdy = Sx24(Reg[RdRdY]), dgdy = Sx24(Reg[RdGdY]), dbdy = Sx24(Reg[RdBdY]), dady = Sx24(Reg[RdAdY]), dzdy = (int)Reg[RdZdY];
        long dwdx = _dwdx, dwdy = _dwdy;
        double s0 = _tS, t0 = _tT, w0 = _tW;
        uint cp = Reg[RFbzColorPath];
        if ((cp & (1 << 26)) != 0)
        {
            // subpixel correction to the first shadow pixel centre (in native gradient units: offsets / 16 / N)
            long dx = 8 - (ax & 15), dy = 8 - (ay & 15), q = 16L * n;
            startr = (int)(startr + (dy * drdy + dx * drdx) / q) << 8 >> 8;
            startg = (int)(startg + (dy * dgdy + dx * dgdx) / q) << 8 >> 8;
            startb = (int)(startb + (dy * dbdy + dx * dbdx) / q) << 8 >> 8;
            starta = (int)(starta + (dy * dady + dx * dadx) / q) << 8 >> 8;
            startz = (int)(startz + (dy * dzdy + dx * dzdx) / q);
            startw += (dy * dwdy + dx * dwdx) / q;
            w0 += (dy * (double)_tdWdY + dx * (double)_tdWdX) / q;
            s0 += (dy * (double)_tdSdY + dx * (double)_tdSdX) / q;
            t0 += (dy * (double)_tdTdY + dx * (double)_tdTdX) / q;
        }
        bool texOn = (Reg[RFbiInit3] & 0x40) == 0 && (TReg[RTLod] & 0x3f) <= 32;
        bool sendConfig = ((TReg[RTrexInit1] >> 18) & 1) != 0;
        uint tmode = TReg[RTextureMode];
        if (texOn) PrepareTexture();
        double ds0dx = _tdSdX, dt0dx = _tdTdX, dw0dx = _tdWdX, ds0dy = _tdSdY, dt0dy = _tdTdY, dw0dy = _tdWdY;
        uint color0 = Reg[RColor0], color1 = Reg[RColor1], key = Reg[RChromaKey], fogcolor = Reg[RFogColor], za = Reg[RZaColor];
        uint stipple = Reg[RStipple];
        uint am = Reg[RAlphaMode], fog = Reg[RFogMode];
        int alpharef = (int)(am >> 24);
        int clipL = (int)((Reg[RClipLeftRight] >> 16) & 0x3ff) * n, clipR = (int)(Reg[RClipLeftRight] & 0x3ff) * n;
        int clipT = (int)((Reg[RClipLowYHighY] >> 16) & 0x3ff) * n, clipB = (int)(Reg[RClipLowYHighY] & 0x3ff) * n;
        // the shadow's texel footprint is 1/N of a native pixel's: the LOD drops by log2(N) (sharper mip levels, as on a real high-res card)
        int lodbase0 = texOn ? Raster3D.LodBase(_tdSdX / n, _tdSdY / n, _tdTdX / n, _tdTdY / n) : 0;
        int yo = YOrigin * n + n - 1;
        uint pin = Reg[RFbiPixelsIn], pout = Reg[RFbiPixelsOut];

        var va = new Raster3D.Vertex(ax * (1.0f / 16.0f), ay * (1.0f / 16.0f));
        var vb = new Raster3D.Vertex((short)Reg[RVertexBx] * n * (1.0f / 16.0f), (short)Reg[RVertexBy] * n * (1.0f / 16.0f));
        var vc = new Raster3D.Vertex((short)Reg[RVertexCx] * n * (1.0f / 16.0f), (short)Reg[RVertexCy] * n * (1.0f / 16.0f));

        _tgt = _sfb!; _tgtMask = _sMask;
        try
        {
            Raster3D.Triangle(va, vb, vc, (y, startx, stopx) =>
            {
                int scry = (fbz & (1 << 17)) != 0 ? yo - y : y;
                if ((fbz & 1) != 0)
                {
                    if (scry < clipT || scry >= clipB) return;
                    if (startx >= clipR) return;
                    if (stopx > clipR) stopx = clipR;
                    if (startx < clipL) startx = clipL;
                }
                if (scry < 0) return;
                int rowOff = scry * rp * 2;
                long dx = startx - (ax >> 4), dy = y - (ay >> 4);
                var it = new Rgba((int)(starta + (dy * dady + dx * dadx) / n) << 8, (int)(startr + (dy * drdy + dx * drdx) / n) << 8,
                                  (int)(startg + (dy * dgdy + dx * dgdx) / n) << 8, (int)(startb + (dy * dbdy + dx * dbdx) / n) << 8);
                var itd = new Rgba((dadx << 8) / n, (drdx << 8) / n, (dgdx << 8) / n, (dbdx << 8) / n);
                int iterz = (int)(startz + (dy * dzdy + dx * dzdx) / n), dz = dzdx / n;
                long iterw = (startw << 16) + ((dy * dwdy + dx * dwdx) << 16) / n, iterwd = (dwdx << 16) / n;
                double iters0 = 0, itert0 = 0, iterw0 = 0, ds = ds0dx / n, dt = dt0dx / n, dw = dw0dx / n;
                if (texOn)
                {
                    iters0 = s0 + (dy * ds0dy + dx * ds0dx) / n;
                    itert0 = t0 + (dy * dt0dy + dx * dt0dx) / n;
                    iterw0 = w0 + (dy * dw0dy + dx * dw0dx) / n;
                }
                var dither = new Dither(scry, fbz);
                for (int x = startx; x < stopx; x++)
                {
                    do
                    {
                        if ((fbz & 4) != 0 && !Stipple(fbz, x / n, scry / n, ref stipple)) break;
                        int o = rowOff + x * 2;
                        if (x < 0 || o < 0) break;
                        int destOff = dest + o + 1 < lim ? dest + o : -1;
                        int depthOff = depthBase >= 0 && depthBase + o + 1 < lim ? depthBase + o : -1;
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
                    iterz += dz;
                    iterw += iterwd;
                    if (texOn) { iters0 += ds; itert0 += dt; iterw0 += dw; }
                }
            });
        }
        finally { _tgt = Fb; _tgtMask = FbMask; Reg[RFbiPixelsIn] = pin; Reg[RFbiPixelsOut] = pout; }
    }
}
