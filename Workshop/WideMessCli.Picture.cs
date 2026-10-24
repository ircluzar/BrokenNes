using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes.Workshop;

/// <summary>
/// Milestone 2's checks (docs/projects/widemess-spec.md sections 3.1-3.4), part of <c>--wms-test</c>:
/// <list type="bullet">
/// <item>The picture test ROMs (WideMess/testroms/picture: sprites, windows, widesnes), each against the picture its generator's model of the spec draws (the .expect.png files
/// beside the ROMs), each with its negative controls (the same ROM without the unlock, and the feature switched off).</item>
/// <item>SNES games drawn wide: with <c>--snes</c>, each game also runs on the WideMess console with WIDE forced on (the extension stays locked, so the game is a SNES game),
/// every line drawn twice, wide and on the SNES chip's own pipeline: columns 72-327 of the wide line must be the SNES line (spec 3.1).</item>
/// <item>Speed: the wide renderer's frames per second on the test ROMs, and zero bytes allocated per frame.</item>
/// </list>
/// </summary>
internal static partial class WideMessCli
{
    private static readonly string[] PictureRoms = { "sprites", "windows", "widesnes" };

    /// <summary>The picture test ROMs: false when one is missing (build WideMess/testroms/picture/build.ps1).</summary>
    private static bool PictureTests(string dir)
    {
        foreach (var rom in PictureRoms)
            foreach (var f in new[] { $"{rom}.wms", $"{rom}_nounlock.wms", $"{rom}.expect.png", $"{rom}_nounlock.expect.png" })
                if (!File.Exists(Path.Combine(dir, f))) { Console.Error.WriteLine($"missing {Path.Combine(dir, f)} (build WideMess/testroms/picture/build.ps1)"); return false; }

        Console.WriteLine("P1. Sprites: the 256-sprite table in the WideMess mode at 400 (WideMess/testroms/picture, scene 'sprites')");
        using (var s = PictureRun(dir, "sprites.wms"))
        {
            Check(s.WideMess is { Unlocked: true } && s.WideMessPpu!.WideSpriteTable, "the ROM unlocked and turned the WideMess table on (WMOBJ bit 0)");
            ComparePicture(s, dir, "sprites.expect.png", "every pixel as the model draws it: 16 sizes 8x8-64x64, 4 and 8 bpp, flips, X = -63 / -8 / 380 / 396 / 400 / 511 / -512, Y = -8 / 216, table order over priority, the slots between the layers (WMOBJPRI)");
            byte stat = s.Board.Wram[0x10], stat77 = s.Board.Wram[0x11];
            Check(stat == 0xC0, "the ROM read WMOBJSTAT: more than 64 sprites on a line (rows 216-223) and more than 128 slivers (rows 0-7)", $"WMOBJSTAT {stat:X2}");
            Check((stat77 & 0xC0) == 0, "the WideMess table leaves the SNES's STAT77 flags alone", $"STAT77 {stat77:X2}");
        }
        PictureVariant(dir, "sprites.wms", "sprites.snes_table.expect.png", p => p.WriteWide(0xA0, 0x00),
            "negative control, WMOBJ bit 0 cleared: the WideMess table's sprites are gone and the SNES table draws, X at picture column 72 + X (X = 256 at 328, X = -60 at 12)");
        PictureVariant(dir, "sprites.wms", "sprites.narrow.expect.png", p => p.WriteWide(0xC0, 0x02),
            "the WideMess mode 256 wide (WMCTRL = LAYERS8 alone): the same sprites in 256 columns");
        using (var s = PictureRun(dir, "sprites_nounlock.wms"))
            ComparePicture(s, dir, "sprites_nounlock.expect.png", "negative control, the same ROM without the unlock: a SNES picture (256 wide, the SNES table at SNES positions), nothing of the WideMess writes", locked: true);
        using (var s = PictureRun(dir, "sprites.wms"))
        {
            byte[] state = s.SaveState();
            var ppu = s.WideMessPpu!;
            Array.Fill(ppu.WideOam, (byte)0x5A);               // spoil what the state must bring back: the table and the M2 registers
            ppu.WriteWide(0xA0, 0x00); ppu.WriteWide(0xA6, 0x00); ppu.WriteWide(0xC0, 0x01);
            s.LoadState(state);
            Run(s, 2);
            ComparePicture(s, dir, "sprites.expect.png", "a save state carries the sprite table and its registers (spoiled after saving, loaded back: the same picture)");
        }

        Console.WriteLine("P2. Windows and colour math at 400 (scene 'windows')");
        using (var s = PictureRun(dir, "windows.wms"))
            ComparePicture(s, dir, "windows.expect.png", "every pixel as the model draws it: windows at 9-bit positions (window 1 shaped by HDMA, 102-297), OR / AND / XOR / XNOR and inverted windows for BG1-BG8, main and sub masks, add / subtract / half, the sub screen and the fixed colour, clip to black and the math region, the per-sprite math bit");
        PictureVariant(dir, "windows.wms", "windows.snes_positions.expect.png", p => p.WriteWide(0xB4, 0x00),
            "WMWCTL bit 0 cleared: the SNES's WH0-WH3 at 400 (0-150 is columns 0-222, 60-255 is 132-399)");
        using (var s = PictureRun(dir, "windows_nounlock.wms"))
            ComparePicture(s, dir, "windows_nounlock.expect.png", "negative control without the unlock: a SNES picture, only the SNES registers' colour math on its backdrop", locked: true);

        Console.WriteLine("P3. SNES modes drawn 400 wide (scene 'widesnes': bands of mode 1, mode 0, hi-res mode 5, mode 2 with offset-per-tile and mosaic, and Mode 7, set by HDMA)");
        uint[] wide;
        using (var s = PictureRun(dir, "widesnes.wms", compare: true))
        {
            var ppu = s.WideMessPpu!;
            ComparePicture(s, dir, "widesnes.expect.png", "every pixel as the model draws it: a 64-wide map's both halves, a 32-wide map repeated, hi-res averaged to 400, offset-per-tile and mosaic past the SNES edge, Mode 7 continued to both edges, the SNES sprite table across 400 (X = 0 / 255 / 256 / 300 / 327 / 328 / -72 / -100), SNES windows with an edge on the SNES edge");
            Check(ppu.CompareRows > 0 && ppu.CompareMismatches == 0, "every line's columns 72-327 are the SNES chip's own line (modes 1, 0, 5, 2 and 7)", $"{ppu.CompareRows} lines, {ppu.CompareMismatches} differ");
            wide = s.Frame.AsSpan(0, 400 * 224).ToArray();
        }
        using (var s = PictureRun(dir, "widesnes_nounlock.wms"))
        {
            ComparePicture(s, dir, "widesnes_nounlock.expect.png", "negative control without the unlock: the SNES picture, 256 wide (its hi-res lines averaged)", locked: true, snesPicture: true);
            var snes = s.WideMessPpu!.FrameBuffer;
            bool centre = s.FrameWidth == PPU_SFC.HiResWidth;   // the published frame is the SNES's own: 512 wide, as the hi-res band asks
            for (int y = 0; centre && y < 224; y++)
                for (int x = 0; x < 256; x++) if (snes[y * 256 + x] != wide[y * 400 + PPU_WMS.SnesOrigin + x]) { centre = false; break; }
            Check(centre, "the negative control is a SNES frame (512 wide for its hi-res band), and its 256-wide picture is exactly the wide picture's columns 72-327");
        }
        PictureVariant(dir, "widesnes.wms", "widesnes.wide_windows.expect.png", p =>
            {
                foreach (var (r, v) in new (byte, byte)[] { (0xB4, 1), (0xB0, 10), (0xB0, 0), (0xB1, 389 & 0xFF), (0xB1, 1), (0xB2, 300 & 0xFF), (0xB2, 1), (0xB3, 399 & 0xFF), (0xB3, 1) }) p.WriteWide(r, v);
            },
            "a SNES mode with WMWCTL's 9-bit windows (10-389 and 300-399)");
        PictureVariant(dir, "widesnes.wms", "widesnes.widemess_table.expect.png", p => p.WriteWide(0xA0, 0x01),
            "a SNES mode with the WideMess sprite table: its priorities in mode 1's and Mode 7's orders");

        Console.WriteLine("P5. Milestone 1's 8-layer picture is unchanged, and the wide renderer's speed");
        using (var s = Session(ConsoleKind.WideMess, File.ReadAllBytes(Path.Combine(dir, "layers.wms"))))
        {
            Run(s, 61);
            ulong h = Hash(s.Frame.AsSpan(0, s.FrameWidth * s.FrameHeight));
            Check(h == 0x1977C01DE6BFDDE7UL, "the 8-layer ROM's frame 61 has milestone 1's hash", $"{h:X16}");
        }
        foreach (var rom in new[] { "layers", "sprites", "windows", "widesnes" }) Speed(dir, rom);
        return true;
    }

    private static SnesSession PictureRun(string dir, string rom, bool compare = false)
    {
        var s = Session(ConsoleKind.WideMess, File.ReadAllBytes(Path.Combine(dir, rom)));
        if (compare) s.WideMessPpu!.CompareWithSnes = true;
        Run(s, 10);
        return s;
    }

    /// <summary>The ROM after its set-up, one register changed on the chip, two more frames: against the model's picture of the same change.</summary>
    private static void PictureVariant(string dir, string rom, string expect, Action<PPU_WMS> poke, string what)
    {
        using var s = PictureRun(dir, rom);
        poke(s.WideMessPpu!);
        Run(s, 2);
        ComparePicture(s, dir, expect, what);
    }

    /// <summary>The session's picture against the model's PNG, pixel for pixel. <paramref name="snesPicture"/>: compare the SNES chip's own 256-wide picture (hi-res pairs averaged,
    /// <see cref="PPU_SFC.FrameBuffer"/>) rather than the published frame, which shows hi-res lines at their sharp 512.</summary>
    private static void ComparePicture(SnesSession s, string dir, string file, string what, bool locked = false, bool snesPicture = false)
    {
        using var bmp = new System.Drawing.Bitmap(Path.Combine(dir, file));
        int w = bmp.Width, h = bmp.Height;
        string state = locked ? (s.WideMess is { Unlocked: false } ? "locked, " : "UNLOCKED, ") : "";
        uint[] frame = snesPicture ? s.WideMessPpu!.FrameBuffer : s.Frame;
        int fw = snesPicture ? PPU_SFC.Width : s.FrameWidth, fh = snesPicture ? s.WideMessPpu!.VisibleHeight : s.FrameHeight;
        if (fw != w || fh != h || (locked && s.WideMess is not { Unlocked: false }))
        {
            Check(false, what, $"{state}the picture is {fw} x {fh}, the model's {w} x {h}");
            return;
        }
        int diff = 0, fx = -1, fy = -1; uint fa = 0, fe = 0;
        var colours = new HashSet<uint>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                uint e = (uint)bmp.GetPixel(x, y).ToArgb(), a = frame[y * w + x];
                colours.Add(a);
                if (a == e) continue;
                if (diff++ == 0) { fx = x; fy = y; fa = a; fe = e; }
            }
        if (diff > 0)
        {
            string actual = Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(file).Replace(".expect", "") + ".actual.png");
            using var out_ = new System.Drawing.Bitmap(w, h);
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) out_.SetPixel(x, y, System.Drawing.Color.FromArgb(unchecked((int)frame[y * w + x])));
            out_.Save(actual, System.Drawing.Imaging.ImageFormat.Png);
            Check(false, what, $"{diff} pixels differ from {file}, the first at ({fx}, {fy}): {fa:X8}, the model {fe:X8}; the picture: {actual}");
            return;
        }
        Check(true, what, $"{state}{w} x {h}, {colours.Count} colours");
    }

    /// <summary>A ROM's speed in Workshop (the whole console: 65816, sound and picture), and the picture chip alone allocating nothing while it draws.</summary>
    private static void Speed(string dir, string rom)
    {
        using var s = Session(ConsoleKind.WideMess, File.ReadAllBytes(Path.Combine(dir, rom + ".wms")));
        Run(s, 30);
        // the CPU is shared (other builds, other agents): the best of two timings of each
        double fps = 0, pictureFps = 0;
        var sw = new Stopwatch();
        for (int k = 0; k < 2; k++) { sw.Restart(); Run(s, 300); fps = Math.Max(fps, 300 / sw.Elapsed.TotalSeconds); }
        var ppu = s.WideMessPpu!;
        for (int line = 1; line <= 224; line++) ppu.RenderLine(line);   // warm
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int k = 0; k < 2; k++)
        {
            sw.Restart();
            for (int f = 0; f < 150; f++) for (int line = 1; line <= 224; line++) ppu.RenderLine(line);
            pictureFps = Math.Max(pictureFps, 150 / sw.Elapsed.TotalSeconds);
        }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(fps >= 90 && pictureFps >= 120 && bytes == 0, $"{rom}.wms runs at {fps:0} frames a second in Workshop (the picture alone {pictureFps:0}), and drawing allocates nothing",
            $"{bytes} bytes allocated over 300 frames of lines; gates: 90 and 120");
    }

    /// <summary>One SNES game drawn 400 wide: every line's centre 256 columns against the SNES chip's own line, with the fast paths (or the reference paths).</summary>
    private static void WideCentre(string path, int frames, bool reference)
    {
        byte[] rom = RomDetect.Unwrap(File.ReadAllBytes(path), path, out _);
        bool fast = PPU_SFC.FastPaths;
        PPU_SFC.FastPaths = !reference;
        try
        {
            using var s = Session(ConsoleKind.WideMess, rom);
            var ppu = s.WideMessPpu!;
            ppu.WriteWide(0xC0, 0x01);      // WMCTRL = WIDE, set on the chip directly: the game never unlocks, so it stays a SNES game drawn wide
            ppu.CompareWithSnes = true;
            long sideDrawn = 0;
            for (int f = 1; f <= frames; f++)
            {
                var pad = (f % 240) is >= 200 and < 206 ? PadButtons.Start : (f % 240) is >= 120 and < 126 ? PadButtons.A : PadButtons.None;
                s.SetPad(0, pad);
                s.RunFrame();
                while (s.ReadSamples(audio) > 0) { }
                if (s.FrameWidth == PPU_WMS.WideWidth)
                    for (int y = 0; y < s.FrameHeight; y++)
                        for (int x = 0; x < PPU_WMS.SnesOrigin; x++)
                            if (s.Frame[y * 400 + x] != 0xFF000000u || s.Frame[y * 400 + 399 - x] != 0xFF000000u) sideDrawn++;
            }
            string paths = reference ? "reference paths" : "fast paths";
            if (!reference)
            {
                var u = ppu.CompareUse;
                var modes = string.Join(" ", Enumerable.Range(0, 8).Where(m => u[m] > 0).Select(m => $"mode {m} {u[m]:N0}"));
                Console.WriteLine($"     lines drawn: {modes}; mosaic {u[8]:N0}, colour math {u[9]:N0}, windows {u[10]:N0}, sprites {u[11]:N0}");
            }
            Check(ppu.CompareRows > 0 && ppu.CompareMismatches == 0 && s.WideMess is { Unlocked: false },
                $"{Path.GetFileName(path)} drawn wide ({paths}): columns 72-327 are the SNES picture, line for line",
                ppu.CompareMismatches == 0
                    ? $"{ppu.CompareRows:N0} lines identical, {ppu.CompareOverflowRows} lines with a sprite overflow skipped, {sideDrawn * 100.0 / ((double)frames * 224 * 144):0}% of the side columns drawn"
                    : $"{ppu.CompareMismatches} of {ppu.CompareRows:N0} lines differ, the first at row {ppu.CompareFirstMismatch}");
        }
        finally { PPU_SFC.FastPaths = fast; }
    }

    /// <summary>The M2 part of the SNES-games section of --wms-test: each game drawn wide, fast paths over all the frames, the reference paths over the first 300.</summary>
    private static void WideSnesGames(string snes, int frames)
    {
        if (snes == "") return;
        Console.WriteLine($"P4. SNES games drawn 400 wide (WIDE forced on, the extension locked): the centre is the SNES picture ({frames} frames; reference paths 300)");
        foreach (var path in snes.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!File.Exists(path)) { Check(false, Path.GetFileName(path), "not found"); continue; }
            WideCentre(path, frames, reference: false);
            WideCentre(path, Math.Min(frames, 300), reference: true);
        }
    }
}
