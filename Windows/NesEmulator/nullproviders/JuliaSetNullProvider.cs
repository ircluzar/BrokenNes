using System;

namespace NesEmulator.NullProviders;

/// <summary>
/// Julia set fractal with slowly changing parameters for mesmerizing patterns.
/// </summary>
public class JuliaSetNullProvider : INullProvider
{
    public string DisplayName => "Julia";
    public string Description => "Hypnotic Julia set fractal with evolving parameters";
    
    public void GenerateFrame(byte[] frameBuffer, int frameCounter)
    {
        const int width = 256;
        const int height = 240;
        double time = frameCounter * 0.003;
        
        // Slowly changing Julia parameters
        double cReal = 0.285 + 0.2 * Math.Sin(time);
        double cImag = 0.01 + 0.2 * Math.Cos(time * 0.7);
        
        for (int py = 0; py < height; py++)
        {
            for (int px = 0; px < width; px++)
            {
                // Map to complex plane
                double zReal = (px - width / 2.0) / (width / 3.0);
                double zImag = (py - height / 2.0) / (height / 3.0);
                
                int iterations = 0;
                const int maxIterations = 50;
                
                while (iterations < maxIterations && zReal * zReal + zImag * zImag < 4)
                {
                    double temp = zReal * zReal - zImag * zImag + cReal;
                    zImag = 2 * zReal * zImag + cImag;
                    zReal = temp;
                    iterations++;
                }
                
                // Smooth coloring
                double t = iterations / (double)maxIterations;
                double hue = (t * 360 + time * 20) % 360;
                double sat = 0.5;
                double val = t < 1 ? 0.4 + t * 0.3 : 0.1;
                
                HsvToRgb(hue, sat, val, out byte r, out byte g, out byte b);
                
                int offset = (py * width + px) * 4;
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
