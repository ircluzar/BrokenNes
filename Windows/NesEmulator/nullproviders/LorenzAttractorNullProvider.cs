using System;
using BrokenNes.Windows.Rendering;

namespace NesEmulator.NullProviders;

/// <summary>
/// 3D Lorenz Attractor chaotic system projected to 2D.
/// </summary>
public class LorenzAttractorNullProvider : INullProvider
{
    public string DisplayName => "Butterfly";
    public string Description => "Chaotic 3D butterfly effect system";
    
    private double x = 0.1, y = 0.0, z = 0.0;
    private const double sigma = 10.0;
    private const double rho = 28.0;
    private const double beta = 8.0 / 3.0;
    private const double dt = 0.005;
    
    public void GenerateFrame(byte[] frameBuffer, int frameCounter)
    {
        const int width = 256;
        const int height = 240;
        
        // Fade previous frame
        for (int i = 0; i < frameBuffer.Length; i += 4)
        {
            frameBuffer[i + 0] = (byte)(frameBuffer[i + 0] * 0.95);
            frameBuffer[i + 1] = (byte)(frameBuffer[i + 1] * 0.95);
            frameBuffer[i + 2] = (byte)(frameBuffer[i + 2] * 0.95);
            frameBuffer[i + 3] = 255;
        }
        
        // Simulate and draw multiple iterations per frame
        for (int iter = 0; iter < 50; iter++)
        {
            double dx = sigma * (y - x);
            double dy = x * (rho - z) - y;
            double dz = x * y - beta * z;
            
            x += dx * dt;
            y += dy * dt;
            z += dz * dt;
            
            // Project 3D to 2D
            int px = (int)(128 + x * 4);
            int py = (int)(120 - y * 4);
            
            if (px >= 0 && px < width && py >= 0 && py < height)
            {
                // Color based on z coordinate
                double hue = (z / 50.0) * 360;
                ColorMath.HsvToRgb(hue, 0.6, 0.6, out byte r, out byte g, out byte b);
                
                int offset = (py * width + px) * 4;
                frameBuffer[offset + 0] = (byte)Math.Min(255, frameBuffer[offset + 0] + r);
                frameBuffer[offset + 1] = (byte)Math.Min(255, frameBuffer[offset + 1] + g);
                frameBuffer[offset + 2] = (byte)Math.Min(255, frameBuffer[offset + 2] + b);
            }
        }
    }
    
}
