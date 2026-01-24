using System;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;

namespace BrokenNes.Windows.Rendering
{
    /// <summary>
    /// Clifford attractor - creates beautiful swirling chaotic patterns
    /// </summary>
    public class CliffordAttractorBackground : IBackground
    {
        private const int Width = 64;
        private const int Height = 48;
        
        private SharpDX.Direct2D1.Bitmap? bitmap;
        private double time = 0.0;
        private double timeSinceLastUpdate = 0.0;
        private const double UpdateInterval = 0.05;
        
        public void Update(double deltaTime)
        {
            time += deltaTime * 0.2;
            timeSinceLastUpdate += deltaTime;
        }
        
        public void Render(RenderTarget renderTarget, RawRectangleF destRect)
        {
            if (bitmap != null)
            {
                renderTarget.DrawBitmap(bitmap, destRect, 0.52f, BitmapInterpolationMode.Linear);
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
            
            // Clifford attractor parameters (slowly evolving)
            float a = -1.4f + (float)Math.Sin(t * 0.15f) * 0.3f;
            float b = 1.6f + (float)Math.Cos(t * 0.12f) * 0.2f;
            float c = 1.0f + (float)Math.Sin(t * 0.18f) * 0.15f;
            float d = 0.7f + (float)Math.Cos(t * 0.1f) * 0.1f;
            
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    float nx = (x / (float)Width - 0.5f) * 4.0f;
                    float ny = (y / (float)Height - 0.5f) * 4.0f;
                    
                    float px = nx;
                    float py = ny;
                    
                    float sumDist = 0.0f;
                    
                    // Iterate Clifford attractor
                    for (int i = 0; i < 10; i++)
                    {
                        float newX = (float)Math.Sin(a * py) + c * (float)Math.Cos(a * px);
                        float newY = (float)Math.Sin(b * px) + d * (float)Math.Cos(b * py);
                        
                        float dist = (float)Math.Sqrt((newX - px) * (newX - px) + (newY - py) * (newY - py));
                        sumDist += dist;
                        
                        px = newX;
                        py = newY;
                    }
                    
                    float value = Math.Clamp(sumDist / 20.0f, 0.0f, 1.0f);
                    
                    // Ruby to rose
                    float hue = 330.0f + value * 30.0f;
                    float saturation = 0.6f + value * 0.25f;
                    float lightness = 0.2f + value * 0.3f;
                    
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
