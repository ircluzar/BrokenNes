// [cer64plus] Converts the VI's current framebuffer (RDRAM, host little-endian 32-bit words) into 0xAARRGGBB pixels.
using cor64.RCP;

namespace cor64.Session
{
    public static class FrameCapture
    {
        // [cer64plus] COR64_SPROF_CAPVEC (default on): 16bpp rows converted 8 pixels at a time with AVX2 (same values).
        private static readonly bool s_CapVec = System.Environment.GetEnvironmentVariable("COR64_SPROF_CAPVEC") != "0" && System.Runtime.Intrinsics.X86.Avx2.IsSupported;
        // measurement only: COR64_SPROF_CAPDUP=1 converts every frame twice (ceiling test)
        private static readonly bool s_CapClamp = System.Environment.GetEnvironmentVariable("COR64_SCRIPTS_SPEED_CAPCLAMP") != "0";
        private static readonly bool s_CapDup = System.Environment.GetEnvironmentVariable("COR64_SPROF_CAPDUP") == "1";
        private static readonly bool s_StrideFix = System.Environment.GetEnvironmentVariable("COR64_VI_STRIDEFIX") != "0";
        /// <summary>Fills <paramref name="dst"/> (resized when too small) with the visible picture. False when the VI shows nothing.</summary>
        public static unsafe bool Capture(Video vi, ref uint[] dst, out int width, out int height)
        {
            width = vi.Width; height = vi.Height;
            int mode = vi.ControlReg.GetPixelMode();
            if (width < 1 || height < 1 || width > 1024 || height > 768 ||
                (mode != VideoControlReg.PIXELMODE_16BPP && mode != VideoControlReg.PIXELMODE_32BPP)) {
                width = 320; height = 240;
                if (dst == null || dst.Length < width * height) dst = new uint[width * height];
                System.Array.Fill(dst, 0xFF000000u, 0, width * height);
                return false;
            }
            // [vi_scanout] VI_WIDTH is the framebuffer's row length in pixels and is what the VI steps by per line; the
            // visible span ((H_END-H_START)*X_SCALE) can be a few pixels wider (re2: 443 vs 436, 324 vs 320, 267 vs 264).
            // The old code then used the visible width as the stride, which sheared the picture. Now the stride is always
            // VI_WIDTH and the picture is clipped to it (COR64_VI_STRIDEFIX=0: old behaviour). Unchanged when width <= VI_WIDTH.
            int stride = (int)vi.WidthReg;
            if (s_StrideFix && stride >= 1 && stride <= 2048) { if (width > stride) width = stride; }
            else if (stride < width || stride > 2048) stride = width;
            if (dst == null || dst.Length < width * height) dst = new uint[width * height];
            byte* src = (byte*)vi.FramebufferPtr;
            // [scripts_speed] COR64_SCRIPTS_SPEED_CAPCLAMP (default on): rows past the end of RDRAM (8 MB) are shown black
            // instead of read (Pokemon Stadium 2 sets an origin near the top of RDRAM: the read crashed the process with an
            // AccessViolationException). Display only; emulation is unchanged.
            int rows = height;
            if (s_CapClamp) {
                long room = 0x800000L - vi.ReadFramebufferAddressSafe();
                long rowBytes = (long)stride * (mode == VideoControlReg.PIXELMODE_32BPP ? 4 : 2), last = (long)width * (mode == VideoControlReg.PIXELMODE_32BPP ? 4 : 2);
                if (room < last) rows = 0;
                else if ((rows - 1) * rowBytes + last > room) rows = (int)((room - last) / rowBytes) + 1;
                if (rows < height) System.Array.Fill(dst, 0xFF000000u, rows * width, (height - rows) * width);
            }
            fixed (uint* outp = dst) {
                if (mode == VideoControlReg.PIXELMODE_32BPP) {
                    for (int y = 0; y < rows; y++) {
                        uint* s = (uint*)(src + y * stride * 4); uint* d = outp + y * width;
                        for (int x = 0; x < width; x++) d[x] = 0xFF000000u | (s[x] >> 8); // RGBA word -> ARGB
                    }
                }
                else {
                    for (int rep = s_CapDup ? 2 : 1; rep > 0; rep--)
                    for (int y = 0; y < rows; y++) {
                        uint* d = outp + y * width; int rowBase = y * stride * 2;
                        int x = 0;
                        if (s_CapVec && (rowBase & 3) == 0) x = Conv16Row((uint*)(src + rowBase), d, width);
                        for (; x < width; x++) {
                            int bo = (rowBase + x * 2) ^ 2;
                            int v = src[bo] | (src[bo + 1] << 8);
                            uint r = (uint)((v >> 11) & 31), g = (uint)((v >> 6) & 31), b = (uint)((v >> 1) & 31);
                            d[x] = 0xFF000000u | ((r << 3 | r >> 2) << 16) | ((g << 3 | g >> 2) << 8) | (b << 3 | b >> 2);
                        }
                    }
                }
            }
            // [sf64_rdp] VI gamma (VI_CONTROL bit 3): the VI maps every channel through a square-root curve,
            // out = 2*floor(sqrt(in*64)) (same values as parallel-rdp's undithered table). It was not emulated, so games
            // that leave gamma on (Star Fox 64: ctrl 0x300E) looked dark. Gamma dither (bit 2): see below (it only
            // adds +-1 noise). Display only. COR64_VI_GAMMA=0 restores the old (no gamma) output.
            if (s_Gamma && (vi.ControlReg.RegisterValue & 8) != 0) {
                fixed (uint* outp = dst) {
                    // [gamma_rejudge] Gamma dither (VI_CONTROL bit 2, COR64_VI_GAMMA_DITHER, default on): with gamma on, the VI
                    // adds 6 bits of per-pixel noise below each channel before the square root: out = 2*isqrt((c<<6)+d)
                    // (matches parallel-rdp's 256*64 dithered gamma table, MIT). Hardware noise is random; here it is a fixed
                    // hash of (x, y) so captures stay deterministic. Gamma-off partial dither (+0/1) is not modelled.
                    if (s_GammaDither && (vi.ControlReg.RegisterValue & 4) != 0) {
                        for (int y = 0; y < rows; y++) {
                            uint* d = outp + y * width;
                            for (int x = 0; x < width; x++) {
                                uint h = ((uint)x * 0x9E3779B1u) ^ ((uint)y * 0x85EBCA77u); h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12;
                                uint c = d[x];
                                d[x] = 0xFF000000u | (GammaD((c >> 16) & 0xFF, h & 63) << 16) | (GammaD((c >> 8) & 0xFF, (h >> 6) & 63) << 8) | GammaD(c & 0xFF, (h >> 12) & 63);
                            }
                        }
                    }
                    else {
                        int n = width * rows;
                        for (int i = 0; i < n; i++) {
                            uint c = outp[i];
                            outp[i] = 0xFF000000u | ((uint)s_GammaLut[(c >> 16) & 0xFF] << 16) | ((uint)s_GammaLut[(c >> 8) & 0xFF] << 8) | s_GammaLut[c & 0xFF];
                        }
                    }
                }
            }
            return true;
        }

        private static readonly bool s_Gamma = System.Environment.GetEnvironmentVariable("COR64_VI_GAMMA") != "0";
        private static readonly byte[] s_GammaLut = BuildGamma();
        private static readonly bool s_GammaDither = System.Environment.GetEnvironmentVariable("COR64_VI_GAMMA_DITHER") != "0";
        private static readonly byte[] s_GammaDLut = BuildGammaD();
        private static uint GammaD(uint c, uint d) => s_GammaDLut[(c << 6) | d];
        private static byte[] BuildGammaD() {
            var t = new byte[256 * 64];
            for (int i = 0; i < t.Length; i++) { int r = (int)System.Math.Sqrt(i); while ((r + 1) * (r + 1) <= i) r++; while (r * r > i) r--; t[i] = (byte)(r * 2); }
            return t;
        }
        private static byte[] BuildGamma() {
            var t = new byte[256];
            for (int i = 0; i < 256; i++) { int r = (int)System.Math.Sqrt(i * 64); while ((r + 1) * (r + 1) <= i * 64) r++; while (r * r > i * 64) r--; t[i] = (byte)(r * 2); }
            return t;
        }

        // Each 32-bit host word of RDRAM holds two big-endian-ordered pixels: pixel x (even) in the high half, x+1 in
        // the low half (the scalar path reads byte offset (2x)^2). Returns the number of pixels done (a multiple of 8).
        private static unsafe int Conv16Row(uint* s, uint* d, int width)
        {
            int x = 0;
            var m5 = System.Runtime.Intrinsics.Vector256.Create(31u); var a = System.Runtime.Intrinsics.Vector256.Create(0xFF000000u);
            for (; x + 8 <= width; x += 8) {
                var w = System.Runtime.Intrinsics.X86.Sse2.LoadVector128(s + (x >> 1));
                w = System.Runtime.Intrinsics.X86.Sse2.Or(System.Runtime.Intrinsics.X86.Sse2.ShiftLeftLogical(w, 16), System.Runtime.Intrinsics.X86.Sse2.ShiftRightLogical(w, 16));
                var v = System.Runtime.Intrinsics.Vector256.AsUInt32(System.Runtime.Intrinsics.X86.Avx2.ConvertToVector256Int32(System.Runtime.Intrinsics.Vector128.AsUInt16(w)));
                var r = (v >> 11) & m5; var g = (v >> 6) & m5; var b = (v >> 1) & m5;
                r = (r << 3) | (r >> 2); g = (g << 3) | (g >> 2); b = (b << 3) | (b >> 2);
                System.Runtime.Intrinsics.X86.Avx.Store(d + x, a | (r << 16) | (g << 8) | b);
            }
            return x;
        }
    }
}
