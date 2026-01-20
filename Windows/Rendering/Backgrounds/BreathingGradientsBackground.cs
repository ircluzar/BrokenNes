using System;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;

namespace BrokenNes.Windows.Rendering
{
    /// <summary>
    /// Serene breathing gradients background with slow color transitions and pulsing motion
    /// </summary>
    public class BreathingGradientsBackground : IBackground
    {
        private const int Width = 64;
        private const int Height = 48;
        
        private SharpDX.Direct2D1.Bitmap? bitmap;
        private double time = 0.0;
        private double timeSinceLastUpdate = 0.0;
        private const double UpdateInterval = 0.06;
        
        public void Update(double deltaTime)
        {
            time += deltaTime * 0.1; // Very slow breathing rhythm
            timeSinceLastUpdate += deltaTime;
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
            
            // Breathing pulse - slow sine wave
            float breathe = (float)Math.Sin(t * 0.5f) * 0.5f + 0.5f; // 0 to 1
            
            // Color cycle - very slow transition through warm colors
            float colorCycle = t * 0.08f;
            
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    float nx = x / (float)Width;
                    float ny = y / (float)Height;
                    
                    // Center point for radial gradient
                    float cx = 0.5f + (float)Math.Sin(t * 0.15f) * 0.1f;
                    float cy = 0.5f + (float)Math.Cos(t * 0.12f) * 0.1f;
                    
                    float dx = nx - cx;
                    float dy = ny - cy;
                    float distFromCenter = (float)Math.Sqrt(dx * dx + dy * dy);
                    
                    // Radial gradient with breathing effect
                    float radialGradient = 1.0f - distFromCenter * (1.0f + breathe * 0.3f);
                    radialGradient = Math.Clamp(radialGradient, 0f, 1f);
                    
                    // Add multiple gradient layers
                    float horizontalGradient = (float)Math.Sin(nx * Math.PI + t * 0.1f) * 0.3f + 0.5f;
                    float verticalGradient = (float)Math.Cos(ny * Math.PI - t * 0.08f) * 0.3f + 0.5f;
                    
                    // Combine gradients
                    float intensity = (radialGradient * 0.5f + horizontalGradient * 0.25f + verticalGradient * 0.25f);
                    intensity = intensity * (0.3f + breathe * 0.2f) + 0.1f;
                    intensity = Math.Clamp(intensity, 0.05f, 0.6f);
                    
                    // Slow color transitions through warm spectrum
                    // Cycle between deep purple -> blue -> teal -> purple
                    float baseHue = 260.0f; // Starting at purple
                    float hueShift = (float)Math.Sin(colorCycle) * 60.0f; // Shift ±60 degrees
                    
                    // Add spatial color variation
                    float spatialHueShift = (float)Math.Sin(nx * 2.0f + ny * 1.5f) * 15.0f;
                    
                    float hue = baseHue + hueShift + spatialHueShift;
                    
                    // Saturation pulses gently with breathing
                    float saturation = 0.5f + breathe * 0.2f + radialGradient * 0.2f;
                    saturation = Math.Clamp(saturation, 0.3f, 0.8f);
                    
                    float lightness = intensity * 0.4f;
                    
                    // Add subtle shimmer at gradient peaks
                    float shimmer = (float)Math.Sin(nx * 15.0f + t * 0.6f) * 
                                   (float)Math.Cos(ny * 13.0f - t * 0.5f);
                    if (shimmer > 0.93f && radialGradient > 0.6f)
                    {
                        lightness += 0.05f * breathe;
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
