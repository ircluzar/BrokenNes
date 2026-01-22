using System;

namespace NesEmulator.NullProviders;

/// <summary>
/// Breathing radial gradient with pulsing colors.
/// </summary>
public class BreathingNullProvider : INullProvider
{
    public string DisplayName => "Breath";
    public string Description => "Pulsing radial gradient that breathes";
    
    public void GenerateFrame(byte[] frameBuffer, int frameCounter)
    {
        const int width = 256;
        const int height = 240;
        double time = frameCounter * 0.015;
        
        // Breathing effect
        double breathe = (Math.Sin(time) + 1) / 2;
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Distance from center
                double dx = x - width / 2.0;
                double dy = y - height / 2.0;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double maxDist = Math.Sqrt(width * width / 4 + height * height / 4);
                
                double normalized = dist / maxDist;
                
                // Pulsing radial gradient
                double wave = Math.Sin(normalized * Math.PI * 3 + breathe * Math.PI * 2);
                
                double hue = (normalized * 180 + time * 20) % 360;
                double sat = 0.4 + breathe * 0.2;
                double val = 0.3 + wave * 0.15;
                
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
