using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes.Workshop;

/// <summary>
/// The WideMess console's tools (docs/projects/widemess-spec.md):
/// <list type="bullet">
/// <item><c>--wms-pack --in game.sfc --out game.wms [--title T] [--features 1] [--map 0|1|2] [--sram BYTES] [--battery]</c>: a SNES-format image (or, with map 2, a WideMess-map ROM with
/// its battery RAM size) as a .wms game file (the 64-byte header, CRC-32).</item>
/// <item><c>--wms-test [--roms DIR] [--snes F1;F2;...] [--frames N] [--reference-paths]</c>: milestone 1's checks, each with its negative control. The test ROMs (WideMess/testroms/bin): the 8-layer ROM draws
/// 400 x 224 with eight layers of 256 colours on a WideMess, a red screen on a SNES, and its NOUNLOCK twin (WideMess registers written without the unlock) stays a SNES picture.
/// The .wms loader refuses damaged and too-new files. A save state round-trips. Then every SNES game given runs frame for frame identically (picture and sound) on the SNES console
/// and on the WideMess console. Exit 0 when every check passes, 1 when one fails, 2 on usage or a missing file.</item>
/// </list>
/// </summary>
internal static partial class WideMessCli
{
    private static string Opt(string[] args, string n, string d)
    {
        int i = Array.FindIndex(args, a => a.Equals("--" + n, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : d;
    }

    public static int Pack(string[] args)
    {
        RomTestCli.EnsureConsole();
        string input = Opt(args, "in", ""), output = Opt(args, "out", "");
        if (!File.Exists(input) || output == "") { Console.Error.WriteLine("usage: --wms-pack --in game.sfc --out game.wms [--title T] [--features 1] [--map 0|1|2] [--sram BYTES] [--battery]"); return 2; }
        byte[] image = File.ReadAllBytes(input);
        if (image.Length % 1024 == 512) image = image[512..];   // a copier header is not part of the image
        byte map = byte.Parse(Opt(args, "map", "0"));
        uint features = uint.Parse(Opt(args, "features", "1"));
        string title = Opt(args, "title", Path.GetFileNameWithoutExtension(input));
        int sram = int.Parse(Opt(args, "sram", "0"));   // the WideMess map's battery RAM (maps 0 and 1 take theirs from the SNES header)
        bool battery = args.Any(a => a.Equals("--battery", StringComparison.OrdinalIgnoreCase));
        var file = WmsFile.Pack(image, title, map, features, sram, battery);
        WmsFile.Cartridge(image, WmsFile.Read(file));   // the file must be one BrokenNes opens, its map included
        File.WriteAllBytes(output, file);
        Console.WriteLine($"{output}: {file.Length:N0} bytes, '{title}', map {map}, features {features:X2}{(sram > 0 ? $", battery RAM {sram:N0}{(battery ? " (battery)" : "")}" : "")}, CRC {WmsFile.Crc32(image):X8}");
        return 0;
    }

    /// <summary><c>--wms-shot --rom game.wms --out shot.png [--frames 60] [--scale 3] [--console wms|snes]</c>: the picture after N frames, as a PNG at its own size times the scale.</summary>
    public static int Shot(string[] args)
    {
        RomTestCli.EnsureConsole();
        Consoles.WideMessEnabled = true;
        string rom = Opt(args, "rom", ""), output = Opt(args, "out", "");
        if (!File.Exists(rom) || output == "") { Console.Error.WriteLine("usage: --wms-shot --rom game.wms --out shot.png [--frames 60] [--scale 3] [--console wms|snes]"); return 2; }
        int frames = int.Parse(Opt(args, "frames", "60")), scale = int.Parse(Opt(args, "scale", "3"));
        using var s = Session(Consoles.FromKey(Opt(args, "console", "wms")), RomDetect.Unwrap(File.ReadAllBytes(rom), rom, out _));
        Run(s, frames);
        int w = s.FrameWidth, h = s.FrameHeight;
        using var bmp = new System.Drawing.Bitmap(w * scale, h * scale, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        for (int y = 0; y < h * scale; y++)
            for (int x = 0; x < w * scale; x++)
                bmp.SetPixel(x, y, System.Drawing.Color.FromArgb(unchecked((int)s.Frame[(y / scale) * w + x / scale])));
        bmp.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"{output}: {w} x {h} at x{scale}, frame {frames}, {s.Description}");
        return 0;
    }

    /// <summary><c>--wms-sheet --rom game.wms --out sheet.png [--frames 120]</c>: the 400-wide picture on top, then each of the 8 layers drawn alone (the others switched off), labelled, in a 2 x 4 grid.</summary>
    public static int Sheet(string[] args)
    {
        RomTestCli.EnsureConsole();
        Consoles.WideMessEnabled = true;
        string rom = Opt(args, "rom", ""), output = Opt(args, "out", "");
        if (!File.Exists(rom) || output == "") { Console.Error.WriteLine("usage: --wms-sheet --rom game.wms --out sheet.png [--frames 120]"); return 2; }
        using var s = Session(ConsoleKind.WideMess, File.ReadAllBytes(rom));
        Run(s, int.Parse(Opt(args, "frames", "120")));
        var ppu = s.WideMessPpu!;
        if (s.FrameWidth != PPU_WMS.WideWidth) { Console.Error.WriteLine("the game did not draw 400 wide"); return 1; }
        const int W = PPU_WMS.WideWidth, H = 224, gap = 16, label = 18;
        using var sheet = new System.Drawing.Bitmap(2 * W + gap, (H + label) * 5 + gap * 4);
        using var g = System.Drawing.Graphics.FromImage(sheet);
        using var font = new System.Drawing.Font("Segoe UI", 10f);
        g.Clear(System.Drawing.Color.FromArgb(24, 24, 32));
        void Put(uint[] px, int x, int y, string text)
        {
            using var bmp = new System.Drawing.Bitmap(W, H);
            for (int yy = 0; yy < H; yy++) for (int xx = 0; xx < W; xx++) bmp.SetPixel(xx, yy, System.Drawing.Color.FromArgb(unchecked((int)px[yy * W + xx])));
            g.DrawString(text, font, System.Drawing.Brushes.White, x, y);
            g.DrawImageUnscaled(bmp, x, y + label);
        }
        Put(Snapshot(s), (2 * W + gap - W) / 2, 0, "All 8 layers, 400 x 224");
        for (int layer = 0; layer < PPU_WMS.Layers; layer++)
        {
            ppu.WriteWide(0xC1, (byte)(1 << layer));
            for (int line = 1; line <= H; line++) ppu.RenderLine(line);
            var px = ppu.WideBuffer.AsSpan(0, W * H).ToArray();
            int colours = px.Distinct().Count() - 1;
            Put(px, (layer & 1) * (W + gap), (H + label + gap) * (1 + layer / 2), $"BG{layer + 1} alone: colour bank {layer & 3}, {colours} colours, scroll {(layer + 1) / 2.0:0.#} px/frame");
        }
        ppu.WriteWide(0xC1, 0xFF);
        sheet.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"{output}: {sheet.Width} x {sheet.Height}");
        return 0;
    }

    private static int fails, passes;
    private static void Check(bool ok, string what, string detail = "")
    {
        if (ok) passes++; else fails++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}{(detail != "" ? "  (" + detail + ")" : "")}");
    }

    public static int Test(string[] args)
    {
        RomTestCli.EnsureConsole();
        Consoles.WideMessEnabled = true;
        string dir = Opt(args, "roms", Path.Combine(AppContext.BaseDirectory, "WideMess", "testroms", "bin"));
        string layers = Path.Combine(dir, "layers.wms"), nounlock = Path.Combine(dir, "layers_nounlock.wms"), snesImage = Path.Combine(dir, "layers.sfc");
        foreach (var f in new[] { layers, nounlock, snesImage }) if (!File.Exists(f)) { Console.Error.WriteLine($"missing test ROM {f} (build WideMess/testroms/layers/build.ps1)"); return 2; }
        fails = passes = 0;
        if (args.Any(a => a.Equals("--reference-paths", StringComparison.OrdinalIgnoreCase)))
        {
            PPU_SFC.FastPaths = DSP_SFC.FastPaths = BOARD_SFC.FastPaths = false;   // every SNES fast path off: the checks run on the reference twins
            Console.WriteLine("(reference paths: the SNES chips' fast paths are off)");
        }

        Console.WriteLine("1. The 8-layer ROM on a WideMess");
        using (var s = Session(ConsoleKind.WideMess, File.ReadAllBytes(layers)))
        {
            Run(s, 60);
            uint[] a = Snapshot(s);
            Check(s.WideMess is { Unlocked: true }, "the game unlocked the extensions and read the ID");
            Check(s.FrameWidth == 400 && s.FrameHeight == 224, "the picture is 400 x 224", $"{s.FrameWidth} x {s.FrameHeight}");
            Check(s.DisplayWidth == 400, "it is shown at 400 (square pixels, 16:9)", $"display width {s.DisplayWidth}");
            int colours = a.Distinct().Count();
            Check(colours >= 800, "it shows the four banks of colours (more than 800 distinct, of 1,024)", $"{colours} colours");
            Check(s.WideMessPpu!.WideOutput && s.WideMessPpu.LayersMode, "WMCTRL: wide output and the WideMess mode");
            int[] perLayer = LayerColours(s);
            Check(perLayer.All(c => c >= 250), "each of the 8 layers alone draws (nearly) all 255 colours of its bank", string.Join(" ", perLayer));
            Run(s, 1);
            uint[] b = Snapshot(s);
            Check(!a.SequenceEqual(b), "the layers scroll (the next frame differs)");
            Check(Edges(b), "the picture reaches both edges (columns 0 and 399 are drawn, not black)");
            Console.WriteLine($"     picture hash at frame 61: {Hash(b):X16}");
        }

        Console.WriteLine("2. Negative control: the same ROM writing the registers WITHOUT the unlock");
        using (var s = Session(ConsoleKind.WideMess, File.ReadAllBytes(nounlock)))
        {
            Run(s, 60);
            uint[] a = Snapshot(s);
            Check(s.WideMess is { Unlocked: false }, "the extensions stay locked");
            Check(s.FrameWidth == 256, "the picture stays the SNES's 256 wide", $"{s.FrameWidth}");
            Check(a.All(p => p == 0xFF000000u), "nothing the game wrote reached the screen (all black, as a SNES draws it)");
        }

        Console.WriteLine("3. Negative control: the ROM's SNES image on a SNES");
        using (var s = Session(ConsoleKind.Snes, File.ReadAllBytes(snesImage)))
        {
            Run(s, 60);
            uint[] a = Snapshot(s);
            Check(s.FrameWidth == 256 && s.WideMess == null, "a SNES has no WideMess", $"{s.FrameWidth}");
            Check(a.All(p => p == 0xFFFF0000u), "the game saw no WideMess ID and drew its red 'not a WideMess' screen");
        }

        Console.WriteLine("4. The SNES image (no .wms header) on a WideMess: a SNES game that knows WideMess finds it");
        using (var s = Session(ConsoleKind.WideMess, File.ReadAllBytes(snesImage)))
        {
            Run(s, 60);
            Check(s.WideMess is { Unlocked: true } && s.FrameWidth == 400, "it unlocks and draws 400 wide", $"{s.FrameWidth}");
            Check(s.GameId.StartsWith("sfc"), "it keeps its SNES id (its saves are the SNES console's)", s.GameId[..7]);
        }

        Console.WriteLine("5. The .wms loader refuses what it cannot run, and says why");
        byte[] good = File.ReadAllBytes(layers);
        Refused(Damage(good, 0x1000), "a damaged file (CRC)", "damaged");
        Refused(Patch(good, 0x14, 0x80), "a game needing a feature this console lacks (debug port)", "debug port");
        Refused(Patch(good, 0x08, 2), "a game needing spec 2.0", "spec 2.0");
        Refused(Patch(good, 0x0A, 3), "a game on an unknown map", "unknown WideMess map");
        Refused(good[..0x800], "a cut file", "says");

        Console.WriteLine("6. Save states carry the WideMess part");
        using (var s = Session(ConsoleKind.WideMess, good))
        {
            Run(s, 100);
            byte[] state = s.SaveState();
            Run(s, 30); uint[] a = Snapshot(s);
            s.LoadState(state);
            Run(s, 30); uint[] b = Snapshot(s);
            Check(a.SequenceEqual(b) && s.FrameWidth == 400, "save at frame 100, run 30, load, run 30: the same 400-wide picture", $"{state.Length / 1024} KB");
        }

        if (!TestCpuMemory(dir)) return 2;   // milestone 4: fast CPU, 1 MB work RAM, the WideMess map, fast DMA (WideMessCli.Memory.cs)

        string snes = Opt(args, "snes", "");
        int frames = int.Parse(Opt(args, "frames", "900"));
        if (snes != "")
        {
            Console.WriteLine($"7. SNES games: the SNES console and the WideMess console, frame for frame ({frames} frames, Start pressed now and then)");
            foreach (var path in snes.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!File.Exists(path)) { Check(false, Path.GetFileName(path), "not found"); continue; }
                Identity(path, frames);
            }
        }
        Console.WriteLine($"{passes} passed, {fails} failed");
        return fails == 0 ? 0 : 1;
    }

    private static SnesSession Session(ConsoleKind console, byte[] rom) =>
        (SnesSession)ConsoleSessions.Create(console, rom, CoreCatalog.Default(console, CoreSlot.Cpu), CoreCatalog.Default(console, CoreSlot.Ppu), CoreCatalog.Default(console, CoreSlot.Apu));

    private static readonly short[] audio = new short[65536];
    private static void Run(IConsoleSession s, int frames) { for (int i = 0; i < frames; i++) { s.RunFrame(); while (s.ReadSamples(audio) > 0) { } } }
    private static uint[] Snapshot(IConsoleSession s) => s.Frame.AsSpan(0, s.FrameWidth * s.FrameHeight).ToArray();

    private static ulong Hash(ReadOnlySpan<uint> px) { ulong h = 14695981039346656037UL; foreach (var p in px) h = (h ^ p) * 1099511628211UL; return h; }

    private static bool Edges(uint[] px) { int black = 0; for (int y = 0; y < 224; y++) { if (px[y * 400] == 0xFF000000u) black++; if (px[y * 400 + 399] == 0xFF000000u) black++; } return black < 20; }

    /// <summary>Each layer drawn alone (the others switched off for one line pass): how many distinct colours it shows.</summary>
    private static int[] LayerColours(SnesSession s)
    {
        var ppu = s.WideMessPpu!;
        var counts = new int[PPU_WMS.Layers];
        for (int layer = 0; layer < PPU_WMS.Layers; layer++)
        {
            var seen = new HashSet<uint>();
            // draw the frame again with one layer on, line by line, through the chip's own registers
            ppu.WriteWide(0xC1, (byte)(1 << layer));
            for (int line = 1; line <= 224; line++) ppu.RenderLine(line);
            foreach (var p in ppu.WideBuffer.AsSpan(0, 400 * 224)) seen.Add(p);
            counts[layer] = seen.Count - 1;   // minus the backdrop
        }
        ppu.WriteWide(0xC1, 0xFF);
        return counts;
    }

    private static byte[] Damage(byte[] f, int at) { var c = (byte[])f.Clone(); c[WmsFile.HeaderSize + at] ^= 0xFF; return c; }
    private static byte[] Patch(byte[] f, int at, byte v)
    {
        var c = (byte[])f.Clone(); c[at] = v;
        return c;
    }

    private static void Refused(byte[] file, string what, string mustSay)
    {
        try
        {
            using var s = Session(ConsoleKind.WideMess, file);
            Check(false, $"refuses {what}", "it ran");
        }
        catch (InvalidDataException e) { Check(e.Message.Contains(mustSay, StringComparison.OrdinalIgnoreCase), $"refuses {what}", e.Message); }
        catch (Exception e) { Check(false, $"refuses {what}", e.GetType().Name + ": " + e.Message); }
    }

    /// <summary>One SNES game on both consoles with the same pad: every frame's picture and the whole sound must be identical.</summary>
    private static void Identity(string path, int frames)
    {
        byte[] rom = RomDetect.Unwrap(File.ReadAllBytes(path), path, out _);
        using var a = Session(ConsoleKind.Snes, rom);
        using var b = Session(ConsoleKind.WideMess, rom);
        ulong ah = 14695981039346656037UL, bh = ah;
        var bufA = new short[65536]; var bufB = new short[65536];
        int firstDiff = -1;
        for (int f = 1; f <= frames; f++)
        {
            var pad = (f % 240) is >= 200 and < 206 ? PadButtons.Start : (f % 240) is >= 120 and < 126 ? PadButtons.A : PadButtons.None;
            a.SetPad(0, pad); b.SetPad(0, pad);
            a.RunFrame(); b.RunFrame();
            int n;
            while ((n = a.ReadSamples(bufA)) > 0) for (int i = 0; i < n; i++) ah = (ah ^ (ushort)bufA[i]) * 1099511628211UL;
            while ((n = b.ReadSamples(bufB)) > 0) for (int i = 0; i < n; i++) bh = (bh ^ (ushort)bufB[i]) * 1099511628211UL;
            if (firstDiff < 0 && (a.FrameWidth != b.FrameWidth || a.FrameHeight != b.FrameHeight || Hash(a.Frame.AsSpan(0, a.FrameWidth * a.FrameHeight)) != Hash(b.Frame.AsSpan(0, b.FrameWidth * b.FrameHeight)))) firstDiff = f;
        }
        Check(firstDiff < 0 && ah == bh, $"{Path.GetFileName(path)} ({a.Title})", firstDiff >= 0 ? $"pictures differ from frame {firstDiff}" : ah != bh ? "the sound differs" : $"{frames} frames identical, sound {ah:X16}, WideMess locked: {b.WideMess is { Unlocked: false }}");
    }
}
