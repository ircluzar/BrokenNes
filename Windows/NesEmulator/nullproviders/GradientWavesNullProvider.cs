using System;
using BrokenNes.Windows.Rendering;

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
                
                ColorMath.HsvToRgb(hue, sat, val, out byte r, out byte g, out byte b);
                
                int offset = (y * width + x) * 4;
                frameBuffer[offset + 0] = r;
                frameBuffer[offset + 1] = g;
                frameBuffer[offset + 2] = b;
                frameBuffer[offset + 3] = 255;
            }
        }
    }
    
}
