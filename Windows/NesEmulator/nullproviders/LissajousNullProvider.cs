using System;
using BrokenNes.Windows.Rendering;

namespace NesEmulator.NullProviders;

/// <summary>
/// Lissajous curves creating flowing harmonic patterns.
/// </summary>
public class LissajousNullProvider : INullProvider
{
    public string DisplayName => "Oscillations";
    public string Description => "Harmonic oscillation patterns with flowing curves";
    
    public void GenerateFrame(byte[] frameBuffer, int frameCounter)
    {
        const int width = 256;
        const int height = 240;
        double time = frameCounter * 0.01;
        
        // Clear to dark blue background
        for (int i = 0; i < frameBuffer.Length; i += 4)
        {
            frameBuffer[i + 0] = 20;
            frameBuffer[i + 1] = 25;
            frameBuffer[i + 2] = 40;
            frameBuffer[i + 3] = 255;
        }
        
        // Draw multiple Lissajous curves
        for (int curve = 0; curve < 3; curve++)
        {
            double a = 3 + curve;
                double bParam = 2 + curve * 0.5;
                double delta = time + curve * 2;
                
                for (double t = 0; t < Math.PI * 2; t += 0.02)
                {
                    double x = 128 + 90 * Math.Sin(a * t + delta);
                    double y = 120 + 90 * Math.Sin(bParam * t);
                    
                    int px = (int)x;
                    int py = (int)y;
                
                if (px >= 0 && px < width && py >= 0 && py < height)
                {
                    double hue = (t / (Math.PI * 2)) * 360;
                    ColorMath.HsvToRgb(hue, 0.5, 0.5, out byte r, out byte g, out byte b);
                    
                    // Draw with slight blur
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int drawX = px + dx;
                            int drawY = py + dy;
                            if (drawX >= 0 && drawX < width && drawY >= 0 && drawY < height)
                            {
                                int offset = (drawY * width + drawX) * 4;
                                frameBuffer[offset + 0] = (byte)Math.Min(255, frameBuffer[offset + 0] + r / 3);
                                frameBuffer[offset + 1] = (byte)Math.Min(255, frameBuffer[offset + 1] + g / 3);
                                frameBuffer[offset + 2] = (byte)Math.Min(255, frameBuffer[offset + 2] + b / 3);
                            }
                        }
                    }
                }
            }
        }
    }
    
}
