using System;

namespace NesEmulator.NullProviders;

/// <summary>
/// Mandelbrot set with slowly zooming view.
/// </summary>
public class MandelbrotNullProvider : INullProvider
{
    public string DisplayName => "Infinity";
    public string Description => "Fractal zoom into the Mandelbrot set";
    
    public void GenerateFrame(byte[] frameBuffer, int frameCounter)
    {
        const int width = 256;
        const int height = 240;
        double time = frameCounter * 0.01;
        
        // Slowly zoom into an interesting region
        double zoom = 1.0 + Math.Sin(time * 0.3) * 0.5;
        double centerX = -0.5 + Math.Sin(time * 0.2) * 0.3;
        double centerY = 0.0 + Math.Cos(time * 0.15) * 0.3;
        
        for (int py = 0; py < height; py++)
        {
            for (int px = 0; px < width; px++)
            {
                // Map to complex plane
                double x0 = centerX + (px - width / 2.0) / (width / 2.0 / zoom);
                double y0 = centerY + (py - height / 2.0) / (height / 2.0 / zoom);
                
                double x = 0;
                double y = 0;
                int iterations = 0;
                const int maxIterations = 50;
                
                while (x * x + y * y < 4 && iterations < maxIterations)
                {
                    double xTemp = x * x - y * y + x0;
                    y = 2 * x * y + y0;
                    x = xTemp;
                    iterations++;
                }
                
                // Smooth coloring
                double t = iterations / (double)maxIterations;
                
                if (iterations == maxIterations)
                {
                    // Inside set - dark
                    int offset = (py * width + px) * 4;
                    frameBuffer[offset + 0] = 10;
                    frameBuffer[offset + 1] = 10;
                    frameBuffer[offset + 2] = 20;
                    frameBuffer[offset + 3] = 255;
                }
                else
                {
                    double hue = (t * 360 + time * 10) % 360;
                    double sat = 0.5;
                    double val = 0.3 + t * 0.3;
                    
                    HsvToRgb(hue, sat, val, out byte r, out byte g, out byte b);
                    
                    int offset = (py * width + px) * 4;
                    frameBuffer[offset + 0] = r;
                    frameBuffer[offset + 1] = g;
                    frameBuffer[offset + 2] = b;
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
