using System;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;

namespace BrokenNes.Windows.Rendering
{
    /// <summary>
    /// Animated wavy pattern background with Phong-like shading and color cycling
    /// </summary>
    public class AnimatedWaveBackground : IBackground
    {
        private const int Width = 64;
        private const int Height = 48;
        
        private SharpDX.Direct2D1.Bitmap? bitmap;
        private double time = 0.0;
        private double timeSinceLastUpdate = 0.0;
        private const double UpdateInterval = 0.033; // ~30 FPS for smoother animation
        
        public void Update(double deltaTime)
        {
            time += deltaTime * 0.5; // Animation speed multiplier (increased from 0.15)
            timeSinceLastUpdate += deltaTime;
        }
        
        public void Render(RenderTarget renderTarget, RawRectangleF destRect)
        {
            if (bitmap != null)
            {
                // Increased opacity from 0.25 to 0.6 for more visibility
                renderTarget.DrawBitmap(bitmap, destRect, 0.6f, BitmapInterpolationMode.NearestNeighbor);
            }
        }
        
        public void Initialize(RenderTarget renderTarget, int clientWidth, int clientHeight)
        {
            // Initial generation
            RegenerateTexture(renderTarget);
        }
        
        private void RegenerateTexture(RenderTarget renderTarget)
        {
            if (timeSinceLastUpdate < UpdateInterval)
            {
                return; // Don't update too frequently
            }
            
            timeSinceLastUpdate = 0.0;
            
            var pixelData = new byte[Width * Height * 4];
            
            // Animation parameters
            float t = (float)time;
            float wave1Freq = 0.15f; // Increased frequency for more visible waves
            float wave2Freq = 0.22f;
            float wave1Speed = 0.5f; // Increased speed
            float wave2Speed = -0.4f;
            
            // Light source from top-left
            float lightX = -0.5f;
            float lightY = -0.5f;
            float lightZ = 1.0f;
            float lightLen = (float)Math.Sqrt(lightX * lightX + lightY * lightY + lightZ * lightZ);
            lightX /= lightLen;
            lightY /= lightLen;
            lightZ /= lightLen;
            
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    // Normalized coordinates
                    float nx = x / (float)Width;
                    float ny = y / (float)Height;
                    
                    // Two overlapping sine wave patterns with increased amplitude
                    float wave1 = (float)Math.Sin(nx * 15.0f * wave1Freq + t * wave1Speed) * 
                                  (float)Math.Cos(ny * 12.0f * wave1Freq + t * wave1Speed * 0.7f);
                    float wave2 = (float)Math.Sin(nx * 18.0f * wave2Freq - t * wave2Speed) * 
                                  (float)Math.Cos(ny * 15.0f * wave2Freq - t * wave2Speed * 0.8f);
                    float height = (wave1 + wave2) * 0.7f; // Increased from 0.5
                    
                    // Calculate surface normal using finite differences
                    float delta = 0.025f;
                    float hx1 = (float)Math.Sin((nx - delta) * 15.0f * wave1Freq + t * wave1Speed) * 
                                (float)Math.Cos(ny * 12.0f * wave1Freq + t * wave1Speed * 0.7f);
                    float hx2 = (float)Math.Sin((nx + delta) * 15.0f * wave1Freq + t * wave1Speed) * 
                                (float)Math.Cos(ny * 12.0f * wave1Freq + t * wave1Speed * 0.7f);
                    float hy1 = (float)Math.Sin(nx * 15.0f * wave1Freq + t * wave1Speed) * 
                                (float)Math.Cos((ny - delta) * 12.0f * wave1Freq + t * wave1Speed * 0.7f);
                    float hy2 = (float)Math.Sin(nx * 15.0f * wave1Freq + t * wave1Speed) * 
                                (float)Math.Cos((ny + delta) * 12.0f * wave1Freq + t * wave1Speed * 0.7f);
                    
                    float normalX = -(hx2 - hx1) / (delta * 2.0f);
                    float normalY = -(hy2 - hy1) / (delta * 2.0f);
                    float normalZ = 1.0f;
                    float normalLen = (float)Math.Sqrt(normalX * normalX + normalY * normalY + normalZ * normalZ);
                    normalX /= normalLen;
                    normalY /= normalLen;
                    normalZ /= normalLen;
                    
                    // Phong shading: diffuse + specular
                    float diffuse = Math.Max(0f, normalX * lightX + normalY * lightY + normalZ * lightZ);
                    
                    // Specular (view from straight on)
                    float reflectX = 2.0f * diffuse * normalX - lightX;
                    float reflectY = 2.0f * diffuse * normalY - lightY;
                    float reflectZ = 2.0f * diffuse * normalZ - lightZ;
                    float specular = (float)Math.Pow(Math.Max(0f, reflectZ), 20.0f);
                    
                    // Combined lighting with increased brightness
                    float lighting = diffuse * 0.7f + specular * 0.5f + 0.15f; // Increased from 0.6/0.4/0.1
                    lighting = Math.Clamp(lighting, 0f, 1f);
                    
                    // Cycle through dark blue hues (HSL 180-240°)
                    float hue = 190.0f + (float)Math.Sin(t * 0.3f + nx * 3.0f) * 25.0f; // Faster color cycling
                    float saturation = 0.75f + lighting * 0.25f;
                    float lightness = lighting * 0.45f; // Increased from 0.3 for more visibility
                    
                    // Convert HSL to RGB
                    var rgb = HslToRgb(hue, saturation, lightness);
                    
                    int offset = (y * Width + x) * 4;
                    pixelData[offset + 0] = rgb.b; // B
                    pixelData[offset + 1] = rgb.g; // G
                    pixelData[offset + 2] = rgb.r; // R
                    pixelData[offset + 3] = 255;   // A
                }
            }
            
            // Update or create bitmap
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
        
        // Call this from Render to regenerate when needed
        internal void UpdateTexture(RenderTarget renderTarget)
        {
            if (timeSinceLastUpdate >= UpdateInterval)
            {
                RegenerateTexture(renderTarget);
            }
        }
    }
}
