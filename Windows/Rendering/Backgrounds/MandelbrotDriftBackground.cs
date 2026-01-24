using System;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;

namespace BrokenNes.Windows.Rendering
{
    /// <summary>
    /// Mandelbrot set with slowly zooming and color cycling
    /// </summary>
    public class MandelbrotDriftBackground : IBackground
    {
        private const int Width = 64;
        private const int Height = 48;
        
        private SharpDX.Direct2D1.Bitmap? bitmap;
        private double time = 0.0;
        private double timeSinceLastUpdate = 0.0;
        private const double UpdateInterval = 0.06;
        
        public void Update(double deltaTime)
        {
            time += deltaTime * 0.18;
            timeSinceLastUpdate += deltaTime;
        }
        
        public void Render(RenderTarget renderTarget, RawRectangleF destRect)
        {
            if (bitmap != null)
            {
                renderTarget.DrawBitmap(bitmap, destRect, 0.5f, BitmapInterpolationMode.Linear);
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
            
            // Slowly zoom in and out
            float zoom = 1.0f + (float)Math.Sin(t * 0.2f) * 0.5f;
            float centerX = -0.7f + (float)Math.Cos(t * 0.15f) * 0.3f;
            float centerY = 0.0f + (float)Math.Sin(t * 0.1f) * 0.3f;
            
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    float px = centerX + (x / (float)Width - 0.5f) * 3.0f / zoom;
                    float py = centerY + (y / (float)Height - 0.5f) * 3.0f / zoom;
                    
                    float zx = 0.0f;
                    float zy = 0.0f;
                    int iteration = 0;
                    int maxIter = 80;
                    
                    while (zx * zx + zy * zy < 4.0f && iteration < maxIter)
                    {
                        float xtemp = zx * zx - zy * zy + px;
                        zy = 2.0f * zx * zy + py;
                        zx = xtemp;
                        iteration++;
                    }
                    
                    if (iteration == maxIter)
                    {
                        // Inside set - dark
                        int offset = (y * Width + x) * 4;
                        pixelData[offset + 0] = 10;
                        pixelData[offset + 1] = 5;
                        pixelData[offset + 2] = 15;
                        pixelData[offset + 3] = 255;
                    }
                    else
                    {
                        // Smooth coloring with time-based shift
                        float smooth = iteration + 1.0f - (float)Math.Log(Math.Log(zx * zx + zy * zy)) / (float)Math.Log(2.0f);
                        smooth = (smooth + t * 10.0f) / maxIter;
                        smooth = (smooth % 1.0f);
                        
                        // Coral to amber gradient
                        float hue = 15.0f + smooth * 45.0f;
                        float saturation = 0.7f + smooth * 0.2f;
                        float lightness = 0.2f + smooth * 0.35f;
                        
                        var rgb = ColorMath.HslToRgb(hue, saturation, lightness);
                        
                        int offset = (y * Width + x) * 4;
                        pixelData[offset + 0] = rgb.b;
                        pixelData[offset + 1] = rgb.g;
                        pixelData[offset + 2] = rgb.r;
                        pixelData[offset + 3] = 255;
                    }
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
