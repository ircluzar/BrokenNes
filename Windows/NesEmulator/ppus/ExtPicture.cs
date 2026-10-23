using System;

namespace NesEmulator
{
    /// <summary>
    /// THE CROSS-CONSOLE PICTURE: one console-neutral description of another console's frame that every enhanced picture chip draws
    /// (<see cref="IEnhancedPictureChip"/>: FIX enhanced PPU_FIXS and Game Boy enhanced PPU_DMGS today; the SFC, SMS and MD enhanced chips next).
    /// A translator fills it from the guest's own chip each frame - its size (<see cref="Width"/> x <see cref="Height"/>, up to 320 x 240: a Genesis in H40,
    /// a 240-line Master System or Genesis), as many background layers as the guest uses (up to <see cref="MaxLayers"/>: SNES mode 0 has four, mode 1 three,
    /// a Genesis plane B, plane A and the window, a Master System one and its vertical-lock columns, a Game Boy its background and window, a NES one), a Mode 7
    /// layer and an extended sprite list - and the chip composes it one source line at a time in its own colours (2C02 colours, Game Boy shades, CGB colours).
    /// Tiles are 2-bit values (a brightness level 1-3 inside a palette, 0 = see-through) and palettes are RGB. Everything that can change down a frame is per
    /// line - which layers are on, scroll, priority placement, windows, the Mode 7 matrix, sprites on/off and the backdrop - so status bars, split screens,
    /// window-cut boxes and Mode 7 floors survive. What stays limited: 2-bit tiles (3 levels per palette), 8 palettes per kind and 1024 tiles per layer.
    /// Colour math: the fixed colour is blended in (SNES rules - which layers, add/subtract, half, the colour window), the sub screen is picked, not mixed.
    /// Source lines are 0-based; x runs 0 .. <see cref="Width"/> - 1 in the guest's pixels. Per-pixel masks are addressed line * <see cref="Stride"/> + x.
    /// </summary>
    public sealed class ExtPicture
    {
        /// <summary>The largest guest picture: 320 wide (Genesis H40), 240 lines (a Genesis in V30, the Master System's 240-line mode, the NES).</summary>
        public const int MaxWidth = 320, MaxLines = 240, Stride = MaxWidth;
        public const int MaxLayers = 8, MaxTiles = 1024, MaxSpriteTiles = 1024, Palettes = 8;

        /// <summary>Composed pixel keys: 0 backdrop, 1 backdrop where colour math is blocked, 2 black (clip to black); else
        /// kind | palette &lt;&lt; 2 | level, with kind 0x20 background layer, 0x40 sprite, 0x60 Mode 7.</summary>
        public const byte KeyBackdrop = 0, KeyBackdropAlt = 1, KeyBlack = 2, KindBg = 0x20, KindObj = 0x40, KindM7 = 0x60;

        /// <summary>The guest picture's size this frame (set by <see cref="BeginFrame"/>).</summary>
        public int Width { get; private set; } = 256;
        public int Height { get; private set; } = 224;
        /// <summary>What the translator says the guest is showing ("SNES mode", "Genesis H40", "Master System", ...: a constant, so a frame makes no string), with <see cref="GuestMode"/>
        /// the mode number where the guest has modes (the SNES's BG mode), else -1. <see cref="Describe"/> makes the whole sentence, for counters and tests.</summary>
        public string Guest = "";
        public int GuestMode = -1;
        /// <summary>"SNES mode 1, 256 x 224, 3 layers".</summary>
        public string Describe() => $"{Guest}{(GuestMode >= 0 ? " " + GuestMode : "")}, {Width} x {Height}, {LayersInUse} layer{(LayersInUse == 1 ? "" : "s")}";

        public sealed class Layer
        {
            public bool Enabled;
            /// <summary>What the layer is in the guest ("BG1", "plane A", "window", "background"), for counters and tests.</summary>
            public string Name = "";
            public readonly byte[] Tiles = new byte[MaxTiles * 64];
            public readonly ushort[] Map = new ushort[128 * 128];
            /// <summary>Bits 0-2 palette 0-7, bit 3 priority.</summary>
            public readonly byte[] Attr = new byte[128 * 128];
            /// <summary>Map size in 8x8 cells (powers of two up to 128); the scroll wraps at 8x that.</summary>
            public int MapWidth = 64, MapHeight = 64;
            public readonly bool[] On = new bool[MaxLines];
            /// <summary>Per line: the map pixel shown at x = 0 of this line is (ScrollX, ScrollY + line).</summary>
            public readonly short[] ScrollX = new short[MaxLines], ScrollY = new short[MaxLines];
            public readonly byte[] ZLow = new byte[MaxLines], ZHigh = new byte[MaxLines];
            /// <summary>Per pixel (line * <see cref="Stride"/> + x): masked by a window.</summary>
            public readonly bool[] Hidden = new bool[MaxLines * Stride];
            public bool AnyHidden;
            /// <summary>The SNES layer (0-3 = BG1-4) this is, for colour math.</summary>
            public int SnesLayer;
            /// <summary>Optional (null for every SNES layer): the Genesis's 2-cell column scroll, set by the Genesis bridge only. A vertical scroll of its own for each PAIR of the plane's cells (16 pixels), by pair
            /// number (20 entries), added to <see cref="ScrollY"/>. The pairs of a line start where <see cref="ColumnShift"/> says (the plane's horizontal scroll mod 16 decides: pair number = (x + shift) >> 4);
            /// pixels before the first pair (x + shift below 0) take <see cref="ColumnLeft"/>.</summary>
            public short[]? ColumnScroll;
            private short[]? columnScrollSpare;
            /// <summary>A column-scroll table of <paramref name="n"/> entries for the Genesis bridge to fill and put in <see cref="ColumnScroll"/>: the layer keeps one between frames (<see cref="Reset"/> clears the field, not the table), so a frame does not make a new array.</summary>
            public short[] TakeColumnScroll(int n) => columnScrollSpare is { } s && s.Length == n ? s : (columnScrollSpare = new short[n]);
            public short ColumnLeft;
            public readonly short[] ColumnShift = new short[MaxLines];
            /// <summary>Per line, the Genesis's window-at-the-left quirk of the column scroll: for the <see cref="ColumnExtra"/> pixels from <see cref="ColumnExtraFrom"/> on, the pair number counts that many pixels further (the pair after the one the shift gives).</summary>
            public readonly short[] ColumnExtraFrom = new short[MaxLines], ColumnExtra = new short[MaxLines];
            /// <summary>The column scroll at source x <paramref name="sx"/> of <paramref name="line"/> (only for a layer that has <see cref="ColumnScroll"/>).</summary>
            public int ColumnValue(int line, int sx)
            {
                int ci = sx + ColumnShift[line];
                int ex = ColumnExtra[line];
                if (ex != 0 && sx >= ColumnExtraFrom[line] && sx < ColumnExtraFrom[line] + ex) ci += ex;
                return ci < 0 ? ColumnLeft : ColumnScroll![Math.Min(ci >> 4, ColumnScroll.Length - 1)];
            }
            /// <summary>Per line, a blank left column (the Genesis's register 0 bit 5, the NES's left-8 clip): the first this-many output pixels show nothing of this layer.</summary>
            public readonly byte[] BlankLeft = new byte[MaxLines];
            /// <summary>Per line, the blank left column's quirk in shadow/highlight mode: from this source x (-1: never) the 8 pixels show this layer's pixels 8 to the left (the second cell shows the first cell's planes).</summary>
            public readonly short[] CellOneStart = new short[MaxLines];

            public void Reset()
            {
                Enabled = false; AnyHidden = false; ColumnScroll = null; Name = "";
                Array.Clear(On); Array.Clear(Map); Array.Clear(Attr);
                Array.Clear(ColumnExtra); Array.Clear(BlankLeft); Array.Fill(CellOneStart, (short)-1);
            }
        }

        public sealed class Mode7Layer
        {
            public bool Enabled;
            /// <summary>128x128 tile map and 256 tiles of 8x8 texels; a texel is palette &lt;&lt; 2 | level (0 = see-through).</summary>
            public readonly byte[] Map = new byte[128 * 128];
            public readonly byte[] Tex = new byte[256 * 64];
            public readonly bool[] On = new bool[MaxLines];
            /// <summary>Per line, as the SNES registers were: matrix A-D, centre X/Y, scroll H/V, M7SEL, and the SNES line number.</summary>
            public readonly int[] A = new int[MaxLines], B = new int[MaxLines], C = new int[MaxLines], D = new int[MaxLines],
                X = new int[MaxLines], Y = new int[MaxLines], H = new int[MaxLines], V = new int[MaxLines], SnesLine = new int[MaxLines];
            public readonly byte[] Sel = new byte[MaxLines], Z = new byte[MaxLines];
            public readonly bool[] Hidden = new bool[MaxLines * Stride];
            public bool AnyHidden;

            public void Reset() { Enabled = false; AnyHidden = false; Array.Clear(On); }
        }

        /// <summary>One 8x8 sprite piece: Palette 0-7, Priority 0-3 (placed by <see cref="ObjZ"/>); earlier pieces are in front.
        /// Drawn only on source lines FirstLine..LastLine (the lines whose OAM it came from - games rewrite OAM mid-frame).</summary>
        public struct Sprite { public short X, Y, FirstLine, LastLine; public ushort Tile; public byte Palette, Priority; public bool HFlip, VFlip, MathPalette; }

        public readonly Layer[] Layers = new Layer[MaxLayers];
        public int LayerCount;
        public readonly Mode7Layer Mode7 = new();

        public readonly byte[] SpriteTiles = new byte[MaxSpriteTiles * 64];
        public Sprite[] Sprites = new Sprite[1024];
        public int SpriteCount;
        public int SpriteLimit = 1024, SpritesPerLine = 34;
        public readonly bool[] ObjOn = new bool[MaxLines];
        /// <summary>Per line and sprite priority (line * 4 + p): its place on the ladder.</summary>
        public readonly byte[] ObjZ = new byte[MaxLines * 4];
        public readonly bool[] ObjHidden = new bool[MaxLines * Stride];
        public bool AnyObjHidden;
        /// <summary>Pieces the per-line limit dropped in the last composed frame.</summary>
        public int SpriteLineDrops;

        /// <summary>0xRRGGBB per palette and level (index palette * 4 + level; level 0 unused).</summary>
        public readonly uint[] BgRgb = new uint[Palettes * 4], ObjRgb = new uint[Palettes * 4], M7Rgb = new uint[Palettes * 4];
        /// <summary>Bumped whenever the palettes change, so a chip knows to re-derive its own colours.</summary>
        public int PaletteVersion;

        public readonly uint[] BackdropRgb = new uint[MaxLines], BackdropAltRgb = new uint[MaxLines];
        public readonly bool[] BackdropAlt = new bool[MaxLines * Stride];
        public bool AnyBackdropAlt;
        public readonly bool[] Black = new bool[MaxLines * Stride];
        public bool AnyBlack;

        /// <summary>Per line, colour math with the fixed colour: which layers take part (bit 0-3 BG1-4, bit 4 sprites with
        /// palettes 4-7; 0 = none), bit 7 subtract, bit 6 half (<see cref="MathMode"/>), and the fixed colour as RGB.</summary>
        public readonly byte[] MathLayers = new byte[MaxLines], MathMode = new byte[MaxLines];
        public readonly uint[] MathRgb = new uint[MaxLines];
        /// <summary>Per pixel: colour math blocked here by the colour window.</summary>
        public readonly bool[] MathBlocked = new bool[MaxLines * Stride];
        public bool AnyMathBlocked;

        /// <summary>False: nothing is drawn from it (no frame has begun).</summary>
        public bool Active;

        public ExtPicture() { for (int i = 0; i < MaxLayers; i++) Layers[i] = new Layer(); }

        /// <summary>The layers the frame uses (enabled and on for at least one line).</summary>
        public int LayersInUse
        {
            get
            {
                int n = 0;
                for (int i = 0; i < LayerCount; i++)
                {
                    var L = Layers[i]; if (!L.Enabled) continue;
                    for (int y = 0; y < Height; y++) if (L.On[y]) { n++; break; }
                }
                return n;
            }
        }

        /// <summary>Start a new frame of a <paramref name="width"/> x <paramref name="height"/> guest picture: everything off (the translator fills in what the frame has).</summary>
        public void BeginFrame(int width, int height)
        {
            if (width < 8 || width > MaxWidth) throw new ArgumentOutOfRangeException(nameof(width), $"8-{MaxWidth}");
            if (height < 8 || height > MaxLines) throw new ArgumentOutOfRangeException(nameof(height), $"8-{MaxLines}");
            Width = width; Height = height; Guest = ""; GuestMode = -1;
            for (int i = 0; i < MaxLayers; i++) Layers[i].Reset();
            LayerCount = 0;
            Mode7.Reset();
            SpriteCount = 0; SpriteLineDrops = 0;
            Array.Clear(ObjOn); AnyObjHidden = false; AnyBackdropAlt = false; AnyBlack = false; AnyMathBlocked = false;
            Array.Clear(MathLayers);
            Array.Clear(BackdropRgb); Array.Clear(BackdropAltRgb);
            Active = true;
        }

        private readonly bool[] taken = new bool[MaxWidth];

        /// <summary>
        /// Compose source line <paramref name="line"/> for output pixels 0..count-1, output pixel i showing source x = x0 + i.
        /// Writes each pixel's ladder position (0 = nothing but the backdrop) and key (see <see cref="KindBg"/>). Returns false
        /// when the line is outside the guest picture.
        /// </summary>
        public bool ComposeLine(int line, int x0, int count, Span<byte> z, Span<byte> key) => ComposeLine(line, x0, count, z, key, default);

        /// <summary>As above; <paramref name="math"/> (when not empty) gets true where the pixel blends with the fixed colour
        /// (<see cref="Blend"/>).</summary>
        public bool ComposeLine(int line, int x0, int count, Span<byte> z, Span<byte> key, Span<bool> math)
        {
            if ((uint)line >= (uint)Height) return false;
            int W = Width;
            z.Slice(0, count).Clear();
            bool wantMath = !math.IsEmpty && MathLayers[line] != 0;
            if (!math.IsEmpty) math.Slice(0, count).Clear();
            var src = wantMath ? winner.AsSpan(0, count) : default;
            if (wantMath) src.Fill(0xFF);
            int row = line * Stride;
            for (int i = 0; i < count; i++)
            {
                int sx = x0 + i;
                key[i] = (uint)sx < (uint)W && AnyBackdropAlt && BackdropAlt[row + sx] ? KeyBackdropAlt : KeyBackdrop;
            }
            for (int k = 0; k < LayerCount; k++)
            {
                var L = Layers[k];
                if (!L.Enabled || !L.On[line]) continue;
                int wMask = L.MapWidth * 8 - 1, hMask = L.MapHeight * 8 - 1, shiftW = Log2(L.MapWidth);
                int baseY = L.ScrollY[line] + line;
                int my = baseY & hMask, ty = my & 7, mapRow = (my >> 3) << shiftW;
                var colScroll = L.ColumnScroll;
                byte zl = L.ZLow[line], zh = L.ZHigh[line];
                int blank = L.BlankLeft[line], cellOne = L.CellOneStart[line];
                for (int i = 0; i < count; i++)
                {
                    int sx = x0 + i;
                    if ((uint)sx >= (uint)W) continue;
                    if (sx < blank) continue;                                           // a blank left column: nothing of this layer
                    int px = cellOne >= 0 && (uint)(sx - cellOne) < 8u ? sx - 8 : sx;   // ... whose second cell shows the first cell's pixels in shadow/highlight mode
                    if (L.AnyHidden && L.Hidden[row + px]) continue;
                    if (colScroll != null) { int cy = (baseY + L.ColumnValue(line, px)) & hMask; ty = cy & 7; mapRow = (cy >> 3) << shiftW; }
                    int mx = (L.ScrollX[line] + px) & wMask, cell = mapRow + (mx >> 3);
                    int v = L.Tiles[L.Map[cell] * 64 + ty * 8 + (mx & 7)];
                    if (v == 0) continue;
                    int a = L.Attr[cell];
                    byte zz = (a & 8) != 0 ? zh : zl;
                    if (zz <= z[i]) continue;   // an earlier layer at the same place stays in front
                    z[i] = zz; key[i] = (byte)(KindBg | (a & 7) << 2 | v);
                    if (wantMath) src[i] = (byte)L.SnesLayer;
                }
            }
            if (Mode7.Enabled && Mode7.On[line]) ComposeMode7(line, x0, count, z, key, wantMath ? src : default);
            if (SpriteCount > 0 && ObjOn[line]) ComposeSprites(line, x0, count, z, key, wantMath ? src : default);
            if (wantMath)
            {
                int ml = MathLayers[line];
                for (int i = 0; i < count; i++)
                {
                    int w = src[i], sx = x0 + i;
                    if (w == 0xFF || (ml & (1 << w)) == 0) continue;   // backdrop (its own colour already has math) or not a math layer
                    if (AnyMathBlocked && (uint)sx < (uint)W && MathBlocked[row + sx]) continue;
                    math[i] = true;
                }
            }
            if (AnyBlack)
                for (int i = 0; i < count; i++) { int sx = x0 + i; if ((uint)sx < (uint)W && Black[row + sx]) key[i] = KeyBlack; }
            return true;
        }

        private readonly byte[] winner = new byte[512];

        /// <summary>A composed colour blended with line's fixed colour (add / subtract, half), channel by channel.</summary>
        public uint Blend(uint rgb, int line)
        {
            uint f = MathRgb[line]; int mode = MathMode[line];
            bool sub = (mode & 0x80) != 0, half = (mode & 0x40) != 0;
            int Ch(int sh)
            {
                int a = (int)(rgb >> sh) & 0xFF, b = (int)(f >> sh) & 0xFF;
                return sub ? Math.Max(0, a - b) >> (half ? 1 : 0) : half ? (a + b) >> 1 : Math.Min(255, a + b);
            }
            return (uint)(Ch(16) << 16 | Ch(8) << 8 | Ch(0));
        }

        private static int Log2(int v) { int s = 0; while ((1 << s) < v) s++; return s; }
        private static int Clip13(int v) => (v & 0x2000) != 0 ? v | ~0x3FF : v & 0x3FF;

        /// <summary>The SNES Mode 7 transform, as PPU_SFC computes it (no mosaic).</summary>
        private void ComposeMode7(int line, int x0, int count, Span<byte> z, Span<byte> key, Span<byte> src)
        {
            var M = Mode7; int W = Width;
            int a = M.A[line], b = M.B[line], c = M.C[line], d = M.D[line], cx = M.X[line], cy = M.Y[line];
            int sel = M.Sel[line], y = M.SnesLine[line];
            if ((sel & 0x02) != 0) y = 255 - y;
            int hc = Clip13(M.H[line] - cx), vc = Clip13(M.V[line] - cy);
            int psx = ((a * hc) & ~63) + ((b * vc) & ~63) + ((b * y) & ~63) + (cx << 8);
            int psy = ((c * hc) & ~63) + ((d * vc) & ~63) + ((d * y) & ~63) + (cy << 8);
            int outside = sel >> 6; byte zz = M.Z[line]; int row = line * Stride;
            for (int i = 0; i < count; i++)
            {
                int sx = x0 + i;
                if ((uint)sx >= (uint)W || zz <= z[i]) continue;
                if (M.AnyHidden && M.Hidden[row + sx]) continue;
                int xx = (sel & 0x01) != 0 ? 255 - sx : sx;
                int px = (psx + a * xx) >> 8, py = (psy + c * xx) >> 8;
                int tile;
                if (((px | py) & ~0x3FF) != 0 && outside >= 2)
                {
                    if (outside == 2) continue;
                    tile = 0;
                }
                else tile = M.Map[(((py >> 3) & 127) << 7) | ((px >> 3) & 127)];
                int v = M.Tex[tile << 6 | (py & 7) << 3 | (px & 7)];
                if (v == 0) continue;
                z[i] = zz; key[i] = (byte)(KindM7 | v);
                if (!src.IsEmpty) src[i] = 0;   // Mode 7 is BG1
            }
        }

        private void ComposeSprites(int line, int x0, int count, Span<byte> z, Span<byte> key, Span<byte> src)
        {
            int W = Width;
            Array.Clear(taken, 0, W);
            int onLine = 0, n = Math.Min(SpriteCount, SpriteLimit), row = line * Stride;
            for (int s = 0; s < n; s++)
            {
                ref var sp = ref Sprites[s];
                int r = line - sp.Y;
                if ((uint)r >= 8 || sp.X <= -8 || sp.X >= W || line < sp.FirstLine || line > sp.LastLine) continue;
                if (++onLine > SpritesPerLine) { SpriteLineDrops++; continue; }
                if (sp.VFlip) r = 7 - r;
                int t = (sp.Tile % MaxSpriteTiles) * 64 + r * 8;
                byte zz = ObjZ[line * 4 + (sp.Priority & 3)];
                for (int c = 0; c < 8; c++)
                {
                    int sx = sp.X + c;
                    if ((uint)sx >= (uint)W || taken[sx]) continue;
                    int v = SpriteTiles[t + (sp.HFlip ? 7 - c : c)];
                    if (v == 0) continue;
                    taken[sx] = true;   // the first sprite with a pixel here owns it, in front of or behind the layers
                    if (AnyObjHidden && ObjHidden[row + sx]) continue;
                    int i = sx - x0;
                    if ((uint)i >= (uint)count || zz <= z[i]) continue;
                    z[i] = zz; key[i] = (byte)(KindObj | (sp.Palette & 7) << 2 | v);
                    if (!src.IsEmpty) src[i] = (byte)(sp.MathPalette ? 4 : 0xFF);   // only SNES palettes 4-7 take part in math
                }
            }
        }

        /// <summary>The RGB of a composed key on a line (backdrop keys use that line's backdrop).</summary>
        public uint Rgb(byte key, int line) => key switch
        {
            KeyBackdrop => BackdropRgb[line],
            KeyBackdropAlt => BackdropAltRgb[line],
            KeyBlack => 0,
            _ => (key & 0x60) switch { KindBg => BgRgb[key & 0x1F], KindObj => ObjRgb[key & 0x1F], _ => M7Rgb[key & 0x1F] },
        };
    }

    /// <summary>
    /// THE ENHANCED PICTURE CONTRACT. An enhanced picture chip takes another console's frame as an <see cref="ExtPicture"/> and draws it at the guest's own size
    /// with as many layers as the guest uses, in the chip's own colours. Implemented by PPU_FIXS (FIX enhanced: the 2C02's colours) and PPU_DMGS (Game Boy enhanced:
    /// 4 shades by brightness, or CGB colours); the SNES, Master System and Genesis enhanced chips implement it next (docs/projects/enhanced-cores.md). Every
    /// cross-console translator (SNES, Game Boy, Master System, Game Gear, Genesis; and a NES game on the Game Boy enhanced chip) fills <see cref="Layers"/> and calls
    /// <see cref="DrawLayers"/>; the session then shows <see cref="Picture"/>.
    /// </summary>
    public interface IEnhancedPictureChip
    {
        /// <summary>The picture a translator fills (the chip owns it; it is reused from frame to frame, no allocation once warm).</summary>
        ExtPicture Layers { get; }
        /// <summary>Draws <see cref="Layers"/> (its <see cref="ExtPicture.Width"/> x <see cref="ExtPicture.Height"/>, all its layers and sprites) in the chip's own colours into <see cref="Picture"/>.</summary>
        void DrawLayers();
        /// <summary>The last drawn picture, 0xFFRRGGBB, <see cref="PictureWidth"/> x <see cref="PictureHeight"/> (row stride <see cref="PictureWidth"/>).</summary>
        uint[] Picture { get; }
        int PictureWidth { get; }
        int PictureHeight { get; }
        /// <summary>The chip's name as the menus say it ("FIX enhanced", "Game Boy enhanced").</summary>
        string EnhancedName { get; }
        /// <summary>The colours the chip draws with, in words ("the 2C02's colours", "4 Game Boy shades", "Game Boy Color colours").</summary>
        string ColourLimit { get; }
    }
}
