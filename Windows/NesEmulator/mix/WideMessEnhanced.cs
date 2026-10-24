using System;
using NesEmulator.Mix;
using NesEmulator.Sega;

// =================================================================================================================================================================================
// WIDEMESS ENHANCED (docs/projects/enhanced-cores.md section 6, docs/projects/widemess-spec.md section 8): the WideMess's picture and sound chips on the other consoles' menus.
//   picture  WmsWideChip   - THE WIDESCREEN CHIP: another console's frame (ExtPicture) drawn 400 wide in the WideMess's 15-bit colours. The guest's own picture is the middle, exactly as the SNES enhanced chip draws
//                            it; the columns either side come from what each layer really keeps beyond the guest's edges (the width its translator reports, ExtPicture.Layer.RealWidth): the chip extends a layer
//                            whose map is wider than the guest's picture, repeats one that is not, and fills one laid out on the screen (a window, a HUD column). Every choice is the chip's rule, said per layer
//                            and counted per frame (columns extended, repeated, filled), never a silent fallback.
//   sound    WmsVoiceChip  - the WideMess sound chip's 32 voices (the S-DSP's 8 + voices 8-31 of spec section 4) in as many banks as the guest's voices need (one holds every guest the consoles have):
//                            every voice on a voice of its own, the first 24 on voices 8-31 playing 16-bit PCM, then voices 0-7 playing BRR; the pan on each voice's own left and right volumes.
//   NES board  PPU_WMSS / APU_WMSS - a NES game on them (ids WMSS; offered only where the WideMess is: CoreCatalog).
// Routes: the other consoles reach the chips as "WMS:WMSS" (CoreCatalog.DirectRoutes; gated by Consoles.WideMessEnabled, so Lite and the FL plugin never offer them while their WideMess is hidden).
// The WideMess console's own menu has no enhanced entry: its own WMS chips are the WideMess, and a WideMess or SNES game draws and sounds on them exactly as it would on these (they never see its registers).
// =================================================================================================================================================================================

namespace NesEmulator.Mix
{
    internal static partial class EnhancedChipRoutes
    {
        static partial void RegisterWideMess() => EnhancedChips.Register("WMS:WMSS", () => new WmsWideChip(), rate => new WmsVoiceChip(rate));
    }

    /// <summary>What the widescreen chip shows of a guest layer beyond the guest's edges (<see cref="WmsWideChip"/>).</summary>
    public enum WideRule : byte
    {
        /// <summary>The layer's map continues past the edges as the guest's own scroll gives it, wrapping at the map's width: the map is wider than the guest's picture (the columns past its real width repeat, counted).</summary>
        Extend,
        /// <summary>The layer's own columns on the guest's screen repeat side by side: its map is no wider than the guest's picture.</summary>
        Repeat,
        /// <summary>Nothing of the layer in the sides (the layers behind it or the backdrop show): it is laid out on the screen (a window, a HUD column), or its translator did not say how wide it is.</summary>
        Fill,
    }

    /// <summary>
    /// THE WIDEMESS WIDESCREEN CHIP (route <c>WMS:WMSS</c>): the enhanced picture contract (<see cref="IEnhancedPictureChip"/>) drawn 400 wide, the WideMess's picture width (spec section 3), in its 15-bit colours.
    /// <list type="bullet">
    /// <item><b>The middle</b> is the guest's own picture at its own size (<see cref="ExtPicture.ComposeLine(int, int, int, Span{byte}, Span{byte}, Span{bool})"/>), in the colours and the colour math the SNES enhanced
    /// chip uses (PPU_SFCS: 15-bit, the SNES's add / subtract / half): pixel for pixel what that chip draws.</item>
    /// <item><b>The sides</b> (400 less the guest's width, halved: 72 columns for a 256-wide guest, 40 for a Genesis in H40, 120 for a Game Boy or a Game Gear) show each layer by its rule (<see cref="WideRule"/>), chosen
    /// from what the translator reported: <see cref="ExtPicture.Layer.FixedToScreen"/> fills; a <see cref="ExtPicture.Layer.RealWidth"/> wider than the guest's picture extends (a NES background with vertical mirroring,
    /// a Genesis plane, a SNES 64-tile map, a Game Boy's or Game Gear's 256-pixel map around its 160 columns); one no wider repeats (a Master System, a NES background with horizontal mirroring); one not reported fills,
    /// and is counted. Mode 7 continues its transformation (its plane is 1024 pixels). The guest's masks (windows, the colour window, clip to black, the backdrop's math) reach into a side from the guest's column next
    /// to it, as the WideMess's own windows reach its edges (spec 3.4). Sprites: only where the translator says the guest's positions beyond its edges are real (<see cref="ExtPicture.SpritesBeyondEdges"/>), drawn
    /// there in the guest's order and never counted against its per-line limit (a piece the guest drops on a line is dropped in the side too).</item>
    /// </list>
    /// What it did is counted every frame (<see cref="Extended"/>, <see cref="Repeated"/>, <see cref="Filled"/>, ...; <see cref="CountInto"/> adds them to a bridge's counters, which the status line and the HTTP API show).
    /// With <see cref="ExtPicture.ViewWidth"/> set to 0 it draws the guest at its own size, as every other enhanced chip does. Allocation-free.
    /// </summary>
    public sealed class WmsWideChip : IEnhancedPictureChip
    {
        /// <summary>The WideMess's picture width (spec section 3; PPU_WMS.WideWidth).</summary>
        public const int WideWidth = 400;

        private readonly ExtPicture layers = new() { ViewWidth = WideWidth };
        private readonly uint[] picture = new uint[ExtPicture.MaxViewWidth * ExtPicture.MaxLines];
        private readonly byte[] z = new byte[ExtPicture.MaxViewWidth], key = new byte[ExtPicture.MaxViewWidth], src = new byte[ExtPicture.MaxViewWidth];
        private readonly bool[] math = new bool[ExtPicture.MaxViewWidth], taken = new bool[ExtPicture.MaxViewWidth];
        private readonly uint[] keyColour = new uint[128];
        private int keyVersion = -1;
        private readonly WideRule[] rules = new WideRule[ExtPicture.MaxLayers];
        private readonly bool[] unreported = new bool[ExtPicture.MaxLayers];
        private bool spritesBeyond;

        /// <summary>TESTS: deliberate faults the checks must catch (null, the default: the chip's own rules): every enabled layer given this rule; the sprites' word on the sides overridden.</summary>
        internal static WideRule? ForceRuleForTests;
        internal static bool? ForceSpritesBeyondForTests;

        public ExtPicture Layers => layers;
        public uint[] Picture => picture;
        public int PictureWidth { get; private set; } = WideWidth;
        public int PictureHeight { get; private set; } = 224;
        public string EnhancedName => "WideMess enhanced";
        public string ColourLimit => "the WideMess's 15-bit colours (32,768), colour math in them; 400 wide";

        // ---- what the last frame did (tests, the status line)
        /// <summary>The columns added on each side of the guest's picture in the last frame, and the guest's own width.</summary>
        public int Side { get; private set; }
        public int GuestWidth { get; private set; }
        /// <summary>The rule each layer had in the last frame (index = <see cref="ExtPicture.Layers"/> index; layers past <see cref="ExtPicture.LayerCount"/> are not used).</summary>
        public WideRule RuleOf(int layer) => rules[layer];
        /// <summary>True when the layer was filled because its translator did not report its width (a fault the checks look for: it must never happen).</summary>
        public bool Unreported(int layer) => unreported[layer];
        /// <summary>Side columns x lines the last frame drew from a layer's map beyond the guest's edges (picture the guest really keeps there; Mode 7's plane counts too).</summary>
        public long Extended { get; private set; }
        /// <summary>Side columns x lines that repeat columns already in the picture (a layer no wider than the guest, or the part of a wider one past its real width).</summary>
        public long Repeated { get; private set; }
        /// <summary>Side columns x lines a layer left to the layers behind it or the backdrop (a layer laid out on the screen, or one not reported).</summary>
        public long Filled { get; private set; }
        /// <summary>Layer-lines filled because the translator did not report the layer's width.</summary>
        public long UnreportedLines { get; private set; }
        /// <summary>Lines whose sprites the sides do not show (the guest's sprite positions beyond its edges are not real places: <see cref="ExtPicture.SpritesBeyondEdges"/>).</summary>
        public long SpriteLinesNotExtended { get; private set; }
        /// <summary>Sprite pixels drawn in the sides (never counted against the guest's per-line limit).</summary>
        public long SideSpritePixels { get; private set; }
        /// <summary>Side columns x lines a guest mask reached (a window, the colour window, clip to black at the guest's edge).</summary>
        public long MaskedSideColumns { get; private set; }

        /// <summary>"extend: background; fill: window" for the last frame (tests and tools; makes a string).</summary>
        public string DescribeRules()
        {
            var sb = new System.Text.StringBuilder();
            for (int k = 0; k < layers.LayerCount; k++)
            {
                var L = layers.Layers[k]; if (!L.Enabled) continue;
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(L.Name.Length > 0 ? L.Name : "layer " + k).Append(' ').Append(rules[k].ToString().ToLowerInvariant());
                if (unreported[k]) sb.Append(" (width not reported)");
                else sb.Append(" (").Append(L.RealWidth).Append(L.FixedToScreen ? ", on the screen)" : ")");
            }
            if (layers.Mode7.Enabled) sb.Append(sb.Length > 0 ? "; " : "").Append("Mode 7 extend");
            sb.Append(layers.SpritesBeyondEdges ? "; sprites extend" : "; sprites fill");
            return sb.ToString();
        }

        public string Describe() =>
            $"{layers.Describe()} drawn {PictureWidth} wide ({Side} columns a side): {DescribeRules()}; side columns x lines extended {Extended}, repeated {Repeated}, filled {Filled}, masked {MaskedSideColumns}, not reported {UnreportedLines}";

        /// <summary>Adds the last frame's numbers to a bridge's counters (the status line, GET /api/console).</summary>
        public void CountInto(BridgeCounters c)
        {
            c.Add("WideMess widescreen: side columns x lines repeated (no more picture there)", Repeated);
            c.Add("WideMess widescreen: side columns x lines filled (a layer laid out on the screen)", Filled - UnreportedLines * 2L * Side);
            c.Add("WideMess widescreen: layer-lines filled because their width was not reported", UnreportedLines);
            c.Add("WideMess widescreen: lines without sprites in the sides (the guest's off-screen sprite places are not real)", SpriteLinesNotExtended);
            c.Level("WideMess widescreen: side columns x lines extended", Extended);
        }

        // ---------------------------------------------------------------------------------------------------- the colours: the SNES enhanced chip's (PPU_SFCS), so the middle is its picture
        private static uint Colour(uint rgb) => NesEmulator.Snes.PPU_SFCS.Argb555(NesEmulator.Snes.PPU_SFCS.To555(rgb));
        private static uint MathPixel(ExtPicture e, uint rgb, int line)
        {
            int mode = e.MathMode[line];
            return NesEmulator.Snes.PPU_SFCS.Argb555(NesEmulator.Snes.PPU_SFC.Blend(NesEmulator.Snes.PPU_SFCS.To555(rgb), NesEmulator.Snes.PPU_SFCS.To555(e.MathRgb[line]), (mode & 0x80) != 0, (mode & 0x40) != 0));
        }

        public void DrawLayers()
        {
            var e = layers;
            int W = e.Width, H = e.Height, S = e.SideColumns, PW = W + 2 * S;
            PictureWidth = PW; PictureHeight = H; Side = S; GuestWidth = W;
            long ext = 0, rep = 0, fil = 0, unrep = 0, noSpr = 0, sprPix = 0, masked = 0;
            uint black = Colour(0);
            if (!e.Active)
            {
                Array.Fill(picture, black, 0, PW * H);
                Extended = Repeated = Filled = UnreportedLines = SpriteLinesNotExtended = SideSpritePixels = MaskedSideColumns = 0;
                return;
            }
            if (keyVersion != e.PaletteVersion)
            {
                for (int k = 0x20; k < 0x80; k++) keyColour[k] = Colour(e.Rgb((byte)k, 0));
                keyVersion = e.PaletteVersion;
            }
            // each layer's rule, from what its translator reported
            for (int k = 0; k < ExtPicture.MaxLayers; k++)
            {
                var L = e.Layers[k];
                unreported[k] = k < e.LayerCount && L.Enabled && L.RealWidth <= 0;
                rules[k] = k >= e.LayerCount || !L.Enabled || L.RealWidth <= 0 || L.FixedToScreen ? WideRule.Fill : L.RealWidth > W ? WideRule.Extend : WideRule.Repeat;
                if (ForceRuleForTests is { } forced && k < e.LayerCount && L.Enabled) rules[k] = forced;
            }
            spritesBeyond = ForceSpritesBeyondForTests ?? e.SpritesBeyondEdges;
            for (int line = 0; line < H; line++)
            {
                int row = line * PW;
                uint back = Colour(e.BackdropRgb[line]), backAlt = Colour(e.BackdropAltRgb[line]);
                // the middle: the guest's own picture
                e.ComposeLine(line, 0, W, z, key, math);
                Paint(e, line, row + S, W, back, backAlt, black);
                if (S == 0) continue;
                // the sides
                ComposeSide(e, line, -S, S, ref sprPix, ref masked);
                Paint(e, line, row, S, back, backAlt, black);
                ComposeSide(e, line, W, S, ref sprPix, ref masked);
                Paint(e, line, row + S + W, S, back, backAlt, black);
                // what the sides showed of each layer on this line
                for (int k = 0; k < e.LayerCount; k++)
                {
                    var L = e.Layers[k];
                    if (!L.Enabled || !L.On[line]) continue;
                    switch (rules[k])
                    {
                        case WideRule.Extend: { long real = Math.Min(2L * S, Math.Max(0, L.RealWidth - W)); ext += real; rep += 2L * S - real; break; }
                        case WideRule.Repeat: rep += 2L * S; break;
                        default: fil += 2L * S; if (unreported[k]) unrep++; break;
                    }
                }
                if (e.Mode7.Enabled && e.Mode7.On[line]) ext += 2L * S;
                if (!spritesBeyond && e.ObjOn[line] && e.SpriteCount > 0) noSpr++;
            }
            Extended = ext; Repeated = rep; Filled = fil; UnreportedLines = unrep; SpriteLinesNotExtended = noSpr; SideSpritePixels = sprPix; MaskedSideColumns = masked;
        }

        /// <summary>The composed keys (<see cref="key"/>, <see cref="math"/>) of <paramref name="count"/> pixels into the picture at <paramref name="at"/>, as LayerPainter turns them into colours.</summary>
        private void Paint(ExtPicture e, int line, int at, int count, uint back, uint backAlt, uint black)
        {
            for (int x = 0; x < count; x++)
            {
                byte k = key[x];
                picture[at + x] = math[x] ? MathPixel(e, e.Rgb(k, line), line)
                    : k switch { ExtPicture.KeyBackdrop => back, ExtPicture.KeyBackdropAlt => backAlt, ExtPicture.KeyBlack => black, _ => keyColour[k & 0x7F] };
            }
        }

        private static int Log2(int v) { int s = 0; while ((1 << s) < v) s++; return s; }
        private static int Clip13(int v) => (v & 0x2000) != 0 ? v | ~0x3FF : v & 0x3FF;
        private static int Mod(int a, int m) { int r = a % m; return r < 0 ? r + m : r; }

        /// <summary>
        /// One side of a line: <paramref name="count"/> pixels from guest column <paramref name="x0"/> (negative on the left, the guest's width and on to the right) into <see cref="z"/>, <see cref="key"/>, <see cref="math"/>,
        /// on the guest's ladder as <see cref="ExtPicture.ComposeLine(int, int, int, Span{byte}, Span{byte}, Span{bool})"/> builds it, each layer by its rule; the masks of the guest's column next to the side.
        /// </summary>
        private void ComposeSide(ExtPicture e, int line, int x0, int count, ref long sprPix, ref long masked)
        {
            int W = e.Width, row = line * ExtPicture.Stride, edge = row + (x0 < 0 ? 0 : W - 1);
            Array.Clear(z, 0, count); Array.Clear(math, 0, count);
            bool wantMath = e.MathLayers[line] != 0;
            if (wantMath) Array.Fill(src, (byte)0xFF, 0, count);
            byte back = e.AnyBackdropAlt && e.BackdropAlt[edge] ? ExtPicture.KeyBackdropAlt : ExtPicture.KeyBackdrop;
            Array.Fill(key, back, 0, count);
            for (int k = 0; k < e.LayerCount; k++)
            {
                var L = e.Layers[k];
                var rule = rules[k];
                if (!L.Enabled || !L.On[line] || rule == WideRule.Fill) continue;
                if (L.AnyHidden && L.Hidden[edge]) { masked += count; continue; }       // a window over the guest's edge column covers the side too (spec 3.4)
                int wMask = L.MapWidth * 8 - 1, hMask = L.MapHeight * 8 - 1, shiftW = Log2(L.MapWidth);
                int baseY = L.ScrollY[line] + line;
                int my = baseY & hMask, ty = my & 7, mapRow = (my >> 3) << shiftW;
                var colScroll = L.ColumnScroll;
                byte zl = L.ZLow[line], zh = L.ZHigh[line];
                int scrollX = L.ScrollX[line];
                for (int i = 0; i < count; i++)
                {
                    int sx = x0 + i;
                    int px = rule == WideRule.Repeat ? Mod(sx, W) : sx;
                    if (colScroll != null) { int cy = (baseY + L.ColumnValue(line, px)) & hMask; ty = cy & 7; mapRow = (cy >> 3) << shiftW; }
                    int mx = (scrollX + px) & wMask, cell = mapRow + (mx >> 3);
                    int v = L.Tiles[L.Map[cell] * 64 + ty * 8 + (mx & 7)];
                    if (v == 0) continue;
                    int a = L.Attr[cell];
                    byte zz = (a & 8) != 0 ? zh : zl;
                    if (zz <= z[i]) continue;      // an earlier layer at the same place stays in front
                    z[i] = zz; key[i] = (byte)(ExtPicture.KindBg | (a & 7) << 2 | v);
                    if (wantMath) src[i] = (byte)L.SnesLayer;
                }
            }
            var M = e.Mode7;
            if (M.Enabled && M.On[line])
            {
                if (M.AnyHidden && M.Hidden[edge]) masked += count;
                else
                {
                    // Mode 7 continues its transformation across the side (spec 3.2: screen x below 0 and past 255)
                    int a = M.A[line], b = M.B[line], c = M.C[line], d = M.D[line], cx = M.X[line], cy = M.Y[line];
                    int sel = M.Sel[line], y = M.SnesLine[line];
                    if ((sel & 0x02) != 0) y = 255 - y;
                    int hc = Clip13(M.H[line] - cx), vc = Clip13(M.V[line] - cy);
                    int psx = ((a * hc) & ~63) + ((b * vc) & ~63) + ((b * y) & ~63) + (cx << 8);
                    int psy = ((c * hc) & ~63) + ((d * vc) & ~63) + ((d * y) & ~63) + (cy << 8);
                    int outside = sel >> 6; byte zz = M.Z[line];
                    for (int i = 0; i < count; i++)
                    {
                        if (zz <= z[i]) continue;
                        int sx = x0 + i;
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
                        z[i] = zz; key[i] = (byte)(ExtPicture.KindM7 | v);
                        if (wantMath) src[i] = 0;    // Mode 7 is BG1
                    }
                }
            }
            if (spritesBeyond && e.SpriteCount > 0 && e.ObjOn[line])
            {
                if (e.AnyObjHidden && e.ObjHidden[edge]) masked += count;
                else
                {
                    Array.Clear(taken, 0, count);
                    int onLine = 0, limit = Math.Min(e.SpriteCount, e.SpriteLimit);
                    for (int s = 0; s < e.SpriteCount; s++)
                    {
                        ref var sp = ref e.Sprites[s];
                        int r = line - sp.Y;
                        if ((uint)r >= 8 || line < sp.FirstLine || line > sp.LastLine) continue;
                        // the guest's per-line limit, as ComposeLine counts it: a piece it drops on this line is not drawn in the side either; pieces wholly beyond the guest's edges are never counted
                        bool inGuest = s < limit && sp.X > -8 && sp.X < W;
                        if (inGuest && ++onLine > e.SpritesPerLine) continue;
                        if (sp.X + 8 <= x0 || sp.X >= x0 + count) continue;
                        if (sp.VFlip) r = 7 - r;
                        int t = (sp.Tile % ExtPicture.MaxSpriteTiles) * 64 + r * 8;
                        byte zz = e.ObjZ[line * 4 + (sp.Priority & 3)];
                        for (int cc = 0; cc < 8; cc++)
                        {
                            int i = sp.X + cc - x0;
                            if ((uint)i >= (uint)count || taken[i]) continue;
                            int v = e.SpriteTiles[t + (sp.HFlip ? 7 - cc : cc)];
                            if (v == 0) continue;
                            taken[i] = true;   // the first sprite with a pixel here owns it, in front of or behind the layers
                            if (zz <= z[i]) continue;
                            z[i] = zz; key[i] = (byte)(ExtPicture.KindObj | (sp.Palette & 7) << 2 | v); sprPix++;
                            if (wantMath) src[i] = (byte)(sp.MathPalette ? 4 : 0xFF);
                        }
                    }
                }
            }
            if (wantMath)
            {
                int ml = e.MathLayers[line];
                bool blocked = e.AnyMathBlocked && e.MathBlocked[edge];
                if (!blocked)
                    for (int i = 0; i < count; i++) { int w = src[i]; if (w != 0xFF && (ml & (1 << w)) != 0) math[i] = true; }
            }
            if (e.AnyBlack && e.Black[edge]) { Array.Fill(key, ExtPicture.KeyBlack, 0, count); Array.Clear(math, 0, count); masked += count; }
        }
    }

    /// <summary>
    /// THE WIDEMESS ENHANCED SOUND CHIP's voice side (route <c>WMS:WMSS</c>): the WideMess sound chip (an S-DSP, <see cref="NesEmulator.Snes.DSP_SFC"/>, with its voices 8-31 and 1 MB of sound RAM, spec section 4, unlocked)
    /// in banks of 32 voices, playing the hub's voices directly: every voice a looped sample of the waveform it is (a pulse of its own duty, a PSG tone a 50% square, a triangle, white noise, the NES's 93-step short noise)
    /// at its pitch, at its level, panned by the voice's left and right volumes. A bank's first 24 voices are 8-31, in 16-bit PCM (a smooth triangle, 16-bit noise); the next 8 are the S-DSP's own 0-7 in BRR (the same
    /// shapes as the SNES enhanced chip's). One bank holds every guest of the other consoles (18 voices at most, a Japanese Master System's PSG + FM); more are made only past 32 voices (4 at most). No echo, no pitch
    /// modulation. Allocation-free once warm.
    /// </summary>
    public sealed class WmsVoiceChip : HubVoiceChip
    {
        public const int VoicesPerBank = 32, MaxBankCount = 4, DspRate = 32000, PcmVoices = 24;
        /// <summary>The voice volume a voice at level 1 plays at (of 127), as the SNES enhanced chip's.</summary>
        public const int VolumeMax = SfcVoiceChip.VolumeMax;
        private const int DirAddr = 0x0200, DataAddr = 0x0300;
        private const int WideDir = 0x10000, WideData = 0x11000;
        private const int SrcPulseLong = 0, SrcPulseShort = 4, SrcTriLong = 8, SrcTriShort = 9, SrcWhite = 10, SrcShortNoise = 11;
        private const byte WBANK = 0x8C, WKON = 0xCC, WDIRL = 0xDD, WDIRH = 0xED;

        private sealed class Bank
        {
            public readonly byte[] Aram = new byte[0x10000];
            public readonly byte[] High = new byte[NesEmulator.Snes.DSP_SFC.SoundRamBytes - 0x10000];
            public readonly NesEmulator.Snes.DSP_SFC Dsp;
            public short L, R;
            public int Kon;                                   // voices 0-7
            public readonly int[] WideKon = new int[3];       // voices 8-31, a bank of 8 each
            public readonly int[] Srcn = new int[32];
            public readonly byte[] VolL = new byte[32], VolR = new byte[32];
            public readonly bool[] OutOfRange = new bool[32];
            public int WBank = -1;
            public Bank()
            {
                Dsp = new NesEmulator.Snes.DSP_SFC(Aram) { Output = Take };
                Dsp.AttachWide(High);
                Dsp.SetWideOn(true);
                Array.Fill(Srcn, -1);
            }
            private void Take(short l, short r) { L = l; R = r; }
        }

        private readonly Bank?[] banks = new Bank?[MaxBankCount];
        private readonly Bank[] live = new Bank[MaxBankCount];
        private int liveCount;
        private long samplesDone = -1;
        private readonly ChipRateConverter? convL, convR;
        private readonly float[] blockL = new float[512], blockR = new float[512], pullL = new float[1024], pullR = new float[1024];
        private readonly short[] ring = new short[1 << 16];
        private int ringHead, ringCount;

        public override string EnhancedName => "WideMess enhanced";
        public override string BankShape => "32 WideMess voices (24 in 16-bit PCM, 8 in BRR)";

        public WmsVoiceChip(int sampleRate) : base(sampleRate, MaxBankCount, VoicesPerBank)
        {
            if (sampleRate != DspRate) { convL = new ChipRateConverter(DspRate, sampleRate); convR = new ChipRateConverter(DspRate, sampleRate); }
            MakeFirstBank();
        }

        /// <summary>The DSP voice a channel plays on: channels 0-23 are voices 8-31 (16-bit PCM), channels 24-31 the S-DSP's voices 0-7 (BRR).</summary>
        public static int VoiceOf(int channel) => (channel + 8) & 31;

        // ---- the sample sets every bank holds: BRR at $0200 / $0300 for voices 0-7, 16-bit PCM at $10000 / $11000 for voices 8-31; the same source numbers in both
        private static readonly byte[] BrrImage = BuildBrr(), PcmImage = BuildPcm();

        private static int[] Repeat(int[] cycle, int length) { var n = new int[length]; for (int i = 0; i < length; i++) n[i] = cycle[i % cycle.Length]; return n; }

        private static byte[] BuildBrr()
        {
            var dir = new System.Collections.Generic.List<byte>(); var data = new System.Collections.Generic.List<byte>();
            int addr = DataAddr;
            void Add(int[] nibbles)
            {
                var b = Brr.Encode(nibbles, 12);
                dir.Add((byte)addr); dir.Add((byte)(addr >> 8)); dir.Add((byte)addr); dir.Add((byte)(addr >> 8));
                data.AddRange(b); addr += b.Length;
            }
            foreach (int high in new[] { 4, 8, 16, 24 }) { var c = new int[32]; for (int i = 0; i < 32; i++) c[i] = i < high ? 7 : -7; Add(c); }
            foreach (int high in new[] { 1, 2, 4, 6 }) { var c = new int[8]; for (int i = 0; i < 8; i++) c[i] = i < high ? 7 : -7; Add(Repeat(c, 16)); }
            { var c = new int[32]; for (int i = 0; i < 32; i++) c[i] = i < 16 ? 7 - i : i - 24; Add(c); }
            Add(Repeat(new[] { -6, -2, 2, 6, 6, 2, -2, -6 }, 16));
            { var n = new int[1024]; uint s = 0x1234567u; for (int i = 0; i < n.Length; i++) { s ^= s << 13; s ^= s >> 17; s ^= s << 5; n[i] = (int)(s % 15) - 7; } Add(n); }
            Add(Repeat(ShortNoise(7), 93 * 16));
            var image = new byte[DataAddr - DirAddr + data.Count];
            dir.CopyTo(image, 0); data.CopyTo(image, DataAddr - DirAddr);
            return image;
        }

        /// <summary>The NES's short (93-step) noise: the 15-bit register with the tap on bit 6, one step a sample, at +/- <paramref name="level"/>.</summary>
        private static int[] ShortNoise(int level)
        {
            var steps = new int[93]; int reg = 1;
            for (int i = 0; i < 93; i++) { steps[i] = (reg & 1) != 0 ? -level : level; int fb = (reg ^ (reg >> 6)) & 1; reg = (reg >> 1) | (fb << 14); }
            return steps;
        }

        /// <summary>The 16-bit PCM set (spec section 4's directory: 16-byte entries, start / format / loop / end, 20-bit addresses), placed at <see cref="WideDir"/> with its samples from <see cref="WideData"/>; returned
        /// as the bytes from <see cref="WideDir"/> on. Every sample loops whole, a whole number of 4-sample groups long.</summary>
        private static byte[] BuildPcm()
        {
            const int Peak = 28672;      // the level the BRR set decodes to (a nibble of 7 at shift 12), so a voice sounds alike on either kind
            var dir = new byte[16 * 12]; var data = new System.Collections.Generic.List<byte>();
            int addr = WideData, n = 0;
            void Put(byte[] d, int at, int v) { d[at] = (byte)v; d[at + 1] = (byte)(v >> 8); d[at + 2] = (byte)((v >> 16) & 0x0F); }
            void Add(int[] samples)
            {
                int start = addr;
                foreach (int s in samples) { short v = (short)Math.Clamp(s, -32000, 32000); data.Add((byte)v); data.Add((byte)(v >> 8)); }
                addr += samples.Length * 2;
                Put(dir, n * 16, start); dir[n * 16 + 3] = 0x03;     // 16-bit PCM, looped
                Put(dir, n * 16 + 4, start); Put(dir, n * 16 + 8, addr);
                n++;
            }
            foreach (int high in new[] { 4, 8, 16, 24 }) { var c = new int[32]; for (int i = 0; i < 32; i++) c[i] = i < high ? Peak : -Peak; Add(c); }
            foreach (int high in new[] { 1, 2, 4, 6 }) { var c = new int[8]; for (int i = 0; i < 8; i++) c[i] = i < high ? Peak : -Peak; Add(c); }
            { var c = new int[32]; for (int i = 0; i < 32; i++) c[i] = (int)Math.Round(Peak * (1 - 4 * Math.Abs((i + 0.5) / 32.0 - 0.5))); Add(c); }     // a smooth triangle (16-bit, not the NES's 16 steps)
            { var c = new int[8]; for (int i = 0; i < 8; i++) c[i] = (int)Math.Round(Peak * (1 - 4 * Math.Abs((i + 0.5) / 8.0 - 0.5))); Add(c); }
            { var c = new int[4096]; uint s = 0x1234567u; for (int i = 0; i < c.Length; i++) { s ^= s << 13; s ^= s >> 17; s ^= s << 5; c[i] = (int)(s % (2u * Peak + 1)) - Peak; } Add(c); }    // 16-bit white noise
            Add(Repeat(ShortNoise(Peak), 93 * 4));
            var image = new byte[WideData - WideDir + data.Count];
            dir.CopyTo(image, 0); data.CopyTo(image, WideData - WideDir);
            return image;
        }

        protected override int ClassOf(in HubVoice v) => 0;      // every WideMess voice plays anything

        // ---- register access: voices 0-7 on the S-DSP's page, voices 8-31 on the second page through WBANK
        private static void Reg(Bank k, int voice, int reg, byte value)
        {
            if (voice < 8) { k.Dsp.Write((voice << 4) | reg, value); return; }
            int b = (voice - 8) >> 3;
            if (k.WBank != b) { k.Dsp.WriteHigh(WBANK, (byte)b); k.WBank = b; }
            k.Dsp.WriteHigh(0x80 | (((voice - 8) & 7) << 4) | reg, value);
        }

        protected override void MakeBank(int b)
        {
            var k = new Bank();
            Array.Copy(BrrImage, 0, k.Aram, DirAddr, BrrImage.Length);
            Array.Copy(PcmImage, 0, k.High, WideDir - 0x10000, PcmImage.Length);
            var d = k.Dsp;
            d.Write(0x6C, 0x20);                              // FLG: no reset, not muted, echo writes off
            d.Write(0x0C, 0x60); d.Write(0x1C, 0x60);         // main volume
            d.Write(0x2C, 0); d.Write(0x3C, 0); d.Write(0x0D, 0); d.Write(0x2D, 0); d.Write(0x3D, 0); d.Write(0x4D, 0); d.Write(0x7D, 0); d.Write(0x6D, 0xFF);
            d.Write(0x5D, DirAddr >> 8); d.Write(0x5C, 0);
            d.WriteHigh(WDIRL, (byte)((WideDir >> 8) & 0xFF)); d.WriteHigh(WDIRH, (byte)(WideDir >> 16));     // the directory at WDIRH x $10000 + WDIRL x $100
            for (int wb = 0; wb < 3; wb++) { d.WriteHigh(WBANK, (byte)wb); d.WriteHigh(0xAD, 0); d.WriteHigh(0xBD, 0); d.WriteHigh(0xCD, 0); d.WriteHigh(0xDC, 0); }   // WPMON, WNON, WEON, WKOFF
            k.WBank = 2;
            for (int v = 0; v < 32; v++)
            {
                Reg(k, v, 0, 0); Reg(k, v, 1, 0); Reg(k, v, 2, 0); Reg(k, v, 3, 0);
                Reg(k, v, 4, 0); Reg(k, v, 5, 0x00); Reg(k, v, 7, 0x7F);     // GAIN mode, direct, full
            }
            banks[b] = k; live[liveCount++] = k;
        }

        private static int Source(in HubVoice v, out double pitch)
        {
            if (v.Kind == HubVoiceKind.Noise) { pitch = v.Hz * 4096.0 / DspRate; return v.ShortNoise ? SrcShortNoise : SrcWhite; }
            bool square = v.Kind == HubVoiceKind.Pulse || v.Psg;
            double longPitch = v.Hz * 32 * 4096.0 / DspRate;
            if (longPitch <= 0x3FFF) { pitch = longPitch; return square ? SrcPulseLong + (v.Psg ? 2 : v.Duty & 3) : SrcTriLong; }
            pitch = v.Hz * 8 * 4096.0 / DspRate;
            return square ? SrcPulseShort + (v.Psg ? 2 : v.Duty & 3) : SrcTriShort;
        }

        protected override void Start(int b, int kind, int ch, in HubVoice v) { var k = banks[b]!; k.Srcn[ch] = -1; k.OutOfRange[ch] = false; }

        protected override void Play(int b, int kind, int ch, in HubVoice v)
        {
            var k = banks[b]!; int voice = VoiceOf(ch);
            int src = Source(v, out double pitch);
            int p = (int)Math.Round(pitch);
            bool tonal = v.Kind != HubVoiceKind.Noise;
            if (tonal && (p > 0x3FFF || PitchGuard.Blocks(v.Hz)))
            {
                if (!k.OutOfRange[ch] && p > 0x3FFF) NotesOutOfRange++;
                k.OutOfRange[ch] = true;
                Vol(k, ch, 0, 0); return;
            }
            k.OutOfRange[ch] = false;
            p = Math.Clamp(p, 1, 0x3FFF);
            if (k.Srcn[ch] != src)
            {
                k.Srcn[ch] = src; Reg(k, voice, 4, (byte)src);
                if (voice < 8) k.Kon |= 1 << voice; else k.WideKon[(voice - 8) >> 3] |= 1 << ((voice - 8) & 7);
            }
            Reg(k, voice, 2, (byte)p); Reg(k, voice, 3, (byte)(p >> 8));
            float level = Math.Clamp(v.Level, 0f, 1f);
            float pan = Math.Clamp(v.Pan, -1f, 1f);
            int left = (int)Math.Round(VolumeMax * level * (pan > 0 ? 1 - pan : 1)), right = (int)Math.Round(VolumeMax * level * (pan < 0 ? 1 + pan : 1));
            Vol(k, ch, left, right);
        }

        private static void Vol(Bank k, int ch, int left, int right)
        {
            int voice = VoiceOf(ch);
            if (k.VolL[ch] != (byte)left) { k.VolL[ch] = (byte)left; Reg(k, voice, 0, (byte)left); }
            if (k.VolR[ch] != (byte)right) { k.VolR[ch] = (byte)right; Reg(k, voice, 1, (byte)right); }
        }

        protected override void Stop(int b, int kind, int ch) { var k = banks[b]; if (k != null) Vol(k, ch, 0, 0); }

        protected override void EndApply()
        {
            for (int i = 0; i < liveCount; i++)
            {
                var k = live[i];
                if (k.Kon != 0) { k.Dsp.Write(0x4C, (byte)k.Kon); k.Kon = 0; }
                for (int wb = 0; wb < 3; wb++)
                    if (k.WideKon[wb] != 0)
                    {
                        if (k.WBank != wb) { k.Dsp.WriteHigh(WBANK, (byte)wb); k.WBank = wb; }
                        k.Dsp.WriteHigh(WKON, (byte)k.WideKon[wb]); k.WideKon[wb] = 0;
                    }
            }
        }

        protected override void SilenceAll()
        {
            for (int i = 0; i < liveCount; i++)
            {
                var k = live[i];
                for (int ch = 0; ch < 32; ch++) { Vol(k, ch, 0, 0); k.Srcn[ch] = -1; }
                k.Kon = 0; Array.Clear(k.WideKon);
            }
        }

        /// <summary>A raw register write of a bank: addresses $00-$7F the S-DSP's page, $80-$FF the WideMess's second page (spec 4).</summary>
        protected override void WriteRaw(int b, ushort address, byte value)
        {
            var k = banks[b]!;
            if ((address & 0x80) == 0) { k.Dsp.Write(address & 0x7F, value); return; }
            if ((address & 0x7F) == 0x0C) k.WBank = value & 3;
            k.Dsp.WriteHigh(address & 0xFF, value);
        }

        /// <summary>The volume a channel was last given, left and right (tests: what the chip was told).</summary>
        public (int Left, int Right) VolumeOf(int bank, int channel) { var k = banks[bank]; return k == null ? (0, 0) : ((sbyte)k.VolL[channel], (sbyte)k.VolR[channel]); }
        /// <summary>The source (sample number) a channel plays (tests): -1 when none.</summary>
        public int SourceOf(int bank, int channel) => banks[bank]?.Srcn[channel] ?? -1;

        public override void Rebase(double seconds) { if (samplesDone >= 0) samplesDone = (long)Math.Floor(seconds * DspRate); }

        public override void RunTo(double seconds)
        {
            long target = (long)Math.Floor(seconds * DspRate);
            if (samplesDone < 0) { samplesDone = target; return; }      // the chip starts where it is first run: no catching up from the guest's power-on
            int fill = 0;
            while (samplesDone < target)
            {
                int sumL = 0, sumR = 0;
                for (int i = 0; i < liveCount; i++) { var k = live[i]; k.Dsp.RunSample(); sumL += k.L; sumR += k.R; }
                blockL[fill] = sumL * (1f / 32768f); blockR[fill] = sumR * (1f / 32768f);
                samplesDone++;
                if (++fill == blockL.Length) { Push(fill); fill = 0; }
            }
            if (fill > 0) Push(fill);
        }

        private void Push(int n)
        {
            if (convL == null) { for (int i = 0; i < n; i++) Put(blockL[i], blockR[i]); return; }
            convL.Push(blockL.AsSpan(0, n)); convR!.Push(blockR.AsSpan(0, n));
            while (true)
            {
                int a = convL.Pull(pullL), b = convR.Pull(pullR.AsSpan(0, a));
                if (a <= 0) break;
                for (int i = 0; i < Math.Min(a, b); i++) Put(pullL[i], pullR[i]);
            }
        }

        private void Put(float l, float r)
        {
            if (ringCount >= ring.Length - 2) { ringHead = (ringHead + 2) & (ring.Length - 1); ringCount -= 2; }
            int w = (ringHead + ringCount) & (ring.Length - 1);
            ring[w] = (short)Math.Clamp(l * 32767f, -32768f, 32767f); ring[(w + 1) & (ring.Length - 1)] = (short)Math.Clamp(r * 32767f, -32768f, 32767f);
            ringCount += 2;
        }

        public override int ReadSamples(Span<short> dest)
        {
            int n = Math.Min(dest.Length & ~1, ringCount);
            for (int i = 0; i < n; i++) dest[i] = ring[(ringHead + i) & (ring.Length - 1)];
            ringHead = (ringHead + n) & (ring.Length - 1); ringCount -= n;
            return n;
        }
    }
}

namespace NesEmulator
{
    /// <summary>A NES game on the WIDEMESS WIDESCREEN CHIP (NES board PPU id <c>WMSS</c>, "WideMess (enhanced)", offered only where the WideMess console is): the NES picture as layers (<see cref="NesLayeredPpu"/>) drawn
    /// 400 wide by <see cref="NesEmulator.Mix.WmsWideChip"/> in the WideMess's 15-bit colours: with vertical mirroring the neighbouring nametable beside the screen, with horizontal or one-screen mirroring the screen's own
    /// columns repeated. The NES's own 256 x 240 frame (<see cref="IPPU.GetFrameBuffer"/>: save states' pictures, recordings, the old tools) is the middle; an app that can show a wider picture asks
    /// <see cref="IWidePictureSource"/>.</summary>
    public sealed class PPU_WMSS : NesLayeredPpu, IWidePictureSource
    {
        public PPU_WMSS(Bus bus) : base(bus, new NesEmulator.Mix.WmsWideChip()) { }
        /// <summary>The widescreen chip itself (tests).</summary>
        public NesEmulator.Mix.WmsWideChip Wide => (NesEmulator.Mix.WmsWideChip)Chip;
        public override string CoreName => $"WMSS:WideMess widescreen+{front.CoreName}";
        public override string Description => "The WideMess picture chip, enhanced: a NES game 400 wide in 15-bit colours (the neighbouring nametable beside the screen with vertical mirroring, the screen repeated otherwise), the NES's own 256 columns in the middle, with a NES PPU keeping the register behaviour";
        public override string BridgeName => $"NES game on the WideMess widescreen chip (front {front.CoreName})";
        protected override void AfterPresent() => Wide.CountInto(Counters);

        public bool HasWidePicture => Chip.PictureWidth > 256;
        public uint[] WidePicture => Chip.Picture;
        public int WideWidth => Chip.PictureWidth;
        public int WideHeight => Chip.PictureHeight;
    }

    /// <summary>A NES game's sound on the WIDEMESS ENHANCED SOUND CHIP (NES board APU id <c>WMSS</c>, offered only where the WideMess console is): pulse 1 and 2, the triangle and the noise each on a WideMess voice of its own
    /// in 16-bit PCM (<see cref="NesApuOnChip"/>; the DMC is dropped and counted). Kept out of the FL plugin's chip list (its family is the WideMess's, which the plugin does not list; chip indexes are append-only).</summary>
    public sealed class APU_WMSS : NesApuOnChip
    {
        public APU_WMSS(Bus bus) : base(bus, "WMS:WMSS") { }
        public override string CoreName => $"WMSS:WideMess voices+{Front.CoreName}";
        public override string Description => "NES channels played on the WideMess enhanced sound chip: every channel on a WideMess voice of its own in 16-bit PCM, pulses with their duty (the DMC is dropped and counted)";
    }
}
