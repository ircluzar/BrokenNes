using System;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;

namespace BrokenNes.Windows.Rendering
{
    /// <summary>
    /// Lava lamp effect with metaball blobs rising slowly
    /// </summary>
    public class LavaLampBackground : IBackground
    {
        private const int Width = 64;
        private const int Height = 48;
        private const int NumBlobs = 6;
        
        private SharpDX.Direct2D1.Bitmap? bitmap;
        private double time = 0.0;
        private double timeSinceLastUpdate = 0.0;
        private const double UpdateInterval = 0.05;
        
        private (float x, float y, float radius, float phase)[] blobs;
        
        public LavaLampBackground()
        {
            blobs = new (float, float, float, float)[NumBlobs];
            var rand = new Random(123);
            for (int i = 0; i < NumBlobs; i++)
            {
                blobs[i] = (
                    (float)rand.NextDouble(),
                    (float)rand.NextDouble(),
                    0.15f + (float)rand.NextDouble() * 0.1f,
                    (float)rand.NextDouble() * 6.28f
                );
            }
        }
        
        public void Update(double deltaTime)
        {
            time += deltaTime * 0.2;
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
            
            // Update blob positions
            for (int i = 0; i < NumBlobs; i++)
            {
                float phase = blobs[i].phase;
                blobs[i].x = 0.5f + (float)Math.Sin(t * 0.4f + phase) * 0.3f;
                blobs[i].y = ((t * 0.1f + phase) % 1.0f);
            }
            
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    float nx = x / (float)Width;
                    float ny = y / (float)Height;
                    
                    // Metaball calculation
                    float value = 0.0f;
                    for (int i = 0; i < NumBlobs; i++)
                    {
                        float dx = nx - blobs[i].x;
                        float dy = ny - blobs[i].y;
                        float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                        value += blobs[i].radius / (dist + 0.001f);
                    }
                    
                    value = Math.Clamp(value, 0.0f, 1.0f);
                    
                    // Warm magenta to orange
                    float hue = 320.0f + value * 60.0f;
                    float saturation = 0.7f;
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
