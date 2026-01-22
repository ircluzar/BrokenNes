using System;

namespace NesEmulator.NullProviders;

/// <summary>
/// Water ripple simulation using wave interference patterns.
/// </summary>
public class WaterRipplesNullProvider : INullProvider
{
    public string DisplayName => "Ripples";
    public string Description => "Concentric ripples with wave interference";
    
    public void GenerateFrame(byte[] frameBuffer, int frameCounter)
    {
        const int width = 256;
        const int height = 240;
        double time = frameCounter * 0.1;
        
        // Ripple sources
        (double x, double y)[] sources = new[]
        {
            (128 + 60 * Math.Sin(time * 0.3), 120 + 40 * Math.Cos(time * 0.25)),
            (128 - 50 * Math.Sin(time * 0.35), 120 + 50 * Math.Cos(time * 0.3)),
            (128 + 30 * Math.Sin(time * 0.4), 120 - 45 * Math.Cos(time * 0.28))
        };
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double wave = 0;
                
                foreach (var source in sources)
                {
                    double dx = x - source.x;
                    double dy = y - source.y;
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    wave += Math.Sin(dist * 0.15 - time) / (1 + dist * 0.02);
                }
                
                // Map to blue-teal-white
                double intensity = (wave + 1.5) / 3.0;
                intensity = Math.Max(0, Math.Min(1, intensity));
                
                byte r = (byte)(40 + intensity * 100);
                byte g = (byte)(80 + intensity * 120);
                byte b = (byte)(120 + intensity * 100);
                
                int offset = (y * width + x) * 4;
                frameBuffer[offset + 0] = r;
                frameBuffer[offset + 1] = g;
                frameBuffer[offset + 2] = b;
                frameBuffer[offset + 3] = 255;
            }
        }
    }
}
