// DOSBOX072: the picture of DOSBox 0.72 (src/hardware/vga_draw.cpp: VGA_VerticalTimer, VGA_DrawPart, VGA_SetupDrawing, VGA_CheckScanLength, the line
// drawers VGA_Draw_Linear_Line / VGA_TEXT_Draw_Line; the panning rule of vga_attr.cpp), DOSBox 0.72 (2007-08-26, sourceforge.net/projects/dosbox),
// GPL-2.0-or-later, Copyright (C) 2002-2007 The DOSBox Team. Ported to C# for BrokenNes as an art instrument, flaws kept. See x86/THIRD_PARTY_NOTICES.md.
//
// How it draws (as 0.72 does): at the top of each frame the vertical timer takes the start address latched at the last retrace and the split line, then
// the frame is drawn in VGA_PARTS = 4 bands, each band all at once when the beam reaches its end (a quarter, a half, three quarters, all of the display).
// Whatever the program changes in video memory between two bands shows in the lower ones only, so a picture redrawn during the display tears on band
// edges, not on the beam's line. The palette is the frame's (taken when the frame is shown). Text is always 8 dots a cell (9-dot modes squeeze), the
// cursor blinks every 8 frames and the characters every 16; the line counter takes only the low 4 bits of the maximum scan line. The pel panning is the
// value worked out when the register was written, for the mode then (so it goes stale across a mode change), and halved in 256-colour modes.
// Left out: the S3 / VESA modes, the hardware mouse cursor, the Tandy / Hercules / CGA machines (this card is a VGA), the render scalers (lines and dots
// are doubled plainly).

using System;
using System.IO;

namespace NesEmulator.X86;

public sealed class Dosbox072Vga : IVgaPicture
{
    private const int Parts = 4, MaxW = 1024, MaxH = 768;
    private const int MText = 0, MEga = 1, MVga = 2, MCga4 = 3;
    private readonly Vga _v;
    private byte[] _buf = new byte[640 * 400];
    private int _w = 640, _h = 400;                 // the drawn picture (before doubling)
    private bool _dw, _dh;
    private int _mode = MText;
    // vga.draw
    private int _address, _addressLine, _addressLineTotal = 16, _addressAdd = 160, _linesTotal = 400, _linesDone, _splitLine, _partsLeft, _partsLines, _blocks = 80;
    private int _cursorAddress, _cursorCount;
    private bool _fontMask1;
    private long _frame = -1;
    private int _nextPartLine;
    private int _linearMask = 0x3ffff;
    // vga.config
    private int _realStart, _pelPanning;

    public Dosbox072Vga(Vga v) { _v = v; Reset(); }
    public string Id => "DOSBOX072";
    public bool TakesCgaModes => false;

    public void Reset()
    {
        _buf = new byte[640 * 400]; _w = 640; _h = 400; _dw = _dh = false; _mode = MText;
        _address = _addressLine = _linesDone = _splitLine = _partsLeft = _partsLines = 0;
        _addressLineTotal = 16; _addressAdd = 160; _linesTotal = 400; _blocks = 80;
        _cursorAddress = _cursorCount = 0; _fontMask1 = true; _frame = -1; _nextPartLine = 0; _linearMask = 0x3ffff;
        _realStart = (_v.Cr[0x0c] << 8) | _v.Cr[0x0d]; _pelPanning = 0;
    }

    private int DetermineMode()
    {
        var v = _v;
        if ((v.Gr[6] & 1) == 0) return MText;
        int shift = (v.Gr[5] >> 5) & 3;
        if ((shift & 2) != 0) return MVga;
        if (shift == 1) return MCga4;
        return MEga;
    }

    public void NoteOut(int port, byte val)
    {
        // vga_attr.cpp: the pel panning is worked out when register 13h is written, for the mode at that moment
        if (port == 0x3c0 && _v.ArFlipFlop == 1 && (_v.ArIndex & 0x1f) == 0x13)
        {
            int p = val & 0xf;
            switch (DetermineMode())
            {
                case MText: _pelPanning = p > 7 ? 0 : p + 1; break;   // (0.72: "if (val==7) 7; if (val>7) 0; else val+1", so 7 ends as 8)
                case MVga: _pelPanning = (p & 7) / 2; break;
                default: _pelPanning = p & 7; break;
            }
        }
    }

    public void NoteWrite(uint addr, byte v) { }
    public void NoteRead(uint addr) { }
    public void ModeSet(int mode) { }

    private int Line()
    {
        long now = _v.PitNow;
        double pos = now % Vga.FrameTicks;
        return (int)(pos * 449.0 / Vga.FrameTicks);
    }

    public void Poll()
    {
        long now = _v.PitNow;
        long frame = (long)(now / Vga.FrameTicks);   // a frame's display starts at line 0 of the 449
        if (frame != _frame) { _frame = frame; VerticalTimer(); }
        int line = Line();
        while (_partsLeft > 0 && line >= _nextPartLine) DrawPartEvent();
    }

    private void DrawPartEvent()
    {
        DrawPart(_partsLeft != 1 ? _partsLines : _linesTotal - _linesDone);
        _partsLeft--;
        _nextPartLine += 400 / Parts;
    }

    public void Render()
    {
        // the retrace (VGA_VerticalDisplayEnd): the start address the next frame will use
        _realStart = (_v.Cr[0x0c] << 8) | _v.Cr[0x0d];
        while (_partsLeft > 0) DrawPartEvent();   // (only after a load or a mode change mid-frame)
        var v = _v;
        int ow = _w * (_dw ? 2 : 1), oh = _h * (_dh ? 2 : 1);
        v.Ensure(ow, oh);
        ow = v.Width; oh = v.Height;
        var pal = new int[256];
        if (_mode == MVga) for (int i = 0; i < 256; i++) pal[i] = v.Dac(i);
        else
            for (int i = 0; i < 16; i++)
            {
                int c = v.Ar[i];
                c = (v.Ar[0x10] & 0x80) != 0 ? ((v.Ar[0x14] & 0xf) << 4) | (c & 0xf) : ((v.Ar[0x14] & 0xc) << 4) | (c & 0x3f);
                pal[i] = v.Dac(c);
            }
        for (int y = 0; y < oh; y++)
        {
            int sy = _dh ? y >> 1 : y;
            for (int x = 0; x < ow; x++)
            {
                int sx = _dw ? x >> 1 : x;
                v.Frame[y * ow + x] = sy < _h && sx < _w ? pal[_buf[sy * _w + sx]] : unchecked((int)0xff000000);
            }
        }
    }

    // ---- VGA_SetupDrawing (taken at every frame start: 0.72 redoes it on every register change that matters) ----

    private void Setup()
    {
        var v = _v; var cr = v.Cr;
        int hdend = 1 + cr[1];
        int vdend = 1 + (cr[0x12] | ((cr[7] & 2) << 7) | ((cr[7] & 0x40) << 3));
        int hbstart = cr[2];
        int vbstart = cr[0x15] | ((cr[7] & 0x08) << 5) | ((cr[9] & 0x20) << 4);
        _addressLineTotal = (cr[9] & 0xf) + 1;
        bool doubleScan = (cr[9] & 0x80) != 0;
        if (hbstart < hdend) hdend = hbstart;
        if (vbstart < vdend) vdend = vbstart;
        int width = hdend, height = vdend;
        bool dh = false, dw = false;
        _mode = DetermineMode();
        _linearMask = 0x3ffff;
        switch (_mode)
        {
            case MVga: dw = true; width <<= 2; break;
            case MEga: dw = (v.Sr[1] & 8) != 0; _blocks = width; width <<= 3; break;
            case MCga4: dw = true; _blocks = width * 2; width <<= 3; break;
            default: _blocks = width; dw = (v.Sr[1] & 8) != 0; width <<= 3; break;
        }
        int scanLen = cr[0x13];
        _addressAdd = _mode switch { MEga => scanLen * 16, MVga => scanLen * 8, MCga4 => 80, _ => scanLen * 4 };
        if (doubleScan) { height /= 2; dh = true; }
        if (!dh && _mode != MText && (_addressLineTotal & 1) == 0) { _addressLineTotal /= 2; dh = true; height /= 2; }
        width = Math.Clamp(width, 8, MaxW); height = Math.Clamp(height, 8, MaxH);
        _linesTotal = height;
        _partsLines = _linesTotal / Parts;
        if (width != _w || height != _h) { _w = width; _h = height; _buf = new byte[_w * _h]; }
        _dw = dw; _dh = dh;
    }

    private void VerticalTimer()
    {
        var v = _v;
        Setup();
        // an unfinished frame is dropped (the parts still queued are removed)
        _partsLeft = Parts;
        _linesDone = 0;
        _nextPartLine = 400 / Parts;
        _address = _realStart;
        _addressLine = v.Cr[8] & 31;   // hlines_skip
        int lineCompare = v.Cr[0x18] | ((v.Cr[7] & 0x10) << 4) | ((v.Cr[9] & 0x40) << 3);
        _splitLine = lineCompare / (_dh ? 2 : 1);
        switch (_mode)
        {
            case MEga: _address = _address * 8 + _pelPanning; break;
            case MVga:
                if ((v.Sr[4] & 8) != 0 && (v.Cr[0x14] & 0x40) != 0) _linearMask = 0xffff;   // compatible chain 4: the 64 KB linear copy
                _address = _address * 4 + _pelPanning;
                break;
            case MCga4: _address = (_address * 2) & 0x1fff; break;
            default:
                _address = _realStart * 2;
                _cursorAddress = ((v.Cr[0x0e] << 8) | v.Cr[0x0f]) * 2;
                _cursorCount++;
                bool blinking = (v.Ar[0x10] & 8) != 0;
                _fontMask1 = !(blinking && ((_cursorCount >> 4) & 1) != 0);
                break;
        }
    }

    private void DrawPart(int lines)
    {
        var v = _v;
        while (lines-- > 0)
        {
            if (_linesDone < _h) DrawLine(_address, _addressLine, _linesDone);
            _addressLine++;
            if (_addressLine >= _addressLineTotal) { _addressLine = 0; _address += _addressAdd; }
            _linesDone++;
            if (_splitLine == _linesDone)
            {
                _address = 0;
                if ((v.Ar[0x10] & 0x20) == 0) _address += _pelPanning;
                _addressLine = 0;
            }
        }
    }

    private void DrawLine(int vidstart, int line, int y)
    {
        var v = _v; var ram = v.Ram;
        int o = y * _w;
        switch (_mode)
        {
            case MVga:
                for (int x = 0; x < _w; x++) _buf[o + x] = ram[(vidstart + x) & _linearMask & (Vga.RamSize - 1)];
                break;
            case MEga:
                for (int x = 0; x < _w; x++)
                {   // the 8-bit cache: pixel p is bit 7 - (p & 7) of the four planes at address p >> 3 (mask 512 KB - 1)
                    int p = (vidstart + x) & (512 * 1024 - 1);
                    int a = ((p >> 3) & 0xffff) * 4, bit = 7 - (p & 7);
                    _buf[o + x] = (byte)(((ram[a] >> bit) & 1) | (((ram[a + 1] >> bit) & 1) << 1) | (((ram[a + 2] >> bit) & 1) << 2) | (((ram[a + 3] >> bit) & 1) << 3));
                }
                break;
            case MCga4:
                Array.Clear(_buf, o, _w);
                break;
            default:
                DrawText(vidstart, line, o);
                break;
        }
    }

    private void DrawText(int vidstart, int line, int o)
    {
        var v = _v; var ram = v.Ram;
        int mapA = ((v.Sr[3] >> 2) & 3) | ((v.Sr[3] >> 3) & 4), mapB = (v.Sr[3] & 3) | ((v.Sr[3] >> 2) & 4);
        int baseA = FontBase(mapA), baseB = FontBase(mapB);
        bool blinking = (v.Ar[0x10] & 8) != 0;
        for (int cx = 0; cx < _blocks && cx * 8 + 7 < _w; cx++)
        {
            int chr = Cpu(vidstart + cx * 2), col = Cpu(vidstart + cx * 2 + 1);
            int fb = ((col >> 3) & 1) != 0 ? baseB : baseA;
            int font = line < 32 ? ram[((fb + chr * 32 + line) & 0xffff) * 4 + 2] : 0;
            if ((col >> 7) != 0 && !_fontMask1) font = 0;
            int fg = col & 0xf, bg = col >> 4;
            if (blinking) bg &= 7;   // TXT_BG_Table[8..15] = 0..7 while blinking
            for (int x = 0; x < 8; x++) _buf[o + cx * 8 + x] = (byte)((font & (0x80 >> x)) != 0 ? fg : bg);
        }
        bool cursorOn = (v.Cr[0x0a] & 0x20) == 0;
        if (!cursorOn || (_cursorCount & 0x8) == 0) return;
        int fa = (_cursorAddress - vidstart) >> 1;
        if (fa >= 0 && fa < _blocks && fa * 8 + 7 < _w)
        {
            if (line < (v.Cr[0x0a] & 0x1f) || line > (v.Cr[0x0b] & 0x1f)) return;
            int att = Cpu(_cursorAddress + 1) & 0xf;
            for (int x = 0; x < 8; x++) _buf[o + fa * 8 + x] = (byte)att;
        }
    }

    private static int FontBase(int map) => map switch { 0 => 0, 1 => 0x4000, 2 => 0x8000, 3 => 0xc000, 4 => 0x2000, 5 => 0x6000, 6 => 0xa000, _ => 0xe000 };
    private byte Cpu(int b) => _v.Ram[VgaPictures.OddEven(b & 0xffff)];

    // ---- state ----

    public void Save(BinaryWriter w)
    {
        w.Write(_w); w.Write(_h); w.Write(_buf); w.Write(_dw); w.Write(_dh); w.Write(_mode);
        w.Write(_address); w.Write(_addressLine); w.Write(_addressLineTotal); w.Write(_addressAdd); w.Write(_linesTotal); w.Write(_linesDone); w.Write(_splitLine);
        w.Write(_partsLeft); w.Write(_partsLines); w.Write(_blocks); w.Write(_cursorAddress); w.Write(_cursorCount); w.Write(_fontMask1);
        w.Write(_frame); w.Write(_nextPartLine); w.Write(_linearMask); w.Write(_realStart); w.Write(_pelPanning);
    }

    public void Load(BinaryReader r)
    {
        _w = r.ReadInt32(); _h = r.ReadInt32(); _buf = r.ReadBytes(_w * _h); _dw = r.ReadBoolean(); _dh = r.ReadBoolean(); _mode = r.ReadInt32();
        _address = r.ReadInt32(); _addressLine = r.ReadInt32(); _addressLineTotal = r.ReadInt32(); _addressAdd = r.ReadInt32(); _linesTotal = r.ReadInt32(); _linesDone = r.ReadInt32(); _splitLine = r.ReadInt32();
        _partsLeft = r.ReadInt32(); _partsLines = r.ReadInt32(); _blocks = r.ReadInt32(); _cursorAddress = r.ReadInt32(); _cursorCount = r.ReadInt32(); _fontMask1 = r.ReadBoolean();
        _frame = r.ReadInt64(); _nextPartLine = r.ReadInt32(); _linearMask = r.ReadInt32(); _realStart = r.ReadInt32(); _pelPanning = r.ReadInt32();
    }
}
