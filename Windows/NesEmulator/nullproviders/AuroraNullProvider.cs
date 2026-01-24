using System;
using BrokenNes.Windows.Rendering;

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
