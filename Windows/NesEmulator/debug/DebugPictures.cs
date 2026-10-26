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
