using System;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;

namespace BrokenNes.Windows.Rendering
{
    /// <summary>
    /// Serene flowing aurora background with slow, ethereal movements
    /// </summary>
    public class FlowingAuroraBackground : IBackground
    {
        private const int Width = 64;
        private const int Height = 48;
        
        private SharpDX.Direct2D1.Bitmap? bitmap;
        private double time = 0.0;
        private double timeSinceLastUpdate = 0.0;
        private const double UpdateInterval = 0.05; // 20 FPS for smooth slow motion
        
        public void Update(double deltaTime)
        {
            time += deltaTime * 0.15; // Very slow animation speed
            timeSinceLastUpdate += deltaTime;
        }
        
        public void Render(RenderTarget renderTarget, RawRectangleF destRect)
        {
            if (bitmap != null)
            {
                renderTarget.DrawBitmap(bitmap, destRect, 0.65f, BitmapInterpolationMode.NearestNeighbor);
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
            
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    float nx = x / (float)Width;
                    float ny = y / (float)Height;
                    
                    // Base gradient from dark at bottom to slightly lighter at top
                    float baseIntensity = (1.0f - ny) * 0.15f + 0.05f;
                    
                    // Multiple layers of flowing aurora curtains with different speeds
                    float wave1 = (float)Math.Sin(nx * 3.0f + t * 0.3f + ny * 2.0f) * 
                                  (float)Math.Sin(ny * 4.0f + t * 0.2f) * 0.3f;
                    
                    float wave2 = (float)Math.Sin(nx * 4.0f - t * 0.25f + ny * 1.5f) * 
                                  (float)Math.Cos(ny * 3.0f - t * 0.15f) * 0.25f;
                    
                    float wave3 = (float)Math.Sin(nx * 2.5f + t * 0.35f + ny * 3.0f) * 
                                  (float)Math.Sin(ny * 2.5f + t * 0.18f) * 0.2f;
                    
                    // Combine waves with vertical gradient bias
                    float auroraIntensity = (wave1 + wave2 + wave3) * (1.0f - ny * 0.5f);
                    auroraIntensity = Math.Max(0f, auroraIntensity);
                    
                    float intensity = baseIntensity + auroraIntensity;
                    intensity = Math.Clamp(intensity, 0f, 0.8f);
                    
                    // Aurora colors: teal to green to purple gradient
                    // Slowly shift colors over time
                    float colorShift = (float)Math.Sin(t * 0.1f + nx * 2.0f) * 30.0f;
                    float hue = 170.0f + colorShift + auroraIntensity * 40.0f; // Teal/cyan to green
                    float saturation = 0.7f + auroraIntensity * 0.2f;
                    float lightness = intensity * 0.4f;
                    
                    // Add subtle shimmer
                    float shimmer = (float)Math.Sin(nx * 20.0f + t * 0.8f) * 
                                   (float)Math.Cos(ny * 18.0f - t * 0.6f);
                    if (shimmer > 0.92f && auroraIntensity > 0.15f)
                    {
                        lightness += 0.08f;
                    }
                    
                    var rgb = HslToRgb(hue, saturation, lightness);
                    
                    int offset = (y * Width + x) * 4;
                    pixelData[offset + 0] = rgb.b;
                    pixelData[offset + 1] = rgb.g;
                    pixelData[offset + 2] = rgb.r;
                    pixelData[offset + 3] = 255;
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
    }
}
