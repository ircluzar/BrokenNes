using System;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;

namespace BrokenNes.Windows.Rendering
{
    /// <summary>
    /// Voronoi diagram with slowly drifting points - organic cell-like patterns
    /// </summary>
    public class VoronoiDriftBackground : IBackground
    {
        private const int Width = 64;
        private const int Height = 48;
        private const int NumPoints = 12;
        
        private SharpDX.Direct2D1.Bitmap? bitmap;
        private double time = 0.0;
        private double timeSinceLastUpdate = 0.0;
        private const double UpdateInterval = 0.06;
        
        private (float x, float y, float phase)[] points;
        
        public VoronoiDriftBackground()
        {
            points = new (float, float, float)[NumPoints];
            var rand = new Random(42);
            for (int i = 0; i < NumPoints; i++)
            {
                points[i] = (
                    (float)rand.NextDouble(),
                    (float)rand.NextDouble(),
                    (float)rand.NextDouble() * 6.28f
                );
            }
        }
        
        public void Update(double deltaTime)
        {
            time += deltaTime * 0.25;
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
            
            // Update point positions
            for (int i = 0; i < NumPoints; i++)
            {
                float phase = points[i].phase;
                points[i].x = 0.5f + (float)Math.Sin(t * 0.3f + phase) * 0.4f;
                points[i].y = 0.5f + (float)Math.Cos(t * 0.25f + phase * 1.3f) * 0.4f;
            }
            
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    float nx = x / (float)Width;
                    float ny = y / (float)Height;
                    
                    // Find closest point
                    float minDist = float.MaxValue;
                    int closestIdx = 0;
                    
                    for (int i = 0; i < NumPoints; i++)
                    {
                        float dx = nx - points[i].x;
                        float dy = ny - points[i].y;
                        float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                        
                        if (dist < minDist)
                        {
                            minDist = dist;
                            closestIdx = i;
                        }
                    }
                    
                    // Color based on cell and distance
                    float hue = 50.0f + closestIdx * 25.0f;
                    float saturation = 0.5f + minDist * 0.3f;
                    float lightness = 0.2f + minDist * 0.25f;
                    
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
