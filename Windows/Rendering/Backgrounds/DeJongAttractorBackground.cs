using System;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;

namespace BrokenNes.Windows.Rendering
{
    /// <summary>
    /// Peter de Jong attractor - organic symmetric patterns
    /// </summary>
    public class DeJongAttractorBackground : IBackground
    {
        private const int Width = 64;
        private const int Height = 48;
        
        private SharpDX.Direct2D1.Bitmap? bitmap;
        private double time = 0.0;
        private double timeSinceLastUpdate = 0.0;
        private const double UpdateInterval = 0.055;
        
        public void Update(double deltaTime)
        {
            time += deltaTime * 0.19;
            timeSinceLastUpdate += deltaTime;
        }
        
        public void Render(RenderTarget renderTarget, RawRectangleF destRect)
        {
            if (bitmap != null)
            {
                renderTarget.DrawBitmap(bitmap, destRect, 0.53f, BitmapInterpolationMode.Linear);
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
            
            // De Jong parameters
            float a = 1.4f + (float)Math.Sin(t * 0.13f) * 0.5f;
            float b = -2.3f + (float)Math.Cos(t * 0.11f) * 0.4f;
            float c = 2.4f + (float)Math.Sin(t * 0.17f) * 0.3f;
            float d = -2.1f + (float)Math.Cos(t * 0.09f) * 0.5f;
            
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    float nx = (x / (float)Width - 0.5f) * 4.0f;
                    float ny = (y / (float)Height - 0.5f) * 4.0f;
                    
                    float px = nx;
                    float py = ny;
                    
                    float density = 0.0f;
                    
                    for (int i = 0; i < 12; i++)
                    {
                        float newX = (float)Math.Sin(a * py) - (float)Math.Cos(b * px);
                        float newY = (float)Math.Sin(c * px) - (float)Math.Cos(d * py);
                        
                        density += 1.0f / (1.0f + (float)Math.Sqrt((newX - nx) * (newX - nx) + (newY - ny) * (newY - ny)));
                        
                        px = newX;
                        py = newY;
                    }
                    
                    float value = Math.Clamp(density / 8.0f, 0.0f, 1.0f);
                    
                    // Sapphire to sky blue
                    float hue = 200.0f + value * 35.0f;
                    float saturation = 0.55f + value * 0.3f;
                    float lightness = 0.2f + value * 0.28f;
                    
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
