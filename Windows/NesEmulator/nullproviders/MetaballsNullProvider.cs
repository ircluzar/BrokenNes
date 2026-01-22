using System;

namespace NesEmulator.NullProviders;

/// <summary>
/// Metaballs (blobby organic spheres) flowing together.
/// </summary>
public class MetaballsNullProvider : INullProvider
{
    public string DisplayName => "Fluid";
    public string Description => "Organic flowing blob patterns";
    
    public void GenerateFrame(byte[] frameBuffer, int frameCounter)
    {
        const int width = 256;
        const int height = 240;
        double time = frameCounter * 0.02;
        
        // Ball positions
        (double x, double y, double radius)[] balls = new[]
        {
            (128 + 50 * Math.Sin(time * 0.7), 120 + 40 * Math.Cos(time * 0.5), 30.0),
            (128 + 45 * Math.Sin(time * 0.9 + 2), 120 + 35 * Math.Cos(time * 0.7 + 2), 25.0),
            (128 + 40 * Math.Sin(time * 0.6 + 4), 120 + 45 * Math.Cos(time * 0.8 + 4), 28.0),
            (128 + 35 * Math.Sin(time * 0.8 + 1), 120 + 38 * Math.Cos(time * 0.6 + 1), 22.0)
        };
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double sum = 0;
                
                foreach (var ball in balls)
                {
                    double dx = x - ball.x;
                    double dy = y - ball.y;
                    double distSq = dx * dx + dy * dy;
                    sum += (ball.radius * ball.radius) / (distSq + 1);
                }
                
                // Threshold for metaball effect
                double intensity = Math.Min(1, sum);
                
                if (intensity > 0.3)
                {
                    double hue = (intensity * 180 + time * 10) % 360;
                    double sat = 0.5;
                    double val = 0.3 + intensity * 0.2;
                    
                    HsvToRgb(hue, sat, val, out byte r, out byte g, out byte b);
                    
                    int offset = (y * width + x) * 4;
                    frameBuffer[offset + 0] = r;
                    frameBuffer[offset + 1] = g;
                    frameBuffer[offset + 2] = b;
                    frameBuffer[offset + 3] = 255;
                }
                else
                {
                    int offset = (y * width + x) * 4;
                    frameBuffer[offset + 0] = 20;
                    frameBuffer[offset + 1] = 20;
                    frameBuffer[offset + 2] = 30;
                    frameBuffer[offset + 3] = 255;
                }
            }
        }
    }
    
    private void HsvToRgb(double h, double s, double v, out byte r, out byte g, out byte b)
    {
        while (h < 0) h += 360;
        while (h >= 360) h -= 360;
        
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
}
