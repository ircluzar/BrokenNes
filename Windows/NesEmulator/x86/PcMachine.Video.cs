// The x86 console's BIOS: INT 10h, the video services (written for BrokenNes, see PcMachine.Bios.cs). Text modes 0-3 and 7 (all set
// up as the VGA's 80x25 colour text), mode 12h (640x480x16) and mode 13h (320x200x256); VESA is not offered.

using System;

namespace NesEmulator.X86;

public sealed partial class PcMachine
{
    private int VMode => Phys8(0x449);
    private bool TextMode => VMode is <= 3 or 7;
    private int Cols => Math.Max(1, (int)Phys16(0x44a));
    private int Rows => Phys8(0x484) + 1;
    private int ActivePage => Phys8(0x462);

    public void SetMode(int mode)
    {
        bool noClear = (mode & 0x80) != 0;
        mode &= 0x7f;
        int cols, rows, pageSize;
        // a CGA picture chip (VGA harvest) also takes the CGA graphics modes 4-6: the card keeps its text registers (B8000, odd/even = the CGA's linear 16 KB)
        bool cgaGfx = mode is 4 or 5 or 6 && Vga.Picture is { TakesCgaModes: true };
        if (cgaGfx) { Vga.SetRegs(Vga.Mode3); cols = mode == 6 ? 80 : 40; rows = 25; pageSize = 0x4000; }
        else if (mode is <= 3 or 7) { Vga.SetRegs(Vga.Mode3); cols = 80; rows = 25; pageSize = 0x1000; }
        else if (mode == 0x13) { Vga.SetRegs(Vga.Mode13); cols = 40; rows = 25; pageSize = 0; }
        else if (mode is 0x12 or 0x11) { Vga.SetRegs(Vga.Mode12); cols = 80; rows = 30; pageSize = 0; mode = 0x12; }
        else return;                                             // not offered: the mode stays as it was
        if (!_v86Video) Vga.S3?.LeaveEnhanced();
        if (mode == 0x13) Vga.LoadDefaultDac256(); else { Array.Clear(Vga.Palette); Vga.LoadEgaDac(); }
        if (!noClear)
        {
            if (mode is <= 3 or 7) for (int i = 0; i < 0x8000; i++) { Vga.Ram[i * 4] = 0x20; Vga.Ram[i * 4 + 1] = 0x07; }
            else if (cgaGfx) for (int i = 0; i < 0x8000; i++) { Vga.Ram[i * 4] = 0; Vga.Ram[i * 4 + 1] = 0; }
            else Array.Clear(Vga.Ram);
        }
        if (mode is <= 3 or 7 || cgaGfx) Vga.LoadFont();
        Vga.Picture?.ModeSet(mode);
        PhysW8(0x449, (byte)mode);
        PhysW16(0x44a, (ushort)cols);
        PhysW16(0x44c, (ushort)(pageSize == 0 ? 0x10000 & 0xffff : pageSize));
        PhysW16(0x44e, 0);
        for (uint i = 0; i < 8; i++) PhysW16(0x450 + i * 2, 0);
        PhysW16(0x460, 0x0607);
        PhysW8(0x462, 0);
        PhysW16(0x463, 0x3d4);
        PhysW8(0x465, (byte)(mode == 0x13 || mode == 0x12 ? 0x09 : 0x29));
        PhysW8(0x484, (byte)(rows - 1));
        PhysW16(0x485, (ushort)(mode == 0x13 ? 8 : 16));
        PhysW8(0x487, (byte)(noClear ? 0xe0 : 0x60));
        PhysW8(0x488, 0x09);
        PhysW8(0x489, 0x11);
        SetCrtcCursor();
    }

    private void SetCrtcCursor()
    {
        if (!TextMode) return;
        int page = ActivePage;
        int col = Phys8(0x450u + (uint)page * 2), row = Phys8(0x451u + (uint)page * 2);
        int pos = page * (Phys16(0x44c) / 2) + row * Cols + col;
        Vga.Cr[0x0e] = (byte)(pos >> 8); Vga.Cr[0x0f] = (byte)pos;
    }

    private (int col, int row) Cursor(int page) => (Phys8(0x450u + (uint)page * 2), Phys8(0x451u + (uint)page * 2));
    private void SetCursor(int page, int col, int row)
    {
        PhysW8(0x450u + (uint)page * 2, (byte)col); PhysW8(0x451u + (uint)page * 2, (byte)row);
        if (page == ActivePage) SetCrtcCursor();
    }

    private int CellIndex(int page, int col, int row) => (page * (Phys16(0x44c) / 2) + row * Cols + col) & 0xffff;

    // ---- drawing a character cell ----

    private void PutCell(int page, int col, int row, byte ch, byte attr, bool withAttr)
    {
        if (TextMode)
        {
            int i = CellIndex(page, col, row) * 4;
            if (i + 1 >= Vga.RamSize) return;
            TxtW(i, ch);
            if (withAttr) TxtW(i + 1, attr);
            if (Vga.Picture != null) { Vga.Picture.NoteWrite(0xb8000u + (uint)(i >> 1), ch); if (withAttr) Vga.Picture.NoteWrite(0xb8001u + (uint)(i >> 1), attr); }   // the BIOS's write, as a bus write
            return;
        }
        int h = VMode == 0x13 ? 8 : 16;
        for (int y = 0; y < h; y++)
        {
            int bits = h == 8 ? VgaFont.Font16[ch * 16 + y * 2] | VgaFont.Font16[ch * 16 + y * 2 + 1] : VgaFont.Font16[ch * 16 + y];
            for (int x = 0; x < 8; x++)
            {
                bool on = (bits & (0x80 >> x)) != 0;
                int px = col * 8 + x, py = row * h + y;
                if (VMode == 0x13) { if (on || (attr & 0x80) == 0) PutPixel(px, py, on ? (byte)attr : (byte)0, false); }
                else PutPixel(px, py, (byte)(on ? attr & 0x0f : 0), on && (attr & 0x80) != 0);
            }
        }
    }

    private (byte ch, byte attr) GetCell(int page, int col, int row)
    {
        if (!TextMode) return (0, 0);
        int i = CellIndex(page, col, row) * 4;
        return (TxtR(i), TxtR(i + 1));
    }

    private void PutPixel(int x, int y, byte c, bool xor)
    {
        if (VMode == 0x13)
        {
            if (x < 0 || x >= 320 || y < 0 || y >= 200) return;
            int i = y * 320 + x;
            Vga.Ram[i] = xor ? (byte)(Vga.Ram[i] ^ c) : c;
            Vga.Picture?.NoteWrite(0xa0000u + (uint)i, Vga.Ram[i]);
            return;
        }
        if (VMode != 0x12 || x < 0 || x >= 640 || y < 0 || y >= 480) return;
        int a = (y * 80 + (x >> 3)) * 4;
        int bit = 0x80 >> (x & 7);
        for (int p = 0; p < 4; p++)
        {
            bool on = (c & (1 << p)) != 0;
            if (xor) { if (on) Vga.Ram[a + p] ^= (byte)bit; }
            else Vga.Ram[a + p] = (byte)(on ? Vga.Ram[a + p] | bit : Vga.Ram[a + p] & ~bit);
        }
        Vga.Picture?.NoteWrite(0xa0000u + (uint)(a >> 2), c);
    }

    private byte GetPixel(int x, int y)
    {
        if (VMode == 0x13) return x is >= 0 and < 320 && y is >= 0 and < 200 ? Vga.Ram[y * 320 + x] : (byte)0;
        if (VMode != 0x12 || x < 0 || x >= 640 || y < 0 || y >= 480) return 0;
        int a = (y * 80 + (x >> 3)) * 4, bit = 0x80 >> (x & 7), c = 0;
        for (int p = 0; p < 4; p++) if ((Vga.Ram[a + p] & bit) != 0) c |= 1 << p;
        return (byte)c;
    }

    private void Scroll(bool up, int lines, byte attr, int top, int left, int bottom, int right, int page)
    {
        int cols = Cols, rows = Rows;
        if (right >= cols) right = cols - 1;
        if (bottom >= rows) bottom = rows - 1;
        if (left > right || top > bottom) return;
        int height = bottom - top + 1;
        if (lines == 0 || lines > height) lines = height;
        if (TextMode)
        {
            for (int k = 0; k < height; k++)
            {
                int r = up ? top + k : bottom - k;
                int src = up ? r + lines : r - lines;
                for (int c = left; c <= right; c++)
                {
                    int di = CellIndex(page, c, r) * 4;
                    if (src >= top && src <= bottom)
                    {
                        int si = CellIndex(page, c, src) * 4;
                        TxtW(di, TxtR(si)); TxtW(di + 1, TxtR(si + 1));
                    }
                    else { TxtW(di, 0x20); TxtW(di + 1, attr); }
                }
            }
            return;
        }
        int ch = VMode == 0x13 ? 8 : 16;
        for (int k = 0; k < height * ch; k++)
        {
            int y = up ? top * ch + k : (bottom + 1) * ch - 1 - k;
            int sy = up ? y + lines * ch : y - lines * ch;
            for (int x = left * 8; x < (right + 1) * 8; x++)
                PutPixel(x, y, sy >= top * ch && sy < (bottom + 1) * ch ? GetPixel(x, sy) : (VMode == 0x13 ? (byte)0 : (byte)(attr >> 4)), false);
        }
    }

    private void TeletypeChar(byte ch, byte color, int page)
    {
        Teletype.Append((char)ch);
        var (col, row) = Cursor(page);
        switch (ch)
        {
            case 7: return;
            case 8: if (col > 0) col--; break;
            case 10: row++; break;
            case 13: col = 0; break;
            default:
                PutCell(page, col, row, ch, color, !TextMode);
                col++;
                break;
        }
        if (col >= Cols) { col = 0; row++; }
        if (row >= Rows)
        {
            byte attr = TextMode ? GetCell(page, Cols - 1, Rows - 1).attr : (byte)0;
            Scroll(true, 1, attr, 0, 0, Rows - 1, Cols - 1, page);
            row = Rows - 1;
        }
        SetCursor(page, col, row);
    }

    public void TeletypeString(string s) { foreach (char c in s) TeletypeChar((byte)c, 7, ActivePage); }

    // ---- the mode-set replay (Windows 98's VDD) ----
    // A real video BIOS programs the card with OUT instructions; a V86 monitor (Windows 98's VDD) traps them and keeps its own copy of
    // the registers. Ours sets them from C#, so the VDD's copy kept the boot logo's mode 13h after Device_Init's INT 10h AX=0003, its
    // text-mode check failed, it never let the display driver's OUTs through, and the first GUI boot stayed black. So in V86 mode the
    // INT 10h stub, after the C# service, replays a mode set's register writes in machine code from a table the service fills.
    private const uint Int10Replay = 0xe070, Int10Table = 0xd000;

    private void InstallInt10Replay()
    {
        var m = Cpu.Mem;
        uint s = Rom + StubBase + 0x10 * 4;
        m[s + 2] = 0xeb; m[s + 3] = (byte)(Int10Replay - (StubBase + 0x10 * 4 + 4));   // OUT E0h,AL / JMP replay
        // push ds, si, dx, ax / ds = cs / si = table; L: lodsw (port; 0 = end; FFFFh = load the ROM 8x16 font into plane 2 the way a VGA BIOS does,
        // through A000h with the planar registers, so a V86 monitor's per-VM copy gets it; bit 15 = IN instead of OUT) / lodsb (value) / OUT or IN
        byte[] code =
        {
            0x1e, 0x56, 0x52, 0x50, 0x0e, 0x1f, 0xbe, 0x00, 0xd0, 0xad, 0x85, 0xc0, 0x74, 0x1c, 0x3d, 0xff, 0xff, 0x75, 0x05, 0xe8, 0x19, 0x00, 0xeb, 0xf1,
            0x89, 0xc2, 0x81, 0xe2, 0xff, 0x7f, 0xa9, 0x00, 0x80, 0xac, 0x75, 0x03, 0xee, 0xeb, 0xe2, 0xec, 0xeb, 0xdf, 0x58, 0x5a, 0x5e, 0x1f, 0xcf,
            // font: push si, es, di, cx, bx; SR2=04 SR4=07 GR4=02 GR5=00 GR6=04; copy 256 x 16 bytes F000:A000 -> A000:0000 (32-byte stride); SR2=03 SR4=02
            // GR4=00 GR5=10 GR6=0E; pop; ret
            0x56, 0x06, 0x57, 0x51, 0x53, 0xba, 0xc4, 0x03, 0xb8, 0x02, 0x04, 0xef, 0xb8, 0x04, 0x07, 0xef, 0xba, 0xce, 0x03, 0xb8, 0x04, 0x02, 0xef,
            0xb8, 0x05, 0x00, 0xef, 0xb8, 0x06, 0x04, 0xef, 0xb8, 0x00, 0xa0, 0x8e, 0xc0, 0x31, 0xff, 0xbe, 0x00, 0xa0, 0xbb, 0x00, 0x01, 0xb9, 0x10,
            0x00, 0xf3, 0xa4, 0x83, 0xc7, 0x10, 0x4b, 0x75, 0xf5, 0xba, 0xc4, 0x03, 0xb8, 0x02, 0x03, 0xef, 0xb8, 0x04, 0x02, 0xef, 0xba, 0xce, 0x03,
            0xb8, 0x04, 0x00, 0xef, 0xb8, 0x05, 0x10, 0xef, 0xb8, 0x06, 0x0e, 0xef, 0x5b, 0x59, 0x5f, 0x07, 0x5e, 0xc3,
        };
        Array.Copy(code, 0, m, Rom + Int10Replay, code.Length);
        m[Rom + Int10Table] = 0; m[Rom + Int10Table + 1] = 0;
    }

    /// <summary>Fills the replay table with the writes that load the card's current standard registers (the VGA BIOS mode set's order).</summary>
    private void QueueModeReplay()
    {
        var m = Cpu.Mem; uint o = Rom + Int10Table;
        void E(int port, int v) { m[o] = (byte)port; m[o + 1] = (byte)(port >> 8); m[o + 2] = (byte)v; o += 3; }
        var v = Vga;
        E(0x3c4, 0); E(0x3c5, 1);                                  // sequencer reset
        E(0x3c2, v.Msr);
        for (int i = 1; i < 5; i++) { E(0x3c4, i); E(0x3c5, v.Sr[i]); }
        E(0x3c4, 0); E(0x3c5, v.Sr[0]);
        E(0x3d4, 0x11); E(0x3d5, v.Cr[0x11] & 0x7f);               // unlock CR0-7
        for (int i = 0; i < 25; i++) { E(0x3d4, i); E(0x3d5, v.Cr[i]); }
        for (int i = 0; i < 9; i++) { E(0x3ce, i); E(0x3cf, v.Gr[i]); }
        E(0x3da | 0x8000, 0);                                      // reset the attribute flip-flop
        for (int i = 0; i < 21; i++) { E(0x3c0, i); E(0x3c0, v.Ar[i]); }
        E(0x3c0, 0x20);                                            // video on
        m[o] = 0; m[o + 1] = 0;
    }

    private void Int10Core()
    {
        Cpu.Mem[Rom + Int10Table] = 0; Cpu.Mem[Rom + Int10Table + 1] = 0;
        int page = (int)BH;
        switch (AH)
        {
            case 0x00:
                SetMode((int)AL); AL = (uint)(VMode == 0x13 ? 0x20 : 0x30);
                break;
            case 0x01:
            {
                int s = (int)CH, e = (int)CL;
                PhysW16(0x460, (ushort)CX);
                if ((s & 0x60) == 0x20) { Vga.Cr[0x0a] = 0x20; break; }
                s &= 0x1f; e &= 0x1f;
                if (s < 8 && e < 8) { s = Math.Min(15, s * 2 + (s >= 4 ? 1 : 0)); e = Math.Min(15, e * 2); }
                Vga.Cr[0x0a] = (byte)s; Vga.Cr[0x0b] = (byte)e;
                break;
            }
            case 0x02: SetCursor(page & 7, (int)DL, (int)DH); break;
            case 0x03: { var (c, r) = Cursor(page & 7); DL = (uint)c; DH = (uint)r; CX = Phys16(0x460); break; }
            case 0x05:
            {
                int p = (int)AL & 7;
                PhysW8(0x462, (byte)p);
                int start = p * (Phys16(0x44c) / 2);
                if (TextMode) { Vga.Cr[0x0c] = (byte)(start >> 8); Vga.Cr[0x0d] = (byte)start; PhysW16(0x44e, (ushort)(start * 2)); }
                SetCrtcCursor();
                break;
            }
            case 0x06: case 0x07:
                Scroll(AH == 0x06, (int)AL, (byte)BH, (int)CH, (int)CL, (int)DH, (int)DL, ActivePage);
                break;
            case 0x08: { var (c, r) = Cursor(page & 7); var (ch, at) = GetCell(page & 7, c, r); AL = ch; AH = at; break; }
            case 0x09: case 0x0a:
            {
                var (c, r) = Cursor(page & 7);
                int n = (int)CX;
                for (int i = 0; i < n; i++)
                {
                    int cc = c + i, rr = r + cc / Cols; cc %= Cols;
                    if (rr >= Rows) break;
                    PutCell(page & 7, cc, rr, (byte)AL, (byte)BL, AH == 0x09 || !TextMode);
                }
                break;
            }
            case 0x0b: break;                                    // CGA palette: nothing on VGA text / 13h
            case 0x0c: PutPixel((int)CX, (int)DX, (byte)AL, (AL & 0x80) != 0 && VMode != 0x13); break;
            case 0x0d: AL = GetPixel((int)CX, (int)DX); break;
            case 0x0e: TeletypeChar((byte)AL, (byte)BL, ActivePage); break;
            case 0x0f: AL = (uint)VMode; AH = (uint)Cols; BH = (uint)ActivePage; break;
            case 0x10: Int10Palette(); break;
            case 0x11: Int10Font(); break;
            case 0x12:
                if (BL == 0x10) { BH = 0; BL = 3; CX = 0x0009; }
                else if (BL is 0x30 or 0x31 or 0x32 or 0x33 or 0x34 or 0x36) AL = 0x12;
                break;
            case 0x13:
            {
                int mode = (int)AL;
                int p = (int)BH & 7;
                var save = Cursor(p);
                SetCursor(p, (int)DL, (int)DH);
                uint s = SegB(I386.SEG_ES) + BP;
                byte attr = (byte)BL;
                for (int i = 0; i < (int)CX; i++)
                {
                    byte ch = Phys8(s++);
                    if ((mode & 2) != 0) attr = Phys8(s++);
                    if (ch is 7 or 8 or 10 or 13) TeletypeChar(ch, attr, p);
                    else
                    {
                        var (c, r) = Cursor(p);
                        PutCell(p, c, r, ch, attr, true);
                        Teletype.Append((char)ch);
                        c++; if (c >= Cols) { c = 0; r++; }
                        if (r >= Rows) { Scroll(true, 1, attr, 0, 0, Rows - 1, Cols - 1, p); r = Rows - 1; }
                        SetCursor(p, c, r);
                    }
                }
                if ((mode & 1) == 0) SetCursor(p, save.col, save.row);
                break;
            }
            case 0x1a: if (AL == 0) { AL = 0x1a; BX = 0x0008; } break;
            case 0x4f when S3 != null: Vbe(); break;
        }
    }

    private void Int10Palette()
    {
        switch (AL)
        {
            case 0x00: if (BL < 16) Vga.Ar[BL] = (byte)(BH & 0x3f); break;
            case 0x01: Vga.Ar[0x11] = (byte)BH; break;
            case 0x02:
            {
                uint p = SegB(I386.SEG_ES) + DX;
                for (uint i = 0; i < 16; i++) Vga.Ar[i] = (byte)(Phys8(p + i) & 0x3f);
                Vga.Ar[0x11] = Phys8(p + 16);
                break;
            }
            case 0x03: Vga.Ar[0x10] = (byte)((Vga.Ar[0x10] & ~8) | (BL != 0 ? 8 : 0)); break;
            case 0x07: BH = BL < 16 ? Vga.Ar[BL] : 0u; break;
            case 0x08: BH = Vga.Ar[0x11]; break;
            case 0x10: { int i = (int)BX & 0xff; Vga.Palette[i * 3] = (byte)(DH & 63); Vga.Palette[i * 3 + 1] = (byte)(CH & 63); Vga.Palette[i * 3 + 2] = (byte)(CL & 63); break; }
            case 0x12:
            {
                uint p = SegB(I386.SEG_ES) + DX;
                int start = (int)BX, n = (int)CX;
                for (int i = 0; i < n && start + i < 256; i++)
                    for (int k = 0; k < 3; k++) Vga.Palette[(start + i) * 3 + k] = (byte)(Phys8(p++) & 63);
                break;
            }
            case 0x15: { int i = (int)BX & 0xff; DH = Vga.Palette[i * 3]; CH = Vga.Palette[i * 3 + 1]; CL = Vga.Palette[i * 3 + 2]; break; }
            case 0x17:
            {
                uint p = SegB(I386.SEG_ES) + DX;
                int start = (int)BX, n = (int)CX;
                for (int i = 0; i < n && start + i < 256; i++)
                    for (int k = 0; k < 3; k++) PhysW8(p++, Vga.Palette[(start + i) * 3 + k]);
                break;
            }
            case 0x1a: BL = (uint)((Vga.Ar[0x10] >> 7) & 1); BH = Vga.Ar[0x14]; break;
        }
    }

    private void Int10Font()
    {
        switch (AL)
        {
            case 0x00: case 0x10:
            {
                uint src = SegB(I386.SEG_ES) + BP;
                int count = (int)CX, first = (int)DX, block = (int)BL & 7, h = (int)BH;
                int mapOff = (block & 3) * 0x4000 + (block >> 2) * 0x2000;
                for (int c = 0; c < count; c++)
                    for (int y = 0; y < 32; y++)
                        Vga.Ram[(mapOff + (first + c) * 32 + y) * 4 + 2] = y < h ? Phys8(src + (uint)(c * h + y)) : (byte)0;
                break;
            }
            case 0x01: case 0x02: case 0x04: case 0x11: case 0x12: case 0x14: Vga.LoadFont(); break;
            case 0x03: Vga.Sr[3] = (byte)BL; break;
            case 0x30:
                SetEs(RomSeg); BP = FontOff;
                CX = 16; DL = (uint)(Rows - 1);
                break;
        }
    }

    // ---- INT 10h from a V86 VM (a DOS box or the system VM under Windows) ----
    // A real video BIOS reaches the card only through OUTs and the VM's own B8000 mapping, and Windows 98's VDD virtualizes both
    // (a background VM's mode set must not touch the screen Windows is drawing). So in V86 mode the C# service runs, its register
    // changes become replayed OUTs (the VDD sees them), and the card's registers (and video memory, for mode/font sets) are put back.
    private bool _v86Video;
    private byte TxtR(int i) => _v86Video ? Lin8(0xb8000u + (uint)((i >> 2) * 2 + (i & 1))) : Vga.Ram[i];
    private void TxtW(int i, byte v) { if (_v86Video) LinW8(0xb8000u + (uint)((i >> 2) * 2 + (i & 1)), v); else Vga.Ram[i] = v; }

    private void Int10()
    {
        if ((C.Flags & I386.VM) == 0) { Int10Core(); return; }
        var v = Vga;
        byte[] sr = (byte[])v.Sr.Clone(), gr = (byte[])v.Gr.Clone(), cr = (byte[])v.Cr.Clone(), ar = (byte[])v.Ar.Clone(), pal = (byte[])v.Palette.Clone();
        byte msr = v.Msr, sri = v.SrIndex, gri = v.GrIndex, cri = v.CrIndex, ari = v.ArIndex, aff = v.ArFlipFlop;
        bool memSet = AH == 0x00 || AH == 0x11 || AX == 0x4f02;
        byte[]? s3State = null;
        if (v.S3 != null) { var ms = new System.IO.MemoryStream(); using (var bw = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8, true)) v.S3.Save(bw); s3State = ms.ToArray(); }
        byte[]? srx = v.S3 != null ? (byte[])v.S3.SrExt.Clone() : null;
        byte[]? ram = memSet ? (byte[])v.Ram.Clone() : null;
        int mode = (int)AL, fn = (int)AH;
        // a mode set replays EVERY standard register and the whole DAC: the VDD's copy for this VM is not the card's (the GUI's) state, so a diff
        // against the card left registers the C# set to the card's old value unwritten (a full-screen DOS box got AR 07h = 00h: grey text drawn black)
        bool all = fn == 0x00 || AX == 0x4f02;
        _v86Video = true;
        try { Int10Core(); }
        finally { _v86Video = false; }
        // the replay table: register writes that move the VDD's (or, passed through, the card's) copy to the new state
        var m = Cpu.Mem; uint o = Rom + Int10Table, end = Rom + Int10Replay - 4;
        void E(int port, int val) { if (o >= end) return; m[o] = (byte)port; m[o + 1] = (byte)(port >> 8); m[o + 2] = (byte)val; o += 3; }
        // a text mode set (or a ROM 8x16 font load) puts the font into plane 2 first: the C# load into the card is undone below, so the VDD's copy
        // for this VM must get it through A000h (a full-screen DOS box showed garbage glyphs). Only with the replay code that knows the op.
        bool fontOp = m[Rom + Int10Replay + 14] == 0x3d && ((fn == 0x00 && (mode & 0x7f) is <= 3 or 7) || (fn == 0x11 && (mode & 0x0f) == 4));
        if (fontOp) { m[o] = 0xff; m[o + 1] = 0xff; o += 2; }
        bool srD = all; for (int i = 0; i < 5; i++) srD |= sr[i] != v.Sr[i];
        if (srD) { E(0x3c4, 0); E(0x3c5, 1); for (int i = 1; i < 5; i++) if (all || sr[i] != v.Sr[i]) { E(0x3c4, i); E(0x3c5, v.Sr[i]); } }
        if (all || msr != v.Msr) E(0x3c2, v.Msr);
        if (srD) { E(0x3c4, 0); E(0x3c5, v.Sr[0]); }
        bool crLow = all; for (int i = 0; i < 8; i++) crLow |= cr[i] != v.Cr[i];
        if (crLow) { E(0x3d4, 0x11); E(0x3d5, v.Cr[0x11] & 0x7f); }
        for (int i = 0; i < 25; i++) if (all || cr[i] != v.Cr[i] || (crLow && i == 0x11)) { E(0x3d4, i); E(0x3d5, v.Cr[i]); }
        // the S3 extensions a VBE mode set programs (CR30-6F behind the CR38/39 locks, SR09-1F behind SR08)
        bool crx = false; for (int i = 0x30; i < 0x70; i++) crx |= cr[i] != v.Cr[i];
        if (crx)
        {
            E(0x3d4, 0x38); E(0x3d5, 0x48); E(0x3d4, 0x39); E(0x3d5, 0xa5);
            for (int i = 0x30; i < 0x70; i++) if (i is not (0x38 or 0x39) && cr[i] != v.Cr[i]) { E(0x3d4, i); E(0x3d5, v.Cr[i]); }
            E(0x3d4, 0x38); E(0x3d5, v.Cr[0x38]); E(0x3d4, 0x39); E(0x3d5, v.Cr[0x39]);
        }
        if (srx != null)
        {
            bool sd = false; for (int i = 9; i < 0x20; i++) sd |= srx[i] != v.S3!.SrExt[i];
            if (sd) { E(0x3c4, 8); E(0x3c5, 6); for (int i = 9; i < 0x20; i++) if (srx[i] != v.S3!.SrExt[i]) { E(0x3c4, i); E(0x3c5, v.S3.SrExt[i]); } E(0x3c4, 8); E(0x3c5, v.S3.SrExt[8]); }
        }
        for (int i = 0; i < 9; i++) if (all || gr[i] != v.Gr[i]) { E(0x3ce, i); E(0x3cf, v.Gr[i]); }
        bool arD = all; for (int i = 0; i < 21; i++) arD |= ar[i] != v.Ar[i];
        if (arD) { E(0x3da | 0x8000, 0); for (int i = 0; i < 21; i++) if (all || ar[i] != v.Ar[i]) { E(0x3c0, i); E(0x3c0, v.Ar[i]); } E(0x3c0, 0x20); }
        for (int i = 0; i < 256; i++)
            if (all || pal[i * 3] != v.Palette[i * 3] || pal[i * 3 + 1] != v.Palette[i * 3 + 1] || pal[i * 3 + 2] != v.Palette[i * 3 + 2])
            { E(0x3c8, i); E(0x3c9, v.Palette[i * 3]); E(0x3c9, v.Palette[i * 3 + 1]); E(0x3c9, v.Palette[i * 3 + 2]); }
        m[o] = 0; m[o + 1] = 0;
        // put the card back: the replay (through the VDD) is now the only way the change reaches it
        Array.Copy(sr, v.Sr, sr.Length); Array.Copy(gr, v.Gr, gr.Length); Array.Copy(cr, v.Cr, cr.Length); Array.Copy(ar, v.Ar, ar.Length); Array.Copy(pal, v.Palette, pal.Length);
        v.Msr = msr; v.SrIndex = sri; v.GrIndex = gri; v.CrIndex = cri; v.ArIndex = ari; v.ArFlipFlop = aff;
        if (ram != null) Array.Copy(ram, v.Ram, ram.Length);
        if (s3State != null) using (var br = new System.IO.BinaryReader(new System.IO.MemoryStream(s3State))) v.S3!.Load(br);
        // a text mode set clears the VM's own text buffer (its B8000, wherever the VDD maps it)
        if (fn == 0x00 && (mode & 0x80) == 0 && (mode & 0x7f) is <= 3 or 7)
            for (uint i = 0; i < 0x4000; i += 2) { LinW8(0xb8000 + i, 0x20); LinW8(0xb8001 + i, 0x07); }
    }
}
