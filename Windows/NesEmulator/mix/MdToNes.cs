using System;
using System.Collections.Generic;
using System.Linq;
using NesEmulator.Sega;

namespace NesEmulator.Mix;

/// <summary>
/// The lines of a Genesis frame as the VDP drew them, and the memories as the picture ended: taken through the board's own 68000 instruction hook (chained), which sees the VDP's line number change.
/// At the first instruction of each picture line it notes the registers, the horizontal scroll entries the VDP reads for the line (from the scroll table in VRAM), the vertical scroll RAM and the colour RAM
/// (kept only when it changed); at the first instruction after the last picture line it copies VRAM, colour RAM, vertical scroll RAM and the registers - the state the picture was drawn from, before the game's
/// vertical-blank work changes it. No change to the board or the VDP is needed.
/// </summary>
internal sealed class MdLineCapture
{
    public const int Lines = 240;
    public readonly byte[][] Reg = new byte[Lines][];
    public readonly ushort[] HsA = new ushort[Lines], HsB = new ushort[Lines];
    public readonly ushort[][] Vsram = new ushort[Lines][];
    public readonly int[] CramState = new int[Lines];
    public readonly List<ushort[]> CramStates = new();
    public readonly bool[] Seen = new bool[Lines];
    // the end of the picture
    public readonly byte[] Vram = new byte[0x10000];
    public readonly ushort[] Cram = new ushort[64];
    public readonly ushort[] VsramEnd = new ushort[64];
    public readonly byte[] RegEnd = new byte[32];
    public bool Snapshotted;
    public int ActiveLines = 224;
    public bool H40, Interlaced;
    private readonly MdVdp vdp;
    private int last = -1;

    public MdLineCapture(BOARD_MD board)
    {
        vdp = board.Vdp;
        for (int i = 0; i < Lines; i++) { Reg[i] = new byte[32]; Vsram[i] = new ushort[40]; }
        var chained = board.InstructionHook;
        board.InstructionHook = pc => { chained?.Invoke(pc); Poll(); };
    }

    private void Poll()
    {
        int ln = vdp.Line;
        if (ln == last) return;
        last = ln;
        if (ln == 0) { Snapshotted = false; Array.Clear(Seen); CramStates.Clear(); }
        int active = vdp.ActiveLines;
        if (ln < active && ln < Lines)
        {
            var r = vdp.Reg;
            Array.Copy(r, Reg[ln], 32);
            int mask = (r[11] & 3) switch { 0 => 0, 1 => 7, 2 => 0xF8, _ => 0xFF };
            int ea = ((r[13] & 0x3F) << 10) + ((ln & mask) << 2);
            var v = vdp.Vram;
            HsA[ln] = (ushort)((v[ea & 0xFFFF] << 8 | v[ea + 1 & 0xFFFF]) & 0x3FF);
            HsB[ln] = (ushort)((v[ea + 2 & 0xFFFF] << 8 | v[ea + 3 & 0xFFFF]) & 0x3FF);
            Array.Copy(vdp.Vsram, Vsram[ln], 40);
            var cr = vdp.Cram;
            int st = CramStates.Count - 1;
            if (st < 0 || !cr.AsSpan().SequenceEqual(CramStates[st])) { CramStates.Add((ushort[])cr.Clone()); st = CramStates.Count - 1; }
            CramState[ln] = st;
            Seen[ln] = true;
        }
        else if (ln >= active && !Snapshotted) Snapshot();
    }

    /// <summary>Take the end-of-picture snapshot now (the board calls this at the end of a frame as a fallback when no instruction fell after the last picture line).</summary>
    public void Snapshot()
    {
        if (Snapshotted) return;
        Array.Copy(vdp.Vram, Vram, 0x10000);
        Array.Copy(vdp.Cram, Cram, 64); Array.Copy(vdp.Vsram, VsramEnd, 64); Array.Copy(vdp.Reg, RegEnd, 32);
        ActiveLines = vdp.ActiveLines; H40 = vdp.H40; Interlaced = vdp.Interlace != 0;
        Snapshotted = true;
    }

    /// <summary>Fill in the lines the poll never saw (it was between two instructions' worth of VDP time): they were drawn as the line before.</summary>
    public void Complete()
    {
        for (int l = 0; l < Math.Min(ActiveLines, Lines); l++)
        {
            if (Seen[l]) continue;
            int p = Math.Max(0, l - 1);
            Array.Copy(Reg[p], Reg[l], 32); HsA[l] = HsA[p]; HsB[l] = HsB[p]; Array.Copy(Vsram[p], Vsram[l], 40); CramState[l] = CramState[p];
        }
        if (CramStates.Count == 0) CramStates.Add((ushort[])Cram.Clone());
    }
}

/// <summary>
/// MIX LAB: the Genesis -> NES picture bridge. After each frame the VDP's state becomes what a NES picture chip can draw. The Genesis picture is 320 or 256 pixels wide and 224 or 240 lines; the NES chip's is 256 x 240,
/// so a 320-wide picture is shown as its middle 256 columns (32 columns lost each side, counted) and a 240-line one as its middle 224 lines.
/// <list type="bullet">
/// <item><b>Layered path</b> (PPU_FIXS, and the Game Boy bridges fronted by it): plane B, plane A and the window become extension layers - each with the VDP's own per-line horizontal scroll, its vertical scroll
/// (a column-scroll plane is split into runs of equal scroll, up to 3 a plane), the plane size, and the priority bit of every tile - ordered on the VDP's own priority ladder with the sprites. The window replaces plane A
/// where the registers say. The 80 (64) sprites are followed along the VDP's link list and filtered by the VDP's own per-line rules (20 / 16 a line, 40 / 32 cells, the X = 0 mask) before they reach the NES chip, so what is drawn
/// is what the VDP drew. 4-bit tiles are reduced to 3 colours + see-through in up to 8 palettes (<see cref="SegaQuantizer"/>), colours matched to the NES chips' palette.</item>
/// <item><b>Flat path</b> (every other NES chip): the planes are composed cell by cell into one background (a cell takes the first of plane A high, plane B high, plane A low, plane B low that has any pixel), 256 tiles, 4 palettes,
/// the NES's single fine scroll; the sprites become NES sprites (64 pieces, the chip enforces 8 a line).</item>
/// </list>
/// Not carried, and counted: shadow and highlight (the shading; the sprites that are its operators are not drawn as pixels either), interlace (each field is drawn as a normal frame), colour RAM written in the middle of a frame (the frame's palette is used), VRAM written in the middle of a frame (the
/// picture's end state is used), more column-scroll runs than layers.
/// </summary>
internal sealed class MdToNes
{
    private readonly NesPictureSink sink;
    private readonly BOARD_MD board;
    private readonly MdLineCapture cap;
    public readonly BridgeCounters Counters = new();
    public string NesPpuId => sink.PpuId;
    public PictureWindow Window { get; private set; }
    private readonly SegaQuantizer layeredBgQ = new(), layeredObjQ = new(), flatBgQ = new(), flatObjQ = new();
    private readonly bool[] vis = new bool[256];

    public MdToNes(BOARD_MD board, string nesPpuId)
    {
        this.board = board;
        sink = new NesPictureSink(nesPpuId);
        cap = new MdLineCapture(board);
    }

    public bool Layered => (sink.Ppu as PPU_FIXS ?? (sink.Ppu as PPU_DMG)?.LayerFront) != null;

    private static int Rgb9(ushort c)
    {
        int r = c >> 1 & 7, g = c >> 5 & 7, b = c >> 9 & 7;
        return Level[r] << 16 | Level[g] << 8 | Level[b];
    }
    private static readonly int[] Level = { 0, 49, 87, 119, 146, 174, 206, 255 };

    // ---------------------------------------------------------------------------------------------------------------- the pictures
    private PictureWindow WindowFor(bool h40, int active)
    {
        int w = 256, x0 = h40 ? 32 : 0;
        int h = Math.Min(active, 224), y0 = (active - h) / 2;
        return PictureWindow.Centered(x0, y0, w, h);
    }

    /// <summary>The Genesis chip's own picture of this frame on the bridge's canvas (ARGB, 256 x 240).</summary>
    public void BuildReference(uint[] reference, out (int X, int Y, int W, int H) region)
    {
        var w = Window; var v = board.Vdp;
        Array.Clear(reference);
        var pic = v.Picture; int pw = v.PictureWidth, ph = v.PictureHeight;
        for (int y = 0; y < w.H && w.Y0 + y < ph; y++)
            for (int x = 0; x < w.W && w.X0 + x < pw; x++) reference[(w.OffsetY + y) * 256 + w.OffsetX + x] = pic[(w.Y0 + y) * MdVdp.PictureStride + w.X0 + x];
        region = (w.OffsetX, w.OffsetY, w.W, w.H);
    }

    public byte[] Render()
    {
        Counters.BeginFrame();
        stamp++;
        var vdp = board.Vdp;
        cap.Snapshot();           // when no instruction fell after the last picture line (a frame that ended there)
        cap.Complete();
        var win = Window = WindowFor(cap.H40, cap.ActiveLines);
        var c = Counters;
        if (cap.H40) c.Add("columns cropped from the 320-wide picture", 64);
        if (cap.ActiveLines > 224) c.Add("lines cropped from the 240-line picture", cap.ActiveLines - 224);
        if ((cap.RegEnd[12] & 8) != 0) c.Add("frames in shadow/highlight mode (the shading is not drawn)");
        il2 = (cap.RegEnd[12] & 6) == 6;
        if (cap.Interlaced) c.Add(il2 ? "frames in interlace mode 2 (the field is drawn from the even rows of the 8x16 cells)" : "interlaced frames (drawn as normal frames)");
        if ((cap.RegEnd[1] & 4) == 0) { c.Add("frames in the Master System compatibility mode (not translated)"); }
        var layered = sink.Ppu as PPU_FIXS ?? (sink.Ppu as PPU_DMG)?.LayerFront;
        if ((cap.RegEnd[1] & 4) == 0)
        {
            if (layered != null) { var e0 = layered.Ext ??= new ExtPicture(); e0.BeginFrame(); layered.ExtLineOffset = 8; }
            return sink.Present(new NesPicture { Blank = true, Pal = Enumerable.Repeat((byte)0x0F, 32).ToArray(), Oam = Enumerable.Repeat((byte)0xFF, 256).ToArray() });
        }
        int changed = 0; for (int l = 0; l < win.H; l++) if (cap.CramState[win.Y0 + l] != cap.CramStates.Count - 1) changed++;
        c.Add("lines drawn with a palette other than the frame's", changed);
        return layered != null ? RenderLayered(win, layered) : RenderFlat(win);
    }

    // ---------------------------------------------------------------------------------------------------------------- reading the planes
    private readonly record struct Cell(int Tile, bool HFlip, bool VFlip, int Pal, bool Prio);

    private static Cell Entry(int e) => new(e & 0x7FF, (e & 0x800) != 0, (e & 0x1000) != 0, e >> 13 & 3, (e & 0x8000) != 0);

    private sealed class Plane
    {
        public int Base, WCells, HCells, RowStride;
        public int WMask => WCells * 8 - 1;
        public int HMask => HCells * 8 - 1;
    }

    private Plane PlaneOf(bool a)
    {
        var r = cap.RegEnd;
        int ws = r[16] & 3, hs = r[16] >> 4 & 3;
        // sizes the 4096-entry plane cannot hold (measured on BlastEm, Track B4a, 16 of 16 scenes identical): a 128-wide plane is only 32 high, and 64 wide with the height code 128 is 64 x 64 (32 x 128 is valid)
        if (ws == 3 && hs != 0) hs = 0; else if (hs == 3 && ws != 0) hs = 1;
        int w = ws == 1 ? 64 : ws == 3 ? 128 : 32, h = hs == 1 ? 64 : hs == 3 ? 128 : 32;
        return new Plane { Base = a ? (r[2] & 0x38) << 10 : (r[4] & 7) << 13, WCells = w, HCells = h, RowStride = ws == 2 ? 0 : w };
    }

    private Cell PlaneCell(Plane p, int row, int col)
    {
        int ea = p.Base + (row * p.RowStride + col) * 2;
        var v = cap.Vram;
        return Entry(v[ea & 0xFFFF] << 8 | v[(ea + 1) & 0xFFFF]);
    }

    private Cell WindowCell(int row, int col)
    {
        var r = cap.RegEnd; int cells = cap.H40 ? 64 : 32;
        int nt = (r[3] & (cap.H40 ? 0x3C : 0x3E)) << 10;
        int ea = nt + (row * cells + col) * 2;
        var v = cap.Vram;
        return Entry(v[ea & 0xFFFF] << 8 | v[(ea + 1) & 0xFFFF]);
    }

    private const string OperatorCounter = "sprite pieces holding shadow/highlight operators (their pixels are not drawn, and neither is the shading they cause)";

    /// <summary>
    /// A sprite's pixel, as a colour index: in shadow/highlight mode the pixels of palette line 3 with colour 14 (highlight) or 15 (shadow) are operators on what lies under them, not pixels of their own
    /// (Track B4a's record, 1.3: measured on BlastEm), so they are see-through here; the shading itself is the part of the mode this bridge does not draw.
    /// </summary>
    private int SpritePixel(int tile, int pal, int y, int x)
    {
        int ci = TilePixel(tile, y, x);
        return ci >= 14 && pal == 3 && (cap.RegEnd[12] & 8) != 0 ? 0 : ci;
    }

    private bool HoldsOperators(int tile, int pal)
    {
        if (pal != 3 || (cap.RegEnd[12] & 8) == 0) return false;
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) if (TilePixel(tile, y, x) >= 14) return true;
        return false;
    }

    /// <summary>Interlace mode 2 (double resolution) in the frame being translated: the cells are 8 x 16, a pattern is 64 bytes, and the field's line y shows pattern row 2y (+ the field).
    /// The bridge draws the field from the even rows (a row off where a cell is flipped vertically or the scroll is odd: the field flag is not visible from here).</summary>
    private bool il2;

    private int TilePixel(int tile, int y, int x)
    {
        int a = il2 ? (tile << 6) + (y << 3) : (tile << 5) + (y << 2);
        var v = cap.Vram;
        int b = v[(a + (x >> 1)) & 0xFFFF];
        return (x & 1) == 0 ? b >> 4 : b & 15;
    }

    private int[] Colours(int line)
    {
        var c = new int[16];
        for (int i = 0; i < 16; i++) c[i] = Rgb9(cap.Cram[line * 16 + i]);
        return c;
    }

    /// <summary>
    /// The vertical scroll the VDP applies to a plane at screen column <paramref name="screenX"/> of line <paramref name="l"/> (<paramref name="vsOff"/>: 0 plane A, 1 plane B; <paramref name="hs"/>: the plane's horizontal
    /// scroll entry on that line), in lines of the picture drawn (field lines in interlace mode 2). Full-screen mode: entry 0 / 1. 2-cell column mode: a vertical scroll entry covers a PAIR of the plane's cells, so the
    /// pairs start at screen x = hs mod 16 and the entry is the pair's number; before the first pair H40 uses the last column's two entries ANDed and H32 uses 0; with a window at the left (16 w pixels) and hs mod 16
    /// != 0, plane A's first b0 pixels after the window take column w (the pair AFTER the one the count gives). The scroll has 10 bits, 11 in interlace mode 2 (where it counts picture rows: half as many field
    /// lines). All measured on BlastEm (Track B4a, docs/projects/sega-track-b4a.md 1.2: the probe and random scene sets, pixel-identical); the first rule counted the columns from the screen's edge (hs mod 16 = 0
    /// is its one case).
    /// </summary>
    private int VerticalScroll(int l, int vsOff, int screenX, int hs)
    {
        var vr = cap.Vsram[l];
        int mask = il2 ? 0x7FF : 0x3FF, vs;
        if ((cap.RegEnd[11] & 4) == 0) vs = vr[vsOff] & mask;
        else
        {
            int b0 = hs & 15, last = cap.H40 ? 19 : 15;
            if (screenX < b0) vs = (cap.H40 ? vr[2 * last] & vr[2 * last + 1] : 0) & mask;
            else
            {
                int col = (screenX - b0) >> 4;
                if (vsOff == 0 && b0 != 0)
                {
                    var (wf, wt) = WindowSpan(l);
                    if (wf == 0 && wt > 0 && wt < (cap.H40 ? 320 : 256) && screenX >= wt && screenX < wt + b0) col = wt >> 4;      // the strip right after a window at the left
                }
                vs = vr[Math.Min(2 * col + vsOff, 39)] & mask;
            }
        }
        return il2 ? vs >> 1 : vs;
    }

    // the window of a line: whole line, a part of it, or none (the VDP's rule, register 17 and 18 as the line was drawn)
    private (int from, int to) WindowSpan(int line)
    {
        var r = cap.Reg[line]; int width = cap.H40 ? 320 : 256;
        int vpos = (r[18] & 0x1F) * 8;
        bool vwin = (r[18] & 0x80) != 0 ? line >= vpos : line < vpos;
        if (vwin) return (0, width);
        int hpos = (r[17] & 0x1F) * 16;
        return (r[17] & 0x80) != 0 ? (Math.Min(hpos, width), width) : (0, Math.Min(hpos, width));
    }

    // ---------------------------------------------------------------------------------------------------------------- sprites, as the VDP evaluates them
    private sealed class Spr
    {
        public int Index, Y, W, H, Tile, Pal, XRaw; public bool HF, VF, Prio;
        public int[] Cols = Array.Empty<int>();       // per picture line: how many screen columns the VDP draws (0 = not drawn)
    }

    private List<Spr> ReadSprites(out int dropped, out int wanted)
    {
        var v = cap.Vram; var r = cap.RegEnd; bool h40 = cap.H40;
        int maxSprites = h40 ? 80 : 64, maxLine = h40 ? 20 : 16, sat = (r[5] & (h40 ? 0x7E : 0x7F)) << 9;
        var list = new List<Spr>();
        int idx = 0;
        for (int n = 0; n < maxSprites; n++)
        {
            int a = sat + idx * 8;
            int ypos = ((v[a & 0xFFFF] << 8 | v[a + 1 & 0xFFFF]) & 0x3FF) - 128;
            if (il2) ypos = ((ypos + 128) - 256) >> 1;       // interlace mode 2 counts picture rows from 256: half as many field lines
            int size = v[a + 2 & 0xFFFF], link = v[a + 3 & 0xFFFF] & 0x7F;
            int attr = v[a + 4 & 0xFFFF] << 8 | v[a + 5 & 0xFFFF];
            int xraw = (v[a + 6 & 0xFFFF] << 8 | v[a + 7 & 0xFFFF]) & 0x1FF;
            list.Add(new Spr { Index = idx, Y = ypos, W = (size >> 2 & 3) + 1, H = (size & 3) + 1, Tile = attr & 0x7FF, HF = (attr & 0x800) != 0, VF = (attr & 0x1000) != 0, Pal = attr >> 13 & 3, Prio = (attr & 0x8000) != 0, XRaw = xraw });
            idx = link;
            if (idx == 0 || idx >= maxSprites) break;
        }
        int active = Math.Min(cap.ActiveLines, MdLineCapture.Lines);
        foreach (var s in list) s.Cols = new int[active];
        // per line, the VDP's own evaluation: the first maxLine sprites touching the line, then the cell budget and the X = 0 mask
        dropped = 0; wanted = 0; bool prevDotLimit = false;
        var onLine = new List<Spr>();
        for (int y = 0; y < active; y++)
        {
            onLine.Clear();
            foreach (var s in list)
            {
                if (y >= s.Y && y < s.Y + s.H * 8)
                {
                    wanted++;
                    if (onLine.Count == maxLine) { dropped++; continue; }
                    onLine.Add(s);
                }
            }
            if (onLine.Count == 0) { prevDotLimit = false; continue; }
            int cellsLeft = h40 ? 40 : 32; bool masked = false, seenVisible = prevDotLimit && y != 0, limitHit = false;
            foreach (var s in onLine)
            {
                if (s.XRaw == 0) { if (seenVisible) masked = true; } else seenVisible = true;
                int draw = Math.Min(s.W, cellsLeft);
                cellsLeft -= draw;
                if (cellsLeft <= 0) limitHit = true;
                if (!masked) s.Cols[y] = draw;
                else dropped++;
            }
            prevDotLimit = limitHit;
        }
        return list;
    }

    // ================================================================================================================ the layered path
    private byte[] RenderLayered(PictureWindow win, PPU_FIXS fx)
    {
        var c = Counters;
        var e = fx.Ext ??= new ExtPicture();
        e.BeginFrame();
        fx.ExtLineOffset = win.OffsetY;
        int dx = win.Dx, h = win.H, y0 = win.Y0;
        var rEnd = cap.RegEnd;
        var A = PlaneOf(true); var B = PlaneOf(false);
        bool displayAny = false;
        for (int el = 0; el < h; el++) if ((cap.Reg[y0 + el][1] & 0x40) != 0) { displayAny = true; break; }
        var states = cap.CramStates;
        var palettes = new int[4][]; for (int p = 0; p < 4; p++) palettes[p] = Colours(p);
        // backdrop per line (colour RAM entry R7 & 0x3F of the line's palette state)
        for (int el = 0; el < h; el++)
        {
            int l = y0 + el; var cr = states[Math.Min(cap.CramState[l], states.Count - 1)];
            int bd = Rgb9(cr[cap.Reg[l][7] & 0x3F]);
            e.BackdropRgb[el] = e.BackdropAltRgb[el] = (uint)NesFixPalette.ForTextbook(bd);
        }
        if (!displayAny) return Finish(e, 0, 0, 0);

        // ---- layers: plane B, plane A and (where the registers put one) the window; a column-scroll plane keeps one layer and a vertical scroll for each 16-pixel column
        var layers = new List<LayerSpec>();
        bool colMode = (rEnd[11] & 4) != 0;
        int width = cap.H40 ? 320 : 256;
        bool windowUsed = false;
        for (int el = 0; el < h; el++) { var (f, t) = WindowSpan(y0 + el); if (t > f && (cap.Reg[y0 + el][1] & 0x40) != 0) { windowUsed = true; break; } }
        layers.Add(new LayerSpec { Plane = B, IsA = false, VsOff = 1 });
        layers.Add(new LayerSpec { Plane = A, IsA = true, VsOff = 0 });
        if (windowUsed) layers.Add(new LayerSpec { Window = true, IsA = true });
        for (int k = 0; k < layers.Count; k++) { layers[k].Index = k; var X = e.Layers[k]; X.Reset(); layers[k].X = X; }
        e.LayerCount = layers.Count;
        int vsLinesOff = 0, blankShLines = 0;

        // ---- per line: which layers are on, their scroll, the masks
        bool anyBg = false;
        foreach (var L in layers)
        {
            var X = L.X;
            X.Enabled = true; X.SnesLayer = L.IsA ? 0 : 1;
            X.MapWidth = L.Window ? (cap.H40 ? 64 : 32) : L.Plane!.WCells; X.MapHeight = L.Window ? 32 : L.Plane!.HCells;
            L.Seen = new bool[X.MapWidth * X.MapHeight];
            if (colMode && !L.Window)
            {
                // one entry per PAIR of the plane's cells (the table is by pair number); each line says where its pairs start (ColumnShift: they follow the line's horizontal scroll) and the stretch
                // before the first pair has a value of its own (ColumnLeft, the two entries of the last column ANDed)
                int vmask = il2 ? 0x7FF : 0x3FF, vshift = il2 ? 1 : 0;      // 11 bits in interlace mode 2, where the scroll counts picture rows (half as many field lines)
                X.ColumnScroll = new short[20];
                for (int k = 0; k < 20; k++) X.ColumnScroll[k] = (short)((cap.VsramEnd[(2 * k + L.VsOff) & 0x3F] & vmask) >> vshift);
                int last = cap.H40 ? 19 : 15;
                X.ColumnLeft = (short)((cap.H40 ? (cap.VsramEnd[2 * last] & cap.VsramEnd[2 * last + 1]) & vmask : 0) >> vshift);      // H40: the last pair's entries ANDed; H32: 0
            }
        }
        int VsAt(int l, LayerSpec L, int screenX, int hs) => VerticalScroll(l, L.VsOff, screenX, hs);
        for (int el = 0; el < h; el++)
        {
            int l = y0 + el; var R = cap.Reg[l];
            if ((R[1] & 0x40) == 0) continue;       // display off on this line: the backdrop only
            anyBg = true;
            var (wf, wt) = WindowSpan(l);
            foreach (var L in layers)
            {
                var X = L.X;
                Array.Fill(vis, false);
                int from = 0, to = width;
                if (L.Window) { from = wf; to = wt; }
                if (to <= from) continue;
                // visible source columns of this layer on this line
                bool any = false;
                for (int sx = from; sx < to; sx++)
                {
                    bool inWin = sx >= wf && sx < wt;
                    if (L.Window ? !inWin : (L.IsA && inWin)) continue;
                    int ex = sx - dx; if ((uint)ex >= 256) continue;
                    vis[ex] = true; any = true;
                }
                if (!any) continue;
                X.On[el] = true;
                X.ZLow[el] = (byte)(L.IsA ? 2 : 1); X.ZHigh[el] = (byte)(L.IsA ? 5 : 4);
                // register 0 bit 5, the left column blank: the first 8 pixels show nothing of the planes (the sprites are hidden below); in shadow/highlight mode the planes of the second cell are the first cell's
                if ((R[0] & 0x20) != 0 && dx < 8)
                {
                    X.BlankLeft[el] = (byte)(8 - dx);
                    if ((R[12] & 8) != 0) X.CellOneStart[el] = (short)(8 - dx);
                }
                if (L.Window) { X.ScrollX[el] = (short)dx; X.ScrollY[el] = (short)y0; }
                else
                {
                    int hs = L.IsA ? cap.HsA[l] : cap.HsB[l];
                    X.ScrollX[el] = (short)(dx - hs);
                    // the plane's vertical scroll: one for the whole line, or (column mode) the layer's column table added to the line offset
                    X.ScrollY[el] = colMode ? (short)y0 : (short)(y0 + VsAt(l, L, 0, hs));
                    if (colMode)
                    {
                        X.ColumnShift[el] = (short)(dx - (hs & 15));
                        if (L.IsA && (hs & 15) != 0 && wf == 0 && wt > 0 && wt < width) { X.ColumnExtraFrom[el] = (short)(wt - dx); X.ColumnExtra[el] = (short)(hs & 15); }     // the strip after a window at the left
                        for (int ex = 0; ex < 256; ex++) if (vis[ex] && VsAt(l, L, ex + dx, hs) != X.ColumnValue(el, ex)) { vsLinesOff++; break; }
                    }
                }
                for (int x = 0; x < 256; x++) X.Hidden[el * 256 + x] = !vis[x];
                X.AnyHidden = true;
                // the cells this line shows
                for (int sx = from; sx < to; sx += 8)
                {
                    int ex = sx - dx; if (ex + 8 <= 0 || ex >= 256) continue;
                    if (L.Window) { L.Seen[(l >> 3) * X.MapWidth + (sx >> 3)] = true; continue; }
                    int hs = L.IsA ? cap.HsA[l] : cap.HsB[l];
                    if (colMode)
                    {
                        // the pairs start at the plane's scroll mod 16, so the vertical scroll can change inside these 8 pixels: each pixel's own
                        for (int p = sx; p < sx + 8; p++)
                        {
                            int pyc = (l + VsAt(l, L, p, hs)) & L.Plane!.HMask, pxc = (p - hs) & L.Plane.WMask;
                            L.Seen[(pyc >> 3) * X.MapWidth + (pxc >> 3)] = true;
                        }
                        continue;
                    }
                    int vs = VsAt(l, L, sx, hs);
                    int py = (l + vs) & L.Plane!.HMask, px0 = (sx - hs) & L.Plane.WMask;
                    L.Seen[(py >> 3) * X.MapWidth + (px0 >> 3)] = true;
                    L.Seen[(py >> 3) * X.MapWidth + (((px0 + 7) & L.Plane.WMask) >> 3)] = true;
                }
            }
            // sprites are drawn on every line the display is on (not in a blank left column)
            e.ObjOn[el] = true;
            if ((R[0] & 0x20) != 0)
            {
                if (dx < 8)
                {
                    if (!e.AnyObjHidden) Array.Clear(e.ObjHidden);       // BeginFrame clears the flag, not the mask: nothing of an earlier frame's mask may remain
                    for (int x = 0; x < 8 - dx; x++) e.ObjHidden[el * 256 + x] = true;
                    e.AnyObjHidden = true;
                }
                if ((R[12] & 8) != 0) blankShLines++;
            }
            e.ObjZ[el * 4 + 0] = 3; e.ObjZ[el * 4 + 1] = 6; e.ObjZ[el * 4 + 2] = 3; e.ObjZ[el * 4 + 3] = 6;
        }
        c.Add("lines with a vertical scroll other than the frame's (column scroll taken from the end of the frame)", vsLinesOff);
        c.Add("lines with the left column blanked in shadow/highlight mode (the column shows the plain backdrop, not the shadowed one)", blankShLines);
        if (!anyBg) return Finish(e, 0, 0, 0);

        // ---- background tiles: one item per (tile, palette line), 3 colours each, 8 palettes
        var bgQ = layeredBgQ; bgQ.BeginFrame();
        var itemOf = new Dictionary<int, int>();
        var variants = new List<(int layer, int cell, int variant, int item, Cell cl)>();
        foreach (var L in layers)
        {
            var X = L.X;
            for (int cellIx = 0; cellIx < L.Seen.Length; cellIx++)
            {
                if (!L.Seen[cellIx]) continue;
                int row = cellIx / X.MapWidth, col = cellIx % X.MapWidth;
                var cl = L.Window ? WindowCell(row, col) : PlaneCell(L.Plane!, row, col);
                int key = ContentOf(cl.Tile) << 2 | cl.Pal;
                if (!itemOf.TryGetValue(key, out int item))
                {
                    var pal = palettes[cl.Pal]; var cc = cl;
                    item = bgQ.Add(key, hist =>
                    {
                        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
                        {
                            int ci = TilePixel(cc.Tile, y, x);
                            if (ci == 0) continue;
                            int rgb = pal[ci]; hist[rgb] = hist.GetValueOrDefault(rgb) + 1;
                        }
                    });
                    itemOf[key] = item;
                }
                variants.Add((L.Index, cellIx, ContentOf(cl.Tile) | cl.Pal << 20 | (cl.HFlip ? 1 << 22 : 0) | (cl.VFlip ? 1 << 23 : 0), item, cl));
            }
        }
        bgQ.Solve(ExtPicture.Palettes);
        for (int g = 0; g < ExtPicture.Palettes; g++) for (int lv = 1; lv < 4; lv++) e.BgRgb[g * 4 + lv] = (uint)NesFixPalette.ForTextbook(bgQ.Palette[g][lv]);
        var tileOf = new Dictionary<int, int>[layers.Count]; var nextTile = new int[layers.Count];
        for (int k = 0; k < layers.Count; k++) { tileOf[k] = new(); nextTile[k] = 1; }
        int overflow = 0;
        foreach (var (layer, cellIx, variant, item, cl) in variants)
        {
            var X = layers[layer].X;
            if (!tileOf[layer].TryGetValue(variant, out int t))
            {
                if (nextTile[layer] >= ExtPicture.MaxTiles) { overflow++; t = 1 + cl.Tile % (ExtPicture.MaxTiles - 1); }
                else
                {
                    t = nextTile[layer]++; tileOf[layer][variant] = t;
                    var pal = palettes[cl.Pal];
                    for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
                    {
                        int ci = TilePixel(cl.Tile, cl.VFlip ? 7 - y : y, cl.HFlip ? 7 - x : x);
                        X.Tiles[t * 64 + y * 8 + x] = ci == 0 ? (byte)0 : bgQ.Level(item, pal[ci]);
                    }
                }
            }
            X.Map[cellIx] = (ushort)t; X.Attr[cellIx] = (byte)(bgQ.Group[item] | (cl.Prio ? 8 : 0));
        }

        // ---- sprites: pieces of the VDP's own evaluated list
        var sprites = ReadSprites(out int dropped, out _);
        c.Add("sprites the VDP dropped itself (20/16 a line, 40/32 cells, the X = 0 mask)", dropped);
        var objQ = layeredObjQ; objQ.BeginFrame();
        var pieceItem = new Dictionary<long, int>(); var itemTile = new List<(int tile, int pal)>(); int operatorPieces = 0;
        var kept = new List<(int x, int y, int from, int to, int item, bool hf, bool vf, bool prio)>();
        foreach (var s in sprites)
        {
            if (s.Cols.Length == 0) continue;
            for (int k = 0; k < s.W; k++)
                for (int r = 0; r < s.H; r++)
                {
                    int top = s.Y + 8 * r;
                    // the lines of this piece on which the VDP draws its column k
                    int runStart = -1;
                    for (int y = Math.Max(0, top); y <= Math.Min(s.Cols.Length, top + 8); y++)
                    {
                        bool on = y < Math.Min(s.Cols.Length, top + 8) && s.Cols[y] > k;
                        if (on && runStart < 0) runStart = y;
                        if (!on && runStart >= 0)
                        {
                            int sc = s.HF ? s.W - 1 - k : k, sr = s.VF ? s.H - 1 - r : r;
                            int tile = (s.Tile + sc * s.H + sr) & 0x7FF;
                            long key = (long)ContentOf(tile) << 2 | (long)s.Pal;
                            if (!pieceItem.TryGetValue(key, out int item))
                            {
                                var pal = palettes[s.Pal]; int tt = tile, sp = s.Pal;
                                item = objQ.Add(key, hist =>
                                {
                                    for (int yy = 0; yy < 8; yy++) for (int xx = 0; xx < 8; xx++) { int ci = SpritePixel(tt, sp, yy, xx); if (ci != 0) { int rgb = pal[ci]; hist[rgb] = hist.GetValueOrDefault(rgb) + 1; } }
                                });
                                pieceItem[key] = item; itemTile.Add((tile, s.Pal));
                                if (HoldsOperators(tile, s.Pal)) operatorPieces++;
                            }
                            int ex = s.XRaw - 128 + 8 * k - dx;
                            if (ex + 8 > 0 && ex < 256)
                                kept.Add((ex, top - y0, runStart - y0, y - 1 - y0, item, s.HF, s.VF, s.Prio));
                            runStart = -1;
                        }
                    }
                }
        }
        objQ.Solve(ExtPicture.Palettes);
        for (int g = 0; g < ExtPicture.Palettes; g++) for (int lv = 1; lv < 4; lv++) e.ObjRgb[g * 4 + lv] = (uint)NesFixPalette.ForTextbook(objQ.Palette[g][lv]);
        int objOver = 0;
        for (int item = 0; item < itemTile.Count; item++)
        {
            if (item >= ExtPicture.MaxSpriteTiles) { objOver++; continue; }
            var (tile, pl) = itemTile[item]; var pal = palettes[pl];
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
            {
                int ci = SpritePixel(tile, pl, y, x);
                e.SpriteTiles[item * 64 + y * 8 + x] = ci == 0 ? (byte)0 : objQ.Level(item, pal[ci]);
            }
        }
        c.Add(OperatorCounter, operatorPieces);
        if (e.Sprites.Length < kept.Count) e.Sprites = new ExtPicture.Sprite[kept.Count];
        int n = 0;
        foreach (var k in kept)
            e.Sprites[n++] = new ExtPicture.Sprite
            {
                X = (short)k.x, Y = (short)k.y, FirstLine = (short)Math.Max(0, k.from), LastLine = (short)Math.Min(h - 1, k.to),
                Tile = (ushort)(k.item % ExtPicture.MaxSpriteTiles), Palette = (byte)objQ.Group[k.item], Priority = (byte)(k.prio ? 1 : 0), HFlip = k.hf, VFlip = k.vf,
            };
        e.SpriteCount = n; e.SpriteLimit = 1024; e.SpritesPerLine = 64;     // the VDP's own limits were applied above
        e.PaletteVersion++;
        return Finish(e, overflow, objOver, 0);
    }

    private sealed class LayerSpec
    {
        public Plane? Plane; public bool IsA, Window; public int VsOff, Index; public ExtPicture.Layer X = null!; public bool[] Seen = Array.Empty<bool>();
    }

    private byte[] Finish(ExtPicture e, int bgOverflow, int objOverflow, int unused)
    {
        var frame = sink.Present(new NesPicture { Pal = Enumerable.Repeat((byte)0x0F, 32).ToArray(), Oam = Enumerable.Repeat((byte)0xFF, 256).ToArray(), Blank = true });
        Counters.Add("background tiles past the layer's 1023 (wrong tiles shown)", bgOverflow);
        Counters.Add("sprite tiles past the 1024 the extension holds", objOverflow);
        Counters.Level("sprite pieces", e.SpriteCount);
        Counters.Level("layers", e.LayerCount);
        return frame;
    }

    // ================================================================================================================ the flat path
    private byte[] RenderFlat(PictureWindow win)
    {
        var c = Counters;
        var pic = new NesPicture();
        int ox = win.OffsetX, oy = win.OffsetY, dx = win.Dx, h = win.H, y0 = win.Y0;
        var rEnd = cap.RegEnd;
        var A = PlaneOf(true); var B = PlaneOf(false);
        bool display = false; for (int el = 0; el < h; el++) if ((cap.Reg[y0 + el][1] & 0x40) != 0) { display = true; break; }
        pic.Blank = !display;
        var palettes = new int[4][]; for (int p = 0; p < 4; p++) palettes[p] = Colours(p);
        int bdFrame = Rgb9(cap.Cram[rEnd[7] & 0x3F]);
        // frame fine scroll: the most common (hscroll of plane A) mod 8, and vertical (A) mod 8
        var xh = new int[8]; var yh = new int[8];
        for (int el = 0; el < h; el++) { xh[-cap.HsA[y0 + el] & 7]++; yh[(cap.Vsram[y0 + el][0] + y0) & 7]++; }
        int fineX = 0, fineY = 0; for (int i = 1; i < 8; i++) { if (xh[i] > xh[fineX]) fineX = i; if (yh[i] > yh[fineY]) fineY = i; }
        fineY = (fineY - oy) & 7;
        pic.FineX = fineX; pic.FineY = fineY;
        int linesOff = 0; for (int el = 0; el < h; el++) if ((-cap.HsA[y0 + el] & 7) != fineX) linesOff++;
        c.Add("lines with a fine scroll other than the frame's", linesOff);
        int blankLines = 0; for (int el = 0; el < h; el++) if ((cap.Reg[y0 + el][0] & 0x20) != 0) blankLines++;
        c.Add("lines with the left column blanked (the flat path draws the column)", blankLines);

        // composed cells (the cell under the middle of each NES cell)
        var cells = new Cell?[30, 33]; int hidden2 = 0;
        for (int cy = 0; cy < 30; cy++)
        {
            int cellTop = cy * 8 - fineY - oy + y0;                // the cell may stick out above or below the window: the part inside it is what counts
            if (cellTop + 7 < y0 || cellTop >= y0 + h) continue;
            int srcY = Math.Clamp(cellTop + 4, y0, y0 + h - 1);
            var (wf, wt) = WindowSpan(srcY);
            for (int cx = 0; cx < 33; cx++)
            {
                int cellLeft = cx * 8 - fineX - ox + win.X0;
                if (cellLeft + 7 < win.X0 || cellLeft >= win.X0 + win.W) continue;
                int srcX = Math.Clamp(cellLeft + 4, win.X0, win.X0 + win.W - 1);
                Cell? Pick()
                {
                    Cell? a = null;
                    if (srcX >= wf && srcX < wt) a = WindowCell(srcY >> 3, srcX >> 3);
                    else
                    {
                        int hs = cap.HsA[srcY], vs = VerticalScroll(srcY, 0, srcX, hs);
                        a = PlaneCell(A, ((srcY + vs) & A.HMask) >> 3, ((srcX - hs) & A.WMask) >> 3);
                    }
                    int hsB = cap.HsB[srcY], vsB = VerticalScroll(srcY, 1, srcX, hsB);
                    var b = PlaneCell(B, ((srcY + vsB) & B.HMask) >> 3, ((srcX - hsB) & B.WMask) >> 3);
                    bool aOpaque = Opaque(a!.Value.Tile), bOpaque = Opaque(b.Tile);
                    if (aOpaque && bOpaque) hidden2++;
                    // the VDP's ladder at cell level: A high, B high, A low, B low
                    if (aOpaque && a.Value.Prio) return a;
                    if (bOpaque && b.Prio) return b;
                    if (aOpaque) return a;
                    if (bOpaque) return b;
                    return null;
                }
                cells[cy, cx] = Pick();
            }
        }
        c.Add("cells where both planes have pixels (the lower one is lost)", hidden2);
        var bgQ = flatBgQ; bgQ.BeginFrame(); var itemOf = new Dictionary<int, int>();
        for (int cy = 0; cy < 30; cy++) for (int cx = 0; cx < 33; cx++)
        {
            if (cells[cy, cx] is not { } cl) continue;
            int key = ContentOf(cl.Tile) << 2 | cl.Pal;
            if (itemOf.ContainsKey(key)) continue;
            var pal = palettes[cl.Pal]; var cc = cl;
            itemOf[key] = bgQ.Add(key, hist =>
            {
                for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) { int ci = TilePixel(cc.Tile, y, x); if (ci != 0) { int rgb = pal[ci]; hist[rgb] = hist.GetValueOrDefault(rgb) + 1; } }
            });
        }
        bgQ.Solve(4);
        pic.Pal[0] = NesFixPalette.Nearest(bdFrame >> 16 & 0xFF, bdFrame >> 8 & 0xFF, bdFrame & 0xFF);
        for (int g = 0; g < 4; g++) for (int lv = 1; lv < 4; lv++) { int rgb = bgQ.Palette[g][lv]; pic.Pal[g * 4 + lv] = NesFixPalette.Nearest(rgb >> 16 & 0xFF, rgb >> 8 & 0xFF, rgb & 0xFF); }
        // the tile variants (content, palette line, flips) the picture needs, by how many cells use each; the chip holds 255, the most used get one each and the rest are shown as the closest
        int VariantKey(Cell cl) => ContentOf(cl.Tile) | cl.Pal << 20 | (cl.HFlip ? 1 << 22 : 0) | (cl.VFlip ? 1 << 23 : 0);
        var variantUses = new Dictionary<int, int>(); var variantCell = new Dictionary<int, Cell>();
        for (int cy = 0; cy < 30; cy++) for (int cx = 0; cx < 33; cx++)
        {
            if (cells[cy, cx] is not { } cl) continue;
            int vk = VariantKey(cl);
            variantUses[vk] = variantUses.GetValueOrDefault(vk) + 1; variantCell[vk] = cl;
        }
        byte[] VariantLevels(int vk)
        {
            var cl = variantCell[vk];
            int item = itemOf[ContentOf(cl.Tile) << 2 | cl.Pal]; var pal = palettes[cl.Pal];
            var lvs = new byte[64];
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
            {
                int ci = TilePixel(cl.Tile, cl.VFlip ? 7 - y : y, cl.HFlip ? 7 - x : x);
                lvs[y * 8 + x] = (byte)(ci == 0 ? 0 : bgQ.Level(item, pal[ci]));
            }
            return lvs;
        }
        var bgMap = FlatTilePacker.Pack(variantUses, VariantLevels, 255, out var bgKept, out int bgOver);
        for (int t = 1; t <= 255; t++)
            if (bgKept[t] is { } lv) for (int y = 0; y < 8; y++) { var (p0, p1) = FlatTilePacker.Row(lv, y); pic.ChrBg[t * 16 + y] = p0; pic.ChrBg[t * 16 + 8 + y] = p1; }
        var bgTiles = bgMap;
        int BgTile(Cell cl) => bgMap[VariantKey(cl)];
        var votes = new int[15, 17, 4]; int conflicts = 0;
        for (int cy = 0; cy < 30; cy++) for (int cx = 0; cx < 33; cx++)
        {
            if (cells[cy, cx] is not { } cl) continue;
            int t = BgTile(cl);
            if (cx < 32) pic.Nt0[cy * 32 + cx] = (byte)t; else pic.Nt1[cy * 32] = (byte)t;
            votes[cy >> 1, cx >> 1, bgQ.Group[itemOf[ContentOf(cl.Tile) << 2 | cl.Pal]]]++;
        }
        for (int by = 0; by < 15; by++) for (int bx = 0; bx < 17; bx++)
        {
            int best = 0, total = 0; for (int g = 0; g < 4; g++) { total += votes[by, bx, g]; if (votes[by, bx, g] > votes[by, bx, best]) best = g; }
            conflicts += total - votes[by, bx, best];
            int cx = bx * 2, cy = by * 2, table = cx < 32 ? 0 : 1, lx = cx & 31;
            var nt = table == 0 ? pic.Nt0 : pic.Nt1;
            int ai = 0x3C0 + (cy >> 2) * 8 + (lx >> 2), sh = ((cy & 2) << 1) | (lx & 2);
            nt[ai] = (byte)((nt[ai] & ~(3 << sh)) | (best << sh));
        }
        c.Add("tiles whose 16x16 block holds another palette (shown in the block's colours)", conflicts);
        c.Add("background tile variants past the 255 the NES holds (shown as the closest kept tile)", bgOver);
        c.Level("background tiles", Math.Min(255, bgTiles.Count));

        // ---- sprites: the VDP's evaluated list, 8x8 pieces, 64 at most
        var sprites = ReadSprites(out int dropped, out _);
        c.Add("sprites the VDP dropped itself (20/16 a line, 40/32 cells, the X = 0 mask)", dropped);
        var objQ = flatObjQ; objQ.BeginFrame();
        var pieceItem = new Dictionary<long, int>(); var itemTile = new List<(int tile, int pal)>(); int operatorPieces = 0;
        var keptPieces =new List<(int x, int y, int item, bool hf, bool vf, bool behind)>();
        foreach (var s in sprites)
            for (int k = 0; k < s.W; k++) for (int r = 0; r < s.H; r++)
            {
                int top = s.Y + 8 * r, ex = s.XRaw - 128 + 8 * k - dx, ey = top - y0;
                if (ey + 8 <= 0 || ey >= h || ex + 8 <= 0 || ex >= 256) continue;
                bool shown = false; for (int y = Math.Max(0, top); y < Math.Min(s.Cols.Length, top + 8); y++) if (s.Cols[y] > k) { shown = true; break; }
                if (!shown) continue;
                int sc = s.HF ? s.W - 1 - k : k, sr = s.VF ? s.H - 1 - r : r;
                int tile = (s.Tile + sc * s.H + sr) & 0x7FF; long key = (long)ContentOf(tile) << 2 | (long)s.Pal;
                if (!pieceItem.TryGetValue(key, out int item))
                {
                    var pal = palettes[s.Pal]; int tt = tile, sp = s.Pal;
                    item = objQ.Add(key, hist => { for (int yy = 0; yy < 8; yy++) for (int xx = 0; xx < 8; xx++) { int ci = SpritePixel(tt, sp, yy, xx); if (ci != 0) { int rgb = pal[ci]; hist[rgb] = hist.GetValueOrDefault(rgb) + 1; } } });
                    pieceItem[key] = item; itemTile.Add((tile, s.Pal));
                    if (HoldsOperators(tile, s.Pal)) operatorPieces++;
                }
                keptPieces.Add((ex, ey, item, s.HF, s.VF, !s.Prio));
            }
        objQ.Solve(4);
        for (int g = 0; g < 4; g++) for (int lv = 1; lv < 4; lv++) { int rgb = objQ.Palette[g][lv]; pic.Pal[16 + g * 4 + lv] = NesFixPalette.Nearest(rgb >> 16 & 0xFF, rgb >> 8 & 0xFF, rgb & 0xFF); }
        for (int item = 0; item < itemTile.Count && item < 256; item++)
        {
            var (tile, pl) = itemTile[item]; var pal = palettes[pl];
            for (int y = 0; y < 8; y++)
            {
                byte p0 = 0, p1 = 0;
                for (int x = 0; x < 8; x++)
                {
                    int ci = SpritePixel(tile, pl, y, x); int lv = ci == 0 ? 0 : objQ.Level(item, pal[ci]);
                    p0 |= (byte)((lv & 1) << (7 - x)); p1 |= (byte)((lv >> 1 & 1) << (7 - x));
                }
                pic.ChrObj[item * 16 + y] = p0; pic.ChrObj[item * 16 + 8 + y] = p1;
            }
        }
        c.Add(OperatorCounter, operatorPieces);
        Array.Fill(pic.Oam, (byte)0xFF);
        int nOam = 0, over = 0; var perLine = new int[240];
        foreach (var p in keptPieces)
        {
            if (nOam >= 64 || p.item >= 256) { over++; continue; }
            int ny = p.y + oy - 1, nx = p.x;
            if (ny < -1 || ny > 238 || nx < 0 || nx > 255) { over++; continue; }
            int o = nOam++ * 4;
            // a low-priority sprite is behind the planes' high-priority tiles only; the NES's flag is behind every background colour, so it is set where the cell under the piece is high priority
            int cx = Math.Clamp((nx + fineX + 4) >> 3, 0, 32), cy = Math.Clamp((ny + 1 + fineY + 4) >> 3, 0, 29);
            bool behind = p.behind && cells[cy, cx] is { Prio: true };
            pic.Oam[o] = (byte)ny; pic.Oam[o + 1] = (byte)p.item;
            pic.Oam[o + 2] = (byte)(objQ.Group[p.item] | (behind ? 0x20 : 0) | (p.hf ? 0x40 : 0) | (p.vf ? 0x80 : 0));
            pic.Oam[o + 3] = (byte)nx;
            for (int ln = Math.Max(0, ny + 1); ln < Math.Min(240, ny + 9); ln++) perLine[ln]++;
        }
        c.Add("sprite pieces past the NES's 64 (or its 256 sprite tiles)", over);
        c.Add("lines with more than 8 sprite pieces (the NES chip drops the rest)", perLine.Count(n => n > 8));
        return sink.Present(pic);
    }

    // Tiles are told apart by what is in them, not by their number: a game that clears a plane with hundreds of different tile numbers that are all the same blank (or all the same black) shows one tile.
    private readonly Dictionary<ulong, int> contentIds = new();
    private readonly int[] tileContent = new int[2048]; private readonly long[] tileStamp = new long[2048];
    private readonly bool[] contentOpaque = new bool[1 << 16]; private long stamp;

    private int ContentOf(int tile)
    {
        tile &= 0x7FF;
        if (tileStamp[tile] == stamp) return tileContent[tile];
        var v = cap.Vram; int a = tile << (il2 ? 6 : 5); ulong h = 14695981039346656037UL; bool opaque = false;
        for (int i = 0; i < (il2 ? 64 : 32); i++) { byte b = v[(a + i) & 0xFFFF]; h = (h ^ b) * 1099511628211UL; if (b != 0) opaque = true; }
        if (!contentIds.TryGetValue(h, out int id))
        {
            if (contentIds.Count >= 60000) contentIds.Clear();
            id = contentIds.Count + 1; contentIds[h] = id;
        }
        contentOpaque[id & 0xFFFF] = opaque;
        tileStamp[tile] = stamp; tileContent[tile] = id;
        return id;
    }

    private bool Opaque(int tile) => contentOpaque[ContentOf(tile) & 0xFFFF];
}
