using System;

namespace NesEmulator.NullProviders;

/// <summary>
/// Aurora borealis-style flowing waves with green and blue hues.
/// </summary>
public class AuroraNullProvider : INullProvider
{
    public string DisplayName => "Aurora";
    public string Description => "Northern lights with flowing waves";
    
    public void GenerateFrame(byte[] frameBuffer, int frameCounter)
    {
        const int width = 256;
        const int height = 240;
        double time = frameCounter * 0.008;
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double nx = x / 40.0;
                double ny = y / 60.0;
                
                // Multiple wave layers
                double wave1 = Math.Sin(nx * 2 + time) * Math.Sin(ny * 1.5 - time * 0.5);
                double wave2 = Math.Sin(nx * 3 - time * 0.7) * Math.Sin(ny * 2 + time * 0.3);
                double wave3 = Math.Sin((nx + ny) * 1.5 + time * 0.4);
                
                double intensity = (wave1 + wave2 * 0.5 + wave3 * 0.3) * 0.5 + 0.5;
                
                // Dark sky gradient
                double skyGradient = y / (double)height;
                
                // Aurora colors (green-blue-purple)
                double hue = 140 + intensity * 40 - skyGradient * 20;
                double sat = 0.6;
                double val = (0.2 + intensity * 0.3) * (1 - skyGradient * 0.5);
                
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
