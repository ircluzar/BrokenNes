// S3 ViRGE (86C325) S3d engine: the 2D (BitBLT, rectangle fill, line, polygon) and 3D (Gouraud / textured / perspective triangle) drawing
// engine behind the ViRGE's MMIO window, plus the subsystem status registers. The SVGA core (CRTC, banking, linear window, cursor) is
// S3Trio64.cs in its ViRGE variant. Written for BrokenNes from the S3 ViRGE data book (DB019-B) with 86Box's vid_s3_virge.c (GPL-2.0) read
// for behaviour only; no code taken from it.
//
// Execution is instant: a command runs inside the register write that starts it, the FIFO is always empty and the engine always idle. That
// keeps it deterministic (no threads), so states, JIT parity and golden hashes hold. Pixels go through Raster3D.cs (shared with later cards).

using System;
using System.IO;

namespace NesEmulator.X86;

public sealed class S3Virge
{
    private readonly S3Trio64 _s3;
    private readonly Vga _v;
    private const uint VramMask = S3Trio64.VramBytes - 1;
    private static readonly bool Log = Environment.GetEnvironmentVariable("X86_VIRGELOG") == "1";
    public long Commands2D, Triangles, Pixels3D;     // diagnostics

    public S3Virge(S3Trio64 s3, Vga v) { _s3 = s3; _v = v; }

    // ---------------- registers ----------------
    // the 2D set (0xA4D4-0xA50C BitBLT/fill, 0xA8D4.. line, 0xACD4.. polygon: one shared state)
    private uint _srcBase, _dstBase, _mono0, _mono1, _patBg, _patFg, _srcBg, _srcFg, _cmd;
    private int _clipL, _clipR, _clipT, _clipB, _srcStr, _dstStr, _w, _h, _sx, _sy, _dx, _dy;
    private int _lxEnd0, _lxEnd1, _lyStart, _lyCnt, _pyStart; private bool _lineDir;
    private int _ldx, _lxStart, _prdx, _prxStart, _pldx, _plxStart; private uint _pycnt;
    private readonly byte[] _pattern = new byte[256];
    // the 3D set (0xB4D4.. triangle; 0xB0D4.. 3D line shares the header registers)
    private uint _zBase, _tDstBase, _texBase, _texBorder, _fog, _tcmd;
    private int _tClipL, _tClipR, _tClipT, _tClipB, _tDstStr, _zStr;
    private readonly int[] _t = new int[0x20];   // B504-B578 as written (index (addr - 0xB504) / 4)
    private int _ty01, _ty12; private bool _tlr;
    // subsystem
    private uint _subStat, _subCtl, _advFunc;
    private readonly uint[] _misc = new uint[0x40];   // 8180-81FC streams + others: kept, read back

    public void Reset()
    {
        _srcBase = _dstBase = _mono0 = _mono1 = _patBg = _patFg = _srcBg = _srcFg = _cmd = 0;
        _clipL = _clipT = 0; _clipR = _clipB = 0x7ff; _srcStr = _dstStr = _w = _h = _sx = _sy = _dx = _dy = 0;
        _lxEnd0 = _lxEnd1 = _lyStart = _lyCnt = _pyStart = 0; _lineDir = false; _ldx = _lxStart = _prdx = _prxStart = _pldx = _plxStart = 0; _pycnt = 0;
        Array.Clear(_pattern);
        _zBase = _tDstBase = _texBase = _texBorder = _fog = _tcmd = 0; _tClipL = _tClipT = 0; _tClipR = _tClipB = 0x7ff; _tDstStr = _zStr = 0;
        Array.Clear(_t); _ty01 = _ty12 = 0; _tlr = false;
        _subStat = 0; _subCtl = 0; _advFunc = 0; Array.Clear(_misc);
        _cpuActive = false; _cpuBytes = 0;
    }

    /// <summary>Advanced function control (MMIO 850Ch, also port 4AE8h): bit 4 linear addressing, bit 5 old MMIO.</summary>
    public uint AdvFunc => _advFunc;
    public void SetAdvFunc(uint v) => _advFunc = v & 0xff;
    public bool StreamsOn => (_v.Cr[0x67] & 0x0c) == 0x0c;
    public uint PriFb0 => _misc[(0x81c0 - 0x8180) / 4] & VramMask;
    public uint PriFb1 => _misc[(0x81c4 - 0x8180) / 4] & VramMask;
    public int PriStride => (int)(_misc[(0x81c8 - 0x8180) / 4] & 0xfff);
    public uint PriCtrl => _misc[0];
    public uint BufferCtrl => _misc[(0x81cc - 0x8180) / 4];

    private uint _pend; private uint _pendAddr = 0xffffffff;   // a dword register written in narrower pieces

    /// <summary>A CPU write into the 64 KB MMIO window (offset 0-FFFFh).</summary>
    public void Write(uint off, uint v, int bits)
    {
        off &= 0xffff;
        if (off < 0x8000) { CpuData(v, bits); return; }
        if (off is >= 0x83b0 and < 0x83e0)
        {
            for (int i = 0; i < bits / 8; i++) _v.Out((int)(0x3b0 + (off - 0x83b0) + i), (byte)(v >> (8 * i)));
            return;
        }
        if (bits == 32) { WriteReg(off & 0xfffc, v); return; }
        // 8/16-bit: merge into the dword; it takes effect when its top byte arrives (drivers write dwords anyway)
        uint a = off & 0xfffc; int sh = (int)(off & 3) * 8;
        if (_pendAddr != a) { _pend = Read32(a); _pendAddr = a; }
        uint mask = (bits == 8 ? 0xffu : 0xffffu) << sh;
        _pend = (_pend & ~mask) | ((v << sh) & mask);
        if (sh + bits >= 32) { _pendAddr = 0xffffffff; WriteReg(a, _pend); }
    }

    private int _lastFrame;
    public uint Read32(uint off)
    {
        off &= 0xfffc;
        if (_v.FrameCount != _lastFrame) { _lastFrame = _v.FrameCount; _subStat |= 1; }   // vertical sync since the last look: INT_VSY
        uint r = Read32Core(off);
        if (Log && !(off is 0x8504)) Console.WriteLine($"virge r {off:X4} = {r:X8}");
        return r;
    }

    private uint Read32Core(uint off)
    {
        if (off is >= 0x83b0 and < 0x83e0) { uint r = 0; for (int i = 0; i < 4; i++) r |= (uint)_v.In((int)(0x3b0 + (off - 0x83b0) + i)) << (8 * i); return r; }
        if (off is >= 0x8180 and < 0x8280) return _misc[((off - 0x8180) / 4) & 0x3f];
        switch (off)
        {
            case 0x8504: return 0xc000 | 0x3000 | (_subStat & 0xff);   // FIFO empty + S3d engine idle (bits 13/12), the interrupt status bits
            case 0x850c: return _advFunc & 0x3f | 8u << 6;                                // 8 free FIFO slots... (always room)
            case 0xa4d4: case 0xa8d4: case 0xacd4: return _srcBase;
            case 0xa4d8: case 0xa8d8: case 0xacd8: return _dstBase;
            case 0xa4dc: case 0xa8dc: case 0xacdc: return (uint)(_clipL << 16 | _clipR);
            case 0xa4e0: case 0xa8e0: case 0xace0: return (uint)(_clipT << 16 | _clipB);
            case 0xa4e4: case 0xa8e4: case 0xace4: return (uint)(_dstStr << 16 | _srcStr);
            case 0xa4e8: return _mono0; case 0xa4ec: return _mono1;
            case 0xa4f0: return _patBg; case 0xa4f4: return _patFg; case 0xa4f8: return _srcBg; case 0xa4fc: return _srcFg;
            case 0xa500: case 0xa900: case 0xad00: return _cmd;
            case 0xa504: return (uint)(_w << 16 | _h);
            case 0xa508: return (uint)(_sx << 16 | _sy);
            case 0xa50c: return (uint)(_dx << 16 | _dy);
            case 0xb4d4: return _zBase; case 0xb4d8: return _tDstBase; case 0xb4ec: return _texBase; case 0xb500: return _tcmd;
        }
        if (off is >= 0xb504 and <= 0xb578) return (uint)_t[(off - 0xb504) / 4];
        return 0xffffffff;
    }

    private void WriteReg(uint a, uint v)
    {
        if (Log && a >= 0x8500) Console.WriteLine($"virge w {a:X4} = {v:X8}");
        if (a is >= 0xa000 and < 0xa200) { int o = (int)(a - 0xa000) & 0xfc; for (int i = 0; i < 4 && o + i < 256; i++) _pattern[o + i] = (byte)(v >> (8 * i)); return; }
        if (a is >= 0x8180 and < 0x8280) { _misc[((a - 0x8180) / 4) & 0x3f] = v; _s3.InvalidateLines(); return; }
        switch (a)
        {
            case 0x8504: _subStat &= ~(v & 0xff); _subCtl = (v >> 8) & 0xff; return;
            case 0x850c: _advFunc = v & 0xff; return;
            // 2D
            case 0xa4d4: case 0xa8d4: case 0xacd4: _srcBase = v & VramMask & ~7u; return;
            case 0xa4d8: case 0xa8d8: case 0xacd8: _dstBase = v & VramMask & ~7u; return;
            case 0xa4dc: case 0xa8dc: case 0xacdc: _clipL = (int)(v >> 16) & 0x7ff; _clipR = (int)v & 0x7ff; return;
            case 0xa4e0: case 0xa8e0: case 0xace0: _clipT = (int)(v >> 16) & 0x7ff; _clipB = (int)v & 0x7ff; return;
            case 0xa4e4: case 0xa8e4: case 0xace4: _dstStr = (int)(v >> 16) & 0xff8; _srcStr = (int)v & 0xff8; return;
            case 0xa4e8: case 0xace8: _mono0 = v; return;
            case 0xa4ec: case 0xacec: _mono1 = v; return;
            case 0xa4f0: case 0xacf0: _patBg = v; return;
            case 0xa4f4: case 0xa8f4: case 0xacf4: _patFg = v; return;
            case 0xa4f8: _srcBg = v; return;
            case 0xa4fc: _srcFg = v; return;
            case 0xa500: case 0xa900: case 0xad00: _cmd = v; if ((v & 1) == 0) Start2D(); return;
            case 0xa504: _w = (int)(v >> 16) & 0x7ff; _h = (int)v & 0x7ff; return;
            case 0xa508: _sx = (int)(v >> 16) & 0x7ff; _sy = (int)v & 0x7ff; return;
            case 0xa50c: _dx = (int)(v >> 16) & 0x7ff; _dy = (int)v & 0x7ff; if ((_cmd & 1) != 0) Start2D(); return;
            case 0xa96c: _lxEnd0 = (int)(v >> 16) & 0x7ff; _lxEnd1 = (int)v & 0x7ff; return;
            case 0xa970: _ldx = (int)v; return;
            case 0xa974: _lxStart = (int)v; return;
            case 0xa978: _lyStart = (int)v & 0x7ff; return;
            case 0xa97c: _lyCnt = (int)v & 0x7ff; _lineDir = (v >> 31) != 0; if ((_cmd & 1) != 0) Start2D(); return;
            case 0xad68: _prdx = (int)v; return;
            case 0xad6c: _prxStart = (int)v; return;
            case 0xad70: _pldx = (int)v; return;
            case 0xad74: _plxStart = (int)v; return;
            case 0xad78: _pyStart = (int)v & 0x7ff; return;
            case 0xad7c: _pycnt = v & 0x300007ff; if ((_cmd & 1) != 0) Start2D(); return;
            // 3D
            case 0xb0d4: case 0xb4d4: _zBase = v & VramMask & ~7u; return;
            case 0xb0d8: case 0xb4d8: _tDstBase = v & VramMask & ~7u; return;
            case 0xb0dc: case 0xb4dc: _tClipL = (int)(v >> 16) & 0x7ff; _tClipR = (int)v & 0x7ff; return;
            case 0xb0e0: case 0xb4e0: _tClipT = (int)(v >> 16) & 0x7ff; _tClipB = (int)v & 0x7ff; return;
            case 0xb0e4: case 0xb4e4: _tDstStr = (int)(v >> 16) & 0xff8; return;
            case 0xb0e8: case 0xb4e8: _zStr = (int)v & 0xff8; return;
            case 0xb4ec: _texBase = v & VramMask & ~7u; return;
            case 0xb4f0: _texBorder = v & 0xffffff; return;
            case 0xb0f4: case 0xb4f4: _fog = v & 0xffffff; return;
            case 0xb100: case 0xb500: _tcmd = v; if ((v & 1) == 0) Triangle(); return;
            case 0xb57c: _ty01 = (int)(v >> 16) & 0x7ff; _ty12 = (int)v & 0x7ff; _tlr = (v >> 31) != 0; if ((_tcmd & 1) != 0) Triangle(); return;
        }
        if (a is >= 0xb504 and <= 0xb578) _t[(a - 0xb504) / 4] = (int)v;
    }

    // ================= 2D =================

    private int Bpp => ((_cmd >> 2) & 7) switch { 0 => 1, 1 => 2, _ => 3 };
    private int Rop => (int)(_cmd >> 17) & 0xff;
    private uint ReadPix(uint a, int bpp)
    {
        var r = _v.Ram;
        return bpp switch { 1 => r[a & VramMask], 2 => (uint)(r[a & VramMask] | r[(a + 1) & VramMask] << 8), _ => (uint)(r[a & VramMask] | r[(a + 1) & VramMask] << 8 | r[(a + 2) & VramMask] << 16) };
    }
    private void WritePix(uint a, int bpp, uint c)
    {
        var r = _v.Ram;
        r[a & VramMask] = (byte)c;
        if (bpp >= 2) r[(a + 1) & VramMask] = (byte)(c >> 8);
        if (bpp == 3) r[(a + 2) & VramMask] = (byte)(c >> 16);
    }
    private bool Clipped(int x, int y) => (_cmd & 2) != 0 && (x < _clipL || x > _clipR || y < _clipT || y > _clipB);
    private uint PixMask(int bpp) => bpp == 1 ? 0xffu : bpp == 2 ? 0xffffu : 0xffffffu;

    private uint Pattern(int x, int y, int bpp)
    {
        if ((_cmd & 0x100) != 0)   // mono pattern: rows 0-3 in pattern 0, 4-7 in pattern 1, bit 7-x of each row byte
        {
            uint bits = (y & 4) == 0 ? _mono0 : _mono1;
            return ((bits >> ((y & 3) * 8 + (7 - (x & 7)))) & 1) != 0 ? _patFg : _patBg;
        }
        int o = (y & 7) * 8 * bpp + (x & 7) * bpp;
        uint c = _pattern[o];
        if (bpp >= 2) c |= (uint)_pattern[o + 1] << 8;
        if (bpp == 3) c |= (uint)_pattern[o + 2] << 16;
        return c;
    }

    // CPU image data for a command waiting for it (cmd bit 7): a byte stream, mono MSB first, colour little-endian pixels, each row padded
    private bool _cpuActive;
    private int _cpuX, _cpuY, _cpuRow, _cpuRowBytes, _cpuBytes, _cpuAlign;
    private ulong _cpuAcc;

    private void Start2D()
    {
        Commands2D++;
        int command = (int)(_cmd >> 27) & 15;
        if (Log) Console.WriteLine($"virge 2d cmd {_cmd:X8} op {command} rop {Rop:X2} src {_srcBase:X}+{_sx},{_sy} str {_srcStr} dst {_dstBase:X}+{_dx},{_dy} str {_dstStr} wh {_w},{_h} pat {_patFg:X}/{_patBg:X} src {_srcFg:X}/{_srcBg:X}");
        _cpuActive = false;
        switch (command)
        {
            case 0: BitBlt(); break;
            case 2: RectFill(); break;
            case 3: Line(); break;
            case 5: Poly(); break;
        }
        _subStat |= 0x48;   // FIFO empty, 3D FIFO empty
    }

    private void BitBlt()
    {
        int bpp = Bpp;
        if ((_cmd & 0x80) != 0)
        {
            // waits for the CPU data
            _cpuActive = _h > 0; _cpuX = 0; _cpuY = 0; _cpuRow = 0; _cpuAcc = 0; _cpuBytes = 0;
            bool mono = (_cmd & 0x40) != 0;
            _cpuRowBytes = mono ? (_w + 1 + 7) / 8 : (_w + 1) * bpp;
            _cpuAlign = ((_cmd >> 10) & 3) switch { 0 => 1, 1 => 2, _ => 4 };
            return;
        }
        int xi = (_cmd & (1 << 25)) != 0 ? 1 : -1, yi = (_cmd & (1 << 26)) != 0 ? 1 : -1;
        uint srcFg = _srcFg & PixMask(bpp);
        bool tp = (_cmd & 0x200) != 0;
        for (int j = 0; j < _h; j++)
        {
            int sy = (_sy + j * yi) & 0x7ff, dy = (_dy + j * yi) & 0x7ff;
            if (xi > 0 && yi > 0 && !tp && Rop == 0xcc && (_cmd & 2) == 0 && SimpleRowCopy(sy, dy, bpp)) continue;
            for (int i = 0; i <= _w; i++)
            {
                int sx = (_sx + i * xi) & 0x7ff, dx = (_dx + i * xi) & 0x7ff;
                uint s = ReadPix((uint)(_srcBase + sx * bpp + sy * _srcStr), bpp);
                if (tp && s == srcFg) continue;
                Pixel(dx, dy, bpp, s);
            }
        }
    }

    /// <summary>A plain copy row (source to destination, no clip, no wrap) as one memmove: what the pixel loop gives when rows do not overlap backwards.</summary>
    private bool SimpleRowCopy(int sy, int dy, int bpp)
    {
        if (_sx + _w > 0x7ff || _dx + _w > 0x7ff) return false;
        long s = _srcBase + _sx * bpp + (long)sy * _srcStr, d = _dstBase + _dx * bpp + (long)dy * _dstStr, n = (_w + 1) * bpp;
        if (s + n > S3Trio64.VramBytes || d + n > S3Trio64.VramBytes) return false;
        if (d > s && d < s + n) return false;   // the pixel loop (left to right) would smear: keep its result
        Array.Copy(_v.Ram, s, _v.Ram, d, n);
        return true;
    }

    private void Pixel(int x, int y, int bpp, uint s)
    {
        if (Clipped(x, y)) return;
        uint a = (uint)(_dstBase + x * bpp + y * _dstStr);
        uint d = ReadPix(a, bpp);
        uint p = Pattern(x, y, bpp);
        WritePix(a, bpp, Raster3D.Rop3(Rop, p, s, d) & PixMask(bpp));
    }

    private void CpuData(uint v, int bits)
    {
        if (!_cpuActive) return;
        for (int i = 0; i < bits / 8 && _cpuActive; i++) CpuByte((byte)(v >> (8 * i)));
    }

    private void CpuByte(byte b)
    {
        int bpp = Bpp;
        bool mono = (_cmd & 0x40) != 0, tp = (_cmd & 0x200) != 0;
        int xi = (_cmd & (1 << 25)) != 0 ? 1 : -1, yi = (_cmd & (1 << 26)) != 0 ? 1 : -1;
        int row = _cpuRow++;
        if (row < _cpuRowBytes)
        {
            int dy = (_dy + _cpuY * yi) & 0x7ff;
            if (mono)
            {
                for (int k = 7; k >= 0 && _cpuX <= _w; k--, _cpuX++)
                {
                    bool fg = ((b >> k) & 1) != 0;
                    if (tp && !fg) continue;
                    Pixel((_dx + _cpuX * xi) & 0x7ff, dy, bpp, fg ? _srcFg : _srcBg);
                }
            }
            else
            {
                _cpuAcc |= (ulong)b << (8 * _cpuBytes++);
                if (_cpuBytes == bpp)
                {
                    uint s = (uint)_cpuAcc & PixMask(bpp);
                    if (!(tp && s == (_srcFg & PixMask(bpp)))) Pixel((_dx + _cpuX * xi) & 0x7ff, dy, bpp, s);
                    _cpuX++; _cpuAcc = 0; _cpuBytes = 0;
                }
            }
        }
        int padded = (_cpuRowBytes + _cpuAlign - 1) / _cpuAlign * _cpuAlign;
        if (_cpuRow >= padded)
        {
            _cpuRow = 0; _cpuX = 0; _cpuAcc = 0; _cpuBytes = 0;
            if (++_cpuY >= _h) _cpuActive = false;
        }
    }

    private void RectFill()
    {
        int bpp = Bpp;
        int xi = (_cmd & (1 << 25)) != 0 ? 1 : -1, yi = (_cmd & (1 << 26)) != 0 ? 1 : -1;
        uint c = _patFg & PixMask(bpp);
        for (int j = 0; j < _h; j++)
        {
            int dy = (_dy + j * yi) & 0x7ff;
            for (int i = 0; i <= _w; i++)
            {
                int dx = (_dx + i * xi) & 0x7ff;
                if (Clipped(dx, dy)) continue;
                uint a = (uint)(_dstBase + dx * bpp + dy * _dstStr);
                WritePix(a, bpp, Raster3D.Rop3(Rop, c, c, ReadPix(a, bpp)) & PixMask(bpp));
            }
        }
    }

    private void Line()
    {
        // one run of pixels per scan line, from the bottom (LYSTART) up; x in 12.20 fixed point, clamped to XEND0 / XEND1 at the ends
        int bpp = Bpp; uint c = _patFg & PixMask(bpp);
        int xf = _lxStart, y = _lyStart;
        for (int n = _lyCnt; n > 0; n--)
        {
            bool first = n == _lyCnt, last = n == 1;
            int x = xf >> 20;
            if (first && (_lineDir ? x > _lxEnd0 : x < _lxEnd0)) x = _lxEnd0;
            int nx = last ? _lxEnd1 + (_lineDir ? 1 : -1) : (xf + _ldx) >> 20;
            if (!(_lineDir ? x > nx : x < nx))
            {
                bool firstPix = true;
                do
                {
                    bool skip = ((first || !firstPix) && (_lineDir ? x < _lxEnd0 : x > _lxEnd0)) || ((last || !firstPix) && (_lineDir ? x > _lxEnd1 : x < _lxEnd1));
                    if (!skip && !Clipped(x, y))
                    {
                        uint a = (uint)(_dstBase + x * bpp + y * _dstStr);
                        WritePix(a, bpp, Raster3D.Rop3(Rop, c, c, ReadPix(a, bpp)) & PixMask(bpp));
                    }
                    if (x < nx) x++; else if (x > nx) x--;
                    firstPix = false;
                } while (x != nx);
            }
            xf += _ldx; y = (y - 1) & 0x7ff;
        }
    }

    private int _polyL, _polyR;
    private void Poly()
    {
        int bpp = Bpp;
        if ((_pycnt & (1 << 28)) != 0) _polyR = _prxStart;
        if ((_pycnt & (1 << 29)) != 0) _polyL = _plxStart;
        for (int n = (int)(_pycnt & 0x7ff); n > 0; n--)
        {
            int x = _polyL >> 20, xe = _polyR >> 20, y = _pyStart & 0x7ff, dir = x < xe ? 1 : -1;
            while (true)
            {
                if (!Clipped(x, y))
                {
                    uint a = (uint)(_dstBase + x * bpp + y * _dstStr), p = Pattern(x, y, bpp);
                    WritePix(a, bpp, Raster3D.Rop3(Rop, p, p, ReadPix(a, bpp)) & PixMask(bpp));
                }
                if (x == xe) break;
                x = (x + dir) & 0x7ff;
            }
            _polyL += _pldx; _polyR += _prdx; _pyStart = (_pyStart - 1) & 0x7ff;
        }
    }

    // ================= 3D =================
    // the triangle registers (B504-B57C): index into _t
    private int T(uint a) => _t[(a - 0xb504) / 4];

    private void Triangle()
    {
        int command = (int)(_tcmd >> 27) & 15;
        if (Log) Console.WriteLine($"virge tri cmd {_tcmd:X8} op {command} y {T(0xb578)} n {_ty01}+{_ty12} lr {_tlr} x {T(0xb574):X8} dst {_tDstBase:X}/{_tDstStr} z {_zBase:X}/{_zStr} tex {_texBase:X}");
        if (command is not (0 or 1 or 2 or 5 or 6)) return;
        Triangles++;
        var st = new TriState();
        st.Persp = (command & 4) != 0;
        st.Textured = command != 0;
        st.Lit = command is 1 or 5;
        st.MaxD = (int)(_tcmd >> 8) & 15;
        st.Bpp = (int)(_tcmd >> 2) & 7;
        st.TexFmt = (int)(_tcmd >> 5) & 7;
        st.Filter = (int)(_tcmd >> 12) & 7;
        st.Blend = (int)(_tcmd >> 15) & 3;
        st.Wrap = (_tcmd & (1 << 26)) != 0;
        st.Tbu = (T(0xb508) & 0xfffff) << 11; st.Tbv = (T(0xb504) & 0xfffff) << 11;
        // the mip levels: the biggest (MaxD) first at the texture base
        uint tb = _texBase;
        int texelBytes = st.TexFmt switch { 0 => 4, 1 or 2 => 2, _ => 1 };
        for (int c = 9; c >= 0; c--)
        {
            st.Level[c] = tb;
            if (c <= st.MaxD) tb += (uint)((1 << (c * 2)) * texelBytes);
        }
        st.BaseU = T(0xb538); st.BaseV = T(0xb534); st.BaseZ = T(0xb55c); st.BaseD = T(0xb530); st.BaseW = T(0xb514);
        st.BaseR = T(0xb550) & 0xffff; st.BaseA = (T(0xb550) >> 16) & 0xffff; st.BaseB = T(0xb54c) & 0xffff; st.BaseG = (T(0xb54c) >> 16) & 0xffff;
        st.Y = T(0xb578); st.X1 = T(0xb574);
        st.X2 = T(0xb56c);
        Span(ref st, _ty01, T(0xb570), T(0xb568));
        st.X2 = T(0xb564);
        Span(ref st, _ty12, T(0xb570), T(0xb560));
        _subStat |= 0x4a;   // S3d done, FIFO empty, 3D FIFO empty
    }

    private struct TriState
    {
        public bool Persp, Textured, Lit, Wrap;
        public int MaxD, Bpp, TexFmt, Filter, Blend, Tbu, Tbv;
        public int BaseU, BaseV, BaseZ, BaseD, BaseW, BaseR, BaseG, BaseB, BaseA;
        public int X1, X2, Y;
        public LevelArray Level;
    }
    [System.Runtime.CompilerServices.InlineArray(10)] private struct LevelArray { private uint _e; }

    // per-pixel X deltas (signed: 16-bit colour, 32-bit others)
    private short dRx => (short)T(0xb540); private short dAx => (short)(T(0xb540) >> 16); private short dBx => (short)T(0xb53c); private short dGx => (short)(T(0xb53c) >> 16);
    private short dRy => (short)T(0xb548); private short dAy => (short)(T(0xb548) >> 16); private short dBy => (short)T(0xb544); private short dGy => (short)(T(0xb544) >> 16);

    private void Span(ref TriState st, int lines, int dx1, int dx2)
    {
        int xdir = _tlr ? 1 : -1;
        bool useZ = (_tcmd & (3u << 24)) == 0, hc = (_tcmd & 2) != 0;
        int zfunc = (int)(_tcmd >> 20) & 7;
        int pixBytes = st.Bpp + 1;
        int dUx = T(0xb520), dVx = T(0xb51c), dWx = T(0xb50c), dDx = T(0xb518), dZx = T(0xb554);
        int dUy = T(0xb52c), dVy = T(0xb528), dWy = T(0xb510), dDy = T(0xb524), dZy = T(0xb558);
        int rX = dRx, gX = dGx, bX = dBx, aX = dAx;
        var ram = _v.Ram;
        for (; lines > 0; lines--)
        {
            int y = st.Y;
            bool lineOn = !hc || (y >= _tClipT && y <= _tClipB);
            int x = (st.X1 + ((1 << 20) - 1)) >> 20, xe = (st.X2 + ((1 << 20) - 1)) >> 20;
            if (xdir < 0) { x--; xe--; }
            if (lineOn && x != xe && (xdir > 0 ? x < xe : x > xe))
            {
                // the first pixel's sub-pixel step: 1/32 units of the X deltas
                int sub = xdir > 0 ? ((31 - ((st.X1 - 1) >> 15)) & 31) + 1 : ((st.X1 - 1) >> 15) & 31;
                int r = st.BaseR + ((rX * sub) >> 5), g = st.BaseG + ((gX * sub) >> 5), b = st.BaseB + ((bX * sub) >> 5), a = st.BaseA + ((aX * sub) >> 5);
                int u = st.BaseU + ((dUx * sub) >> 5), v = st.BaseV + ((dVx * sub) >> 5), w = st.BaseW + ((dWx * sub) >> 5), d = st.BaseD + ((dDx * sub) >> 5);
                uint z = (st.BaseZ > 0 ? (uint)st.BaseZ << 1 : 0) + (uint)((dZx * sub) >> 5);
                uint dstRow = (uint)(_tDstBase + y * _tDstStr), zRow = (uint)(_zBase + y * _zStr);
                x &= 0xfff; xe &= 0xfff;
                for (; x != xe; x = (x + xdir) & 0xfff)
                {
                    bool draw = !hc || (x >= _tClipL && x <= _tClipR);
                    uint za = (zRow + (uint)(x << 1)) & VramMask;
                    uint zs = z >> 16, zb = 0;
                    if (draw && useZ)
                    {
                        zb = (uint)(ram[za] | ram[(za + 1) & VramMask] << 8);
                        draw = Raster3D.DepthPass(zfunc, zs, zb);
                    }
                    if (draw)
                    {
                        Pixels3D++;
                        var col = Shade(ref st, r, g, b, a, u, v, w, d);
                        if ((_tcmd & (1 << 17)) != 0)   // fog: blend toward the fog colour by the iterated alpha
                        {
                            int fa = Raster3D.Clamp8(a >> 7);
                            col.R = Raster3D.Lerp255(col.R, (int)(_fog >> 16) & 255, fa); col.G = Raster3D.Lerp255(col.G, (int)(_fog >> 8) & 255, fa); col.B = Raster3D.Lerp255(col.B, (int)_fog & 255, fa);
                        }
                        uint da = (dstRow + (uint)(x * pixBytes)) & VramMask;
                        if ((_tcmd & (1 << 19)) != 0)    // alpha blending with what is in the frame buffer
                        {
                            Rgba dst = st.Bpp == 1 ? Raster3D.Unpack555(ram[da] | ram[(da + 1) & VramMask] << 8)
                                     : st.Bpp == 2 ? new Rgba(255, ram[(da + 2) & VramMask], ram[(da + 1) & VramMask], ram[da]) : new Rgba(255, 0, 0, 0);
                            if (st.Bpp != 0) Raster3D.AlphaBlend(ref col, dst, col.A);
                        }
                        switch (st.Bpp)
                        {
                            case 1: { int p = Raster3D.Pack555(col); ram[da] = (byte)p; ram[(da + 1) & VramMask] = (byte)(p >> 8); break; }
                            case 2: ram[da] = (byte)col.B; ram[(da + 1) & VramMask] = (byte)col.G; ram[(da + 2) & VramMask] = (byte)col.R; break;
                        }
                        if (useZ && (_tcmd & (1 << 23)) != 0) { ram[za] = (byte)zs; ram[(za + 1) & VramMask] = (byte)(zs >> 8); }
                    }
                    z += (uint)dZx; u += dUx; v += dVx; r += rX; g += gX; b += bX; a += aX; d += dDx; w += dWx;
                }
            }
            st.X1 += dx1; st.X2 += dx2;
            st.BaseU += dUy; st.BaseV += dVy; st.BaseZ += dZy; st.BaseR += dRy; st.BaseG += dGy; st.BaseB += dBy; st.BaseA += dAy; st.BaseD += dDy; st.BaseW += dWy;
            st.Y--;
        }
    }

    private Rgba Shade(ref TriState st, int r, int g, int b, int a, int u, int v, int w, int d)
    {
        var c = new Rgba(a >> 7, r >> 7, g >> 7, b >> 7);
        if (!st.Textured) { c.Clamp8(); return c; }
        bool alphaSrc = (_tcmd & (1 << 18)) != 0;
        var t = Texel(ref st, u, v, w, d);
        if (st.Lit)
        {
            switch (st.Blend)
            {
                case 0: t.R += c.R; t.G += c.G; t.B += c.B; if (alphaSrc) t.A += c.A; t.Clamp8(); break;   // complex reflection: add
                case 1: c.Clamp8(); Raster3D.Modulate(ref t, c); if (alphaSrc) t.A = c.A; break;
                default: if (alphaSrc) t.A = Raster3D.Clamp8(c.A); break;                               // decal
            }
        }
        else if (alphaSrc) t.A = Raster3D.Clamp8(c.A);
        return t;
    }

    private Rgba Texel(ref TriState st, int u, int v, int w, int d)
    {
        bool mip = (st.Filter & 4) == 0, bilinear = (st.Filter & 2) != 0;
        int level = !mip || d < 0 ? st.MaxD : st.MaxD - ((d >> 27) & 15);
        if (level < 0) level = 0;
        if (st.Persp)
        {
            int wi = w != 0 ? (int)((1L << 46) / w) : 0;
            u = (int)(((long)u * wi) >> (12 + st.MaxD)); v = (int)(((long)v * wi) >> (12 + st.MaxD));   // (the 86C325's divide; the DX keeps 4 more bits)
        }
        u += st.Tbu; v += st.Tbv;
        int shift = 18 + (9 - level);
        if (!bilinear) return Fetch(ref st, level, shift, u, v);
        int one = 1 << shift;
        int fu = (u >> (shift - 8)) & 0xff, fv = (v >> (shift - 8)) & 0xff;
        return Raster3D.Bilinear(Fetch(ref st, level, shift, u, v), Fetch(ref st, level, shift, u + one, v), Fetch(ref st, level, shift, u, v + one), Fetch(ref st, level, shift, u + one, v + one), fu, fv);
    }

    private Rgba Fetch(ref TriState st, int level, int shift, int u, int v)
    {
        if (!st.Wrap && ((u | v) & 0xf8000000) == 0xf8000000) return Border(st.TexFmt);
        int tu = (int)(((uint)u & 0x7fc0000) >> shift), tv = (int)(((uint)v & 0x7fc0000) >> shift);
        int i = tu + (tv << level);
        var ram = _v.Ram;
        switch (st.TexFmt)
        {
            case 0: { uint a = (st.Level[level] + (uint)i * 4) & VramMask; return Raster3D.Argb8888((uint)(ram[a] | ram[(a + 1) & VramMask] << 8 | ram[(a + 2) & VramMask] << 16 | ram[(a + 3) & VramMask] << 24)); }
            case 1: { uint a = (st.Level[level] + (uint)i * 2) & VramMask; return Raster3D.Argb4444(ram[a] | ram[(a + 1) & VramMask] << 8); }
            default: { uint a = (st.Level[level] + (uint)i * 2) & VramMask; return Raster3D.Argb1555(ram[a] | ram[(a + 1) & VramMask] << 8); }
        }
    }

    private Rgba Border(int fmt) => fmt switch
    {
        0 => Raster3D.Argb8888(_texBorder | 0xff000000), 1 => Raster3D.Argb4444((int)_texBorder & 0xffff), _ => Raster3D.Argb1555((int)_texBorder & 0xffff),
    };

    // ================= state =================

    public void Save(BinaryWriter w)
    {
        foreach (uint v in new[] { _srcBase, _dstBase, _mono0, _mono1, _patBg, _patFg, _srcBg, _srcFg, _cmd, _pycnt, _zBase, _tDstBase, _texBase, _texBorder, _fog, _tcmd, _subStat, _subCtl, _advFunc, _pend, _pendAddr }) w.Write(v);
        foreach (int v in new[] { _clipL, _clipR, _clipT, _clipB, _srcStr, _dstStr, _w, _h, _sx, _sy, _dx, _dy, _lxEnd0, _lxEnd1, _lyStart, _lyCnt, _pyStart, _ldx, _lxStart, _prdx, _prxStart, _pldx, _plxStart,
            _tClipL, _tClipR, _tClipT, _tClipB, _tDstStr, _zStr, _ty01, _ty12, _cpuX, _cpuY, _cpuRow, _cpuRowBytes, _cpuBytes, _cpuAlign, _polyL, _polyR }) w.Write(v);
        w.Write(_lineDir); w.Write(_tlr); w.Write(_cpuActive); w.Write(_cpuAcc);
        w.Write(_pattern); foreach (int v in _t) w.Write(v); foreach (uint v in _misc) w.Write(v);
    }

    public void Load(BinaryReader r)
    {
        var u = new uint[21]; for (int i = 0; i < u.Length; i++) u[i] = r.ReadUInt32();
        (_srcBase, _dstBase, _mono0, _mono1, _patBg, _patFg, _srcBg, _srcFg, _cmd, _pycnt) = (u[0], u[1], u[2], u[3], u[4], u[5], u[6], u[7], u[8], u[9]);
        (_zBase, _tDstBase, _texBase, _texBorder, _fog, _tcmd, _subStat, _subCtl, _advFunc, _pend, _pendAddr) = (u[10], u[11], u[12], u[13], u[14], u[15], u[16], u[17], u[18], u[19], u[20]);
        var a = new int[39]; for (int i = 0; i < a.Length; i++) a[i] = r.ReadInt32();
        (_clipL, _clipR, _clipT, _clipB, _srcStr, _dstStr, _w, _h, _sx, _sy, _dx, _dy) = (a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9], a[10], a[11]);
        (_lxEnd0, _lxEnd1, _lyStart, _lyCnt, _pyStart, _ldx, _lxStart, _prdx, _prxStart, _pldx, _plxStart) = (a[12], a[13], a[14], a[15], a[16], a[17], a[18], a[19], a[20], a[21], a[22]);
        (_tClipL, _tClipR, _tClipT, _tClipB, _tDstStr, _zStr, _ty01, _ty12) = (a[23], a[24], a[25], a[26], a[27], a[28], a[29], a[30]);
        (_cpuX, _cpuY, _cpuRow, _cpuRowBytes, _cpuBytes, _cpuAlign, _polyL, _polyR) = (a[31], a[32], a[33], a[34], a[35], a[36], a[37], a[38]);
        _lineDir = r.ReadBoolean(); _tlr = r.ReadBoolean(); _cpuActive = r.ReadBoolean(); _cpuAcc = r.ReadUInt64();
        r.Read(_pattern, 0, _pattern.Length); for (int i = 0; i < _t.Length; i++) _t[i] = r.ReadInt32(); for (int i = 0; i < _misc.Length; i++) _misc[i] = r.ReadUInt32();
    }
}
