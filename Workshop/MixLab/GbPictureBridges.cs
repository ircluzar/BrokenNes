using System;
using System.Collections.Generic;
using System.Linq;
using NesEmulator.Gb;

namespace BrokenNes.Workshop.MixLab;

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
    private static readonly byte[] NesGreen = Green.Select(c => NesPalette.Nearest(c.r, c.g, c.b)).ToArray();
    public static readonly byte[] NesGrey = { 0x30, 0x10, 0x00, 0x0F };
    public static readonly ushort[] Bgr555Green = Green.Select(c => (ushort)(c.r >> 3 | (c.g >> 3) << 5 | (c.b >> 3) << 10)).ToArray();
    public static int Shade(byte palette, int index) => palette >> (index * 2) & 3;
    public static bool UseGreen = Environment.GetEnvironmentVariable("MIX_GB_GREY") != "1";
    public static byte Nes(int shade) => (UseGreen ? NesGreen : NesGrey)[shade];
    public static ushort Bgr555(int shade) => UseGreen ? Bgr555Green[shade] : new ushort[] { 0x7FFF, 0x56B5, 0x294A, 0x0000 }[shade];
}

/// <summary>
/// MIX LAB: a Game Boy picture drawn by a NES PPU (any core, through <see cref="NesPictureSink"/>).
/// The Game Boy's 160x144 screen sits at NES (48,48). Background and window tiles become NES background tiles
/// (tile flips and Game Boy Color palettes are baked in where the NES can't do them), sprites become NES sprites
/// (8x16 = two 8x8, 8 per line instead of 10), palettes become 2C02 colours (DMG shades as NES greens). Each NES tile
/// row takes the scroll of the Game Boy line it covers, so status-bar splits survive (with at most 7 pixels of fine
/// scroll mismatch). With bleed on (default) the rest of the NES screen shows the Game Boy map beyond the screen edges.
/// </summary>
internal sealed class GbToNes
{
    private readonly NesPictureSink sink;
    public bool Bleed = true;
    public string Stats = "";
    public GbToNes(string nesPpuId) { sink = new NesPictureSink(nesPpuId); }
    public string PpuId => sink.PpuId;

    public byte[] Render(BOARD_GB board, GbLineCapture cap)
    {
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
        byte Cgb(byte[] palRam, int p, int c) { int o = p * 8 + c * 2; var (r, g, b) = NesPalette.FromBgr555((ushort)(palRam[o] | palRam[o + 1] << 8)); return NesPalette.Nearest(r, g, b); }
        byte bgp = cap.Bgp[Ref];
        pic.Pal[0] = cgb ? Cgb(ppu.BgPalRam, bgSlots.Count > 0 ? bgSlots[0] : 0, 0) : GbShades.Nes(GbShades.Shade(bgp, 0));
        for (int k = 0; k < 4; k++)
            for (int c = 1; c < 4; c++)
                pic.Pal[k * 4 + c] = cgb ? (k < bgSlots.Count ? Cgb(ppu.BgPalRam, bgSlots[k], c) : (byte)0x0F) : GbShades.Nes(GbShades.Shade(bgp, c));

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
        for (int k = 0; k < 4; k++)
            for (int c = 1; c < 4; c++)
                pic.Pal[16 + k * 4 + c] = cgb ? (k < objSlots.Count ? Cgb(ppu.ObjPalRam, objSlots[k], c) : (byte)0x0F)
                                              : GbShades.Nes(GbShades.Shade(k == 1 ? cap.Obp1[Ref] : cap.Obp0[Ref], c));
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
        Stats = $"bg tiles {bgTiles.Count} (overflow {overflow}), obj tiles {objTiles.Count}, sprites {kept}/{pieces.Count} 8x8 pieces, bg palettes {bgPalUse.Count}";
        return sink.Present(pic);
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
                ushort bgc = cgb ? (ushort)(ppu.BgPalRam[p * 8 + c * 2] | ppu.BgPalRam[p * 8 + c * 2 + 1] << 8) : GbShades.Bgr555(GbShades.Shade(cap.Bgp[Ref], c));
                ushort obc = cgb ? (ushort)(ppu.ObjPalRam[p * 8 + c * 2] | ppu.ObjPalRam[p * 8 + c * 2 + 1] << 8) : GbShades.Bgr555(GbShades.Shade(p == 1 ? cap.Obp1[Ref] : cap.Obp0[Ref], c));
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
