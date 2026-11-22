// GeForce FX (NV30-NV34): the Rankine 3D class (0x0397 / 0x0497 / 0x0697), executed synchronously.
// Method numbers from envytools rnndb graph/nv30-40_3d.xml. This file holds the state and the parts that need no shader ISA:
// render targets + clear, the vertex program memory (instruction + constant uploads), the fragment program pointer and the draw
// boundary (BEGIN_END). Vertex / fragment program execution (the Rankine VP / FP ISAs) comes in a later milestone.
using System;
using System.Collections.Generic;
using System.IO;

namespace NesEmulator.X86;

public sealed partial class NvGeForce
{
    // the vertex program memory: 544 instructions of 4 words (NV30 VP RAM), 256 vec4 constants (raw float bits)
    private readonly uint[] _vpInst = new uint[544 * 4];
    private readonly uint[] _vpConst = new uint[256 * 4];
    private int _vpUpInst, _vpUpWord, _vpUpConst, _vpUpConstWord;
    public long RankineClears, RankinePrims, VpInstUploads, VpConstUploads;   // diagnostics
    private bool _rankineDrawNoted;
    private long _queryBase;

    private static bool IsRankine(int cls) => cls is 0x397 or 0x497 or 0x697;

    private void Rankine(uint inst, int mthd, uint data)
    {
        switch (mthd)
        {
            case 0x1d94: RankineClear(inst, data); return;
            case 0x1e9c: _vpUpInst = (int)data; _vpUpWord = 0; return;                 // VP_UPLOAD_FROM_ID
            case >= 0xb80 and <= 0xb8c:                                                 // VP_UPLOAD_INST[4]
                if (_vpUpInst < 544) _vpInst[_vpUpInst * 4 + ((mthd - 0xb80) >> 2)] = data;
                VpInstUploads++;
                if (mthd == 0xb8c) _vpUpInst++;
                return;
            case 0x1efc: _vpUpConst = (int)data; return;                               // VP_UPLOAD_CONST_ID
            case >= 0x1f00 and < 0x1f40:                                                // VP_UPLOAD_CONST[4] (x,y,z,w)
            {
                int w = ((mthd - 0x1f00) >> 2) & 3;
                if (_vpUpConst < 256) _vpConst[_vpUpConst * 4 + w] = data;
                VpConstUploads++;
                if (w == 3) _vpUpConst++;
                return;
            }
            case 0x130 when Environment.GetEnvironmentVariable("X86_NVFLIPINTR") is string fi:   // experiment: FLIP_WAIT raises PGRAPH INTR bit N (BUFFER_NOTIFY = 16)
            {
                int bit = int.Parse(fi);
                _reg[0x400100] = Reg(0x400100) | (1u << bit); _reg[0x400108] = Reg(0x400108) | 1;
                _reg[0x400704] = ((uint)ActiveChid << 20) | 0x130; _reg[0x400708] = data; UpdateIrq();
                OpLog?.Invoke($"rankine flip-wait intr bit {bit}");
                return;
            }
            case 0x324: _rankineMaskSeen = true; return;
            case 0x1d70:                                                                // FENCE_VALUE: written to DMA_FENCE + FENCE_OFFSET
            {
                uint fd = G(inst, 0x1a4 | 0x8000);
                if (fd != 0) DmaWrite32(fd, G(inst, 0x1d6c), data);
                if (OpLog != null) { var (fv, fa) = fd != 0 ? DmaAddr(fd, G(inst, 0x1d6c)) : (false, 0u); OpLog($"rankine fence {data:X} obj {fd:X} -> {(fv ? "vram" : "sys")} {fa:X}"); }
                return;
            }
            case 0x1800:                                                                // QUERY_GET: a 16-byte report at DMA_QUERY + offset
            {
                uint qd = G(inst, 0x1a8 | 0x8000);
                if (qd == 0) return;
                uint off = data & 0xffffff; long ns = TimeNs(0);
                DmaWrite32(qd, off, (uint)ns); DmaWrite32(qd, off + 4, (uint)(ns >> 32));
                DmaWrite32(qd, off + 8, (uint)(data >> 24 == 1 ? Math.Min(RankinePixels - _queryBase, uint.MaxValue) : 0));
                DmaWrite32(qd, off + 12, 0);
                return;
            }
            case 0x17c8: _queryBase = RankinePixels; return;                           // (query counter reset)
            // the viewport is VP constant memory too (as Kelvin's XFCTX VPSCL / VPOFF): SCALE -> c118, TRANSLATE -> c119; the driver's
            // programs compute screen-space positions with them (oPos = v0 * c118 + c119 for pre-transformed D3D vertices)
            case >= 0xa20 and < 0xa30: _vpConst[119 * 4 + ((mthd - 0xa20) >> 2)] = data; return;
            case >= 0xa30 and < 0xa40: _vpConst[118 * 4 + ((mthd - 0xa30) >> 2)] = data; return;
        }
        if (mthd == 0x1808 && data != 0)
        {
            RankinePrims++;
            if (!_rankineDrawNoted && OpLog != null) { _rankineDrawNoted = true; OpLog($"rankine first draw prim {data} vp start {G(inst, 0x1ea0)} fp {G(inst, 0x8e4):X} engine {G(inst, 0x1e94):X}"); }
            LoadFp(inst);
        }
        RankineDrawMethod(inst, mthd, data);
    }

    /// <summary>CLEAR_BUFFERS: fill the colour target (CLEAR_COLOR_VALUE, A8R8G8B8, with the R/G/B/A mask) and the zeta target
    /// (CLEAR_DEPTH_VALUE, stencil in the low byte for Z24S8) over the render target rectangle (RT_HORIZ / RT_VERT).</summary>
    private void RankineClear(uint inst, uint what)
    {
        RankineClears++;
        uint rtf = G(inst, 0x208), pitches = G(inst, 0x20c), hz = G(inst, 0x200), vt = G(inst, 0x204);
        int x0 = (int)(hz & 0xffff), w = (int)(hz >> 16), y0 = (int)(vt & 0xffff), h = (int)(vt >> 16);
        if (w <= 0 || h <= 0 || (long)w * h > 4 << 20) return;
        int cfmt = (int)(rtf & 0x1f), zfmt = (int)(rtf >> 5) & 7;
        OpLog?.Invoke($"rankine clear {what:X} rect {x0},{y0} {w}x{h} fmt {rtf:X} col {G(inst, 0x210):X} z {G(inst, 0x214):X} pitch {pitches:X} value {G(inst, 0x1d90):X8}/{G(inst, 0x1d8c):X8}");
        if ((what & 0xf0) != 0)
        {
            int bpp = cfmt == 3 ? 2 : cfmt == 9 ? 1 : 4;
            uint dma = G(inst, 0x194 | 0x8000), off = G(inst, 0x210);
            uint b = (dma != 0 ? DmaAddr(dma, off).addr : off) & VramMask;
            int pitch = (int)(pitches & 0xffff);
            uint c = G(inst, 0x1d90);
            uint v = bpp == 2 ? ((c >> 8) & 0xf800) | ((c >> 5) & 0x07e0) | ((c >> 3) & 0x1f) : bpp == 1 ? c & 0xff : c;
            uint mask = 0;   // which bytes of a 32-bit pixel the R/G/B/A bits keep
            if ((what & 0x10) != 0) mask |= 0x00ff0000; if ((what & 0x20) != 0) mask |= 0x0000ff00;
            if ((what & 0x40) != 0) mask |= 0x000000ff; if ((what & 0x80) != 0) mask |= 0xff000000;
            for (int y = y0; y < y0 + h; y++)
                for (int x = x0; x < x0 + w; x++)
                {
                    uint a = (uint)(b + y * pitch + x * bpp) & VramMask;
                    if (bpp == 4 && a <= VramBytes - 4)
                    {
                        uint old = BitConverter.ToUInt32(_v.Ram, (int)a);
                        BitConverter.TryWriteBytes(_v.Ram.AsSpan((int)a), (old & ~mask) | (v & mask));
                    }
                    else if (bpp == 2) { _v.Ram[a] = (byte)v; _v.Ram[(a + 1) & VramMask] = (byte)(v >> 8); }
                    else if (bpp == 1) _v.Ram[a] = (byte)v;
                }
        }
        if ((what & 3) != 0)
        {
            bool z16 = zfmt == 1;
            uint dma = G(inst, 0x198 | 0x8000), off = G(inst, 0x214);
            uint b = (dma != 0 ? DmaAddr(dma, off).addr : off) & VramMask;
            int pitch = (int)(pitches >> 16);
            uint zv = G(inst, 0x1d8c);
            for (int y = y0; y < y0 + h; y++)
                for (int x = x0; x < x0 + w; x++)
                {
                    uint a = (uint)(b + y * pitch + x * (z16 ? 2 : 4)) & VramMask;
                    if (z16) { _v.Ram[a] = (byte)zv; _v.Ram[(a + 1) & VramMask] = (byte)(zv >> 8); }
                    else if (a <= VramBytes - 4)
                    {
                        uint old = BitConverter.ToUInt32(_v.Ram, (int)a);
                        uint keep = ((what & 1) != 0 ? 0 : 0xffffff00u) | ((what & 2) != 0 ? 0 : 0xffu);
                        BitConverter.TryWriteBytes(_v.Ram.AsSpan((int)a), (old & keep) | (zv & ~keep));
                    }
                }
        }
    }

    private void SaveRankine(BinaryWriter w)
    {
        foreach (var x in _vpInst) w.Write(x);
        foreach (var x in _vpConst) w.Write(x);
        w.Write(_vpUpInst); w.Write(_vpUpConst);
    }

    private void LoadRankine(BinaryReader r)
    {
        for (int i = 0; i < _vpInst.Length; i++) _vpInst[i] = r.ReadUInt32();
        for (int i = 0; i < _vpConst.Length; i++) _vpConst[i] = r.ReadUInt32();
        _vpUpInst = r.ReadInt32(); _vpUpConst = r.ReadInt32();
    }
}
