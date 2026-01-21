using System;

namespace NesEmulator.NullProviders;

/// <summary>
/// Simple black screen - no visual effects, just solid black.
/// Useful for minimal distraction or testing.
/// </summary>
public class BlackNullProvider : INullProvider
{
    public string DisplayName => "Void";
    public string Description => "Solid black screen with no effects";
    
    public void GenerateFrame(byte[] frameBuffer, int frameCounter)
    {
        const int width = 256;
        const int height = 240;
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                frameBuffer[offset + 0] = 0;   // R
                frameBuffer[offset + 1] = 0;   // G
                frameBuffer[offset + 2] = 0;   // B
                frameBuffer[offset + 3] = 255; // A
            }
        }
    }
}
