using System;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;

namespace BrokenNes.Windows.Rendering
{
    /// <summary>
    /// Belousov-Zhabotinsky reaction simulation - chemical oscillator creating spiral waves
    /// </summary>
    public class BelousovZhabotinskyBackground : IBackground
    {
        private const int Width = 64;
        private const int Height = 48;
        
        private SharpDX.Direct2D1.Bitmap? bitmap;
        private double time = 0.0;
        private double timeSinceLastUpdate = 0.0;
        private const double UpdateInterval = 0.06;
        
        private float[,] u, v;
        
        public BelousovZhabotinskyBackground()
        {
            u = new float[Width, Height];
            v = new float[Width, Height];
            
            var rand = new Random(456);
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    u[x, y] = (float)rand.NextDouble() * 0.1f;
                    v[x, y] = (float)rand.NextDouble() * 0.05f;
                }
            }
        }
        
        public void Update(double deltaTime)
        {
            time += deltaTime * 0.16;
            timeSinceLastUpdate += deltaTime;
        }
        
        public void Render(RenderTarget renderTarget, RawRectangleF destRect)
        {
            if (bitmap != null)
            {
                renderTarget.DrawBitmap(bitmap, destRect, 0.56f, BitmapInterpolationMode.Linear);
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
            
            // BZ reaction parameters
            float Du = 0.082f;
            float Dv = 0.041f;
            float k = 0.06f;
            float dt = 0.8f;
            
            var newU = new float[Width, Height];
            var newV = new float[Width, Height];
            
            for (int y = 1; y < Height - 1; y++)
            {
                for (int x = 1; x < Width - 1; x++)
                {
                    float uVal = u[x, y];
                    float vVal = v[x, y];
                    
                    // Laplacian
                    float lapU = (u[x - 1, y] + u[x + 1, y] + u[x, y - 1] + u[x, y + 1] - 4.0f * uVal);
                    float lapV = (v[x - 1, y] + v[x + 1, y] + v[x, y - 1] + v[x, y + 1] - 4.0f * vVal);
                    
                    // BZ kinetics
                    float reaction = uVal * vVal;
                    newU[x, y] = uVal + (Du * lapU + uVal * (1.0f - uVal) - vVal) * dt;
                    newV[x, y] = vVal + (Dv * lapV + uVal - vVal * (1.0f + k)) * dt;
                    
                    newU[x, y] = Math.Clamp(newU[x, y], 0.0f, 1.0f);
                    newV[x, y] = Math.Clamp(newV[x, y], 0.0f, 1.0f);
                }
            }
            
            u = newU;
            v = newV;
            
            var pixelData = new byte[Width * Height * 4];
            
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    float value = u[x, y];
                    
                    // Crimson to scarlet
                    float hue = 350.0f + value * 25.0f;
                    float saturation = 0.65f + value * 0.25f;
                    float lightness = 0.2f + value * 0.32f;
                    
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
