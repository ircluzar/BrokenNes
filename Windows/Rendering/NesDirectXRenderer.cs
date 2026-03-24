using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Forms;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.DirectWrite;
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
        // 8x8 Bayer matrix for optimal ordered dithering (static for performance)
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

        // Palette for dithered gradient (horizontal double gradient: dark gray -> black -> dark gray)
        private static readonly byte[] GradientPalette = new byte[] { 0, 12, 24, 36, 50 };

        // DirectX core components
        private Device device;
        private SwapChain swapChain;
        private RenderTarget d2dRenderTarget;
        private SharpDX.Direct2D1.Bitmap gameBitmap;
        private RawRectangleF clientArea;
        private readonly object renderLock = new object();
        
        // Pluggable background system
        private List<IBackground> backgrounds = new List<IBackground>();
        private Stopwatch backgroundTimer;

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
        
        // FPS tracking
        private bool showFps = false;
        private Stopwatch fpsTimer;
        private int frameCount = 0;
        private double currentFps = 0.0;
        private TextFormat fpsTextFormat;
        private SolidColorBrush fpsTextBrush;
        private bool[] currentInputState = new bool[8];
        
        // Reentrancy guard for DoEvents pumping during VSync
        private volatile bool isRendering = false;
        
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
        /// Gets or sets whether to display FPS counter
        /// </summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool ShowFps
        {
            get => showFps;
            set => showFps = value;
        }
        
        /// <summary>
        /// Gets or sets whether VSync (vertical sync) is enabled to prevent screen tearing.
        /// When enabled, frame presentation is synchronized to the monitor's refresh rate.
        /// Application.DoEvents() is called during Present to keep UI responsive while blocking.
        /// </summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool EnableVSync { get; set; } = false;
        
        /// <summary>
        /// Gets or sets whether to render scanlines on the background
        /// </summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool RenderScanlines { get; set; } = false;
        
        /// <summary>
        /// Gets or sets whether to render a shadow behind the viewport
        /// </summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool RenderViewportShadow { get; set; } = false;
        
        /// <summary>
        /// Gets or sets the horizontal alignment of the viewport (0.0 = left, 0.5 = center, 1.0 = right)
        /// </summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public float ViewportAlignmentX { get; set; } = 0.5f; // Default: centered
        
        /// <summary>
        /// Gets the last calculated viewport rectangle (useful for positioning UI elements)
        /// </summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public RawRectangleF LastViewportRect { get; private set; }
        
        /// <summary>
        /// Calculate and return the current viewport rectangle without rendering
        /// </summary>
        public RawRectangleF GetViewportRect()
        {
            return CalculateDestinationRect();
        }
        
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
            fpsTimer = Stopwatch.StartNew();
            backgroundTimer = Stopwatch.StartNew();
            
            // Default background (can be changed via SetBackground)
            backgrounds.Add(new StaticGradientBackground());
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
            
            // Create text format for FPS display
            var writeFactory = new SharpDX.DirectWrite.Factory();
            fpsTextFormat = new TextFormat(writeFactory, "Consolas", 
                SharpDX.DirectWrite.FontWeight.Bold, 
                SharpDX.DirectWrite.FontStyle.Normal, 
                SharpDX.DirectWrite.FontStretch.Normal, 
                16f)
            {
                TextAlignment = SharpDX.DirectWrite.TextAlignment.Leading,
                ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Near
            };
            fpsTextBrush = new SolidColorBrush(d2dRenderTarget, new RawColor4(0.2f, 1, 0.2f, 1)); // Bright green
            writeFactory.Dispose();

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
        /// Set the active background renderer (automatically discovers all IBackground implementations via reflection)
        /// </summary>
        /// <param name="backgroundName">Name of the background (class name without "Background" suffix, e.g., "Wave", "Bubble", "FlowingAurora")</param>
        public void SetBackground(string backgroundName)
        {
            lock (renderLock)
            {
                // Dispose existing backgrounds
                foreach (var background in backgrounds)
                {
                    background.Dispose();
                }
                backgrounds.Clear();
                
                // Handle special display names
                string actualBackgroundName = backgroundName;
                if (backgroundName == "Gradient (Default)")
                {
                    actualBackgroundName = "StaticGradient";
                }
                else if (backgroundName == "None (Black)")
                {
                    actualBackgroundName = "Black";
                }
                else if (backgroundName == "---")
                {
                    // Ignore separator
                    return;
                }
                
                // Use reflection to find and instantiate the background by name
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var backgroundTypes = assembly.GetTypes()
                    .Where(t => typeof(IBackground).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);
                
                IBackground? selectedBackground = null;
                
                // Try to find a background matching the requested name
                foreach (var type in backgroundTypes)
                {
                    // Match by class name (with or without "Background" suffix)
                    var typeName = type.Name;
                    var nameWithoutSuffix = typeName.EndsWith("Background") 
                        ? typeName.Substring(0, typeName.Length - "Background".Length) 
                        : typeName;
                    
                    if (nameWithoutSuffix.Equals(actualBackgroundName, StringComparison.OrdinalIgnoreCase) ||
                        typeName.Equals(actualBackgroundName, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedBackground = (IBackground?)Activator.CreateInstance(type);
                        break;
                    }
                }
                
                // Fallback to StaticGradientBackground if not found
                if (selectedBackground == null)
                {
                    selectedBackground = new StaticGradientBackground();
                }
                
                backgrounds.Add(selectedBackground);
                
                // Reinitialize backgrounds with current render target
                InitializeBackgrounds();
            }
        }
        
        /// <summary>
        /// Get all available background names via reflection
        /// </summary>
        public static List<string> GetAvailableBackgrounds()
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var backgroundTypes = assembly.GetTypes()
                .Where(t => typeof(IBackground).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);
            
            var names = new List<string>();
            foreach (var type in backgroundTypes)
            {
                var typeName = type.Name;
                // Remove "Background" suffix for cleaner display names
                var displayName = typeName.EndsWith("Background") 
                    ? typeName.Substring(0, typeName.Length - "Background".Length) 
                    : typeName;
                names.Add(displayName);
            }
            
            // Sort alphabetically
            var sortedNames = names.OrderBy(n => n).ToList();
            
            // Build final list with special ordering
            var result = new List<string>();
            
            // 1. Gradient (Default) at top
            var gradientName = sortedNames.FirstOrDefault(n => n.Equals("StaticGradient", StringComparison.OrdinalIgnoreCase));
            if (gradientName != null)
            {
                result.Add("Gradient (Default)");
                sortedNames.Remove(gradientName);
            }
            
            // 2. None (Black) second
            var blackName = sortedNames.FirstOrDefault(n => n.Equals("Black", StringComparison.OrdinalIgnoreCase));
            if (blackName != null)
            {
                result.Add("None (Black)");
                sortedNames.Remove(blackName);
            }
            
            // 3. Separator
            if (result.Count > 0)
            {
                result.Add("---");
            }
            
            // 4. All other backgrounds in alphabetical order
            result.AddRange(sortedNames);
            
            return result;
        }

        /// <summary>
        /// Render a frame from the NES emulator
        /// </summary>
        /// <param name="frameBuffer">The NES frame buffer as a DirectBitmap</param>
        /// <param name="inputState">Current state of controller buttons</param>
        public void DrawFrame(DirectBitmap frameBuffer, bool[] inputState = null)
        {
            using (BrokenNes.Windows.PerformanceProfiler.Time("DX.DrawFrame"))
            {
                // Reentrancy guard: prevent recursive calls during DoEvents pumping
                if (isRendering) return;
                
                lock (renderLock)
                {
                    if (!IsReady || frameBuffer == null) return;
                    
                    isRendering = true;
                    try
                    {

                    if (inputState != null && inputState.Length >= 8)
                    {
                        Array.Copy(inputState, currentInputState, 8);
                    }

                    // Update FPS counter
                    frameCount++;
                    if (fpsTimer.ElapsedMilliseconds >= 1000)
                    {
                        currentFps = frameCount / (fpsTimer.ElapsedMilliseconds / 1000.0);
                        frameCount = 0;
                        fpsTimer.Restart();
                    }

                    try
                    {
                        if (UseShader && shaderManager != null && renderTargetView != null)
                        {
                            using (BrokenNes.Windows.PerformanceProfiler.Time("DX.DrawWithShader"))
                            {
                                DrawWithShader(frameBuffer);
                            }
                        }
                        else
                        {
                            using (BrokenNes.Windows.PerformanceProfiler.Time("DX.DrawDirect2D"))
                            {
                                DrawDirect2D(frameBuffer);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Render error: {ex.Message}");
                        // Try to recover by drawing this frame via Direct2D without changing shader-on state.
                        DrawDirect2D(frameBuffer);
                    }
                    }
                    finally
                    {
                        isRendering = false;
                    }
                }
            }
        }

        private void DrawDirect2D(DirectBitmap frameBuffer)
        {
            if (d2dRenderTarget == null) return;

            InitializeBackgrounds();
            d2dRenderTarget.BeginDraw();
            DrawBackground();

            // Copy frame buffer data to Direct2D bitmap
            int stride = nesWidth * 4;
            gameBitmap.CopyFromMemory(frameBuffer.BitsPtr, stride);

            // Calculate destination rectangle based on settings
            RawRectangleF destRect = CalculateDestinationRect();

            // Draw glow effect around the NES render
            DrawGlowEffect(destRect);

            // Draw with calculated rectangle
            d2dRenderTarget.DrawBitmap(gameBitmap, destRect, 1f, InterpolationMode);

            // Draw FPS counter if enabled
            if (showFps)
            {
                DrawFpsCounter();
            }

            d2dRenderTarget.EndDraw();
            // Use VSync (sync interval 1) for smooth, tear-free rendering
            // Sync interval 0 = no VSync (immediate, causes tearing)
            // Sync interval 1 = VSync enabled (wait for vertical blank)
            int syncInterval = EnableVSync ? 1 : 0;
            swapChain.Present(syncInterval, PresentFlags.None);
            
            // Pump messages if VSync is enabled to keep UI responsive during blocking
            if (EnableVSync)
            {
                System.Windows.Forms.Application.DoEvents();
            }
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
            
            // Position the image based on alignment (0.0 = left, 0.5 = center, 1.0 = right)
            float offsetX = (clientWidth - destWidth) * ViewportAlignmentX;
            float offsetY = (clientHeight - destHeight) / 2.0f;
            
            var rect = new RawRectangleF
            {
                Left = offsetX,
                Top = offsetY,
                Right = offsetX + destWidth,
                Bottom = offsetY + destHeight
            };
            
            // Store for external use
            LastViewportRect = rect;
            
            return rect;
        }

        private void InitializeBackgrounds()
        {
            if (d2dRenderTarget == null || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            {
                return;
            }

            foreach (var background in backgrounds)
            {
                background.Initialize(d2dRenderTarget, ClientSize.Width, ClientSize.Height);
            }
        }

        private void DrawBackground()
        {
            if (d2dRenderTarget == null) return;
            
            // Update all backgrounds with elapsed time
            double deltaTime = backgroundTimer.Elapsed.TotalSeconds;
            backgroundTimer.Restart();
            
            foreach (var background in backgrounds)
            {
                background.Update(deltaTime);
            }
            
            // Render all backgrounds in order (they composite on top of each other)
            var dest = new RawRectangleF
            {
                Left = 0,
                Top = 0,
                Right = ClientSize.Width,
                Bottom = ClientSize.Height
            };
            
            foreach (var background in backgrounds)
            {
                background.Render(d2dRenderTarget, dest);
            }
            
            // Update animated backgrounds if needed
            foreach (var background in backgrounds)
            {
                if (background is AnimatedWaveBackground animBg)
                {
                    animBg.UpdateTexture(d2dRenderTarget);
                }
            }
            
            // Render scanlines if enabled
            if (RenderScanlines)
            {
                DrawScanlines();
            }
        }
        
        private void DrawScanlines()
        {
            if (d2dRenderTarget == null) return;
            
            // Draw horizontal scanlines across the entire background
            using (var scanlineBrush = new SolidColorBrush(d2dRenderTarget, new RawColor4(0, 0, 0, 0.35f)))
            {
                int scanlineSpacing = 4; // Space between scanlines
                float scanlineThickness = 2.0f; // Thickness of each scanline
                for (int y = 0; y < ClientSize.Height; y += scanlineSpacing)
                {
                    d2dRenderTarget.DrawLine(
                        new RawVector2(0, y),
                        new RawVector2(ClientSize.Width, y),
                        scanlineBrush,
                        scanlineThickness);
                }
            }
        }
        
        private void DrawFpsCounter()
        {
            if (d2dRenderTarget == null || fpsTextFormat == null || fpsTextBrush == null) return;
            
            string fpsText = $"FPS: {currentFps:F1}";
            
            // Construct input string: B A - + ↑ ↓ ← →
            // Indices: 0=A, 1=B, 2=Select, 3=Start, 4=Up, 5=Down, 6=Left, 7=Right
            string inputText = "";
            inputText += currentInputState[1] ? "B " : "  ";
            inputText += currentInputState[0] ? "A " : "  ";
            inputText += currentInputState[2] ? "- " : "  ";
            inputText += currentInputState[3] ? "+ " : "  ";
            inputText += currentInputState[4] ? "↑ " : "  ";
            inputText += currentInputState[5] ? "↓ " : "  ";
            inputText += currentInputState[6] ? "← " : "  ";
            inputText += currentInputState[7] ? "→" : " ";
            
            // Position at bottom left
            float bottom = ClientSize.Height - 10;
            float top = bottom - 60; // Increased height to accommodate two lines
            var textRect = new RawRectangleF(10, top, 250, bottom);
            
            string fullText = fpsText + "\n" + inputText;
            
            // Draw shadow for better visibility
            using (var shadowBrush = new SolidColorBrush(d2dRenderTarget, new RawColor4(0, 0, 0, 0.8f)))
            {
                var shadowRect = new RawRectangleF(11, top + 1, 251, bottom + 1);
                d2dRenderTarget.DrawText(fullText, fpsTextFormat, shadowRect, shadowBrush);
            }
            
            // Draw main text
            d2dRenderTarget.DrawText(fullText, fpsTextFormat, textRect, fpsTextBrush);
        }

        private void DrawGlowEffect(RawRectangleF destRect)
        {
            if (!RenderViewportShadow || d2dRenderTarget == null)
                return;
            
            // Create a smooth, soft, blur-like shadow behind the viewport
            // Using many layers with gradual falloff for a gaussian blur appearance
            
            int shadowLayers = 200; // More layers = smoother blur
            float maxShadowExpansion = 40.0f; // How far the blur extends beyond the viewport
            
            for (int i = shadowLayers; i > 0; i--)
            {
                float ratio = (float)i / shadowLayers;
                float expansion = maxShadowExpansion * ratio;
                
                // Opacity with gentle gaussian-like falloff
                float fadeRatio = 1.0f - ratio; // Inverted: 1.0 at edge, 0.0 at max expansion
                
                // Use lower power for more gradual, blur-like transition
                float opacity = 0.06f * (float)Math.Pow(fadeRatio, 3.0); // Very subtle, transparent shadow
                
                var shadowRect = new RawRectangleF
                {
                    Left = destRect.Left - expansion,
                    Top = destRect.Top - expansion,
                    Right = destRect.Right + expansion,
                    Bottom = destRect.Bottom + expansion
                };
                
                using (var shadowBrush = new SolidColorBrush(d2dRenderTarget, new RawColor4(0, 0, 0, opacity)))
                {
                    d2dRenderTarget.FillRectangle(shadowRect, shadowBrush);
                }
            }
        }
        
        private void DrawWithShader(DirectBitmap frameBuffer)
        {
            InitializeBackgrounds();
            
            // Calculate destination rectangle for glow effect
            var destRect = CalculateDestinationRect();
            
            if (d2dRenderTarget != null)
            {
                d2dRenderTarget.BeginDraw();
                DrawBackground();
                DrawGlowEffect(destRect);
                d2dRenderTarget.EndDraw();
            }

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

            // Apply shader and draw
            shaderManager.ApplyShader(context, shaderTextureView, constants, hasPreviousFrame ? previousShaderTextureView : shaderTextureView);
            context.Draw(4, 0);

            if (!hasPreviousFrame)
            {
                // Seed previous frame after first render
                context.CopyResource(previousShaderTexture, shaderTexture);
                hasPreviousFrame = true;
            }
            
            // Draw FPS counter if enabled (using Direct2D overlay)
            if (showFps && d2dRenderTarget != null)
            {
                d2dRenderTarget.BeginDraw();
                DrawFpsCounter();
                d2dRenderTarget.EndDraw();
            }

            // Present with VSync for smooth, tear-free rendering
            int syncInterval = EnableVSync ? 1 : 0;
            swapChain.Present(syncInterval, PresentFlags.None);
            
            // Pump messages if VSync is enabled to keep UI responsive during blocking
            if (EnableVSync)
            {
                System.Windows.Forms.Application.DoEvents();
            }
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
                        // Save current configuration before cleanup
                        var currentBackgroundTypes = backgrounds
                            .Select(b => b.GetType())
                            .ToList();
                        
                        // Save renderer settings that will be lost during initialization
                        bool savedShowFps = this.ShowFps;
                        bool savedVSync = this.EnableVSync;
                        bool savedScanlines = this.RenderScanlines;
                        bool savedShadow = this.RenderViewportShadow;
                        bool savedPixelPerfect = this.PixelPerfect;
                        bool savedNativeAspect = this.ForceNativeAspectRatio;
                        var savedInterpolation = this.InterpolationMode;
                        bool savedUseShader = this.useShader;
                        var savedShaderType = this.currentShaderType;
                        float savedShaderStrength = this.ShaderStrength;
                        
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
                        
                        // Restore renderer settings
                        this.ShowFps = savedShowFps;
                        this.EnableVSync = savedVSync;
                        this.RenderScanlines = savedScanlines;
                        this.RenderViewportShadow = savedShadow;
                        this.PixelPerfect = savedPixelPerfect;
                        this.ForceNativeAspectRatio = savedNativeAspect;
                        this.InterpolationMode = savedInterpolation;
                        this.useShader = savedUseShader;
                        this.ShaderStrength = savedShaderStrength;
                        
                        // Restore shader if it was active
                        if (savedUseShader && shaderAvailable)
                        {
                            SwitchShader(savedShaderType);
                        }
                        
                        // Restore backgrounds
                        foreach (var backgroundType in currentBackgroundTypes)
                        {
                            var background = (IBackground?)Activator.CreateInstance(backgroundType);
                            if (background != null)
                            {
                                backgrounds.Add(background);
                            }
                        }
                        
                        // Reinitialize backgrounds with new render target
                        InitializeBackgrounds();
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
                
                fpsTextFormat?.Dispose();
                fpsTextBrush?.Dispose();
                shaderManager?.Dispose();
                shaderTexture?.Dispose();
                shaderTextureView?.Dispose();
                previousShaderTexture?.Dispose();
                previousShaderTextureView?.Dispose();
                renderTargetView?.Dispose();
                
                // Dispose all backgrounds
                foreach (var background in backgrounds)
                {
                    background?.Dispose();
                }
                backgrounds.Clear();
                
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
