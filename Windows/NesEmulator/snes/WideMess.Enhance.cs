using System;
using System.Numerics;

namespace NesEmulator.Snes;

// WideMess milestone 6: SNES games enhanced (docs/projects/widemess-spec.md section 6). The console's enhancements of a locked SNES game, on the picture chip:
//   - widescreen: the SNES modes drawn 400 wide by the wide compositor (WideMess.Picture.cs), the side columns per the game's profile (extend / repeat / fill per layer, masks)
//   - no sprite flicker: the SNES sprite table drawn with the WideMess line limits (64 sprites, 128 slivers)
// The game never sees either: the sprite pass here counts the SNES's limits on the SNES's 256 columns for STAT77, exactly as the SNES chip does, and the centre 256 columns
// are the SNES chip's picture. Nothing here runs unless an enhancement is on (PPU_WMS.RenderLine goes straight to the SNES chip otherwise). No allocation per line or frame.

/// <summary>A side rule (spec 6.1): what a layer shows in the 72 columns either side of the SNES picture.</summary>
public enum WmsSideRule : byte
{
    /// <summary>The layer's tile map continues past the SNES edge (Mode 7: the transformation continues); sprites: their side parts are drawn.</summary>
    Extend,
    /// <summary>The layer's own 256 visible columns repeat: picture column c shows column 72 + ((c - 72) mod 256).</summary>
    Repeat,
    /// <summary>The layer is transparent in the sides (sprites: none there).</summary>
    Fill,
    /// <summary>Masks only: the columns are black, every layer and the backdrop.</summary>
    Black,
    /// <summary>BG layers: extend, but only where the game has drawn the layer's map for the place shown (spec 6.1): a game that draws new columns of its map just ahead of the
    /// SNES edge as it scrolls leaves old tiles beyond them, and those side columns are filled instead. Tracked from the layer's horizontal scroll (<see cref="WmsWidescreen.DrawnAhead"/>).</summary>
    Drawn,
}

/// <summary>A mask of a widescreen profile (spec 6.1): a rectangle of the side columns where <see cref="Rule"/> replaces the layers' own rules.</summary>
public sealed class WmsMask
{
    /// <summary>Bits 0-3 BG1-BG4, bit 4 the sprites.</summary>
    public int Layers;
    /// <summary>Picture rows, inclusive.</summary>
    public int Top, Bottom;
    /// <summary>The band of the sides: from <see cref="From"/> (inclusive) to <see cref="To"/> (exclusive) pixels out from the SNES picture's edge (0 = the column next to it, 72 = the picture's edge).</summary>
    public int From, To = PPU_WMS.SnesOrigin;
    public bool Left = true, Right = true;
    public WmsSideRule Rule;
    public string Why = "";
    /// <summary>Scene conditions (spec 6.1): the mask applies only on lines where each listed SNES register, as written, ANDed with its mask, equals its value. Empty: always.</summary>
    public WmsCondition[] When = Array.Empty<WmsCondition>();
}

/// <summary>The SNES registers a mask's scene condition can test (spec 6.2's <c>when</c> keys).</summary>
public enum WmsRegister : byte { Bgmode, Bg1sc, Bg2sc, Bg3sc, Bg4sc, Bg12nba, Bg34nba, Tm, Ts, Obsel, Setini, Cgwsel, Cgadsub }

/// <summary>One scene condition: (register AND mask) == value.</summary>
public readonly record struct WmsCondition(WmsRegister Register, byte Mask, byte Value);

/// <summary>The level in work RAM (spec 6.1): where a game keeps its whole level as 16 x 16-pixel blocks of 2 x 2 map entries. Offsets into the board's 128 KB work RAM.</summary>
public sealed class WmsLevel
{
    /// <summary>The BG layer (0-3) it describes.</summary>
    public int Layer;
    /// <summary>The blocks (one 16-bit word each, row by row), the two 16-bit words holding the level's width and height in blocks, the block table (8 bytes a block number).</summary>
    public int Blocks, Width, Height, Tiles;
    /// <summary>The block word's bits: the block number, flipped in x, flipped in y.</summary>
    public int Index = 0x03FF, FlipX = 0x0400, FlipY = 0x0800;
}

/// <summary>A profile's widescreen rules (spec 6.1): one rule per layer (BG1-BG4, sprites) and the masks.</summary>
public sealed class WmsWidescreen
{
    public const int Sprites = 4;
    /// <summary>[0-3] BG1-BG4, [4] the sprites (Extend or Fill).</summary>
    public readonly WmsSideRule[] Layers = new WmsSideRule[5];
    public WmsMask[] Masks = Array.Empty<WmsMask>();
    /// <summary>True (the default, spec 3.4): a SNES window edge on the SNES picture's edge (left 0, right 255) reaches the picture's edge. False: the SNES's window positions stay where the
    /// SNES draws them (left 0 is column 72, right 255 column 327), for games that mask a few edge columns with a window.</summary>
    public bool WindowsReachEdge = true;
    /// <summary>[0-3] BG1-BG4, for a layer whose rule is <see cref="WmsSideRule.Drawn"/>: how many pixels past the SNES edge the game draws its map ahead of where it scrolls (0-72).</summary>
    public readonly int[] DrawnAhead = { 16, 16, 16, 16 };
    /// <summary>[0-3] BG1-BG4, for a layer whose rule is <see cref="WmsSideRule.Drawn"/>: the map memory (spec 6.1) fills side columns outside the drawn span with the place as the SNES
    /// picture last showed it.</summary>
    public readonly bool[] Memory = new bool[4];
    /// <summary>The level in work RAM (spec 6.1) for one layer on the drawn rule, or null.</summary>
    public WmsLevel? Level;

    /// <summary>Every layer extended, the sprites too, no masks: what the tests and the profile tools start from (never applied to a game without its profile saying so).</summary>
    public static WmsWidescreen AllExtend() => new();

    /// <summary>A copy (tools: the same rules with a part taken away, as a negative control).</summary>
    public WmsWidescreen Copy()
    {
        var c = new WmsWidescreen { Masks = Masks, WindowsReachEdge = WindowsReachEdge, Level = Level };
        Layers.CopyTo(c.Layers, 0); DrawnAhead.CopyTo(c.DrawnAhead, 0); Memory.CopyTo(c.Memory, 0);
        return c;
    }
}

public sealed partial class PPU_WMS
{
    // ================================================================ the enhancements switched on (console side)

    private WmsWidescreen? enhWide;
    private bool enhNoFlicker, enhOn;
    private const int NoFlickerSprites = SpritesPerLine, NoFlickerSlivers = SliversPerLine;   // the WideMess line limits (spec 3.3)

    /// <summary>True while an enhancement of the picture is on (widescreen or no flicker).</summary>
    public bool Enhanced => enhOn;
    /// <summary>The widescreen rules in use (null: widescreen off).</summary>
    public WmsWidescreen? EnhancedWidescreen => enhWide;
    /// <summary>True while no flicker is on.</summary>
    public bool EnhancedNoFlicker => enhNoFlicker;

    /// <summary>Switches the picture's enhancements of a locked SNES game (spec 6.1): <paramref name="widescreen"/> the profile's rules (null: off), <paramref name="noFlicker"/> the WideMess line
    /// limits for the SNES sprite table. Both off: the SNES chip's own picture, untouched. They apply only while WMCTRL is 0 (a game that unlocks and sets it draws its own picture).</summary>
    public void SetEnhancements(WmsWidescreen? widescreen, bool noFlicker)
    {
        if (widescreen != null)
        {
            if (widescreen.Masks.Length > 63) throw new ArgumentException("a widescreen profile has at most 63 masks");
            if (widescreen.Layers[WmsWidescreen.Sprites] is not (WmsSideRule.Extend or WmsSideRule.Fill)) throw new ArgumentException("the sprites' side rule is extend or fill");
            for (int i = 0; i < 4; i++) if (widescreen.Layers[i] == WmsSideRule.Black) throw new ArgumentException("black is a mask's rule, not a layer's");
            for (int i = 0; i < 4; i++) if (widescreen.DrawnAhead[i] is < 0 or > SnesOrigin) throw new ArgumentException("a drawn layer's ahead is 0-72 pixels");
            for (int i = 0; i < 4; i++) if (widescreen.Memory[i] && widescreen.Layers[i] != WmsSideRule.Drawn) throw new ArgumentException("the map memory is for layers on the drawn rule");
            if (widescreen.Level is { } lv && (lv.Layer is < 0 or > 3 || widescreen.Layers[lv.Layer] != WmsSideRule.Drawn)) throw new ArgumentException("the level in work RAM is for a layer on the drawn rule");
        }
        Array.Clear(drawn);
        for (int i = 0; i < 4; i++)
        {
            memory[i] = widescreen != null && widescreen.Memory[i] ? memory[i] ?? new MapMemory() : null;
            memory[i]?.Forget();
        }
        level = widescreen?.Level != null ? new LevelTrack() : null;
        Array.Clear(sideLine);
        enhWide = widescreen; enhNoFlicker = noFlicker;
        enhOn = widescreen != null || noFlicker;
        remapSig = -1;
        rankOrder = null;
        Array.Fill(winSig, -1L);
    }

    // ================================================================ the enhanced line

    private bool lnEnh;   // this line is drawn by the enhanced path (the SNES modes, locked game)

    /// <summary>Test: per row of the last frame, true when the SNES's sprite limits (32 sprites, 34 slivers on its 256 columns) were exceeded there, as the enhanced sprite pass counted them.</summary>
    public readonly bool[] EnhancedOverflowRow = new bool[MaxHeight];
    /// <summary>Test: also draw each enhanced line on the SNES chip's own pipeline and compare: in widescreen the centre 256 columns, without it the whole line (no flicker alone).</summary>
    public bool CompareEnhanced { get; set; }
    /// <summary>Test results of <see cref="CompareEnhanced"/>: lines compared, lines that differ, of those the ones where the SNES's sprite limits were exceeded (no flicker shows more there), first differing row.</summary>
    public int EnhCompareRows, EnhCompareDiffer, EnhCompareDifferOverflow, EnhCompareFirst = -1;
    private uint[]? enhSnesRow;

    /// <summary>One line of a locked SNES game with an enhancement on (WMCTRL = 0): the wide compositor, 400 wide with widescreen, 256 without.</summary>
    private void EnhancedLine(int line)
    {
        int row = line - 1;
        if (row < 0 || row >= VisibleHeight) return;
        bool wide = enhWide != null;
        if (CompareEnhanced)
        {
            base.RenderLine(line);    // the SNES chip's own line (it raises the same STAT77 flags the enhanced pass raises: the same count)
            enhSnesRow ??= new uint[Width];
            FrameBuffer.AsSpan(row * Width, Width).CopyTo(enhSnesRow);
        }
        EnhancedOverflowRow[row] = false;
        lnEnh = true;
        try { RenderWideLine(line, wide, false); }
        finally { lnEnh = false; }
        if (!CompareEnhanced) return;
        EnhCompareRows++;
        var mine = wide ? WideBuffer.AsSpan(row * WideWidth + SnesOrigin, Width) : FrameBuffer.AsSpan(row * Width, Width);
        if (mine.SequenceEqual(enhSnesRow)) return;
        EnhCompareDiffer++;
        if (EnhancedOverflowRow[row]) EnhCompareDifferOverflow++;
        if (EnhCompareFirst < 0) EnhCompareFirst = row;
    }

    /// <summary>Test: clears the <see cref="CompareEnhanced"/> counters.</summary>
    public void ResetEnhancedCompare() { EnhCompareRows = EnhCompareDiffer = EnhCompareDifferOverflow = 0; EnhCompareFirst = -1; }

    // ---------------------------------------------------------------- the side rules: a column map per layer for this line

    private readonly short[][] remap = { new short[WideWidth], new short[WideWidth], new short[WideWidth], new short[WideWidth], new short[WideWidth] };
    private readonly bool[] remapOn = new bool[5];      // per layer (BG1-4, sprites) on this line: some side column is not extended
    private readonly bool[] blackCol = new bool[WideWidth];
    private bool blackOn;
    private long remapSig = -1;
    private const short FillColumn = -1;

    /// <summary>The side rules for this row (widescreen): per layer, picture column -> the column whose pixel it shows (itself for extend, the repeated one, or -1 for fill), rebuilt only
    /// when the set of masks covering the row changes.</summary>
    private void BuildRemap(int row)
    {
        var w = enhWide!;
        var masks = w.Masks;
        long sig = 0;
        for (int i = 0; i < masks.Length; i++) if (row >= masks[i].Top && row <= masks[i].Bottom && SceneMatches(masks[i].When)) sig |= 1L << i;
        if (sig == remapSig) return;
        remapSig = sig;
        blackOn = false;
        anyDrawn = false;
        for (int layer = 0; layer < 5; layer++)
        {
            var map = remap[layer];
            bool any = false;
            if (layer < 4) drawnOn[layer] = false;
            for (int c = 0; c < WideWidth; c++)
            {
                if (layer < 4) drawnCol[layer][c] = false;
                if (c >= SnesOrigin && c < SnesOrigin + Width) { map[c] = (short)c; continue; }
                bool left = c < SnesOrigin;
                int d = left ? SnesOrigin - 1 - c : c - (SnesOrigin + Width);   // 0 = the column next to the SNES edge
                var rule = w.Layers[layer];
                bool black = false;
                for (int i = 0; i < masks.Length; i++)
                {
                    if ((sig & (1L << i)) == 0) continue;
                    var m = masks[i];
                    if (d < m.From || d >= m.To || (left ? !m.Left : !m.Right)) continue;
                    if (m.Rule == WmsSideRule.Black) { black = true; continue; }
                    if ((m.Layers & (1 << layer)) != 0) rule = m.Rule;
                }
                if (layer == 0) blackCol[c] = black;
                if (black) blackOn = true;
                map[c] = rule switch
                {
                    WmsSideRule.Extend or WmsSideRule.Drawn => (short)c,
                    WmsSideRule.Repeat => (short)(SnesOrigin + FloorMod(c - SnesOrigin, Width)),
                    _ => FillColumn,
                };
                if (rule == WmsSideRule.Drawn && layer < 4) { drawnCol[layer][c] = true; drawnOn[layer] = true; anyDrawn = true; }
                if (rule != WmsSideRule.Extend) any = true;
            }
            remapOn[layer] = any;
            if (layer < 4 && drawnOn[layer]) map.AsSpan().CopyTo(remapBase[layer]);
        }
    }

    // ---------------------------------------------------------------- the drawn rule: extend only where the game has drawn the map for the place shown

    // Per BG layer, the scroll unwrapped across frames (HOFS is 10 bits; a frame's step is taken as the short way round) and the span of the layer's map, in those unwrapped
    // pixels, that the game has drawn: from the left frontier L to the right frontier R. Each frame the SNES picture's columns are drawn, and the profile's ahead past the
    // edge it scrolls towards (the game streams them as it scrolls): R = the furthest right the picture's right edge (+ ahead, in a frame it scrolled right) has reached,
    // L likewise to the left. A map holds one map width: drawing past one frontier overwrites the columns one map width back, so a frontier that moves on pushes the other
    // along (L >= R - mapWidth). The drawn span is [L, R). A cut (a step of more than 64 pixels in a
    // frame, the layer's map, its tile size or BGMODE changing) starts the span again at the picture's own columns. Vertical scroll is not tracked: the side columns show the
    // centre's rows.
    private struct DrawnTrack { public bool Init, VInit; public int H, V, Sig; public long U, Vu, L, R, Frame; public int MapWidth; }
    private readonly DrawnTrack[] drawn = new DrawnTrack[4];
    private readonly short[][] remapBase = { new short[WideWidth], new short[WideWidth], new short[WideWidth], new short[WideWidth] };
    private readonly bool[][] drawnCol = { new bool[WideWidth], new bool[WideWidth], new bool[WideWidth], new bool[WideWidth] };
    private readonly bool[] drawnOn = new bool[4];
    private bool anyDrawn;
    private long enhFrame;
    private const int DrawnCutPixels = 64;

    /// <summary>Tools: a BG layer's drawn span this frame (unwrapped pixels: the picture's left edge, the span's left and right ends), or null when the layer is not on the drawn rule.</summary>
    public (long left, long from, long to)? DrawnSpan(int bg)
    {
        var t = drawn[bg];
        if (!t.Init || enhWide == null || enhWide.Layers[bg] != WmsSideRule.Drawn) return null;
        return (t.U, t.L, t.R);
    }

    private static int Signed10(int d) => ((d + 512) & 1023) - 512;

    /// <summary>This line's column maps for the layers on the drawn rule: the mask-resolved map, with the side columns the game has not drawn for the place they show filled, or,
    /// on a line of the layer's main scroll with the level in work RAM or the map memory, drawn from them (<see cref="SideFill"/>).</summary>
    private void ApplyDrawn(int row, int mode, int enabled)
    {
        if (mode == 7) { for (int bg = 0; bg < 4; bg++) if (drawnOn[bg]) remapBase[bg].AsSpan().CopyTo(remap[bg]); return; }
        var w = enhWide!;
        byte bgmode = RegBgmode;
        for (int bg = 0; bg < 4; bg++)
        {
            if (!drawnOn[bg]) continue;
            ref var t = ref drawn[bg];
            var mem = memory[bg];
            bool lvl = level != null && w.Level!.Layer == bg;
            int h = RegHofs(bg) & 0x3FF;
            if (t.Frame != enhFrame && (enabled & (1 << bg)) != 0)
            {
                // once a frame, on the first line that draws the layer: follow its scroll
                long prevFrame = t.Frame;
                int sc = RegBgsc(bg), big = (bgmode >> (4 + bg) & 1) | (mode is 5 or 6 ? 1 : 0);
                int sig = sc | big << 8 | mode << 9, ahead = w.DrawnAhead[bg];
                bool cut = !t.Init || sig != t.Sig;
                int d = 0;
                if (t.Init) { d = Signed10(h - t.H); t.U += d; if (Math.Abs(d) > DrawnCutPixels) cut = true; }
                else { t.U = h; t.Init = true; }
                t.H = h; t.Sig = sig; t.Frame = enhFrame;
                t.MapWidth = ((sc & 1) != 0 ? 512 : 256) << big;
                // the game draws ahead only on the side it scrolls towards: the frontier on that side moves to the picture's edge + ahead, the other to the picture's edge
                long l = t.U - (d < 0 && !cut ? ahead : 0), r = t.U + Width + (d > 0 && !cut ? ahead : 0);
                if (cut) { t.L = l; t.R = r; }
                else
                {
                    // a frontier that moves on pushes the other one along: the map holds one map width, so drawing past R overwrites what was drawn at R - mapWidth
                    if (r > t.R) { t.R = r; t.L = Math.Max(t.L, r - t.MapWidth); }
                    if (l < t.L) { t.L = l; t.R = Math.Min(t.R, l + t.MapWidth); }
                }
                // the vertical scroll, unwrapped the same way (the drawn span does not use it; the memory and the level do), and the main scroll's rows of last frame
                int v = RegVofs(bg) & 0x3FF;
                int dv = t.VInit ? Signed10(v - t.V) : 0;
                bool vcut = !t.VInit || Math.Abs(dv) > DrawnCutPixels;
                if (t.VInit) t.Vu += dv; else t.Vu = v;
                t.V = v; t.VInit = true;
                (bandPrev[bg], bandCur[bg]) = (bandCur[bg], bandPrev[bg]);
                Array.Clear(bandCur[bg]);
                if (mem != null) TrackMemory(bg, mem, ref t, cut || vcut, prevFrame);
                if (lvl) TrackLevel(bg, t, cut || vcut);
            }
            var map = remap[bg]; var bas = remapBase[bg]; var col = drawnCol[bg];
            sideLine[bg] = false;
            if (!t.Init) { bas.AsSpan().CopyTo(map); continue; }
            long lo = t.L, hi = t.R;
            long left = t.U + Signed10(h - t.H) - SnesOrigin;   // the unwrapped pixel picture column 0 shows on this line
            // a line of the layer's main scroll (this frame's HOFS and VOFS), in a mode the memory and the level read
            bool main = t.Frame == enhFrame && h == t.H && (RegVofs(bg) & 0x3FF) == t.V && mode is 0 or 1 or 3
                && ((RegMosaic & (1 << bg)) == 0 || (RegMosaic >> 4) == 0);
            if (main)
            {
                bandCur[bg][row] = true;
                if (mem != null) RecordLine(bg, mem, t.U, t.Vu, row + 1);
            }
            bool fromSource = main && (mem != null || (lvl && level!.Verified));
            sideLine[bg] = fromSource;
            var src = sideCol[bg];
            for (int c = 0; c < WideWidth; c++)
            {
                if (c == SnesOrigin) c = SnesOrigin + Width;
                long x = left + c;
                bool outside = col[c] && (x < lo || x >= hi);
                if (fromSource) { src[c] = outside; map[c] = outside ? (short)c : bas[c]; }
                else map[c] = outside ? FillColumn : bas[c];
            }
        }
    }

    // Per BG layer on the drawn rule: the rows of the main scroll last frame and this one, and this line's side columns drawn from the level or the memory.
    private bool[][] bandPrev = { new bool[MaxHeight], new bool[MaxHeight], new bool[MaxHeight], new bool[MaxHeight] };
    private bool[][] bandCur = { new bool[MaxHeight], new bool[MaxHeight], new bool[MaxHeight], new bool[MaxHeight] };
    private readonly bool[] sideLine = new bool[4];
    private readonly bool[][] sideCol = { new bool[WideWidth], new bool[WideWidth], new bool[WideWidth], new bool[WideWidth] };

    // ---------------------------------------------------------------- the map memory (spec 6.1): what the SNES picture showed of a drawn layer, by place in the level

    /// <summary>One BG layer's map memory: map entries by world tile (the tile column of the unwrapped horizontal scroll, the tile row of the unwrapped vertical scroll plus the line),
    /// a direct-mapped table of 131,072 tiles (a tile that collides with another is forgotten: filled). Emptied by a new generation, so forgetting costs nothing.</summary>
    private sealed class MapMemory
    {
        public const int Bits = 17, Slots = 1 << Bits;
        private readonly long[] key = new long[Slots];
        private readonly ushort[] entry = new ushort[Slots];
        private readonly int[] gen = new int[Slots];
        private int generation = 1;
        public bool Init;
        public long Writes, Pixels, Remembered, CutsScroll, CutsGap, CutsDisagree, Compared, Differed;
        public bool DisagreeCut = true;   // tests: false shows why the disagreement cut is needed (the negative control)
        public void Forget() { generation++; Init = false; }
        public void Clear() => generation++;
        private static int Slot(long k) => (int)(((ulong)k * 0x9E3779B97F4A7C15UL) >> (64 - Bits));
        public static long Key(long tx, long ty) => tx << 32 ^ (ty & 0xFFFFFFFFL);
        public bool TryGet(long k, out ushort e)
        {
            int s = Slot(k);
            if (gen[s] == generation && key[s] == k) { e = entry[s]; return true; }
            e = 0; return false;
        }
        public void Set(long k, ushort e) { int s = Slot(k); key[s] = k; entry[s] = e; gen[s] = generation; }
    }

    private readonly MapMemory?[] memory = new MapMemory?[4];

    /// <summary>Tools: the map memory's and the level's counters for a BG layer (null: the layer has neither).</summary>
    public string? MemoryStats(int bg)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (memory[bg] is { } m)
            parts.Add($"memory: {m.Writes:N0} tiles written; {m.Pixels:N0} side pixels asked, {m.Remembered:N0} remembered; emptied {m.CutsScroll:N0}x by a cut of the scroll or map, "
                    + $"{m.CutsGap:N0}x by a frame without the layer, {m.CutsDisagree:N0}x by the game's map disagreeing ({m.Differed:N0} of {m.Compared:N0} entries compared differed)");
        if (level is { } l && enhWide?.Level?.Layer == bg)
            parts.Add($"level in work RAM: verified on {l.FramesVerified:N0} of {l.FramesChecked:N0} frames ({l.Searches:N0} searches, {l.Found:N0} found an alignment); {l.Pixels:N0} side pixels from the level"
                    + $" ({l.Outside:N0} asked outside it); last check {l.LastMatch:N0} of {l.LastCompared:N0} entries agreed; the level {l.Width} x {l.Height} blocks");
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>Tests: the map memory's disagreement cut on (the default) or off (the negative control).</summary>
    public void SetMemoryDisagreeCut(bool on) { foreach (var m in memory) if (m != null) m.DisagreeCut = on; }

    /// <summary>The geometry of a SNES layer's map for the memory and the level: the map's base, its width and height in tiles, the tile's size.</summary>
    private (int mapBase, bool wide, bool tall, int shift) MemoryGeometry(int bg)
    {
        int sc = RegBgsc(bg);
        return ((sc & 0xFC) << 8, (sc & 1) != 0, (sc & 2) != 0, (RegBgmode & (0x10 << bg)) != 0 ? 4 : 3);
    }

    /// <summary>Once a frame, on the first line that draws the layer (before any of the frame is drawn): forget at a cut, after a frame without the layer, or when the map the SNES
    /// picture will show disagrees with the memory (spec 6.1).</summary>
    private void TrackMemory(int bg, MapMemory mem, ref DrawnTrack t, bool cut, long prevFrame)
    {
        bool gap = mem.Init && prevFrame != enhFrame - 1;
        bool scrollCut = cut || !mem.Init;
        mem.Init = true;
        if (scrollCut || gap)
        {
            mem.Clear();
            if (gap && !scrollCut) mem.CutsGap++; else mem.CutsScroll++;
            return;
        }
        // the map entries of last frame's main-scroll rows, at this frame's scroll, against the memory wherever it holds the same place
        var (mapBase, wide, tall, shift) = MemoryGeometry(bg);
        int colMask = wide ? 63 : 31, rowMask = tall ? 63 : 31;
        var (tx0, tx1) = UnaliasedTiles(t.U, shift, colMask);
        long lastTy = long.MinValue;
        int compared = 0, differ = 0;
        var rows = bandPrev[bg];
        for (int y = 0; y < VisibleHeight; y++)
        {
            if (!rows[y]) continue;
            long ty = (t.Vu + y + 1) >> shift;
            if (ty == lastTy) continue;
            lastTy = ty;
            for (long tx = tx0; tx <= tx1; tx++)
            {
                if (!mem.TryGet(MapMemory.Key(tx, ty), out ushort e)) continue;
                compared++;
                if (SnesMapEntry(mapBase, wide, tall, (int)(tx & colMask), (int)(ty & rowMask)) != e) differ++;
            }
        }
        mem.Compared += compared; mem.Differed += differ;
        if (mem.DisagreeCut && compared >= 64 && differ * 8 > compared) { mem.Clear(); mem.CutsDisagree++; }
    }

    /// <summary>The tile columns under the SNES picture's 256 columns whose map column holds them alone: when the picture shows more tile columns than the map has (a 32-tile map
    /// not on a tile boundary shows 33), the two at its ends share one map column and the picture shows one of them for both: neither is remembered (nor compared).</summary>
    private static (long from, long to) UnaliasedTiles(long u, int shift, int colMask)
    {
        long from = u >> shift, to = (u + Width - 1) >> shift;
        if (to - from + 1 > colMask + 1) { from++; to--; }
        return (from, to);
    }

    /// <summary>A line of the layer's main scroll: the map entries of the tiles under the SNES picture's 256 columns, at their places.</summary>
    private void RecordLine(int bg, MapMemory mem, long u, long vu, int line)
    {
        var (mapBase, wide, tall, shift) = MemoryGeometry(bg);
        int colMask = wide ? 63 : 31, rowMask = tall ? 63 : 31;
        long ty = (vu + line) >> shift;
        int rowT = (int)(ty & rowMask);
        var (from, to) = UnaliasedTiles(u, shift, colMask);
        for (long tx = from; tx <= to; tx++)
        {
            mem.Set(MapMemory.Key(tx, ty), SnesMapEntry(mapBase, wide, tall, (int)(tx & colMask), rowT));
            mem.Writes++;
        }
    }

    // ---------------------------------------------------------------- the level in work RAM (spec 6.1)

    /// <summary>The console's reading of the game's work RAM for the level source (the board's 128 KB; set by the session). Read only, as a debugger reads: no side effects.</summary>
    public byte[]? WorkRam { get; set; }

    /// <summary>The level source's state: the alignment (level tile = unwrapped scroll tile + offset), this frame's verdict, the counters.</summary>
    private sealed class LevelTrack
    {
        public bool Verified, Aligned;
        public long Ox, Oy, LastSearch = -LevelSearchEvery;
        public long FramesChecked, FramesVerified, Searches, Found, Pixels, Outside;
        public int LastMatch, LastCompared;
        public int Width, Height;
        public readonly int[] Seen = new int[65536];   // the search's count of each map entry in the picture (reused: no allocation)
    }
    private LevelTrack? level;
    /// <summary>Tests only (the negative control): once aligned, keep showing the level although the check fails (what a level source without the check would do).</summary>
    public bool LevelTrustWithoutCheck { get; set; }
    /// <summary>Tools: told each entry the level check finds different (null: nothing).</summary>
    public static Action<string>? LevelDebug;
    private const int LevelSearchEvery = 8;

    /// <summary>The level's map entry at level tile (lx, ly) (8 x 8 tiles, 2 x 2 to a block), or -1 outside the level or the work RAM.</summary>
    private int LevelEntry(WmsLevel d, byte[] ram, int width, int height, long lx, long ly)
    {
        if (lx < 0 || ly < 0) return -1;
        long bx = lx >> 1, by = ly >> 1;
        if (bx >= width || by >= height) return -1;
        long i = d.Blocks + 2 * (by * width + bx);
        if (i + 1 >= ram.Length) return -1;
        int wv = ram[i] | ram[i + 1] << 8;
        bool fx = (wv & d.FlipX) != 0, fy = (wv & d.FlipY) != 0;
        int qx = (int)(lx & 1), qy = (int)(ly & 1);
        if (fx) qx ^= 1;
        if (fy) qy ^= 1;
        long ti = d.Tiles + (long)(wv & d.Index) * 8 + (qy * 2 + qx) * 2;
        if (ti + 1 >= ram.Length) return -1;
        int e = ram[ti] | ram[ti + 1] << 8;
        if (fx) e ^= 0x4000;
        if (fy) e ^= 0x8000;
        return e;
    }

    /// <summary>Once a frame, before any of the frame is drawn: is the level in work RAM, at the alignment found before, what the SNES picture shows? (spec 6.1) If not, search for the
    /// alignment again (at most every 8 frames). The frame shows the level only when verified.</summary>
    private void TrackLevel(int bg, DrawnTrack t, bool cut)
    {
        var lt = level!; var d = enhWide!.Level!; var ram = WorkRam;
        lt.Verified = false;
        lt.FramesChecked++;
        if (ram == null) return;
        var (mapBase, wide, tall, shift) = MemoryGeometry(bg);
        if (shift != 3) return;   // the level's blocks are 2 x 2 tiles of 8 x 8
        lt.Width = ram[d.Width & 0x1FFFF] | ram[(d.Width + 1) & 0x1FFFF] << 8;
        lt.Height = ram[d.Height & 0x1FFFF] | ram[(d.Height + 1) & 0x1FFFF] << 8;
        if (lt.Width <= 0 || lt.Height <= 0 || (long)lt.Width * lt.Height > 0x10000) return;
        int colMask = wide ? 63 : 31, rowMask = tall ? 63 : 31;
        var (tx0, tx1) = UnaliasedTiles(t.U, shift, colMask);
        var rows = bandPrev[bg];
        bool any = false;
        for (int y = 0; y < VisibleHeight && !any; y++) any = rows[y];
        if (!any) return;   // no main-scroll rows last frame: nothing to check against
        (int match, int compared) Check(long ox, long oy, int step)
        {
            int match = 0, compared = 0; long lastTy = long.MinValue;
            for (int y = 0; y < VisibleHeight; y += 1)
            {
                if (!rows[y]) continue;
                long ty = (t.Vu + y + 1) >> 3;
                if (ty == lastTy || (step > 1 && ty % step != 0)) continue;
                lastTy = ty;
                for (long tx = tx0; tx <= tx1; tx += step)
                {
                    compared++;
                    int e = LevelEntry(d, ram, lt.Width, lt.Height, tx + ox, ty + oy);
                    if (e >= 0 && e == SnesMapEntry(mapBase, wide, tall, (int)(tx & colMask), (int)(ty & rowMask))) match++;
                    else if (LevelDebug != null && step == 1) LevelDebug($"frame {enhFrame} tile ({tx}, {ty}) + ({ox}, {oy}): map {SnesMapEntry(mapBase, wide, tall, (int)(tx & colMask), (int)(ty & rowMask)):X4} level {e:X4}");
                }
            }
            return (match, compared);
        }
        if (lt.Aligned && !cut)
        {
            var (m, n) = Check(lt.Ox, lt.Oy, 1);
            lt.LastMatch = m; lt.LastCompared = n;
            if (n >= 200 && (n - m) * 50 <= n) { lt.Verified = true; lt.FramesVerified++; return; }
            if (LevelTrustWithoutCheck) { lt.Verified = true; return; }   // tests only: the negative control of the check
        }
        // search: a distinctive tile of the picture (its entry appears once among the checked ones), every level place holding it tried
        if (enhFrame - lt.LastSearch < LevelSearchEvery) return;
        lt.LastSearch = enhFrame; lt.Searches++; lt.Aligned = false;
        long ay = long.MinValue, ax = 0; ushort ae = 0;
        var seen = lt.Seen;
        long lastRow = long.MinValue;
        for (int y = 0; y < VisibleHeight; y++)
        {
            if (!rows[y]) continue;
            long ty = (t.Vu + y + 1) >> 3;
            if (ty == lastRow) continue;
            lastRow = ty;
            for (long tx = tx0; tx <= tx1; tx++)
            {
                ushort e = SnesMapEntry(mapBase, wide, tall, (int)(tx & colMask), (int)(ty & rowMask));
                seen[e]++;
            }
        }
        lastRow = long.MinValue;
        for (int y = 0; y < VisibleHeight && ay == long.MinValue; y++)
        {
            if (!rows[y]) continue;
            long ty = (t.Vu + y + 1) >> 3;
            if (ty == lastRow) continue;
            lastRow = ty;
            for (long tx = tx0; tx <= tx1; tx++)
            {
                ushort e = SnesMapEntry(mapBase, wide, tall, (int)(tx & colMask), (int)(ty & rowMask));
                if (seen[e] == 1) { ax = tx; ay = ty; ae = e; break; }
            }
        }
        // the counts back to 0 for the next search
        lastRow = long.MinValue;
        for (int y = 0; y < VisibleHeight; y++)
        {
            if (!rows[y]) continue;
            long ty = (t.Vu + y + 1) >> 3;
            if (ty == lastRow) continue;
            lastRow = ty;
            for (long tx = tx0; tx <= tx1; tx++) seen[SnesMapEntry(mapBase, wide, tall, (int)(tx & colMask), (int)(ty & rowMask))] = 0;
        }
        if (ay == long.MinValue) return;
        for (long ly = 0; ly < lt.Height * 2L; ly++)
            for (long lx = 0; lx < lt.Width * 2L; lx++)
            {
                if (LevelEntry(d, ram, lt.Width, lt.Height, lx, ly) != ae) continue;
                long ox = lx - ax, oy = ly - ay;
                var (sm, sn) = Check(ox, oy, 3);
                if (sn == 0 || (sn - sm) * 50 > sn) continue;
                var (m, n) = Check(ox, oy, 1);
                lt.LastMatch = m; lt.LastCompared = n;
                if (n >= 200 && (n - m) * 50 <= n)
                {
                    lt.Ox = ox; lt.Oy = oy; lt.Aligned = true; lt.Verified = true; lt.Found++; lt.FramesVerified++;
                    return;
                }
            }
    }

    /// <summary>The side columns this line draws from the level in work RAM or the map memory (after <see cref="SnesBg"/>): each place's tile, with the game's tiles and colours of
    /// now; a place neither holds is transparent (filled).</summary>
    private void SideFill(int bg, int bpp, int mode, int line)
    {
        var mem = memory[bg];
        var lt = level != null && enhWide!.Level!.Layer == bg && level.Verified ? level : null;
        var d = lt != null ? enhWide!.Level : null; var ram = WorkRam;
        var colors = bgCol[bg]; var prios = bgPri[bg]; var src = sideCol[bg];
        var (_, _, _, shift) = MemoryGeometry(bg);
        int size = 1 << shift, charBase = CharBase(bg), wordsPerTile = bpp * 4;
        int paletteBase = mode == 0 ? bg * 32 : 0;
        bool direct = bpp == 8 && (RegCgwsel & 0x01) != 0;
        ref var t = ref drawn[bg];
        long left = t.U - SnesOrigin;   // a line of the main scroll: HOFS is the frame's
        long wy = t.Vu + line, ty = wy >> shift;
        int pyRaw = (int)(wy & (size - 1));
        var vram = Vram; var cgram = Cgram;
        long lastTx = long.MinValue; int entry = -1; bool memHit = false;
        for (int c = 0; c < WideWidth; c++)
        {
            if (c == SnesOrigin) c = SnesOrigin + Width;
            if (!src[c]) continue;
            long wx = left + c, tx = wx >> shift;
            if (tx != lastTx)
            {
                lastTx = tx; entry = -1; memHit = false;
                if (lt != null)
                {
                    entry = LevelEntry(d!, ram!, lt.Width, lt.Height, tx + lt.Ox, ty + lt.Oy);
                    if (entry < 0) lt.Outside++;
                }
                if (entry < 0 && mem != null && mem.TryGet(MapMemory.Key(tx, ty), out ushort me)) { entry = me; memHit = true; }
            }
            if (mem != null) { mem.Pixels++; if (memHit) mem.Remembered++; }
            if (entry < 0) { colors[c] = Clear; continue; }
            if (lt != null && !memHit) lt.Pixels++;
            int px = (int)(wx & (size - 1)), py = pyRaw;
            if ((entry & 0x4000) != 0) px = size - 1 - px;
            if ((entry & 0x8000) != 0) py = size - 1 - py;
            int tile = entry & 0x3FF;
            if (px >= 8) tile += 1;
            if (py >= 8) tile += 16;
            int addr = charBase + (tile & 0x3FF) * wordsPerTile + (py & 7);
            int bit = 7 - (px & 7), v = 0;
            for (int plane = 0; plane < bpp; plane += 2)
            {
                ushort wd = vram[(addr + plane * 4) & 0x7FFF];
                v |= ((wd >> bit) & 1) << plane | ((wd >> (8 + bit)) & 1) << (plane + 1);
            }
            if (v == 0) { colors[c] = Clear; continue; }
            int pal = (entry >> 10) & 7;
            colors[c] = direct ? SnesDirectColour(v, pal) : cgram[(paletteBase + (bpp == 8 ? 0 : pal << bpp) + v) & 0xFF];
            prios[c] = (byte)((entry >> 13) & 1);
        }
    }

    /// <summary>A state load: the map memory and the level's alignment describe the machine that was here before (spec 6.1: forgotten).</summary>
    private void ForgetMemory()
    {
        foreach (var m in memory) m?.Forget();
        if (level != null) { level.Aligned = false; level.Verified = false; level.LastSearch = enhFrame - LevelSearchEvery; }
    }

    /// <summary>True when every scene condition holds on this line (the registers as the game last wrote them).</summary>
    private bool SceneMatches(WmsCondition[] when)
    {
        for (int i = 0; i < when.Length; i++)
        {
            var c = when[i];
            byte v = c.Register switch
            {
                WmsRegister.Bgmode => RegBgmode,
                WmsRegister.Bg1sc => RegBgsc(0), WmsRegister.Bg2sc => RegBgsc(1), WmsRegister.Bg3sc => RegBgsc(2), WmsRegister.Bg4sc => RegBgsc(3),
                WmsRegister.Bg12nba => RegBg12nba, WmsRegister.Bg34nba => RegBg34nba,
                WmsRegister.Tm => RegTm, WmsRegister.Ts => RegTs, WmsRegister.Obsel => RegObsel, WmsRegister.Setini => RegSetini,
                WmsRegister.Cgwsel => RegCgwsel, _ => RegCgadsub,
            };
            if ((v & c.Mask) != c.Value) return false;
        }
        return true;
    }

    /// <summary>True when this BG layer's side columns are not all extended on this line (it then takes the per-pixel path and the column map).</summary>
    private bool Remapped(int layer) => lnEnh && enhWide != null && remapOn[layer];

    /// <summary>A layer's line (drawn over the whole picture into bgCol / bgPri) composed through this line's column map (widescreen side rules).</summary>
    private void ComposeBgMapped(int layer, bool windowed, int total, int shift, byte[] rank, ushort[] color, byte[] lay)
    {
        byte r0 = rankBg[layer * 2], r1 = rankBg[layer * 2 + 1];
        if (r0 == 0xFF && r1 == 0xFF) return;
        var src = bgCol[layer]; var prio = bgPri[layer]; var mask = win[layer]; var map = remap[layer];
        for (int bx = 0; bx < total; bx++)
        {
            int m = map[bx >> shift];
            if (m < 0) continue;
            int sx = shift == 0 ? m : (m << 1) | (bx & 1);
            ushort v = src[sx];
            if (v == Clear || (windowed && mask[bx >> shift])) continue;
            byte r = prio[sx] != 0 ? r1 : r0;
            if (r < rank[bx]) { rank[bx] = r; color[bx] = v; lay[bx] = (byte)layer; }
        }
    }

    /// <summary>No flicker without widescreen, a hi-res line (modes 5, 6): the line's 512 half-pixels into the SNES chip's hi-res picture, as the SNES chip keeps them, so the published frame
    /// stays sharp (the 256-wide line, the pairs averaged, is in the frame buffer already).</summary>
    private void EnhancedHiRes(int row, uint[] lut, bool math)
    {
        int hiRow = row * HiResWidth;
        for (int x = 0; x < Width; x++)
        {
            HiResBuffer[hiRow + 2 * x] = lut[FinalPixel(2 * x, x)];
            HiResBuffer[hiRow + 2 * x + 1] = lut[FinalPixel(2 * x + 1, x)];
        }
        MarkHiResRow(row);
    }

    /// <summary>Masks with the black rule: those columns of the line are black (spec 6.1).</summary>
    private void BlackColumns(Span<uint> dst)
    {
        if (!blackOn) return;
        for (int c = 0; c < dst.Length; c++) if (blackCol[c]) dst[c] = 0xFF000000u;
    }

    // ---------------------------------------------------------------- sprites

    /// <summary>
    /// The SNES sprite table for an enhanced line (spec 6.1).
    /// <list type="bullet">
    /// <item>The SNES's pass on its 256 columns, in picture columns 72-327 (or 0-255 without widescreen): the in-range list and the sliver budget of the SNES chip's RenderSprites,
    /// with the SNES's limits (32, 34) or, with no flicker, the WideMess's (64, 128). STAT77's flags are raised from the SNES's own count either way, so the game reads what a SNES gives it.</item>
    /// <item>Widescreen with the sprites extended: the parts of every sprite that fall in the side columns, drawn in addition and never counted, a lower table index (after the
    /// rotation) in front. X as spec 3.3: 0-327 is picture column 72 + X, 328-511 is X - 512.</item>
    /// </list>
    /// </summary>
    private void EnhancedSprites(int row, int width, int origin)
    {
        objCol.AsSpan(0, width).Fill(Clear);
        byte obsel = RegObsel; ushort oamadd = RegOamadd;
        int sizeSel = obsel >> 5;
        int nameBase = (obsel & 7) << 13;
        int nameGap = (((obsel >> 3) & 3) + 1) << 12;
        int first = (oamadd & 0x8000) != 0 ? (oamadd >> 1) & 0x7F : 0;   // OAM priority rotation
        var oam = Oam;
        int limit = enhNoFlicker ? NoFlickerSprites : 32, sliverLimit = enhNoFlicker ? NoFlickerSlivers : 34;
        Span<byte> list = stackalloc byte[NoFlickerSprites];
        int count = 0;
        bool range = false;
        for (int i = 0; i < 128; i++)
        {
            int n = (first + i) & 127;
            int hi = (oam[0x200 + (n >> 2)] >> ((n & 3) * 2)) & 3;
            var (w, h) = SnesObjSize(sizeSel, (hi & 2) != 0);
            int x = oam[n * 4] | (hi & 1) << 8;
            if (x >= 256) x -= 512;                                   // the SNES's X
            if (x <= -w && x != -256) continue;
            if (((row - oam[n * 4 + 1]) & 0xFF) >= h) continue;
            if (count == 32) range = true;                             // the SNES stops here: STAT77's range flag
            if (count == limit) break;
            list[count++] = (byte)n;
        }
        // STAT77's time flag: the SNES fetches the slivers of its first 32 (last first) and stops past 34
        int snesSlivers = 0;
        for (int k = Math.Min(count, 32) - 1; k >= 0; k--) snesSlivers += SnesVisibleSlivers(list[k], sizeSel);
        bool time = snesSlivers > 34;
        RaiseSpriteOverflow(range, time);
        if (range || time) EnhancedOverflowRow[row] = true;

        // the centre: last sprite first, the sliver budget the SNES's way (on overflow the front sprites lose theirs); later draws overwrite earlier ones
        int slivers = 0;
        for (int k = count - 1; k >= 0; k--)
        {
            int n = list[k];
            int hi = (oam[0x200 + (n >> 2)] >> ((n & 3) * 2)) & 3;
            var (w, h) = SnesObjSize(sizeSel, (hi & 2) != 0);
            int x = oam[n * 4] | (hi & 1) << 8;
            if (x >= 256) x -= 512;
            int tilesWide = w >> 3;
            bool stop = false;
            for (int tx = 0; tx < tilesWide; tx++)
            {
                int sx = x + tx * 8;
                if (sx <= -8 || sx >= 256) continue;
                if (++slivers > sliverLimit) { stop = true; break; }
                DrawSnesSliver(n, sizeSel, nameBase, nameGap, row, w, h, tx, origin + sx, origin, origin + Width);
            }
            if (stop) break;
        }

        if (width == WideWidth && enhWide!.Layers[WmsWidescreen.Sprites] == WmsSideRule.Extend) SideSprites(row, sizeSel, nameBase, nameGap, first);
    }

    /// <summary>The slivers of sprite n the SNES fetches on a line (its 8-pixel columns that reach its 256 columns).</summary>
    private int SnesVisibleSlivers(int n, int sizeSel)
    {
        var oam = Oam;
        int hi = (oam[0x200 + (n >> 2)] >> ((n & 3) * 2)) & 3;
        var (w, _) = SnesObjSize(sizeSel, (hi & 2) != 0);
        int x = oam[n * 4] | (hi & 1) << 8;
        if (x >= 256) x -= 512;
        int c = 0;
        for (int tx = 0; tx < w >> 3; tx++) { int sx = x + tx * 8; if (sx > -8 && sx < 256) c++; }
        return c;
    }

    /// <summary>The side columns' sprites (widescreen, sprites extended): every sprite's parts in columns 0-71 and 328-399, last first so a lower index ends in front; never counted.</summary>
    private void SideSprites(int row, int sizeSel, int nameBase, int nameGap, int first)
    {
        var oam = Oam;
        int split = Width + SnesOrigin;
        for (int i = 127; i >= 0; i--)
        {
            int n = (first + i) & 127;
            int hi = (oam[0x200 + (n >> 2)] >> ((n & 3) * 2)) & 3;
            var (w, h) = SnesObjSize(sizeSel, (hi & 2) != 0);
            if (((row - oam[n * 4 + 1]) & 0xFF) >= h) continue;
            int x = oam[n * 4] | (hi & 1) << 8;
            if (x == 256) continue;                                    // the SNES's X = -256, where games hide sprites: never drawn in the sides (spec 6.1)
            if (x >= split) x -= 512;                                 // spec 3.3: 0-327 at 72 + X, 328-511 at X - 512 + 72
            int left = SnesOrigin + x, right = left + w;               // picture columns [left, right)
            bool inLeft = left < SnesOrigin && right > 0, inRight = right > SnesOrigin + Width && left < WideWidth;
            if (!inLeft && !inRight) continue;
            for (int tx = 0; tx < w >> 3; tx++)
            {
                int px = left + tx * 8;
                if (px + 8 <= 0 || px >= WideWidth) continue;
                if (px >= SnesOrigin && px + 8 <= SnesOrigin + Width) continue;   // wholly in the centre: the SNES's pass drew (or dropped) it
                if (px < SnesOrigin) DrawSnesSliver(n, sizeSel, nameBase, nameGap, row, w, h, tx, px, 0, SnesOrigin);
                if (px + 8 > SnesOrigin + Width) DrawSnesSliver(n, sizeSel, nameBase, nameGap, row, w, h, tx, px, SnesOrigin + Width, WideWidth);
            }
        }
    }

    /// <summary>One 8-pixel sliver of SNES sprite n (tile column tx) at picture column px, drawn only into picture columns [clipLeft, clipRight).</summary>
    private void DrawSnesSliver(int n, int sizeSel, int nameBase, int nameGap, int row, int w, int h, int tx, int px, int clipLeft, int clipRight)
    {
        var oam = Oam;
        int tileLo = oam[n * 4 + 2], attr = oam[n * 4 + 3];
        bool hflip = (attr & 0x40) != 0;
        int pal = (attr >> 1) & 7, prio = (attr >> 4) & 3;
        int r = (row - oam[n * 4 + 1]) & 0xFF;
        if ((attr & 0x80) != 0) r = h - 1 - r;
        int ty = r >> 3, fy = r & 7;
        int table = (attr & 1) != 0 ? nameBase + nameGap : nameBase;
        int tilesWide = w >> 3;
        int ctx = hflip ? tilesWide - 1 - tx : tx;
        int chr = (((tileLo >> 4) + ty) & 0x0F) << 4 | ((tileLo + ctx) & 0x0F);
        int addr = (table + chr * 16 + fy) & 0x7FFF;
        var vram = Vram;
        int p01 = vram[addr], p23 = vram[(addr + 8) & 0x7FFF];
        var e = hflip ? ExpandFlip : Expand;
        ulong pixels = e[p01 & 0xFF] | e[p01 >> 8] << 1 | e[p23 & 0xFF] << 2 | e[p23 >> 8] << 3;
        if (pixels == 0) return;
        int start = Math.Max(0, clipLeft - px), end = Math.Min(8, clipRight - px);
        var cgram = Cgram;
        for (int i = start; i < end; i++)
        {
            int c = (int)(pixels >> (i << 3)) & 0xFF;
            if (c == 0) continue;
            objCol[px + i] = cgram[128 + (pal << 4) + c];
            objPri[px + i] = (byte)prio;
            objMth[px + i] = pal >= 4;
        }
    }

    /// <summary>The sprites composed through this line's column map: a side column whose sprite rule is fill shows no sprite.</summary>
    private void ComposeObjMapped(bool windowed, int total, int shift, byte[] rank, ushort[] color, byte[] lay)
    {
        var mask = win[ObjId]; var map = remap[WmsWidescreen.Sprites];
        for (int bx = 0; bx < total; bx++)
        {
            int x = bx >> shift;
            if (map[x] < 0) continue;
            ushort v = objCol[x];
            if (v == Clear || (windowed && mask[x])) continue;
            byte r = rankObj[objPri[x]];
            if (r < rank[bx]) { rank[bx] = r; color[bx] = v; lay[bx] = ObjId; }
        }
    }
}
