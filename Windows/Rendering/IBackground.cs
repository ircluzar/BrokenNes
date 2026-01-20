using SharpDX.Direct2D1;
using SharpDX.Mathematics.Interop;

namespace BrokenNes.Windows.Rendering
{
    /// <summary>
    /// Interface for background renderers that can be composited behind the NES framebuffer
    /// </summary>
    public interface IBackground
    {
        /// <summary>
        /// Update the background animation/state (called each frame or at desired interval)
        /// </summary>
        /// <param name="deltaTime">Time elapsed since last update in seconds</param>
        void Update(double deltaTime);
        
        /// <summary>
        /// Render the background to the Direct2D render target
        /// </summary>
        /// <param name="renderTarget">The Direct2D render target to draw to</param>
        /// <param name="destRect">The destination rectangle covering the full client area</param>
        void Render(RenderTarget renderTarget, RawRectangleF destRect);
        
        /// <summary>
        /// Initialize or reinitialize the background (called when render target changes)
        /// </summary>
        /// <param name="renderTarget">The Direct2D render target</param>
        /// <param name="clientWidth">Client area width</param>
        /// <param name="clientHeight">Client area height</param>
        void Initialize(RenderTarget renderTarget, int clientWidth, int clientHeight);
        
        /// <summary>
        /// Clean up resources
        /// </summary>
        void Dispose();
    }
}
