using System;

namespace BrokenNes.Windows.Rendering
{
    internal static class ColorMath
    {
        public static (byte r, byte g, byte b) HslToRgb(float h, float s, float l)
        {
            h = h % 360.0f;
            if (h < 0)
            {
                h += 360.0f;
            }

            h /= 360.0f;

            float r, g, b;

            if (s == 0)
            {
                r = g = b = l;
            }
            else
            {
                float q = l < 0.5f ? l * (1.0f + s) : l + s - l * s;
                float p = 2.0f * l - q;

                r = HueToRgb(p, q, h + 1.0f / 3.0f);
                g = HueToRgb(p, q, h);
                b = HueToRgb(p, q, h - 1.0f / 3.0f);
            }

            return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }

        public static void HsvToRgb(double h, double s, double v, out byte r, out byte g, out byte b)
        {
            while (h < 0)
            {
                h += 360;
            }

            while (h >= 360)
            {
                h -= 360;
            }

            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;

            double rPrime, gPrime, bPrime;

            if (h < 60) { rPrime = c; gPrime = x; bPrime = 0; }
            else if (h < 120) { rPrime = x; gPrime = c; bPrime = 0; }
            else if (h < 180) { rPrime = 0; gPrime = c; bPrime = x; }
            else if (h < 240) { rPrime = 0; gPrime = x; bPrime = c; }
            else if (h < 300) { rPrime = x; gPrime = 0; bPrime = c; }
            else { rPrime = c; gPrime = 0; bPrime = x; }

            r = (byte)Math.Round((rPrime + m) * 255);
            g = (byte)Math.Round((gPrime + m) * 255);
            b = (byte)Math.Round((bPrime + m) * 255);
        }

        private static float HueToRgb(float p, float q, float t)
        {
            if (t < 0f) t += 1f;
            if (t > 1f) t -= 1f;
            if (t < 1f / 6f) return p + (q - p) * 6f * t;
            if (t < 1f / 2f) return q;
            if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
            return p;
        }
    }
}