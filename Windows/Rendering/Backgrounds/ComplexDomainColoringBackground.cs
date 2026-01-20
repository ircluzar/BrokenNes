using System;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;

namespace BrokenNes.Windows.Rendering
{
    /// <summary>
    /// Complex function domain coloring - maps complex plane to colors revealing structure
    /// </summary>
    public class ComplexDomainColoringBackground : IBackground
    {
        private const int Width = 64;
        private const int Height = 48;
        
        private SharpDX.Direct2D1.Bitmap? bitmap;
        private double time = 0.0;
        private double timeSinceLastUpdate = 0.0;
        private const double UpdateInterval = 0.054;
        
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
            
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    float nx = (x / (float)Width - 0.5f) * 3.0f;
                    float ny = (y / (float)Height - 0.5f) * 3.0f;
                    
                    // Complex number z = nx + i*ny
                    float zReal = nx;
                    float zImag = ny;
                    
                    // Apply complex function: f(z) = z^3 + c*z where c varies with time
                    float cReal = (float)Math.Cos(t * 0.3f) * 0.5f;
                    float cImag = (float)Math.Sin(t * 0.3f) * 0.5f;
                    
                    // z^3
                    float z2Real = zReal * zReal - zImag * zImag;
                    float z2Imag = 2.0f * zReal * zImag;
                    float z3Real = z2Real * zReal - z2Imag * zImag;
                    float z3Imag = z2Real * zImag + z2Imag * zReal;
                    
                    // + c*z
                    float czReal = cReal * zReal - cImag * zImag;
                    float czImag = cReal * zImag + cImag * zReal;
                    
                    float fReal = z3Real + czReal;
                    float fImag = z3Imag + czImag;
                    
                    // Convert to polar
                    float magnitude = (float)Math.Sqrt(fReal * fReal + fImag * fImag);
                    float angle = (float)Math.Atan2(fImag, fReal);
                    
                    // Map angle to hue (domain coloring)
                    float hue = (angle / (float)Math.PI + 1.0f) * 180.0f; // 0-360
                    
                    // Map magnitude to lightness with log scaling
                    float lightness = 0.2f + Math.Clamp((float)Math.Log(magnitude + 1.0f) * 0.15f, 0.0f, 0.35f);
                    float saturation = 0.6f;
                    
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
                bitmap = new SharpDX.Direct2D1.Bitmap(renderTarget, new Size2(Width, Height), stream, Width * 4, props);
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
        
        internal void UpdateTexture(RenderTarget renderTarget)
        {
            if (timeSinceLastUpdate >= UpdateInterval)
            {
                RegenerateTexture(renderTarget);
            }
        }
    }
}
