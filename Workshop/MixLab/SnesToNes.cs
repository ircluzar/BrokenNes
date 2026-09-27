using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NesEmulator;

namespace BrokenNes.Workshop.MixLab;

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
    private readonly NES nes;
    private readonly Bus bus;
    public readonly Stats Last = new();

    public sealed class Stats
    {
        public int UniqueBgTiles, BgOverflow, UniqueObjTiles, ObjOverflow, SnesPalettesUsed, SpritesWanted, SpritesKept;
        public string Note = "";
        public override string ToString() =>
            $"bg tiles {UniqueBgTiles} (overflow {BgOverflow}), obj tiles {UniqueObjTiles} (overflow {ObjOverflow}), " +
            $"snes bg palettes {SnesPalettesUsed}, sprites {SpritesKept}/{SpritesWanted} 8x8 pieces {Note}";
    }

    public SnesToNes(string nesPpuId)
    {
        nes = new NES { RomName = "mixlab-idle.nes" };
        nes.LoadROM(IdleRom());
        if (!nes.SetPpuCore(nesPpuId) || !string.Equals(nes.GetPpuCoreId(), "PPU_" + nesPpuId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"NES PPU '{nesPpuId}' not applied (got {nes.GetPpuCoreId()})");
        bus = (Bus)typeof(NES).GetField("bus", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(nes)!;
    }

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

    private void W(ushort a, byte v) => bus.Write(a, v);

    /// <summary>Program the NES PPU from the SNES PPU's current state, run one NES frame, return RGBA 256x240.</summary>
    public byte[] Render(ISnesPpuCore sp, SnesPpuSnapshot? midFrame = null)
    {
        var s = midFrame ?? sp.Snapshot();
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

        // --- program the NES PPU (rendering off while uploading), then run one frame
        W(0x2001, 0x00); W(0x2000, 0x08);
        _ = bus.Read(0x2002);
        // Instant register writes are fine for cores without a $2006 -> v delay, but PPU_FIX models the real 4-dot
        // delay: with no time passing between writes, every $2007 byte lands at the old address. Where the core has
        // a PPU-bus backdoor (IPpuProbe) and MIX_BACKDOOR is not 0, upload through it instead.
        if (bus.ppu is IPpuProbe pb && Environment.GetEnvironmentVariable("MIX_BACKDOOR") != "0")
        {
            for (int i = 0; i < 4096; i++) pb.ProbePpuBusWrite((ushort)i, chrBg[i]);
            for (int i = 0; i < 4096; i++) pb.ProbePpuBusWrite((ushort)(0x1000 + i), chrObj[i]);
            for (int i = 0; i < 1024; i++) { pb.ProbePpuBusWrite((ushort)(0x2000 + i), nt[0][i]); pb.ProbePpuBusWrite((ushort)(0x2400 + i), nt[1][i]); }
            for (int i = 0; i < 32; i++) pb.ProbePpuBusWrite((ushort)(0x3F00 + i), pal[i]);
        }
        else
        {
        W(0x2006, 0x00); W(0x2006, 0x00);
        foreach (var b in chrBg) W(0x2007, b);
        foreach (var b in chrObj) W(0x2007, b);
        W(0x2006, 0x20); W(0x2006, 0x00);
        foreach (var b in nt[0]) W(0x2007, b);
        foreach (var b in nt[1]) W(0x2007, b);
        W(0x2006, 0x3F); W(0x2006, 0x00);
        foreach (var b in pal) W(0x2007, b);
        }
        W(0x2003, 0x00);
        foreach (var b in nesOam) W(0x2004, b);
        _ = bus.Read(0x2002);
        W(0x2000, 0x08); W(0x2005, (byte)fx); W(0x2005, (byte)fy);
        W(0x2001, blank ? (byte)0x00 : (byte)0x1E);
        nes.RunFrame(); nes.RunFrame();   // one frame to latch the new picture, one to present it
        Last.UniqueBgTiles = st.UniqueBgTiles; Last.BgOverflow = st.BgOverflow; Last.UniqueObjTiles = st.UniqueObjTiles; Last.ObjOverflow = st.ObjOverflow;
        Last.SnesPalettesUsed = st.SnesPalettesUsed; Last.SpritesWanted = st.SpritesWanted; Last.SpritesKept = st.SpritesKept; Last.Note = st.Note;
        return nes.GetFrameBuffer();
    }
}
