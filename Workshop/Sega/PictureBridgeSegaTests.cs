using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NesEmulator;
using NesEmulator.Mix;
using NesEmulator.Sega;
using NesEmulator.Systems;
using static BrokenNes.Workshop.Sega.PictureBridgeTestCli;

namespace BrokenNes.Workshop.Sega;

/// <summary>The Sega -> NES checks of <c>--picture-bridge-test</c>: scenes poked into a real VDP (a Master System, Game Gear or Genesis session running a program that does nothing), drawn by the VDP itself and by the bridge, and compared.</summary>
internal static class SegaScenes
{
    // ================================================================================================================ rigs
    private sealed class SmsRig
    {
        public readonly SmsSession Inner; public readonly SegaPictureSession Wrap; public SmsVdp V => Inner.Board.Vdp;
        public SmsRig(string ppu, bool gg = false)
        {
            var rom = new byte[0x8000]; rom[0] = 0xF3; rom[1] = 0x18; rom[2] = 0xFE;      // DI ; JR $
            Inner = gg ? new GgSession(rom, "Z80", "GG", "GG", "t.gg") : new SmsSession(rom, ConsoleKind.MasterSystem, "Z80", "SMS", "SMS", "t.sms");
            Wrap = new SegaPictureSession(Inner, ppu);
            V.WritesImmediate = true;
            Regs(0x04, 0x40, 0x0E, 0, 0, 0x7E, 0xFB, 0x00, 0, 0, 0xFF);
        }
        public void Regs(params int[] r) { for (int i = 0; i < r.Length; i++) V.Regs[i] = (byte)r[i]; }
        public void Cram(int i, int v) { V.WriteControl((byte)i, 0); V.WriteControl(0xC0, 0); V.WriteData((byte)v, 0); }
        public void Gg(int i, int bgr12) { V.WriteControl((byte)(i * 2), 0); V.WriteControl(0xC0, 0); V.WriteData((byte)bgr12, 0); V.WriteData((byte)(bgr12 >> 8), 0); }
        public void Tile(int tile, int colour) { for (int r = 0; r < 8; r++) for (int p = 0; p < 4; p++) V.Vram[tile * 32 + r * 4 + p] = (byte)((colour >> p & 1) != 0 ? 0xFF : 0); }
        /// <summary>A tile with a vertical stripe: columns 0-3 colour a, 4-7 colour b.</summary>
        public void Stripe(int tile, int a, int b) { for (int r = 0; r < 8; r++) for (int p = 0; p < 4; p++) V.Vram[tile * 32 + r * 4 + p] = (byte)(((a >> p & 1) != 0 ? 0xF0 : 0) | ((b >> p & 1) != 0 ? 0x0F : 0)); }
        public void Cell(int row, int col, int tile, int flags = 0) { int a = 0x3800 + row * 64 + col * 2; V.Vram[a] = (byte)tile; V.Vram[a + 1] = (byte)(tile >> 8 | flags >> 8); }
        public void Sprite(int i, int x, int y, int tile) { V.Vram[0x3F00 + i] = (byte)y; V.Vram[0x3F80 + i * 2] = (byte)x; V.Vram[0x3F81 + i * 2] = (byte)tile; }
        public void Frames(int n = 3) { for (int i = 0; i < n; i++) Wrap.RunFrame(); }
    }

    private sealed class MdRig
    {
        public readonly MdSession Inner; public readonly SegaPictureSession Wrap; public MdVdp V => Inner.Board.Vdp;
        public const int PlaneA = 0xC000, PlaneB = 0xE000, WindowNt = 0xB000, Sat = 0xF800, HScroll = 0xFC00;
        public MdRig(string ppu)
        {
            var rom = new byte[0x20000];
            void L(int at, uint v) { rom[at] = (byte)(v >> 24); rom[at + 1] = (byte)(v >> 16); rom[at + 2] = (byte)(v >> 8); rom[at + 3] = (byte)v; }
            L(0, 0xFFFE00); L(4, 0x200); for (int v = 2; v < 64; v++) L(v * 4, 0x200);
            Encoding.ASCII.GetBytes("SEGA MEGA DRIVE ").CopyTo(rom, 0x100); Encoding.ASCII.GetBytes("(C)TEST 2026.OCT").CopyTo(rom, 0x110);
            Encoding.ASCII.GetBytes("BRIDGE SCENES".PadRight(48)).CopyTo(rom, 0x120); Encoding.ASCII.GetBytes("BRIDGE SCENES".PadRight(48)).CopyTo(rom, 0x150);
            Encoding.ASCII.GetBytes("GM 00000000-00").CopyTo(rom, 0x180); Encoding.ASCII.GetBytes("JUE".PadRight(16)).CopyTo(rom, 0x1F0);
            rom[0x200] = 0x60; rom[0x201] = 0xFE;                                         // BRA.S *
            Inner = new MdSession(rom, "M68K", "MD", "MD");
            Wrap = new SegaPictureSession(Inner, ppu);
            Regs(0x04, 0x44, 0x30, 0x2C, 0x07, 0x7C, 0, 0x00, 0, 0, 0xFF, 0x00, 0x00, 0x3F, 0, 0x02, 0x01, 0, 0);
        }
        public void Regs(params int[] r) { for (int i = 0; i < r.Length; i++) V.Reg[i] = (byte)r[i]; }
        public void Tile(int tile, int colour) { for (int i = 0; i < 32; i++) V.Vram[tile * 32 + i] = (byte)(colour << 4 | colour); }
        public void Stripe(int tile, int a, int b) { for (int r = 0; r < 8; r++) for (int k = 0; k < 4; k++) V.Vram[tile * 32 + r * 4 + k] = (byte)(((k < 2 ? a : b) << 4) | (k < 2 ? a : b)); }
        public void W16(int addr, int v) { V.Vram[addr] = (byte)(v >> 8); V.Vram[addr + 1] = (byte)v; }
        public void Cell(int nt, int width, int row, int col, int tile, int pal = 0, bool prio = false, bool hf = false, bool vf = false) =>
            W16(nt + (row * width + col) * 2, tile | pal << 13 | (prio ? 0x8000 : 0) | (hf ? 0x800 : 0) | (vf ? 0x1000 : 0));
        public void Sprite(int i, int x, int y, int w, int h, int tile, int pal = 0, bool prio = false, bool hf = false, bool vf = false, int link = -1)
        {
            int a = Sat + i * 8; W16(a, y + 128); V.Vram[a + 2] = (byte)((w - 1) << 2 | (h - 1)); V.Vram[a + 3] = (byte)(link < 0 ? i + 1 : link);
            W16(a + 4, tile | pal << 13 | (prio ? 0x8000 : 0) | (hf ? 0x800 : 0) | (vf ? 0x1000 : 0)); W16(a + 6, x + 128);
        }
        public void Color(int i, int c) => V.LoadColour(i, (ushort)c);
        public void Go(int n = 3) { V.ResetPictureState(); for (int i = 0; i < n; i++) Wrap.RunFrame(); }
    }

    // ================================================================================================================ comparing
    private static uint At(uint[] c, int x, int y) => c[y * 256 + x] & 0xFFFFFF;

    /// <summary>How many pixels of the region are "drawn" in one picture and not in the other (a pixel is drawn when it is not black: every scene's backdrop is colour 0, black on both pictures).</summary>
    private static int MaskMismatch(uint[] bridge, uint[] reference, (int X, int Y, int W, int H) r)
    {
        const uint bb = 0, rb = 0; int bad = 0;
        for (int y = r.Y; y < r.Y + r.H; y++) for (int x = r.X; x < r.X + r.W; x++) if ((At(bridge, x, y) != bb) != (At(reference, x, y) != rb)) bad++;
        return bad;
    }

    /// <summary>Mismatch with a picture dump (reference | bridge | difference) into the folder named by BRIDGE_TEST_PNG when there is one and the scene differs.</summary>
    private static int Mm(SegaPictureSession w, string tag, int tolerance = 0, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        string name = tag + "_line" + line;
        int mm = MaskMismatch(w.BridgeCanvas, w.ReferenceCanvas, w.Region);
        string dir = Environment.GetEnvironmentVariable("BRIDGE_TEST_PNG") ?? "";
        if (mm > tolerance && dir.Length > 0)
        {
            Directory.CreateDirectory(dir);
            var both = new uint[3 * 256 * 240]; const uint bb = 0, rb = 0;
            for (int y = 0; y < 240; y++) for (int x = 0; x < 256; x++)
            {
                both[y * 768 + x] = w.ReferenceCanvas[y * 256 + x]; both[y * 768 + 256 + x] = w.BridgeCanvas[y * 256 + x];
                bool d = (At(w.BridgeCanvas, x, y) != bb) != (At(w.ReferenceCanvas, x, y) != rb);
                both[y * 768 + 512 + x] = d ? 0xFFFF0000u : 0xFF000000u;
            }
            MixLab.MixLabCli.SaveArgb(both, 768, 240, Path.Combine(dir, string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '_')) + ".png"));
        }
        return mm;
    }

    /// <summary>How many pixels of the region are not the NES colour nearest to the reference's own colour there: the exact colour check of the layered path (which draws a pixel in the textbook colour of the NES index it picked).
    /// Unlike <see cref="MaskMismatch"/> it sees a tile swapped for another of the same drawn-ness, a wrong palette line or the wrong layer on top.</summary>
    private static int ColourMismatch(SegaPictureSession w, (int X, int Y, int W, int H) r)
    {
        int bad = 0; var b = w.BridgeCanvas; var f = w.ReferenceCanvas;
        for (int y = r.Y; y < r.Y + r.H; y++) for (int x = r.X; x < r.X + r.W; x++)
        {
            uint rf = At(f, x, y);
            if (At(b, x, y) != NesLook((int)(rf >> 16 & 0xFF), (int)(rf >> 8 & 0xFF), (int)(rf & 0xFF))) bad++;
        }
        return bad;
    }

    private static uint NesLook(int r, int g, int b) => (uint)NesFixPalette.Rgb(NesFixPalette.Nearest(r, g, b));
    private static uint SmsLook(int bgr6) { int[] l = { 0, 0x52, 0xAD, 0xFF }; return NesLook(l[bgr6 & 3], l[bgr6 >> 2 & 3], l[bgr6 >> 4 & 3]); }
    private static uint MdLook(int c) { int[] l = { 0, 49, 87, 119, 146, 174, 206, 255 }; return NesLook(l[c >> 1 & 7], l[c >> 5 & 7], l[c >> 9 & 7]); }
    private const int Red = 0x03, Green = 0x0C, Blue = 0x30, White = 0x3F, Yellow = 0x0F;

    // ================================================================================================================ the scenes
    public static void Run()
    {
        foreach (var chip in new[] { "NES:FIXS", "NES:FIX" })
        {
            Guard($"Master System scenes on {chip}", () => SmsScenes(chip));
        }
        Guard("Game Gear: the LCD window and the black bezel", GgScenes);
        foreach (var chip in new[] { "NES:FIXS", "NES:FIX" })
            Guard($"Genesis scenes on {chip}", () => MdScenes(chip));
        Guard("Genesis VDP rules measured by Track B4a (column scroll pairs, plane sizes, shadow/highlight operators) on NES:FIXS", MdRuleScenes);
    }

    // ================================================================================================================ the VDP rules Track B4a measured on BlastEm
    // These checks do not compare with this build's VDP (whose copy of the rules moves with its own track): the expected picture is computed from the rules as the oracle showed them (docs/projects/sega-track-b4a.md, 1.2 and
    // 1.3), for scenes built to tell the old rule from the new one - each scene also counts how many pixels the OLD rule would have drawn differently, and that number has to be large, so a scene that cannot tell them apart fails.
    private static readonly int[] RowCram = { 0, 0x00E, 0x0E0, 0xE00, 0x0EE, 0xEE0, 0xE0E };

    /// <summary>A rig whose plane (name table at <paramref name="nt"/>, <paramref name="width"/> cells wide, <paramref name="rows"/> rows) shows plane row R in the solid colour 1 + R % 6, so the row every pixel shows can be told from its colour.</summary>
    private static MdRig PlaneRig(string chip, int nt, int width, int rows, int r11, int r12, int r16)
    {
        var r = new MdRig(chip); r.Color(0, 0);
        for (int i = 1; i <= 6; i++) { r.Color(i, RowCram[i]); r.Tile(i, i); }
        for (int row = 0; row < rows; row++) for (int col = 0; col < width; col++) r.Cell(nt, width, row, col, 1 + row % 6);
        r.Regs(0x04, 0x44, 0x30, 0x2C, 0x07, 0x7C, 0, 0x00, 0, 0, 0xFF, r11, r12, 0x3F, 0, 0x02, r16, 0, 0);
        return r;
    }

    /// <summary>Pixels of the bridge's picture that are not the colour of the plane row <paramref name="rowAt"/>(line, screen x) gives; <paramref name="dx"/>: the screen column of the picture's first column (32 in H40).</summary>
    private static int RowMismatch(MdRig r, int dx, Func<int, int, int> rowAt)
    {
        var c = r.Wrap.BridgeCanvas; int bad = 0;
        for (int y = 0; y < 224; y++) for (int x = 0; x < 256; x++)
            if (At(c, x, 8 + y) != MdLook(RowCram[1 + rowAt(y, x + dx) % 6])) bad++;
        return bad;
    }

    private static (int bad, int oldRuleBad) ColumnScrollScene(string chip, bool h40, bool planeB, Func<int, int> hsOf, int r11)
    {
        int nt = planeB ? MdRig.PlaneB : MdRig.PlaneA, off = planeB ? 1 : 0, last = h40 ? 19 : 15, dx = h40 ? 32 : 0, mask = (r11 & 3) == 0 ? 0 : 0xF8;
        var r = PlaneRig(chip, nt, 64, 32, r11, h40 ? 0x81 : 0x00, 0x01);
        var vs = new int[40];
        for (int c = 0; c < 20; c++) { vs[2 * c + off] = 8 * ((5 * c + 1) % 32); vs[2 * c + 1 - off] = 8 * ((5 * c + 3) % 32); }
        vs[2 * last + off] = h40 ? 144 : 96; vs[2 * last + 1 - off] = h40 ? 160 : 80;      // the last pair's two entries differ, and so does their AND (128 / 64) from both: the strip before the first pair shows which rule made it
        for (int i = 0; i < 40; i++) r.V.Vsram[i] = (ushort)vs[i];
        for (int l = 0; l < 224; l += mask == 0 ? 224 : 8) r.W16(MdRig.HScroll + l * 4 + (planeB ? 2 : 0), hsOf(l));
        r.Go();
        int Row(int y, int sx, int b0)
        {
            int v = sx < b0 ? (vs[2 * last] & vs[2 * last + 1]) & 0x3FF : vs[2 * ((sx - b0) >> 4) + off] & 0x3FF;
            return ((y + v) & 255) >> 3;
        }
        return (RowMismatch(r, dx, (y, sx) => Row(y, sx, hsOf(y & mask) & 15)), RowMismatch(r, dx, (y, sx) => Row(y, sx, 0)));
    }

    private static void MdRuleScenes()
    {
        const string chip = "NES:FIXS"; string tag = $"Genesis on {chip}";
        // ---- 1. 2-cell column vertical scroll: the pairs of cells start at the plane's scroll mod 16, the strip before the first pair takes the last column's two entries ANDed
        var scenes = new (string name, bool h40, bool planeB, Func<int, int> hs, int r11, bool tellsApart)[]
        {
            ("plane A, H32, scrolled 5 pixels", false, false, _ => 5, 0x04, true),
            ("plane B, H32, scrolled 29 pixels", false, true, _ => 29, 0x04, true),
            ("plane A, H40 (the picture's first column is screen column 32), scrolled 27 pixels", true, false, _ => 27, 0x04, true),
            ("plane A, H32, a different scroll every 8 lines (0, 7, 14, ... mod 16 decide the pairs line by line)", false, false, l => (l >> 3) * 7 & 0x1FF, 0x04 | 0x02, true),
            ("plane A, H32, not scrolled (the one case the old rule had right)", false, false, _ => 0, 0x04, false),
        };
        foreach (var s in scenes)
        {
            var (bad, oldBad) = ColumnScrollScene(chip, s.h40, s.planeB, s.hs, s.r11);
            Check($"{tag}: 2-cell column scroll, {s.name}: every pixel is the row the rule picks", bad == 0, $"{bad} pixels differ (the old rule would differ in {oldBad})");
            Check($"{tag}: ... and the scene tells the rules apart", s.tellsApart ? oldBad > 1000 : oldBad == 0, $"the old rule differs in {oldBad} pixels");
        }

        // ---- 2. plane sizes the 4096-entry plane cannot hold
        {
            var r = PlaneRig(chip, MdRig.PlaneA, 128, 32, 0x00, 0x00, 0x13);          // width code 3 (128), height code 1 (64): a 128-wide plane is only 32 high
            r.V.Vsram[0] = 320; r.Go();
            int bad = RowMismatch(r, 0, (y, _) => ((y + 320) & 255) >> 3), oldBad = RowMismatch(r, 0, (y, _) => ((y + 320) & 511) >> 3);
            Check($"{tag}: a 128-wide plane with the height code 64 is 32 high (it wraps at 256 lines)", bad == 0 && oldBad > 1000, $"{bad} pixels differ (a 64-high plane would differ in {oldBad})");
            r = PlaneRig(chip, MdRig.PlaneA, 64, 64, 0x00, 0x00, 0x31);               // width code 1 (64), height code 3 (128): 64 x 64
            r.V.Vsram[0] = 536; r.Go();
            bad = RowMismatch(r, 0, (y, _) => ((y + 536) & 511) >> 3); oldBad = RowMismatch(r, 0, (y, _) => ((y + 536) & 1023) >> 3);
            Check($"{tag}: a 64-wide plane with the height code 128 is 64 x 64 (it wraps at 512 lines)", bad == 0 && oldBad > 1000, $"{bad} pixels differ (a 128-high plane would differ in {oldBad})");
            r = PlaneRig(chip, MdRig.PlaneA, 32, 128, 0x00, 0x00, 0x30);              // width code 0 (32), height code 3 (128): valid, stays 32 x 128
            r.V.Vsram[0] = 800; r.Go();
            bad = RowMismatch(r, 0, (y, _) => ((y + 800) & 1023) >> 3);
            Check($"{tag}: a 32-wide plane with the height code 128 is a valid 32 x 128 plane (unchanged)", bad == 0, $"{bad} pixels differ");
        }

        // ---- 3. shadow/highlight: a sprite pixel of palette line 3 with colour 14 or 15 is an operator on what lies under it, not a pixel of its own (the shading itself is not drawn, and says so)
        foreach (bool sh in new[] { true, false })
        {
            var r = new MdRig(chip); r.Color(0, 0); r.Color(1, 0x00E); r.Color(2, 0x0E0); r.Color(55, 0xE0E); r.Color(62, 0xEEE); r.Color(63, 0xEEE);
            r.Tile(1, 1); r.Tile(2, 15); r.Tile(3, 14); r.Tile(4, 7); r.Tile(5, 2);
            for (int row = 12; row < 15; row++) for (int col = 10; col < 17; col++) r.Cell(MdRig.PlaneA, 64, row, col, 1);        // a block of plane A (colour 1) the operator sprite lies over
            r.Sprite(0, 40, 40, 1, 1, 2, pal: 3); r.Sprite(1, 88, 96, 1, 1, 2, pal: 3); r.Sprite(2, 40, 60, 1, 1, 3, pal: 3); r.Sprite(3, 200, 40, 1, 1, 4, pal: 3); r.Sprite(4, 200, 60, 1, 1, 5, link: 0);
            r.Regs(0x04, 0x44, 0x30, 0x2C, 0x07, 0x7C, 0, 0x00, 0, 0, 0xFF, 0x00, sh ? 0x08 : 0x00, 0x3F, 0, 0x02, 0x01, 0, 0);
            r.Go();
            uint P(int x, int y) => At(r.Wrap.BridgeCanvas, x + 3, 8 + y + 3);
            uint white = MdLook(0xEEE);
            string mode = sh ? "in shadow/highlight mode" : "with shadow/highlight off";
            Check($"{tag}: {mode}: a shadow operator (palette 3, colour 15) over the backdrop {(sh ? "is not drawn" : "is an ordinary white pixel")}", P(40, 40) == (sh ? 0u : white), $"{P(40, 40):X6}");
            Check($"{tag}: {mode}: a highlight operator (palette 3, colour 14) over the backdrop {(sh ? "is not drawn" : "is an ordinary white pixel")}", P(40, 60) == (sh ? 0u : white), $"{P(40, 60):X6}");
            Check($"{tag}: {mode}: an operator over a plane pixel {(sh ? "leaves the plane's colour (the darkening is not drawn)" : "is an ordinary white pixel")}", P(88, 96) == (sh ? MdLook(0x00E) : white), $"{P(88, 96):X6}");
            Check($"{tag}: {mode}: a palette 3 sprite pixel that is not colour 14 or 15 is drawn", P(200, 40) == MdLook(0xE0E), $"{P(200, 40):X6}");
            Check($"{tag}: {mode}: a palette 0 sprite is drawn", P(200, 60) == MdLook(0x0E0), $"{P(200, 60):X6}");
            long ops = r.Wrap.Counters.Total("sprite pieces holding shadow/highlight operators (their pixels are not drawn, and neither is the shading they cause)");
            Check($"{tag}: {mode}: the operator pieces are counted ({(sh ? "2 distinct pieces a frame, over the frames run" : "none")})", sh ? ops >= 2 : ops == 0, $"{ops}; {r.Wrap.Counters}");
        }
    }

    private static void SmsScenes(string chip)
    {
        bool layered = chip == "NES:FIXS";
        string tag = $"Master System on {chip}";
        // ---- background: two tiles, backdrop, the cell's place
        var r = new SmsRig(chip);
        r.Cram(0, 0); r.Cram(1, Red); r.Cram(2, Green); r.Cram(16, 0);
        r.Tile(1, 1); r.Tile(2, 2);
        for (int row = 4; row < 9; row++) for (int col = 4; col < 20; col++) r.Cell(row, col, 1);
        for (int row = 10; row < 13; row++) for (int col = 6; col < 10; col++) r.Cell(row, col, 2);
        r.Frames();
        var reg = r.Wrap.Region;
        Check($"{tag}: the picture is the chip's 256x192, centred on the NES's 240 lines", reg == (0, 24, 256, 192), reg.ToString());
        Check($"{tag}: background cells are drawn where the VDP draws them", MaskMismatch(r.Wrap.BridgeCanvas, r.Wrap.ReferenceCanvas, reg) == 0, $"{MaskMismatch(r.Wrap.BridgeCanvas, r.Wrap.ReferenceCanvas, reg)} pixels differ");
        Check($"{tag}: the red cell is the NES colour that looks most like red", At(r.Wrap.BridgeCanvas, 40, 24 + 4 * 8 + 3) == SmsLook(Red) && At(r.Wrap.BridgeCanvas, 52, 24 + 10 * 8 + 3) == SmsLook(Green), $"{At(r.Wrap.BridgeCanvas, 40, 24 + 35):X6} want {SmsLook(Red):X6}; {At(r.Wrap.BridgeCanvas, 52, 24 + 83):X6} want {SmsLook(Green):X6}");
        Check($"{tag}: the lines above and below the 192 are black", At(r.Wrap.BridgeCanvas, 100, 3) == 0x000000 && At(r.Wrap.BridgeCanvas, 100, 236) == 0x000000 || !layered);
        var drawn = r.Wrap.Counters;
        Check($"{tag}: it says what it is and counts frames", r.Wrap.Description.Contains("PICTURE " + chip) && drawn.Frames >= 3, r.Wrap.Description.Split('|').Last().Trim());

        // ---- scroll: horizontal 13, vertical 5, tiles in a pattern
        r = new SmsRig(chip); r.Cram(0, 0); r.Cram(1, Red); r.Cram(2, Blue); r.Cram(16, 0); r.Tile(1, 1); r.Stripe(2, 2, 1);
        for (int row = 0; row < 24; row++) for (int col = 0; col < 32; col++) if ((col % 3 == 0 && row % 2 == 0) || (row == 12)) r.Cell(row, col, col % 2 == 0 ? 1 : 2);
        r.Regs(0x04, 0x40, 0x0E, 0, 0, 0x7E, 0xFB, 0x00, 13, 5, 0xFF);
        r.Frames();
        int mm = Mm(r.Wrap, tag);
        Check($"{tag}: horizontal scroll 13 and vertical scroll 5 move it as the VDP moves it", mm <= (layered ? 0 : 600), $"{mm} pixels differ (the flat path repeats the left column at the right edge)");
        // ---- scroll locks and the left-column mask
        r.Regs(0x04 | 0x40 | 0x80 | 0x20, 0x40, 0x0E, 0, 0, 0x7E, 0xFB, 0x00, 21, 3, 0xFF);
        r.Frames();
        mm = Mm(r.Wrap, tag);
        Check($"{tag}: the top-rows horizontal lock, the right-columns vertical lock and the left-column mask follow the VDP", mm <= (layered ? 0 : 4000), $"{mm} pixels differ");
        if (!layered) Check($"{tag}: ... the flat path cannot carry a lock (one fine scroll for the frame) and counts the lines that disagree", r.Wrap.Counters.Total("lines with a fine scroll other than the frame's") > 0, r.Wrap.Counters.ToString());

        // ---- sprites: ten on one line (8 drawn), tall, zoomed, shifted
        r = new SmsRig(chip); r.Cram(0, 0); r.Cram(16, 0); r.Cram(17, Green); r.Cram(18, Yellow); r.Tile(3, 1); r.Tile(4, 2);
        for (int i = 0; i < 10; i++) r.Sprite(i, 8 + i * 20, 80, 3);
        r.Sprite(10, 100, 120, 4); r.Frames();
        mm = Mm(r.Wrap, tag);
        Check($"{tag}: ten sprites on a line show the eight the VDP shows", mm == 0, $"{mm} pixels differ");
        if (layered) Check($"{tag}: the pieces the chip drops are counted", r.Wrap.Counters.Total("sprite pieces over the 8-per-line limit (the chip drops them too)") >= 16, r.Wrap.Counters.ToString());
        r.Regs(0x04, 0x42, 0x0E, 0, 0, 0x7E, 0xFB, 0x00, 0, 0, 0xFF); r.Frames();                // 8x16 sprites
        mm = Mm(r.Wrap, tag);
        Check($"{tag}: 8x16 sprites (tile pairs)", mm == 0, $"{mm} pixels differ");
        r.Regs(0x04, 0x41, 0x0E, 0, 0, 0x7E, 0xFB, 0x00, 0, 0, 0xFF); r.Frames();                // zoomed
        mm = Mm(r.Wrap, tag);
        Check($"{tag}: zoomed sprites (16x16 from 8x8)", mm == 0 || !layered, $"{mm} pixels differ");
        r.Regs(0x04 | 0x08, 0x40, 0x0E, 0, 0, 0x7E, 0xFB, 0x00, 0, 0, 0xFF); r.Frames();         // X shifted by 8
        mm = Mm(r.Wrap, tag);
        Check($"{tag}: the 8-pixel sprite shift", mm == 0, $"{mm} pixels differ");
        // six zoomed sprites on one line are twelve 8x8 pieces a line: the VDP shows them all, a NES chip drops the 9th on (16 lines), and the count says so
        r = new SmsRig(chip); r.Cram(0, 0); r.Cram(16, 0); r.Cram(17, Green); r.Tile(3, 1);
        for (int i = 0; i < 6; i++) r.Sprite(i, 8 + i * 30, 80, 3);
        r.V.Vram[0x3F00 + 6] = 0xD0;                                                          // the list ends here, as a game's does
        r.Regs(0x04, 0x41, 0x0E, 0, 0, 0x7E, 0xFB, 0x00, 0, 0, 0xFF); r.Frames();
        long overLines = r.Wrap.Counters.Total(layered ? "sprite pieces over the 8-per-line limit (the chip drops them too)" : "lines with more than 8 sprite pieces (the NES chip drops the rest)");
        Check($"{tag}: twelve sprite pieces a line: the flat chip (8 a line) counts the 16 lines it drops pieces on, each of 3 frames; the layered one (16 a line for zoomed sprites) loses nothing", layered ? overLines == 0 : overLines == 3 * 16, $"{overLines}; {r.Wrap.Counters}");

        // ---- priority: a background tile with the priority bit covers a sprite, one without does not
        r = new SmsRig(chip); r.Cram(0, 0); r.Cram(1, Blue); r.Cram(16, 0); r.Cram(17, Green); r.Tile(1, 1); r.Tile(3, 1);
        r.Cell(10, 10, 1, 0x1000); r.Cell(10, 14, 1, 0);
        r.Sprite(0, 80, 80, 3); r.Sprite(1, 112, 80, 3); r.Frames();
        bool inFront = At(r.Wrap.BridgeCanvas, 84, 24 + 84) == SmsLook(Green), covered = At(r.Wrap.BridgeCanvas, 84 + 0, 24 + 84) != SmsLook(Blue);
        uint overPrio = At(r.Wrap.BridgeCanvas, 84, 24 + 84), overPlain = At(r.Wrap.BridgeCanvas, 116, 24 + 84);
        Check($"{tag}: a priority background tile covers the sprite over it, a plain one does not", overPrio == SmsLook(Blue) && overPlain == SmsLook(Green), $"{overPrio:X6} / {overPlain:X6}");

        // ---- the Master System II's taller picture and an untranslated mode
        r = new SmsRig(chip); r.Cram(0, 0); r.Cram(1, Red); r.Cram(16, 0); r.Tile(1, 1);
        r.Regs(0x06, 0x48, 0x0E, 0, 0, 0x7E, 0xFB, 0x00, 0, 0, 0xFF);
        for (int row = 0; row < 31; row++) for (int col = 2; col < 30; col++) r.V.Vram[0x3700 + row * 64 + col * 2] = 1;
        r.Frames();
        Check($"{tag}: the 240-line mode shows its middle 224 lines (the 16 cut are counted)", r.Wrap.Region == (0, 8, 256, 224), r.Wrap.Region.ToString());
        mm = Mm(r.Wrap, tag);
        Check($"{tag}: ... and draws them as the VDP does", mm == 0, $"{mm} pixels differ");
        r = new SmsRig(chip); r.Regs(0x00, 0x40, 0x0E, 0, 0, 0x7E, 0xFB, 0x00, 0, 0, 0xFF); r.Frames();
        Check($"{tag}: a TMS9918 graphics mode is not translated, and the counter says so", r.Wrap.Counters.Total("frames in a TMS9918 graphics mode (not translated)") >= 2, r.Wrap.Counters.ToString());

        // ---- a static scene stays steady from frame to frame (no shimmer in the palettes)
        r = new SmsRig(chip); r.Cram(0, 0); r.Cram(1, Red); r.Cram(2, Green); r.Cram(3, Blue); r.Cram(4, Yellow); r.Cram(5, White); r.Cram(16, 0);
        for (int t = 1; t <= 5; t++) r.Tile(t, t);
        for (int row = 0; row < 24; row++) for (int col = 0; col < 32; col++) r.Cell(row, col, 1 + (row + col) % 5);
        r.Frames(2); var hashes = new HashSet<ulong>();
        for (int i = 0; i < 6; i++) { r.Frames(1); ulong h = 14695981039346656037UL; foreach (uint c in r.Wrap.Frame) h = (h ^ c) * 1099511628211UL; hashes.Add(h); }
        Check($"{tag}: a static scene is drawn identically every frame", hashes.Count == 1, $"{hashes.Count} different pictures");
    }

    private static void GgScenes()
    {
        foreach (var chip in new[] { "NES:FIXS", "NES:FIX" })
        {
            var r = new SmsRig(chip, gg: true);
            r.Gg(0, 0x000); r.Gg(1, 0x00F); r.Gg(16, 0x000); r.Tile(1, 1);
            for (int row = 0; row < 24; row++) for (int col = 0; col < 32; col++) if (col % 2 == 0 && row % 2 == 0) r.Cell(row, col, 1);
            r.Frames();
            string tag = $"Game Gear on {chip}";
            Check($"{tag}: the LCD window is 160x144 at the middle of the NES picture", r.Wrap.Region == (48, 48, 160, 144), r.Wrap.Region.ToString());
            Check($"{tag}: the bezel around it is black", At(r.Wrap.BridgeCanvas, 10, 10) == 0 && At(r.Wrap.BridgeCanvas, 240, 200) == 0 && At(r.Wrap.BridgeCanvas, 100, 20) == 0);
            int mm = Mm(r.Wrap, tag);
            Check($"{tag}: what the window shows is what the Game Gear shows", mm <= (chip == "NES:FIXS" ? 0 : 200), $"{mm} pixels differ");
            Check($"{tag}: a 12-bit colour (red 15) is the NES colour nearest to it", At(r.Wrap.BridgeCanvas, 49, 48 + 8) == NesLook(0xFF, 0, 0), $"{At(r.Wrap.BridgeCanvas, 49, 56):X6} want {NesLook(0xFF, 0, 0):X6}");
            if (chip == "NES:FIXS") Check($"{tag}: every pixel of the window is the NES colour nearest to the Game Gear's own", ColourMismatch(r.Wrap, r.Wrap.Region) == 0, $"{ColourMismatch(r.Wrap, r.Wrap.Region)} pixels differ");
        }
    }

    private static void MdScenes(string chip)
    {
        bool layered = chip == "NES:FIXS";
        string tag = $"Genesis on {chip}";
        // ---- planes, priority, the picture's place (H32: the 256 columns, V28: 224 lines at NES line 8)
        var r = new MdRig(chip);
        r.Color(0, 0); r.Color(1, 0x00E); r.Color(2, 0x0E0); r.Color(17, 0xE00); r.Color(18, 0x0EE); r.Color(33, 0xEEE);
        r.Tile(1, 1); r.Tile(2, 2); r.Tile(3, 1); r.Tile(4, 2); r.Tile(5, 1);
        for (int row = 3; row < 12; row++) for (int col = 3; col < 20; col++) r.Cell(MdRig.PlaneB, 64, row, col, 1);                 // plane B: red
        for (int row = 5; row < 9; row++) for (int col = 8; col < 14; col++) r.Cell(MdRig.PlaneA, 64, row, col, 4, 1);              // plane A low: yellow over it
        for (int row = 7; row < 11; row++) for (int col = 12; col < 17; col++) r.Cell(MdRig.PlaneB, 64, row, col, 3, 2, prio: true);   // plane B high: blue, over plane A low
        r.Go();
        var reg = r.Wrap.Region;
        Check($"{tag}: the picture is the chip's 256x224 at NES line 8", reg == (0, 8, 256, 224), reg.ToString());
        int mm = MaskMismatch(r.Wrap.BridgeCanvas, r.Wrap.ReferenceCanvas, reg);
        Check($"{tag}: planes are drawn where the VDP draws them", mm == 0, $"{mm} pixels differ");
        uint pa = At(r.Wrap.BridgeCanvas, 9 * 8 + 2, 8 + 5 * 8 + 2), pb = At(r.Wrap.BridgeCanvas, 13 * 8 + 2, 8 + 7 * 8 + 2), pc = At(r.Wrap.BridgeCanvas, 4 * 8 + 2, 8 + 4 * 8 + 2);
        Check($"{tag}: plane A over plane B, plane B's priority tile over plane A, by the VDP's ladder", pa == MdLook(0x0EE) && pb == MdLook(0xEEE) && pc == MdLook(0x00E), $"{pa:X6}/{pb:X6}/{pc:X6} want {MdLook(0x0EE):X6}/{MdLook(0xEEE):X6}/{MdLook(0x00E):X6}");

        // ---- per-line horizontal scroll and vertical scroll
        r = new MdRig(chip); r.Color(0, 0); r.Color(1, 0x00E); r.Color(2, 0x0E0); r.Tile(1, 1); r.Stripe(2, 2, 0);
        for (int row = 0; row < 28; row++) for (int col = 0; col < 64; col++) if ((col + row) % 4 == 0) r.Cell(MdRig.PlaneA, 64, row, col, col % 2 == 0 ? 1 : 2);
        for (int l = 0; l < 224; l++) r.W16(MdRig.HScroll + l * 4, (l * 3) & 0x1FF);
        r.Regs(0x04, 0x44, 0x30, 0x2C, 0x07, 0x7C, 0, 0x00, 0, 0, 0xFF, 0x03, 0x00, 0x3F, 0, 0x02, 0x01, 0, 0);
        r.V.Vsram[0] = 11; r.Go();
        mm = Mm(r.Wrap, tag);
        Check($"{tag}: a scroll value for every line and vertical scroll 11 move the picture as the VDP moves it", layered ? mm == 0 : r.Wrap.Counters.Total("lines with a fine scroll other than the frame's") > 100, $"{mm} pixels differ; {r.Wrap.Counters}");
        r.Regs(0x04, 0x44, 0x30, 0x2C, 0x07, 0x7C, 0, 0x00, 0, 0, 0xFF, 0x04 | 0x02, 0x00, 0x3F, 0, 0x02, 0x01, 0, 0);     // 8-line scroll, column vertical scroll
        for (int c = 0; c < 20; c++) r.V.Vsram[c * 2] = (ushort)(c * 5);
        for (int c = 0; c < 28; c++) r.W16(MdRig.HScroll + (c * 8) * 4, (c * 7) & 0x1FF);
        r.Go();
        mm = Mm(r.Wrap, tag);
        Check($"{tag}: 2-cell column vertical scroll (20 different values) is followed", layered ? mm == 0 : mm <= 12000, $"{mm} pixels differ; {r.Wrap.Counters}");
        if (!layered) Check($"{tag}: ... the flat path has one fine scroll for the frame and counts what disagrees", r.Wrap.Counters.Total("lines with a fine scroll other than the frame's") > 0, r.Wrap.Counters.ToString());

        // ---- the window replaces plane A
        r = new MdRig(chip); r.Color(0, 0); r.Color(1, 0x00E); r.Color(17, 0x0E0); r.Color(18, 0xE0E); r.Color(33, 0xE00); r.Tile(1, 1); r.Tile(2, 1); r.Tile(3, 2);
        // the window's cells alternate between two tiles of different colours, so a window drawn one cell to the side shows in the colours
        for (int row = 0; row < 28; row++) for (int col = 0; col < 32; col++) { if ((col + row) % 2 == 0) r.Cell(MdRig.PlaneA, 64, row, col, 1, 0); if (row < 3) r.Cell(MdRig.WindowNt, 32, row, col, 2 + col % 2, 1); }
        r.Regs(0x04, 0x44, 0x30, 0x2C, 0x07, 0x7C, 0, 0x00, 0, 0, 0xFF, 0x00, 0x00, 0x3F, 0, 0x02, 0x01, 0x04, 0x03);   // window at the top, 3 cells high ; left part 4*16 wide
        r.Go();
        mm = Mm(r.Wrap, tag);
        Check($"{tag}: the window layer replaces plane A where the registers put it", mm <= (layered ? 0 : 600), $"{mm} pixels differ");
        Check($"{tag}: the window's own tiles and palette show in it", At(r.Wrap.BridgeCanvas, 4, 8 + 4) == MdLook(0x0E0) || !layered, $"{At(r.Wrap.BridgeCanvas, 4, 12):X6}");
        if (layered) Check($"{tag}: the window's cells are on the columns the VDP puts them (every colour is the VDP's)", At(r.Wrap.BridgeCanvas, 4, 12) == MdLook(0x0E0) && At(r.Wrap.BridgeCanvas, 12, 12) == MdLook(0xE0E) && ColourMismatch(r.Wrap, r.Wrap.Region) == 0, $"{At(r.Wrap.BridgeCanvas, 4, 12):X6}/{At(r.Wrap.BridgeCanvas, 12, 12):X6}; {ColourMismatch(r.Wrap, r.Wrap.Region)} pixels differ");

        // ---- sprites against the planes: the ladder spr_hi > A_hi > B_hi > spr_lo > A_lo > B_lo
        r = new MdRig(chip); r.Color(0, 0); r.Color(1, 0x00E); r.Color(17, 0xE00); r.Color(33, 0x0E0); r.Tile(1, 1);
        r.Cell(MdRig.PlaneA, 64, 5, 4, 1, 0);                  // 1: plane A low, a low sprite over it: the sprite wins (blue)
        r.Cell(MdRig.PlaneA, 64, 5, 8, 1, 0, prio: true);      // 2: plane A high, a low sprite: plane A wins (red)
        r.Cell(MdRig.PlaneA, 64, 5, 12, 1, 0, prio: true);     // 3: plane A high, a high sprite: the sprite wins (blue)
        r.Cell(MdRig.PlaneB, 64, 5, 16, 1, 2, prio: true);     // 4: plane B high, a low sprite: plane B wins (green)
        r.Cell(MdRig.PlaneB, 64, 5, 20, 1, 2, prio: true); r.Cell(MdRig.PlaneA, 64, 5, 20, 1, 0);   // 5: plane B high over plane A low, a low sprite: plane B wins (green)
        for (int k = 0; k < 5; k++) r.Sprite(k, (4 + 4 * k) * 8, 5 * 8, 1, 1, 1, pal: 1, prio: k == 2, link: k == 4 ? 0 : k + 1);
        r.Go();
        uint[] want = { MdLook(0xE00), MdLook(0x00E), MdLook(0xE00), MdLook(0x0E0), MdLook(0x0E0) }; var got = new uint[5];
        for (int k = 0; k < 5; k++) got[k] = At(r.Wrap.BridgeCanvas, (4 + 4 * k) * 8 + 3, 8 + 5 * 8 + 3);
        Check($"{tag}: a sprite and a plane meet as the VDP's ladder says (sprite over plane A low, plane A high over a low sprite, a high sprite over everything, plane B high over a low sprite)", got.SequenceEqual(want) || !layered, string.Join("/", got.Select(g => g.ToString("X6"))) + " want " + string.Join("/", want.Select(g => g.ToString("X6"))));
        if (layered) Check($"{tag}: ... and every pixel of the scene is the VDP's", ColourMismatch(r.Wrap, r.Wrap.Region) == 0, $"{ColourMismatch(r.Wrap, r.Wrap.Region)} pixels differ");

        // ---- sprites: sizes, flips, a link chain, the per-line limit
        r = new MdRig(chip); r.Color(0, 0); r.Color(1, 0x00E); r.Color(2, 0x0E0); r.Color(17, 0xE00); r.Color(18, 0x0EE);
        for (int t = 1; t < 40; t++) r.Stripe(t, 1 + t % 2, 2 - t % 2);
        r.Sprite(0, 20, 30, 1, 1, 1); r.Sprite(1, 60, 30, 2, 2, 2, hf: true); r.Sprite(2, 120, 30, 3, 3, 8, pal: 1, vf: true); r.Sprite(3, 200, 30, 4, 4, 20, hf: true, vf: true, link: 0);
        r.Go();
        mm = Mm(r.Wrap, tag);
        Check($"{tag}: sprites of every size with flips, as the VDP draws them", layered ? mm == 0 : mm <= 400, $"{mm} pixels differ");
        if (!layered) Check($"{tag}: ... the NES chip drops the pieces past 8 on a line and the count says so", r.Wrap.Counters.Total("lines with more than 8 sprite pieces (the NES chip drops the rest)") > 0, r.Wrap.Counters.ToString());
        r = new MdRig(chip); r.Color(0, 0); r.Color(1, 0x00E); r.Tile(1, 1);
        for (int i = 0; i < 22; i++) r.Sprite(i, 4 + i * 11, 100, 1, 1, 1, link: i == 21 ? 0 : i + 1);
        r.Go();
        mm = Mm(r.Wrap, tag);
        Check($"{tag}: 22 sprites on a line show the 16 the 32-column VDP shows", mm == 0 || !layered, $"{mm} pixels differ");
        if (layered) Check($"{tag}: the sprites the VDP drops are counted", r.Wrap.Counters.Total("sprites the VDP dropped itself (20/16 a line, 40/32 cells, the X = 0 mask)") > 0, r.Wrap.Counters.ToString());

        // ---- H40 (320 columns) is cropped to the middle 256 and says so; V30 (240 lines) to the middle 224
        r = new MdRig(chip); r.Color(0, 0); r.Color(1, 0x00E); r.Color(2, 0x0E0); r.Tile(1, 1); r.Tile(2, 2);
        for (int row = 0; row < 28; row++) { r.Cell(MdRig.PlaneA, 64, row, 0, 1); r.Cell(MdRig.PlaneA, 64, row, 39, 2); r.Cell(MdRig.PlaneA, 64, row, 20, 1); }
        r.Regs(0x04, 0x4C, 0x30, 0x2C, 0x07, 0x7C, 0, 0x00, 0, 0, 0xFF, 0x00, 0x81, 0x3F, 0, 0x02, 0x01, 0, 0);
        r.Go();
        Check($"{tag}: a 320-column picture shows its middle 256 and counts the 64 columns cut", r.Wrap.Region == (0, 8, 256, 224) && r.Wrap.Counters.Total("columns cropped from the 320-wide picture") >= 64 * 2, r.Wrap.Counters.ToString());
        mm = Mm(r.Wrap, tag);
        Check($"{tag}: ... the cut picture is what the VDP shows in those columns (the edge cells are gone)", mm == 0, $"{mm} pixels differ");
        r = new MdRig(chip); r.Color(0, 0); r.Color(1, 0x00E); r.Tile(1, 1);
        for (int row = 0; row < 30; row++) for (int col = 2; col < 30; col++) r.Cell(MdRig.PlaneA, 64, row, col, 1);
        r.Regs(0x04, 0x4C, 0x30, 0x2C, 0x07, 0x7C, 0, 0x00, 0, 0, 0xFF, 0x00, 0x00, 0x3F, 0, 0x02, 0x01, 0, 0);
        r.Go();
        mm = Mm(r.Wrap, tag);
        Check($"{tag}: a 240-line picture shows its middle 224 lines", r.Wrap.Region == (0, 8, 256, 224) && mm == 0, $"{r.Wrap.Region} {mm} pixels differ");
        // shadow/highlight is not drawn, and says so; a Master System compatibility frame is not translated
        r = new MdRig(chip); r.Color(0, 0); r.Tile(1, 1); r.Cell(MdRig.PlaneA, 64, 4, 4, 1);
        r.Regs(0x04, 0x44, 0x30, 0x2C, 0x07, 0x7C, 0, 0x00, 0, 0, 0xFF, 0x00, 0x08, 0x3F, 0, 0x02, 0x01, 0, 0); r.Go();
        Check($"{tag}: shadow/highlight frames are counted (the shading is not drawn)", r.Wrap.Counters.Total("frames in shadow/highlight mode (the shading is not drawn)") >= 2, r.Wrap.Counters.ToString());
        r = new MdRig(chip); r.Regs(0x04, 0x40, 0x30, 0x2C, 0x07, 0x7C, 0, 0x00, 0, 0, 0xFF); r.Go();
        Check($"{tag}: a frame in the Master System compatibility mode is not translated and says so", r.Wrap.Counters.Total("frames in the Master System compatibility mode (not translated)") >= 2, r.Wrap.Counters.ToString());
        // a static scene is steady
        r = new MdRig(chip); r.Color(0, 0); r.Color(1, 0x00E); r.Color(2, 0x0E0); r.Color(3, 0xE00); r.Tile(1, 1); r.Tile(2, 2); r.Tile(3, 3);
        for (int row = 0; row < 28; row++) for (int col = 0; col < 40; col++) r.Cell(MdRig.PlaneA, 64, row, col, 1 + (row + col) % 3);
        r.Go(2); var hashes = new HashSet<ulong>();
        for (int i = 0; i < 6; i++) { r.Go(1); ulong h = 14695981039346656037UL; foreach (uint c in r.Wrap.Frame) h = (h ^ c) * 1099511628211UL; hashes.Add(h); }
        Check($"{tag}: a static scene is drawn identically every frame", hashes.Count == 1, $"{hashes.Count} different pictures");
    }

    // ================================================================================================================ the session wrapper
    public static void Session()
    {
        var r = new SmsRig("SMS");
        r.Cram(0, 0); r.Cram(1, Red); r.Cram(16, 0); r.Tile(1, 1);
        for (int row = 0; row < 24; row++) for (int col = 0; col < 32; col += 2) r.Cell(row, col, 1);
        r.Frames(2);
        Check("the console's own chip is a pass-through: 256x192, the VDP's own frame object", r.Wrap.FrameWidth == 256 && r.Wrap.FrameHeight == 192 && ReferenceEquals(r.Wrap.Frame, r.Inner.Frame) && !r.Wrap.Bridged);
        long frames0 = r.Inner.Board.Frames;
        Check("it swaps to a NES chip in place (the board goes on)", r.Wrap.TrySwapCore(CoreSlot.Ppu, "NES:FIXS") && r.Wrap.Bridged);
        r.Frames(2);
        Check("... the picture is the NES's 256x240 and the board's frame counter kept counting", r.Wrap.FrameWidth == 256 && r.Wrap.FrameHeight == 240 && r.Wrap.DisplayWidth == 256 && r.Inner.Board.Frames >= frames0 + 2, $"{r.Inner.Board.Frames} vs {frames0}");
        Check("... the description names the chip and carries the counters", r.Wrap.Description.Contains("PICTURE NES:FIXS") && r.Wrap.Description.Contains("["), r.Wrap.Description.Split('|').Last());
        Check("... it is a picture bridge with a name and a reference to compare with", r.Wrap.BridgeName == "Master System on NES:FIXS" && r.Wrap.ReferenceCanvas.Length == 256 * 240);
        Check("... through the NES onto the Game Boy chip too", r.Wrap.TrySwapCore(CoreSlot.Ppu, "NES:DMG") && Run3(r) && r.Wrap.Bridged);
        Check("... and back to the console's own chip", r.Wrap.TrySwapCore(CoreSlot.Ppu, "SMS") && !r.Wrap.Bridged && r.Wrap.FrameHeight == 192);
        Check("a chip that is not on the menu is refused (the app then restarts the game on it and says why), not replaced", !r.Wrap.TrySwapCore(CoreSlot.Ppu, "NES:NOPE") && !r.Wrap.TrySwapCore(CoreSlot.Ppu, "NOPE") && !r.Wrap.Bridged);
        Check("the sound and CPU slots still go to the board's own handling", r.Wrap.TrySwapCore(CoreSlot.Apu, "SMS") && r.Wrap.TrySwapCore(CoreSlot.Cpu, "Z80"));
        // creating a session straight on a NES chip, through ConsoleSessions
        var rom = new byte[0x8000]; rom[0] = 0xF3; rom[1] = 0x18; rom[2] = 0xFE;
        using (var s = ConsoleSessions.Create(ConsoleKind.MasterSystem, rom, "Z80", "NES:FIX", "SMS", null, "t.sms"))
        {
            s.RunFrame(); s.RunFrame();
            Check("ConsoleSessions.Create builds a Master System session on a NES chip", s is SegaPictureSession ps && ps.Bridged && s.FrameHeight == 240 && s.Description.Contains("PPU SMS") && s.Description.Contains("PICTURE NES:FIX"), s.Description);
        }
        using (var s = ConsoleSessions.Create(ConsoleKind.GameGear, new byte[0x8000], "Z80", "GG", "GG", null, "t.gg")) { s.RunFrame(); Check("a Game Gear session on its own chip is the 160x144 LCD", s.FrameWidth == 160 && s.FrameHeight == 144); }
        var bad = false; try { using var s = ConsoleSessions.Create(ConsoleKind.MasterSystem, rom, "Z80", "NES:NOPE", "SMS", null, "t.sms"); } catch (ArgumentException) { bad = true; }
        Check("a NES chip that does not exist makes the session refuse to start, not start on another chip", bad);
    }

    private static bool Run3(SmsRig r) { r.Frames(3); return r.Wrap.FrameWidth == 256 && r.Wrap.FrameHeight == 240; }

    // ================================================================================================================ real games
    public static void Games(string[] args)
    {
        string nesDir = Arg(args, "--nes-roms"), smsDir = Arg(args, "--sms-roms"), ggDir = Arg(args, "--gg-roms"), mdDir = Arg(args, "--md-roms");
        if (nesDir + smsDir + ggDir + mdDir == "") { Console.WriteLine("  (no ROM directories given: --nes-roms --sms-roms --gg-roms --md-roms; real games not tested)"); return; }
        const string input = "100:Start,106:,160:Start,166:,200:A,210:,260:Start,266:,300:Right,420:A,430:";
        void Sega(string dir, string file, ConsoleKind console, string chip, double minSsim)
        {
            string path = Path.Combine(dir, file);
            if (!File.Exists(path)) { Console.WriteLine($"  (missing {file})"); return; }
            var script = PictureBridgeCli.ParseInput(input);
            byte[] rom = File.ReadAllBytes(path);
            using var s = ConsoleSessions.Create(console, rom, CoreCatalog.Default(console, CoreSlot.Cpu), chip, CoreCatalog.Default(console, CoreSlot.Apu), null, Path.GetFileName(path));
            var info = (IPictureBridgeInfo)s; var held = PadButtons.None; var fid = new List<PictureMetrics.Result>(); var buf = new short[65536];
            for (int f = 1; f <= 400; f++)
            {
                if (script.TryGetValue(f - 1, out var h)) held = h;
                s.SetPad(0, held); s.RunFrame(); while (s.ReadSamples(buf) > 0) { }
                if (f > 60 && f % 20 == 0) fid.Add(PictureMetrics.Compare(info.ReferenceCanvas, info.BridgeCanvas, 256, info.Region.X, info.Region.Y, info.Region.W, info.Region.H));
            }
            double ssim = fid.Average(x => x.Ssim);
            Check($"{Consoles.DisplayName(console)} {Path.GetFileNameWithoutExtension(file)} on {chip}: recognisable and tracked ({fid.Count} samples)", ssim >= minSsim && fid.Count(x => x.Ssim >= 0.5) >= fid.Count * 0.8, $"SSIM {ssim:F3} (worst {fid.Min(x => x.Ssim):F3}); {info.Counters.Summary(3)}");
        }
        if (smsDir != "")
            foreach (var (file, min) in new[] { ("Alex Kidd in Miracle World (USA).sms", 0.80), ("Sonic The Hedgehog (USA, Europe).sms", 0.90), ("Shinobi (USA, Europe).sms", 0.95), ("R-Type (USA, Europe).sms", 0.95), ("Wonder Boy (USA, Europe).sms", 0.90) })
                foreach (var chip in new[] { "NES:FIXS", "NES:FIX" }) Sega(smsDir, file, ConsoleKind.MasterSystem, chip, chip == "NES:FIXS" ? min : min - 0.1);
        if (ggDir != "")
            foreach (var (file, min) in new[] { ("Sonic The Hedgehog (U) (V1.1) [!].gg", 0.80), ("Columns (U) [!].gg", 0.80), ("Castle of Illusion Starring Mickey Mouse (U) [S][!].gg", 0.70) })
                Sega(ggDir, file, ConsoleKind.GameGear, "NES:FIXS", min);
        if (mdDir != "")
            foreach (var (file, min) in new[] { ("Sonic the Hedgehog (JUE) [!].bin", 0.90), ("Streets of Rage (JUE) (REV 00) [!].bin", 0.85), ("Golden Axe (JU) (REV 00) [!].bin", 0.88), ("Flicky (JUE) [!].bin", 0.95), ("ECCO (U) [!].bin", 0.85), ("Strider (UE) [!].bin", 0.80) })
                Sega(mdDir, file, ConsoleKind.Genesis, "NES:FIXS", min);
        if (nesDir != "")
            foreach (var game in (Environment.GetEnvironmentVariable("PBT_NES_ONLY") ?? "smb1,smb3,zelda,mm3,cv3,battletoads,vrun").Split(','))
            {
                string path = Path.Combine(nesDir, game + ".nes"); if (!File.Exists(path)) { Console.WriteLine($"  (missing {game}.nes)"); continue; }
                foreach (var chip in new[] { "SMS", "MD" })
                {
                    var script = MixLab.MixLabCli.ParseNesInput(input);
                    var nes = new NES { RomName = game + ".nes" }; nes.LoadROM(File.ReadAllBytes(path)); nes.SetCpuCore("FIX"); nes.SetApuCore("FIX"); nes.SetPpuCore(chip);
                    var fix = new NES { RomName = game + ".nes" }; fix.LoadROM(File.ReadAllBytes(path)); fix.SetCpuCore("FIX"); fix.SetApuCore("FIX"); fix.SetPpuCore("FIX");     // selected the same way, so the power-on state is the same
                    var held = new bool[8]; var fid = new List<PictureMetrics.Result>(); bool sameFront = true;
                    for (int f = 1; f <= 600; f++)
                    {
                        if (script.TryGetValue(f - 1, out var h)) held = h;
                        nes.SetInputs(held, null); nes.RunFrame(); fix.SetInputs(held, null); fix.RunFrame();
                        var info = nes.GetPictureBridge()!;
                        if (f > 60 && f % 20 == 0)
                        {
                            fid.Add(PictureMetrics.Compare(info.ReferenceCanvas, info.BridgeCanvas, 256, 0, 0, 256, 240));
                            // the NES chip in front draws what PPU_FIX alone draws: the bridge does not disturb the game
                            var bFix = fix.GetFrameBuffer(); var refc = info.ReferenceCanvas;
                            for (int i = 0; i < refc.Length && sameFront; i += 997) { uint c = 0xFF000000u | (uint)bFix[i * 4] << 16 | (uint)bFix[i * 4 + 1] << 8 | bFix[i * 4 + 2]; if (c != refc[i]) sameFront = false; }
                        }
                    }
                    double ssim = fid.Average(x => x.Ssim);
                    Check($"NES {game} on PPU_{chip}: tracks the NES picture ({fid.Count} samples) and the game runs exactly as on PPU_FIX", ssim >= (chip == "MD" ? 0.88 : 0.82) && sameFront, $"SSIM {ssim:F3} (worst {fid.Min(x => x.Ssim):F3}); front identical: {sameFront}");
                }
            }
    }
}
