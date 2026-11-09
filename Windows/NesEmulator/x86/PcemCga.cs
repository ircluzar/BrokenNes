// PCEM_CGA: an IBM CGA, the picture of PCem v10 (src/vid_cga.c: cga_out / cga_write / cga_read / cga_poll; the RGBI colours of video.c initvideo's
// cgapal), PCem (Sarah Walker), github.com/sarah-walker-pcem/pcem commit 30826ba ("Bumped version number up to v10", 2015-10-31), GPL-2.0. Ported to C#
// for BrokenNes as an art instrument, flaws kept. See x86/THIRD_PARTY_NOTICES.md.
//
// The card: a 6845 CRTC (its own registers 0-11, from the CGA BIOS tables when the BIOS sets a mode, and from port writes to 3D4/3D5 with PCem's
// masks), the mode register 3D8 and the colour register 3D9, and 16 KB of video memory at B8000 (this machine keeps it in the VGA card's odd/even
// planes, which is the same bytes). The start address and the cursor position (registers 12-15) are the ones the BIOS keeps. The VGA's own modes
// (12h, 13h) are not this card's: a game that needs a VGA shows whatever is left in B8000.
// The snow (PCem's model): in 80-column text the card fetches a row's characters into a line buffer before each scan line; a CPU write or read of
// video memory while the line is being displayed puts the bus byte into the buffer at the beam's position (a pair of cells, as both character and
// attribute), so the cell shows a sliver of garbage on that one scan line. The beam's position comes from the machine clock, mapped onto the CGA's
// 262-line frame. The font is the 8x8 CGA font folded from the 8x16 VGA font (no character ROM is shipped); composite colour is not offered (PCem's
// default is RGB). Left out: interlace (register 8), the light pen, the status register (3DA stays the VGA's).

using System;
using System.Collections.Generic;
using System.IO;

namespace NesEmulator.X86;

public sealed class PcemCga : IVgaPicture
{
    private static readonly byte[] CrtcMask =
    {
        0xff, 0xff, 0xff, 0xff, 0x7f, 0x1f, 0x7f, 0x7f, 0xf3, 0x1f, 0x7f, 0x1f, 0x3f, 0xff, 0x3f, 0xff,
        0xff, 0xff, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    };
    // the IBM CGA BIOS's CRTC tables (registers 0-15): 40x25, 80x25, graphics
    private static readonly byte[] T40 = { 0x38, 0x28, 0x2d, 0x0a, 0x1f, 0x06, 0x19, 0x1c, 0x02, 0x07, 0x06, 0x07, 0, 0, 0, 0 };
    private static readonly byte[] T80 = { 0x71, 0x50, 0x5a, 0x0a, 0x1f, 0x06, 0x19, 0x1c, 0x02, 0x07, 0x06, 0x07, 0, 0, 0, 0 };
    private static readonly byte[] TGfx = { 0x38, 0x28, 0x2d, 0x0a, 0x7f, 0x06, 0x64, 0x70, 0x02, 0x01, 0x06, 0x07, 0, 0, 0, 0 };
    private static readonly byte[] Font8 = BuildFont8();
    private static byte[] BuildFont8()
    {
        var f = new byte[256 * 8];
        for (int c = 0; c < 256; c++)
            for (int y = 0; y < 8; y++) f[c * 8 + y] = (byte)(VgaFont.Font16[c * 16 + y * 2] | VgaFont.Font16[c * 16 + y * 2 + 1]);
        return f;
    }
    private static readonly int[] Rgbi = BuildRgbi();
    private static int[] BuildRgbi()
    {   // cgapal[c + 64] for the RGBI colours: 2/3 for the colour bit, 1/3 for intensity, brown's green halved
        var p = new int[16];
        for (int i = 0; i < 16; i++)
        {
            int c = (i & 7) | ((i & 8) << 1);
            int r = (((c & 4) != 0 ? 2 : 0) | ((c & 0x10) != 0 ? 1 : 0)) * 21;
            int g = (((c & 2) != 0 ? 2 : 0) | ((c & 0x10) != 0 ? 1 : 0)) * 21;
            int b = (((c & 1) != 0 ? 2 : 0) | ((c & 0x10) != 0 ? 1 : 0)) * 21;
            if ((c & 0x17) == 6) g >>= 1;
            p[i] = unchecked((int)0xff000000) | (((r << 2) | (r >> 4)) << 16) | (((g << 2) | (g >> 4)) << 8) | ((b << 2) | (b >> 4));
        }
        return p;
    }

    private readonly Vga _v;
    private readonly byte[] _crtc = new byte[32];
    private byte _crtcReg, _mode = 0x29, _col = 0x30;
    private int _blink;
    private readonly List<(int line, int idx, byte val)> _snow = new();
    private readonly byte[] _charBuffer = new byte[256];

    public PcemCga(Vga v) { _v = v; Reset(); }
    public string Id => "PCEM_CGA";
    public bool TakesCgaModes => true;

    public void Reset()
    {
        Array.Clear(_crtc); Array.Copy(T80, _crtc, 16);
        _crtcReg = 0; _mode = 0x29; _col = 0x30; _blink = 0; _snow.Clear();
    }

    public void ModeSet(int mode)
    {
        byte[] t; byte m, c = 0x30;
        switch (mode)
        {
            // modes 0 and 1 are laid out 80 columns wide by this machine's BIOS, so the card is set the same way
            case 0: case 2: t = T80; m = 0x2d; break;
            case 1: case 3: t = T80; m = 0x29; break;
            case 4: t = TGfx; m = 0x2a; break;
            case 5: t = TGfx; m = 0x2e; break;
            case 6: t = TGfx; m = 0x1e; c = 0x3f; break;
            default: return;   // a VGA mode: the CGA keeps what it had
        }
        Array.Clear(_crtc); Array.Copy(t, _crtc, 16); _mode = m; _col = c;
    }

    public void NoteOut(int port, byte val)
    {
        switch (port)
        {
            case 0x3d4: _crtcReg = (byte)(val & 31); break;
            case 0x3d5: if (_crtcReg < 12) _crtc[_crtcReg] = (byte)(val & CrtcMask[_crtcReg]); break;   // 12-15 are the ones the card shares with the BIOS
            case 0x3d8: _mode = val; break;
            case 0x3d9: _col = val; break;
        }
    }

    public void NoteWrite(uint addr, byte val) { if (addr >= 0xb8000) Snow(val); }
    public void NoteRead(uint addr) { if (addr >= 0xb8000) Snow(Vram((int)(addr - 0xb8000))); }

    private int TotalLines => (_crtc[4] + 1) * (_crtc[9] + 1) + _crtc[5];
    private int DisplayedLines => Math.Min(_crtc[6], _crtc[4] + 1) * (_crtc[9] + 1);

    /// <summary>cga_write / cga_read: the bus byte lands in the line buffer at the beam's place, while the line is displayed (80-column text only shows it).</summary>
    private void Snow(byte val)
    {
        if ((_mode & 1) == 0) return;
        double f = (_v.PitNow % Vga.FrameTicks) / Vga.FrameTicks * TotalLines;
        int line = (int)f;
        if (line >= DisplayedLines) return;
        double chars = (f - line) * (_crtc[0] + 1);
        if (chars >= _crtc[1]) return;   // horizontal blank: the line buffer is refilled before the next line
        int idx = (int)(chars * 2) & 0xfc;
        _snow.Add((line, idx, val));
        _snow.Add((line, idx | 1, val));
        if (_snow.Count > 1 << 16) _snow.RemoveRange(0, 2);
    }

    public void Poll() { }

    private byte Vram(int b) => _v.Ram[VgaPictures.OddEven(b & 0x3fff)];

    public void Render()
    {
        var v = _v;
        bool hi = (_mode & 1) != 0;
        int cols = _crtc[1];
        int lineW = (hi ? cols << 3 : cols << 4) + 16;
        int dispLines = DisplayedLines;
        int h = (dispLines + 8) * 2;
        v.Ensure(lineW, h);
        int w = v.Width; h = v.Height;
        var line = new int[Math.Max(w, lineW)];
        int border = (_mode & 0x12) == 0x12 ? 0 : (_col & 15) + 16;
        int ma = ((v.Cr[0x0d] | (v.Cr[0x0c] << 8)) & 0x3fff);
        int ca = (v.Cr[0x0f] | (v.Cr[0x0e] << 8)) & 0x3fff;
        bool cursorOn = (_crtc[10] & 0x60) != 0x20 && (_blink & 8) != 0;
        int cs = _crtc[10] & 31, ce = _crtc[11] & 31;
        int snowAt = 0;
        // (the events are in time order, so in line order: the beam only moves down within a frame)
        var cbuf = _charBuffer;
        int y = 0;
        void Emit(int[] src) { for (int k = 0; k < 2 && y < h; k++, y++) for (int x = 0; x < w; x++) v.Frame[y * w + x] = x < lineW ? Color(src[x]) : unchecked((int)0xff000000); }
        Array.Fill(line, border);
        for (int i = 0; i < 4; i++) Emit(line);
        for (int vc = 0, displine = 0; vc < Math.Min(_crtc[6], _crtc[4] + 1); vc++)
        {
            for (int sc = 0; sc <= _crtc[9]; sc++, displine++)
            {
                Array.Fill(line, border);
                int m = ma;
                bool con = sc >= cs && sc <= ce;
                if (hi)
                {
                    for (int x = 0; x < cols << 1 && x < cbuf.Length; x++) cbuf[x] = Vram((m << 1) + x);
                    while (snowAt < _snow.Count && _snow[snowAt].line < displine) snowAt++;
                    while (snowAt < _snow.Count && _snow[snowAt].line == displine) { var e = _snow[snowAt++]; if (e.idx < cbuf.Length) cbuf[e.idx] = e.val; }
                    for (int x = 0; x < cols; x++)
                    {
                        int chr = cbuf[x << 1], attr = cbuf[(x << 1) + 1];
                        bool dc = m == ca && con && cursorOn;
                        int c1 = (attr & 15) + 16, c0;
                        if ((_mode & 0x20) != 0) { c0 = ((attr >> 4) & 7) + 16; if ((_blink & 8) != 0 && (attr & 0x80) != 0) c1 = c0; }
                        else c0 = (attr >> 4) + 16;
                        int bits = Font8[chr * 8 + (sc & 7)];
                        for (int c = 0; c < 8; c++) { int k = (bits & (1 << (c ^ 7))) != 0 ? c1 : c0; line[(x << 3) + c + 8] = dc ? k ^ 15 : k; }
                        m++;
                    }
                }
                else if ((_mode & 2) == 0)
                {
                    for (int x = 0; x < cols; x++)
                    {
                        int chr = Vram(m << 1), attr = Vram((m << 1) + 1);
                        bool dc = m == ca && con && cursorOn;
                        int c1 = (attr & 15) + 16, c0;
                        if ((_mode & 0x20) != 0) { c0 = ((attr >> 4) & 7) + 16; if ((_blink & 8) != 0 && (attr & 0x80) != 0) c1 = c0; }
                        else c0 = (attr >> 4) + 16;
                        m++;
                        int bits = Font8[chr * 8 + (sc & 7)];
                        for (int c = 0; c < 8; c++) { int k = (bits & (1 << (c ^ 7))) != 0 ? c1 : c0; if (dc) k ^= 15; line[(x << 4) + (c << 1) + 8] = line[(x << 4) + (c << 1) + 9] = k; }
                    }
                }
                else if ((_mode & 16) == 0)
                {
                    int[] pc = new int[4];
                    pc[0] = (_col & 15) | 16;
                    int col = (_col & 16) != 0 ? 24 : 16;
                    if ((_mode & 4) != 0) { pc[1] = col | 3; pc[2] = col | 4; pc[3] = col | 7; }
                    else if ((_col & 32) != 0) { pc[1] = col | 3; pc[2] = col | 5; pc[3] = col | 7; }
                    else { pc[1] = col | 2; pc[2] = col | 4; pc[3] = col | 6; }
                    for (int x = 0; x < cols; x++)
                    {
                        int dat = (Vram(((m << 1) & 0x1fff) + (sc & 1) * 0x2000) << 8) | Vram(((m << 1) & 0x1fff) + (sc & 1) * 0x2000 + 1);
                        m++;
                        for (int c = 0; c < 8; c++) { line[(x << 4) + (c << 1) + 8] = line[(x << 4) + (c << 1) + 9] = pc[(dat >> 14) & 3]; dat <<= 2; }
                    }
                }
                else
                {
                    int f1 = (_col & 15) + 16;
                    for (int x = 0; x < cols; x++)
                    {
                        int dat = (Vram(((m << 1) & 0x1fff) + (sc & 1) * 0x2000) << 8) | Vram(((m << 1) & 0x1fff) + (sc & 1) * 0x2000 + 1);
                        m++;
                        for (int c = 0; c < 16; c++) { line[(x << 4) + c + 8] = (dat & 0x8000) != 0 ? f1 : 0; dat <<= 1; }
                    }
                }
                Emit(line);
            }
            ma += cols;
        }
        Array.Fill(line, border);
        while (y < h) Emit(line);
        _blink++;
        _snow.Clear();
    }

    private static int Color(int i) => i >= 16 && i < 32 ? Rgbi[i - 16] : Rgbi[0];

    // ---- state ----

    public void Save(BinaryWriter w)
    {
        w.Write(_crtc); w.Write(_crtcReg); w.Write(_mode); w.Write(_col); w.Write(_blink);
        w.Write(_snow.Count);
        foreach (var (line, idx, val) in _snow) { w.Write(line); w.Write(idx); w.Write(val); }
    }

    public void Load(BinaryReader r)
    {
        r.Read(_crtc, 0, 32); _crtcReg = r.ReadByte(); _mode = r.ReadByte(); _col = r.ReadByte(); _blink = r.ReadInt32();
        _snow.Clear();
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++) _snow.Add((r.ReadInt32(), r.ReadInt32(), r.ReadByte()));
    }
}
