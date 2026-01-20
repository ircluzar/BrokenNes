using System;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;

namespace BrokenNes.Windows.Rendering
{
    /// <summary>
    /// Reaction-diffusion system creating organic patterns
    /// </summary>
    public class ReactDiffusionBackground : IBackground
    {
        private const int Width = 64;
        private const int Height = 48;
        
        private SharpDX.Direct2D1.Bitmap? bitmap;
        private double time = 0.0;
        private double timeSinceLastUpdate = 0.0;
        private const double UpdateInterval = 0.05;
        
        private float[,] gridA;
        private float[,] gridB;
        
        public ReactDiffusionBackground()
        {
            gridA = new float[Width, Height];
            gridB = new float[Width, Height];
            
            var rand = new Random(789);
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    gridA[x, y] = 1.0f;
                    gridB[x, y] = (float)rand.NextDouble() < 0.1f ? 1.0f : 0.0f;
                }
            }
        }
        
        public void Update(double deltaTime)
        {
            time += deltaTime * 0.15;
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
            
            // Gray-Scott parameters
            float Da = 1.0f;
            float Db = 0.5f;
            float f = 0.055f; // Feed rate
            float k = 0.062f; // Kill rate
            float dt = 0.5f;
            
            // Update grid
            var newA = new float[Width, Height];
            var newB = new float[Width, Height];
            
            for (int y = 1; y < Height - 1; y++)
            {
                for (int x = 1; x < Width - 1; x++)
                {
                    float a = gridA[x, y];
                    float b = gridB[x, y];
                    
                    // Laplacian
                    float laplaceA = (gridA[x - 1, y] + gridA[x + 1, y] + gridA[x, y - 1] + gridA[x, y + 1] - 4.0f * a);
                    float laplaceB = (gridB[x - 1, y] + gridB[x + 1, y] + gridB[x, y - 1] + gridB[x, y + 1] - 4.0f * b);
                    
                    float abb = a * b * b;
                    newA[x, y] = a + (Da * laplaceA - abb + f * (1.0f - a)) * dt;
                    newB[x, y] = b + (Db * laplaceB + abb - (k + f) * b) * dt;
                    
                    newA[x, y] = Math.Clamp(newA[x, y], 0.0f, 1.0f);
                    newB[x, y] = Math.Clamp(newB[x, y], 0.0f, 1.0f);
                }
            }
            
            gridA = newA;
            gridB = newB;
            
            var pixelData = new byte[Width * Height * 4];
            
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    float value = gridB[x, y];
                    
                    // Indigo to light blue
                    float hue = 220.0f + value * 40.0f;
                    float saturation = 0.6f;
                    float lightness = 0.2f + value * 0.3f;
                    
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
