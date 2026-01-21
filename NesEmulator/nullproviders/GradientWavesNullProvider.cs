using System;

namespace NesEmulator.NullProviders;

/// <summary>
/// Flowing sinusoidal gradient waves.
/// </summary>
public class GradientWavesNullProvider : INullProvider
{
    public string DisplayName => "Waves";
    public string Description => "Smooth flowing color gradients";
    
    public void GenerateFrame(byte[] frameBuffer, int frameCounter)
    {
        const int width = 256;
        const int height = 240;
        double time = frameCounter * 0.01;
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double nx = x / (double)width;
                double ny = y / (double)height;
                
                // Multiple gradient waves
                double wave1 = Math.Sin(nx * Math.PI * 4 + time);
                double wave2 = Math.Sin(ny * Math.PI * 3 - time * 0.7);
                double wave3 = Math.Sin((nx + ny) * Math.PI * 2 + time * 0.5);
                
                double hue = ((wave1 + wave2 + wave3) * 30 + time * 20) % 360;
                double sat = 0.4;
                double val = 0.4 + (wave1 + wave2) * 0.1;
                
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
