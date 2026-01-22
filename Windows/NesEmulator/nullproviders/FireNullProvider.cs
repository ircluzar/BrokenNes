using System;

namespace NesEmulator.NullProviders;

/// <summary>
/// Flowing fire or heat simulation using upward advection.
/// </summary>
public class FireNullProvider : INullProvider
{
    public string DisplayName => "Ember";
    public string Description => "Warm flowing fire simulation";
    
    private byte[,]? heatMap;
    
    public void GenerateFrame(byte[] frameBuffer, int frameCounter)
    {
        const int width = 256;
        const int height = 240;
        
        // Initialize heat map
        if (heatMap == null)
        {
            heatMap = new byte[height, width];
        }
        
        // Add heat at bottom
        var rand = new Random(frameCounter);
        for (int x = 0; x < width; x++)
        {
            heatMap[height - 1, x] = (byte)(180 + rand.Next(75));
        }
        
        // Propagate heat upward with cooling
        for (int y = 0; y < height - 1; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int sum = heatMap[y + 1, x] * 4;
                if (x > 0) sum += heatMap[y + 1, x - 1];
                if (x < width - 1) sum += heatMap[y + 1, x + 1];
                
                heatMap[y, x] = (byte)Math.Max(0, sum / 6 - 1);
            }
        }
        
        // Render heat map to colors
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double heat = heatMap[y, x] / 255.0;
                
                // Fire colors: black -> red -> orange -> yellow
                byte r, g, b;
                if (heat < 0.3)
                {
                    r = (byte)(heat * 255 / 0.3);
                    g = 0;
                    b = 0;
                }
                else if (heat < 0.6)
                {
                    r = 255;
                    g = (byte)((heat - 0.3) * 255 / 0.3 * 0.6);
                    b = 0;
                }
                else
                {
                    r = 255;
                    g = (byte)(150 + (heat - 0.6) * 105 / 0.4);
                    b = (byte)((heat - 0.6) * 100 / 0.4);
                }
                
                // Dim overall for serene effect
                r = (byte)(r * 0.5);
                g = (byte)(g * 0.5);
                b = (byte)(b * 0.5);
                
                int offset = (y * width + x) * 4;
                frameBuffer[offset + 0] = r;
                frameBuffer[offset + 1] = g;
                frameBuffer[offset + 2] = b;
                frameBuffer[offset + 3] = 255;
            }
        }
    }
}
