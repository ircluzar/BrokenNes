using System;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;

namespace BrokenNes.Windows.Rendering
{
    /// <summary>
    /// Fractal flame-inspired background with smooth transformations
    /// </summary>
    public class FractalFlameBackground : IBackground
    {
        private const int Width = 64;
        private const int Height = 48;
        
        private SharpDX.Direct2D1.Bitmap? bitmap;
        private double time = 0.0;
        private double timeSinceLastUpdate = 0.0;
        private const double UpdateInterval = 0.05;
        
        public void Update(double deltaTime)
        {
            time += deltaTime * 0.25;
            timeSinceLastUpdate += deltaTime;
        }
        
        public void Render(RenderTarget renderTarget, RawRectangleF destRect)
        {
            if (bitmap != null)
            {
                renderTarget.DrawBitmap(bitmap, destRect, 0.55f, BitmapInterpolationMode.Linear);
            }
        }
        
        public void Initialize(RenderTarget renderTarget, int clientWidth, int clientHeight)
        {
            RegenerateTexture(renderTarget);
        }
        
        private void RegenerateTexture(RenderTarget renderTarget)
        {
            if (timeSinceLastUpdate < UpdateInterval)
                return;
            
            timeSinceLastUpdate = 0.0;
            
            var pixelData = new byte[Width * Height * 4];
            float t = (float)time;
            
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    float nx = (x / (float)Width - 0.5f) * 3.0f;
                    float ny = (y / (float)Height - 0.5f) * 3.0f;
                    
                    // Apply flame-like transformations
                    float px = nx;
                    float py = ny;
                    
                    for (int i = 0; i < 3; i++)
                    {
                        float r = (float)Math.Sqrt(px * px + py * py);
                        float theta = (float)Math.Atan2(py, px);
                        
                        // Sinusoidal variation
                        float tx = (float)Math.Sin(px + t * 0.3f);
                        float ty = (float)Math.Sin(py - t * 0.2f);
                        
                        // Spherical variation
                        float r2 = r * r + 0.0001f;
                        tx = (tx + px / r2) * 0.5f;
                        ty = (ty + py / r2) * 0.5f;
                        
                        px = tx;
                        py = ty;
                    }
                    
                    float intensity = (float)Math.Exp(-Math.Sqrt(px * px + py * py) * 0.5f);
                    intensity = Math.Clamp(intensity, 0.0f, 1.0f);
                    
                    // Deep blue to violet
                    float hue = 240.0f + intensity * 60.0f;
                    float saturation = 0.6f + intensity * 0.3f;
                    float lightness = 0.15f + intensity * 0.35f;
                    
                    var rgb = ColorMath.HslToRgb(hue, saturation, lightness);
                    
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
                bitmap = new SharpDX.Direct2D1.Bitmap(renderTarget, new Size2(Width, Height), stream, Width * 4, props);
            }
        }
        
        public void Dispose()
        {
            bitmap?.Dispose();
            bitmap = null;
        }
        
        internal void UpdateTexture(RenderTarget renderTarget)
        {
            if (timeSinceLastUpdate >= UpdateInterval)
            {
                RegenerateTexture(renderTarget);
            }
        }
    }
}
