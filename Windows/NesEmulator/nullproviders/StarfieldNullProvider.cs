using System;

namespace NesEmulator.NullProviders;

/// <summary>
/// Flowing particle starfield with depth parallax.
/// </summary>
public class StarfieldNullProvider : INullProvider
{
    public string DisplayName => "Stars";
    public string Description => "Flowing stars with parallax depth";
    
    private const int NumStars = 100;
    private (float x, float y, float z, float hue)[]? stars;
    
    public void GenerateFrame(byte[] frameBuffer, int frameCounter)
    {
        const int width = 256;
        const int height = 240;
        
        // Initialize stars once
        if (stars == null)
        {
            stars = new (float, float, float, float)[NumStars];
            var rand = new Random(42);
            for (int i = 0; i < NumStars; i++)
            {
                stars[i] = (
                    (float)(rand.NextDouble() * width),
                    (float)(rand.NextDouble() * height),
                    (float)(rand.NextDouble() * 10),
                    (float)(rand.NextDouble() * 360)
                );
            }
        }
        
        // Clear to dark purple
        for (int i = 0; i < frameBuffer.Length; i += 4)
        {
            frameBuffer[i + 0] = 15;
            frameBuffer[i + 1] = 10;
            frameBuffer[i + 2] = 30;
            frameBuffer[i + 3] = 255;
        }
        
        // Update and draw stars
        for (int i = 0; i < NumStars; i++)
        {
            var star = stars[i];
            
            // Move star
            star.y += star.z * 0.1f;
            if (star.y > height) star.y = 0;
            
            stars[i] = star;
            
            // Draw with glow
            int cx = (int)star.x;
            int cy = (int)star.y;
            float brightness = 0.3f + star.z * 0.05f;
            
            HsvToRgb(star.hue, 0.5, brightness, out byte r, out byte g, out byte b);
            
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int px = cx + dx;
                    int py = cy + dy;
                    
                    if (px >= 0 && px < width && py >= 0 && py < height)
                    {
                        int offset = (py * width + px) * 4;
                        float intensity = (dx == 0 && dy == 0) ? 1.0f : 0.3f;
                        frameBuffer[offset + 0] = (byte)Math.Min(255, frameBuffer[offset + 0] + r * intensity);
                        frameBuffer[offset + 1] = (byte)Math.Min(255, frameBuffer[offset + 1] + g * intensity);
                        frameBuffer[offset + 2] = (byte)Math.Min(255, frameBuffer[offset + 2] + b * intensity);
                    }
                }
            }
        }
    }
    
    private void HsvToRgb(double h, double s, double v, out byte r, out byte g, out byte b)
    {
        while (h < 0) h += 360;
        while (h >= 360) h -= 360;
        
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = v - c;
        
        double rPrime, gPrime, bPrime;
        
        if (h < 60) { rPrime = c; gPrime = x; bPrime = 0; }
        else if (h < 120) { rPrime = x; gPrime = c; bPrime = 0; }
        else if (h < 180) { rPrime = 0; gPrime = c; bPrime = x; }
        else if (h < 240) { rPrime = 0; gPrime = x; bPrime = c; }
        else if (h < 300) { rPrime = x; gPrime = 0; bPrime = c; }
        else { rPrime = c; gPrime = 0; bPrime = x; }
        
        r = (byte)Math.Round((rPrime + m) * 255);
        g = (byte)Math.Round((gPrime + m) * 255);
        b = (byte)Math.Round((bPrime + m) * 255);
    }
}
