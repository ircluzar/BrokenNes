using System;
using BrokenNes.Windows.Rendering;

namespace NesEmulator.NullProviders;

/// <summary>
/// Particle system simulating flocking behavior (boids algorithm).
/// </summary>
public class BoidsFlockingNullProvider : INullProvider
{
    public string DisplayName => "Murmuration";
    public string Description => "Particle swarm with flocking behavior";
    
    private class Boid
    {
        public double X, Y, Vx, Vy;
    }
    
    private Boid[] boids = new Boid[80];
    
    public BoidsFlockingNullProvider()
    {
        var random = new Random();
        for (int i = 0; i < boids.Length; i++)
        {
            boids[i] = new Boid
            {
                X = random.NextDouble() * 256,
                Y = random.NextDouble() * 240,
                Vx = (random.NextDouble() - 0.5) * 2,
                Vy = (random.NextDouble() - 0.5) * 2
            };
        }
    }
    
    public void GenerateFrame(byte[] frameBuffer, int frameCounter)
    {
        const int width = 256;
        const int height = 240;
        
        // Fade background
        for (int i = 0; i < frameBuffer.Length; i += 4)
        {
            frameBuffer[i + 0] = (byte)(frameBuffer[i + 0] * 0.9);
            frameBuffer[i + 1] = (byte)(frameBuffer[i + 1] * 0.9);
            frameBuffer[i + 2] = (byte)(frameBuffer[i + 2] * 0.9);
            frameBuffer[i + 3] = 255;
        }
        
        // Update boids
        for (int i = 0; i < boids.Length; i++)
        {
            double alignX = 0, alignY = 0;
            double cohesionX = 0, cohesionY = 0;
            double separationX = 0, separationY = 0;
            int neighbors = 0;
            
            for (int j = 0; j < boids.Length; j++)
            {
                if (i == j) continue;
                
                double dx = boids[j].X - boids[i].X;
                double dy = boids[j].Y - boids[i].Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                
                if (dist < 50)
                {
                    alignX += boids[j].Vx;
                    alignY += boids[j].Vy;
                    cohesionX += boids[j].X;
                    cohesionY += boids[j].Y;
                    neighbors++;
                    
                    if (dist < 20 && dist > 0)
                    {
                        separationX -= dx / dist;
                        separationY -= dy / dist;
                    }
                }
            }
            
            if (neighbors > 0)
            {
                alignX /= neighbors;
                alignY /= neighbors;
                cohesionX /= neighbors;
                cohesionY /= neighbors;
                
                boids[i].Vx += (alignX - boids[i].Vx) * 0.05;
                boids[i].Vy += (alignY - boids[i].Vy) * 0.05;
                boids[i].Vx += (cohesionX - boids[i].X) * 0.001;
                boids[i].Vy += (cohesionY - boids[i].Y) * 0.001;
            }
            
            boids[i].Vx += separationX * 0.05;
            boids[i].Vy += separationY * 0.05;
            
            // Limit speed
            double speed = Math.Sqrt(boids[i].Vx * boids[i].Vx + boids[i].Vy * boids[i].Vy);
            if (speed > 3)
            {
                boids[i].Vx = (boids[i].Vx / speed) * 3;
                boids[i].Vy = (boids[i].Vy / speed) * 3;
            }
            
            // Update position
            boids[i].X += boids[i].Vx;
            boids[i].Y += boids[i].Vy;
            
            // Wrap around
            if (boids[i].X < 0) boids[i].X += width;
            if (boids[i].X >= width) boids[i].X -= width;
            if (boids[i].Y < 0) boids[i].Y += height;
            if (boids[i].Y >= height) boids[i].Y -= height;
            
            // Draw
            int px = (int)boids[i].X;
            int py = (int)boids[i].Y;
            
            if (px >= 0 && px < width && py >= 0 && py < height)
            {
                double hue = (i * 360.0 / boids.Length + frameCounter * 0.5) % 360;
                ColorMath.HsvToRgb(hue, 0.5, 0.6, out byte r, out byte g, out byte b);
                
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int drawX = px + dx;
                        int drawY = py + dy;
                        if (drawX >= 0 && drawX < width && drawY >= 0 && drawY < height)
                        {
                            int offset = (drawY * width + drawX) * 4;
                            frameBuffer[offset + 0] = (byte)Math.Min(255, frameBuffer[offset + 0] + r);
                            frameBuffer[offset + 1] = (byte)Math.Min(255, frameBuffer[offset + 1] + g);
                            frameBuffer[offset + 2] = (byte)Math.Min(255, frameBuffer[offset + 2] + b);
                        }
                    }
                }
            }
        }
    }
    
}
