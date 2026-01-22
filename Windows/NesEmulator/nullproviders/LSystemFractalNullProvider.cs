using System;
using System.Collections.Generic;

namespace NesEmulator.NullProviders;

/// <summary>
/// L-System fractal generator with turtle graphics.
/// </summary>
public class LSystemFractalNullProvider : INullProvider
{
    public string DisplayName => "Growth";
    public string Description => "Algorithmic fractal tree generation";
    
    private string currentGeneration = "F";
    private int generation = 0;
    
    public void GenerateFrame(byte[] frameBuffer, int frameCounter)
    {
        const int width = 256;
        const int height = 240;
        
        // Clear background
        for (int i = 0; i < frameBuffer.Length; i += 4)
        {
            frameBuffer[i + 0] = 10;
            frameBuffer[i + 1] = 15;
            frameBuffer[i + 2] = 20;
            frameBuffer[i + 3] = 255;
        }
        
        // Regenerate every 120 frames
        if (frameCounter % 120 == 0)
        {
            generation = (generation + 1) % 6;
            currentGeneration = "F";
            for (int i = 0; i < generation; i++)
            {
                currentGeneration = Evolve(currentGeneration);
            }
        }
        
        // Draw L-System
        double x = 128, y = 220;
        double angle = -90; // Start pointing up
        double angleStep = 25;
        double segmentLength = generation == 0 ? 50 : 100.0 / Math.Pow(2, generation);
        
        var stack = new Stack<(double x, double y, double angle)>();
        double time = frameCounter * 0.5;
        
        for (int i = 0; i < currentGeneration.Length && i < 5000; i++)
        {
            char c = currentGeneration[i];
            
            if (c == 'F')
            {
                double newX = x + Math.Cos(angle * Math.PI / 180) * segmentLength;
                double newY = y + Math.Sin(angle * Math.PI / 180) * segmentLength;
                
                // Color based on position and time
                double hue = (y / height * 120 + time) % 360;
                HsvToRgb(hue, 0.5, 0.5, out byte r, out byte g, out byte b);
                
                DrawLine(frameBuffer, width, height, (int)x, (int)y, (int)newX, (int)newY, r, g, b);
                
                x = newX;
                y = newY;
            }
            else if (c == '+')
            {
                angle += angleStep;
            }
            else if (c == '-')
            {
                angle -= angleStep;
            }
            else if (c == '[')
            {
                stack.Push((x, y, angle));
            }
            else if (c == ']')
            {
                if (stack.Count > 0)
                {
                    (x, y, angle) = stack.Pop();
                }
            }
        }
    }
    
    private string Evolve(string current)
    {
        // L-System rule: F -> F[+F]F[-F]F
        var result = "";
        foreach (char c in current)
        {
            if (c == 'F')
                result += "F[+F]F[-F]F";
            else
                result += c;
        }
        return result;
    }
    
    private void DrawLine(byte[] buffer, int width, int height, int x0, int y0, int x1, int y1, byte r, byte g, byte b)
    {
        int dx = Math.Abs(x1 - x0);
        int dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;
        
        int steps = 0;
        while (steps++ < 500)
        {
            if (x0 >= 0 && x0 < width && y0 >= 0 && y0 < height)
            {
                int offset = (y0 * width + x0) * 4;
                buffer[offset + 0] = (byte)Math.Min(255, buffer[offset + 0] + r);
                buffer[offset + 1] = (byte)Math.Min(255, buffer[offset + 1] + g);
                buffer[offset + 2] = (byte)Math.Min(255, buffer[offset + 2] + b);
            }
            
            if (x0 == x1 && y0 == y1) break;
            
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }
    
    private void HsvToRgb(double h, double s, double v, out byte r, out byte g, out byte b)
    {
        h = h % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c;
        double rPrime = 0, gPrime = 0, bPrime = 0;
        
        if (h < 60) { rPrime = c; gPrime = x; }
        else if (h < 120) { rPrime = x; gPrime = c; }
        else if (h < 180) { gPrime = c; bPrime = x; }
        else if (h < 240) { gPrime = x; bPrime = c; }
        else if (h < 300) { rPrime = x; bPrime = c; }
        else { rPrime = c; bPrime = x; }
        
        r = (byte)((rPrime + m) * 255);
        g = (byte)((gPrime + m) * 255);
        b = (byte)((bPrime + m) * 255);
    }
}
