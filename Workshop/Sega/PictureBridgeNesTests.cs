using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NesEmulator;
using NesEmulator.Mix;
using static BrokenNes.Workshop.Sega.PictureBridgeTestCli;

namespace BrokenNes.Workshop.Sega;

/// <summary>The NES -> Sega checks of <c>--picture-bridge-test</c>: synthetic NES pictures through <c>PPU_SMS</c> and <c>PPU_MD</c>, with the picture the Sega chip drew compared with what the rule says it must show.</summary>
internal static class NesOnSega
{
    private sealed class Rig
    {
        public readonly NesPictureSink Sink; public readonly NES Nes; public readonly Bus Bus;
        public Rig(string ppu)
        {
            Sink = new NesPictureSink(ppu);
            Nes = (NES)typeof(NesPictureSink).GetField("nes", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Sink)!;
            Bus = (Bus)typeof(NES).GetField("bus", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Nes)!;
        }
        public NesFrontedPpu Ppu => (NesFrontedPpu)Sink.Ppu;
        public byte[] Frame() => Nes.GetFrameBuffer();
        /// <summary>Run two more NES frames with $2000 / $2001 set to the given values (the picture's own programming is kept).</summary>
        public byte[] Again(byte ctrl, byte mask) { Bus.Write(0x2000, ctrl); Bus.Write(0x2001, mask); Nes.RunFrame(); Nes.RunFrame(); return Nes.GetFrameBuffer(); }
    }

    private static int Px(byte[] f, int x, int y) { int o = (y * 256 + x) * 4; return f[o] << 16 | f[o + 1] << 8 | f[o + 2]; }

    private static int SmsRgb(int rgb) { int six = SegaColour.Sms6(rgb); int[] l = { 0, 0x52, 0xAD, 0xFF }; return l[six & 3] << 16 | l[six >> 2 & 3] << 8 | l[six >> 4 & 3]; }
    private static int MdRgb(int rgb) { int v = SegaColour.Md9(rgb); int[] l = { 0, 49, 87, 119, 146, 174, 206, 255 }; return l[v >> 1 & 7] << 16 | l[v >> 5 & 7] << 8 | l[v >> 9 & 7]; }
    private static int Nes(int idx) => NesFixPalette.Rgb(idx);

    private static void Solid(byte[] chr, int tile, int c) { for (int r = 0; r < 8; r++) { chr[tile * 16 + r] = (byte)((c & 1) != 0 ? 0xFF : 0); chr[tile * 16 + 8 + r] = (byte)((c & 2) != 0 ? 0xFF : 0); } }
    private static void Halves(byte[] chr, int tile, int left, int right)
    {
        for (int r = 0; r < 8; r++)
        {
            int p0 = ((left & 1) != 0 ? 0xF0 : 0) | ((right & 1) != 0 ? 0x0F : 0), p1 = ((left & 2) != 0 ? 0xF0 : 0) | ((right & 2) != 0 ? 0x0F : 0);
            chr[tile * 16 + r] = (byte)p0; chr[tile * 16 + 8 + r] = (byte)p1;
        }
    }
    /// <summary>A sprite tile: the top four rows left half colour a, the bottom four rows right half colour b, everything else clear.</summary>
    private static void Corner(byte[] chr, int tile, int a, int b)
    {
        for (int r = 0; r < 8; r++)
        {
            int c = r < 4 ? a : b, mask = r < 4 ? 0xF0 : 0x0F;
            chr[tile * 16 + r] = (byte)((c & 1) != 0 ? mask : 0); chr[tile * 16 + 8 + r] = (byte)((c & 2) != 0 ? mask : 0);
        }
    }
    private static void Cell(NesPicture p, int cx, int cy, int tile, int pal)
    {
        p.Nt0[cy * 32 + cx] = (byte)tile;
        int ai = 0x3C0 + (cy >> 2) * 8 + (cx >> 2), sh = ((cy & 2) << 1) | (cx & 2);
        p.Nt0[ai] = (byte)((p.Nt0[ai] & ~(3 << sh)) | (pal << sh));
    }
    private static void Spr(NesPicture p, int i, int x, int y, int tile, int attr) { p.Oam[i * 4] = (byte)y; p.Oam[i * 4 + 1] = (byte)tile; p.Oam[i * 4 + 2] = (byte)attr; p.Oam[i * 4 + 3] = (byte)x; }
    private static NesPicture Blank() { var p = new NesPicture(); Array.Fill(p.Oam, (byte)0xFF); return p; }

    public static void Run()
    {
        foreach (var (id, rgbOf) in new (string, Func<int, int>)[] { ("SMS", SmsRgb), ("MD", MdRgb) })
        {
            Console.WriteLine($"-- PPU_{id}");
            Guard($"PPU_{id}", () => Chip(id, rgbOf));
        }
        Guard("PPU_SMS: the tile budget and the sprite limit", Limits);
        Guard("PPU_MD: all 12 sprites on a line are shown", MdSprites);
        Guard("the NES in front is untouched", FrontUntouched);
    }

    private static void Chip(string id, Func<int, int> rgbOf)
    {
        // palettes: background 0 = 16 2A 11, background 2 = 28 1A 30, sprite 1 = 21 37 06, universal backdrop 0F
        var pic = Blank();
        pic.Pal[0] = 0x0F;
        pic.Pal[1] = 0x16; pic.Pal[2] = 0x2A; pic.Pal[3] = 0x11;
        pic.Pal[9] = 0x28; pic.Pal[10] = 0x1A; pic.Pal[11] = 0x30;
        pic.Pal[16 + 4 + 1] = 0x21; pic.Pal[16 + 4 + 2] = 0x37; pic.Pal[16 + 4 + 3] = 0x06;
        Halves(pic.ChrBg, 1, 1, 2); Solid(pic.ChrBg, 2, 3);
        Corner(pic.ChrObj, 0, 1, 2); Solid(pic.ChrObj, 1, 3);
        Cell(pic, 4, 5, 1, 0); Cell(pic, 5, 5, 2, 0); Cell(pic, 10, 10, 1, 2);
        var rig = new Rig(id);
        var f = rig.Sink.Present(pic);
        Check($"PPU_{id}: the backdrop is the universal colour", Px(f, 1, 1) == rgbOf(Nes(0x0F)), $"{Px(f, 1, 1):X6} want {rgbOf(Nes(0x0F)):X6}");
        Check($"PPU_{id}: a tile's two colours of palette 0 land on the NES colours by the reduction rule", Px(f, 33, 41) == rgbOf(Nes(0x16)) && Px(f, 38, 41) == rgbOf(Nes(0x2A)), $"{Px(f, 33, 41):X6}/{Px(f, 38, 41):X6}");
        Check($"PPU_{id}: the next tile's colour 3", Px(f, 43, 41) == rgbOf(Nes(0x11)), $"{Px(f, 43, 41):X6}");
        Check($"PPU_{id}: a cell of attribute palette 2 takes that palette", Px(f, 81, 81) == rgbOf(Nes(0x28)) && Px(f, 86, 81) == rgbOf(Nes(0x1A)), $"{Px(f, 81, 81):X6}/{Px(f, 86, 81):X6}");
        Check($"PPU_{id}: the tile's edges are where the nametable puts them", Px(f, 31, 41) == rgbOf(Nes(0x0F)) && Px(f, 32, 41) == rgbOf(Nes(0x16)) && Px(f, 32, 39) == rgbOf(Nes(0x0F)) && Px(f, 32, 40) == rgbOf(Nes(0x16)) && Px(f, 32, 47) == rgbOf(Nes(0x16)) && Px(f, 32, 48) == rgbOf(Nes(0x0F)));

        // sprites: y 49 puts the top at line 50; palette 1
        var s = Blank(); s.Pal[0] = 0x0F; Array.Copy(pic.Pal, s.Pal, 32); Array.Copy(pic.ChrObj, s.ChrObj, 4096); Array.Copy(pic.ChrBg, s.ChrBg, 4096);
        Spr(s, 0, 100, 49, 0, 1); Spr(s, 1, 140, 49, 0, 1 | 0x40); Spr(s, 2, 180, 49, 0, 1 | 0x80); Spr(s, 3, 220, 49, 0, 1 | 0xC0);
        f = rig.Sink.Present(s);
        Check($"PPU_{id}: a sprite's top-left block (colour 1 of sprite palette 1) is at x, y + 1", Px(f, 100, 50) == rgbOf(Nes(0x21)) && Px(f, 103, 53) == rgbOf(Nes(0x21)) && Px(f, 99, 50) == rgbOf(Nes(0x0F)) && Px(f, 100, 49) == rgbOf(Nes(0x0F)));
        Check($"PPU_{id}: its bottom-right block is colour 2 and the rest is clear", Px(f, 104, 54) == rgbOf(Nes(0x37)) && Px(f, 107, 57) == rgbOf(Nes(0x37)) && Px(f, 104, 50) == rgbOf(Nes(0x0F)) && Px(f, 100, 54) == rgbOf(Nes(0x0F)));
        Check($"PPU_{id}: horizontal flip moves the blocks to the other side", Px(f, 144, 50) == rgbOf(Nes(0x21)) && Px(f, 147, 53) == rgbOf(Nes(0x21)) && Px(f, 140, 54) == rgbOf(Nes(0x37)) && Px(f, 143, 57) == rgbOf(Nes(0x37)) && Px(f, 140, 50) == rgbOf(Nes(0x0F)));
        Check($"PPU_{id}: vertical flip swaps top and bottom", Px(f, 180, 54) == rgbOf(Nes(0x21)) && Px(f, 183, 57) == rgbOf(Nes(0x21)) && Px(f, 184, 50) == rgbOf(Nes(0x37)) && Px(f, 187, 53) == rgbOf(Nes(0x37)) && Px(f, 180, 50) == rgbOf(Nes(0x0F)));
        Check($"PPU_{id}: both flips", Px(f, 224, 54) == rgbOf(Nes(0x21)) && Px(f, 227, 57) == rgbOf(Nes(0x21)) && Px(f, 220, 50) == rgbOf(Nes(0x37)) && Px(f, 223, 53) == rgbOf(Nes(0x37)));

        // a sprite behind the background: hidden by an opaque background pixel, shown over a clear one
        var b = Blank(); Array.Copy(pic.Pal, b.Pal, 32); Array.Copy(pic.ChrObj, b.ChrObj, 4096); Array.Copy(pic.ChrBg, b.ChrBg, 4096);
        Cell(b, 12, 6, 2, 0);                                                   // solid colour 3 at (96..103, 48..55)
        Spr(b, 0, 96, 47, 1, 0x20 | 1);                                          // solid sprite (colour 3 of palette 1) behind it, over the whole cell
        Spr(b, 1, 120, 47, 1, 1);                                                // the same, in front, over nothing
        Spr(b, 2, 100, 51, 1, 0x20 | 1);                                         // behind, half over the cell and half over clear background
        f = rig.Sink.Present(b);
        Check($"PPU_{id}: a sprite behind the background is hidden by it", Px(f, 98, 50) == rgbOf(Nes(0x11)), $"{Px(f, 98, 50):X6}");
        Check($"PPU_{id}: a sprite in front of clear background is drawn", Px(f, 122, 50) == rgbOf(Nes(0x06)), $"{Px(f, 122, 50):X6}");
        Check($"PPU_{id}: a behind-sprite shows where nothing opaque is", Px(f, 106, 56) == rgbOf(Nes(0x06)), $"{Px(f, 106, 56):X6}");

        // fine scroll
        var sc = Blank(); Array.Copy(pic.Pal, sc.Pal, 32); Array.Copy(pic.ChrBg, sc.ChrBg, 4096);
        Cell(sc, 4, 5, 2, 0); sc.FineX = 3; sc.FineY = 2;
        f = rig.Sink.Present(sc);
        Check($"PPU_{id}: fine X scroll 3 moves the picture 3 pixels left", Px(f, 29, 40) == rgbOf(Nes(0x11)) && Px(f, 28, 40) == rgbOf(Nes(0x0F)) && Px(f, 36, 40) == rgbOf(Nes(0x11)) && Px(f, 37, 40) == rgbOf(Nes(0x0F)), $"{Px(f, 29, 40):X6} {Px(f, 28, 40):X6} {Px(f, 36, 40):X6} {Px(f, 37, 40):X6}");
        Check($"PPU_{id}: fine Y scroll 2 moves it 2 lines up", Px(f, 30, 38) == rgbOf(Nes(0x11)) && Px(f, 30, 37) == rgbOf(Nes(0x0F)) && Px(f, 30, 45) == rgbOf(Nes(0x11)) && Px(f, 30, 46) == rgbOf(Nes(0x0F)), $"{Px(f, 30, 38):X6} {Px(f, 30, 37):X6} {Px(f, 30, 45):X6} {Px(f, 30, 46):X6}");

        // rendering off shows the backdrop only; counters exist and the chip says what it is
        var off = Blank(); Array.Copy(pic.Pal, off.Pal, 32); Array.Copy(pic.ChrBg, off.ChrBg, 4096); Cell(off, 4, 5, 2, 0); off.Blank = true;
        f = rig.Sink.Present(off);
        Check($"PPU_{id}: with rendering off the picture is the backdrop", Px(f, 36, 44) == rgbOf(Nes(0x0F)));
        Check($"PPU_{id}: it counts the frames it translated and names itself", rig.Ppu.Counters.Frames > 3 && rig.Ppu.CoreName.StartsWith(id + ":"), rig.Ppu.CoreName);
        // 8x16 sprites: tile 1 (odd) takes the top half from tile 0 and the bottom half from tile 1 of the sprite table at $1000
        var t = Blank(); Array.Copy(pic.Pal, t.Pal, 32); Array.Copy(pic.ChrObj, t.ChrObj, 4096);
        Spr(t, 0, 60, 99, 1, 1);
        f = rig.Sink.Present(t);          // programs the picture ($2000 = 8x8) ...
        f = rig.Again(0x28, 0x1E);        // ... then the NES chip is switched to 8x16 sprites for two more frames
        Check($"PPU_{id}: an 8x16 sprite is a tile pair, the top half then the bottom half", Px(f, 60, 100) == rgbOf(Nes(0x21)) && Px(f, 63, 103) == rgbOf(Nes(0x21)) && Px(f, 64, 104) == rgbOf(Nes(0x37)) && Px(f, 60, 108) == rgbOf(Nes(0x06)) && Px(f, 67, 115) == rgbOf(Nes(0x06)) && Px(f, 60, 116) == rgbOf(Nes(0x0F)), $"{Px(f, 60, 100):X6} {Px(f, 64, 104):X6} {Px(f, 60, 108):X6} {Px(f, 60, 116):X6}");
        // greyscale (bit 0 of $2001) draws every colour as its grey (index & $30)
        rig.Sink.Present(sc);
        f = rig.Again(0x08, 0x1F);
        Check($"PPU_{id}: greyscale shows the colour index & $30", Px(f, 32, 44) == rgbOf(NesFixPalette.Rgb(0x11 & 0x30)), $"{Px(f, 32, 44):X6} want {rgbOf(NesFixPalette.Rgb(0x11 & 0x30)):X6}");
    }

    private static void Limits()
    {
        // 256 background tiles x 2 palettes is more variants than the chip's video memory holds
        var p = Blank(); p.Pal[0] = 0x0F; p.Pal[1] = 0x16; p.Pal[2] = 0x2A; p.Pal[3] = 0x11; p.Pal[5] = 0x21; p.Pal[6] = 0x37; p.Pal[7] = 0x06;
        for (int t = 0; t < 256; t++) for (int r = 0; r < 8; r++) { p.ChrBg[t * 16 + r] = (byte)(t ^ (r * 37)); p.ChrBg[t * 16 + 8 + r] = (byte)(~t + r * 11); }
        for (int cy = 0; cy < 30; cy++) for (int cx = 0; cx < 32; cx++) Cell(p, cx, cy, (cy * 32 + cx) % 256, (cy / 8) & 1);
        var rig = new Rig("SMS");
        var f = rig.Sink.Present(p);
        Check("PPU_SMS: more tile variants than the chip can hold is counted, not hidden", rig.Ppu.Counters.Total("background tiles past the video memory") > 0, rig.Ppu.Counters.ToString());
        Check("PPU_SMS: ... and the picture is still drawn", Enumerable.Range(0, 256 * 240).Select(i => Px(f, i % 256, i / 256)).Distinct().Count() > 2);
        // nine sprites on one line: the chip draws eight, as a NES would
        var s = Blank(); s.Pal[0] = 0x0F; s.Pal[16 + 1] = 0x16; Solid(s.ChrObj, 0, 1);
        for (int i = 0; i < 10; i++) Spr(s, i, 10 + i * 20, 99, 0, 0);
        rig = new Rig("SMS");
        f = rig.Sink.Present(s);
        int drawn = Enumerable.Range(0, 10).Count(i => Px(f, 12 + i * 20, 102) != SmsRgb(Nes(0x0F)));
        Check("PPU_SMS: ten sprites on a line show eight (the chip's own limit, the same as the NES's)", drawn == 8, $"{drawn} drawn");
        Check("PPU_SMS: the lines with more than eight are counted", rig.Ppu.Counters.Total("lines with more than 8 sprites (the 9th and later are dropped, as on a NES)") >= 8, rig.Ppu.Counters.ToString());
    }

    private static void MdSprites()
    {
        var s = Blank(); s.Pal[0] = 0x0F; s.Pal[16 + 1] = 0x16; Solid(s.ChrObj, 0, 1);
        for (int i = 0; i < 12; i++) Spr(s, i, 10 + i * 20, 99, 0, 0);
        var rig = new Rig("MD");
        var f = rig.Sink.Present(s);
        int drawn = Enumerable.Range(0, 12).Count(i => Px(f, 12 + i * 20, 102) != MdRgb(Nes(0x0F)));
        Check("PPU_MD: twelve sprites on a line are all shown (the chip allows 16)", drawn == 12, $"{drawn} drawn");
        Check("PPU_MD: and it says the NES would have dropped some", rig.Ppu.Counters.Total("lines with more than 8 sprites (all shown; a NES would drop the rest)") >= 8, rig.Ppu.Counters.ToString());
    }

    private static void FrontUntouched()
    {
        // the same picture through PPU_FIX, PPU_SMS and PPU_MD: the NES chip in front draws the same picture whatever the Sega chip does
        var p = Blank(); p.Pal[0] = 0x0F; p.Pal[1] = 0x16; p.Pal[2] = 0x2A; p.Pal[3] = 0x11; p.Pal[17 + 3] = 0x21;
        Halves(p.ChrBg, 1, 1, 2); Cell(p, 4, 5, 1, 0); Solid(p.ChrObj, 0, 3); Spr(p, 0, 50, 50, 0, 0);
        var fix = new Rig("FIX").Sink.Present(p);
        foreach (var id in new[] { "SMS", "MD" })
        {
            var rig = new Rig(id);
            rig.Sink.Present(p);
            var front = rig.Ppu.FrontFrame;
            bool same = front.Length == fix.Length && front.AsSpan().SequenceEqual(fix);
            Check($"PPU_{id}: the NES chip in front draws exactly what PPU_FIX alone draws", same);
            Check($"PPU_{id}: it claims the FIX timing, so the board runs a NES game on it as on PPU_FIX", rig.Bus.ppu is IPpuFixTiming);
        }
    }
}
