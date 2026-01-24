using System;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;

namespace BrokenNes.Windows.Rendering
{
    /// <summary>
    /// Serene water ripple background with slow, calming concentric waves
    /// </summary>
    public class GentleRipplesBackground : IBackground
    {
        private const int Width = 64;
        private const int Height = 48;
        
        private SharpDX.Direct2D1.Bitmap? bitmap;
        private double time = 0.0;
        private double timeSinceLastUpdate = 0.0;
        private const double UpdateInterval = 0.05;
        
        // Ripple sources
        private struct RippleSource
        {
            public float X, Y;
            public float Phase;
            public float Speed;
        }
        
        private readonly RippleSource[] rippleSources;
        
        public GentleRipplesBackground()
        {
            // Initialize a few ripple sources at strategic positions
            rippleSources = new RippleSource[]
            {
                new RippleSource { X = 0.3f, Y = 0.4f, Phase = 0.0f, Speed = 0.2f },
                new RippleSource { X = 0.7f, Y = 0.6f, Phase = 1.5f, Speed = 0.15f },
                new RippleSource { X = 0.5f, Y = 0.3f, Phase = 3.0f, Speed = 0.18f }
            };
        }
        
        public void Update(double deltaTime)
        {
            time += deltaTime * 0.12; // Very slow ripple propagation
            timeSinceLastUpdate += deltaTime;
        }
        
        public void Render(RenderTarget renderTarget, RawRectangleF destRect)
        {
            if (bitmap != null)
            {
                renderTarget.DrawBitmap(bitmap, destRect, 0.6f, BitmapInterpolationMode.NearestNeighbor);
            }
        }
        
        public void Initialize(RenderTarget renderTarget, int clientWidth, int clientHeight)
        {
            RegenerateTexture(renderTarget);
        }
        
        private void RegenerateTexture(RenderTarget renderTarget)
        {
            if (timeSinceLastUpdate < UpdateInterval)
            {
                return;
            }
            
            timeSinceLastUpdate = 0.0;
            
            var pixelData = new byte[Width * Height * 4];
            float t = (float)time;
            
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    float nx = x / (float)Width;
                    float ny = y / (float)Height;
                    
                    // Base color - deep blue
                    float baseIntensity = 0.12f;
                    
                    // Accumulate ripple effects from all sources
                    float rippleEffect = 0f;
                    
                    foreach (var source in rippleSources)
                    {
                        float dx = nx - source.X;
                        float dy = ny - source.Y;
                        float distance = (float)Math.Sqrt(dx * dx + dy * dy);
                        
                        // Concentric ripples expanding outward
                        float ripple = (float)Math.Sin((distance * 15.0f) - (t * source.Speed) + source.Phase);
                        
                        // Fade out with distance
                        float attenuation = 1.0f / (1.0f + distance * 3.0f);
                        
                        rippleEffect += ripple * attenuation * 0.15f;
                    }
                    
                    float intensity = baseIntensity + rippleEffect;
                    intensity = Math.Clamp(intensity, 0.05f, 0.5f);
                    
                    // Blue water colors with slight color shift based on depth
                    float hue = 200.0f + rippleEffect * 20.0f; // Blue to cyan
                    float saturation = 0.65f + Math.Abs(rippleEffect) * 0.25f;
                    float lightness = intensity * 0.45f;
                    
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
                bitmap = new SharpDX.Direct2D1.Bitmap(
                    renderTarget,
                    new Size2(Width, Height),
                    stream,
                    Width * 4,
                    props);
            }
        }
        
        public void Dispose()
        {
            bitmap?.Dispose();
            bitmap = null;
        }
    }
}
