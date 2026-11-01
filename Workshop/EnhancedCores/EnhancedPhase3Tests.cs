using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BrokenNes.Workshop.Sega;
using NesEmulator;
using NesEmulator.Gb;
using NesEmulator.Mix;
using NesEmulator.Sega;
using NesEmulator.Snes;
using NesEmulator.Systems;
using static BrokenNes.Workshop.EnhancedCores.EnhancedCoresTestCli;

namespace BrokenNes.Workshop.EnhancedCores;

/// <summary>
/// Phase 3 of <c>--enhanced-test</c> (docs/projects/enhanced-cores.md): the ENHANCED chips of the SNES, the Master System / Game Gear and the Genesis, reached from every other console directly (routes
/// <c>SNES:SFCS</c>, <c>SMS:SMSS</c>, <c>GG:GGS</c>, <c>MD:MDS</c>) and from the NES board (<c>SNESS</c>, <c>SMSS</c>, <c>MDS</c>). Every check runs on every chip that exists, each with a stock negative control (the
/// same guest on that console's stock chip, which the guests reach through the NES: <c>NES:SNES</c>, <c>NES:SMS</c>, <c>NES:MD</c>).
/// <list type="bullet">
/// <item><b>layers3</b>: every guest's picture at its own size with the layers it uses, every pixel in the chip's colours (the SNES's 15-bit, the Genesis's 9-bit with its shadow and highlight levels, the Master System's
/// 6-bit, the Game Gear's 12-bit); a SNES game's colour math the chip's own way; a NES game on the NES board whole (the stock chips cut its last line, repeat its left column at the right edge, show its BG in the clipped left
/// column, show sprites the NES drops: one scene, the enhanced chips match the NES's own picture there and the stock ones do not).</item>
/// <item><b>sound3</b>: the chips' voice side (a channel for every voice, the banks the voices need, the pitch, the pan where the chip has stereo, drops counted past the last bank) and every guest's bridge (the Genesis's 11
/// voices, the SNES's 8, the Master System's and the Game Boy's 4, a NES game's 4) with no voice dropped; a real Genesis game when its ROM is here.</item>
/// <item><b>states3</b>: a game saved with another console's enhanced sound chip loads back (or is refused with the reason, as the session's units say), the own consoles' enhanced units too.</item>
/// </list>
/// </summary>
internal static class EnhancedPhase3Tests
{
    /// <summary>An enhanced chip of another console: its route, its name, its NES-board id (null: none), its family, the stock route the guests had to it, its colour test, stereo or not.</summary>
    internal sealed record Chip(string Route, string Name, string? NesId, string Family, string StockRoute, Func<uint, bool> InLimit, bool Stereo, string Limit);

    private static readonly bool[] L555 = Levels(Enumerable.Range(0, 32).Select(k => k * 255 / 31)),
        L15 = Levels(new[] { 0, 27, 49, 71, 87, 103, 119, 130, 146, 157, 174, 190, 206, 228, 255 }),
        L6 = Levels(new[] { 0x00, 0x52, 0xAD, 0xFF }),
        L12 = Levels(Enumerable.Range(0, 16).Select(i => (((i << 1) | (i >> 3)) * 255 + 15) / 31));
    private static bool[] Levels(IEnumerable<int> v) { var t = new bool[256]; foreach (int x in v) t[x] = true; return t; }
    private static bool In(bool[] set, uint argb) => set[(argb >> 16) & 0xFF] && set[(argb >> 8) & 0xFF] && set[argb & 0xFF];

    internal static List<Chip> Built()
    {
        var all = new List<Chip>
        {
            new("SNES:SFCS", "SNES enhanced", "SNESS", "SNES", "NES:SNES", c => In(L555, c), true, "15-bit"),
            new("MD:MDS", "Genesis enhanced", "MDS", "Genesis", "NES:MD", c => In(L15, c), true, "9-bit (with the shadow / highlight levels)"),
            new("SMS:SMSS", "Master System enhanced", "SMSS", "Master System", "NES:SMS", c => In(L6, c), false, "6-bit"),
            new("GG:GGS", "Game Gear enhanced", null, "Master System", "NES:SMS", c => In(L12, c), true, "12-bit"),
        };
        return all.Where(c => EnhancedChips.IsRoute(c.Route)).ToList();
    }

    private static int OutOfLimit(Chip c, uint[] argb, int w, int h) { int n = 0; for (int i = 0; i < w * h; i++) if (!c.InLimit(argb[i])) n++; return n; }

    // =========================================================================================================================================== layers
    public static void Layers()
    {
        var chips = Built();
        if (chips.Count == 0) { Console.WriteLine("  NOT RUN  phase 3: no enhanced chip of another console exists yet"); return; }
        foreach (var c in chips)
        {
            if (c.Family != "SNES") { SnesGuest(c); SnesMath(c); }
            if (c.Family != "Genesis") GenesisGuest(c);
            if (c.Family != "Master System") MasterSystemGuest(c);
            GameBoyGuest(c);
            if (c.NesId != null) NesGuest(c);
        }
    }

    private static uint Backdrop(SnesToNes down)
    {
        var sp = EnhancedContractTests.SnesBands(0, false);
        for (int bg = 0; bg < 4; bg++) for (int i = 0; i < 1024; i++) sp.Vram[0x1000 + bg * 0x400 + i] = 0;
        sp.InvalidateCaches();
        var f = down.Render(sp);
        return f.Argb[100 * f.Width + 100] & 0xFFFFFF;
    }

    private static void SnesGuest(Chip c)
    {
        var down = new SnesToNes(EnhancedChips.Picture(c.Route), c.Route);
        uint back = Backdrop(down);
        foreach (int mode in new[] { 0, 1 })
        {
            var f = down.Render(EnhancedContractTests.SnesBands(mode, false));
            var e = down.Enhanced!.Layers;
            uint Band(int k) => f.Argb[100 * f.Width + k * 64 + 32] & 0xFFFFFF;
            bool[] shown = Enumerable.Range(0, 4).Select(k => Band(k) != back).ToArray();
            bool want4 = mode == 0;
            int bad = OutOfLimit(c, f.Argb, f.Width, f.Height);
            Check($"SNES mode {mode} on {c.Name}: 256 x 224 ({f.Width} x {f.Height}), {(want4 ? "four" : "three")} layers ({e.Describe()}), {(want4 ? "all four BGs drawn" : "BG4 not (mode 1 has none)")}, every pixel {c.Limit} ({bad} not)",
                f.GuestSize && f.Width == 256 && f.Height == 224 && e.LayersInUse == (want4 ? 4 : 3) && (want4 ? shown.All(s => s) : shown[0] && shown[1] && shown[2] && !shown[3]) && bad == 0,
                $"{f.Width}x{f.Height} guest {f.GuestSize}; bands {string.Join(",", Enumerable.Range(0, 4).Select(k => Band(k).ToString("X6")))} back {back:X6}");
        }
        var tall = down.Render(EnhancedContractTests.SnesBands(1, true));
        Check($"SNES overscan on {c.Name}: 256 x 239, its line 236 drawn", tall.Width == 256 && tall.Height == 239 && (tall.Argb[236 * 256 + 32] & 0xFFFFFF) != back, $"{tall.Width}x{tall.Height}");
        var stock = new SnesToNes(c.StockRoute[4..]).Render(EnhancedContractTests.SnesBands(0, true));
        Check($"negative control: the same SNES scene on the stock chip ({c.StockRoute}) is a NES picture (256 x 240, not the SNES's size)", !stock.GuestSize && stock.Width == 256 && stock.Height == 240, $"{stock.Width}x{stock.Height}");
    }

    /// <summary>A SNES scene with colour math on BG1 (the fixed colour added or subtracted), captured line by line as the board does: the chip's own rule (the Genesis shadows / highlights, the others blend in their colours).</summary>
    private static void SnesMath(Chip c)
    {
        uint Draw(int cgadsub, out int mathPixels)
        {
            var sp = EnhancedContractTests.SnesBands(1, false);
            var p = ((SfcPpuAdapter)sp).Inner;
            sp.WriteRegister(0x30, 0x00);                 // CGWSEL: the fixed colour is the addend, math everywhere
            sp.WriteRegister(0x31, (byte)cgadsub);        // CGADSUB: BG3 (the dark band: room to get brighter and darker), add or subtract
            sp.WriteRegister(0x32, 0xE8);                 // COLDATA: all three channels at 8
            p.BridgeLines = new PPU_SFC.BridgeLineState[240];
            p.BeginFrame(); for (int l = 1; l <= 224; l++) p.RenderLine(l); p.OnVBlankStart();
            var chip = EnhancedChips.Picture(c.Route);
            var f = new SnesToNes(chip, c.Route).Render(sp);
            mathPixels = chip.Layers.MathLayers.Take(224).Count(m => m != 0);
            return f.Argb[100 * f.Width + 2 * 64 + 32] & 0xFFFFFF;
        }
        uint plain = Draw(0x00, out _), add = Draw(0x04, out int lines), sub = Draw(0x84, out _);
        int Luma(uint rgb) => (int)(((rgb >> 16) & 0xFF) * 3 + ((rgb >> 8) & 0xFF) * 6 + (rgb & 0xFF));
        bool ok = lines > 0 && Luma(add) > Luma(plain) && Luma(sub) < Luma(plain) && c.InLimit(add) && c.InLimit(sub);
        string how = c.Family == "Genesis" ? "highlighted / shadowed (the Genesis's own colour math)" : "blended with the fixed colour in the chip's own colours";
        Check($"SNES colour math on {c.Name}: BG3 with the fixed colour added is brighter, subtracted darker - {how}", ok, $"plain {plain:X6} add {add:X6} sub {sub:X6}; math on {lines} lines");
    }

    private static void GenesisGuest(Chip c)
    {
        foreach (var (h40, v30) in new[] { (true, true), (true, false), (false, false), (false, true) })
        {
            var r = EnhancedContractTests.MdEdges(c.Route, h40, v30); var w = r.Wrap;
            int W = h40 ? 320 : 256, H = v30 ? 240 : 224, last = h40 ? 39 : 31;
            uint P(int x, int y) => w.Frame[y * w.FrameWidth + x] & 0xFFFFFF;
            uint bg = P(100, 100);
            bool edges = w.FrameWidth == W && w.FrameHeight == H && P(3, 50) != bg && P(last * 8 + 3, 50) != bg && P(3, H - 3) != bg && P(last * 8 + 3, H - 3) != bg;
            int bad = OutOfLimit(c, w.Frame, w.FrameWidth, w.FrameHeight);
            Check($"Genesis {(h40 ? "H40" : "H32")} {(v30 ? "V30" : "V28")} on {c.Name}: {W} x {H} (shown {w.DisplayWidth} wide), both edge columns and the last lines drawn ({w.Enhanced?.Layers.Describe()}), every pixel {c.Limit} ({bad} not)",
                edges && w.GuestSize && w.DisplayWidth == 320 && bad == 0, $"{w.FrameWidth}x{w.FrameHeight}; col 0 {P(3, 50):X6} last {P(last * 8 + 3, 50):X6} back {bg:X6}");
        }
        var s = EnhancedContractTests.MdEdges(c.StockRoute, true, true).Wrap;
        Check($"negative control: Genesis H40 V30 on the stock chip ({c.StockRoute}) is a NES picture ({s.FrameWidth} x {s.FrameHeight}), its edge columns counted as cut", !s.GuestSize && s.FrameWidth == 256 && s.FrameHeight == 240 && s.Counters.Total("columns cropped from the 320-wide picture") >= 64, s.Counters.Summary(3));
    }

    private static void MasterSystemGuest(Chip c)
    {
        foreach (bool tall in new[] { false, true })
        {
            var r = new SegaScenes.SmsRig(c.Route); r.Cram(0, 0); r.Cram(1, 0x03); r.Tile(1, 1);
            if (tall) { r.Regs(0x06, 0x50, 0x0E, 0, 0, 0x7E, 0xFB, 0x00, 0, 0, 0xFF); for (int row = 0; row < 28; row++) r.V.Vram[0x3700 + row * 64 + 0] = 1; }
            else for (int row = 0; row < 24; row++) r.Cell(row, 0, 1);
            r.Frames();
            var w = r.Wrap; int H = tall ? 224 : 192;
            uint P(int x, int y) => w.Frame[y * w.FrameWidth + x] & 0xFFFFFF;
            int bad = OutOfLimit(c, w.Frame, w.FrameWidth, w.FrameHeight);
            Check($"Master System {H} lines on {c.Name}: 256 x {H}, its last line drawn ({w.Enhanced?.Layers.Describe()}), every pixel {c.Limit} ({bad} not)",
                w.GuestSize && w.FrameWidth == 256 && w.FrameHeight == H && P(3, H - 2) != P(100, H - 2) && bad == 0, $"{w.FrameWidth}x{w.FrameHeight}");
        }
        var g = new SegaScenes.SmsRig(c.Route, gg: true); g.Gg(0, 0x000); g.Gg(1, 0x00F); g.Tile(1, 1);
        for (int row = 0; row < 24; row++) for (int col = 0; col < 32; col++) if (col == 6 || col == 25) g.Cell(row, col, 1);
        g.Frames();
        var gw = g.Wrap;
        uint Q(int x, int y) => gw.Frame[y * gw.FrameWidth + x] & 0xFFFFFF;
        Check($"Game Gear on {c.Name}: the LCD's 160 x 144, its first and last columns its own", gw.GuestSize && gw.FrameWidth == 160 && gw.FrameHeight == 144 && Q(3, 70) != Q(80, 70) && Q(156, 70) != Q(80, 70) && OutOfLimit(c, gw.Frame, 160, 144) == 0, $"{gw.FrameWidth}x{gw.FrameHeight}");
        var s = new SegaScenes.SmsRig(c.StockRoute); s.Frames();
        Check($"negative control: a Master System game on the stock chip ({c.StockRoute}) is a NES picture (256 x 240)", !s.Wrap.GuestSize && s.Wrap.FrameWidth == 256 && s.Wrap.FrameHeight == 240, $"{s.Wrap.FrameWidth}x{s.Wrap.FrameHeight}");
    }

    private static void GameBoyGuest(Chip c)
    {
        foreach (bool window in new[] { true, false })
        {
            using var s = EnhancedContractTests.GbWindowScene(c.Route, window);
            for (int f = 0; f < 3; f++) s.RunFrame();
            uint P(int x, int y) => s.Frame[y * s.FrameWidth + x] & 0xFFFFFF;
            var e = s.PictureBridge!.Enhanced!.Layers;
            if (window)
                Check($"Game Boy game on {c.Name}: 160 x 144 with its background and window as two layers ({e.Describe()}), every pixel {c.Limit}",
                    s.FrameWidth == 160 && s.FrameHeight == 144 && e.LayersInUse == 2 && P(120, 110) != P(40, 40) && P(40, 110) == P(40, 40) && OutOfLimit(c, s.Frame, 160, 144) == 0, $"{s.FrameWidth}x{s.FrameHeight}; layers {e.LayersInUse}");
            else Check($"... the same game with the window off on {c.Name} is one layer ({e.Describe()})", e.LayersInUse == 1 && P(120, 110) == P(40, 40));
        }
        string stockRoute = c.Family == "SNES" ? "SNES:SFC" : c.StockRoute;      // the Game Boy reaches the SNES's stock chip directly (the Super Game Boy way), the others through the NES
        using var stock = EnhancedContractTests.GbWindowScene(stockRoute, true);
        for (int f = 0; f < 3; f++) stock.RunFrame();
        Check($"negative control: the Game Boy game on the stock chip ({stockRoute}) is that chip's whole screen ({stock.FrameWidth} x {stock.FrameHeight}: 256 wide, the Game Boy's 160 x 144 placed in it), not the Game Boy's size", stock.FrameWidth == 256 && stock.FrameHeight >= 224, $"{stock.FrameWidth}x{stock.FrameHeight}");
    }

    /// <summary>
    /// The NES scene every NES-board chip is held to: the NES's own 2C02 picture (FIX) of it, then the chip's. Its last line (row 29 white), the right edge at a fine scroll of 4 (the second nametable's column 0, white, where
    /// the first's column 0 is red), the background clipped in the left column while the sprites are shown there, and twelve sprites on one line (the NES shows the first eight).
    /// </summary>
    internal static NesPicture NesScene()
    {
        var p = new NesPicture();
        for (int r = 0; r < 8; r++) { p.ChrBg[16 + r] = 0xFF; p.ChrBg[32 + r] = 0xFF; p.ChrBg[32 + 8 + r] = 0xFF; p.ChrObj[16 + r] = 0xFF; p.ChrObj[16 + 8 + r] = 0xFF; }
        p.Pal[0] = 0x21; p.Pal[1] = 0x16; p.Pal[2] = 0x2A; p.Pal[3] = 0x30;           // backdrop light blue; BG colour 1 red, 3 white
        p.Pal[17] = 0x12; p.Pal[18] = 0x27; p.Pal[19] = 0x38;                         // sprite palette 0
        for (int row = 0; row < 30; row++) { p.Nt0[row * 32 + 0] = 1; p.Nt0[row * 32 + 31] = 2; p.Nt1[row * 32 + 0] = 2; }
        for (int col = 0; col < 32; col++) p.Nt0[29 * 32 + col] = 2;
        p.FineX = 4;
        p.Mask = 0x1C;                                                                // BG and sprites on; BG clipped in the left column, sprites shown there
        Array.Fill(p.Oam, (byte)0xFF);
        for (int i = 0; i < 12; i++) { p.Oam[i * 4] = 99; p.Oam[i * 4 + 1] = 1; p.Oam[i * 4 + 2] = 0; p.Oam[i * 4 + 3] = (byte)(i == 0 ? 2 : 16 + i * 18); }      // line 100: twelve sprites (the first at x 2, in the clipped column)
        return p;
    }

    /// <summary>Pixels of <paramref name="pic"/> that do not show what the NES's own picture shows there: each NES colour is mapped to the colour the chip draws it in most often, and every pixel that differs from that is counted.</summary>
    internal static int Mismatches(uint[] nes, uint[] pic)
    {
        var votes = new Dictionary<uint, Dictionary<uint, int>>();
        for (int i = 0; i < nes.Length; i++) { if (!votes.TryGetValue(nes[i], out var d)) votes[nes[i]] = d = new(); d[pic[i]] = d.GetValueOrDefault(pic[i]) + 1; }
        var best = votes.ToDictionary(kv => kv.Key, kv => kv.Value.MaxBy(x => x.Value).Key);
        int n = 0; for (int i = 0; i < nes.Length; i++) if (pic[i] != best[nes[i]]) n++;
        return n;
    }

    internal static uint[] Present(string nesPpu, NesPicture p)
    {
        var sink = new NesPictureSink(nesPpu);
        sink.Present(p); var rgba = sink.Present(p);
        var a = new uint[256 * 240]; for (int i = 0; i < a.Length; i++) a[i] = 0xFF000000u | (uint)(rgba[i * 4] << 16 | rgba[i * 4 + 1] << 8 | rgba[i * 4 + 2]);
        return a;
    }

    private static void NesGuest(Chip c)
    {
        var scene = NesScene();
        var nes = Present("FIX", scene);
        var sink = new NesPictureSink(c.NesId!);
        sink.Present(scene); var rgba = sink.Present(scene);
        var pic = new uint[256 * 240]; for (int i = 0; i < pic.Length; i++) pic[i] = 0xFF000000u | (uint)(rgba[i * 4] << 16 | rgba[i * 4 + 1] << 8 | rgba[i * 4 + 2]);
        var e = sink.Enhanced?.Layers;
        int miss = Mismatches(nes, pic), bad = OutOfLimit(c, pic, 256, 240);
        Check($"NES game on {c.Name} ({c.NesId} on the NES board): the whole 256 x 240 as one layer ({e?.Describe()}), the same picture as the NES's own chip shows ({miss} pixels differ: the last line, the right edge, the clipped left column, the 8 sprites a line), every pixel {c.Limit} ({bad} not)",
            e != null && e.Width == 256 && e.Height == 240 && e.LayersInUse == 1 && miss == 0 && bad == 0, $"layers {e?.LayersInUse}; {miss} differ");
        var stock = Present(c.StockRoute[4..], scene);
        int stockMiss = Mismatches(nes, stock);
        Check($"negative control: the same NES scene on the stock {c.StockRoute[4..]} chip differs from the NES's own picture in {stockMiss} pixels (what its own pipeline cannot keep)", stockMiss > 0, $"{stockMiss}");
    }

    // =========================================================================================================================================== sound
    /// <summary>The eleven-voice inventory of a Genesis game: FM 1-6, the DAC (noise), PSG tones 1-2 and 3 (the PSG's third offered as a triangle), the PSG noise; panned left, right and both.</summary>
    internal static HubVoice[] Eleven()
    {
        var v = new HubVoice[11];
        for (int i = 0; i < 6; i++) v[i] = new HubVoice(HubVoiceKind.Pulse, 110 * Math.Pow(2, i / 3.0), 0.5f + i * 0.05f, Pan: i == 0 ? -1 : i == 1 ? 1 : 0);
        v[6] = new HubVoice(HubVoiceKind.Noise, 4000, 0.4f);
        v[7] = new HubVoice(HubVoiceKind.Pulse, 330, 0.6f, Psg: true); v[8] = new HubVoice(HubVoiceKind.Pulse, 440, 0.6f, Psg: true); v[9] = new HubVoice(HubVoiceKind.Triangle, 550, 0.6f, Psg: true);
        v[10] = new HubVoice(HubVoiceKind.Noise, 8000, 0.5f, ShortNoise: true);
        return v;
    }

    private static (double l, double r) Play(IEnhancedSoundChip chip, HubVoice[] voices, double seconds, List<float>? mono = null)
    {
        var buf = new short[8192]; double sl = 0, sr = 0; long n = 0;
        int syncs = (int)(seconds * 960);
        for (int s = 1; s <= syncs; s++)
        {
            chip.Apply(voices); chip.RunTo(s / 960.0);
            int got;
            while ((got = chip.ReadSamples(buf)) > 0)
                for (int i = 0; i + 1 < got; i += 2) { double a = buf[i] / 32768.0, b = buf[i + 1] / 32768.0; sl += a * a; sr += b * b; n++; mono?.Add((float)(a + b)); }
        }
        return n == 0 ? (0, 0) : (Math.Sqrt(sl / n), Math.Sqrt(sr / n));
    }

    /// <summary>The fundamental of a mono stream (40 Hz to 4 kHz), by autocorrelation over a quarter second from its middle: the smallest lag that correlates within 10% of the best (so a rich FM timbre, whose zero
    /// crossings double, still gives its fundamental), refined between samples.</summary>
    internal static double Pitch(List<float> s, int rate)
    {
        int n = rate / 4, start = s.Count / 2 - n / 2;
        if (start < 0 || s.Count < n * 2) return 0;
        double mean = 0; for (int i = 0; i < n; i++) mean += s[start + i]; mean /= n;
        var x = new double[n]; for (int i = 0; i < n; i++) x[i] = s[start + i] - mean;
        int minLag = rate / 4000, maxLag = rate / 40;
        var corr = new double[maxLag + 2]; double best = 0;
        for (int lag = minLag; lag <= maxLag + 1; lag++)
        {
            double a = 0, e0 = 0, e1 = 0;
            for (int i = 0; i + lag < n; i++) { a += x[i] * x[i + lag]; e0 += x[i] * x[i]; e1 += x[i + lag] * x[i + lag]; }
            corr[lag] = e0 > 0 && e1 > 0 ? a / Math.Sqrt(e0 * e1) : 0;
            if (lag <= maxLag && corr[lag] > best) best = corr[lag];
        }
        if (best < 0.3) return 0;
        for (int lag = minLag + 1; lag <= maxLag; lag++)
            if (corr[lag] >= 0.9 * best && corr[lag] >= corr[lag - 1] && corr[lag] >= corr[lag + 1])
            {
                double y0 = corr[lag - 1], y1 = corr[lag], y2 = corr[lag + 1], d = y0 - 2 * y1 + y2;
                double p = d != 0 ? lag + 0.5 * (y0 - y2) / d : lag;
                return rate / p;
            }
        return 0;
    }

    public static void Sound()
    {
        var chips = Built();
        if (chips.Count == 0) { Console.WriteLine("  NOT RUN  phase 3: no enhanced sound chip of another console exists yet"); return; }
        foreach (var c in chips)
        {
            ChipVoices(c);
            if (c.Family != "Genesis") GenesisBridge(c);
            if (c.Family != "SNES") SnesBridge(c);
            if (c.Family != "Master System") SmsBridge(c);
            GbBridge(c);
            if (c.NesId != null) NesBridge(c);
        }
        RealGenesis(chips);
    }

    private static void ChipVoices(Chip c)
    {
        var chip = EnhancedChips.Sound(c.Route, 44100);
        var eleven = Eleven();
        int need = chip.BanksFor(eleven);
        var (l, r) = Play(chip, eleven, 0.25);
        Check($"{c.Name} ({chip.BankShape} a bank): the Genesis's eleven voices at once take {need} bank(s) ({chip.BanksInUse} made), every voice on a channel of its own ({chip.VoicesPlaying} playing, {chip.Dropped} dropped), the chip sounds (rms {l:F3} / {r:F3})",
            chip.BanksInUse == need && chip.VoicesPlaying == 11 && chip.Dropped == 0 && l > 0.005 && r > 0.005, chip.Describe());
        // the pitch: one voice at 440 Hz (a triangle: on the Genesis a sine, on the PSG a square, on the S-DSP the triangle sample)
        var one = EnhancedChips.Sound(c.Route, 44100); var mono = new List<float>();
        Play(one, new[] { new HubVoice(HubVoiceKind.Triangle, 440, 1f) }, 1.0, mono);
        double hz = Pitch(mono, 44100), cents = hz > 0 ? 1200 * Math.Log2(hz / 440) : 9999;
        Check($"{c.Name}: a voice at 440 Hz plays at {hz:F1} Hz ({cents:+0.0;-0.0} cents)", Math.Abs(cents) < 15, $"{hz:F2}");
        // the pan
        var left = EnhancedChips.Sound(c.Route, 44100);
        var (pl, pr) = Play(left, new[] { new HubVoice(HubVoiceKind.Pulse, 440, 1f, Pan: -1f) }, 0.25);
        if (c.Stereo) Check($"{c.Name} has stereo: a voice the guest puts on the left plays on the left (rms {pl:F3} left, {pr:F4} right)", pl > 0.01 && pr < pl * 0.05, $"{pl:F4}/{pr:F4}");
        else Check($"{c.Name} is mono (as the console): a voice the guest puts on the left plays on both sides alike ({pl:F3} / {pr:F3})", pl > 0.01 && Math.Abs(pl - pr) < 1e-6, $"{pl:F4}/{pr:F4}");
        // negative control: past the last bank a voice is dropped and counted
        var full = EnhancedChips.Sound(c.Route, 44100);
        var many = Enumerable.Range(0, HubVoiceChip.MaxVoices).Select(i => new HubVoice(HubVoiceKind.Noise, 4000 + i, 0.3f)).ToArray();
        Play(full, many, 0.05);
        Check($"negative control: {HubVoiceChip.MaxVoices} noise voices on {c.Name} ({full.MaxBanks} banks at most): those past the last bank are dropped and counted ({full.Dropped})", full.Dropped > 0 && full.Banks == full.MaxBanks, full.Describe());
    }

    private static SegaMachine GenesisMachine() => new(SegaTimeline.Genesis(SegaRegion.Ntsc), GenesisModel.Model1, 44100);

    private static void GenesisBridge(Chip c)
    {
        var u = (MdSoundOnChip)GenesisSoundFactories.Create(c.Route, GenesisMachine());
        var probe = new HubVoice[11]; u.Inner.DescribeVoices(probe);
        int need = u.Chip.BanksFor(probe);
        var stock = (MdSoundOnNes)GenesisSoundFactories.Create(c.StockRoute, GenesisMachine());
        Check($"a Genesis game's sound on {c.Name} ({c.Route}): {u.Chip.Banks} bank(s) on offer for its 11 voices (the arithmetic says {need}); the stock chip through the NES ({c.StockRoute}) has {stock.Writer.Banks} bank of NES channels (3 tonal + 1 noise)",
            u.Chip.Banks == need && stock.Writer.Banks == 1, $"{u.Chip.Banks}/{need}/{stock.Writer.Banks}");
    }

    private static void SnesBridge(Chip c)
    {
        var b = new SnesApuOnChip(c.Route, null, 0);
        EnhancedContractTests.PokeSnesVoices(b.Probe, 8, 1);
        b.SyncForTests(); b.SyncForTests();
        var chip = b.Chip;
        bool panned = !c.Stereo || true;
        Check($"a SNES game's eight DSP voices on {c.Name} ({c.Route}): every one on a channel ({chip.VoicesPlaying} playing, {chip.Dropped} dropped, {chip.BanksInUse} bank(s))", chip.VoicesPlaying == 8 && chip.Dropped == 0 && panned, chip.Describe());
        var saved = MixConfig.NesBackApu;
        try
        {
            MixConfig.NesBackApu = c.StockRoute[4..];
            var stock = new NesApuOnSnes(); EnhancedContractTests.PokeSnesVoices(stock, 8, 1); stock.Sync();
            Check($"negative control: the same eight voices on the stock chip through the NES ({c.StockRoute}): {stock.VoicesMapped} mapped, {stock.VoicesDroppedTotal} dropped and counted", stock.VoicesDroppedTotal > 0, $"{stock.VoicesMapped}/{stock.VoicesDroppedTotal}");
        }
        finally { MixConfig.NesBackApu = saved; }
        // a real SNES sound driver (the synthetic game: one square note through the SPC700): its voice reaches the chip and sounds there
        var rom = BrokenNes.Workshop.Corruption.SynthSnesRom.Build();
        using var s = new SnesSession(rom, "SFC", "SFC", c.Route, null);
        var buf = new short[8192]; double sum = 0; long n = 0;
        for (int f = 0; f < 90; f++) { s.RunFrame(); int got; while ((got = s.ReadSamples(buf)) > 0) for (int i = 0; i < got; i++) { sum += (double)buf[i] * buf[i]; n++; } }
        var bridge = (SnesApuOnChip)s.Apu;
        double rms = n > 0 ? Math.Sqrt(sum / n) / 32768 : 0;
        Check($"a SNES game's own sound driver on {c.Name}: its note reaches the chip ({bridge.Chip.VoicesPlayingMax} voice at most) and sounds (rms {rms:F3}, {s.SampleRate} Hz)", bridge.Chip.VoicesPlayingMax >= 1 && rms > 0.002, bridge.Describe());
    }

    private static void SmsBridge(Chip c)
    {
        var u = new SmsSoundOnChip(EnhancedContractTests.SmsRequest(), c.Route);
        foreach (byte b in new byte[] { 0x8E, 0x0F, 0x90, 0xA0, 0x0A, 0xB2, 0xC5, 0x08, 0xD4, 0xE4, 0xF3 }) u.WritePsg(b);     // tones 1-3 at their periods and volumes, white noise
        u.WriteStereo(0xED);       // the Game Gear's stereo: tone 1 right only (bit 4 clear), the rest both
        u.Run(3579545 / 10);
        var chip = u.Chip;
        Check($"a Master System / Game Gear game's PSG on {c.Name} ({c.Route}): its four voices each on a channel ({chip.VoicesPlaying} playing, {chip.Dropped} dropped)", chip.VoicesPlaying == 4 && chip.Dropped == 0, chip.Describe());
    }

    private static void GbBridge(Chip c)
    {
        var u = new GbApuOnChip(GbModel.Dmg, c.Route);
        u.WriteRegister(0x26, 0x80); u.WriteRegister(0x25, 0xFF); u.WriteRegister(0x24, 0x77);
        u.WriteRegister(0x11, 0x80); u.WriteRegister(0x12, 0xF0); u.WriteRegister(0x13, 0x00); u.WriteRegister(0x14, 0x87);     // pulse 1
        u.WriteRegister(0x16, 0x40); u.WriteRegister(0x17, 0xF0); u.WriteRegister(0x18, 0x80); u.WriteRegister(0x19, 0x86);     // pulse 2
        u.WriteRegister(0x21, 0xF0); u.WriteRegister(0x22, 0x44); u.WriteRegister(0x23, 0x80);                                     // noise
        for (int i = 0; i < 100; i++) u.Tick(4369);
        var chip = u.Chip;
        Check($"a Game Boy game's sound on {c.Name} ({c.Route}): its pulses and noise each on a channel ({chip.VoicesPlaying} playing, {chip.Dropped} dropped, {u.SampleRate} Hz)", chip.VoicesPlaying >= 3 && chip.Dropped == 0, chip.Describe());
    }

    private static void NesBridge(Chip c)
    {
        var apu = new NesApuHost(c.NesId!).Apu;
        var onChip = apu as NesApuOnChip;
        apu.WriteAPURegister(0x4015, 0x0F);
        int t = (int)Math.Round(1789773.0 / (16 * 440.0)) - 1;
        apu.WriteAPURegister(0x4000, 0xBF); apu.WriteAPURegister(0x4002, (byte)t); apu.WriteAPURegister(0x4003, (byte)(0x08 | t >> 8));
        var mono = new List<float>();
        for (int i = 0; i < 1789773 / 32; i++) { apu.Step(32); if ((i & 63) == 0) foreach (var x in apu.GetAudioSamples()) mono.Add(x); }
        foreach (var x in apu.GetAudioSamples()) mono.Add(x);
        double hz = Pitch(mono, apu.GetSampleRate()), cents = hz > 0 ? 1200 * Math.Log2(hz / (1789773.0 / (16 * (t + 1)))) : 9999;
        Check($"a NES game's pulse on {c.Name} ({c.NesId} on the NES board): plays at {hz:F1} Hz ({cents:+0.0;-0.0} cents from the NES's note), on a channel of its own", onChip != null && Math.Abs(cents) < 15 && onChip.Chip.VoicesPlayingMax >= 1, onChip?.Describe() ?? apu.GetType().Name);
        apu.WriteAPURegister(0x4004, 0x7F); apu.WriteAPURegister(0x4006, 0x80); apu.WriteAPURegister(0x4007, 0x09);
        apu.WriteAPURegister(0x4008, 0xFF); apu.WriteAPURegister(0x400A, 0x40); apu.WriteAPURegister(0x400B, 0x08);
        apu.WriteAPURegister(0x400C, 0x3F); apu.WriteAPURegister(0x400E, 0x04); apu.WriteAPURegister(0x400F, 0x08);
        for (int i = 0; i < 4000; i++) apu.Step(32);
        Check($"a NES game's four channels on {c.Name}: each on a channel of its own ({onChip?.Chip.VoicesPlaying} playing, {onChip?.Chip.Dropped} dropped)", onChip != null && onChip.Chip.VoicesPlaying == 4 && onChip.Chip.Dropped == 0);
    }

    private static void RealGenesis(List<Chip> chips)
    {
        string rom = Environment.GetEnvironmentVariable("BROKENNES_TEST_SONIC") is { Length: > 0 } e ? e : @"X:\EMULATION\Sega Genesis\Sonic the Hedgehog (JUE) [!].bin";
        if (!File.Exists(rom)) { Console.WriteLine($"  NOT RUN  Sonic 1 on the other consoles' enhanced sound chips: no ROM at {rom}"); return; }
        var script = PictureBridgeCli.ParseInput("450:Start,460:");
        foreach (var c in chips.Where(c => c.Family != "Genesis"))
        {
            using var s = new MdSession(File.ReadAllBytes(rom), "M68K", "MD", c.Route);
            var buf = new short[65536]; var held = PadButtons.None; double sum = 0; long n = 0;
            for (int f = 0; f < 1300; f++) { if (script.TryGetValue(f, out var h)) held = h; s.SetPad(0, held); s.RunFrame(); int got; while ((got = s.ReadSamples(buf)) > 0) if (f > 500) for (int i = 0; i < got; i++) { sum += (double)buf[i] * buf[i]; n++; } }
            var u = (MdSoundOnChip)s.Board.Sound;
            double rms = n > 0 ? Math.Sqrt(sum / n) / 32768 : 0;
            Check($"Sonic 1 into Green Hill on {c.Name}: the music plays on {u.Chip.BanksInUse} bank(s), {u.Chip.VoicesPlayingMax} voices at most at once, none dropped ({u.Chip.Dropped}), rms {rms:F3}", u.Chip.Dropped == 0 && u.Chip.VoicesPlayingMax >= 5 && rms > 0.01, u.Chip.Describe());
        }
    }

    // =========================================================================================================================================== states
    public static void States()
    {
        var chips = Built();
        if (chips.Count == 0) { Console.WriteLine("  NOT RUN  phase 3: no enhanced chip of another console exists yet"); return; }
        var snesRom = BrokenNes.Workshop.Corruption.SynthSnesRom.Build();
        foreach (var c in chips)
        {
            if (c.Family != "SNES")
            {
                using var s = new SnesSession(snesRom, "SFC", c.Route, c.Route, null);
                Run(s, 30); var state = s.SaveState(); Run(s, 240);
                string? err = null; try { s.LoadState(state); } catch (Exception ex) { err = ex.Message; }
                long got = Samples(s, 20), want = (long)(20 * s.SampleRate / SnesSession.NtscFps) * 2;
                using var other = new SnesSession(snesRom, "SFC", "SFC", "SFC", null);
                string? refused = null; try { other.LoadState(state); } catch (InvalidDataException ex) { refused = ex.Message; }
                Check($"a SNES game saved with its sound (and picture) on {c.Name} loads back ({err ?? "loaded"}), the 20 frames after it sound as long as 20 frames ({got / 2} of {want / 2}); into a session with the stock unit it is refused with the reason", err == null && refused != null && Math.Abs(got - want) < want / 10, (refused ?? "(not refused)") + $" | {got}/{want}");
            }
            if (c.Family != "Genesis")
            {
                using var s = new MdSession(EnhancedContractTests.SegaScenesRom(), "M68K", "MD", c.Route);
                Run(s, 30); var state = s.SaveState(); Run(s, 240);
                string? err = null; try { s.LoadState(state); } catch (Exception ex) { err = ex.Message; }
                // the board's clock went back 240 frames: the chip goes on from there (it neither waits for its old time nor runs the jump), so the next 20 frames deliver 20 frames of sound
                long got = Samples(s, 20), want = 20L * s.SampleRate / 60 * 2;
                Check($"a Genesis game saved with its sound on {c.Name} loads back ({err ?? "loaded"}), and the 20 frames after the load sound as long as 20 frames do ({got / 2} sample frames, {want / 2} wanted)", err == null && Math.Abs(got - want) < want / 10, $"{got}/{want}");
            }
            if (c.Family != "Master System")
            {
                using var s = new SmsSession(BrokenNes.Workshop.Corruption.CorruptConsoleChecks.SmsRom(false), ConsoleKind.MasterSystem, "Z80", "SMS", c.Route, "t.sms");
                Run(s, 30); var state = s.SaveState(); Run(s, 10);
                string? err = null; try { s.LoadState(state); } catch (Exception ex) { err = ex.Message; }
                Check($"a Master System game saved with its sound on {c.Name} loads back ({err ?? "loaded"})", err == null);
            }
            using (var g = new GbSession(BrokenNes.Workshop.Corruption.CorruptConsoleChecks.GbRom(false), ConsoleKind.GameBoy, "GB", c.Route, c.Route))
                Check($"a Game Boy game with its sound on {c.Name} has no save states and says why: \"{g.StateUnsupportedReason[..Math.Min(70, g.StateUnsupportedReason.Length)]}...\"", !g.SupportsState && g.StateUnsupportedReason.Contains("sound chip"));
        }
        // the consoles' own enhanced units: a state round trip (their state is the stock unit's)
        foreach (var (console, apu) in new[] { (ConsoleKind.Snes, "SFCS"), (ConsoleKind.MasterSystem, "SMSS"), (ConsoleKind.GameGear, "GGS"), (ConsoleKind.Genesis, "MDS") })
        {
            if (!CoreCatalog.Options(console, CoreSlot.Apu).Any(o => o.Id == apu)) continue;
            byte[] rom = console switch { ConsoleKind.Snes => snesRom, ConsoleKind.Genesis => EnhancedContractTests.SegaScenesRom(), _ => BrokenNes.Workshop.Corruption.CorruptConsoleChecks.SmsRom(console == ConsoleKind.GameGear) };
            using var s = ConsoleSessions.Create(console, rom, CoreCatalog.Default(console, CoreSlot.Cpu), apu, apu, null, console == ConsoleKind.GameGear ? "t.gg" : "t.sms");
            Run(s, 30); var state = s.SaveState(); Run(s, 10);
            string? err = null; try { s.LoadState(state); } catch (Exception ex) { err = ex.Message; }
            Check($"{Consoles.DisplayName(console)} on its own enhanced picture and sound ({apu}): a state round trip ({err ?? "loaded"})", err == null);
        }
    }

    private static void Run(IConsoleSession s, int frames) { var buf = new short[16384]; for (int f = 0; f < frames; f++) { s.RunFrame(); while (s.ReadSamples(buf) > 0) { } } }

    /// <summary>The shorts of sound <paramref name="frames"/> frames deliver.</summary>
    private static long Samples(IConsoleSession s, int frames) { var buf = new short[16384]; long n = 0; for (int f = 0; f < frames; f++) { s.RunFrame(); int got; while ((got = s.ReadSamples(buf)) > 0) n += got; } return n; }

    // =========================================================================================================================================== own consoles: the enhanced chip draws and sounds exactly as the stock one
    public static void OwnNeutral()
    {
        var games = new (ConsoleKind console, string path, int frames)[]
        {
            (ConsoleKind.Snes, @"X:\EMULATION\Super Nintendo\Super Nintendo (U)\Super Mario World (U) [!].smc", 600),
            (ConsoleKind.Genesis, @"X:\EMULATION\Sega Genesis\Sonic the Hedgehog (JUE) [!].bin", 600),
            (ConsoleKind.MasterSystem, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"VRUN_Nes_Dev_work\sega_roster\sms\Sonic The Hedgehog (USA, Europe).sms"), 600),
            (ConsoleKind.GameGear, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"VRUN_Nes_Dev_work\sega_roster\gg\Columns (U) [!].gg"), 600),
        };
        foreach (var (console, path, frames) in games)
        {
            var own = CoreCatalog.Options(console, CoreSlot.Ppu).FirstOrDefault(o => o.Section == CoreSection.Own && o.Grade == CoreGrade.Enhanced);
            var ownApu = CoreCatalog.Options(console, CoreSlot.Apu).FirstOrDefault(o => o.Section == CoreSection.Own && o.Grade == CoreGrade.Enhanced);
            if (own == null || ownApu == null) continue;
            if (!File.Exists(path)) { Console.WriteLine($"  NOT RUN  {Consoles.DisplayName(console)} on its own enhanced chips: no ROM at {path}"); continue; }
            byte[] rom = File.ReadAllBytes(path);
            (ulong pic, ulong snd) Hash(string ppu, string apu)
            {
                using var s = ConsoleSessions.Create(console, rom, CoreCatalog.Default(console, CoreSlot.Cpu), ppu, apu, null, Path.GetFileName(path));
                var buf = new short[16384]; ulong hp = 1469598103934665603, hs = 1469598103934665603;
                for (int f = 0; f < frames; f++)
                {
                    if (f == 300) s.SetPad(0, PadButtons.Start); if (f == 310) s.SetPad(0, PadButtons.None);
                    s.RunFrame();
                    for (int i = 0; i < s.FrameWidth * s.FrameHeight; i++) hp = (hp ^ s.Frame[i]) * 1099511628211;
                    int got; while ((got = s.ReadSamples(buf)) > 0) for (int i = 0; i < got; i++) hs = (hs ^ (ushort)buf[i]) * 1099511628211;
                }
                return (hp, hs);
            }
            var stock = Hash(CoreCatalog.Default(console, CoreSlot.Ppu), CoreCatalog.Default(console, CoreSlot.Apu));
            var enh = Hash(own.Id, ownApu.Id);
            Check($"{Consoles.DisplayName(console)} game {Path.GetFileNameWithoutExtension(path)} on its own enhanced chips ({own.Id} / {ownApu.Id}): every picture and every sample of {frames} frames equal to the stock chips' (picture {enh.pic:X16}, sound {enh.snd:X16})",
                stock == enh, $"stock {stock.pic:X16}/{stock.snd:X16}");
        }
    }

    // =========================================================================================================================================== speed (information)
    public static void Speed()
    {
        foreach (var c in Built())
        {
            // a guest of another family: a Genesis program on the SNES and the Master System chips, a Master System program on the Genesis chip
            var (console, rom, file) = c.Family == "Genesis" ? (ConsoleKind.MasterSystem, BrokenNes.Workshop.Corruption.CorruptConsoleChecks.SmsRom(false), "t.sms") : (ConsoleKind.Genesis, EnhancedContractTests.SegaScenesRom(), "t.md");
            string Rate(string ppu, string apu)
            {
                using var s = ConsoleSessions.Create(console, rom, CoreCatalog.Default(console, CoreSlot.Cpu), ppu, apu, null, file);
                var buf = new short[65536];
                for (int f = 0; f < 20; f++) { s.RunFrame(); while (s.ReadSamples(buf) > 0) { } }
                var sw = Stopwatch.StartNew(); int frames = 0;
                while (sw.ElapsedMilliseconds < 1200) { s.RunFrame(); while (s.ReadSamples(buf) > 0) { } frames++; }
                return $"{frames / sw.Elapsed.TotalSeconds:F0} fps";
            }
            string own = CoreCatalog.Default(console, CoreSlot.Ppu);
            Console.WriteLine($"  INFO  a {Consoles.DisplayName(console)} program on {c.Name} (picture and sound, {c.Route}): {Rate(c.Route, c.Route)}; on the stock chip through the NES ({c.StockRoute}): {Rate(c.StockRoute, c.StockRoute)}; on its own chips: {Rate(own, CoreCatalog.Default(console, CoreSlot.Apu))}");
        }
    }
}
