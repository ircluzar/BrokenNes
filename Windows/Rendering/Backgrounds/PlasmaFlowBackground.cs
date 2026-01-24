using System;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;

namespace BrokenNes.Windows.Rendering
{
    /// <summary>
    /// Classic plasma effect with slow, flowing colors
    /// </summary>
    public class PlasmaFlowBackground : IBackground
    {
        private const int Width = 64;
        private const int Height = 48;
        
        private SharpDX.Direct2D1.Bitmap? bitmap;
        private double time = 0.0;
        private double timeSinceLastUpdate = 0.0;
        private const double UpdateInterval = 0.04;
        
        public void Update(double deltaTime)
        {
            time += deltaTime * 0.3;
            timeSinceLastUpdate += deltaTime;
        }
        
        public void Render(RenderTarget renderTarget, RawRectangleF destRect)
        {
            if (bitmap != null)
            {
                renderTarget.DrawBitmap(bitmap, destRect, 0.6f, BitmapInterpolationMode.Linear);
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
                    float nx = x / (float)Width;
                    float ny = y / (float)Height;
                    
                    // Classic plasma formula with multiple sine waves
                    float v1 = (float)Math.Sin(nx * 10.0f + t);
                    float v2 = (float)Math.Sin(10.0f * (nx * (float)Math.Sin(t / 2.0f) + ny * (float)Math.Cos(t / 3.0f)) + t);
                    float cx = nx + 0.5f * (float)Math.Sin(t / 5.0f);
                    float cy = ny + 0.5f * (float)Math.Cos(t / 3.0f);
                    float v3 = (float)Math.Sin((float)Math.Sqrt(100.0f * (cx * cx + cy * cy) + 1.0f) + t);
                    
                    float plasma = (v1 + v2 + v3) / 3.0f;
                    plasma = (plasma + 1.0f) * 0.5f;
                    
                    // Map to orange-red gradient
                    float hue = 10.0f + plasma * 40.0f;
                    float saturation = 0.7f;
                    float lightness = 0.25f + plasma * 0.25f;
                    
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
