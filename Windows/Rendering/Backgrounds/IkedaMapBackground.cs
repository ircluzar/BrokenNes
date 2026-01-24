using System;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;

namespace BrokenNes.Windows.Rendering
{
    /// <summary>
    /// Ikeda map - chaotic laser dynamics creating fractal-like patterns
    /// </summary>
    public class IkedaMapBackground : IBackground
    {
        private const int Width = 64;
        private const int Height = 48;
        
        private SharpDX.Direct2D1.Bitmap? bitmap;
        private double time = 0.0;
        private double timeSinceLastUpdate = 0.0;
        private const double UpdateInterval = 0.048;
        
        public void Update(double deltaTime)
        {
            time += deltaTime * 0.21;
            timeSinceLastUpdate += deltaTime;
        }
        
        public void Render(RenderTarget renderTarget, RawRectangleF destRect)
        {
            if (bitmap != null)
            {
                renderTarget.DrawBitmap(bitmap, destRect, 0.54f, BitmapInterpolationMode.Linear);
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
            
            float u = 0.9f; // Parameter
            
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    float nx = (x / (float)Width - 0.5f) * 6.0f;
                    float ny = (y / (float)Height - 0.5f) * 6.0f;
                    
                    float ix = nx;
                    float iy = ny;
                    
                    float divergence = 0.0f;
                    
                    for (int i = 0; i < 15; i++)
                    {
                        float c0 = 0.4f - 6.0f / (1.0f + ix * ix + iy * iy);
                        float tn = c0 + (float)Math.Sin(t * 0.3f) * 0.1f;
                        
                        float newX = 1.0f + u * (ix * (float)Math.Cos(tn) - iy * (float)Math.Sin(tn));
                        float newY = u * (ix * (float)Math.Sin(tn) + iy * (float)Math.Cos(tn));
                        
                        divergence += (float)Math.Sqrt((newX - ix) * (newX - ix) + (newY - iy) * (newY - iy));
                        
                        ix = newX;
                        iy = newY;
                    }
                    
                    float value = Math.Clamp(divergence / 25.0f, 0.0f, 1.0f);
                    
                    // Violet to lavender
                    float hue = 270.0f + value * 25.0f;
                    float saturation = 0.5f + value * 0.35f;
                    float lightness = 0.22f + value * 0.28f;
                    
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
