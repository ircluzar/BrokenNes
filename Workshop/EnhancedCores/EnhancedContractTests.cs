using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// The two enhanced contracts of <c>--enhanced-test</c> (docs/projects/enhanced-cores.md, sections 4 and 5):
/// <list type="bullet">
/// <item><b>layers</b>: one layered picture (<see cref="ExtPicture"/>) at the guest's own size and layer count, filled by every translator and drawn by both enhanced picture chips (FIX enhanced
/// <c>PPU_FIXS</c>, Game Boy enhanced <c>PPU_DMGS</c>) through <see cref="IEnhancedPictureChip"/>: a SNES game in mode 0 is four layers, in mode 1 three (the same video memory: the fourth band
/// is the negative control), 224 or 239 lines; a Genesis game 320 or 256 wide, 224 or 240 lines (its edge columns there; the stock chip's cropped picture is the negative control); a Master
/// System game 256 x 192 or 224; a Game Gear game 160 x 144; a Game Boy game on FIX enhanced 160 x 144 with its background and window as two layers (a frame without the window: one); a NES game
/// on Game Boy enhanced 256 x 240, one layer, its right edge its own (the next nametable's column, not a wrap). Every stock chip is shown drawing the NES's 256 x 240 instead.</item>
/// <item><b>sound</b>: one voice list per guest (<see cref="IHubVoiceSource"/>: SNES 8, Genesis 11, Master System FM 18, Master System PSG 4, Game Boy 4, with the guest's pan where it says)
/// and the enhanced sound chips (<c>APU_FIXS</c>, <c>APU_DMGS</c>) growing to as many channel banks as those voices need (<see cref="IApuExtChannels"/>): no voice without a channel there,
/// voices dropped and counted on the stock chips (the negative control).</item>
/// </list>
/// </summary>
internal static class EnhancedContractTests
{
    private static readonly string[] EnhancedChips = { "FIXS", "DMGS" };

    // =========================================================================================================== layers
    public static void Layers()
    {
        SnesLayers();
        GenesisSizes();
        MasterSystemSizes();
        GameBoyLayers();
        NesOnGameBoy();
    }

    // ---- SNES: four bands, one per BG (BG1 x 0-63, BG2 64-127, BG3 128-191, BG4 192-255), on a black backdrop

    private static ISnesPpuCore SnesBands(int mode, bool overscan)
    {
        var sp = SnesCores.CreatePpu("SFC");
        var v = sp.Vram; var cg = sp.Cgram;
        Array.Clear(v); Array.Clear(cg);
        // 2bpp tile 4 (words 32-39) and 4bpp tile 3 (words 48-63): every pixel colour 1. They share no word with each other or with tile 0 of either depth (all clear: see-through)
        for (int r = 0; r < 8; r++) { v[32 + r] = 0x00FF; v[48 + r] = 0x00FF; }
        // colour 1 of the palette each BG uses, in both modes (mode 0: BG k's palettes at 32 k; mode 1: BG1/BG2 4bpp palette p at 16 p, BG3 2bpp palette p at 4 p)
        ushort white = 0x7FFF, light = 0x5294, dark = 0x294A;
        cg[1] = white; cg[33] = light; cg[64 + 4 + 1] = dark; cg[97] = white;      // mode 0: BG1 pal 0, BG2 pal 0 (32+1), BG3 pal 1 (64+4+1), BG4 pal 0 (96+1)
        cg[2 * 16 + 1] = light; cg[4 + 1] = dark;                                    // mode 1: BG2 palette 2 (33, the same entry), BG3 palette 1 (5)
        for (int bg = 0; bg < 4; bg++)
        {
            int map = 0x1000 + bg * 0x400;
            int pal = mode == 0 ? (bg == 2 ? 1 : 0) : bg switch { 1 => 2, 2 => 1, _ => 0 };
            int tile = mode == 1 && bg < 2 ? 3 : 4;      // mode 1: BG1 and BG2 are 4bpp
            for (int row = 0; row < 32; row++) for (int col = 0; col < 32; col++) v[map + row * 32 + col] = (ushort)(col >= bg * 8 && col < bg * 8 + 8 ? tile | pal << 10 : 0);
        }
        sp.InvalidateCaches();
        sp.WriteRegister(0x00, 0x0F); sp.WriteRegister(0x05, (byte)mode);
        sp.WriteRegister(0x07, 0x10); sp.WriteRegister(0x08, 0x14); sp.WriteRegister(0x09, 0x18); sp.WriteRegister(0x0A, 0x1C);
        sp.WriteRegister(0x0B, 0x00); sp.WriteRegister(0x0C, 0x00); sp.WriteRegister(0x2C, 0x0F); sp.WriteRegister(0x2D, 0x00);
        sp.WriteRegister(0x33, (byte)(overscan ? 0x04 : 0x00));
        (sp as SfcPpuAdapter)!.Inner.BeginFrame();       // the frame's height (224, or 239 with overscan) is set when a frame begins
        return sp;
    }

    private static void SnesLayers()
    {
        foreach (var chip in EnhancedChips)
        {
            string name = chip == "FIXS" ? "FIX enhanced" : "Game Boy enhanced";
            var down = new SnesToNes(chip);
            foreach (int mode in new[] { 0, 1 })
            {
                // the backdrop (CGRAM 0, black) as this chip draws it: the same scene with no tiles at all
                uint blackish = (uint)Backdrop(down, chip);
                var f = down.Render(SnesBands(mode, false));
                var e = down.Enhanced!.Layers;
                int Band(int k) => (int)(f.Argb[100 * f.Width + k * 64 + 32] & 0xFFFFFF);
                bool[] shown = Enumerable.Range(0, 4).Select(k => (uint)Band(k) != blackish).ToArray();
                bool want4 = mode == 0;
                Check($"SNES mode {mode} on {name}: the picture is the SNES's 256 x 224 ({f.Width} x {f.Height}), {(want4 ? "four" : "three")} layers ({e.LayersInUse}: {e.Describe()})",
                    f.GuestSize && f.Width == 256 && f.Height == 224 && e.LayersInUse == (want4 ? 4 : 3), $"{f.Width}x{f.Height} guest {f.GuestSize}; layers {e.LayersInUse}");
                Check(want4 ? $"SNES mode 0 on {name}: all four BGs are drawn (each band shows its layer)" : $"SNES mode 1 on {name}: BG1-BG3 are drawn and BG4 is not (mode 1 has no BG4: the same video memory's fourth band is the backdrop - the mode decides the layers)",
                    want4 ? shown.All(s => s) : shown[0] && shown[1] && shown[2] && !shown[3], string.Join(", ", Enumerable.Range(0, 4).Select(k => $"band {k + 1} {Band(k):X6}")) + $"; backdrop {blackish:X6}");
            }
            int back = Backdrop(down, chip);
            var tall = down.Render(SnesBands(1, true));
            int tw = tall.Width, th = tall.Height, tallRow = (int)(tall.Argb[236 * tall.Width + 32] & 0xFFFFFF);
            Check($"SNES overscan (239 lines) on {name}: the picture is 256 x 239 and its lines past 224 are drawn (BG1 at line 236)", tw == 256 && th == 239 && tallRow != back, $"{tw}x{th}, line 236 {tallRow:X6}, backdrop {back:X6}");
        }
        // negative control: a stock chip draws a NES picture (256 x 240), not the SNES's own
        var stock = new SnesToNes("FIX").Render(SnesBands(0, true));
        Check("negative control: on the stock FIX chip the same overscan scene is a NES picture (256 x 240, not at the SNES's size, no layers)", !stock.GuestSize && stock.Width == 256 && stock.Height == 240 && new SnesToNes("FIX").Enhanced == null, $"{stock.Width}x{stock.Height} guest {stock.GuestSize}");
    }

    /// <summary>The colour an enhanced chip draws the SNES scenes' black backdrop in.</summary>
    private static int Backdrop(SnesToNes down, string chip)
    {
        var sp = SnesBands(0, false);
        for (int bg = 0; bg < 4; bg++) for (int i = 0; i < 1024; i++) sp.Vram[0x1000 + bg * 0x400 + i] = 0;       // no tiles at all: the backdrop only
        sp.InvalidateCaches();
        var f = down.Render(sp);
        return (int)(f.Argb[100 * f.Width + 100] & 0xFFFFFF);
    }

    // ---- the Genesis: H40 / H32, V30 / V28; column 0 blue, column 39 (H40) or 31 (H32) green, the middle column blue, the rest backdrop

    private static SegaScenes.MdRig MdEdges(string ppu, bool h40, bool v30)
    {
        var r = new SegaScenes.MdRig(ppu); r.Color(0, 0); r.Color(1, 0x00E); r.Color(2, 0x0E0); r.Tile(1, 1); r.Tile(2, 2);
        int last = h40 ? 39 : 31;
        for (int row = 0; row < 32; row++) { r.Cell(SegaScenes.MdRig.PlaneA, 64, row, 0, 1); r.Cell(SegaScenes.MdRig.PlaneA, 64, row, last, 2); r.Cell(SegaScenes.MdRig.PlaneA, 64, row, 20, 1); }
        r.Regs(0x04, v30 ? 0x4C : 0x44, 0x30, 0x2C, 0x07, 0x7C, 0, 0x00, 0, 0, 0xFF, 0x00, h40 ? 0x81 : 0x00, 0x3F, 0, 0x02, 0x01, 0, 0);
        r.Go();
        return r;
    }

    private static void GenesisSizes()
    {
        foreach (var chip in EnhancedChips)
        {
            string name = chip == "FIXS" ? "FIX enhanced" : "Game Boy enhanced";
            foreach (var (h40, v30) in new[] { (true, true), (true, false), (false, false), (false, true) })
            {
                var r = MdEdges("NES:" + chip, h40, v30); var w = r.Wrap;
                int W = h40 ? 320 : 256, H = v30 ? 240 : 224, last = h40 ? 39 : 31;
                uint P(int x, int y) => w.Frame[y * w.FrameWidth + x] & 0xFFFFFF;
                uint bg = P(100, 100);
                bool edges = w.FrameWidth == W && w.FrameHeight == H && P(3, 50) != bg && P(last * 8 + 3, 50) != bg && P(3, H - 3) != bg && P(last * 8 + 3, H - 3) != bg && P(3, 50) != P(last * 8 + 3, 50);
                var e = w.Enhanced?.Layers;
                Check($"Genesis {(h40 ? "H40" : "H32")} {(v30 ? "V30" : "V28")} on {name}: the picture is {W} x {H} (shown {w.DisplayWidth} wide), both edge columns and the last lines drawn, {e?.LayersInUse} layer(s) ({e?.Describe()})",
                    edges && w.GuestSize && w.DisplayWidth == 320 && w.Region == (0, 0, W, H), $"{w.FrameWidth}x{w.FrameHeight} display {w.DisplayWidth} region {w.Region}; col 0 {P(3, 50):X6} last {P(last * 8 + 3, 50):X6} back {bg:X6}");
            }
        }
        // negative control: the stock chips crop a 320 x 240 picture to the NES's 256 x 224 (the edge columns gone, counted)
        foreach (var chip in new[] { "FIX", "DMG" })
        {
            var r = MdEdges("NES:" + chip, true, true); var w = r.Wrap;
            Check($"negative control: Genesis H40 V30 on the stock {chip} chip is a NES picture ({w.FrameWidth} x {w.FrameHeight}), its 64 edge columns and 16 lines counted as cut", !w.GuestSize && w.FrameWidth == 256 && w.FrameHeight == 240
                && w.Counters.Total("columns cropped from the 320-wide picture") >= 64 && w.Counters.Total("lines cropped from the 240-line picture") >= 16, w.Counters.ToString());
        }
    }

    // ---- the Master System and the Game Gear

    private static void MasterSystemSizes()
    {
        foreach (var chip in EnhancedChips)
        {
            string name = chip == "FIXS" ? "FIX enhanced" : "Game Boy enhanced";
            foreach (bool tall in new[] { false, true })
            {
                var r = new SegaScenes.SmsRig("NES:" + chip); r.Cram(0, 0); r.Cram(1, 0x03); r.Tile(1, 1);
                if (tall) { r.Regs(0x06, 0x50, 0x0E, 0, 0, 0x7E, 0xFB, 0x00, 0, 0, 0xFF); for (int row = 0; row < 28; row++) r.V.Vram[0x3700 + row * 64 + 0] = 1; }       // the 224-line mode: name table at $3700
                else for (int row = 0; row < 24; row++) r.Cell(row, 0, 1);
                r.Frames();
                var w = r.Wrap; int H = tall ? 224 : 192;
                uint P(int x, int y) => w.Frame[y * w.FrameWidth + x] & 0xFFFFFF;
                Check($"Master System {H} lines on {name}: the picture is 256 x {H} (its last line drawn: column 0 at line {H - 2}), one background layer ({w.Enhanced?.Layers.Describe()})",
                    w.GuestSize && w.FrameWidth == 256 && w.FrameHeight == H && P(3, H - 2) != P(100, H - 2) && w.Enhanced!.Layers.LayersInUse >= 1, $"{w.FrameWidth}x{w.FrameHeight}; {P(3, H - 2):X6} vs {P(100, H - 2):X6}");
            }
            var g = new SegaScenes.SmsRig("NES:" + chip, gg: true); g.Gg(0, 0x000); g.Gg(1, 0x00F); g.Tile(1, 1);
            for (int row = 0; row < 24; row++) for (int col = 0; col < 32; col++) if (col == 6 || col == 25) g.Cell(row, col, 1);      // the LCD's first and last columns (VDP x 48 and 200)
            g.Frames();
            var gw = g.Wrap;
            uint Q(int x, int y) => gw.Frame[y * gw.FrameWidth + x] & 0xFFFFFF;
            Check($"Game Gear on {name}: the picture is the LCD's 160 x 144, its first and last columns its own (VDP columns 6 and 25)", gw.GuestSize && gw.FrameWidth == 160 && gw.FrameHeight == 144 && Q(3, 70) != Q(80, 70) && Q(156, 70) != Q(80, 70),
                $"{gw.FrameWidth}x{gw.FrameHeight}; {Q(3, 70):X6} {Q(156, 70):X6} vs {Q(80, 70):X6}");
        }
        var s = new SegaScenes.SmsRig("NES:FIX"); s.Frames();
        Check("negative control: a Master System game on the stock FIX chip is a NES picture (256 x 240)", !s.Wrap.GuestSize && s.Wrap.FrameWidth == 256 && s.Wrap.FrameHeight == 240, $"{s.Wrap.FrameWidth}x{s.Wrap.FrameHeight}");
    }

    // ---- a Game Boy game on FIX enhanced: background and window

    private static GbSession GbWindowScene(string ppu, bool window)
    {
        var rom = BrokenNes.Workshop.Corruption.CorruptConsoleChecks.GbRom(false);
        rom[0x150] = 0x18; rom[0x151] = 0xFE;
        var s = new GbSession(rom, ConsoleKind.GameBoy, "GB", ppu, "GB");
        var p = s.Board.Ppu;
        for (int r = 0; r < 8; r++) { p.Vram[16 + r * 2] = 0xFF; p.Vram[16 + r * 2 + 1] = 0x00; p.Vram[32 + r * 2] = 0xFF; p.Vram[32 + r * 2 + 1] = 0xFF; }   // tile 1 colour 1, tile 2 colour 3
        for (int i = 0; i < 0x400; i++) { p.Vram[0x1800 + i] = 1; p.Vram[0x1C00 + i] = 2; }       // the background map all tile 1, the window's all tile 2
        Array.Clear(p.Oam);
        p.Bgp = 0xE4; p.Lcdc = (byte)(0x91 | (window ? 0x60 : 0x00)); p.Wy = 72; p.Wx = 7 + 80;      // the window over the lower right quarter
        return s;
    }

    private static void GameBoyLayers()
    {
        foreach (bool window in new[] { true, false })
        {
            using var s = GbWindowScene("NES:FIXS", window);
            for (int f = 0; f < 3; f++) s.RunFrame();
            uint P(int x, int y) => s.Frame[y * s.FrameWidth + x] & 0xFFFFFF;
            var e = s.PictureBridge!.Enhanced!.Layers;
            if (window)
                Check($"Game Boy game on FIX enhanced: the picture is the Game Boy's 160 x 144 with its background and its window as two layers ({e.Describe()}); the window shows over the lower right, the background elsewhere",
                    s.FrameWidth == 160 && s.FrameHeight == 144 && e.LayersInUse == 2 && P(120, 110) != P(40, 40) && P(40, 110) == P(40, 40) && P(120, 40) == P(40, 40), $"{s.FrameWidth}x{s.FrameHeight}; layers {e.LayersInUse}; window {P(120, 110):X6} bg {P(40, 40):X6}");
            else
                Check($"negative control: the same game with the window off is one layer ({e.Describe()}) and the lower right is background", e.LayersInUse == 1 && P(120, 110) == P(40, 40), $"layers {e.LayersInUse}");
        }
        using var stock = GbWindowScene("NES:FIX", true);
        for (int f = 0; f < 3; f++) stock.RunFrame();
        Check("negative control: on the stock FIX chip the Game Boy game is a NES picture (256 x 240, the screen at (48, 48))", stock.FrameWidth == 256 && stock.FrameHeight == 240 && stock.PictureBridge!.Enhanced == null, $"{stock.FrameWidth}x{stock.FrameHeight}");
    }

    // ---- a NES game on Game Boy enhanced

    private static void NesOnGameBoy()
    {
        var p = new NesPicture();
        for (int r = 0; r < 8; r++) { p.ChrBg[16 + r] = 0xFF; p.ChrBg[32 + r] = 0xFF; p.ChrBg[32 + 8 + r] = 0xFF; }     // tile 1 colour 1 (black), tile 2 colour 3 (white)
        p.Pal[0] = 0x10; p.Pal[1] = 0x0F; p.Pal[2] = 0x00; p.Pal[3] = 0x30;
        for (int row = 10; row < 15; row++) { p.Nt0[row * 32 + 0] = 1; p.Nt0[row * 32 + 31] = 2; p.Nt1[row * 32 + 0] = 2; }
        Array.Fill(p.Oam, (byte)0xFF);
        var sink = new NesPictureSink("DMGS");
        uint[] Draw(int fineX) { p.FineX = fineX; sink.Present(p); var rgba = sink.Present(p); var a = new uint[256 * 240]; for (int i = 0; i < a.Length; i++) a[i] = (uint)(rgba[i * 4] << 16 | rgba[i * 4 + 1] << 8 | rgba[i * 4 + 2]); return a; }
        var flat = Draw(0);
        var e = sink.Enhanced!.Layers;
        uint At(uint[] a, int x, int y) => a[y * 256 + x];
        Check($"NES game on Game Boy enhanced: the picture is the NES's 256 x 240 as one layer ({e.Describe()}); column 0 (dark) and column 31 (light) at the two edges",
            e.Width == 256 && e.Height == 240 && e.LayersInUse == 1 && At(flat, 3, 100) != At(flat, 252, 100) && GbColour.Luma(At(flat, 3, 100)) < GbColour.Luma(At(flat, 252, 100)), $"{e.Width}x{e.Height}, layers {e.LayersInUse}; x 3 {At(flat, 3, 100):X6}, x 252 {At(flat, 252, 100):X6}");
        var moved = Draw(4);
        // fine scroll 4: x 244-251 is column 31 of the first nametable, x 252-255 column 0 of the NEXT one (light); a wrap inside the first nametable would show its column 0 (dark) there
        Check("NES game on Game Boy enhanced, scrolled 4 pixels: the right edge is the next nametable's column 0 (light), not a wrap to the first nametable's (dark)", At(moved, 253, 100) == At(flat, 252, 100) && At(moved, 246, 100) == At(flat, 252, 100), $"x 253 {At(moved, 253, 100):X6}, x 246 {At(moved, 246, 100):X6}, light {At(flat, 252, 100):X6}");
    }

    // =========================================================================================================== sound
    public static void Sound()
    {
        VoiceLists();
        SnesBanks();
        HubBanks();
        RealGenesisGame();
        Speed();
    }

    /// <summary>A real game (Sonic 1, when its ROM is on this machine): into Green Hill, its music on the enhanced chip opens the banks it needs and no voice goes without a channel; on the stock chip voices are dropped and counted.</summary>
    private static void RealGenesisGame()
    {
        string rom = Environment.GetEnvironmentVariable("BROKENNES_TEST_SONIC") is { Length: > 0 } e ? e : @"X:\EMULATION\Sega Genesis\Sonic the Hedgehog (JUE) [!].bin";
        if (!System.IO.File.Exists(rom)) { Console.WriteLine($"  NOT RUN  Sonic 1 on the enhanced sound chip: no ROM at {rom} (BROKENNES_TEST_SONIC)"); return; }
        var script = PictureBridgeCli.ParseInput("450:Start,460:");
        (int open, long without, long syncs) Run(string apu)
        {
            using var s = new MdSession(System.IO.File.ReadAllBytes(rom), "M68K", "MD", apu);
            var buf = new short[65536]; var held = PadButtons.None;
            for (int f = 0; f < 1500; f++) { if (script.TryGetValue(f, out var h)) held = h; s.SetPad(0, held); s.RunFrame(); while (s.ReadSamples(buf) > 0) { } }
            var u = (MdSoundOnNes)s.Board.Sound;
            return (u.Writer.BanksOpen, u.VoicesWithoutChannel, u.Syncs);
        }
        var fixs = Run("NES:FIXS"); var fix = Run("NES:FIX");
        Check($"Sonic 1 into Green Hill on FIXS (enhanced): the music opens {fixs.open} of the 5 banks and no voice goes without a channel ({fixs.without} voice-syncs of {fixs.syncs} syncs)", fixs.open > 2 && fixs.without == 0, $"{fixs.open} banks open, {fixs.without} without a channel");
        Check($"negative control: the same on the stock FIX: one bank, {fix.without} voice-syncs without a channel, counted", fix.open == 1 && fix.without > 0, $"{fix.open} / {fix.without}");
    }

    private static void VoiceLists()
    {
        var buf = new HubVoice[64];
        // the SNES: eight voices (the bridge's own list of the DSP's)
        var saved = MixConfig.NesBackApu;
        try
        {
            MixConfig.NesBackApu = "FIXS";
            var snes = new NesApuOnSnes(); PokeSnesVoices(snes, 8, 0);
            int n = snes.DescribeVoices(buf);
            Check($"the SNES's voice list is its 8 DSP voices, with the pan of their volumes (voice 1 left only: {buf[0].Pan:+0.0;-0.0}, voice 2 right only: {buf[1].Pan:+0.0;-0.0}, voice 3 both: {buf[2].Pan:+0.0;-0.0})", n == 8 && buf[0].Pan == -1f && buf[1].Pan == 1f && buf[2].Pan == 0f && buf.Take(8).All(v => v.Audible), $"{n} voices");
        }
        finally { MixConfig.NesBackApu = saved; }
        // the Genesis: 11 (FM 1-6, the DAC, PSG 1-3, the PSG noise); the YM2612's pan bits
        var ym = new Ym2612(false);
        ym.Write(0, 0xB4, 0); ym.Write(1, 0x40, 0);       // FM 1: right only
        ym.Write(2, 0xB6, 0); ym.Write(3, 0x80, 0);       // FM 6 (and the DAC): left only
        int m = ym.DescribeVoices(buf);
        Check($"the YM2612 says where its voices sit: FM 1 right only ({buf[0].Pan:+0.0;-0.0}), FM 2 both ({buf[1].Pan:+0.0;-0.0}), FM 6 left only ({buf[5].Pan:+0.0;-0.0}), the DAC with FM 6 ({buf[6].Pan:+0.0;-0.0})", buf[0].Pan == 1f && buf[1].Pan == 0f && buf[5].Pan == -1f && buf[6].Pan == -1f && m == 7, $"{m} voices");
        var md = new MdSession(SegaScenesRom(), "M68K", "MD", "NES:FIXS");
        int mdN = ((IHubVoiceSource)md.Board.Sound).DescribeVoices(buf);
        Check($"the Genesis's voice list is its 11 voices (FM 1-6, the DAC, 3 PSG tones, the PSG noise)", mdN == 11, $"{mdN}");
        // the Master System: the PSG's 4, with the FM chip 18; the Game Gear's stereo port
        var psg = new Sn76489(3579545, 44100); psg.WriteStereo(0x12);          // tone 1 right only (bit 0... noise left only: bits 7-4 left, 3-0 right) -> 0x12 = tone 2 right? see below
        int pn = psg.DescribeVoices(buf);
        // 0x12: left enables bit 4 (tone 1); right enables bit 1 (tone 2): tone 1 left only, tone 2 right only, tone 3 and the noise on neither (silent)
        Check($"the PSG's voice list is its 4 voices, with the Game Gear's stereo port as the pan (tone 1 left only {buf[0].Pan:+0.0;-0.0}, tone 2 right only {buf[1].Pan:+0.0;-0.0})", pn == 4 && buf[0].Pan == -1f && buf[1].Pan == 1f, $"{pn}");
        var smsFm = (IHubVoiceSource)SmsSoundFactories.Create("NES-FM:FIXS", SmsRequest());
        Check("the Master System FM unit's voice list is its 18 voices (the PSG's 4, the YM2413's 9 melodic channels and 5 rhythm)", smsFm.DescribeVoices(buf) == 18);
        // the Game Boy: 4, with NR51's pan
        var gb = new GbApuOnNes(GbModel.Dmg);
        gb.WriteRegister(0x26, 0x80); gb.WriteRegister(0x25, 0x12);        // NR51: pulse 1 left (bit 4), pulse 2 right (bit 1)
        int gn = gb.DescribeVoices(buf);
        Check($"the Game Boy's voice list is its 4 voices, with NR51 as the pan (pulse 1 left only {buf[0].Pan:+0.0;-0.0}, pulse 2 right only {buf[1].Pan:+0.0;-0.0})", gn == 4 && buf[0].Pan == -1f && buf[1].Pan == 1f, $"{gn}");
    }

    private static SmsSoundRequest SmsRequest() => new(new SegaMachine(SegaTimeline.MasterSystem(SegaRegion.Ntsc, ConsoleKind.MasterSystem), GenesisModel.Model1, 44100), ConsoleKind.MasterSystem, PsgVariant.Sega);

    private static byte[] SegaScenesRom()
    {
        var rom = new byte[0x20000];
        void L(int at, uint v) { rom[at] = (byte)(v >> 24); rom[at + 1] = (byte)(v >> 16); rom[at + 2] = (byte)(v >> 8); rom[at + 3] = (byte)v; }
        L(0, 0xFFFE00); L(4, 0x200); for (int v = 2; v < 64; v++) L(v * 4, 0x200);
        System.Text.Encoding.ASCII.GetBytes("SEGA MEGA DRIVE ").CopyTo(rom, 0x100);
        rom[0x200] = 0x60; rom[0x201] = 0xFE;
        return rom;
    }

    /// <summary>Pokes <paramref name="voices"/> sounding DSP voices into the bridge's S-DSP: a looped square sample (period 16), voices 0 .. tonal-1 at 220 Hz and up, the last <paramref name="noises"/> of them noise.
    /// Voice 1 left only, voice 2 right only, the others both.</summary>
    private static void PokeSnesVoices(NesApuOnSnes bridge, int voices, int noises)
    {
        var r = bridge.Probe.DspRegs; var ar = bridge.Probe.Aram;
        Array.Clear(r);
        r[0x5D] = 0x02;                                                                          // the sample directory at $0200
        ar[0x200] = 0x00; ar[0x201] = 0x03; ar[0x202] = 0x00; ar[0x203] = 0x03;                  // sample 0: start and loop at $0300
        for (int b = 0; b < 2; b++)
        {
            ar[0x300 + b * 9] = (byte)(0x80 | (b == 1 ? 0x03 : 0x00));                           // shift 8; the second block loops and ends
            for (int i = 0; i < 8; i++) ar[0x301 + b * 9 + i] = (byte)(i < 4 ? 0x77 : 0x99);    // 8 samples at +7, 8 at -7: a square of 16 samples
        }
        for (int v = 0; v < voices; v++)
        {
            int b = v * 16;
            r[b] = (byte)(v == 1 ? 0 : 0x7F); r[b + 1] = (byte)(v == 0 ? 0 : 0x7F);
            int pitch = (int)Math.Round(220 * Math.Pow(2, v / 4.0) * 16 * 4096 / 32000.0);
            r[b + 2] = (byte)pitch; r[b + 3] = (byte)(pitch >> 8); r[b + 4] = 0; r[b + 8] = 0x7F;
            if (v >= voices - noises) r[0x3D] |= (byte)(1 << v);
        }
        r[0x6C] = 0x10;
    }

    private static void SnesBanks()
    {
        var saved = MixConfig.NesBackApu;
        try
        {
            foreach (var (back, enhanced) in new[] { ("FIXS", true), ("DMGS", true), ("FIX", false), ("DMG", false) })
            {
                MixConfig.NesBackApu = back;
                var b = new NesApuOnSnes();
                int start = b.Banks;
                PokeSnesVoices(b, 8, 1);       // seven tonal voices and a noise: four banks' pulses and one noise channel
                b.Sync(); b.Sync();
                long dropped = b.VoicesDroppedTotal;
                if (enhanced)
                    Check($"SNES sound on {back} (enhanced): two banks to start ({start}), grown to the four that 7 tonal voices and a noise need ({b.Banks}), every voice on a channel ({b.VoicesMapped} mapped, {dropped} dropped)",
                        start == 2 && b.Banks == 4 && b.VoicesMapped == 8 && dropped == 0 && ((IApuExtChannels)b.Back).ExtBanks == 3, $"banks {b.Banks}, mapped {b.VoicesMapped}, dropped {dropped}");
                else
                    Check($"negative control: SNES sound on the stock {back} chip: one bank, the loudest 3 tonal voices and the noise played, 4 dropped and counted each sync", start == 1 && b.Banks == 1 && b.VoicesMapped == 4 && dropped == 8, $"banks {b.Banks}, mapped {b.VoicesMapped}, dropped {dropped}");
            }
            // the same as before for a game of up to 4 tonal voices and 2 noises: two banks, the old layout (an enhanced chip only grows when it must)
            MixConfig.NesBackApu = "FIXS";
            var small = new NesApuOnSnes(); PokeSnesVoices(small, 6, 2); small.Sync();
            Check("SNES sound on FIXS with 4 tonal voices and 2 noises stays at its two banks (the layout of before: nothing grows that does not have to)", small.Banks == 2 && small.VoicesDroppedTotal == 0, $"banks {small.Banks}");
        }
        finally { MixConfig.NesBackApu = saved; }
    }

    /// <summary>The hub bridges (Genesis, Master System FM and PSG) through the real enhanced chips: the writer's banks, then every voice sounding at once on the replayer.</summary>
    private static void HubBanks()
    {
        var machine = new SegaMachine(SegaTimeline.Genesis(SegaRegion.Ntsc), GenesisModel.Model1, 44100);
        foreach (var (chip, enhanced) in new[] { ("FIXS", true), ("DMGS", true), ("FIX", false), ("DMG", false) })
        {
            var md = (MdSoundOnNes)GenesisSoundFactories.Create("NES:" + chip, machine);
            var fm = (SmsFmSoundOnNes)SmsSoundFactories.Create("NES-FM:" + chip, SmsRequest());
            var psg = (SmsSoundOnNes)SmsSoundFactories.Create("NES:" + chip, SmsRequest());
            Check($"{(enhanced ? "" : "negative control: ")}the writers' banks on {chip}{(enhanced ? " (enhanced)" : " (stock)")}: Genesis {md.Writer.Banks}, Master System FM {fm.Writer.Banks}, Master System PSG {psg.Writer.Banks}",
                enhanced ? md.Writer.Banks == 5 && fm.Writer.Banks == 7 && psg.Writer.Banks == 2 : md.Writer.Banks == 1 && fm.Writer.Banks == 1 && psg.Writer.Banks == 1, $"{md.Writer.Banks}/{fm.Writer.Banks}/{psg.Writer.Banks}");
        }
        // every voice of the Genesis sounding at once, on the replayer and the real chip: none without a channel on the enhanced chips, the stock ones drop and count
        foreach (var (chip, enhanced) in new[] { ("FIXS", true), ("DMGS", true), ("FIX", false) })
        {
            var apu = new NesApuHost(chip).Apu;
            var writer = new NesVoiceWriter(NesRegisterPorts.For(apu), NesVoiceWriter.BanksForTonal(9, 2));
            var rp = new HubReplayer(writer, 11, i => i == 6 || i == 10, APU_MD.TriangleRms, LowNotePolicy.OctaveUp);
            for (int sync = 0; sync < 20; sync++)
            {
                for (int i = 0; i < 11; i++) { rp.Wanted[i] = true; rp.VoiceHz[i] = 110 * Math.Pow(2, i / 3.0); rp.TargetRms[i] = APU_MD.PulseRms(2, 8 + i % 6); rp.ShortNoise[i] = false; }
                rp.Replay();
            }
            for (int c = 0; c < 29780 * 2; c += 32) apu.Step(32);          // as the bridges step it (the Game Boy chip's lanes sync between steps)
            var s = apu.GetAudioSamples(); double rms = Math.Sqrt(s.Select(x => (double)x * x).DefaultIfEmpty(0).Average());
            string inUse = apu is APU_FIXS fx ? $", {fx.ExtBanksInUse} extra banks written" : apu is APU_DMGS dg ? $", {dg.ExtBanksInUse} extra banks written" : "";
            if (enhanced)
                Check($"eleven Genesis voices at once on {chip} (enhanced, {writer.Banks} banks{inUse}): every one on a channel ({rp.VoicesWithoutChannel} voice-syncs without one, {rp.VoicesStolen} stolen), the chip sounds (rms {rms:F3})",
                    writer.Banks == 5 && rp.VoicesWithoutChannel == 0 && rp.VoicesStolen == 0 && rms > 0.01, $"{rp.VoicesWithoutChannel} / {rp.VoicesStolen}");
            else
                Check($"negative control: eleven Genesis voices at once on the stock {chip} chip: {rp.VoicesWithoutChannel} voice-syncs without a channel, counted", writer.Banks == 1 && rp.VoicesWithoutChannel > 100, $"{rp.VoicesWithoutChannel}");
        }
    }

    /// <summary>What the extra banks cost: a silent Genesis game's frames on the stock unit, the stock NES chip and the enhanced one (information, not a check: the machine is shared).</summary>
    private static void Speed()
    {
        var rom = SegaScenesRom();
        string Rate(string apu)
        {
            using var s = new MdSession(rom, "M68K", "MD", apu);
            var buf = new short[65536];
            for (int f = 0; f < 30; f++) { s.RunFrame(); while (s.ReadSamples(buf) > 0) { } }
            var sw = Stopwatch.StartNew(); int frames = 0;
            while (sw.ElapsedMilliseconds < 1500) { s.RunFrame(); while (s.ReadSamples(buf) > 0) { } frames++; }
            return $"{apu} {frames / sw.Elapsed.TotalSeconds:F0} fps";
        }
        Console.WriteLine($"  INFO  Genesis frames a second with the sound on: {Rate("MD")}, {Rate("NES:FIX")}, {Rate("NES:FIXS")} (5 banks), {Rate("NES:DMGS")} (5 banks)");
    }
}
