using System;
using System.ComponentModel;
using System.Windows.Forms;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using AlphaMode = SharpDX.Direct2D1.AlphaMode;
using Device = SharpDX.Direct3D11.Device;
using Factory = SharpDX.DXGI.Factory;
using SharpDX.Mathematics.Interop;
using Resource = SharpDX.Direct3D11.Resource;
using System.Diagnostics;
using D3D11MapFlags = SharpDX.Direct3D11.MapFlags;

namespace BrokenNes.Windows.Rendering
{
    /// <summary>
    /// Hardware-accelerated DirectX renderer for the NES emulator.
    /// Supports Direct2D rendering and optional HLSL shader effects.
    /// </summary>
    public class NesDirectXRenderer : Control
    {
        // DirectX core components
        private Device device;
        private SwapChain swapChain;
        private RenderTarget d2dRenderTarget;
        private SharpDX.Direct2D1.Bitmap gameBitmap;
        private RawRectangleF clientArea;
        private readonly object renderLock = new object();

        // Shader support
        private NesShaderManager shaderManager;
        private Texture2D shaderTexture;
        private ShaderResourceView shaderTextureView;
        private Texture2D previousShaderTexture;
        private ShaderResourceView previousShaderTextureView;
        private RenderTargetView renderTargetView;
        private Stopwatch shaderTimer;
        private bool useShader = true;
        private bool shaderAvailable = false;
        private NesShaderManager.ShaderType currentShaderType = NesShaderManager.ShaderType.BLD;
        private bool hasPreviousFrame = false;
        
        // NES display configuration
        private int nesWidth = 256;
        private int nesHeight = 240;
        
        // Interpolation mode for Direct2D rendering
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public BitmapInterpolationMode InterpolationMode { get; set; } = BitmapInterpolationMode.NearestNeighbor;
        
        /// <summary>
        /// Gets or sets whether pixel perfect scaling is enabled (integer scaling only)
        /// </summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool PixelPerfect { get; set; } = false;
        
        /// <summary>
        /// Gets or sets whether to force native NES aspect ratio (8:7)
        /// </summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool ForceNativeAspectRatio { get; set; } = false;
        
        /// <summary>
        /// Gets or sets whether shader effects are enabled
        /// </summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool UseShader
        {
            get => useShader && shaderAvailable;
            set => useShader = value;
        }
        
        /// <summary>
        /// Gets or sets the shader effect strength (typically 0.5 - 3.0)
        /// </summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public float ShaderStrength { get; set; } = 2.0f;
        
        /// <summary>
        /// Gets the currently active shader type
        /// </summary>
        public NesShaderManager.ShaderType CurrentShaderType => currentShaderType;
        
        /// <summary>
        /// Gets whether the renderer is initialized and ready
        /// </summary>
        public bool IsReady { get; private set; }

        public NesDirectXRenderer()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque | ControlStyles.UserPaint, true);
            ResizeRedraw = true;
        }

        /// <summary>
        /// Initialize the DirectX renderer with NES display dimensions
        /// </summary>
        /// <param name="width">NES display width (default: 256)</param>
        /// <param name="height">NES display height (default: 240)</param>
        public void Initialize(int width = 256, int height = 240)
        {
            lock (renderLock)
            {
                if (IsReady)
                {
                    Cleanup();
                }

                nesWidth = width;
                nesHeight = height;

                InitializeDirectX();
                InitializeShaders();
                IsReady = true;
            }
        }

        private void InitializeDirectX()
        {
            // Create swap chain description
            var desc = new SwapChainDescription
            {
                BufferCount = 1,
                ModeDescription = new ModeDescription(
                    ClientSize.Width, 
                    ClientSize.Height, 
                    new Rational(60, 1),
                    Format.R8G8B8A8_UNorm),
                IsWindowed = true,
                OutputHandle = Handle,
                SampleDescription = new SampleDescription(1, 0),
                SwapEffect = SwapEffect.Discard,
                Usage = Usage.RenderTargetOutput
            };

            // Create device and swap chain
            Device.CreateWithSwapChain(
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                new[] { SharpDX.Direct3D.FeatureLevel.Level_10_0 },
                desc,
                out device,
                out swapChain);

            // Create Direct2D factory and render target
            var d2dFactory = new SharpDX.Direct2D1.Factory();
            Factory factory = swapChain.GetParent<Factory>();
            factory.MakeWindowAssociation(Handle, WindowAssociationFlags.IgnoreAll);

            Texture2D backBuffer = Resource.FromSwapChain<Texture2D>(swapChain, 0);
            Surface surface = backBuffer.QueryInterface<Surface>();

            d2dRenderTarget = new RenderTarget(d2dFactory, surface,
                new RenderTargetProperties(new PixelFormat(Format.Unknown, AlphaMode.Premultiplied)));

            // Create game bitmap for Direct2D rendering
            var bitmapProperties = new BitmapProperties(
                new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Ignore));
            gameBitmap = new SharpDX.Direct2D1.Bitmap(
                d2dRenderTarget, 
                new Size2(nesWidth, nesHeight), 
                bitmapProperties);

            // Set client area for scaling
            clientArea = new RawRectangleF
            {
                Left = 0,
                Top = 0,
                Right = ClientSize.Width,
                Bottom = ClientSize.Height
            };

            // Create render target view for shader rendering
            renderTargetView = new RenderTargetView(device, backBuffer);

            factory.Dispose();
            surface.Dispose();
            backBuffer.Dispose();
        }

        private void InitializeShaders()
        {
            try
            {
                shaderManager = new NesShaderManager(device, currentShaderType);
                shaderTimer = Stopwatch.StartNew();

                // Create texture for shader input
                var textureDesc = new Texture2DDescription
                {
                    Width = nesWidth,
                    Height = nesHeight,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Dynamic,
                    BindFlags = BindFlags.ShaderResource,
                    CpuAccessFlags = CpuAccessFlags.Write,
                    OptionFlags = ResourceOptionFlags.None
                };

                shaderTexture = new Texture2D(device, textureDesc);
                shaderTextureView = new ShaderResourceView(device, shaderTexture);

                previousShaderTexture = new Texture2D(device, textureDesc);
                previousShaderTextureView = new ShaderResourceView(device, previousShaderTexture);
                hasPreviousFrame = false;
                
                shaderAvailable = true;
            }
            catch (Exception ex)
            {
                // Shaders are optional, log but continue
                Debug.WriteLine($"Shader initialization failed: {ex.Message}");
                shaderAvailable = false;
                shaderManager?.Dispose();
                shaderTexture?.Dispose();
                shaderTextureView?.Dispose();
                previousShaderTexture?.Dispose();
                previousShaderTextureView?.Dispose();
            }
        }

        /// <summary>
        /// Switch to a different shader effect
        /// </summary>
        /// <param name="shaderType">The shader type to switch to</param>
        public void SwitchShader(NesShaderManager.ShaderType shaderType)
        {
            lock (renderLock)
            {
                if (shaderManager != null && shaderAvailable)
                {
                    try
                    {
                        shaderManager.SwitchShader(shaderType);
                        currentShaderType = shaderType;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Shader switch failed: {ex.Message}");
                        // Keep current shader on failure
                    }
                }
            }
        }

        /// <summary>
        /// Get a list of all available shader names
        /// </summary>
        public static string[] GetAvailableShaders()
        {
            return Enum.GetNames(typeof(NesShaderManager.ShaderType));
        }

        /// <summary>
        /// Render a frame from the NES emulator
        /// </summary>
        /// <param name="frameBuffer">The NES frame buffer as a DirectBitmap</param>
        public void DrawFrame(DirectBitmap frameBuffer)
        {
            lock (renderLock)
            {
                if (!IsReady || frameBuffer == null) return;

                try
                {
                    if (UseShader && shaderManager != null && renderTargetView != null)
                    {
                        DrawWithShader(frameBuffer);
                    }
                    else
                    {
                        DrawDirect2D(frameBuffer);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Render error: {ex.Message}");
                    // Try to recover by falling back to Direct2D
                    if (UseShader)
                    {
                        useShader = false;
                        DrawDirect2D(frameBuffer);
                    }
                }
            }
        }

        private void DrawDirect2D(DirectBitmap frameBuffer)
        {
            if (d2dRenderTarget == null) return;
            
            d2dRenderTarget.BeginDraw();
            d2dRenderTarget.Clear(SharpDX.Color.Black);

            // Copy frame buffer data to Direct2D bitmap
            int stride = nesWidth * 4;
            gameBitmap.CopyFromMemory(frameBuffer.BitsPtr, stride);

            // Calculate destination rectangle based on settings
            RawRectangleF destRect = CalculateDestinationRect();

            // Draw with calculated rectangle
            d2dRenderTarget.DrawBitmap(gameBitmap, destRect, 1f, InterpolationMode);

            d2dRenderTarget.EndDraw();
            swapChain.Present(0, PresentFlags.None);
        }
        
        private RawRectangleF CalculateDestinationRect()
        {
            float clientWidth = ClientSize.Width;
            float clientHeight = ClientSize.Height;
            
            // Native NES aspect ratio is approximately 8:7 (256:224 visible, but 256:240 buffer)
            float nesAspect = ForceNativeAspectRatio ? (8.0f / 7.0f) : ((float)nesWidth / nesHeight);
            float clientAspect = clientWidth / clientHeight;
            
            float destWidth, destHeight;
            
            if (PixelPerfect)
            {
                // Calculate integer scale that fits in the window
                int scaleX = (int)(clientWidth / nesWidth);
                int scaleY = (int)(clientHeight / nesHeight);
                int scale = Math.Max(1, Math.Min(scaleX, scaleY));
                
                destWidth = nesWidth * scale;
                destHeight = nesHeight * scale;
            }
            else
            {
                // Scale to fit while maintaining aspect ratio
                if (clientAspect > nesAspect)
                {
                    // Client is wider than NES aspect, fit to height
                    destHeight = clientHeight;
                    destWidth = destHeight * nesAspect;
                }
                else
                {
                    // Client is taller than NES aspect, fit to width
                    destWidth = clientWidth;
                    destHeight = destWidth / nesAspect;
                }
            }
            
            // Center the image
            float offsetX = (clientWidth - destWidth) / 2.0f;
            float offsetY = (clientHeight - destHeight) / 2.0f;
            
            return new RawRectangleF
            {
                Left = offsetX,
                Top = offsetY,
                Right = offsetX + destWidth,
                Bottom = offsetY + destHeight
            };
        }

        private void DrawWithShader(DirectBitmap frameBuffer)
        {
            var context = device.ImmediateContext;

            // Update shader texture with frame buffer data
            if (hasPreviousFrame)
            {
                context.CopyResource(previousShaderTexture, shaderTexture);
            }

            var dataBox = context.MapSubresource(shaderTexture, 0, MapMode.WriteDiscard, D3D11MapFlags.None);
            
            int stride = nesWidth * 4;
            for (int y = 0; y < nesHeight; y++)
            {
                Utilities.CopyMemory(
                    dataBox.DataPointer + y * dataBox.RowPitch,
                    frameBuffer.BitsPtr + y * stride,
                    stride);
            }
            
            context.UnmapSubresource(shaderTexture, 0);

            // Calculate destination rectangle based on image settings
            var destRect = CalculateDestinationRect();

            // Setup shader constants
            var constants = new NesShaderManager.ShaderConstants
            {
                TexSize = new Vector2(nesWidth, nesHeight),
                Time = (float)shaderTimer.Elapsed.TotalSeconds,
                Strength = ShaderStrength
            };

            // Setup render state with viewport respecting image settings
            var viewport = new Viewport(
                (int)destRect.Left, 
                (int)destRect.Top, 
                (int)(destRect.Right - destRect.Left), 
                (int)(destRect.Bottom - destRect.Top), 
                0.0f, 
                1.0f);
            context.Rasterizer.SetViewport(viewport);
            context.OutputMerger.SetRenderTargets(renderTargetView);
            context.ClearRenderTargetView(renderTargetView, new Color4(0, 0, 0, 1));

            // Apply shader and draw
            shaderManager.ApplyShader(context, shaderTextureView, constants, hasPreviousFrame ? previousShaderTextureView : shaderTextureView);
            context.Draw(4, 0);

            if (!hasPreviousFrame)
            {
                // Seed previous frame after first render
                context.CopyResource(previousShaderTexture, shaderTexture);
                hasPreviousFrame = true;
            }

            // Present
            swapChain.Present(0, PresentFlags.None);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            
            if (IsReady)
            {
                lock (renderLock)
                {
                    try
                    {
                        // Update client area for new size
                        clientArea = new RawRectangleF
                        {
                            Left = 0,
                            Top = 0,
                            Right = ClientSize.Width,
                            Bottom = ClientSize.Height
                        };

                        // Recreate DirectX resources for new size
                        Cleanup();
                        Initialize(nesWidth, nesHeight);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Resize error: {ex.Message}");
                    }
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // DirectX handles all painting
            // Don't call base.OnPaint to avoid flicker
        }

        private void Cleanup()
        {
            if (IsReady)
            {
                IsReady = false;
                
                shaderManager?.Dispose();
                shaderTexture?.Dispose();
                shaderTextureView?.Dispose();
                previousShaderTexture?.Dispose();
                previousShaderTextureView?.Dispose();
                renderTargetView?.Dispose();
                gameBitmap?.Dispose();
                d2dRenderTarget?.Dispose();
                swapChain?.Dispose();
                device?.Dispose();
                hasPreviousFrame = false;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (renderLock)
                {
                    Cleanup();
                }
            }
            base.Dispose(disposing);
        }
    }
}
