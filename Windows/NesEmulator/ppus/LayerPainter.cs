using System;

namespace NesEmulator
{
    /// <summary>
    /// The drawing half every enhanced picture chip of another console shares (the SNES's, the Master System's / Game Gear's, the Genesis's: <see cref="IEnhancedPictureChip"/>): each line of the guest's
    /// <see cref="ExtPicture"/> composed (<see cref="ExtPicture.ComposeLine(int, int, int, Span{byte}, Span{byte}, Span{bool})"/>: the layers, Mode 7 and the sprites on the guest's ladder) and every pixel turned into the
    /// chip's own colour by its <see cref="Rules"/>: the colours it has (<see cref="Rules.Colour"/>, applied to every palette entry when the palettes change and to each line's backdrop) and what it does where the guest's
    /// colour math asks (<see cref="Rules.MathPixel"/>: the SNES blends in its 15-bit colours, the Genesis shadows or highlights, the Master System shows the blended colour in its own). Allocation-free.
    /// </summary>
    public sealed class LayerPainter
    {
        /// <summary>A chip's colour rules.</summary>
        public abstract class Rules
        {
            /// <summary>An RGB colour (0xRRGGBB) as the chip shows it (0xFFRRGGBB).</summary>
            public abstract uint Colour(uint rgb);
            /// <summary>A pixel where the guest's colour math applies (<paramref name="rgb"/> the pixel's colour before the math): the chip's own rule. The default blends (<see cref="ExtPicture.Blend"/>) and shows the result in the chip's colours.</summary>
            public virtual uint MathPixel(ExtPicture e, uint rgb, int line) => Colour(e.Blend(rgb, line));
        }

        private readonly Rules rules;
        private readonly byte[] z = new byte[ExtPicture.MaxWidth], key = new byte[ExtPicture.MaxWidth];
        private readonly bool[] math = new bool[ExtPicture.MaxWidth];
        private readonly uint[] keyColour = new uint[128];
        private int keyVersion = -1;

        /// <summary>The last picture, 0xFFRRGGBB, <see cref="Width"/> x <see cref="Height"/> (row stride <see cref="Width"/>).</summary>
        public readonly uint[] Picture = new uint[ExtPicture.MaxWidth * ExtPicture.MaxLines];
        public int Width { get; private set; } = 256;
        public int Height { get; private set; } = 224;
        /// <summary>Pixels the last picture drew with the guest's colour math (tests).</summary>
        public int MathPixels { get; private set; }

        public LayerPainter(Rules rules) { this.rules = rules; }

        public void Draw(ExtPicture e)
        {
            int w = e.Width, h = e.Height;
            Width = w; Height = h;
            uint black = rules.Colour(0);
            if (!e.Active) { Array.Fill(Picture, black, 0, w * h); MathPixels = 0; return; }
            if (keyVersion != e.PaletteVersion)
            {
                for (int k = 0x20; k < 0x80; k++) keyColour[k] = rules.Colour(e.Rgb((byte)k, 0));
                keyVersion = e.PaletteVersion;
            }
            int mathPixels = 0;
            for (int line = 0; line < h; line++)
            {
                int row = line * w;
                if (!e.ComposeLine(line, 0, w, z, key, math)) { Array.Fill(Picture, black, row, w); continue; }
                uint back = rules.Colour(e.BackdropRgb[line]), backAlt = rules.Colour(e.BackdropAltRgb[line]);
                for (int x = 0; x < w; x++)
                {
                    byte k = key[x];
                    uint c;
                    if (math[x]) { c = rules.MathPixel(e, e.Rgb(k, line), line); mathPixels++; }
                    else c = k switch { ExtPicture.KeyBackdrop => back, ExtPicture.KeyBackdropAlt => backAlt, ExtPicture.KeyBlack => black, _ => keyColour[k & 0x7F] };
                    Picture[row + x] = c;
                }
            }
            MathPixels = mathPixels;
        }
    }
}
