using System;

namespace NesEmulator.NullProviders;

/// <summary>
/// Kaleidoscope effect with rotating symmetric patterns.
/// </summary>
public class KaleidoscopeNullProvider : INullProvider
{
    public string DisplayName => "Mirrors";
    public string Description => "Rotating symmetric kaleidoscope patterns";
    
    public void GenerateFrame(byte[] frameBuffer, int frameCounter)
    {
        const int width = 256;
        const int height = 240;
        double time = frameCounter * 0.01;
        const int segments = 6;
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Convert to polar coordinates
                double dx = x - width / 2.0;
                double dy = y - height / 2.0;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double angle = Math.Atan2(dy, dx);
                
                // Apply kaleidoscope symmetry
                angle = angle + time;
                angle = Math.Abs((angle % (Math.PI * 2 / segments)) - Math.PI / segments);
                
                // Pattern based on polar coordinates
                double pattern = Math.Sin(dist * 0.1 + angle * 5);
                pattern += Math.Cos(dist * 0.15 - angle * 3 + time * 2);
                pattern = (pattern + 2) / 4;
                
                // Radial fade
                double fade = 1.0 / (1 + dist * 0.01);
                
                double hue = (angle * 180 / Math.PI + time * 30) % 360;
                double sat = 0.5;
                double val = pattern * fade * 0.5;
                
                HsvToRgb(hue, sat, val, out byte r, out byte g, out byte b);
                
                int offset = (y * width + x) * 4;
                frameBuffer[offset + 0] = r;
                frameBuffer[offset + 1] = g;
                frameBuffer[offset + 2] = b;
                frameBuffer[offset + 3] = 255;
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
