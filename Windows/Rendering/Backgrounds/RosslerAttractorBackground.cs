using System;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;

namespace BrokenNes.Windows.Rendering
{
    /// <summary>
    /// Rössler attractor - chaotic strange attractor creating organic flowing patterns
    /// </summary>
    public class RosslerAttractorBackground : IBackground
    {
        private const int Width = 64;
        private const int Height = 48;
        
        private SharpDX.Direct2D1.Bitmap? bitmap;
        private double time = 0.0;
        private double timeSinceLastUpdate = 0.0;
        private const double UpdateInterval = 0.045;
        
        public void Update(double deltaTime)
        {
            time += deltaTime * 0.22;
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
            
            // Rössler parameters
            float a = 0.2f;
            float b = 0.2f;
            float c = 5.7f + (float)Math.Sin(t * 0.1f) * 0.5f; // Slowly varying parameter
            float dt = 0.01f;
            
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    float nx = (x / (float)Width - 0.5f) * 2.0f;
                    float ny = (y / (float)Height - 0.5f) * 2.0f;
                    
                    // Initialize state from position
                    float rx = nx * 5.0f + (float)Math.Sin(t * 0.4f) * 2.0f;
                    float ry = ny * 5.0f + (float)Math.Cos(t * 0.3f) * 2.0f;
                    float rz = 10.0f;
                    
                    // Iterate system
                    for (int i = 0; i < 8; i++)
                    {
                        float dx = -ry - rz;
                        float dy = rx + a * ry;
                        float dz = b + rz * (rx - c);
                        
                        rx += dx * dt;
                        ry += dy * dt;
                        rz += dz * dt;
                    }
                    
                    // Map trajectory to color
                    float magnitude = (float)Math.Sqrt(rx * rx + ry * ry + rz * rz) / 30.0f;
                    magnitude = Math.Clamp(magnitude, 0.0f, 1.0f);
                    
                    float phase = (float)Math.Atan2(ry, rx) / (float)Math.PI;
                    
                    // Emerald to jade
                    float hue = 150.0f + phase * 30.0f + magnitude * 20.0f;
                    float saturation = 0.5f + magnitude * 0.3f;
                    float lightness = 0.18f + magnitude * 0.32f;
                    
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
