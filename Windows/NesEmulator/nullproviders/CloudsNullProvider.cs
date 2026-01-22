using System;

namespace NesEmulator.NullProviders;

/// <summary>
/// Perlin-style noise creating flowing cloud-like patterns.
/// Uses layered sine waves to approximate smooth noise.
/// </summary>
public class CloudsNullProvider : INullProvider
{
    public string DisplayName => "Clouds";
    public string Description => "Flowing cloud-like patterns using smooth noise";
    
    public void GenerateFrame(byte[] frameBuffer, int frameCounter)
    {
        const int width = 256;
        const int height = 240;
        double time = frameCounter * 0.01;
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double nx = x / 32.0;
                double ny = y / 32.0;
                
                // Layered noise
                double noise = 0;
                noise += 0.5 * Math.Sin(nx * 2 + time) * Math.Cos(ny * 2 - time * 0.7);
                noise += 0.25 * Math.Sin(nx * 4 + time * 1.3) * Math.Cos(ny * 4 + time * 0.9);
                noise += 0.125 * Math.Sin(nx * 8 - time * 0.8) * Math.Cos(ny * 8 + time * 1.1);
                
                noise = (noise + 1.0) / 2.0; // Normalize
                
                // Sky blue to white gradient
                byte r = (byte)(180 + noise * 75);
                byte g = (byte)(200 + noise * 55);
                byte b = (byte)(220 + noise * 35);
                
                int offset = (y * width + x) * 4;
                frameBuffer[offset + 0] = r;
                frameBuffer[offset + 1] = g;
                frameBuffer[offset + 2] = b;
                frameBuffer[offset + 3] = 255;
            }
        }
    }
}
