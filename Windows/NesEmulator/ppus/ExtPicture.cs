using System;

namespace NesEmulator
{
    /// <summary>
    /// The SNES-support picture extension shared by the variant chips PPU_FIXS (NES) and PPU_GBXS (Game Boy): extra
    /// background layers, a Mode 7 layer and an extended sprite list that the cross-console bridge fills from a SNES frame,
    /// composed one source line at a time. Chip-neutral: tiles are 2-bit values (a brightness level 1-3 inside a palette,
    /// 0 = see-through) and palettes are RGB, so each chip turns a composed pixel into its own colours (NES palette entries,
    /// Game Boy shades, CGB colours). Everything that can change down a SNES frame is per line - which layers are on,
    /// scroll, priority placement, windows, the Mode 7 matrix, sprites on/off and the backdrop - so status bars, split
    /// screens, window-cut boxes and Mode 7 floors survive. What stays limited: 2-bit tiles (3 levels per palette), 8
    /// palettes per kind and 1024 tiles per layer. Colour math: the fixed colour is blended in (SNES rules - which layers,
    /// add/subtract, half, the colour window), the sub screen is picked, not mixed (it shows through the backdrop).
    /// Source lines are the SNES picture's 224 (0-based); x is 0-255 in SNES pixels.
    /// </summary>
    public sealed class ExtPicture
    {
        public const int Width = 256, Lines = 224, MaxLayers = 8, MaxTiles = 1024, MaxSpriteTiles = 1024, Palettes = 8;

        /// <summary>Composed pixel keys: 0 backdrop, 1 backdrop where colour math is blocked, 2 black (clip to black); else
        /// kind | palette &lt;&lt; 2 | level, with kind 0x20 background layer, 0x40 sprite, 0x60 Mode 7.</summary>
        public const byte KeyBackdrop = 0, KeyBackdropAlt = 1, KeyBlack = 2, KindBg = 0x20, KindObj = 0x40, KindM7 = 0x60;

        public sealed class Layer
        {
            public bool Enabled;
            public readonly byte[] Tiles = new byte[MaxTiles * 64];
            public readonly ushort[] Map = new ushort[128 * 128];
            /// <summary>Bits 0-2 palette 0-7, bit 3 priority.</summary>
            public readonly byte[] Attr = new byte[128 * 128];
            /// <summary>Map size in 8x8 cells (powers of two up to 128); the scroll wraps at 8x that.</summary>
            public int MapWidth = 64, MapHeight = 64;
            public readonly bool[] On = new bool[Lines];
            /// <summary>Per line: the map pixel shown at x = 0 of this line is (ScrollX, ScrollY + line).</summary>
            public readonly short[] ScrollX = new short[Lines], ScrollY = new short[Lines];
            public readonly byte[] ZLow = new byte[Lines], ZHigh = new byte[Lines];
            /// <summary>Per pixel (line * 256 + x): masked by a window.</summary>
            public readonly bool[] Hidden = new bool[Lines * Width];
            public bool AnyHidden;
            /// <summary>The SNES layer (0-3 = BG1-4) this is, for colour math.</summary>
            public int SnesLayer;

            public void Reset()
            {
                Enabled = false; AnyHidden = false;
                Array.Clear(On); Array.Clear(Map); Array.Clear(Attr);
            }
        }

        public sealed class Mode7Layer
        {
            public bool Enabled;
            /// <summary>128x128 tile map and 256 tiles of 8x8 texels; a texel is palette &lt;&lt; 2 | level (0 = see-through).</summary>
            public readonly byte[] Map = new byte[128 * 128];
            public readonly byte[] Tex = new byte[256 * 64];
            public readonly bool[] On = new bool[Lines];
            /// <summary>Per line, as the SNES registers were: matrix A-D, centre X/Y, scroll H/V, M7SEL, and the SNES line number.</summary>
            public readonly int[] A = new int[Lines], B = new int[Lines], C = new int[Lines], D = new int[Lines],
                X = new int[Lines], Y = new int[Lines], H = new int[Lines], V = new int[Lines], SnesLine = new int[Lines];
            public readonly byte[] Sel = new byte[Lines], Z = new byte[Lines];
            public readonly bool[] Hidden = new bool[Lines * Width];
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
        public readonly bool[] ObjOn = new bool[Lines];
        /// <summary>Per line and sprite priority (line * 4 + p): its place on the ladder.</summary>
        public readonly byte[] ObjZ = new byte[Lines * 4];
        public readonly bool[] ObjHidden = new bool[Lines * Width];
        public bool AnyObjHidden;
        /// <summary>Pieces the per-line limit dropped in the last composed frame.</summary>
        public int SpriteLineDrops;

        /// <summary>0xRRGGBB per palette and level (index palette * 4 + level; level 0 unused).</summary>
        public readonly uint[] BgRgb = new uint[Palettes * 4], ObjRgb = new uint[Palettes * 4], M7Rgb = new uint[Palettes * 4];
        /// <summary>Bumped whenever the palettes change, so a chip knows to re-derive its own colours.</summary>
        public int PaletteVersion;

        public readonly uint[] BackdropRgb = new uint[Lines], BackdropAltRgb = new uint[Lines];
        public readonly bool[] BackdropAlt = new bool[Lines * Width];
        public bool AnyBackdropAlt;
        public readonly bool[] Black = new bool[Lines * Width];
        public bool AnyBlack;

        /// <summary>Per line, colour math with the fixed colour: which layers take part (bit 0-3 BG1-4, bit 4 sprites with
        /// palettes 4-7; 0 = none), bit 7 subtract, bit 6 half (<see cref="MathMode"/>), and the fixed colour as RGB.</summary>
        public readonly byte[] MathLayers = new byte[Lines], MathMode = new byte[Lines];
        public readonly uint[] MathRgb = new uint[Lines];
        /// <summary>Per pixel: colour math blocked here by the colour window.</summary>
        public readonly bool[] MathBlocked = new bool[Lines * Width];
        public bool AnyMathBlocked;

        /// <summary>False: the extension is off and the chip draws its own picture only.</summary>
        public bool Active;

        public ExtPicture() { for (int i = 0; i < MaxLayers; i++) Layers[i] = new Layer(); }

        /// <summary>Start a new frame: everything off (the bridge fills in what the SNES frame has).</summary>
        public void BeginFrame()
        {
            for (int i = 0; i < MaxLayers; i++) Layers[i].Reset();
            LayerCount = 0;
            Mode7.Reset();
            SpriteCount = 0; SpriteLineDrops = 0;
            Array.Clear(ObjOn); AnyObjHidden = false; AnyBackdropAlt = false; AnyBlack = false; AnyMathBlocked = false;
            Array.Clear(MathLayers);
            Array.Clear(BackdropRgb); Array.Clear(BackdropAltRgb);
            Active = true;
        }

        private readonly bool[] taken = new bool[Width];

        /// <summary>
        /// Compose source line <paramref name="line"/> for output pixels 0..count-1, output pixel i showing source x = x0 + i.
        /// Writes each pixel's ladder position (0 = nothing but the backdrop) and key (see <see cref="KindBg"/>). Returns false
        /// when the line is outside the SNES picture (the chip keeps its own picture there).
        /// </summary>
        public bool ComposeLine(int line, int x0, int count, Span<byte> z, Span<byte> key) => ComposeLine(line, x0, count, z, key, default);

        /// <summary>As above; <paramref name="math"/> (when not empty) gets true where the pixel blends with the fixed colour
        /// (<see cref="Blend"/>).</summary>
        public bool ComposeLine(int line, int x0, int count, Span<byte> z, Span<byte> key, Span<bool> math)
        {
            if ((uint)line >= Lines) return false;
            z.Slice(0, count).Clear();
            bool wantMath = !math.IsEmpty && MathLayers[line] != 0;
            if (!math.IsEmpty) math.Slice(0, count).Clear();
            var src = wantMath ? winner.AsSpan(0, count) : default;
            if (wantMath) src.Fill(0xFF);
            int row = line * Width;
            for (int i = 0; i < count; i++)
            {
                int sx = x0 + i;
                key[i] = (uint)sx < Width && AnyBackdropAlt && BackdropAlt[row + sx] ? KeyBackdropAlt : KeyBackdrop;
            }
            for (int k = 0; k < LayerCount; k++)
            {
                var L = Layers[k];
                if (!L.Enabled || !L.On[line]) continue;
                int wMask = L.MapWidth * 8 - 1, hMask = L.MapHeight * 8 - 1, shiftW = Log2(L.MapWidth);
                int my = (L.ScrollY[line] + line) & hMask, ty = my & 7, mapRow = (my >> 3) << shiftW;
                byte zl = L.ZLow[line], zh = L.ZHigh[line];
                for (int i = 0; i < count; i++)
                {
                    int sx = x0 + i;
                    if ((uint)sx >= Width) continue;
                    if (L.AnyHidden && L.Hidden[row + sx]) continue;
                    int mx = (L.ScrollX[line] + sx) & wMask, cell = mapRow + (mx >> 3);
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
                    if (AnyMathBlocked && (uint)sx < Width && MathBlocked[row + sx]) continue;
                    math[i] = true;
                }
            }
            if (AnyBlack)
                for (int i = 0; i < count; i++) { int sx = x0 + i; if ((uint)sx < Width && Black[row + sx]) key[i] = KeyBlack; }
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
            var M = Mode7;
            int a = M.A[line], b = M.B[line], c = M.C[line], d = M.D[line], cx = M.X[line], cy = M.Y[line];
            int sel = M.Sel[line], y = M.SnesLine[line];
            if ((sel & 0x02) != 0) y = 255 - y;
            int hc = Clip13(M.H[line] - cx), vc = Clip13(M.V[line] - cy);
            int psx = ((a * hc) & ~63) + ((b * vc) & ~63) + ((b * y) & ~63) + (cx << 8);
            int psy = ((c * hc) & ~63) + ((d * vc) & ~63) + ((d * y) & ~63) + (cy << 8);
            int outside = sel >> 6; byte zz = M.Z[line]; int row = line * Width;
            for (int i = 0; i < count; i++)
            {
                int sx = x0 + i;
                if ((uint)sx >= Width || zz <= z[i]) continue;
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
            Array.Clear(taken);
            int onLine = 0, n = Math.Min(SpriteCount, SpriteLimit), row = line * Width;
            for (int s = 0; s < n; s++)
            {
                ref var sp = ref Sprites[s];
                int r = line - sp.Y;
                if ((uint)r >= 8 || sp.X <= -8 || sp.X >= Width || line < sp.FirstLine || line > sp.LastLine) continue;
                if (++onLine > SpritesPerLine) { SpriteLineDrops++; continue; }
                if (sp.VFlip) r = 7 - r;
                int t = (sp.Tile % MaxSpriteTiles) * 64 + r * 8;
                byte zz = ObjZ[line * 4 + (sp.Priority & 3)];
                for (int c = 0; c < 8; c++)
                {
                    int sx = sp.X + c;
                    if ((uint)sx >= Width || taken[sx]) continue;
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
}
