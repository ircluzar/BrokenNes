using System;

namespace NesEmulator.NullProviders;

/// <summary>
/// Classic TV static effect - fully decorrelated spatial noise each frame with no directional drift.
/// Replicates the behavior of the original GenerateStaticFrame() implementation.
/// </summary>
public class StaticNullProvider : INullProvider
{
    public string DisplayName => "Static";
    public string Description => "Classic television static noise with no drift";
    
    public void GenerateFrame(byte[] frameBuffer, int frameCounter)
    {
        const int width = 256;
        const int height = 240;
        
        // Mix frame into seed for temporal variation
        uint frameSeed = (uint)frameCounter * 0x9E3779B1u + 0xB5297A4Du;
        
        for (int y = 0; y < height; y++)
        {
            uint rowSeed = frameSeed ^ (uint)(y * 0x1F123BB5u);
            for (int x = 0; x < width; x++)
            {
                uint pixelSeed = rowSeed ^ (uint)(x * 0x6C078965u);
                // Mix to get pseudo-random value
                pixelSeed = (pixelSeed ^ (pixelSeed >> 15)) * 0x85EBCA77u;
                pixelSeed = (pixelSeed ^ (pixelSeed >> 13)) * 0xC2B2AE3Du;
                pixelSeed = pixelSeed ^ (pixelSeed >> 16);
                
                byte gray = (byte)(pixelSeed & 0xFF);
                int offset = (y * width + x) * 4;
                
                frameBuffer[offset + 0] = gray; // R
                frameBuffer[offset + 1] = gray; // G
                frameBuffer[offset + 2] = gray; // B
                frameBuffer[offset + 3] = 255;  // A
            }
        }
    }
}
