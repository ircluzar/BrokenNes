// GeForce FX (NV30-NV34) Rankine pipeline: vertex fetch, the vertex program (VP) interpreter, primitive assembly, the rasterizer and the
// fragment program (FP) interpreter, all synchronous and in float. Instruction encodings: Mesa nouveau nv30 (nv30_vertprog.h, nvfx_shader.h,
// MIT, read as reference only: geforce\dl\mesa); state registers: envytools rnndb graph/nv30-40_3d.xml.
using System;
using System.Collections.Generic;

namespace NesEmulator.X86;

public sealed partial class NvGeForce
{
    // ---------------- vertex input ----------------
    private readonly float[] _attrCur = InitAttrs();                 // VTX_ATTR_4F: the current value of each of the 16 attributes
    private readonly List<uint> _inline = new();                      // VERTEX_DATA words of the open BEGIN_END
    private readonly List<int> _elems = new();                        // vertex indices / array vertices of the open BEGIN_END (-1 = none)
    private int _rPrim;
    public long RankineTris, RankinePixels;                           // diagnostics

    private static float[] InitAttrs() { var a = new float[64]; for (int i = 0; i < 16; i++) a[i * 4 + 3] = 1; return a; }

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
                case 0x01: sr = x; break;
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
            if (sop == 0x05) { _vsr[0] = MathF.Pow(2, MathF.Floor(x)); _vsr[1] = x - MathF.Floor(x); _vsr[2] = MathF.Pow(2, x); _vsr[3] = 1; }
            if (sop == 0x06) { float ax = MathF.Abs(x); float e = ax == 0 ? -float.MaxValue : MathF.Floor(MathF.Log2(ax)); _vsr[0] = e; _vsr[1] = ax == 0 ? 1 : ax / MathF.Pow(2, e); _vsr[2] = ax == 0 ? -float.MaxValue : MathF.Log2(ax); _vsr[3] = 1; }
            // writes
            int tid = (int)(h0 >> 16) & 0x1f, oid = (int)(h3 >> 2) & 0x1f;
            int vtm = (int)(h3 >> 20) & 15, vom = (int)(h3 >> 12) & 15, stm = (int)(h3 >> 24) & 15, som = (int)(h3 >> 16) & 15;
            for (int c = 0; c < 4; c++)
            {
                bool cp = (h0 & (1u << 14)) == 0 || CondTest((int)(h0 >> 11) & 7, _vcc[(int)(h0 >> (9 - 2 * c)) & 3]);
                if (!cp) continue;
                int bit = VpMaskBit[c];
                if (vecW)
                {
                    if ((vtm & bit) != 0 && tid != 0x1f) _vt[(tid & 31) * 4 + c] = _vr[c];
                    if ((vom & bit) != 0 && oid < 16) vout[oid * 4 + c] = _vr[c];
                    if ((h0 & (1u << 15)) != 0) _vcc[c] = _vr[c];
                }
                if (scaW)
                {
                    if ((stm & bit) != 0 && tid != 0x1f) _vt[(tid & 31) * 4 + c] = _vsr[c];
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
            default: src = vin; bo = (input & 15) * 4; break;
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
    private sealed class RVert { public float X, Y, Z, Iw; public readonly float[] Var = new float[16 * 4]; }   // screen x, y, depth, 1/w; VP outputs

    private void RankineFlush(uint inst)
    {
        if (_rPrim == 0) return;
        var vin = RankineVertices(inst);
        if (vin.Count == 0) return;
        float tx = F32(G(inst, 0xa20)), ty = F32(G(inst, 0xa24)), tz = F32(G(inst, 0xa28));
        float sx = F32(G(inst, 0xa30)), sy = F32(G(inst, 0xa34)), sz = F32(G(inst, 0xa38));
        var vs = new RVert[vin.Count];
        var vout = new float[64];
        for (int i = 0; i < vin.Count; i++)
        {
            RunVp(inst, vin[i], vout);
            var v = new RVert();
            Array.Copy(vout, v.Var, 64);
            // Kelvin-style: the program's position is already in screen space (x, y in pixels, z in depth units); w is the clip w,
            // used for perspective-correct varyings only (X86_RKW=rhw: read it as 1/w instead)
            float w = vout[3]; if (w == 0 || float.IsNaN(w)) w = 1;
            v.X = vout[0]; v.Y = vout[1]; v.Z = vout[2]; v.Iw = RkwRhw ? w : 1 / w;
            vs[i] = v;
        }
        if (RankinePrims < 4 && OpLog != null)
        {
            var v0 = vs[0];
            var i0 = vin[0];
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
            default: break;                                                                                             // points / lines: not yet
        }
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
        static uint Sw(uint w) => w >> 16 | w << 16;
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
    }

    private readonly float[] _fr = new float[64 * 4];     // R0-R63 (FP32) - the half registers H0-H127 share them (H2n, H2n+1 = R n's lower / upper half: approximated as R)
    private readonly float[] _fh = new float[64 * 4];
    private readonly float[] _fin = new float[12 * 4];    // inputs: WPOS, COL0, COL1, FOGC, TC0-7
    private readonly float[] _fs = new float[12];
    private readonly float[] _fcc = new float[4];
    private bool _fpHalfOut;

    private bool RunFp(uint inst, int px, int py)
    {
        Array.Clear(_fcc); _fpHalfOut = false;
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
                    uint c = RTex(inst, (int)(f.W0 >> 17) & 15, u, v, px, py, op == 0x31 ? a[3] : 0);
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
                case 0x2e: r0 = r1 = r2 = r3 = a[0] * a[4] + a[1] * a[5] + a[8]; break;                             // DP2A
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
                if (!none) { if (half) _fh[(di & 63) * 4 + c] = res[c]; else _fr[(di & 63) * 4 + c] = res[c]; }
                if ((f.W0 & 0x100) != 0) _fcc[c] = res[c];
            }
            if (half && di == 0) _fpHalfOut = true;
            if (!half && di == 0) _fpHalfOut = false;
            if ((f.W0 & 1) != 0) break;
        }
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
        t.Mip = !t.Rect && (f & (1u << 19)) != 0 && t.MinF >= 3;
        t.Levels = 1;
        if (t.Mip) { int m = Math.Max(t.W, t.H); while (m > 1) { m >>= 1; t.Levels++; } }
        t.Ok = t.W > 0 && t.H > 0 && t.Dma != 0;
        return t;
    }

    private readonly Tex[] _rtex = new Tex[16];

    private uint RTex(uint inst, int unit, float u, float v, int px, int py, float bias)
    {
        ref var t = ref _rtex[unit & 15];
        if (!t.Ok) return 0xff000000;
        return SampleLevel(t, 0, u, v, t.Lin);
    }

    private void RTri(uint inst, RVert a, RVert b, RVert c)
    {
        if (a.Iw <= 0 || b.Iw <= 0 || c.Iw <= 0) return;   // (no clipping yet: drop triangles crossing the near plane)
        float area = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
        if (area == 0 || float.IsNaN(area)) return;
        if (G(inst, 0x183c) != 0)
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
        bool at = G(inst, 0x304) != 0; uint af = G(inst, 0x308); float aref = (G(inst, 0x30c) & 0xff) / 255f;
        bool blend = G(inst, 0x310) != 0;
        uint bs = G(inst, 0x314), bd = G(inst, 0x318), beq = G(inst, 0x320), bcol = G(inst, 0x31c);
        uint cmask = G(inst, 0x324);
        if (cmask == 0 && !_rankineMaskSeen) cmask = 0x01010101;
        uint keep = ((cmask & 0xff) == 0 ? 0xffu : 0) | ((cmask & 0xff00) == 0 ? 0xff00u : 0) | ((cmask & 0xff0000) == 0 ? 0xff0000u : 0) | ((cmask & 0xff000000) == 0 ? 0xff000000u : 0);
        float zmax = z16 ? 65535f : 16777215f;
        for (int u = 0; u < 16; u++) _rtex[u] = RTexUnit(inst, u);
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
                uint zi = (uint)Math.Clamp(z, 0, zmax);
                if (zt && zpitch > 0)
                {
                    uint zold = z16 ? (uint)(_v.Ram[za] | _v.Ram[(za + 1) & VramMask] << 8) : (za <= VramBytes - 4 ? BitConverter.ToUInt32(_v.Ram, (int)za) >> 8 : 0);
                    if (!Cmp(zf, zi, zold)) continue;
                }
                // varyings, perspective correct
                float q0 = w0 * a.Iw, q1 = w1 * b.Iw, q2 = w2 * c.Iw, qs = 1 / (q0 + q1 + q2);
                q0 *= qs; q1 *= qs; q2 *= qs;
                _fin[0] = px; _fin[1] = py; _fin[2] = z / zmax; _fin[3] = a.Iw * w0 + b.Iw * w1 + c.Iw * w2;
                Lerp(1, 3, a, b, c, q0, q1, q2); Lerp(2, 4, a, b, c, q0, q1, q2); Lerp(3, 5, a, b, c, q0, q1, q2);
                for (int t = 0; t < 8; t++) Lerp(4 + t, 8 + t, a, b, c, q0, q1, q2);
                if (!RunFp(inst, x, y)) continue;
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
