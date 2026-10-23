using System;
using System.Collections.Generic;
using System.Linq;
using NesEmulator.Gb;

namespace NesEmulator.Mix;

/// <summary>The registers each visible Game Boy line was drawn with (captured through PPU_GB.LineStarted).</summary>
internal sealed class GbLineCapture
{
    public readonly byte[] Lcdc = new byte[144], Scx = new byte[144], Scy = new byte[144], Wx = new byte[144], Wy = new byte[144], Bgp = new byte[144], Obp0 = new byte[144], Obp1 = new byte[144];
    public bool Complete => seen == 144;
    private int seen;
    private readonly PPU_GB ppu;

    public GbLineCapture(PPU_GB ppu)
    {
        this.ppu = ppu;
        ppu.LineStarted += OnLine;
    }

    private void OnLine(int ly)
    {
        if (ly == 0) seen = 0;
        Lcdc[ly] = ppu.Lcdc; Scx[ly] = ppu.Scx; Scy[ly] = ppu.Scy; Wx[ly] = ppu.Wx; Wy[ly] = ppu.Wy; Bgp[ly] = ppu.Bgp; Obp0[ly] = ppu.Obp0; Obp1[ly] = ppu.Obp1;
        seen++;
    }

    public int Clamp(int line) => Math.Clamp(line, 0, 143);
}

/// <summary>Game Boy shades as NES colours: the classic green DMG look (default) or plain greys.</summary>
internal static class GbShades
{
    /// <summary>The DMG screen: #9BBC0F, #8BAC0F, #306230, #0F380F (lightest first).</summary>
    public static readonly (int r, int g, int b)[] Green = { (0x9B, 0xBC, 0x0F), (0x8B, 0xAC, 0x0F), (0x30, 0x62, 0x30), (0x0F, 0x38, 0x0F) };
    public static int Shade(byte palette, int index) => palette >> (index * 2) & 3;
    /// <summary>A shade (0 lightest .. 3 darkest) as the NES colour nearest the chosen Game Boy look's.</summary>
    public static byte Nes(int shade) { var (r, g, b) = GbLook.Shades[shade]; return NesPalette.Nearest(r, g, b); }
    /// <summary>A shade as a SNES / CGB BGR555 colour in the chosen Game Boy look.</summary>
    public static ushort Bgr555(int shade) { var (r, g, b) = GbLook.Shades[shade]; return (ushort)(r >> 3 | (g >> 3) << 5 | (b >> 3) << 10); }
    /// <summary>Colour <paramref name="c"/> of a CGB palette as BGR555.</summary>
    public static ushort Cgb(byte[] palRam, int palette, int c) { int o = palette * 8 + c * 2; return (ushort)((palRam[o] | palRam[o + 1] << 8) & 0x7FFF); }   // bit 15 is unused on the chip; a game may leave it set, and the SNES colour tables have 32,768 entries
}

/// <summary>Which Game Boy screen colours to show: the original DMG green, greyscale, the Game Boy Pocket's tamer
/// greens, or Game Boy Color colours (a DMG game runs on a GBC in its compatibility mode, colourised by title).</summary>
public enum GbPaletteChoice { Green, Grey, Pocket, Color }

/// <summary>
/// BrokenNes 2: the Game Boy look, app-wide. <see cref="Palette"/> applies wherever a Game Boy picture is shown: the
/// Game Boy console, other consoles' games through the Game Boy picture chips (stock DMG, enhanced DMGS - Color runs that chip as a GBC)
/// and Game Boy games through NES/SNES picture chips. The inversions apply to those cross-console translations only: background and sprites apart.
/// <para>
/// Every path is upright by default: a colour's Game Boy shade follows its own brightness (<see cref="NesEmulator.Gb.GbColour.ShadeOf"/>), so bright is light and dark is dark,
/// whatever console the game is and whichever Game Boy chip draws it. <see cref="InvertBackground"/> mirrors the lightness of the background - the layers AND the backdrop -
/// and <see cref="InvertSprites"/> that of the sprites, the same way on every path: a DMG shade s becomes 3 - s, a Game Boy Color colour has its lightness mirrored with its hue kept
/// (<see cref="NesEmulator.Gb.GbColour.FlipLightness"/>, not a colour negative). The bezel around a cropped picture and the black outside a picture never flip.
/// </para>
/// </summary>
public static class GbLook
{
    public static volatile GbPaletteChoice Palette = GbPaletteChoice.Green;
    public static volatile bool InvertBackground, InvertSprites;

    private static readonly (int r, int g, int b)[] Grey = { (0xFF, 0xFF, 0xFF), (0xAA, 0xAA, 0xAA), (0x55, 0x55, 0x55), (0x00, 0x00, 0x00) };
    /// <summary>The Game Boy Pocket's screen: greys with a faint olive cast.</summary>
    private static readonly (int r, int g, int b)[] Pocket = { (0xC4, 0xCF, 0xA1), (0x8B, 0x95, 0x6D), (0x4D, 0x53, 0x3C), (0x1F, 0x1F, 0x1F) };

    /// <summary>The 4 DMG shades (lightest first) of the chosen look (Color: the original green, for places that need shades).</summary>
    public static (int r, int g, int b)[] Shades => Palette switch { GbPaletteChoice.Grey => Grey, GbPaletteChoice.Pocket => Pocket, _ => GbShades.Green };
    public static uint Argb(int shade) { var (r, g, b) = Shades[shade & 3]; return 0xFF000000u | (uint)(r << 16 | g << 8 | b); }
    /// <summary>Game Boy Color colours wanted (Game Boy pictures run as a GBC).</summary>
    public static bool Color => Palette == GbPaletteChoice.Color;
    public static int Background(int shade) => InvertBackground ? 3 - shade : shade;
    public static int Sprite(int shade) => InvertSprites ? 3 - shade : shade;
    /// <summary>A BGR555 colour with its lightness mirrored when asked (hue and saturation kept: <see cref="NesEmulator.Gb.GbColour.FlipLightness555"/>).</summary>
    public static ushort BackgroundColour(ushort bgr) => InvertBackground ? NesEmulator.Gb.GbColour.FlipLightness555(bgr) : bgr;
    public static ushort SpriteColour(ushort bgr) => InvertSprites ? NesEmulator.Gb.GbColour.FlipLightness555(bgr) : bgr;

    public static string Key(GbPaletteChoice p) => p.ToString().ToLowerInvariant();
    public static GbPaletteChoice FromKey(string? key) => key?.ToLowerInvariant() switch
    {
        "grey" or "gray" or "greyscale" or "grayscale" => GbPaletteChoice.Grey,
        "pocket" => GbPaletteChoice.Pocket,
        "color" or "colour" or "cgb" => GbPaletteChoice.Color,
        _ => GbPaletteChoice.Green,
    };
}

/// <summary>
/// MIX LAB: a Game Boy picture drawn by a NES PPU (any core, through <see cref="NesPictureSink"/>).
/// The Game Boy's 160x144 screen sits at NES (48,48). Background and window tiles become NES background tiles
/// (tile flips and Game Boy Color palettes are baked in where the NES can't do them), sprites become NES sprites
/// (8x16 = two 8x8, 8 per line instead of 10), palettes become 2C02 colours (DMG shades as NES greens). Each NES tile
/// row takes the scroll of the Game Boy line it covers, so status-bar splits survive (with at most 7 pixels of fine
/// scroll mismatch). With bleed on (default) the rest of the NES screen shows the Game Boy map beyond the screen edges.
/// On an enhanced chip (FIX enhanced: <see cref="IEnhancedPictureChip"/>) the picture is instead the Game Boy's own 160 x 144 as layers (<see cref="RenderLayers"/>):
/// the background and the window apart, each line's own scroll and window position, all 40 sprites with the Game Boy's 10 a line and its priorities.
/// </summary>
internal sealed class GbToNes
{
    private readonly IPictureSink sink;
    public bool Bleed = true;
    private string stats = "";
    private int layersUsed, bgTilesUsed, piecesUsed; private bool layeredLast;
    /// <summary>What the last frame used (tools): tiles, sprites, palettes; made when read (the layered path makes no string a frame).</summary>
    public string Stats => layeredLast ? $"layers {layersUsed} (background tiles {bgTilesUsed}), sprite pieces {piecesUsed}" : stats;
    public GbToNes(string nesPpuId) { sink = new NesPictureSink(nesPpuId); }
    /// <summary>Onto an enhanced picture chip of another console directly (no NES in between): the Game Boy picture as layers at its 160 x 144, in that chip's colours.</summary>
    public GbToNes(IEnhancedPictureChip chip, string routeId) { sink = new ChipPictureSink(chip, routeId); }
    public string PpuId => sink.PpuId;
    /// <summary>The last picture (the enhanced chip's 160 x 144, or the NES chip's 256 x 240).</summary>
    public BridgeFrame Frame { get; } = new();
    public bool Layered => sink.Enhanced != null;
    /// <summary>The enhanced chip the layers go to (null on a stock chip): tests read its layers.</summary>
    internal IEnhancedPictureChip? Enhanced => sink.Enhanced;

    public BridgeFrame Render(BOARD_GB board, GbLineCapture cap)
    {
        layeredLast = sink.Enhanced != null;
        if (sink.Enhanced is { } chip) return RenderLayers(board, cap, chip);
        var ppu = board.Ppu; var vram = ppu.Vram; bool cgb = board.CgbMode;
        var pic = new NesPicture();
        const int Ref = 72;
        int fineX = cap.Scx[Ref] & 7, fineY = cap.Scy[Ref] & 7;
        pic.FineX = fineX; pic.FineY = fineY;
        pic.Blank = (cap.Lcdc[Ref] & 0x80) == 0;

        // ---- background cells (33 x 30): (vram tile address, palette, hflip, vflip) or -1 for an empty cell
        var cells = new (int addr, int pal, bool hf, bool vf)?[30, 33];
        var bgPalUse = new Dictionary<int, int>();
        for (int cy = 0; cy < 30; cy++)
        for (int cx = 0; cx < 33; cx++)
        {
            int x0 = cx * 8 - fineX, y0 = cy * 8 - fineY;
            int gx = x0 + 4 - 48, gy = y0 + 4 - 48;
            bool inside = gx >= 0 && gx < 160 && gy >= 0 && gy < 144;
            if (!inside && !Bleed) continue;
            int line = cap.Clamp(gy); byte lcdc = cap.Lcdc[line];
            if (!cgb && (lcdc & 0x01) == 0) continue;
            int map, mx, my;
            bool window = inside && (lcdc & 0x20) != 0 && gy >= cap.Wy[line] && gx >= cap.Wx[line] - 7;
            if (window) { map = (lcdc & 0x40) != 0 ? 0x1C00 : 0x1800; mx = (gx - (cap.Wx[line] - 7)) >> 3; my = (gy - cap.Wy[line]) >> 3; }
            else
            {
                map = (lcdc & 0x08) != 0 ? 0x1C00 : 0x1800;
                mx = ((x0 - 48 + cap.Scx[line]) & 255) >> 3; my = ((y0 - 48 + cap.Scy[line]) & 255) >> 3;
            }
            int ma = map + (my & 31) * 32 + (mx & 31);
            int tile = vram[ma]; byte attr = cgb ? vram[0x2000 + ma] : (byte)0;
            int addr = (lcdc & 0x10) != 0 ? tile * 16 : 0x1000 + (sbyte)tile * 16;
            if ((attr & 0x08) != 0) addr += 0x2000;
            int pal = attr & 7;
            cells[cy, cx] = (addr, pal, (attr & 0x20) != 0, (attr & 0x40) != 0);
            bgPalUse[pal] = bgPalUse.GetValueOrDefault(pal) + 1;
        }
        var bgSlots = bgPalUse.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).Take(4).ToList();
        int Slot(List<int> slots, int p) { int i = slots.IndexOf(p); return i < 0 ? 0 : i; }

        // ---- palettes
        byte Nes555(ushort c) { var (r, g, b) = NesPalette.FromBgr555(c); return NesPalette.Nearest(r, g, b); }
        byte Cgb(byte[] palRam, int p, int c) => Nes555(GbLook.BackgroundColour(GbShades.Cgb(palRam, p, c)));
        // DMG games: BGP picks the shade (inverted when asked); the shade is a colour of the chosen look, or of the
        // GBC's compatibility palette when the game runs on a GBC.
        byte bgp = cap.Bgp[Ref];
        byte BgShade(int c) { int sh = GbLook.Background(GbShades.Shade(bgp, c)); return ppu.CompatMode ? Nes555(GbShades.Cgb(ppu.BgPalRam, 0, sh)) : GbShades.Nes(sh); }
        pic.Pal[0] = cgb ? Cgb(ppu.BgPalRam, bgSlots.Count > 0 ? bgSlots[0] : 0, 0) : BgShade(0);
        for (int k = 0; k < 4; k++)
            for (int c = 1; c < 4; c++)
                pic.Pal[k * 4 + c] = cgb ? (k < bgSlots.Count ? Cgb(ppu.BgPalRam, bgSlots[k], c) : (byte)0x0F) : BgShade(c);

        // ---- background tiles, flips baked in (the NES can't flip background tiles)
        var bgTiles = new Dictionary<long, int>(); int nextBg = 1, overflow = 0;
        void RowBits(int addr, int r, bool hf, out byte lo, out byte hi)
        {
            lo = vram[(addr + r * 2) & 0x3FFF]; hi = vram[(addr + r * 2 + 1) & 0x3FFF];
            if (hf) { lo = Rev(lo); hi = Rev(hi); }
        }
        int BgTile((int addr, int pal, bool hf, bool vf) c)
        {
            long key = c.addr | (long)(c.hf ? 1 : 0) << 20 | (long)(c.vf ? 1 : 0) << 21;
            if (bgTiles.TryGetValue(key, out var t)) return t;
            if (nextBg > 255) { overflow++; return 1 + (c.addr >> 4) % 255; }
            t = nextBg++; bgTiles[key] = t;
            for (int r = 0; r < 8; r++) { RowBits(c.addr, c.vf ? 7 - r : r, c.hf, out var lo, out var hi); pic.ChrBg[t * 16 + r] = lo; pic.ChrBg[t * 16 + 8 + r] = hi; }
            return t;
        }
        for (int cy = 0; cy < 30; cy++)
        for (int cx = 0; cx < 33; cx++)
        {
            if (cells[cy, cx] is not { } c) continue;
            int t = BgTile(c);
            if (cx < 32) pic.Nt0[cy * 32 + cx] = (byte)t; else pic.Nt1[cy * 32] = (byte)t;
        }
        for (int by = 0; by < 15; by++)
        for (int bx = 0; bx < 17; bx++)
        {
            int cx = bx * 2, cy = by * 2;
            if (cells[cy, Math.Min(cx, 32)] is not { } c) continue;
            int slot = Slot(bgSlots, c.pal);
            var nt = cx < 32 ? pic.Nt0 : pic.Nt1; int lx = cx & 31;
            int ai = 0x3C0 + (cy >> 2) * 8 + (lx >> 2), sh = ((cy & 2) << 1) | (lx & 2);
            nt[ai] = (byte)((nt[ai] & ~(3 << sh)) | (slot << sh));
        }

        // ---- sprites
        bool tall = (cap.Lcdc[Ref] & 0x04) != 0, objOn = (cap.Lcdc[Ref] & 0x02) != 0;
        var pieces = new List<(int x, int y, int addr, int pal, int flags)>();
        var objPalUse = new Dictionary<int, int>();
        if (objOn)
            for (int i = 0; i < 40; i++)
            {
                int y = ppu.Oam[i * 4] - 16, x = ppu.Oam[i * 4 + 1] - 8, tile = ppu.Oam[i * 4 + 2], f = ppu.Oam[i * 4 + 3];
                int h = tall ? 16 : 8;
                if (y <= -h || y >= 144 || x <= -8 || x >= 160) continue;
                int pal = cgb ? f & 7 : (f >> 4) & 1, bank = cgb && (f & 0x08) != 0 ? 0x2000 : 0;
                bool vf = (f & 0x40) != 0;
                if (!tall) pieces.Add((x, y, bank + tile * 16, pal, f));
                else
                {
                    int top = tile & 0xFE;
                    pieces.Add((x, y, bank + (vf ? top + 1 : top) * 16, pal, f));
                    pieces.Add((x, y + 8, bank + (vf ? top : top + 1) * 16, pal, f));
                }
                objPalUse[pal] = objPalUse.GetValueOrDefault(pal) + 1;
            }
        if (!cgb) pieces = pieces.OrderBy(p => p.x).ToList();   // DMG: smaller X wins; the NES goes by OAM order
        var objSlots = cgb ? objPalUse.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).Take(4).ToList() : new List<int> { 0, 1 };
        byte ObjShade(int k, int c)
        {
            int sh = GbLook.Sprite(GbShades.Shade(k == 1 ? cap.Obp1[Ref] : cap.Obp0[Ref], c));
            return ppu.CompatMode ? Nes555(GbShades.Cgb(ppu.ObjPalRam, k == 1 ? 1 : 0, sh)) : GbShades.Nes(sh);
        }
        for (int k = 0; k < 4; k++)
            for (int c = 1; c < 4; c++)
                pic.Pal[16 + k * 4 + c] = cgb ? (k < objSlots.Count ? Nes555(GbLook.SpriteColour(GbShades.Cgb(ppu.ObjPalRam, objSlots[k], c))) : (byte)0x0F)
                                              : ObjShade(k, c);
        var objTiles = new Dictionary<int, int>(); int nextObj = 0;
        Array.Fill(pic.Oam, (byte)0xFF);
        int kept = 0;
        foreach (var p in pieces)
        {
            if (kept >= 64 || 48 + p.x > 255) continue;
            if (!objTiles.TryGetValue(p.addr, out var t))
            {
                if (nextObj > 255) continue;
                t = nextObj++; objTiles[p.addr] = t;
                for (int r = 0; r < 8; r++) { pic.ChrObj[t * 16 + r] = vram[(p.addr + r * 2) & 0x3FFF]; pic.ChrObj[t * 16 + 8 + r] = vram[(p.addr + r * 2 + 1) & 0x3FFF]; }
            }
            int o = kept++ * 4;
            pic.Oam[o] = (byte)(48 + p.y - 1); pic.Oam[o + 1] = (byte)t;
            pic.Oam[o + 2] = (byte)(Slot(objSlots, p.pal) | ((p.flags & 0x80) != 0 ? 0x20 : 0) | ((p.flags & 0x20) != 0 ? 0x40 : 0) | ((p.flags & 0x40) != 0 ? 0x80 : 0));
            pic.Oam[o + 3] = (byte)(48 + p.x);
        }
        stats = $"bg tiles {bgTiles.Count} (overflow {overflow}), obj tiles {objTiles.Count}, sprites {kept}/{pieces.Count} 8x8 pieces, bg palettes {bgPalUse.Count}";
        return Frame.FromRgba(sink.Present(pic));
    }

    // ---------------------------------------------------------------------------------------------------- the layered path
    private readonly Dictionary<int, int> tileOf = new();
    private readonly Dictionary<int, int> objOf = new();
    private readonly int[] winLine = new int[144];
    private readonly List<(int x, int oam)> order = new();
    private readonly byte[] selected = new byte[144 * 40];

    private static uint Rgb(ushort bgr) { var (r, g, b) = NesPalette.FromBgr555(bgr); return (uint)(r << 16 | g << 8 | b); }
    private static uint Rgb((int r, int g, int b) c) => (uint)(c.r << 16 | c.g << 8 | c.b);

    /// <summary>
    /// The Game Boy picture as the enhanced chip's layers, at 160 x 144: layer 0 the background (the 32 x 32 map, each line's SCX / SCY), layer 1 the window (its map, from WX - 7, its
    /// own line counter, hiding the background where it is: a window pixel of colour 0 is the backdrop, as the Game Boy shows it), the sprites (40, 8 x 16 as two pieces, the first 10
    /// a line in OAM order as the Game Boy picks them, in front in X order on a DMG and OAM order on a GBC; behind the background's colours 1-3 when their flag says so; a GBC
    /// background tile with its priority bit over every sprite). Colours: the Game Boy look's shades (DMG, through BGP / OBP0 / OBP1 of the middle line) or the GBC palettes, with
    /// the look's inversions. Not carried: map and tile-data selects that change down the frame (the middle line's are used), a GBC background's colour 0 other than palette 0's.
    /// </summary>
    private BridgeFrame RenderLayers(BOARD_GB board, GbLineCapture cap, IEnhancedPictureChip chip)
    {
        var ppu = board.Ppu; var vram = ppu.Vram; bool cgb = board.CgbMode;
        const int W = 160, H = 144, Ref = 72;
        var e = chip.Layers;
        e.BeginFrame(W, H);
        e.Guest = cgb ? "Game Boy Color" : "Game Boy";
        byte lcdc = cap.Lcdc[Ref];
        int bgMap = (lcdc & 0x08) != 0 ? 0x1C00 : 0x1800, winMap = (lcdc & 0x40) != 0 ? 0x1C00 : 0x1800;
        bool unsignedTiles = (lcdc & 0x10) != 0;
        // ---- palettes: BG / OBJ palette p, colour c (level c); the backdrop is BG colour 0
        for (int p = 0; p < 8; p++)
            for (int c = 1; c < 4; c++)
            {
                if (cgb)
                {
                    e.BgRgb[p * 4 + c] = Rgb(GbLook.BackgroundColour(GbShades.Cgb(ppu.BgPalRam, p, c)));
                    e.ObjRgb[p * 4 + c] = Rgb(GbLook.SpriteColour(GbShades.Cgb(ppu.ObjPalRam, p, c)));
                }
                else if (p < 2)
                {
                    int bsh = GbLook.Background(GbShades.Shade(cap.Bgp[Ref], c)), osh = GbLook.Sprite(GbShades.Shade(p == 1 ? cap.Obp1[Ref] : cap.Obp0[Ref], c));
                    e.BgRgb[p * 4 + c] = ppu.CompatMode ? Rgb(GbShades.Cgb(ppu.BgPalRam, 0, bsh)) : Rgb(GbLook.Shades[bsh]);
                    e.ObjRgb[p * 4 + c] = ppu.CompatMode ? Rgb(GbShades.Cgb(ppu.ObjPalRam, p, osh)) : Rgb(GbLook.Shades[osh]);
                }
            }
        uint backdrop = cgb ? Rgb(GbLook.BackgroundColour(GbShades.Cgb(ppu.BgPalRam, 0, 0)))
            : ppu.CompatMode ? Rgb(GbShades.Cgb(ppu.BgPalRam, 0, GbLook.Background(GbShades.Shade(cap.Bgp[Ref], 0)))) : Rgb(GbLook.Shades[GbLook.Background(GbShades.Shade(cap.Bgp[Ref], 0))]);
        // ---- the two layers, per line
        var bg = e.Layers[0]; var win = e.Layers[1];
        bg.Enabled = true; bg.Name = "background"; bg.MapWidth = 32; bg.MapHeight = 32;
        win.Enabled = true; win.Name = "window"; win.MapWidth = 32; win.MapHeight = 32;
        e.LayerCount = 2;
        int wl = 0; bool anyHidden = false, windowShown = false;
        for (int y = 0; y < H; y++)
        {
            e.BackdropRgb[y] = e.BackdropAltRgb[y] = backdrop;
            byte lc = cap.Lcdc[y];
            if ((lc & 0x80) == 0) continue;                                        // the LCD off: the backdrop only
            bool bgOn = cgb || (lc & 0x01) != 0;
            byte zBg = 5, zBgHigh = cgb && (lc & 0x01) != 0 ? (byte)11 : (byte)5;   // a GBC tile's priority bit puts it over every sprite (when LCDC bit 0, the master priority, is set)
            if (bgOn) { bg.On[y] = true; bg.ScrollX[y] = cap.Scx[y]; bg.ScrollY[y] = cap.Scy[y]; bg.ZLow[y] = zBg; bg.ZHigh[y] = zBgHigh; }
            int wx = cap.Wx[y] - 7;
            bool winOn = bgOn && (lc & 0x20) != 0 && y >= cap.Wy[y] && cap.Wx[y] <= 166;
            winLine[y] = winOn ? wl++ : -1;
            if (winOn)
            {
                windowShown = true;
                win.On[y] = true; win.ScrollX[y] = (short)(-wx & 255); win.ScrollY[y] = (short)((winLine[y] - y) & 255); win.ZLow[y] = zBg; win.ZHigh[y] = zBgHigh;
                if (!anyHidden) { Array.Clear(bg.Hidden); Array.Clear(win.Hidden); anyHidden = true; }
                for (int x = 0; x < W; x++) { bool inWin = x >= wx; win.Hidden[y * ExtPicture.Stride + x] = !inWin; bg.Hidden[y * ExtPicture.Stride + x] = inWin; }
            }
            else if (anyHidden) for (int x = 0; x < W; x++) { bg.Hidden[y * ExtPicture.Stride + x] = false; win.Hidden[y * ExtPicture.Stride + x] = true; }
            if ((lc & 0x02) != 0)
            {
                e.ObjOn[y] = true;
                e.ObjZ[y * 4 + 0] = 4;     // behind the background's colours 1-3 (OAM flag bit 7), over colour 0 and the backdrop
                e.ObjZ[y * 4 + 1] = 10;    // in front of the background
            }
        }
        bg.AnyHidden = win.AnyHidden = anyHidden;
        if (!windowShown) win.Enabled = false;
        // ---- tiles: each map cell's tile (bank, flips baked in), the colour index its level
        tileOf.Clear(); int next = 1;
        void Cells(ExtPicture.Layer L, int map)
        {
            for (int cell = 0; cell < 1024; cell++)
            {
                int t = vram[map + cell]; byte attr = cgb ? vram[0x2000 + map + cell] : (byte)0;
                int addr = unsignedTiles ? t * 16 : 0x1000 + (sbyte)t * 16;
                if ((attr & 0x08) != 0) addr += 0x2000;
                bool hf = (attr & 0x20) != 0, vf = (attr & 0x40) != 0;
                int key = addr | (hf ? 1 << 15 : 0) | (vf ? 1 << 16 : 0);
                if (!tileOf.TryGetValue(key, out int id))
                {
                    id = next < ExtPicture.MaxTiles ? next++ : 1;
                    tileOf[key] = id;
                    for (int r = 0; r < 8; r++)
                    {
                        int rr = vf ? 7 - r : r;
                        int lo = vram[(addr + rr * 2) & 0x3FFF], hi = vram[(addr + rr * 2 + 1) & 0x3FFF];
                        for (int x = 0; x < 8; x++) { int bit = hf ? x : 7 - x; L.Tiles[id * 64 + r * 8 + x] = (byte)((lo >> bit & 1) | (hi >> bit & 1) << 1); }
                    }
                }
                L.Map[cell] = (ushort)id; L.Attr[cell] = (byte)((cgb ? attr & 7 : 0) | ((attr & 0x80) != 0 ? 8 : 0));
            }
        }
        Cells(bg, bgMap);
        bgTilesUsed = next - 1;
        // the two layers keep their own tile tables: the window's are numbered again
        tileOf.Clear(); next = 1;
        if (win.Enabled) Cells(win, winMap);
        // ---- sprites: the first 10 a line in OAM order (as the Game Boy picks them), in front in X order (DMG) or OAM order (GBC)
        bool tall = (lcdc & 0x04) != 0; int hgt = tall ? 16 : 8;
        Array.Clear(selected);
        for (int y = 0; y < H; y++)
        {
            int count = 0;
            for (int i = 0; i < 40 && count < 10; i++)
            {
                int sy = ppu.Oam[i * 4] - 16;
                if (y >= sy && y < sy + hgt) { selected[y * 40 + i] = 1; count++; }
            }
        }
        order.Clear();
        for (int i = 0; i < 40; i++) order.Add((ppu.Oam[i * 4 + 1], i));
        if (!cgb) order.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.oam.CompareTo(b.oam));
        objOf.Clear(); int n = 0;
        if (e.Sprites.Length < 256) e.Sprites = new ExtPicture.Sprite[256];
        foreach (var (_, i) in order)
        {
            int sy = ppu.Oam[i * 4] - 16, sx = ppu.Oam[i * 4 + 1] - 8, tile = ppu.Oam[i * 4 + 2], f = ppu.Oam[i * 4 + 3];
            if (sy <= -hgt || sy >= H || sx <= -8 || sx >= W) continue;
            int pal = cgb ? f & 7 : (f >> 4) & 1, bank = cgb && (f & 0x08) != 0 ? 0x2000 : 0;
            bool hf = (f & 0x20) != 0, vf = (f & 0x40) != 0;
            for (int piece = 0; piece < (tall ? 2 : 1); piece++)
            {
                int t = tall ? (tile & 0xFE) + (vf ? 1 - piece : piece) : tile;
                int addr = bank + t * 16, py = sy + piece * 8;
                if (!objOf.TryGetValue(addr, out int id))
                {
                    id = objOf.Count; objOf[addr] = id;
                    for (int r = 0; r < 8; r++)
                    {
                        int lo = vram[(addr + r * 2) & 0x3FFF], hi = vram[(addr + r * 2 + 1) & 0x3FFF];
                        for (int x = 0; x < 8; x++) e.SpriteTiles[id * 64 + r * 8 + x] = (byte)((lo >> (7 - x) & 1) | (hi >> (7 - x) & 1) << 1);
                    }
                }
                // one piece for every run of lines on which the Game Boy picked this sprite
                int from = -1;
                for (int y = Math.Max(0, py); y <= Math.Min(H, py + 8); y++)
                {
                    bool on = y < Math.Min(H, py + 8) && selected[y * 40 + i] != 0;
                    if (on && from < 0) from = y;
                    if (!on && from >= 0 && n < e.Sprites.Length)
                    {
                        e.Sprites[n++] = new ExtPicture.Sprite { X = (short)sx, Y = (short)py, FirstLine = (short)from, LastLine = (short)(y - 1), Tile = (ushort)id, Palette = (byte)pal,
                            Priority = (byte)((f & 0x80) != 0 && !(cgb && (lcdc & 0x01) == 0) ? 0 : 1), HFlip = hf, VFlip = vf };
                        from = -1;
                    }
                }
            }
        }
        e.SpriteCount = n; e.SpriteLimit = 1024; e.SpritesPerLine = 64;      // the Game Boy's own 10 a line were applied above
        e.PaletteVersion++;
        layersUsed = e.LayersInUse; piecesUsed = n;
        chip.DrawLayers();
        return Frame.From(chip);
    }

    private static byte Rev(byte b) { b = (byte)((b & 0xF0) >> 4 | (b & 0x0F) << 4); b = (byte)((b & 0xCC) >> 2 | (b & 0x33) << 2); return (byte)((b & 0xAA) >> 1 | (b & 0x55) << 1); }
}

/// <summary>
/// MIX LAB: a Game Boy picture drawn by a SNES PPU (any <see cref="ISnesPpuCore"/>) - the Super Game Boy idea
/// without the ICD2: Game Boy tiles are already in the SNES 2bpp format, so they are copied as-is. SNES mode 0:
/// BG1 = the Game Boy background, BG2 = the window (clipped with SNES window 1), OBJ = sprites (4bpp, 8x16 = two
/// 8x8). Game Boy Color palettes are BGR555 like CGRAM and are copied verbatim; DMG shades become greens. Every SNES
/// line takes the scroll of its Game Boy line, so raster effects carry over exactly. The Game Boy screen sits at
/// (48,40) of the 256x224 picture; with bleed on the rest shows the map beyond its edges.
/// </summary>
internal sealed class GbToSnes
{
    private readonly ISnesPpuCore back;
    public bool Bleed = true;
    public GbToSnes(string snesPpuId) { back = SnesCores.CreatePpu(snesPpuId); }
    public string PpuId => back.Id;

    private const int Map1 = 0x3000, Map2 = 0x3400, ObjBase = 0x4000;

    public uint[] Render(BOARD_GB board, GbLineCapture cap)
    {
        var ppu = board.Ppu; var gv = ppu.Vram; bool cgb = board.CgbMode;
        var vram = back.Vram; var cg = back.Cgram; var oam = back.Oam;
        // Tiles: 384 per bank, same bit layout (row = plane 0 byte, plane 1 byte = one SNES word).
        int banks = cgb ? 2 : 1;
        for (int bank = 0; bank < banks; bank++)
            for (int t = 0; t < 384; t++)
                for (int r = 0; r < 8; r++)
                {
                    int a = bank * 0x2000 + t * 16 + r * 2;
                    ushort w = (ushort)(gv[a] | gv[a + 1] << 8);
                    vram[(bank * 384 + t) * 8 + r] = w;
                    if (t < 256) { vram[ObjBase + (bank * 256 + t) * 16 + r] = w; vram[ObjBase + (bank * 256 + t) * 16 + 8 + r] = 0; }
                }
        const int Ref = 72;
        byte lcdc = cap.Lcdc[Ref];
        void Map(int snesBase, int gbMap)
        {
            for (int i = 0; i < 1024; i++)
            {
                int tile = gv[gbMap + i]; byte attr = cgb ? gv[0x2000 + gbMap + i] : (byte)0;
                int idx = (lcdc & 0x10) != 0 ? tile : (tile < 128 ? 256 + tile : tile);
                if ((attr & 0x08) != 0) idx += 384;
                vram[snesBase + i] = (ushort)(idx | (attr & 7) << 10 | ((attr & 0x80) != 0 ? 0x2000 : 0) | ((attr & 0x20) != 0 ? 0x4000 : 0) | ((attr & 0x40) != 0 ? 0x8000 : 0));
            }
        }
        Map(Map1, (lcdc & 0x08) != 0 ? 0x1C00 : 0x1800);
        Map(Map2, (lcdc & 0x40) != 0 ? 0x1C00 : 0x1800);
        // Palettes: BG1 uses CGRAM 0-31 and BG2 32-63 in mode 0; OBJ palettes at 128 + 16p.
        for (int p = 0; p < 8; p++)
            for (int c = 0; c < 4; c++)
            {
                int bsh = GbLook.Background(GbShades.Shade(cap.Bgp[Ref], c)), osh = GbLook.Sprite(GbShades.Shade(p == 1 ? cap.Obp1[Ref] : cap.Obp0[Ref], c));
                ushort bgc = cgb ? GbLook.BackgroundColour(GbShades.Cgb(ppu.BgPalRam, p, c)) : ppu.CompatMode ? GbShades.Cgb(ppu.BgPalRam, 0, bsh) : GbShades.Bgr555(bsh);
                ushort obc = cgb ? GbLook.SpriteColour(GbShades.Cgb(ppu.ObjPalRam, p, c)) : ppu.CompatMode ? GbShades.Cgb(ppu.ObjPalRam, p == 1 ? 1 : 0, osh) : GbShades.Bgr555(osh);
                cg[p * 4 + c] = bgc; cg[32 + p * 4 + c] = bgc; cg[128 + p * 16 + c] = obc;
            }
        // Sprites.
        Array.Clear(oam);
        bool tall = (lcdc & 0x04) != 0; int n = 0;
        for (int i = 0; i < 40 && n < 128; i++)
        {
            int y = ppu.Oam[i * 4] - 16, x = ppu.Oam[i * 4 + 1] - 8, tile = ppu.Oam[i * 4 + 2], f = ppu.Oam[i * 4 + 3];
            int h = tall ? 16 : 8;
            if (y <= -h || y >= 144 || x <= -8 || x >= 160) continue;
            int pal = cgb ? f & 7 : (f >> 4) & 1, bank = cgb && (f & 0x08) != 0 ? 256 : 0;
            bool vf = (f & 0x40) != 0;
            int attr = pal << 1 | ((f & 0x80) != 0 ? 1 : 2) << 4 | ((f & 0x20) != 0 ? 0x40 : 0) | (vf ? 0x80 : 0);
            if (!tall) Put(n++, 48 + x, 40 + y, bank + tile, attr);
            else
            {
                int top = tile & 0xFE;
                Put(n++, 48 + x, 40 + y, bank + (vf ? top + 1 : top), attr);
                Put(n++, 48 + x, 40 + y + 8, bank + (vf ? top : top + 1), attr);
            }
        }
        for (; n < 128; n++) Put(n, 0, 240, 0, 0);
        void Put(int k, int x, int y, int t, int attr)
        {
            oam[k * 4] = (byte)x; oam[k * 4 + 1] = (byte)y; oam[k * 4 + 2] = (byte)t; oam[k * 4 + 3] = (byte)(attr | (t >> 8) & 1);
            int hi = 512 + (k >> 2), sh = (k & 3) * 2; oam[hi] = (byte)((oam[hi] & ~(3 << sh)) | ((x >> 8) & 1) << sh);
        }
        back.InvalidateCaches();
        back.WriteRegister(0x00, 0x8F);
        back.WriteRegister(0x05, 0x00);                          // mode 0, 8x8 tiles
        back.WriteRegister(0x07, Map1 >> 8); back.WriteRegister(0x08, Map2 >> 8);   // 32x32 maps
        back.WriteRegister(0x0B, 0x00);                          // BG1/BG2 chr at word 0
        back.WriteRegister(0x01, ObjBase >> 13);                 // OBJ 8x8 / 16x16, tiles at 0x4000, no gap
        back.WriteRegister(0x33, 0x00);
        back.WriteRegister(0x00, (lcdc & 0x80) != 0 ? (byte)0x0F : (byte)0x8F);
        back.BeginFrame();
        for (int L = 1; L <= 224; L++)
        {
            int gl = L - 1 - 40, line = cap.Clamp(gl); bool inside = gl >= 0 && gl < 144;
            byte lc = cap.Lcdc[line];
            int h1 = (cap.Scx[line] - 48) & 0x3FF, v1 = (cap.Scy[line] - 41) & 0x3FF;
            back.WriteRegister(0x0D, (byte)h1); back.WriteRegister(0x0D, (byte)(h1 >> 8));
            back.WriteRegister(0x0E, (byte)v1); back.WriteRegister(0x0E, (byte)(v1 >> 8));
            bool win = inside && (lc & 0x20) != 0 && gl >= cap.Wy[line] && cap.Wx[line] <= 166;
            int wx = cap.Wx[line];
            int h2 = (-(41 + wx)) & 0x3FF, v2 = (-41 - cap.Wy[line]) & 0x3FF;
            back.WriteRegister(0x0F, (byte)h2); back.WriteRegister(0x0F, (byte)(h2 >> 8));
            back.WriteRegister(0x10, (byte)v2); back.WriteRegister(0x10, (byte)(v2 >> 8));
            back.WriteRegister(0x26, (byte)Math.Max(0, 48 + wx - 7)); back.WriteRegister(0x27, 207);
            back.WriteRegister(0x23, 0x30);                      // BG2: window 1 enabled, inverted (visible only inside)
            back.WriteRegister(0x2E, 0x02);                      // apply the window to BG2 on the main screen
            bool bg = cgb || (lc & 0x01) != 0, obj = (lc & 0x02) != 0;
            if (!inside && !Bleed) bg = obj = false;
            back.WriteRegister(0x2C, (byte)((bg ? 0x01 : 0) | (win ? 0x02 : 0) | (obj ? 0x10 : 0)));
            back.RenderLine(L);
        }
        back.OnVBlankStart();
        return back.FrameBuffer;
    }
}
