using System;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;

namespace BrokenNes.Windows.Rendering
{
    /// <summary>
    /// Hénon map - discrete-time dynamical system creating butterfly patterns
    /// </summary>
    public class HenonMapBackground : IBackground
    {
        private const int Width = 64;
        private const int Height = 48;
        
        private SharpDX.Direct2D1.Bitmap? bitmap;
        private double time = 0.0;
        private double timeSinceLastUpdate = 0.0;
        private const double UpdateInterval = 0.052;
        
        public void Update(double deltaTime)
        {
            time += deltaTime * 0.17;
            timeSinceLastUpdate += deltaTime;
        }
        
        public void Render(RenderTarget renderTarget, RawRectangleF destRect)
        {
            if (bitmap != null)
            {
                renderTarget.DrawBitmap(bitmap, destRect, 0.51f, BitmapInterpolationMode.Linear);
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
            
            // Hénon parameters
            float a = 1.4f + (float)Math.Sin(t * 0.14f) * 0.08f;
            float b = 0.3f;
            
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    float nx = (x / (float)Width - 0.5f) * 3.0f;
                    float ny = (y / (float)Height - 0.5f) * 3.0f;
                    
                    float hx = nx;
                    float hy = ny;
                    float prevHx = 0.0f;
                    
                    float lyapunov = 0.0f;
                    
                    for (int i = 0; i < 20; i++)
                    {
                        prevHx = hx;
                        float newX = 1.0f - a * hx * hx + hy;
                        float newY = b * hx;
                        
                        lyapunov += (float)Math.Abs(newX - hx);
                        
                        hx = newX;
                        hy = newY;
                        
                        if (float.IsNaN(hx) || float.IsInfinity(hx))
                        {
                            lyapunov = 0.0f;
                            break;
                        }
                    }
                    
                    float value = Math.Clamp(lyapunov / 15.0f, 0.0f, 1.0f);
                    
                    // Chartreuse to lime
                    float hue = 80.0f + value * 35.0f;
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
