using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
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
/// WideMess milestone 7b (docs/projects/enhanced-cores.md section 6, docs/projects/widemess-spec.md section 8): the WideMess's picture and sound chips on the other consoles' menus (route <c>WMS:WMSS</c>, NES board <c>WMSS</c>),
/// by measurement, every check with a negative control or a deliberate fault it catches.
/// <list type="bullet">
/// <item><b>wmsmenu</b>: the route on every other console's picture and sound menus (and WMSS on the NES's) where the WideMess is offered, in the other consoles' section as WideMess (enhanced); none of it where the
/// WideMess is hidden (Lite and the FL plugin: <see cref="Consoles.WideMessEnabled"/> off), and a session refuses it there.</item>
/// <item><b>wms</b>: the widescreen picture, guest by guest, in synthetic scenes: 400 wide; the middle is the SNES enhanced chip's picture of the same frame, pixel for pixel; each layer's rule as its translator's width
/// says (a NES background with vertical mirroring shows the real neighbouring nametable, the same picture the NES shows scrolled there; with horizontal mirroring it repeats; a Game Boy's 256-pixel map, a Game Gear's VDP
/// line around its LCD, a Genesis plane and a SNES 64-tile map extend; a Master System map repeats; windows and HUD columns fill), sprites beyond the edges where they are real places; the counters. Deliberate faults
/// (<see cref="WmsWideChip.ForceRuleForTests"/>, <see cref="WmsWideChip.ForceSpritesBeyondForTests"/>) are caught by the same checks. Then the sound chip: every voice on a voice of its own (16-bit PCM first), the
/// pitch, the pan, every guest's bridge, a NES game on the NES board; the stock routes through the NES drop what it keeps.</item>
/// <item><b>wmsgames</b>: real games from the user's library on the widescreen chip in lockstep with the SNES enhanced chip: the middle identical every frame, the sides drawn as the rules say; their sound on the
/// WideMess sound chip with no voice dropped; 0 bytes allocated a frame on the new paths.</item>
/// </list>
/// </summary>
internal static class WideMessGuestTests
{
    private const string Route = "WMS:WMSS", Sfcs = "SNES:SFCS";

    private static uint C555(uint rgb) => PPU_SFCS.Argb555(PPU_SFCS.To555(rgb)) & 0xFFFFFF;
    private static uint NesColour(int v) => C555((uint)NesPalette.Rgb[v & 0x3F]);
    private static uint Px(uint[] a, int w, int x, int y) => a[y * w + x] & 0xFFFFFF;

    /// <summary>True when columns <paramref name="ax"/>.. of picture a equal columns <paramref name="bx"/>.. of picture b over <paramref name="n"/> columns and every row.</summary>
    private static int ColumnsDiffer(uint[] a, int aw, int ax, uint[] b, int bw, int bx, int n, int h)
    {
        int bad = 0;
        for (int y = 0; y < h; y++) for (int x = 0; x < n; x++) if (Px(a, aw, ax + x, y) != Px(b, bw, bx + x, y)) bad++;
        return bad;
    }

    // =========================================================================================================================================== menus
    public static void Menus()
    {
        var guests = new[] { ConsoleKind.Snes, ConsoleKind.GameBoy, ConsoleKind.GameBoyColor, ConsoleKind.MasterSystem, ConsoleKind.GameGear, ConsoleKind.Genesis };
        bool was = Consoles.WideMessEnabled;
        try
        {
            Consoles.WideMessEnabled = true;
            foreach (var c in guests)
                foreach (var slot in new[] { CoreSlot.Ppu, CoreSlot.Apu })
                {
                    var menu = CoreCatalog.Options(c, slot);
                    var o = menu.FirstOrDefault(m => m.Id == Route);
                    string? why = OrderProblem(c, slot, menu);
                    Check($"{Consoles.DisplayName(c)} {slot}: offers {Route} as WideMess (enhanced) in the other consoles' section, last of them, and the menu keeps its order",
                        o != null && o.Grade == CoreGrade.Enhanced && o.Section == CoreSection.OtherConsoles && o.Family == "WideMess" && o.Label.StartsWith("WideMess (enhanced)") && why == null
                        && menu.Where(m => m.Section == CoreSection.OtherConsoles).Last().Id == Route, o?.Label ?? why ?? "not offered");
                }
            foreach (var slot in new[] { CoreSlot.Ppu, CoreSlot.Apu })
            {
                var o = CoreCatalog.Options(ConsoleKind.Nes, slot).FirstOrDefault(m => m.Id == "WMSS");
                Check($"NES {slot}: offers the NES board's WMSS as WideMess (enhanced) in the other consoles' section", o != null && o.Grade == CoreGrade.Enhanced && o.Section == CoreSection.OtherConsoles && o.Label.StartsWith("WideMess (enhanced)"), o?.Label ?? "not offered");
            }
            Check("no console but the NES offers the NES board's WMSS through the NES (NES:WMSS), and the WideMess's own menus offer no route to its own chips", guests.All(c => new[] { CoreSlot.Ppu, CoreSlot.Apu }.All(s => !CoreCatalog.Options(c, s).Any(m => m.Id.Equals("NES:WMSS", StringComparison.OrdinalIgnoreCase))))
                && new[] { CoreSlot.Ppu, CoreSlot.Apu }.All(s => !CoreCatalog.Options(ConsoleKind.WideMess, s).Any(m => m.Id == Route)));
            Check("the FL plugin's chip list cannot take the NES board's WMSS: its family is the WideMess's, which the plugin lists no chip of (its chip indexes stay as they are)", CoreCatalog.NesFamilyOf(CoreSlot.Apu, "WMSS") == "WideMess");
            Check($"the defaults did not move: every guest console's first picture and sound entry is its own stock core", guests.All(c => CoreCatalog.Default(c, CoreSlot.Ppu) == CoreCatalog.Options(c, CoreSlot.Ppu)[0].Id && CoreCatalog.Options(c, CoreSlot.Ppu)[0].Section == CoreSection.Own && CoreCatalog.Options(c, CoreSlot.Apu)[0].Section == CoreSection.Own));

            // negative control: the WideMess hidden (Lite and the FL plugin today)
            Consoles.WideMessEnabled = false;
            bool none = guests.Concat(new[] { ConsoleKind.Nes }).All(c => new[] { CoreSlot.Ppu, CoreSlot.Apu }.All(s => !CoreCatalog.Options(c, s).Any(m => m.Id == Route || m.Id == "WMSS")));
            Check("negative control: with the WideMess hidden (Lite, the FL plugin) no menu offers WMS:WMSS or WMSS, and the route is no route", none && !CoreCatalog.IsDirectRoute(Route));
            using (var gb = GbScene(Route))     // made while hidden: the session takes the picture id as given, but a swap goes through the menu
                Check("negative control: with the WideMess hidden a running game refuses a swap to WMS:WMSS (picture and sound)", !gb.TrySwapCore(CoreSlot.Ppu, Route) && !gb.TrySwapCore(CoreSlot.Apu, Route));
        }
        finally { Consoles.WideMessEnabled = was; }
    }

    // =========================================================================================================================================== the picture
    public static void Pictures()
    {
        try
        {
            NesGuest();
            GameBoyGuest();
            MasterSystemGuest();
            GameGearGuest();
            GenesisGuest();
            SnesGuest();
        }
        finally { WmsWideChip.ForceRuleForTests = null; WmsWideChip.ForceSpritesBeyondForTests = null; }
    }

    // ---- NES: vertical mirroring shows the neighbouring nametable
    /// <summary>Nametable 0 all red (tile 1), nametable 1 all white (tile 2) but its first and last columns green (tile 3); a sprite at x 252 (its right half past the NES's edge).</summary>
    private static NesPicture NesNeighbours(bool sameTables, int scrollX = 0, bool sprite = false)
    {
        var p = new NesPicture();
        for (int r = 0; r < 8; r++) { p.ChrBg[16 + r] = 0xFF; p.ChrBg[32 + r] = 0xFF; p.ChrBg[32 + 8 + r] = 0xFF; p.ChrBg[48 + 8 + r] = 0xFF; p.ChrObj[16 + r] = 0xFF; p.ChrObj[16 + 8 + r] = 0xFF; }
        p.Pal[0] = 0x0F; p.Pal[1] = 0x16; p.Pal[2] = 0x2A; p.Pal[3] = 0x30;      // backdrop black; colour 1 red, 2 green, 3 white
        p.Pal[17] = 0x12; p.Pal[18] = 0x12; p.Pal[19] = 0x12;                    // sprite palette 0: blue
        for (int i = 0; i < 960; i++) { p.Nt0[i] = 1; p.Nt1[i] = (byte)((i & 31) == 0 || (i & 31) == 31 ? 3 : 2); }
        if (sameTables) Array.Copy(p.Nt0, p.Nt1, 1024);
        p.FineX = scrollX;
        Array.Fill(p.Oam, (byte)0xFF);
        if (sprite) { p.Oam[0] = 99; p.Oam[1] = 1; p.Oam[2] = 0; p.Oam[3] = 252; }
        return p;
    }

    private static Cartridge Cart(NesPictureSink s) => ((Bus)typeof(NesPictureSink).GetField("bus", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(s)!).cartridge;

    private static uint[] NesRgba(byte[] rgba) { var a = new uint[256 * 240]; for (int i = 0; i < a.Length; i++) a[i] = 0xFF000000u | (uint)(rgba[i * 4] << 16 | rgba[i * 4 + 1] << 8 | rgba[i * 4 + 2]); return a; }

    private static (PPU_WMSS ppu, uint[] centre) NesWide(NesPicture p, bool horizontal)
    {
        var sink = new NesPictureSink("WMSS");
        if (horizontal) Cart(sink).SetMirroring(Mirroring.Horizontal);
        sink.Present(p); var rgba = sink.Present(p);
        return ((PPU_WMSS)sink.Ppu, NesRgba(rgba));
    }

    private static uint[] NesOn(string id, NesPicture p, bool horizontal = false)
    {
        var sink = new NesPictureSink(id);
        if (horizontal) Cart(sink).SetMirroring(Mirroring.Horizontal);
        sink.Present(p); return NesRgba(sink.Present(p));
    }

    private static void NesGuest()
    {
        uint red = NesColour(0x16), green = NesColour(0x2A), white = NesColour(0x30), blue = NesColour(0x12);
        var (ppu, centre) = NesWide(NesNeighbours(false, sprite: true), false);
        var w = ppu.Wide; var pic = ppu.WidePicture; int W = ppu.WideWidth, H = ppu.WideHeight;
        var sness = NesOn("SNESS", NesNeighbours(false, sprite: true));
        int midDiff = ColumnsDiffer(pic, W, 72, sness, 256, 0, 256, 240), frameDiff = ColumnsDiffer(centre, 256, 0, sness, 256, 0, 256, 240);
        Check($"NES game (vertical mirroring) on the NES board's WMSS: 400 x 240 ({W} x {H}), 72 columns a side; the middle is the SNES enhanced chip's picture (SNESS) pixel for pixel ({midDiff} differ), and the NES's own 256 x 240 frame is that middle ({frameDiff} differ)",
            W == 400 && H == 240 && w.Side == 72 && midDiff == 0 && frameDiff == 0, w.Describe());
        Check($"... the background's rule is extend (its translator reports 512: the neighbouring nametable): {w.DescribeRules()}", w.RuleOf(0) == WideRule.Extend && w.Layers.Layers[0].RealWidth == 512 && !w.Unreported(0));
        bool right = Px(pic, W, 330, 50) == green && Px(pic, W, 345, 50) == white, left = Px(pic, W, 66, 50) == green && Px(pic, W, 20, 50) == white;
        Check($"... the sides show the real neighbouring nametable: right of the screen its first column (green) then white, left of the screen its last column (green) then white, the screen itself red",
            right && left && Px(pic, W, 200, 50) == red, $"x330 {Px(pic, W, 330, 50):X6} x345 {Px(pic, W, 345, 50):X6} x66 {Px(pic, W, 66, 50):X6} x20 {Px(pic, W, 20, 50):X6} x200 {Px(pic, W, 200, 50):X6}; green {green:X6} white {white:X6} red {red:X6}");
        // the right side is what the NES itself shows when it scrolls 72 pixels on (an independent picture: the SNES enhanced chip at the NES's own size)
        var (plainPpu, _) = NesWide(NesNeighbours(false), false);
        var scrolled = NesOn("SNESS", NesNeighbours(false, 72));
        int scrollDiff = ColumnsDiffer(plainPpu.WidePicture, W, 328, scrolled, 256, 184, 72, 240);
        Check($"... the right side is exactly what the game shows scrolled 72 pixels to the right (its columns 184-255 there; {scrollDiff} pixels differ)", scrollDiff == 0);
        bool spr = Enumerable.Range(328, 4).All(x => Px(pic, W, x, 102) == blue);
        Check($"... a sprite at x 252 shows its right half beyond the NES's edge (the NES's sprite positions past the right edge are real: {w.Layers.SpritesBeyondEdges})", spr && w.Layers.SpritesBeyondEdges, $"x328 {Px(pic, W, 328, 102):X6} blue {blue:X6}");
        Check($"... counted: side columns x lines extended {w.Extended} (144 a line), repeated {w.Repeated}, filled {w.Filled}", w.Extended == 144L * 240 && w.Repeated == 0 && w.Filled == 0 && w.UnreportedLines == 0);
        // horizontal mirroring: the right nametable is the left one: the screen repeats
        var (hp, _) = NesWide(NesNeighbours(true), true);
        var hw = hp.Wide; var hpic = hp.WidePicture;
        int rep = ColumnsDiffer(hpic, 400, 328, hpic, 400, 72, 72, 240) + ColumnsDiffer(hpic, 400, 0, hpic, 400, 256, 72, 240);
        Check($"NES game with horizontal mirroring on WMSS: the background repeats (its translator reports 256: {hw.DescribeRules()}), each side the screen's own columns ({rep} pixels differ), counted as repeated ({hw.Repeated})",
            hw.RuleOf(0) == WideRule.Repeat && hw.Layers.Layers[0].RealWidth == 256 && rep == 0 && hw.Repeated == 144L * 240 && hw.Extended == 0);
        // deliberate fault: a chip that repeated where the map is wider would hide the neighbouring nametable: the check above catches it
        WmsWideChip.ForceRuleForTests = WideRule.Repeat;
        var (fp, _) = NesWide(NesNeighbours(false), false);
        WmsWideChip.ForceRuleForTests = null;
        bool caught = Px(fp.WidePicture, 400, 330, 50) != green;
        Check("deliberate fault caught: the same vertical-mirroring scene on a chip forced to repeat shows the screen's red instead of the neighbouring nametable's green", caught, $"x330 {Px(fp.WidePicture, 400, 330, 50):X6}");
        var neg = NesOn("SNESS", NesNeighbours(false));
        Check("negative control: on the SNES enhanced chip (SNESS) the same NES game is the NES's own 256 x 240, nothing beside it", new NesPictureSink("SNESS").Enhanced!.PictureWidth == 256 && neg.Length == 256 * 240);
    }

    // ---- Game Boy: the background's 256-pixel map extends, the window fills
    private static GbSession GbScene(string ppu, byte scx = 0, bool window = false, bool hiddenSprite = false)
    {
        var rom = BrokenNes.Workshop.Corruption.CorruptConsoleChecks.GbRom(false);
        rom[0x150] = 0x18; rom[0x151] = 0xFE;
        var s = new GbSession(rom, ConsoleKind.GameBoy, "GB", ppu, "GB");
        var p = s.Board.Ppu;
        for (int r = 0; r < 8; r++)
        {
            p.Vram[16 + r * 2] = 0xFF; p.Vram[16 + r * 2 + 1] = 0x00;      // tile 1: colour 1
            p.Vram[32 + r * 2] = 0xFF; p.Vram[32 + r * 2 + 1] = 0xFF;      // tile 2: colour 3
            p.Vram[48 + r * 2] = 0x00; p.Vram[48 + r * 2 + 1] = 0xFF;      // tile 3: colour 2
        }
        for (int row = 0; row < 32; row++) for (int col = 0; col < 32; col++) p.Vram[0x1800 + row * 32 + col] = (byte)(col == 20 ? 3 : col > 20 ? 2 : 1);     // the screen's 20 columns tile 1, the off-screen 12 (from 20) a marker then tile 2
        for (int i = 0; i < 0x400; i++) p.Vram[0x1C00 + i] = 3;
        Array.Clear(p.Oam);
        if (hiddenSprite) { p.Oam[0] = 16 + 50; p.Oam[1] = 0; p.Oam[2] = 2; p.Oam[3] = 0; p.Oam[4] = 16 + 50; p.Oam[5] = 48; p.Oam[6] = 2; p.Oam[7] = 0; }   // X = 0: 8 pixels left of the screen, where a Game Boy hides a sprite; and one on the screen
        p.Bgp = 0xE4; p.Obp0 = 0xE4; p.Lcdc = (byte)(0x91 | (window ? 0x60 : 0x00) | (hiddenSprite ? 0x02 : 0)); p.Wy = 72; p.Wx = 7 + 80; p.Scx = scx;
        return s;
    }

    private static void Frames(IConsoleSession s, int n = 3) { for (int f = 0; f < n; f++) s.RunFrame(); }

    private static void GameBoyGuest()
    {
        using var s = GbScene(Route, window: true); Frames(s);
        using var r = GbScene(Sfcs, window: true); Frames(r);
        var w = (WmsWideChip)s.PictureBridge!.Enhanced!;
        int mid = ColumnsDiffer(s.Frame, s.FrameWidth, 120, r.Frame, r.FrameWidth, 0, 160, 144);
        Check($"Game Boy game on {Route}: 400 x 144 ({s.FrameWidth} x {s.FrameHeight}, shown {s.DisplayWidth} wide), 120 columns a side; the middle is the SNES enhanced chip's 160 x 144 pixel for pixel ({mid} differ)",
            s.FrameWidth == 400 && s.FrameHeight == 144 && s.DisplayWidth == 400 && w.Side == 120 && mid == 0, w.Describe());
        Check($"... the background extends (its map is 256 wide, the screen 160), the window fills (laid out on the screen): {w.DescribeRules()}", w.RuleOf(0) == WideRule.Extend && w.RuleOf(1) == WideRule.Fill && !w.Unreported(0) && !w.Unreported(1));
        // the sides above the window are the map around the screen: compare with the game scrolled there (an independent picture: the SNES enhanced chip, the Game Boy's own size)
        using var rr = GbScene(Sfcs, scx: 160); Frames(rr);
        using var rl = GbScene(Sfcs, scx: 136); Frames(rl);
        int rightDiff = ColumnsDiffer(s.Frame, 400, 280, rr.Frame, 160, 0, 120, 72), leftDiff = ColumnsDiffer(s.Frame, 400, 0, rl.Frame, 160, 0, 120, 72);
        Check($"... above the window each side is the background as the game shows it scrolled there: the right side = scrolled to 160 (the map's 96 columns past the screen, then its first 24 again), the left = scrolled to 136 ({rightDiff} and {leftDiff} pixels differ)", rightDiff == 0 && leftDiff == 0);
        uint backdrop = Px(r.Frame, 160, 3, 3) == Px(s.Frame, 400, 123, 3) ? Px(s.Frame, 400, 200, 0) : 0;
        uint bgAt = Px(s.Frame, 400, 10, 110), rightAt = Px(s.Frame, 400, 390, 110), winAt = Px(s.Frame, 400, 270, 110);
        Check($"... beside the window (lines 72-143, the window over the screen's right half) the right side is the backdrop (the window fills, the background is hidden where the window covers the edge) and the left side the background",
            rightAt != winAt && rightAt != Px(s.Frame, 400, 390, 40) && bgAt == Px(s.Frame, 400, 10, 40), $"right {rightAt:X6} window {winAt:X6} left {bgAt:X6} above {Px(s.Frame, 400, 10, 40):X6}");
        Check($"... counted: side columns x lines extended {w.Extended}, repeated {w.Repeated}, filled {w.Filled} (each line: 96 of the 240 extended, 144 repeated; the window's lines filled)",
            w.Extended == 96L * 144 && w.Repeated == 144L * 144 && w.Filled == 240L * 72 && w.UnreportedLines == 0);
        // deliberate fault: repeating would show the screen's own tile 1 where the map's marker is
        WmsWideChip.ForceRuleForTests = WideRule.Repeat;
        using (var f = GbScene(Route)) { Frames(f); WmsWideChip.ForceRuleForTests = null; Check("deliberate fault caught: forced to repeat, the right side no longer matches the game scrolled there", ColumnsDiffer(f.Frame, 400, 280, rr.Frame, 160, 0, 120, 72) > 0); }
        WmsWideChip.ForceRuleForTests = null;
        // a sprite hidden at X = 0 stays hidden: the Game Boy's positions beyond its edges are not real places
        using var hs = GbScene(Route, hiddenSprite: true); Frames(hs);
        var hw = (WmsWideChip)hs.PictureBridge!.Enhanced!;
        using var hn = GbScene(Route); Frames(hn);      // the same scene without the sprites
        int spriteDiff = ColumnsDiffer(hs.Frame, 400, 0, hn.Frame, 400, 0, 120, 144);
        Check($"... a sprite parked at X = 0 (8 pixels left of the screen, where Game Boy games hide sprites) is not drawn in the left side ({spriteDiff} pixels differ from the same scene without it; sprites beyond the edges: {hw.Layers.SpritesBeyondEdges}), counted ({hw.SpriteLinesNotExtended} lines)",
            spriteDiff == 0 && !hw.Layers.SpritesBeyondEdges && hw.SpriteLinesNotExtended > 0);
        using var neg = GbScene(Sfcs, window: true); Frames(neg);
        Check($"negative control: on the SNES enhanced chip ({Sfcs}) the same game is the Game Boy's own 160 x 144", neg.FrameWidth == 160 && neg.FrameHeight == 144);
    }

    // ---- Master System: the map is the screen's width: it repeats; the left-column mask stays the screen's
    private static SegaScenes.SmsRig SmsScene(string ppu, bool leftMask = false, int hscroll = 0)
    {
        var r = new SegaScenes.SmsRig(ppu); r.Cram(0, 0); r.Cram(1, 0x03); r.Cram(2, 0x0C); r.Tile(1, 1); r.Tile(2, 2);
        for (int row = 0; row < 24; row++) for (int col = 0; col < 32; col++) r.Cell(row, col, col == 0 || col == 20 ? 2 : 1);
        if (leftMask) r.V.Regs[0] |= 0x20;
        r.V.Regs[8] = (byte)hscroll;
        r.Frames();
        return r;
    }

    private static void MasterSystemGuest()
    {
        var s = SmsScene(Route); var r = SmsScene(Sfcs);
        var w = (WmsWideChip)s.Wrap.Enhanced!; var f = s.Wrap.Frame;
        int mid = ColumnsDiffer(f, 400, 72, r.Wrap.Frame, 256, 0, 256, 192);
        int rep = ColumnsDiffer(f, 400, 328, f, 400, 72, 72, 192) + ColumnsDiffer(f, 400, 0, f, 400, 256, 72, 192);
        Check($"Master System game on {Route}: 400 x 192 ({s.Wrap.FrameWidth} x {s.Wrap.FrameHeight}, shown {s.Wrap.DisplayWidth} wide); the middle is the SNES enhanced chip's 256 x 192 ({mid} differ); its map is the screen's 256: it repeats ({w.DescribeRules()}), each side the screen's own columns ({rep} differ), counted as repeated ({w.Repeated})",
            s.Wrap.FrameWidth == 400 && s.Wrap.FrameHeight == 192 && s.Wrap.DisplayWidth == 400 && mid == 0 && w.RuleOf(0) == WideRule.Repeat && rep == 0 && w.Repeated == 144L * 192 && w.Extended == 0);
        // the left-column mask (register 0 bit 5) blanks the screen's first column only: the right side still shows the map's column 0 (the marker)
        var m = SmsScene(Route, leftMask: true); var mr = SmsScene(Sfcs, leftMask: true);
        var mf = m.Wrap.Frame;
        int mmid = ColumnsDiffer(mf, 400, 72, mr.Wrap.Frame, 256, 0, 256, 192);
        Check($"... with the left-column mask on, the screen's column 0 is blank as on the Master System (the middle still the SNES enhanced chip's, {mmid} differ) and the right side still shows the map's column 0 (the mask is the screen's, not a window that reaches a side)",
            mmid == 0 && Px(mf, 400, 76, 50) != Px(f, 400, 76, 50) && Px(mf, 400, 332, 50) == Px(f, 400, 76, 50), $"masked {Px(mf, 400, 76, 50):X6} right {Px(mf, 400, 332, 50):X6} marker {Px(f, 400, 76, 50):X6}");
        var neg = SmsScene(Sfcs);
        Check($"negative control: on the SNES enhanced chip the same game is the Master System's own 256 x 192", neg.Wrap.FrameWidth == 256 && neg.Wrap.FrameHeight == 192);
    }

    // ---- Game Gear: the VDP's 256-pixel line around the LCD extends, with its sprites
    private static SegaScenes.SmsRig GgScene(string ppu, int hscroll = 0, bool sprite = false)
    {
        var g = new SegaScenes.SmsRig(ppu, gg: true); g.Gg(0, 0x000); g.Gg(1, 0x00F); g.Gg(2, 0x0F0); g.Gg(16 + 2, 0xF00); g.Tile(1, 1); g.Tile(2, 2); g.Tile(256 + 2, 2);     // register 6 bit 2: sprites take tiles 256 on
        for (int row = 0; row < 24; row++) for (int col = 0; col < 32; col++) g.Cell(row, col, col == 4 || col == 27 ? 2 : 1);      // VDP columns 4 (x 32-39) and 27 (x 216-223): beyond the LCD's 48-207
        g.V.Regs[8] = (byte)hscroll;
        if (sprite) { g.Sprite(0, 16, 59, 2); g.V.Vram[0x3F01] = 0xD0; } else g.V.Vram[0x3F00] = 0xD0;
        g.Frames();
        return g;
    }

    private static void GameGearGuest()
    {
        var s = GgScene(Route, sprite: true); var r = GgScene(Sfcs, sprite: true);
        var w = (WmsWideChip)s.Wrap.Enhanced!; var f = s.Wrap.Frame;
        int mid = ColumnsDiffer(f, 400, 120, r.Wrap.Frame, 160, 0, 160, 144);
        Check($"Game Gear game on {Route}: 400 x 144 ({s.Wrap.FrameWidth} x {s.Wrap.FrameHeight}); the middle is the SNES enhanced chip's LCD (160 x 144, {mid} differ); the VDP's map is 256 wide around the LCD's 160: it extends ({w.DescribeRules()})",
            s.Wrap.FrameWidth == 400 && s.Wrap.FrameHeight == 144 && mid == 0 && w.RuleOf(0) == WideRule.Extend);
        // VDP x 208-255 right of the LCD = what the LCD shows with the scroll moved 160 to the left (register 8 = 96)
        var sc = GgScene(Sfcs, hscroll: 96);
        int right = ColumnsDiffer(f, 400, 280, sc.Wrap.Frame, 160, 0, 48, 144);
        Check($"... the right side's first 48 columns are the VDP's columns 208-255, which the Game Gear draws and its LCD does not show (the same as the LCD scrolled there: {right} pixels differ); the markers at VDP columns 4 and 27 show at x 104 and 288",
            right == 0 && Px(f, 400, 106, 70) != Px(f, 400, 150, 70) && Px(f, 400, 290, 70) != Px(f, 400, 150, 70));
        // a sprite at VDP x 16 (off the LCD) is drawn in the left side: the Game Gear's VDP draws it there
        uint blue = Px(f, 400, 90, 38), bg = Px(GgScene(Route).Wrap.Frame, 400, 90, 38);
        Check($"... a sprite at VDP x 16 (32 pixels left of the LCD) shows in the left side (sprites beyond the edges: {w.Layers.SpritesBeyondEdges}; {w.SideSpritePixels} side sprite pixels)", blue != bg && w.Layers.SpritesBeyondEdges && w.SideSpritePixels > 0, $"{blue:X6} vs {bg:X6}");
        WmsWideChip.ForceSpritesBeyondForTests = false;
        var fs = GgScene(Route, sprite: true);
        WmsWideChip.ForceSpritesBeyondForTests = null;
        Check("deliberate fault caught: a chip that never draws sprites in the sides loses that sprite", Px(fs.Wrap.Frame, 400, 90, 38) == bg);
        var neg = GgScene("GG:GGS");
        Check($"negative control: on the Game Gear's own enhanced chip (GG:GGS from another console; here the GG's own GGS route) the picture is the LCD's 160 x 144", neg.Wrap.FrameWidth == 160);
    }

    // ---- Genesis: a 64-cell plane extends, a sprite past the right edge shows
    private static SegaScenes.MdRig MdScene(string ppu, bool h40 = true, int hscroll = 0, bool sprite = false, bool window = false, bool lossy = false)
    {
        var r = new SegaScenes.MdRig(ppu); r.Color(0, 0); r.Color(1, 0x00E); r.Color(2, 0x0E0); r.Color(18, 0xE00); r.Tile(1, 1); r.Tile(2, 2);
        r.Color(3, 0xE0E); r.Tile(3, 3);
        for (int row = 0; row < 32; row++) for (int col = 0; col < 64; col++) r.Cell(SegaScenes.MdRig.PlaneA, 64, row, col, col == 10 ? 2 : col < 40 ? 1 : col == 40 || col == 63 ? 2 : col == 42 && lossy ? 3 : 0);    // green also in the screen (column 10); a magenta cell only beside it (lossy)
        for (int row = 0; row < 32; row++) for (int col = 0; col < 64; col++) r.Cell(SegaScenes.MdRig.WindowNt, 64, row, col, 2);
        r.Regs(0x04, 0x44, 0x30, 0x2C, 0x07, 0x7C, 0, 0x00, 0, 0, 0xFF, 0x00, h40 ? 0x81 : 0x00, 0x3F, 0, 0x02, 0x01, 0, window ? 0x83 : 0);
        r.W16(SegaScenes.MdRig.HScroll, (-hscroll) & 0x3FF);
        if (sprite) r.Sprite(0, 330, 100, 1, 1, 2, pal: 1, link: 0); else r.Sprite(0, -128, -128, 1, 1, 0, link: 0);
        r.Go();
        return r;
    }

    private static void GenesisGuest()
    {
        var s = MdScene(Route, sprite: true); var r = MdScene(Sfcs, sprite: true);
        var w = (WmsWideChip)s.Wrap.Enhanced!; var f = s.Wrap.Frame;
        int mid = ColumnsDiffer(f, 400, 40, r.Wrap.Frame, 320, 0, 320, 224);
        Check($"Genesis game (H40) on {Route}: 400 x 224 ({s.Wrap.FrameWidth} x {s.Wrap.FrameHeight}, shown {s.Wrap.DisplayWidth} wide), 40 columns a side; the middle is the SNES enhanced chip's 320 x 224 ({mid} differ); plane A (64 cells, 512 wide) extends ({w.DescribeRules()})",
            s.Wrap.FrameWidth == 400 && s.Wrap.FrameHeight == 224 && s.Wrap.DisplayWidth == 400 && w.Side == 40 && mid == 0 && w.Layers.Layers[1].RealWidth == 512 && w.RuleOf(1) == WideRule.Extend, w.Describe());
        var sc = MdScene(Sfcs, hscroll: 40);
        int right = ColumnsDiffer(MdScene(Route).Wrap.Frame, 400, 360, sc.Wrap.Frame, 320, 280, 40, 224);     // (without the sprite: the scrolled picture has none there)
        Check($"... the right side is the plane as the game shows it scrolled 40 pixels on ({right} pixels differ); the plane's last cell shows left of the screen (x 32-39)", right == 0 && Px(f, 400, 35, 50) == Px(f, 400, 362, 50) && Px(f, 400, 35, 50) != Px(f, 400, 200, 50));
        uint spr = Px(f, 400, 372, 102), plain = Px(MdScene(Route).Wrap.Frame, 400, 372, 102);
        Check($"... a sprite at x 330 (past the 320 columns) shows in the right side (the Genesis's sprite plane is 512 wide: {w.Layers.SpritesBeyondEdges}; {w.SideSpritePixels} side sprite pixels)", spr != plain && w.SideSpritePixels > 0, $"{spr:X6} vs {plain:X6}");
        WmsWideChip.ForceSpritesBeyondForTests = false;
        var fs = MdScene(Route, sprite: true);
        WmsWideChip.ForceSpritesBeyondForTests = null;
        Check("deliberate fault caught: a chip that never draws sprites in the sides loses it", Px(fs.Wrap.Frame, 400, 372, 102) == plain);
        var h32 = MdScene(Route, h40: false);
        var hw = (WmsWideChip)h32.Wrap.Enhanced!;
        Check($"Genesis game in H32 on {Route}: 400 x 224 with 72 columns a side, the 512-wide plane extends ({hw.DescribeRules()})", h32.Wrap.FrameWidth == 400 && hw.Side == 72 && hw.RuleOf(1) == WideRule.Extend);
        var win = MdScene(Route, window: true);
        var ww = (WmsWideChip)win.Wrap.Enhanced!;
        int wi = Enumerable.Range(0, ww.Layers.LayerCount).FirstOrDefault(k => ww.Layers.Layers[k].Name == "window", -1);
        Check($"Genesis game with the window plane on {Route}: the window is laid out on the screen and fills ({ww.DescribeRules()}); its lines' sides show what is behind it", wi >= 0 && ww.RuleOf(wi) == WideRule.Fill && ww.Filled > 0, ww.Describe());
        var lo = MdScene(Route, lossy: true);
        long lost = lo.Wrap.Counters.Total(MdToNes.SideColourLoss);
        Check($"... a colour only beside the screen (a magenta cell at plane column 42) is drawn in the screen's palettes, so the screen keeps its own colours, and counted ({lost}: \"{MdToNes.SideColourLoss}\")", lost > 0 && ColumnsDiffer(lo.Wrap.Frame, 400, 40, r.Wrap.Frame, 320, 0, 320, 224) == 0);
        Check("negative control: the same scene without that cell counts no colour loss", s.Wrap.Counters.Total(MdToNes.SideColourLoss) == 0);
        var neg = MdScene(Sfcs);
        Check("negative control: on the SNES enhanced chip the same game is the Genesis's own 320 x 224", neg.Wrap.FrameWidth == 320 && neg.Wrap.FrameHeight == 224);
    }

    // ---- SNES: a 64-tile map extends, a 32-tile one repeats, a window over the edge reaches the side, a sprite at x 300
    private static ISnesPpuCore SnesScene(bool wideMap, int hofs = 0, bool sprite = false, bool edgeWindow = false)
    {
        var sp = SnesCores.CreatePpu("SFC");
        var v = sp.Vram; var cg = sp.Cgram;
        Array.Clear(v); Array.Clear(cg); Array.Clear(sp.Oam);
        for (int r = 0; r < 8; r++) { v[16 + r] = 0x00FF; v[48 + r] = 0x00FF; v[80 + r] = 0xFF00; v[96 + r] = 0xFFFF; }     // 4bpp tiles: 1 (the sprite's, OBSEL base 0) and 3 colour 1, 5 colour 2, 6 colour 3
        cg[1] = 0x7FFF; cg[2] = 0x03E0; cg[3] = 0x001F; cg[129] = 0x7C00;  // white, green, red; sprite palette 0 colour 1 blue
        for (int row = 0; row < 32; row++) for (int col = 0; col < 32; col++)
        {
            v[0x1000 + row * 32 + col] = (ushort)(col == 10 ? 5 : col == 12 ? 6 : 3);    // the left screen: white, a green and a red column
            v[0x1400 + row * 32 + col] = (ushort)(col == 0 || col == 31 ? 6 : 5);      // the right screen (64-tile maps): green, its first and last columns red
        }
        for (int i = 0; i < 128; i++) sp.Oam[i * 4 + 1] = 0xF0;
        if (sprite) { sp.Oam[0] = 300 & 0xFF; sp.Oam[1] = 100; sp.Oam[2] = 1; sp.Oam[3] = 0x30; sp.Oam[512] = 0x01; }
        sp.InvalidateCaches();
        sp.WriteRegister(0x00, 0x0F); sp.WriteRegister(0x01, 0x00); sp.WriteRegister(0x05, 0x01);
        sp.WriteRegister(0x07, (byte)(wideMap ? 0x11 : 0x10)); sp.WriteRegister(0x0B, 0x00);
        sp.WriteRegister(0x0D, (byte)hofs); sp.WriteRegister(0x0D, (byte)(hofs >> 8));
        sp.WriteRegister(0x2C, 0x11); sp.WriteRegister(0x2D, 0x00);
        if (edgeWindow) { sp.WriteRegister(0x23, 0x02); sp.WriteRegister(0x26, 0); sp.WriteRegister(0x27, 7); sp.WriteRegister(0x2E, 0x01); }    // window 1 over columns 0-7 masks BG1 on the main screen
        var inner = (sp as SfcPpuAdapter)!.Inner;
        inner.BridgeLines = new PPU_SFC.BridgeLineState[240];      // every line's registers, as the board captures them (windows, the fixed colour)
        inner.BeginFrame(); for (int l = 1; l <= 224; l++) inner.RenderLine(l); inner.OnVBlankStart();
        return sp;
    }

    private static void SnesGuest()
    {
        var down = new SnesToNes(EnhancedChips.Picture(Route), Route);
        var f = down.Render(SnesScene(true, sprite: true));
        var w = (WmsWideChip)down.Enhanced!;
        var r = new SnesToNes(EnhancedChips.Picture(Sfcs), Sfcs).Render(SnesScene(true, sprite: true));
        int mid = ColumnsDiffer(f.Argb, f.Width, 72, r.Argb, r.Width, 0, 256, 224);
        Check($"SNES game on {Route}: 400 x 224 ({f.Width} x {f.Height}, 72 a side); the middle is the SNES enhanced chip's picture ({mid} differ); BG1's 64-tile map extends ({w.DescribeRules()})",
            f.Width == 400 && f.Height == 224 && f.Side == 72 && mid == 0 && w.RuleOf(0) == WideRule.Extend && w.Layers.Layers[0].RealWidth == 512, w.Describe());
        var sc = new SnesToNes(EnhancedChips.Picture(Sfcs), Sfcs).Render(SnesScene(true, hofs: 72));
        int right = ColumnsDiffer(f.Argb, 400, 328, sc.Argb, 256, 184, 72, 99);
        Check($"... the right side is the map's other half as the game shows it scrolled 72 pixels on ({right} pixels differ over the lines above the sprite)", right == 0);
        uint spr = f.Argb[102 * 400 + 374] & 0xFFFFFF, plain = new SnesToNes(EnhancedChips.Picture(Route), Route).Render(SnesScene(true)).Argb[102 * 400 + 374] & 0xFFFFFF;
        Check($"... a sprite at x 300 (past the SNES's 256 columns: its 9-bit X is a real place) shows in the right side ({w.SideSpritePixels} side sprite pixels)", spr != plain && w.SideSpritePixels > 0, $"{spr:X6} vs {plain:X6}");
        var narrow = new SnesToNes(EnhancedChips.Picture(Route), Route); var nf = narrow.Render(SnesScene(false));
        var nw = (WmsWideChip)narrow.Enhanced!;
        int rep = ColumnsDiffer(nf.Argb, 400, 328, nf.Argb, 400, 72, 72, 224);
        Check($"SNES game with a 32-tile map on {Route}: BG1 repeats ({nw.DescribeRules()}), the right side the screen's first columns ({rep} differ)", nw.RuleOf(0) == WideRule.Repeat && rep == 0);
        var wd = new SnesToNes(EnhancedChips.Picture(Route), Route); var wf = wd.Render(SnesScene(true, edgeWindow: true));
        uint back = wf.Argb[50 * 400 + 30] & 0xFFFFFF, rightSide = wf.Argb[50 * 400 + 380] & 0xFFFFFF, centre = wf.Argb[50 * 400 + 200] & 0xFFFFFF;
        Check($"SNES game with a window over its columns 0-7 masking BG1: the window reaches the left side (BG1 hidden there, the backdrop shows) and not the right (WideMess spec 3.4); {((WmsWideChip)wd.Enhanced!).MaskedSideColumns} side columns x lines masked",
            back != centre && rightSide != back && wf.Argb[50 * 400 + 76] == wf.Argb[50 * 400 + 30], $"left {back:X6} right {rightSide:X6} centre {centre:X6}");
        WmsWideChip.ForceRuleForTests = WideRule.Fill;
        var ff = new SnesToNes(EnhancedChips.Picture(Route), Route).Render(SnesScene(true));
        WmsWideChip.ForceRuleForTests = null;
        Check("deliberate fault caught: a chip that fills every layer shows only the backdrop in the sides, not the map's other half", ColumnsDiffer(ff.Argb, 400, 328, sc.Argb, 256, 184, 72, 99) > 0);
    }

    // =========================================================================================================================================== the sound
    public static void Sound()
    {
        // the chip
        var chip = EnhancedChips.Sound(Route, 44100);
        var eleven = EnhancedPhase3Tests.Eleven();
        var (l, r) = Play(chip, eleven, 0.25);
        Check($"WideMess enhanced ({chip.BankShape} a bank): the Genesis's eleven voices at once take {chip.BanksFor(eleven)} bank ({chip.BanksInUse} made), every voice on a voice of its own ({chip.VoicesPlaying} playing, {chip.Dropped} dropped), the chip sounds (rms {l:F3} / {r:F3})",
            chip.BanksFor(eleven) == 1 && chip.BanksInUse == 1 && chip.VoicesPlaying == 11 && chip.Dropped == 0 && l > 0.005 && r > 0.005, chip.Describe());
        var wv = (WmsVoiceChip)chip;
        Check("... the first voices sit on WideMess voices 8-31 (16-bit PCM): channel 0 is voice 8, channel 23 voice 31, channel 24 the S-DSP's voice 0 (BRR)", WmsVoiceChip.VoiceOf(0) == 8 && WmsVoiceChip.VoiceOf(23) == 31 && WmsVoiceChip.VoiceOf(24) == 0 && wv.SourceOf(0, 0) >= 0);
        var fm = Enumerable.Range(0, 18).Select(i => new HubVoice(i % 4 == 3 ? HubVoiceKind.Noise : HubVoiceKind.Pulse, 200 + 30 * i, 0.4f)).ToArray();
        var c18 = EnhancedChips.Sound(Route, 44100); Play(c18, fm, 0.1);
        Check($"... the Japanese Master System's eighteen voices (PSG + FM) fit one bank, each on a voice of its own ({c18.VoicesPlaying} playing, {c18.BanksInUse} bank, {c18.Dropped} dropped)", c18.VoicesPlaying == 18 && c18.BanksInUse == 1 && c18.Dropped == 0);
        foreach (var (kind, label) in new[] { (HubVoiceKind.Triangle, "a triangle (16-bit PCM)"), (HubVoiceKind.Pulse, "a pulse (16-bit PCM)") })
        {
            var one = EnhancedChips.Sound(Route, 44100); var mono = new List<float>();
            Play(one, new[] { new HubVoice(kind, 440, 1f) }, 1.0, mono);
            double hz = EnhancedPhase3Tests.Pitch(mono, 44100), cents = hz > 0 ? 1200 * Math.Log2(hz / 440) : 9999;
            Check($"... {label} at 440 Hz plays at {hz:F1} Hz ({cents:+0.0;-0.0} cents)", Math.Abs(cents) < 15, $"{hz:F2}");
        }
        {
            // the S-DSP's own voices (BRR) once 24 PCM voices are taken: 24 quiet high voices, then one at 440
            var one = EnhancedChips.Sound(Route, 44100); var mono = new List<float>();
            var v = Enumerable.Range(0, 24).Select(i => new HubVoice(HubVoiceKind.Pulse, 3000 + i, 0.0015f)).Append(new HubVoice(HubVoiceKind.Triangle, 440, 1f)).ToArray();
            Play(one, v, 1.0, mono);
            var ww = (WmsVoiceChip)one;
            double hz = EnhancedPhase3Tests.Pitch(mono, 44100), cents = hz > 0 ? 1200 * Math.Log2(hz / 440) : 9999;
            Check($"... the 25th voice plays on the S-DSP's voice 0 (BRR) at {hz:F1} Hz ({cents:+0.0;-0.0} cents)", ww.SourceOf(0, 24) >= 0 && Math.Abs(cents) < 15, $"{hz:F2}");
        }
        var left = EnhancedChips.Sound(Route, 44100);
        var (pl, pr) = Play(left, new[] { new HubVoice(HubVoiceKind.Pulse, 440, 1f, Pan: -1f) }, 0.25);
        Check($"... stereo: a voice the guest puts on the left plays on the left (rms {pl:F3} left, {pr:F4} right)", pl > 0.01 && pr < pl * 0.05);
        // the guests' bridges
        var md = (MdSoundOnChip)GenesisSoundFactories.Create(Route, new SegaMachine(SegaTimeline.Genesis(SegaRegion.Ntsc), GenesisModel.Model1, 44100));
        var probe = new HubVoice[11]; md.Inner.DescribeVoices(probe);
        Check($"a Genesis game's sound on {Route}: {md.Chip.Banks} bank on offer for its 11 voices (the arithmetic says {md.Chip.BanksFor(probe)})", md.Chip.Banks == 1 && md.Chip.BanksFor(probe) == 1);
        var b = new SnesApuOnChip(Route, null, 0);
        EnhancedContractTests.PokeSnesVoices(b.Probe, 8, 1); b.SyncForTests(); b.SyncForTests();
        Check($"a SNES game's eight DSP voices on {Route}: every one on a voice ({b.Chip.VoicesPlaying} playing, {b.Chip.Dropped} dropped, {b.Chip.BanksInUse} bank)", b.Chip.VoicesPlaying == 8 && b.Chip.Dropped == 0 && b.Chip.BanksInUse == 1);
        var saved = MixConfig.NesBackApu;
        try
        {
            MixConfig.NesBackApu = "SNES";
            var stock = new NesApuOnSnes(); EnhancedContractTests.PokeSnesVoices(stock, 8, 1); stock.Sync();
            Check($"negative control: the same eight voices through the NES (NES:SNES): {stock.VoicesMapped} mapped, {stock.VoicesDroppedTotal} dropped and counted", stock.VoicesDroppedTotal > 0);
        }
        finally { MixConfig.NesBackApu = saved; }
        var u = new SmsSoundOnChip(EnhancedContractTests.SmsRequest(), Route);
        foreach (byte x in new byte[] { 0x8E, 0x0F, 0x90, 0xA0, 0x0A, 0xB2, 0xC5, 0x08, 0xD4, 0xE4, 0xF3 }) u.WritePsg(x);
        u.Run(3579545 / 10);
        Check($"a Master System / Game Gear game's PSG on {Route}: its four voices each on a voice ({u.Chip.VoicesPlaying} playing, {u.Chip.Dropped} dropped)", u.Chip.VoicesPlaying == 4 && u.Chip.Dropped == 0);
        var g = new GbApuOnChip(GbModel.Dmg, Route);
        g.WriteRegister(0x26, 0x80); g.WriteRegister(0x25, 0xFF); g.WriteRegister(0x24, 0x77);
        g.WriteRegister(0x11, 0x80); g.WriteRegister(0x12, 0xF0); g.WriteRegister(0x13, 0x00); g.WriteRegister(0x14, 0x87);
        g.WriteRegister(0x16, 0x40); g.WriteRegister(0x17, 0xF0); g.WriteRegister(0x18, 0x80); g.WriteRegister(0x19, 0x86);
        g.WriteRegister(0x21, 0xF0); g.WriteRegister(0x22, 0x44); g.WriteRegister(0x23, 0x80);
        for (int i = 0; i < 100; i++) g.Tick(4369);
        Check($"a Game Boy game's sound on {Route}: its pulses and noise each on a voice ({g.Chip.VoicesPlaying} playing, {g.Chip.Dropped} dropped)", g.Chip.VoicesPlaying >= 3 && g.Chip.Dropped == 0);
        // a NES game on the NES board's APU_WMSS
        var apu = new NesApuHost("WMSS").Apu; var onChip = apu as NesApuOnChip;
        apu.WriteAPURegister(0x4015, 0x0F);
        int t = (int)Math.Round(1789773.0 / (16 * 440.0)) - 1;
        apu.WriteAPURegister(0x4000, 0xBF); apu.WriteAPURegister(0x4002, (byte)t); apu.WriteAPURegister(0x4003, (byte)(0x08 | t >> 8));
        var nesMono = new List<float>();
        for (int i = 0; i < 1789773 / 32; i++) { apu.Step(32); if ((i & 63) == 0) foreach (var x in apu.GetAudioSamples()) nesMono.Add(x); }
        double nhz = EnhancedPhase3Tests.Pitch(nesMono, apu.GetSampleRate()), ncents = nhz > 0 ? 1200 * Math.Log2(nhz / (1789773.0 / (16 * (t + 1)))) : 9999;
        apu.WriteAPURegister(0x4004, 0x7F); apu.WriteAPURegister(0x4006, 0x80); apu.WriteAPURegister(0x4007, 0x09);
        apu.WriteAPURegister(0x4008, 0xFF); apu.WriteAPURegister(0x400A, 0x40); apu.WriteAPURegister(0x400B, 0x08);
        apu.WriteAPURegister(0x400C, 0x3F); apu.WriteAPURegister(0x400E, 0x04); apu.WriteAPURegister(0x400F, 0x08);
        for (int i = 0; i < 4000; i++) apu.Step(32);
        Check($"a NES game on the NES board's WMSS sound: its pulse plays at {nhz:F1} Hz ({ncents:+0.0;-0.0} cents from the NES's note), its four channels each on a WideMess voice ({onChip?.Chip.VoicesPlaying} playing, {onChip?.Chip.Dropped} dropped)",
            onChip != null && Math.Abs(ncents) < 15 && onChip.Chip.VoicesPlaying == 4 && onChip.Chip.Dropped == 0, onChip?.Describe() ?? apu.GetType().Name);
        // save states with the WideMess sound chip
        var snesRom = BrokenNes.Workshop.Corruption.SynthSnesRom.Build();
        using (var s = new SnesSession(snesRom, "SFC", Route, Route, null))
        {
            for (int f = 0; f < 30; f++) s.RunFrame();
            var state = s.SaveState(); for (int f = 0; f < 60; f++) s.RunFrame();
            string? err = null; try { s.LoadState(state); } catch (Exception ex) { err = ex.Message; }
            Check($"a SNES game saved with its picture and sound on {Route} loads back ({err ?? "loaded"})", err == null);
        }
        using (var gs = new GbSession(BrokenNes.Workshop.Corruption.CorruptConsoleChecks.GbRom(false), ConsoleKind.GameBoy, "GB", Route, Route))
            Check("a Game Boy game with its sound on the WideMess chip has no save states and says why", !gs.SupportsState && gs.StateUnsupportedReason.Contains("sound chip"));
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

    // =========================================================================================================================================== the other way: the other consoles' chips on the WideMess console
    private static string? WmsRoms()
    {
        foreach (var d in new[] { Environment.GetEnvironmentVariable("BROKENNES_WMS_ROMS") ?? "", Path.Combine(Environment.CurrentDirectory, "WideMess", "testroms", "bin"), Path.Combine(AppContext.BaseDirectory, "WideMess", "testroms", "bin") })
            if (d.Length > 0 && File.Exists(Path.Combine(d, "layers.wms"))) return d;
        return null;
    }

    public static void OnWideMess()
    {
        bool was = Consoles.WideMessEnabled; Consoles.WideMessEnabled = true;
        try
        {
            var ppu = CoreCatalog.Options(ConsoleKind.WideMess, CoreSlot.Ppu); var apu = CoreCatalog.Options(ConsoleKind.WideMess, CoreSlot.Apu);
            string? why = OrderProblem(ConsoleKind.WideMess, CoreSlot.Ppu, ppu) ?? OrderProblem(ConsoleKind.WideMess, CoreSlot.Apu, apu);
            string[] wantPpu = { "NES:FIX", "NES:FIXS", "NES:DMG", "NES:DMGS", "NES:SMS", "SMS:SMSS", "GG:GGS", "NES:MD", "MD:MDS" }, wantApu = { "NES:FIX", "NES:FIXS", "NES:DMGS", "SMS:SMSS", "GG:GGS", "MD:MDS" };
            Check($"the WideMess console's picture and sound menus: its own first (WMS; WMS, SFC, HLE), then the other consoles' chips as the SNES console offers them ({string.Join(", ", ppu.Where(o => o.Section == CoreSection.OtherConsoles).Select(o => o.Id))}), never the SNES's own nor the route to its own chips",
                ppu[0].Id == "WMS" && apu[0].Id == "WMS" && why == null && wantPpu.All(i => ppu.Any(o => o.Id == i)) && wantApu.All(i => apu.Any(o => o.Id == i))
                && !ppu.Concat(apu).Any(o => o.Id is "SNES:SFCS" or "WMS:WMSS" or "NES:SNES" or "NES:SNESS"), why ?? "");
            // a SNES game on the WideMess console on another console's chips is the same game as on the SNES console on them: frame and sample for sample
            var rom = BrokenNes.Workshop.Corruption.SynthSnesRom.Build();
            foreach (var (pic, snd) in new[] { ("NES:FIXS", "NES:FIXS"), ("MD:MDS", "MD:MDS") })
            {
                using var w = ConsoleSessions.Create(ConsoleKind.WideMess, rom, "WMS", pic, snd);
                using var s = ConsoleSessions.Create(ConsoleKind.Snes, rom, "SFC", pic, snd);
                var bw = new short[16384]; var bs = new short[16384]; int framesDiffer = 0, samplesDiffer = 0; long n = 0;
                for (int f = 0; f < 180; f++)
                {
                    w.RunFrame(); s.RunFrame();
                    if (w.FrameWidth != s.FrameWidth || w.FrameHeight != s.FrameHeight || !w.Frame.AsSpan(0, w.FrameWidth * w.FrameHeight).SequenceEqual(s.Frame.AsSpan(0, s.FrameWidth * s.FrameHeight))) framesDiffer++;
                    int a, b;
                    while ((a = w.ReadSamples(bw)) > 0 | (b = s.ReadSamples(bs)) > 0) { if (a != b || !bw.AsSpan(0, a).SequenceEqual(bs.AsSpan(0, b))) samplesDiffer++; n += a; }
                }
                Check($"a SNES game on the WideMess console with picture {pic} and sound {snd}: every frame and every sample as on the SNES console with them (180 frames, {n} samples; {framesDiffer} frames and {samplesDiffer} reads differ); its sound unit inside the bridge is the WideMess's ({((SnesSession)w).WideMessSound != null})",
                    framesDiffer == 0 && samplesDiffer == 0 && n > 0 && ((SnesSession)w).WideMessSound != null);
            }
            string? dir = WmsRoms();
            if (dir == null) { Console.WriteLine("  NOT RUN  WideMess games on the other consoles' chips: no WideMess\\testroms\\bin (run from the repository, or set BROKENNES_WMS_ROMS)"); return; }
            byte[] layers = File.ReadAllBytes(Path.Combine(dir, "layers.wms")), sound = File.ReadAllBytes(Path.Combine(dir, "sound.wms"));
            long Pic(string chip) { using var s = (SnesSession)ConsoleSessions.Create(ConsoleKind.WideMess, layers, "WMS", chip, "WMS"); for (int f = 0; f < 60; f++) s.RunFrame(); return s.WideMessPictureFramesNotCarried; }
            long onMd = Pic("MD:MDS"), onOwn = Pic("WMS");
            using (var d = (SnesSession)ConsoleSessions.Create(ConsoleKind.WideMess, layers, "WMS", "MD:MDS", "WMS"))
            {
                for (int f = 0; f < 60; f++) d.RunFrame();
                string desc = d.Description; int at = desc.IndexOf("not carried", StringComparison.Ordinal);
                Check($"a WideMess game drawing its own picture (8 layers, 400 wide) on another console's picture chip (MD:MDS): every such frame counted ({onMd} of 60) and said in the status line (\"{(at >= 0 ? desc[at..Math.Min(desc.Length, at + 90)] : "not said")}...\"); on its own WMS chip none ({onOwn})", onMd > 30 && onOwn == 0 && at >= 0);
            }
            long Snd(string chip) { using var s = (SnesSession)ConsoleSessions.Create(ConsoleKind.WideMess, sound, "WMS", "WMS", chip); var b = new short[16384]; for (int f = 0; f < 240; f++) { s.RunFrame(); while (s.ReadSamples(b) > 0) { } } return s.WideMessVoiceFramesNotCarried; }
            long sndNes = Snd("NES:FIXS"), sndOwn = Snd("WMS");
            Check($"a WideMess game sounding its voices 8-31 on another console's sound chip (NES:FIXS, which replays the S-DSP's eight): every such frame counted ({sndNes} of 240); on its own WMS chip none ({sndOwn})", sndNes > 0 && sndOwn == 0);
        }
        finally { Consoles.WideMessEnabled = was; }
    }

    // =========================================================================================================================================== real games
    /// <summary>A game of the user's library: its console, the file, a few presses to reach play (frame: buttons), and the frames to look at.</summary>
    internal sealed record Game(string Name, ConsoleKind Console, string Path, string Input, int Frames, int[] Moments);

    internal static readonly string Lib = @"X:\EMULATION", Roster = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"VRUN_Nes_Dev_work\sega_roster");

    internal static Game[] Games() => new[]
    {
        new Game("Super Mario Bros.", ConsoleKind.Nes, $@"{Lib}\Nintendo Entertainment System\Super Mario Bros. (JU) (PRG0) [!].nes", "120:Start,130:," + Walk(200, 1400, "Right+B", "A", 40, 22), 1500, new[] { 300, 520, 800, 1100 }),
        new Game("Ice Climber", ConsoleKind.Nes, $@"{Lib}\Nintendo Entertainment System\Ice Climber (U) [!].nes", "120:Start,130:,200:Start,210:," + Walk(300, 1000, "Right", "A", 50, 20), 1100, new[] { 250, 500, 800 }),
        new Game("Kirby's Adventure", ConsoleKind.Nes, $@"{Lib}\Nintendo Entertainment System\Kirby's Adventure (U) (PRG1) [!].nes", "400:Start,410:,700:Start,710:,1000:Start,1010:," + Walk(1300, 1800), 1900, new[] { 600, 1200, 1500, 1850 }),
        new Game("Super Mario Land", ConsoleKind.GameBoy, $@"{Lib}\GameBoy\Super Mario Land (W) (V1.1) [!].zip", "200:Start,210:," + Walk(300, 1300, "Right", "A", 36, 20), 1400, new[] { 350, 600, 900, 1200 }),
        new Game("Kirby's Dream Land", ConsoleKind.GameBoy, $@"{Lib}\GameBoy\Kirby's Dream Land (UE) [!].zip", "300:Start,310:,600:Start,610:," + Walk(900, 1500), 1600, new[] { 400, 900, 1200, 1550 }),
        new Game("Link's Awakening", ConsoleKind.GameBoy, $@"{Lib}\GameBoy\Legend of Zelda, The - Link's Awakening (U) (V1.2) [!].zip", "300:Start,310:,500:Start,510:,700:A,710:,900:A,910:", 1200, new[] { 400, 800, 1150 }),
        new Game("Super Mario Bros. Deluxe", ConsoleKind.GameBoyColor, $@"{Lib}\GameBoy Color\Super Mario Bros. Deluxe (USA, Europe).gbc", "300:Start,310:,450:A,460:,600:A,610:,750:A,760:,900:Start,910:,1000:A,1010:," + Walk(1100, 1800, "Right+B", "A", 40, 20), 1900, new[] { 400, 1100, 1450, 1850 }),
        new Game("Sonic the Hedgehog (Master System)", ConsoleKind.MasterSystem, System.IO.Path.Combine(Roster, @"sms\Sonic The Hedgehog (USA, Europe).sms"), "400:A,410:," + Walk(600, 1400, "Right", "A", 60, 15), 1500, new[] { 300, 800, 1100, 1450 }),
        new Game("Alex Kidd in Miracle World", ConsoleKind.MasterSystem, System.IO.Path.Combine(Roster, @"sms\Alex Kidd in Miracle World (USA, Europe) (v1.1).sms"), "300:Start,310:,500:Start,510:," + Walk(700, 1300, "Right", "B", 50, 15), 1400, new[] { 400, 900, 1350 }),
        new Game("Sonic the Hedgehog (Game Gear)", ConsoleKind.GameGear, System.IO.Path.Combine(Roster, @"gg\Sonic The Hedgehog (U) (V1.1) [!].gg"), "700:Start,710:,800:Start,810:,900:Start,910:," + Walk(1000, 1800, "Right", "A", 60, 15), 1900, new[] { 750, 1200, 1500, 1850 }),
        new Game("Sonic the Hedgehog (Genesis)", ConsoleKind.Genesis, $@"{Lib}\Sega Genesis\Sonic the Hedgehog (JUE) [!].bin", "450:Start,460:," + Walk(700, 1500, "Right", "A", 70, 15), 1600, new[] { 300, 800, 1200, 1550 }),
        new Game("Super Mario World", ConsoleKind.Snes, $@"{Lib}\Super Nintendo\Super Nintendo (U)\Super Mario World (U) [!].smc", "300:Start,310:,500:Start,510:,700:A,710:,900:A,910:,1400:A,1410:,1500:A,1510:,1700:A,1710:," + Walk(1900, 2300, "Right+Y", "B", 50, 20), 2400, new[] { 400, 1300, 2000, 2350 }),
    };

    internal static byte[] Load(string path)
    {
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return File.ReadAllBytes(path);
        using var z = ZipFile.OpenRead(path);
        var e = z.Entries.First(x => x.Name.EndsWith(".gb", StringComparison.OrdinalIgnoreCase) || x.Name.EndsWith(".gbc", StringComparison.OrdinalIgnoreCase) || x.Name.EndsWith(".nes", StringComparison.OrdinalIgnoreCase) || x.Name.EndsWith(".sms", StringComparison.OrdinalIgnoreCase) || x.Name.EndsWith(".gg", StringComparison.OrdinalIgnoreCase));
        using var s = e.Open(); using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray();
    }

    /// <summary>One run of a game: the widescreen picture and, in lockstep, the same game on the SNES enhanced chip (the NES: the board's SNESS) and on its own chip.</summary>
    internal sealed class Run : IDisposable
    {
        public readonly Game G;
        private readonly NES? wideNes, refNes, ownNes;
        private readonly IConsoleSession? wide, refer, own;
        private readonly Dictionary<int, PadButtons> input;
        private PadButtons held;
        public int Frame;

        public Run(Game g, bool withOwn, string? wideApu = null)
        {
            G = g; input = PictureBridgeCli.ParseInput(g.Input);
            byte[] rom = Load(g.Path);
            if (g.Console == ConsoleKind.Nes)
            {
                NES Make(string ppu, string apu) { var n = new NES { RomName = System.IO.Path.GetFileName(g.Path) }; n.LoadROM(rom); if (!n.SetPpuCore(ppu) || !n.SetApuCore(apu)) throw new InvalidOperationException($"NES core {ppu}/{apu} not applied"); return n; }
                wideNes = Make("WMSS", wideApu ?? "FIX"); refNes = Make("SNESS", "FIX"); if (withOwn) ownNes = Make("FIX", "FIX");
            }
            else
            {
                string cpu = CoreCatalog.Default(g.Console, CoreSlot.Cpu), ownPpu = CoreCatalog.Default(g.Console, CoreSlot.Ppu), ownApu = CoreCatalog.Default(g.Console, CoreSlot.Apu);
                string refPpu = g.Console == ConsoleKind.Snes ? "SNES:SFCS" : Sfcs;
                IConsoleSession Make(string ppu, string apu) => ConsoleSessions.Create(g.Console, rom, cpu, ppu, apu, null, System.IO.Path.GetFileName(g.Path));
                wide = Make(Route, wideApu ?? ownApu); refer = Make(refPpu, ownApu); if (withOwn) own = Make(ownPpu, ownApu);
            }
        }

        private static readonly bool[] none = new bool[8];
        private static bool[] Nes(PadButtons b) => new[] { (b & PadButtons.A) != 0, (b & PadButtons.B) != 0, (b & PadButtons.Select) != 0, (b & PadButtons.Start) != 0, (b & PadButtons.Up) != 0, (b & PadButtons.Down) != 0, (b & PadButtons.Left) != 0, (b & PadButtons.Right) != 0 };

        public void Step()
        {
            if (input.TryGetValue(Frame, out var h)) held = h;
            foreach (var n in new[] { wideNes, refNes, ownNes }) if (n != null) { n.SetInput(Nes(held)); n.RunFrame(); n.GetAudioBuffer(); }
            foreach (var s in new[] { wide, refer, own }) if (s != null) { s.SetPad(0, held); s.RunFrame(); var buf = sound; while (s.ReadSamples(buf) > 0) { } }
            Frame++;
        }
        private readonly short[] sound = new short[65536];

        /// <summary>The widescreen chip of this run.</summary>
        public WmsWideChip Chip => wideNes != null ? ((PPU_WMSS)wideNes.WidePicture!).Wide
            : wide is SegaPictureSession sp ? (WmsWideChip)sp.Enhanced! : wide is GbSession gs ? (WmsWideChip)gs.PictureBridge!.Enhanced! : (WmsWideChip)SnesChip((SnesSession)wide!);

        private static IEnhancedPictureChip SnesChip(SnesSession s) => ((SnesToNes)typeof(SnesSession).GetField("down", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(s)!).Enhanced!;

        /// <summary>The wide picture (ARGB), its size; the reference picture (the SNES enhanced chip at the guest's size), its size; the guest's own picture, its size.</summary>
        public (uint[] a, int w, int h) Wide => wideNes != null ? (Chip.Picture, Chip.PictureWidth, Chip.PictureHeight) : (wide!.Frame, wide.FrameWidth, wide.FrameHeight);
        public (uint[] a, int w, int h) Ref => refNes != null ? (NesRgba(refNes.GetFrameBuffer()), 256, 240) : (refer!.Frame, refer.FrameWidth, refer.FrameHeight);
        public (uint[] a, int w, int h) Own => ownNes != null ? (NesRgba(ownNes.GetFrameBuffer()), 256, 240) : (own!.Frame, own.FrameWidth, own.FrameHeight);
        public IConsoleSession? WideSession => wide;
        public NES? WideNes => wideNes;

        public void Dispose() { wide?.Dispose(); refer?.Dispose(); own?.Dispose(); }
    }

    /// <summary>The games a run checks: every one, or those whose names contain one of BROKENNES_WMS_GAMES's comma-separated words (to check one game again).</summary>
    private static IEnumerable<Game> Chosen()
    {
        var only = (Environment.GetEnvironmentVariable("BROKENNES_WMS_GAMES") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Games().Where(g => only.Length == 0 || only.Any(o => g.Name.Contains(o, StringComparison.OrdinalIgnoreCase)));
    }

    public static void GamesCheck()
    {
        foreach (var g in Chosen())
        {
            if (!File.Exists(g.Path)) { Console.WriteLine($"  NOT RUN  {g.Name}: no ROM at {g.Path}"); continue; }
            using var run = new Run(g, withOwn: false, wideApu: Route2(g.Console));
            int differ = 0, firstBad = -1; long ext = 0, rep = 0, fil = 0, unrep = 0; string rules = "";
            var seen = new HashSet<string>();
            for (int f = 0; f < g.Frames; f++)
            {
                run.Step();
                var (wa, ww, wh) = run.Wide; var (ra, rw, rh) = run.Ref;
                int side = (ww - rw) / 2;
                int d = ww == 400 && wh == rh ? ColumnsDiffer(wa, ww, side, ra, rw, 0, rw, rh) : rw * rh;
                if (d > 0 && run.Chip.Layers.Active) { differ++; if (firstBad < 0) firstBad = f; }     // (a NES board's chip draws from its first whole frame on)
                var c = run.Chip; ext += c.Extended; rep += c.Repeated; fil += c.Filled; unrep += c.UnreportedLines;
                string r = c.DescribeRules(); if (seen.Add(r) && seen.Count <= 4) rules += (rules.Length > 0 ? " | " : "") + r;
            }
            Check($"{g.Name} ({Consoles.DisplayName(g.Console)}) on the widescreen chip, {g.Frames} frames: 400 wide, the middle identical to the SNES enhanced chip's picture every frame ({differ} frames differ{(firstBad >= 0 ? ", first " + firstBad : "")}); side columns x lines extended {ext:N0}, repeated {rep:N0}, filled {fil:N0}; no layer without its width ({unrep}); rules seen: {rules}",
                differ == 0 && unrep == 0 && run.Wide.w == 400);
            // the sound on the WideMess sound chip: no voice dropped
            if (g.Console != ConsoleKind.Nes && run.WideSession != null)
            {
                IEnhancedSoundChip? chip = run.WideSession switch
                {
                    SnesSession s => (s.Apu as SnesApuOnChip)?.Chip,
                    GbSession gs => (gs.Apu as GbApuOnChip)?.Chip,
                    SegaPictureSession sp => sp.Inner is MdSession md ? (md.Board.Sound as MdSoundOnChip)?.Chip : sp.Inner is SmsSession sm ? (sm.Board.Sound as SmsSoundOnChip)?.Chip : null,
                    _ => null,
                };
                Check($"{g.Name}'s sound on the WideMess sound chip: {chip?.VoicesPlayingMax} voices at most at once on {chip?.BanksInUse} bank, none dropped ({chip?.Dropped})", chip != null && chip.VoicesPlayingMax >= 1 && chip.Dropped == 0, chip?.Describe() ?? "no chip");
            }
        }
        Alloc();
    }

    private static string? Route2(ConsoleKind c) => c == ConsoleKind.Nes ? "WMSS" : Route;

    /// <summary>0 bytes allocated a frame on the new picture paths (warm), the sound with them: a Game Boy, Master System, Game Gear and Genesis game on WMS:WMSS, a NES game on the NES board's WMSS.</summary>
    /// <summary>Bytes a frame (after <paramref name="warm"/> frames, over <paramref name="frames"/>) of a game with these picture and sound chips, the same presses as its run.</summary>
    internal static long BytesPerFrame(Game g, string ppu, string apu, int warm, int frames)
    {
        var input = PictureBridgeCli.ParseInput(g.Input); var held = PadButtons.None; var buf = new short[65536];
        using var s = ConsoleSessions.Create(g.Console, Load(g.Path), CoreCatalog.Default(g.Console, CoreSlot.Cpu), ppu, apu, null, System.IO.Path.GetFileName(g.Path));
        long before = 0;
        for (int f = 0; f < warm + frames; f++)
        {
            if (f == warm) before = GC.GetAllocatedBytesForCurrentThread();
            if (input.TryGetValue(f, out var h)) held = h;
            s.SetPad(0, held); s.RunFrame(); while (s.ReadSamples(buf) > 0) { }
        }
        return (GC.GetAllocatedBytesForCurrentThread() - before) / frames;
    }

    /// <summary>Presses for a run through a level: <paramref name="move"/> held from <paramref name="from"/> to <paramref name="to"/>, with <paramref name="jump"/> added for <paramref name="hold"/> frames every <paramref name="every"/>.</summary>
    private static string Walk(int from, int to, string move = "Right", string jump = "A", int every = 45, int hold = 18)
    {
        var parts = new List<string>();
        for (int f = from; f < to; f += every) { parts.Add($"{f}:{move}+{jump}"); parts.Add($"{f + hold}:{move}"); }
        parts.Add($"{to}:");
        return string.Join(",", parts);
    }

    private static void Alloc()
    {
        foreach (var g in Chosen().Where(g => g.Console != ConsoleKind.Snes).GroupBy(g => g.Console).Select(x => x.First()))
        {
            if (!File.Exists(g.Path)) continue;
            if (g.Console == ConsoleKind.Nes)
            {
                using var run = new Run(g, withOwn: false, wideApu: Route2(g.Console));
                for (int f = 0; f < 400; f++) run.Step();
                var n = run.WideNes!;
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int f = 0; f < 120; f++) { n.RunFrame(); n.GetAudioBuffer(); }
                long after = GC.GetAllocatedBytesForCurrentThread();
                long perFrameNes = (after - before) / 120;
                var fix = new NES { RomName = "x" }; fix.LoadROM(Load(g.Path)); fix.SetPpuCore("SNESS"); for (int f = 0; f < 400; f++) { fix.RunFrame(); fix.GetAudioBuffer(); }
                long b2 = GC.GetAllocatedBytesForCurrentThread(); for (int f = 0; f < 120; f++) { fix.RunFrame(); fix.GetAudioBuffer(); } long base_ = (GC.GetAllocatedBytesForCurrentThread() - b2) / 120;
                Check($"{g.Name} on the NES board's WMSS (picture and sound): {perFrameNes} bytes allocated a frame beyond SNESS's {base_} (the NES's own audio buffer)", perFrameNes <= base_, $"{perFrameNes} vs {base_}");
                continue;
            }
            string own = CoreCatalog.Default(g.Console, CoreSlot.Ppu), ownA = CoreCatalog.Default(g.Console, CoreSlot.Apu);
            long wide = BytesPerFrame(g, Route, ownA, 1000, 300), guestSize = BytesPerFrame(g, Sfcs, ownA, 1000, 300), sound = BytesPerFrame(g, own, Route, 1000, 300);
            // a frame's allocation would be one object at least (24 bytes) every frame; less than 16 bytes a frame more on average is the translator's pools growing once for the side columns' extra items
            Check($"{g.Name}: the widescreen picture allocates no object a frame beyond what the same translator allocates for the SNES enhanced chip at the game's own size ({wide} and {guestSize} bytes a frame on average over frames 1000-1300; the difference, under one object a frame, is the pools growing once for the side columns' items), the WideMess sound chip {sound} bytes",
                wide - guestSize < 16 && sound == 0, $"{wide} / {guestSize} / {sound}");
        }
    }
}
