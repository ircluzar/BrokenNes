using System;
using BrokenNes.Windows.Rendering;

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
