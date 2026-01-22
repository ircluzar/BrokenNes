using System;

namespace NesEmulator.NullProviders;

/// <summary>
/// Algorithmic Perlin noise generating flowing organic patterns.
/// </summary>
public class PerlinNoiseFieldNullProvider : INullProvider
{
    public string DisplayName => "Flow";
    public string Description => "Procedural noise with flowing patterns";
    
    private int[] permutation = new int[512];
    
    public PerlinNoiseFieldNullProvider()
    {
        // Initialize permutation table
        var random = new Random(42);
        for (int i = 0; i < 256; i++) permutation[i] = i;
        for (int i = 255; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (permutation[i], permutation[j]) = (permutation[j], permutation[i]);
        }
        for (int i = 0; i < 256; i++) permutation[256 + i] = permutation[i];
    }
    
    public void GenerateFrame(byte[] frameBuffer, int frameCounter)
    {
        const int width = 256;
        const int height = 240;
        double time = frameCounter * 0.01;
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double nx = x / 40.0;
                double ny = y / 40.0;
                
                // Multi-octave noise
                double value = 0;
                value += Noise(nx, ny, time) * 0.5;
                value += Noise(nx * 2, ny * 2, time * 1.5) * 0.25;
                value += Noise(nx * 4, ny * 4, time * 2) * 0.125;
                
                value = (value + 1) * 0.5; // Normalize to 0-1
                
                double hue = (value * 180 + time * 20) % 360;
                HsvToRgb(hue, 0.4, 0.4 + value * 0.2, out byte r, out byte g, out byte b);
                
                int offset = (y * width + x) * 4;
                frameBuffer[offset + 0] = r;
                frameBuffer[offset + 1] = g;
                frameBuffer[offset + 2] = b;
                frameBuffer[offset + 3] = 255;
            }
        }
    }
    
    private double Noise(double x, double y, double z)
    {
        int xi = (int)Math.Floor(x) & 255;
        int yi = (int)Math.Floor(y) & 255;
        int zi = (int)Math.Floor(z) & 255;
        
        double xf = x - Math.Floor(x);
        double yf = y - Math.Floor(y);
        double zf = z - Math.Floor(z);
        
        double u = Fade(xf);
        double v = Fade(yf);
        double w = Fade(zf);
        
        int a = permutation[xi] + yi;
        int aa = permutation[a] + zi;
        int ab = permutation[a + 1] + zi;
        int b = permutation[xi + 1] + yi;
        int ba = permutation[b] + zi;
        int bb = permutation[b + 1] + zi;
        
        return Lerp(w,
            Lerp(v,
                Lerp(u, Grad(permutation[aa], xf, yf, zf), Grad(permutation[ba], xf - 1, yf, zf)),
                Lerp(u, Grad(permutation[ab], xf, yf - 1, zf), Grad(permutation[bb], xf - 1, yf - 1, zf))),
            Lerp(v,
                Lerp(u, Grad(permutation[aa + 1], xf, yf, zf - 1), Grad(permutation[ba + 1], xf - 1, yf, zf - 1)),
                Lerp(u, Grad(permutation[ab + 1], xf, yf - 1, zf - 1), Grad(permutation[bb + 1], xf - 1, yf - 1, zf - 1))));
    }
    
    private double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
    private double Lerp(double t, double a, double b) => a + t * (b - a);
    
    private double Grad(int hash, double x, double y, double z)
    {
        int h = hash & 15;
        double u = h < 8 ? x : y;
        double v = h < 4 ? y : h == 12 || h == 14 ? x : z;
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
    }
    
    private void HsvToRgb(double h, double s, double v, out byte r, out byte g, out byte b)
    {
        h = h % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c;
        double rPrime = 0, gPrime = 0, bPrime = 0;
        
        if (h < 60) { rPrime = c; gPrime = x; }
        else if (h < 120) { rPrime = x; gPrime = c; }
        else if (h < 180) { gPrime = c; bPrime = x; }
        else if (h < 240) { gPrime = x; bPrime = c; }
        else if (h < 300) { rPrime = x; bPrime = c; }
        else { rPrime = c; bPrime = x; }
        
        r = (byte)((rPrime + m) * 255);
        g = (byte)((gPrime + m) * 255);
        b = (byte)((bPrime + m) * 255);
    }
}
