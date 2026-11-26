// GeForce FX (NV30-NV34) Rankine pipeline: vertex fetch, the vertex program (VP) interpreter, primitive assembly, the rasterizer and the
// fragment program (FP) interpreter, all synchronous and in float. Instruction encodings: Mesa nouveau nv30 (nv30_vertprog.h, nvfx_shader.h,
// MIT, read as reference only: geforce\dl\mesa); state registers: envytools rnndb graph/nv30-40_3d.xml.
using System;
using System.Collections.Generic;
using System.Linq;

namespace NesEmulator.X86;

public sealed partial class NvGeForce
{
    // ---------------- vertex input ----------------
    private readonly float[] _attrCur = InitAttrs();                 // VTX_ATTR_4F: the current value of each of the 16 attributes
    private readonly List<uint> _inline = new();                      // VERTEX_DATA words of the open BEGIN_END
    private readonly List<int> _elems = new();                        // vertex indices / array vertices of the open BEGIN_END (-1 = none)
    private int _rPrim;
    public long RankineTris, RankinePixels;                           // diagnostics

    private static float[] InitAttrs() { var a = new float[64]; for (int i = 0; i < 16; i++) a[i * 4 + 3] = 1; if (Environment.GetEnvironmentVariable("X86_NVCOLWHITE") == "1") { a[12] = a[13] = a[14] = 1; } return a; }

    private static float F32(uint u) => BitConverter.Int32BitsToSingle((int)u);

    /// <summary>Methods of the vertex / draw path (called from Rankine()). True when handled.</summary>
    private bool RankineDrawMethod(uint inst, int mthd, uint data)
    {
        switch (mthd)
        {
            case 0x1808:
                if (data != 0) { _rPrim = (int)data; _inline.Clear(); _elems.Clear(); }
                else { RankineFlush(inst); _rPrim = 0; }
                return true;
            case 0x1818: _inline.Add(data); return true;
            case 0x39c:                                                     // (undocumented, seen in the 45.23 D3D path between inline vertices:
                if (_rPrim != 0)                                            //  repeat the vertex n back from the last one; 0 = the last)
                {
                    int per = 0;
                    for (int i = 0; i < 16; i++) { uint f = G(inst, 0x1740 + i * 4); per += AttrWords((int)(f & 15), (int)(f >> 4) & 15); }
                    int src = _inline.Count - per * ((int)data + 1);
                    if (per > 0 && src >= 0 && _inline.Count % per == 0) for (int k = 0; k < per; k++) _inline.Add(_inline[src + k]);
                }
                return true;
            case 0x1814:                                                    // VB_VERTEX_BATCH: start + (count - 1) << 24
            { int s = (int)(data & 0xffffff), n = (int)(data >> 24) + 1; for (int i = 0; i < n; i++) _elems.Add(s + i); return true; }
            case 0x180c: _elems.Add((int)(data & 0xffff)); _elems.Add((int)(data >> 16)); return true;
            case 0x1810: _elems.Add((int)data); return true;
            case 0x1824:                                                    // VB_INDEX_BATCH: indices from the index buffer
            {
                int s = (int)(data & 0xffffff), n = (int)(data >> 24) + 1;
                uint fmt = G(inst, 0x1820), off = G(inst, 0x181c);
                uint dma = G(inst, ((fmt & 1) != 0 ? 0x1a0 : 0x19c) | 0x8000);
                bool u16 = ((fmt >> 4) & 15) == 1;
                for (int i = 0; i < n; i++)
                {
                    uint a = off + (uint)(s + i) * (u16 ? 2u : 4u);
                    uint w = DmaRead32Any(dma, a & ~3u);
                    _elems.Add(u16 ? (int)((w >> (int)((a & 2) * 8)) & 0xffff) : (int)w);
                }
                return true;
            }
        }
        if (mthd is >= 0x1c00 and < 0x1d00) { _attrCur[(mthd - 0x1c00) >> 2] = F32(data); return true; }   // VTX_ATTR_4F[16]
        return false;
    }

    private static int AttrWords(int type, int size) => size == 0 ? 0 : type switch { 2 => size, 0 or 4 or 7 => 1, _ => (size + 1) / 2 };

    private static void DecodeAttr(int type, int size, Func<int, uint> word, float[] dst, int o)
    {
        dst[o] = 0; dst[o + 1] = 0; dst[o + 2] = 0; dst[o + 3] = 1;
        switch (type)
        {
            case 2: for (int c = 0; c < size && c < 4; c++) dst[o + c] = F32(word(c)); break;
            case 0:                                                           // B8G8R8A8 UNORM: x = R (byte 2), y = G, z = B, w = A
            { uint w = word(0); dst[o] = ((w >> 16) & 0xff) / 255f; dst[o + 1] = ((w >> 8) & 0xff) / 255f; dst[o + 2] = (w & 0xff) / 255f; dst[o + 3] = (w >> 24) / 255f; break; }
            case 4: case 7:
            { uint w = word(0); for (int c = 0; c < size && c < 4; c++) { float b = (w >> (8 * c)) & 0xff; dst[o + c] = type == 4 ? b / 255f : b; } break; }
            default:                                                          // 16-bit: SNORM (1), half float (3), SSCALED (5)
                for (int c = 0; c < size && c < 4; c++)
                {
                    uint w = word(c >> 1); short s = (short)(w >> (16 * (c & 1)));
                    dst[o + c] = type == 1 ? Math.Max(-1f, s / 32767f) : type == 3 ? (float)BitConverter.Int16BitsToHalf(s) : s;
                }
                break;
        }
    }

    /// <summary>The attributes (16 x vec4) of each vertex of the open BEGIN_END.</summary>
    private List<float[]> RankineVertices(uint inst)
    {
        var res = new List<float[]>();
        var fmt = new (int type, int size, int stride)[16];
        int perVtx = 0;
        for (int i = 0; i < 16; i++)
        {
            uint f = G(inst, 0x1740 + i * 4);
            fmt[i] = ((int)(f & 15), (int)(f >> 4) & 15, (int)(f >> 8) & 0xff);
            perVtx += AttrWords(fmt[i].type, fmt[i].size);
        }
        if (_inline.Count > 0 && perVtx > 0)
        {
            for (int b = 0; b + perVtx <= _inline.Count && res.Count < 1 << 16; b += perVtx)
            {
                var v = (float[])_attrCur.Clone(); int o = b;
                for (int i = 0; i < 16; i++)
                {
                    int n = AttrWords(fmt[i].type, fmt[i].size); if (n == 0) continue;
                    int bo = o; DecodeAttr(fmt[i].type, fmt[i].size, k => _inline[bo + k], v, i * 4); o += n;
                }
                res.Add(v);
            }
            return res;
        }
        foreach (int e in _elems)
        {
            var v = (float[])_attrCur.Clone();
            for (int i = 0; i < 16; i++)
            {
                if (fmt[i].size == 0) continue;
                uint vb = G(inst, 0x1680 + i * 4);
                uint dma = G(inst, ((vb & 0x80000000) != 0 ? 0x1a0 : 0x19c) | 0x8000);
                uint a = (vb & 0x0fffffff) + (uint)(e * fmt[i].stride);
                uint aa = a;
                DecodeAttr(fmt[i].type, fmt[i].size, k => DmaReadU32(dma, aa + (uint)k * 4), v, i * 4);
            }
            res.Add(v);
            if (res.Count >= 1 << 16) break;
        }
        return res;
    }

    private uint DmaReadU32(uint dma, uint a)
    {
        if ((a & 3) == 0) return DmaRead32Any(dma, a);
        ulong lo = DmaRead32Any(dma, a & ~3u), hi = DmaRead32Any(dma, (a & ~3u) + 4);
        return (uint)((hi << 32 | lo) >> (int)((a & 3) * 8));
    }

    // ---------------- the vertex program ----------------
    private readonly float[] _vt = new float[32 * 4];    // temps
    private readonly float[] _vs = new float[3 * 4];     // the three sources of an instruction
    private readonly float[] _vr = new float[4], _vsr = new float[4];
    private readonly float[] _vcc = new float[4];        // condition register
    private readonly int[] _va = new int[8];             // address registers A0, A1 (xyzw)

    private static readonly int[] VpMaskBit = { 8, 4, 2, 1 };   // VP write masks: x = 8 ... w = 1

    /// <summary>Runs the vertex program on one vertex: in = 16 x vec4 attributes, out = 16 x vec4 results (0 POS, 3 COL0, 4 COL1, 5 FOGC, 8+ TC).</summary>
    private void RunVp(uint inst, float[] vin, float[] vout)
    {
        Array.Clear(_vt); Array.Clear(_va); Array.Clear(_vcc);
        for (int i = 0; i < 64; i++) vout[i] = 0;
        vout[3 * 4 + 3] = vout[4 * 4 + 3] = 1;
        int pc = (int)G(inst, 0x1ea0);
        var stack = new Stack<int>();
        for (int steps = 0; steps < 4096 && pc is >= 0 and < 544; steps++)
        {
            int b = pc * 4;
            uint h0 = _vpInst[b], h1 = _vpInst[b + 1], h2 = _vpInst[b + 2], h3 = _vpInst[b + 3];
            int vop = (int)(h1 >> 23) & 31, sop = (int)((h0 & 1) << 4 | h1 >> 28);
            int input = (int)(h1 >> 9) & 15, cidx = (int)(h1 >> 14) & 0xff;
            int asel = (h0 & (1u << 24)) != 0 ? 1 : 0, aswz = (int)(h0 >> 1) & 3;
            if ((h3 & 2) != 0) cidx += _va[asel * 4 + aswz];
            uint s0 = (h1 & 0x1ff) << 6 | h2 >> 26, s1 = (h2 >> 11) & 0x7fff, s2 = (h2 & 0x7ff) << 4 | h3 >> 28;
            int inIdx = input + ((h0 & (1u << 27)) != 0 ? _va[asel * 4 + aswz] : 0);
            VpSrc(s0, (h0 & (1u << 21)) != 0, vin, inIdx, cidx, 0);
            VpSrc(s1, (h0 & (1u << 22)) != 0, vin, inIdx, cidx, 1);
            VpSrc(s2, (h0 & (1u << 23)) != 0, vin, inIdx, cidx, 2);
            bool pass = true;
            if ((h0 & (1u << 14)) != 0)
            {
                int cond = (int)(h0 >> 11) & 7; pass = false;
                int[] cs = { (int)(h0 >> 9) & 3, (int)(h0 >> 7) & 3, (int)(h0 >> 5) & 3, (int)(h0 >> 3) & 3 };
                for (int c = 0; c < 4; c++) if (CondTest(cond, _vcc[cs[c]])) pass = true;   // (per component for writes; any for branches)
            }
            bool last = (h3 & 1) != 0;
            // scalar flow control
            if (sop is 0x09 or 0x0b or 0x0c)
            {
                int target = (int)(h2 >> 2) & 0x1ff;
                if (pass)
                {
                    if (sop == 0x09) { pc = target; continue; }
                    if (sop == 0x0b) { stack.Push(pc + 1); pc = target; continue; }
                    if (stack.Count == 0) return; pc = stack.Pop(); continue;
                }
                if (last) return; pc++; continue;
            }
            // vector op
            float[] a = _vs; float r0 = 0, r1 = 0, r2 = 0, r3 = 0;
            bool vecW = vop != 0;
            switch (vop)
            {
                case 0x01: r0 = a[0]; r1 = a[1]; r2 = a[2]; r3 = a[3]; break;                                        // MOV
                case 0x02: r0 = a[0] * a[4]; r1 = a[1] * a[5]; r2 = a[2] * a[6]; r3 = a[3] * a[7]; break;            // MUL
                case 0x03: r0 = a[0] + a[8]; r1 = a[1] + a[9]; r2 = a[2] + a[10]; r3 = a[3] + a[11]; break;          // ADD (src0 + src2)
                case 0x04: r0 = a[0] * a[4] + a[8]; r1 = a[1] * a[5] + a[9]; r2 = a[2] * a[6] + a[10]; r3 = a[3] * a[7] + a[11]; break;   // MAD
                case 0x05: r0 = r1 = r2 = r3 = a[0] * a[4] + a[1] * a[5] + a[2] * a[6]; break;                       // DP3
                case 0x06: r0 = r1 = r2 = r3 = a[0] * a[4] + a[1] * a[5] + a[2] * a[6] + a[7]; break;                // DPH
                case 0x07: r0 = r1 = r2 = r3 = a[0] * a[4] + a[1] * a[5] + a[2] * a[6] + a[3] * a[7]; break;         // DP4
                case 0x08: r0 = 1; r1 = a[1] * a[5]; r2 = a[2]; r3 = a[7]; break;                                    // DST
                case 0x09: r0 = MathF.Min(a[0], a[4]); r1 = MathF.Min(a[1], a[5]); r2 = MathF.Min(a[2], a[6]); r3 = MathF.Min(a[3], a[7]); break;
                case 0x0a: r0 = MathF.Max(a[0], a[4]); r1 = MathF.Max(a[1], a[5]); r2 = MathF.Max(a[2], a[6]); r3 = MathF.Max(a[3], a[7]); break;
                case 0x0b: r0 = a[0] < a[4] ? 1 : 0; r1 = a[1] < a[5] ? 1 : 0; r2 = a[2] < a[6] ? 1 : 0; r3 = a[3] < a[7] ? 1 : 0; break;   // SLT
                case 0x0c: r0 = a[0] >= a[4] ? 1 : 0; r1 = a[1] >= a[5] ? 1 : 0; r2 = a[2] >= a[6] ? 1 : 0; r3 = a[3] >= a[7] ? 1 : 0; break; // SGE
                case 0x0d:                                                                                            // ARL: the address register
                    for (int c = 0; c < 4; c++) _va[asel * 4 + c] = (int)MathF.Floor(a[c]);
                    vecW = false; break;
                case 0x0e: r0 = a[0] - MathF.Floor(a[0]); r1 = a[1] - MathF.Floor(a[1]); r2 = a[2] - MathF.Floor(a[2]); r3 = a[3] - MathF.Floor(a[3]); break;   // FRC
                case 0x0f: r0 = MathF.Floor(a[0]); r1 = MathF.Floor(a[1]); r2 = MathF.Floor(a[2]); r3 = MathF.Floor(a[3]); break;   // FLR
                case 0x10: r0 = a[0] == a[4] ? 1 : 0; r1 = a[1] == a[5] ? 1 : 0; r2 = a[2] == a[6] ? 1 : 0; r3 = a[3] == a[7] ? 1 : 0; break;
                case 0x11: break;                                                                                     // SFL
                case 0x12: r0 = a[0] > a[4] ? 1 : 0; r1 = a[1] > a[5] ? 1 : 0; r2 = a[2] > a[6] ? 1 : 0; r3 = a[3] > a[7] ? 1 : 0; break;
                case 0x13: r0 = a[0] <= a[4] ? 1 : 0; r1 = a[1] <= a[5] ? 1 : 0; r2 = a[2] <= a[6] ? 1 : 0; r3 = a[3] <= a[7] ? 1 : 0; break;
                case 0x14: r0 = a[0] != a[4] ? 1 : 0; r1 = a[1] != a[5] ? 1 : 0; r2 = a[2] != a[6] ? 1 : 0; r3 = a[3] != a[7] ? 1 : 0; break;
                case 0x15: r0 = r1 = r2 = r3 = 1; break;                                                              // STR
                case 0x16: r0 = MathF.Sign(a[0]); r1 = MathF.Sign(a[1]); r2 = MathF.Sign(a[2]); r3 = MathF.Sign(a[3]); break;   // SSG
                default: vecW = false; break;
            }
            _vr[0] = r0; _vr[1] = r1; _vr[2] = r2; _vr[3] = r3;
            // scalar op (on src2.x of its swizzle)
            float x = a[8]; float sr = 0; bool scaW = sop != 0;
            switch (sop)
            {
                case 0x01: sr = x; break;                                                                         // (MOV: the whole swizzled src2, below)
                case 0x02: sr = 1 / x; break;
                case 0x03: sr = Math.Clamp(1 / x, -1.884467e19f, 1.884467e19f); if (MathF.Abs(sr) < 5.42101e-20f) sr = MathF.CopySign(5.42101e-20f, sr); break;
                case 0x04: sr = 1 / MathF.Sqrt(MathF.Abs(x)); break;
                case 0x05: case 0x0e: sr = MathF.Pow(2, x); break;
                case 0x06: case 0x0d: sr = MathF.Log2(MathF.Abs(x)); break;
                case 0x07:                                                                                             // LIT: a whole vector
                {
                    float d = a[8], s = a[9], p = Math.Clamp(a[11], -128f, 128f);
                    _vsr[0] = 1; _vsr[1] = MathF.Max(0, d); _vsr[2] = d > 0 ? MathF.Pow(MathF.Max(0, s), p) : 0; _vsr[3] = 1;
                    break;
                }
                case 0x0f: sr = MathF.Sin(x); break;
                case 0x10: sr = MathF.Cos(x); break;
                default: scaW = false; break;
            }
            if (sop != 0x07) { _vsr[0] = _vsr[1] = _vsr[2] = _vsr[3] = sr; }
            if (sop == 0x01) { _vsr[0] = a[8]; _vsr[1] = a[9]; _vsr[2] = a[10]; _vsr[3] = a[11]; }
            if (sop == 0x05) { _vsr[0] = MathF.Pow(2, MathF.Floor(x)); _vsr[1] = x - MathF.Floor(x); _vsr[2] = MathF.Pow(2, x); _vsr[3] = 1; }
            if (sop == 0x06) { float ax = MathF.Abs(x); float e = ax == 0 ? -float.MaxValue : MathF.Floor(MathF.Log2(ax)); _vsr[0] = e; _vsr[1] = ax == 0 ? 1 : ax / MathF.Pow(2, e); _vsr[2] = ax == 0 ? -float.MaxValue : MathF.Log2(ax); _vsr[3] = 1; }
            // writes
            int tid = (int)(h0 >> 16) & 0x1f, oid = (int)(h3 >> 2) & 0x1f;
            int vtm = (int)(h3 >> 20) & 15, vom = (int)(h3 >> 12) & 15, stm = (int)(h3 >> 24) & 15, som = (int)(h3 >> 16) & 15;
            // one output index for both units: when the scalar unit takes it, the vector result goes to the position (the 45.23
            // compiler pairs "dp4 oPos.c" with "mov oTn" this way); a scalar temp write without a temp index goes to R1. Both inferred from the 45.23 programs (3DMark03), not documented.
            int vod = scaW && som != 0 && vecW && vom != 0 ? 0 : oid;
            int stid = tid != 0x1f ? tid : 1;   // (no temp index: the 45.23 programs read the scalar result back from R1 - "rsq r1.w" patterns in GT1/GT2)
            for (int c = 0; c < 4; c++)
            {
                bool cp = (h0 & (1u << 14)) == 0 || CondTest((int)(h0 >> 11) & 7, _vcc[(int)(h0 >> (9 - 2 * c)) & 3]);
                if (!cp) continue;
                int bit = VpMaskBit[c];
                if (vecW)
                {
                    if ((vtm & bit) != 0 && tid != 0x1f) _vt[(tid & 31) * 4 + c] = _vr[c];
                    if ((vom & bit) != 0 && vod < 16) vout[vod * 4 + c] = _vr[c];
                    if ((h0 & (1u << 15)) != 0) _vcc[c] = _vr[c];
                }
                if (scaW)
                {
                    if ((stm & bit) != 0 && stid != 0x1f) _vt[(stid & 31) * 4 + c] = _vsr[c];
                    if ((som & bit) != 0 && oid < 16) vout[oid * 4 + c] = _vsr[c];
                    if ((h0 & (1u << 15)) != 0 && !vecW) _vcc[c] = _vsr[c];
                }
            }
            if (last) return;
            pc++;
        }
    }

    private void VpSrc(uint sr, bool abs, float[] vin, int input, int cidx, int pos)
    {
        int type = (int)(sr & 3), o = pos * 4;
        float[] src; int bo;
        switch (type)
        {
            case 1: src = _vt; bo = (int)((sr >> 2) & 15) * 4; break;
            case 2: src = vin; bo = (input & 15) * 4; break;
            case 3:
            {
                int ci = Math.Clamp(cidx, 0, 255);
                for (int c = 0; c < 4; c++) _cbuf[c] = F32(_vpConst[ci * 4 + c]);
                src = _cbuf; bo = 0; break;
            }
            default: src = vin; bo = 0; break;                                    // (type 0: v0 - the 45.23 programs read the position this way beside another input)
        }
        int sx = (int)(sr >> 12) & 3, sy = (int)(sr >> 10) & 3, sz = (int)(sr >> 8) & 3, sw = (int)(sr >> 6) & 3;
        float x = src[bo + sx], y = src[bo + sy], z = src[bo + sz], w = src[bo + sw];
        if (abs) { x = MathF.Abs(x); y = MathF.Abs(y); z = MathF.Abs(z); w = MathF.Abs(w); }
        if ((sr & (1u << 14)) != 0) { x = -x; y = -y; z = -z; w = -w; }
        _vs[o] = x; _vs[o + 1] = y; _vs[o + 2] = z; _vs[o + 3] = w;
    }
    private readonly float[] _cbuf = new float[4];

    private static bool CondTest(int cond, float v) => cond switch
    {
        0 => false, 1 => v < 0, 2 => v == 0, 3 => v <= 0, 4 => v > 0, 5 => v != 0, 6 => v >= 0, _ => true,
    };

    // ---------------- primitive assembly ----------------
    private static readonly bool RkwRhw = Environment.GetEnvironmentVariable("X86_RKW") == "rhw";
    /// <summary>The fixed-function transform (ENGINE without the VP bit): clip = PROJECTION x (MODELVIEW x v0), both row-major at 0x680 / 0x480;
    /// screen = clip / w x VIEWPORT_SCALE + VIEWPORT_TRANSLATE (the same screen-space result the driver's own programs produce);
    /// colours / fog pass through (no lighting yet); texture coordinates through TEX_MATRIX n (0x6C0 + 64 n) when TEX_MATRIX_ENABLE n.</summary>
    private void FixedFunction(uint inst, float[] vin, float[] vout)
    {
        for (int i = 0; i < 64; i++) vout[i] = 0;
        float x = vin[0], y = vin[1], z = vin[2], w = vin[3];
        float[] mv = RMat(inst, 0x480), pj = RMat(inst, 0x680);
        // the 45.23 driver loads the composite world-view-projection into PROJECTION (MODELVIEW is the lighting/eye matrix): clip = P x v
        // (DotProduct3 SDK sample: MV z' = 2 - z, P w = 2 - z; P x MV x v put the quad behind the eye). X86_NVFFMV=1: clip = P x MV x v
        float ex = x, ey = y, ez = z, ew = w;
        if (FfUseMv) { ex = mv[0] * x + mv[1] * y + mv[2] * z + mv[3] * w; ey = mv[4] * x + mv[5] * y + mv[6] * z + mv[7] * w;
              ez = mv[8] * x + mv[9] * y + mv[10] * z + mv[11] * w; ew = mv[12] * x + mv[13] * y + mv[14] * z + mv[15] * w; }
        float cx = pj[0] * ex + pj[1] * ey + pj[2] * ez + pj[3] * ew, cy = pj[4] * ex + pj[5] * ey + pj[6] * ez + pj[7] * ew,
              cz = pj[8] * ex + pj[9] * ey + pj[10] * ez + pj[11] * ew, cw = pj[12] * ex + pj[13] * ey + pj[14] * ez + pj[15] * ew;
        if (cw == 0) cw = 1e-6f;
        float iw = 1 / cw;
        vout[0] = cx * iw * F32(G(inst, 0xa30)) + F32(G(inst, 0xa20));
        vout[1] = cy * iw * F32(G(inst, 0xa34)) + F32(G(inst, 0xa24));
        vout[2] = cz * iw * F32(G(inst, 0xa38)) + F32(G(inst, 0xa28));
        vout[3] = cw;
        for (int c = 0; c < 4; c++) { vout[12 + c] = vin[12 + c]; vout[16 + c] = vin[16 + c]; vout[20 + c] = vin[20 + c]; }
        for (int t = 0; t < 8; t++)
        {
            int si = (8 + t) * 4;
            if (G(inst, 0x240 + t * 4) != 0)
            {
                float[] tm = RMat(inst, 0x6c0 + t * 64);
                for (int r = 0; r < 4; r++) vout[si + r] = tm[r * 4] * vin[si] + tm[r * 4 + 1] * vin[si + 1] + tm[r * 4 + 2] * vin[si + 2] + tm[r * 4 + 3] * vin[si + 3];
            }
            else for (int c = 0; c < 4; c++) vout[si + c] = vin[si + c];
        }
    }

    private float[] RMat(uint inst, int b)
    {
        var m = new float[16]; bool any = false;
        for (int i = 0; i < 16; i++) { uint u = G(inst, b + i * 4); m[FfTranspose ? (i & 3) * 4 + (i >> 2) : i] = F32(u); any |= u != 0; }
        if (!any) m[0] = m[5] = m[10] = m[15] = 1;
        return m;
    }

    private sealed class RVert { public float X, Y, Z, Iw; public readonly float[] Var = new float[16 * 4]; }   // screen x, y, depth, 1/w; VP outputs

    private static readonly bool ScreenVp = Environment.GetEnvironmentVariable("X86_NVSCREENVP") == "1";   // old behaviour: VP positions always screen space
    private static readonly bool DrawTrace = Environment.GetEnvironmentVariable("X86_NVDRAWTRACE") == "1";
    private long _dtLastPix;
    private static readonly int[] DtProbe = ParseProbe(Environment.GetEnvironmentVariable("X86_NVPROBE"));   // X86_NVPROBE=x,y (default 320,240)
    private static int[] ParseProbe(string? s) { try { var a = s!.Split(','); return new[] { int.Parse(a[0]), int.Parse(a[1]) }; } catch { return new[] { 320, 240 }; } }
    private static readonly bool FfTranspose = Environment.GetEnvironmentVariable("X86_NVFFT") == "1";
    private static readonly bool FfUseMv = Environment.GetEnvironmentVariable("X86_NVFFMV") == "1";
    private static readonly bool ProgDump = Environment.GetEnvironmentVariable("X86_NVPROGDUMP") == "1";
    private readonly HashSet<int> _seenVp = new();
    private readonly HashSet<uint> _seenFp = new();
    private readonly HashSet<string> _seenDraw = new();

    private void RankineFlush(uint inst)
    {
        if (_rPrim == 0) return;
        var vin = RankineVertices(inst);
        if (vin.Count == 0) return;
        float tx = F32(G(inst, 0xa20)), ty = F32(G(inst, 0xa24)), tz = F32(G(inst, 0xa28));
        float sx = F32(G(inst, 0xa30)), sy = F32(G(inst, 0xa34)), sz = F32(G(inst, 0xa38));
        var vs = new RVert[vin.Count];
        bool clipSpace = (G(inst, 0x1e94) & 0x102) == 2 && !ScreenVp;
        _clipZ = clipSpace; _clipZ0 = F32(G(inst, 0xa28));
        var vout = new float[64];
        for (int i = 0; i < vin.Count; i++)
        {
            if ((G(inst, 0x1e94) & 2) != 0) RunVp(inst, vin[i], vout); else FixedFunction(inst, vin[i], vout);
            var v = new RVert();
            Array.Copy(vout, v.Var, 64);
            // Kelvin-style: the program's position is already in screen space (x, y in pixels, z in depth units); w is the clip w,
            // used for perspective-correct varyings only (X86_RKW=rhw: read it as 1/w instead)
            float w = vout[3]; if (w == 0 || float.IsNaN(w)) w = 1;
            if (clipSpace)
            {   // ENGINE bit 8 clear: the program's position is in clip space; the hardware divides by w and applies VIEWPORT_SCALE/TRANSLATE
                float iw = 1 / w;
                v.X = vout[0] * iw * sx + tx; v.Y = vout[1] * iw * sy + ty; v.Z = vout[2] * iw * sz + tz; v.Iw = iw;
            }
            else { v.X = vout[0]; v.Y = vout[1]; v.Z = vout[2]; v.Iw = RkwRhw ? w : 1 / w; }
            vs[i] = v;
        }
        if (OpLog != null && ProgDump)
        {
            uint eng = G(inst, 0x1e94);
            int vps = (int)G(inst, 0x1ea0);
            if ((eng & 2) != 0 && _seenVp.Add(vps))
            {
                var sb = new System.Text.StringBuilder();
                for (int pc = vps; pc < 544; pc++) { for (int k = 0; k < 4; k++) sb.Append(_vpInst[pc * 4 + k].ToString("X8")).Append(' '); if ((_vpInst[pc * 4 + 3] & 1) != 0) break; }
                OpLog($"rankine vpdump {vps}: {sb}");
            }
            uint fpa = G(inst, 0x8e4);
            if (_seenFp.Add(fpa))
                OpLog($"rankine fpdump {fpa:X}: {string.Join(" ", Array.ConvertAll(_fp, f => $"{f.W0:X8} {f.W1:X8} {f.W2:X8} {f.W3:X8}" + (f.HasConst ? $" [{f.C0} {f.C1} {f.C2} {f.C3}]" : "")))}");
            if (_seenDraw.Add($"{eng:X}_{vps}_{fpa:X}_{G(inst, 0x8fc):X}_{G(inst, 0x904):X}"))
            {
                var i0 = vin[0]; var o0 = vs[0];
                var sb = new System.Text.StringBuilder();
                for (int ai = 0; ai < 16; ai++) { uint vf = G(inst, 0x1740 + ai * 4); if ((vf & 0xf0) != 0) sb.Append($" a{ai}=({i0[ai * 4]:G4},{i0[ai * 4 + 1]:G4},{i0[ai * 4 + 2]:G4},{i0[ai * 4 + 3]:G4})"); }
                sb.Append(" ->");
                for (int oi = 0; oi < 16; oi++) if (o0.Var[oi * 4] != 0 || o0.Var[oi * 4 + 1] != 0 || o0.Var[oi * 4 + 2] != 0) sb.Append($" o{oi}=({o0.Var[oi * 4]:G4},{o0.Var[oi * 4 + 1]:G4},{o0.Var[oi * 4 + 2]:G4},{o0.Var[oi * 4 + 3]:G4})");
                if ((eng & 2) == 0) OpLog($"rankine ffmat mv {string.Join(",", RMat(inst, 0x480).Select(v => v.ToString("G4")))} pj {string.Join(",", RMat(inst, 0x680).Select(v => v.ToString("G4")))} vp {F32(G(inst, 0xa30))},{F32(G(inst, 0xa34))},{F32(G(inst, 0xa38))} / {F32(G(inst, 0xa20))},{F32(G(inst, 0xa24))},{F32(G(inst, 0xa28))}");
                OpLog($"rankine draw eng {eng:X} rc {G(inst, 0x8fc):X}/{G(inst, 0x900):X}/{G(inst, 0x904):X}/{G(inst, 0x910):X}/{G(inst, 0x914):X} s1 {G(inst, 0x920):X}/{G(inst, 0x924):X}/{G(inst, 0x930):X}/{G(inst, 0x934):X} fin {G(inst, 0x8f4):X}/{G(inst, 0x8f8):X} fpc {G(inst, 0x1d60):X} vp {vps} fp {fpa:X} prim {_rPrim} n {vin.Count} zt {G(inst, 0xa74)} zf {G(inst, 0xa6c):X} blend {G(inst, 0x310)} {G(inst, 0x314):X}/{G(inst, 0x318):X} at {G(inst, 0x304)} cull {G(inst, 0x183c)} tex0 {RTexInfo(inst, 0)} tex1 {RTexInfo(inst, 1)} tex2 {RTexInfo(inst, 2)} cube2 {(G(inst, 0x1a44) & 4) != 0} tex3 {RTexInfo(inst, 3)}{sb}");
            }
        }
        if (RankinePrims < 4 && OpLog != null)
        {
            var v0 = vs[0];
            var i0 = vin[0];
            var sb2 = new System.Text.StringBuilder();
            for (int ai = 0; ai < 16; ai++) { uint vf = G(inst, 0x1740 + ai * 4); if ((vf & 0xf0) != 0) sb2.Append($" a{ai}[{vf:X} @{G(inst, 0x1680 + ai * 4):X}]=({i0[ai * 4]:G4},{i0[ai * 4 + 1]:G4},{i0[ai * 4 + 2]:G4},{i0[ai * 4 + 3]:G4})"); }
            OpLog($"rankine attrs{sb2} vpstart {G(inst, 0x1ea0)} vpinst@start {_vpInst[G(inst, 0x1ea0) * 4]:X8} {_vpInst[G(inst, 0x1ea0) * 4 + 1]:X8} {_vpInst[G(inst, 0x1ea0) * 4 + 2]:X8} {_vpInst[G(inst, 0x1ea0) * 4 + 3]:X8}");
            OpLog($"rankine in v0 ({i0[0]},{i0[1]},{i0[2]},{i0[3]}) v3 ({i0[12]:F2},{i0[13]:F2},{i0[14]:F2},{i0[15]:F2}) inline {_inline.Count} elems {_elems.Count} fmt0 {G(inst, 0x1740):X} c118 ({F32(_vpConst[472])},{F32(_vpConst[473])},{F32(_vpConst[474])},{F32(_vpConst[475])}) c119 ({F32(_vpConst[476])},{F32(_vpConst[477])},{F32(_vpConst[478])},{F32(_vpConst[479])})");
            OpLog($"rankine prim {_rPrim} verts {vs.Length} v0 scr ({v0.X:F1},{v0.Y:F1},{v0.Z:F1}) iw {v0.Iw:G3} col0 ({v0.Var[12]:F2},{v0.Var[13]:F2},{v0.Var[14]:F2},{v0.Var[15]:F2}) tc0 ({v0.Var[32]:F2},{v0.Var[33]:F2}) vp {G(inst, 0x1ea0)} vt {tx},{ty},{tz} vsc {sx},{sy},{sz}");
        }
        int n = vs.Length;
        switch (_rPrim)
        {
            case 5: for (int i = 0; i + 2 < n; i += 3) RTri(inst, vs[i], vs[i + 1], vs[i + 2]); break;                   // TRIANGLES
            case 6: for (int i = 0; i + 2 < n; i++) if ((i & 1) == 0) RTri(inst, vs[i], vs[i + 1], vs[i + 2]); else RTri(inst, vs[i + 1], vs[i], vs[i + 2]); break;
            case 7: case 10: for (int i = 1; i + 1 < n; i++) RTri(inst, vs[0], vs[i], vs[i + 1]); break;                // FAN, POLYGON
            case 8: for (int i = 0; i + 3 < n; i += 4) { RTri(inst, vs[i], vs[i + 1], vs[i + 2]); RTri(inst, vs[i], vs[i + 2], vs[i + 3]); } break;
            case 9: for (int i = 0; i + 3 < n; i += 2) { RTri(inst, vs[i], vs[i + 1], vs[i + 3]); RTri(inst, vs[i], vs[i + 3], vs[i + 2]); } break;
            case 1: foreach (var v in vs) RPoint(inst, v); break;                                                       // POINTS
            case 2: for (int i = 0; i + 1 < n; i += 2) RLine(inst, vs[i], vs[i + 1]); break;                           // LINES
            case 3: for (int i = 0; i < n; i++) RLine(inst, vs[i], vs[(i + 1) % n]); break;                             // LINE_LOOP
            case 4: for (int i = 0; i + 1 < n; i++) RLine(inst, vs[i], vs[i + 1]); break;                               // LINE_STRIP
            default: break;
        }
        if (DrawTrace && OpLog != null)
        {   // X86_NVDRAWTRACE=1: every draw in order, with the pixels it wrote and the colour at the screen centre after it
            uint cdma = G(inst, 0x194 | 0x8000);
            uint cb = (cdma != 0 ? DmaAddr(cdma, G(inst, 0x210)).addr : G(inst, 0x210)) & VramMask;
            int cp = (int)(G(inst, 0x20c) & 0xffff);
            uint probe = (uint)(cb + DtProbe[1] * cp + DtProbe[0] * 4) & VramMask;
            OpLog($"rankine dt eng {G(inst, 0x1e94):X} fp {G(inst, 0x8e4):X} rt {G(inst, 0x210):X} prim {_rPrim} n {vin.Count} pix {RankinePixels - _dtLastPix} blend {G(inst, 0x310)} {G(inst, 0x314):X}/{G(inst, 0x318):X} eq {G(inst, 0x320):X} zt {G(inst, 0xa74)} zf {G(inst, 0xa6c):X} st {G(inst, 0x328)} cmask {G(inst, 0x324):X} centre {BitConverter.ToUInt32(_v.Ram, (int)probe):X8}");
            _dtLastPix = RankinePixels;
        }
        if (RankinePrims < 12 && OpLog != null)
            OpLog($"rankine after prim {_rPrim}: tris {RankineTris} pixels {RankinePixels} rt {G(inst, 0x210):X} fmt {G(inst, 0x208):X} pitch {G(inst, 0x20c):X} hz {G(inst, 0x200):X} vt {G(inst, 0x204):X} scis {G(inst, 0x8c0):X}/{G(inst, 0x8c4):X} zt {G(inst, 0xa74)} zf {G(inst, 0xa6c):X} cull {G(inst, 0x183c)}/{G(inst, 0x1830):X} fp {_fp.Length} ops {string.Join(",", Array.ConvertAll(_fp, f => ((f.W0 >> 24) & 0x3f).ToString("X")))} blend {G(inst, 0x310)} cmask {G(inst, 0x324):X} pcrtc {PcrtcStart:X} | tex0 {RTexInfo(inst, 0)}");
    }

    // ---------------- the rasterizer + fragment program ----------------
    private struct FpInsn { public uint W0, W1, W2, W3; public float C0, C1, C2, C3; public bool HasConst; }
    private FpInsn[] _fp = Array.Empty<FpInsn>();

    private void LoadFp(uint inst)
    {
        uint p = G(inst, 0x8e4);
        uint dma = G(inst, ((p & 2) != 0 ? 0x188 : 0x184) | 0x8000);
        uint off = p & ~3u;
        var list = new List<FpInsn>();
        static uint Sw(uint w) => w;   // (the NVIDIA driver uploads native-order words; Mesa halfword-swaps its own uploads)
        for (int i = 0; i < 512; i++)
        {
            var f = new FpInsn { W0 = Sw(DmaRead32Any(dma, off)), W1 = Sw(DmaRead32Any(dma, off + 4)), W2 = Sw(DmaRead32Any(dma, off + 8)), W3 = Sw(DmaRead32Any(dma, off + 12)) };
            off += 16;
            bool usesConst = ((f.W1 & 3) == 2) || ((f.W2 & 3) == 2) || ((f.W3 & 3) == 2);
            if (usesConst)
            {
                f.HasConst = true;
                f.C0 = F32(Sw(DmaRead32Any(dma, off))); f.C1 = F32(Sw(DmaRead32Any(dma, off + 4))); f.C2 = F32(Sw(DmaRead32Any(dma, off + 8))); f.C3 = F32(Sw(DmaRead32Any(dma, off + 12)));
                off += 16;
            }
            list.Add(f);
            if ((f.W0 & 1) != 0) break;
        }
        _fp = list.ToArray();
        // programs that hand the combiners packed values (pack opcodes into R0/R1: the PS 1.x path) are not modelled: their FP output is
        // used directly; the others feed H0-H3 to the combiners
        _fpPacks = Array.Exists(_fp, f => ((f.W0 >> 24) & 0x3f) is 0x13 or 0x24 or 0x27 or 0x29 or 0x2e or 0x2f);
        _fpFetchOnly = _fpPacks && !Array.Exists(_fp, f => ((f.W0 >> 24) & 0x3f) is 0x13 or 0x24 or 0x27 or 0x29 or 0x2f)
                       && Array.FindAll(_fp, f => ((f.W0 >> 24) & 0x3f) == 0x2e && ((f.W0 >> 17) & 15) >= 4).Length > 0;   // (fetches from units 4+: GT4 terrain; GT3's stay on 0-3)
        if (RankinePrims < 3 && OpLog != null)
        {
            uint o2 = p & ~3u; var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 16; i++) sb.Append(DmaRead32Any(dma, o2 + (uint)i * 4).ToString("X8")).Append(' ');
            var (fv, fa) = DmaAddr(dma, o2);
            OpLog($"rankine fp at {p:X} dma {dma:X} -> {(fv ? "vram" : "sys")} {fa:X}: {sb}");
        }
    }

    private readonly float[] _fr = new float[64 * 4];     // R0-R63 (FP32) - the half registers H0-H127 share them (H2n, H2n+1 = R n's lower / upper half: approximated as R)
    private readonly float[] _fh = new float[64 * 4];
    private readonly float[] _fin = new float[12 * 4];    // inputs: WPOS, COL0, COL1, FOGC, TC0-7
    private readonly float[] _fs = new float[12];
    private readonly float[] _fcc = new float[4];
    private static readonly bool PackNative = Environment.GetEnvironmentVariable("X86_NVPKNATIVE") == "1";
    private static readonly bool ZEqLoose = Environment.GetEnvironmentVariable("X86_NVZEQ") == "1";
    private static readonly bool PackRaw = Environment.GetEnvironmentVariable("X86_NVPKRAW") == "1";
    private static readonly string? PkModeEnv = Environment.GetEnvironmentVariable("X86_NVPKMODE");
    // routing of the pack/fetch results into the combiners: mode 2 (fetch -> texture n of its unit field) for programs made only of
    // 0x2E fetches (GT4 terrain), the slot map (X86_NVPKMAP) for programs with raw packs / projective fetches (GT2's PS 1.x lighting);
    // X86_NVPKMODE=1 / 2 forces one
    private bool PkMode2 => PkModeEnv == "2" || (PkModeEnv != "1" && _fpFetchOnly);
    private bool _fpFetchOnly;
    private readonly float[] _pkReg = new float[16 * 4]; private int _pkSet;
    private readonly System.Text.StringBuilder _rcTrace = new();
    private bool _fpHalfOut, _fpPacks; private int _pkDiag;
    private readonly float[] _pk = new float[8 * 4];   // packed slots R0.xyzw, R1.xyzw (unpacked vec4 each)
    private static readonly HashSet<int> PackUnsigned = new((Environment.GetEnvironmentVariable("X86_NVPKUNS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => Convert.ToInt32(x, 16)));
    private static readonly int[] PkMap = (Environment.GetEnvironmentVariable("X86_NVPKMAP") ?? "1,3,2,-1,0,4,13,-1").Split(',').Select(int.Parse).ToArray();

    private bool RunFp(uint inst, int px, int py)
    {
        Array.Clear(_fcc); _fpHalfOut = false;
        Array.Clear(_fr, 0, 16); Array.Clear(_fh, 0, 32); if (_fpPacks) { Array.Clear(_pk); _pkSet = 0; }
        _fr[0] = _fr[1] = _fr[2] = 0; _fr[3] = 1; _fh[0] = _fh[1] = _fh[2] = 0; _fh[3] = 1;
        for (int k = 0; k < _fp.Length; k++)
        {
            ref var f = ref _fp[k];
            int op = (int)(f.W0 >> 24) & 0x3f;
            int input = (int)(f.W0 >> 13) & 15;
            FpSrc(f.W1, (f.W1 & (1u << 29)) != 0, input, f, 0);
            FpSrc(f.W2, (f.W2 & (1u << 18)) != 0, input, f, 1);
            FpSrc(f.W3, (f.W3 & (1u << 18)) != 0, input, f, 2);
            float[] a = _fs; float r0 = 0, r1 = 0, r2 = 0, r3 = 0;
            switch (op)
            {
                case 0x00: break;
                case 0x01: r0 = a[0]; r1 = a[1]; r2 = a[2]; r3 = a[3]; break;
                case 0x02: r0 = a[0] * a[4]; r1 = a[1] * a[5]; r2 = a[2] * a[6]; r3 = a[3] * a[7]; break;
                case 0x03: r0 = a[0] + a[4]; r1 = a[1] + a[5]; r2 = a[2] + a[6]; r3 = a[3] + a[7]; break;
                case 0x04: r0 = a[0] * a[4] + a[8]; r1 = a[1] * a[5] + a[9]; r2 = a[2] * a[6] + a[10]; r3 = a[3] * a[7] + a[11]; break;
                case 0x05: r0 = r1 = r2 = r3 = a[0] * a[4] + a[1] * a[5] + a[2] * a[6]; break;
                case 0x06: r0 = r1 = r2 = r3 = a[0] * a[4] + a[1] * a[5] + a[2] * a[6] + a[3] * a[7]; break;
                case 0x07: r0 = 1; r1 = a[1] * a[5]; r2 = a[2]; r3 = a[7]; break;
                case 0x08: r0 = MathF.Min(a[0], a[4]); r1 = MathF.Min(a[1], a[5]); r2 = MathF.Min(a[2], a[6]); r3 = MathF.Min(a[3], a[7]); break;
                case 0x09: r0 = MathF.Max(a[0], a[4]); r1 = MathF.Max(a[1], a[5]); r2 = MathF.Max(a[2], a[6]); r3 = MathF.Max(a[3], a[7]); break;
                case 0x0a: r0 = a[0] < a[4] ? 1 : 0; r1 = a[1] < a[5] ? 1 : 0; r2 = a[2] < a[6] ? 1 : 0; r3 = a[3] < a[7] ? 1 : 0; break;
                case 0x0b: r0 = a[0] >= a[4] ? 1 : 0; r1 = a[1] >= a[5] ? 1 : 0; r2 = a[2] >= a[6] ? 1 : 0; r3 = a[3] >= a[7] ? 1 : 0; break;
                case 0x0c: r0 = a[0] <= a[4] ? 1 : 0; r1 = a[1] <= a[5] ? 1 : 0; r2 = a[2] <= a[6] ? 1 : 0; r3 = a[3] <= a[7] ? 1 : 0; break;
                case 0x0d: r0 = a[0] > a[4] ? 1 : 0; r1 = a[1] > a[5] ? 1 : 0; r2 = a[2] > a[6] ? 1 : 0; r3 = a[3] > a[7] ? 1 : 0; break;
                case 0x0e: r0 = a[0] != a[4] ? 1 : 0; r1 = a[1] != a[5] ? 1 : 0; r2 = a[2] != a[6] ? 1 : 0; r3 = a[3] != a[7] ? 1 : 0; break;
                case 0x0f: r0 = a[0] == a[4] ? 1 : 0; r1 = a[1] == a[5] ? 1 : 0; r2 = a[2] == a[6] ? 1 : 0; r3 = a[3] == a[7] ? 1 : 0; break;
                case 0x10: r0 = a[0] - MathF.Floor(a[0]); r1 = a[1] - MathF.Floor(a[1]); r2 = a[2] - MathF.Floor(a[2]); r3 = a[3] - MathF.Floor(a[3]); break;
                case 0x11: r0 = MathF.Floor(a[0]); r1 = MathF.Floor(a[1]); r2 = MathF.Floor(a[2]); r3 = MathF.Floor(a[3]); break;
                case 0x12:                                                                                           // KIL (when the condition passes)
                {
                    int cond = (int)(f.W1 >> 18) & 7;
                    bool kill = cond == 7;
                    if (!kill) for (int c = 0; c < 4; c++) if (CondTest(cond, _fcc[(int)(f.W1 >> (21 + 2 * c)) & 3])) kill = true;
                    if (kill) return false;
                    continue;
                }
                case 0x17: case 0x18: case 0x19: case 0x31:                                                         // TEX, TXP, TXD, TXB
                {
                    float u = a[0], v = a[1];
                    if (op == 0x18 && a[3] != 0) { u /= a[3]; v /= a[3]; }
                    float rr = a[2]; if (op == 0x18 && a[3] != 0) rr /= a[3];
                    int tu = (int)(f.W0 >> 17) & 15;
                    uint c = RTex(inst, tu, u, v, rr, op == 0x31 ? a[3] : 0);
                    if (_rtex[tu].Fmt == 0x28) { r0 = (sbyte)(c & 0xff) / 127f; r1 = (sbyte)((c >> 8) & 0xff) / 127f; r2 = 0; r3 = 1; break; }   // DSDT8: signed du, dv
                    r0 = ((c >> 16) & 0xff) / 255f; r1 = ((c >> 8) & 0xff) / 255f; r2 = (c & 0xff) / 255f; r3 = (c >> 24) / 255f;
                    break;
                }
                case 0x33:                                                                                           // (the 45.23 FF BUMPENVMAP): sample the unit at
                {                                                                                                    // src0.xy + 2x2 matrix (src2 = m00 m01 m10 m11) x src1.xy
                    float du = a[4], dv = a[5];
                    float bu = a[0] + a[8] * du + a[10] * dv, bv = a[1] + a[9] * du + a[11] * dv;
                    uint c = RTex(inst, (int)(f.W0 >> 17) & 15, bu, bv, 0, 0);
                    r0 = ((c >> 16) & 0xff) / 255f; r1 = ((c >> 8) & 0xff) / 255f; r2 = (c & 0xff) / 255f; r3 = (c >> 24) / 255f;
                    break;
                }
                case 0x1a: r0 = r1 = r2 = r3 = 1 / a[0]; break;
                case 0x1b: r0 = r1 = r2 = r3 = 1 / MathF.Sqrt(MathF.Abs(a[0])); break;
                case 0x1c: r0 = r1 = r2 = r3 = MathF.Pow(2, a[0]); break;
                case 0x1d: r0 = r1 = r2 = r3 = MathF.Log2(a[0]); break;
                case 0x1e: { float d = a[0], s = a[1], p = Math.Clamp(a[3], -128f, 128f); r0 = 1; r1 = MathF.Max(0, d); r2 = d > 0 ? MathF.Pow(MathF.Max(0, s), p) : 0; r3 = 1; break; }
                case 0x1f: r0 = a[0] * a[4] + (1 - a[0]) * a[8]; r1 = a[1] * a[5] + (1 - a[1]) * a[9]; r2 = a[2] * a[6] + (1 - a[2]) * a[10]; r3 = a[3] * a[7] + (1 - a[3]) * a[11]; break;   // LRP
                case 0x20: r0 = r1 = r2 = r3 = 1; break;
                case 0x21: break;
                case 0x22: r0 = r1 = r2 = r3 = MathF.Cos(a[0]); break;
                case 0x23: r0 = r1 = r2 = r3 = MathF.Sin(a[0]); break;
                case 0x26: r0 = r1 = r2 = r3 = MathF.Pow(a[0], a[4]); break;
                case 0x2e: case 0x2f: case 0x27: case 0x13:                                                         // packs (PK4B-like): 4 x 8 bits into one
                {                                                                                                    // component; kept unpacked for the combiners
                    int pm = (int)(f.W0 >> 9) & 15, pd = (int)(f.W0 >> 1) & 63;
                    bool uns = op == 0x27 || (op != 0x13 && PackUnsigned.Contains(op));
                    float[] pv = a;
                    if ((op == 0x2e || op == 0x2f) && !PackRaw)
                    {   // texture fetch through unit (bits 17-20) - 0x2F projective - packed to 8 bits per channel for the combiners
                        float pu = a[0], pvv = a[1], pr = a[2];
                        if (op == 0x2f && a[3] != 0) { pu /= a[3]; pvv /= a[3]; pr /= a[3]; }
                        uint tc = RTex(inst, (int)(f.W0 >> 17) & 15, pu, pvv, pr, 0);
                        pv = new[] { ((tc >> 16) & 0xff) / 255f, ((tc >> 8) & 0xff) / 255f, (tc & 0xff) / 255f, (tc >> 24) / 255f };
                        uns = true;
                    }
                    for (int c = 0; c < 4; c++)
                        if ((pm & (1 << c)) != 0 && pd < 2)
                            for (int kk = 0; kk < 4; kk++) { float v = pv[kk]; _pk[(pd * 4 + c) * 4 + kk] = uns ? Math.Clamp(v, 0, 1) : Math.Clamp(v, -1, 1); }
                    if (PkMode2)
                    {   // mode 2: a fetch (0x2E/0x2F) lands in combiner register = its unit field (0-3 texture n, 4 primary, 5 secondary);
                        // a plain pack (0x27) lands by its slot (R0.y tex1, R0.z tex2, R0.w / R1.z tex3, R1.x primary, R1.y secondary)
                        int tgt;
                        if (op is 0x2e or 0x2f) { int un = (int)(f.W0 >> 17) & 15; tgt = un <= 3 ? 8 + un : un == 4 ? 4 : un == 5 ? 5 : -1; }
                        else
                        {
                            int c0 = pm switch { 1 => 0, 2 => 1, 4 => 2, 8 => 3, _ => 0 }, sl = pd * 4 + c0;
                            tgt = sl switch { 0 => 8, 1 => 9, 2 => 10, 3 => 11, 4 => 4, 5 => 5, 6 => 11, _ => 13 };
                        }
                        if (tgt >= 0) for (int kk = 0; kk < 4; kk++) { float v = pv[kk]; _pkReg[tgt * 4 + kk] = uns ? Math.Clamp(v, 0, 1) : Math.Clamp(v, -1, 1); }
                        if (tgt >= 0) _pkSet |= 1 << tgt;
                    }
                    if ((f.W0 & 1) != 0) goto fpEnd;
                    continue;
                }
                case 0x3a: r0 = a[0] / a[4]; r1 = a[1] / a[4]; r2 = a[2] / a[4]; r3 = a[3] / a[4]; break;           // DIV
                case 0x15: case 0x16: r0 = r1 = r2 = r3 = 0; break;                                                  // DDX / DDY (0: per pixel only)
                default: r0 = a[0]; r1 = a[1]; r2 = a[2]; r3 = a[3]; break;
            }
            int scale = (int)(f.W2 >> 28) & 7;
            if (scale != 0) { float m = scale switch { 1 => 2, 2 => 4, 3 => 8, 5 => 0.5f, 6 => 0.25f, 7 => 0.125f, _ => 1 }; r0 *= m; r1 *= m; r2 *= m; r3 *= m; }
            if ((f.W0 & 0x80000000) != 0) { r0 = Math.Clamp(r0, 0, 1); r1 = Math.Clamp(r1, 0, 1); r2 = Math.Clamp(r2, 0, 1); r3 = Math.Clamp(r3, 0, 1); }
            int prec = (int)(f.W0 >> 22) & 3;
            if (prec == 2) { r0 = Math.Clamp(r0, -2, 2); r1 = Math.Clamp(r1, -2, 2); r2 = Math.Clamp(r2, -2, 2); r3 = Math.Clamp(r3, -2, 2); }   // FX12
            int mask = (int)(f.W0 >> 9) & 15;
            bool half = (f.W0 & 0x80) != 0;
            int di = (int)(f.W0 >> 1) & 63;
            bool none = (f.W0 & (1u << 30)) != 0 && false;   // (NV40 only)
            float[] res = { r0, r1, r2, r3 };
            int cnd = (int)(f.W1 >> 18) & 7;
            for (int c = 0; c < 4; c++)
            {
                if ((mask & (1 << c)) == 0) continue;
                if (cnd != 7 && !CondTest(cnd, _fcc[(int)(f.W1 >> (21 + 2 * c)) & 3])) continue;
                if (!none)
                {   // (R n and H 2n share storage on the hardware: kept as equal values here so R0 / H0 outputs both reach the combiners)
                    if (half) { _fh[(di & 63) * 4 + c] = res[c]; if ((di & 1) == 0) _fr[(di >> 1) * 4 + c] = res[c]; }
                    else { _fr[(di & 63) * 4 + c] = res[c]; if (di < 32) _fh[di * 2 * 4 + c] = res[c]; }
                }
                if ((f.W0 & 0x100) != 0) _fcc[c] = res[c];
            }
            if (half && di == 0) _fpHalfOut = true;
            if (!half && di == 0) _fpHalfOut = false;
            if ((f.W0 & 1) != 0) break;
        }
        fpEnd:
        return true;
    }

    private void FpSrc(uint w, bool abs, int input, in FpInsn f, int pos)
    {
        int type = (int)(w & 3), o = pos * 4;
        float x, y, z, ww;
        float[] s; int b;
        if (type == 2) { _cbuf[0] = f.C0; _cbuf[1] = f.C1; _cbuf[2] = f.C2; _cbuf[3] = f.C3; s = _cbuf; b = 0; }
        else if (type == 1) { s = _fin; b = Math.Min(input, 11) * 4; }
        else { int idx = (int)(w >> 2) & 63; bool h = (w & 0x100) != 0; s = h ? _fh : _fr; b = idx * 4; }
        x = s[b + ((int)(w >> 9) & 3)]; y = s[b + ((int)(w >> 11) & 3)]; z = s[b + ((int)(w >> 13) & 3)]; ww = s[b + ((int)(w >> 15) & 3)];
        if (abs) { x = MathF.Abs(x); y = MathF.Abs(y); z = MathF.Abs(z); ww = MathF.Abs(ww); }
        if ((w & (1u << 17)) != 0) { x = -x; y = -y; z = -z; ww = -ww; }
        _fs[o] = x; _fs[o + 1] = y; _fs[o + 2] = z; _fs[o + 3] = ww;
    }

    /// <summary>A Rankine texture unit as the shared texel decoder's Tex (formats are the NV10 ones on the NV30-NV34).</summary>
    private Tex RTexUnit(uint inst, int u)
    {
        var t = new Tex();
        uint en = G(inst, 0x1a0c + u * 32);
        if ((en & 0x40000000) == 0) return t;
        uint f = G(inst, 0x1a04 + u * 32);
        t.Dma = G(inst, ((f & 2) != 0 ? 0x188 : 0x184) | 0x8000);
        t.Fmt = (int)(f >> 8) & 0xff;
        t.Rect = t.Fmt is >= 0x10 and <= 0x13 or 0x1b or 0x1d or 0x1e or 0x20;
        if (t.Fmt == 0x1b) t.Fmt = 0x13;
        if (t.Rect) { uint sz = G(inst, 0x1a18 + u * 32); t.W = (int)(sz >> 16); t.H = (int)(sz & 0xffff); t.Pitch = (int)(G(inst, 0x1a10 + u * 32) >> 16); }
        else { t.W = 1 << (int)((f >> 20) & 15); t.H = 1 << (int)((f >> 24) & 15); }
        uint wr = G(inst, 0x1a08 + u * 32);
        t.WrapS = (int)(wr & 0xff); t.WrapT = (int)(wr >> 8) & 15;
        t.Base = G(inst, 0x1a00 + u * 32);
        uint flt = G(inst, 0x1a14 + u * 32);
        t.MagF = (int)(flt >> 24) & 15; t.MinF = (int)(flt >> 16) & 15;
        t.Lin = t.MagF == 2;
        // TEX_FORMAT bits 16-19 = the mipmap level count (the 45.23 driver: 6 for a 32x32 chain); the levels are packed in memory whether
        // or not the min filter uses them (cube faces follow one another by the whole chain)
        int cnt = (int)(f >> 16) & 15;
        t.Levels = !t.Rect && cnt > 1 ? cnt : 1;
        t.Mip = t.Levels > 1 && t.MinF >= 3;
        t.Ok = t.W > 0 && t.H > 0 && t.Dma != 0;
        return t;
    }

    private readonly Tex[] _rtex = new Tex[16];

    private readonly bool[] _rcube = new bool[16];

    // diagnostics: X86_NVTEXDUMP=DIR writes each texture the rasterizer samples (once per base/format/size) as a BMP, through Texel()
    private static readonly string? TexDumpDir = Environment.GetEnvironmentVariable("X86_NVTEXDUMP");
    private readonly HashSet<string> _dumped = new();
    private void DumpTex(in Tex t)
    {
        string key = $"{t.Base:X}_{t.Fmt:X}_{t.W}x{t.H}";
        if (_dumped.Count > 64 || !_dumped.Add(key)) return;
        int w = Math.Min(t.W, 1024), h = Math.Min(t.H, 1024);
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            uint c = Texel(t, x, y); int o = ((h - 1 - y) * w + x) * 4;
            px[o] = (byte)c; px[o + 1] = (byte)(c >> 8); px[o + 2] = (byte)(c >> 16); px[o + 3] = 255;
        }
        using var f = System.IO.File.Create(System.IO.Path.Combine(TexDumpDir!, key + ".bmp"));
        using var bw = new System.IO.BinaryWriter(f);
        bw.Write((ushort)0x4d42); bw.Write(54 + px.Length); bw.Write(0); bw.Write(54);
        bw.Write(40); bw.Write(w); bw.Write(h); bw.Write((ushort)1); bw.Write((ushort)32); bw.Write(0); bw.Write(px.Length); bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
        bw.Write(px);
    }

    private string RTexInfo(uint inst, int u)
    {
        var t = RTexUnit(inst, u);
        if (!t.Ok) return $"off (en {G(inst, 0x1a0c + u * 32):X})";
        var (vr, ad) = DmaAddr(t.Dma, t.Base);
        return $"fmt {t.Fmt:X} {t.W}x{t.H} rect {t.Rect} base {t.Base:X} {(vr ? "vram" : "sys")} {ad:X} f {G(inst, 0x1a04 + u * 32):X} wrap {G(inst, 0x1a08 + u * 32):X} filt {G(inst, 0x1a14 + u * 32):X} swz {G(inst, 0x1a10 + u * 32):X} t00 {Texel(t, 0, 0):X8} tmid {Texel(t, t.W / 2, t.H / 2):X8}";
    }

    private uint RTex(uint inst, int unit, float u, float v, float r, float bias)
    {
        ref var t = ref _rtex[unit & 15];
        if (!t.Ok) return 0xff000000;
        if (!_rcube[unit & 15]) return SampleLevel(t, 0, u, v, t.Lin);
        // a cube map: the face from the major axis (+X, -X, +Y, -Y, +Z, -Z), each face a full (mip) chain after the previous one
        float ax = MathF.Abs(u), ay = MathF.Abs(v), az = MathF.Abs(r);
        int face; float sc, tc, ma;
        if (ax >= ay && ax >= az) { face = u >= 0 ? 0 : 1; ma = ax; sc = u >= 0 ? -r : r; tc = -v; }
        else if (ay >= az) { face = v >= 0 ? 2 : 3; ma = ay; sc = u; tc = v >= 0 ? r : -r; }
        else { face = r >= 0 ? 4 : 5; ma = az; sc = r >= 0 ? u : -u; tc = -v; }
        if (ma == 0) return 0xff000000;
        float fu = (sc / ma + 1) * 0.5f, fv = (tc / ma + 1) * 0.5f;
        uint faceBytes = 0; int w = t.W, h = t.H;
        for (int l = 0; l < t.Levels; l++) { faceBytes += LevelBytes(t.Fmt, w, h); w = Math.Max(1, w >> 1); h = Math.Max(1, h >> 1); }
        faceBytes = (faceBytes + CubeAlign - 1) & ~(CubeAlign - 1);   // (faces 256-byte aligned: GT1 32x32 X8R8G8B8 faces 0x1600 apart)
        var tf = t; tf.Base = t.Base + (uint)face * faceBytes; tf.WrapS = tf.WrapT = 3;
        return SampleLevel(tf, 0, fu, fv, tf.Lin);
    }

    private static RVert Moved(RVert v, float dx, float dy)
    {
        var r = new RVert { X = v.X + dx, Y = v.Y + dy, Z = v.Z, Iw = v.Iw };
        Array.Copy(v.Var, r.Var, 64);
        return r;
    }

    /// <summary>A point: a screen-aligned square of the point size (output 6 .x, or POINT_SIZE), texcoords 0..1 across it when point
    /// sprites are on (0x1EE8 POINT_SPRITE).</summary>
    private void RPoint(uint inst, RVert v)
    {
        float size = v.Var[6 * 4] > 0 ? v.Var[6 * 4] : F32(G(inst, 0x1ee0));
        if (!(size > 0)) size = 1;
        float h = Math.Clamp(size, 1, 256) * 0.5f;
        bool sprite = (G(inst, 0x1ee8) & 1) != 0;
        var q = new[] { Moved(v, -h, -h), Moved(v, h, -h), Moved(v, h, h), Moved(v, -h, h) };
        if (sprite)
        {
            float[] su = { 0, 1, 1, 0 }, sv = { 0, 0, 1, 1 };
            for (int k = 0; k < 4; k++) for (int t = 0; t < 8; t++) { q[k].Var[(8 + t) * 4] = su[k]; q[k].Var[(8 + t) * 4 + 1] = sv[k]; }
        }
        _noCullR = true;
        try { RTri(inst, q[0], q[1], q[2]); RTri(inst, q[0], q[2], q[3]); } finally { _noCullR = false; }
    }

    /// <summary>A line: a one-pixel-wide quad along it.</summary>
    private void RLine(uint inst, RVert a, RVert b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy); if (len == 0) return;
        float nx = -dy / len * 0.5f, ny = dx / len * 0.5f;
        _noCullR = true;
        try
        {
            var a0 = Moved(a, nx, ny); var a1 = Moved(a, -nx, -ny); var b0 = Moved(b, nx, ny); var b1 = Moved(b, -nx, -ny);
            RTri(inst, a0, b0, b1); RTri(inst, a0, b1, a1);
        }
        finally { _noCullR = false; }
    }
    private bool _noCullR;

    /// <summary>Near-plane clipping: a triangle with a vertex at w <= 0 is clipped against w >= eps in clip space (x*w, y*w, z*w, w; the
    /// screen positions are the projected ones), varyings linear in clip space, and the pieces drawn.</summary>
    private void RTri(uint inst, RVert a, RVert b, RVert c)
    {
        bool inW = a.Iw > 0 && b.Iw > 0 && c.Iw > 0;
        if (inW && (!_clipZ || (a.Z >= _clipZ0 && b.Z >= _clipZ0 && c.Z >= _clipZ0))) { RTriIn(inst, a, b, c); return; }
        // homogeneous clipping (screen x, y, z times w are linear in clip space): w >= eps, and for clip-space programs the D3D near
        // plane (depth >= the viewport's z translate); varyings linear in clip space
        const float eps = 1e-5f;
        static float W(RVert v) => v.Iw == 0 ? -1 : 1 / v.Iw;
        var poly = new List<RVert> { a, b, c };
        for (int plane = 0; plane < (_clipZ ? 2 : 1); plane++)
        {
            float D(RVert v) { float w = W(v); return plane == 0 ? w - eps : (v.Z - _clipZ0) * w; }
            var outp = new List<RVert>();
            for (int i = 0; i < poly.Count; i++)
            {
                RVert p = poly[i], q = poly[(i + 1) % poly.Count];
                float dp = D(p), dq = D(q);
                bool pin = dp >= 0, qin = dq >= 0;
                if (pin) outp.Add(p);
                if (pin != qin)
                {
                    float t = dp / (dp - dq);
                    float wp = W(p), wq = W(q);
                    var r = new RVert();
                    float cxp = p.X * wp, cyp = p.Y * wp, czp = p.Z * wp, cxq = q.X * wq, cyq = q.Y * wq, czq = q.Z * wq;
                    float w = wp + (wq - wp) * t;
                    if (w <= 0) w = eps;
                    r.X = (cxp + (cxq - cxp) * t) / w; r.Y = (cyp + (cyq - cyp) * t) / w; r.Z = (czp + (czq - czp) * t) / w; r.Iw = 1 / w;
                    for (int k = 0; k < 64; k++) r.Var[k] = p.Var[k] + (q.Var[k] - p.Var[k]) * t;
                    outp.Add(r);
                }
            }
            poly = outp;
            if (poly.Count < 3) return;
        }
        for (int i = 1; i + 1 < poly.Count; i++) RTriIn(inst, poly[0], poly[i], poly[i + 1]);
    }
    private bool _clipZ; private float _clipZ0; private int _zDiag;

    private void RTriIn(uint inst, RVert a, RVert b, RVert c)
    {
        float area = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
        if (area == 0 || float.IsNaN(area)) return;
        if (G(inst, 0x183c) != 0 && !_noCullR)
        {
            bool ccw = area < 0;                                       // (y grows downward on the screen)
            bool front = G(inst, 0x1834) == 0x900 ? !ccw : ccw;
            uint cf = G(inst, 0x1830);
            if (cf == 0x408 || (cf == 0x405 && !front) || (cf == 0x404 && front)) return;
        }
        RankineTris++;
        // the target
        uint rtf = G(inst, 0x208), pitches = G(inst, 0x20c);
        int cfmt = (int)(rtf & 0x1f), cbpp = cfmt == 3 ? 2 : cfmt == 9 ? 1 : 4;
        bool z16 = ((rtf >> 5) & 7) == 1;
        uint cdma = G(inst, 0x194 | 0x8000), zdma = G(inst, 0x198 | 0x8000);
        uint cbase = (cdma != 0 ? DmaAddr(cdma, G(inst, 0x210)).addr : G(inst, 0x210)) & VramMask;
        uint zbase = (zdma != 0 ? DmaAddr(zdma, G(inst, 0x214)).addr : G(inst, 0x214)) & VramMask;
        int cpitch = (int)(pitches & 0xffff), zpitch = (int)(pitches >> 16);
        uint hz = G(inst, 0x200), vt = G(inst, 0x204);
        int rx0 = (int)(hz & 0xffff), rw = (int)(hz >> 16), ry0 = (int)(vt & 0xffff), rh = (int)(vt >> 16);
        uint sh = G(inst, 0x8c0), sv = G(inst, 0x8c4);
        int cx0 = Math.Max(rx0, (int)(sh & 0xffff)), cx1 = Math.Min(rx0 + rw, (int)(sh & 0xffff) + (int)(sh >> 16));
        int cy0 = Math.Max(ry0, (int)(sv & 0xffff)), cy1 = Math.Min(ry0 + rh, (int)(sv & 0xffff) + (int)(sv >> 16));
        if (sh == 0 && sv == 0) { cx0 = rx0; cx1 = rx0 + rw; cy0 = ry0; cy1 = ry0 + rh; }
        int x0 = Math.Max(cx0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)))), x1 = Math.Min(cx1 - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        int y0 = Math.Max(cy0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)))), y1 = Math.Min(cy1 - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        if (x1 < x0 || y1 < y0 || (long)(x1 - x0 + 1) * (y1 - y0 + 1) > 4 << 20) return;
        // state
        bool zt = G(inst, 0xa74) != 0, zw = G(inst, 0xa70) != 0;
        uint zf = G(inst, 0xa6c);
        if (ZEqLoose && zf == 0x202) zf = 0x203;
        bool at = G(inst, 0x304) != 0; uint af = G(inst, 0x308); float aref = (G(inst, 0x30c) & 0xff) / 255f;
        bool blend = G(inst, 0x310) != 0;
        uint bs = G(inst, 0x314), bd = G(inst, 0x318), beq = G(inst, 0x320), bcol = G(inst, 0x31c);
        uint cmask = G(inst, 0x324);
        if (cmask == 0 && !_rankineMaskSeen) cmask = 0x01010101;
        uint keep = ((cmask & 0xff) == 0 ? 0xffu : 0) | ((cmask & 0xff00) == 0 ? 0xff00u : 0) | ((cmask & 0xff0000) == 0 ? 0xff0000u : 0) | ((cmask & 0xff000000) == 0 ? 0xff000000u : 0);
        float zmax = z16 ? 65535f : 16777215f;
        for (int u = 0; u < 16; u++) { _rtex[u] = RTexUnit(inst, u); _rcube[u] = (G(inst, 0x1a04 + u * 32) & 4) != 0; }
        if (TexDumpDir != null) for (int u = 0; u < 4; u++) if (_rtex[u].Ok) DumpTex(_rtex[u]);
        // stencil (Z24S8 only: the low byte of the zeta word); two-sided when STENCIL_ENABLE(1) is set
        bool frontFace = (area < 0) == (G(inst, 0x1834) != 0x900);
        int sb = (!frontFace && G(inst, 0x348) != 0) ? 0x348 : 0x328;
        bool st = !z16 && zpitch > 0 && G(inst, sb) != 0 && !NoStencil;
        uint sWm = G(inst, sb + 4) & 0xff, sFn = G(inst, sb + 8), sRef = G(inst, sb + 12) & 0xff, sMsk = G(inst, sb + 16) & 0xff;
        uint sOpF = G(inst, sb + 20), sOpZf = G(inst, sb + 24), sOpZp = G(inst, sb + 28);
        bool useRc = RcOn ? (G(inst, 0x1e94) & 1) == 0 : !RcOffNoFp && (G(inst, 0x1e94) & 3) == 2;   // VP on, FP off (ENGINE 0x106: CPU test 2): the combiners read the units directly, projective   // (experiment X86_NVRC=1: register combiners when ENGINE bit 0 is clear - the 45.23 driver
                                                            //  draws with the FP then too, so off by default)
        float inv = 1 / area;
        for (int y = y0; y <= y1; y++)
        {
            float py = y + 0.5f;
            for (int x = x0; x <= x1; x++)
            {
                float px = x + 0.5f;
                float w0 = ((b.X - px) * (c.Y - py) - (c.X - px) * (b.Y - py)) * inv;
                float w1 = ((c.X - px) * (a.Y - py) - (a.X - px) * (c.Y - py)) * inv;
                float w2 = 1 - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                float z = a.Z * w0 + b.Z * w1 + c.Z * w2;
                uint za = (uint)(zbase + y * zpitch + x * (z16 ? 2 : 4)) & VramMask;
                uint zi = (uint)Math.Clamp(z * zmax, 0, zmax);   // (z from the program is 0..1: D3D depth)
                uint sOld = st ? (uint)_v.Ram[za] : 0u;
                if (st && !Cmp(sFn, sRef & sMsk, sOld & sMsk)) { StencilOp(za, sOld, sOpF, sRef, sWm); continue; }
                if (zt && zpitch > 0)
                {
                    uint zold = z16 ? (uint)(_v.Ram[za] | _v.Ram[(za + 1) & VramMask] << 8) : (za <= VramBytes - 4 ? BitConverter.ToUInt32(_v.Ram, (int)za) >> 8 : 0);
                    if (!Cmp(zf, zi, zold)) { if (st) StencilOp(za, sOld, sOpZf, sRef, sWm); continue; }
                }
                if (st) StencilOp(za, sOld, sOpZp, sRef, sWm);
                // varyings, perspective correct
                float q0 = w0 * a.Iw, q1 = w1 * b.Iw, q2 = w2 * c.Iw, qs = 1 / (q0 + q1 + q2);
                if (!float.IsFinite(qs)) continue;
                q0 *= qs; q1 *= qs; q2 *= qs;
                _fin[0] = px; _fin[1] = py; _fin[2] = z; _fin[3] = a.Iw * w0 + b.Iw * w1 + c.Iw * w2;
                Lerp(1, 3, a, b, c, q0, q1, q2); Lerp(2, 4, a, b, c, q0, q1, q2); Lerp(3, 5, a, b, c, q0, q1, q2);
                for (int t = 0; t < 8; t++) Lerp(4 + t, 8 + t, a, b, c, q0, q1, q2);
                if (_pixDiag < 12 && OpLog != null && ((x ^ y) & 127) == 0) { _pixDiag++; OpLog($"rankine px {x},{y} tc0 ({_fin[16]:F3},{_fin[17]:F3},{_fin[18]:F3},{_fin[19]:F3}) col0 ({_fin[4]:F2},{_fin[5]:F2},{_fin[6]:F2}) verts tc ({a.Var[32]:F2},{a.Var[33]:F2}) ({b.Var[32]:F2},{b.Var[33]:F2}) ({c.Var[32]:F2},{c.Var[33]:F2})"); }
                if (useRc)
                {
                    RunRc(inst, false);
                    if (_pkDiag < 40 && OpLog != null && DiagFp != 0 && G(inst, 0x8e4) == DiagFp && ((x ^ y) & 31) == 0)
                    {
                        _pkDiag++;
                        OpLog($"rankine rcn {x},{y} tc1 ({_fin[20]:F3},{_fin[21]:F3},{_fin[22]:F3},{_fin[23]:F3}) tex1 {RTexInfo(inst, 1).Split(' ')[1]} col0 ({_fin[4]:F2},{_fin[5]:F2},{_fin[6]:F2}) -> ({_fr[0]:F2},{_fr[1]:F2},{_fr[2]:F2},{_fr[3]:F2}){_rcTrace}");
                    }
                }
                else
                {
                    if (!RunFp(inst, x, y)) continue;
                    if (FpRcEnv != "0") RunRc(inst, !(_fpPacks && PackNative));   // (ENGINE bit 0 clear - loading-screen 2D draws, CPU test 2: FP output as is)
                    if (_pkDiag < 40 && OpLog != null && (DiagFp != 0 ? G(inst, 0x8e4) == DiagFp : _fpPacks) && ((x ^ y) & 31) == 0)
                    {
                        _pkDiag++;
                        if (_pkDiag == 1) OpLog($"rankine rcregs {string.Join(" ", Enumerable.Range(0, 72).Select(i => G(inst, 0x8e0 + i * 4).ToString("X")))}");
                        OpLog($"rankine pk {x},{y} fp {G(inst, 0x8e4):X} H0 ({_fh[0]:F2},{_fh[1]:F2},{_fh[2]:F2},{_fh[3]:F2}) H1 ({_fh[4]:F2},{_fh[5]:F2},{_fh[6]:F2}) H2 ({_fh[8]:F2},{_fh[9]:F2},{_fh[10]:F2}) H3 ({_fh[12]:F2},{_fh[13]:F2},{_fh[14]:F2}) tc1 ({_fin[20]:F3},{_fin[21]:F3},{_fin[22]:F3},{_fin[23]:F3}) c58 ({F32(_vpConst[232]):G4},{F32(_vpConst[233]):G4},{F32(_vpConst[234]):G4}) tc4 ({_fin[32]:F2},{_fin[33]:F2},{_fin[34]:F2}) tc5 ({_fin[36]:F2},{_fin[37]:F2},{_fin[38]:F2}) pk {string.Join(" ", Enumerable.Range(0, 8).Select(i => $"[{_pk[i * 4]:F2},{_pk[i * 4 + 1]:F2},{_pk[i * 4 + 2]:F2},{_pk[i * 4 + 3]:F2}]"))} col0 ({_fin[4]:F2},{_fin[5]:F2},{_fin[6]:F2},{_fin[7]:F2}) -> ({_fr[0]:F2},{_fr[1]:F2},{_fr[2]:F2},{_fr[3]:F2}){_rcTrace}");
                    }   // the NV30 pipe: the FP's H0-H3 feed the register combiners as "textures" 0-3
                }
                var o = _fpHalfOut ? _fh : _fr;
                float cr = Math.Clamp(o[0], 0, 1), cg = Math.Clamp(o[1], 0, 1), cb = Math.Clamp(o[2], 0, 1), ca = Math.Clamp(o[3], 0, 1);
                if (at && !Cmp(af, (uint)(ca * 255 + 0.5f), (uint)(aref * 255 + 0.5f))) continue;
                uint ca8 = (uint)(ca * 255 + 0.5f) << 24 | (uint)(cr * 255 + 0.5f) << 16 | (uint)(cg * 255 + 0.5f) << 8 | (uint)(cb * 255 + 0.5f);
                uint caddr = (uint)(cbase + y * cpitch + x * cbpp) & VramMask;
                uint dst = cbpp == 4 ? (caddr <= VramBytes - 4 ? BitConverter.ToUInt32(_v.Ram, (int)caddr) : 0)
                         : cbpp == 2 ? C565((uint)(_v.Ram[caddr] | _v.Ram[(caddr + 1) & VramMask] << 8)) : _v.Ram[caddr] * 0x010101u;
                uint outc = blend ? Blend(ca8, dst, bs, bd, beq, bcol) : ca8;
                outc = (outc & ~keep) | (dst & keep);
                if (cbpp == 4 && caddr <= VramBytes - 4) BitConverter.TryWriteBytes(_v.Ram.AsSpan((int)caddr), cfmt == 5 ? outc | 0xff000000 : outc);
                else if (cbpp == 2) { uint p = ((outc >> 8) & 0xf800) | ((outc >> 5) & 0x07e0) | ((outc >> 3) & 0x1f); _v.Ram[caddr] = (byte)p; _v.Ram[(caddr + 1) & VramMask] = (byte)(p >> 8); }
                else if (cbpp == 1) _v.Ram[caddr] = (byte)outc;
                if (zt && zw && zpitch > 0)
                {
                    if (z16) { _v.Ram[za] = (byte)zi; _v.Ram[(za + 1) & VramMask] = (byte)(zi >> 8); }
                    else if (za <= VramBytes - 4) BitConverter.TryWriteBytes(_v.Ram.AsSpan((int)za), zi << 8 | (BitConverter.ToUInt32(_v.Ram, (int)za) & 0xff));
                }
                RankinePixels++;
            }
        }
    }
    private bool _rankineMaskSeen;
    private int _pixDiag;

    private static readonly bool RcOn = Environment.GetEnvironmentVariable("X86_NVRC") == "1";
    private static readonly bool RcOffNoFp = Environment.GetEnvironmentVariable("X86_NVRC") == "0";

    /// <summary>The register combiner path (ENGINE without the FP bit; envytools nv30-40_3d RC_*): textures 0-3 sampled at their
    /// (projected) coordinates, up to 8 general stages (RC_IN/OUT at 0x900 + 32 n, per-stage constants when RC_ENABLE says so), then
    /// the final combiner (0x8F4 / 0x8F8). Same register encoding as Celsius / Kelvin. Result in R0.</summary>
    private static readonly uint RcStageMask0 = Convert.ToUInt32(Environment.GetEnvironmentVariable("X86_NVRCSTAGE0") ?? "1000F000", 16);
    private static readonly uint RcStageMask1 = Convert.ToUInt32(Environment.GetEnvironmentVariable("X86_NVRCSTAGE1") ?? "100F0000", 16);
    private static readonly bool Sp0Zero = Environment.GetEnvironmentVariable("X86_NVSP0ZERO") != "0";   // (spare0 alpha starts at 0 for the FP-fed combiners: GT2 lights)
    private static readonly bool FpRcUns = Environment.GetEnvironmentVariable("X86_NVFPRCUNS") == "1";
    private static readonly uint DiagFp = Convert.ToUInt32(Environment.GetEnvironmentVariable("X86_NVDIAGFP") ?? "0", 16);
    private static readonly uint CubeAlign = Convert.ToUInt32(Environment.GetEnvironmentVariable("X86_NVCUBEALIGN") ?? "256");
    private static readonly string? FpRcEnv = Environment.GetEnvironmentVariable("X86_NVFPRC");   // default: every FP feeds the combiners (the NV30 pipe); 0 = FP output only   // experiment, off by default: GT1 gains lighting but GT2's PS 1.x passes go wrong

    private void RunRc(uint inst, bool fromFp)
    {
        var reg = new Vec4[16];
        uint en = G(inst, 0x8fc);
        int n = Math.Clamp((int)(en & 15), 1, 8);
        reg[1] = Vec4.FromArgb(G(inst, 0x8ec)); reg[2] = Vec4.FromArgb(G(inst, 0x8f0));
        uint fogc = G(inst, 0x370);
        float fogf = G(inst, 0x36c) != 0 ? Math.Clamp(_fin[12], 0, 1) : 1;
        reg[3] = new Vec4((fogc & 0xff) / 255f, ((fogc >> 8) & 0xff) / 255f, ((fogc >> 16) & 0xff) / 255f, fogf);   // (FOG_COLOR is R in the low byte)
        reg[4] = new Vec4(Math.Clamp(_fin[4], 0, 1), Math.Clamp(_fin[5], 0, 1), Math.Clamp(_fin[6], 0, 1), Math.Clamp(_fin[7], 0, 1));
        reg[5] = new Vec4(Math.Clamp(_fin[8], 0, 1), Math.Clamp(_fin[9], 0, 1), Math.Clamp(_fin[10], 0, 1), Math.Clamp(_fin[11], 0, 1));
        for (int t = 0; t < 4; t++)
        {
            if (fromFp)
            {
                var h = _fpHalfOut || t > 0 ? _fh : _fr;
                if (t == 0 && !_fpHalfOut) h = _fr;
                static float C1(float v) => float.IsNaN(v) ? 0 : Math.Clamp(v, FpRcUns ? 0 : -1, 1);
                reg[8 + t] = new Vec4(C1(h[t * 4]), C1(h[t * 4 + 1]), C1(h[t * 4 + 2]), C1(h[t * 4 + 3]));
                continue;
            }
            if (!_rtex[t].Ok) { reg[8 + t] = new Vec4(0, 0, 0, 1); continue; }
            int b = (4 + t) * 4;
            float q = _fin[b + 3]; float u = _fin[b], v = _fin[b + 1], r = _fin[b + 2];
            if (q != 0 && q != 1) { u /= q; v /= q; r /= q; }
            reg[8 + t] = Vec4.FromArgb(RTex(inst, t, u, v, r, 0));
        }
        reg[12] = new Vec4(0, 0, 0, reg[8].W);
        if (fromFp && _fpPacks && PkMode2)
        {
            for (int r = 0; r < 16; r++) if ((_pkSet & (1 << r)) != 0) reg[r] = new Vec4(_pkReg[r * 4], _pkReg[r * 4 + 1], _pkReg[r * 4 + 2], _pkReg[r * 4 + 3]);
            reg[12] = new Vec4(0, 0, 0, Sp0Zero ? 0 : reg[8].W);
        }
        else if (fromFp && _fpPacks)
        {
            for (int i = 0; i < 8; i++)
            {
                int m = PkMap[i]; if (m < 0) continue;
                int r = m <= 3 ? 8 + m : m;
                reg[r] = new Vec4(_pk[i * 4], _pk[i * 4 + 1], _pk[i * 4 + 2], _pk[i * 4 + 3]);
            }
            reg[12] = new Vec4(0, 0, 0, Sp0Zero ? 0 : reg[8].W);
        }
        _rcTrace.Clear();
        for (int s = 0; s < n; s++)
        {
            int o = 0x900 + s * 32;
            uint inA = G(inst, o), inC = G(inst, o + 4), outA = G(inst, o + 0x10), outC = G(inst, o + 0x14);
            if ((en & RcStageMask0) != 0) reg[1] = Vec4.FromArgb(G(inst, o + 8));   // per-stage constants (envytools: bits 12-15 / 16-19, "maybe";
            if ((en & RcStageMask1) != 0) reg[2] = Vec4.FromArgb(G(inst, o + 12));  //  the 45.23 driver's PS 1.x passes set bit 28)
            Vec4 A = In(reg, inC >> 24, false), B = In(reg, inC >> 16, false), C = In(reg, inC >> 8, false), D = In(reg, inC, false);
            Vec4 ab = (outC & 0x2000) != 0 ? Dot(A, B) : new Vec4(A.X * B.X, A.Y * B.Y, A.Z * B.Z, 0);
            Vec4 cd = (outC & 0x1000) != 0 ? Dot(C, D) : new Vec4(C.X * D.X, C.Y * D.Y, C.Z * D.Z, 0);
            Vec4 sum = (outC & 0x4000) != 0 ? (reg[12].W >= 0.5f ? cd : ab) : new Vec4(ab.X + cd.X, ab.Y + cd.Y, ab.Z + cd.Z, 0);
            float aA = InA(reg, inA >> 24), aB = InA(reg, inA >> 16), aC = InA(reg, inA >> 8), aD = InA(reg, inA);
            float aab = aA * aB, acd = aC * aD, asum = (outA & 0x4000) != 0 ? (reg[12].W >= 0.5f ? acd : aab) : aab + acd;
            var nr = (Vec4[])reg.Clone();
            WriteOut(nr, (int)(outC >> 4) & 15, Scale(ab, outC), true); WriteOut(nr, (int)outC & 15, Scale(cd, outC), true); WriteOut(nr, (int)(outC >> 8) & 15, Scale(sum, outC), true);
            WriteOutA(nr, (int)(outA >> 4) & 15, ScaleA(aab, outA)); WriteOutA(nr, (int)outA & 15, ScaleA(acd, outA)); WriteOutA(nr, (int)(outA >> 8) & 15, ScaleA(asum, outA));
            reg = nr;
            if (_pkDiag < 40 && OpLog != null) _rcTrace.Append($" s{s}[t0 {reg[8].X:F2},{reg[8].Y:F2},{reg[8].Z:F2},{reg[8].W:F2} t1 {reg[9].X:F2},{reg[9].W:F2} t3 {reg[11].X:F2} p {reg[4].X:F2} sp0 {reg[12].X:F2},{reg[12].W:F2} c0 {reg[1].Z:F2}]");
        }
        reg[1] = Vec4.FromArgb(G(inst, 0x8ec)); reg[2] = Vec4.FromArgb(G(inst, 0x8f0));
        uint f0 = G(inst, 0x8f4), f1 = G(inst, 0x8f8);
        var E = In(reg, f1 >> 24, true); var Fv = In(reg, f1 >> 16, true);
        reg[15] = new Vec4(E.X * Fv.X, E.Y * Fv.Y, E.Z * Fv.Z, 0);
        reg[14] = new Vec4(reg[12].X + reg[5].X, reg[12].Y + reg[5].Y, reg[12].Z + reg[5].Z, 0);
        if ((f1 & 0x80) != 0) reg[14] = new Vec4(Math.Min(reg[14].X, 1), Math.Min(reg[14].Y, 1), Math.Min(reg[14].Z, 1), 0);
        Vec4 fa = In(reg, f0 >> 24, true), fb = In(reg, f0 >> 16, true), fc = In(reg, f0 >> 8, true), fd = In(reg, f0, true);
        _fr[0] = fa.X * fb.X + (1 - fa.X) * fc.X + fd.X; _fr[1] = fa.Y * fb.Y + (1 - fa.Y) * fc.Y + fd.Y;
        _fr[2] = fa.Z * fb.Z + (1 - fa.Z) * fc.Z + fd.Z; _fr[3] = InA(reg, f1 >> 8, true);
        _fpHalfOut = false;
    }

    private static readonly bool NoStencil = Environment.GetEnvironmentVariable("X86_NVNOSTENCIL") == "1";

    private void StencilOp(uint za, uint old, uint op, uint sref, uint wm)
    {
        uint n = op switch
        {
            0 => 0, 0x150a => ~old & 0xff, 0x1e01 => sref, 0x1e02 => Math.Min(old + 1, 255), 0x1e03 => old == 0 ? 0 : old - 1,
            0x8507 => (old + 1) & 0xff, 0x8508 => (old - 1) & 0xff, _ => old,
        };
        _v.Ram[za] = (byte)((old & ~wm) | (n & wm));
    }

    private void Lerp(int fin, int vo, RVert a, RVert b, RVert c, float q0, float q1, float q2)
    {
        int o = vo * 4, d = fin * 4;
        for (int k = 0; k < 4; k++) _fin[d + k] = a.Var[o + k] * q0 + b.Var[o + k] * q1 + c.Var[o + k] * q2;
    }

    private static bool Cmp(uint func, uint v, uint r) => func switch
    {
        0x200 => false, 0x201 => v < r, 0x202 => v == r, 0x203 => v <= r, 0x204 => v > r, 0x205 => v != r, 0x206 => v >= r, _ => true,
    };

    private static uint Blend(uint s, uint d, uint fs, uint fd, uint eq, uint k)
    {
        uint r = 0;
        for (int sh = 0; sh < 32; sh += 8)
        {
            bool alpha = sh == 24;
            float sc = ((s >> sh) & 0xff) / 255f, dc = ((d >> sh) & 0xff) / 255f;
            float sa = (s >> 24) / 255f, da = (d >> 24) / 255f, kc = ((k >> sh) & 0xff) / 255f, ka = (k >> 24) / 255f;
            float F(uint f) => (alpha ? f >> 16 : f & 0xffff) switch
            {
                0 => 0, 1 => 1, 0x300 => sc, 0x301 => 1 - sc, 0x302 => sa, 0x303 => 1 - sa, 0x304 => da, 0x305 => 1 - da,
                0x306 => dc, 0x307 => 1 - dc, 0x308 => alpha ? 1 : MathF.Min(sa, 1 - da), 0x8001 => kc, 0x8002 => 1 - kc, 0x8003 => ka, 0x8004 => 1 - ka, _ => 1,
            };
            float o = (eq & 0xffff) switch
            {
                0x800a => sc * F(fs) - dc * F(fd), 0x800b => dc * F(fd) - sc * F(fs), 0x8007 => MathF.Min(sc, dc), 0x8008 => MathF.Max(sc, dc),
                _ => sc * F(fs) + dc * F(fd),
            };
            r |= (uint)(Math.Clamp(o, 0, 1) * 255 + 0.5f) << sh;
        }
        return r;
    }
}
