using System;
using System.Collections.Generic;
using System.Linq;
using NesEmulator.Corruption;

namespace NesEmulator.Debugging;

/// <summary>
/// The pictures the debug viewers draw from memory (BrokenDev D8): the tile sheet of the NES's pattern tables, the SNES's VRAM and the Game
/// Boy's tile data. One decoder for the desktop's Tiles window and the MCP server's <c>tiles</c> tool. It only reads the memory domains.
/// </summary>
public static class DebugPictures
{
    public sealed class TileSheet
    {
        public uint[] Pixels = Array.Empty<uint>();   // ARGB
        public byte[] Indices = Array.Empty<byte>();  // the colour index of each pixel (what the tile data says, before the palette)
        public int Width, Height, Tiles, TileBytes, Columns = 16;
        public long Start;
        public string What = "";
        public string Colours = "";
        public int? Palette;
    }

    public static uint Bgr555(int v) { int r = v & 31, g = v >> 5 & 31, b = v >> 10 & 31; return 0xFF000000u | (uint)(r << 3 | r >> 2) << 16 | (uint)(g << 3 | g >> 2) << 8 | (uint)(b << 3 | b >> 2); }
    public static readonly uint[] Greys = { 0xFF000000, 0xFF555555, 0xFFAAAAAA, 0xFFFFFFFF };

    /// <summary>One sprite of the OAM list (what the hardware's attribute bytes say; the fields a console has not are left at their defaults).</summary>
    public sealed class SpriteInfo
    {
        public int Index, X, Y, Width = 8, Height = 8, Tile, TileDigits = 2, Palette, Flags;
        public bool Behind, FlipX, FlipY, Large;
        public int Priority, DmgPalette, CgbPalette, VramBank;
        public override string ToString() =>
            $"#{Index,-3} x {X,4} y {Y,4} {Width}x{Height} tile ${Tile.ToString(TileDigits == 3 ? "X3" : "X2")} pal {Palette}" +
            (Behind ? " behind" : "") + (FlipX ? " flipX" : "") + (FlipY ? " flipY" : "") + (Priority != 0 ? $" pri {Priority}" : "");
    }

    /// <summary>The sprites on screen, from OAM: the NES's 64 (y below $EF), the Game Boy's 40 (in the 160x144 picture), the SNES's 128 (y not
    /// 224-239). <paramref name="note"/> says what the boxes cannot know (sizes held in write-only registers).</summary>
    public static List<SpriteInfo> Sprites(DebugMachine m, out string note)
    {
        var list = new List<SpriteInfo>();
        var doms = m.Domains();
        IMemoryDomain Need(string id) => doms.FirstOrDefault(x => x.Id == id && x.Unavailable == null && x.Size > 0) ?? throw new ArgumentException($"this machine has no {id} memory");
        switch (Family(m.Console))
        {
            case "nes":
            {
                var c = m.Main;
                var oamSpace = c.FindSpace("oam") ?? throw new ArgumentException("this NES has no OAM space in the debugger");
                for (int i = 0; i < 64; i++)
                {
                    int y = c.PeekSpace(oamSpace.Id, (uint)(i * 4)), tile = c.PeekSpace(oamSpace.Id, (uint)(i * 4 + 1)), attr = c.PeekSpace(oamSpace.Id, (uint)(i * 4 + 2)), x = c.PeekSpace(oamSpace.Id, (uint)(i * 4 + 3));
                    if (y >= 0xEF) continue;
                    list.Add(new SpriteInfo { Index = i, X = x, Y = y + 1, Tile = tile, Palette = attr & 3, Flags = attr, Behind = (attr & 0x20) != 0, FlipX = (attr & 0x40) != 0, FlipY = (attr & 0x80) != 0 });
                }
                note = "boxes are 8x8 (the 8x16 mode is in PPUCTRL, a write-only register)";
                return list;
            }
            case "gb":
            {
                var oam = Need("OAM"); var io = Need("IO");
                bool tall = (io.Peek(0x40) & 4) != 0;
                for (int i = 0; i < 40; i++)
                {
                    int y = oam.Peek(i * 4) - 16, x = oam.Peek(i * 4 + 1) - 8, tile = oam.Peek(i * 4 + 2), f = oam.Peek(i * 4 + 3);
                    if (y <= -16 || y >= 144 || x <= -8 || x >= 160) continue;
                    list.Add(new SpriteInfo { Index = i, X = x, Y = y, Height = tall ? 16 : 8, Tile = tile, Flags = f, Behind = (f & 0x80) != 0, FlipY = (f & 0x40) != 0, FlipX = (f & 0x20) != 0,
                                              DmgPalette = f >> 4 & 1, CgbPalette = f & 7, VramBank = f >> 3 & 1, Palette = m.Console == "gbc" ? f & 7 : f >> 4 & 1 });
                }
                note = "";
                return list;
            }
            case "snes":
            {
                var oam = Need("OAM");
                for (int i = 0; i < 128; i++)
                {
                    int lo = oam.Peek(i * 4), y = oam.Peek(i * 4 + 1), tile = oam.Peek(i * 4 + 2), attr = oam.Peek(i * 4 + 3);
                    int hi = oam.Peek(512 + i / 4) >> (i % 4 * 2) & 3;
                    int x = lo | (hi & 1) << 8; if (x >= 256) x -= 512;
                    if (y >= 224 && y < 240) continue;
                    bool large = (hi & 2) != 0;
                    list.Add(new SpriteInfo { Index = i, X = x, Y = y, Width = large ? 16 : 8, Height = large ? 16 : 8, Tile = tile | (attr & 1) << 8, TileDigits = 3, Palette = attr >> 1 & 7, Priority = attr >> 4 & 3,
                                              Flags = attr, FlipX = (attr & 0x40) != 0, FlipY = (attr & 0x80) != 0, Large = large });
                }
                note = "boxes are 8x8 or 16x16 for small or large: the real sizes are in OBSEL ($2101, write-only); y in 224-239 is off screen";
                return list;
            }
            default:
                throw new NotSupportedException($"the sprite list covers the NES, SNES, WideMess and Game Boy; not '{m.Console}' yet");
        }
    }

    /// <summary>The game's last picture as ARGB: a NES's frame buffer (256x240) or a console session's.</summary>
    public static (uint[] px, int w, int h) Frame(object? game)
    {
        if (game is NES n)
        {
            var fb = n.GetFrameBuffer();
            var a = new uint[256 * 240];
            for (int i = 0; i < a.Length && i * 4 + 2 < fb.Length; i++) a[i] = 0xFF000000u | (uint)fb[i * 4] << 16 | (uint)fb[i * 4 + 1] << 8 | fb[i * 4 + 2];
            return (a, 256, 240);
        }
        if (game is NesEmulator.Systems.IConsoleSession s)
        {
            int w = s.FrameWidth, h = s.FrameHeight;
            var px = new uint[w * h];
            Array.Copy(s.Frame, px, Math.Min(px.Length, s.Frame.Length));
            return (px, w, h);
        }
        return (Array.Empty<uint>(), 0, 0);
    }

    /// <summary>Draws each sprite's box (magenta) onto a picture.</summary>
    public static void DrawBoxes(uint[] px, int w, int h, IEnumerable<SpriteInfo> sprites, uint colour = 0xFFFF00FF)
    {
        foreach (var s in sprites)
            for (int k = 0; k < s.Width; k++)
                for (int e = 0; e < s.Height; e++)
                {
                    if (k != 0 && k != s.Width - 1 && e != 0 && e != s.Height - 1) continue;
                    int X = s.X + k, Y = s.Y + e;
                    if (X >= 0 && X < w && Y >= 0 && Y < h) px[Y * w + X] = colour;
                }
    }

    /// <summary>A tile map drawn whole: the NES's four nametables, a SNES background layer, the Game Boy's background or window map.</summary>
    public sealed class MapView
    {
        public uint[] Pixels = Array.Empty<uint>();
        public int Width, Height, CellsX, CellsY, CellSize = 8;
        /// <summary>The visible area (the scroll), or Width 0 when the map has none (the Game Boy's window).</summary>
        public int ViewX, ViewY, ViewW, ViewH;
        public string What = "";
        /// <summary>(tile, palette, flags text) of a cell.</summary>
        public Func<int, int, (int tile, int palette, string flags)> Cell = (_, _) => (0, 0, "");
    }

    /// <summary>The layers <see cref="Map"/> can draw for a console family.</summary>
    public static string[] MapLayers(string family) => Family(family) switch
    {
        "nes" => new[] { "nametables" },
        "snes" => new[] { "bg1", "bg2", "bg3", "bg4" },
        "gb" => new[] { "background", "window" },
        _ => Array.Empty<string>(),
    };

    /// <summary>
    /// Draws a tile map from video memory and the registers the hardware reads it with. NES: the four nametables at $2000-$2FFF through the
    /// mirroring, the background pattern table and the scroll from the PPU's PPUCTRL and t (PPU_FIX; another core says so). SNES: layer
    /// BG1-BG4 with BGMODE's bits a pixel, BGnSC's map and size, the NBA's character base, 8x8 or 16x16 tiles, CGRAM's colours, the scroll
    /// (mode 7 is not drawn: said). Game Boy: the background or window map LCDC picks, its tile data, BGP's greys or the Color's palettes and
    /// attributes, SCX / SCY for the background.
    /// </summary>
    public static MapView Map(DebugMachine m, object? game, string layer)
    {
        var doms = m.Domains();
        IMemoryDomain? Find(string id) => doms.FirstOrDefault(x => x.Id == id && x.Unavailable == null && x.Size > 0);
        IMemoryDomain Need(string id) => Find(id) ?? throw new ArgumentException($"this machine has no {id} memory");
        switch (Family(m.Console))
        {
            case "nes":
            {
                var ppu = Need("PPU");
                var view = (game as NES)?.DebugBus?.ppu as IPpuViewProbe;
                int ctrl = view?.ProbeCtrl ?? 0;
                int bgTable = (ctrl >> 4 & 1) * 0x1000;
                var palette = Enumerable.Range(0, 16).Select(i => 0xFF000000u | (uint)NesEmulator.Mix.NesPalette.Rgb[ppu.Peek(0x3F00 + ((i & 3) == 0 ? 0 : i)) & 0x3F]).ToArray();
                const int W = 512, H = 480;
                var px = new uint[W * H];
                (int tile, int pal) At(int cx, int cy)
                {
                    int nt = (cx >> 5) + (cy / 30) * 2, x = cx & 31, y = cy % 30;
                    int baseAddr = 0x2000 + nt * 0x400;
                    int tile = ppu.Peek(baseAddr + y * 32 + x);
                    int attr = ppu.Peek(baseAddr + 0x3C0 + (y >> 2) * 8 + (x >> 2));
                    int pal = attr >> (((y >> 1) & 1) * 4 + ((x >> 1) & 1) * 2) & 3;
                    return (tile, pal);
                }
                for (int cy = 0; cy < 60; cy++)
                    for (int cx = 0; cx < 64; cx++)
                    {
                        var (tile, pal) = At(cx, cy);
                        int pat = bgTable + tile * 16;
                        for (int y = 0; y < 8; y++)
                        {
                            int lo = ppu.Peek(pat + y), hi = ppu.Peek(pat + y + 8);
                            for (int x = 0; x < 8; x++)
                            {
                                int v = (lo >> (7 - x) & 1) | (hi >> (7 - x) & 1) << 1;
                                px[(cy * 8 + y) * W + cx * 8 + x] = palette[v == 0 ? 0 : pal * 4 + v];
                            }
                        }
                    }
                int t = view?.ProbeT ?? 0;
                int fineX = view?.ProbeFineX ?? 0;
                int sx = ((t >> 10) & 1) * 256 + (t & 31) * 8 + fineX, sy = ((t >> 11) & 1) * 240 + ((t >> 5) & 31) * 8 + ((t >> 12) & 7);
                return new MapView
                {
                    Pixels = px, Width = W, Height = H, CellsX = 64, CellsY = 60, ViewX = sx, ViewY = sy, ViewW = 256, ViewH = 240,
                    What = view == null ? "the four nametables (this PPU core shows neither PPUCTRL nor the scroll: pattern table 0, no outline)" : $"the four nametables, background patterns at ${bgTable:X4}, scroll {sx},{sy}",
                    Cell = (cx, cy) => { var a = At(cx, cy); return (a.tile, a.pal, ""); },
                };
            }
            case "gb":
            {
                var vram = Need("VRAM"); var io = Need("IO");
                bool window = string.Equals(layer, "window", StringComparison.OrdinalIgnoreCase);
                int lcdc = io.Peek(0x40);
                int mapBase = (window ? (lcdc & 0x40) != 0 : (lcdc & 0x08) != 0) ? 0x1C00 : 0x1800;
                bool unsigned8000 = (lcdc & 0x10) != 0;
                int bgp = io.Peek(0x47);
                var cram = Find("CRAM");
                bool color = cram != null && vram.Size >= 0x4000;
                uint Grey(int shade) => shade switch { 0 => 0xFFFFFFFF, 1 => 0xFFAAAAAA, 2 => 0xFF555555, _ => 0xFF000000 };
                const int W = 256, H = 256;
                var px = new uint[W * H];
                (int tile, int pal, int attr) At(int cx, int cy)
                {
                    int i = mapBase + cy * 32 + cx;
                    int attr = color ? vram.Peek(0x2000 + i) : 0;
                    return (vram.Peek(i), attr & 7, attr);
                }
                for (int cy = 0; cy < 32; cy++)
                    for (int cx = 0; cx < 32; cx++)
                    {
                        var (tile, pal, attr) = At(cx, cy);
                        int data = (unsigned8000 ? tile * 16 : 0x1000 + (sbyte)(byte)tile * 16) + ((attr >> 3 & 1) * 0x2000);
                        for (int y = 0; y < 8; y++)
                        {
                            int ry = (attr & 0x40) != 0 ? 7 - y : y;
                            int lo = vram.Peek(data + ry * 2), hi = vram.Peek(data + ry * 2 + 1);
                            for (int x = 0; x < 8; x++)
                            {
                                int bit = (attr & 0x20) != 0 ? x : 7 - x;
                                int v = (lo >> bit & 1) | (hi >> bit & 1) << 1;
                                px[(cy * 8 + y) * W + cx * 8 + x] = color ? Bgr555(cram!.Peek(pal * 8 + v * 2) | cram.Peek(pal * 8 + v * 2 + 1) << 8) : Grey(bgp >> (2 * v) & 3);
                            }
                        }
                    }
                int scx = io.Peek(0x43), scy = io.Peek(0x42);
                return new MapView
                {
                    Pixels = px, Width = W, Height = H, CellsX = 32, CellsY = 32,
                    ViewX = window ? 0 : scx, ViewY = window ? 0 : scy, ViewW = window ? 0 : 160, ViewH = window ? 0 : 144,
                    What = $"the {(window ? "window" : "background")} map at ${0x8000 + mapBase:X4}, tiles from ${(unsigned8000 ? 0x8000 : 0x8800):X4}" + (window ? $", the window at {io.Peek(0x4B) - 7},{io.Peek(0x4A)}" : $", scroll {scx},{scy}"),
                    Cell = (cx, cy) => { var a = At(cx, cy); return (a.tile, a.pal, color ? $"attr ${a.attr:X2}" : ""); },
                };
            }
            case "snes":
            {
                var vram = Need("VRAM"); var cg = Need("CGRAM");
                var board = (game as NesEmulator.Systems.SnesSession)?.Board;
                if (board == null) throw new ArgumentException("the SNES map needs the running SNES session");
                var r = board.Ppu.GetRegisterSnapshot();
                int n = layer?.ToLowerInvariant() switch { "bg2" => 1, "bg3" => 2, "bg4" => 3, _ => 0 };
                int mode = r.Bgmode & 7;
                if (mode == 7) throw new ArgumentException("mode 7 is not drawn by the map viewer yet (its one 128x128 map of 8bpp tiles needs its own drawing)");
                int[][] bppOf = { new[] { 2, 2, 2, 2 }, new[] { 4, 4, 2, 0 }, new[] { 4, 4, 0, 0 }, new[] { 8, 4, 0, 0 }, new[] { 8, 2, 0, 0 }, new[] { 4, 2, 0, 0 }, new[] { 4, 0, 0, 0 } };
                int bpp = bppOf[mode][n];
                if (bpp == 0) throw new ArgumentException($"mode {mode} has no BG{n + 1}");
                int sc = n switch { 0 => r.Bg1sc, 1 => r.Bg2sc, 2 => r.Bg3sc, _ => r.Bg4sc };
                int mapBase = (sc >> 2) * 0x800;   // bytes: BGnSC's base is in 1K-word steps
                int scW = (sc & 1) != 0 ? 64 : 32, scH = (sc & 2) != 0 ? 64 : 32;
                int nba = n < 2 ? (r.Bg12nba >> (n * 4) & 15) : (r.Bg34nba >> ((n - 2) * 4) & 15);
                int charBase = nba * 0x2000;       // bytes: 4K-word steps
                bool big = (r.Bgmode >> (4 + n) & 1) != 0;
                int cell = big ? 16 : 8;
                int palBase = mode == 0 ? n * 32 : 0;
                int colours = 1 << bpp, tileBytes = 8 * bpp;
                int W = scW * cell, H = scH * cell;
                var px = new uint[W * H];
                int Entry(int cx, int cy)
                {
                    int sx = cx >> 5, sy = cy >> 5;              // which 32x32 screen
                    int screen = sx + sy * (scW == 64 ? 2 : 1);
                    int at = mapBase + screen * 0x800 + ((cy & 31) * 32 + (cx & 31)) * 2;
                    return vram.Peek(at & (vram.Size - 1)) | vram.Peek((at + 1) & (vram.Size - 1)) << 8;
                }
                for (int cy = 0; cy < scH; cy++)
                    for (int cx = 0; cx < scW; cx++)
                    {
                        int e = Entry(cx, cy);
                        int tile = e & 0x3FF, pal = e >> 10 & 7;
                        bool fx = (e & 0x4000) != 0, fy = (e & 0x8000) != 0;
                        for (int y = 0; y < cell; y++)
                            for (int x = 0; x < cell; x++)
                            {
                                int lx = fx ? cell - 1 - x : x, ly = fy ? cell - 1 - y : y;
                                int sub = tile + (lx >> 3) + (ly >> 3) * 16;   // a 16x16 tile is four 8x8 tiles: t, t+1, t+16, t+17
                                int at = charBase + (sub & 0x3FF) * tileBytes;
                                int bit = 7 - (lx & 7), row = ly & 7, v = 0;
                                for (int p = 0; p < bpp; p++) v |= (vram.Peek((at + (p / 2) * 16 + row * 2 + (p & 1)) & (vram.Size - 1)) >> bit & 1) << p;
                                int ci = v == 0 ? 0 : (bpp == 8 ? v : palBase + pal * colours + v);
                                px[(cy * cell + y) * W + cx * cell + x] = Bgr555(cg.Peek(ci * 2) | cg.Peek(ci * 2 + 1) << 8);
                            }
                    }
                int hofs = n switch { 0 => r.Bg1hofs, 1 => r.Bg2hofs, 2 => r.Bg3hofs, _ => r.Bg4hofs } % W;
                int vofs = n switch { 0 => r.Bg1vofs, 1 => r.Bg2vofs, 2 => r.Bg3vofs, _ => r.Bg4vofs } % H;
                return new MapView
                {
                    Pixels = px, Width = W, Height = H, CellsX = scW, CellsY = scH, CellSize = cell, ViewX = hofs, ViewY = vofs, ViewW = 256, ViewH = 224,
                    What = $"BG{n + 1} in mode {mode}: {bpp} bits a pixel, {scW}x{scH} {cell}x{cell} tiles, map at VRAM ${mapBase:X4}, characters at ${charBase:X4}, scroll {hofs},{vofs}",
                    Cell = (cx, cy) => { int e = Entry(cx, cy); return (e & 0x3FF, e >> 10 & 7, $"entry ${e:X4}{((e & 0x2000) != 0 ? " priority" : "")}{((e & 0x4000) != 0 ? " flipX" : "")}{((e & 0x8000) != 0 ? " flipY" : "")}"); },
                };
            }
            default:
                throw new NotSupportedException($"the map viewer covers the NES, SNES, WideMess and Game Boy; not '{m.Console}' yet");
        }
    }

    /// <summary>Outlines the visible area on a map, wrapping at its edges like the hardware's scroll.</summary>
    public static void DrawView(MapView v, uint colour = 0xFFFF00FF)
    {
        if (v.ViewW == 0) return;
        for (int k = 0; k < v.ViewW; k++)
            for (int e = 0; e < v.ViewH; e++)
            {
                if (k != 0 && k != v.ViewW - 1 && e != 0 && e != v.ViewH - 1) continue;
                int X = (v.ViewX + k) % v.Width, Y = (v.ViewY + e) % v.Height;
                v.Pixels[Y * v.Width + X] = colour;
            }
    }

    /// <summary>The console family a debug machine's <c>Console</c> belongs to for the viewers: nes, snes (also WideMess), gb (also the Color).</summary>
    public static string Family(string console) => console.ToLowerInvariant() switch { "widemess" or "wms" or "snes" => "snes", "gbc" or "gb" => "gb", var c => c };

    /// <summary>
    /// The tiles of <paramref name="family"/>'s video memory, 16 to a row. NES: the pattern tables as the PPU sees them now ($0000-$1FFF), or
    /// <paramref name="source"/> "chr" for the cartridge's CHR; greys (index 0 black). SNES: VRAM as 2, 4 or 8 bits a pixel, greys or CGRAM's
    /// palette <paramref name="palette"/>. Game Boy: VRAM tile data, greys (index 0 white) or the Color's background palette.
    /// </summary>
    public static TileSheet Tiles(IReadOnlyList<IMemoryDomain> domains, string family, string? source, int bpp, int palette, long start, int count)
    {
        IMemoryDomain? Find(string id) => domains.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && x.Unavailable == null && x.Size > 0);
        IMemoryDomain Need(string id) => Find(id) ?? throw new ArgumentException($"this machine has no {id} memory");
        Func<int, byte> peek; int size; int tileBytes; int planes; bool gbFormat = false, nes = false; string what; uint[] pal;
        switch (Family(family))
        {
            case "nes":
            {
                var d = string.Equals(source, "chr", StringComparison.OrdinalIgnoreCase) ? Need("CHR") : Find("PPU") ?? Need("CHR");
                peek = d.Peek; size = d.Id == "PPU" ? 0x2000 : d.Size; planes = 2; tileBytes = 16; nes = true;
                what = d.Id == "PPU" ? "the pattern tables as the PPU sees them now ($0000-$1FFF)" : "CHR";
                pal = Greys;
                break;
            }
            case "gb":
            {
                var d = Need("VRAM"); peek = d.Peek; size = Math.Min(d.Size, 0x4000); planes = 2; tileBytes = 16; gbFormat = true; what = "VRAM tile data";
                pal = Greys.Reverse().ToArray();
                var cram = domains.FirstOrDefault(x => x.Id == "CRAM" && x.Unavailable == null && x.Size >= 64);
                if (palette >= 0 && cram != null) pal = Enumerable.Range(0, 4).Select(i => Bgr555(cram.Peek(palette * 8 + i * 2) | cram.Peek(palette * 8 + i * 2 + 1) << 8)).ToArray();
                break;
            }
            case "snes":
            {
                var d = Need("VRAM"); peek = d.Peek; size = d.Size;
                planes = bpp is 2 or 4 or 8 ? bpp : 4; tileBytes = 8 * planes; what = $"VRAM as {planes}bpp tiles";
                int colours = 1 << planes;
                pal = planes == 2 ? Greys : Enumerable.Range(0, colours).Select(i => { uint g = (uint)(i * 255 / (colours - 1)); return 0xFF000000u | g << 16 | g << 8 | g; }).ToArray();
                var cg = Find("CGRAM");
                if (palette >= 0 && cg != null)
                {
                    int baseIdx = planes == 8 ? 0 : palette * colours;
                    pal = Enumerable.Range(0, colours).Select(i => { int at = (baseIdx + i) * 2; return Bgr555(cg.Peek(at) | cg.Peek(at + 1) << 8); }).ToArray();
                }
                break;
            }
            default:
                throw new NotSupportedException($"the tile viewer covers the NES, SNES, WideMess and Game Boy; not '{family}' yet");
        }

        long first = Math.Clamp(start, 0, Math.Max(0, size - tileBytes));
        int total = (int)((size - first) / tileBytes);
        int n = Math.Clamp(count <= 0 ? Math.Min(total, 512) : count, 1, Math.Max(1, Math.Min(total, 4096)));
        int cols = 16, rows = (n + cols - 1) / cols;
        int W = cols * 8, H = rows * 8;
        var px = new uint[W * H];
        var ix = new byte[W * H];
        for (int t = 0; t < n; t++)
        {
            int at = (int)first + t * tileBytes, tx = t % cols * 8, ty = t / cols * 8;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int bit = 7 - x, v = 0;
                    if (nes) v = (peek(at + y) >> bit & 1) | (peek(at + y + 8) >> bit & 1) << 1;
                    else if (gbFormat) v = (peek(at + y * 2) >> bit & 1) | (peek(at + y * 2 + 1) >> bit & 1) << 1;
                    else
                        for (int p = 0; p < planes; p++)
                        {
                            int pairBase = at + (p / 2) * 16;   // SNES: planes in pairs, each pair 16 bytes (rows interleaved)
                            v |= (peek(pairBase + y * 2 + (p & 1)) >> bit & 1) << p;
                        }
                    px[(ty + y) * W + tx + x] = pal[Math.Min(v, pal.Length - 1)];
                    ix[(ty + y) * W + tx + x] = (byte)v;
                }
        }
        return new TileSheet
        {
            Pixels = px, Indices = ix, Width = W, Height = H, Tiles = n, TileBytes = tileBytes, Columns = cols, Start = first, What = what,
            Palette = palette >= 0 && !nes ? palette : null,
            Colours = nes ? "greys (index 0 black)" : palette >= 0 ? "the game's palette" : "greys",
        };
    }
}
