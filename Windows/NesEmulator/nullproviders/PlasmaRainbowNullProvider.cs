using System;
using BrokenNes.Windows.Rendering;

namespace NesEmulator.NullProviders;

/// <summary>
/// Rainbow plasma effect using mathematical sine/cosine functions.
/// Creates flowing, colorful plasma patterns with smooth rainbow gradients.
/// </summary>
public class PlasmaRainbowNullProvider : INullProvider
{
    public string DisplayName => "Plasma";
    public string Description => "Flowing rainbow plasma effect using mathematical functions";
    
    public void GenerateFrame(byte[] frameBuffer, int frameCounter)
    {
        const int width = 256;
        const int height = 240;
        const int lowResWidth = 64;  // Render at lower resolution
        const int lowResHeight = 60;
        const int scaleX = width / lowResWidth;
        const int scaleY = height / lowResHeight;
        
        // Time factor for animation - much slower
        double time = frameCounter * 0.008;
        
        // Pre-compute low-res plasma values
        byte[,] lowResR = new byte[lowResHeight, lowResWidth];
        byte[,] lowResG = new byte[lowResHeight, lowResWidth];
        byte[,] lowResB = new byte[lowResHeight, lowResWidth];
        
        for (int ly = 0; ly < lowResHeight; ly++)
        {
            for (int lx = 0; lx < lowResWidth; lx++)
            {
                // Map low-res coords to normalized coordinates
                double nx = (lx * scaleX - width / 2.0) / (width / 2.0);
                double ny = (ly * scaleY - height / 2.0) / (height / 2.0);
                
                // Create plasma effect with multiple sine waves
                double plasma1 = Math.Sin(nx * 5.0 + time);
                double plasma2 = Math.Sin(ny * 3.0 + time * 1.3);
                double plasma3 = Math.Sin((nx + ny) * 4.0 + time * 0.7);
                double plasma4 = Math.Sin(Math.Sqrt(nx * nx + ny * ny) * 8.0 + time * 1.5);
                
                // Combine plasma functions
                double value = (plasma1 + plasma2 + plasma3 + plasma4) / 4.0;
                
                // Map to 0..1 range
                value = (value + 1.0) / 2.0;
                
                // Convert to rainbow colors using HSV-like mapping
                // value represents hue (0..1 wraps around the color wheel)
                double hue = value;
                
                // Pastel and darker: reduced saturation (0.5) and value (0.5)
                byte r, g, b;
                ColorMath.HsvToRgb(hue * 360.0, 0.5, 0.5, out r, out g, out b);
                
                lowResR[ly, lx] = r;
                lowResG[ly, lx] = g;
                lowResB[ly, lx] = b;
            }
        }
        
        // Scale up to full resolution with nearest neighbor
        for (int y = 0; y < height; y++)
        {
            int ly = y / scaleY;
            for (int x = 0; x < width; x++)
            {
                int lx = x / scaleX;
                
                int offset = (y * width + x) * 4;
                frameBuffer[offset + 0] = lowResR[ly, lx];
                frameBuffer[offset + 1] = lowResG[ly, lx];
                frameBuffer[offset + 2] = lowResB[ly, lx];
                frameBuffer[offset + 3] = 255; // A
            }
        }
    }
    
    /// <summary>
    /// Convert HSV color space to RGB
    /// </summary>
    /// <param name="h">Hue (0-360 degrees)</param>
    /// <param name="s">Saturation (0-1)</param>
    /// <param name="v">Value/Brightness (0-1)</param>
}
