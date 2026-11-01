using System;
using System.IO;
using System.Linq;
using System.Text;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--md-pic-test</c>: the Genesis picture, checked against the documented rules and against what Nemesis' sprite masking ROM, MacDonald's window and V counter ROMs and BlastEm's own pictures showed (each check that
/// comes from the oracle says so). The VDP is set up directly (memories and registers), run for two frames and its <see cref="MdVdp.Picture"/> read pixel by pixel; mid-line behaviour (the colour RAM dot, the display
/// switched off in the middle of a line, the window registers' latch) is driven through the control port at chosen times. After the picture: the session (pad mapping, the picture it hands the apps), battery saves, and the
/// cartridge mappers (SRAM, the SSF2 banks, the Sonic &amp; Knuckles lock-on image).
/// </summary>
internal static class MdPictureTest
{
    private static int pass, fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) pass++; else fail++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(ok || detail == "" ? "" : "  (" + detail + ")")}");
    }

    private static void Group(string title) => Console.WriteLine($"-- {title}");

    // the DAC: the 15 output levels the picture uses
    private static readonly int[] L15 = { 0, 27, 49, 71, 87, 103, 119, 130, 146, 157, 174, 190, 206, 228, 255 };
    private static uint Argb(int cram, int mode)
    {
        int r = cram >> 1 & 7, g = cram >> 5 & 7, b = cram >> 9 & 7;
        int Lv(int v) => mode == 0 ? L15[v * 2] : mode == 1 ? L15[v] : L15[v + 7];
        return 0xFF000000u | (uint)(Lv(r) << 16 | Lv(g) << 8 | Lv(b));
    }

    // =====================================================================================================================
    //  A VDP with a little scenery
    // =====================================================================================================================

    private const int PlaneA = 0x2000, PlaneB = 0x4000, Window = 0x1000, SatAddr = 0x6000, HScroll = 0x7C00;

    private sealed class Pic
    {
        public readonly MdVdp V;
        public long T;
        public Pic(bool h40 = true, bool v30 = false, SegaRegion region = SegaRegion.Ntsc)
        {
            V = new MdVdp(SegaTimeline.Genesis(region));
            V.Reg[1] = (byte)(0x44 | (v30 ? 8 : 0));
            V.Reg[2] = PlaneA >> 10; V.Reg[3] = Window >> 10; V.Reg[4] = PlaneB >> 13; V.Reg[5] = SatAddr >> 9; V.Reg[13] = HScroll >> 10;
            V.Reg[12] = (byte)(h40 ? 0x81 : 0x00);
            V.Reg[16] = 0x01;            // 64 x 32
            V.Reg[15] = 2;
            for (int t = 1; t < 16; t++) Tile(t, t);             // tile n: solid colour n
            Colours();
            V.ResetPictureState();
        }

        /// <summary>CRAM: colour i of palette line p = a distinctive colour; entry 0 of each line is black.</summary>
        public void Colours()
        {
            for (int p = 0; p < 4; p++)
                for (int i = 0; i < 16; i++)
                    V.LoadColour(p * 16 + i, (ushort)(i == 0 ? 0 : (i & 7) << 1 | ((i >> 3) * 4 + (p & 3)) << 5 | ((7 - (i & 3) + p) & 7) << 9));
        }

        public int CramOf(int pal, int idx) => V.Cram[pal * 16 + idx];

        public void Tile(int n, int colour) { for (int i = 0; i < 32; i++) V.Vram[n * 32 + i] = (byte)(colour << 4 | colour); }

        public void TileRows(int n, params int[] rowColours) { for (int r = 0; r < 8; r++) for (int k = 0; k < 4; k++) V.Vram[n * 32 + r * 4 + k] = (byte)(rowColours[r] << 4 | rowColours[r]); }

        /// <summary>A tile with colour <paramref name="c"/> in its left half and 0 (transparent) in its right half.</summary>
        public void TileLeftHalf(int n, int c) { for (int r = 0; r < 8; r++) { V.Vram[n * 32 + r * 4] = (byte)(c << 4 | c); V.Vram[n * 32 + r * 4 + 1] = (byte)(c << 4 | c); V.Vram[n * 32 + r * 4 + 2] = 0; V.Vram[n * 32 + r * 4 + 3] = 0; } }

        public void Cell(int plane, int cx, int cy, int entry, int width = 64)
        {
            int a = plane + (cy * width + cx) * 2;
            V.Vram[a] = (byte)(entry >> 8); V.Vram[a + 1] = (byte)entry;
        }

        public void Sprite(int index, int x, int y, int w, int h, int tile, int pal = 0, bool prio = false, int link = 0, bool hflip = false, bool vflip = false)
        {
            int a = SatAddr + index * 8;
            V.Vram[a] = (byte)((y + 128) >> 8); V.Vram[a + 1] = (byte)(y + 128);
            V.Vram[a + 2] = (byte)((w - 1) << 2 | (h - 1)); V.Vram[a + 3] = (byte)link;
            int attr = tile | pal << 13 | (prio ? 0x8000 : 0) | (hflip ? 0x800 : 0) | (vflip ? 0x1000 : 0);
            V.Vram[a + 4] = (byte)(attr >> 8); V.Vram[a + 5] = (byte)attr;
            int xr = x + 128;
            V.Vram[a + 6] = (byte)(xr >> 8); V.Vram[a + 7] = (byte)xr;
        }

        public void SetHScroll(int line, int a, int b) { int at = HScroll + line * 4; V.Vram[at] = (byte)(a >> 8); V.Vram[at + 1] = (byte)a; V.Vram[at + 2] = (byte)(b >> 8); V.Vram[at + 3] = (byte)b; }

        public void Frame() { V.ResetPictureState(); V.RunTo(V.Time + V.Timeline.MasterClocksPerFrame * 2); }
        public uint Px(int x, int y) => V.Picture[y * MdVdp.PictureStride + x];

        public void Tick(int cycles = 4) { T += cycles * 7L; }
        public void Ctl(ushort v) { Tick(); V.WriteControl(v, T); if (V.Time > T) T = V.Time; }
        public void Reg(int r, int val) => Ctl((ushort)(0x8000 | r << 8 | val));
        public void Cmd(int code, int addr) { Ctl((ushort)((code & 3) << 14 | addr & 0x3FFF)); Ctl((ushort)((code >> 2 & 0xF) << 4 | addr >> 14 & 3)); }
        public void Data(ushort v) { Tick(); V.WriteData(v, T); if (V.Time > T) T = V.Time; }
        /// <summary>Let the FIFO empty: queued data port writes reach their memory at a later slot, and what they do (the sprite cache) depends on the registers at that moment.</summary>
        public void Settle() { V.RunTo(V.Time + 20000); T = V.Time; }
    }

    public static int Run(string[] args)
    {
        Console.WriteLine("== Genesis picture, session, saves and mappers self-test");
        // proof that the checks can fail: --slot-set "winstretch=0;h32stretch=0" (or any other experiment name) runs the same checks on the old rule
        if (M68kTestCli.Arg(args, "--slot-set") is { } slotSet) { MdRunCli.ApplySlotSet(slotSet); Console.WriteLine($"   (experiment settings: {slotSet})"); }
        Planes();
        Scroll();
        ColumnScrollAndSizes();
        WindowTests();
        Sprites();
        ShadowHighlight();
        ModesAndTiming();
        Interlace();
        Session();
        Mappers();
        Console.WriteLine($"== {pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }

    // =====================================================================================================================

    private static void Planes()
    {
        Group("planes, priority and the backdrop");
        var p = new Pic();
        p.V.Reg[7] = 0x03; p.V.LoadColour(3, 0x0E00);
        p.Frame();
        Check("an empty picture is the backdrop colour (register 7: palette 0, colour 3)", p.Px(0, 0) == Argb(0x0E00, 0) && p.Px(319, 223) == Argb(0x0E00, 0) && p.Px(160, 100) == Argb(0x0E00, 0));
        Check("H40 NTSC 224 lines is a 320 x 224 picture", p.V.PictureWidth == 320 && p.V.PictureHeight == 224);

        p = new Pic();
        p.Cell(PlaneB, 0, 0, 1); p.Cell(PlaneB, 1, 0, 0x2002);      // cell 1 uses palette line 1
        p.Frame();
        Check("plane B draws a tile at the top left in its palette colour", p.Px(0, 0) == Argb(p.CramOf(0, 1), 0) && p.Px(7, 7) == Argb(p.CramOf(0, 1), 0));
        Check("... the tile ends at the cell edge, the backdrop (black) beyond", p.Px(16, 0) == Argb(0, 0));
        Check("... and the palette line in the name table entry selects the colour line", p.Px(8, 0) == Argb(p.CramOf(1, 2), 0), $"{p.Px(8, 0):X8} {Argb(p.CramOf(1, 2), 0):X8}");

        p = new Pic();
        p.Cell(PlaneB, 0, 0, 1); p.Cell(PlaneA, 0, 0, 2); p.Cell(PlaneB, 1, 0, 1); p.Cell(PlaneA, 1, 0, 0);
        p.Frame();
        Check("plane A is drawn over plane B (both without the priority bit)", p.Px(2, 2) == Argb(p.CramOf(0, 2), 0));
        Check("a transparent plane A pixel (colour 0) shows plane B behind it", p.Px(10, 2) == Argb(p.CramOf(0, 1), 0));

        p = new Pic();
        p.Cell(PlaneA, 0, 0, 2); p.Cell(PlaneB, 0, 0, 0x8001);                  // B has the priority bit, A does not
        p.Cell(PlaneA, 1, 0, 0x8002); p.Cell(PlaneB, 1, 0, 0x8001);              // both have it
        p.Frame();
        Check("a high-priority plane B tile covers a low-priority plane A tile", p.Px(2, 2) == Argb(p.CramOf(0, 1), 0));
        Check("with both high, plane A is on top again", p.Px(10, 2) == Argb(p.CramOf(0, 2), 0));

        p = new Pic();
        p.Tile(5, 0); p.V.Vram[5 * 32] = 0x12; p.V.Vram[5 * 32 + 1] = 0x34; p.V.Vram[5 * 32 + 2] = 0x56; p.V.Vram[5 * 32 + 3] = 0x70;   // row 0: 1 2 3 4 5 6 7 0
        p.Cell(PlaneA, 0, 0, 5); p.Cell(PlaneA, 1, 0, 0x0805); p.Cell(PlaneA, 2, 0, 0x1005); p.Cell(PlaneA, 3, 0, 0x1805);
        p.Frame();
        Check("a tile's pixels run left to right, high nibble first", p.Px(0, 0) == Argb(p.CramOf(0, 1), 0) && p.Px(6, 0) == Argb(p.CramOf(0, 7), 0) && p.Px(7, 0) == Argb(0, 0));
        Check("horizontal flip mirrors the row", p.Px(8, 0) == Argb(0, 0) && p.Px(9, 0) == Argb(p.CramOf(0, 7), 0) && p.Px(15, 0) == Argb(p.CramOf(0, 1), 0));
        Check("vertical flip shows the tile's last row at the top (zero pixels: backdrop)", p.Px(16, 0) == Argb(0, 0) && p.Px(16, 7) == Argb(p.CramOf(0, 1), 0));
        Check("both flips together", p.Px(24 + 1, 7) == Argb(p.CramOf(0, 7), 0));

        p = new Pic();
        p.Cell(PlaneA, 0, 0, 2); p.Cell(PlaneA, 1, 0, 3);
        p.V.Reg[0] |= 0x20;
        p.Frame();
        Check("the left column blank bit (register 0 bit 5) shows the backdrop in the first 8 pixels", p.Px(0, 0) == Argb(0, 0) && p.Px(7, 5) == Argb(0, 0) && p.Px(8, 0) == Argb(p.CramOf(0, 3), 0));

        p = new Pic();
        p.Cell(PlaneB, 0, 0, 1);
        p.Cell(PlaneB, 1, 0, 2);
        p.V.Reg[16] = 0x02;      // the invalid width code: every row reads row 0 (MacDonald's window test)
        p.Cell(PlaneB, 0, 1, 7); p.Cell(PlaneB, 1, 1, 7);
        p.Frame();
        Check("the invalid plane width code repeats the name table's first row on every row (MacDonald's window test)", p.Px(2, 12) == Argb(p.CramOf(0, 1), 0) && p.Px(10, 100) == Argb(p.CramOf(0, 2), 0), $"{p.Px(2, 12):X8}");

        // plane sizes: 32 cells wide (register 16 = 0, rows 64 bytes apart, the plane repeats every 256 pixels) and 128 cells wide (register 16 = 3, rows 256 bytes apart, 1024 pixels)
        p = new Pic();
        p.V.Reg[16] = 0x00;
        p.Cell(PlaneA, 31, 0, 2, 32); p.Cell(PlaneA, 0, 1, 3, 32);
        p.Frame();
        Check("a 32-cell wide plane: cell 31 is the last of its row (x 248..255) and the plane repeats after 256 pixels", p.Px(250, 2) == Argb(p.CramOf(0, 2), 0) && p.Px(2, 2) == Argb(0, 0) && p.Px(256 + 2, 12) == Argb(p.CramOf(0, 3), 0));
        Check("... its second row is 64 bytes after the first", p.Px(2, 12) == Argb(p.CramOf(0, 3), 0));
        p = new Pic();
        p.V.Reg[16] = 0x03;
        p.Cell(PlaneA, 100, 0, 2, 128); p.Cell(PlaneA, 0, 1, 3, 128); p.Cell(PlaneA, 127, 0, 4, 128);
        p.SetHScroll(0, 224, 0);        // plane x = screen x - 224 (mod 1024): cell 100 (x 800..807) shows at screen x 0
        p.Frame();
        Check("a 128-cell wide plane: cell 100 is reached by a scroll of 224 and shows at the left edge", p.Px(2, 2) == Argb(p.CramOf(0, 2), 0) && p.Px(10, 2) == Argb(0, 0));
        Check("... cell 127 is the last (its right neighbour is cell 0 of the next repeat) and rows are 256 bytes apart", p.Px(218, 2) == Argb(p.CramOf(0, 4), 0) && p.Px(226, 12) == Argb(p.CramOf(0, 3), 0), $"{p.Px(218, 2):X8} {p.Px(226, 12):X8}");
    }

    // =====================================================================================================================

    private static void Scroll()
    {
        Group("horizontal and vertical scrolling");
        var p = new Pic();
        p.Cell(PlaneA, 1, 0, 2); p.Cell(PlaneA, 63, 0, 3);
        p.SetHScroll(0, 8, 0);
        p.Frame();
        Check("a horizontal scroll of 8 moves plane A 8 pixels to the right", p.Px(16, 0) == Argb(p.CramOf(0, 2), 0) && p.Px(8, 0) == Argb(0, 0));
        Check("... and the plane wraps round: its last cell (63) shows at the left edge", p.Px(0, 0) == Argb(p.CramOf(0, 3), 0));

        p = new Pic();
        p.Cell(PlaneA, 0, 0, 2); p.Cell(PlaneA, 0, 1, 3); p.Cell(PlaneA, 0, 2, 4);
        p.SetHScroll(0, 0, 0);
        p.Frame();
        Check("rows of cells line up: three cells one above the other", p.Px(3, 3) == Argb(p.CramOf(0, 2), 0) && p.Px(3, 11) == Argb(p.CramOf(0, 3), 0) && p.Px(3, 19) == Argb(p.CramOf(0, 4), 0));

        // per-line horizontal scroll (register 11 bits 1-0 = 3): each line has its own entry
        p = new Pic();
        p.Cell(PlaneA, 2, 0, 5);
        p.V.Reg[11] = 3;
        for (int l = 0; l < 8; l++) p.SetHScroll(l, l * 8, 0);
        p.Frame();
        bool lines = true;
        for (int l = 0; l < 8; l++) lines &= p.Px(16 + l * 8, l) == Argb(p.CramOf(0, 5), 0) && p.Px(15 + l * 8, l) != Argb(p.CramOf(0, 5), 0);
        Check("per-line scroll: line l has its own offset (8 * l)", lines);

        p = new Pic();
        p.Cell(PlaneA, 2, 0, 5);
        p.V.Reg[11] = 2;
        p.SetHScroll(0, 24, 0); p.SetHScroll(1, 99, 0); p.SetHScroll(7, 99, 0); p.SetHScroll(8, 0, 0);
        p.Frame();
        Check("per-8-line scroll uses the entry of the first line of each cell row for all 8 lines", p.Px(40, 0) == Argb(p.CramOf(0, 5), 0) && p.Px(40, 7) == Argb(p.CramOf(0, 5), 0) && p.Px(16, 0) != Argb(p.CramOf(0, 5), 0));

        // plane B has its own scroll value (the second word of an entry)
        p = new Pic();
        p.Cell(PlaneB, 0, 0, 6);
        p.SetHScroll(0, 0, 16);
        p.Frame();
        Check("plane B's horizontal scroll is the second word of the entry", p.Px(18, 0) == Argb(p.CramOf(0, 6), 0) && p.Px(2, 0) != Argb(p.CramOf(0, 6), 0));

        // vertical scroll
        p = new Pic();
        p.Cell(PlaneA, 0, 1, 7); p.Cell(PlaneA, 2, 1, 8);
        p.V.Vsram[0] = 8;
        p.Frame();
        Check("a vertical scroll of 8 moves plane A up by one cell row", p.Px(3, 0) == Argb(p.CramOf(0, 7), 0) && p.Px(19, 0) == Argb(p.CramOf(0, 8), 0));

        p = new Pic();
        p.Cell(PlaneA, 0, 1, 7); p.Cell(PlaneA, 2, 1, 8);
        p.V.Reg[11] = 4;                 // per two-cell column
        p.V.Vsram[0] = 8; p.V.Vsram[2] = 0;
        p.Frame();

        Check("per-column vertical scroll: column 0 scrolled by 8 and column 1 (x 16..31) by 0", p.Px(3, 0) == Argb(p.CramOf(0, 7), 0) && p.Px(19, 0) != Argb(p.CramOf(0, 8), 0) && p.Px(19, 8) == Argb(p.CramOf(0, 8), 0), $"{p.Px(3, 0):X8} {p.Px(19, 0):X8} {p.Px(19, 8):X8} want {Argb(p.CramOf(0, 7), 0):X8} {Argb(p.CramOf(0, 8), 0):X8}");

        p = new Pic();
        p.Cell(PlaneB, 0, 1, 7);
        p.V.Vsram[1] = 8;
        p.Frame();
        Check("plane B's vertical scroll is the odd VSRAM word", p.Px(3, 0) == Argb(p.CramOf(0, 7), 0));

        p = new Pic();
        p.Cell(PlaneA, 0, 31, 9); p.Cell(PlaneA, 0, 0, 2);
        p.V.Vsram[0] = 248;
        p.Frame();
        Check("the plane wraps vertically at its height (a 32-cell plane is 256 lines): cell row 31 at the top, row 0 after it", p.Px(2, 0) == Argb(p.CramOf(0, 9), 0) && p.Px(2, 8) == Argb(p.CramOf(0, 2), 0));
    }

    // =====================================================================================================================

    /// <summary>Column vertical scroll (R11 bit 2) with a fine horizontal scroll, and the plane sizes the VDP cannot hold: both measured on BlastEm with probe scenes (every pixel row of the plane readable).</summary>
    private static void ColumnScrollAndSizes()
    {
        Group("2-cell column scroll with a horizontal scroll, and plane sizes that do not fit");
        // plane A: each cell row cy is tile 1 + cy % 14 (a solid colour per row); column c of plane A scrolls by VSRAM[2c] = 8 * (c + 2), plane B by 0x7F8 ... the last column's pair is ANDed for the left stretch
        int Colour(int vs) => (vs >> 3) % 14 + 1;
        Pic Setup(int hscroll, bool h40 = true, int r17 = 0)
        {
            var q = new Pic(h40);
            q.V.Reg[11] = 0x04;
            q.V.Reg[17] = (byte)r17;
            for (int cy = 0; cy < 32; cy++) for (int cx = 0; cx < 64; cx++) q.Cell(PlaneA, cx, cy, 1 + cy % 14);
            for (int c = 0; c < 20; c++) { q.V.Vsram[2 * c] = (ushort)(8 * (c + 2)); q.V.Vsram[2 * c + 1] = 0x7F8; }
            q.V.Vsram[38] = 40; q.V.Vsram[39] = 24;            // the last column: 40 & 24 = 8
            q.SetHScroll(0, hscroll, 0);
            q.Frame();
            return q;
        }
        var p = Setup(0);
        Check("column scroll without a horizontal scroll: column c (16 pixels) uses VSRAM[2c]", p.Px(2, 0) == Argb(p.CramOf(0, Colour(16)), 0) && p.Px(18, 0) == Argb(p.CramOf(0, Colour(24)), 0) && p.Px(302, 0) == Argb(p.CramOf(0, Colour(160)), 0));
        p = Setup(13);
        // hscroll 13: the plane's cell pairs start at screen x = 13 + 16 k; the entries count from the first of them, and the stretch before it uses the last column's two entries ANDed
        Check("with a horizontal scroll of 13 the first column starts at x = 13 (hscroll mod 16), not at 0", p.Px(13, 0) == Argb(p.CramOf(0, Colour(16)), 0) && p.Px(28, 0) == Argb(p.CramOf(0, Colour(16)), 0), $"{p.Px(13, 0):X8} {p.Px(28, 0):X8}");
        Check("... the second at x = 29", p.Px(29, 0) == Argb(p.CramOf(0, Colour(24)), 0) && p.Px(44, 0) == Argb(p.CramOf(0, Colour(24)), 0));
        Check("... and the stretch to its left (x 0-12) scrolls by VSRAM[38] AND VSRAM[39] (the last column's pair: 40 and 24 give 8)", p.Px(2, 0) == Argb(p.CramOf(0, Colour(8)), 0) && p.Px(12, 0) == Argb(p.CramOf(0, Colour(8)), 0), $"{p.Px(2, 0):X8}");
        p = Setup(29);
        Check("a horizontal scroll of 29 starts the first column at x = 13 as well (29 mod 16)", p.Px(13, 0) == Argb(p.CramOf(0, Colour(16)), 0) && p.Px(12, 0) == Argb(p.CramOf(0, Colour(8)), 0));
        // H32: the stretch at the left uses vertical scroll 0 (oracle: probe scenes vsprobe 3 and vsprobe2 5-7, 9, and the fuzz families on H32); H40 keeps the two ANDed entries (above)
        p = Setup(13, false);
        Check("H32: with a horizontal scroll of 13 the first column starts at x = 13 as in H40 ...", p.Px(13, 0) == Argb(p.CramOf(0, Colour(16)), 0) && p.Px(28, 0) == Argb(p.CramOf(0, Colour(16)), 0) && p.Px(29, 0) == Argb(p.CramOf(0, Colour(24)), 0));
        Check("... and the stretch to its left (x 0-12) scrolls by 0, not by the two last entries ANDed (VSRAM[30] and [31] would give 136)", p.Px(2, 0) == Argb(p.CramOf(0, Colour(0)), 0) && p.Px(12, 0) == Argb(p.CramOf(0, Colour(0)), 0), $"{p.Px(2, 0):X8} want {Argb(p.CramOf(0, Colour(0)), 0):X8}");
        // a window at the left and a fine scroll: the part of the first pair the window leaves (from the window's edge to the edge + the fine scroll) takes the entry of the pair after it (oracle: vsprobe 3 scenes
        // 3-11 and the fuzz family with window and column scroll; the entry index is the window's width in 16-pixel steps)
        p = Setup(13, true, 2);        // window left of x = 32; plane pairs at 13 + 16 k
        Check("a window at the left (x < 32) with a fine scroll of 13: the pair that would be column 1 (x 29-44) uses the entry of column 2 from the window's edge on (x 32-44) ...", p.Px(35, 0) == Argb(p.CramOf(0, Colour(8 * 4)), 0) && p.Px(44, 0) == Argb(p.CramOf(0, Colour(8 * 4)), 0), $"{p.Px(35, 0):X8} want {Argb(p.CramOf(0, Colour(8 * 4)), 0):X8}");
        Check("... column 2 (x 45-60) keeps its own entry and column 3 starts at x = 61", p.Px(50, 0) == Argb(p.CramOf(0, Colour(8 * 4)), 0) && p.Px(60, 0) == Argb(p.CramOf(0, Colour(8 * 4)), 0) && p.Px(62, 0) == Argb(p.CramOf(0, Colour(8 * 5)), 0));
        p = Setup(0, true, 2);
        Check("a window at the left without a fine scroll changes nothing: x 32-47 is column 2, x 48 column 3", p.Px(33, 0) == Argb(p.CramOf(0, Colour(8 * 4)), 0) && p.Px(47, 0) == Argb(p.CramOf(0, Colour(8 * 4)), 0) && p.Px(49, 0) == Argb(p.CramOf(0, Colour(8 * 5)), 0));

        // plane sizes: a width of 128 cells forces a height of 32; a height of 128 with a width of 64 is a 64 x 64 plane
        var q = new Pic();
        q.V.Reg[16] = 0x13;            // 128 wide, 64 high
        q.V.Reg[11] = 0;
        q.Cell(PlaneA, 0, 0, 2, 128);
        q.V.Vram[PlaneA + 8192] = 0; q.V.Vram[PlaneA + 8193] = 3;        // what a 128 x 64 plane would show 32 rows down (cell row 32 starts 8 KB after the table's start)
        q.V.Vsram[0] = 256;
        q.Frame();
        Check("a plane 128 cells wide is only 32 high (a height code of 64 does not give 128 x 64): a vertical scroll of 256 wraps to the first row", q.Px(2, 0) == Argb(q.CramOf(0, 2), 0), $"{q.Px(2, 0):X8}");
        q = new Pic();
        q.V.Reg[16] = 0x31;            // 64 wide, 128 high
        q.Cell(PlaneA, 0, 0, 2, 64);
        q.V.Vram[PlaneA + 8192] = 0; q.V.Vram[PlaneA + 8193] = 3;
        q.V.Vsram[0] = 512;
        q.Frame();
        Check("a plane 64 cells wide with a height code of 128 is 64 x 64: a vertical scroll of 512 wraps to the first row", q.Px(2, 0) == Argb(q.CramOf(0, 2), 0), $"{q.Px(2, 0):X8}");
        q = new Pic();
        q.V.Reg[16] = 0x30;            // 32 wide, 128 high: valid
        q.Cell(PlaneA, 0, 0, 2, 32);
        q.Cell(PlaneA, 0, 64, 3, 32);
        q.V.Vsram[0] = 512;
        q.Frame();
        Check("a plane 32 cells wide and 128 high is valid: a vertical scroll of 512 shows cell row 64", q.Px(2, 0) == Argb(q.CramOf(0, 3), 0), $"{q.Px(2, 0):X8}");
    }

    private static void WindowTests()
    {
        Group("the window");
        var p = new Pic();
        p.Cell(PlaneA, 0, 0, 2); p.Cell(PlaneA, 4, 0, 2); p.Cell(PlaneA, 0, 4, 2);
        for (int cx = 0; cx < 4; cx++) p.Cell(Window, cx, 0, 3);
        p.V.Reg[17] = 0x02;               // left of x = 32
        p.V.Reg[18] = 0x00;
        p.Frame();
        Check("a window at the left (register 17 = 2) replaces plane A for x < 32", p.Px(3, 0) == Argb(p.CramOf(0, 3), 0) && p.Px(31, 0) == Argb(p.CramOf(0, 3), 0));
        Check("... and plane A shows from x = 32 on", p.Px(33, 0) == Argb(p.CramOf(0, 2), 0), $"{p.Px(33, 0):X8}");

        p = new Pic();
        for (int cx = 0; cx < 64; cx++) { p.Cell(Window, cx, 3, 4); p.Cell(PlaneA, cx, 3, 2); }
        p.V.Reg[17] = 0x00; p.V.Reg[18] = 0x83;            // from line 24 down
        p.Frame();
        Check("a window from line 24 down (register 18 = $83) covers the lines below it", p.Px(10, 24) == Argb(p.CramOf(0, 4), 0) && p.Px(10, 31) == Argb(p.CramOf(0, 4), 0));
        Check("... and not the line above", p.Px(10, 23) != Argb(p.CramOf(0, 4), 0));

        p = new Pic();
        for (int cx = 0; cx < 64; cx++) { p.Cell(Window, cx, 0, 5); p.Cell(PlaneA, cx, 0, 2); }
        p.V.Reg[17] = 0x8A; p.V.Reg[18] = 0x00;                // right of x = 160
        p.Frame();
        Check("a window at the right (register 17 bit 7): from x = 160", p.Px(159, 0) == Argb(p.CramOf(0, 2), 0) && p.Px(160, 0) == Argb(p.CramOf(0, 5), 0));

        // the window distortion bug (MacDonald): window at the left, plane A fine scroll not a multiple of 16 -> the plane column that fills the first screen column after the window comes from the column after it
        p = new Pic();
        for (int cx = 0; cx < 40; cx++) p.Cell(PlaneA, cx, 0, cx % 7 + 1);
        for (int cx = 0; cx < 4; cx++) p.Cell(Window, cx, 0, 9);
        p.V.Reg[17] = 0x02; p.SetHScroll(0, 0, 0);
        p.Frame();
        Check("with a fine scroll of 0 there is no distortion: x = 40 shows cell 5 (tile 6)", p.Px(40, 0) == Argb(p.CramOf(0, 5 % 7 + 1), 0) && p.Px(36, 0) == Argb(p.CramOf(0, 4 % 7 + 1), 0));
        p = new Pic();
        for (int cx = 0; cx < 40; cx++) p.Cell(PlaneA, cx, 0, cx % 7 + 1);
        for (int cx = 0; cx < 4; cx++) p.Cell(Window, cx, 0, 9);
        p.V.Reg[17] = 0x02; p.SetHScroll(0, 8, 0);       // plane A moves right by 8 pixels
        p.Frame();
        // screen x 32..47 shows plane pixels 24..39: cells 3 and 4 (plane column 1 = cells 2,3 is replaced by column 2 = cells 4,5; cell 4 belongs to column 2): the cell displayed at x 32..39 is cell 3 -> read from cell 5
        Check("with the plane scrolled by 8 the first cell after the window is read from two cells further on (MacDonald's window bug)", p.Px(33, 0) == Argb(p.CramOf(0, 5 % 7 + 1), 0) && p.Px(41, 0) == Argb(p.CramOf(0, 4 % 7 + 1), 0), $"{p.Px(33, 0):X8} {p.Px(41, 0):X8}");
    }

    // =====================================================================================================================

    private static void Sprites()
    {
        Group("sprites");
        var p = new Pic();
        p.Sprite(0, 100, 50, 1, 1, 4, 0, false, 0);
        p.Frame();
        Check("a sprite at (100, 50) draws its 8 x 8 tile", p.Px(100, 50) == Argb(p.CramOf(0, 4), 0) && p.Px(107, 57) == Argb(p.CramOf(0, 4), 0));
        Check("... and nothing outside it", p.Px(99, 50) != Argb(p.CramOf(0, 4), 0) && p.Px(108, 50) != Argb(p.CramOf(0, 4), 0) && p.Px(100, 49) != Argb(p.CramOf(0, 4), 0) && p.Px(100, 58) != Argb(p.CramOf(0, 4), 0));

        p = new Pic();
        p.Sprite(0, 60, 20, 2, 2, 4, 0, false, 0);        // tiles 4 5 | 6 7 column by column
        p.Frame();
        Check("a 2 x 2 sprite's tiles run down the columns (tile, tile + 1 below it, then the next column)", p.Px(61, 21) == Argb(p.CramOf(0, 4), 0) && p.Px(61, 29) == Argb(p.CramOf(0, 5), 0) && p.Px(69, 21) == Argb(p.CramOf(0, 6), 0) && p.Px(69, 29) == Argb(p.CramOf(0, 7), 0));

        p = new Pic();
        p.Sprite(0, 60, 20, 2, 2, 4, 0, false, 0, hflip: true);
        p.Frame();
        Check("a horizontally flipped 2 x 2 sprite swaps its columns", p.Px(61, 21) == Argb(p.CramOf(0, 6), 0) && p.Px(69, 21) == Argb(p.CramOf(0, 4), 0));
        p = new Pic();
        p.Sprite(0, 60, 20, 2, 2, 4, 0, false, 0, vflip: true);
        p.Frame();
        Check("a vertically flipped 2 x 2 sprite swaps its rows", p.Px(61, 21) == Argb(p.CramOf(0, 5), 0) && p.Px(61, 29) == Argb(p.CramOf(0, 4), 0));

        p = new Pic();
        p.Cell(PlaneA, 12, 6, 2);
        p.Sprite(0, 96, 48, 1, 1, 4, 0, false, 0);
        p.Frame();
        Check("a low-priority sprite is drawn over a low-priority plane", p.Px(98, 50) == Argb(p.CramOf(0, 4), 0));
        p = new Pic();
        p.Cell(PlaneA, 12, 6, 0x8002);
        p.Sprite(0, 96, 48, 1, 1, 4, 0, false, 0);
        p.Frame();
        Check("... and under a high-priority plane tile", p.Px(98, 50) == Argb(p.CramOf(0, 2), 0));
        p = new Pic();
        p.Cell(PlaneA, 12, 6, 0x8002);
        p.Sprite(0, 96, 48, 1, 1, 4, 0, true, 0);
        p.Frame();
        Check("a high-priority sprite beats a high-priority plane", p.Px(98, 50) == Argb(p.CramOf(0, 4), 0));

        p = new Pic();
        p.Sprite(0, 20, 20, 1, 1, 4, 0, true, 1);        // sprite 0 (high priority) links to sprite 1 (low priority), both at the same place
        p.Sprite(1, 20, 20, 1, 1, 5, 0, false, 0);
        p.Frame();
        Check("of two overlapping sprites the first in the link list is on top", p.Px(22, 22) == Argb(p.CramOf(0, 4), 0));
        p = new Pic();
        p.Sprite(0, 20, 20, 1, 1, 4, 0, false, 1);
        p.Sprite(1, 20, 20, 1, 1, 5, 0, true, 0);
        p.Frame();
        Check("... whatever their priority bits say (the priority bit only decides against the planes)", p.Px(22, 22) == Argb(p.CramOf(0, 4), 0));

        p = new Pic();
        p.Sprite(0, 20, 20, 1, 1, 4, 0, false, 5);        // link to sprite 5, skipping 1..4 which sit elsewhere in the table
        p.Sprite(1, 100, 100, 1, 1, 6, 0, false, 0);
        p.Sprite(5, 40, 20, 1, 1, 7, 0, false, 0);
        p.Frame();
        Check("the link list is followed: sprite 5 is drawn, sprite 1 (not linked to) is not", p.Px(42, 22) == Argb(p.CramOf(0, 7), 0) && p.Px(102, 102) != Argb(p.CramOf(0, 6), 0));

        // per-line limits
        p = new Pic();
        for (int i = 0; i < 21; i++) p.Sprite(i, 8 + i * 12, 100, 1, 1, 4, 0, false, i == 20 ? 0 : i + 1);
        p.Frame();
        int drawn = Enumerable.Range(0, 21).Count(i => p.Px(8 + i * 12 + 2, 102) == Argb(p.CramOf(0, 4), 0));
        Check("H40: at most 20 sprites on a line (the 21st is not drawn)", drawn == 20 && p.Px(8 + 20 * 12 + 2, 102) != Argb(p.CramOf(0, 4), 0), $"{drawn}");
        Check("... and the sprite overflow flag (status bit 6) is set; reading the status clears it", (p.V.ReadStatus(p.V.Time) & 0x40) != 0 && (p.V.ReadStatus(p.V.Time + 100) & 0x40) == 0);

        p = new Pic(h40: false);
        for (int i = 0; i < 17; i++) p.Sprite(i, 8 + i * 12, 100, 1, 1, 4, 0, false, i == 16 ? 0 : i + 1);
        p.Frame();
        drawn = Enumerable.Range(0, 17).Count(i => p.Px(8 + i * 12 + 2, 102) == Argb(p.CramOf(0, 4), 0));
        Check("H32: at most 16 sprites on a line", drawn == 16 && p.V.PictureWidth == 256, $"{drawn} {p.V.PictureWidth}");

        // the cell limit (32 in H32) cuts a sprite after its first tile columns: a horizontally flipped sprite shows its tile columns right to left, so the cut takes its LEFT cell and keeps the right-hand ones
        // (BlastEm, the interlace-2 random scene f_il2 number 5: a flipped 4-cell sprite after 29 cells had its left cell missing and its right cell drawn; the old rule drew the left three)
        for (int variant = 0; variant < 2; variant++)
        {
            p = new Pic(h40: false);
            int filler = variant == 0 ? 8 : 7;                                         // 29 cells before the flipped sprite (7 sprites of 4 and one of 1), or 28
            for (int i = 0; i < filler; i++) p.Sprite(i, 8, 100, i < 7 ? 4 : 1, 1, 1, 0, false, i + 1);
            p.Sprite(filler, 100, 100, 4, 1, 4, 0, false, 0, hflip: true);            // tiles 4 to 7, shown 7 6 5 4 from left to right
            p.Frame();
            uint c4 = Argb(p.CramOf(0, 4), 0), c5 = Argb(p.CramOf(0, 5), 0), c6 = Argb(p.CramOf(0, 6), 0), c7 = Argb(p.CramOf(0, 7), 0);
            if (variant == 0)
                Check("H32: a flipped 4-cell sprite cut by the 32-cell limit (29 before it) keeps its right three cells, the left one is missing", p.Px(102, 102) != c7 && p.Px(110, 102) == c6 && p.Px(118, 102) == c5 && p.Px(126, 102) == c4, $"{p.Px(102, 102):X8} {p.Px(110, 102):X8} {p.Px(118, 102):X8} {p.Px(126, 102):X8}");
            else
                Check("... and with exactly 32 cells nothing is cut: all four cells show (tile 7 at the left)", p.Px(102, 102) == c7 && p.Px(110, 102) == c6 && p.Px(118, 102) == c5 && p.Px(126, 102) == c4, $"{p.Px(102, 102):X8} {p.Px(110, 102):X8} {p.Px(118, 102):X8} {p.Px(126, 102):X8}");
        }

        p = new Pic();
        p.Sprite(0, 50, 50, 1, 1, 4, 0, false, 1);
        p.Sprite(1, 52, 52, 1, 1, 5, 0, false, 0);
        p.Frame();
        Check("two overlapping sprites set the collision flag (status bit 5)", (p.V.ReadStatus(p.V.Time) & 0x20) != 0 && (p.V.ReadStatus(p.V.Time + 100) & 0x20) == 0);

        // masking (Nemesis' sprite masking test ROM; the first sprite of a line at X = 0 does not mask)
        p = new Pic();
        p.Sprite(0, 20, 20, 1, 1, 4, 0, false, 1);
        p.Sprite(1, -128, 20, 1, 1, 4, 0, false, 2);       // X = 0 on the screen's coordinate -128
        p.Sprite(2, 60, 20, 1, 1, 6, 0, false, 0);
        p.Frame();
        Check("a sprite at X = 0 after another sprite on the line hides the sprites that follow (Nemesis' sprite masking ROM)", p.Px(22, 22) == Argb(p.CramOf(0, 4), 0) && p.Px(62, 22) != Argb(p.CramOf(0, 6), 0));
        p = new Pic();
        p.Sprite(0, -128, 20, 1, 1, 4, 0, false, 1);
        p.Sprite(1, 60, 20, 1, 1, 6, 0, false, 0);
        p.Frame();
        Check("... but the first sprite of a line at X = 0 masks nothing", p.Px(62, 22) == Argb(p.CramOf(0, 6), 0));

        p = new Pic();
        p.Sprite(0, 300, 40, 1, 1, 4, 0, false, 1);     // partly off the right edge
        p.Sprite(1, 4, 40, 1, 1, 6, 0, false, 0);
        p.Frame();
        Check("sprites are clipped at the right edge of the picture and still counted", p.Px(300, 42) == Argb(p.CramOf(0, 4), 0) && p.Px(6, 42) == Argb(p.CramOf(0, 6), 0));
        p = new Pic();
        p.Sprite(0, 12, 40, 1, 1, 7, 2, false, 0);
        p.Frame();
        Check("a sprite uses the palette line in its attributes", p.Px(14, 42) == Argb(p.CramOf(2, 7), 0));

        // the sprite attribute cache (BlastEm, scenes whose table address register named another table while the memories were loaded): Y, size and link of each sprite are kept as the table is written
        // through the data port while the register names it; the position, tile and attributes are read from VRAM when the line is drawn
        p = new Pic();
        p.Sprite(0, 100, 100, 1, 1, 4, 0, false, 0);          // poked into $6000: Y 100
        p.V.ResetPictureState();                              // the cache is taken from it (as a tool or a bridge that sets the memories directly does)
        p.Reg(5, 0x6800 >> 9);                                // the register names another table now ...
        p.Cmd(1, 0x6800); p.Data((ushort)(50 + 128)); p.Data(0x0000);            // ... and a write there (Y 50, size 1 x 1, link 0) goes into the cache
        p.Settle();
        p.Cmd(1, SatAddr); p.Data((ushort)(170 + 128));       // a write to $6000 while the register names $6800 does not
        p.Settle();
        p.Reg(5, SatAddr >> 9);
        p.V.RunTo(p.V.Time + p.V.Timeline.MasterClocksPerFrame * 2);
        uint t4 = Argb(p.CramOf(0, 4), 0);
        Check("the sprite attribute cache: a sprite's Y comes from the cache (50, written while the register named $6800), its X and tile from the table in VRAM", p.Px(102, 52) == t4 && p.Px(102, 102) != t4 && p.Px(102, 172) != t4, $"{p.Px(102, 52):X8} {p.Px(102, 102):X8} {p.Px(102, 172):X8}");
        p.Cmd(1, SatAddr); p.Data((ushort)(150 + 128));
        p.V.RunTo(p.V.Time + p.V.Timeline.MasterClocksPerFrame * 2);
        Check("... and a write to the table the register names now moves it (Y 150)", p.Px(102, 152) == t4 && p.Px(102, 52) != t4);

        // a sprite's Y word has nine bits outside interlace mode 2
        p = new Pic();
        p.Sprite(0, 100, 50, 1, 1, 4, 0, false, 0);
        p.V.Vram[SatAddr] |= 2;                               // bit 9 of the Y word
        p.Frame();
        Check("a sprite's Y word has 9 bits: with bit 9 set (Y = 512 + 178) the sprite is still at line 50 (oracle: attribute cache scenes with random Y words)", p.Px(102, 52) == t4, $"{p.Px(102, 52):X8}");

        // the link list: H32 has a table of 64 sprites (512 bytes); the walk follows links up to 79 and the Y, size and link of those sprites come from the cache, but their position and attributes are read from the table
        // modulo 512: sprite 70 takes them from sprite 6 (Nemesis' sprite masking ROM: sprite 64 is at the X of sprite 0, off the left edge; the attribute cache scenes: a link to 78 shows the position and palette of sprite 14)
        p = new Pic(h40: false);
        p.Sprite(0, 20, 20, 1, 1, 4, 0, false, 70);           // 70 is past the 64 entries of an H32 table
        p.Sprite(70, 140, 20, 1, 1, 5, 0, false, 5);          // its own position and tile (140, 5) are not used ...
        p.Sprite(6, 60, 20, 1, 1, 6, 0, false, 0);            // ... sprite 6's (60, 6) are
        p.Sprite(5, 100, 20, 1, 1, 7, 0, false, 85);
        p.Sprite(85, 160, 20, 1, 1, 2, 0, false, 7);          // 85 is past the 80 the cache covers: the list ends at sprite 5
        p.Sprite(7, 200, 20, 1, 1, 3, 0, false, 0);
        p.Frame();
        Check("H32: the walk goes on through a link to sprite 70 (sprite 5 behind it is drawn) ...", p.Px(22, 22) == t4 && p.Px(102, 22) == Argb(p.CramOf(0, 7), 0), $"{p.Px(102, 22):X8}");
        Check("... sprite 70 shows at the position and with the tile of sprite 6 (modulo 512), not its own", p.Px(62, 22) == Argb(p.CramOf(0, 6), 0) && p.Px(142, 22) != Argb(p.CramOf(0, 5), 0), $"{p.Px(62, 22):X8} {p.Px(142, 22):X8}");
        Check("... and a link of 80 or more ends the list", p.Px(162, 22) != Argb(p.CramOf(0, 2), 0) && p.Px(202, 22) != Argb(p.CramOf(0, 3), 0));
        p = new Pic();
        p.Sprite(0, 20, 20, 1, 1, 4, 0, false, 70);
        p.Sprite(70, 60, 20, 1, 1, 6, 0, false, 0);
        p.Frame();
        Check("H40: sprite 70 is part of the table and is drawn at its own position", p.Px(62, 22) == Argb(p.CramOf(0, 6), 0));
    }

    // =====================================================================================================================

    private static void ShadowHighlight()
    {
        Group("shadow and highlight");
        var p = new Pic();
        p.V.Reg[12] |= 8;
        p.Cell(PlaneA, 0, 0, 2); p.Cell(PlaneA, 1, 0, 0x8002);
        p.Frame();
        Check("shadow/highlight mode: a low-priority plane tile is in shadow", p.Px(2, 2) == Argb(p.CramOf(0, 2), 1));
        Check("... a high-priority one is normal", p.Px(10, 2) == Argb(p.CramOf(0, 2), 0));
        Check("... the backdrop is in shadow too", p.Px(100, 100) == Argb(0, 1));

        p = new Pic();
        p.V.Reg[12] |= 8; p.V.Reg[7] = 0x02;
        p.TileLeftHalf(9, 4);
        p.Cell(PlaneA, 0, 0, 0x8009);                      // high-priority tile, right half transparent: the backdrop under it is not darkened (vctest, oracle)
        p.Frame();
        Check("the backdrop under a high-priority tile's transparent pixels stays normal (vctest, seen in BlastEm)", p.Px(2, 2) == Argb(p.CramOf(0, 4), 0) && p.Px(6, 2) == Argb(p.CramOf(0, 2), 0), $"{p.Px(6, 2):X8}");

        p = new Pic();
        p.V.Reg[12] |= 8;
        p.Cell(PlaneA, 0, 0, 0x8002);
        p.Sprite(0, 0, 0, 1, 1, 0xF, 3, true, 0);       // palette 3 colour 15: shadow operator (tile 15 is solid colour 15); with the priority bit it wins over the high-priority plane tile
        p.Frame();
        Check("a sprite pixel of palette 3 colour 15 shadows what is below it without drawing itself", p.Px(2, 2) == Argb(p.CramOf(0, 2), 1));
        p = new Pic();
        p.V.Reg[12] |= 8;
        p.Cell(PlaneA, 0, 0, 0x8002);
        p.Tile(14, 14);
        p.Sprite(0, 0, 0, 1, 1, 14, 3, true, 0);       // colour 14 of palette 3: highlight operator (with the priority bit: it wins over the high-priority plane tile)
        p.Frame();
        Check("palette 3 colour 14 highlights a normal pixel", p.Px(2, 2) == Argb(p.CramOf(0, 2), 2));
        p = new Pic();
        p.V.Reg[12] |= 8;
        p.Cell(PlaneA, 0, 0, 2);
        p.Tile(14, 14);
        p.Sprite(0, 0, 0, 1, 1, 14, 3, false, 0);
        p.Frame();
        Check("... and brings a shadowed pixel back to normal", p.Px(2, 2) == Argb(p.CramOf(0, 2), 0));
        // oracle scenes (random planes and sprites, 16 + 20 scenes against BlastEm): a sprite is part of the priority ladder before it acts
        p = new Pic();
        p.V.Reg[12] |= 8;
        p.Cell(PlaneA, 0, 0, 0x8002);                     // a high-priority plane A pixel (colour 2) under a LOW-priority shadow operator sprite
        p.Sprite(0, 0, 0, 1, 1, 0xF, 3, false, 0);
        p.Frame();
        Check("a low-priority operator sprite under a visible high-priority plane pixel does nothing (the plane wins the ladder, the pixel stays normal)", p.Px(2, 2) == Argb(p.CramOf(0, 2), 0));
        p = new Pic();
        p.V.Reg[12] |= 8;
        p.Tile(5, 5); p.Cell(PlaneB, 0, 0, 0x2);           // plane B: low priority, colour 2
        p.Cell(PlaneA, 0, 0, 0x8000);                     // plane A: a high-priority tile that is transparent (tile 0) there
        p.Sprite(0, 0, 0, 1, 1, 4, 0, false, 0);           // a low-priority sprite (colour 4)
        p.Frame();
        Check("a low-priority sprite shows over a low-priority plane even where plane A has a transparent high-priority tile (the pixel is then normal, not shadowed)", p.Px(2, 2) == Argb(p.CramOf(0, 4), 0), $"{p.Px(2, 2):X8}");
        p = new Pic();
        p.V.Reg[12] |= 8;
        p.Cell(PlaneA, 0, 0, 0x0002);
        p.Sprite(0, 0, 0, 1, 1, 4, 0, false, 0);
        p.Frame();
        Check("a low-priority sprite over low-priority planes is itself in shadow", p.Px(2, 2) == Argb(p.CramOf(0, 4), 1));
        p = new Pic();
        p.V.Reg[12] |= 8;
        p.Tile(14, 14);
        p.Cell(PlaneA, 0, 0, 0x0002);
        p.Sprite(0, 0, 0, 1, 1, 14, 2, false, 0);          // colour 14 of palette 2 (an operator only in palette 3)
        p.Frame();
        Check("a low-priority sprite pixel of colour 14 outside palette 3 is drawn normal, not shadowed (oracle: random scenes, colour 14 over shadow is always normal)", p.Px(2, 2) == Argb(p.CramOf(2, 14), 0), $"{p.Px(2, 2):X8}");
        p = new Pic();
        p.V.Reg[12] |= 8;
        p.Tile(15, 15);
        p.Cell(PlaneA, 0, 0, 0x0002);
        p.Sprite(0, 0, 0, 1, 1, 15, 2, false, 0);          // colour 15 of palette 2 (an operator only in palette 3)
        p.Frame();
        Check("... but colour 15 outside palette 3 follows the state like any other colour: shadowed over shadow (oracle: 1,700 such pixels, never normal; the first guess had it normal)", p.Px(2, 2) == Argb(p.CramOf(2, 15), 1), $"{p.Px(2, 2):X8}");
        p = new Pic();
        p.V.Reg[12] |= 8;
        p.Sprite(0, 0, 0, 1, 1, 4, 0, true, 0); p.Sprite(1, 16, 0, 1, 1, 4, 0, false, 0); p.V.Vram[SatAddr + 3] = 1;
        p.Frame();
        Check("an ordinary sprite is drawn at normal brightness with the priority bit and in shadow without it", p.Px(2, 2) == Argb(p.CramOf(0, 4), 0) && p.Px(18, 2) == Argb(p.CramOf(0, 4), 1));

        // the left column blank in shadow/highlight mode (oracle, four random-scene families): the blank column is the backdrop in shadow; the planes in the next cell are the blanked cell's planes; sprites stay put
        p = new Pic();
        p.V.Reg[12] |= 8; p.V.Reg[0] |= 0x20; p.V.Reg[7] = 0x02;
        p.Cell(PlaneA, 0, 0, 0x8002); p.Cell(PlaneA, 1, 0, 0x8003); p.Cell(PlaneA, 2, 0, 0x8004);
        p.Frame();
        Check("shadow/highlight with the left column blank: the blank column shows the backdrop in shadow (a high-priority tile hidden under it does not make it normal)", p.Px(2, 2) == Argb(p.CramOf(0, 2), 1) && p.Px(7, 5) == Argb(p.CramOf(0, 2), 1), $"{p.Px(2, 2):X8}");
        Check("... the second cell (x 8-15) shows the first cell's plane pixels (tile 2) instead of its own (tile 3), the third its own", p.Px(10, 2) == Argb(p.CramOf(0, 2), 0) && p.Px(18, 2) == Argb(p.CramOf(0, 4), 0), $"{p.Px(10, 2):X8} {p.Px(18, 2):X8}");
        p = new Pic();
        p.V.Reg[12] |= 8; p.V.Reg[0] |= 0x20;
        p.Cell(PlaneA, 0, 0, 0x8002); p.Cell(PlaneA, 1, 0, 0x8003);
        p.Sprite(0, 8, 0, 1, 1, 5, 0, true, 0);
        p.Frame();
        Check("... a sprite in the second cell stays in the second cell", p.Px(10, 2) == Argb(p.CramOf(0, 5), 0), $"{p.Px(10, 2):X8}");
        p = new Pic();
        p.V.Reg[0] |= 0x20;
        p.Cell(PlaneA, 0, 0, 0x8002); p.Cell(PlaneA, 1, 0, 0x8003);
        p.Frame();
        Check("... without shadow/highlight mode the second cell is its own and the blank column normal", p.Px(10, 2) == Argb(p.CramOf(0, 3), 0) && p.Px(2, 2) == Argb(0, 0));
    }

    // =====================================================================================================================

    /// <summary>A 8x16 cell pattern (interlace mode 2: 64 bytes, rows 0-7 then 8-15): <paramref name="top"/> colour in the first eight rows, <paramref name="bottom"/> in the last eight.</summary>
    private static void Cell16(Pic p, int n, int top, int bottom)
    {
        for (int r = 0; r < 16; r++) for (int k = 0; k < 4; k++) p.V.Vram[n * 64 + r * 4 + k] = (byte)((r < 8 ? top : bottom) << 4 | (r < 8 ? top : bottom));
    }

    private static void Interlace()
    {
        Group("interlace (R12 bits 2-1): the field flag, and mode 2's double-height picture");
        // mode 2: cells are 8 x 16 (a name table entry's pattern is 64 bytes), the picture is 448 rows of both fields woven
        var p = new Pic();
        p.V.Reg[12] = 0x87;
        Cell16(p, 3, 1, 2);
        p.Cell(PlaneA, 0, 0, 3);
        p.Frame();
        Check("interlace mode 2: the finished frame is a double-height picture (448 rows) and the one-field picture stays 224 rows", p.V.WovenValid && p.V.WovenHeight == 448 && p.V.PictureHeight == 224 && p.V.PictureWidth == 320);
        uint c1 = Argb(p.CramOf(0, 1), 0), c2 = Argb(p.CramOf(0, 2), 0), bd = Argb(0, 0);
        uint W(int x, int y) => p.V.WovenPicture[y * MdVdp.PictureStride + x];
        Check("a cell is 16 rows tall: the first eight rows of its pattern, then the next eight (rows 0-7 colour 1, 8-15 colour 2, the cell below is empty)", W(2, 0) == c1 && W(2, 7) == c1 && W(2, 8) == c2 && W(2, 15) == c2 && W(2, 16) == bd, $"{W(2, 0):X8} {W(2, 8):X8} {W(2, 16):X8}");
        Check("both fields are in it: the rows 6, 7, 8, 9 around the pattern's seam all come out right", W(2, 6) == c1 && W(2, 9) == c2);
        Check("the one-field picture has the field's lines: line y is picture row 2y or 2y+1", p.Px(2, 3) == c1 && p.Px(2, 4) == c2 && p.Px(2, 7) == c2 && p.Px(2, 8) == bd);

        // vertical scroll counts picture rows (a cell is 16): a scroll of 16 brings the cell row below to the top
        p = new Pic();
        p.V.Reg[12] = 0x87;
        Cell16(p, 3, 1, 1); Cell16(p, 4, 5, 5);
        p.Cell(PlaneA, 0, 0, 3); p.Cell(PlaneA, 0, 1, 4);
        p.V.Vsram[0] = 16;
        p.Frame();
        Check("vertical scroll in mode 2 counts picture rows: a scroll of 16 shows the second cell row at the top", p.V.WovenPicture[2] == Argb(p.CramOf(0, 5), 0) && p.V.WovenPicture[15 * MdVdp.PictureStride + 2] == Argb(p.CramOf(0, 5), 0));

        // a 128-cell-high plane is 2048 picture rows in mode 2, and the vertical scroll has eleven bits there (oracle: a random scene with such a plane, 53,000 pixels off with ten bits, 15 with eleven)
        p = new Pic();
        p.V.Reg[12] = 0x87; p.V.Reg[16] = 0x30;
        Cell16(p, 3, 1, 1); Cell16(p, 4, 5, 5);
        p.Cell(PlaneA, 0, 0, 3, 32); p.Cell(PlaneA, 0, 64, 4, 32);
        p.V.Vsram[0] = 0x400;
        p.Frame();
        Check("interlace mode 2 with a 32 x 128 plane: a vertical scroll of 1024 (eleven bits) shows cell row 64 (picture row 1024) at the top", p.V.WovenPicture[2] == Argb(p.CramOf(0, 5), 0), $"{p.V.WovenPicture[2]:X8}");

        // sprites: Y is in picture rows (offset 256), a cell is 16 rows
        p = new Pic();
        p.V.Reg[12] = 0x87;
        Cell16(p, 5, 1, 2);
        Array.Clear(p.V.Vram, 0, 64);          // cell 0 (the planes' empty entries) must be blank in this pattern layout
        p.Sprite(0, 100, 0, 1, 1, 5, 0, false, 0);
        int ya = SatAddr; p.V.Vram[ya] = (byte)((40 + 256) >> 8 & 3); p.V.Vram[ya + 1] = (byte)((40 + 256) & 0xFF);
        p.Frame();
        Check("a sprite in mode 2 is 8 x 16 pixels at Y - 256 picture rows: rows 40-47 colour 1, 48-55 colour 2, nothing above or below", W(100, 40) == c1 && W(107, 47) == c1 && W(100, 48) == c2 && W(107, 55) == c2 && W(100, 39) == bd && W(100, 56) == bd, $"{W(100, 40):X8} {W(100, 48):X8} {W(100, 56):X8}");
        p.V.Vram[ya] |= 2;
        p.Frame();
        Check("... Y keeps its tenth bit in mode 2: with bit 9 set the sprite is far below the picture (outside mode 2 the bit is ignored)", W(100, 40) == bd && W(100, 48) == bd, $"{W(100, 40):X8}");

        // the field flag toggles every frame in either interlace mode, and is zero without interlace
        foreach (var (mode, r12) in new[] { (1, 0x83), (2, 0x87), (0, 0x81) })
        {
            p = new Pic(); p.V.Reg[12] = (byte)r12;
            p.V.ResetPictureState();
            var bits = new System.Collections.Generic.List<int>();
            long t = p.V.Time;
            for (int f = 0; f < 6; f++) { t += p.V.Timeline.MasterClocksPerFrame; p.V.RunTo(t + 5000); bits.Add(p.V.ReadStatus(p.V.Time) >> 4 & 1); }
            bool toggles = bits.Zip(bits.Skip(1), (a, b) => a != b).All(x => x);
            Check(mode == 0 ? "without interlace the odd-frame flag (status bit 4) stays 0" : $"interlace mode {mode}: the odd-frame flag (status bit 4) toggles every frame", mode == 0 ? bits.All(b => b == 0) : toggles, string.Join("", bits));
            Check(mode == 2 ? "mode 2 hands out the woven picture" : "no woven picture unless it is mode 2", p.V.WovenValid == (mode == 2));
        }
        p = new Pic(false, true); p.V.Reg[12] = 0x06; p.V.Reg[1] = 0x4C;
        p.Frame();
        Check("mode 2 with H32 and V30: the woven picture is 480 rows of 256", p.V.WovenValid && p.V.WovenHeight == 480 && p.V.PictureWidth == 256);
    }

    private static void ModesAndTiming()
    {
        Group("screen modes, display off, mid-line writes");
        var p = new Pic(h40: false);
        p.Cell(PlaneA, 0, 0, 2);
        p.Frame();
        Check("H32 is a 256-pixel wide picture", p.V.PictureWidth == 256 && p.V.PictureHeight == 224);
        p = new Pic(v30: true);
        p.Frame();
        Check("V30 (register 1 bit 3) is a 240-line picture", p.V.PictureHeight == 240);
        p = new Pic(region: SegaRegion.Pal);
        p.Frame();
        Check("PAL, 224-line mode: 224 picture lines inside a 313-line frame", p.V.PictureHeight == 224 && p.V.Timeline.LinesPerFrame == 313);

        p = new Pic();
        p.Cell(PlaneA, 0, 0, 2); p.V.Reg[7] = 0x01; p.V.Reg[1] = 0x04;      // display disabled
        p.Frame();
        Check("with the display disabled (register 1 bit 6) the picture is the backdrop", p.Px(2, 2) == Argb(p.CramOf(0, 1), 0) && p.Px(100, 100) == Argb(p.CramOf(0, 1), 0));

        // mid-line: the display switched off at pixel 100 of line 50 (as many games and demos do)
        p = new Pic();
        for (int cx = 0; cx < 40; cx++) for (int cy = 0; cy < 28; cy++) p.Cell(PlaneA, cx, cy, 2);
        p.V.Reg[7] = 0x01;
        p.V.ResetPictureState();
        long line50 = 50L * MdVdp.ClocksPerLine + p.V.PixelOrigin;        // the line's first picture pixel (it starts at its H counter 0, 12 ticks before)
        p.T = line50 + 100 * 8;
        p.V.RunTo(p.T);
        p.Reg(1, 0x04);
        p.T = line50 + 120 * 8; p.V.RunTo(p.T); p.Reg(1, 0x44);
        p.T = 240L * MdVdp.ClocksPerLine; p.V.RunTo(p.T);
        bool before = p.Px(90, 50) == Argb(p.CramOf(0, 2), 0), during = p.Px(110, 50) == Argb(p.CramOf(0, 1), 0), after = p.Px(200, 50) == Argb(p.CramOf(0, 2), 0);
        Check("the display switched off for pixels 100-120 of one line: before and after the line is drawn, between it is the backdrop", before && during && after, $"{before} {during} {after}");
        Check("... and the neighbouring lines are not touched", p.Px(110, 49) == Argb(p.CramOf(0, 2), 0) && p.Px(110, 51) == Argb(p.CramOf(0, 2), 0));

        // the colour RAM dot: a colour RAM write made while the picture is drawn puts the written colour on the pixel being output
        p = new Pic();
        p.V.Reg[7] = 0x01;
        p.V.ResetPictureState();
        p.T = 40L * MdVdp.ClocksPerLine + p.V.PixelOrigin + 96;
        p.V.RunTo(p.T);
        p.Cmd(3, 0x40);                                   // CRAM write at colour 32
        p.Data(0x0E00);
        p.T += 1000; p.V.RunTo(p.T);
        p.T = 240L * MdVdp.ClocksPerLine; p.V.RunTo(p.T);
        int dots = 0; for (int x = 0; x < 320; x++) if (p.Px(x, 40) == Argb(0x0E00, 0)) dots++;
        Check("a colour RAM write during the picture leaves a one-pixel dot of the written colour on the line", dots >= 1 && dots <= 2, $"{dots} dots");
        Check("... on that line only, and none on the lines around it", Enumerable.Range(0, 320).All(x => p.Px(x, 39) != Argb(0x0E00, 0) && p.Px(x, 41) != Argb(0x0E00, 0)));

        // the window registers are latched at the end of the line: a write in the line's blanking first shows on the line after next
        p = new Pic();
        for (int cx = 0; cx < 64; cx++) { p.Cell(Window, cx, 1, 5); p.Cell(PlaneA, cx, 1, 2); }
        p.V.Reg[17] = 0x00; p.V.Reg[18] = 0x00;
        p.V.ResetPictureState();
        // switch the window on (down from line 0) with a write at the end of line 10's active picture: it takes effect on line 12
        p.T = 10L * MdVdp.ClocksPerLine + 2700; p.V.RunTo(p.T);
        p.Reg(18, 0x80);
        p.T = 240L * MdVdp.ClocksPerLine; p.V.RunTo(p.T);
        Check("the window position registers are taken at the end of the active line: a write after that point shows from the line after next (line 12, not 11)", p.Px(5, 10) != Argb(p.CramOf(0, 5), 0) && p.Px(5, 11) != Argb(p.CramOf(0, 5), 0) && p.Px(5, 12) == Argb(p.CramOf(0, 5), 0), $"{p.Px(5, 10):X8} {p.Px(5, 11):X8} {p.Px(5, 12):X8}");

        // the full-screen vertical scroll (register 11 bit 2 clear) is taken once a line, near its end, for the next line (BlastEm, probe vsmid.py): a write never shows on the line it is made in
        Pic Rows(bool h40 = true, bool column = false)
        {
            var q = new Pic(h40);
            q.V.Reg[11] = (byte)(column ? 0x04 : 0x00);
            for (int cy = 0; cy < 32; cy++) for (int cx = 0; cx < 64; cx++) q.Cell(PlaneA, cx, cy, 1 + cy % 14);
            return q;
        }
        uint RowColour(Pic q, int y, int vs) => Argb(q.CramOf(0, 1 + ((y + vs) >> 3) % 32 % 14), 0);       // plane A cell row r is tile 1 + r % 14, a solid colour
        void WriteVsram(Pic q, int entry, int v) { q.Cmd(5, entry * 2); q.Data((ushort)v); }
        p = Rows();
        p.V.ResetPictureState();
        p.T = 20L * MdVdp.ClocksPerLine + p.V.PixelOrigin + 100 * 8; p.V.RunTo(p.T);
        WriteVsram(p, 0, 8);
        p.T = 240L * MdVdp.ClocksPerLine; p.V.RunTo(p.T);
        Check("a full-screen vertical scroll written in the middle of line 20 does not change line 20 (the entry is taken once, near the line's end, for the line after)", p.Px(250, 20) == RowColour(p, 20, 0) && p.Px(5, 20) == RowColour(p, 20, 0), $"{p.Px(250, 20):X8} {RowColour(p, 20, 0):X8} {RowColour(p, 20, 8):X8}");
        Check("... and shows from line 21 on, over the whole line", p.Px(5, 21) == RowColour(p, 21, 8) && p.Px(300, 21) == RowColour(p, 21, 8) && p.Px(100, 40) == RowColour(p, 40, 8), $"{p.Px(5, 21):X8} {RowColour(p, 21, 8):X8}");
        p = Rows();
        p.V.Vsram[0] = 8; p.V.ResetPictureState();
        p.T = 30L * MdVdp.ClocksPerLine + 3000; p.V.RunTo(p.T);
        WriteVsram(p, 0, 16);
        p.T = 240L * MdVdp.ClocksPerLine; p.V.RunTo(p.T);
        Check("a write after that point (clock 3000 of an H40 line) misses the next line too: line 31 still has the old scroll, line 32 the new", p.Px(100, 31) == RowColour(p, 31, 8) && p.Px(100, 32) == RowColour(p, 32, 16), $"{p.Px(100, 31):X8} {RowColour(p, 31, 8):X8} {RowColour(p, 31, 16):X8}");
        p = Rows(h40: false);
        p.V.ResetPictureState();
        p.T = 20L * MdVdp.ClocksPerLine + 2700; p.V.RunTo(p.T);
        WriteVsram(p, 0, 8);
        p.T = 240L * MdVdp.ClocksPerLine; p.V.RunTo(p.T);
        Check("H32: a write at clock 2700 of line 20 shows on line 21, not on line 20", p.Px(100, 20) == RowColour(p, 20, 0) && p.Px(100, 21) == RowColour(p, 21, 8));

        // the 2-cell column scroll reads each column's entry ahead of the picture (about 36 pixels): a column's entry written when the display is 10 pixels short of that is too late, one written 10 pixels earlier is in time
        // (the writes take about 10 pixels to reach the VDP: two control words and a data word)
        p = Rows(column: true);
        p.V.ResetPictureState();
        p.T = 20L * MdVdp.ClocksPerLine + p.V.PixelOrigin + 128 * 8; p.V.RunTo(p.T);
        WriteVsram(p, 20, 8);
        p.T = 240L * MdVdp.ClocksPerLine; p.V.RunTo(p.T);
        Check("column scroll: an entry written with the display at pixel 128 is too late for the column that starts at 160 (the entry is read about 36 pixels ahead of the display)", p.Px(160, 20) == RowColour(p, 20, 0) && p.Px(170, 20) == RowColour(p, 20, 0), $"{p.Px(160, 20):X8} {RowColour(p, 20, 0):X8} {RowColour(p, 20, 8):X8}");
        p = Rows(column: true);
        p.V.ResetPictureState();
        p.T = 20L * MdVdp.ClocksPerLine + p.V.PixelOrigin + 110 * 8; p.V.RunTo(p.T);
        WriteVsram(p, 22, 8);
        p.T = 240L * MdVdp.ClocksPerLine; p.V.RunTo(p.T);
        Check("... one written with the display at pixel 110 is in time for the column that starts at 176, and no other column changes", p.Px(176, 20) == RowColour(p, 20, 8) && p.Px(190, 20) == RowColour(p, 20, 8) && p.Px(160, 20) == RowColour(p, 20, 0) && p.Px(300, 20) == RowColour(p, 20, 0), $"{p.Px(176, 20):X8} {RowColour(p, 20, 0):X8} {RowColour(p, 20, 8):X8}");

        // the picture stage is part of what a state round trip keeps
        p = new Pic();
        p.Cell(PlaneA, 0, 0, 2);
        p.Frame();
        var ms = new MemoryStream(); using (var w = new BinaryWriter(ms, Encoding.UTF8, true)) p.V.SaveState(w);
        var q = new Pic();
        ms.Position = 0; using (var r = new BinaryReader(ms, Encoding.UTF8, true)) q.V.LoadState(r);
        q.V.RunTo(q.V.Time + q.V.Timeline.MasterClocksPerFrame * 2);
        Check("a VDP state restored into another VDP draws the same picture", q.Px(2, 2) == p.Px(2, 2) && q.Px(100, 100) == p.Px(100, 100));
    }

    // =====================================================================================================================
    //  The session, saves, mappers
    // =====================================================================================================================

    private static byte[] BareRom(int size, string regions = "JUE", Action<byte[]>? patch = null)
    {
        var rom = new byte[size];
        void L(int at, uint v) { rom[at] = (byte)(v >> 24); rom[at + 1] = (byte)(v >> 16); rom[at + 2] = (byte)(v >> 8); rom[at + 3] = (byte)v; }
        L(0, 0xFFFE00); L(4, 0x200);
        for (int v = 2; v < 64; v++) L(v * 4, 0x400);
        Encoding.ASCII.GetBytes("SEGA MEGA DRIVE ").CopyTo(rom, 0x100);
        Encoding.ASCII.GetBytes("(C)TEST 2026.OCT").CopyTo(rom, 0x110);
        Encoding.ASCII.GetBytes("PICTURE TEST ROM".PadRight(48)).CopyTo(rom, 0x120);
        Encoding.ASCII.GetBytes("PICTURE TEST ROM".PadRight(48)).CopyTo(rom, 0x150);
        Encoding.ASCII.GetBytes("GM 00000000-00").CopyTo(rom, 0x180);
        Encoding.ASCII.GetBytes(regions.PadRight(16)).CopyTo(rom, 0x1F0);
        rom[0x400] = 0x60; rom[0x401] = 0xFE;
        patch?.Invoke(rom);
        return rom;
    }

    private static void Put(byte[] rom, int at, params ushort[] words) { for (int i = 0; i < words.Length; i++) { rom[at + i * 2] = (byte)(words[i] >> 8); rom[at + i * 2 + 1] = (byte)words[i]; } }

    private static void Session()
    {
        Group("the session: the picture, the pads, saves");
        // a program that sets the backdrop and the colour: move.w #$8144,$C00004 ; move.w #$8707,$C00004 ; set CRAM 7 ; loop
        var rom = BareRom(0x20000, "JUE", r => Put(r, 0x200,
            0x33FC, 0x8144, 0x00C0, 0x0004,           // register 1: display on
            0x33FC, 0x8C81, 0x00C0, 0x0004,           // register 12: H40
            0x33FC, 0x8707, 0x00C0, 0x0004,           // register 7: backdrop colour 7
            0x23FC, 0xC00E, 0x0000, 0x00C0, 0x0004,   // CRAM write at colour 7
            0x33FC, 0x0E00, 0x00C0, 0x0000,           // blue
            0x60FE));
        // the 6 words per instruction above: move.w #imm,abs.l is 4 words; move.l #imm,abs.l is 5 words
        var s = new MdSession(rom, "M68K", "MD", "MD");
        for (int i = 0; i < 3; i++) s.RunFrame();
        Check("the session hands the apps the VDP's picture: 320 x 224 here, the backdrop colour in every pixel", s.FrameWidth == 320 && s.FrameHeight == 224 && s.Frame[0] == Argb(0x0E00, 0) && s.Frame[s.FrameWidth * s.FrameHeight - 1] == Argb(0x0E00, 0), $"{s.FrameWidth}x{s.FrameHeight} {s.Frame[0]:X8}");
        Check("... the width the apps show it at is 320 (an H32 picture is stretched to the same screen width)", s.DisplayWidth == 320);
        Check("the session says what it runs (no 'no renderer' stand-in)", !s.Description.Contains("NO PICTURE", StringComparison.OrdinalIgnoreCase));

        var pads = new MdSession(BareRom(0x20000), "M68K", "MD", "MD");
        pads.SetPad(0, PadButtons.R | PadButtons.L | PadButtons.Select | PadButtons.A);
        // the pad as the board's I/O chip answers: set TH low/high through the bus
        var board = pads.Board;
        board.BusWrite(0xA10008, false, true, 0x0040, 4);     // control: TH is an output
        ushort Rd(uint a) => board.BusRead(a, false, true, 4);
        board.BusWrite(0xA10002, false, true, 0x0040, 4);     // TH high
        int th1 = Rd(0xA10002) & 0xFF;
        Check("R is the Genesis C button (bit 5 of the TH-high read is 0), generic L and Select map to Z and Mode", (th1 & 0x20) == 0, $"{th1:X2}");
        board.BusWrite(0xA10002, false, true, 0x0000, 4);     // TH low
        int th0 = Rd(0xA10002) & 0xFF;
        Check("A reads as pressed with TH low (bit 4 = 0)", (th0 & 0x10) == 0, $"{th0:X2}");

        // battery SRAM: a 128 KB ROM with an odd-byte SRAM header at $200001-$203FFF
        var sram = BareRom(0x20000, "JUE", r =>
        {
            Encoding.ASCII.GetBytes("RA").CopyTo(r, 0x1B0); r[0x1B2] = 0xF8; r[0x1B3] = 0x20;
            r[0x1B4] = 0; r[0x1B5] = 0x20; r[0x1B6] = 0; r[0x1B7] = 0x01;          // start $200001
            r[0x1B8] = 0; r[0x1B9] = 0x20; r[0x1BA] = 0x3F; r[0x1BB] = 0xFF;        // end $203FFF
            // move.b #$5A,$200001 ; move.b #$A5,$200003 ; loop
            Put(r, 0x200, 0x13FC, 0x005A, 0x0020, 0x0001, 0x13FC, 0x00A5, 0x0020, 0x0003, 0x60FE);
        });
        var s2 = new MdSession(sram, "M68K", "MD", "MD");
        Check("a ROM with an SRAM header reports a battery (and a plain ROM does not)", s2.HasBattery && !pads.HasBattery);
        for (int i = 0; i < 3; i++) s2.RunFrame();
        byte[] save = s2.ExportSave();
        Check("the program's writes land in the battery image (odd bytes: one byte per word)", save.Length == 0x2000 && save[0] == 0x5A && save[1] == 0xA5, $"{save.Length} {save[0]:X2} {save[1]:X2}");
        var s3 = new MdSession(sram, "M68K", "MD", "MD");
        var other = new byte[0x2000]; other[0] = 0x11; other[1] = 0x22; other[7] = 0x77;
        s3.ImportSave(other);
        Check("an imported save is what the cartridge holds before the game runs", s3.ExportSave()[0] == 0x11 && s3.ExportSave()[7] == 0x77);
        string tmp = Path.Combine(Path.GetTempPath(), "mdsave_" + Guid.NewGuid().ToString("N") + ".srm");
        File.WriteAllBytes(tmp, save);
        var s4 = new MdSession(sram, "M68K", "MD", "MD");
        s4.ImportSave(File.ReadAllBytes(tmp)); File.Delete(tmp);
        Check("a save written to a file and read back into a new session is the same battery image", s4.ExportSave().AsSpan().SequenceEqual(save));
        Check("the game id (the file name of the save) is the ROM's SHA-1, lower-case", s2.GameId.Length == 40 && s2.GameId == s2.GameId.ToLowerInvariant() && s4.GameId == s2.GameId);
    }

    private static void Mappers()
    {
        Group("cartridge mappers");
        // SSF2: a 5 MB ROM whose every 512 KB bank starts with its own number; $A130F3-$A130FF select the banks of the windows 1-7
        var rom = BareRom(0x500000, "JUE", r => { for (int b = 0; b < 10; b++) { r[b * 0x80000 + 0x300] = (byte)(0xB0 + b); } r[0x300] = 0xB0; });
        var cart = new MdCartridge(rom);
        Check("a ROM above 4 MB is the SSF2 mapper", cart.Mapper.Contains("SSF2"));
        Check("at power-on window 1 shows bank 1, window 7 bank 7", cart.ReadByte(0x80300) == 0xB1 && cart.ReadByte(0x380300) == 0xB7);
        cart.WriteMapper(0xA130F3, 8); cart.WriteMapper(0xA130FF, 9);
        Check("writing bank numbers to $A130F3 and $A130FF maps banks 8 and 9 into windows 1 and 7", cart.ReadByte(0x80300) == 0xB8 && cart.ReadByte(0x380300) == 0xB9 && cart.ReadByte(0x100300) == 0xB2);
        Check("window 0 is fixed to bank 0", cart.ReadByte(0x300) == 0xB0 && cart.ReadByte(0x7FFFF) == cart.Rom[0x7FFFF]);

        // the 4 MB image of Sonic & Knuckles with a game locked on: the locked-on game's SRAM header at $2001B0
        var lo = BareRom(0x400000, "JUE", r =>
        {
            Encoding.ASCII.GetBytes("SEGA GENESIS    ").CopyTo(r, 0x200100);
            Encoding.ASCII.GetBytes("RA").CopyTo(r, 0x2001B0); r[0x2001B2] = 0xF8; r[0x2001B3] = 0x20;
            r[0x2001B4] = 0; r[0x2001B5] = 0x20; r[0x2001B6] = 0; r[0x2001B7] = 0x01;
            r[0x2001B8] = 0; r[0x2001B9] = 0x20; r[0x2001BA] = 0x03; r[0x2001BB] = 0xFF;
            r[0x200010] = 0xC3;
        });
        var lc = new MdCartridge(lo);
        Check("a 4 MB Sonic & Knuckles lock-on image takes the locked-on game's SRAM header ($200001-$2003FF, 512 odd bytes)", lc.LockedOn && lc.HasBattery && lc.Sram.Length == 512 && lc.SramStart == 0x200001, $"{lc.LockedOn} {lc.Sram.Length}");
        Check("... the SRAM is not in the map until the game switches it on through $A130F1", !lc.SramMapped && lc.ReadByte(0x200010) == 0xC3);
        lc.WriteMapper(0xA130F1, 1);
        lc.WriteByte(0x200001, 0x42); lc.WriteByte(0x200003, 0x43);
        Check("... after that the SRAM answers at $200001, $200003 ... and keeps what is written", lc.ReadByte(0x200001) == 0x42 && lc.ReadByte(0x200003) == 0x43 && lc.Sram[0] == 0x42 && lc.Sram[1] == 0x43);
        lc.WriteMapper(0xA130F1, 0);
        Check("... and with the register cleared the ROM is back", lc.ReadByte(0x200010) == 0xC3 && lc.Sram[0] == 0x42);
        var plain = new MdCartridge(BareRom(0x400000));
        Check("a plain 4 MB ROM is not mistaken for a lock-on image", !plain.LockedOn && !plain.HasBattery);

        // an interleaved .smd image is normalised (a 512-byte copier header and 16 KB blocks with the even bytes in the second half)
        var plainRom = BareRom(0x20000);
        var smd = new byte[512 + plainRom.Length];
        smd[0] = (byte)(plainRom.Length / 16384); smd[1] = 3; smd[8] = 0xAA; smd[9] = 0xBB;
        for (int blk = 0; blk < plainRom.Length / 16384; blk++)
            for (int i = 0; i < 8192; i++) { smd[512 + blk * 16384 + i + 8192] = plainRom[blk * 16384 + i * 2]; smd[512 + blk * 16384 + i] = plainRom[blk * 16384 + i * 2 + 1]; }
        var sc = new MdCartridge(smd);
        Check("an .smd image (copier header, interleaved blocks) reads as the same ROM", sc.ReadWord(0x100) == 0x5345 && sc.ReadWord(4) == 0x0000 && sc.ReadWord(6) == 0x0200, $"{sc.ReadWord(0x100):X4}");
    }
}
