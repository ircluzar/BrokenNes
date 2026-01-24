using System;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;

namespace BrokenNes.Windows.Rendering
{
    /// <summary>
    /// Serene starfield background with subtle parallax layers and gentle twinkling
    /// </summary>
    public class StarfieldDriftBackground : IBackground
    {
        private const int Width = 64;
        private const int Height = 48;
        
        private SharpDX.Direct2D1.Bitmap? bitmap;
        private double time = 0.0;
        private double timeSinceLastUpdate = 0.0;
        private const double UpdateInterval = 0.08;
        
        // Star data for three parallax layers
        private struct Star
        {
            public float X, Y;
            public float Brightness;
            public float TwinklePhase;
            public float TwinkleSpeed;
            public int Layer; // 0 = far, 1 = mid, 2 = near
        }
        
        private readonly Star[] stars;
        
        public StarfieldDriftBackground()
        {
            // Initialize stars with different layers for parallax
            var random = new Random(12345);
            stars = new Star[40]; // Sparse starfield for serenity
            
            for (int i = 0; i < stars.Length; i++)
            {
                stars[i] = new Star
                {
                    X = (float)random.NextDouble(),
                    Y = (float)random.NextDouble(),
                    Brightness = 0.3f + (float)random.NextDouble() * 0.5f,
                    TwinklePhase = (float)random.NextDouble() * (float)Math.PI * 2.0f,
                    TwinkleSpeed = 0.5f + (float)random.NextDouble() * 1.0f,
                    Layer = random.Next(3)
                };
            }
        }
        
        public void Update(double deltaTime)
        {
            time += deltaTime * 0.1; // Very slow drift
            timeSinceLastUpdate += deltaTime;
            
            // Slowly drift stars based on their layer for parallax
            for (int i = 0; i < stars.Length; i++)
            {
                float layerSpeed = (stars[i].Layer + 1) * 0.00015f;
                stars[i].X += (float)deltaTime * layerSpeed;
                
                // Wrap around
                if (stars[i].X > 1.05f)
                {
                    stars[i].X -= 1.1f;
                }
            }
        }
        
        public void Render(RenderTarget renderTarget, RawRectangleF destRect)
        {
            if (bitmap != null)
            {
                renderTarget.DrawBitmap(bitmap, destRect, 0.75f, BitmapInterpolationMode.NearestNeighbor);
            }
        }
        
        public void Initialize(RenderTarget renderTarget, int clientWidth, int clientHeight)
        {
            RegenerateTexture(renderTarget);
        }
        
        private void RegenerateTexture(RenderTarget renderTarget)
        {
            if (timeSinceLastUpdate < UpdateInterval)
            {
                return;
            }
            
            timeSinceLastUpdate = 0.0;
            
            var pixelData = new byte[Width * Height * 4];
            float t = (float)time;
            
            // Initialize with deep space background
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    float nx = x / (float)Width;
                    float ny = y / (float)Height;
                    
                    // Very subtle nebula-like background
                    float nebula = (float)Math.Sin(nx * 2.0f + t * 0.05f) * 
                                  (float)Math.Cos(ny * 1.5f + t * 0.03f) * 0.03f + 0.05f;
                    
                    float intensity = Math.Clamp(nebula, 0.03f, 0.15f);
                    
                    // Deep blue-purple space color
                    float hue = 240.0f + (float)Math.Sin(nx * 3.0f + ny * 2.0f) * 20.0f;
                    float saturation = 0.5f;
                    float lightness = intensity * 0.3f;
                    
                    var rgb = ColorMath.HslToRgb(hue, saturation, lightness);
                    
                    int offset = (y * Width + x) * 4;
                    pixelData[offset + 0] = rgb.b;
                    pixelData[offset + 1] = rgb.g;
                    pixelData[offset + 2] = rgb.r;
                    pixelData[offset + 3] = 255;
                }
            }
            
            // Render stars on top
            foreach (var star in stars)
            {
                int sx = (int)(star.X * Width);
                int sy = (int)(star.Y * Height);
                
                if (sx >= 0 && sx < Width && sy >= 0 && sy < Height)
                {
                    // Twinkle effect
                    float twinkle = (float)Math.Sin(t * star.TwinkleSpeed + star.TwinklePhase) * 0.3f + 0.7f;
                    float brightness = star.Brightness * twinkle;
                    
                    // Layer affects brightness (farther = dimmer)
                    brightness *= (3 - star.Layer) / 3.0f;
                    
                    int offset = (sy * Width + sx) * 4;
                    byte val = (byte)(brightness * 255);
                    
                    // White-ish stars with slight color variation
                    pixelData[offset + 0] = (byte)(val * 0.95f); // B
                    pixelData[offset + 1] = (byte)(val * 0.97f); // G
                    pixelData[offset + 2] = val;                  // R
                    
                    // Add subtle glow to brighter stars
                    if (brightness > 0.6f && star.Layer == 2)
                    {
                        // Add glow to adjacent pixels
                        if (sx > 0)
                        {
                            int leftOffset = (sy * Width + (sx - 1)) * 4;
                            byte glowVal = (byte)(brightness * 80);
                            pixelData[leftOffset + 0] = (byte)Math.Max(pixelData[leftOffset + 0], glowVal);
                            pixelData[leftOffset + 1] = (byte)Math.Max(pixelData[leftOffset + 1], glowVal);
                            pixelData[leftOffset + 2] = (byte)Math.Max(pixelData[leftOffset + 2], glowVal);
                        }
                        if (sx < Width - 1)
                        {
                            int rightOffset = (sy * Width + (sx + 1)) * 4;
                            byte glowVal = (byte)(brightness * 80);
                            pixelData[rightOffset + 0] = (byte)Math.Max(pixelData[rightOffset + 0], glowVal);
                            pixelData[rightOffset + 1] = (byte)Math.Max(pixelData[rightOffset + 1], glowVal);
                            pixelData[rightOffset + 2] = (byte)Math.Max(pixelData[rightOffset + 2], glowVal);
                        }
                    }
                }
            }
            
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
        
        public void Dispose()
        {
            bitmap?.Dispose();
            bitmap = null;
        }
    }
}
