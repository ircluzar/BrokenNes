using System;
using BrokenNes.Windows.Rendering;

namespace NesEmulator.NullProviders;

/// <summary>
/// Rössler strange attractor chaotic system.
/// </summary>
public class StrangeAttractorNullProvider : INullProvider
{
    public string DisplayName => "Chaos";
    public string Description => "Rössler chaotic attractor system";
    
    private double x = 0.1, y = 0.0, z = 0.0;
    private const double a = 0.2;
    private const double b = 0.2;
    private const double c = 5.7;
    private const double dt = 0.02;
    
    public void GenerateFrame(byte[] frameBuffer, int frameCounter)
    {
        const int width = 256;
        const int height = 240;
        
        // Fade background
        for (int i = 0; i < frameBuffer.Length; i += 4)
        {
            frameBuffer[i + 0] = (byte)(frameBuffer[i + 0] * 0.97);
            frameBuffer[i + 1] = (byte)(frameBuffer[i + 1] * 0.97);
            frameBuffer[i + 2] = (byte)(frameBuffer[i + 2] * 0.97);
            frameBuffer[i + 3] = 255;
        }
        
        // Simulate multiple steps
        for (int iter = 0; iter < 40; iter++)
        {
            double dx = -y - z;
            double dy = x + a * y;
            double dz = b + z * (x - c);
            
            x += dx * dt;
            y += dy * dt;
            z += dz * dt;
            
            // Project to 2D
            int px = (int)(128 + x * 12);
            int py = (int)(120 - y * 12);
            
            if (px >= 0 && px < width && py >= 0 && py < height)
            {
                double hue = (Math.Atan2(y, x) / Math.PI * 180 + 180 + frameCounter * 0.3) % 360;
                ColorMath.HsvToRgb(hue, 0.5, 0.5, out byte r, out byte g, out byte b);
                
                // Draw with slight glow
                for (int offsetY = -1; offsetY <= 1; offsetY++)
                {
                    for (int offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        int drawX = px + offsetX;
                        int drawY = py + offsetY;
                        if (drawX >= 0 && drawX < width && drawY >= 0 && drawY < height)
                        {
                            int offset = (drawY * width + drawX) * 4;
                            byte factor = (byte)(offsetX == 0 && offsetY == 0 ? 255 : 128);
                            frameBuffer[offset + 0] = (byte)Math.Min(255, frameBuffer[offset + 0] + r * factor / 255);
                            frameBuffer[offset + 1] = (byte)Math.Min(255, frameBuffer[offset + 1] + g * factor / 255);
                            frameBuffer[offset + 2] = (byte)Math.Min(255, frameBuffer[offset + 2] + b * factor / 255);
                        }
                    }
                }
            }
        }
    }
    
}
