using System;

namespace NesEmulator.NullProviders;

/// <summary>
/// Hexagonal honeycomb pattern with pulsing cells.
/// </summary>
public class HoneycombNullProvider : INullProvider
{
    public string DisplayName => "Lattice";
    public string Description => "Hexagonal cells with pulsing patterns";
    
    public void GenerateFrame(byte[] frameBuffer, int frameCounter)
    {
        const int width = 256;
        const int height = 240;
        double time = frameCounter * 0.02;
        const double hexSize = 20.0;
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Convert to hexagonal grid coordinates
                double hx = x / hexSize;
                double hy = y / hexSize * 1.1547; // sqrt(4/3) for hex spacing
                
                // Offset every other row
                if ((int)hy % 2 == 1) hx += 0.5;
                
                // Find nearest hex center
                double cx = Math.Round(hx);
                double cy = Math.Round(hy);
                
                // Distance to hex center
                double dx = (hx - cx) * hexSize;
                double dy = (hy - cy) * hexSize / 1.1547;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                
                // Pulsing pattern
                double pulse = Math.Sin(cx + cy + time) * 0.5 + 0.5;
                double hexEdge = 1.0 - Math.Min(1, dist / (hexSize * 0.45));
                
                // Color based on position and pulse
                double hue = ((cx * 30 + cy * 40) % 360);
                double sat = 0.4;
                double val = 0.25 + hexEdge * pulse * 0.25;
                
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
