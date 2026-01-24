using System;
using BrokenNes.Windows.Rendering;

namespace NesEmulator.NullProviders;

/// <summary>
/// Voronoi diagram with slowly drifting seed points creating organic cell patterns.
/// </summary>
public class VoronoiNullProvider : INullProvider
{
    public string DisplayName => "Cells";
    public string Description => "Organic cell patterns with drifting boundaries";
    
    private const int NumPoints = 8;
    
    public void GenerateFrame(byte[] frameBuffer, int frameCounter)
    {
        const int width = 256;
        const int height = 240;
        double time = frameCounter * 0.005;
        
        // Generate point positions with circular motion
        (double x, double y)[] points = new (double, double)[NumPoints];
        for (int i = 0; i < NumPoints; i++)
        {
            double angle = (i / (double)NumPoints) * Math.PI * 2 + time;
            double radius = 80 + 30 * Math.Sin(time * 0.7 + i);
            points[i] = (128 + radius * Math.Cos(angle), 120 + radius * Math.Sin(angle));
        }
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Find nearest point
                double minDist = double.MaxValue;
                int nearestIdx = 0;
                
                for (int i = 0; i < NumPoints; i++)
                {
                    double dx = x - points[i].x;
                    double dy = y - points[i].y;
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    
                    if (dist < minDist)
                    {
                        minDist = dist;
                        nearestIdx = i;
                    }
                }
                
                // Color based on which cell
                double hue = (nearestIdx / (double)NumPoints) * 360;
                double brightness = 0.4 + 0.1 * Math.Sin(minDist * 0.1 + time);
                
                ColorMath.HsvToRgb(hue, 0.4, brightness, out byte r, out byte g, out byte b);
                
                int offset = (y * width + x) * 4;
                frameBuffer[offset + 0] = r;
                frameBuffer[offset + 1] = g;
                frameBuffer[offset + 2] = b;
                frameBuffer[offset + 3] = 255;
            }
        }
    }
    
}
