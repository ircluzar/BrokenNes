using System;
using BrokenNes.Windows.Rendering;

namespace NesEmulator.NullProviders;

/// <summary>
/// 5D hypercube (penteract) rotation projected to 2D, with emphasis on the 5th dimension.
/// </summary>
public class Penteract5DRotationNullProvider : INullProvider
{
    public string DisplayName => "Fifth Dimension";
    public string Description => "Rotating 5D hypercube with 5th dimension visualization";
    
    // 32 vertices of a penteract in 5D space (2^5 = 32)
    private static readonly double[,] vertices = {
        // v coordinate = -1
        {-1,-1,-1,-1,-1}, {1,-1,-1,-1,-1}, {-1,1,-1,-1,-1}, {1,1,-1,-1,-1},
        {-1,-1,1,-1,-1}, {1,-1,1,-1,-1}, {-1,1,1,-1,-1}, {1,1,1,-1,-1},
        {-1,-1,-1,1,-1}, {1,-1,-1,1,-1}, {-1,1,-1,1,-1}, {1,1,-1,1,-1},
        {-1,-1,1,1,-1}, {1,-1,1,1,-1}, {-1,1,1,1,-1}, {1,1,1,1,-1},
        // v coordinate = 1
        {-1,-1,-1,-1,1}, {1,-1,-1,-1,1}, {-1,1,-1,-1,1}, {1,1,-1,-1,1},
        {-1,-1,1,-1,1}, {1,-1,1,-1,1}, {-1,1,1,-1,1}, {1,1,1,-1,1},
        {-1,-1,-1,1,1}, {1,-1,-1,1,1}, {-1,1,-1,1,1}, {1,1,-1,1,1},
        {-1,-1,1,1,1}, {1,-1,1,1,1}, {-1,1,1,1,1}, {1,1,1,1,1}
    };
    
    // Edges connecting the vertices (80 edges total)
    private static readonly int[,] edges;
    
    static Penteract5DRotationNullProvider()
    {
        // Generate edges programmatically
        var edgeList = new System.Collections.Generic.List<(int, int)>();
        
        // Connect vertices that differ by exactly one coordinate
        for (int i = 0; i < 32; i++)
        {
            for (int j = i + 1; j < 32; j++)
            {
                int differences = 0;
                for (int dim = 0; dim < 5; dim++)
                {
                    if (vertices[i, dim] != vertices[j, dim])
                        differences++;
                }
                
                if (differences == 1)
                    edgeList.Add((i, j));
            }
        }
        
        edges = new int[edgeList.Count, 2];
        for (int i = 0; i < edgeList.Count; i++)
        {
            edges[i, 0] = edgeList[i].Item1;
            edges[i, 1] = edgeList[i].Item2;
        }
    }
    
    public void GenerateFrame(byte[] frameBuffer, int frameCounter)
    {
        const int width = 256;
        const int height = 240;
        double time = frameCounter * 0.015;
        
        // Clear to cosmic background
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                double gradient = Math.Sin(x * 0.02 + y * 0.03 + time) * 0.1 + 0.1;
                frameBuffer[offset + 0] = (byte)(5 + gradient * 10);
                frameBuffer[offset + 1] = (byte)(8 + gradient * 15);
                frameBuffer[offset + 2] = (byte)(20 + gradient * 20);
                frameBuffer[offset + 3] = 255;
            }
        }
        
        // Rotation matrices in 5D space
        double[,] rotated = new double[32, 5];
        
        for (int i = 0; i < 32; i++)
        {
            double x = vertices[i, 0];
            double y = vertices[i, 1];
            double z = vertices[i, 2];
            double w = vertices[i, 3];
            double v = vertices[i, 4]; // 5th dimension
            
            // Rotate in XY plane
            double cosXY = Math.Cos(time * 0.8);
            double sinXY = Math.Sin(time * 0.8);
            double nx = x * cosXY - y * sinXY;
            double ny = x * sinXY + y * cosXY;
            
            // Rotate in ZW plane
            double cosZW = Math.Cos(time * 0.6);
            double sinZW = Math.Sin(time * 0.6);
            double nz = z * cosZW - w * sinZW;
            double nw = z * sinZW + w * cosZW;
            
            // Rotate in XV plane (emphasize 5th dimension)
            double cosXV = Math.Cos(time);
            double sinXV = Math.Sin(time);
            double nnx = nx * cosXV - v * sinXV;
            double nv = nx * sinXV + v * cosXV;
            
            // Rotate in YW plane
            double cosYW = Math.Cos(time * 0.7);
            double sinYW = Math.Sin(time * 0.7);
            double nny = ny * cosYW - nw * sinYW;
            double nnw = ny * sinYW + nw * cosYW;
            
            // Rotate in ZV plane (further emphasize 5th dimension)
            double cosZV = Math.Cos(time * 1.2);
            double sinZV = Math.Sin(time * 1.2);
            double nnz = nz * cosZV - nv * sinZV;
            double nnv = nz * sinZV + nv * cosZV;
            
            rotated[i, 0] = nnx;
            rotated[i, 1] = nny;
            rotated[i, 2] = nnz;
            rotated[i, 3] = nnw;
            rotated[i, 4] = nnv; // 5th dimension
        }
        
        // Project 5D -> 4D -> 3D -> 2D and draw edges
        for (int e = 0; e < edges.GetLength(0); e++)
        {
            int v1 = edges[e, 0];
            int v2 = edges[e, 1];
            
            // Get the 5th dimension values for color mapping
            double v1_5th = rotated[v1, 4];
            double v2_5th = rotated[v2, 4];
            double avg_5th = (v1_5th + v2_5th) * 0.5;
            
            // Project 5D to 4D (using the 5th dimension for projection depth)
            double distance5D = 2.5;
            double scale5D_1 = distance5D / (distance5D - rotated[v1, 4]);
            double scale5D_2 = distance5D / (distance5D - rotated[v2, 4]);
            
            double x1_4d = rotated[v1, 0] * scale5D_1;
            double y1_4d = rotated[v1, 1] * scale5D_1;
            double z1_4d = rotated[v1, 2] * scale5D_1;
            double w1_4d = rotated[v1, 3] * scale5D_1;
            
            double x2_4d = rotated[v2, 0] * scale5D_2;
            double y2_4d = rotated[v2, 1] * scale5D_2;
            double z2_4d = rotated[v2, 2] * scale5D_2;
            double w2_4d = rotated[v2, 3] * scale5D_2;
            
            // Project 4D to 3D
            double distance4D = 2.5;
            double scale4D_1 = 1.0 / (distance4D - w1_4d);
            double scale4D_2 = 1.0 / (distance4D - w2_4d);
            
            double x1_3d = x1_4d * scale4D_1;
            double y1_3d = y1_4d * scale4D_1;
            double z1_3d = z1_4d * scale4D_1;
            
            double x2_3d = x2_4d * scale4D_2;
            double y2_3d = y2_4d * scale4D_2;
            double z2_3d = z2_4d * scale4D_2;
            
            // Project 3D to 2D (perspective)
            double distance3D = 3.0;
            double scale3D_1 = distance3D / (distance3D + z1_3d);
            double scale3D_2 = distance3D / (distance3D + z2_3d);
            
            int x1 = (int)(128 + x1_3d * 45 * scale3D_1);
            int y1 = (int)(120 - y1_3d * 45 * scale3D_1);
            int x2 = (int)(128 + x2_3d * 45 * scale3D_2);
            int y2 = (int)(120 - y2_3d * 45 * scale3D_2);
            
            // Color based on 5th dimension value
            // The 5th dimension is visualized through color intensity and hue
            double hue = (avg_5th + 1.2) * 120 + time * 40;
            double saturation = 0.7 + avg_5th * 0.15;
            double brightness = 0.5 + (avg_5th + 1.5) * 0.15;
            
            // Pulse effect based on 5th dimension
            double pulse = Math.Sin(avg_5th * 3 + time * 2) * 0.2 + 1.0;
            brightness *= pulse;
            
            ColorMath.HsvToRgb(hue, saturation, Math.Min(1.0, brightness), out byte r, out byte g, out byte b);
            
            // Line thickness also varies with 5th dimension
            int thickness = (int)(1 + Math.Max(0, avg_5th + 1) * 1.5);
            
            DrawThickLine(frameBuffer, width, height, x1, y1, x2, y2, r, g, b, thickness);
        }
        
        // Draw legend showing 5th dimension
        DrawLegend(frameBuffer, width, height, time);
    }
    
    private void DrawLegend(byte[] buffer, int width, int height, double time)
    {
        const int legendX = 10;
        const int legendY = 220;
        const int legendWidth = 236;
        const int legendHeight = 12;
        
        // Draw gradient bar representing the 5th dimension
        for (int x = 0; x < legendWidth; x++)
        {
            double t = (double)x / legendWidth;
            double v5d = t * 2 - 1; // Map to [-1, 1]
            
            double hue = (v5d + 1.2) * 120 + time * 40;
            double brightness = 0.5 + (v5d + 1.5) * 0.15;
            double pulse = Math.Sin(v5d * 3 + time * 2) * 0.2 + 1.0;
            brightness *= pulse;
            
            ColorMath.HsvToRgb(hue, 0.7, Math.Min(1.0, brightness), out byte r, out byte g, out byte b);
            
            for (int y = 0; y < legendHeight; y++)
            {
                int px = legendX + x;
                int py = legendY + y;
                if (px >= 0 && px < width && py >= 0 && py < height)
                {
                    int offset = (py * width + px) * 4;
                    buffer[offset + 0] = r;
                    buffer[offset + 1] = g;
                    buffer[offset + 2] = b;
                }
            }
        }
    }
    
    private void DrawThickLine(byte[] buffer, int width, int height, int x0, int y0, int x1, int y1, byte r, byte g, byte b, int thickness)
    {
        int dx = Math.Abs(x1 - x0);
        int dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;
        
        int steps = 0;
        while (steps++ < 800)
        {
            for (int ty = -thickness; ty <= thickness; ty++)
            {
                for (int tx = -thickness; tx <= thickness; tx++)
                {
                    int drawX = x0 + tx;
                    int drawY = y0 + ty;
                    if (drawX >= 0 && drawX < width && drawY >= 0 && drawY < height)
                    {
                        int offset = (drawY * width + drawX) * 4;
                        buffer[offset + 0] = (byte)Math.Min(255, buffer[offset + 0] + r);
                        buffer[offset + 1] = (byte)Math.Min(255, buffer[offset + 1] + g);
                        buffer[offset + 2] = (byte)Math.Min(255, buffer[offset + 2] + b);
                    }
                }
            }
            
            if (x0 == x1 && y0 == y1) break;
            
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }
    
}
