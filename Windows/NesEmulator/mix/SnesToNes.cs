using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NesEmulator;
using NesEmulator.Snes;

namespace NesEmulator.Mix;

/// <summary>
/// MIX LAB: the SNES -> NES downgrade. After each SNES frame, whatever an <see cref="ISnesPpuCore"/> was
/// programmed to show is squeezed into what a NES PPU can hold, and a real NES PPU core (any id) draws it:
///   one background layer (BG1 only - BG2-4 have nowhere to go), 256 BG tiles and 256 sprite tiles,
///   4 background + 4 sprite palettes of 3 colours from the 2C02's 54, palettes per 16x16 block,
///   no background tile flipping (flips are baked into extra tiles), 64 sprites of 8x8, 8 per line.
/// Colours: each SNES colour index is reduced to one of 3 brightness levels of its own palette; the NES
/// palette slot gets the nearest 2C02 colour to each level's average. The 4 most used SNES palettes get
/// the 4 NES slots, the rest share slot 0. Tiles beyond 255 unique (tile, palette, flip) overflow onto
/// existing slots. The SNES picture is centred: NES rows 8-231 = SNES rows 0-223.
/// The NES side is a real NES running a 3-instruction idle ROM; the bridge programs its PPU through $2000-$2007.
/// </summary>
internal sealed class SnesToNes
{
    private readonly NesPictureSink sink;
    public readonly Stats Last = new();

    public sealed class Stats
    {
        public int UniqueBgTiles, BgOverflow, UniqueObjTiles, ObjOverflow, SnesPalettesUsed, SpritesWanted, SpritesKept;
        public string Note = "";
        /// <summary>Layered path (PPU_FIXS): per SNES layer, "BG1:123" = unique tiles it needed; empty on the flat path.</summary>
        public string Layers = "";
        public int LineDrops;
        public override string ToString() => Layers.Length == 0
            ? $"bg tiles {UniqueBgTiles} (overflow {BgOverflow}), obj tiles {UniqueObjTiles} (overflow {ObjOverflow}), " +
              $"snes bg palettes {SnesPalettesUsed}, sprites {SpritesKept}/{SpritesWanted} 8x8 pieces {Note}"
            : $"layers {Layers} (tile overflow {BgOverflow}), obj tiles {UniqueObjTiles} (overflow {ObjOverflow}), snes bg palettes {SnesPalettesUsed}, " +
              $"sprites {SpritesKept}/{SpritesWanted} 8x8 pieces (line-limit drops {LineDrops}) {Note}";
    }

    public SnesToNes(string nesPpuId) { sink = new NesPictureSink(nesPpuId); }

    /// <summary>True when the picture chip is a SNES-support one (PPU_FIXS, or a Game Boy bridge fronted by it): it is
    /// translated line by line from PPU_SFC.BridgeLines.</summary>
    public bool Layered => (sink.Ppu as PPU_FIXS ?? (sink.Ppu as PPU_DMG)?.LayerFront) != null;

    /// <summary>NROM, 16K PRG, CHR-RAM, vertical mirroring: JMP * forever, RTI for NMI/IRQ.</summary>
    internal static byte[] IdleRom()
    {
        var rom = new byte[16 + 0x4000];
        rom[0] = (byte)'N'; rom[1] = (byte)'E'; rom[2] = (byte)'S'; rom[3] = 0x1A; rom[4] = 1; rom[5] = 0; rom[6] = 0x01;
        int p = 16;
        rom[p + 0] = 0x4C; rom[p + 1] = 0x00; rom[p + 2] = 0xC0;   // $C000: JMP $C000
        rom[p + 3] = 0x40;                                         // $C003: RTI
        void Vec(int at, int addr) { rom[p + at] = (byte)addr; rom[p + at + 1] = (byte)(addr >> 8); }
        Vec(0x3FFA, 0xC003); Vec(0x3FFC, 0xC000); Vec(0x3FFE, 0xC003);
        return rom;
    }


    /// <summary>Program the NES PPU from the SNES PPU's current state, run one NES frame, return RGBA 256x240.</summary>
    public byte[] Render(ISnesPpuCore sp, SnesPpuSnapshot? midFrame = null)
    {
        var s = midFrame ?? sp.Snapshot();
        // A chip with SNES-support layers (PPU_FIXS, or a Game Boy bridge fronted by one) gets every layer and sprite.
        var layered = sink.Ppu as PPU_FIXS ?? (sink.Ppu as PPU_DMG)?.LayerFront;
        if (layered != null) return RenderLayered(sp, s, layered);
        var st = new Stats();
        var chrBg = new byte[256 * 16];
        var chrObj = new byte[256 * 16];
        var nt = new byte[2][]; nt[0] = new byte[1024]; nt[1] = new byte[1024];
        var pal = new byte[32];

        int mode = s.BgMode & 7;
        int bpp = mode switch { 0 => 2, 1 or 2 or 5 or 6 => 4, 3 or 4 => 8, _ => 0 };
        if (mode == 7) st.Note = "(BG1 is Mode 7 - not translated)";
        if ((s.BgMode & 0x10) != 0) st.Note += " (BG1 uses 16x16 tiles - read as 8x8)";
        bool blank = (s.Inidisp & 0x80) != 0 || (s.Inidisp & 0x0F) == 0;

        var cg = sp.Cgram; var vram = sp.Vram;
        int mapBase = (s.Bgsc[0] & 0xFC) << 8, size = s.Bgsc[0] & 3;
        bool wide = (size & 1) != 0, tall = (size & 2) != 0;
        int chrBase = (s.Bg12Nba & 0x0F) << 12;
        int hofs = s.Hofs[0], vofs = s.Vofs[0];
        int fx = hofs & 7, oy = vofs - 7, fy = ((oy % 8) + 8) % 8, baseTx = hofs >> 3, baseTy = (int)Math.Floor(oy / 8.0);

        ushort MapEntry(int tx, int ty)
        {
            tx &= wide ? 63 : 31; ty &= tall ? 63 : 31;
            int a = mapBase + (ty & 31) * 32 + (tx & 31);
            if (tx >= 32) a += 0x400;
            if (ty >= 32) a += wide ? 0x800 : 0x400;
            return vram[a & 0x7FFF];
        }
        int PalBase(int p) => bpp switch { 2 => p * 4, 4 => p * 16, _ => 0 };
        int Pixel(int tileAddr, int wordsPerTile, int r, int c)
        {
            int i = 0;
            for (int plane = 0; plane < wordsPerTile / 8; plane++)
            {
                ushort w = vram[(tileAddr + plane * 8 + r) & 0x7FFF];
                i |= ((w >> (7 - c)) & 1) << (plane * 2);
                i |= ((w >> (15 - c)) & 1) << (plane * 2 + 1);
            }
            return i;
        }

        // --- background: gather cells (33 x 30), pick palettes by 16x16 block
        var cells = new ushort[30, 33];
        var palCount = new Dictionary<int, int>();
        if (bpp > 0)
            for (int cy = 0; cy < 30; cy++) for (int cx = 0; cx < 33; cx++)
            {
                var e = MapEntry(baseTx + cx, baseTy + cy); cells[cy, cx] = e;
                int p = bpp == 8 ? 0 : (e >> 10) & 7; palCount[p] = palCount.GetValueOrDefault(p) + 1;
            }
        var bgSlots = palCount.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).Take(4).ToList();
        st.SnesPalettesUsed = palCount.Count;
        int SlotOf(List<int> slots, int p) { int i = slots.IndexOf(p); return i < 0 ? 0 : i; }

        // brightness level (1..3) of every colour of every palette
        int Level(ushort c) { var (r, g, b) = NesPalette.FromBgr555(c); int y = (r * 3 + g * 6 + b) / 10; return y < 80 ? 1 : y < 165 ? 2 : 3; }
        byte[] SlotColors(int cgBase, int count)
        {
            var sum = new (long r, long g, long b, int n)[4];
            for (int i = 1; i < count; i++) { ushort c = cg[(cgBase + i) & 0xFF]; int L = Level(c); var (r, g, b) = NesPalette.FromBgr555(c); sum[L].r += r; sum[L].g += g; sum[L].b += b; sum[L].n++; }
            var outp = new byte[4];
            byte[] fallback = { 0x0F, 0x00, 0x10, 0x30 };
            for (int L = 1; L < 4; L++) outp[L] = sum[L].n == 0 ? fallback[L] : NesPalette.Nearest((int)(sum[L].r / sum[L].n), (int)(sum[L].g / sum[L].n), (int)(sum[L].b / sum[L].n));
            return outp;
        }
        { var (r, g, b) = NesPalette.FromBgr555(cg[0]); pal[0] = NesPalette.Nearest(r, g, b); }
        for (int k = 0; k < 4; k++)
        {
            if (k >= bgSlots.Count) continue;
            var cols = SlotColors(PalBase(bgSlots[k]), bpp == 8 ? 256 : 1 << bpp);
            for (int L = 1; L < 4; L++) pal[k * 4 + L] = cols[L];
        }

        // --- background tiles
        var bgTiles = new Dictionary<int, int>();   // key -> NES tile
        int wpt = bpp * 4;                         // words per tile
        int nextBg = 1;
        int BgTile(ushort e)
        {
            int tile = e & 0x3FF, p = bpp == 8 ? 0 : (e >> 10) & 7; bool hf = (e & 0x4000) != 0, vf = (e & 0x8000) != 0;
            int key = tile | (p << 10) | (hf ? 1 << 13 : 0) | (vf ? 1 << 14 : 0);
            if (bgTiles.TryGetValue(key, out var t)) return t;
            if (nextBg > 255) { st.BgOverflow++; return 1 + tile % 255; }
            t = nextBg++; bgTiles[key] = t;
            int addr = chrBase + tile * wpt, pb = PalBase(p);
            for (int r = 0; r < 8; r++)
            {
                byte p0 = 0, p1 = 0;
                for (int c = 0; c < 8; c++)
                {
                    int i = Pixel(addr, wpt, vf ? 7 - r : r, hf ? 7 - c : c);
                    int L = i == 0 ? 0 : Level(cg[(pb + i) & 0xFF]);
                    p0 |= (byte)((L & 1) << (7 - c)); p1 |= (byte)(((L >> 1) & 1) << (7 - c));
                }
                chrBg[t * 16 + r] = p0; chrBg[t * 16 + 8 + r] = p1;
            }
            return t;
        }
        if (bpp > 0 && !blank)
        {
            for (int cy = 0; cy < 30; cy++) for (int cx = 0; cx < 33; cx++)
            {
                int t = BgTile(cells[cy, cx]);
                if (cx < 32) nt[0][cy * 32 + cx] = (byte)t; else nt[1][cy * 32] = (byte)t;
            }
            for (int by = 0; by < 15; by++) for (int bx = 0; bx < 17; bx++)
            {
                int cx = bx * 2, cy = by * 2; var e = cells[cy, Math.Min(cx, 32)];
                int slot = SlotOf(bgSlots, bpp == 8 ? 0 : (e >> 10) & 7);
                int table = cx < 32 ? 0 : 1, lx = cx & 31;
                int ai = 0x3C0 + (cy >> 2) * 8 + (lx >> 2), sh = ((cy & 2) << 1) | (lx & 2);
                nt[table][ai] = (byte)((nt[table][ai] & ~(3 << sh)) | (slot << sh));
            }
        }
        st.UniqueBgTiles = bgTiles.Count;

        // --- sprites
        (int w, int h)[] small = { (8, 8), (8, 8), (8, 8), (16, 16), (16, 16), (32, 32), (16, 32), (16, 32) };
        (int w, int h)[] large = { (16, 16), (32, 32), (64, 64), (32, 32), (64, 64), (64, 64), (32, 64), (32, 32) };
        int objBase = (s.Obsel & 7) << 13, gap = (((s.Obsel >> 3) & 3) + 1) << 12;
        var oamS = sp.Oam;
        var pieces = new List<(int x, int y, int tile, int pal, bool hf, bool vf, bool behind)>();
        var objPalCount = new Dictionary<int, int>();
        for (int i = 0; i < 128; i++)
        {
            int hi = (oamS[512 + (i >> 2)] >> ((i & 3) * 2)) & 3;
            int x = oamS[i * 4] | ((hi & 1) << 8); if (x >= 256) x -= 512;
            int y = oamS[i * 4 + 1], a = oamS[i * 4 + 3], tile = oamS[i * 4 + 2] | ((a & 1) << 8);
            var (w, h) = (hi & 2) != 0 ? large[(s.Obsel >> 5) & 7] : small[(s.Obsel >> 5) & 7];
            if (y >= 224 && y + h <= 256) continue;              // parked off the bottom
            if (y >= 224) y -= 256;
            if (x >= 256 || x + w <= 0) continue;
            int p = (a >> 1) & 7, prio = (a >> 4) & 3; bool hf = (a & 0x40) != 0, vf = (a & 0x80) != 0;
            for (int r = 0; r < h / 8; r++) for (int c = 0; c < w / 8; c++)
            {
                int tt = (tile & 0x100) | (((tile & 0xF0) + (r << 4)) & 0xF0) | (((tile & 0x0F) + c) & 0x0F);
                int sx = x + 8 * (hf ? w / 8 - 1 - c : c), sy = y + 8 * (vf ? h / 8 - 1 - r : r);
                if (sx <= -8 || sx >= 256 || sy <= -8 || sy >= 224) continue;
                pieces.Add((sx, sy, tt, p, hf, vf, prio == 0));
                objPalCount[p] = objPalCount.GetValueOrDefault(p) + 1;
            }
        }
        st.SpritesWanted = pieces.Count;
        var objSlots = objPalCount.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).Take(4).ToList();
        for (int k = 0; k < objSlots.Count; k++) { var cols = SlotColors(128 + objSlots[k] * 16, 16); for (int L = 1; L < 4; L++) pal[16 + k * 4 + L] = cols[L]; }
        var objTiles = new Dictionary<int, int>(); int nextObj = 0;
        int ObjTile(int tt, int p)
        {
            int key = tt | (p << 9);
            if (objTiles.TryGetValue(key, out var t)) return t;
            if (nextObj > 255) { st.ObjOverflow++; return tt % 256; }
            t = nextObj++; objTiles[key] = t;
            int addr = objBase + (tt >= 256 ? gap : 0) + (tt & 0xFF) * 16;
            for (int r = 0; r < 8; r++)
            {
                byte p0 = 0, p1 = 0;
                for (int c = 0; c < 8; c++)
                {
                    int i = Pixel(addr, 16, r, c);
                    int L = i == 0 ? 0 : Level(cg[128 + p * 16 + i]);
                    p0 |= (byte)((L & 1) << (7 - c)); p1 |= (byte)(((L >> 1) & 1) << (7 - c));
                }
                chrObj[t * 16 + r] = p0; chrObj[t * 16 + 8 + r] = p1;
            }
            return t;
        }
        var nesOam = Enumerable.Repeat((byte)0xFF, 256).ToArray();
        int kept = 0;
        foreach (var pc in pieces)
        {
            if (kept >= 64) break;
            int t = ObjTile(pc.tile, pc.pal);
            int o = kept * 4;
            nesOam[o] = (byte)(pc.y + 8 - 1); nesOam[o + 1] = (byte)t;
            nesOam[o + 2] = (byte)(SlotOf(objSlots, pc.pal) | (pc.behind ? 0x20 : 0) | (pc.hf ? 0x40 : 0) | (pc.vf ? 0x80 : 0));
            nesOam[o + 3] = (byte)pc.x;
            kept++;
        }
        st.SpritesKept = kept; st.UniqueObjTiles = objTiles.Count;

        var frame = sink.Present(new NesPicture { ChrBg = chrBg, ChrObj = chrObj, Nt0 = nt[0], Nt1 = nt[1], Pal = pal, Oam = nesOam, FineX = fx, FineY = fy, Blank = blank });
        Last.UniqueBgTiles = st.UniqueBgTiles; Last.BgOverflow = st.BgOverflow; Last.UniqueObjTiles = st.UniqueObjTiles; Last.ObjOverflow = st.ObjOverflow;
        Last.SnesPalettesUsed = st.SnesPalettesUsed; Last.SpritesWanted = st.SpritesWanted; Last.SpritesKept = st.SpritesKept; Last.Note = st.Note;
        Last.Layers = ""; Last.LineDrops = 0;
        return frame;
    }

    // =========================================================================================== the layered path
    /// <summary>Which BG layers a SNES mode has, and their bit depth (0 = none).</summary>
    private static int[] LayerBpp(int mode) => mode switch
    {
        0 => new[] { 2, 2, 2, 2 },
        1 => new[] { 4, 4, 2, 0 },
        2 => new[] { 4, 4, 0, 0 },
        3 => new[] { 8, 4, 0, 0 },
        4 => new[] { 8, 2, 0, 0 },
        5 => new[] { 4, 2, 0, 0 },
        6 => new[] { 4, 0, 0, 0 },
        _ => new[] { 0, 0, 0, 0 },   // mode 7: not translated
    };

    /// <summary>The SNES priority ladder (higher = in front) for a mode: z of each layer's priority-0 and priority-1 tiles.
    /// Sprites of priority 0-3 sit at 3, 6, 9, 12 in every mode (PPU_FIXS.SpriteZ).</summary>
    private static (byte lo, byte hi)[] Ladder(int mode, bool bg3Top) => mode switch
    {
        0 => new[] { ((byte)8, (byte)11), ((byte)7, (byte)10), ((byte)2, (byte)5), ((byte)1, (byte)4) },
        1 => new[] { ((byte)8, (byte)11), ((byte)7, (byte)10), ((byte)2, bg3Top ? (byte)13 : (byte)5), ((byte)0, (byte)0) },
        _ => new[] { ((byte)5, (byte)11), ((byte)2, (byte)8), ((byte)0, (byte)0), ((byte)0, (byte)0) },
    };

    /// <summary>
    /// A SNES frame into PPU_FIXS's SNES-support picture (<see cref="ExtPicture"/>), line by line: each captured line's
    /// registers decide which layers, Mode 7 and sprites are on there, their scroll, their place on the priority ladder
    /// (main-screen layers in front of sub-screen-only ones, which show through the backdrop only where colour math adds the
    /// sub screen), their window masks, and the backdrop (colour math applied, the fixed colour included). Layers with the
    /// same map, tiles and depth share one extension layer; a split screen gets one per part (at most 8). What the NES keeps
    /// limiting: 2-bit tiles (3 brightness levels per SNES palette) and 8 palettes of each kind - the most used SNES
    /// palettes get their own, the rest borrow the closest-looking one. No colour blending: math picks, it does not mix.
    /// </summary>
    private byte[] RenderLayered(ISnesPpuCore sp, SnesPpuSnapshot s, PPU_FIXS fx)
    {
        var st = new Stats();
        var cg = sp.Cgram; var vram = sp.Vram;
        var e = fx.Ext ??= new ExtPicture();
        e.BeginFrame();
        e.SpriteLimit = MixConfig.ExtSpriteLimit; e.SpritesPerLine = MixConfig.ExtSpritesPerLine;
        var lines = LinesOf(sp, s);
        const int N = ExtPicture.Lines, W = ExtPicture.Width;

        int Level(ushort c) { var (r, g, b) = NesPalette.FromBgr555(c); int y = (r * 3 + g * 6 + b) / 10; return y < 80 ? 1 : y < 165 ? 2 : 3; }
        int Pixel(int tileAddr, int bpp, int r, int c)
        {
            int i = 0;
            for (int plane = 0; plane < bpp / 2; plane++)
            {
                ushort w = vram[(tileAddr + plane * 8 + r) & 0x7FFF];
                i |= ((w >> (7 - c)) & 1) << (plane * 2);
                i |= ((w >> (15 - c)) & 1) << (plane * 2 + 1);
            }
            return i;
        }
        // Brightness: the frame's (the most common among its lit lines).
        var brightCount = new int[16];
        for (int y = 0; y < N; y++) { var ini = lines[y + 1].Regs.Inidisp; if ((ini & 0x80) == 0) brightCount[ini & 0x0F]++; }
        int bright = Array.IndexOf(brightCount, brightCount.Max());
        uint Rgb(ushort c, int b)
        {
            var (r, g, bl) = NesPalette.FromBgr555(c);
            return (uint)((r * b / 15) << 16 | (g * b / 15) << 8 | (bl * b / 15));
        }
        int RgbOf(ushort c) { var (r, g, b) = NesPalette.FromBgr555(c); return r << 16 | g << 8 | b; }
        uint Scaled(uint rgb) => (uint)(((int)(rgb >> 16 & 0xFF) * bright / 15) << 16 | ((int)(rgb >> 8 & 0xFF) * bright / 15) << 8 | ((int)(rgb & 0xFF) * bright / 15));

        // ---- windows (PPU_SFC.ComputeWindows): layer 0-3 BG, 4 OBJ, 5 colour window
        var win = new bool[W];
        bool Window(in PPU_SFC.BridgeLineState L, int layer, bool[] mask)
        {
            int sel = layer switch { 0 => L.W12sel & 0x0F, 1 => L.W12sel >> 4, 2 => L.W34sel & 0x0F, 3 => L.W34sel >> 4, 4 => L.Wobjsel & 0x0F, _ => L.Wobjsel >> 4 };
            int logic = layer < 4 ? (L.Wbglog >> (layer * 2)) & 3 : (L.Wobjlog >> ((layer - 4) * 2)) & 3;
            bool en1 = (sel & 0x02) != 0, en2 = (sel & 0x08) != 0;
            if (!en1 && !en2) { Array.Clear(mask); return false; }
            bool inv1 = (sel & 0x01) != 0, inv2 = (sel & 0x04) != 0, any = false;
            for (int x = 0; x < W; x++)
            {
                bool w1 = (x >= L.Wh0 && x <= L.Wh1) ^ inv1, w2 = (x >= L.Wh2 && x <= L.Wh3) ^ inv2;
                bool m = !en2 ? w1 : !en1 ? w2 : logic switch { 0 => w1 | w2, 1 => w1 & w2, 2 => w1 ^ w2, _ => !(w1 ^ w2) };
                mask[x] = m; any |= m;
            }
            return any;
        }
        // Colour-window regions: 0 never, 1 outside the colour window, 2 inside, 3 always.
        var colorWin = new bool[W];
        bool Region(int sel, int x) => sel == 3 || (sel == 1 && !colorWin[x]) || (sel == 2 && colorWin[x]);
        // Writes one line of a per-pixel mask, clearing the whole mask the first time anything is set this frame.
        void Mark(bool[] mask, ref bool any, int y, Func<int, bool> hidden)
        {
            bool lineAny = false;
            for (int x = 0; x < W && !lineAny; x++) lineAny = hidden(x);
            if (!lineAny && !any) return;
            if (!any) { Array.Clear(mask); any = true; }
            for (int x = 0; x < W; x++) mask[y * W + x] = hidden(x);
        }

        // ---- pass 1: per line, what is on, where, and which map cells are ever seen
        var segs = new List<Seg>(); var segOf = new Dictionary<long, int>();
        var palCount = new Dictionary<int, int>();
        bool anyM7 = false, anyObj = false; int segOverflow = 0;
        for (int y = 0; y < N; y++)
        {
            var L = lines[y + 1]; var R = L.Regs;
            bool blank = (R.Inidisp & 0x80) != 0 || (R.Inidisp & 0x0F) == 0;
            int mode = R.Bgmode & 7;
            bool subLayersShow = (R.Cgadsub & 0x20) != 0 && (R.Cgwsel & 0x02) != 0;   // backdrop math adding the sub screen
            int preventSel = (R.Cgwsel >> 4) & 3, blackSel = (R.Cgwsel >> 6) & 3;
            bool colorWinOn = Window(L, 5, colorWin);
            // Backdrop: CGRAM 0 with colour math (the fixed colour as the sub screen's backdrop), and plain where math is blocked.
            ushort back = blank ? (ushort)0 : L.Backdrop;
            bool backMath = !blank && (R.Cgadsub & 0x20) != 0;
            e.BackdropRgb[y] = blank ? 0 : Rgb(backMath ? Blend(back, L.Coldata, R.Cgadsub) : back, R.Inidisp & 0x0F);
            e.BackdropAltRgb[y] = blank ? 0 : Rgb(back, R.Inidisp & 0x0F);
            if (backMath) Mark(e.BackdropAlt, ref e.AnyBackdropAlt, y, x => Region(preventSel, x));
            else if (e.AnyBackdropAlt) Mark(e.BackdropAlt, ref e.AnyBackdropAlt, y, _ => false);
            if (!blank) Mark(e.Black, ref e.AnyBlack, y, x => Region(blackSel, x));
            if (blank) continue;
            // Fixed-colour math on the layers (the addend is the fixed colour when CGWSEL bit 1 is clear): blended per pixel.
            if ((R.Cgwsel & 0x02) == 0 && (R.Cgadsub & 0x1F) != 0 && preventSel != 3)
            {
                e.MathLayers[y] = (byte)(R.Cgadsub & 0x1F); e.MathMode[y] = (byte)(R.Cgadsub & 0xC0);
                e.MathRgb[y] = Rgb(L.Coldata, R.Inidisp & 0x0F);
                Mark(e.MathBlocked, ref e.AnyMathBlocked, y, x => Region(preventSel, x));
            }
            else if (e.AnyMathBlocked) Mark(e.MathBlocked, ref e.AnyMathBlocked, y, _ => false);
            var ladder = Ladder(mode, (R.Bgmode & 0x08) != 0);
            // Visible on this line: on the main screen, or sub-screen only where colour math adds it (and not where it is blocked).
            bool Visible(int bit, out bool main) { main = (R.Tm & bit) != 0; return main || ((R.Ts & bit) != 0 && subLayersShow); }
            Func<int, bool> Masked(int layer, bool main)
            {
                bool useWin = main ? (R.Tmw & (1 << layer)) != 0 : (R.Tsw & (1 << layer)) != 0;
                var m = new bool[W];
                if (useWin) Window(L, layer, m);
                return main ? x => m[x] : x => m[x] || Region(preventSel, x);
            }
            if (mode == 7)
            {
                if (Visible(1, out bool main7))
                {
                    anyM7 = true;
                    var M = e.Mode7; M.On[y] = true;
                    M.A[y] = L.M7a; M.B[y] = L.M7b; M.C[y] = L.M7c; M.D[y] = L.M7d; M.X[y] = L.M7x; M.Y[y] = L.M7y;
                    M.H[y] = L.M7hofs; M.V[y] = L.M7vofs; M.Sel[y] = R.M7sel; M.SnesLine[y] = y + 1;
                    M.Z[y] = (byte)(5 + (main7 ? 16 : 0));
                    Mark(M.Hidden, ref M.AnyHidden, y, Masked(0, main7));
                }
            }
            else
            {
                var bpps = LayerBpp(mode);
                ushort[] hofs = { R.Bg1hofs, R.Bg2hofs, R.Bg3hofs, R.Bg4hofs }, vofs = { R.Bg1vofs, R.Bg2vofs, R.Bg3vofs, R.Bg4vofs };
                byte[] bgsc = { R.Bg1sc, R.Bg2sc, R.Bg3sc, R.Bg4sc };
                for (int k = 0; k < 4; k++)
                {
                    int bpp = bpps[k];
                    if (bpp == 0 || !Visible(1 << k, out bool main)) continue;
                    int chrBase = k switch { 0 => (R.Bg12nba & 0x0F) << 12, 1 => (R.Bg12nba & 0xF0) << 8, 2 => (R.Bg34nba & 0x0F) << 12, _ => (R.Bg34nba & 0xF0) << 8 };
                    int big = (R.Bgmode >> (4 + k)) & 1;
                    long key = k | (long)bgsc[k] << 2 | (long)chrBase << 10 | (long)bpp << 26 | (long)big << 30 | (long)(mode == 0 ? 1 : 0) << 31;
                    if (!segOf.TryGetValue(key, out int si))
                    {
                        if (segs.Count >= ExtPicture.MaxLayers) { segOverflow++; continue; }
                        si = segs.Count; segOf[key] = si;
                        bool wide = (bgsc[k] & 1) != 0, tall = (bgsc[k] & 2) != 0;
                        segs.Add(new Seg { K = k, MapBase = (bgsc[k] & 0xFC) << 8, Wide = wide, Tall = tall, ChrBase = chrBase, Bpp = bpp, Big = big, Mode0 = mode == 0,
                            MapW = (wide ? 64 : 32) << big, MapH = (tall ? 64 : 32) << big });
                    }
                    var sg = segs[si]; var X = e.Layers[si];
                    X.On[y] = true;
                    X.ScrollX[y] = (short)(hofs[k] & 0x3FF); X.ScrollY[y] = (short)((vofs[k] + 1) & 0x3FF);
                    X.ZLow[y] = (byte)(ladder[k].lo + (main ? 16 : 0)); X.ZHigh[y] = (byte)(ladder[k].hi + (main ? 16 : 0));
                    Mark(X.Hidden, ref X.AnyHidden, y, Masked(k, main));
                    // The map cells this line shows.
                    int my = (vofs[k] + y + 1) & (sg.MapH * 8 - 1), ey = my >> 3;
                    int ex0 = (hofs[k] & 0x3FF) >> 3;
                    for (int c = 0; c <= 32; c++)
                    {
                        int ex = (ex0 + c) & (sg.MapW - 1), cell = ey * sg.MapW + ex;
                        if (sg.Seen.Add(cell))
                        {
                            ushort entry = MapEntry(vram, sg, ex, ey);
                            int pk = PalBase(sg.K, sg.Bpp, (entry >> 10) & 7, sg.Mode0);
                            palCount[pk] = palCount.GetValueOrDefault(pk) + 1;
                        }
                    }
                }
            }
            if (Visible(0x10, out bool mainObj))
            {
                anyObj = true; e.ObjOn[y] = true;
                for (int p = 0; p < 4; p++) e.ObjZ[y * 4 + p] = (byte)(ObjLadder[p] + (mainObj ? 16 : 0));
                Mark(e.ObjHidden, ref e.AnyObjHidden, y, Masked(4, mainObj));
            }
        }

        st.SnesPalettesUsed = palCount.Count;

        // ---- background tiles: every (tile, SNES palette) some line shows, and 8 palettes of 3 colours chosen for them
        // (tiles grouped by the colours they actually use, the way NES palettes are laid out) - each tile takes its group's
        var bgQ = new Quantizer();
        var cellItems = new List<(int si, int cell, int variant, int item, int tile, int pb, bool hf, bool vf, bool prio)>();
        for (int si = 0; si < segs.Count; si++)
        {
            var sg = segs[si]; int wpt = sg.Bpp * 4;
            foreach (int cell in sg.Seen)
            {
                int ex = cell % sg.MapW, ey = cell / sg.MapW;
                ushort entry = MapEntry(vram, sg, ex, ey);
                bool hf = (entry & 0x4000) != 0, vf = (entry & 0x8000) != 0, prio = (entry & 0x2000) != 0;
                int p = (entry >> 10) & 7, pb = PalBase(sg.K, sg.Bpp, p, sg.Mode0);
                int tile = entry & 0x3FF;
                if (sg.Big == 1) tile = (tile + ((ex & 1) ^ (hf ? 1 : 0)) + 16 * ((ey & 1) ^ (vf ? 1 : 0))) & 0x3FF;
                int addr = sg.ChrBase + tile * wpt, bpp = sg.Bpp;
                int item = bgQ.Add((long)addr << 20 | (long)bpp << 16 | (uint)pb, h =>
                {
                    for (int r = 0; r < 8; r++) for (int x = 0; x < 8; x++)
                    {
                        int i = Pixel(addr, bpp, r, x);
                        if (i != 0) { int c = RgbOf(cg[(pb + i) & 0xFF]); h[c] = h.GetValueOrDefault(c) + 1; }
                    }
                });
                cellItems.Add((si, cell, tile | (p << 10) | (hf ? 1 << 13 : 0) | (vf ? 1 << 14 : 0), item, tile, pb, hf, vf, prio));
            }
        }
        bgQ.Solve(ExtPicture.Palettes);
        for (int g = 0; g < ExtPicture.Palettes; g++) for (int L = 1; L < 4; L++) e.BgRgb[g * 4 + L] = Scaled(bgQ.Palette[g][L]);
        var notes = new List<string>();
        var segTiles = new Dictionary<int, int>[segs.Count]; var segNext = new int[segs.Count];
        for (int si = 0; si < segs.Count; si++)
        {
            var X = e.Layers[si]; var sg = segs[si];
            X.Enabled = true; X.MapWidth = sg.MapW; X.MapHeight = sg.MapH; X.SnesLayer = sg.K;
            segTiles[si] = new Dictionary<int, int>(); segNext[si] = 1;   // tile 0 stays see-through
        }
        foreach (var ci in cellItems)
        {
            var sg = segs[ci.si]; var X = e.Layers[ci.si]; var tiles = segTiles[ci.si];
            if (!tiles.TryGetValue(ci.variant, out int t))
            {
                if (segNext[ci.si] >= ExtPicture.MaxTiles) { st.BgOverflow++; t = 1 + ci.tile % (ExtPicture.MaxTiles - 1); }
                else
                {
                    t = segNext[ci.si]++; tiles[ci.variant] = t;
                    int addr = sg.ChrBase + ci.tile * sg.Bpp * 4;
                    for (int r = 0; r < 8; r++) for (int x = 0; x < 8; x++)
                    {
                        int i = Pixel(addr, sg.Bpp, ci.vf ? 7 - r : r, ci.hf ? 7 - x : x);
                        X.Tiles[t * 64 + r * 8 + x] = i == 0 ? (byte)0 : bgQ.Level(ci.item, RgbOf(cg[(ci.pb + i) & 0xFF]));
                    }
                }
            }
            X.Map[ci.cell] = (ushort)t;
            X.Attr[ci.cell] = (byte)(bgQ.Group[ci.item] | (ci.prio ? 8 : 0));
        }
        for (int si = 0; si < segs.Count; si++) notes.Add($"BG{segs[si].K + 1}:{segTiles[si].Count}");
        e.LayerCount = segs.Count;
        if (segOverflow > 0) st.Note += $" ({segOverflow} layer-lines past the {ExtPicture.MaxLayers}-layer limit)";

        // ---- Mode 7: the 128x128 map and 256 8x8 tiles straight from VRAM (low bytes map, high bytes texels), with 8
        // palettes of its own chosen for its tiles
        if (anyM7)
        {
            var M = e.Mode7; M.Enabled = true;
            for (int i = 0; i < 128 * 128; i++) M.Map[i] = (byte)vram[i];
            // The texture and its palettes only change when Mode 7's VRAM half or CGRAM does: reuse last frame's otherwise.
            ulong h7 = 14695981039346656037;
            Span<bool> used = stackalloc bool[256];
            for (int i = 0; i < 256 * 64; i++) { int c = vram[i] >> 8; used[c] = true; h7 = (h7 ^ (uint)c) * 1099511628211; }
            for (int i = 1; i < 256; i++) if (used[i]) h7 = (h7 ^ cg[i]) * 1099511628211;   // only the colours the texels use
            h7 ^= (ulong)bright << 56;
            if (h7 == m7Hash) { Array.Copy(m7Tex, M.Tex, m7Tex.Length); Array.Copy(m7Rgb, e.M7Rgb, m7Rgb.Length); notes.Add("M7"); goto mode7Done; }
            var m7Q = new Quantizer();
            for (int t = 0; t < 256; t++)
            {
                int tt = t;
                m7Q.Add(t, h => { for (int j = 0; j < 64; j++) { int c = vram[tt * 64 + j] >> 8; if (c != 0) { int rgb = RgbOf(cg[c]); h[rgb] = h.GetValueOrDefault(rgb) + 1; } } });
            }
            m7Q.Solve(ExtPicture.Palettes);
            for (int t = 0; t < 256; t++)
                for (int j = 0; j < 64; j++)
                {
                    int c = vram[t * 64 + j] >> 8;
                    M.Tex[t * 64 + j] = (byte)(c == 0 ? 0 : m7Q.Group[t] << 2 | m7Q.Level(t, RgbOf(cg[c])));
                }
            for (int g = 0; g < ExtPicture.Palettes; g++) for (int L = 1; L < 4; L++) e.M7Rgb[g * 4 + L] = Scaled(m7Q.Palette[g][L]);
            Array.Copy(M.Tex, m7Tex, m7Tex.Length); Array.Copy(e.M7Rgb, m7Rgb, m7Rgb.Length); m7Hash = h7;
            notes.Add("M7");
        }
        mode7Done:
        st.Layers = notes.Count == 0 ? "none" : string.Join(" ", notes);
        st.UniqueBgTiles = notes.Count;

        // ---- sprites: every piece, in OAM order (earlier = in front), with the SNES's own 8 sprite palettes
        (int w, int h)[] small = { (8, 8), (8, 8), (8, 8), (16, 16), (16, 16), (32, 32), (16, 32), (16, 32) };
        (int w, int h)[] large = { (16, 16), (32, 32), (64, 64), (32, 32), (64, 64), (64, 64), (32, 64), (32, 32) };
        int kept = 0, wanted = 0;
        // OAM epochs: runs of lines drawn from the same OAM contents and OBSEL (a game may rewrite OAM mid-frame).
        var epochs = new List<(int from, int to, byte[] oam, int obsel)>();
        for (int y = 0; y < N; y++)
        {
            var oamL = lines[y + 1].Oam ?? sp.Oam; int ob = lines[y + 1].Regs.Obsel;
            if (epochs.Count > 0 && ReferenceEquals(epochs[^1].oam, oamL) && epochs[^1].obsel == ob) { var last = epochs[^1]; epochs[^1] = (last.from, y, last.oam, last.obsel); }
            else epochs.Add((y, y, oamL, ob));
        }
        // Every 8x8 piece, in OAM order (earlier = in front); each (tile, SNES palette) is one item of the quantizer.
        var objQ = new Quantizer();
        var pieces = new List<(int sx, int sy, int from, int to, int item, int prio, bool hf, bool vf, bool mathPal)>();
        var itemTile = new List<(int addr, int pal)>();
        if (anyObj)
            foreach (var (from, to, oamS, obsel) in epochs)
            {
                int objBase = (obsel & 7) << 13, gap = (((obsel >> 3) & 3) + 1) << 12;
                for (int i = 0; i < 128; i++)
                {
                    int hi = (oamS[512 + (i >> 2)] >> ((i & 3) * 2)) & 3;
                    int x = oamS[i * 4] | ((hi & 1) << 8); if (x >= 256) x -= 512;
                    int y = oamS[i * 4 + 1], a = oamS[i * 4 + 3], tile = oamS[i * 4 + 2] | ((a & 1) << 8);
                    var (w, h) = (hi & 2) != 0 ? large[(obsel >> 5) & 7] : small[(obsel >> 5) & 7];
                    if (y >= 224 && y + h <= 256) continue;
                    if (y >= 224) y -= 256;
                    if (x >= 256 || x + w <= 0 || y + h <= from || y > to) continue;
                    int pal = (a >> 1) & 7, prio = (a >> 4) & 3; bool hf = (a & 0x40) != 0, vf = (a & 0x80) != 0;
                    for (int r = 0; r < h / 8; r++) for (int c = 0; c < w / 8; c++)
                    {
                        int tt = (tile & 0x100) | (((tile & 0xF0) + (r << 4)) & 0xF0) | (((tile & 0x0F) + c) & 0x0F);
                        int sx = x + 8 * (hf ? w / 8 - 1 - c : c), sy = y + 8 * (vf ? h / 8 - 1 - r : r);
                        if (sx <= -8 || sx >= 256 || sy + 8 <= from || sy > to) continue;
                        wanted++;
                        if (pieces.Count >= e.SpriteLimit) continue;
                        int addr = objBase + (tt >= 256 ? gap : 0) + (tt & 0xFF) * 16, pp = pal;
                        int before = objQ.Count;
                        int item = objQ.Add((long)addr << 8 | (uint)pal, hh =>
                        {
                            for (int rr = 0; rr < 8; rr++) for (int xx = 0; xx < 8; xx++)
                            {
                                int ci = Pixel(addr, 4, rr, xx);
                                if (ci != 0) { int rgb = RgbOf(cg[128 + pp * 16 + ci]); hh[rgb] = hh.GetValueOrDefault(rgb) + 1; }
                            }
                        });
                        if (objQ.Count > before) itemTile.Add((addr, pal));
                        pieces.Add((sx, sy, from, to, item, prio, hf, vf, pal >= 4));
                    }
                }
            }
        objQ.Solve(ExtPicture.Palettes);
        for (int g = 0; g < ExtPicture.Palettes; g++) for (int L = 1; L < 4; L++) e.ObjRgb[g * 4 + L] = Scaled(objQ.Palette[g][L]);
        for (int item = 0; item < itemTile.Count && item < ExtPicture.MaxSpriteTiles; item++)
        {
            var (addr, pal) = itemTile[item];
            for (int rr = 0; rr < 8; rr++) for (int xx = 0; xx < 8; xx++)
            {
                int ci = Pixel(addr, 4, rr, xx);
                e.SpriteTiles[item * 64 + rr * 8 + xx] = ci == 0 ? (byte)0 : objQ.Level(item, RgbOf(cg[128 + pal * 16 + ci]));
            }
        }
        if (itemTile.Count > ExtPicture.MaxSpriteTiles) st.ObjOverflow += itemTile.Count - ExtPicture.MaxSpriteTiles;
        if (e.Sprites.Length < pieces.Count) e.Sprites = new ExtPicture.Sprite[pieces.Count];
        foreach (var pc in pieces)
            e.Sprites[kept++] = new ExtPicture.Sprite
            {
                X = (short)pc.sx, Y = (short)pc.sy, FirstLine = (short)pc.from, LastLine = (short)pc.to,
                Tile = (ushort)(pc.item % ExtPicture.MaxSpriteTiles), Palette = (byte)objQ.Group[pc.item], Priority = (byte)pc.prio,
                HFlip = pc.hf, VFlip = pc.vf, MathPalette = pc.mathPal,
            };
        e.SpriteCount = kept;
        e.PaletteVersion++;
        st.SpritesWanted = wanted; st.SpritesKept = kept; st.UniqueObjTiles = itemTile.Count;

        // The native NES picture stays off (only the extension draws).
        var pal0 = Enumerable.Repeat((byte)0x0F, 32).ToArray();
        var frame = sink.Present(new NesPicture { Pal = pal0, Oam = Enumerable.Repeat((byte)0xFF, 256).ToArray(), Blank = true });
        st.LineDrops = e.SpriteLineDrops;
        Last.UniqueBgTiles = st.UniqueBgTiles; Last.BgOverflow = st.BgOverflow; Last.UniqueObjTiles = st.UniqueObjTiles; Last.ObjOverflow = st.ObjOverflow;
        Last.SnesPalettesUsed = st.SnesPalettesUsed; Last.SpritesWanted = st.SpritesWanted; Last.SpritesKept = st.SpritesKept; Last.Note = st.Note;
        Last.Layers = st.Layers; Last.LineDrops = st.LineDrops;
        return frame;
    }

    // Last frame's Mode 7 texture and palettes, and the hash of what they were made from.
    private ulong m7Hash;
    private readonly byte[] m7Tex = new byte[256 * 64];
    private readonly uint[] m7Rgb = new uint[ExtPicture.Palettes * 4];

    /// <summary>Sprite priorities 0-3 on the ladder, in every mode (the BG ladders are built around them).</summary>
    private static readonly byte[] ObjLadder = { 3, 6, 9, 12 };

    /// <summary>One extension layer: a SNES layer with one map / tile base / depth, and the map cells some line shows.</summary>
    private sealed class Seg
    {
        public int K, MapBase, ChrBase, Bpp, Big, MapW, MapH; public bool Wide, Tall, Mode0;
        public readonly HashSet<int> Seen = new();
    }

    /// <summary>
    /// Chooses palettes the way NES art is laid out: items (tiles, each with the colours it really uses) are grouped by
    /// likeness into a few palettes of 3 colours, and every item takes its group's palette - so a sky tile, a grass tile and
    /// a star tile that shared one 16-colour SNES palette can land in different palettes instead of averaging into one pale
    /// colour. Distances weigh green most (as the eye does). Levels 1-3 run from dark to bright, so Game Boy shades follow.
    /// </summary>
    private sealed class Quantizer
    {
        private readonly Dictionary<long, int> index = new();
        private readonly List<Dictionary<int, int>> hist = new();
        public int Count => hist.Count;
        public int[] Group = Array.Empty<int>();
        public uint[][] Palette = Array.Empty<uint[]>();
        private readonly List<Dictionary<int, byte>> levels = new();

        /// <summary>The item for this key, created (its colour histogram filled by <paramref name="fill"/>) the first time.</summary>
        public int Add(long key, Action<Dictionary<int, int>> fill)
        {
            if (index.TryGetValue(key, out int i)) return i;
            var h = new Dictionary<int, int>(); fill(h);
            index[key] = hist.Count; hist.Add(h);
            return hist.Count - 1;
        }

        public byte Level(int item, int rgb) => levels[item].TryGetValue(rgb, out var l) ? l : (byte)1;

        private static double D((double r, double g, double b) a, (double r, double g, double b) c) { double dr = a.r - c.r, dg = a.g - c.g, db = a.b - c.b; return dr * dr * 3 + dg * dg * 6 + db * db; }
        private static (double r, double g, double b) C(int rgb) => (rgb >> 16 & 0xFF, rgb >> 8 & 0xFF, rgb & 0xFF);
        private static double Lum((double r, double g, double b) c) => c.r * 3 + c.g * 6 + c.b;

        /// <summary>k-means of weighted colours into k centres, sorted dark to bright.</summary>
        private static (double r, double g, double b)[] KMeans(List<((double r, double g, double b) c, double w)> pts, int k)
        {
            var cen = new (double r, double g, double b)[k];
            if (pts.Count == 0) { for (int i = 0; i < k; i++) cen[i] = (80 * (i + 1), 80 * (i + 1), 80 * (i + 1)); return cen; }
            var byLum = pts.OrderBy(p => Lum(p.c)).ToList();
            for (int i = 0; i < k; i++) cen[i] = byLum[Math.Min(byLum.Count - 1, (2 * i + 1) * byLum.Count / (2 * k))].c;
            var g = new int[pts.Count];
            for (int it = 0; it < 10; it++)
            {
                for (int i = 0; i < pts.Count; i++) { double best = double.MaxValue; for (int j = 0; j < k; j++) { double d = D(pts[i].c, cen[j]); if (d < best) { best = d; g[i] = j; } } }
                var sum = new (double r, double g, double b, double w)[k];
                for (int i = 0; i < pts.Count; i++) { var (c, w) = pts[i]; sum[g[i]].r += c.r * w; sum[g[i]].g += c.g * w; sum[g[i]].b += c.b * w; sum[g[i]].w += w; }
                for (int j = 0; j < k; j++) if (sum[j].w > 0) cen[j] = (sum[j].r / sum[j].w, sum[j].g / sum[j].w, sum[j].b / sum[j].w);
            }
            return cen.OrderBy(Lum).ToArray();
        }

        public void Solve(int groups)
        {
            int n = hist.Count;
            Group = new int[n];
            Palette = new uint[groups][];
            for (int g = 0; g < groups; g++) Palette[g] = new uint[] { 0, 0x505050, 0xA0A0A0, 0xF0F0F0 };
            if (n == 0) return;
            // 1. each item's own 3 colours (its signature) and its weight
            var sig = new (double r, double g, double b)[n][]; var weight = new double[n];
            for (int i = 0; i < n; i++)
            {
                var pts = hist[i].Select(kv => (C(kv.Key), (double)kv.Value)).ToList();
                sig[i] = KMeans(pts, 3); weight[i] = hist[i].Values.Sum();
            }
            double SigD(int a, (double r, double g, double b)[] c) { double d = 0; for (int j = 0; j < 3; j++) d += D(sig[a][j], c[j]); return d; }
            // 2. items into groups: farthest-point starts from the heaviest item, then k-means on signatures
            int k = Math.Min(groups, n);
            var cen = new List<(double r, double g, double b)[]> { sig[Enumerable.Range(0, n).OrderByDescending(i => weight[i]).First()] };
            while (cen.Count < k)
            {
                int far = 0; double farD = -1;
                for (int i = 0; i < n; i++) { double d = cen.Min(c => SigD(i, c)) * Math.Sqrt(weight[i] + 1); if (d > farD) { farD = d; far = i; } }
                cen.Add(sig[far]);
            }
            for (int it = 0; it < 8; it++)
            {
                for (int i = 0; i < n; i++) { double best = double.MaxValue; for (int j = 0; j < k; j++) { double d = SigD(i, cen[j]); if (d < best) { best = d; Group[i] = j; } } }
                for (int j = 0; j < k; j++)
                {
                    var members = Enumerable.Range(0, n).Where(i => Group[i] == j).ToList();
                    if (members.Count == 0) continue;
                    var c = new (double r, double g, double b)[3];
                    double wsum = members.Sum(i => weight[i]);
                    for (int s2 = 0; s2 < 3; s2++)
                        c[s2] = (members.Sum(i => sig[i][s2].r * weight[i]) / wsum, members.Sum(i => sig[i][s2].g * weight[i]) / wsum, members.Sum(i => sig[i][s2].b * weight[i]) / wsum);
                    cen[j] = c;
                }
            }
            // 3. each group's palette: 3 colours over all its items' pixels. Then refine: every item moves to the palette
            // that draws its real pixels with the least error, and the palettes are rebuilt from their new members (an
            // emptied palette is re-seeded with the item drawn worst) - a few rounds.
            var pal = new (double r, double g, double b)[k][];
            void Rebuild()
            {
                for (int j = 0; j < k; j++)
                {
                    var pts = new List<((double r, double g, double b) c, double w)>();
                    for (int i = 0; i < n; i++) if (Group[i] == j) foreach (var kv in hist[i]) pts.Add((C(kv.Key), kv.Value));
                    if (pts.Count > 0 || pal[j] == null) pal[j] = KMeans(pts, 3);
                }
            }
            double Err(int i, (double r, double g, double b)[] P)
            {
                double e = 0;
                foreach (var kv in hist[i]) { var c = C(kv.Key); double best = double.MaxValue; for (int L = 0; L < 3; L++) best = Math.Min(best, D(c, P[L])); e += best * kv.Value; }
                return e;
            }
            Rebuild();
            for (int round = 0; round < 3 && k > 1; round++)
            {
                var err = new double[n];
                for (int i = 0; i < n; i++)
                {
                    double best = double.MaxValue;
                    for (int j = 0; j < k; j++) { double e2 = Err(i, pal[j]); if (e2 < best) { best = e2; Group[i] = j; } }
                    err[i] = best;
                }
                for (int j = 0; j < k; j++)
                    if (!Group.Contains(j))
                    {
                        int worst = Enumerable.Range(0, n).OrderByDescending(i => err[i]).First();
                        Group[worst] = j; err[worst] = 0;
                    }
                Rebuild();
            }
            for (int j = 0; j < k; j++)
                for (int L = 0; L < 3; L++) Palette[j][L + 1] = (uint)((int)Math.Round(pal[j][L].r) << 16 | (int)Math.Round(pal[j][L].g) << 8 | (int)Math.Round(pal[j][L].b));
            levels.Clear();
            for (int i = 0; i < n; i++)
            {
                var map = new Dictionary<int, byte>(); var P = pal[Group[i]];
                foreach (var col in hist[i].Keys)
                {
                    var c = C(col); int best = 0; double bd = double.MaxValue;
                    for (int L = 0; L < 3; L++) { double d = D(c, P[L]); if (d < bd) { bd = d; best = L; } }
                    map[col] = (byte)(best + 1);
                }
                levels.Add(map);
            }
        }
    }

    /// <summary>CGRAM base of palette p of layer k: mode 0 gives each layer its own 32 colours; 8bpp layers use all 256.</summary>
    private static int PalBase(int k, int bpp, int p, bool mode0) => bpp == 8 ? 0 : bpp == 4 ? p * 16 : (mode0 ? k * 32 : 0) + p * 4;

    /// <summary>The SNES map entry behind extension cell (ex, ey) of a segment (8x8 cells; a 16x16 tile spans 2x2).</summary>
    private static ushort MapEntry(ushort[] vram, Seg sg, int ex, int ey)
    {
        int tx = ex >> sg.Big, ty = ey >> sg.Big;
        int a = sg.MapBase + (ty & 31) * 32 + (tx & 31);
        if (tx >= 32) a += 0x400;
        if (ty >= 32) a += sg.Wide ? 0x800 : 0x400;
        return vram[a & 0x7FFF];
    }

    /// <summary>SNES colour math on two BGR555 colours (CGADSUB bit 7 subtract, bit 6 half).</summary>
    private static ushort Blend(ushort a, ushort b, byte cgadsub)
    {
        bool sub = (cgadsub & 0x80) != 0, half = (cgadsub & 0x40) != 0;
        int Ch(int sh) { int x = (a >> sh) & 31, y = (b >> sh) & 31; return sub ? Math.Max(0, x - y) >> (half ? 1 : 0) : half ? (x + y) >> 1 : Math.Min(31, x + y); }
        return (ushort)(Ch(0) | Ch(5) << 5 | Ch(10) << 10);
    }

    /// <summary>
    /// The frame's per-line registers: PPU_SFC's own capture when it ran (index = SNES line 1-224), otherwise the one
    /// snapshot for every line (no windows, no fixed colour, no Mode 7 matrix).
    /// </summary>
    private static PPU_SFC.BridgeLineState[] LinesOf(ISnesPpuCore sp, SnesPpuSnapshot s)
    {
        if (sp is SfcPpuAdapter ad && ad.Inner.BridgeLines is { Length: >= ExtPicture.Lines + 1 } captured) return captured;
        var regs = new PPU_SFC.RegisterSnapshot(s.Inidisp, s.BgMode, 0, s.Obsel, s.Bgsc[0], s.Bgsc[1], s.Bgsc[2], s.Bgsc[3], s.Bg12Nba, s.Bg34Nba,
            s.Hofs[0], s.Vofs[0], s.Hofs[1], s.Vofs[1], s.Hofs[2], s.Vofs[2], s.Hofs[3], s.Vofs[3], s.Tm, s.Ts, 0, 0, 0, 0, 0, 0);
        var one = new PPU_SFC.BridgeLineState(regs, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, sp.Cgram[0]);
        return Enumerable.Repeat(one, ExtPicture.Lines + 1).ToArray();
    }
}
