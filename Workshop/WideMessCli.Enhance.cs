using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes.Workshop;

/// <summary>
/// Milestone 6, SNES games enhanced (docs/projects/widemess-spec.md section 6): the tools and the checks.
/// <list type="bullet">
/// <item><c>--wms-enhance-sheet (--game KEY | --rom game.sfc [--input S]) --out sheet.png [--at F1,F2,...] [--scratch-profile p.json] [--no-flicker] [--profiles DIR]</c>: the contact sheet, the SNES
/// picture beside WideMess widescreen (the game's profile) at several moments of play, from one run (widescreen leaves the game as it is, so the SNES chip's own line is drawn beside it).</item>
/// <item><c>--wms-enhance-film (--game KEY | --rom game.sfc [--input S]) --out film.png [--every 300] [--frames 6000] [--scratch-profile p.json]</c>: the wide picture every N frames, for writing a
/// profile or an input script.</item>
/// <item><c>--wms-test</c>'s E checks (<see cref="EnhanceTests"/>): every enhancement against a negative control, the proven games, the profiles folder.</item>
/// </list>
/// </summary>
internal static partial class WideMessCli
{
    /// <summary>A proven game: its file in the SNES set, the pad script that plays it (frame:Buttons, held until the next step) and the moments of its contact sheet.</summary>
    internal sealed record EnhanceGame(string Key, string Title, string File, string Script, int[] Moments);

    internal const string DefaultSnesDir = @"X:\EMULATION\Super Nintendo\Super Nintendo (U)";

    /// <summary>Taps: a button for 6 frames every <paramref name="period"/> frames from <paramref name="from"/> to <paramref name="to"/>, cycling through <paramref name="buttons"/>.</summary>
    private static string Taps(int from, int to, int period, params string[] buttons)
    {
        var steps = new List<string>(); int n = 0;
        for (int f = from; f < to; f += period) { steps.Add($"{f}:{buttons[n++ % buttons.Length]}"); steps.Add($"{f + 6}:"); }
        return string.Join(",", steps);
    }

    /// <summary>Holds <paramref name="held"/> from <paramref name="from"/> to <paramref name="to"/>, adding each of <paramref name="extra"/> in turn for 8 frames every <paramref name="period"/> frames.</summary>
    private static string Hold(int from, int to, int period, string held, params string[] extra)
    {
        var steps = new List<string> { $"{from}:{held}" }; int n = 0;
        for (int f = from + period; f < to; f += period) { steps.Add($"{f}:{held}+{extra[n++ % extra.Length]}"); steps.Add($"{f + 8}:{held}"); }
        steps.Add($"{to}:");
        return string.Join(",", steps);
    }

    /// <summary>The games milestone 6 proves (spec 6.4): each one's pad script reaches play, or leaves the game to its own attract demos (which play the game), and its contact sheet's moments.</summary>
    internal static readonly EnhanceGame[] EnhanceRoster =
    {
        new("smw", "Super Mario World", "Super Mario World (U) [!].smc",
            "450:Start,460:," + Taps(560, 1700, 50, "A") + ",1720:Right,1736:,1800:A,1810:," + Hold(2000, 6400, 35, "Right+Y", "B"), new[] { 400, 1200, 2000, 3200, 4000 }),
        new("yoshi", "Super Mario World 2: Yoshi's Island", "Super Mario World 2 - Yoshi's Island (U) (M3) (V1.1).smc",
            Taps(600, 3000, 60, "Start", "A") + "," + Hold(3000, 6400, 50, "Right", "B", "A", "B"), new[] { 800, 1600, 4000, 4800, 5200 }),
        new("dkc", "Donkey Kong Country", "Donkey Kong Country (U) (V1.2) [!].smc",
            Taps(200, 1900, 50, "Start", "A", "B", "Right", "A", "Down", "Right+B") + "," + Hold(2500, 6000, 70, "Right+Y", "B"), new[] { 800, 2000, 2800, 3600, 4400 }),
        new("sm", "Super Metroid", "Super Metroid (JU) [!].smc", "", new[] { 2250, 3150, 4050, 5400, 6300 }),
        new("alttp", "The Legend of Zelda: A Link to the Past", "Legend of Zelda, The - A Link to the Past (U) [!].smc",
            "1300:Start,1308:," + Taps(1500, 2400, 60, "Start", "A", "Down", "A") + "," + Taps(2400, 4000, 50, "A") + ",4000:Down,4040:Right,4060:Down,4300:Down+Right,4400:Down,4700:Right,5000:Down,5400:", new[] { 1200, 2000, 3600, 4800, 5600 }),
        new("fzero", "F-Zero", "F-ZERO (U) [!].smc", "", new[] { 450, 1800, 2700, 5400, 6750 }),
        new("smk", "Super Mario Kart", "Super Mario Kart (U) [!].smc", "", new[] { 450, 2700, 3600, 5850, 6750 }),
        new("ct", "Chrono Trigger", "Chrono Trigger (U) [!].smc", "", new[] { 1800, 2700, 4050, 5850, 6750 }),
        new("mmx", "Mega Man X", "Mega Man X (U) (V1.1) [!].smc",
            Taps(400, 1100, 90, "Start") + "," + Hold(1300, 6400, 30, "Right", "Y", "B", "Y"), new[] { 800, 1200, 2000, 3600, 5600 }),
        new("contra3", "Contra III: The Alien Wars", "Contra III - The Alien Wars (U) [!].smc", "", new[] { 900, 3600, 4500, 5400, 6300 }),
        new("scv4", "Super Castlevania IV", "Super Castlevania IV (U) [!].smc",
            Taps(1900, 2500, 90, "Start") + "," + Hold(2700, 6400, 40, "Right", "Y", "B", "Y"), new[] { 2000, 2800, 3600, 4400, 5200 }),
    };

    /// <summary>A pad script: "frame:Button+Button,frame:,..." (each step held until the next).</summary>
    internal static SortedDictionary<int, PadButtons> ParsePad(string script)
    {
        var d = new SortedDictionary<int, PadButtons>();
        foreach (var part in script.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = part.IndexOf(':');
            if (colon < 0) throw new FormatException($"'{part}' needs frame:buttons");
            PadButtons b = 0;
            foreach (var name in part[(colon + 1)..].Split('+', StringSplitOptions.RemoveEmptyEntries)) b |= Enum.Parse<PadButtons>(name.Trim(), true);
            d[int.Parse(part[..colon])] = b;
        }
        return d;
    }

    /// <summary>Runs a session to <paramref name="frame"/> following the script (the pad as of each frame).</summary>
    private static void PlayTo(SnesSession s, SortedDictionary<int, PadButtons> pad, ref int at, int frame, ref PadButtons held)
    {
        while (at < frame)
        {
            at++;
            if (pad.TryGetValue(at, out var b)) held = b;
            s.SetPad(0, held);
            s.RunFrame();
            while (s.ReadSamples(audio) > 0) { }
        }
    }

    private static (string rom, string script, int[] moments, string name)? GameArgs(string[] args)
    {
        string key = Opt(args, "game", "");
        string dir = Opt(args, "snes-dir", DefaultSnesDir);
        if (key != "")
        {
            var g = EnhanceRoster.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (g == null) { Console.Error.WriteLine($"unknown --game '{key}' ({string.Join(", ", EnhanceRoster.Select(x => x.Key))})"); return null; }
            string at = Opt(args, "at", "");
            return (Path.Combine(dir, g.File), Opt(args, "input", g.Script), at != "" ? at.Split(',').Select(int.Parse).ToArray() : g.Moments, g.Title);
        }
        string rom = Opt(args, "rom", "");
        if (!File.Exists(rom)) { Console.Error.WriteLine("give --game KEY or --rom game.sfc"); return null; }
        return (rom, Opt(args, "input", ""), Opt(args, "at", "600,1200,1800").Split(',').Select(int.Parse).ToArray(), Path.GetFileNameWithoutExtension(rom));
    }

    /// <summary>The WideMess session of a SNES game with its profile's widescreen on (or a scratch profile's), comparing every line with the SNES chip's own.</summary>
    private static SnesSession? EnhancedSession(string rom, string[] args, out string profileNote)
    {
        Consoles.WideMessEnabled = true;
        string profiles = Opt(args, "profiles", "");
        if (profiles != "") WmsProfiles.Directory = profiles;
        var s = Session(ConsoleKind.WideMess, RomDetect.Unwrap(File.ReadAllBytes(rom), rom, out _));
        string scratch = Opt(args, "scratch-profile", "");
        if (scratch != "") s.SetScratchProfile(WmsProfile.Parse(File.ReadAllText(scratch), Path.GetFileName(scratch)));
        if (args.Contains("--all-extend", StringComparer.OrdinalIgnoreCase)) s.SetScratchProfile(AllExtendProfile(s.ImageSha1));
        profileNote = s.EnhanceProfile.Describe();
        string? why = s.SetEnhancements(widescreen: true, noFlicker: args.Contains("--no-flicker", StringComparer.OrdinalIgnoreCase), fastCpu: args.Contains("--fast-cpu", StringComparer.OrdinalIgnoreCase));
        if (why != null) { Console.Error.WriteLine($"{Path.GetFileName(rom)}: {why} (SHA-1 {s.ImageSha1})"); s.Dispose(); return null; }
        s.WideMessPpu!.CompareEnhanced = true;
        s.WideMessPpu.DebugLineRegisters = new PPU_SFC.RegisterSnapshot[PPU_SFC.MaxHeight + 2];
        return s;
    }

    /// <summary>A scratch profile with every layer and the sprites extended and no masks: not a game's profile, the raw view of what the game keeps in video RAM (for writing profiles, and to show
    /// why a game's widescreen is unavailable).</summary>
    internal static WmsProfile AllExtendProfile(string sha1) => new()
    {
        Source = "--all-extend", Title = "every layer extended (no profile rules)", Sha1 = new[] { sha1 }, Widescreen = WmsWidescreen.AllExtend(),
        Verdicts = new[] { WmsVerdict.Risky, WmsVerdict.Risky, WmsVerdict.Risky, WmsVerdict.Risky }, VerifiedHow = "scratch",
    };

    /// <summary><c>--wms-enhance-sheet</c>: the SNES picture beside WideMess widescreen at each moment.</summary>
    public static int EnhanceSheet(string[] args)
    {
        RomTestCli.EnsureConsole();
        var g = GameArgs(args);
        string output = Opt(args, "out", "");
        if (g == null || output == "") { Console.Error.WriteLine("usage: --wms-enhance-sheet (--game KEY | --rom game.sfc [--input S]) --out sheet.png [--at F1,F2] [--scratch-profile p.json] [--no-flicker]"); return 2; }
        var (rom, script, moments, name) = g.Value;
        using var s = EnhancedSession(rom, args, out string note);
        if (s == null) return 1;
        var pad = ParsePad(script);
        const int W = PPU_WMS.WideWidth, SW = PPU_SFC.Width, H = 224, gap = 12, label = 18, head = 40;
        int rows = moments.Length;
        using var sheet = new Bitmap(SW + gap + W + 2 * gap, head + rows * (H + label + gap) + gap);
        using var gr = Graphics.FromImage(sheet);
        using var font = new Font("Segoe UI", 9f);
        using var bold = new Font("Segoe UI", 10f, FontStyle.Bold);
        gr.Clear(Color.FromArgb(24, 24, 32));
        gr.DrawString($"{name}: the SNES (256 x 224) and WideMess widescreen (400 x 224, {s.EnhanceProfile.Profile?.Source ?? "scratch"})", bold, Brushes.White, gap, 6);
        var real = WmsProfiles.Find(s.ImageSha1).Profile;
        string verdict = real == null ? "no profile in WideMess/profiles"
            : real.Verdict(WmsEnhancement.Widescreen) == WmsVerdict.Unavailable ? $"{real.Source}: widescreen UNAVAILABLE for this game - this sheet shows every layer extended, which is why"
            : $"{real.Source}: widescreen {WmsProfile.VerdictText(real.Verdict(WmsEnhancement.Widescreen))}";
        gr.DrawString($"{verdict}{(s.Enhancements.NoFlicker ? "; no flicker on" : "")}", font, Brushes.Gainsboro, gap, 22);
        int at = 0; PadButtons held = 0;
        var ppu = s.WideMessPpu!;
        for (int i = 0; i < rows; i++)
        {
            ppu.ResetEnhancedCompare();
            PlayTo(s, pad, ref at, moments[i] - 1, ref held);
            ppu.ResetEnhancedCompare();
            PlayTo(s, pad, ref at, moments[i], ref held);   // the last frame: its lines compared
            int y = head + i * (H + label + gap);
            int h = Math.Min(H, ppu.VisibleHeight);
            using (var snes = new Bitmap(SW, H)) using (var wide = new Bitmap(W, H))
            {
                for (int yy = 0; yy < h; yy++)
                {
                    for (int xx = 0; xx < SW; xx++) snes.SetPixel(xx, yy, Color.FromArgb(unchecked((int)ppu.FrameBuffer[yy * SW + xx])));
                    for (int xx = 0; xx < W; xx++) wide.SetPixel(xx, yy, Color.FromArgb(unchecked((int)(s.FrameWidth == W ? s.Frame[yy * W + xx] : 0xFF000000u))));
                }
                gr.DrawString($"frame {moments[i]}: SNES", font, Brushes.White, gap, y);
                gr.DrawString($"WideMess widescreen{(ppu.EnhCompareDiffer == 0 ? ", centre identical" : $", {ppu.EnhCompareDiffer} lines differ in the centre")}", font, Brushes.White, SW + 2 * gap, y);
                gr.DrawImageUnscaled(snes, gap, y + label);
                gr.DrawImageUnscaled(wide, SW + 2 * gap, y + label);
                using var pen = new Pen(Color.FromArgb(90, 255, 255, 255));
                gr.DrawLine(pen, SW + 2 * gap + PPU_WMS.SnesOrigin, y + label + H + 2, SW + 2 * gap + PPU_WMS.SnesOrigin + SW - 1, y + label + H + 2);
            }
            Console.WriteLine($"  frame {moments[i]}: {s.FrameWidth} x {s.FrameHeight}, centre {(ppu.EnhCompareDiffer == 0 ? "identical" : $"{ppu.EnhCompareDiffer} lines differ")}; {Probe(ppu)}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        sheet.Save(output, ImageFormat.Png);
        Console.WriteLine($"{output}: {sheet.Width} x {sheet.Height}, {name}, {note}, SHA-1 {s.ImageSha1}");
        return 0;
    }

    /// <summary><c>--wms-enhance-film</c>: the wide picture every N frames in a grid, labelled.</summary>
    public static int EnhanceFilm(string[] args)
    {
        RomTestCli.EnsureConsole();
        var g = GameArgs(args);
        string output = Opt(args, "out", "");
        if (g == null || output == "") { Console.Error.WriteLine("usage: --wms-enhance-film (--game KEY | --rom game.sfc [--input S]) --out film.png [--every 300] [--frames 6000] [--scratch-profile p.json]"); return 2; }
        var (rom, script, _, name) = g.Value;
        int every = int.Parse(Opt(args, "every", "300")), frames = int.Parse(Opt(args, "frames", "6000"));
        using var s = EnhancedSession(rom, args, out string note);
        if (s == null) return 1;
        var pad = ParsePad(script);
        Console.WriteLine($"  script: {pad.Count} steps, first {(pad.Count > 0 ? pad.First().Key + ":" + pad.First().Value : "-")}");
        const int W = PPU_WMS.WideWidth, H = 224, label = 16, cols = 4;
        int shots = frames / every, rowsN = (shots + cols - 1) / cols;
        using var sheet = new Bitmap(cols * (W + 4), rowsN * (H + label + 4));
        using var gr = Graphics.FromImage(sheet);
        using var font = new Font("Segoe UI", 8f);
        gr.Clear(Color.FromArgb(24, 24, 32));
        int at = 0; PadButtons held = 0;
        for (int i = 0; i < shots; i++)
        {
            PlayTo(s, pad, ref at, (i + 1) * every, ref held);
            int x = (i % cols) * (W + 4), y = (i / cols) * (H + label + 4);
            using var bmp = new Bitmap(W, H);
            for (int yy = 0; yy < Math.Min(H, s.FrameHeight); yy++)
                for (int xx = 0; xx < W; xx++) bmp.SetPixel(xx, yy, Color.FromArgb(unchecked((int)(s.FrameWidth == W ? s.Frame[yy * W + xx] : 0xFF000000u))));
            gr.DrawString($"{(i + 1) * every}", font, Brushes.White, x, y);
            Console.WriteLine($"  {(i + 1) * every}: {Probe(s.WideMessPpu!)}");
            gr.DrawImageUnscaled(bmp, x, y + label);
        }
        sheet.Save(output, ImageFormat.Png);
        Console.WriteLine($"{output}: {shots} pictures, {name}, {note}, SHA-1 {s.ImageSha1}");
        return 0;
    }

    /// <summary><c>--wms-fast-probe --rom game.sfc [--frames 300] [--from 0] [--every 10]</c>: fast CPU on from frame <c>--from</c>; every N frames the 65816's PC and INIDISP beside the SNES
    /// console's, and the picture's colours.</summary>
    public static int FastProbe(string[] args)
    {
        RomTestCli.EnsureConsole();
        Consoles.WideMessEnabled = true;
        string rom = Opt(args, "rom", "");
        int frames = int.Parse(Opt(args, "frames", "300")), from = int.Parse(Opt(args, "from", "0")), every = int.Parse(Opt(args, "every", "10"));
        using var s = Session(ConsoleKind.WideMess, RomDetect.Unwrap(File.ReadAllBytes(rom), rom, out _));
        using var snes = Session(ConsoleKind.Snes, RomDetect.Unwrap(File.ReadAllBytes(rom), rom, out _));
        for (int f = 1; f <= frames; f++)
        {
            if (f == from + 1) Console.WriteLine($"  fast CPU: {s.SetEnhancements(fastCpu: true) ?? "on"}");
            s.RunFrame(); snes.RunFrame();
            while (s.ReadSamples(audio) > 0) { }
            while (snes.ReadSamples(audio) > 0) { }
            if (f % every != 0) continue;
            var c = s.Board.Cpu; var r = s.Ppu.GetRegisterSnapshot();
            var c2 = snes.Board.Cpu; var r2 = snes.Ppu.GetRegisterSnapshot();
            int colours = s.Frame.AsSpan(0, s.FrameWidth * s.FrameHeight).ToArray().Distinct().Count();
            Console.WriteLine($"  {f}: fast PC {c.PC:X4} INIDISP {r.Inidisp:X2} colours {colours} | SNES PC {c2.PC:X4} INIDISP {r2.Inidisp:X2} | board fast {s.Board.FastCpu}");
        }
        return 0;
    }

    /// <summary>The picture's set-up in bands of rows (for writing profiles): BGMODE, TM / TS and each layer's map width (w = 64 tiles, n = 32) per run of lines with the same set-up,
    /// from the lines the SNES chip last drew (<see cref="PPU_SFC.DebugLineRegisters"/>).</summary>
    private static string Probe(PPU_WMS ppu)
    {
        var lines = ppu.DebugLineRegisters;
        if (lines == null) return "";
        var bands = new List<string>();
        string? last = null; int start = 0;
        for (int line = 1; line <= ppu.VisibleHeight + 1; line++)
        {
            string? k = null;
            if (line <= ppu.VisibleHeight)
            {
                var r = lines[line];
                k = (r.Inidisp & 0x80) != 0 ? "blank" : $"BGMODE {r.Bgmode:X2} TM {r.Tm:X2} TS {r.Ts:X2} SC {r.Bg1sc:X2} {r.Bg2sc:X2} {r.Bg3sc:X2} {r.Bg4sc:X2} NBA {r.Bg12nba:X2} {r.Bg34nba:X2}";
            }
            if (k == last) continue;
            if (last != null) bands.Add($"{start - 1}-{line - 2}: {last}");
            last = k; start = line;
        }
        return string.Join(" | ", bands);
    }
}
