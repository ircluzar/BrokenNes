using System;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;

namespace BrokenNes.Windows.Rendering
{
    /// <summary>
    /// Animated bubble background with purple colors and chaotic resonance
    /// </summary>
    public class AnimatedBubbleBackground : IBackground
    {
        private const int Width = 64;
        private const int Height = 48;
        
        private SharpDX.Direct2D1.Bitmap? bitmap;
        private double time = 0.0;
        private double timeSinceLastUpdate = 0.0;
        private const double UpdateInterval = 0.033; // ~30 FPS for smoother animation
        
        // Bubble parameters - more chaotic with varying speeds
        private readonly Bubble[] bubbles;
        private const int BubbleCount = 25; // More bubbles!
        
        private struct Bubble
        {
            public float X, Y;
            public float VX, VY;
            public float Size;
            public float Phase;
            public float Frequency;
            public float Speed;
        }
        
        public AnimatedBubbleBackground()
        {
            // Initialize bubbles with chaotic parameters
            bubbles = new Bubble[BubbleCount];
            var random = new Random(42); // Seeded for consistency
            
            for (int i = 0; i < BubbleCount; i++)
            {
                bubbles[i] = new Bubble
                {
                    X = (float)random.NextDouble(),
                    Y = (float)random.NextDouble(),
                    VX = ((float)random.NextDouble() - 0.5f) * 0.15f,
                    VY = ((float)random.NextDouble() - 0.5f) * 0.15f + 0.05f, // Slight upward bias
                    Size = 0.03f + (float)random.NextDouble() * 0.12f, // Varying sizes
                    Phase = (float)random.NextDouble() * (float)Math.PI * 2.0f,
                    Frequency = 2.0f + (float)random.NextDouble() * 4.0f,
                    Speed = 0.5f + (float)random.NextDouble() * 1.5f
                };
            }
        }
        
        public void Update(double deltaTime)
        {
            time += deltaTime * 0.6; // Animation speed
            timeSinceLastUpdate += deltaTime;
            
            // Update bubble positions with chaotic resonance
            for (int i = 0; i < bubbles.Length; i++)
            {
                bubbles[i].X += bubbles[i].VX * (float)deltaTime * bubbles[i].Speed;
                bubbles[i].Y += bubbles[i].VY * (float)deltaTime * bubbles[i].Speed;
                
                // Add sine wave movement for more chaotic motion
                bubbles[i].X += (float)Math.Sin(time * bubbles[i].Frequency + bubbles[i].Phase) * 0.002f;
                bubbles[i].Y += (float)Math.Cos(time * bubbles[i].Frequency * 0.7f + bubbles[i].Phase) * 0.002f;
                
                // Wrap around screen edges
                if (bubbles[i].X < -0.2f) bubbles[i].X = 1.2f;
                if (bubbles[i].X > 1.2f) bubbles[i].X = -0.2f;
                if (bubbles[i].Y < -0.2f) bubbles[i].Y = 1.2f;
                if (bubbles[i].Y > 1.2f) bubbles[i].Y = -0.2f;
            }
        }
        
        public void Render(RenderTarget renderTarget, RawRectangleF destRect)
        {
            if (bitmap != null)
            {
                renderTarget.DrawBitmap(bitmap, destRect, 0.7f, BitmapInterpolationMode.NearestNeighbor);
            }
        }
        
        public void Initialize(RenderTarget renderTarget, int clientWidth, int clientHeight)
        {
            // Initial generation
            RegenerateTexture(renderTarget);
        }
        
        private void RegenerateTexture(RenderTarget renderTarget)
        {
            if (timeSinceLastUpdate < UpdateInterval)
            {
                return; // Don't update too frequently
            }
            
            timeSinceLastUpdate = 0.0;
            
            var pixelData = new byte[Width * Height * 4];
            
            float t = (float)time;
            
            // Create base purple gradient background
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    float nx = x / (float)Width;
                    float ny = y / (float)Height;
                    
                    // Base purple gradient from dark to lighter purple
                    float gradient = ny * 0.3f + 0.1f;
                    
                    // Add subtle animated noise for chaotic effect
                    float noise = (float)Math.Sin(nx * 10.0f + t * 0.5f) * 
                                  (float)Math.Cos(ny * 8.0f - t * 0.3f) * 0.05f;
                    
                    float intensity = gradient + noise;
                    
                    // Calculate bubble contribution at this pixel
                    float bubbleIntensity = 0f;
                    float bubbleGlow = 0f;
                    
                    for (int i = 0; i < bubbles.Length; i++)
                    {
                        float dx = nx - bubbles[i].X;
                        float dy = ny - bubbles[i].Y;
                        float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                        float bubbleRadius = bubbles[i].Size;
                        
                        // Bubble with soft edge and pulsing
                        float pulse = 1.0f + (float)Math.Sin(t * bubbles[i].Frequency + bubbles[i].Phase) * 0.15f;
                        float edgeSoftness = 0.015f;
                        
                        if (dist < bubbleRadius * pulse)
                        {
                            // Inside bubble - lighter with soft edges
                            float edgeFactor = 1.0f - Math.Clamp((bubbleRadius * pulse - dist) / edgeSoftness, 0f, 1f);
                            float bubbleCore = (1.0f - dist / (bubbleRadius * pulse)) * 0.6f;
                            bubbleIntensity += bubbleCore * (1.0f - edgeFactor * 0.7f);
                        }
                        
                        // Glow around bubble
                        float glowRadius = bubbleRadius * pulse * 2.0f;
                        if (dist < glowRadius)
                        {
                            float glowFactor = 1.0f - dist / glowRadius;
                            bubbleGlow += glowFactor * glowFactor * 0.15f;
                        }
                    }
                    
                    intensity += bubbleIntensity + bubbleGlow;
                    intensity = Math.Clamp(intensity, 0f, 1f);
                    
                    // Purple color palette (hue 270-300°)
                    // More saturated and varied purples with chaotic shifts
                    float hue = 270.0f + (float)Math.Sin(t * 0.4f + nx * 5.0f + ny * 3.0f) * 30.0f;
                    float saturation = 0.6f + intensity * 0.3f + (float)Math.Sin(t + nx * 8.0f) * 0.1f;
                    float lightness = intensity * 0.5f + 0.05f;
                    
                    // Add chaotic sparkle effect
                    float sparkle = (float)Math.Sin(nx * 50.0f + t * 3.0f) * 
                                    (float)Math.Cos(ny * 47.0f - t * 2.5f);
                    if (sparkle > 0.95f && bubbleIntensity > 0.1f)
                    {
                        lightness += 0.2f;
                    }
                    
                    // Convert HSL to RGB
                    var rgb = HslToRgb(hue, saturation, lightness);
                    
                    int offset = (y * Width + x) * 4;
                    pixelData[offset + 0] = rgb.b; // B
                    pixelData[offset + 1] = rgb.g; // G
                    pixelData[offset + 2] = rgb.r; // R
                    pixelData[offset + 3] = 255;   // A
                }
            }
            
            // Update or create bitmap
            bitmap?.Dispose();
            
            using (var stream = new DataStream(pixelData.Length, true, true))
            {
                stream.Write(pixelData, 0, pixelData.Length);
                stream.Position = 0;
                
                var props = new BitmapProperties(new PixelFormat(Format.B8G8R8A8_UNorm, SharpDX.Direct2D1.AlphaMode.Ignore));
                bitmap = new SharpDX.Direct2D1.Bitmap(
                    renderTarget,
                    new Size2(Width, Height),
                    stream,
                    Width * 4,
                    props);
            }
        }
        
        private (byte r, byte g, byte b) HslToRgb(float h, float s, float l)
        {
            h = h % 360.0f;
            if (h < 0) h += 360.0f;
            h /= 360.0f;
            
            float r, g, b;
            
            if (s == 0)
            {
                r = g = b = l;
            }
            else
            {
                float q = l < 0.5f ? l * (1.0f + s) : l + s - l * s;
                float p = 2.0f * l - q;
                
                r = HueToRgb(p, q, h + 1.0f / 3.0f);
                g = HueToRgb(p, q, h);
                b = HueToRgb(p, q, h - 1.0f / 3.0f);
            }
            
            return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }
        
        private float HueToRgb(float p, float q, float t)
        {
            if (t < 0f) t += 1f;
            if (t > 1f) t -= 1f;
            if (t < 1f / 6f) return p + (q - p) * 6f * t;
            if (t < 1f / 2f) return q;
            if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
            return p;
        }
        
        public void Dispose()
        {
            bitmap?.Dispose();
            bitmap = null;
        }
        
        // Call this from Render to regenerate when needed
        internal void UpdateTexture(RenderTarget renderTarget)
        {
            if (timeSinceLastUpdate >= UpdateInterval)
            {
                RegenerateTexture(renderTarget);
            }
        }
    }
}
