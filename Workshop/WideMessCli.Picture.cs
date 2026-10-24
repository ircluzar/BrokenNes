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
