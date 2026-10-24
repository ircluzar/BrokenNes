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

    /// <summary>Every layer extended, the sprites too, no masks: what the tests and the profile tools start from (never applied to a game without its profile saying so).</summary>
    public static WmsWidescreen AllExtend() => new();
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
        }
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
        for (int layer = 0; layer < 5; layer++)
        {
            var map = remap[layer];
            bool any = false;
            for (int c = 0; c < WideWidth; c++)
            {
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
                    WmsSideRule.Extend => (short)c,
                    WmsSideRule.Repeat => (short)(SnesOrigin + FloorMod(c - SnesOrigin, Width)),
                    _ => FillColumn,
                };
                if (rule != WmsSideRule.Extend) any = true;
            }
            remapOn[layer] = any;
        }
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
