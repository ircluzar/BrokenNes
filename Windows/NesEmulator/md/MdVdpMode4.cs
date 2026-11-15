using System;

namespace NesEmulator.Sega;

/// <summary>
/// SMS Mode 4 on the Genesis VDP, entered from Genesis mode by clearing register 1 bit 2 (M5): what the user's real Genesis showed with the Nested Everything test ROM
/// (T:\BrokenDev\Releases\NestedEverything_MD_Mode4Test, 2026-11-15) and what Charles MacDonald measured ("VDP odds and ends", SpritesMind, March 2013); the
/// structure follows Genesis Plus GX (core/vdp_ctrl.c vdp_68k_ctrl_w / vdp_68k_data_w_m4 / vdp_hvc_r, core/vdp_render.c render_bg_m4 / render_obj_m4), written here from memory of it.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Every control word is a whole command: <c>$0000|a</c> read (with read-ahead), <c>$4000|a</c> VRAM write, <c>$8rvv</c> register write (it also loads the address, as on the SMS), <c>$C000|i</c> CRAM write. No DMA, no VSRAM, registers 11+ cannot be written.</item>
/// <item>The address steps by register 15 + 1 (the test sets 1: two bytes a word), 14 bits.</item>
/// <item>VRAM is 16 KB, the SMS byte address remapped into the 64 KB array: P = (A &amp; $3C00) | ((A &amp; $1FE) &lt;&lt; 1) | ((A &gt;&gt; 8) &amp; 2), the even SMS byte at P+1, the odd one at P. One physical array serves both modes,
/// so VRAM filled in Mode 5 at the remapped addresses shows in Mode 4 (screen 5) without a conversion step; Genesis Plus GX keeps Mode 4 VRAM linear and so has to convert on an M5 switch, which is the same thing seen from the other side.</item>
/// <item>A 68000 word puts its LOW byte at the even SMS address (hardware: the screen 2 "MODE 4 OK" line).</item>
/// <item>CRAM: 32 entries, the word's bits 0-5 are an SMS colour --BBGGRR, shown as the 9-bit colour with each 2-bit level at the top of the 3-bit one (Genesis Plus GX's Mode 4 colour on the MD VDP).</item>
/// <item>256x192 picture; the frame is handed over as 256x224 with 16 lines of border (sprite colour 16 + R7 low nybble) above and below, as the TV showed.</item>
/// <item>Interrupts: the frame interrupt at line 192 (V $C0), the line counter (R10) steps on lines 0-191 and the pre-render line (HInt every R10+1 lines); only a status read acknowledges.</item>
/// <item>The H counter is frozen (the value it had when M5 was cleared); the V counter runs and follows the SMS 192-line sequence.</item>
/// </list>
/// Not modelled: the 224/240-line extended modes, sprite zoom, the SMS1 name-table mask quirk.
/// </remarks>
public partial class MdVdp
{
    /// <summary>A code value with this bit set is a Mode 4 access (bits 1-0: the SMS code).</summary>
    private const int M4Code = 0x40;
    /// <summary>Rows of border above the 192-line picture in the 224-row frame handed over.</summary>
    public const int M4Top = 16, M4FrameHeight = 224;

    private uint StepAddress(uint a) => Mode5 ? (a + (uint)AutoIncrement) & 0x1FFFF : (a + (uint)Reg[15] + 1) & 0x3FFF;

    /// <summary>The 64 KB array index of SMS VRAM byte <paramref name="a"/>.</summary>
    public static int M4Phys(int a) => (a & 0x3C00) | ((a & 0x1FE) << 1) | ((a >> 8) & 2) | ((a & 1) ^ 1);

    private byte M4B(int a) => VR(M4Phys(a & 0x3FFF));

    private void OnModeSwitch(bool wasMode5)
    {
        if (wasMode5)
        {
            // into Mode 4: the H counter stops where it is (Genesis Plus GX vdp_hvc_r: "Mode 4: V counter runs normally & H counter is frozen"; hardware screen 4 reads the same H four times)
            hvLatched = HvNow();
            pendingCommand = false; code = M4Code | 2;
        }
        else hvLatched = (Reg[0] & 2) == 0 ? -1 : HvNow();
    }

    private void WriteControlM4(ushort value)
    {
        pendingCommand = false;
        int c = value >> 14 & 3;
        address = (uint)(value & 0x3FFF);
        code = M4Code | c;
        if (c == 2) { RegisterWrite(value >> 8 & 0x1F, (byte)value); return; }
        if (c == 0) StartReadAhead();
    }

    private void WriteTargetM4(int c, uint addr, ushort data)
    {
        if ((c & 3) == 3)
        {
            int d = data & 0x3F;
            OnCramWrite((int)addr & 0x1F, (ushort)((d & 0x30) << 6 | (d & 0x0C) << 4 | (d & 0x03) << 2));
            return;
        }
        if (LineIsActive) ActiveVramWrites++;
        int a = (int)addr & 0x3FFF;
        if ((a & 1) != 0) data = (ushort)(data << 8 | data >> 8);
        a &= ~1;
        Vram[M4Phys(a)] = (byte)data;            // the low byte at the even SMS address
        Vram[M4Phys(a | 1)] = (byte)(data >> 8);
    }

    private ushort ReadSourceM4(uint addr)
    {
        int a = (int)addr & 0x3FFE;
        ushort w = (ushort)(Vram[M4Phys(a | 1)] << 8 | Vram[M4Phys(a)]);
        return (addr & 1) != 0 ? (ushort)(w << 8 | w >> 8) : w;
    }

    // ---- the picture

    private int M4Border => 16 | Reg[7] & 15;

    private void BeginLineM4()
    {
        lineY = Line; dbl = false; imgRow = lineY; cellShift = 3;
        lineWidth = 256; linePxDiv = H40 ? 8 : 10;
        renderX = colourX = 0;
        rowBase = (lineY + M4Top) * PictureStride;
        hscrollA = Reg[8];                       // the H scroll is taken as the line starts
        if (lineY == 0) hscrollB = Reg[9];       // the V scroll once a frame (hscrollB holds it in Mode 4)
        BuildSpritesM4();
    }

    private void BuildSpritesM4()
    {
        Array.Clear(sprLine, 0, 256);
        if (!DisplayEnabled) return;
        int sat = (Reg[5] & 0x7E) << 7;
        int h = (Reg[1] & 2) != 0 ? 16 : 8;
        int found = 0;
        for (int i = 0; i < 64; i++)
        {
            int y = M4B(sat + i);
            if (y == 0xD0) break;
            int dy = (lineY - y - 1) & 0xFF;
            if (dy >= h) continue;
            if (++found > 8) { sprOverflow = true; break; }
            int x = M4B(sat + 0x80 + i * 2) - ((Reg[0] & 8) != 0 ? 8 : 0);
            int tile = M4B(sat + 0x81 + i * 2) | (Reg[6] & 4) << 6;
            if (h == 16) tile &= ~1;
            int pa = tile * 32 + dy * 4;
            int p0 = M4B(pa), p1 = M4B(pa + 1), p2 = M4B(pa + 2), p3 = M4B(pa + 3);
            for (int px = 0; px < 8; px++)
            {
                int sx = x + px;
                if (sx < 0 || sx >= 256) continue;
                int b = 7 - px;
                int col = (p0 >> b & 1) | (p1 >> b & 1) << 1 | (p2 >> b & 1) << 2 | (p3 >> b & 1) << 3;
                if (col == 0) continue;
                if (sprLine[sx] != 0) { sprCollision = true; continue; }
                sprLine[sx] = (byte)col;
            }
        }
    }

    private void RenderM4(int x0, int x1)
    {
        int border = M4Border;
        if (!DisplayEnabled) { for (int x = x0; x < x1; x++) lineIdx[x] = (byte)border; return; }
        int nt = (Reg[2] & 0x0E) << 10;
        int hs = (Reg[0] & 0x40) != 0 && lineY < 16 ? 0 : hscrollA;
        for (int x = x0; x < x1; x++)
        {
            if ((Reg[0] & 0x20) != 0 && x < 8) { lineIdx[x] = (byte)border; continue; }
            int vs = (Reg[0] & 0x80) != 0 && x >= 192 ? 0 : hscrollB;
            int row = (lineY + vs) % 224;
            int sx = (x - hs) & 255;
            int ea = nt + ((row >> 3) * 32 + (sx >> 3)) * 2;
            int e = M4B(ea) | M4B(ea + 1) << 8;
            int tr = (e & 0x400) != 0 ? 7 - (row & 7) : row & 7;
            int pa = (e & 0x1FF) * 32 + tr * 4;
            int b = (e & 0x200) != 0 ? sx & 7 : 7 - (sx & 7);
            int col = (M4B(pa) >> b & 1) | (M4B(pa + 1) >> b & 1) << 1 | (M4B(pa + 2) >> b & 1) << 2 | (M4B(pa + 3) >> b & 1) << 3;
            int bg = (e & 0x800) >> 7 | col;
            int sp = sprLine[x];
            lineIdx[x] = (byte)(sp != 0 && !((e & 0x1000) != 0 && col != 0) ? 16 | sp : bg);
        }
    }

    /// <summary>The border rows above and below the 192-line picture.</summary>
    private void FinishFrameM4()
    {
        uint c = pal[M4Border];
        for (int y = 0; y < M4Top; y++) { Array.Fill(building, c, y * PictureStride, 256); Array.Fill(building, c, (M4Top + 192 + y) * PictureStride, 256); }
    }
}
