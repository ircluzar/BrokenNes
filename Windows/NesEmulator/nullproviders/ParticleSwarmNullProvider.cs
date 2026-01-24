using System;
using BrokenNes.Windows.Rendering;

namespace NesEmulator.NullProviders;

/// <summary>
/// Particle swarm optimization algorithm visualization.
/// </summary>
public class ParticleSwarmNullProvider : INullProvider
{
    public string DisplayName => "Swarm";
    public string Description => "Swarm optimization algorithm particles";
    
    private class Particle
    {
        public double X, Y, Vx, Vy;
        public double BestX, BestY, BestValue;
    }
    
    private Particle[] particles = new Particle[60];
    private double globalBestX = 128, globalBestY = 120;
    
    public ParticleSwarmNullProvider()
    {
        var random = new Random();
        for (int i = 0; i < particles.Length; i++)
        {
            particles[i] = new Particle
            {
                X = random.NextDouble() * 256,
                Y = random.NextDouble() * 240,
                Vx = (random.NextDouble() - 0.5) * 4,
                Vy = (random.NextDouble() - 0.5) * 4,
                BestValue = double.MaxValue
            };
            particles[i].BestX = particles[i].X;
            particles[i].BestY = particles[i].Y;
        }
    }
    
    public void GenerateFrame(byte[] frameBuffer, int frameCounter)
    {
        const int width = 256;
        const int height = 240;
        
        // Fade background
        for (int i = 0; i < frameBuffer.Length; i += 4)
        {
            frameBuffer[i + 0] = (byte)(frameBuffer[i + 0] * 0.92);
            frameBuffer[i + 1] = (byte)(frameBuffer[i + 1] * 0.92);
            frameBuffer[i + 2] = (byte)(frameBuffer[i + 2] * 0.92);
            frameBuffer[i + 3] = 255;
        }
        
        // Update global best (moving target)
        double time = frameCounter * 0.02;
        globalBestX = 128 + Math.Cos(time) * 60;
        globalBestY = 120 + Math.Sin(time * 1.3) * 50;
        
        // Update particles
        var random = new Random(frameCounter);
        for (int i = 0; i < particles.Length; i++)
        {
            // Evaluate fitness (distance to moving target)
            double dx = particles[i].X - globalBestX;
            double dy = particles[i].Y - globalBestY;
            double value = Math.Sqrt(dx * dx + dy * dy);
            
            if (value < particles[i].BestValue)
            {
                particles[i].BestValue = value;
                particles[i].BestX = particles[i].X;
                particles[i].BestY = particles[i].Y;
            }
            
            // Update velocity (PSO algorithm)
            double w = 0.7; // inertia
            double c1 = 1.5; // cognitive
            double c2 = 1.5; // social
            
            double r1 = random.NextDouble();
            double r2 = random.NextDouble();
            
            particles[i].Vx = w * particles[i].Vx +
                c1 * r1 * (particles[i].BestX - particles[i].X) +
                c2 * r2 * (globalBestX - particles[i].X);
            
            particles[i].Vy = w * particles[i].Vy +
                c1 * r1 * (particles[i].BestY - particles[i].Y) +
                c2 * r2 * (globalBestY - particles[i].Y);
            
            // Limit velocity
            double speed = Math.Sqrt(particles[i].Vx * particles[i].Vx + particles[i].Vy * particles[i].Vy);
            if (speed > 5)
            {
                particles[i].Vx = (particles[i].Vx / speed) * 5;
                particles[i].Vy = (particles[i].Vy / speed) * 5;
            }
            
            // Update position
            particles[i].X += particles[i].Vx;
            particles[i].Y += particles[i].Vy;
            
            // Bounce off walls
            if (particles[i].X < 0 || particles[i].X >= width) particles[i].Vx *= -0.5;
            if (particles[i].Y < 0 || particles[i].Y >= height) particles[i].Vy *= -0.5;
            particles[i].X = Math.Clamp(particles[i].X, 0, width - 1);
            particles[i].Y = Math.Clamp(particles[i].Y, 0, height - 1);
            
            // Draw particle
            int px = (int)particles[i].X;
            int py = (int)particles[i].Y;
            
            double hue = (value / 150.0 * 180 + frameCounter * 0.5) % 360;
            ColorMath.HsvToRgb(hue, 0.6, 0.5, out byte r, out byte g, out byte b);
            
            for (int offsetY = -1; offsetY <= 1; offsetY++)
            {
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    int drawX = px + offsetX;
                    int drawY = py + offsetY;
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
        
        // Draw target
        int tx = (int)globalBestX;
        int ty = (int)globalBestY;
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                if (dx * dx + dy * dy <= 4)
                {
                    int drawX = tx + dx;
                    int drawY = ty + dy;
                    if (drawX >= 0 && drawX < width && drawY >= 0 && drawY < height)
                    {
                        int offset = (drawY * width + drawX) * 4;
                        frameBuffer[offset + 0] = 255;
                        frameBuffer[offset + 1] = 255;
                        frameBuffer[offset + 2] = 255;
                    }
                }
            }
        }
    }
    
}
