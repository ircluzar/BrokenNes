using SharpDX.Direct2D1;
using SharpDX.Mathematics.Interop;

namespace BrokenNes.Windows.Rendering
{
    /// <summary>
    /// Simple solid black background
    /// </summary>
    public class BlackBackground : IBackground
    {
        private SolidColorBrush? blackBrush;
        
        public void Update(double deltaTime)
        {
            // No animation needed
        }
        
        public void Render(RenderTarget renderTarget, RawRectangleF destRect)
        {
            if (blackBrush != null)
            {
                renderTarget.FillRectangle(destRect, blackBrush);
            }
        }
        
        public void Initialize(RenderTarget renderTarget, int clientWidth, int clientHeight)
        {
            blackBrush?.Dispose();
            blackBrush = new SolidColorBrush(renderTarget, new RawColor4(0, 0, 0, 1));
        }
        
        public void Dispose()
        {
            blackBrush?.Dispose();
            blackBrush = null;
        }
    }
}
