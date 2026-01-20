using System;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;

namespace BrokenNes.Windows.Rendering
{
    /// <summary>
    /// Static horizontal gradient background (dark gray -> black -> dark gray) with dithering
    /// </summary>
    public class StaticGradientBackground : IBackground
    {
        // 8x8 Bayer matrix for optimal ordered dithering
        private static readonly float[,] BayerMatrix8x8 = new float[8, 8]
        {
            {  0f/64f, 48f/64f, 12f/64f, 60f/64f,  3f/64f, 51f/64f, 15f/64f, 63f/64f },
            { 32f/64f, 16f/64f, 44f/64f, 28f/64f, 35f/64f, 19f/64f, 47f/64f, 31f/64f },
            {  8f/64f, 56f/64f,  4f/64f, 52f/64f, 11f/64f, 59f/64f,  7f/64f, 55f/64f },
            { 40f/64f, 24f/64f, 36f/64f, 20f/64f, 43f/64f, 27f/64f, 39f/64f, 23f/64f },
            {  2f/64f, 50f/64f, 14f/64f, 62f/64f,  1f/64f, 49f/64f, 13f/64f, 61f/64f },
            { 34f/64f, 18f/64f, 46f/64f, 30f/64f, 33f/64f, 17f/64f, 45f/64f, 29f/64f },
            { 10f/64f, 58f/64f,  6f/64f, 54f/64f,  9f/64f, 57f/64f,  5f/64f, 53f/64f },
            { 42f/64f, 26f/64f, 38f/64f, 22f/64f, 41f/64f, 25f/64f, 37f/64f, 21f/64f }
        };
        
        // Palette for horizontal double gradient
        private static readonly byte[] GradientPalette = new byte[] { 0, 12, 24, 36, 50 };
        
        private SharpDX.Direct2D1.Bitmap? bitmap;
        private int cachedWidth;
        private int cachedHeight;
        
        public void Update(double deltaTime)
        {
            // Static background - no updates needed
        }
        
        public void Render(RenderTarget renderTarget, RawRectangleF destRect)
        {
            if (bitmap != null)
            {
                renderTarget.DrawBitmap(bitmap, destRect, 1f, BitmapInterpolationMode.NearestNeighbor);
            }
        }
        
        public void Initialize(RenderTarget renderTarget, int clientWidth, int clientHeight)
        {
            if (bitmap != null && cachedWidth == clientWidth && cachedHeight == clientHeight)
            {
                return; // Already initialized with correct size
            }
            
            bitmap?.Dispose();
            
            // Use low resolution for pixelated aesthetic
            int logicalWidth = 256;
            int logicalHeight = 240;
            
            var pixelData = new byte[logicalWidth * logicalHeight * 4];
            float centerX = logicalWidth * 0.5f;
            int paletteSize = GradientPalette.Length;
            int totalPixels = logicalWidth * logicalHeight;
            
            for (int i = 0; i < totalPixels; i++)
            {
                int x = i % logicalWidth;
                int y = i / logicalWidth;
                
                // Create horizontal double gradient: dark gray (edges) -> black (center) -> dark gray (edges)
                float distFromCenterX = Math.Abs((x - centerX) / centerX);
                
                // Use power curve to keep more black in the middle
                float intensity = 1.0f - (float)Math.Pow(distFromCenterX, 1.8);
                intensity = Math.Clamp(intensity, 0f, 1f);
                
                // Map intensity to palette space
                float paletteFloat = intensity * (paletteSize - 1);
                
                // 8x8 Bayer matrix ordered dithering
                int bayerX = x & 7;
                int bayerY = y & 7;
                float threshold = BayerMatrix8x8[bayerY, bayerX];
                
                // Dither between adjacent palette colors
                float fractional = paletteFloat - (float)Math.Floor(paletteFloat);
                int paletteIndex = (int)paletteFloat;
                
                if (fractional > threshold && paletteIndex < paletteSize - 1)
                {
                    paletteIndex++;
                }
                
                paletteIndex = Math.Clamp(paletteIndex, 0, paletteSize - 1);
                byte c = GradientPalette[paletteIndex];
                
                int offset = i * 4;
                pixelData[offset + 0] = c; // B
                pixelData[offset + 1] = c; // G
                pixelData[offset + 2] = c; // R
                pixelData[offset + 3] = 255; // A
            }
            
            using (var stream = new DataStream(pixelData.Length, true, true))
            {
                stream.Write(pixelData, 0, pixelData.Length);
                stream.Position = 0;
                
                var props = new BitmapProperties(new PixelFormat(Format.B8G8R8A8_UNorm, SharpDX.Direct2D1.AlphaMode.Ignore));
                bitmap = new SharpDX.Direct2D1.Bitmap(
                    renderTarget,
                    new Size2(logicalWidth, logicalHeight),
                    stream,
                    logicalWidth * 4,
                    props);
            }
            
            cachedWidth = clientWidth;
            cachedHeight = clientHeight;
        }
        
        public void Dispose()
        {
            bitmap?.Dispose();
            bitmap = null;
        }
    }
}
