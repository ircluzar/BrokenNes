// BOCHS20: the picture of Bochs 2.0's VGA (iodev/vga.cc, bx_vga_c::update + the tile marking in mem_write, determine_screen_dimensions; the text
// drawing of gui/win32.cc text_update), Bochs 2.0 (2002-12-21, bochs.sourceforge.io), LGPL-2.1, Copyright (C) 2002 MandrakeSoft S.A. Ported to C# for
// BrokenNes as an art instrument, flaws kept. See x86/THIRD_PARTY_NOTICES.md.
//
// How it draws (as Bochs 2.0 does): the window is a 640x480 picture of DAC indices that only changes where the update finds work. update() runs on its
// own timer, every 30,000 us of machine time (the 2.0 default vga_update_interval: 33 updates a second, so every other 70 Hz frame shows the same picture),
// and does nothing unless something marked the memory updated. Graphics are redrawn in 16x24 tiles, and only the tiles a memory write marked; a register-only
// change (start address, panning, line compare) marks nothing, so the picture stays as it was until the tiles are written again. Mode 12h ignores the
// panning, the split screen and the line offset (it assumes 640 dots a line); mode 13h ignores the start address altogether; Mode X redraws every tile on
// every update. Text compares the text with the snapshot of the last update and redraws only the cells that differ: 8 dots a cell, the attribute's nibbles
// straight into the DAC (no attribute controller: no blink, bit 7 is a bright background), a solid cursor drawn in Bochs's own font.
// Left out: VBE, the BX_PANIC paths (shift register 3 draws nothing), the Win32 GDI's colour matching (the window keeps DAC indices).

using System;
using System.IO;

namespace NesEmulator.X86;

public sealed class Bochs20Vga : IVgaPicture
{
    private const int MaxW = 640, MaxH = 480, TileW = 16, TileH = 24, TilesX = MaxW / TileW, TilesY = MaxH / TileH;
    private const long Interval = 30000;   // vga_update_interval, us
    private readonly Vga _v;
    private readonly byte[] _screen = new byte[MaxW * MaxH];          // the GUI window: DAC indices
    private readonly bool[] _tile = new bool[TilesX * TilesY];
    private readonly byte[] _snapshot = new byte[2 * 80 * 100];        // text_snapshot (BX_MAX_TEXT_LINES 100)
    private readonly byte[] _font = new byte[256 * 32];                 // the GUI's font (vga_charmap), compared with font RAM each update
    private bool _memUpdated, _fontLoaded;
    private int _oldW = 640, _oldH = 400, _oldMsl = 15, _scanBits = 640, _prevCx, _prevCy;
    private readonly byte[] _crShadow = new byte[6];                    // CRTC 0A-0F as the port writes left them (our BIOS writes the registers directly)
    private long _nextUpdate = -1;

    public Bochs20Vga(Vga v) { _v = v; Reset(); }
    public string Id => "BOCHS20";
    public bool TakesCgaModes => false;

    public void Reset()
    {
        Array.Clear(_screen); Array.Clear(_snapshot); Array.Clear(_font);
        for (int i = 0; i < _tile.Length; i++) _tile[i] = false;
        _memUpdated = false; _fontLoaded = false;
        _oldW = 640; _oldH = 400; _oldMsl = 15; _scanBits = 640; _prevCx = _prevCy = 0;
        Array.Copy(_v.Cr, 0x0a, _crShadow, 0, 6);
        _nextUpdate = -1;
    }

    private void MarkTile(int xt, int yt) { if (xt < TilesX && yt < TilesY && xt >= 0 && yt >= 0) _tile[yt * TilesX + xt] = true; }
    private void MarkAll() { for (int i = 0; i < _tile.Length; i++) _tile[i] = true; }

    // ---- what marks the memory updated (vga.cc write_handler / mem_write) ----

    public void NoteOut(int port, byte v)
    {
        switch (port)
        {
            case 0x3c0:
                // attribute mode control / palette: a video-enable transition clears the snapshot and marks every tile
                if (_v.ArFlipFlop == 0 && (v & 0x20) != 0 && (_v.ArIndex & 0x20) == 0) { Array.Clear(_snapshot); _memUpdated = true; MarkAll(); }
                break;
            case 0x3c9:
                // palette_change: the window takes the new colour and everything is marked for redraw
                if (_v.DacSub == 2) { _memUpdated = true; MarkAll(); }
                break;
            case 0x3cf:
                if (_v.GrIndex == 6 && ((v >> 2) & 3) != ((_v.Gr[6] >> 2) & 3)) _memUpdated = true;   // memory_mapping changed
                break;
            case 0x3b5: case 0x3d5:
                if (_v.CrIndex >= 0x0a && _v.CrIndex <= 0x0f) _memUpdated = true;               // start address or cursor: updated, but no tile is marked
                break;
        }
    }

    public void NoteRead(uint addr) { }

    public void NoteWrite(uint addr, byte val)
    {
        var v = _v;
        bool graphics = (v.Gr[6] & 1) != 0;
        int mapping = (v.Gr[6] >> 2) & 3;
        if (graphics)
        {
            if (mapping == 3)
            {   // CGA 320x200x4 at B8000 (the 2.0 code, its odd-line slip included)
                if (addr < 0xb8000 || addr > 0xbffff) return;
                int offset = (int)(addr - 0xb8000), xt, yt;
                if (offset >= 0x2000) { yt = ((offset - 0x2000) / 80 << 1) + 1; xt = ((offset - 0x2000) % 80) << 2; }
                else { yt = offset << 1; xt = (offset % 80) << 2; }
                _memUpdated = true;
                MarkTile(xt / TileW, yt / TileH);
                return;
            }
            if ((v.Sr[4] & 8) != 0)
            {   // chain four: 320 x 200, tile from the plain offset
                int offset = (int)(addr - 0xa0000);
                _memUpdated = true;
                MarkTile(offset % 320 / TileW, offset / 320 / TileH);
                return;
            }
        }
        else
        {
            _memUpdated = true;   // text: the update compares with the snapshot
            return;
        }
        // planar: offset within the mapped window, tiles from scan_bits (640 dots a line unless determine_screen_dimensions said otherwise)
        uint off = addr;
        switch (mapping)
        {
            case 1: if (off > 0xaffff) return; off -= 0xa0000; break;
            case 2: if (off < 0xb0000 || off > 0xb7fff) return; off -= 0xb0000; break;
            case 3: if (off < 0xb8000) return; off -= 0xb8000; break;
            default: off -= 0xa0000; break;
        }
        if ((v.Sr[2] & 0x0f) == 0) return;
        _memUpdated = true;
        int bytesPerLine = _scanBits / 8;
        if (bytesPerLine <= 0) return;
        MarkTile((int)(off % (uint)bytesPerLine) / (TileW / 8), (int)(off / (uint)bytesPerLine) / TileH);
    }

    public void ModeSet(int mode)
    {   // the Bochs BIOS clears the screen through the bus: every cell / tile it writes is marked
        _memUpdated = true; MarkAll();
    }

    public void Poll()
    {
        long now = _v.PitNow;
        long period = Interval * Pit8254.Freq / 1_000_000;
        if (_nextUpdate < 0) _nextUpdate = now + period;
        while (now >= _nextUpdate) { Update(); _nextUpdate += period; }
    }

    public void Render()
    {
        var v = _v;
        int w = Math.Min(_oldW, MaxW), h = Math.Min(_oldH, MaxH);
        v.Ensure(w, h);
        w = v.Width; h = v.Height;
        var pal = new int[256];
        for (int i = 0; i < 256; i++) pal[i] = v.Dac(i);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                v.Frame[y * w + x] = y < MaxH && x < MaxW ? pal[_screen[y * MaxW + x]] : unchecked((int)0xff000000);
    }

    // ---- bx_vga_c::update ----

    private byte Plane(int idx) => idx >= 0 && idx < 4 * 65536 ? _v.Ram[(idx & 0xffff) * 4 + (idx >> 16)] : (byte)0;   // vga_memory[p * 65536 + off]
    private byte CpuByte(int b) => _v.Ram[VgaPictures.OddEven(b & 0xffff) & (Vga.RamSize - 1)];

    private void Dimensions(out int h, out int w)
    {
        var cr = _v.Cr;
        _scanBits = 640;
        int hh = (cr[1] + 1) * 8;
        int vv = (cr[18] | ((cr[7] & 0x02) << 7) | ((cr[7] & 0x40) << 3)) + 1;
        if ((cr[9] & 0x9f) > 0) vv >>= 1;
        int shift = (_v.Gr[5] >> 5) & 3;
        if (shift == 0)
        {
            w = 640; h = 480;
            if (cr[6] == 0xbf)
            {
                if (cr[23] == 0xa3 && cr[20] == 0x40 && cr[9] == 0x41) { w = 320; h = 240; }
                if (cr[23] == 0xe3 && cr[20] == 0x0f && cr[9] == 0x40) { w = 640; h = 350; }
                if (cr[23] == 0xe3 && cr[20] == 0 && cr[9] == 0xc0) { _scanBits = cr[19] << 4; w = hh; h = vv; }
            }
        }
        else if (shift == 2)
        {
            if ((_v.Sr[4] & 8) != 0) { w = 320; h = 200; }
            else { w = hh / 2; h = vv; }
        }
        else { w = hh; h = vv; }
    }

    private void Update()
    {
        var v = _v;
        if ((v.Sr[0] & 3) != 3) return;   // sequencer in reset
        for (int i = 0; i < 6; i++) if (_crShadow[i] != v.Cr[0x0a + i]) { _crShadow[i] = v.Cr[0x0a + i]; _memUpdated = true; }
        if (!_memUpdated) return;
        if ((v.Gr[6] & 1) != 0) UpdateGraphics(); else UpdateText();
    }

    private void Dim(int w, int h) { _oldW = w; _oldH = h; }

    private void UpdateGraphics()
    {
        var v = _v;
        int shift = (v.Gr[5] >> 5) & 3;
        int w, h;
        switch (shift)
        {
            case 0:
            {
                Dimensions(out h, out w);
                Dim(w, h);
                int start = (v.Cr[0x0c] << 8) | v.Cr[0x0d];
                int yTiles = h / TileH + (h % TileH > 0 ? 1 : 0);
                for (int yt = 0; yt < yTiles; yt++)
                    for (int xt = 0; xt < w / TileW; xt++)
                    {
                        if (!Tile(xt, yt)) continue;
                        for (int r = 0; r < TileH; r++)
                            for (int c = 0; c < TileW; c++)
                            {
                                int bit = 7 - (c % 8);
                                int off = start + (xt * TileW + c) / 8 + (yt * TileH + r) * (_scanBits / 8);
                                int attr = ((Plane(off) >> bit) & 1) | (((Plane(65536 + off) >> bit) & 1) << 1) | (((Plane(2 * 65536 + off) >> bit) & 1) << 2) | (((Plane(3 * 65536 + off) >> bit) & 1) << 3);
                                attr &= v.Ar[0x12];
                                int pr = v.Ar[attr];
                                int dac = (v.Ar[0x10] & 0x80) != 0 ? (pr & 0x0f) | (v.Ar[0x14] << 4) : (pr & 0x3f) | ((v.Ar[0x14] & 0x0c) << 4);
                                Put(xt * TileW + c, yt * TileH + r, (byte)dac);
                            }
                        ClearTile(xt, yt);
                    }
                break;
            }
            case 1:
            {
                w = 320; h = 200; Dim(w, h);
                int yTiles = h / TileH + (h % TileH > 0 ? 1 : 0);
                for (int yt = 0; yt < yTiles; yt++)
                    for (int xt = 0; xt < w / TileW; xt++)
                    {
                        if (!Tile(xt, yt)) continue;
                        for (int r = 0; r < TileH; r++)
                            for (int c = 0; c < TileW; c++)
                            {
                                int y = yt * TileH + r, x = xt * TileW + c;
                                int off = ((y & 1) << 13) + 80 * (y / 2) + x / 4;
                                int pr = (Plane(off) >> (6 - 2 * (x % 4))) & 3;
                                pr |= ((v.Ar[0x10] >> 2) & 1) << 2;
                                Put(x, y, v.Ar[pr]);
                            }
                        ClearTile(xt, yt);
                    }
                break;
            }
            case 2:
            {
                Dimensions(out h, out w);
                Dim(w, h);
                int yTiles = h / TileH + (h % TileH > 0 ? 1 : 0);
                if ((v.Sr[4] & 8) != 0)
                {
                    for (int yt = 0; yt < yTiles; yt++)
                        for (int xt = 0; xt < w / TileW; xt++)
                        {
                            if (!Tile(xt, yt)) continue;
                            for (int r = 0; r < TileH; r++)
                                for (int c = 0; c < TileW; c++)
                                {
                                    int y = yt * TileH + r, x = xt * TileW + c;
                                    int off = y * 320 + x;   // plane x%4 at y*320 + (x & ~3): the chained byte, start address ignored
                                    Put(x, y, off < Vga.RamSize ? v.Ram[off] : (byte)0);
                                }
                            ClearTile(xt, yt);
                        }
                }
                else
                {   // Mode X: every tile, every update
                    int start = (v.Cr[0x0c] << 8) | v.Cr[0x0d];
                    for (int yt = 0; yt < yTiles; yt++)
                        for (int xt = 0; xt < w / TileW; xt++)
                        {
                            for (int r = 0; r < TileH; r++)
                                for (int c = 0; c < TileW; c++)
                                {
                                    int y = yt * TileH + r, x = xt * TileW + c;
                                    int off = (x % 4) * 65536 + y * (v.Cr[0x13] << 1) + (x >> 2);
                                    Put(x, y, Plane(start + off));
                                }
                            ClearTile(xt, yt);
                        }
                }
                break;
            }
            default: break;   // BX_PANIC in 2.0
        }
        _memUpdated = false;
    }

    private bool Tile(int xt, int yt) => xt < TilesX && yt < TilesY && _tile[yt * TilesX + xt];
    private void ClearTile(int xt, int yt) { if (xt < TilesX && yt < TilesY) _tile[yt * TilesX + xt] = false; }
    private void Put(int x, int y, byte c) { if (x < MaxW && y < MaxH) _screen[y * MaxW + x] = c; }

    private void UpdateText()
    {
        var v = _v;
        int mapping = (v.Gr[6] >> 2) & 3;
        int start, rows, msl, w, h;
        if (mapping <= 2)
        {
            w = 640; h = 400; msl = 15; rows = 25;
            start = mapping == 2 ? 2 * ((v.Cr[12] << 8) + v.Cr[13]) : 0;
        }
        else
        {
            int vde = v.Cr[0x12] | ((v.Cr[7] << 7) & 0x100) | ((v.Cr[7] << 3) & 0x200);
            msl = v.Cr[9] & 0x1f;
            if (msl == 0) return;   // "character height = 1, skipping text update"
            rows = (vde + 1) / (msl + 1);
            if (rows > 100) rows = 100;
            w = 8 * (v.Cr[1] + 1); h = vde + 1;
            start = 2 * ((v.Cr[12] << 8) + v.Cr[13]);
        }
        Dim(w, h);
        int yChar = msl + 1;
        _oldMsl = msl;
        int ncols = Math.Min(w / 8, 80);
        int cursorAddr = 2 * ((v.Cr[0x0e] << 8) | v.Cr[0x0f]);
        int cx, cy;
        if (cursorAddr < start) { cx = 0xffff; cy = 0xffff; }
        else { cx = (cursorAddr - start) / 2 % (w / 8); cy = (cursorAddr - start) / 2 / (w / 8); }
        int csStart = v.Cr[0x0a] & 0x3f, csEnd = v.Cr[0x0b] & 0x1f;

        // the GUI's font: refreshed from font RAM (plane 2, map A) when it changed, which forces a full redraw
        int mapA = ((v.Sr[3] >> 2) & 3) | ((v.Sr[3] >> 3) & 4);
        int fontBase = mapA switch { 0 => 0, 1 => 0x4000, 2 => 0x8000, 3 => 0xc000, 4 => 0x2000, 5 => 0x6000, 6 => 0xa000, _ => 0xe000 };
        bool force = false;
        for (int i = 0; i < 256 * 32; i++)
        {
            byte f = v.Ram[(fontBase + i) * 4 + 2];
            if (_font[i] != f || !_fontLoaded) { _font[i] = f; force = true; }
        }
        _fontLoaded = true;

        int nchars = ncols * rows;
        var cur = new byte[2 * nchars];
        for (int i = 0; i < cur.Length; i++) cur[i] = CpuByte(start + i);
        // erase the old block cursor
        if (_prevCy * ncols + _prevCx < nchars) DrawCell(_prevCx, _prevCy, cur[(_prevCy * ncols + _prevCx) * 2], cur[(_prevCy * ncols + _prevCx) * 2 + 1], yChar, -1, -1, false);
        for (int i = 0; i < nchars * 2; i += 2)
            if (force || _snapshot[i] != cur[i] || _snapshot[i + 1] != cur[i + 1])
                DrawCell(i / 2 % ncols, i / 2 / ncols, cur[i], cur[i + 1], yChar, -1, -1, false);
        _prevCx = cx; _prevCy = cy;
        if (cy * ncols + cx < nchars && csStart <= csEnd)
            DrawCell(cx, cy, cur[(cy * ncols + cx) * 2], cur[(cy * ncols + cx) * 2 + 1], yChar, csStart, csEnd, true);
        Array.Copy(cur, _snapshot, Math.Min(cur.Length, _snapshot.Length));
        _memUpdated = false;
    }

    private void DrawCell(int cx, int cy, byte ch, byte attr, int yChar, int cs, int ce, bool cursor)
    {
        byte fg = (byte)(attr & 0x0f), bg = (byte)((attr >> 4) & 0x0f);
        for (int y = 0; y < yChar; y++)
        {
            int bits = y < 32 ? (cursor ? (y < 16 ? VgaFont.Font16[ch * 16 + y] : 0) : _font[ch * 32 + y]) : 0;   // the cursor cell is drawn from Bochs's own font
            if (cursor && y >= cs && y <= ce) bits = 255 - bits;
            for (int x = 0; x < 8; x++) Put(cx * 8 + x, cy * yChar + y, (bits & (0x80 >> x)) != 0 ? fg : bg);
        }
    }

    // ---- state ----

    public void Save(BinaryWriter w)
    {
        w.Write(_screen); w.Write(_snapshot); w.Write(_font);
        foreach (var t in _tile) w.Write(t);
        w.Write(_memUpdated); w.Write(_fontLoaded);
        w.Write(_oldW); w.Write(_oldH); w.Write(_oldMsl); w.Write(_scanBits); w.Write(_prevCx); w.Write(_prevCy);
        w.Write(_crShadow); w.Write(_nextUpdate);
    }

    public void Load(BinaryReader r)
    {
        r.Read(_screen, 0, _screen.Length); r.Read(_snapshot, 0, _snapshot.Length); r.Read(_font, 0, _font.Length);
        for (int i = 0; i < _tile.Length; i++) _tile[i] = r.ReadBoolean();
        _memUpdated = r.ReadBoolean(); _fontLoaded = r.ReadBoolean();
        _oldW = r.ReadInt32(); _oldH = r.ReadInt32(); _oldMsl = r.ReadInt32(); _scanBits = r.ReadInt32(); _prevCx = r.ReadInt32(); _prevCy = r.ReadInt32();
        r.Read(_crShadow, 0, 6); _nextUpdate = r.ReadInt64();
    }
}
